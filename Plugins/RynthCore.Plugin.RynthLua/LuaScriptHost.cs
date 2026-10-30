using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// One script's Lua state (MoonSharp, Lua 5.2), run on the plugin pump thread.
///
/// Every piece of script code runs as a coroutine ("thread") resumed from
/// <see cref="Tick"/>: the main chunk, each event handler call and each
/// <c>spawn</c>. <c>sleep(ms)</c> parks the thread until its wake time, <c>await(action)</c>
/// until the action's <c>IsFinished</c> is true, and MoonSharp's auto-yield pauses any
/// thread that runs too long without sleeping, so an endless loop slows the script down
/// instead of freezing the client. A script stays running while it has live threads,
/// handlers (v1 <c>on</c>, v2 events, commands) or a global <c>OnBotTick</c>; Stop clears them.
/// </summary>
internal sealed class LuaScriptHost
{
    /// <summary>C:\Games\RynthSuite\RynthLua\Scripts (see <see cref="RynthLuaPaths"/>).</summary>
    public static string ScriptFolder => RynthLuaPaths.Scripts;
    /// <summary>Where scripts lived while Lua was part of RynthAi (copied over once, then left alone).</summary>
    public static string LegacyScriptFolder => RynthLuaPaths.LegacyScripts;

    private const int MaxConsoleLines   = 400;
    private const int AutoYieldCounter  = 5_000;   // VM instructions before a forced pause

    private sealed class LuaThread
    {
        public DynValue Co = DynValue.Nil;
        public long WakeAt;
        public Func<bool>? WaitFor;     // await: resume only once this is true
        public DynValue[] Args = Array.Empty<DynValue>();
        public bool Started;
        /// <summary>Called once when the thread ends: its return values, or the error message.</summary>
        public Action<DynValue?, string?>? OnDone;
    }

    private readonly Action<string> _log;
    private readonly Action<Script, LuaScriptHost> _registerApi;
    private readonly List<LuaThread> _threads = new();
    private readonly Dictionary<string, List<DynValue>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _console = new();
    private readonly object _consoleLock = new();
    private Script? _script;
    private LuaThread? _current;
    private int _resumeFrom;   // round-robin start within this script

    /// <summary>The script's folder for <c>require</c> (null: single-file script, only built-in modules).</summary>
    public string? Folder { get; set; }

    /// <summary>Built-in modules for <c>require(name)</c>, created per script on first use.</summary>
    public Func<Script, string, DynValue?>? ResolveModule;

    /// <summary>Extra "keeps the script alive" count (v2 events, commands) from the ScriptContext.</summary>
    public Func<int>? ExtraHandlers;

    /// <summary>Called once when the script stops for any reason (cleanup of v2 registrations).</summary>
    public Action<string>? Stopped;

    /// <summary>Runs when the script calls stop(), before its state goes away (game.OnScriptEnd).</summary>
    public Action? BeforeScriptStop;

    public bool Running => _script != null && (_threads.Count > 0 || HandlerCount > 0 || HasBotTick || (ExtraHandlers?.Invoke() ?? 0) > 0);
    public bool Loaded => _script != null;
    public Script? Lua => _script;

    /// <summary>True while the RynthAi macro runs; gates OnBotTick (the Decal RynthAi's hook).</summary>
    public Func<bool>? MacroRunning;
    private const long BotTickMs = 250;
    private long _nextBotTick;
    private LuaThread? _botTickThread;

    private bool HasBotTick => _script != null && _script.Globals.Get("OnBotTick").Type == DataType.Function;
    public string ScriptName { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public int ConsoleSeq { get; private set; }

    private int HandlerCount
    {
        get { int n = 0; foreach (var l in _handlers.Values) n += l.Count; return n; }
    }

    public LuaScriptHost(Action<string> log, Action<Script, LuaScriptHost> registerApi)
    {
        _log = log;
        _registerApi = registerApi;
    }

    // ── Console ─────────────────────────────────────────────────────────────

    public void Print(string text)
    {
        lock (_consoleLock)
        {
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                _console.AddLast(line);
                if (_console.Count > MaxConsoleLines) _console.RemoveFirst();
            }
            ConsoleSeq++;
        }
    }

    public string ConsoleText()
    {
        lock (_consoleLock) return string.Join("\n", _console);
    }

    public void ClearConsole()
    {
        lock (_consoleLock) { _console.Clear(); ConsoleSeq++; }
    }

    // ── Files ───────────────────────────────────────────────────────────────

    /// <summary>Single-file scripts (<c>Name.lua</c>) in the scripts folder.</summary>
    public static List<string> ListScripts()
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(ScriptFolder))
                foreach (string f in Directory.GetFiles(ScriptFolder, "*.lua"))
                    list.Add(Path.GetFileNameWithoutExtension(f));
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Maps a script name to its single file, or null when the name can't be a file name.</summary>
    public static string? PathFor(string name)
    {
        string n = (name ?? string.Empty).Trim();
        if (n.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) n = n[..^4].TrimEnd();
        if (n.Length == 0 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return Path.Combine(ScriptFolder, n + ".lua");
    }

    // ── Run / stop ──────────────────────────────────────────────────────────

    public void Run(string code, string name)
    {
        if (_script != null) Stop("replaced by a new run");

        ScriptName = name ?? string.Empty;
        var script = new Script(CoreModules.Preset_SoftSandbox | CoreModules.Coroutine | CoreModules.OS_Time);
        script.Options.DebugPrint = s => Print(s);
        _script = script;

        try
        {
            RegisterCore(script);
            _registerApi(script, this);
            DynValue chunk = script.LoadString(code, null, ScriptName.Length > 0 ? ScriptName : "editor");
            Start(chunk, Array.Empty<DynValue>());
            Status = string.Empty;
            Print($"--- running {(ScriptName.Length > 0 ? ScriptName : "editor script")} ---");
            _log($"[Lua] Started {(ScriptName.Length > 0 ? ScriptName : "editor script")}");
        }
        catch (InterpreterException ex)
        {
            Print("error: " + (ex.DecoratedMessage ?? ex.Message));
            Stop("failed to load");
        }
        catch (Exception ex)
        {
            Print("error: " + ex.Message);
            Stop("failed to load");
        }
    }

    /// <summary>Runs extra code inside this running script's state (/lua execin), as a new thread.</summary>
    public bool Exec(string code)
    {
        if (_script == null) return false;
        try
        {
            DynValue chunk = _script.LoadString(code, null, "exec");
            Start(chunk, Array.Empty<DynValue>());
            return true;
        }
        catch (InterpreterException ex)
        {
            Print("error: " + (ex.DecoratedMessage ?? ex.Message));
            return false;
        }
    }

    public void Stop(string reason = "stopped")
    {
        bool was = _script != null;
        _threads.Clear();
        _handlers.Clear();
        _current = null;
        _botTickThread = null;
        _script = null;
        if (was)
        {
            Status = $"Lua: {reason}";
            Print($"--- {reason} ---");
            _log($"[Lua] {(ScriptName.Length > 0 ? ScriptName : "editor script")}: {reason}");
            try { Stopped?.Invoke(reason); } catch { }
        }
    }

    /// <summary>Starts <paramref name="fn"/> as a new script thread.</summary>
    public void Start(DynValue fn, DynValue[] args) => Start(fn, args, onDone: null);

    /// <summary>
    /// Starts <paramref name="fn"/> as a new script thread and calls <paramref name="onDone"/> once
    /// it ends: with its return values (a tuple for several, Nil for none) and a null error, or with
    /// a null result and the error message when it raised one. Not called when the script stops
    /// first (its stop handlers clean up), nor after <see cref="Abort"/>. Returns the thread's
    /// handle (for Abort), or null when nothing was started.
    /// </summary>
    public object? Start(DynValue fn, DynValue[] args, Action<DynValue?, string?>? onDone)
    {
        if (_script == null || fn.Type != DataType.Function) return null;
        DynValue co = _script.CreateCoroutine(fn);
        co.Coroutine.AutoYieldCounter = AutoYieldCounter;
        var t = new LuaThread { Co = co, Args = args, OnDone = onDone };
        _threads.Add(t);
        return t;
    }

    /// <summary>
    /// Ends the thread <paramref name="handle"/> (from <see cref="Start(DynValue, DynValue[], Action{DynValue?, string?}?)"/>)
    /// without resuming it again; its onDone is not called. Returns false when it had already ended.
    /// </summary>
    public bool Abort(object? handle)
    {
        if (handle is not LuaThread t) return false;
        t.OnDone = null;
        if (ReferenceEquals(_botTickThread, t)) _botTickThread = null;
        return _threads.Remove(t);
    }

    private static void Done(LuaThread t, DynValue? result, string? error)
    {
        Action<DynValue?, string?>? cb = t.OnDone;
        t.OnDone = null;
        try { cb?.Invoke(result, error); } catch { }
    }

    /// <summary>Calls every v1 handler registered for <paramref name="evt"/>, each on its own thread.</summary>
    public void Fire(string evt, params DynValue[] args)
    {
        if (_script == null || !_handlers.TryGetValue(evt, out var list) || list.Count == 0) return;
        foreach (DynValue fn in list.ToArray()) Start(fn, args);
    }

    public bool HasHandlers(string evt)
        => _script != null && _handlers.TryGetValue(evt, out var l) && l.Count > 0;

    // ── Scheduler ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resumes this script's threads that are due, until <paramref name="deadline"/>
    /// (a Stopwatch timestamp). Threads are taken round-robin, so a busy one can't
    /// starve the others.
    /// </summary>
    public void Tick(long deadline)
    {
        if (_script == null) return;
        if (!Running) { Stop("finished"); return; }

        // OnBotTick(): every 250 ms while the macro runs, one call at a time.
        long nowMs = Environment.TickCount64;
        if (nowMs >= _nextBotTick && HasBotTick && MacroRunning?.Invoke() == true
            && (_botTickThread == null || !_threads.Contains(_botTickThread)))
        {
            _nextBotTick = nowMs + BotTickMs;
            Start(_script.Globals.Get("OnBotTick"), Array.Empty<DynValue>());
            _botTickThread = _threads[^1];
        }

        bool OverBudget() => System.Diagnostics.Stopwatch.GetTimestamp() >= deadline;
        int n = _threads.Count;
        if (n == 0) return;
        if (_resumeFrom >= n) _resumeFrom = 0;
        // Threads that haven't run yet go first, in the order they were started (event handler
        // calls then run in the order their events happened); the running ones take turns
        // round-robin, so a busy one can't starve the others.
        var order = new List<LuaThread>(n);
        foreach (LuaThread t in _threads) if (!t.Started) order.Add(t);
        for (int k = 0; k < n; k++)
        {
            LuaThread t = _threads[(_resumeFrom + k) % n];
            if (t.Started) order.Add(t);
        }
        _resumeFrom++;

        foreach (LuaThread t in order)
        {
            if (_script == null) return;           // the script called stop()
            if (!_threads.Contains(t)) continue;   // removed by an earlier thread (stop in a handler, ...)
            if (Environment.TickCount64 < t.WakeAt) continue;
            if (t.WaitFor != null)
            {
                bool ready;
                try { ready = t.WaitFor(); } catch { ready = true; }
                if (!ready) continue;
                t.WaitFor = null;
            }

            _current = t;
            DynValue r = DynValue.Nil;
            try
            {
                while (_script != null)
                {
                    r = t.Started ? t.Co.Coroutine.Resume() : t.Co.Coroutine.Resume(t.Args);
                    t.Started = true;
                    if (t.Co.Coroutine.State == CoroutineState.Dead) break;
                    // Forced (auto) yield: keep going while this tick has budget.
                    if (r.Type == DataType.YieldRequest && !OverBudget()) continue;
                    break;
                }
            }
            catch (InterpreterException ex)
            {
                Print("error: " + (ex.DecoratedMessage ?? ex.Message));
                _threads.Remove(t);
                Done(t, null, ex.DecoratedMessage ?? ex.Message);
                continue;
            }
            catch (Exception ex)
            {
                Print("error: " + ex.Message);
                _threads.Remove(t);
                Done(t, null, ex.Message);
                continue;
            }
            finally { _current = null; }

            if (_script == null) return;
            if (t.Co.Coroutine.State == CoroutineState.Dead)
            {
                _threads.Remove(t);
                Done(t, r, null);
            }
            if (OverBudget()) break;
        }
    }

    // ── Core globals ────────────────────────────────────────────────────────

    private readonly Dictionary<string, DynValue> _modules = new(StringComparer.OrdinalIgnoreCase);

    private void RegisterCore(Script s)
    {
        _modules.Clear();

        // sleep(ms): park the calling thread. Implemented as a CLR call that
        // sets the wake time, then a Lua-side yield (a CLR callback can't
        // yield by itself).
        s.Globals["__rynth_setwake"] = DynValue.NewCallback((ctx, a) =>
        {
            if (_current != null)
                _current.WakeAt = Environment.TickCount64 + (long)Math.Max(0, a.Count > 0 && a[0].Type == DataType.Number ? a[0].Number : 0);
            return DynValue.Nil;
        });
        // await(action): park until the object's IsFinished field is true, then return it.
        s.Globals["__rynth_setawait"] = DynValue.NewCallback((ctx, a) =>
        {
            DynValue obj = a.Count > 0 ? a[0] : DynValue.Nil;
            if (obj.Type != DataType.Table)
                throw new ScriptRuntimeException("await(action) needs an action (what game.Actions... returns)");
            Table t = obj.Table;
            if (_current != null) _current.WaitFor = () => t.RawGet("IsFinished")?.CastToBool() == true;
            return DynValue.Nil;
        });
        s.Globals["on"] = DynValue.NewCallback((ctx, a) =>
        {
            string evt = a.Count > 0 ? a[0].CastToString() ?? "" : "";
            DynValue fn = a.Count > 1 ? a[1] : DynValue.Nil;
            if (evt.Length == 0 || fn.Type != DataType.Function)
                throw new ScriptRuntimeException("on(event, function) needs an event name and a function");
            if (!_handlers.TryGetValue(evt, out var list)) _handlers[evt] = list = new List<DynValue>();
            list.Add(fn);
            return DynValue.Nil;
        });
        s.Globals["off"] = DynValue.NewCallback((ctx, a) =>
        {
            string evt = a.Count > 0 ? a[0].CastToString() ?? "" : "";
            if (a.Count > 1 && a[1].Type == DataType.Function && _handlers.TryGetValue(evt, out var list))
                list.RemoveAll(f => f.Equals(a[1]));
            else
                _handlers.Remove(evt);
            return DynValue.Nil;
        });
        s.Globals["spawn"] = DynValue.NewCallback((ctx, a) =>
        {
            if (a.Count == 0 || a[0].Type != DataType.Function)
                throw new ScriptRuntimeException("spawn(function, ...) needs a function");
            var rest = new DynValue[Math.Max(0, a.Count - 1)];
            for (int i = 1; i < a.Count; i++) rest[i - 1] = a[i];
            Start(a[0], rest);
            return DynValue.Nil;
        });
        s.Globals["stop"] = DynValue.NewCallback((ctx, a) =>
        {
            Action? before = BeforeScriptStop;
            BeforeScriptStop = null;
            try { before?.Invoke(); } catch { }
            Stop("stopped by script");
            return DynValue.Nil;
        });
        s.Globals["clock"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Environment.TickCount64));

        // require(name): a built-in module, else a .lua file in this script's folder (never outside it).
        s.Globals["require"] = DynValue.NewCallback((ctx, a) =>
        {
            string name = (a.Count > 0 ? a[0].CastToString() : null)?.Trim() ?? "";
            if (name.Length == 0) throw new ScriptRuntimeException("require(name) needs a module name");
            if (_modules.TryGetValue(name, out DynValue? cached)) return cached;

            DynValue? builtIn = ResolveModule?.Invoke(s, name);
            if (builtIn != null) { _modules[name] = builtIn; return builtIn; }

            if (Folder == null)
                throw new ScriptRuntimeException($"require(\"{name}\"): no such module (single-file scripts can only require built-in modules)");
            string rel = name.Replace('.', Path.DirectorySeparatorChar);
            if (rel.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) rel = rel[..^4];
            string root = Path.GetFullPath(Folder);
            string file = Path.GetFullPath(Path.Combine(root, rel + ".lua"));
            if (!file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ScriptRuntimeException($"require(\"{name}\"): outside the script's folder");
            if (!File.Exists(file))
                throw new ScriptRuntimeException($"require(\"{name}\"): no module or file {Path.GetFileName(file)} in the script's folder");
            DynValue loaded = s.DoString(File.ReadAllText(file), null, Path.GetFileName(file));
            DynValue result = loaded.IsNil() ? DynValue.True : loaded;
            _modules[name] = result;
            return result;
        });

        s.DoString(@"
function sleep(ms) __rynth_setwake(ms or 0); coroutine.yield() end
wait = sleep
function await(action) __rynth_setawait(action); coroutine.yield(); return action end
function waitfor(cond, timeout)
  local deadline = timeout and (clock() + timeout) or nil
  while not cond() do
    if deadline and clock() >= deadline then return false end
    sleep(100)
  end
  return true
end

-- Lua 5.2+ gives %s any value through tostring (nil -> ""nil""); MoonSharp throws
-- ""bad argument #n to 'format' (string expected, got nil)"" instead, which stops
-- scripts written for standard Lua (a missing chat field, an unset setting).
do
  local rawformat = string.format
  local unpack = table.unpack or unpack
  string.format = function(fmt, ...)
    local n = select('#', ...)
    if n == 0 or type(fmt) ~= 'string' then return rawformat(fmt, ...) end
    local args = { ... }
    local i = 0
    for conv in fmt:gmatch('%%[-+ #0]*%d*%.?%d*(.)') do
      if conv ~= '%' then
        i = i + 1
        local v = args[i]
        if conv == 's' and i <= n and type(v) ~= 'string' and type(v) ~= 'number' then
          args[i] = tostring(v)
        end
      end
    end
    return rawformat(fmt, unpack(args, 1, n))
  end
end
", null, "rynth-core");
    }
}
