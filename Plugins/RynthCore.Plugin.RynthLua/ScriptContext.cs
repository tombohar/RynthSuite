using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>Optional <c>script.json</c> next to a folder script's <c>index.lua</c>.</summary>
public sealed class ScriptManifest
{
    [JsonPropertyName("name")]        public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("version")]     public string? Version { get; set; }
    [JsonPropertyName("author")]      public string? Author { get; set; }
    /// <summary>"none" (the default) | "global" | "character": seeds autostart once; the player can change it.</summary>
    [JsonPropertyName("autostart")]   public string? Autostart { get; set; }
}

/// <summary>
/// One script: where it lives, its Lua state (<see cref="LuaScriptHost"/>), and everything
/// it registered through the v2 API (events, commands). Stopping the script clears those.
/// </summary>
internal sealed class ScriptContext
{
    public string Name { get; }
    /// <summary>The script's folder (folder scripts) or null (single-file scripts).</summary>
    public string? Folder { get; }
    /// <summary>index.lua (folder scripts) or Name.lua.</summary>
    public string EntryFile { get; }
    public ScriptManifest Manifest { get; }
    public LuaScriptHost Host { get; }
    /// <summary>True when the script came from the panel editor or /lua exec, not a file.</summary>
    public bool Transient { get; init; }

    private readonly Dictionary<string, ScriptEvent> _events = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Chat commands this script registered ("/name" → handler).</summary>
    public readonly Dictionary<string, DynValue> Commands = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Anything else an API piece wants dropped when the script stops (actions, timers, ...).</summary>
    public readonly List<Action> OnStop = new();
    /// <summary>Counts of pending work that keeps the script alive (e.g. its queued actions).</summary>
    public readonly List<Func<int>> KeepAlive = new();

    public ScriptContext(string name, string? folder, string entryFile, ScriptManifest manifest, LuaScriptHost host)
    {
        Name = name;
        Folder = folder;
        EntryFile = entryFile;
        Manifest = manifest;
        Host = host;
        host.Folder = folder;
        host.ExtraHandlers = () =>
        {
            int n = Commands.Count;
            foreach (var e in _events.Values) n += e.Count;
            foreach (var k in KeepAlive)
            {
                try { n += k(); } catch { }
            }
            return n;
        };
        host.Stopped += _ => Clear();
    }

    /// <summary>The event registered under <paramref name="key"/> (e.g. "World.OnObjectCreated"), created on first use.</summary>
    public ScriptEvent Event(string key)
    {
        if (!_events.TryGetValue(key, out var e)) _events[key] = e = new ScriptEvent(key);
        return e;
    }

    public bool HasHandlers(string key) => _events.TryGetValue(key, out var e) && e.Count > 0;

    /// <summary>Keys starting with <paramref name="prefix"/> that have handlers (per-object events).</summary>
    public IEnumerable<string> EventKeys(string prefix)
    {
        foreach (var kv in _events)
            if (kv.Value.Count > 0 && kv.Key.StartsWith(prefix, StringComparison.Ordinal)) yield return kv.Key;
    }

    /// <summary>Forgets an event and its handlers (a per-object event whose object is gone).</summary>
    public void DropEvent(string key)
    {
        if (_events.TryGetValue(key, out var e)) e.Clear();
    }

    /// <summary>Runs every handler of <paramref name="key"/> with an args table built for this script's Lua state.</summary>
    public void Fire(string key, Func<Script, DynValue> makeArgs)
    {
        if (!Host.Loaded || !_events.TryGetValue(key, out var e) || e.Count == 0) return;
        Script? s = Host.Lua;
        if (s == null) return;
        DynValue args;
        try { args = makeArgs(s); }
        catch (Exception ex) { Host.Print($"error building {key} args: {ex.Message}"); return; }
        e.Fire(Host, args);
    }

    /// <summary>
    /// Calls every handler of <paramref name="key"/> right now, outside coroutines (handlers
    /// can't sleep). For events that must run before the Lua state goes away (OnScriptEnd).
    /// </summary>
    public void FireNow(string key, Func<Script, DynValue> makeArgs)
    {
        if (!Host.Loaded || !_events.TryGetValue(key, out var e) || e.Count == 0) return;
        Script? s = Host.Lua;
        if (s == null) return;
        DynValue args;
        try { args = makeArgs(s); }
        catch (Exception ex) { Host.Print($"error building {key} args: {ex.Message}"); return; }
        e.CallNow(s, Host, args);
    }

    private void Clear()
    {
        _events.Clear();
        Commands.Clear();
        KeepAlive.Clear();
        foreach (Action a in OnStop)
        {
            try { a(); } catch { }
        }
        OnStop.Clear();
    }
}

/// <summary>
/// A v2 event (UtilityBelt style): a Lua object with <c>Add(fn)</c>, <c>Once(fn)</c>,
/// <c>Until(fn)</c> (kept until the handler returns true) and <c>Remove(fn)</c>. Each handler
/// call runs as its own coroutine, with one args table.
/// </summary>
internal sealed class ScriptEvent
{
    private enum Mode { Always, Once }

    private readonly List<(DynValue Fn, Mode Mode, DynValue Original)> _handlers = new();
    public string Key { get; }
    public int Count => _handlers.Count;

    public ScriptEvent(string key) => Key = key;

    /// <summary>The Lua-side object for this event, in <paramref name="s"/>.</summary>
    public DynValue CreateTable(Script s)
    {
        var t = new Table(s);
        DynValue self = DynValue.NewTable(t);
        t["Add"] = DynValue.NewCallback((ctx, a) => { Add(LuaArgs.Func(a, t, "Add"), Mode.Always); return DynValue.Nil; });
        t["Once"] = DynValue.NewCallback((ctx, a) => { Add(LuaArgs.Func(a, t, "Once"), Mode.Once); return DynValue.Nil; });
        t["Until"] = DynValue.NewCallback((ctx, a) =>
        {
            DynValue fn = LuaArgs.Func(a, t, "Until");
            // Wrapped in Lua: the handler's return value is only known once its coroutine
            // finishes, so the wrapper removes itself when fn returns true (and skips calls
            // for events that were already on their way when it did).
            DynValue make = s.LoadString(
                "local evt, fn = ...; local w, done; w = function(e) if done then return end; if fn(e) then done = true; evt.Remove(w) end end; return w");
            DynValue wrapper = s.Call(make, self, fn);
            _handlers.Add((wrapper, Mode.Always, fn));
            return DynValue.Nil;
        });
        t["Remove"] = DynValue.NewCallback((ctx, a) =>
        {
            DynValue fn = LuaArgs.Func(a, t, "Remove");
            _handlers.RemoveAll(h => h.Fn.Equals(fn) || h.Original.Equals(fn));
            return DynValue.Nil;
        });
        // UB's verbs in lower case too (evt.add(fn), evt.once(fn), ...).
        t["add"] = t.Get("Add");
        t["once"] = t.Get("Once");
        t["until"] = t.Get("Until");
        t["remove"] = t.Get("Remove");
        var mt = new Table(s);
        mt["__tostring"] = DynValue.NewCallback((ctx, a) => DynValue.NewString($"Event {Key} ({Count} handler(s))"));
        t.MetaTable = mt;
        return self;
    }

    private void Add(DynValue fn, Mode mode) => _handlers.Add((fn, mode, fn));

    /// <summary>
    /// The handlers, for a caller that runs them itself in one coroutine (a hud's render
    /// pass). Once handlers are removed, as <see cref="Fire"/> does.
    /// </summary>
    public DynValue[] TakeForCall()
    {
        if (_handlers.Count == 0) return Array.Empty<DynValue>();
        var fns = new DynValue[_handlers.Count];
        for (int i = 0; i < fns.Length; i++) fns[i] = _handlers[i].Fn;
        _handlers.RemoveAll(h => h.Mode == Mode.Once);
        return fns;
    }

    /// <summary>Drops every handler (hud.Dispose).</summary>
    public void Clear() => _handlers.Clear();

    public void CallNow(Script s, LuaScriptHost host, DynValue args)
    {
        if (_handlers.Count == 0) return;
        var snapshot = _handlers.ToArray();
        _handlers.RemoveAll(h => h.Mode == Mode.Once);
        foreach (var h in snapshot)
        {
            try { s.Call(h.Fn, args); }
            catch (InterpreterException ex) { host.Print($"{Key}: " + (ex.DecoratedMessage ?? ex.Message)); }
            catch (Exception ex) { host.Print($"{Key}: {ex.Message}"); }
        }
    }

    public void Fire(LuaScriptHost host, DynValue args)
    {
        if (_handlers.Count == 0) return;
        var snapshot = _handlers.ToArray();
        _handlers.RemoveAll(h => h.Mode == Mode.Once);
        foreach (var h in snapshot) host.Start(h.Fn, new[] { args });
    }
}

/// <summary>
/// Argument helpers for API callbacks. Methods accept both <c>obj.Method(a)</c> and
/// <c>obj:Method(a)</c>: a leading argument that is the object's own table is dropped.
/// </summary>
internal static class LuaArgs
{
    public static DynValue[] Of(CallbackArguments a, Table? self)
    {
        int start = self != null && a.Count > 0 && a[0].Type == DataType.Table && ReferenceEquals(a[0].Table, self) ? 1 : 0;
        var r = new DynValue[Math.Max(0, a.Count - start)];
        for (int i = start; i < a.Count; i++) r[i - start] = a[i];
        return r;
    }

    public static DynValue At(DynValue[] args, int i) => i < args.Length ? args[i] : DynValue.Nil;

    public static DynValue Func(CallbackArguments a, Table self, string what)
    {
        DynValue fn = At(Of(a, self), 0);
        if (fn.Type != DataType.Function && fn.Type != DataType.ClrFunction)
            throw new ScriptRuntimeException($"{what}(function) needs a function");
        return fn;
    }

    public static string Str(DynValue v, string fallback = "") => v.IsNil() ? fallback : v.CastToString() ?? fallback;

    public static double Num(DynValue v, double fallback = 0) =>
        v.Type == DataType.Number ? v.Number
        : LuaEnumValues.TryRead(v, out _, out long typed) ? typed
        : double.TryParse(v.CastToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : fallback;

    public static uint Id(DynValue v)
    {
        if (v.Type == DataType.Number) return (uint)v.Number;
        if (v.Type == DataType.Table)
        {
            DynValue id = v.Table.Get("Id");
            if (id.Type == DataType.Number) return (uint)id.Number;
        }
        return 0;
    }
}
