using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// Evaluates RynthAi expressions.
///
/// Follows the UtilityBelt expression reference (utilitybelt.gitlab.io/docs/expressions).
///
/// Syntax:
///   Infix operators: == != > < >= <= + - * / % # &amp;&amp; || ~ &lt;&lt; >> &amp; ^ |
///                    (&amp;&amp; and || skip the side that can't change the answer)
///   Function calls:  funcname[arg1, arg2, ...]   (args can be nested expressions)
///   Sequences:       expr; expr; ...             (value of the last)
///   Variables:       $name / @name / &amp;name = getvar / getpvar / getgvar[name];
///                    $name = value assigns; x{i}, x{key}, x{a:b}, $x[a:b], text[a:b]
///   Literals:        numbers (decimal or 0x hex), true/false (1/0), `backtick text`,
///                    bare text of words and spaces, \x escapes one character
///
/// Boolean convention: "0" and "" are false, everything else is true.
/// All values are strings internally; numeric coercion happens per-operator.
/// Lists are text ("[a,b]"); the list functions that change a list write it back to the
/// variable it came from. Dictionaries and stopwatches are handles ("D:0", "SW:0").
/// </summary>
internal sealed partial class ExpressionEngine
{
    private readonly RynthCoreHost _host;
    private WorldObjectCache? _worldObjectCache;
    private FellowshipTracker? _fellowshipTracker;
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    // Lists are value-typed: serialized as "[item1,item2,...]" strings — no backing store.

    // Dict store: handle → (key → value).  Handles look like "D:0", "D:1", …
    // A dict lives as long as something holds its handle: "setvar[myDict,dictcreate[]]" and
    // later "dictadditem[$myDict,foo,1]" is the reference's own pattern. (Dicts used to be
    // cleared after every top-level evaluation and the ids restarted at D:0, so a stored handle
    // was dead or, worse, named a newer, unrelated dict.) Unreferenced ones are swept once the
    // store grows past _dictSweepAt; handles are never reused.
    private readonly Dictionary<string, Dictionary<string, string>> _dicts = new(StringComparer.Ordinal);
    private int _nextDictId;
    private const int DictSweepMin = 256;
    private int _dictSweepAt = DictSweepMin;
    private static readonly Regex DictHandleRx = new(@"D:\d+", RegexOptions.CultureInvariant);

    /// <summary>Drops dicts no variable (memory, persistent, global), option, pending delayexec
    /// or live dict refers to. Runs only between top-level evaluations, so no temporary of an
    /// evaluation in progress is lost.</summary>
    private void SweepDicts()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Queue<string>();
        void Scan(string? s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("D:", StringComparison.Ordinal) < 0) return;
            foreach (Match m in DictHandleRx.Matches(s))
                if (_dicts.ContainsKey(m.Value) && live.Add(m.Value)) todo.Enqueue(m.Value);
        }
        foreach (string v in _variables.Values) Scan(v);
        if (_pvars != null) foreach (string v in _pvars.Values) Scan(v);
        if (_gvars != null) foreach (string v in _gvars.Values) Scan(v);
        foreach (string v in _options.Values) Scan(v);
        foreach (var (_, _, expr) in _delayedExecs) Scan(expr);
        while (todo.Count > 0)
            foreach (var (k, v) in _dicts[todo.Dequeue()]) { Scan(k); Scan(v); }

        foreach (string h in _dicts.Keys.ToList())
            if (!live.Contains(h)) _dicts.Remove(h);
        _dictSweepAt = Math.Max(DictSweepMin, live.Count * 2);
    }
    private int _evalDepth;

    // Hard recursion caps. A .NET StackOverflowException is uncatchable and
    // instantly kills acclient.exe, so a self-referential meta expression
    // (exec[$x] loop, listmap template, nested funcs) or a pathological paren
    // nest MUST be bounded before it reaches the runtime. 64/256 are far above
    // any legitimate meta and well under an 8 MB stack.
    private const int MaxEvalDepth = 64;
    private const int MaxParenDepth = 256;
    private int _parenDepth;

    // Plugin options store: name → value (case-insensitive keys).
    // Populated by the plugin via RegisterOption / SetOption.
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    // ItemGiver profile cache: absolute path → (profile, mtime)
    private readonly Dictionary<string, (VTankLootProfile Profile, DateTime Mtime)> _giveProfileCache
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (RynthCore.Loot.LootProfile Profile, DateTime Mtime)> _giveNativeProfileCache
        = new(StringComparer.OrdinalIgnoreCase);
    // actiontrygiveprofile arguments already logged as refused (the meta retries every pulse).
    private readonly HashSet<string> _giveProfileRefused = new(StringComparer.OrdinalIgnoreCase);
    // The three data paths are settable only so offline tests can point them at a scratch
    // folder before anything reads or writes them; the plugin never changes them.
    internal static string ItemGiverDir { get; set; }
        = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "ItemGiver");

    /// <summary>Millisecond tick for delayexec and the var-flush throttle. Tests swap it for a
    /// fake clock; the plugin never changes it.</summary>
    internal Func<long> TickMs { get; set; } = static () => Environment.TickCount64;

    // Stopwatch store: handle → Stopwatch. Persistent (not cleared per eval) — handles are stored in variables.
    private readonly Dictionary<string, System.Diagnostics.Stopwatch> _stopwatches = new(StringComparer.Ordinal);
    private int _nextSwId;

    // Motion name → (AC motion uint, wanted state). Matches UB Motion enum values.
    private static readonly Dictionary<string, uint> MotionValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Forward"]     = 0x45000005,
        ["Backward"]    = 0x45000006,
        ["TurnRight"]   = 0x6500000D,
        ["TurnLeft"]    = 0x6500000E,
        ["StrafeRight"] = 0x6500000F,
        ["StrafeLeft"]  = 0x65000010,
        ["Walk"]        = 0x11112222,
    };
    private readonly Dictionary<string, bool> _wantedMotion = new(StringComparer.OrdinalIgnoreCase);

    // delayexec queue. Drained on the game/plugin-tick thread via
    // PumpDelayedExecs() — NOT a threadpool Timer (which executed expressions
    // against AC native memory off-thread and, being static, survived plugin
    // hot-reload to fire into a freed engine). Instance-scoped so a hot-reload
    // discards pending execs. Single-threaded access (Evaluate and the pump
    // both run on the plugin-tick thread) → no lock needed.
    private readonly List<(long Id, long DueMs, string Expr)> _delayedExecs = new();
    private long _nextDelayedExecId;

    private uint _playerId;
    private LegacyUiSettings? _settings;

    // Persistent / global variable storage (lazy-loaded from disk)
    private Dictionary<string, string>? _pvars;
    private Dictionary<string, string>? _gvars;
    // set/clear/touch mark dirty; FlushVars() writes at most every
    // VarFlushIntervalMs (from the plugin-tick pump) instead of a synchronous
    // full-file write on every call. A bot setting a pvar each tick was doing a
    // disk write per tick on the game thread.
    private bool _pvarsDirty, _gvarsDirty;
    private long _lastVarFlushMs;
    private string? _pvarPathCached;
    private const long VarFlushIntervalMs = 2000;
    internal static string PvarsDir  { get; set; } = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "pvars");
    internal static string GvarsPath { get; set; } = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "gvars.txt");
    private Dictionary<string, (Func<string> Get, Action<string> Set)>? _settingsMap;

    public IReadOnlyDictionary<string, string> Variables => _variables;
    public IReadOnlyDictionary<string, string> Options   => _options;
    public IReadOnlyDictionary<string, string> Pvars     => GetPvars();
    public IReadOnlyDictionary<string, string> Gvars     => GetGvars();

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

    private QuestTracker? _questTracker;

    public void SetPlayerId(uint id)
    {
        if (id != _playerId)
        {
            FlushVars(force: true);   // persist the old char's pending pvars first
            _pvars = null;
            _pvarPathCached = null;
            ResetXpMeter();
        }
        _playerId = id;
    }
    public void SetObjectCache(WorldObjectCache? cache) => _worldObjectCache = cache;
    public void SetFellowshipTracker(FellowshipTracker? tracker) => _fellowshipTracker = tracker;
    public void SetQuestTracker(QuestTracker? tracker) => _questTracker = tracker;
    public void SetSettings(LegacyUiSettings settings) { _settings = settings; _settingsMap = null; }

    private CreatureProfileStore? _creatureStore;
    public void SetCreatureStore(CreatureProfileStore? store) => _creatureStore = store;

    // secondssincelastkill[]: the plugin's kill clock. Null (not wired) reads as 0, i.e.
    // "just killed", so a "no kills for a while" test never holds by mistake.
    private KillClock? _killClock;
    public void SetKillClock(KillClock? clock) => _killClock = clock;

    // ratargetcount[] (RynthAi only, 2026-10-22): the targets that passed every combat filter in combat's
    // last scan (CombatManager.LastScanPossible). -1 before the first scan or with no combat manager. The
    // self-test meta uses it to tell "nothing here to fight" from "a fight that never happened":
    // wobjectfindnearestmonster[] also finds attackable NPCs (a buff bot) combat rightly leaves alone.
    internal Func<int>? CombatTargetCount { get; set; }

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
        string expr = TrimArg(expression ?? "");
        if (expr.Length == 0) return "";
        if (_evalDepth >= MaxEvalDepth) return "ERR:Depth:max recursion exceeded";
        if (_evalDepth == 0) { _parenDepth = 0; if (_dicts.Count > _dictSweepAt) SweepDicts(); }
        _evalDepth++;
        try   { return new Parser(this, expr).Run(); }
        catch (Exception ex) { return $"ERR:{ex.GetType().Name}:{ex.Message}"; }
        finally { _evalDepth--; }
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
            // "If the first parameter is false or not a number, returns the third argument":
            // text is not true here (iif[abc,yes,no] is no). The reference also evaluates both
            // branches; we still evaluate only the chosen one (no meta relies on both running).
            var raw = SplitArgs(argsStr);
            if (raw.Count < 2) return "";
            return IsNumber(Evaluate(raw[0]), out double c) && c != 0
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

        // A name, pattern, state, setting or format argument (see TextArg).
        string Text(int i) => i < rawArgs.Count ? TextArg(rawArgs[i]) : "";

        return funcName switch
        {
            // ── Session variables ──────────────────────────────────────────────
            "setvar"       => EvalSetVar(A(0), A(1)),
            "getvar"       => _variables.TryGetValue(A(0), out string? sv) ? sv : "0",
            "testvar"      => _variables.ContainsKey(A(0)) ? "1" : "0",
            "touchvar"     => EvalTouchVar(A(0)),
            "clearvar"     => _variables.Remove(A(0)) ? "1" : "0",
            "clearallvars" => EvalClearAllVars(),

            // ── Persistent variables (per-character, survive relog) ────────────
            "setpvar"       => EvalSetPvar(A(0), A(1)),
            "getpvar"       => GetPvars().TryGetValue(A(0), out string? pv) ? pv : "0",
            "testpvar"      => GetPvars().ContainsKey(A(0)) ? "1" : "0",
            "touchpvar"     => EvalTouchPvar(A(0)),
            "clearpvar"     => EvalClearPvar(A(0)),
            "clearallpvars" => EvalClearAllPvars(),

            // ── Global variables (shared across all characters) ────────────────
            "setgvar"       => EvalSetGvar(A(0), A(1)),
            "getgvar"       => GetGvars().TryGetValue(A(0), out string? gv) ? gv : "0",
            "testgvar"      => GetGvars().ContainsKey(A(0)) ? "1" : "0",
            "touchgvar"     => EvalTouchGvar(A(0)),
            "cleargvar"     => EvalClearGvar(A(0)),
            "clearallgvars" => EvalClearAllGvars(),

            "getfreeitemslots"       => EvalGetFreeItemSlots(rawArgs.Count > 0 ? A(0) : null),
            "getfreecontainerslots"  => EvalGetFreeContainerSlots(rawArgs.Count > 0 ? A(0) : null),
            "getcontaineritemcount"  => EvalGetContainerItemCount(rawArgs.Count > 0 ? A(0) : null),

            // ── Character properties ──────────────────────────────────────────
            "getcharintprop"    => EvalCharInt(A(0)),
            "getchardoubleprop" => EvalCharDouble(A(0)),
            "getcharquadprop"   => EvalCharQuad(A(0)),
            "getcharboolprop"     => EvalCharBool(A(0)),
            "getcharstringprop"   => EvalCharString(A(0)),
            "getisspellknown"         => EvalIsSpellKnown(A(0)),
            "getcancastspell_hunt"    => EvalCanCastSpell(A(0)),
            "getcancastspell_buff"    => EvalCanCastSpell(A(0)),
            "getspellexpiration"       => EvalSpellExpiration(A(0)),
            "getspellexpirationbyname" => EvalSpellExpirationByName(A(0)),
            "getcharvital_base"        => EvalCharVitalBase(A(0)),
            "getcharvital_current"     => EvalCharVitalCurrent(A(0)),
            "getcharvital_buffedmax"   => EvalCharVitalBuffedMax(A(0)),
            "getcharskill_traininglevel" => EvalCharSkillTrainingLevel(A(0)),
            "getcharskill_base"          => EvalCharSkillBase(A(0)),
            "getcharskill_buffed"        => EvalCharSkillBuffed(A(0)),
            "getcharattribute_base"      => EvalCharAttributeBase(A(0)),
            "getcharattribute_buffed"    => EvalCharAttributeBuffed(A(0)),
            "getcharburden"              => EvalCharBurdenPct(),
            "getcharburden_total"        => EvalCharInt("5"),
            "getplayerlandcell"          => EvalPlayerLandcell(),
            "getplayerlandblock"         => EvalPlayerLandblock(),
            "getplayercoordinates"       => EvalPlayerCoordinates(),
            "coordinategetns"            => EvalCoordinateGetNS(A(0)),
            "coordinategetwe"            => EvalCoordinateGetEW(A(0)),
            "coordinategetz"             => EvalCoordinateGetZ(A(0)),
            "coordinatetostring"         => EvalCoordinateToString(A(0)),
            "coordinateparse"            => EvalCoordinateParse(string.Join(",", Enumerable.Range(0, rawArgs.Count).Select(i => A(i)))),
            "coordinatedistancewithz"    => EvalCoordinateDistanceWithZ(A(0), A(1)),
            "coordinatedistanceflat"     => EvalCoordinateDistanceFlat(A(0), A(1)),

            // ── List functions ────────────────────────────────────────────────
            "listcreate"      => EvalListCreate(rawArgs),
            // These change the list (the reference: "Returns the list object"). A list is text
            // here, so the changed list is written back to the variable it came from.
            "listadd"         => ListOp(rawArgs, A(0), l => EvalListAdd(l, A(1))),
            "listinsert"      => ListOp(rawArgs, A(0), l => EvalListInsert(l, A(1), A(2))),
            "listremove"      => ListOp(rawArgs, A(0), l => EvalListRemove(l, A(1))),
            "listremoveat"    => ListOp(rawArgs, A(0), l => EvalListRemoveAt(l, A(1))),
            "listgetitem"     => EvalListGetItem(A(0), A(1)),
            "listcontains"    => EvalListContains(A(0), A(1)),
            "listindexof"     => EvalListIndexOf(A(0), A(1)),
            "listlastindexof" => EvalListLastIndexOf(A(0), A(1)),
            "listcopy"        => EvalListCopy(A(0)),
            "listreverse"     => EvalListReverse(A(0)),
            "listpop"         => EvalListPop(rawArgs, A(0), rawArgs.Count >= 2 ? A(1) : "-1"),
            "listcount"       => EvalListCount(A(0)),
            "listclear"       => ListOp(rawArgs, A(0), EvalListClear),
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
            // Whole seconds since our last kill; before the first, since the macro start (or login).
            "secondssincelastkill" => (_killClock?.SecondsSinceLastKill() ?? 0).ToString(CultureInfo.InvariantCulture),
            "ratargetcount"        => (CombatTargetCount?.Invoke() ?? -1).ToString(CultureInfo.InvariantCulture),

            // ── World object queries ──────────────────────────────────────────
            "wobjectfindnearestbytemplatetype"        => EvalWobjectFindNearestByTemplateType(A(0)),
            "wobjectfindininventorybytemplatetype"    => EvalWobjectFindInInventoryByTemplateType(A(0)),
            "wobjectfindall"                          => EvalWobjectFindAll(),
            "wobjectfindallbyobjectclass"             => EvalWobjectFindAllByObjectClass(A(0)),
            "wobjectfindallbytemplatetype"            => EvalWobjectFindAllByTemplateType(A(0)),
            "wobjectfindallbynamerx"                  => EvalWobjectFindAllByNameRx(Text(0)),
            "wobjectfindallinventory"                 => EvalWobjectFindAllInventory(),
            "wobjectfindalllandscape"                 => EvalWobjectFindAllLandscape(),
            "wobjectfindallinventorybytemplatetype"   => EvalWobjectFindAllInventoryByTemplateType(A(0)),
            "wobjectfindallinventorybyobjectclass"    => EvalWobjectFindAllInventoryByObjectClass(A(0)),
            "wobjectfindallinventorybynamerx"         => EvalWobjectFindAllInventoryByNameRx(Text(0)),
            "wobjectfindininventorybyname"             => EvalWobjectFindInInventoryByName(Text(0)),
            "wobjectfindininventorybynamerx"          => EvalWobjectFindInInventoryByNameRx(Text(0)),
            "wobjectfindalllandscapebyobjectclass"    => EvalWobjectFindAllLandscapeByObjectClass(A(0)),
            "wobjectfindalllandscapebytemplatetype"   =>EvalWobjectFindAllLandscapeByTemplateType(A(0)),
            "wobjectfindalllandscapebynamerx"         => EvalWobjectFindAllLandscapeByNameRx(Text(0)),
            "wobjectfindallbycontainer"               => EvalWobjectFindAllByContainer(A(0)),
            "wobjectgetselection"          => EvalWobjectGetSelection(),
            "wobjectgetplayer"             => EvalWobjectGetPlayer(),
            "wobjectgetopencontainer"      => EvalWobjectGetOpenContainer(),
            "wobjectgetphysicscoordinates" => EvalWobjectGetPhysicsCoordinates(A(0)),
            "wobjectgetname"         => EvalWobjectGetName(A(0)),
            "wobjectgetid"           => EvalWobjectGetId(A(0)),
            "wobjectgettemplatetype" => EvalWobjectGetTemplateType(A(0)),
            "wobjectgetobjectclass"   => EvalWobjectGetObjClass(A(0)),
            "wobjectgetobjclass"     => EvalWobjectGetObjClass(A(0)), // legacy alias
            "wobjectfindnearestdoor" => EvalWobjectFindNearestDoor(),
            "wobjectfindnearestmonster" => EvalWobjectFindNearestMonster(),
            "wobjectfindbyid"                 => EvalWobjectFindById(A(0)),
            "wobjectfindnearestbyobjectclass" => EvalWobjectFindNearestByObjectClass(A(0)),
            "wobjectfindnearestbynameandobjectclass" => EvalWobjectFindNearestByNameAndObjectClass(A(0), Text(1)),
            "wobjectgetisdooropen"   => EvalWobjectGetIsDoorOpen(A(0)),
            "wobjectgetphysicsstate" => EvalWobjectGetPhysicsState(A(0)),
            "wobjectgetintprop"      => EvalWobjectGetIntProp(A(0), A(1)),
            "wobjectgetdoubleprop"   => EvalWobjectGetDoubleProp(A(0), A(1)),
            "wobjectgetboolprop"     => EvalWobjectGetBoolProp(A(0), A(1)),
            "wobjectgetstringprop"   => EvalWobjectGetStringProp(A(0), A(1)),
            "wobjecthasdata"         => EvalWobjectHasData(A(0)),
            "wobjectrequestdata"     => EvalWobjectRequestData(A(0)),
            "wobjectlastidtime"      => EvalWobjectLastIdTime(A(0)),
            "wobjectisvalid"         => EvalWobjectIsValid(A(0)),
            "wobjectgethealth"         => EvalWobjectGetHealth(A(0)),
            "creaturegetmaxhealth"     => EvalCreatureGetMaxHealth(A(0)),
            "creaturegetresist"        => EvalCreatureGetResist(A(0), A(1)),
            "creaturegetweakest"       => EvalCreatureGetWeakest(A(0)),
            "creaturegetarmor"         => EvalCreatureGetArmor(A(0)),
            "creaturehasprofile"       => EvalCreatureHasProfile(A(0)),
            "wobjectgetspellids"       => EvalWobjectGetSpellIds(A(0)),
            "wobjectgetactivespellids"     => EvalWobjectGetActiveSpellIds(A(0)),
            "wobjectgetactivespelldurations" => EvalWobjectGetActiveSpellDurations(A(0)),
            "getitemcountininventorybyname"   => EvalItemCountByName(Text(0)),
            "getitemcountininventorybynamerx" => EvalItemCountByNameRx(Text(0)),
            "getinventorycountbytemplatetype" => EvalGetInventoryCountByTemplateType(A(0)),
            "actiontryselect"        => EvalActionTrySelect(A(0)),
            "actiontryuseitem"       => EvalActionTryUseItem(A(0)),
            "actiontryapplyitem"     => EvalActionTryApplyItem(A(0), A(1)),
            "actiontrygiveitem"      => EvalActionTryGiveItem(A(0), A(1)),
            "actiontryequipanywand"  => EvalActionTryEquipAnyWand(),
            "actiontrycastbyid"            => EvalActionTryCastById(A(0)),
            "actiontrycastbyidontarget"    => EvalActionTryCastByIdOnTarget(A(0), A(1)),
            "actiontrygiveprofile"   => EvalActionTryGiveProfile(A(0), A(1)),
            "getequippedweapontype"  => EvalGetEquippedWeaponType(),
            "getcombatstate"         => EvalGetCombatState(),
            "setcombatstate"         => EvalSetCombatState(A(0)),
            "getbusystate"           => EvalGetBusyState(),
            "setmotion"              => EvalSetMotion(A(0), A(1)),
            "getmotion"              => EvalGetMotion(A(0)),
            "clearmotion"            => EvalClearMotion(),

            // ── Quest flag queries ────────────────────────────────────────────
            "testquestflag"        => EvalTestQuestFlag(A(0)),
            "getqueststatus"       => EvalGetQuestStatus(A(0)),
            "getquestktprogress"   => EvalGetQuestKtProgress(A(0)),
            "getquestktrequired"   => EvalGetQuestKtRequired(A(0)),
            "isrefreshingquests"   => (_questTracker?.IsRefreshing == true) ? "1" : "0",
            "refreshquests"        => EvalRefreshQuests(),

            // ── Spell / component lookups ─────────────────────────────────────
            "spellname"      => SpellDatabase.GetSpellName((int)ToDouble(A(0))),
            "componentname"  => ComponentDatabase.GetComponentName((uint)ToDouble(A(0))),
            "componentdata"  => EvalComponentData(A(0)),
            "getknownspells" => EvalGetKnownSpells(),

            "getheadingto"           => EvalGetHeadingTo(A(0)),
            "getheading"             => EvalGetHeading(A(0)),

            // ── Math ──────────────────────────────────────────────────────────
            "getobjectinternaltype" => EvalGetObjectInternalType(A(0)),
            // "Checks if a value is equal to false (0)" / "equal to true (1)": text is neither
            // (isfalse[abc] was 1), and 2 is not 1.
            "isfalse"  => IsNumber(A(0), out double fv) && fv == 0 ? "1" : "0",
            "istrue"   => IsNumber(A(0), out double tv) && tv == 1 ? "1" : "0",
            "randint"  => Random.Shared.Next((int)ToDouble(A(0)), (int)ToDouble(A(1))).ToString(CultureInfo.InvariantCulture),
            "cstr"     => A(0),
            "cstrf"    => EvalCstrf(A(0), Text(1)),
            "cnumber"  => ToDouble(A(0)).ToString("G", CultureInfo.InvariantCulture),
            "strlen"   => A(0).Length.ToString(CultureInfo.InvariantCulture),
            "floor"    => Fmt(Math.Floor(ToDouble(A(0)))),
            "ceiling"  => Fmt(Math.Ceiling(ToDouble(A(0)))),
            "round"    => Fmt(Math.Round(ToDouble(A(0)), MidpointRounding.AwayFromZero)),
            "abs"      => Math.Abs(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "ord"      => EvalOrd(A(0)),
            "chr"      => EvalChr(A(0)),

            // ── Stopwatch ─────────────────────────────────────────────────────
            "stopwatchcreate"         => EvalStopwatchCreate(),
            "stopwatchstart"          => EvalStopwatchStart(A(0)),
            "stopwatchstop"           => EvalStopwatchStop(A(0)),
            "stopwatchelapsedseconds" => EvalStopwatchElapsedSeconds(A(0)),
            "hexstr" => "0x" + ((long)ToDouble(A(0))).ToString("X", CultureInfo.InvariantCulture),
            "acos"   => Math.Acos(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "asin"   => Math.Asin(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "atan"   => Math.Atan(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "atan2"  => Math.Atan2(ToDouble(A(0)), ToDouble(A(1))).ToString("G", CultureInfo.InvariantCulture),
            "cos"    => Math.Cos(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "cosh"   => Math.Cosh(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "sin"    => Math.Sin(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "sinh"   => Math.Sinh(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "sqrt"   => Math.Sqrt(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "tan"    => Math.Tan(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "tanh"   => Math.Tanh(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),

            // ── Chat ──────────────────────────────────────────────────────────
            "chatbox"      => EvalChatbox(A(0)),
            "chatboxpaste" => "0",   // no backing API — stub
            "echo"         => EvalEcho(A(0), A(1)),

            // ── Salvage (UST) ─────────────────────────────────────────────────
            "ustopen"    => EvalUstOpen(),
            "ustadd"     => EvalUstAdd(A(0)),
            "ustsalvage" => EvalUstSalvage(),

            // ── Game time (Dereth) ────────────────────────────────────────────
            "getgameyear"          => GetGameYear().ToString(CultureInfo.InvariantCulture),
            "getgamemonth"         => GetGameMonth().ToString(CultureInfo.InvariantCulture),
            // The reference's own example calls it getmonthname[1].
            "getgamemonthname" or "getmonthname" => GetGameMonthName(A(0)),
            "getgameday"           => GetGameDay().ToString(CultureInfo.InvariantCulture),
            "getgamehour"          => GetGameHour().ToString(CultureInfo.InvariantCulture),
            "getgamehourname"      => GetGameHourName(A(0)),
            "getminutesuntilday"   => GetMinutesUntilDay().ToString(CultureInfo.InvariantCulture),
            "getminutesuntilnight" => GetMinutesUntilNight().ToString(CultureInfo.InvariantCulture),
            "getgameticks"         => GetGameTicks().ToString("G", CultureInfo.InvariantCulture),
            "getisday"             => GetIsDay() ? "1" : "0",
            "getisnight"           => GetIsDay() ? "0" : "1",

            // ── Fellowship ────────────────────────────────────────────────────
            "getfellowshipname"        => _fellowshipTracker?.FellowshipName ?? "",
            "getfellowshipcount"       => (_fellowshipTracker?.MemberCount ?? 0).ToString(CultureInfo.InvariantCulture),
            "getfellowshipleaderid"    => (_fellowshipTracker?.LeaderId ?? 0).ToString(CultureInfo.InvariantCulture),
            "getfellowshiplocked"      => (_fellowshipTracker?.IsLocked == true ? "1" : "0"),
            "getfellowshipisleader"    => (_fellowshipTracker?.IsLeader == true ? "1" : "0"),
            "getfellowshipisopen"      => (_fellowshipTracker?.IsOpen == true ? "1" : "0"),
            "getfellowshipstatus"      => EvalGetFellowshipStatus(),
            "getfellowshipisfull"      => EvalFellowshipIsFull(),
            "getfellowshipcanrecruit"  => EvalFellowshipCanRecruit(),
            "getfellowid"              => EvalGetFellowId(A(0)),
            "getfellowname"            => EvalGetFellowName(A(0)),
            "getfellownames"           => EvalGetFellowNames(),
            "getfellowids"             => EvalGetFellowIds(),

            // ── Plugin options ────────────────────────────────────────────────
            "raoptget" or "uboptget" => GetOption(A(0)),
            "raoptset" or "uboptset" => EvalOptSet(A(0), A(1)),

            // ── RynthAi settings / meta state (VTank-compatible names) ────────
            "rasetmetastate" or "vtsetmetastate" or "setmetastate" => EvalVtSetMetaState(Text(0)),
            "ragetmetastate" or "vtgetmetastate" or "getmetastate" => _settings?.CurrentState ?? "",
            // UB vtgetmeta[]: the loaded meta's name (file name without extension).
            "vtgetmeta" or "ragetmeta" => string.IsNullOrEmpty(_settings?.CurrentMetaPath)
                ? "" : Path.GetFileNameWithoutExtension(_settings!.CurrentMetaPath),
            // vt*: VTank option names and units (landblock distances), the same as '/vt opt set|get';
            // ra*: RynthAi's own setting names, as they are.
            "vtsetsetting"                       => VtOptionSetter != null && Text(0).Length > 0 ? (VtOptionSetter(Text(0), A(1)) ? "1" : "0") : EvalVtSetSetting(Text(0), A(1)),
            "vtgetsetting" or "vtankgetsetting"  => VtOptionGetter != null && Text(0).Length > 0 ? VtOptionGetter(Text(0)) ?? "" : EvalVtGetSetting(Text(0)),
            "rasetsetting"                       => EvalVtSetSetting(Text(0), A(1)),
            "ragetsetting"                       => EvalVtGetSetting(Text(0)),

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

            // "Strings support slices like arrays. ie test[1:]": a word that isn't a function,
            // followed by [start:stop].
            _ when TopLevelColon(argsStr) >= 0 => SliceBy(expr.Substring(0, lb).Trim(), argsStr),
            // ub-IT's remaining functions (ExpressionEngine.UbCompat.cs), then a real unknown.
            _ => TryEvalUbCompat(funcName, rawArgs, A, out string ubValue) ? ubValue : EvalUnknownFunction(funcName)
        };
    }

    /// <summary>Unknown-function fallback (M3): returns "" per engine convention but logs the
    /// missing verb ONCE per distinct name, so a typo'd or unsupported meta function is visible
    /// instead of silently evaluating to empty. Rate-limited by the seen-set to avoid log spam.</summary>
    private readonly HashSet<string> _loggedUnknownFns = new();
    private bool _sawUnknownFn;   // set by any unknown call (TextArg asks)
    private int _quietUnknownFn;  // > 0: don't log (TextArg is only trying an argument)
    private string EvalUnknownFunction(string funcName)
    {
        _sawUnknownFn = true;
        if (_quietUnknownFn == 0 && !string.IsNullOrEmpty(funcName) && _loggedUnknownFns.Add(funcName))
            _host.Log($"[Meta] unknown expression function '{funcName}[...]' — evaluates to empty; " +
                      "check spelling / supported verbs. (logged once per name)");
        return "";
    }

    // ── Variable / char-prop implementations ──────────────────────────────────

    public void SetVariable(string key, string value)
    {
        if (!string.IsNullOrEmpty(key)) _variables[key] = value;
    }

    private string EvalSetVar(string key, string value)
    {
        if (!string.IsNullOrEmpty(key)) _variables[key] = value;
        return value;
    }

    private string EvalTouchVar(string key)
    {
        if (_variables.ContainsKey(key)) return "1";
        _variables[key] = "0";
        return "0";
    }

    private string EvalClearAllVars()
    {
        _variables.Clear();
        return "1";
    }

    // ── Persistent variable implementations ───────────────────────────────────

    private Dictionary<string, string> GetPvars()
    {
        if (_pvars != null) return _pvars;
        return _pvars = LoadVarFile(GetPvarPath());
    }

    private string GetPvarPath()
    {
        if (_pvarPathCached != null) return _pvarPathCached;
        string charName = "unknown";
        if (_playerId != 0 && _host.HasGetObjectName)
            _host.TryGetObjectName(_playerId, out charName);
        charName = SanitizeFileName(charName ?? "unknown");
        string p = Path.Combine(PvarsDir, $"{charName}.txt");
        if (charName != "unknown") _pvarPathCached = p;   // only cache a resolved name
        return p;
    }

    private string EvalSetPvar(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return value;
        GetPvars()[key] = value;
        _pvarsDirty = true;
        return value;
    }

    private string EvalTouchPvar(string key)
    {
        var pvars = GetPvars();
        if (pvars.ContainsKey(key)) return "1";
        pvars[key] = "0";
        _pvarsDirty = true;
        return "0";
    }

    private string EvalClearPvar(string key)
    {
        var pvars = GetPvars();
        if (!pvars.Remove(key)) return "0";
        _pvarsDirty = true;
        return "1";
    }

    private string EvalClearAllPvars()
    {
        GetPvars().Clear();
        _pvarsDirty = true;
        return "1";
    }

    // ── Global variable implementations ───────────────────────────────────────

    private Dictionary<string, string> GetGvars()
        => _gvars ??= LoadVarFile(GvarsPath);

    private string EvalSetGvar(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return value;
        GetGvars()[key] = value;
        _gvarsDirty = true;
        return value;
    }

    private string EvalTouchGvar(string key)
    {
        var gvars = GetGvars();
        if (gvars.ContainsKey(key)) return "1";
        gvars[key] = "0";
        _gvarsDirty = true;
        return "0";
    }

    private string EvalClearGvar(string key)
    {
        var gvars = GetGvars();
        if (!gvars.Remove(key)) return "0";
        _gvarsDirty = true;
        return "1";
    }

    private string EvalClearAllGvars()
    {
        GetGvars().Clear();
        _gvarsDirty = true;
        return "1";
    }

    /// <summary>
    /// Writes dirty pvar/gvar files. Called from the plugin-tick pump; throttled
    /// to <see cref="VarFlushIntervalMs"/> unless <paramref name="force"/>
    /// (char switch / macro stop / shutdown) to bound data loss to ~2 s without
    /// a per-tick synchronous disk write on the game thread.
    /// </summary>
    public void FlushVars(bool force = false)
    {
        if (!_pvarsDirty && !_gvarsDirty) return;
        long now = TickMs();
        if (!force && now - _lastVarFlushMs < VarFlushIntervalMs) return;
        if (_pvarsDirty && _pvars != null) { SaveVarFile(GetPvarPath(), _pvars); _pvarsDirty = false; }
        if (_gvarsDirty && _gvars != null) { SaveVarFile(GvarsPath, _gvars);     _gvarsDirty = false; }
        _lastVarFlushMs = now;
    }

    // ── Var file helpers ──────────────────────────────────────────────────────

    private static Dictionary<string, string> LoadVarFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return dict;
        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0) dict[line[..tab]] = line[(tab + 1)..];
            }
        }
        catch { /* return whatever was loaded */ }
        return dict;
    }

    private static void SaveVarFile(string path, Dictionary<string, string> dict)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, dict.Select(kv => $"{kv.Key}\t{kv.Value}"));
        }
        catch { /* non-fatal */ }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length > 0 ? sb.ToString() : "unknown";
    }

    // ── Stopwatch implementations ─────────────────────────────────────────────

    private string EvalStopwatchCreate()
    {
        string handle = $"SW:{_nextSwId++}";
        _stopwatches[handle] = new System.Diagnostics.Stopwatch();
        return handle;
    }

    private string EvalStopwatchStart(string handle)
    {
        if (_stopwatches.TryGetValue(handle, out var sw)) sw.Start();
        return handle;
    }

    private string EvalStopwatchStop(string handle)
    {
        if (_stopwatches.TryGetValue(handle, out var sw)) sw.Stop();
        return handle;
    }

    private string EvalStopwatchElapsedSeconds(string handle)
    {
        if (!_stopwatches.TryGetValue(handle, out var sw)) return "0";
        return sw.Elapsed.TotalSeconds.ToString("G", CultureInfo.InvariantCulture);
    }

    private static string EvalOrd(string s)
        => s.Length > 0 ? ((int)s[0]).ToString(CultureInfo.InvariantCulture) : "0";

    private static string EvalChr(string arg)
    {
        int code = (int)ToDouble(arg);
        return code is >= 0 and <= 0xFFFF ? ((char)code).ToString() : "";
    }

    private static string EvalCstrf(string numArg, string fmt)
    {
        double d = ToDouble(numArg);
        try { return d.ToString(fmt, CultureInfo.InvariantCulture); }
        catch { return d.ToString("G", CultureInfo.InvariantCulture); }
    }

    private static bool IsNumber(string s, out double d)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d);

    // 0=none, 1=number, 3=string, 7=object — matches UB internal type constants. Lists and
    // coordinates are objects in UB (they read as 3, text, because they are text here).
    private static string EvalGetObjectInternalType(string val)
    {
        if (string.IsNullOrEmpty(val)) return "0";
        // A world object handle. Not a bare number: an id is still a number (any nonzero
        // integer used to read as an object, getobjectinternaltype[5] was 7).
        if (val.StartsWith("[WorldObject] ", StringComparison.Ordinal) && TryParseWobjectHandle(val, out _, out _)) return "7";
        if (val.Length > 3 && val[0] == 'S' && val[1] == 'W' && val[2] == ':') return "7";
        if (val.Length > 2 && val[0] == 'D' && val[1] == ':') return "7";
        if (double.TryParse(val, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return "1";
        if (GetList(val) != null) return "7";
        if (val.Split('|').Length == 3 && TryParseCoordObject(val, out _, out _, out _)) return "7";
        return "3";
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

    private string EvalCharQuad(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectQuadProperty) return "0";
        return _host.TryGetObjectQuadProperty(_playerId, st, out long v) ? v.ToString(CultureInfo.InvariantCulture) : "0";
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

    private string EvalIsSpellKnown(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint spellId) || _playerId == 0 || !_host.HasIsSpellKnown) return "0";
        return _host.IsSpellKnown(_playerId, spellId, out bool known) ? (known ? "1" : "0") : "0";
    }

    private string EvalCanCastSpell(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint spellId) || _playerId == 0) return "0";
        if (_host.HasIsSpellKnown && !(_host.IsSpellKnown(_playerId, spellId, out bool known) && known)) return "0";
        if (_host.HasGetPlayerVitals &&
            _host.TryGetPlayerVitals(out _, out _, out _, out _, out uint mana, out _) && mana == 0) return "0";
        return "1";
    }

    private string EvalCharVitalBase(string arg)
    {
        // Map vitalId 1/2/3 → STypeAttribute2nd 1/3/5 (MAX_HEALTH/MAX_STAMINA/MAX_MANA)
        // Base = _initLevel + _levelFromCp from InqAttribute2ndStruct — confirmed unbuffed in PlayerVitalsHooks.
        if (!_host.HasGetPlayerBaseVitals) return "0";
        if (!_host.TryGetPlayerBaseVitals(out uint baseHp, out uint baseStam, out uint baseMana)) return "0";
        return arg.Trim() switch
        {
            "1" => baseHp.ToString(CultureInfo.InvariantCulture),
            "2" => baseStam.ToString(CultureInfo.InvariantCulture),
            "3" => baseMana.ToString(CultureInfo.InvariantCulture),
            _   => "0",
        };
    }

    private string EvalCharVitalCurrent(string arg)
    {
        if (!_host.HasGetPlayerVitals) return "0";
        if (!_host.TryGetPlayerVitals(out uint hp, out _, out uint stam, out _, out uint mana, out _)) return "0";
        return arg.Trim() switch
        {
            "1" => hp.ToString(CultureInfo.InvariantCulture),
            "2" => stam.ToString(CultureInfo.InvariantCulture),
            "3" => mana.ToString(CultureInfo.InvariantCulture),
            _   => "0",
        };
    }

    private string EvalCharVitalBuffedMax(string arg)
    {
        // Uses InqAttribute2nd live — always returns current buffed max, no stale snapshot issue.
        uint stype2nd = arg.Trim() switch { "1" => 1u, "2" => 3u, "3" => 5u, _ => 0u };
        if (stype2nd == 0 || _playerId == 0 || !_host.HasGetObjectAttribute2ndBaseLevel) return "0";
        return _host.TryGetObjectAttribute2ndBaseLevel(_playerId, stype2nd, out uint buffedMax)
            ? buffedMax.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharSkillTrainingLevel(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint skillId) || skillId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectSkill) return "0";
        return _host.TryGetObjectSkill(_playerId, skillId, out _, out int training)
            ? training.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharSkillBase(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint skillId) || skillId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectSkillBuffed) return "0";
        return _host.TryGetObjectSkillLevel(_playerId, skillId, 1, out int level)
            ? level.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharSkillBuffed(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint skillId) || skillId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectSkillBuffed) return "0";
        return _host.TryGetObjectSkillLevel(_playerId, skillId, 0, out int level)
            ? level.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharAttributeBase(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint attrId) || attrId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectAttribute) return "0";
        return _host.TryGetObjectAttribute(_playerId, attrId, 1, out uint value)
            ? value.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharAttributeBuffed(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint attrId) || attrId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectAttribute) return "0";
        return _host.TryGetObjectAttribute(_playerId, attrId, 0, out uint value)
            ? value.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharBurdenPct()
    {
        // Burden capacity = buffed Strength * 150 (not stored as a property, derived from attribute)
        if (_playerId == 0 || !_host.HasGetObjectIntProperty || !_host.HasGetObjectAttribute) return "0";
        if (!_host.TryGetObjectIntProperty(_playerId, 5u, out int current)) return "0";
        if (!_host.TryGetObjectAttribute(_playerId, 1u, 0, out uint strength) || strength == 0) return "0";
        double pct = current * 100.0 / ((double)strength * 150.0);
        return pct.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalPlayerLandcell()
    {
        if (!_host.HasGetPlayerPose) return "0";
        if (!_host.TryGetPlayerPose(out uint objCellId, out _, out _, out _, out _, out _, out _, out _)) return "0";
        return ((double)objCellId).ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalPlayerLandblock()
    {
        if (!_host.HasGetPlayerPose) return "0";
        if (!_host.TryGetPlayerPose(out uint objCellId, out _, out _, out _, out _, out _, out _, out _)) return "0";
        return ((double)(objCellId & 0xFFFF0000u)).ToString("G", CultureInfo.InvariantCulture);
    }

    // Coordinates object internal format: "NS|EW|Z" (pipe-delimited, full precision).
    // NS positive=N, negative=S. EW positive=E, negative=W. Z is in coordinate units (raw_z/240).
    // coordinatetostring[] formats for display ("41.53N, 34.46E, 0.47Z"); coordinateparse[] converts display → internal.

    private static readonly System.Text.RegularExpressions.Regex _coordParseRegex = new(
        @"(?<NSval>[0-9]{1,3}(?:\.[0-9]{1,3})?)(?<NSchr>[ns])(?:[,\s]+)?(?<EWval>[0-9]{1,3}(?:\.[0-9]{1,3})?)(?<EWchr>[ew])?(,?\s*(?<Zval>-?\d+\.?\d*)z)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool TryParseCoordObject(string coords, out double ns, out double ew, out double z)
    {
        ns = ew = z = 0;
        if (string.IsNullOrEmpty(coords)) return false;
        var parts = coords.Split('|');
        if (parts.Length < 2) return false;
        return double.TryParse(parts[0], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out ns)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out ew)
            && (parts.Length < 3 || double.TryParse(parts[2], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out z));
    }

    private static string MakeCoordObject(double ns, double ew, double z)
        => string.Create(CultureInfo.InvariantCulture, $"{ns}|{ew}|{z}");

    private string EvalPlayerCoordinates()
    {
        if (!_host.HasGetPlayerPose) return "";
        if (!_host.TryGetPlayerPose(out uint objCellId, out float x, out float y, out float z, out _, out _, out _, out _)) return "";
        if (!LegacyUi.NavCoordinateHelper.TryConvertPoseToCoords(objCellId, x, y, out double ns, out double ew)) return "";
        return MakeCoordObject(ns, ew, z / 240.0);
    }

    private string EvalCoordinateGetNS(string coords)
    {
        if (!TryParseCoordObject(coords, out double ns, out _, out _)) return "0";
        return ns.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalCoordinateGetEW(string coords)
    {
        if (!TryParseCoordObject(coords, out _, out double ew, out _)) return "0";
        return ew.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalCoordinateGetZ(string coords)
    {
        if (!TryParseCoordObject(coords, out _, out _, out double z)) return "0";
        return z.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalCoordinateToString(string coords)
    {
        if (!TryParseCoordObject(coords, out double ns, out double ew, out double z)) return "";
        string nsStr = ns >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ns:F2}N")
            : string.Create(CultureInfo.InvariantCulture, $"{-ns:F2}S");
        string ewStr = ew >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ew:F2}E")
            : string.Create(CultureInfo.InvariantCulture, $"{-ew:F2}W");
        return string.Create(CultureInfo.InvariantCulture, $"{nsStr}, {ewStr}, {z:F2}Z");
    }

    private string EvalCoordinateParse(string input)
    {
        var m = _coordParseRegex.Match(input.Trim());
        if (!m.Success) return "";
        if (!double.TryParse(m.Groups["NSval"].Value, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double ns)) return "";
        if (!double.TryParse(m.Groups["EWval"].Value, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double ew)) return "";
        ns *= m.Groups["NSchr"].Value.Equals("n", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        ew *= m.Groups["EWchr"].Value.Equals("e", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        double z = 0;
        if (m.Groups["Zval"].Success && !string.IsNullOrEmpty(m.Groups["Zval"].Value))
            double.TryParse(m.Groups["Zval"].Value, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out z);
        return MakeCoordObject(ns, ew, z);
    }

    private string EvalCoordinateDistanceWithZ(string coords1, string coords2)
    {
        if (!TryParseCoordObject(coords1, out double ns1, out double ew1, out double z1)) return "0";
        if (!TryParseCoordObject(coords2, out double ns2, out double ew2, out double z2)) return "0";
        double dNS = (ns1 - ns2) * 240.0;
        double dEW = (ew1 - ew2) * 240.0;
        double dZ  = (z1  - z2)  * 240.0;
        return Math.Sqrt(dNS * dNS + dEW * dEW + dZ * dZ).ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalWobjectGetPhysicsCoordinates(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "";
        if (!_host.HasGetObjectPosition) return "";
        if (!_host.TryGetObjectPosition(uid, out uint objCellId, out float x, out float y, out float z)) return "";
        if (!LegacyUi.NavCoordinateHelper.TryConvertPoseToCoords(objCellId, x, y, out double ns, out double ew)) return "";
        return MakeCoordObject(ns, ew, z / 240.0);
    }

    private string EvalCoordinateDistanceFlat(string coords1, string coords2)
    {
        if (!TryParseCoordObject(coords1, out double ns1, out double ew1, out _)) return "0";
        if (!TryParseCoordObject(coords2, out double ns2, out double ew2, out _)) return "0";
        double dNS = (ns1 - ns2) * 240.0;
        double dEW = (ew1 - ew2) * 240.0;
        return Math.Sqrt(dNS * dNS + dEW * dEW).ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalSpellExpiration(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint spellId)) return "0";
        if (!_host.HasReadPlayerEnchantments || !_host.HasGetServerTime) return "0";
        double serverNow = _host.GetServerTime();
        if (serverNow <= 0) return "0";
        const int Max = 512;
        uint[] ids = new uint[Max];
        double[] expiry = new double[Max];
        int count = _host.ReadPlayerEnchantments(ids, expiry, Max);
        if (count <= 0) return "0";
        for (int i = 0; i < count; i++)
        {
            if (ids[i] != spellId) continue;
            double remaining = expiry[i] - serverNow;
            if (remaining <= 0) return "0";
            if (remaining > 86400 * 365) return "2147483647";
            return ((int)Math.Ceiling(remaining)).ToString(CultureInfo.InvariantCulture);
        }
        return "0";
    }

    private string EvalSpellExpirationByName(string nameArg)
    {
        if (string.IsNullOrEmpty(nameArg)) return "-1";
        if (!_host.HasReadPlayerEnchantments || !_host.HasGetServerTime) return "-1";
        double serverNow = _host.GetServerTime();
        if (serverNow <= 0) return "-1";
        const int Max = 512;
        uint[] ids = new uint[Max];
        double[] expiry = new double[Max];
        int count = _host.ReadPlayerEnchantments(ids, expiry, Max);
        if (count < 0) return "-1";
        for (int i = 0; i < count; i++)
        {
            var info = SpellTableStub.GetById((int)ids[i]);
            if (info == null) continue;
            if (info.Name.IndexOf(nameArg, StringComparison.OrdinalIgnoreCase) < 0) continue;
            double remaining = expiry[i] - serverNow;
            if (remaining <= 0) return "0";
            if (remaining > 86400 * 365) return "2147483647";
            return ((int)Math.Ceiling(remaining)).ToString(CultureInfo.InvariantCulture);
        }
        return "0";
    }

    // ── List helpers ──────────────────────────────────────────────────────────

    private string EvalListCreate(List<string> rawArgs)
    {
        if (rawArgs.Count == 0) return "[]";
        var items = new List<string>(rawArgs.Count);
        foreach (var arg in rawArgs) items.Add(Evaluate(arg));
        return NewList(items);
    }

    // List text: "[item,item,...]". An item that is itself a whole list is written as is, so
    // lists nest ("[a,[b,c]]"); any other item has '\', ',', '[' and ']' escaped with '\'.
    // Items without those characters are written exactly as before ("[a,b]").
    private static string NewList(IEnumerable<string>? items = null)
    {
        if (items == null) return "[]";
        var sb = new System.Text.StringBuilder("[");
        bool first = true;
        foreach (string? item in items)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendListItem(sb, item ?? "");
        }
        return sb.Append(']').ToString();
    }

    private static void AppendListItem(System.Text.StringBuilder sb, string item)
    {
        if (IsWholeList(item)) { sb.Append(item); return; }
        foreach (char c in item)
        {
            if (c is '\\' or ',' or '[' or ']') sb.Append('\\');
            sb.Append(c);
        }
    }

    /// <summary>True when <paramref name="s"/> is one list: its first '[' closes on the last character (escapes skipped).</summary>
    private static bool IsWholeList(string s)
    {
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']') return false;
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i == s.Length - 1;
        }
        return false;
    }

    private static List<string>? GetList(string handle)
    {
        handle = handle.Trim();
        if (handle.Length < 2 || handle[0] != '[' || handle[handle.Length - 1] != ']') return null;
        string inner = handle.Substring(1, handle.Length - 2);
        var items = new List<string>();
        if (inner.Length == 0) return items;

        // Split on commas outside nested lists. Escapes are undone for this list's own items;
        // a nested list keeps them for when it is read as a list itself. Only "\\", "\,", "\["
        // and "\]" are escapes (all NewList writes); any other backslash is plain text, so a
        // list saved before escaping existed ("[c:\d]") reads back unchanged.
        var cur = new System.Text.StringBuilder();
        int depth = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\\' && i + 1 < inner.Length && inner[i + 1] is '\\' or ',' or '[' or ']')
            {
                if (depth > 0) cur.Append(c);
                cur.Append(inner[++i]);
                continue;
            }
            if (c == '[') depth++;
            else if (c == ']' && depth > 0) depth--;
            else if (c == ',' && depth == 0) { items.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        items.Add(cur.ToString());
        return items;
    }

    /// <summary>
    /// A name, pattern, state, setting or format argument. The reference makes these ordinary
    /// string arguments, so an expression is evaluated: "$itemname", "getvar[x]",
    /// "`^`+$name+`$`", "\(Knight\|Wisp\)\.\+Essence". They used to be taken as typed, so all
    /// of those searched for their own source text. Anything else is still taken as typed, so
    /// plain text with punctuation keeps working: "The Baron's Key", "High-Stakes Gambling
    /// Token", "(Contract)", "Recharge-Norm-Hitp". A lone word[...] that isn't a function
    /// (a regex such as "Pack[0-9]") stays as typed too.
    /// </summary>
    private string TextArg(string raw)
    {
        string a = raw.Trim();
        if (a.Length == 0) return "";
        bool expr = a[0] is '`' or '$' or '@'
            || (a[0] == '&' && a.Length > 1 && (char.IsLetterOrDigit(a[1]) || a[1] == '_'))
            || a.IndexOf('\\') >= 0;
        if (expr) return Evaluate(a);

        int lb = a.IndexOf('[');
        if (lb <= 0 || FindClose(a, lb, '[', ']') != a.Length) return a;
        for (int i = 0; i < lb; i++)
            if (!char.IsLetterOrDigit(a[i]) && a[i] != '_') return a;
        _sawUnknownFn = false;
        _quietUnknownFn++;
        try
        {
            string v = Evaluate(a);
            return _sawUnknownFn ? a : v;
        }
        finally { _quietUnknownFn--; }
    }

    /// <summary>
    /// A list function's expression argument: the text inside its backticks, with the two
    /// escapes a backtick string has ("\`" and "\\") turned back into what they stand for, so
    /// the inner expression can quote text itself. Without that, listfilter[...,`chatbox[\`/t \`+
    /// wobjectgetname[$1]+\`, xp\`]`] evaluated "\`/t \`" with the backslashes still in and
    /// sent "0Name`" as a say (2026-10-13, CQ_DragonIsle_Water.met).
    /// </summary>
    private static string StripBackticks(string s)
    {
        s = s.Trim();
        if (!(s.Length >= 2 && s[0] == '`' && s[s.Length - 1] == '`'))
            return s;
        string inner = s.Substring(1, s.Length - 2);
        if (inner.IndexOf('\\') < 0)
            return inner;
        var sb = new System.Text.StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] is '`' or '\\')
                sb.Append(inner[++i]);
            else
                sb.Append(inner[i]);
        }
        return sb.ToString();
    }

    // ── List function implementations ─────────────────────────────────────────

    private static string EvalListAdd(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        list.Add(item);
        return NewList(list);
    }

    private static string EvalListRemove(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        for (int i = 0; i < list.Count; i++)
        {
            if (AreEqual(list[i], item)) { list.RemoveAt(i); break; }
        }
        return NewList(list);
    }

    private static string EvalListRemoveAt(string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        int idx = (int)ToLong(indexArg);
        if (idx < 0 || idx > list.Count - 1) return "0";
        list.RemoveAt(idx);
        return NewList(list);
    }

    private static string EvalListInsert(string handle, string item, string indexArg)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        int idx = (int)ToLong(indexArg);
        if (idx < 0 || idx > list.Count) return "0";
        list.Insert(idx, item);
        return NewList(list);
    }

    private static string EvalListGetItem(string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null) return "";
        int idx = (int)ToLong(indexArg);
        return idx >= 0 && idx < list.Count ? list[idx] : "";
    }

    private static string EvalListContains(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        foreach (var s in list)
            if (AreEqual(s, item)) return "1";
        return "0";
    }

    private static string EvalListIndexOf(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "-1";
        for (int i = 0; i < list.Count; i++)
            if (AreEqual(list[i], item)) return Fmt((long)i);
        return "-1";
    }

    private static string EvalListLastIndexOf(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "-1";
        for (int i = list.Count - 1; i >= 0; i--)
            if (AreEqual(list[i], item)) return Fmt((long)i);
        return "-1";
    }

    private static string EvalListCopy(string handle)
        => GetList(handle) != null ? handle.Trim() : "[]";

    private static string EvalListReverse(string handle)
    {
        var list = GetList(handle);
        if (list == null) return "[]";
        var copy = new List<string>(list);
        copy.Reverse();
        return NewList(copy);
    }

    /// <summary>Removes the item and returns it (it used to stay in the list: listpop on a
    /// variable never shrank it).</summary>
    private string EvalListPop(List<string> rawArgs, string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null || list.Count == 0) return "";
        int idx = (int)ToLong(indexArg);
        if (idx < 0) idx = list.Count - 1; // -1 = last item
        if (idx < 0 || idx >= list.Count) return "";
        string item = list[idx];
        list.RemoveAt(idx);
        WriteBackList(rawArgs, NewList(list));
        return item;
    }

    /// <summary>Runs a list-changing function and, when its list came from a variable
    /// ($x, @x, &amp;x, getvar[x], getpvar[x], getgvar[x]), stores the changed list there, so
    /// "listadd[getvar[CharList], Drakkon]" adds to CharList. A list given any other way
    /// (listcreate[...], an item of another list) just gives the changed copy.</summary>
    private string ListOp(List<string> rawArgs, string handle, Func<string, string> op)
    {
        string result = op(handle);
        if (GetList(handle) != null && GetList(result) != null) WriteBackList(rawArgs, result);
        return result;
    }

    private static readonly Regex ListSourceCall = new(@"^(getvar|getpvar|getgvar)\[(.*)\]$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private void WriteBackList(List<string> rawArgs, string list)
    {
        if (rawArgs.Count == 0) return;
        string a = rawArgs[0].Trim();
        if (a.Length > 1 && a[0] is '$' or '@' or '&' && a.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            WriteVar(a[0], a.Substring(1), list);
            return;
        }
        var m = ListSourceCall.Match(a);
        if (!m.Success || FindClose(a, m.Groups[1].Length, '[', ']') != a.Length) return;
        char sigil = char.ToLowerInvariant(m.Groups[1].Value[3]) switch { 'p' => '@', 'g' => '&', _ => '$' };
        string name = Evaluate(m.Groups[2].Value);
        if (name.Length > 0) WriteVar(sigil, name, list);
    }

    private static string EvalListCount(string handle)
    {
        var list = GetList(handle);
        return list != null ? Fmt((long)list.Count) : "0";
    }

    private static string EvalListClear(string _) => "[]";

    private static string EvalListFromRange(string startArg, string endArg)
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
    //
    // Deep-audit finding #26 (2026-06-18): despite this comment, none of the
    // four functions below actually restored "0"/"1"/"2" afterward — they
    // wrote straight into the persistent _variables dictionary and left
    // whatever the last loop iteration set. Nested list ops clobbered each
    // other's $0/$1 mid-evaluation, and a user's own numeric-named $0/$1/$2
    // (if they had any) were silently overwritten for the rest of the meta.
    // Snapshot before, restore-or-Remove in a finally, per the fix note.

    /// <summary>Snapshots the given _variables keys, returning a restore action for a finally block.</summary>
    private Action SnapshotVars(params string[] keys)
    {
        var saved = new (string Key, bool Had, string? Value)[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            saved[i] = (keys[i], _variables.TryGetValue(keys[i], out var v), v);
        return () =>
        {
            foreach (var (key, had, value) in saved)
            {
                if (had) _variables[key] = value!;
                else _variables.Remove(key);
            }
        };
    }

    private string EvalListFilter(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || exprTemplate.Length == 0) return NewList();
        var restore = SnapshotVars("0", "1");
        try
        {
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
        finally { restore(); }
    }

    private string EvalListMap(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || exprTemplate.Length == 0) return NewList();
        var restore = SnapshotVars("0", "1");
        try
        {
            var result = new List<string>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                _variables["0"] = Fmt((long)i);
                _variables["1"] = list[i];
                result.Add(Evaluate(exprTemplate));
            }
            return NewList(result);
        }
        finally { restore(); }
    }

    private string EvalListReduce(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || list.Count == 0 || exprTemplate.Length == 0) return "0";
        var restore = SnapshotVars("0", "1", "2");
        try
        {
            string acc = "0";
            for (int i = 0; i < list.Count; i++)
            {
                _variables["0"] = Fmt((long)i);
                _variables["1"] = list[i];
                _variables["2"] = acc;
                acc = Evaluate(exprTemplate);
            }
            return acc;
        }
        finally { restore(); }
    }

    private string EvalListSort(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null) return NewList();
        var copy = new List<string>(list);
        var restore = SnapshotVars("1", "2");
        try
        {
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
        finally { restore(); }
    }

    // ── World state implementations ───────────────────────────────────────────

    private string EvalIsPortaling()
    {
        if (!_host.HasIsPortaling) return "0";
        return _host.IsPortaling() ? "1" : "0";
    }

    private string EvalVitae()
    {
        // Unreadable (no engine call, or before login): no known penalty. It returned
        // 100, a full penalty, while a real "no vitae" reading is 0.
        if (!_host.HasGetVitae || _playerId == 0) return "0";
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

    private string EvalWobjectFindInInventoryByName(string name)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(name)) return "0";

        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                return MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name);
        }
        return "0";
    }

    private string EvalWobjectFindInInventoryByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "0";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "0";

        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            if (re.IsMatch(wo.Name))
                return MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name);
        }
        return "0";
    }

    private string EvalWobjectFindAllInventoryByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "[]";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            if (re.IsMatch(wo.Name))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));
        }
        return NewList(items);
    }

    private string EvalWobjectFindInInventoryByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "0";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "0";

        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                return MakeWobjectHandle(uid, wo.Name);
        }
        return "0";
    }

    private string EvalWobjectFindAllInventoryByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "[]";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                items.Add(MakeWobjectHandle(uid, wo.Name));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllInventoryByObjectClass(string arg)
    {
        if (_worldObjectCache == null) return "[]";
        if (!int.TryParse(arg.Trim(), out int targetClass)) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            if (ResolveEffectiveObjectClass(wo) == targetClass)
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllByObjectClass(string arg)
    {
        if (_worldObjectCache == null) return "[]";
        if (!int.TryParse(arg.Trim(), out int targetClass)) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(WorldObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            if (ResolveEffectiveObjectClass(wo) != targetClass) return;
            items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects()) Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape()) Check(wo);

        return NewList(items);
    }

    private string EvalWobjectFindAllByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "[]";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(WorldObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            if (re.IsMatch(wo.Name ?? string.Empty))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects()) Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape()) Check(wo);
        foreach (var wo in _worldObjectCache.GetInventory()) Check(wo);

        return NewList(items);
    }

    private string EvalWobjectFindAllByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "[]";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(WorldObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                items.Add(MakeWobjectHandle(uid, wo.Name));
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects()) Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape()) Check(wo);
        foreach (var wo in _worldObjectCache.GetInventory()) Check(wo);

        return NewList(items);
    }

    private string EvalWobjectFindAllLandscape()
    {
        if (_worldObjectCache == null) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));

        foreach (var wo in _worldObjectCache.GetLandscape())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));

        return NewList(items);
    }

    private string EvalWobjectFindAllInventory()
    {
        if (_worldObjectCache == null) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetInventory())
            items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));

        return NewList(items);
    }

    private string EvalWobjectFindAll()
    {
        if (_worldObjectCache == null) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));

        foreach (var wo in _worldObjectCache.GetLandscape())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));

        foreach (var wo in _worldObjectCache.GetInventory())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));

        return NewList(items);
    }

    private string EvalWobjectFindAllLandscapeByObjectClass(string arg)
    {
        if (_worldObjectCache == null) return "[]";
        if (!int.TryParse(arg.Trim(), out int targetClass)) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();
        // Never the character running the meta: "every player around" (class 24) sending
        // tells reached the sender too (2026-10-13).
        int self = unchecked((int)_host.GetPlayerId());
        void Check(WorldObject wo)
        {
            if (wo.Id == self || !seen.Add(wo.Id)) return;
            if (ResolveEffectiveObjectClass(wo) == targetClass)
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));
        }
        foreach (var wo in _worldObjectCache.GetLandscapeObjects()) Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape()) Check(wo);
        return NewList(items);
    }

    private string EvalWobjectFindAllLandscapeByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "[]";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                items.Add(MakeWobjectHandle(uid, wo.Name));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllLandscapeByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "[]";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            if (re.IsMatch(wo.Name))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllByContainer(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetContainerContents) return "[]";
        if (!TryParseWobjectHandle(arg, out uint containerId, out _) || containerId == 0) return "[]";

        uint[] buf = new uint[512];
        int count = _host.GetContainerContents(containerId, buf);

        var items = new List<string>();
        for (int i = 0; i < count; i++)
        {
            uint itemId = buf[i];
            string name = _worldObjectCache[unchecked((int)itemId)]?.Name ?? "";
            if (string.IsNullOrEmpty(name))
                _host.TryGetObjectName(itemId, out name);
            items.Add(MakeWobjectHandle(itemId, name ?? ""));
        }
        return NewList(items);
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
        if (wo == null) return "0";
        return ResolveEffectiveObjectClass(wo).ToString(CultureInfo.InvariantCulture);
    }

    // PublicWeenieDesc._bitfield flags for ObjectClass resolution
    private const uint BF_PLAYER    = 0x8;
    private const uint BF_ATTACKABLE= 0x10;
    private const uint BF_VENDOR    = 0x200;
    private const uint BF_DOOR      = 0x1000;
    private const uint BF_CORPSE    = 0x2000;
    private const uint BF_LIFESTONE = 0x4000;
    private const uint BF_FOOD      = 0x8000;
    private const uint BF_HEALER    = 0x10000;
    private const uint BF_LOCKPICK  = 0x20000;
    private const uint BF_PORTAL    = 0x40000;

    /// <summary>
    /// Resolves the VTank-compatible ObjectClass for a world object at runtime.
    /// Uses PublicWeenieDesc._bitfield flags for accurate classification.
    /// </summary>
    private int ResolveEffectiveObjectClass(WorldObject wo)
    {
        int cls = (int)wo.ObjectClass;
        uint uid = unchecked((uint)wo.Id);

        if (_host.HasGetObjectBitfield && _host.TryGetObjectBitfield(uid, out uint bf))
        {
            if ((bf & BF_PLAYER) != 0)    return (int)AcObjectClass.Player;
            if ((bf & BF_VENDOR) != 0)    return (int)AcObjectClass.Vendor;
            if (cls == (int)AcObjectClass.Monster)
                return (bf & BF_ATTACKABLE) != 0 ? (int)AcObjectClass.Monster : (int)AcObjectClass.Npc;
            if ((bf & BF_DOOR) != 0)      return (int)AcObjectClass.Door;
            if ((bf & BF_CORPSE) != 0)    return (int)AcObjectClass.Corpse;
            if ((bf & BF_PORTAL) != 0)    return (int)AcObjectClass.Portal;
            if ((bf & BF_LIFESTONE) != 0) return (int)AcObjectClass.Lifestone;
            if ((bf & BF_FOOD) != 0)      return (int)AcObjectClass.Food;
            if ((bf & BF_HEALER) != 0)    return (int)AcObjectClass.HealingKit;
            if ((bf & BF_LOCKPICK) != 0)  return (int)AcObjectClass.Lockpick;
        }
        else if (cls == (int)AcObjectClass.Monster && _host.HasObjectIsAttackable)
        {
            return _host.ObjectIsAttackable(uid) ? (int)AcObjectClass.Monster : (int)AcObjectClass.Npc;
        }

        return cls;
    }

    private string EvalWobjectFindNearestDoor()
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";

        int bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        bool hasBitfield = _host.HasGetObjectBitfield;

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);

            // Doors have static GUIDs (< 0x80000000). Dynamic objects are creatures,
            // NPCs, pack items — none of them are doors.
            if (uid >= 0x80000000u) continue;

            // Primary: check PublicWeenieDesc._bitfield for BF_DOOR (0x1000).
            // Fallback: name-based detection for older engine builds without GetObjectBitfield.
            if (hasBitfield)
            {
                if (!_host.TryGetObjectBitfield(uid, out uint bf) || (bf & 0x1000u) == 0)
                    continue;
            }
            else
            {
                string nl = (wo.Name ?? string.Empty).ToLowerInvariant();
                if (!nl.Contains("door") && !nl.Contains("gate") && !nl.Contains("hatch") && !nl.Contains("portcullis"))
                    continue;
            }

            double dist = _worldObjectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = wo.Id;
                bestName = wo.Name ?? string.Empty;
            }
        }

        if (bestId == 0) return "0";
        return MakeWobjectHandle(unchecked((uint)bestId), bestName);
    }

    private string EvalWobjectFindNearestMonster()
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";

        uint bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        bool hasAttackCheck = _host.HasObjectIsAttackable;

        foreach (var wo in _worldObjectCache.GetLandscape())
        {
            uint uid = unchecked((uint)wo.Id);
            if (uid == _playerId) continue;

            // Only monsters — skip NPCs, vendors, pets
            if (hasAttackCheck && !_host.ObjectIsAttackable(uid)) continue;

            double dist = _worldObjectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = uid;
                bestName = wo.Name ?? string.Empty;
            }
        }

        if (bestId == 0) return "0";
        return MakeWobjectHandle(bestId, bestName);
    }

    private string EvalWobjectFindById(string arg)
    {
        if (_worldObjectCache == null) return "0";
        arg = arg.Trim();
        uint uid;
        if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(arg.AsSpan(2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uid))
                return "0";
        }
        else if (!uint.TryParse(arg, out uid))
        {
            return "0";
        }
        if (uid == 0) return "0";

        var wo = _worldObjectCache[unchecked((int)uid)];
        if (wo == null) return "0";

        string name = wo.Name ?? string.Empty;
        if (name.Length == 0)
            _host.TryGetObjectName(uid, out name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalWobjectFindNearestByObjectClass(string arg)
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";
        if (!int.TryParse(arg.Trim(), out int targetClass)) return "0";

        uint bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        int pid = unchecked((int)_playerId);
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(WorldObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            uint uid = unchecked((uint)wo.Id);
            if (uid == _playerId) return;
            if (ResolveEffectiveObjectClass(wo) != targetClass) return;

            double dist = _worldObjectCache.Distance(pid, wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = uid;
                bestName = wo.Name ?? string.Empty;
            }
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape())
            Check(wo);

        if (bestId == 0) return "0";
        return MakeWobjectHandle(bestId, bestName);
    }

    private string EvalWobjectFindNearestByNameAndObjectClass(string classArg, string pattern)
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";
        if (!int.TryParse(classArg.Trim(), out int targetClass)) return "0";
        if (string.IsNullOrEmpty(pattern)) return "0";

        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "0";

        uint bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        int pid = unchecked((int)_playerId);
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(WorldObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            uint uid = unchecked((uint)wo.Id);
            if (uid == _playerId) return;
            if (ResolveEffectiveObjectClass(wo) != targetClass) return;
            if (!re.IsMatch(wo.Name ?? string.Empty)) return;

            double dist = _worldObjectCache.Distance(pid, wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = uid;
                bestName = wo.Name ?? string.Empty;
            }
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape())
            Check(wo);

        if (bestId == 0) return "0";
        return MakeWobjectHandle(bestId, bestName);
    }

    private string EvalWobjectGetIsDoorOpen(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "0";
        // Read CPhysicsObj::m_state directly — no m_pQualities needed.
        // ETHEREAL_PS (0x4) is set when a door is open (passable); clear when closed.
        if (!_host.HasGetObjectState) return "0";
        if (!_host.TryGetObjectState(uid, out uint physState)) return "0";
        return (physState & 0x4u) != 0 ? "1" : "0";
    }

    /// <summary>Returns the raw CPhysicsObj::m_state bitfield as a decimal string for debugging.
    /// Use wobjectgetphysicsstate[wobjectfindnearestdoor[]] to observe state change on open/close.</summary>
    private string EvalWobjectGetPhysicsState(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "noobj";
        if (!_host.HasGetObjectState) return "nofn";
        return _host.TryGetObjectState(uid, out uint s) ? s.ToString() : "nostate";
    }

    private string EvalWobjectGetSelection()
    {
        if (!_host.HasGetSelectedItemId) return "0";
        uint uid = _host.GetSelectedItemId();
        if (uid == 0) return "0";
        _host.TryGetObjectName(uid, out string name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalWobjectGetPlayer()
    {
        uint uid = _host.GetPlayerId();
        if (uid == 0) return "0";
        _host.TryGetObjectName(uid, out string name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalGetFreeContainerSlots(string? arg)
    {
        // Resolve the container UID: no arg → player backpack
        uint uid;
        if (string.IsNullOrEmpty(arg))
        {
            uid = _playerId;
            if (uid == 0) return "-1";
        }
        else
        {
            if (!TryParseWobjectHandle(arg, out uid, out _) || uid == 0)
                return "-1";
        }

        // STypeInt 7 = ContainersCapacity (max sub-containers)
        if (!_host.HasGetObjectIntProperty) return "-1";
        if (!_host.TryGetObjectIntProperty(uid, (uint)LongValueKey.ContainersCapacity, out int capacity) || capacity <= 0)
            return "-1";

        // Use the AC client's own GetNumContainedContainers() — authoritative count of
        // packs + foci occupying container slots (same source as Decal's ContainersContained).
        if (!_host.HasGetNumContainedContainers) return "-1";
        int containerCount = _host.GetNumContainedContainers(uid);
        if (containerCount < 0) return "-1";

        int free = capacity - containerCount;
        return free < 0 ? "0" : free.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalGetContainerItemCount(string? arg)
    {
        uint uid;
        if (string.IsNullOrEmpty(arg))
        {
            uid = _playerId;
            if (uid == 0) return "-1";
        }
        else
        {
            if (!TryParseWobjectHandle(arg, out uid, out _) || uid == 0)
                return "-1";
        }

        if (!_host.HasGetNumContainedItems) return "-1";
        int count = _host.GetNumContainedItems(uid);
        return count < 0 ? "-1" : count.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalWobjectGetOpenContainer()
    {
        if (!_host.HasGetGroundContainerId) return "0";
        uint uid = _host.GetGroundContainerId();
        if (uid == 0) return "0";
        _host.TryGetObjectName(uid, out string name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalGetFreeItemSlots(string? arg)
    {
        // Resolve the container UID: no arg → player backpack
        uint uid;
        if (string.IsNullOrEmpty(arg))
        {
            uid = _playerId;
            if (uid == 0) return "-1";
        }
        else
        {
            if (!TryParseWobjectHandle(arg, out uid, out _) || uid == 0)
                return "-1";
        }

        // STypeInt 6 = ItemsCapacity (max item slots)
        if (!_host.HasGetObjectIntProperty) return "-1";
        if (!_host.TryGetObjectIntProperty(uid, (uint)LongValueKey.ItemsCapacity, out int capacity) || capacity <= 0)
            return "-1";

        // Current item count from GetContainerContents
        if (!_host.HasGetContainerContents) return "-1";
        var buf = new uint[capacity + 1];
        int count = _host.GetContainerContents(uid, buf);
        if (count < 0) return "-1";

        int free = capacity - count;
        return free < 0 ? "0" : free.ToString(CultureInfo.InvariantCulture);
    }

    // Last automatic ID request per object (TickCount64), see EnsureIdentified.
    private readonly Dictionary<uint, long> _autoIdAt = new();

    /// <summary>
    /// Asks the server to identify an object a meta or script reads properties
    /// from, if it hasn't been identified this session. Many properties (uses
    /// left on a pet essence, workmanship, spells ...) only arrive with an ID:
    /// before this, a meta reading them got 0 until the item was identified by
    /// hand, e.g. endlessly "refilling" a full essence. At most once per 10 s
    /// per object; the value is right on the next read once the ID lands.
    /// </summary>
    private void EnsureIdentified(uint uid)
    {
        if (uid == 0 || !_host.HasHasAppraisalData || !_host.HasRequestId) return;
        // Used (or used on) since its last identify: the identified values may be
        // stale, e.g. an essence's uses after a summon or refill. Re-identify.
        bool stale = ObjectChangeTracker.IsStale(uid, _host.HasGetLastIdTime ? _host.GetLastIdTime(uid) : 0);
        if (!stale && _host.HasAppraisalData(uid)) return;
        long now = Environment.TickCount64;
        long gap = stale ? 2_000 : 10_000;
        lock (_autoIdAt)
        {
            if (_autoIdAt.TryGetValue(uid, out long last) && now - last < gap) return;
            if (_autoIdAt.Count > 500) _autoIdAt.Clear();
            _autoIdAt[uid] = now;
        }
        _host.RequestId(uid);
    }

    private string EvalWobjectGetIntProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "0";
        EnsureIdentified(uid);

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
        EnsureIdentified(uid);

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
        EnsureIdentified(uid);
        if (!_host.HasGetObjectBoolProperty) return "0";
        return _host.TryGetObjectBoolProperty(uid, prop, out bool value) ? (value ? "1" : "0") : "0";
    }

    private string EvalWobjectGetStringProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "";
        EnsureIdentified(uid);
        if (!_host.HasGetObjectStringProperty) return "";
        return _host.TryGetObjectStringProperty(uid, prop, out string? value) ? value ?? "" : "";
    }

    private string EvalWobjectHasData(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasHasAppraisalData) return "0";
        return _host.HasAppraisalData(uid) ? "1" : "0";
    }

    private string EvalWobjectRequestData(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasRequestId) return "0";
        _host.RequestId(uid);
        return "1";
    }

    private string EvalWobjectLastIdTime(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasGetLastIdTime) return "0";
        return _host.GetLastIdTime(uid).ToString(CultureInfo.InvariantCulture);
    }

    private string EvalWobjectIsValid(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (uid == 0) return "0";
        if (!_host.HasGetObjectName) return "0";
        return _host.TryGetObjectName(uid, out _) ? "1" : "0";
    }

    private string EvalWobjectGetHealth(string objArg)
    {
        if (_worldObjectCache == null) return "-1";
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "-1";
        if (uid == 0) return "-1";
        float ratio = _worldObjectCache.GetHealthRatio((int)uid);
        return ratio.ToString("G", CultureInfo.InvariantCulture);
    }

    // ── Creature profile lookups (persistent store) ───────────────────────────
    // Resolves a creature handle or raw name to a CreatureProfile.
    private CreatureProfile? ResolveCreatureProfile(string arg)
    {
        if (_creatureStore == null) return null;
        string? name = null;
        if (TryParseWobjectHandle(arg, out uint uid, out _) && uid != 0
            && _host.HasGetObjectName && _host.TryGetObjectName(uid, out string n))
        {
            name = n;
        }
        if (string.IsNullOrEmpty(name)) name = arg;
        if (string.IsNullOrEmpty(name)) return null;
        return _creatureStore.TryGetByName(name, out var p) ? p : null;
    }

    private string EvalCreatureGetMaxHealth(string arg)
    {
        var p = ResolveCreatureProfile(arg);
        return (p != null && p.MaxHealth > 0)
            ? p.MaxHealth.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    // Resists come from CreatureWeakness (the shipped server table, else the creature
    // type's average): ACE never sends them on appraisal, so the profile's were all 1.0
    // and creaturegetweakest always said "slash" (2026-09-29). Values are damage taken.
    private CreatureWeakness.Ranking? ResolveWeakness(string arg)
    {
        uint wcid = 0;
        string? name = null;
        if (TryParseWobjectHandle(arg, out uint uid, out _) && uid != 0)
        {
            if (_host.HasGetObjectName && _host.TryGetObjectName(uid, out string n)) name = n;
            if (_host.HasGetObjectWcid) _host.TryGetObjectWcid(uid, out wcid);
        }
        if (string.IsNullOrEmpty(name)) name = arg;
        int ctype = ResolveCreatureProfile(arg)?.CreatureType ?? 0;
        return CreatureWeakness.Rank(wcid, name, ctype, null);
    }

    private string EvalCreatureGetResist(string arg, string typeArg)
    {
        string t = (typeArg ?? "").Trim().ToLowerInvariant();
        string element = t switch
        {
            "slash" or "sl"           => "Slash",
            "pierce" or "pi"          => "Pierce",
            "bludgeon" or "bl" or "b" => "Bludgeon",
            "fire" or "fi" or "f"     => "Fire",
            "cold" or "co" or "c"     => "Cold",
            "acid" or "ac" or "a"     => "Acid",
            "electric" or "lightning" or "el" or "li" => "Lightning",
            "nether" or "ne" or "n"   => "Nether",
            _ => "",
        };
        var r = ResolveWeakness(arg);
        if (r != null && element.Length > 0)
            foreach (var (e, m) in r.Order)
                if (e == element && !double.IsNaN(m)) return m.ToString("G", CultureInfo.InvariantCulture);
        return "1";
    }

    /// <summary>The element the creature takes most damage from, spelled as before ("electric" for lightning); "" when unknown.</summary>
    private string EvalCreatureGetWeakest(string arg)
    {
        var r = ResolveWeakness(arg);
        if (r == null || r.Order.Count == 0) return "";
        string e = r.Order[0].Element.ToLowerInvariant();
        return e == "lightning" ? "electric" : e;
    }

    private string EvalCreatureGetArmor(string arg)
    {
        var p = ResolveCreatureProfile(arg);
        return (p != null && p.ArmorLevel > 0)
            ? p.ArmorLevel.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private string EvalCreatureHasProfile(string arg)
    {
        return ResolveCreatureProfile(arg) != null ? "1" : "0";
    }

    private string EvalWobjectGetSpellIds(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "[]";
        if (!_host.HasGetObjectSpellIds) return "[]";
        var buf = new uint[512];
        int count = _host.GetObjectSpellIds(uid, buf, buf.Length);
        if (count <= 0) return "[]";
        var strs = new List<string>(count);
        int fill = Math.Min(count, buf.Length);
        for (int i = 0; i < fill; i++)
            strs.Add(buf[i].ToString(CultureInfo.InvariantCulture));
        return NewList(strs);
    }

    private string EvalWobjectGetActiveSpellIds(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "[]";

        // Primary path: enchantment registry (works for player and creatures).
        if (_host.HasReadObjectEnchantments)
        {
            var spellIds = new uint[128];
            var expiry = new double[128];
            int count = _host.ReadObjectEnchantments(uid, spellIds, expiry, spellIds.Length);
            if (count > 0)
            {
                var strs = new List<string>(count);
                for (int i = 0; i < count; i++)
                    strs.Add(spellIds[i].ToString(CultureInfo.InvariantCulture));
                return NewList(strs);
            }
        }

        // Fallback for items: use appraisal spell book entries with the high bit set.
        // The AC client uses bit 31 to distinguish active (player-cast) enchantments from
        // native item spells — same logic used by ItemExamineUI to render the "Enchantments:"
        // section. Strip the high bit to recover the actual spell ID.
        if (_host.HasGetObjectSpellIds)
        {
            var buf = new uint[512];
            int total = _host.GetObjectSpellIds(uid, buf, buf.Length);
            if (total > 0)
            {
                var strs = new List<string>();
                int fill = Math.Min(total, buf.Length);
                for (int i = 0; i < fill; i++)
                {
                    if ((buf[i] & 0x80000000u) != 0)
                        strs.Add((buf[i] & 0x7FFFFFFFu).ToString(CultureInfo.InvariantCulture));
                }
                if (strs.Count > 0)
                    return NewList(strs);
            }
        }

        return "[]";
    }

    private string EvalWobjectGetActiveSpellDurations(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "[]";

        // Primary path: enchantment registry (works for player and creatures).
        if (_host.HasReadObjectEnchantments)
        {
            var spellIds = new uint[128];
            var expiry = new double[128];
            int count = _host.ReadObjectEnchantments(uid, spellIds, expiry, spellIds.Length);
            if (count > 0)
            {
                double serverTime = _host.GetServerTime();
                var strs = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    double remaining = expiry[i] > 0 && serverTime > 0 ? expiry[i] - serverTime : -1;
                    strs.Add(remaining.ToString("G", CultureInfo.InvariantCulture));
                }
                return NewList(strs);
            }
        }

        // Fallback for items: the AC server never sends expiry times for item enchantments
        // to the client (no 0x2C2 packet is issued for item buffs — only for body buffs).
        // Return -1 per active spell so the list length matches wobjectgetactivespellids[]
        // and scripts can still count/detect spells by ID.
        if (_host.HasGetObjectSpellIds)
        {
            var buf = new uint[512];
            int total = _host.GetObjectSpellIds(uid, buf, buf.Length);
            if (total > 0)
            {
                var strs = new List<string>();
                int fill = Math.Min(total, buf.Length);
                for (int i = 0; i < fill; i++)
                {
                    if ((buf[i] & 0x80000000u) != 0)
                        strs.Add("-1");
                }
                if (strs.Count > 0)
                    return NewList(strs);
            }
        }

        return "[]";
    }

    private string EvalItemCountByName(string name)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(name)) return "0";
        int total = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
            if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                total += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalItemCountByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "0";
        var rx = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (rx == null) return "0";
        int total = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
            if (rx.IsMatch(wo.Name))
                total += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalGetInventoryCountByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "0";
        if (!uint.TryParse(arg, out uint targetWcid) || targetWcid == 0) return "0";
        int total = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            uint uid = (uint)wo.Id;
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                total += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        }
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalActionTrySelect(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out _) || uid == 0) return "0";
        if (!_host.HasSelectItem) return "0";
        RynthCore.Plugin.RynthAi.ItemInfo.SelfSelect.Mark(uid);
        return _host.SelectItem(uid) ? "1" : "0";
    }

    private string EvalActionTryUseItem(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out _) || uid == 0) return "0";
        if (!_host.HasUseObject) return "0";
        _host.UseFor(uid, "Meta", "actiontryuseitem", UseKind.Asked);
        return "0";
    }

    private string EvalActionTryApplyItem(string useArg, string onArg)
    {
        if (!TryParseWobjectHandle(useArg, out uint useId, out _) || useId == 0) return "0";
        if (!TryParseWobjectHandle(onArg, out uint onId, out _) || onId == 0) return "0";
        if (!_host.HasUseObjectOn) return "0";
        return _host.UseOnFor(useId, onId, "Meta", "actiontryapplyitem", UseKind.Asked) ? "1" : "0";
    }

    private string EvalActionTryCastByIdOnTarget(string spellArg, string targetArg)
    {
        if (!int.TryParse(spellArg.Trim(), out int spellId) || spellId == 0) return "2";
        if (!_host.HasCastSpell) return "2";
        if (!TryParseWobjectHandle(targetArg, out uint targetId, out _) || targetId == 0) return "2";
        if (SpellKnownToBeUnknown(spellId)) return "2";

        if (!EnsureMagicModeForCast()) return "0";

        _host.CastSpellFor(targetId, spellId, "Meta", $"spell {spellId} (meta)", asked: true);
        return "1";
    }

    // ── Meta-cast magic-mode swap state (mirrors BuffManager.EnsureMagicMode) ──
    // FIX (2026-06-24, bow->wand meta-cast swap): stock ACE refuses to wield a
    // Held-slot wand while a melee/missile weapon is in the main hand and does NOT
    // auto-dequip it (CheckWeaponCollision, Player_Inventory.cs:1991). The OLD
    // bare UseObject(wand) below therefore never confirmed, so a Meta-driven
    // self-buff/cast (actiontrycast / actiontrycastbyid) deadlocked the bot
    // forever — stuck re-issuing the equip, never reaching Magic, blocking
    // everything (the exact BuffManager coma). We must stow the wielded weapon
    // into a capacity-verified open pack FIRST (AutoCram pattern, AV-safe), then
    // wield the wand. Every path is bounded so a stale dequip, a full inventory,
    // or an unwieldable wand degrades and yields instead of looping.
    private int _castWieldPendingId;
    private DateTime _castWieldPendingAt = DateTime.MinValue;
    private DateTime _castWieldCooldownUntil = DateTime.MinValue;
    private int _castWieldFailCount;
    private int _castBowDequipPendingId;
    private DateTime _castBowDequipAt = DateTime.MinValue;
    private int _castBowDequipAttempts;
    private bool _castCombatTornDown;
    private DateTime _castStanceStuckSince = DateTime.MinValue;
    private DateTime _castStanceLastRecoverAt = DateTime.MinValue;
    private DateTime _castStanceLastFlipAt = DateTime.MinValue;
    private int _castStanceReEquips;
    private const double CastWieldResolveTimeoutMs = 2500;
    private const double CastWieldCooldownMs = 5000;
    private const int CastWieldFailMax = 3;
    private const int CastBowDequipMaxAttempts = 3;
    private const int CastStanceReEquipMax = 2;
    private const double CastStanceStuckRecoverMs = 8000;
    private const double CastStanceFlipGateMs = 1200;

    /// <summary>
    /// Ensures the character is in magic mode. Returns true if already in magic mode.
    /// Otherwise takes one bounded step (dequip blocking weapon, equip wand, or change
    /// stance) and returns false. Mirrors BuffManager.EnsureMagicMode so a Meta cast
    /// can never deadlock on the stock-ACE bow→wand collision.
    /// </summary>
    private bool EnsureMagicModeForCast()
    {
        if (!_host.HasGetCurrentCombatMode)
        {
            // Can't read mode — best-effort flip and bail.
            if (_host.HasChangeCombatMode) _host.ChangeCombatModeFor(CombatMode.Magic, "Meta", "meta cast: Magic mode", force: true);
            return false;
        }

        if (_host.GetCurrentCombatMode() == CombatMode.Magic)
        {
            // Reached Magic — clear the whole swap state machine so a later
            // recoverable episode (e.g. after a slot frees from looting) starts clean.
            _castWieldPendingId = 0;
            _castWieldCooldownUntil = DateTime.MinValue;
            _castWieldFailCount = 0;
            _castBowDequipPendingId = 0;
            _castBowDequipAttempts = 0;
            _castCombatTornDown = false;
            _castStanceStuckSince = DateTime.MinValue;
            _castStanceReEquips = 0;
            return true;
        }

        int wandId = FindCastWand();
        if (wandId == 0 || !_host.HasUseObject)
        {
            // No wand (or no UseObject) — try bare-handed magic.
            if (_host.HasChangeCombatMode) _host.ChangeCombatModeFor(CombatMode.Magic, "Meta", "meta cast: Magic mode", force: true);
            return false;
        }

        DateTime now = RynthClock.Now;

        if (!IsCastWielded(wandId))
        {
            // Wield gate: don't spam UseObject while a prior equip is in flight.
            if (now < _castWieldCooldownUntil) return false;

            if (_castWieldPendingId != 0)
            {
                if ((now - _castWieldPendingAt).TotalMilliseconds < CastWieldResolveTimeoutMs)
                    return false;
                _host.Log($"[MetaCast] wand 0x{(uint)_castWieldPendingId:X8} wield not confirmed in " +
                          $"{CastWieldResolveTimeoutMs:0}ms — cooling down {CastWieldCooldownMs:0}ms");
                _castWieldPendingId = 0;
                _castWieldCooldownUntil = now.AddMilliseconds(CastWieldCooldownMs);
                // Bound the previously-infinite wield loop: after a few cooldowns the
                // swap provably can't complete now (no pack to free the bow, or an
                // unresolvable collision) — degrade so the meta retries later, not forever.
                if (++_castWieldFailCount >= CastWieldFailMax)
                    return CastSwapDegrade(wandId, FindCastWieldedNonWand(wandId));
                return false;
            }

            // Tear down any in-flight physical attack BEFORE the UseObject (once per
            // episode), or AC gets an equip while m_bAttacking is set and wedges the
            // item-action gate ("you can only move or use one item at a time").
            int mode = _host.GetCurrentCombatMode();
            if (!_castCombatTornDown && (mode == CombatMode.Melee || mode == CombatMode.Missile))
            {
                if (_host.HasCancelAttack)   _host.CancelAttack();
                if (_host.HasStopCompletely) _host.StopCompletelyBy("Meta");
                _castCombatTornDown = true;
                _host.Log($"[MetaCast] CancelAttack+StopCompletely before wand equip (mode was {mode})");
            }

            // Stock ACE won't auto-dequip the bow for a Held-slot wand: stow the wielded
            // non-wand weapon into a capacity-verified open pack FIRST, then wield the wand.
            int bowId = FindCastWieldedNonWand(wandId);
            if (bowId != 0 && _host.HasMoveItemInternal)
            {
                if (_castBowDequipPendingId == bowId
                    && (now - _castBowDequipAt).TotalMilliseconds < CastWieldResolveTimeoutMs
                    && IsCastWielded(bowId))
                    return false; // dequip still resolving

                if (IsCastWielded(bowId))
                {
                    int openPack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: 1);
                    if (openPack == 0 || _castBowDequipAttempts >= CastBowDequipMaxAttempts)
                    {
                        _host.Log($"[MetaCast] bow 0x{(uint)bowId:X8} dequip blocked (openPack=0x{(uint)openPack:X8}, " +
                                  $"attempts={_castBowDequipAttempts}/{CastBowDequipMaxAttempts}) — degrading");
                        return CastSwapDegrade(wandId, bowId);
                    }
                    _castBowDequipAttempts++;
                    _host.MoveItemInternal((uint)bowId, (uint)openPack, 0, 1); // amount>=1 (engine rejects 0)
                    _castBowDequipPendingId = bowId;
                    _castBowDequipAt = now;
                    _host.Log($"[MetaCast] dequip bow 0x{(uint)bowId:X8} -> pack 0x{(uint)openPack:X8} " +
                              $"(attempt {_castBowDequipAttempts}/{CastBowDequipMaxAttempts}) before wand equip");
                    return false; // yield until the bow is out of hand
                }
                _castBowDequipPendingId = 0; // bow confirmed unwielded — fall through to wield the wand
            }

            _host.UseFor((uint)wandId, "Meta", "meta cast: wield the wand", UseKind.Asked);
            _castWieldPendingId = wandId;
            _castWieldPendingAt = now;
            return false; // yield — let the server equip the wand
        }

        // Wand wielded — clear the wield gate and flip stance with bounded recovery.
        if (_castWieldPendingId == wandId)
        {
            _castWieldPendingId = 0;
            _castWieldCooldownUntil = DateTime.MinValue;
        }

        if (_castStanceStuckSince == DateTime.MinValue)
            _castStanceStuckSince = now;
        double stuckMs = (now - _castStanceStuckSince).TotalMilliseconds;

        // Capped re-equip recovery (mirrors CombatManager/BuffManager): resyncs a rare
        // stale "wielded reads true". HARD-CAPPED — UseObject on a genuinely wielded wand
        // is a MOVE to AC, and unbounded re-equip jams the item-action queue permanently.
        if (_castStanceReEquips < CastStanceReEquipMax
            && stuckMs > CastStanceStuckRecoverMs
            && (now - _castStanceLastRecoverAt).TotalMilliseconds > CastStanceStuckRecoverMs)
        {
            _castStanceLastRecoverAt = now;
            _castStanceReEquips++;
            _host.Log($"[MetaCast] stance STUCK {stuckMs:0}ms (wielded, mode≠Magic) — re-equip {_castStanceReEquips}/{CastStanceReEquipMax} 0x{(uint)wandId:X8}");
            _host.UseFor((uint)wandId, "Meta", "meta cast: stance stuck, wield the wand again", UseKind.Asked);
            return false;
        }

        // Throttle the mode flip so we don't issue ChangeCombatMode every pulse.
        if (_host.HasChangeCombatMode
            && (now - _castStanceLastFlipAt).TotalMilliseconds > CastStanceFlipGateMs)
        {
            // Only a request that went out arms the flip throttle: a refusal is asked again next pulse.
            if (_host.ChangeCombatModeFor(CombatMode.Magic, "Meta", "meta cast: Magic mode", force: true))
                _castStanceLastFlipAt = now;
        }
        return false;
    }

    // Bounded degrade for the meta-cast swap. If the wand ended up wielded but mode never
    // flipped, dequip it so the bow can re-wield; otherwise re-wield the bow we stowed so
    // the character is never left bare-handed. Reset the state machine and yield — the meta
    // retries on a later pulse once inventory/stance frees up (no buff family to park here).
    private bool CastSwapDegrade(int wandId, int bowId)
    {
        if (wandId != 0 && _host.HasMoveItemInternal && IsCastWielded(wandId))
        {
            int openPack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: 1);
            if (openPack != 0) _host.MoveItemInternal((uint)wandId, (uint)openPack, 0, 1);
        }
        else if (bowId != 0 && !IsCastWielded(bowId) && _host.HasUseObject)
        {
            _host.UseFor((uint)bowId, "Meta", "meta cast done: wield the bow again", UseKind.Asked); // re-wield the bow we dequipped
        }
        _castWieldPendingId = 0;
        _castBowDequipPendingId = 0;
        _castBowDequipAttempts = 0;
        _castWieldFailCount = 0;
        _castCombatTornDown = false;
        return false;
    }

    // First wand in direct inventory. Robust IsWandObject (ObjectClass OR name) per the
    // wand-classification pitfall: an Unknown-class wand still needs to be found and wielded.
    private int FindCastWand()
    {
        if (_worldObjectCache == null) return 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
            if (IsCastWandObject(wo)) return wo.Id;
        return 0;
    }

    // Live (forceRefresh) wielded check — never trusts a stale cache snapshot (gates an
    // AV-risky move). Mirrors BuffManager.IsWieldedLive.
    private bool IsCastWielded(int id)
    {
        if (id == 0 || _worldObjectCache == null) return false;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
            if (wo.Id == id)
                return WorldObjectCache.IsWieldedByPlayer(_host, wo);
        if (_host.HasGetObjectWielderInfo)
        {
            uint pid = _host.GetPlayerId();
            if (EquippedState.IsWielded(_host, id) is bool tracked) return tracked;
            if (pid != 0 && _host.TryGetObjectWielderInfo((uint)id, out uint wielder, out _))
                return wielder == pid && _worldObjectCache?.InPackWalk(id) != true;
        }
        return false;
    }

    // The currently-wielded non-wand weapon (the bow) that blocks the Held-slot wand.
    private int FindCastWieldedNonWand(int wandId)
    {
        if (_worldObjectCache == null) return 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            if (wo.Id == wandId) continue;
            if (IsCastWandObject(wo)) continue;
            if (WorldObjectCache.IsAmmo(wo)) continue;   // arrows stay on; only the bow blocks the wand
            if ((wo.ObjectClass == AcObjectClass.MeleeWeapon
                 || wo.ObjectClass == AcObjectClass.MissileWeapon)
                && WorldObjectCache.IsWieldedByPlayer(_host, wo))
                return wo.Id;
        }
        return 0;
    }

    // FindCastOpenPack DELETED (P2a) — replaced by shared
    // WorldObjectCache.FindPackFor(host, cache, includeMainPack:true, requireFree:1).
    // MetaCast wand-swap: requireFree:1 so a single free slot still allows the swap.
    // Call sites: the meta-cast dequip block + CastSwapDegrade.

    private static bool IsCastWandObject(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.WandStaffOrb || IsCastWandName(wo.Name);

    private static bool IsCastWandName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("Orb",      StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Staff",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Wand",     StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Scepter",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Sceptre",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Baton",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Crozier",  StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private string EvalActionTryCastById(string arg)
    {
        if (!int.TryParse(arg.Trim(), out int spellId) || spellId == 0) return "2";
        if (!_host.HasCastSpell) return "2";
        if (SpellKnownToBeUnknown(spellId)) return "2";

        if (!EnsureMagicModeForCast()) return "0";

        uint targetId = (_host.HasGetSelectedItemId ? _host.GetSelectedItemId() : 0);
        if (targetId == 0) targetId = _host.GetPlayerId();
        if (targetId == 0) return "2";

        _host.CastSpellFor(targetId, spellId, "Meta", $"spell {spellId} (meta)", asked: true);
        return "1";
    }

    /// <summary>The character doesn't know the spell (as far as the client can tell): the
    /// reference's "2 if the attempt is impossible". It used to wield a wand, cast anyway and
    /// report 1 (started). Unreadable counts as known, so nothing is refused by mistake.</summary>
    private bool SpellKnownToBeUnknown(int spellId)
        => _playerId != 0 && _host.HasIsSpellKnown
           && _host.IsSpellKnown(_playerId, (uint)spellId, out bool known) && !known;

    /// <summary>getknownspells[]: the ids of the spells in the character's spellbook.</summary>
    private string EvalGetKnownSpells()
    {
        if (!_host.HasReadKnownSpells) return "[]";
        var buf = new uint[8192];
        int count = _host.ReadKnownSpells(buf, buf.Length);
        if (count <= 0) return "[]";
        var ids = new List<string>(Math.Min(count, buf.Length));
        for (int i = 0; i < count && i < buf.Length; i++)
            ids.Add(buf[i].ToString(CultureInfo.InvariantCulture));
        return NewList(ids);
    }

    private string EvalActionTryEquipAnyWand()
    {
        if (_worldObjectCache == null || !_host.HasUseObject) return "0";

        WorldObject? unequipped = null;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            if (wo.ObjectClass != AcObjectClass.WandStaffOrb) continue;
            if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) > 0)
                return "1"; // already wielded
            unequipped ??= wo;
        }

        if (unequipped != null)
            _host.UseFor(unchecked((uint)unequipped.Id), "Meta", "actiontryequipanywand", UseKind.Asked);

        return "0";
    }

    private string EvalActionTryGiveItem(string giveArg, string destArg)
    {
        if (!TryParseWobjectHandle(giveArg, out uint giveId, out _) || giveId == 0) return "0";
        if (!TryParseWobjectHandle(destArg, out uint destId, out _) || destId == 0) return "0";
        if (!_host.HasMoveItemExternal) return "0";
        var wo = _worldObjectCache?[unchecked((int)giveId)];
        int amount = wo != null ? Math.Max(1, wo.Values(LongValueKey.StackCount, 1)) : 1;
        return _host.MoveItemExternal(giveId, destId, amount) ? "1" : "0";
    }

    private string EvalActionTryGiveProfile(string profileArg, string targetNameArg)
    {
        // No busy check — keep retrying the give for the first matching item each pulse.
        // The item disappearing from GetInventory() is the confirmation it was accepted.
        if (_worldObjectCache == null || string.IsNullOrEmpty(profileArg) || string.IsNullOrEmpty(targetNameArg))
            return "0";
        // An NPC ignores MoveItemExternal (move-to-container): only AC's give action reaches one.
        if (!_host.HasGiveObjectTo) return "0";

        // The profile stays inside ItemGiverDir: a shared meta can't point the give at another file.
        string? profilePath = ResolveGiveProfilePath(profileArg, ItemGiverDir);
        if (profilePath == null)
        {
            if (_giveProfileRefused.Add(profileArg))   // once per argument, not every pulse
                _host.Log($"[RynthAi] actiontrygiveprofile: refused profile '{profileArg}' (only files inside ItemGiver\\).");
            return "0";
        }
        bool isJson = profilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        if (!File.Exists(profilePath)) return "0";

        // Target: an NPC or vendor with that exact name (case-insensitive). Never a player, so a
        // shared meta can't hand the inventory to someone's character; /ra ig gives to players.
        uint targetId = 0;
        foreach (var wo in _worldObjectCache.GetLandscape())
        {
            if (string.Equals(wo.Name, targetNameArg, StringComparison.OrdinalIgnoreCase) && IsGiveProfileTarget(wo))
            {
                targetId = (uint)wo.Id;
                break;
            }
        }
        if (targetId == 0) return "0";

        DateTime mtime = File.GetLastWriteTime(profilePath);

        // Each pulse: fire MoveItemExternal for the first matching item still in inventory.
        // We retry the same item until it vanishes from the cache (server confirmed).
        // Once gone, the next iteration naturally picks up the next item.
        // Returns 1 while matching items remain, 0 when inventory is clear.
        if (isJson)
        {
            RynthCore.Loot.LootProfile nativeProfile;
            if (_giveNativeProfileCache.TryGetValue(profilePath, out var nCached) && nCached.Mtime == mtime)
                nativeProfile = nCached.Profile;
            else
            {
                try
                {
                    nativeProfile = RynthCore.Loot.LootProfile.Load(profilePath);
                    _giveNativeProfileCache[profilePath] = (nativeProfile, mtime);
                }
                catch { return "0"; }
            }

            foreach (var wo in _worldObjectCache.GetInventory())
            {
                var (action, _) = RynthCore.Plugin.RynthAi.Loot.LootEvaluator.Classify(nativeProfile, wo, null);
                if (action != RynthCore.Loot.LootAction.Keep) continue;
                _host.GiveObjectTo((uint)wo.Id, targetId, 0);
                return "1";
            }
        }
        else
        {
            VTankLootProfile vtProfile;
            if (_giveProfileCache.TryGetValue(profilePath, out var vtCached) && vtCached.Mtime == mtime)
                vtProfile = vtCached.Profile;
            else
            {
                try
                {
                    vtProfile = VTankLootParser.Load(profilePath);
                    _giveProfileCache[profilePath] = (vtProfile, mtime);
                }
                catch { return "0"; }
            }

            VTankLootContext lootCtx = new(_host, _playerId) { Cache = _worldObjectCache };
            foreach (var wo in _worldObjectCache.GetInventory())
            {
                VTankLootRule? match = null;
                foreach (var rule in vtProfile.Rules)
                    if (VTankLootEvaluator.Match(rule, wo, lootCtx)) { match = rule; break; }

                if (match == null || match.Action != VTankLootAction.Keep) continue;
                _host.GiveObjectTo((uint)wo.Id, targetId, 0);
                return "1";
            }
        }

        return "0"; // No matching items remain
    }

    /// <summary>An NPC or a vendor (weenie bitfield first, as the meta's ObjectClass): a player never is.</summary>
    private bool IsGiveProfileTarget(WorldObject wo)
    {
        int cls = ResolveEffectiveObjectClass(wo);
        return cls == (int)AcObjectClass.Npc || cls == (int)AcObjectClass.Vendor;
    }

    /// <summary>
    /// actiontrygiveprofile's profile file: <paramref name="profileArg"/> relative to
    /// <paramref name="baseDir"/>, ".utl" added unless it ends in .utl or .json. Null when the
    /// argument is rooted (C:\..., \\server\..., \x, C:x), names an alternate stream (':'), or
    /// leaves <paramref name="baseDir"/> ("..\").
    /// </summary>
    internal static string? ResolveGiveProfilePath(string profileArg, string baseDir)
    {
        string arg = profileArg.Trim();
        if (arg.Length == 0 || string.IsNullOrWhiteSpace(baseDir) || arg.IndexOf(':') >= 0 || Path.IsPathRooted(arg))
            return null;

        string root;
        string full;
        try
        {
            root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.Combine(root, arg));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length)
            return null;

        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".utl", StringComparison.OrdinalIgnoreCase))
            full += ".utl";
        return full;
    }

    private string EvalGetCombatState()
    {
        if (!_host.HasGetCurrentCombatMode) return "Peace";
        return _host.GetCurrentCombatMode() switch
        {
            2 => "Melee",
            4 => "Missile",
            8 => "Magic",
            _ => "Peace",
        };
    }

    private string EvalSetCombatState(string stateArg)
    {
        if (!_host.HasChangeCombatMode) return "0";
        int mode = stateArg.Trim().ToLowerInvariant() switch
        {
            "peace"   => 1,
            "melee"   => 2,
            "missile" => 4,
            "magic"   => 8,
            _ => 0,
        };
        if (mode == 0) return "0";
        if (!_host.HasChangeCombatMode) return "0";
        int seq = _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0;
        bool ok = _host.ChangeCombatMode(mode);
        if (ok) ActionPacer.Begin(_host, PacedKind.CombatMode, 0, ActionPacer.ModeName(mode), "RynthAi/Meta (asked)", seq, mode);
        return ok ? "1" : "0";
    }

    private string EvalGetBusyState()
    {
        if (!_host.HasGetBusyState) return "0";
        return _host.GetBusyState().ToString(CultureInfo.InvariantCulture);
    }

    private string EvalSetMotion(string motionArg, string stateArg)
    {
        if (!_host.HasSetMotion) return "0";
        if (!MotionValues.TryGetValue(motionArg, out uint motionVal)) return "0";
        bool on = ToDouble(stateArg) != 0;
        _wantedMotion[motionArg] = on;
        return _host.SetMotionBy("Meta", motionVal, on) ? "1" : "0";
    }

    private string EvalGetMotion(string motionArg)
    {
        if (!MotionValues.ContainsKey(motionArg)) return "0";
        bool wanted = _wantedMotion.TryGetValue(motionArg, out bool w) && w;
        // Returns 0 (inactive+unwanted), 1 (wanted+inactive), matching UB convention.
        // Active-but-unwanted (-1) and wanted+active (2) require client state polling not available.
        return wanted ? "1" : "0";
    }

    private string EvalClearMotion()
    {
        if (_host.HasSetMotion)
        {
            foreach (var kvp in MotionValues)
                _host.SetMotionBy("Meta", kvp.Value, false);
        }
        _wantedMotion.Clear();
        return "1";
    }

    // ── Quest flag expressions ───────────────────────────────────────────────

    private string EvalTestQuestFlag(string key)
        => (_questTracker != null && _questTracker.HasFlag(key)) ? "1" : "0";

    private string EvalGetQuestStatus(string key)
    {
        if (_questTracker == null)
            return "1"; // no data — assume ready (matches UB behaviour)
        if (!_questTracker.TryGetFlag(key, out QuestRecord? rec))
            return "1"; // not in list → ready
        return rec.IsReady() ? "1" : "0";
    }

    private string EvalGetQuestKtProgress(string key)
    {
        if (_questTracker == null || !_questTracker.TryGetFlag(key, out QuestRecord? rec))
            return "0";
        return rec.Solves.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string EvalGetQuestKtRequired(string key)
    {
        if (_questTracker == null || !_questTracker.TryGetFlag(key, out QuestRecord? rec))
            return "0";
        return rec.MaxSolves.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string EvalRefreshQuests()
    {
        _questTracker?.Refresh();
        return "1";
    }

    private string EvalComponentData(string idArg)
    {
        uint id = (uint)ToDouble(idArg);
        if (!ComponentDatabase.TryGetRecord(id, out ComponentDatabase.ComponentRecord? rec) || rec == null)
            return NewDict();

        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"]           = rec.Id.ToString(CultureInfo.InvariantCulture),
            ["Name"]         = rec.Name,
            ["IconId"]       = rec.IconId.ToString(CultureInfo.InvariantCulture),
            ["Type"]         = rec.TypeName,
            ["GestureId"]    = ((int)rec.GestureId).ToString(CultureInfo.InvariantCulture),
            ["GestureSpeed"] = rec.GestureSpeed.ToString("R", CultureInfo.InvariantCulture),
            ["BurnRate"]     = rec.BurnRate.ToString("R", CultureInfo.InvariantCulture),
            ["Word"]         = rec.Word,
            ["SortKey"]      = rec.Category.ToString(CultureInfo.InvariantCulture),
        };
        return NewDict(d);
    }

    private string EvalGetEquippedWeaponType()
    {
        if (_worldObjectCache == null) return "None";
        foreach (var item in _worldObjectCache.GetDirectInventory())
        {
            if (item.WieldedLocation <= 0) continue;
            switch (item.ObjectClass)
            {
                case AcObjectClass.MeleeWeapon:   return "Melee";
                case AcObjectClass.MissileWeapon: return "Missile";
                case AcObjectClass.WandStaffOrb:  return "Wand";
            }
        }
        return "None";
    }

    private string EvalGetHeading(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasGetObjectHeading) return "0";
        if (!_host.TryGetObjectHeading(uid, out float heading)) return "0";
        return heading.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalGetHeadingTo(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasGetPlayerPose) return "0";
        if (!_host.TryGetPlayerPose(out uint pc, out float px, out float py, out _, out _, out _, out _, out _)) return "0";
        if (!_host.HasGetObjectPosition) return "0";
        if (!_host.TryGetObjectPosition(uid, out uint tc, out float tx, out float ty, out _)) return "0";

        // x/y are metres within each one's own landblock (192 m square); add the landblocks'
        // offsets, or a target across a landblock edge points the wrong way.
        double dx = ((int)(tc >> 24) - (int)(pc >> 24)) * 192.0 + (tx - px);
        double dy = ((int)((tc >> 16) & 0xFF) - (int)((pc >> 16) & 0xFF)) * 192.0 + (ty - py);
        double deg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
        if (deg < 0) deg += 360.0;
        return deg.ToString("G", CultureInfo.InvariantCulture);
    }

    // ── Chat implementations ──────────────────────────────────────────────────

    private string EvalChatbox(string message)
    {
        if (string.IsNullOrEmpty(message) || !_host.HasInvokeChatParser) return message;
        _host.InvokeChatParser(message);
        return message;
    }

    private string EvalEcho(string message, string colorArg)
    {
        if (!_host.HasWriteToChat) return "0";
        int chatType = (int)ToDouble(colorArg);
        return _host.WriteToChat(message, chatType) ? "1" : "0";
    }

    // ── Salvage (UST) implementations ────────────────────────────────────────

    private string EvalUstOpen()
    {
        if (_worldObjectCache == null || !_host.HasUseObject) return "0";
        foreach (var item in _worldObjectCache.GetDirectInventory())
        {
            if (string.Equals(item.Name, "Ust", StringComparison.OrdinalIgnoreCase))
            {
                _host.UseFor((uint)item.Id, "Meta", "ustopen", UseKind.Asked);
                return "1";
            }
        }
        return "0";
    }

    private string EvalUstAdd(string objArg)
    {
        if (!_host.HasSalvagePanel) return "0";
        uint uid;
        // Accept either a wobject handle ("[WorldObject] 0x...") or a plain numeric ID
        if (!TryParseWobjectHandle(objArg, out uid, out _))
        {
            if (!uint.TryParse(objArg.Trim(), out uid) || uid == 0) return "0";
        }
        _host.SalvagePanelAddItem(uid);
        return "1";
    }

    private string EvalUstSalvage()
    {
        if (!_host.HasSalvagePanel) return "0";
        _host.SalvagePanelExecute();
        return "1";
    }

    // ── Game time (Dereth) implementations ───────────────────────────────────

    // AC game clock: raw double at 0x008379A8 in game ticks.
    // CurrentTime = rawTicks - 210 + (476.25 * 8) + (476.25 * 16 * 30 * 12 * 10)
    // Matches UB DerethTime.cs exactly.
    private const double TicksInHour  = 476.25;
    private const int    HoursInDay   = 16;
    private const int    DaysInMonth  = 30;
    private const int    MonthsInYear = 12;
    private static readonly int    TicksInDay   = (int)(TicksInHour * HoursInDay);
    private static readonly int    TicksInMonth = (int)(TicksInHour * HoursInDay * DaysInMonth);
    private static readonly int    TicksInYear  = (int)(TicksInHour * HoursInDay * DaysInMonth * MonthsInYear);

    private static readonly string[] MonthNames =
    {
        "Morningthaw","Solclaim","Seedsow","Leafdawning","Verdantine","Thistledown",
        "Harvestgain","Leafcull","Frostfell","Snowreap","Coldeve","Wintersebb"
    };

    private static readonly string[] HourNames =
    {
        "Darktide","Darktide-and-Half","Foredawn","Foredawn-and-Half",
        "Dawnsong","Dawnsong-and-Half","Morntide","Morntide-and-Half",
        "Midsong","Midsong-and-Half","Warmtide","Warmtide-and-Half",
        "Evensong","Evensong-and-Half","Gloaming","Gloaming-and-Half"
    };

    private bool _loggedGameClockNotInWorld;

    /// <summary>
    /// Reads the raw double at 0x008379A8 — the AC game clock register. That address
    /// is only mapped once the client is in world, so any getgame* expression
    /// evaluated before world entry used to fault (2026-06-03 audit P3). An
    /// AccessViolationException is a corrupted-state exception and is NOT catchable
    /// in .NET, so this guards instead of catching: no player id means no world,
    /// which means the register must not be touched.
    /// </summary>
    private double ReadGameClock()
    {
        if (!_host.HasGetPlayerId || _host.GetPlayerId() == 0)
        {
            if (!_loggedGameClockNotInWorld)
            {
                _loggedGameClockNotInWorld = true;
                _host.Log("[Meta] getgame* evaluated before world entry — game clock unreadable, returning 0 (not faulting).");
            }
            return 0;
        }

        long raw = System.Runtime.InteropServices.Marshal.ReadInt64(new IntPtr(unchecked((int)0x008379A8)));
        return BitConverter.Int64BitsToDouble(raw);
    }

    private double GetGameTicks()
    {
        double rawTicks = ReadGameClock();
        return rawTicks - 210 + (TicksInHour * 8) + (TicksInHour * HoursInDay * DaysInMonth * MonthsInYear * 10);
    }

    private int GetGameYear()   => (int)(GetGameTicks() / TicksInYear);
    private int GetGameMonth()  => (int)(GetGameTicks() % TicksInYear  / TicksInMonth);
    // "the current ingame day of the month. 1-30" (it was 0-29).
    private int GetGameDay()    => (int)(GetGameTicks() % TicksInMonth / TicksInDay) + 1;
    private int GetGameHour()   => (int)(GetGameTicks() % TicksInDay   / TicksInHour);
    private bool GetIsDay()     { int h = GetGameHour(); return h >= 4 && h < 12; }

    // Lookup by index (0-based), matches UB: getgamemonthname[0] = "Morningthaw", getgamemonthname[1] = "Solclaim"
    private static string GetGameMonthName(string arg)
    {
        if (!int.TryParse(arg.Trim(), out int n)) return "";
        return n >= 0 && n < MonthNames.Length ? MonthNames[n] : "";
    }

    private static string GetGameHourName(string arg)
    {
        if (!int.TryParse(arg.Trim(), out int n)) return "";
        return n >= 0 && n < HourNames.Length ? HourNames[n] : "";
    }

    private int GetMinutesUntilDay()
    {
        if (GetIsDay()) return 0;
        // Ticks remaining until hour 4 (dawn)
        double ticksIntoDay = GetGameTicks() % TicksInDay;
        double dawnTick     = 4 * TicksInHour;
        double ticksUntil   = ticksIntoDay < dawnTick
            ? dawnTick - ticksIntoDay
            : TicksInDay - ticksIntoDay + dawnTick;
        return (int)(ticksUntil / 60);
    }

    private int GetMinutesUntilNight()
    {
        if (!GetIsDay()) return 0;
        // Ticks remaining until hour 12 (dusk)
        double ticksIntoDay = GetGameTicks() % TicksInDay;
        double duskTick     = 12 * TicksInHour;
        double ticksUntil   = duskTick - ticksIntoDay;
        return (int)(ticksUntil / 60);
    }

    // ── Fellowship implementations ────────────────────────────────────────────

    private string EvalFellowshipIsFull()
    {
        if (_fellowshipTracker == null || !_fellowshipTracker.IsInFellowship) return "0";
        return _fellowshipTracker.MemberCount >= 9 ? "1" : "0";
    }

    private string EvalFellowshipCanRecruit()
    {
        if (_fellowshipTracker == null || !_fellowshipTracker.IsInFellowship) return "0";
        if (_fellowshipTracker.MemberCount >= 9) return "0";
        return (_fellowshipTracker.IsLeader || _fellowshipTracker.IsOpen) ? "1" : "0";
    }

    // VTank getfellowshipstatus[] — the migration keystone (~149 metas use it).
    // Documented: "Returns 1 if you are in a fellowship, 0 otherwise". It used to return 2 for a
    // member who isn't the leader (a guess from meta usage), so metas testing ==1 saw a member
    // as not in a fellowship (2026-10-11). Leader checks: getfellowshipisleader[].
    private string EvalGetFellowshipStatus()
    {
        if (_fellowshipTracker == null || !_fellowshipTracker.IsInFellowship) return "0";
        return "1";
    }

    private string EvalGetFellowId(string arg)
    {
        if (_fellowshipTracker == null) return "0";
        if (!int.TryParse(arg.Trim(), out int idx)) return "0";
        int id = _fellowshipTracker.GetMemberId(idx);
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalGetFellowName(string arg)
    {
        if (_fellowshipTracker == null) return "";
        if (!int.TryParse(arg.Trim(), out int idx)) return "";
        return _fellowshipTracker.GetMemberName(idx);
    }

    private string EvalGetFellowNames()
    {
        if (_fellowshipTracker == null) return "[]";
        var names = new List<string>(_fellowshipTracker.GetMemberNames());
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return NewList(names);
    }

    private string EvalGetFellowIds()
    {
        if (_fellowshipTracker == null) return "[]";
        int count = _fellowshipTracker.MemberCount;
        var ids = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int id = _fellowshipTracker.GetMemberId(i);
            if (id != 0) ids.Add(id);
        }
        ids.Sort();
        var strs = new List<string>(ids.Count);
        foreach (int id in ids) strs.Add(id.ToString(CultureInfo.InvariantCulture));
        return NewList(strs);
    }

    // ── Option function implementations ───────────────────────────────────────

    private string EvalOptSet(string name, string value)
    {
        if (string.IsNullOrEmpty(name)) return "0";
        _options[name] = value;
        return "1";
    }

    // ── RynthAi settings / meta state implementations ─────────────────────────

    private string EvalVtSetMetaState(string state)
    {
        if (_settings == null || string.IsNullOrEmpty(state)) return "0";
        _settings.CurrentState = state;
        _settings.ForceStateReset = true;
        return "1";
    }

    private string EvalVtGetSetting(string name)
    {
        if (_settings == null || string.IsNullOrEmpty(name)) return "";
        var map = BuildSettingsMap();
        if (map.TryGetValue(name, out var entry)) return entry.Get();
        // VTank option names (EnableNav, NavPriorityBoost, Recharge-Norm-Hitp...) → RynthAi setting.
        if (MetaManager.TryMapVtOption(name, out string raName) && map.TryGetValue(raName, out entry)) return entry.Get();
        return _options.TryGetValue(name, out string? v) ? v : "";
    }

    private string EvalVtSetSetting(string name, string value)
    {
        if (_settings == null || string.IsNullOrEmpty(name)) return "0";
        var map = BuildSettingsMap();
        // VTank option names map to RynthAi settings, and bool text (True/false/on/off)
        // becomes 1/0: the bool setters read a number, so 'True' used to switch it OFF.
        if (map.TryGetValue(name, out var entry)
            || (MetaManager.TryMapVtOption(name, out string raName) && map.TryGetValue(raName, out entry)))
        {
            entry.Set(MetaManager.NormalizeVtValue(value));
            return "1";
        }
        _options[name] = value;
        return "1";
    }

    public Dictionary<string, (Func<string> Get, Action<string> Set)> BuildSettingsMapPublic()
        => BuildSettingsMap();

    private Dictionary<string, (Func<string> Get, Action<string> Set)> BuildSettingsMap()
    {
        if (_settingsMap != null) return _settingsMap;
        var s = _settings!;
        static string B(bool v) => v ? "1" : "0";
        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        static string F(float v) => v.ToString("G", CultureInfo.InvariantCulture);
        static string D(double v) => v.ToString("G", CultureInfo.InvariantCulture);
        _settingsMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EnableCombat"]        = (() => B(s.EnableCombat),        v => s.EnableCombat        = ToDouble(v) != 0),
            ["EnableBuffing"]       = (() => B(s.EnableBuffing),       v => s.EnableBuffing       = ToDouble(v) != 0),
            ["EnableNavigation"]    = (() => B(s.EnableNavigation),    v => s.EnableNavigation    = ToDouble(v) != 0),
            ["EnableLooting"]       = (() => B(s.EnableLooting),       v => s.EnableLooting       = ToDouble(v) != 0),
            ["EnableMeta"]          = (() => B(s.EnableMeta),          v => s.EnableMeta          = ToDouble(v) != 0),
            ["EnableRaycasting"]    = (() => B(s.EnableRaycasting),    v => s.EnableRaycasting    = ToDouble(v) != 0),
            ["UseArcs"]             = (() => B(s.UseArcs),             v => s.UseArcs             = ToDouble(v) != 0),
            ["LosDebugLog"]         = (() => B(s.LosDebugLog),         v => s.LosDebugLog         = ToDouble(v) != 0),
            ["BowArcVelocity"]      = (() => F(s.BowArcVelocity),      v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) s.BowArcVelocity      = Math.Clamp(f, 10f, 60f); }),
            ["CrossbowArcVelocity"] = (() => F(s.CrossbowArcVelocity), v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) s.CrossbowArcVelocity = Math.Clamp(f, 10f, 80f); }),
            ["AtlatlArcVelocity"]   = (() => F(s.AtlatlArcVelocity),   v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) s.AtlatlArcVelocity   = Math.Clamp(f, 10f, 60f); }),
            ["MagicArcVelocity"]    = (() => F(s.MagicArcVelocity),    v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) s.MagicArcVelocity    = Math.Clamp(f, 10f, 60f); }),
            ["MissileArcClearance"] = (() => F(s.MissileArcClearance), v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) s.MissileArcClearance = Math.Clamp(f, 0f, 3f); }),
            ["EnableAutostack"]     = (() => B(s.EnableAutostack),     v => s.EnableAutostack     = ToDouble(v) != 0),
            ["EnableAutocram"]      = (() => B(s.EnableAutocram),      v => s.EnableAutocram      = ToDouble(v) != 0),
            ["ReadUnknownScrolls"]  = (() => B(s.ReadUnknownScrolls),  v => s.ReadUnknownScrolls  = ToDouble(v) != 0),
            ["EnableCombineSalvage"]= (() => B(s.EnableCombineSalvage),v => s.EnableCombineSalvage= ToDouble(v) != 0),
            ["SalvageWithoutPanel"] = (() => B(s.SalvageWithoutPanel), v => s.SalvageWithoutPanel = ToDouble(v) != 0),
            ["PeaceModeWhenIdle"]   = (() => B(s.PeaceModeWhenIdle),   v => s.PeaceModeWhenIdle   = ToDouble(v) != 0),
            ["RearmAfterVitals"]    = (() => B(s.RearmAfterVitals),    v => s.RearmAfterVitals    = ToDouble(v) != 0),
            ["FastWeaponSwap"]      = (() => B(s.FastWeaponSwap),      v => s.FastWeaponSwap      = ToDouble(v) != 0),
            ["RebuffWhenIdle"]      = (() => B(s.RebuffWhenIdle),      v => s.RebuffWhenIdle      = ToDouble(v) != 0),
            ["UsePotions"] = (() => B(s.UsePotions), v => s.UsePotions = ToDouble(v) != 0),
            ["UseBuffItems"] = (() => B(s.UseBuffItems), v => s.UseBuffItems = ToDouble(v) != 0),
            ["MakeRationsBelow"] = (() => s.MakeRationsBelow.ToString(CultureInfo.InvariantCulture), v => s.MakeRationsBelow = Math.Clamp((int)ToDouble(v), 0, 100)),
            ["UseKitsInMagicMode"] = (() => B(s.UseKitsInMagicMode), v => s.UseKitsInMagicMode = ToDouble(v) != 0),
            ["PeaceModeForKits"] = (() => B(s.PeaceModeForKits), v => s.PeaceModeForKits = ToDouble(v) != 0),
            ["KitMinSuccessPct"] = (() => s.KitMinSuccessPct.ToString(CultureInfo.InvariantCulture), v => s.KitMinSuccessPct = Math.Clamp((int)ToDouble(v), 0, 100)),
            ["StaminaKitMinSuccessPct"] = (() => s.StaminaKitMinSuccessPct.ToString(CultureInfo.InvariantCulture), v => s.StaminaKitMinSuccessPct = Math.Clamp((int)ToDouble(v), 0, 100)),
            ["EmergencyHealAt"] = (() => s.EmergencyHealAt.ToString(CultureInfo.InvariantCulture), v => s.EmergencyHealAt = Math.Clamp((int)ToDouble(v), 0, 100)),
            ["StaminaToHealthAt"] = (() => s.StaminaToHealthAt.ToString(CultureInfo.InvariantCulture), v => s.StaminaToHealthAt = Math.Clamp((int)ToDouble(v), 0, 100)),
            ["StaminaToHealthMinStamina"] = (() => s.StaminaToHealthMinStamina.ToString(CultureInfo.InvariantCulture), v => s.StaminaToHealthMinStamina = Math.Clamp((int)ToDouble(v), 0, 100)),
            ["StopMacroOnDeath"] = (() => B(s.StopMacroOnDeath), v => s.StopMacroOnDeath = ToDouble(v) != 0),
            ["DeathRecallMode"] = (() => I(s.DeathRecallMode), v => { if (int.TryParse(v, out int i)) s.DeathRecallMode = Math.Clamp(i, 0, 2); }),
            ["DeathRecallSpellId"] = (() => I(s.DeathRecallSpellId), v => { if (int.TryParse(v, out int i) && i > 0) s.DeathRecallSpellId = i; }),
            ["DeathRecallItem"] = (() => s.DeathRecallItem, v => s.DeathRecallItem = v?.Trim() ?? string.Empty),
            ["StopMacroOnNoComponents"] = (() => B(s.StopMacroOnNoComponents), v => s.StopMacroOnNoComponents = ToDouble(v) != 0),
            ["StopLootingWhenPackFull"] = (() => B(s.StopLootingWhenPackFull), v => s.StopLootingWhenPackFull = ToDouble(v) != 0),
            ["LootOwnCorpse"] = (() => B(s.LootOwnCorpse), v => s.LootOwnCorpse = ToDouble(v) != 0),
            ["EscapeHazards"] = (() => B(s.EscapeHazards), v => s.EscapeHazards = ToDouble(v) != 0),
            ["TravelToOwnCorpse"] = (() => B(s.TravelToOwnCorpse), v => s.TravelToOwnCorpse = ToDouble(v) != 0),
            ["ItemInfoOnSelect"] = (() => B(s.ItemInfoSettings.OnSelect), v => s.ItemInfoSettings.OnSelect = ToDouble(v) != 0),
            // The looter's "Looted" line names the rule and the item's details (salvage: on request).
            ["ItemInfoLootChat"] = (() => B(s.ItemInfoSettings.LootChat), v => s.ItemInfoSettings.LootChat = ToDouble(v) != 0),
            ["ItemInfoLootSalvage"] = (() => B(s.ItemInfoSettings.LootChatSalvage), v => s.ItemInfoSettings.LootChatSalvage = ToDouble(v) != 0),
            // -1 = the Item Info chat type.
            ["ItemInfoLootChatType"] = (() => I(s.ItemInfoSettings.LootChatType), v => { if (int.TryParse(v, out int i)) s.ItemInfoSettings.LootChatType = Math.Clamp(i, -1, 31); }),
            ["StopMacroWhenPackFull"] = (() => B(s.StopMacroWhenPackFull), v => s.StopMacroWhenPackFull = ToDouble(v) != 0),
            ["SummonPets"]          = (() => B(s.SummonPets),          v => s.SummonPets          = ToDouble(v) != 0),
            ["MineOnly"]            = (() => B(s.MineOnly),            v => s.MineOnly            = ToDouble(v) != 0),
            ["UseDispelItems"]      = (() => B(s.UseDispelItems),      v => s.UseDispelItems      = ToDouble(v) != 0),
            ["CastDispelSelf"]      = (() => B(s.CastDispelSelf),      v => s.CastDispelSelf      = ToDouble(v) != 0),
            ["AutoFellowMgmt"]      = (() => B(s.AutoFellowMgmt),      v => s.AutoFellowMgmt      = ToDouble(v) != 0),
            ["UseRecklessness"]     = (() => B(s.UseRecklessness),     v => s.UseRecklessness     = ToDouble(v) != 0),
            // The range a monster rule's Variable attack power picks from (percent of the bar).
            ["VariablePowerMin"]    = (() => I(s.VariablePowerMin),    v => { if (int.TryParse(v, out int i)) s.VariablePowerMin = Math.Clamp(i, 0, 100); }),
            ["VariablePowerMax"]    = (() => I(s.VariablePowerMax),    v => { if (int.TryParse(v, out int i)) s.VariablePowerMax = Math.Clamp(i, 0, 100); }),
            // Environment hits: 0 Off, 1 Move closer, 2 New target, 3 Move closer then new target.
            ["MissileEnvironmentResponse"] = (() => I(s.MissileEnvironmentResponse), v => { if (int.TryParse(v, out int i)) s.MissileEnvironmentResponse = Math.Clamp(i, 0, 3); }),
            ["MagicEnvironmentResponse"]   = (() => I(s.MagicEnvironmentResponse),   v => { if (int.TryParse(v, out int i)) s.MagicEnvironmentResponse   = Math.Clamp(i, 0, 3); }),
            ["EnvironmentImpactLimit"]     = (() => I(s.EnvironmentImpactLimit),     v => { if (int.TryParse(v, out int i)) s.EnvironmentImpactLimit     = Math.Clamp(i, 1, 10); }),
            ["EnvironmentImpactWindowSec"] = (() => I(s.EnvironmentImpactWindowSec), v => { if (int.TryParse(v, out int i)) s.EnvironmentImpactWindowSec = Math.Clamp(i, 5, 120); }),
            ["EnvironmentApproachMeters"]  = (() => I(s.EnvironmentApproachMeters),  v => { if (int.TryParse(v, out int i)) s.EnvironmentApproachMeters  = Math.Clamp(i, 1, 30); }),
            ["EnvironmentMinDistance"]     = (() => I(s.EnvironmentMinDistance),     v => { if (int.TryParse(v, out int i)) s.EnvironmentMinDistance     = Math.Clamp(i, 1, 30); }),
            ["EnvironmentSkipSec"]         = (() => I(s.EnvironmentSkipSec),         v => { if (int.TryParse(v, out int i)) s.EnvironmentSkipSec         = Math.Clamp(i, 5, 300); }),
            // Pull-away kite (Combat/KiteModes.cs).
            ["KitePullMeters"]             = (() => I(s.KitePullMeters),             v => { if (int.TryParse(v, out int i)) s.KitePullMeters             = Math.Clamp(i, 3, 100); }),
            ["KiteTagFirst"]               = (() => B(s.KiteTagFirst),               v => s.KiteTagFirst               = ToDouble(v) != 0),
            ["WeaponSwapSettleMs"]         = (() => I(s.WeaponSwapSettleMs),         v => { if (int.TryParse(v, out int i)) s.WeaponSwapSettleMs         = Math.Clamp(i, 0, 3000); }),
            ["WeaponSwapMinHealthPct"]     = (() => I(s.WeaponSwapMinHealthPct),     v => { if (int.TryParse(v, out int i)) s.WeaponSwapMinHealthPct     = Math.Clamp(i, 0, 100); }),
            ["WeaponSwapMinGainPct"]       = (() => I(s.WeaponSwapMinGainPct),       v => { if (int.TryParse(v, out int i)) s.WeaponSwapMinGainPct       = Math.Clamp(i, 0, 200); }),
            ["UseNativeAttack"]     = (() => B(s.UseNativeAttack),     v => s.UseNativeAttack     = ToDouble(v) != 0),
            ["WieldUnlistedWandWhenNoneListed"] = (() => B(s.WieldUnlistedWandWhenNoneListed), v => s.WieldUnlistedWandWhenNoneListed = ToDouble(v) != 0),
            // The off hand when a monster rule doesn't choose: Auto, Shield, Weapon or None (or 0-3).
            ["OffhandDefault"]      = (() => s.OffhandDefault,         v => { if (OffhandRules.TryParse(v, out var m)) s.OffhandDefault = OffhandRules.SettingValue(m); }),
            ["PreferDualWield"]     = (() => B(s.PreferDualWield),     v => s.PreferDualWield     = ToDouble(v) != 0),
            ["MonsterRange"]        = (() => I(s.MonsterRange),        v => { if (int.TryParse(v, out int i)) s.MonsterRange     = i; }),
            // Targeting: 0 lowest health, 1 closest, 2 farthest, 3 least turning.
            ["TargetSelection"]     = (() => I(s.TargetSelection),     v => { if (int.TryParse(v, out int i)) s.TargetSelection  = Math.Clamp(i, 0, 3); }),
            ["TargetLock"]          = (() => B(s.TargetLock),          v => s.TargetLock          = ToDouble(v) != 0),
            ["FightSelectedTarget"] = (() => B(s.FightSelectedTarget), v => s.FightSelectedTarget = ToDouble(v) != 0),
            ["RingRange"]           = (() => I(s.RingRange),           v => { if (int.TryParse(v, out int i)) s.RingRange        = i; }),
            ["ApproachRange"]       = (() => I(s.ApproachRange),       v => { if (int.TryParse(v, out int i)) s.ApproachRange    = i; }),
            ["MinRingTargets"]      = (() => I(s.MinRingTargets),      v => { if (int.TryParse(v, out int i)) s.MinRingTargets   = i; }),
            ["BlastRange"]          = (() => I(s.BlastRange),      v => { if (int.TryParse(v, out int i)) s.BlastRange   = i; }),
            ["MinBlastTargets"]     = (() => I(s.MinBlastTargets),      v => { if (int.TryParse(v, out int i)) s.MinBlastTargets   = i; }),
            ["WallRange"]           = (() => I(s.WallRange),           v => { if (int.TryParse(v, out int i)) s.WallRange        = i; }),
            ["MinWallTargets"]      = (() => I(s.MinWallTargets),      v => { if (int.TryParse(v, out int i)) s.MinWallTargets   = i; }),
            ["HealAt"]              = (() => I(s.HealAt),              v => { if (int.TryParse(v, out int i)) s.HealAt           = i; }),
            ["RestamAt"]            = (() => I(s.RestamAt),            v => { if (int.TryParse(v, out int i)) s.RestamAt         = i; }),
            ["RestamTo"]            = (() => I(s.RestamTo),            v => { if (int.TryParse(v, out int i)) s.RestamTo         = i; }),
            ["GetManaAt"]           = (() => I(s.GetManaAt),           v => { if (int.TryParse(v, out int i)) s.GetManaAt        = i; }),
            ["TopOffHP"]            = (() => I(s.TopOffHP),            v => { if (int.TryParse(v, out int i)) s.TopOffHP         = i; }),
            ["TopOffStam"]          = (() => I(s.TopOffStam),          v => { if (int.TryParse(v, out int i)) s.TopOffStam       = i; }),
            ["TopOffMana"]          = (() => I(s.TopOffMana),          v => { if (int.TryParse(v, out int i)) s.TopOffMana       = i; }),
            ["HealOthersAt"]        = (() => I(s.HealOthersAt),        v => { if (int.TryParse(v, out int i)) s.HealOthersAt     = i; }),
            ["MeleeAttackPower"]    = (() => I(s.MeleeAttackPower),    v => { if (int.TryParse(v, out int i)) s.MeleeAttackPower = i; }),
            ["MissileAttackPower"]  = (() => I(s.MissileAttackPower),  v => { if (int.TryParse(v, out int i)) s.MissileAttackPower = i; }),
            ["CustomPetRange"]      = (() => I(s.CustomPetRange),      v => { if (int.TryParse(v, out int i)) s.CustomPetRange   = i; }),
            ["PetMinMonsters"]      = (() => I(s.PetMinMonsters),      v => { if (int.TryParse(v, out int i)) s.PetMinMonsters   = i; }),
            // VTank-meta migration (Phase 2): fields existed but weren't exposed here.
            ["CorpseApproachRangeMax"] = (() => D(s.CorpseApproachRangeMax), v => { if (double.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) s.CorpseApproachRangeMax = d; }),
            ["CorpseApproachRangeMin"] = (() => D(s.CorpseApproachRangeMin), v => { if (double.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) s.CorpseApproachRangeMin = d; }),
            ["ManaStoneKeepCount"]     = (() => I(s.ManaStoneKeepCount),     v => { if (int.TryParse(v, out int i)) s.ManaStoneKeepCount = i; }),
            ["RebuffSecondsRemaining"] = (() => I(s.RebuffSecondsRemaining), v => { if (int.TryParse(v, out int i)) s.RebuffSecondsRemaining = i; }),
            ["BlacklistTimeoutSec"]    = (() => I(s.BlacklistTimeoutSec),    v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0) s.BlacklistTimeoutSec = (int)Math.Round(d); }),
            ["RebuffTopOffSecondsRemaining"] = (() => I(s.RebuffTopOffSecondsRemaining), v => { if (int.TryParse(v, out int i)) s.RebuffTopOffSecondsRemaining = i; }),
            ["RebuffIdleMarginMinutes"]      = (() => I(s.RebuffIdleMarginMinutes),      v => { if (int.TryParse(v, out int i)) s.RebuffIdleMarginMinutes = Math.Max(0, i); }),
            ["CastBuffsOverItemBuffs"]       = (() => B(s.CastBuffsOverItemBuffs),       v => s.CastBuffsOverItemBuffs       = ToDouble(v) != 0),
            ["FastBuffChain"]                = (() => B(s.FastBuffChain),                v => s.FastBuffChain                = ToDouble(v) != 0),
            ["SkipUnusableBuffs"]            = (() => B(s.SkipUnusableBuffs),            v => s.SkipUnusableBuffs            = ToDouble(v) != 0),
            ["BuffShield"]                   = (() => B(s.BuffShield),                   v => s.BuffShield                   = ToDouble(v) != 0),
            // AutoVendor (UB names map here through /ub opt: AutoVendor.Enabled -> AutoVendorEnabled, ...)
            ["AutoVendorEnabled"]          = (() => B(s.AutoVendorEnabled),          v => s.AutoVendorEnabled          = ToDouble(v) != 0),
            ["AutoVendorEnableBuying"]     = (() => B(s.AutoVendorEnableBuying),     v => s.AutoVendorEnableBuying     = ToDouble(v) != 0),
            ["AutoVendorEnableSelling"]    = (() => B(s.AutoVendorEnableSelling),    v => s.AutoVendorEnableSelling    = ToDouble(v) != 0),
            ["AutoVendorTestMode"]         = (() => B(s.AutoVendorTestMode),         v => s.AutoVendorTestMode         = ToDouble(v) != 0),
            ["AutoVendorThink"]            = (() => B(s.AutoVendorThink),            v => s.AutoVendorThink            = ToDouble(v) != 0),
            ["AutoVendorShowMerchantInfo"] = (() => B(s.AutoVendorShowMerchantInfo), v => s.AutoVendorShowMerchantInfo = ToDouble(v) != 0),
            ["AutoVendorOnlyFromMainPack"] = (() => B(s.AutoVendorOnlyFromMainPack), v => s.AutoVendorOnlyFromMainPack = ToDouble(v) != 0),
            ["AutoVendorTries"]            = (() => I(s.AutoVendorTries),            v => { if (int.TryParse(v, out int i)) s.AutoVendorTries = Math.Clamp(i, 1, 20); }),
            ["AutoVendorTriesTime"]        = (() => I(s.AutoVendorTriesTime),        v => { if (int.TryParse(v, out int i)) s.AutoVendorTriesTime = Math.Clamp(i, 500, 30000); }),
            // AutoTrade (UB names map here through /ub opt: AutoTrade.Enabled -> AutoTradeEnabled, ...)
            ["AutoTradeEnabled"]           = (() => B(s.AutoTradeEnabled),           v => s.AutoTradeEnabled           = ToDouble(v) != 0),
            ["AutoTradeTestMode"]          = (() => B(s.AutoTradeTestMode),          v => s.AutoTradeTestMode          = ToDouble(v) != 0),
            ["AutoTradeThink"]             = (() => B(s.AutoTradeThink),             v => s.AutoTradeThink             = ToDouble(v) != 0),
            ["AutoTradeOnlyFromMainPack"]  = (() => B(s.AutoTradeOnlyFromMainPack),  v => s.AutoTradeOnlyFromMainPack  = ToDouble(v) != 0),
            ["AutoTradeAutoAccept"]        = (() => B(s.AutoTradeAutoAccept),        v => s.AutoTradeAutoAccept        = ToDouble(v) != 0),
            // Nav point reach: how close nav gets to each nav point before moving on (yards).
            ["FollowNavMin"]           = (() => F(s.FollowNavMin),           v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f)) s.FollowNavMin = LegacyUiSettings.ClampFollowNavMin(f); }),
            // VTank's NavCloseStopRange IS its "Follow/Nav Min Distance" box (the box shows it x240
            // in yards; utank2-i.dll fills txtWPRange from it). It used to be read here as a
            // separate "stop short of a Once route's last point" range, so a meta's
            // navclosestoprange never changed how close nav got to each point. Same setting now,
            // in VTank's landblock units: 0.00625 = 1.5 yd.
            ["NavCloseStopRange"]      = (() => F(s.FollowNavMin / 240f),    v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f) && f > 0f) s.FollowNavMin = LegacyUiSettings.ClampFollowNavMin(f * 240f); }),
            ["NavShortcutYards"]       = (() => F(s.NavShortcutYards),       v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f)) s.NavShortcutYards = Math.Clamp(f, 0f, 10f); }),
            ["MaxMonRange"]         = (() => D(s.MaxMonRange),         v => { if (double.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) s.MaxMonRange = d; }),
            ["NavRingThickness"]    = (() => F(s.NavRingThickness),    v => { if (float.TryParse(v,  System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f))  s.NavRingThickness = f; }),
            ["NavLineThickness"]    = (() => F(s.NavLineThickness),    v => { if (float.TryParse(v,  System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f))  s.NavLineThickness = f; }),
            ["NavLabelMode"]        = (() => I(s.NavLabelMode),        v => { if (int.TryParse(v, out int i)) s.NavLabelMode = NavLabelModes.Clamp(i); }),
            ["NavGuideLine"]        = (() => B(s.NavGuideLine),        v => s.NavGuideLine    = ToDouble(v) != 0),
            ["CurrentNavPath"]      = (() => s.CurrentNavPath,         v => s.CurrentNavPath  = v),
            ["CurrentLootPath"]     = (() => s.CurrentLootPath,        v => s.CurrentLootPath = v),
            // Loot Mode: 0 = profile, 1 = rares and needed mana stones only (the words work too).
            ["LootMode"]            = (() => I(Loot.LootModes.Clamp(s.LootMode)), v => { if (Loot.LootModes.TryParse(v, out int m)) s.LootMode = m; }),
            ["CurrentMetaPath"]     = (() => s.CurrentMetaPath,        v => s.CurrentMetaPath = v),
            ["OpenDoors"]           = (() => B(s.OpenDoors),           v => s.OpenDoors       = ToDouble(v) != 0),
            ["NavRecoveryEnabled"]  = (() => B(s.NavRecoveryEnabled),  v => s.NavRecoveryEnabled = ToDouble(v) != 0),
            ["NavResumeNearest"]    = (() => B(s.NavResumeNearest),    v => s.NavResumeNearest   = ToDouble(v) != 0),
            ["NavOffTrackYards"]    = (() => F(s.NavOffTrackYards),    v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f)) s.NavOffTrackYards = Math.Clamp(f, 160f, 1000f); }),
            ["OutdoorHuntRange"]    = (() => F(s.OutdoorHuntRange),    v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f)) s.OutdoorHuntRange = (float)Math.Clamp(f, RynthAiPlugin.OutdoorHuntRangeMin, RynthAiPlugin.OutdoorHuntRangeMax); }),
            ["OutdoorHuntRadius"]   = (() => F(s.OutdoorHuntRadius),   v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f)) s.OutdoorHuntRadius = (float)Math.Clamp(f, 0, RynthAiPlugin.OutdoorHuntRadiusMax); }),
            ["NavMaxDetourAttempts"] = (() => I(s.NavMaxDetourAttempts), v => { if (int.TryParse(v, out int i)) s.NavMaxDetourAttempts = Math.Clamp(i, 1, 10); }),
            ["OpenDoorRange"]       = (() => F(s.OpenDoorRange),       v => { if (float.TryParse(v, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float f)) s.OpenDoorRange = f; }),
            ["BoostNavPriority"]    = (() => B(s.BoostNavPriority),    v => s.BoostNavPriority  = ToDouble(v) != 0),
            ["BoostLootPriority"]   = (() => B(s.BoostLootPriority),   v => s.BoostLootPriority = ToDouble(v) != 0),
        };
        return _settingsMap;
    }

    // ── Dynamic evaluation implementations ───────────────────────────────────

    private string EvalDelayExec(string delayArg, string exprArg)
    {
        if (!double.TryParse(delayArg, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double delayMs))
            delayMs = 0;
        long ms = (long)Math.Max(0, delayMs);
        // ub-IT returns the job's id (for clearexec); ids start at 1 so the result stays true.
        long id = ++_nextDelayedExecId;
        _delayedExecs.Add((id, TickMs() + ms, exprArg));
        return id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Fires delayexec entries whose delay has elapsed. MUST be called on the
    /// game/plugin-tick thread (it Evaluates expressions that touch AC native
    /// state). Resolution is the tick rate (~30 Hz), fine for meta timing. A
    /// delayexec re-scheduled by a fired expression runs no earlier than the
    /// next pump (snapshot-then-fire, so it can't loop within one pump).
    /// </summary>
    public void PumpDelayedExecs()
    {
        if (_delayedExecs.Count == 0) return;
        long now = TickMs();
        List<string>? due = null;
        for (int i = _delayedExecs.Count - 1; i >= 0; i--)
        {
            if (_delayedExecs[i].DueMs <= now)
            {
                (due ??= new()).Add(_delayedExecs[i].Expr);
                _delayedExecs.RemoveAt(i);
            }
        }
        if (due == null) return;
        // Collected back-to-front; fire in original scheduled order.
        for (int i = due.Count - 1; i >= 0; i--)
        {
            try { Evaluate(due[i]); } catch { }
        }
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

    public bool TryGetDict(string handle, out Dictionary<string, string>? dict)
        => _dicts.TryGetValue(handle, out dict);

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

    // ── Variable shorthand, indexes and slices ────────────────────────────────

    /// <summary>Reads a variable by sigil: $ memory, @ persistent, &amp; global. Unset reads "0".</summary>
    internal string ReadVar(char sigil, string name)
    {
        var store = sigil switch { '@' => GetPvars(), '&' => GetGvars(), _ => _variables };
        return store.TryGetValue(name, out string? v) ? v ?? "" : "0";
    }

    /// <summary>Writes a variable by sigil, as setvar / setpvar / setgvar do.</summary>
    internal string WriteVar(char sigil, string name, string value) => sigil switch
    {
        '@' => EvalSetPvar(name, value),
        '&' => EvalSetGvar(name, value),
        _   => EvalSetVar(name, value),
    };

    /// <summary>x{i}: a list item, a dictionary value by key, or a character of text. A negative
    /// index counts from the end. Missing → "" (the reference throws).</summary>
    internal string IndexValue(string value, string key)
    {
        var dict = GetDict(value.Trim());
        if (dict != null) return dict.TryGetValue(key, out string? dv) ? dv : "";
        var list = GetList(value);
        int count = list?.Count ?? value.Length;
        long i = ToLong(key);
        if (i < 0) i += count;
        if (i < 0 || i >= count) return "";
        return list != null ? list[(int)i] : value[(int)i].ToString();
    }

    /// <summary>x{start:stop} / x[start:stop]: a sub-list or substring, as Python slices
    /// (stop excluded, negatives count from the end, empty means the start / the end).</summary>
    internal string SliceValue(string value, string start, string stop)
    {
        var list = GetList(value);
        int n = list?.Count ?? value.Length;
        int Norm(string s, int dflt)
        {
            if (s.Trim().Length == 0) return dflt;
            long i = ToLong(s);
            if (i < 0) i += n;
            return (int)Math.Clamp(i, 0, n);
        }
        int a = Norm(start, 0), b = Norm(stop, n);
        if (b < a) b = a;
        return list != null ? NewList(list.GetRange(a, b - a)) : value.Substring(a, b - a);
    }

    /// <summary>Index of a ':' outside brackets and backticks, or -1 (marks a slice).</summary>
    internal static int TopLevelColon(string s)
    {
        int depth = 0;
        bool inBacktick = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (IsEscape(s, i, inBacktick)) { i++; continue; }
            char c = s[i];
            if (c == '`') inBacktick = !inBacktick;
            else if (inBacktick) continue;
            else if (c is '[' or '{' or '(') depth++;
            else if (c is ']' or '}' or ')') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Evaluates "start:stop" (either side may be empty) and slices.</summary>
    internal string SliceBy(string value, string spec)
    {
        int colon = TopLevelColon(spec);
        string a = spec.Substring(0, colon), b = spec.Substring(colon + 1);
        return SliceValue(value, a.Trim().Length > 0 ? Evaluate(a) : "", b.Trim().Length > 0 ? Evaluate(b) : "");
    }

    // ── Coercions ─────────────────────────────────────────────────────────────

    /// <summary>"0" and "" are false; everything else is true.</summary>
    internal static bool ToBool(string s)
        => s.Length > 0 && s != "0" && s != "-0"
           && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)
           // An evaluation error / depth-guard sentinel is never "true" — keeps
           // if[]/iif[]/&&/|| on a broken sub-expression failing closed too.
           && !s.StartsWith("ERR:", StringComparison.Ordinal);

    private static double ToDouble(string s)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double d) ? d : 0.0;

    private static long ToLong(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
        return (long)ToDouble(s);
    }

    // -0 (ceiling[-0.5], round[-0.4], 0*-1) printed "-0", and "-0" counted as true.
    private static string Fmt(double d) => (d == 0 ? 0.0 : d).ToString("G", CultureInfo.InvariantCulture);
    private static string Fmt(long l)   => l.ToString(CultureInfo.InvariantCulture);

    /// <summary>Equality: numeric if both sides parse as numbers, otherwise case-INSENSITIVE
    /// string (M1). Case-insensitivity matches author intent ($x==True is true for 'true') and
    /// the conventions of the metas we import; numeric compares are unaffected.</summary>
    private static bool AreEqual(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double da)
         && double.TryParse(b, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double db))
            return da == db;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // ── Argument splitter ─────────────────────────────────────────────────────

    /// <summary>Splits a comma-separated argument list, respecting nested bracket depth, backtick
    /// strings and backslash escapes ("echo[Not here\, sorry,15]" is two arguments).</summary>
    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        bool inBacktick = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (IsEscape(s, i, inBacktick)) { i++; continue; }
            if (c == '`') { inBacktick = !inBacktick; }
            else if (!inBacktick)
            {
                if (c == '[') depth++;
                else if (c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    list.Add(TrimArg(s.Substring(start, i - start)));
                    start = i + 1;
                }
            }
        }
        list.Add(TrimArg(s.Substring(start)));
        return list;
    }

    /// <summary>A backslash escape at <paramref name="i"/>: outside backticks '\' escapes any
    /// character; inside them only "\`" and "\\" are escapes (other backslashes are text, so
    /// a regex like `\d+` keeps its backslash).</summary>
    private static bool IsEscape(string s, int i, bool inBacktick)
        => s[i] == '\\' && i + 1 < s.Length && (!inBacktick || s[i + 1] is '`' or '\\');

    /// <summary>Trim, but keep an escaped trailing space ("abc\ ").</summary>
    private static string TrimArg(string s)
    {
        string t = s.Trim();
        if (t.Length < s.Length && t.Length > 0 && t[^1] == '\\')
        {
            int slashes = 0;
            for (int i = t.Length - 1; i >= 0 && t[i] == '\\'; i--) slashes++;
            int at = s.IndexOf(t, StringComparison.Ordinal) + t.Length;
            if (slashes % 2 == 1 && at < s.Length) t += s[at];
        }
        return t;
    }

    /// <summary>Index just past the bracket that closes the one at <paramref name="open"/>
    /// (or the end of the text), skipping backtick strings and escapes.</summary>
    private static int FindClose(string s, int open, char openCh, char closeCh)
    {
        int depth = 0;
        bool inBacktick = false;
        for (int i = open; i < s.Length; i++)
        {
            if (IsEscape(s, i, inBacktick)) { i++; continue; }
            char c = s[i];
            if (c == '`') inBacktick = !inBacktick;
            else if (inBacktick) continue;
            else if (c == openCh) depth++;
            else if (c == closeCh && --depth == 0) return i + 1;
        }
        return s.Length;
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

        // > 0 while parsing the side of && / || that must not run: function calls and
        // assignments are skipped (read for their length only), so "0 && echo[x]" echoes nothing.
        private int _skip;

        internal Parser(ExpressionEngine eng, string s) { _eng = eng; _s = s; }

        /// <summary>
        /// Expressions separated by ';' run in order; the value is the last one's
        /// ("setvar[t, stopwatchcreate[]]; stopwatchstart[$t]"). Everything after the first
        /// ';' used to be dropped.
        /// </summary>
        internal string Run()
        {
            string last = ParseOr();
            while (true)
            {
                Ws();
                if (_p >= _s.Length || _s[_p] != ';') break;
                _p++;
                Ws();
                if (_p >= _s.Length) break;
                if (_s[_p] == ';') continue;
                last = ParseOr();
            }
            return last;
        }

        /// <summary>Parses (and so consumes) one operand without running anything in it.</summary>
        private void Skip(Func<string> parse)
        {
            _skip++;
            try { parse(); }
            finally { _skip--; }
        }

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

        // Priority 1 (lowest): ||  (short-circuit: a true left side skips the right side)
        private string ParseOr()
        {
            var l = ParseAnd();
            Ws();
            while (Try("||"))
            {
                if (ToBool(l)) { Skip(ParseAnd); l = "1"; }
                else l = ToBool(ParseAnd()) ? "1" : "0";
                Ws();
            }
            return l;
        }

        // Priority 2: &&  (short-circuit: a false left side skips the right side, so
        // "$debug && echo[...]" only echoes when debugging; it used to always run)
        private string ParseAnd()
        {
            var l = ParseBitOr();
            Ws();
            while (Try("&&"))
            {
                if (!ToBool(l)) { Skip(ParseBitOr); l = "0"; }
                else l = ToBool(ParseBitOr()) ? "1" : "0";
                Ws();
            }
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

        // Priority 6: ==  != (M1: != was previously unsupported and silently dropped, failing OPEN)
        private string ParseEquality()
        {
            var l = ParseComparison();
            Ws();
            while (true)
            {
                if (Try("=="))      { var r = ParseComparison(); l =  AreEqual(l, r) ? "1" : "0"; }
                else if (Try("!=")) { var r = ParseComparison(); l = !AreEqual(l, r) ? "1" : "0"; }
                else break;
                Ws();
            }
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
                var pattern = ParseRegexOperand();
                l = RegexCache.IsMatch(l, pattern, RegexOptions.IgnoreCase) ? "1" : "0";
                Ws();
            }
            return l;
        }

        /// <summary>
        /// The right side of #. A backtick string, $var, (group), function call, number or
        /// plain word is parsed as before. Anything else is taken as raw pattern text, as
        /// the reference documents (getcharstringprop[1] # ^Virindi,
        /// getvar[lastchat] # killed.*olthoi): up to whitespace, a ',' or an unmatched
        /// ')' / ']', or ==, !=, &amp;&amp;, ||. Only the unquoted pattern read as an expression
        /// before (^ as XOR, * as multiply), so it never matched.
        /// </summary>
        private string ParseRegexOperand()
        {
            Ws();
            if (_p >= _s.Length) return "";
            char c = _s[_p];
            if (c is '`' or '$' or '@' or '(' or '~' or '-') return ParseUnary();

            int wordEnd = _p;
            while (wordEnd < _s.Length && (char.IsLetterOrDigit(_s[wordEnd]) || _s[wordEnd] == '_' || _s[wordEnd] == '.'))
                wordEnd++;
            if (wordEnd > _p && wordEnd < _s.Length && _s[wordEnd] == '[') return ParseUnary(); // function call

            int start = _p, q = _p, depth = 0;
            while (q < _s.Length)
            {
                char ch = _s[q];
                if (ch is ' ' or '\t' or '\r' or '\n') break;
                if (ch == '\\' && q + 1 < _s.Length) { q += 2; continue; }   // escaped char stays in the pattern
                if (ch is '[' or '(') depth++;
                else if (ch is ']' or ')') { if (depth == 0) break; depth--; }
                else if (depth == 0)
                {
                    if (ch is ',' or ';') break;
                    if (q + 1 < _s.Length)
                    {
                        char n = _s[q + 1];
                        if ((ch == '=' && n == '=') || (ch == '!' && n == '=')
                            || (ch == '&' && n == '&') || (ch == '|' && n == '|')) break;
                    }
                }
                q++;
            }

            // A plain word (or number) reads exactly as before.
            if (q <= wordEnd) return ParseUnary();
            _p = q;
            return _s.Substring(start, q - start);
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
                if (_eng._parenDepth >= MaxParenDepth) { _p = _s.Length; return "ERR:Depth:paren nesting"; }
                _eng._parenDepth++;
                _p++; // consume '('
                var inner = ParseOr();
                Ws();
                if (_p < _s.Length && _s[_p] == ')') _p++;
                _eng._parenDepth--;
                return ParseIndexes(inner);
            }
            return ParseIndexes(ParseAtom());
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
            // \` inside the string is an escaped backtick (needed for nested expressions), and
            // \\ is one backslash: real metas nest "`chatbox[\\/vt forcebuff]`" (the inner text
            // is then "\/vt forcebuff") and write the regex `\\d+`. Any other \x stays as is,
            // as in the reference's `\d+`.
            if (_s[_p] == '`')
            {
                _p++; // consume opening backtick
                var sb = new System.Text.StringBuilder();
                while (_p < _s.Length && _s[_p] != '`')
                {
                    if (_s[_p] == '\\' && _p + 1 < _s.Length && _s[_p + 1] is '`' or '\\')
                    {
                        sb.Append(_s[_p + 1]);
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

            // $name / @name / &name: getvar / getpvar / getgvar shorthand
            if (_s[_p] == '$' || _s[_p] == '@'
                || (_s[_p] == '&' && _p + 1 < _s.Length && (char.IsLetterOrDigit(_s[_p + 1]) || _s[_p + 1] == '_')))
                return ParseVariable();

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

            // A word: letters, digits, underscores, dots (decimal numbers), and backslash
            // escapes, each of which stands for the character after it ("p\@cMan\$" = p@cMan$).
            var word = new System.Text.StringBuilder();
            bool escaped = false;
            ReadWord(word, ref escaped);

            if (!escaped && _p > start && _p < _s.Length && _s[_p] == '[')
            {
                // Function call: scan to the matching ']' (backticks and escapes skipped)
                _p = FindClose(_s, _p, '[', ']');
                // Pass the entire "funcname[args]" string to EvaluatePrimary
                // (not on the skipped side of && / ||: nothing in it may run)
                if (_skip > 0) return "0";
                return _eng.EvaluatePrimary(_s.Substring(start, _p - start));
            }

            if (_p == start)
            {
                // Skip unrecognised characters (guards against infinite loops). A ';' is left
                // for Run(), which starts the next expression there.
                if (_s[_p] != ';') _p++;
                return "";
            }

            // Text of letters and spaces needs no escaping, so the words that follow (with
            // the spaces between them) are part of it: "echo[Uses dispel gems,15]". Only the
            // first word used to be kept. A number stays one word ("\1\2\3 test" is how the
            // reference writes text that starts with digits).
            bool number = !escaped && char.IsDigit(_s[start]);
            bool words = !number && ReadMoreWords(word, ref escaped);
            string text = word.ToString();

            // "Although there are keywords for true/false, internally they represent number 1/0."
            if (!escaped && !words)
            {
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return "1";
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return "0";
            }
            return text;
        }

        /// <summary>
        /// $name, @name, &amp;name: a memory, persistent or global variable (the reference's
        /// shorthand for getvar / getpvar / getgvar; only $ was supported). The name may be a
        /// function call whose value is the name ("$wobjectgetname[wobjectgetplayer[]]"), and
        /// "$name[1:3]" is a slice. "$name = value" assigns (not in the reference, but UB has
        /// it and real metas use it ~500 times): the value is everything up to &amp;&amp;, ||, ';' or
        /// the end, so "$x=listfilter[...] &amp;&amp; listcount[$x]>0" sets x to the list.
        /// </summary>
        private string ParseVariable()
        {
            char sigil = _s[_p++];
            int ns = _p;
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
            if (_p == ns) return "";                                  // a stray sigil: as before
            string name = _s.Substring(ns, _p - ns);
            string? slice = null;
            if (_p < _s.Length && _s[_p] == '[')
            {
                int close = FindClose(_s, _p, '[', ']');
                string inner = _s.Substring(_p + 1, Math.Max(0, close - _p - 2));
                if (TopLevelColon(inner) >= 0) slice = inner;
                else name = _skip > 0 ? "" : _eng.EvaluatePrimary(_s.Substring(ns, close - ns));
                _p = close;
            }

            if (slice == null)
            {
                int save = _p;
                Ws();
                if (_p < _s.Length && _s[_p] == '=' && (_p + 1 >= _s.Length || _s[_p + 1] != '='))
                {
                    _p++;
                    string value = ParseBitOr();
                    return _skip > 0 ? value : _eng.WriteVar(sigil, name, value);
                }
                _p = save;
            }

            string v = _eng.ReadVar(sigil, name);
            return slice == null || _skip > 0 ? v : _eng.SliceBy(v, slice);
        }

        /// <summary>x{key} after a value: list item, dictionary value or character; x{a:b} slices.
        /// Repeats: "$commands{$name}{params}".</summary>
        private string ParseIndexes(string value)
        {
            while (_p < _s.Length && _s[_p] == '{')
            {
                int close = FindClose(_s, _p, '{', '}');
                string inner = _s.Substring(_p + 1, Math.Max(0, close - _p - 2));
                _p = close;
                if (_skip > 0) continue;
                value = TopLevelColon(inner) >= 0
                    ? _eng.SliceBy(value, inner)
                    : _eng.IndexValue(value, _eng.Evaluate(inner));
            }
            return value;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

        private void ReadWord(System.Text.StringBuilder sb, ref bool escaped)
        {
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (IsWordChar(c)) { sb.Append(c); _p++; }
                else if (c == '\\' && _p + 1 < _s.Length) { sb.Append(_s[_p + 1]); _p += 2; escaped = true; }
                else break;
            }
        }

        /// <summary>
        /// Appends the words that follow the one just read, with the spaces between them.
        /// Spaces before a '+' are kept too ("\/vt nav load +getpvar[HuntNav]" is the text
        /// "/vt nav load " joined to the nav name); other leading and trailing spaces are not.
        /// A word that is a function name ("text func[...]") is not taken.
        /// </summary>
        private bool ReadMoreWords(System.Text.StringBuilder sb, ref bool escaped)
        {
            bool any = false;
            while (true)
            {
                int q = _p;
                while (q < _s.Length && (_s[q] == ' ' || _s[q] == '\t')) q++;
                if (q == _p || q >= _s.Length) break;
                char n = _s[q];
                if (n == '+') { sb.Append(_s, _p, q - _p); _p = q; break; }
                if (!IsWordChar(n) && n != '\\') break;

                int save = _p, saveLen = sb.Length;
                sb.Append(_s, _p, q - _p);
                _p = q;
                bool e2 = false;
                ReadWord(sb, ref e2);
                if (!e2 && _p < _s.Length && _s[_p] == '[') { _p = save; sb.Length = saveLen; break; }
                escaped |= e2;
                any = true;
            }
            return any;
        }
    }
}
