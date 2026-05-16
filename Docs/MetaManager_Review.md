# Meta System Full Review

**Reviewer:** Claude (Opus 4.7)
**Date:** May 2026
**Scope:** The whole `Plugins/RynthCore.Plugin.RynthAi/Meta/` subsystem end to end —
`MetaManager.cs` (969 LOC), `ExpressionEngine.cs` (3426 LOC), `MetFileParser.cs`
(1006 LOC), `AfFileParser.cs` (1018 LOC), `AfFileWriter.cs` (469 LOC),
`QuestTracker.cs`, `PropertyNames.cs`, `LoadedMeta.cs` — plus **both** meta UIs
(`RynthCore.Engine/UI/Panels/MetaPanel.cs` 1223 LOC, plugin-side
`LegacyUi/LegacyMetaUi.cs` 1223 LOC) and the JSON bridge between them
(`PluginExports.cs` meta exports, `LegacyDashboardRenderer.cs`
`BuildMetaJson`/`HandleMetaCommand`). This is the first review of the meta
system; it goes to the same depth as the CombatManager and SalvageManager
reviews.

The meta system is the VTank-compatibility layer: it loads `.met` (VTank binary)
and `.af` (RynthAi's own text format) macros, runs a state machine of
condition→action rules every tick, and ships a ~250-function string expression
language. It is the largest single subsystem in RynthAi and the least observed.

---

## TL;DR — three categories

**Category 1: Real bugs and crash risks.**

- **No recursion/eval-depth guard in `ExpressionEngine`.** A self-referential or
  deeply-nested expression stack-overflows. A .NET `StackOverflowException` is
  uncatchable and instantly kills the process — i.e. crashes `acclient.exe`.
  Highest-severity item; see §2.1.
- **`_settings.MetaRules` is mutated cross-thread with no lock.** The Avalonia
  `MetaPanel` reads/writes it on the dispatcher thread while `MetaManager.Think`
  enumerates it on the game thread every tick. `List<T>` is not thread-safe.
  This is a live data race in the *current dev mode* (Avalonia-only). See §2.2.
- **Expression errors evaluate as `true`.** `Evaluate` turns any exception into
  the string `"ERR:…"`, and `ToBool("ERR:…")` is `true`. A typo'd safety
  condition silently becomes "always fire." See §2.3.
- **`delayexec` runs game-state expressions on a threadpool thread**, against AC
  native memory, via a `static` timer list that survives hot-reload. See §2.4.
- **Shipped debug instrumentation writes to a hardcoded foreign path**
  (`C:\Users\tboha\Desktop\AfParser.log`) on every `.af` load. See §2.5.
- **`.af` round-trip is lossy and silent**: `TimeLeftOnSpell_LE`→`_GE`, typed
  vital conditions → `Expr`, `.met`→`.af` auto-fork. See §2.6.
- **`PropertyNames` is mis-indexed and its doc-comment says the opposite.** See §2.7.
- **Parsers fail silently** — bad meta, no rules, zero diagnostics. See §2.8.

**Category 2: Architectural smells that will become bugs.**

- `Think()` is invoked from **5 copy-pasted call sites** in the plugin tick — the
  same "called from 3 places" smell the CombatManager review flagged, grown.
- One condition/action set has **≥5 hand-synchronized representations** (enum,
  `.met` map, `.af` keyword maps, two UI label arrays). In sync today, nothing
  enforces it.
- Meta is **yet another writer of the contested `BotAction`/state signal**,
  applied a tick after the Activity Arbiter decided.
- `_lastChatMatch` is shared mutable capture state — cross-rule contamination.
- UI rule commands are **index-based over a 2-second-stale snapshot** (TOCTOU).

**Category 3: Inefficiency and observability gaps.**

- Per-tick linear scan of all rules; regex recompiled every evaluation.
- pvars/gvars do a full-file rewrite on **every** set, on the game thread.
- **No `GetStateSnapshot()` / metrics** anywhere in the meta system. You cannot
  see current state, stack depth, last-fired rule, or eval errors. Same gap that
  made every other manager's bugs invisible.

Most of Category 1 is small to fix individually. Two items (§2.1 recursion guard,
§2.2 the data race) are crash-class and should jump the queue regardless of how
the rest is prioritized — they tie directly to the open AC-crash investigation.

---

## 1. What the system does well (keep these)

- **The `.af` text format + Source view.** Human-readable, diffable,
  round-trippable, with embedded navs inline. This is a genuinely good design and
  a real improvement over VTank's opaque binary `.met`. The Source↔Visual toggle
  in both UIs is the right idea.
- **The `.met` byte-stream reader.** `MetFileParser.MetReader`
  (`MetFileParser.cs:87-158`) correctly handles VTank `ba` fixed-size byte
  sections that can end mid-line — reading exact byte counts rather than lines.
  That is the genuinely hard part of VTank binary compatibility and it is done
  correctly, including the nav extraction from `ba` blocks.
- **The expression parser.** The precedence-climbing recursive-descent `Parser`
  (`ExpressionEngine.cs:3100-3425`) is clean and correct, including the tricky
  single-vs-multi-char operator disambiguation in `Try` (`:3125-3147`) and
  numeric-vs-string `+` overloading. ~250 functions is a lot of surface and the
  dispatch (`EvaluatePrimary` switch) is well organized.
- **The `HasFired`/`ForceStateReset` discipline.** The comment at
  `MetaManager.cs:198-210` shows the subtle "operational state cycling must not
  re-fire latched rules or EmbedNav reloads from index 0 every tick" bug was
  already discovered and reasoned about. Keep that comment; it is load-bearing.
- **UI/enum alignment is currently correct.** I verified `MetaPanel.ConditionNames`
  and `ActionNames` against the authoritative `MetaConditionType`/`MetaActionType`
  enums (`LegacyUiSettings.cs:588-645`) — all 35 condition and 16 action indices
  line up. The editor's hardcoded special cases (`r.Action == 2 || 5`, `== 3`)
  are also correct. This is fragile (see §3.3) but it is not currently broken.
- **The marshalling buffer handoff.** `GetMetaJson` (`PluginExports.cs:332-348`)
  uses `Interlocked.Exchange` + free-old-pointer — shows awareness that the
  native string crosses a boundary and can't just leak or double-free.
- **`QuestTracker`.** Compact, correct UB-compatible `/myquests` parser with
  sane end-of-list detection (silence timeout + global timeout). No notes.

OK, now the issues.

---

## 2. Real bugs and crash risks

### 2.1 No recursion / evaluation-depth guard — stack overflow kills acclient

`ExpressionEngine` has a field `_evalDepth` (`ExpressionEngine.cs:41`). It is
incremented at `:142` and decremented at `:145`. Its **only** use is at `:141`:

```csharp
if (_evalDepth == 0) { _dicts.Clear(); _nextDictId = 0; }
```

It is never compared against a maximum. Nothing anywhere caps recursion. The
recursive paths are numerous and reachable from user-authored meta:

- `exec[expr]` → `Evaluate(A(0))` (`:462`)
- `iif`/`if`/`ifthen` re-evaluate branch strings (`:167-190`)
- `listfilter`/`listmap`/`listreduce`/`listsort` re-evaluate a template per item
- the parser itself: `ParseOr → … → ParseAtom → EvaluatePrimary → Evaluate →
  new Parser(...).Run()` (`:3415`, `:143`) — one nested `[...]` call is one full
  recursive descent.

A meta rule like `setvar[x, exec[$x]]` followed by `exec[$x]`, or a pathological
nest of parentheses, recurses without bound. A `StackOverflowException` in .NET
**cannot be caught** (the `try/catch` at `:144` will not save you) and
**immediately terminates the process**. In an injected NativeAOT `acclient.exe`
that is an instant client crash with no managed stack — exactly the signature in
the open crash investigation.

**Fix:** a hard depth cap, checked in `Evaluate` before recursing:

```csharp
private const int MaxEvalDepth = 64;

public string Evaluate(string expression)
{
    string expr = expression?.Trim() ?? "";
    if (expr.Length == 0) return "";
    if (_evalDepth >= MaxEvalDepth)
        return "ERR:Depth:max recursion";          // see §2.3 — make this falsey-safe
    if (_evalDepth == 0) { _dicts.Clear(); _nextDictId = 0; }
    _evalDepth++;
    try   { return new Parser(this, expr).Run(); }
    catch (Exception ex) { return $"ERR:{ex.GetType().Name}:{ex.Message}"; }
    finally { _evalDepth--; }
}
```

64 is generous for legitimate metas. Combine with §2.3 so the depth-exceeded
sentinel doesn't itself fire an action.

### 2.2 `_settings.MetaRules` mutated across threads with no synchronization

`_settings.MetaRules` is a plain `List<MetaRule>`. Three independent actors touch
it:

1. **Game thread**, every tick: `MetaManager.Think` does
   `foreach (var rule in _settings.MetaRules)` (`MetaManager.cs:251`).
2. **Avalonia dispatcher thread**, every 2 s: `MetaPanel`'s `DispatcherTimer`
   (`MetaPanel.cs:305`) calls `RynthPluginGetMetaJson` →
   `BuildMetaJson` (`LegacyDashboardRenderer.cs:2262`) which enumerates
   `_settings.MetaRules` *and* calls `AfFileWriter.SaveToString(_settings.MetaRules, …)`.
3. **Avalonia dispatcher thread**, on any user action: `RynthPluginSendMetaCommand`
   → `HandleMetaCommand` (`:2347`) does `.Add`, `[index] =`, `.RemoveAt`, element
   swaps, and `_settings.MetaRules = loaded.Rules` (whole-list replace, `:2396`).

`List<T>` is explicitly not safe for concurrent enumerate + mutate. Best case is
an `InvalidOperationException ("Collection was modified")` thrown out of `Think`
on the game thread (caught by the plugin's outer handler, meta silently stops).
Worst case is a torn read of the backing array during a resize, an
index-out-of-range, or heap corruption — the kind of intermittent,
no-managed-stack crash that matches the multibox-stability and crash-investigation
findings. There is no `lock` anywhere on this path (grep confirms).

The collision window is small (2 s poll) but non-zero, and widens during active
editing. Per `CLAUDE.md`, **Avalonia-only is the current dev mode**, so the
`MetaPanel` timer path is live, not hypothetical. (The ImGui `LegacyMetaUi`
mutates the same list but runs *on the game thread* via EndScene, so it is not
itself a cross-thread hazard — the hazard is specifically the Avalonia bridge.)

**Fix:** a single lock object guarding every read and write of `_settings.MetaRules`
(`Think`'s enumeration, `BuildMetaJson`, every `HandleMetaCommand` case). A
`lock` around the per-tick `foreach` plus snapshot-to-array for the JSON build is
the minimal correct change. Longer term, marshal `HandleMetaCommand` onto the
game thread (post to a queue drained at the top of `Think`) so all rule
mutation happens on one thread — that also fixes §3.5 (TOCTOU) for free.

### 2.3 Expression evaluation errors are truthy

`Evaluate` catches everything and returns a string:

```csharp
catch (Exception ex) { return $"ERR:{ex.GetType().Name}:{ex.Message}"; }   // :144
```

`ToBool` (`:3041`):

```csharp
internal static bool ToBool(string s)
    => s.Length > 0 && s != "0" && !string.Equals(s, "false", ...);
```

`"ERR:RegexParseException:…"` has length > 0, isn't `"0"`, isn't `"false"` → it
is **`true`**. So in `MetaManager.EvaluateCondition`:

```csharp
case MetaConditionType.Expression:
    return !string.IsNullOrEmpty(rule.ConditionData) &&
           ExpressionEngine.ToBool(Expressions.Evaluate(rule.ConditionData));   // :508
```

A condition with a typo, a malformed regex, a missing function, or (post-§2.1) a
depth blowout evaluates to an error string → `ToBool` → `true` → **the rule
fires**. A safety gate like `Expr {getcharvital_current[2] < 100}` that is
supposed to flee at low health, if it throws for any reason, becomes "always
flee" — or, worse, an offensive rule becomes "always attack." The same applies
to the `if[cond, action]` family inside expressions.

**Fix:** treat the error sentinel as falsey at the boolean boundary. Either make
`ToBool` special-case a leading `"ERR:"`, or have `MetaConditionType.Expression`
fail closed:

```csharp
case MetaConditionType.Expression:
{
    if (string.IsNullOrEmpty(rule.ConditionData)) return false;
    string v = Expressions.Evaluate(rule.ConditionData);
    if (v.StartsWith("ERR:", StringComparison.Ordinal))
    {
        if (_settings.MetaDebug)
            _host.WriteToChat($"[Meta] expr error in {rule.State}: {v}", 1);
        return false;                       // fail closed, and surface it
    }
    return ExpressionEngine.ToBool(v);
}
```

This also gives you the first real error visibility in the whole system (§3.4).

### 2.4 `delayexec` runs on a threadpool thread against AC native memory

`EvalDelayExec` (`ExpressionEngine.cs:2896-2931`):

```csharp
t = new System.Threading.Timer(_ =>
{
    try { engine.Evaluate(expr); }   // :2910 — threadpool thread
    catch { }
    ...
}, null, ms, Timeout.Infinite);
lock (_timerLock) _activeTimers.Add((t, addedAt));
```

The timer callback fires on a **threadpool thread**, not the game thread, and
calls `Evaluate` — which reaches `_worldObjectCache`, `_host` native function
pointers, etc. Those read AC client memory and were only ever meant to be touched
from the game thread. Off-thread native access in an injected NativeAOT client is
the classic AV/race signature documented in the NativeAOT-pitfalls memory.

Worse: `_activeTimers` is `static` (`:73`). Static state survives a plugin
hot-reload. A timer scheduled before a reload captures the *old* `engine`
(`:2902`) and the old `_host` native pointers; when it fires after the reload
those pointers are gone → AV into freed memory. The `TimerMaxAge` reaper
(`:2917-2924`) only prunes *other* entries when *some* timer happens to fire — a
lone stale timer past a reload still fires once into the dead engine before
anything reaps it.

**Fix:** don't execute on the timer thread. Enqueue the expression onto a
game-thread work queue drained at the top of `MetaManager.Think` (or the plugin
tick). Make `_activeTimers` an **instance** field disposed in the engine's
teardown so a hot-reload cancels its own pending timers. (Cross-ref: the
out-of-process-tooling/isolation preference applies — anything that can run on a
deterministic game-thread pump should, not a free-running `Timer`.)

### 2.5 Leftover foreign debug-log path written on every `.af` load

`AfFileParser.cs:78-79`:

```csharp
string dbgLog = @"C:\Users\tboha\Desktop\AfParser.log";
try { File.AppendAllText(dbgLog, $"\n=== {DateTime.Now:HH:mm:ss} Load {filePath} ..."); } catch { }
```

`tboha` is not this machine's user. This is debug instrumentation that was never
removed: it `File.AppendAllText`s on every `Load`, again per NAV section
(`:122-123`, `:132`, `:136`), and on every parse exception (`:140-141`). It is
`try`-wrapped so it can't crash, but: (a) it is the *only* place `.af` parse
exceptions are recorded, so on any other machine `.af` parse failures vanish
entirely (and `LoadFromText` passes `dbgLog = null` — see §2.8); (b) shipping a
hardcoded foreign desktop path in production is wrong on its face.

**Fix:** delete the `dbgLog` plumbing and route the parse-exception and
nav-count diagnostics through `_host.Log(...)` to the unified
`RynthCore.log` like everything else.

### 2.6 `.af` round-trip is lossy and silent

Three independent losses, all silent:

1. **`TimeLeftOnSpell_LE` → `TimeLeftOnSpell_GE`.** `AfFileWriter.cs:41-42` maps
   *both* `_GE` and `_LE` to the keyword `SecsOnSpellGE`; `AfFileParser`'s
   `ConditionKeywords` only has `SecsOnSpellGE` → `TimeLeftOnSpell_GE`
   (`AfFileParser.cs:42`). Load a meta with a "spell about to expire"
   (`_LE`) rule, save it, reload → it is now `_GE` and the rule's meaning is
   inverted.
2. **Typed vital conditions → `Expr`.** `MainHealthLE`, `MainHealthPHE`,
   `MainManaLE`, `MainManaPHE`, `MainStamLE`, `VitaePHE` are written as
   `Expr {getcharvital_current[...]...}` (`AfFileWriter.cs:147-164`) and reload
   as `MetaConditionType.Expression`. They are *semantically* equivalent, but the
   typed forms hit a cheap direct `_vitals` read in `MetaManager.EvaluateCondition`
   (`:304-334`) while `Expression` runs the full expression parser **every tick
   for every such rule**. Round-tripping silently converts a near-free check into
   a per-tick parse. It also means half of `MetaConditionType` (the
   `MainHealthLE…VitaePHE` block) is **unreachable through the `.af` authoring
   path** — only `.met` import or in-memory construction can produce them.
3. **`.met` → `.af` auto-fork.** `TryAutoSaveMetaCmd`
   (`LegacyDashboardRenderer.cs:2455-2469`) silently rewrites a loaded `.met`
   path to `.af`, repoints `_settings.CurrentMetaPath`, and saves there on the
   first edit. The user opened `Foo.met`; after one panel click they are
   editing `Foo.af` (with losses 1 and 2 baked in) and the original `.met` is
   abandoned with no message.

**Fix:** give `.af` a distinct keyword for `_LE` (e.g. `SecsOnSpellLE`) and a
real round-trip for the typed vital conditions (dedicated keywords, or keep them
as `Expr` but *parse them back* into the typed enum when the expression matches
the known template). At minimum, surface the `.met`→`.af` fork to the user
(chat line: "converted Foo.met → Foo.af for editing").

### 2.7 `PropertyNames` is mis-indexed and its doc-comment claims otherwise

`PropertyNames.cs:3-8`:

```csharp
/// AC property enum names from STypes.cs — sequential from 0, no gaps.
```

It is not. `SalvageManager.cs:229-231` already documents the workaround:

> *"The PropertyNames.IntNames index in this codebase is off-by-some-rows so do
> NOT use it as the source of truth."*

Spot check: AC `STypeInt.MaxStructure` is `91`, but `IntNames[91]` is not
`MaxStructure` (the table places `MaxStructure` around index 86 — the rows are
missing entries relative to the real `STypes` enum). So `dumpprops` and anything
else using `GetIntName` mislabels properties. Separately, `GetIntName`/`GetBoolName`/
`GetStringName` are declared `string` but `return … : null` (`:138-145`) — a
nullable contract violation that will surface as a warning or an NRE in a
consumer that trusts the signature.

**Fix:** regenerate the three tables from the actual `STypes` enum (with the
gaps), correct the doc-comment, and change the return type to `string?`. Then the
inlined-constant workaround in `SalvageManager` can be removed and future code
can trust the table.

### 2.8 Parsers fail silently — bad meta, nothing happens, no diagnostic

- `MetFileParser.Load` wraps the whole parse in `catch { result.Rules = new(); … }`
  (`MetFileParser.cs:171-180`) — any malformed `.met` yields zero rules with no
  log line.
- `AfFileParser.ParseLines` only records exceptions to the foreign debug path of
  §2.5; `LoadFromText` (the Source-view Apply path) calls it with `dbgLog = null`
  (`AfFileParser.cs:90`) so a syntactically broken meta typed into the Source box
  is **completely silently discarded**.
- Unknown condition/action keywords degrade to `Never`/`None`
  (`AfFileParser.cs:417`, `:527`). A single typo (`Allways`, `ChatComand`)
  silently disables that rule — and since `Never` never fires and the state may
  have no other escape, a typo can dead-end a whole state with no feedback.

The user-facing symptom is exactly the original complaint that started this
review series elsewhere: "I loaded it and the bot just stands there." Here it
would be "I loaded the meta and nothing happens," with no way to tell whether the
file failed to parse, parsed to zero rules, or parsed fine but every rule's
keyword was wrong.

**Fix:** every parser returns (or logs) a structured result: rule count,
nav count, and a list of `(line, problem)`. Surface "loaded N rules, M states,
K warnings" on load via `_host.WriteToChat`, and list unknown-keyword lines.
This is the single highest-leverage observability change in the system.

---

## 3. Architecture

### 3.1 `Think()` is called from five copy-pasted sites

`RynthAiPlugin.OnTick` calls `_metaManager?.Think()` immediately before five
separate `return`s: the Buffing block (`RynthAiPlugin.cs:430`), the
missile-crafting block (`:493`), the BoostNav block (`:511`), the normal
end-of-tick (`:591`), and the priority cascade. The intent is "Meta always gets
to think regardless of which exclusive activity ran this tick" — correct intent,
wrong mechanism. The CombatManager review already flagged this as
*"MetaManager.Think called three times … any new early return will silently skip
Meta."* It is now five. Any future early-return that omits the line silently
disables meta in that mode, and nobody will notice because there is no
observability (§3.4).

This is the same finding as CombatManager review §3: meta should be a
**Background activity** in a scheduler that always runs after the exclusive
activity, exactly once, by construction — not five hand-placed calls.

### 3.2 Meta is a second writer of the contested `BotAction`/state signal

`TryHandleVtCommand`/`TrySetVtOption` (`MetaManager.cs:916-941`) maps VTank
options through `VtOptionMap` → `Expressions.BuildSettingsMapPublic()` →
`entry.Set(value)`, mutating `EnableCombat`, `EnableNavigation`, `CurrentState`,
etc. `SetMetaState`/`CallMetaState`/`EmbeddedNavRoute` actions mutate
`_settings.CurrentState`/`CurrentRoute`/`EnableNavigation` directly
(`:619-717`). Because `Think()` runs *after* the operational `BotAction` is
decided each tick (§3.1), every meta-driven state change lands one tick late and
competes with the Activity Arbiter for the same `BotAction`/settings fields the
arbiter rewrite was created to make single-owner. This is precisely the
distributed-writer pathology the Activity Arbiter project is fixing — meta is a
~20th writer that the arbiter plan does not yet account for. Meta should become a
declared arbiter input (a high-priority claimant when a meta rule wants to force
a state) rather than a post-hoc settings mutator.

### 3.3 One rule schema, five hand-synchronized representations

The same condition/action vocabulary exists as:

1. `MetaConditionType` / `MetaActionType` enums (`LegacyUiSettings.cs:588-645`) —
   the authority.
2. `.met` integer maps `VTankCTypeMap`/`VTankATypeMap` (`MetFileParser.cs:18-69`).
3. `.af` keyword maps — read side `AfFileParser.ConditionKeywords`/`ActionKeywords`
   and write side `AfFileWriter.ConditionKeywordMap`/`ActionKeywordMap` (two more,
   and they are *not* symmetric — see §2.6).
4. `MetaPanel.ConditionNames`/`ActionNames` (`MetaPanel.cs:67-110`).
5. `LegacyMetaUi._metaConditionNames`/`_metaActionNames` — `MetaPanel.cs:66`
   literally comments *"mirrors LegacyMetaUi"*.

The JSON bridge serializes `(int)r.Condition` (`LegacyDashboardRenderer.cs:2336`)
and `MetaPanel` indexes its label array by that int and sends it back as an int
that `DtoToMetaRule` casts straight to the enum. So the **integer position of
each enum member is a wire contract** across the native boundary *and* the file
formats *and* both UIs. Insert a member in the middle of `MetaConditionType` and
every saved meta, every `.met` mapping offset, and every UI label silently
shifts. They are all correct *today* (verified) — but nothing enforces it: no
test, no single source of truth, no `[Description]`-attributed enum driving the
tables.

**Fix:** drive the label tables and keyword maps from one attributed enum
(reflection at startup, cached) or add a unit/asserting test that fails when the
counts/positions drift. Make the `.af` read/write maps a single bidirectional
table so §2.6's `_LE`/`_GE` asymmetry can't recur.

### 3.4 No observability anywhere in the meta system

`CombatManager.GetStateSnapshot()` exposes ~20 fields; `BuffManager` and the
SalvageManager review both call for the same. The meta system has **none**.
There is no way to see, at runtime: current state, seconds-in-state, the
`_stateStack` (Call/Return) depth, which rule last fired, watchdog
armed/expiry, `_lastChatMatch`, or that an expression errored. `MetaDebug`
(`:262`) only emits a chat line *when a rule fires* — it tells you nothing about
why a state is stuck with nothing firing, which is the actual failure mode.

This is the same gap, in the same shape, as every prior manager review: the bug
is invisible until you add the snapshot. Add `MetaManager.GetStateSnapshot()`
(state, secsInState, stackDepth, lastFiredRule+when, watchdog, ruleCount per
state, lastExprError) and surface it in the diagnostic command and the meta
panel header.

### 3.5 Index-based rule commands over a stale snapshot (TOCTOU)

`HandleMetaCommand`'s `update_rule`/`delete_rule`/`move_up`/`move_down` index
into `_settings.MetaRules` by `cmd.Index` (`LegacyDashboardRenderer.cs:2415-2449`).
That index came from a `MetaPanel` payload built up to 2 s earlier. Between the
poll and the command, a meta rule firing `SetMetaState`, an autoload, the 2 s
re-poll, or a fast second click can change the list. The command then edits or
deletes the wrong rule. `LegacyMetaUi` is safer (`_settings.MetaRules.IndexOf(rule)`
on the live list, `:244`) but pays O(n) per row per ImGui frame (§4). The clean
fix is the same as §2.2: serialize all mutation onto the game thread and address
rules by a stable id, not a positional index.

### 3.6 Single watchdog only

`SetWatchdog` early-returns if one is already armed (`MetaManager.cs:721`,
`if (_watchdogActive) break;`). A VTank meta that arms a movement watchdog and a
separate state-timeout watchdog gets only the first; the second is silently
dropped. Either support a small list of named watchdogs or document the
single-watchdog limitation in the `.af`/command reference so meta authors aren't
surprised.

---

## 4. Performance

- **Per-tick linear scan.** `Think` iterates *all* rules every tick and filters
  by `string.Equals(rule.State, CurrentState, OrdinalIgnoreCase)`
  (`MetaManager.cs:251-254`). For a large meta (hundreds of rules) that is
  hundreds of string comparisons per tick to find the handful in the current
  state. Build a `Dictionary<string, List<MetaRule>>` keyed by state on load and
  on rule edit; iterate only the current state's bucket.
- **Regex recompiled every evaluation.** `ChatMessage`/`ChatMessageCapture`
  (`:349`), `MonsterNameCountWithinDistance` (`:428`),
  `MonsterPriorityCountWithinDistance` (`:561`), the `#` operator
  (`ExpressionEngine.cs:3294`), and every `wobjectfindall*rx` build a fresh
  `Regex` per call with no cache. A per-tick chat/monster rule recompiles its
  pattern 60×/s. Cache compiled `Regex` keyed by pattern string (bounded LRU).
- **pvars/gvars full-file rewrite per set.** `EvalSetPvar`/`EvalSetGvar`/`touch`/
  `clear` call `SaveVarFile` → `File.WriteAllLines` of the entire dictionary on
  *every* call (`ExpressionEngine.cs:528-595`), and `GetPvarPath` calls
  `_host.TryGetObjectName` each time. A meta doing `setpvar` per tick performs a
  synchronous full-file disk write per tick on the game thread → frame hitches.
  Batch writes (dirty flag + flush on a timer / on logout) and cache the path.
- **UI rebuild cost.** `MetaPanel` tears down and rebuilds the entire Avalonia
  visual tree every 2 s (`MetaPanel.cs:294-317`); `LegacyMetaUi` calls
  `_settings.MetaRules.IndexOf(rule)` per row per ImGui frame
  (`LegacyMetaUi.cs:244,268,278`) — O(n²) per frame on a big meta at 60 fps.
  Both are tolerable for small metas and quadratic for large ones.

---

## 5. The two UIs

The user asked specifically about the meta UI panel. There are two, and they are
near-duplicates with different hosts:

- **`MetaPanel.cs`** — engine-side **Avalonia** floating panel (the one in the
  overlay panel list; live in the current Avalonia-only dev mode). Talks to the
  plugin purely over the JSON bridge (`RynthPluginGetMetaJson` /
  `RynthPluginSendMetaCommand`). List/Editor/Source views, state grouping,
  fired-rule red flash, picker overlay. Clean Avalonia code. Its problems are not
  in the panel itself — they are the bridge it sits on: the 2 s poll drives the
  cross-thread race (§2.2) and the stale-index commands (§3.5), and a full
  visual-tree rebuild every 2 s (§4).
- **`LegacyMetaUi.cs`** — plugin-side **ImGui** editor, runs on the game thread
  inside EndScene. Same feature set, its own copy of the label arrays (§3.3),
  mutates `_settings.MetaRules` directly (game-thread, so no cross-thread hazard
  for itself) but with the O(n)-IndexOf-per-row cost (§4).

Structurally this is the same dual-implementation smell as the rest of the
codebase's ImGui↔Avalonia parity work: two 1223-line files implementing the same
editor, kept in sync by hand. Findings that touch both: label-table drift (§3.3),
the lossy Source-view Apply (§2.6/§2.8 — both call `AfFileParser.LoadFromText`
then replace `_settings.MetaRules`), and no observability surface (§3.4). The
Avalonia panel is the one to harden because it is the live path and the one
carrying the crash-class race.

One concrete bridge note: `MetaPanel.TryFetch` reads
`Marshal.PtrToStringAnsi(ptr)` from the pointer `GetMetaJson` returned, but the
*next* `GetMetaJson` call frees that buffer (`PluginExports.cs:339-341`). Today
the same Avalonia dispatcher thread does both, serialized, so it is safe — but it
is one refactor (a second caller, or moving the read off-dispatcher) away from a
use-after-free. Worth a comment pinning the single-caller assumption.

---

## 6. Action plan

### Day 1 — crash-class (do these regardless of broader prioritization)

| # | Item | Effort | Section |
|---|---|---|---|
| 1 | Add `MaxEvalDepth` guard in `Evaluate` | 20min | §2.1 |
| 2 | `lock` around every `_settings.MetaRules` read/write (Think, BuildMetaJson, HandleMetaCommand) | 1-2h | §2.2 |
| 3 | Make `"ERR:"` falsey at the condition boundary + debug-log it | 30min | §2.3 |
| 4 | Delete the `C:\Users\tboha\…` debug path; route to `_host.Log` | 20min | §2.5 |

### Day 2 — correctness + the observability that makes everything else debuggable

| # | Item | Effort | Section |
|---|---|---|---|
| 5 | Parser load result: "loaded N rules / M states / K warnings", list unknown keywords | 2-3h | §2.8 |
| 6 | `MetaManager.GetStateSnapshot()` + surface in diag/panel | 1-2h | §3.4 |
| 7 | `delayexec` → game-thread queue; `_activeTimers` instance-scoped + disposed on reload | 2-3h | §2.4 |
| 8 | `.af` `SecsOnSpellLE` keyword; surface `.met`→`.af` fork to user | 1-2h | §2.6 |

### Week 1 — structural

| # | Item | Effort | Section |
|---|---|---|---|
| 9  | Marshal `HandleMetaCommand` onto the game thread; address rules by id not index | 3-4h | §2.2, §3.5 |
| 10 | Per-state rule index (`Dictionary<string,List<MetaRule>>`) | 2h | §4 |
| 11 | Compiled-regex LRU cache (shared by MetaManager + ExpressionEngine) | 2h | §4 |
| 12 | Batch pvar/gvar writes (dirty + flush); cache pvar path | 2h | §4 |
| 13 | Fix `PropertyNames` tables + `string?` return; drop SalvageManager workaround | 2h | §2.7 |
| 14 | `try/catch` around `Think` body with state-consistent reset | 30min | §3.4 |

### Week 2 — bigger refactors (align with other reviews)

| # | Item | Effort | Section |
|---|---|---|---|
| 15 | Meta as a Background activity in the scheduler (one call site) | 3-4h | §3.1 |
| 16 | Meta as a declared Activity Arbiter input, not a settings mutator | 1-2d | §3.2 |
| 17 | Single attributed-enum source for label tables + `.af`/`.met` maps; add drift test | 1d | §3.3 |
| 18 | Round-trip typed vital conditions through `.af` (or parse `Expr` back to typed) | 1d | §2.6 |
| 19 | Collapse the two 1223-line UIs toward a shared core (parity migration) | 3-5d | §5 |

### Cross-reference

- **CombatManager review §3 (scheduler):** items 15 and the "Think called N
  times" finding are the same problem; do them together.
- **Activity Arbiter plan:** item 16 — meta is an unaccounted-for ~20th writer of
  `BotAction`/state; the arbiter migration must include it or the arbiter's
  single-owner guarantee is incomplete.
- **SalvageManager review §7.1:** item 13 removes the `PropertyNames` workaround
  that review documents.
- **The recurring pattern across all manager reviews:** verify/fail-closed before
  acting (§2.3), expose state through a snapshot (§3.4), unify duplicated
  representations (§3.3, §5). Same lesson, fourth subsystem.

---

## 7. The honest small thing worth doing today

If you do nothing else this week, do these three, in order:

1. **Recursion guard (§2.1)** — 20 minutes, removes an uncatchable
   process-killer that almost certainly contributes to the open AC-crash
   investigation.
2. **Lock `MetaRules` (§2.2)** — the other crash-class item; a single lock object
   threaded through three methods.
3. **Parser load summary + `ERR:` fail-closed (§2.8 + §2.3)** — converts the meta
   system from "silently does nothing and maybe fires the wrong rule" to
   "tells you what it loaded and what broke."

Items 1 and 2 are the only two in this review that can crash the client; they are
small and should not wait behind the structural work. Everything after that is
the same arc as the prior reviews — once the meta system can *report* what it is
doing, the rest of the list becomes verifiable instead of guesswork. Right now
the meta system is the largest unobserved surface in RynthAi, and that, not any
single bug, is the core finding.
