# RynthCore.RynthAiTests

A console test runner for RynthAi's pure logic. It needs no game, no engine and no host.
It follows the house style of the other test tools here (`RynthCore.LootSdkTests`,
`RynthCore.AutoVendorTests`, `RynthCore.MissileArcTests`): a plain net10.0 exe, no test
framework, and exit code 0 when everything passes.

```
dotnet run -c Release                  # all tests
dotnet run -c Release -- weakness      # only tests whose name contains "weakness"
```

Each test prints one line:

| Line      | Meaning |
|-----------|---------|
| `[PASS]`  | Every check passed. |
| `[FAIL]`  | At least one check failed, the test threw, or it made no checks. Each failed check is listed under the line. The run exits with code 1. |
| `[KNOWN]` | The test was registered with `Runner.KnownFailure(name, reason, body)` because it exposes a real bug that is not fixed yet. It still fails, but the run does not. |
| `[XPASS]` | A known-failure test now passes. The run fails so that the marker gets removed along with the fix. |

The run also exits with code 1 when no test runs (an empty registry or a filter that matches nothing).

Layout:

- `Harness/Check.cs`: the assertion helper (`True`, `False`, `Eq`, `Near`, `Null`, `NotNull`, `Throws`). A failed check is recorded and the test keeps going, so one run shows every broken expectation.
- `Harness/Runner.cs`: registers tests, prints PASS/FAIL, handles known failures, sets the exit code.
- `Tests/*.cs`: one static class per area. Each has a `Register(Runner)` method, which `Program.cs` calls.
- `Fakes/`: stand-ins for plugin classes that a linked file mentions but that are not linked.

To add a test: write a `static void` method that calls `Check.*`, then register it in that
area's `Register` method. For a new area, add a class and call its `Register` from
`Program.cs`. When the code under test lives in a new source file, add a `<Compile Include
... Link=...>` for it to the csproj.

Current smoke tests (13 tests, 142 checks, all passing on 2026-09-29):

- **nav** (`LegacyUi/NavRouteParser.cs`): the trailer of every waypoint type, the EW-before-NS order, a write/read round trip, the fix that converts a Z stored in metres, Follow routes, and stopping with a warning at an unknown type instead of desyncing.
- **stall** (`Combat/CombatStallWatchdog.cs`): the whole 4-step ladder with its 6 s / 3 s / 30 s timings, recovery, the tick-gap reset, and the ineligible case.
- **weakness** (`CreatureData/CreatureWeakness.cs`): element aliases, server-table ranking (with a check that the wcid and the name agree), creature-type keywords (whole words only), and a learned element going first. Assertions that depend on the data are computed from the embedded tables, so regenerating `creature_resists.tsv` / `creature_types.txt` does not break them.

**Second runner: `Tools/RynthCore.RynthAiHostTests`** (added 2026-09-30). Loot rule matching
and metas are host-bound, so they are tested there, through the project-reference route
described below (net10.0-windows, win-x86, InternalsVisibleTo). It links this runner's
`Harness/` files, so both print the same PASS / FAIL / KNOWN / XPASS lines. Its
`Fakes/FakeHost.cs` fills a `RynthCoreApiNative` with `[UnmanagedCallersOnly]` fakes, so a
test can set skills, level, item spells, vitals, position, enchantments and so on. Its
`Program.cs` points the pvar/gvar/ItemGiver paths at a scratch folder before any test runs
(the seam is `ExpressionEngine.PvarsDir` / `GvarsPath` / `ItemGiverDir`); `MetaManager.Clock`
and `ExpressionEngine.TickMs` make the timers testable without sleeping.

**Both runners in one go: `Tools\Run-RynthAiTests.ps1`** (added 2026-09-30, `-Filter text`
optional). It builds and runs both and exits 1 only on FAIL or XPASS (or a build error).

Added 2026-09-30 (nav, buffs, combat numbers):

- here: `Tests/NavFormatTests.cs` reads the hand-written `.nav` files in `Fixtures/nav` (every route type and waypoint kind, malformed files).
- host runner: `NavEngineTests` (route walking on a fake clock: advancing, pause/chat/jump/recall points, the closest-point start, the 160 yd off-track rule, the rejoin choice, the different-area hold and its decision), `BuffTests` (tier by buffed skill, the spell every buff resolves to at every tier checked against `SpellData.txt`, timer lengths, rebuff timing), `CombatMathTests` (damage text, element words, weapon choice by weakness, the target scan's range ring). Seams: `NavigationEngine.Clock`, `INavRecoveryPlanner`, `NavRecoveryPlanner.ClassifyArea`, and a few private helpers in `BuffManager` / `CombatManager` made internal.

## How the runner reaches RynthAi's code

**Decision: link individual source files (`<Compile Include ... Link=...>`), the same way
the existing test tools do.** The alternative was checked and works, but only with
conditions attached.

- **Project reference from a plain net10.0 exe: not possible.** The plugin targets `net10.0-windows`, so restore fails with `NU1201: Project RynthCore.Plugin.RynthAi is not compatible with net10.0`. RynthCore.PluginSdk and PluginCore are also `net10.0-windows`.
- **Project reference from a `net10.0-windows` exe with `PlatformTarget=x86` and `RuntimeIdentifier=win-x86`: works.** This was tried in a scratch project on 2026-09-29. NativeAOT does not get in the way: `PublishAot` and `NativeLib` only apply when publishing, a normal build produces an ordinary IL dll, and the `[UnmanagedCallersOnly]` exports are just static methods nobody calls. The exe has to be x86, because the plugin dll is marked 32-bit and a 64-bit process would throw BadImageFormatException. That requires the x86 .NET runtime, which this PC has (10.0.7 under `C:\Program Files (x86)\dotnet`). The costs: the whole plugin compiles first (about 60 s and 100+ warnings); ImGui.NET comes along; and most classes are `internal`, so tests need `[assembly: InternalsVisibleTo("...")]` in the plugin, which is an allowed seam. `default(RynthCoreHost)` works as a null host: every `Has*` property is false and every call does nothing. With that host, the scratch probe (using reflection instead of InternalsVisibleTo) got these results:
  - `ExpressionEngine.Evaluate`: `1+2*3` gave 7, `setvar[x,5]` then `$x*2` gave 10, `listcount[listcreate[a,b,c]]` gave 3, and `iif[1>2,yes,no]` gave no.
  - `LootEvaluator.Classify`: matched a `StringValue(Name)` rule against a `WorldObject` whose `ItemPropertyOverlay` was set and which had no cache.
  - `MetaManager.Think()`: a `Default` state `Always` -> `SetMetaState Hunt` rule moved `CurrentState` to `Hunt`.

**Recommendation:** keep this runner link-based and fast. For any area below, first check
whether a small seam (a pure helper moved into its own file with no dependencies) lets
the code link. If many host-bound classes need testing, add a second runner (for example
`RynthCore.RynthAiHostTests`: net10.0-windows, win-x86, a ProjectReference to the plugin,
and InternalsVisibleTo) rather than turning this one into it.

**Safety note for any runner:** some RynthAi paths are hard-coded to the live install.
`ExpressionEngine` has `PvarsDir`, `GvarsPath` and `ItemGiverDir` under
`C:\Games\RynthSuite\RynthAi`, and `FlushVars` writes pvars and gvars there.
`CreatureProfileStore.Folder` and the `MonsterDamageStore` files are also under `C:\Games`.
Tests must not call `setpvar`, `setgvar` or `touchpvar`, save stores, or run meta rules that
do, until those paths can be redirected. A redirect seam would be one internal settable
root, defaulting to the current path. This runner's `Fakes/CreatureProfileStore.cs`
points the creature override folder at a folder that does not exist, for exactly this
reason.

## Seams, by area

"Linkable" means the file compiles into this plain net10.0 project as it is, with at most
a LootSdk ProjectReference and `GlobalUsings.cs`. "Host runner" means it is reachable today
only through the project-reference route above.

### 1. Loot rule matching

- **`Loot/LootEvaluator.cs`**: `Evaluate`, `Matches` and `Classify` are public static and pure. Their only input they don't own is `WorldObject` (`Combat/AcStubs.cs`). `WorldObject` can already be fed without a cache: set its internal `Overlay` to an `ItemPropertyOverlay` with `LiveFallback = false` and fill in `Ints`, `Strings` and `Doubles`.
  - **What blocks linking:** `AcStubs.cs` also contains `CharacterSkills`, which needs `RynthCoreHost` and therefore PluginSdk, which is net10.0-windows. It also contains `SpellTableStub`, which needs `SpellDatabase`. And `WorldObject.Cache` is typed `WorldObjectCache` (1843 lines, host-bound).
  - **Seam (moves only, behaviour-neutral):** put `WorldObject`, `ItemPropertyOverlay` and the `LongValueKey` / `StringValueKey` / `DoubleValueKey` enums in their own file. Have `WorldObject` read through a narrow interface that `WorldObjectCache` implements: `GetIntProperty`, `GetStringProperty`, `GetDoubleProperty`, `GetContainerId`, `GetWielderId` and `GetWieldedLocation`. Then `LootEvaluator.cs` and that file link on their own. `CharacterSkills` is only used by `CharacterSkillGECondition`, and `null` skills already mean "pass".
  - **Worth pinning:**
    - The first matching rule wins, and a disabled rule never matches.
    - `Classify` returns `(Sell, null)` when nothing matches, and callers look at the null rule, not the action.
    - Unknown condition types pass (`_ => true`).
    - A `StringValue(Name)` condition falls back to `WorldObject.Name` (the 2026-09-28 healing-kit fix).
    - `MinDamage` is computed as `max - variance * max`.
    - Total ratings are summed over keys 370-376 and 379.
- **`Loot/VTankLootEvaluator.cs`**: `Match(rule, item, ctx: null)` is pure when `ctx` is null, because character and spell checks then pass. The file also contains `VTankLootContext`, which is host-bound. **Seam:** move `VTankLootContext` to its own file, and give the evaluator an interface for the five lookups it uses: skill, level, empty pack slots, spell ids and palettes. It also uses `Meta/RegexCache.cs`, which links.
- `Vendor/AutoVendorRules.cs` and `AutoVendorPlanner.cs` are already covered by `RynthCore.AutoVendorTests`.

### 2. Meta: conditions, expressions, state transitions

- **.af parsing and writing: linkable now.** `Meta/AfFileParser.cs`, `AfFileWriter.cs`, `MetaSchema.cs`, `LoadedMeta.cs`, `RegexCache.cs`, `LegacyUi/LegacyUiSettings.cs` (which holds `MetaRule` and the condition/action enums), `LegacyUi/NavRouteParser.cs` and `GlobalUsings.cs`, plus a LootSdk reference, compile and run in a plain net10.0 exe. This was verified in a scratch project on 2026-09-29. **Next tests:**
  - Parse, write and parse again, and compare the results.
  - Check that every `MetaConditionType` and `MetaActionType` has exactly one keyword in `MetaSchema`, in both directions. This guards against the "SecsOnSpell _LE -> _GE" loss the schema comment mentions.
  - Check that embedded nav routes use `NavRouteParser.TrailerLineCount`.
  - `MetFileParser.cs` (.met) looks similar (it depends only on LegacyUi) but was not tried.
- **Expression evaluation (`Meta/ExpressionEngine.cs`, about 3900 lines): host runner.** It needs `RynthCoreHost`, `WorldObjectCache`, `FellowshipTracker`, `QuestTracker`, `LegacyUiSettings` and the loot types. It works with a null host (see above), but mind the pvar/gvar paths. `ToBool` is already `internal static` and pure. **Seam for linking (larger):** the nested `Parser` (operator precedence, `||` down to unary), together with the private static list, dict and string helpers (`EvalListAdd`, `EvalListRemove`, `NewList`, `EvalCstrf`, `EvalIif`, `TryParseCoordObject` and the rest), could move into a dependency-free `ExpressionCore.cs` that calls back into the engine for function calls and variables. **Worth pinning:**
  - Operator precedence, including the single-character disambiguation of `&`, `|`, `<` and `>`.
  - Numeric versus string coercion per operator.
  - The recursion caps: `MaxEvalDepth` 64 and `MaxParenDepth` 256, which exist to prevent an uncatchable StackOverflow in acclient.
  - List handles.
  - Found by the probe, not investigated: `getregexmatch[abc123,[0-9]+]` returned an empty string. It may be argument splitting on the brackets, and deserves a test.
- **State transitions (`Meta/MetaManager.cs`): host runner.** It can be built from `new LegacyUiSettings()`, `default(RynthCoreHost)` and `new PlayerVitalsCache()`, and `Think()` runs. **Seam:** it reads `DateTime.Now` directly (seconds in state, the watchdog, the 1 s chat window), so injecting a `Func<DateTime>` clock (internal, defaulting to `DateTime.Now`) would make timing rules testable without sleeping. `EvaluateCondition` is private. It becomes testable through `Think()` once the class can be constructed, or directly with InternalsVisibleTo if made internal. **Worth pinning:**
  - `HasFired` latches until `ForceStateReset`.
  - A state change stops the rest of that tick's rules.
  - The `CallMetaState` / `ReturnFromCall` stack.
  - Disabled rules are never indexed.
  - Composite conditions with no children: `All` gives true, `Any` gives **true**, `Not` gives false. The `Any` case is surprising and should be compared with VTank before anyone relies on it.
  - Vital conditions read the public `PlayerVitalsCache`.

### 3. Nav: route parsing, rejoin, closest point, 159 yd, different area

- **Parsing:** `LegacyUi/NavRouteParser.cs` is linked, and the tests above cover it.
- **Rejoin, closest point, off track and area** all live inside `NavigationEngine` (about 2500 lines, instance state, `_host`, `_settings`, `_linearDir`). None of it links today. The pure parts are:
  - `PickRejoinIndex(route, idx, ns, ew, out skipped)`: the nearest of the target and up to 8 waypoints after it (`RejoinLookaheadPts`), never more than 20 yd of route ahead (`RejoinLookaheadYards`), stopping at any point that is not walkable, with 0.5 yd of hysteresis.
  - `DistanceToSegmentYd`, `PrevIndex` and `NextInOrder`: pure given the route type and `_linearDir`.
  - `NavYd` and `IsWalkTarget`: already private static.
  - The closest-point start in `ResumeFromNearestWaypoint`: an inline loop over plain waypoints. It starts from the current index on a Once route. Note that `MetaManager.StartIndexForRoute` / `FindNearestWaypoint` is a **second, different** rule: a Once route there always starts at 0. Pin both.
  - The closest-approach sweep: `_prevDist < ArrivalYards * SweepMult && dist > _prevDist + 0.3`, inline in the tick.
  - Off track, the "159 yd" rule: `OffTrackYards = max(160, NavOffTrackYards)` (the default is 170). `CheckOffTrack` arms when within the limit, is disarmed by every `Stop()` (combat, loot, buff, doors, metas), waits a 5 s grace and starts one recovery per excursion.
  - Area-grace overshoot: walks 4 yd on through the last waypoint when within 10 yd of it, for 6 s.
- **Seam (a pure move):** a dependency-free `NavRouteMath` static class holding `NavYd`, `IsWalkTarget`, `PrevIndex` / `NextInOrder(routeType, count, cur, linearDir)`, `DistanceToSegmentYd`, `PickRejoinIndex` and `NearestPlainWaypoint(route, from, ns, ew)`. `NavigationEngine` would call it. A small `OffTrackGate` class in the style of `CombatStallWatchdog` (`Tick(nowMs, offYd)` returns whether to start recovery, plus `Disarm()`) would carry the arming and grace logic. **Next tests:**
  - 159 yd off never triggers.
  - 161 yd triggers once, after 5 s.
  - A pause disarms it, and coming back within the limit re-arms it.
  - A rejoin never goes backwards and never passes a Portal, Recall or Chat point.
  - A Linear route in reverse.
- **Different area:** `NavRecoveryPlanner.CheckArea` needs dat geometry (`CellDat`), so it is not pure. The decision inputs are pure: the landblock is `cell >> 16`, "indoors" is `(cell & 0xFFFF) >= 0x0100`, and nav-to-landblock math is in `LegacyUi/NavCoordinateHelper.TryConvertPoseToCoords`, which is pure but sits in a file that imports PluginSdk. **Seam:** move `TryConvertPoseToCoords` into its own file. Pull the classification (same dungeon / other dungeon / dungeon versus landscape / unknown) out into a static function that takes `Func<uint,bool> isDungeonLandblock` and a cell-reach callback, and test it with fakes.

### 4. Buff and spell choice

- `SpellManager.ComputeTier` is private instance code over `_charSkills[skill].Buffed` (250 when there are no skills). **Seam:** `static int TierFor(int buffed, int t2..t8)`. Note that the `t1` threshold is never read, and anything below `t2` gives tier 1. Pin that.
- `SpellManager.GetDynamicSelfBuffId` builds candidate names (Incantation of X Self, Aura of ..., the lore names in `LoreNames`, Roman numerals) and checks them against `SpellDictionary` and the known and unresolvable sets. The known-spell refresh needs the host. **Seam:** a static candidate-name generator, `IEnumerable<string> CandidateNames(baseName, tier)`, testable without a spellbook.
- `BuffManager`: `SkillForBuff`, `IsItemEnchantment` and `GetSpellLevel(SpellInfo)` are private static and pure, but `BuffManager.cs` (2800+ lines) does not link. **Seam:** move them into a small `BuffNames.cs`. **Worth probing (not confirmed as bugs):**
  - `GetSpellLevel` tests `Contains(" V")` after `" VI"`, so a name with no numeral and a word starting with V (for example "... Vulnerability ...") would read as level 5.
  - `" I "` has a similar risk.
  - Impregnability must map to Creature Enchantment and Impenetrability to Item Enchantment, as the comment in `SkillForBuff` insists.
- The `CharacterSkills` zero-artifact rule (`AcStubs.cs`): a success with buffed 0 for a trained skill, or for one that read a real value before, is ignored. **Seam:** `static bool IsZeroArtifact(buffed, training, hadGood, prevBuffed)`.
- `Combat/CastGateWatchdog.cs` depends only on System and is **linkable now**.
- `Combat/PetChoice.cs`: `Order`, `ElementFromName` and `NormalizeElement` are pure, but the file imports PluginSdk for `ElementOf(host, ...)`. With a null host, `ElementOf` falls back to the name. **Seam:** move `ElementOf` out, or use the host runner. **Worth probing:** `ElementFromName` matches substrings, so "Ice" hits any name containing "ice" (Justice, Price, Vice).

### 5. Other pure, valuable logic

- **Weapon weakness selection:** the data side, `CreatureData/CreatureWeakness.cs`, is linked and tested. The choice itself is inline in `CombatManager` (around lines 2930-2960). It tries, in order: the Monsters rule's element, then the weakness order filtered to weapons of the same `WeaponKind` as the first usable Items entry (so a bow character with a buff wand listed is not switched to magic), then the Damage tab's learned best weapon. **Seam:** `static int PickWeapon(items, ranking, Func<int,bool> usable, Func<int,int> kind, ...)` returning the id and the reason.
- **Damage-line parsing:** `CombatManager.TryParseOutgoingDamage` and `WordAt` are private static, allocation-free and pure. They handle ACE lines ("for N points with <spell>") and retail lines ("points of X damage"), and reject incoming damage and vital drains. `ElementFromWord` and `ElementFromDamageType` are similar. This is an ideal table-test target. **Seam:** move them into a `CombatText.cs` (a pure move).
- **Already covered elsewhere:** `Raycasting/MissileBallistics.cs` (MissileArcTests) and the AutoVendor rules and planner (AutoVendorTests).
- **Linkable now, no tests yet:**
  - `Combat/CastGateWatchdog.cs`.
  - `Combat/AmmoRecipes.cs` + `AmmoRecipes.Data.cs`: System and LINQ only.
  - `Meta/RegexCache.cs` and `Meta/MetaSchema.cs`.
- **Needs a clock seam:** `Combat/WeaponSwapGate.cs` reads `Environment.TickCount64` directly. An internal `Func<long>` clock would make the 3 s gate testable without sleeping.
- **Host runner:** `Combat/MonsterMatchEvaluator.cs` (the vTank monster-match expressions `range>5 && species==drudge`). The parser is pure, but variables come from `WorldObjectCache`. Not tried.
