using System;
using System.Collections.Concurrent;
using System.Linq;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The v2 API root: the <c>game</c> global (UtilityBelt's shape), script chat commands,
/// the module registry for <c>require</c>, and the hooks each API area implements in its
/// own file (partial methods: an area that isn't built yet simply isn't there).
///
///   World      GameWorld*.cs      game.World, WorldObject
///   Character  GameCharacter*.cs  game.Character
///   Actions    GameActions*.cs    game.Actions, game.ActionQueue, action objects
///   Library    Library*.cs        require("storage"), "enums" (+ globals), "json", "rynth.ai"
///
/// Everything here runs on the plugin pump thread.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    // ── Hooks the API areas implement ───────────────────────────────────────

    // World
    partial void RegisterWorldApi(Script s, ScriptContext ctx, Table game);
    partial void WorldTick();
    partial void WorldOnLogin();
    partial void WorldOnLogout();
    partial void WorldOnCreateObject(uint objectId);
    partial void WorldOnDeleteObject(uint objectId);
    partial void WorldOnChatText(string text, int chatType);
    partial void WorldOnSelected(uint currentId, uint previousId);
    partial void WorldOnVendor(uint vendorId, bool open);

    // Character
    partial void RegisterCharacterApi(Script s, ScriptContext ctx, Table game);
    partial void CharacterTick();
    partial void CharacterOnLogin();
    partial void CharacterOnLogout();
    partial void CharacterOnCombatModeChange(int currentMode, int previousMode);
    partial void CharacterOnEnchantmentAdded(uint spellId, double durationSeconds);
    partial void CharacterOnEnchantmentRemoved(uint enchantmentId);
    partial void CharacterOnChatText(string text, int chatType);
    partial void CharacterOnUpdateHealth(uint targetId, float ratio, uint current, uint max);

    // Actions
    partial void RegisterActionsApi(Script s, ScriptContext ctx, Table game);
    partial void ActionsTick();
    partial void ActionsOnChatText(string text, int chatType);
    partial void ActionsOnLogout();

    // Library modules and globals
    partial void RegisterLibraryGlobals(Script s, ScriptContext ctx);
    partial void ResolveLibraryModule(Script s, ScriptContext ctx, string name, ref DynValue? module);

    // WorldObject: WrapObject makes the table (raw fields Id and __rynth_object = true);
    // World fills in the properties (its __index metatable), Actions adds the action methods.
    partial void DecorateObject(Script s, ScriptContext ctx, uint id, Table obj);
    partial void DecorateObjectActions(Script s, ScriptContext ctx, uint id, Table obj);

    /// <summary>
    /// The Lua object for world object <paramref name="id"/> in this script (nil for 0).
    /// Every area hands out objects through this, so they all have the same shape:
    /// a table with the raw field <c>Id</c> (LuaArgs.Id reads it), properties from World and
    /// action methods from Actions.
    /// </summary>
    internal DynValue WrapObject(Script s, ScriptContext ctx, uint id)
    {
        if (id == 0) return DynValue.Nil;
        var t = new Table(s);
        t["Id"] = (double)id;
        t["__rynth_object"] = true;
        DecorateObject(s, ctx, id, t);
        DecorateObjectActions(s, ctx, id, t);
        return DynValue.NewTable(t);
    }

    // ── Game state ──────────────────────────────────────────────────────────

    /// <summary>"CharacterSelect", "InGame" or "LoggingOut".</summary>
    internal string GameState { get; private set; } = "CharacterSelect";
    internal long LastTick { get; private set; }

    /// <summary>Script chat commands ("/name" → owning script), readable from AC's thread.</summary>
    private readonly ConcurrentDictionary<string, string> _commandOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Script, string Command, string Args)> _pendingScriptCommands = new();

    private void SetGameState(string state)
    {
        if (GameState == state) return;
        GameState = state;
        FireAll("Game.OnStateChanged", s =>
        {
            var t = new Table(s);
            t["NewState"] = state;
            return DynValue.NewTable(t);
        });
    }

    /// <summary>Fires a v2 event on every running script that listens for it.</summary>
    internal void FireAll(string key, Func<Script, DynValue> makeArgs)
    {
        foreach (var ctx in _scripts.Values.ToArray())
            if (ctx.Host.Loaded && ctx.HasHandlers(key)) ctx.Fire(key, makeArgs);
    }

    /// <summary>
    /// game.OnScriptEnd: handlers are called directly (no coroutine), right before the
    /// script's state goes away, whether it was stopped from outside or called stop().
    /// </summary>
    private void FireScriptEnd(ScriptContext ctx)
    {
        ctx.Host.BeforeScriptStop = null;
        ctx.FireNow("Game.OnScriptEnd", s => DynValue.NewTable(new Table(s)));
    }

    // ── Registration ────────────────────────────────────────────────────────

    /// <summary>Every script's globals: v1 (chat, me, meta, RynthAi, ...) and v2 (game, require modules).</summary>
    private void RegisterApi(Script s, LuaScriptHost host, ScriptContext ctx)
    {
        RegisterV1Api(s, host);
        host.BeforeScriptStop = () => FireScriptEnd(ctx);
        RegisterGame(s, ctx);
        RegisterLibraryGlobals(s, ctx);
        ExtraGlobals?.Invoke(s, ctx.Name);
    }

    // ── Seams for the offline test harness (Tools\RynthLua.CompatTests) ─────
    // Unused by the plugin itself: ExtraGlobals stays null and nothing calls the readers.

    /// <summary>Test harness only: adds globals to every new script state (script name given).</summary>
    internal static Action<Script, string>? ExtraGlobals { get; set; }

    /// <summary>Test harness only: a script's console text ("" when it never ran).</summary>
    internal string ScriptConsoleText(string name) =>
        _scripts.TryGetValue(name, out var ctx) ? ctx.Host.ConsoleText() : string.Empty;

    /// <summary>Test harness only: whether a script's Lua state is still alive.</summary>
    internal bool ScriptIsLoaded(string name) => _scripts.TryGetValue(name, out var ctx) && ctx.Host.Loaded;

    private void RegisterGame(Script s, ScriptContext ctx)
    {
        var game = new Table(s);
        game["OnTick"] = ctx.Event("Game.OnTick").CreateTable(s);
        game["OnStateChanged"] = ctx.Event("Game.OnStateChanged").CreateTable(s);
        game["OnScriptEnd"] = ctx.Event("Game.OnScriptEnd").CreateTable(s);
        game["ScriptName"] = ctx.Name;

        // UB's Capabilities: Has(ClientCapability.X) / Get(). ImGui (1) when the engine can
        // show script windows.
        var caps = new Table(s);
        int CapsNow() => Host.HasUi ? 1 : 0;
        caps["Get"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(CapsNow()));
        caps["Has"] = DynValue.NewCallback((c, a) =>
        {
            long want = (long)LuaArgs.Num(LuaArgs.At(LuaArgs.Of(a, caps), 0), 0);
            return DynValue.NewBoolean(want != 0 && (CapsNow() & want) == want);
        });
        game["Capabilities"] = DynValue.NewTable(caps);

        game["RegisterCommand"] = DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, game);
            string cmd = LuaArgs.Str(LuaArgs.At(args, 0)).Trim();
            DynValue fn = LuaArgs.At(args, 1);
            if (cmd.Length == 0 || fn.Type != DataType.Function)
                throw new ScriptRuntimeException("RegisterCommand(\"/name\", function(args) end) needs a command and a function");
            if (!cmd.StartsWith('/')) cmd = "/" + cmd;
            if (_commandOwners.TryGetValue(cmd, out string? owner) && !owner.Equals(ctx.Name, StringComparison.OrdinalIgnoreCase))
                throw new ScriptRuntimeException($"{cmd} is already a command of script {owner}");
            ctx.Commands[cmd] = fn;
            _commandOwners[cmd] = ctx.Name;
            return DynValue.Nil;
        });
        game["UnregisterCommand"] = DynValue.NewCallback((c, a) =>
        {
            string cmd = LuaArgs.Str(LuaArgs.At(LuaArgs.Of(a, game), 0)).Trim();
            if (!cmd.StartsWith('/')) cmd = "/" + cmd;
            ctx.Commands.Remove(cmd);
            if (_commandOwners.TryGetValue(cmd, out string? owner) && owner.Equals(ctx.Name, StringComparison.OrdinalIgnoreCase))
                _commandOwners.TryRemove(cmd, out _);
            return DynValue.Nil;
        });
        ctx.OnStop.Add(() =>
        {
            foreach (var kv in _commandOwners.ToArray())
                if (kv.Value.Equals(ctx.Name, StringComparison.OrdinalIgnoreCase)) _commandOwners.TryRemove(kv.Key, out _);
        });

        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            switch (key)
            {
                case "ServerName":  return DynValue.NewString(Host.TryGetWorldName(out string w) ? w : string.Empty);
                case "AccountName": return DynValue.NewString(Host.TryGetAccountName(out string acct) ? acct : string.Empty);
                case "CharacterId": return DynValue.NewNumber(Host.GetPlayerId());
                case "State":       return DynValue.NewString(GameState);
                case "LastTick":    return DynValue.NewNumber(LastTick);
                default:            return DynValue.Nil;
            }
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString("game"));
        game.MetaTable = mt;

        RegisterWorldApi(s, ctx, game);
        RegisterCharacterApi(s, ctx, game);
        RegisterActionsApi(s, ctx, game);
        s.Globals["game"] = DynValue.NewTable(game);
    }

    /// <summary>require(name): library modules first; "rynthlua" is the plugin's own info.</summary>
    private DynValue? ResolveModule(Script s, ScriptContext ctx, string name)
    {
        DynValue? module = null;
        ResolveLibraryModule(s, ctx, name, ref module);
        return module;
    }

    // ── Script commands ─────────────────────────────────────────────────────

    /// <summary>AC's thread: a line starting with a script's command is eaten and queued.</summary>
    private bool TryQueueScriptCommand(string line)
    {
        if (_commandOwners.IsEmpty) return false;
        int sp = line.IndexOf(' ');
        string cmd = sp < 0 ? line : line[..sp];
        if (!_commandOwners.TryGetValue(cmd, out string? owner)) return false;
        _pendingScriptCommands.Enqueue((owner, cmd, sp < 0 ? string.Empty : line[(sp + 1)..].Trim()));
        return true;
    }

    private void RunPendingScriptCommands()
    {
        while (_pendingScriptCommands.TryDequeue(out var p))
        {
            if (!_scripts.TryGetValue(p.Script, out var ctx) || !ctx.Host.Loaded) continue;
            if (!ctx.Commands.TryGetValue(p.Command, out DynValue? fn)) continue;
            ctx.Host.Start(fn, new[] { DynValue.NewString(p.Args) });
        }
    }

    /// <summary>Per tick, before scripts run: areas poll state, game.OnTick fires.</summary>
    private void TickApi()
    {
        LastTick = Environment.TickCount64;
        RunPendingScriptCommands();
        WorldTick();
        WorldTickChatInput();
        CharacterTick();
        TradeTick();
        ActionsTick();
        FireAll("Game.OnTick", s => DynValue.NewTable(new Table(s)));
        FireAll("World.OnTick", s => DynValue.NewTable(new Table(s)));
    }
}
