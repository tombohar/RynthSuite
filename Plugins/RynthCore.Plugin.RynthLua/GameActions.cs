using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// game.Actions and game.ActionQueue: UtilityBelt's action queue. AC does one use / cast /
/// move at a time, so scripts queue actions and <c>await</c> them:
///
///   local a = await(game.Actions.ObjectUse(potion.Id))
///   if not a.Success then print(a.Error, a.ErrorDetails) end
///
/// One queue for every script (plugin-wide), highest priority first, one action running at
/// a time; Sleep and InvokeChat run beside it. An action's Lua object is a plain table whose
/// fields are rewritten as it runs (see <see cref="LuaAction"/>). A script's actions are
/// cancelled when it stops, everything is cancelled on logout.
///
/// Every call is <c>Kind(args... [, options] [, callback])</c>: the options table
/// (<c>{ Priority, TimeoutMilliseconds, MaxRetryCount, SkipChecks, Name }</c>) and the callback
/// (called with the action when it finishes, as its own coroutine) may come in any order after
/// the arguments. Objects can be passed as WorldObjects or ids.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    /// <summary>The SDK host for the action classes (RynthPluginBase.Host is protected).</summary>
    internal RynthCoreHost ActHost => Host;

    private readonly List<LuaAction> _queuedActions = new();
    private readonly List<LuaAction> _immediateActions = new();
    private LuaAction? _currentAction;
    /// <summary>Unfinished actions by their Lua table (Contains/Remove/RunAllOrdered look them up).</summary>
    private readonly Dictionary<Table, LuaAction> _actionsByTable = new(ReferenceEqualityComparer.Instance);

    private const string MakeAwaitKey = "rynth_actions_makeawait";

    // ── Registration ────────────────────────────────────────────────────────

    partial void RegisterActionsApi(Script s, ScriptContext ctx, Table game)
    {
        var actions = new Table(s);
        void Fn(string name, string kind) =>
            actions[name] = DynValue.NewCallback((c, a) => CreateAction(s, ctx, kind, LuaArgs.Of(a, actions)));

        Fn("ObjectUse", "ObjectUse");
        Fn("ObjectUseOn", "ObjectUseOn");
        Fn("ObjectApply", "ObjectUseOn");     // UB's name for use-on
        Fn("ObjectGive", "ObjectGive");
        Fn("ObjectMove", "ObjectMove");
        Fn("ObjectSplit", "ObjectSplit");
        Fn("ObjectDrop", "ObjectDrop");
        Fn("ObjectWield", "ObjectWield");
        Fn("ObjectSelect", "ObjectSelect");
        Fn("ObjectAppraise", "ObjectAppraise");
        Fn("CastSpell", "CastSpell");
        Fn("SetCombatMode", "SetCombatMode");
        Fn("InvokeChat", "InvokeChat");
        Fn("Sleep", "Sleep");
        // UtilityBelt's others (GameActionTypesUb.cs).
        foreach (string kind in new[]
        {
            "Blank", "SetAutorun", "SendTellById", "CastEquippedWandSpell", "SalvageAdd", "Salvage",
            "VendorAddToBuyList", "VendorAddToSellList", "VendorClearBuyList", "VendorClearSellList",
            "VendorBuyAll", "VendorSellAll", "TradeAdd", "TradeAccept", "TradeDecline", "TradeReset", "TradeEnd",
        })
            Fn(kind, kind);
        foreach (string kind in UnsupportedActions.Keys) Fn(kind, kind);
        actions["RunAllOrdered"] = DynValue.NewCallback((c, a) => CreateRunAllOrdered(s, ctx, LuaArgs.Of(a, actions)));

        var mt = new Table(s);
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString("game.Actions"));
        actions.MetaTable = mt;

        game["Actions"] = DynValue.NewTable(actions);
        game["ActionQueue"] = CreateQueueTable(s, ctx);

        // UB scripts compare against ActionError.X / pass ActionType.X as a priority.
        if (s.Globals.Get("ActionError").IsNil())
        {
            var errors = new Table(s);
            foreach (string e in ActionErrors.All) errors[e] = e;
            s.Globals["ActionError"] = DynValue.NewTable(errors);
        }
        if (s.Globals.Get("ActionType").IsNil())
        {
            var types = new Table(s);
            foreach (var (n, v) in ActionPriority.All) types[n] = (double)v;
            s.Globals["ActionType"] = DynValue.NewTable(types);
        }
        // UB's ActionOptions type: ActionOptions.new() / ActionOptions() give an options table
        // (Name, Priority, SkipChecks, MaxRetryCount, TimeoutMilliseconds) to fill in.
        if (s.Globals.Get("ActionOptions").IsNil())
        {
            var ao = new Table(s);
            DynValue make = DynValue.NewCallback((c, a) =>
            {
                var o = new Table(s);
                o["SkipChecks"] = false;
                return DynValue.NewTable(o);
            });
            ao["new"] = make;
            ao["__new"] = make;
            var aoMeta = new Table(s);
            aoMeta["__call"] = DynValue.NewCallback((c, a) => make.Callback.ClrCallback(c, a));
            aoMeta["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString("ActionOptions"));
            ao.MetaTable = aoMeta;
            s.Globals["ActionOptions"] = DynValue.NewTable(ao);
        }

        ctx.OnStop.Add(() => CancelActionsOf(ctx, "the script stopped"));
        // A script whose main code ends with actions still queued keeps running until they finish
        // (so their callbacks run).
        ctx.KeepAlive.Add(() =>
        {
            int n = 0;
            foreach (LuaAction a in _queuedActions) if (a.Ctx == ctx && a.Phase != ActionPhase.Finished) n++;
            foreach (LuaAction a in _immediateActions) if (a.Ctx == ctx && a.Phase != ActionPhase.Finished) n++;
            return n;
        });
    }

    /// <summary>WorldObject action methods (raw fields): Use, UseOn, Give, Move, Split, Drop, Select, Appraise, Wield.</summary>
    partial void DecorateObjectActions(Script s, ScriptContext ctx, uint id, Table obj)
    {
        DynValue idv = DynValue.NewNumber(id);
        DynValue[] With(DynValue[] rest)
        {
            var r = new DynValue[rest.Length + 1];
            r[0] = idv;
            Array.Copy(rest, 0, r, 1, rest.Length);
            return r;
        }
        void M(string name, string kind) =>
            obj.Set(name, DynValue.NewCallback((c, a) => CreateAction(s, ctx, kind, With(LuaArgs.Of(a, obj)))));

        M("Use", "ObjectUse");
        M("UseOn", "ObjectUseOn");
        M("Give", "ObjectGive");
        M("Move", "ObjectMove");
        M("Drop", "ObjectDrop");
        M("Select", "ObjectSelect");
        M("Appraise", "ObjectAppraise");
        M("Wield", "ObjectWield");
        M("AddToSalvage", "SalvageAdd");
        M("AddToTrade", "TradeAdd");
        M("Inscribe", "Inscribe");
        // Salvage(): UB's RunAllOrdered of SalvageAdd(this) and Salvage().
        obj.Set("Salvage", DynValue.NewCallback((c, a) =>
        {
            SplitArgs(LuaArgs.Of(a, obj), out _, out ActionOptions o, out DynValue cb);
            DynValue add = Finish(s, ctx, BuildAction(ctx, "SalvageAdd", new List<DynValue> { idv }), default, DynValue.Nil);
            DynValue run = Finish(s, ctx, BuildAction(ctx, "Salvage", new List<DynValue>()), default, DynValue.Nil);
            var list = new Table(s);
            list.Set(1, add);
            list.Set(2, run);
            return CreateRunAllOrderedWith(s, ctx, list, o, cb);
        }));
        // Split(amount [, container [, slot]]) → ObjectSplit(id, container, amount, slot)
        obj.Set("Split", DynValue.NewCallback((c, a) =>
        {
            SplitArgs(LuaArgs.Of(a, obj), out var pos, out ActionOptions o, out DynValue cb);
            var p = new List<DynValue> { idv, Pos(pos, 1), Pos(pos, 0), Pos(pos, 2) };
            return Finish(s, ctx, BuildAction(ctx, "ObjectSplit", p), o, cb);
        }));
    }

    // ── Creating actions ────────────────────────────────────────────────────

    private DynValue CreateAction(Script s, ScriptContext ctx, string kind, DynValue[] args)
    {
        SplitArgs(args, out var pos, out ActionOptions o, out DynValue cb);
        return Finish(s, ctx, BuildAction(ctx, kind, pos), o, cb);
    }

    private DynValue Finish(Script s, ScriptContext ctx, LuaAction action, ActionOptions o, DynValue cb)
    {
        action.ApplyOptions(o);
        action.Callback = cb;
        action.CreateTable(s, MakeAwait(s));
        _actionsByTable[action.T] = action;
        if (action.Immediate) _immediateActions.Add(action);
        else _queuedActions.Add(action);
        return DynValue.NewTable(action.T);
    }

    private LuaAction BuildAction(ScriptContext ctx, string kind, List<DynValue> pos)
    {
        switch (kind)
        {
            case "ObjectUse":
                return new UseAction(this, ctx, Obj(pos, 0, kind), OptObj(pos, 1), on: false);
            case "ObjectUseOn":
                return new UseAction(this, ctx, Obj(pos, 0, kind), Obj(pos, 1, kind, "target"), on: true);
            case "ObjectGive":
                return new GiveAction(this, ctx, Obj(pos, 0, kind), Obj(pos, 1, kind, "target"));
            case "ObjectMove":
            {
                uint id = Obj(pos, 0, kind);
                uint container = Obj(pos, 1, kind, "container");
                int slot = (int)LuaArgs.Num(Pos(pos, 2), 0);
                DynValue st = Pos(pos, 3);
                bool stack = st.IsNil() || st.CastToBool();
                return new MoveAction(this, ctx, id, container, slot, stack);
            }
            case "ObjectSplit":
            {
                // UB: (object, container, newStackSize [, slot]); short form: (object, newStackSize).
                uint id = Obj(pos, 0, kind);
                uint container;
                int amount, slot;
                if (pos.Count >= 3)
                {
                    container = OptObj(pos, 1);
                    amount = (int)LuaArgs.Num(Pos(pos, 2), 0);
                    slot = (int)LuaArgs.Num(Pos(pos, 3), 0);
                }
                else
                {
                    container = 0;
                    amount = (int)LuaArgs.Num(Pos(pos, 1), 0);
                    slot = 0;
                }
                if (amount < 1) throw new ScriptRuntimeException("ObjectSplit needs the size of the new stack (1 or more)");
                return new SplitAction(this, ctx, id, container, amount, slot);
            }
            case "ObjectDrop":
                return new DropAction(this, ctx, Obj(pos, 0, kind));
            case "ObjectWield":
                return new WieldAction(this, ctx, Obj(pos, 0, kind), (uint)LuaArgs.Num(Pos(pos, 1), 0));
            case "ObjectSelect":
                return new SelectAction(this, ctx, Obj(pos, 0, kind));
            case "ObjectAppraise":
                return new AppraiseAction(this, ctx, Obj(pos, 0, kind));
            case "CastSpell":
            {
                DynValue sp = Pos(pos, 0);
                if (sp.Type != DataType.Number && !double.TryParse(sp.CastToString(), out _))
                    throw new ScriptRuntimeException("CastSpell(spellId [, target]) needs a spell id");
                return new CastSpellAction(this, ctx, (int)LuaArgs.Num(sp), OptObj(pos, 1));
            }
            case "SetCombatMode":
                return new CombatModeAction(this, ctx, CombatModeArg(Pos(pos, 0)));
            case "InvokeChat":
            {
                string text = LuaArgs.Str(Pos(pos, 0)).Trim();
                if (text.Length == 0) throw new ScriptRuntimeException("InvokeChat(text) needs some text");
                return new InvokeChatAction(this, ctx, text);
            }
            case "Sleep":
            {
                DynValue ms = Pos(pos, 0);
                if (ms.Type != DataType.Number) throw new ScriptRuntimeException("Sleep(milliseconds) needs a number");
                return new SleepAction(this, ctx, (int)Math.Min(int.MaxValue, Math.Max(0, ms.Number)));
            }
            case "Blank":
                return new BlankAction(this, ctx);
            case "SetAutorun":
                return new AutorunAction(this, ctx, Pos(pos, 0).CastToBool());
            case "SendTellById":
            {
                uint id = Obj(pos, 0, kind, "player");
                string text = LuaArgs.Str(Pos(pos, 1)).Trim();
                if (text.Length == 0) throw new ScriptRuntimeException("SendTellById(id, message) needs a message");
                return new TellAction(this, ctx, id, text);
            }
            case "CastEquippedWandSpell":
                return new WandCastAction(this, ctx, OptObj(pos, 0));
            case "SalvageAdd":
                return new SalvageAddAction(this, ctx, Obj(pos, 0, kind));
            case "Salvage":
                return new SalvageAction(this, ctx);
            case "VendorAddToBuyList":
            {
                DynValue amt = Pos(pos, 1);
                return new VendorListAction(this, ctx, VendorListAction.Op.AddBuy, Obj(pos, 0, kind), amt.IsNil() ? 1 : (int)LuaArgs.Num(amt, 1));
            }
            case "VendorAddToSellList":
                return new VendorListAction(this, ctx, VendorListAction.Op.AddSell, Obj(pos, 0, kind), 0);
            case "VendorClearBuyList":
                return new VendorListAction(this, ctx, VendorListAction.Op.ClearBuy, 0, 0);
            case "VendorClearSellList":
                return new VendorListAction(this, ctx, VendorListAction.Op.ClearSell, 0, 0);
            case "VendorBuyAll":
                return new VendorTradeAction(this, ctx, buy: true);
            case "VendorSellAll":
                return new VendorTradeAction(this, ctx, buy: false);
            case "TradeAdd":
                return new TradeAction(this, ctx, TradeAction.Op.Add, Obj(pos, 0, kind));
            case "TradeAccept":
                return new TradeAction(this, ctx, TradeAction.Op.Accept);
            case "TradeDecline":
                return new TradeAction(this, ctx, TradeAction.Op.Decline);
            case "TradeReset":
                return new TradeAction(this, ctx, TradeAction.Op.Reset);
            case "TradeEnd":
                return new TradeAction(this, ctx, TradeAction.Op.End);
        }
        if (UnsupportedActions.TryGetValue(kind, out string? why))
            return new UnsupportedAction(this, ctx, kind, why);
        throw new ScriptRuntimeException($"unknown action {kind}");
    }

    /// <summary>UB actions the engine can't do yet, and why (they fail with that reason).</summary>
    private static readonly Dictionary<string, string> UnsupportedActions = new()
    {
        ["Login"] = "no host function logs a character in",
        ["Logout"] = "no host function logs out",
        ["SkillAdvance"] = "no host function trains a skill",
        ["AttributeAddExperience"] = "no host function spends experience",
        ["SkillAddExperience"] = "no host function spends experience",
        ["VitalAddExperience"] = "no host function spends experience",
        ["FellowCreate"] = "no host functions for the fellowship",
        ["FellowDisband"] = "no host functions for the fellowship",
        ["FellowDismiss"] = "no host functions for the fellowship",
        ["FellowSetLeader"] = "no host functions for the fellowship",
        ["FellowQuit"] = "no host functions for the fellowship",
        ["FellowRecruit"] = "no host functions for the fellowship",
        ["FellowSetOpen"] = "no host functions for the fellowship",
        ["AllegianceSwear"] = "no host functions for the allegiance",
        ["AllegianceBreak"] = "no host functions for the allegiance",
        ["Inscribe"] = "no host function inscribes an item",
    };

    private DynValue CreateRunAllOrdered(Script s, ScriptContext ctx, DynValue[] args)
    {
        DynValue listArg = args.Length > 0 ? args[0] : DynValue.Nil;
        if (listArg.Type != DataType.Table)
            throw new ScriptRuntimeException("RunAllOrdered({ action, action, ... } [, options] [, callback]) needs a list of actions");
        SplitArgs(args.Skip(1).ToArray(), out _, out ActionOptions o, out DynValue cb);
        return CreateRunAllOrderedWith(s, ctx, listArg.Table, o, cb);
    }

    private DynValue CreateRunAllOrderedWith(Script s, ScriptContext ctx, Table list, ActionOptions o, DynValue cb)
    {
        var children = new List<LuaAction?>();
        var tables = new List<Table>();
        for (int i = 1; i <= list.Length; i++)
        {
            DynValue v = list.Get(i);
            if (v.Type != DataType.Table || v.Table.RawGet("__rynth_action") == null)
                throw new ScriptRuntimeException($"RunAllOrdered: item {i} isn't an action (make them with game.Actions...)");
            Table t = v.Table;
            _actionsByTable.TryGetValue(t, out LuaAction? child);
            if (child != null && child.Ctx != ctx)
                throw new ScriptRuntimeException($"RunAllOrdered: item {i} belongs to another script");
            if (child != null && child.Owner != null)
                throw new ScriptRuntimeException($"RunAllOrdered: item {i} is already in another RunAllOrdered");
            children.Add(child);
            tables.Add(t);
        }

        var run = new RunAllOrderedAction(this, ctx, children, tables);
        // Take the actions that haven't started out of the queue: the list runs them.
        foreach (LuaAction? c in children)
        {
            if (c == null || c.Phase != ActionPhase.Queued) continue;
            _queuedActions.Remove(c);
            _immediateActions.Remove(c);
            c.Owner = run;
        }
        return Finish(s, ctx, run, o, cb);
    }

    /// <summary>A per-script Lua factory for the actions' Await() closures.</summary>
    private static DynValue MakeAwait(Script s)
    {
        DynValue f = s.Registry.Get(MakeAwaitKey);
        if (f.Type == DataType.Function) return f;
        try
        {
            f = s.DoString("return function(a) return function() return await(a) end end", null, "rynth-actions");
            s.Registry.Set(MakeAwaitKey, f);
        }
        catch { f = DynValue.Nil; }
        return f;
    }

    // ── Argument helpers ────────────────────────────────────────────────────

    /// <summary>Positional arguments, then an options table and a callback in either order.</summary>
    private static void SplitArgs(DynValue[] args, out List<DynValue> pos, out ActionOptions options, out DynValue callback)
    {
        pos = new List<DynValue>();
        options = default;
        callback = DynValue.Nil;
        foreach (DynValue v in args)
        {
            if (v.Type == DataType.Function) { callback = v; continue; }
            if (v.Type == DataType.ClrFunction) throw new ScriptRuntimeException("the action callback must be a Lua function");
            if (v.Type == DataType.Table && !IsObjectTable(v.Table) && !LuaEnumValues.IsTyped(v)) { options = ParseOptions(v.Table); continue; }
            pos.Add(v);
        }
        // Trailing nils (e.g. f(id, nil, callback)) are just missing arguments.
        while (pos.Count > 0 && pos[^1].IsNil()) pos.RemoveAt(pos.Count - 1);
    }

    private static bool IsObjectTable(Table t) =>
        t.RawGet("__rynth_object") != null || t.RawGet("Id")?.Type == DataType.Number;

    private static ActionOptions ParseOptions(Table t)
    {
        var o = new ActionOptions();
        DynValue p = t.Get("Priority");
        if (p.Type == DataType.Number) o.Priority = (int)Math.Clamp(p.Number, int.MinValue, int.MaxValue);
        else if (p.Type == DataType.String)
        {
            if (!ActionPriority.TryParse(p.String, out int pv))
                throw new ScriptRuntimeException($"Priority \"{p.String}\": use a number or an ActionType name (Wield, CastSpell, Inventory, Misc, ...)");
            o.Priority = pv;
        }
        DynValue to = t.Get("TimeoutMilliseconds");
        if (to.Type == DataType.Number) o.TimeoutMs = (int)Math.Clamp(to.Number, 0, int.MaxValue);
        DynValue r = t.Get("MaxRetryCount");
        if (r.Type == DataType.Number) o.MaxRetryCount = (int)Math.Clamp(r.Number, 0, 1000);
        o.SkipChecks = t.Get("SkipChecks").CastToBool();
        DynValue n = t.Get("Name");
        if (n.Type == DataType.String && n.String.Length > 0) o.Name = n.String;
        return o;
    }

    private static DynValue Pos(List<DynValue> pos, int i) => i < pos.Count ? pos[i] : DynValue.Nil;

    private static uint Obj(List<DynValue> pos, int i, string fn, string what = "object")
    {
        uint id = LuaArgs.Id(Pos(pos, i));
        if (id == 0) throw new ScriptRuntimeException($"{fn} needs {(what == "object" ? "an object" : "a " + what)} (a WorldObject or an id)");
        return id;
    }

    private static uint OptObj(List<DynValue> pos, int i) => LuaArgs.Id(Pos(pos, i));

    private static int CombatModeArg(DynValue v)
    {
        if (v.Type == DataType.Number)
        {
            int m = (int)v.Number;
            if (m is 1 or 2 or 4 or 8) return m;
        }
        else if (v.Type == DataType.String && LuaEnums.CombatMode.TryGetValue(v.String.Trim(), out int named))
            return named;
        throw new ScriptRuntimeException("SetCombatMode(mode) needs CombatMode.NonCombat/Melee/Missile/Magic (1, 2, 4, 8) or its name");
    }

    // ── The queue ───────────────────────────────────────────────────────────

    partial void ActionsTick()
    {
        if (_currentAction == null && _queuedActions.Count == 0 && _immediateActions.Count == 0) return;
        long now = Environment.TickCount64;

        if (_immediateActions.Count > 0)
        {
            foreach (LuaAction a in _immediateActions.ToArray())
                if (a.Owner == null) StepAction(a, now);
            _immediateActions.RemoveAll(a => a.Phase == ActionPhase.Finished || a.Owner != null);
        }

        if (_currentAction != null && _currentAction.Phase == ActionPhase.Finished) _currentAction = null;
        _queuedActions.RemoveAll(a => a.Phase == ActionPhase.Finished || a.Owner != null);

        if (_currentAction == null && _queuedActions.Count > 0)
        {
            // Highest priority first; the same priority in the order queued.
            LuaAction best = _queuedActions[0];
            foreach (LuaAction a in _queuedActions)
                if (a.Priority > best.Priority || (a.Priority == best.Priority && a.ActionId < best.ActionId)) best = a;
            _queuedActions.Remove(best);
            _currentAction = best;
        }

        if (_currentAction != null)
        {
            StepAction(_currentAction, now);
            if (_currentAction.Phase == ActionPhase.Finished) _currentAction = null;
        }
    }

    private static void StepAction(LuaAction a, long now)
    {
        if (!a.Ctx.Host.Loaded) { a.Cancel("the script stopped"); return; }
        try { a.Step(now); }
        catch (Exception ex) { a.Fail(ActionErrors.Exception, ex.Message); }
    }

    /// <summary>An action finished (any way): it isn't in the queue anymore.</summary>
    internal void OnActionFinished(LuaAction a)
    {
        if (a.T != null) _actionsByTable.Remove(a.T);
    }

    partial void ActionsOnChatText(string text, int chatType)
    {
        LuaAction? a = _currentAction;
        if (a == null) return;
        string t = text.Trim();
        if (t.Length == 0 || ActionChat.IsSpeech(t)) return;
        ChatHint hint = ActionChat.Classify(t, out string error, out bool spellOnly, out bool coded);
        if (hint == ChatHint.None) return;
        try { a.HandleChat(t, hint, error, spellOnly, coded); }
        catch (Exception ex) { a.Fail(ActionErrors.Exception, ex.Message); }
    }

    partial void ActionsOnLogout()
    {
        foreach (LuaAction a in AllLiveActions()) a.Cancel("logged out");
        _queuedActions.Clear();
        _immediateActions.Clear();
        _currentAction = null;
    }

    private void CancelActionsOf(ScriptContext ctx, string why)
    {
        foreach (LuaAction a in AllLiveActions())
            if (a.Ctx == ctx) a.Cancel(why);
        _queuedActions.RemoveAll(a => a.Phase == ActionPhase.Finished);
        _immediateActions.RemoveAll(a => a.Phase == ActionPhase.Finished);
        if (_currentAction?.Phase == ActionPhase.Finished) _currentAction = null;
    }

    /// <summary>Running first, then the queue in run order, then the immediate ones.</summary>
    private List<LuaAction> AllLiveActions()
    {
        var list = new List<LuaAction>();
        if (_currentAction != null && _currentAction.Phase != ActionPhase.Finished) list.Add(_currentAction);
        list.AddRange(_queuedActions.Where(a => a.Phase != ActionPhase.Finished && a.Owner == null)
            .OrderByDescending(a => a.Priority).ThenBy(a => a.ActionId));
        list.AddRange(_immediateActions.Where(a => a.Phase != ActionPhase.Finished && a.Owner == null));
        return list;
    }

    // ── game.ActionQueue ────────────────────────────────────────────────────

    /// <summary>
    /// game.ActionQueue: <c>Queue</c> (running + waiting, in run order), <c>ImmediateQueue</c>,
    /// <c>Count</c>, <c>Current</c>, <c>IsBusy</c>; <c>Contains(action)</c>, <c>Add(action)</c>,
    /// <c>Remove(action)</c>, <c>Clear()</c>. Another script's action shows as a summary table
    /// (Name, ActionType, Priority, ScriptName, IsStarted, IsRunning); Remove and Clear only
    /// touch this script's own actions.
    /// </summary>
    private DynValue CreateQueueTable(Script s, ScriptContext ctx)
    {
        var q = new Table(s);
        LuaAction? Lookup(DynValue v) =>
            v.Type == DataType.Table && _actionsByTable.TryGetValue(v.Table, out LuaAction? a) ? a : null;

        q["Contains"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(Lookup(LuaArgs.At(LuaArgs.Of(a, q), 0)) != null));
        // Actions are queued when they're made; Add only reports whether this one is (still) queued.
        q["Add"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(Lookup(LuaArgs.At(LuaArgs.Of(a, q), 0)) != null));
        q["Remove"] = DynValue.NewCallback((c, a) =>
        {
            LuaAction? act = Lookup(LuaArgs.At(LuaArgs.Of(a, q), 0));
            if (act == null) return DynValue.False;
            if (act.Ctx != ctx) throw new ScriptRuntimeException("ActionQueue.Remove: that action belongs to another script");
            act.Cancel("removed from the queue");
            return DynValue.True;
        });
        q["Clear"] = DynValue.NewCallback((c, a) =>
        {
            int n = AllLiveActions().Count(x => x.Ctx == ctx);
            CancelActionsOf(ctx, "the queue was cleared");
            return DynValue.NewNumber(n);
        });

        DynValue View(LuaAction a)
        {
            if (a.Ctx == ctx) return DynValue.NewTable(a.T);
            var t = new Table(s);
            t["Name"] = a.Name;
            t["ActionType"] = (double)a.Category;
            t["Kind"] = a.Kind;
            t["Priority"] = (double)a.Priority;
            t["ScriptName"] = a.Ctx.Name;
            t["IsStarted"] = a.Phase != ActionPhase.Queued;
            t["IsRunning"] = a.Phase is ActionPhase.Waiting or ActionPhase.Sent or ActionPhase.RetryWait;
            t["IsFinished"] = false;
            return DynValue.NewTable(t);
        }
        DynValue ListOf(IEnumerable<LuaAction> actions)
        {
            var t = new Table(s);
            int i = 1;
            foreach (LuaAction a in actions) t.Set(i++, View(a));
            return DynValue.NewTable(t);
        }

        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            switch (key)
            {
                case "Queue":          return ListOf(AllLiveActions().Where(x => !x.Immediate));
                case "ImmediateQueue": return ListOf(AllLiveActions().Where(x => x.Immediate));
                case "Count":          return DynValue.NewNumber(AllLiveActions().Count);
                case "Current":        return _currentAction != null && _currentAction.Phase != ActionPhase.Finished ? View(_currentAction) : DynValue.Nil;
                case "IsBusy":         return DynValue.NewBoolean(_currentAction != null && _currentAction.Phase != ActionPhase.Finished);
                default:               return DynValue.Nil;
            }
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
            DynValue.NewString($"ActionQueue ({AllLiveActions().Count} action(s){(_currentAction != null ? ", running " + _currentAction.Name : "")})"));
        q.MetaTable = mt;
        return DynValue.NewTable(q);
    }
}
