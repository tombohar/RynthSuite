using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// Evaluates RynthAi expressions.
///
/// Syntax:
///   Infix operators: == > < >= <= + - * / % # &amp;&amp; || ~ &lt;&lt; >> &amp; ^ |
///   Function calls:  funcname[arg1, arg2, ...]   (args can be nested expressions)
///   Variables:       $name  is shorthand for getvar[name]
///   Literals:        plain numbers (decimal or 0x hex) or identifiers
///
/// Boolean convention: "0" and "" are false, everything else is true.
/// All values are strings internally; numeric coercion happens per-operator.
/// Lists are stored by reference; variables hold a list handle (e.g. "L:0").
/// </summary>
internal sealed class ExpressionEngine
{
    private readonly RynthCoreHost _host;
    private WorldObjectCache? _worldObjectCache;
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    // List store: handle → items.  Handles look like "L:0", "L:1", …
    private readonly Dictionary<string, List<string>> _lists = new(StringComparer.Ordinal);
    private int _nextListId;

    // Dict store: handle → (key → value).  Handles look like "D:0", "D:1", …
    private readonly Dictionary<string, Dictionary<string, string>> _dicts = new(StringComparer.Ordinal);
    private int _nextDictId;

    // Plugin options store: name → value (case-insensitive keys).
    // Populated by the plugin via RegisterOption / SetOption.
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    // Keeps delayexec timers rooted so the GC doesn't collect them before they fire.
    private static readonly List<System.Threading.Timer> _activeTimers = new();
    private static readonly object _timerLock = new();

    private uint _playerId;

    public IReadOnlyDictionary<string, string> Variables => _variables;
    public IReadOnlyDictionary<string, string> Options   => _options;

    /// <summary>Registers a named option with a default value if it has not been set.</summary>
    public void RegisterOption(string name, string defaultValue)
    {
        if (!_options.ContainsKey(name))
            _options[name] = defaultValue;
    }

    /// <summary>Gets an option value, or "0" if not registered.</summary>
    public string GetOption(string name)
        => _options.TryGetValue(name, out string? v) ? v : "0";

    /// <summary>Sets an option value. Returns false if the option was not previously registered.</summary>
    public bool SetOption(string name, string value)
    {
        _options[name] = value;
        return true;
    }

    public ExpressionEngine(RynthCoreHost host) => _host = host;

    public void SetPlayerId(uint id) => _playerId = id;
    public void SetObjectCache(WorldObjectCache? cache) => _worldObjectCache = cache;

    // ── Backward-compat shim ──────────────────────────────────────────────────

    /// <summary>Executes an expression for its side effects. Always returns true.</summary>
    public bool TryExecuteAction(string expression)
    {
        Evaluate(expression);
        return true;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a full expression (infix operators + function calls) and returns the result.
    /// </summary>
    public string Evaluate(string expression)
    {
        string expr = expression?.Trim() ?? "";
        if (expr.Length == 0) return "";
        return new Parser(this, expr).Run();
    }

    // ── Primary evaluator ─────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a primary: a function call <c>funcname[args]</c> or a plain literal.
    /// Called by the Parser when it has isolated an atom.
    /// </summary>
    internal string EvaluatePrimary(string expression)
    {
        string expr = expression.Trim();
        if (expr.Length == 0) return "";

        int lb = expr.IndexOf('[');
        if (lb < 0 || !expr.EndsWith("]", StringComparison.Ordinal))
            return expr; // plain literal

        string funcName = expr.Substring(0, lb).Trim().ToLowerInvariant();
        string argsStr  = expr.Substring(lb + 1, expr.Length - lb - 2);

        // ── Lazy (only the taken branch is evaluated) ─────────────────────────
        if (funcName == "iif")
        {
            var raw = SplitArgs(argsStr);
            if (raw.Count < 2) return "";
            return ToBool(Evaluate(raw[0]))
                ? Evaluate(raw[1])
                : (raw.Count >= 3 ? Evaluate(raw[2]) : "");
        }
        if (funcName == "if")
        {
            var raw = SplitArgs(argsStr);
            if (raw.Count < 2) return "";
            return ToBool(Evaluate(raw[0])) ? Evaluate(raw[1]) : "";
        }
        if (funcName == "ifthen")
        {
            // ifthen[value, trueexpr, falseexpr?]
            // Evaluates value, picks the matching branch string, re-evaluates it.
            var raw = SplitArgs(argsStr);
            if (raw.Count < 2) return "";
            bool cond = ToBool(Evaluate(raw[0]));
            string branchExpr = cond ? Evaluate(raw[1]) : (raw.Count >= 3 ? Evaluate(raw[2]) : "");
            return branchExpr.Length > 0 ? Evaluate(branchExpr) : "";
        }

        // ── Eager (args evaluated on demand via A()) ──────────────────────────
        var rawArgs = SplitArgs(argsStr);
        string A(int i) => i < rawArgs.Count ? Evaluate(rawArgs[i]) : "";

        // Raw expression template for higher-order list functions (not pre-evaluated).
        string Tmpl(int i) => i < rawArgs.Count ? StripBackticks(rawArgs[i]) : "";

        return funcName switch
        {
            // ── Variables ─────────────────────────────────────────────────────
            "setvar"  => EvalSetVar(A(0), A(1)),
            "getvar"  => _variables.TryGetValue(A(0), out string? sv) ? sv : "",
            "testvar" => _variables.ContainsKey(A(0)) ? "1" : "0",

            // ── Character properties ──────────────────────────────────────────
            "getcharintprop"    => EvalCharInt(A(0)),
            "getchardoubleprop" => EvalCharDouble(A(0)),
            "getcharboolprop"   => EvalCharBool(A(0)),
            "getcharstringprop" => EvalCharString(A(0)),

            // ── List functions ────────────────────────────────────────────────
            "listgetitem"     => EvalListGetItem(A(0), A(1)),
            "listcontains"    => EvalListContains(A(0), A(1)),
            "listindexof"     => EvalListIndexOf(A(0), A(1)),
            "listlastindexof" => EvalListLastIndexOf(A(0), A(1)),
            "listcopy"        => EvalListCopy(A(0)),
            "listreverse"     => EvalListReverse(A(0)),
            "listpop"         => EvalListPop(A(0), rawArgs.Count >= 2 ? A(1) : "-1"),
            "listcount"       => EvalListCount(A(0)),
            "listclear"       => EvalListClear(A(0)),
            "listfromrange"   => EvalListFromRange(A(0), A(1)),

            // Higher-order: expression arg is a raw template evaluated per iteration
            "listfilter"  => EvalListFilter(A(0), Tmpl(1)),
            "listmap"     => EvalListMap(A(0), Tmpl(1)),
            "listreduce"  => EvalListReduce(A(0), Tmpl(1)),
            "listsort"    => EvalListSort(A(0), Tmpl(1)),

            // ── World state ───────────────────────────────────────────────────
            "isportaling"       => EvalIsPortaling(),
            "getcellid"         => EvalGetCellId(),
            "vitae"             => EvalVitae(),
            "getaccounthash"    => EvalGetAccountHash(),
            "getworldname"      => EvalGetWorldName(),
            "getdatetimelocal"  => DateTime.Now.ToString(A(0)),
            "getdatetimeutc"    => DateTime.UtcNow.ToString(A(0)),
            "getunixtime"       => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),

            // ── World object queries ──────────────────────────────────────────
            "wobjectfindnearestbytemplatetype" => EvalWobjectFindNearestByTemplateType(A(0)),
            "wobjectgetselection"    => EvalWobjectGetSelection(),
            "wobjectgetname"         => EvalWobjectGetName(A(0)),
            "wobjectgetid"           => EvalWobjectGetId(A(0)),
            "wobjectgettemplatetype" => EvalWobjectGetTemplateType(A(0)),
            "wobjectgetobjclass"     => EvalWobjectGetObjClass(A(0)),
            "wobjectgetintprop"      => EvalWobjectGetIntProp(A(0), A(1)),
            "wobjectgetdoubleprop"   => EvalWobjectGetDoubleProp(A(0), A(1)),
            "wobjectgetboolprop"     => EvalWobjectGetBoolProp(A(0), A(1)),

            // ── Plugin options ────────────────────────────────────────────────
            "raoptget" or "uboptget" => GetOption(A(0)),
            "raoptset" or "uboptset" => EvalOptSet(A(0), A(1)),

            // ── Dynamic evaluation ────────────────────────────────────────────
            "exec"      => Evaluate(A(0)),
            "delayexec" => EvalDelayExec(A(0), A(1)),
            "tostring"  => A(0),

            // ── String functions ──────────────────────────────────────────────
            "getregexmatch" => EvalGetRegexMatch(A(0), A(1)),

            // ── Dictionary functions ───────────────────────────────────────────
            "dictcreate"    => EvalDictCreate(rawArgs),
            "dictgetitem"   => EvalDictGetItem(A(0), A(1)),
            "dictadditem"   => EvalDictAddItem(A(0), A(1), A(2)),
            "dicthaskey"    => EvalDictHasKey(A(0), A(1)),
            "dictremovekey" => EvalDictRemoveKey(A(0), A(1)),
            "dictkeys"      => EvalDictKeys(A(0)),
            "dictvalues"    => EvalDictValues(A(0)),
            "dictsize"      => EvalDictSize(A(0)),
            "dictclear"     => EvalDictClear(A(0)),
            "dictcopy"      => EvalDictCopy(A(0)),

            _ => ""
        };
    }

    // ── Variable / char-prop implementations ──────────────────────────────────

    private string EvalSetVar(string key, string value)
    {
        if (!string.IsNullOrEmpty(key)) _variables[key] = value;
        return "";
    }

    private string EvalCharInt(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectIntProperty) return "0";
        return _host.TryGetObjectIntProperty(_playerId, st, out int v) ? v.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharDouble(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectDoubleProperty) return "0";
        return _host.TryGetObjectDoubleProperty(_playerId, st, out double v) ? v.ToString("G", CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharBool(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectBoolProperty) return "0";
        return _host.TryGetObjectBoolProperty(_playerId, st, out bool v) ? (v ? "1" : "0") : "0";
    }

    private string EvalCharString(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectStringProperty) return "";
        return _host.TryGetObjectStringProperty(_playerId, st, out string? v) ? v ?? "" : "";
    }

    // ── List helpers ──────────────────────────────────────────────────────────

    private string NewList(IEnumerable<string>? items = null)
    {
        string handle = "L:" + _nextListId++;
        _lists[handle] = items != null ? new List<string>(items) : new List<string>();
        return handle;
    }

    private List<string>? GetList(string handle)
        => _lists.TryGetValue(handle, out var l) ? l : null;

    private static string StripBackticks(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '`' && s[s.Length - 1] == '`'
            ? s.Substring(1, s.Length - 2)
            : s;
    }

    // ── List function implementations ─────────────────────────────────────────

    private string EvalListGetItem(string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null) return "";
        int idx = (int)ToLong(indexArg);
        return idx >= 0 && idx < list.Count ? list[idx] : "";
    }

    private string EvalListContains(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        foreach (var s in list)
            if (AreEqual(s, item)) return "1";
        return "0";
    }

    private string EvalListIndexOf(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "-1";
        for (int i = 0; i < list.Count; i++)
            if (AreEqual(list[i], item)) return Fmt((long)i);
        return "-1";
    }

    private string EvalListLastIndexOf(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "-1";
        for (int i = list.Count - 1; i >= 0; i--)
            if (AreEqual(list[i], item)) return Fmt((long)i);
        return "-1";
    }

    private string EvalListCopy(string handle)
    {
        var list = GetList(handle);
        return list != null ? NewList(list) : NewList();
    }

    private string EvalListReverse(string handle)
    {
        var list = GetList(handle);
        if (list == null) return NewList();
        var copy = new List<string>(list);
        copy.Reverse();
        return NewList(copy);
    }

    private string EvalListPop(string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null || list.Count == 0) return "";
        int idx = (int)ToLong(indexArg);
        if (idx < 0) idx = list.Count - 1; // -1 = last item
        if (idx < 0 || idx >= list.Count) return "";
        string item = list[idx];
        list.RemoveAt(idx);
        return item;
    }

    private string EvalListCount(string handle)
    {
        var list = GetList(handle);
        return list != null ? Fmt((long)list.Count) : "0";
    }

    private string EvalListClear(string handle)
    {
        GetList(handle)?.Clear();
        return handle;
    }

    private string EvalListFromRange(string startArg, string endArg)
    {
        long start = ToLong(startArg);
        long end   = ToLong(endArg);
        var items  = new List<string>();
        if (start <= end)
            for (long i = start; i <= end; i++) items.Add(Fmt(i));
        else
            for (long i = start; i >= end; i--) items.Add(Fmt(i));
        return NewList(items);
    }

    // Higher-order helpers: set $0/$1/$2, evaluate template, restore.

    private string EvalListFilter(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || exprTemplate.Length == 0) return NewList();
        var result = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            _variables["0"] = Fmt((long)i);
            _variables["1"] = list[i];
            if (ToBool(Evaluate(exprTemplate)))
                result.Add(list[i]);
        }
        return NewList(result);
    }

    private string EvalListMap(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || exprTemplate.Length == 0) return NewList();
        var result = new List<string>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            _variables["0"] = Fmt((long)i);
            _variables["1"] = list[i];
            result.Add(Evaluate(exprTemplate));
        }
        return NewList(result);
    }

    private string EvalListReduce(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || list.Count == 0 || exprTemplate.Length == 0) return "";
        string acc = "";
        for (int i = 0; i < list.Count; i++)
        {
            _variables["0"] = Fmt((long)i);
            _variables["1"] = list[i];
            _variables["2"] = acc;
            acc = Evaluate(exprTemplate);
        }
        return acc;
    }

    private string EvalListSort(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null) return NewList();
        var copy = new List<string>(list);
        if (exprTemplate.Length > 0)
        {
            copy.Sort((a, b) =>
            {
                _variables["1"] = a;
                _variables["2"] = b;
                return (int)ToLong(Evaluate(exprTemplate));
            });
        }
        else
        {
            copy.Sort(StringComparer.Ordinal);
        }
        return NewList(copy);
    }

    // ── World state implementations ───────────────────────────────────────────

    private string EvalIsPortaling()
    {
        if (!_host.HasIsPortaling) return "0";
        return _host.IsPortaling() ? "1" : "0";
    }

    private string EvalVitae()
    {
        if (!_host.HasGetVitae || _playerId == 0) return "100";
        float v = _host.GetVitae(_playerId);
        // Convert multiplier to penalty: 1.0 → 0 (no penalty), 0.95 → 5 (5% penalty).
        int penalty = 100 - (int)Math.Round(v * 100.0f);
        if (penalty < 0) penalty = 0;
        if (penalty > 100) penalty = 100;
        return penalty.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string EvalGetWorldName()
    {
        if (!_host.HasGetWorldName) return string.Empty;
        return _host.TryGetWorldName(out string name) ? name : string.Empty;
    }

    private string EvalGetAccountHash()
    {
        if (!_host.HasGetAccountName) return string.Empty;
        if (!_host.TryGetAccountName(out string name) || string.IsNullOrEmpty(name)) return string.Empty;
        byte[] bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string EvalGetCellId()
    {
        if (!_host.HasGetPlayerPose) return "unavailable";
        if (!_host.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _)) return "pose_failed";
        return $"0x{cellId:X8}";
    }

    // ── WorldObject handle helpers ────────────────────────────────────────────
    // Handle format (UB-compatible): "[WorldObject] 0xHHHHHHHH: Name"
    // "0" means no object found.

    private static string MakeWobjectHandle(uint uid, string name)
        => $"[WorldObject] 0x{uid:X8}: {name}";

    /// <summary>
    /// Parses a WorldObject handle string into a uid and name.
    /// Accepts "[WorldObject] 0xHHHHHHHH: Name", bare hex "0xHHHHHHHH", or unsigned decimal.
    /// </summary>
    private static bool TryParseWobjectHandle(string handle, out uint uid, out string name)
    {
        uid = 0;
        name = string.Empty;
        handle = handle.Trim();

        if (handle == "0" || handle == string.Empty) return false;

        // Full handle: "[WorldObject] 0xHHHHHHHH: Name"
        const string prefix = "[WorldObject] 0x";
        if (handle.StartsWith(prefix, StringComparison.Ordinal))
        {
            int colonIdx = handle.IndexOf(':', prefix.Length);
            string hexPart = colonIdx > prefix.Length
                ? handle.Substring(prefix.Length, colonIdx - prefix.Length)
                : handle.Substring(prefix.Length);
            if (!uint.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out uid))
                return false;
            name = colonIdx >= 0 && colonIdx + 2 < handle.Length
                ? handle.Substring(colonIdx + 2)
                : string.Empty;
            return uid != 0;
        }

        // Bare hex: "0xHHHHHHHH"
        if (handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(handle.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uid))
                return false;
            return uid != 0;
        }

        // Unsigned or signed decimal
        if (uint.TryParse(handle, out uid)) return uid != 0;
        if (long.TryParse(handle, out long l)) { uid = unchecked((uint)l); return uid != 0; }
        return false;
    }

    private string EvalWobjectFindNearestByTemplateType(string arg)
    {
        if (_worldObjectCache == null || _playerId == 0 || !_host.HasGetObjectWcid) return "0";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "0";

        int bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (!_host.TryGetObjectWcid(uid, out uint wcid) || wcid != targetWcid) continue;
            double dist = _worldObjectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = wo.Id;
                bestName = wo.Name;
            }
        }

        if (bestId == 0) return "0";
        uint bestUid = unchecked((uint)bestId);
        if (string.IsNullOrEmpty(bestName))
            _host.TryGetObjectName(bestUid, out bestName);
        return MakeWobjectHandle(bestUid, bestName ?? string.Empty);
    }

    private string EvalWobjectGetName(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out string name)) return "";
        if (!string.IsNullOrEmpty(name)) return name;
        return _host.TryGetObjectName(uid, out string fetched) ? fetched : "";
    }

    private static string EvalWobjectGetId(string arg)
    {
        return TryParseWobjectHandle(arg, out uint uid, out _)
            ? uid.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private string EvalWobjectGetTemplateType(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out _) || !_host.HasGetObjectWcid) return "0";
        return _host.TryGetObjectWcid(uid, out uint wcid) ? wcid.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalWobjectGetObjClass(string arg)
    {
        if (_worldObjectCache == null || !TryParseWobjectHandle(arg, out uint uid, out _)) return "0";
        var wo = _worldObjectCache[unchecked((int)uid)];
        return wo == null ? "0" : ((int)wo.ObjectClass).ToString(CultureInfo.InvariantCulture);
    }

    private string EvalWobjectGetSelection()
    {
        if (!_host.HasGetSelectedItemId) return "0";
        uint uid = _host.GetSelectedItemId();
        if (uid == 0) return "0";
        _host.TryGetObjectName(uid, out string name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalWobjectGetIntProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "0";

        // UB extended property 218103808 (0xD000000) = TEMPLATE_TYPE → reads WCID from PublicWeenieDesc
        if (prop == 218103808u)
        {
            if (!_host.HasGetObjectWcid) return "0";
            return _host.TryGetObjectWcid(uid, out uint wcid) ? wcid.ToString(CultureInfo.InvariantCulture) : "0";
        }

        if (!_host.HasGetObjectIntProperty) return "0";
        return _host.TryGetObjectIntProperty(uid, prop, out int value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private string EvalWobjectGetDoubleProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "0";

        // UB extended double property constants use their own index, not AC STypeFloat values.
        // Map each known UB extended constant to the correct AC STypeFloat (or special path).
        uint stype = prop switch
        {
            167772169u => 280u, // 0xA000009 = WORKMANSHIP → STypeFloat 280 (ITEM_WORKMANSHIP), reads PWD+152
            _ => prop,
        };

        if (!_host.HasGetObjectDoubleProperty) return "0";
        return _host.TryGetObjectDoubleProperty(uid, stype, out double value)
            ? value.ToString("G", CultureInfo.InvariantCulture)
            : "0";
    }

    private string EvalWobjectGetBoolProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "0";
        if (!_host.HasGetObjectBoolProperty) return "0";
        return _host.TryGetObjectBoolProperty(uid, prop, out bool value) ? (value ? "1" : "0") : "0";
    }

    // ── Option function implementations ───────────────────────────────────────

    private string EvalOptSet(string name, string value)
    {
        if (string.IsNullOrEmpty(name)) return "0";
        _options[name] = value;
        return "1";
    }

    // ── Dynamic evaluation implementations ───────────────────────────────────

    private string EvalDelayExec(string delayArg, string exprArg)
    {
        if (!double.TryParse(delayArg, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double delayMs))
            delayMs = 0;
        int ms = (int)Math.Max(0, delayMs);
        var engine = this;
        string expr = exprArg;
        System.Threading.Timer? t = null;
        t = new System.Threading.Timer(_ =>
        {
            try { engine.Evaluate(expr); }
            catch { }
            lock (_timerLock)
            {
                if (t != null) _activeTimers.Remove(t);
            }
        }, null, ms, System.Threading.Timeout.Infinite);
        lock (_timerLock)
            _activeTimers.Add(t);
        return "1";
    }

    // ── String function implementations ───────────────────────────────────────

    private static string EvalGetRegexMatch(string text, string pattern)
    {
        if (pattern.Length == 0) return "";
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success ? m.Value : "";
        }
        catch { return ""; }
    }

    // ── Dict helpers ──────────────────────────────────────────────────────────

    private string NewDict(Dictionary<string, string>? entries = null)
    {
        string handle = "D:" + _nextDictId++;
        _dicts[handle] = entries != null
            ? new Dictionary<string, string>(entries, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return handle;
    }

    private Dictionary<string, string>? GetDict(string handle)
        => _dicts.TryGetValue(handle, out var d) ? d : null;

    // ── Dict function implementations ─────────────────────────────────────────

    private string EvalDictCreate(List<string> rawArgs)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        // rawArgs are already the un-split arg strings from the outer SplitArgs call.
        // Evaluate them eagerly in pairs: key, value, key, value, …
        for (int i = 0; i + 1 < rawArgs.Count; i += 2)
        {
            string key = Evaluate(rawArgs[i]);
            string val = Evaluate(rawArgs[i + 1]);
            dict[key] = val;
        }
        return NewDict(dict);
    }

    private string EvalDictGetItem(string handle, string key)
    {
        var dict = GetDict(handle);
        if (dict == null) return "";
        return dict.TryGetValue(key, out string? v) ? v : "";
    }

    private string EvalDictAddItem(string handle, string key, string value)
    {
        var dict = GetDict(handle);
        if (dict == null) return "0";
        bool overwrote = dict.ContainsKey(key);
        dict[key] = value;
        return overwrote ? "1" : "0";
    }

    private string EvalDictHasKey(string handle, string key)
    {
        var dict = GetDict(handle);
        return dict != null && dict.ContainsKey(key) ? "1" : "0";
    }

    private string EvalDictRemoveKey(string handle, string key)
    {
        var dict = GetDict(handle);
        if (dict == null) return "0";
        return dict.Remove(key) ? "1" : "0";
    }

    private string EvalDictKeys(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? NewList(dict.Keys) : NewList();
    }

    private string EvalDictValues(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? NewList(dict.Values) : NewList();
    }

    private string EvalDictSize(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? Fmt((long)dict.Count) : "0";
    }

    private string EvalDictClear(string handle)
    {
        GetDict(handle)?.Clear();
        return handle;
    }

    private string EvalDictCopy(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? NewDict(dict) : NewDict();
    }

    // ── Coercions ─────────────────────────────────────────────────────────────

    /// <summary>"0" and "" are false; everything else is true.</summary>
    internal static bool ToBool(string s)
        => s.Length > 0 && s != "0" && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);

    private static double ToDouble(string s)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double d) ? d : 0.0;

    private static long ToLong(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
        return (long)ToDouble(s);
    }

    private static string Fmt(double d) => d.ToString("G", CultureInfo.InvariantCulture);
    private static string Fmt(long l)   => l.ToString(CultureInfo.InvariantCulture);

    /// <summary>Equality: numeric if both sides parse as numbers, otherwise ordinal string.</summary>
    private static bool AreEqual(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double da)
         && double.TryParse(b, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double db))
            return da == db;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    // ── Argument splitter ─────────────────────────────────────────────────────

    /// <summary>Splits a comma-separated argument list, respecting nested bracket depth.</summary>
    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                list.Add(s.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        list.Add(s.Substring(start).Trim());
        return list;
    }

    // ── Infix expression parser ───────────────────────────────────────────────

    /// <summary>
    /// Recursive-descent infix parser.
    ///
    /// Operator precedence (lowest → highest):
    ///   ||  →  &amp;&amp;  →  |  →  ^  →  &amp;  →  ==  →  &lt; &lt;= > >=  →  &lt;&lt; >>  →  + -  →  * / %  →  #  →  unary ~  →  primary
    /// </summary>
    private sealed class Parser
    {
        private readonly ExpressionEngine _eng;
        private readonly string _s;
        private int _p;

        internal Parser(ExpressionEngine eng, string s) { _eng = eng; _s = s; }

        internal string Run() => ParseOr();

        // ── Whitespace ────────────────────────────────────────────────────────

        private void Ws()
        {
            while (_p < _s.Length && (_s[_p] == ' ' || _s[_p] == '\t' || _s[_p] == '\r' || _s[_p] == '\n'))
                _p++;
        }

        // ── Token consumer ────────────────────────────────────────────────────

        /// <summary>
        /// Tries to consume <paramref name="tok"/> at the current position.
        /// Disambiguates single-char tokens against longer operators (e.g., &amp; vs &amp;&amp;).
        /// Returns true and advances on success; leaves position unchanged on failure.
        /// </summary>
        private bool Try(string tok)
        {
            Ws();
            if (_p + tok.Length > _s.Length) return false;
            for (int i = 0; i < tok.Length; i++)
                if (_s[_p + i] != tok[i]) return false;

            // Single-char disambiguation: don't match if a longer operator follows.
            if (tok.Length == 1)
            {
                char nc = _p + 1 < _s.Length ? _s[_p + 1] : '\0';
                switch (tok[0])
                {
                    case '&': if (nc == '&') return false; break;
                    case '|': if (nc == '|') return false; break;
                    case '<': if (nc == '<' || nc == '=') return false; break;
                    case '>': if (nc == '>' || nc == '=') return false; break;
                }
            }

            _p += tok.Length;
            return true;
        }

        // ── Precedence levels ─────────────────────────────────────────────────

        // Priority 1 (lowest): ||
        private string ParseOr()
        {
            var l = ParseAnd();
            Ws();
            while (Try("||")) { var r = ParseAnd(); l = ToBool(l) || ToBool(r) ? "1" : "0"; Ws(); }
            return l;
        }

        // Priority 2: &&
        private string ParseAnd()
        {
            var l = ParseBitOr();
            Ws();
            while (Try("&&")) { var r = ParseBitOr(); l = ToBool(l) && ToBool(r) ? "1" : "0"; Ws(); }
            return l;
        }

        // Priority 3: | (bitwise OR)
        private string ParseBitOr()
        {
            var l = ParseBitXor();
            Ws();
            while (Try("|")) { var r = ParseBitXor(); l = Fmt(ToLong(l) | ToLong(r)); Ws(); }
            return l;
        }

        // Priority 4: ^ (bitwise XOR)
        private string ParseBitXor()
        {
            var l = ParseBitAnd();
            Ws();
            while (Try("^")) { var r = ParseBitAnd(); l = Fmt(ToLong(l) ^ ToLong(r)); Ws(); }
            return l;
        }

        // Priority 5: & (bitwise AND)
        private string ParseBitAnd()
        {
            var l = ParseEquality();
            Ws();
            while (Try("&")) { var r = ParseEquality(); l = Fmt(ToLong(l) & ToLong(r)); Ws(); }
            return l;
        }

        // Priority 6: ==
        private string ParseEquality()
        {
            var l = ParseComparison();
            Ws();
            while (Try("==")) { var r = ParseComparison(); l = AreEqual(l, r) ? "1" : "0"; Ws(); }
            return l;
        }

        // Priority 7: < <= > >=  (multi-char tried first to beat single-char)
        private string ParseComparison()
        {
            var l = ParseShift();
            Ws();
            while (true)
            {
                string op;
                if      (Try("<=")) op = "<=";
                else if (Try(">=")) op = ">=";
                else if (Try("<"))  op = "<";
                else if (Try(">"))  op = ">";
                else break;
                var r = ParseShift();
                l = op switch
                {
                    "<=" => ToDouble(l) <= ToDouble(r) ? "1" : "0",
                    ">=" => ToDouble(l) >= ToDouble(r) ? "1" : "0",
                    "<"  => ToDouble(l) <  ToDouble(r) ? "1" : "0",
                    _    => ToDouble(l) >  ToDouble(r) ? "1" : "0",
                };
                Ws();
            }
            return l;
        }

        // Priority 8: << >>
        private string ParseShift()
        {
            var l = ParseAddSub();
            Ws();
            while (true)
            {
                if      (Try("<<")) { var r = ParseAddSub(); l = Fmt(ToLong(l) << (int)ToLong(r)); }
                else if (Try(">>")) { var r = ParseAddSub(); l = Fmt(ToLong(l) >> (int)ToLong(r)); }
                else break;
                Ws();
            }
            return l;
        }

        // Priority 9: + -   (+ also works on strings)
        private string ParseAddSub()
        {
            var l = ParseMulDiv();
            Ws();
            while (true)
            {
                if (Try("+"))
                {
                    var r = ParseMulDiv();
                    // Numeric add if both sides parse as numbers; string concat otherwise.
                    if (double.TryParse(l, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double dl)
                     && double.TryParse(r, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double dr))
                        l = Fmt(dl + dr);
                    else
                        l = l + r;
                }
                else if (Try("-")) { var r = ParseMulDiv(); l = Fmt(ToDouble(l) - ToDouble(r)); }
                else break;
                Ws();
            }
            return l;
        }

        // Priority 10: * / %
        private string ParseMulDiv()
        {
            var l = ParseRegex();
            Ws();
            while (true)
            {
                if (Try("*")) { var r = ParseRegex(); l = Fmt(ToDouble(l) * ToDouble(r)); }
                else if (Try("/")) { var r = ParseRegex(); double b = ToDouble(r); l = b != 0 ? Fmt(ToDouble(l) / b) : "0"; }
                else if (Try("%")) { var r = ParseRegex(); double b = ToDouble(r); l = b != 0 ? Fmt(ToDouble(l) % b) : "0"; }
                else break;
                Ws();
            }
            return l;
        }

        // Priority 11: # (regex match — left#pattern, case-insensitive)
        private string ParseRegex()
        {
            var l = ParseUnary();
            Ws();
            while (Try("#"))
            {
                var pattern = ParseUnary();
                try { l = Regex.IsMatch(l, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "1" : "0"; }
                catch { l = "0"; }
                Ws();
            }
            return l;
        }

        // Priority 12: unary ~  (also unary - on non-digit expressions)
        private string ParseUnary()
        {
            Ws();
            if (_p < _s.Length && _s[_p] == '~') { _p++; return Fmt(~ToLong(ParseUnary())); }
            // Unary minus on a non-digit (digit case is handled inside ParseAtom as a negative literal)
            if (_p < _s.Length && _s[_p] == '-' && (_p + 1 >= _s.Length || !char.IsDigit(_s[_p + 1])))
            {
                _p++;
                return Fmt(-ToDouble(ParseUnary()));
            }
            return ParsePrimary();
        }

        // Priority 13 (highest): parenthesised group or atom
        private string ParsePrimary()
        {
            Ws();
            if (_p < _s.Length && _s[_p] == '(')
            {
                _p++; // consume '('
                var inner = ParseOr();
                Ws();
                if (_p < _s.Length && _s[_p] == ')') _p++;
                return inner;
            }
            return ParseAtom();
        }

        /// <summary>
        /// Reads one atom: a function call <c>name[...]</c>, a <c>$variable</c> reference,
        /// a number literal (decimal or 0x hex), or a plain identifier.
        /// </summary>
        private string ParseAtom()
        {
            Ws();
            if (_p >= _s.Length) return "";

            // Backtick string literal: `content` → content (verbatim, no evaluation)
            // \` inside the string is an escaped backtick (needed for nested expressions).
            if (_s[_p] == '`')
            {
                _p++; // consume opening backtick
                var sb = new System.Text.StringBuilder();
                while (_p < _s.Length && _s[_p] != '`')
                {
                    if (_s[_p] == '\\' && _p + 1 < _s.Length && _s[_p + 1] == '`')
                    {
                        sb.Append('`');
                        _p += 2;
                    }
                    else
                    {
                        sb.Append(_s[_p++]);
                    }
                }
                if (_p < _s.Length) _p++; // consume closing backtick
                return sb.ToString();
            }

            // $name — shorthand for getvar[name]
            if (_s[_p] == '$')
            {
                _p++;
                int vs = _p;
                while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_'))
                    _p++;
                string varName = _s.Substring(vs, _p - vs);
                return _eng._variables.TryGetValue(varName, out string? vv) ? vv ?? "" : "";
            }

            // Hex literal: 0x / 0X (positive or negative)
            bool negHex = _s[_p] == '-' && _p + 2 < _s.Length && _s[_p + 1] == '0' && (_s[_p + 2] == 'x' || _s[_p + 2] == 'X');
            bool posHex = _s[_p] == '0' && _p + 1 < _s.Length && (_s[_p + 1] == 'x' || _s[_p + 1] == 'X');
            if (negHex || posHex)
            {
                if (negHex) _p++;   // skip leading '-'
                _p += 2;            // skip '0x'
                int hs = _p;
                while (_p < _s.Length && (char.IsDigit(_s[_p]) ||
                       (_s[_p] >= 'a' && _s[_p] <= 'f') || (_s[_p] >= 'A' && _s[_p] <= 'F')))
                    _p++;
                if (long.TryParse(_s.Substring(hs, _p - hs), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv))
                    return Fmt(negHex ? -hv : hv);
                return "0";
            }

            // Negative decimal literal: '-' immediately followed by a digit.
            // (Unary minus on expressions is handled by ParseUnary above.)
            if (_s[_p] == '-' && _p + 1 < _s.Length && char.IsDigit(_s[_p + 1]))
            {
                int ns = _p++;
                while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) _p++;
                return _s.Substring(ns, _p - ns);
            }

            int start = _p;

            // Identifier (letters, digits, underscores, dots for decimal numbers)
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_' || _s[_p] == '.'))
                _p++;

            if (_p < _s.Length && _s[_p] == '[')
            {
                // Function call: consume '[' and scan to its matching ']'
                _p++;
                int depth = 1;
                while (_p < _s.Length && depth > 0)
                {
                    if      (_s[_p] == '[') depth++;
                    else if (_s[_p] == ']') depth--;
                    _p++;
                }
                // Pass the entire "funcname[args]" string to EvaluatePrimary
                return _eng.EvaluatePrimary(_s.Substring(start, _p - start));
            }

            if (_p > start)
                return _eng.EvaluatePrimary(_s.Substring(start, _p - start));

            // Skip unrecognised characters (guards against infinite loops)
            _p++;
            return "";
        }
    }
}
