using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Automates missile ammunition management.
///
/// Triggers when ammo slot is empty:
///   1. Can craft better ammo than loose inventory? → Craft (up to 2x), then equip
///   2. Loose ammo available? → Equip best one
///   3. Can craft anything? → Craft, then equip
///
/// Crafting is chat-driven: issues UseObjectOn (ApplyItem), waits for "You make" chat,
/// then advances to the next combine or equips. Retries every 2s if no response.
///
/// Make ammo when low (a players' request, 2026-11-23; MakeAmmoWhenLow, off by default): a top-off
/// between fights, so the empty-slot path above is rarely needed. The plugin (RynthAiPlugin.Ammo.cs)
/// decides when, on the Make kits when low rules (<see cref="Auto"/>, a KitAuto of its own): fewer
/// than MakeAmmoBelow arrows, quarrels or darts for the launcher in hand (quiver and pack), nothing
/// else going, no monster near, no hit for 20 s, in peace mode. <see cref="StartTopOff"/> then makes
/// enough combines of the best recipe the pack and Fletching make reliably (the same choice as the
/// empty-slot path, never a risky one) to reach MakeAmmoUpTo, and wields ammo only if the slot is
/// empty. A fight, a launcher swap or leaving peace mode stops it at once (<see cref="StopTopOff"/>).
/// </summary>
public class MissileCraftingManager
{
    // ── Make ammo when low ──────────────────────────────────────────────
    /// <summary>A plain bundle makes 100, so one combine always lifts you back over this.</summary>
    public const int DefaultAmmoBelow = 100;
    /// <summary>Five plain combines, or one wrapped bundle (1000).</summary>
    public const int DefaultAmmoUpTo = 500;
    public const int MaxAmmoBelow = 5000;
    public const int MaxAmmoUpTo = 10000;
    /// <summary>The most combines one top-off makes.</summary>
    public const int MaxTopOffCombines = 10;

    /// <summary>When a top-off may start, and the waits after one (ticked by RynthAiPlugin.Ammo.cs).</summary>
    internal KitAuto Auto { get; } = new KitAuto { Name = "Make ammo when low", Doing = "make ammo" };
    /// <summary>The chat line for the top-off's own lines (the plugin's ChatLine; null = the host's chat).</summary>
    public Action<string>? Say { get; set; }

    private bool _topOff, _topOffManual;
    private int _topOffTarget, _topOffYield, _topOffFailed;
    private WeaponCategory _topOffCategory;
    private string _topOffOutput = string.Empty;
    private KitStopKind? _topOffEndKind;
    private string _topOffWhy = string.Empty;
    private Func<KitStopKind, int, string>? _topOffEnded;
    private volatile bool _makeNowAsked, _stopAsked;

    /// <summary>A top-off is under way (it holds the bot while it goes).</summary>
    public bool TopOffRunning => _topOff && State != CraftState.Idle;
    /// <summary>The top-off going was asked for (Make ammo now), not started by the low line.</summary>
    public bool TopOffManual => _topOff && _topOffManual;
    /// <summary>Combines made in the top-off going.</summary>
    public int TopOffMade => _topOff ? _combinesCompleted : 0;
    /// <summary>Combines the top-off going plans.</summary>
    public int TopOffPlanned => _topOff ? _totalCombines : 0;
    /// <summary>The last top-off's summary line ("" = none this session).</summary>
    public string LastTopOffSummary { get; private set; } = string.Empty;
    /// <summary>The last look at the launcher and the ammo (for the panels; null = none yet).</summary>
    internal AmmoLook? LastLook { get; private set; }
    /// <summary>Make ammo now: asked until this (plugin clock, ms; 0 = not asked).</summary>
    internal long MakeNowUntilMs { get; set; }

    /// <summary>A panel's Make ammo now (any thread): the plugin takes it on its next tick.</summary>
    public void RequestMakeNow() => _makeNowAsked = true;
    /// <summary>A panel's Stop (any thread): the plugin takes it on its next tick.</summary>
    public void RequestStop() => _stopAsked = true;
    internal bool TakeMakeNowRequest() { if (!_makeNowAsked) return false; _makeNowAsked = false; return true; }
    internal bool TakeStopRequest() { if (!_stopAsked) return false; _stopAsked = false; return true; }

    /// <summary>The launcher in hand and its ammo: what a top-off would make and why not.</summary>
    internal sealed record AmmoLook(bool Launcher, WeaponCategory Category, int Carried, bool Wielded,
                                    AmmoRecipe? Recipe, int Combines, string PackWhy)
    {
        public string CarriedText => AmmoText(Carried, Category);
    }

    /// <summary>"1 arrow", "230 quarrels", "12 darts".</summary>
    public static string AmmoText(int n, WeaponCategory c) => $"{n} {AmmoNoun(c, n)}";

    public static string AmmoNoun(WeaponCategory c, int n = 2) => c switch
    {
        WeaponCategory.Crossbow => n == 1 ? "quarrel" : "quarrels",
        WeaponCategory.Atlatl   => n == 1 ? "dart" : "darts",
        _                       => n == 1 ? "arrow" : "arrows",
    };

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private WorldObjectCache? _objectCache;
    private CharacterSkills? _charSkills;

    public bool IsBusy { get; set; } = false;
    /// <summary>Live combat mode — read from AC each access (1=peace, 2=melee, 4=missile, 8=magic).</summary>
    public int CurrentCombatMode =>
        _host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : CombatMode.NonCombat;

    public void SetObjectCache(WorldObjectCache cache) => _objectCache = cache;
    /// <summary>
    /// While this says true, no new ammo check starts: combat is swapping launchers and wields
    /// the new launcher's ammo itself (CombatManager.AmmoSwapInProgress). Without it, crafting
    /// could equip ammo for the launcher being swapped away from, or start a craft mid-swap.
    /// </summary>
    public Func<bool>? HoldWhile { get; set; }

    /// <summary>
    /// Set by the plugin (RynthAiPlugin.FightIsOn): true while a fight is on. Crafting holds combat
    /// (Peace mode, up to CRAFT_TIMEOUT_MS a step), so in a fight it does only what the fight needs:
    /// with ammo in the pack, that ammo is wielded at once and better ammo is not crafted, and a
    /// craft under way stops as soon as there is ammo to shoot (carried before it, or one combine
    /// made) and wields it. With no ammo at all it still makes the first combine: combat keeps an
    /// empty launcher (it doesn't swap to melee or a wand for want of ammo), so without the craft
    /// the bot could not fight back at all.
    /// </summary>
    public Func<bool>? FightIsOn { get; set; }
    // The craft under way is better ammo than ammo already carried (an upgrade, not a need).
    private bool _craftIsUpgrade;
    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;

    public enum CraftState { Idle, Evaluating, Combining, EquippingAmmo }

    public CraftState State { get; private set; } = CraftState.Idle;
    public bool IsCrafting => State != CraftState.Idle;
    public string StatusMessage { get; private set; } = "";

    private DateTime _phaseStart = DateTime.MinValue;
    private DateTime _lastAmmoCheck = DateTime.MinValue;
    private DateTime _lastApplyAttempt = DateTime.MinValue;
    private const int AMMO_CHECK_INTERVAL_MS = 5000;
    private const int APPLY_RETRY_MS = 2000;
    private const int CRAFT_TIMEOUT_MS = 15000;
    private const int EQUIP_DELAY_MS = 500;
    private const int ERROR_BACKOFF_MS = 60000;

    // An ammo wield the server ignores (ammo that doesn't fit the launcher really in hand) came
    // round again every AMMO_CHECK_INTERVAL_MS, and each try stopped nav while it waited for the
    // answer: Silentkelpie stood still ~3.5 s of every 5 on 2026-10-15. The same item sent more
    // than MAX_EQUIP_REPEATS times inside EQUIP_REPEAT_WINDOW_MS backs off instead.
    private const int EQUIP_REPEAT_WINDOW_MS = 30000;
    private const int MAX_EQUIP_REPEATS = 2;
    private const int EQUIP_IGNORED_BACKOFF_MS = 120000;
    private int _lastEquipSentId;
    private DateTime _lastEquipSentAt = DateTime.MinValue;
    private int _equipRepeats;

    private int _headBundleId = 0;
    private int _shaftBundleId = 0;
    private AmmoRecipe? _currentRecipe = null;
    private int _equipTargetId = 0;
    private bool _allCombinesDone = false;
    private WeaponCategory _lastWeaponCategory = WeaponCategory.Bow;

    private readonly Queue<int[]> _pendingBundlePairs = new();
    private int _totalCombines = 0;
    private int _combinesCompleted = 0;

    public MissileCraftingManager(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    // ══════════════════════════════════════════════════════════════════
    //  MAIN TICK
    // ══════════════════════════════════════════════════════════════════

    public void ProcessCrafting()
    {
        // While State isn't Idle the plugin holds combat, nav and looting, so turning
        // crafting off mid-craft must let go too.
        if (!_settings.EnableMissileCrafting)
        {
            if (_topOff) _topOffEndKind = KitStopKind.Other;
            if (State != CraftState.Idle) Reset("Missile crafting turned off");
            return;
        }
        if (IsBusy) return;

        // An exception here used to leave State where it was, and the bot stood still
        // forever with nothing in chat: say what broke, let go, and try again later.
        try
        {
            switch (State)
            {
                case CraftState.Idle:          ProcessIdle();          break;
                case CraftState.Evaluating:    ProcessEvaluating();    break;
                case CraftState.Combining:     ProcessCombining();     break;
                case CraftState.EquippingAmmo: ProcessEquippingAmmo(); break;
            }
        }
        catch (Exception ex)
        {
            string reason = $"Stopped by an error ({ex.GetType().Name}: {ex.Message}); trying again in a minute";
            if (State == CraftState.Idle) ChatLog(reason);   // Reset only speaks for a craft in progress
            Reset(reason);
            _lastAmmoCheck = RynthClock.Now.AddMilliseconds(ERROR_BACKOFF_MS - AMMO_CHECK_INTERVAL_MS);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  MAKE AMMO WHEN LOW (the top-off between fights)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The launcher in hand, the ammo carried for it (quiver and pack, stacks summed), and what a
    /// top-off would make: the best recipe the pack and Fletching make reliably (the empty-slot path's
    /// own choice, AmmoRecipes.BestCraftable; never one under its 90% line) and how many combines the
    /// bundles allow. <paramref name="forceRefresh"/> false re-reads a scan under a second old.
    /// </summary>
    internal AmmoLook LookTopOff(bool forceRefresh = true)
    {
        var inv = GetInventory(forceRefresh);
        AmmoLook look = LookTopOff(inv);
        LastLook = look;
        return look;
    }

    private AmmoLook LookTopOff(List<WorldObject> inv)
    {
        if (!TryGetWieldedMissileCategory(inv, out WeaponCategory category, out _))
            return new AmmoLook(false, _lastWeaponCategory, 0, false, null, 0, "no bow, crossbow or atlatl in hand");
        _lastWeaponCategory = category;
        int carried = CountAmmo(inv, category);
        bool wielded = HasWieldedAmmo(inv, category);

        var fletching = _charSkills?[AcSkillType.Fletching];
        if (fletching != null && fletching.Training < 2)
            return new AmmoLook(true, category, carried, wielded, null, 0, "Fletching is untrained");
        int skill = fletching?.Buffed ?? 250;
        var best = AmmoRecipes.BestCraftable(inv.Select(i => i.Name), category, skill, out var tooHard);
        if (best == null)
        {
            string why = tooHard != null
                ? $"{tooHard.OutputName} is about {AmmoRecipes.SuccessChance(skill, tooHard.Difficulty):P0} with Fletching {skill}, under the 90% line (a failed combine destroys both bundles)"
                : $"no heads and shafts for {AmmoNoun(category)} carried";
            return new AmmoLook(true, category, carried, wielded, null, 0, why);
        }
        int pairs = PairsCarried(best, inv);
        return new AmmoLook(true, category, carried, wielded, best, pairs,
                            pairs > 0 ? string.Empty : $"no bundles for {best.OutputName}");
    }

    /// <summary>Loose ammo for the launcher: the stack in the quiver and every stack in the packs.</summary>
    private int CountAmmo(List<WorldObject> inv, WeaponCategory category)
    {
        int n = 0;
        var seen = new HashSet<int>();
        foreach (var item in WornCandidates(inv))
        {
            if (!seen.Add(item.Id) || !IsLooseAmmo(item, category)) continue;
            n += Math.Max(1, item.Values(LongValueKey.StackCount, 1));
        }
        return n;
    }

    /// <summary>Combines the first head and shaft stacks allow (an Infinite head is never used up).</summary>
    private static int PairsCarried(AmmoRecipe recipe, List<WorldObject> inv)
    {
        var head = inv.FirstOrDefault(i => recipe.IsHead(i.Name));
        var shaft = inv.FirstOrDefault(i => recipe.IsShaft(i.Name));
        if (head == null || shaft == null) return 0;
        int heads = AmmoRecipes.HeadIsInfinite(recipe) ? int.MaxValue : Math.Max(1, head.Values(LongValueKey.StackCount, 1));
        int shafts = Math.Max(1, shaft.Values(LongValueKey.StackCount, 1));
        return Math.Min(heads, shafts);
    }

    /// <summary>
    /// Starts a top-off toward <paramref name="target"/> (the caller has checked it is safe: no fight,
    /// no monster near, in peace mode). <paramref name="why"/> is the start line's reason ("under 100").
    /// <paramref name="ended"/> hears how it ended and the combines made, and returns what the summary
    /// adds (the next try). Returns "" or why not.
    /// </summary>
    internal string StartTopOff(int target, bool manual, string why, Func<KitStopKind, int, string>? ended)
    {
        if (!_settings.EnableMissileCrafting) return "Enable Missile Crafting is off";
        if (State != CraftState.Idle) return "ammo crafting is already under way";
        if (HoldWhile?.Invoke() == true) return "a launcher swap is under way";
        if (InFight()) return "a fight is on";
        var inv = GetInventory(forceRefresh: true);
        AmmoLook look = LookTopOff(inv);
        LastLook = look;
        if (!look.Launcher || look.Recipe == null || look.Combines <= 0) return look.PackWhy;
        if (look.Carried >= target) return $"{look.CarriedText} carried already (makes up to {target})";

        var recipe = look.Recipe;
        int yield = AmmoRecipes.EstimatedYield(recipe);
        int combines = Math.Clamp((target - look.Carried + yield - 1) / yield, 1, Math.Min(MaxTopOffCombines, look.Combines));
        _topOffQuiet = true;
        try { StartCrafting(recipe, inv, upgrade: false, pairs: combines); }
        finally { _topOffQuiet = false; }
        if (State != CraftState.Combining) return StatusMessage.Length > 0 ? StatusMessage : "the bundles couldn't be read";

        _topOff = true;
        _topOffManual = manual;
        _topOffTarget = target;
        _topOffYield = yield;
        _topOffFailed = 0;
        _topOffCategory = look.Category;
        _topOffOutput = recipe.OutputName;
        _topOffEndKind = null;
        _topOffWhy = string.Empty;
        _topOffEnded = ended;
        string line = $"[RynthAi] Make ammo when low: {look.CarriedText} carried ({why}); making {Combines(combines)} of "
                      + $"{recipe.OutputName}, about {combines * yield}, toward {target}.";
        SayLine(line);
        RynthLog.Write(LogCat.Crafting, "[Ammo] start: " + line);
        return string.Empty;
    }

    /// <summary>Stops the top-off going (a monster, a hit, the player's Stop, ...): one summary line.</summary>
    internal void StopTopOff(string why, KitStopKind kind)
    {
        if (!_topOff) return;
        _topOffEndKind = kind;
        _topOffWhy = why;
        Reset(why);
    }

    /// <summary>What stops a top-off from inside crafting ("" = go on): a fight, a launcher swap, leaving peace mode.</summary>
    private string TopOffDanger()
    {
        if (InFight()) return "a fight";
        if (HoldWhile?.Invoke() == true) return "a launcher swap";
        try
        {
            if (_peaceSent && _host.HasGetCurrentCombatMode && _host.GetCurrentCombatMode() != CombatMode.NonCombat)
                return "left peace mode";
        }
        catch { }
        return string.Empty;
    }

    /// <summary>The top-off's summary line and the end callback (from <see cref="Reset"/>, before it clears the craft).</summary>
    private void FinishTopOff(string reason)
    {
        int made = _combinesCompleted, planned = _totalCombines, failed = _topOffFailed;
        KitStopKind kind = _topOffEndKind ?? (made > 0 ? KitStopKind.Finished : KitStopKind.Refused);
        string why = _topOffWhy.Length > 0 ? _topOffWhy : reason;
        if (why.StartsWith("Done", StringComparison.Ordinal))
            why = made >= planned ? $"made the {Combines(planned)} planned" : "done";
        var ended = _topOffEnded;
        string noun = AmmoNoun(_topOffCategory);
        int yield = _topOffYield;
        string output = _topOffOutput;
        _topOff = false;
        _topOffManual = false;
        _topOffEnded = null;
        _topOffEndKind = null;
        _topOffWhy = string.Empty;

        string tail = string.Empty;
        try { tail = ended?.Invoke(kind, made) ?? string.Empty; }
        catch (Exception ex) { RynthLog.Exception(LogCat.Crafting, ex, "Make ammo when low: the end of a top-off"); }
        string madeText = made == 0 ? "made nothing" : $"made {Combines(made)} of {output} (about {made * yield} {noun})";
        string failText = failed == 0 ? "" : $", {Combines(failed)} failed (a failed combine destroys both bundles)";
        string line = $"[RynthAi] Make ammo when low: {madeText}{failText}. Stopped: {why}.{tail}";
        LastTopOffSummary = line;
        SayLine(line);
        RynthLog.Write(LogCat.Crafting, "[Ammo] " + line);
    }

    private static string Combines(int n) => n == 1 ? "1 combine" : $"{n} combines";

    /// <summary>All combines answered (or a timeout after some): wield only if the slot is empty, else done.</summary>
    private bool TopOffSlotFilled()
    {
        var inv = GetInventory(forceRefresh: true);
        return HasWieldedAmmo(inv, ResolveWeaponCategory(inv));
    }

    private void SayLine(string line)
    {
        try
        {
            if (Say != null) Say(line);
            else if (_host.HasWriteToChat) _host.WriteToChat(line, 1);
        }
        catch { }
    }

    // While a top-off starts or goes, crafting's own "[RynthAi Craft]" lines go to the log only:
    // the top-off says one line when it starts and one when it stops.
    private bool _topOffQuiet;

    // ══════════════════════════════════════════════════════════════════
    //  CHAT HANDLER — called from RynthAiPlugin.OnChatWindowText
    // ══════════════════════════════════════════════════════════════════

    public void HandleChat(string text)
    {
        if (State != CraftState.Combining) return;
        if (string.IsNullOrEmpty(text)) return;
        bool made = text.IndexOf("You make", StringComparison.OrdinalIgnoreCase) >= 0;
        // A top-off counts a failed combine and goes on with the next (the empty-slot path never
        // matched failures and retries until its timeout, as before).
        bool failed = _topOff && !made && text.IndexOf("You fail to make", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!made && !failed) return;

        if (made) _combinesCompleted++; else _topOffFailed++;
        if (_topOff) _phaseStart = RynthClock.Now;   // each combine of a top-off gets its own timeout
        ChatLog(made ? $"Craft confirmed ({_combinesCompleted}/{_totalCombines})" : $"Combine failed ({_topOffFailed})");

        if (_pendingBundlePairs.Count > 0)
        {
            var pair = _pendingBundlePairs.Dequeue();
            _headBundleId = pair[0];
            _shaftBundleId = pair[1];
            _lastApplyAttempt = DateTime.MinValue;
            StatusMessage = $"Crafting {_currentRecipe!.OutputName} (batch {_combinesCompleted + 1})...";
        }
        else
        {
            _allCombinesDone = true;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  IDLE
    // ══════════════════════════════════════════════════════════════════

    private void ProcessIdle()
    {
        if ((RynthClock.Now - _lastAmmoCheck).TotalMilliseconds < AMMO_CHECK_INTERVAL_MS) return;
        if (HoldWhile?.Invoke() == true) return;
        _lastAmmoCheck = RynthClock.Now;

        var inv = GetInventory(forceRefresh: true);
        if (!TryGetWieldedMissileCategory(inv, out WeaponCategory category, out WorldObject? launcher))
            return;

        _lastWeaponCategory = category;
        if (HasWieldedAmmo(inv, category))
            return;

        _host.Log($"[RynthAi] MissileCraft: no {GetCategoryLabel(category)} wielded with launcher " +
                  $"0x{launcher!.Id:X8} '{launcher.Name}' (slot {launcher.WieldedLocation}); evaluating");
        ChatLog($"No wielded {GetCategoryLabel(category)}s. Evaluating...");
        SetState(CraftState.Evaluating);
    }

    // ══════════════════════════════════════════════════════════════════
    //  EVALUATING
    // ══════════════════════════════════════════════════════════════════

    private void ProcessEvaluating()
    {
        var inv = GetInventory(forceRefresh: true);
        WeaponCategory category = ResolveWeaponCategory(inv);

        // Fletching can't be used untrained. Past that, the recipe's difficulty against
        // the buffed skill decides — a failed combine destroys both bundles.
        var fletching = _charSkills?[AcSkillType.Fletching];
        AmmoRecipe? bestCraftable = null, tooHard = null;
        if ((fletching?.Training ?? 2) >= 2)
            bestCraftable = AmmoRecipes.BestCraftable(inv.Select(i => i.Name), category,
                                                      fletching?.Buffed ?? 250, out tooHard);

        int bestExistingPriority = 0;
        WorldObject? bestExistingAmmo = null;
        foreach (var item in inv)
        {
            if (!IsLooseAmmo(item, category)) continue;
            int pri = GetAmmoPriority(item, category);
            if (pri > bestExistingPriority) { bestExistingPriority = pri; bestExistingAmmo = item; }
        }

        if (bestCraftable != null && bestCraftable.Rank > bestExistingPriority)
        {
            // In a fight, better ammo waits: the carried ammo goes on now (no Peace mode, no combine).
            if (bestExistingAmmo != null && InFight())
            {
                _host.Log($"[RynthAi] MissileCraft: a fight is on - wielding '{bestExistingAmmo.Name}' now instead of crafting {bestCraftable.OutputName}.");
                ChatLog($"In a fight: wielding {bestExistingAmmo.Name} now instead of crafting {bestCraftable.OutputName}");
            }
            else
            {
                StartCrafting(bestCraftable, inv, upgrade: bestExistingAmmo != null);
                return;
            }
        }

        if (bestExistingAmmo != null)
        {
            _equipTargetId = bestExistingAmmo.Id;
            ChatLog($"Equipping {bestExistingAmmo.Name}");
            SetState(CraftState.EquippingAmmo);
            return;
        }

        if (bestCraftable != null)
        { StartCrafting(bestCraftable, inv); return; }

        // No ammo at all and only bundles the skill check calls risky: a bow with nothing
        // to shoot can't fight, so a combine that may fail beats standing there.
        if (tooHard != null)
        {
            int skill = fletching?.Buffed ?? 250;
            ChatLog($"No ammo: making {tooHard.OutputName} anyway, about " +
                    $"{AmmoRecipes.SuccessChance(skill, tooHard.Difficulty):P0} with Fletching {skill} " +
                    "(a failed combine destroys both bundles)");
            StartCrafting(tooHard, inv);
            return;
        }

        if (fletching != null && fletching.Training < 2
                 && AmmoRecipes.BestCraftable(inv.Select(i => i.Name), category, 0, out var carried) == null
                 && carried != null)
            Reset($"No ammo, and Fletching is untrained, so the bundles for {carried.OutputName} can't be combined");
        else
            Reset("No ammo and no bundles available");
    }

    /// <param name="pairs">Combines to queue (a top-off's count); 0 = the empty-slot path's up to 2.</param>
    private void StartCrafting(AmmoRecipe recipe, List<WorldObject> inv, bool upgrade = false, int pairs = 0)
    {
        _craftIsUpgrade = upgrade;
        _currentRecipe = recipe;
        _pendingBundlePairs.Clear();
        _combinesCompleted = 0;
        _allCombinesDone = false;

        _peaceSent = false;
        _peaceAskedAt = RynthClock.Now;   // the wait for the stance counts from the first ask
        TryPeaceForCrafting();

        var headItems  = inv.Where(i => recipe.IsHead(i.Name)).ToList();
        var shaftItems = inv.Where(i => recipe.IsShaft(i.Name)).ToList();

        if (headItems.Count == 0 || shaftItems.Count == 0)
        {
            Reset($"No bundles available for {recipe.OutputName} ({headItems.Count} head, {shaftItems.Count} shaft)");
            return;
        }

        // Check stack sizes to decide how many combines (1 or 2)
        int availableHeads = headItems[0].Values(LongValueKey.StackCount, 1);
        int availableShafts = shaftItems[0].Values(LongValueKey.StackCount, 1);
        int pairCount = pairs > 0 ? pairs : Math.Min(2, Math.Min(availableHeads, availableShafts));

        // Queue combines with same IDs — server decrements stacked bundles
        for (int i = 0; i < pairCount; i++)
            _pendingBundlePairs.Enqueue(new[] { headItems[0].Id, shaftItems[0].Id });

        _totalCombines = pairCount;
        var first = _pendingBundlePairs.Dequeue();
        _headBundleId  = first[0];
        _shaftBundleId = first[1];

        ChatLog($"Crafting {recipe.OutputName} x{_totalCombines}");
        StatusMessage = $"Crafting {recipe.OutputName}...";
        _lastApplyAttempt = DateTime.MinValue;
        SetState(CraftState.Combining);
    }

    // ══════════════════════════════════════════════════════════════════
    //  COMBINING
    // ══════════════════════════════════════════════════════════════════

    private bool _peaceSent;
    private DateTime _peaceAskedAt;
    private const double PeaceWaitMaxMs = 2500;

    /// <summary>Asks for Peace mode until the engine accepts (a refusal is retried, not counted as sent).</summary>
    private void TryPeaceForCrafting()
    {
        if (_peaceSent) return;
        try
        {
            if (_host.HasGetCurrentCombatMode && _host.GetCurrentCombatMode() == CombatMode.NonCombat) { _peaceSent = true; return; }
            _peaceSent = _host.ChangeCombatModeFor(CombatMode.NonCombat, "Crafting", "missile crafting needs peace mode");
        }
        catch { }
    }

    private void ProcessCombining()
    {
        // Make ammo when low: a top-off never holds the bot into a fight, a launcher swap or out of
        // peace mode. It stops at once; the combine already sent finishes on the server.
        if (_topOff)
        {
            string danger = TopOffDanger();
            if (danger.Length > 0) { StopTopOff(danger, KitStopKind.Danger); return; }
        }

        // In a fight, with ammo to shoot (carried before the craft, or a combine already made):
        // stop holding combat and wield the best ammo carried now.
        if (!_allCombinesDone && (_craftIsUpgrade || _combinesCompleted > 0) && InFight())
        {
            string making = _currentRecipe?.OutputName ?? "ammo";
            _host.Log($"[RynthAi] MissileCraft: a fight is on - {making} stopped after {_combinesCompleted} combine(s); wielding the ammo carried.");
            ChatLog($"In a fight: stopped crafting {making} after {_combinesCompleted} combine(s), wielding the ammo carried");
            _pendingBundlePairs.Clear();
            _currentRecipe = null;
            _craftIsUpgrade = false;
            _equipTargetId = 0;   // the best carried
            SetState(CraftState.EquippingAmmo);
            return;
        }

        if (_allCombinesDone)
        {
            // A top-off wields only into an empty slot; the ammo in the quiver stays.
            if (_topOff && TopOffSlotFilled()) { Reset("Done"); return; }
            StatusMessage = $"Equipping {_currentRecipe!.OutputName}...";
            _equipTargetId = 0;
            SetState(CraftState.EquippingAmmo);
            return;
        }

        if ((RynthClock.Now - _phaseStart).TotalMilliseconds > CRAFT_TIMEOUT_MS)
        {
            if (_topOff && (_combinesCompleted > 0 || _topOffFailed > 0))
            {
                _topOffWhy = $"no answer to the next combine after {Combines(_combinesCompleted + _topOffFailed)}";
                if (_combinesCompleted == 0 || TopOffSlotFilled()) { Reset("Craft timed out"); return; }
                _equipTargetId = 0;
                SetState(CraftState.EquippingAmmo);
            }
            else if (_combinesCompleted > 0)
            {
                ChatLog($"Timeout after {_combinesCompleted} combine(s). Equipping what we have.");
                _equipTargetId = 0;
                SetState(CraftState.EquippingAmmo);
            }
            else
            {
                Reset("Craft timed out — no response from server");
            }
            return;
        }

        if (!_peaceSent)
        {
            TryPeaceForCrafting();
            if (!_peaceSent && (RynthClock.Now - _peaceAskedAt).TotalMilliseconds < PeaceWaitMaxMs) return;   // still asking, inside the ceiling
        }

        if ((RynthClock.Now - _lastApplyAttempt).TotalMilliseconds >= APPLY_RETRY_MS)
        {
            try
            {
                if (!_host.UseOnFor((uint)_headBundleId, (uint)_shaftBundleId, "Crafting", "missile crafting: heads on shafts"))
                {
                    ChatLog($"UseObjectOn returned false for 0x{_headBundleId:X8} -> 0x{_shaftBundleId:X8}");
                    _lastApplyAttempt = RynthClock.Now;
                    return;
                }

                _lastApplyAttempt = RynthClock.Now;
                StatusMessage = $"Combining -> {_currentRecipe!.OutputName} ({_combinesCompleted + 1}/{_totalCombines})...";
            }
            catch (Exception ex)
            {
                Reset($"Combine failed: {ex.Message}");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  EQUIPPING
    // ══════════════════════════════════════════════════════════════════

    private void ProcessEquippingAmmo()
    {
        if ((RynthClock.Now - _phaseStart).TotalMilliseconds < EQUIP_DELAY_MS) return;

        try
        {
            var inv = GetInventory(forceRefresh: true);
            int targetId = _equipTargetId;

            if (targetId == 0)
            {
                WeaponCategory cat = ResolveWeaponCategory(inv);
                var ammo = FindBestAmmoInInventory(cat, inv);
                if (ammo != null) targetId = ammo.Id;
            }

            if (targetId != 0)
            {
                if (!NoteEquipSent(targetId))
                {
                    string name = inv.FirstOrDefault(i => i.Id == targetId)?.Name ?? $"0x{targetId:X8}";
                    string why = $"The server didn't wield {name} after {MAX_EQUIP_REPEATS} tries; " +
                                 $"leaving the ammo alone for {EQUIP_IGNORED_BACKOFF_MS / 60000} minutes";
                    _host.Log($"[RynthAi] MissileCraft: {why} (0x{targetId:X8})");
                    Reset(why);
                    _equipRepeats = 0;
                    _lastAmmoCheck = RynthClock.Now.AddMilliseconds(EQUIP_IGNORED_BACKOFF_MS - AMMO_CHECK_INTERVAL_MS);
                    return;
                }

                _host.UseFor((uint)targetId, "Crafting", "missile crafting: wield the new ammo");
                LastAmmoEquipAt = RynthClock.Now;
                ChatLog($"Equipped ammo (id=0x{targetId:X8})");
                Reset($"Done: {_currentRecipe?.OutputName ?? "ammo"}");
            }
            else
            {
                if ((RynthClock.Now - _phaseStart).TotalMilliseconds < CRAFT_TIMEOUT_MS)
                {
                    StatusMessage = $"Waiting for {_currentRecipe?.OutputName ?? GetCategoryLabel(_lastWeaponCategory)} to appear...";
                    return;
                }

                ChatLog("Could not find ammo to equip.");
                Reset("Equip timed out — no ammo found");
            }
        }
        catch (Exception ex)
        {
            ChatLog($"Equip error: {ex.Message}");
            Reset($"Equip failed: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════════

    // ── Direct inventory scan (bypasses WorldObjectCache to avoid crash) ────

    private List<WorldObject> GetInventory(bool forceRefresh = false)
        => _objectCache?.GetDirectInventory(forceRefresh).ToList() ?? new();

    private WeaponCategory ResolveWeaponCategory(List<WorldObject> inventory)
    {
        if (TryGetWieldedMissileCategory(inventory, out WeaponCategory category, out _))
        {
            _lastWeaponCategory = category;
            return category;
        }

        return _lastWeaponCategory;
    }

    private bool TryGetWieldedMissileCategory(List<WorldObject> inventory, out WeaponCategory category,
                                              out WorldObject? launcher)
    {
        foreach (var item in WornCandidates(inventory))
        {
            // AC files ammo as a missile weapon: wielded quarrels must not pass for the launcher
            // (no "crossbow" in the name reads as a bow, and arrows went on at the crossbow).
            if (WorldObjectCache.IsAmmo(item) || !IsPlayerWielded(item) || !LooksLikeMissileWeapon(item))
                continue;

            // The launcher's AmmoType when known (a crossbow not named so is still a crossbow), else its name.
            int lt = CombatManager.LauncherTypeOf(item);
            category = lt != 0 ? MissileAmmo.CategoryOf(lt) : GetWeaponCategory(item.Name);
            _launcherType = lt;
            launcher = item;
            return true;
        }

        category = WeaponCategory.Bow;
        launcher = null;
        _launcherType = 0;
        return false;
    }

    // The ammo type of the launcher last seen in hand (0 = not known: by name only).
    private int _launcherType;

    /// <summary>When crafting last sent an ammo wield (combat's ammo step waits a moment after it).</summary>
    public DateTime LastAmmoEquipAt { get; private set; } = DateTime.MinValue;

    private bool HasWieldedAmmo(List<WorldObject> inventory, WeaponCategory category)
    {
        foreach (var item in WornCandidates(inventory))
        {
            if (!IsPlayerWielded(item))
                continue;

            if (IsLooseAmmo(item, category))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The direct scan plus the cache-known items it left out. The scan merges worn gear only when
    /// its slot reads above 0, and the slot reads 0 for gear wielded after login (a launcher combat
    /// swapped in, and its ammo), so the launcher really in hand was missing from the scan alone.
    /// </summary>
    private IEnumerable<WorldObject> WornCandidates(List<WorldObject> inventory)
    {
        foreach (var item in inventory)
            yield return item;

        if (_objectCache == null)
            yield break;

        var listed = new HashSet<int>(inventory.Select(i => i.Id));
        foreach (var item in _objectCache.GetInventory())
            if (listed.Add(item.Id))
                yield return item;
    }

    private bool IsPlayerWielded(WorldObject item)
    {
        // The server's word (the engine's EquipmentTracker, API v87) when the engine has it. The
        // client's own fields go stale both ways: a launcher put away after a swap keeps its old
        // wielder and slot, so the slot alone said a stowed bow was in hand while a crossbow was, and
        // arrows were wielded at it every 5 s (Silentkelpie, 2026-10-15); gear wielded after login
        // reads slot 0.
        if (EquippedState.IsWielded(_host, item.Id) is bool tracked)
            return tracked;

        uint playerId = _host.GetPlayerId();
        if (playerId == 0)
            return false;

        // Found inside a side pack by the direct scan (or in the main pack with its container field
        // set and no wielder): not in hand, whatever the stale record says. A parent of the player
        // says nothing: the engine lists what the player wields in the player's own contents too
        // (ScanByContainerId matches the wielder as well as the container). Taking every item the
        // scan filed under a container as "not in hand" (2026-10-19) hid the bow in hand as well,
        // so crafting saw no launcher and never started, with nothing in chat (2026-10-21).
        if (item.DirectContainerId != 0 && item.DirectContainerId != unchecked((int)playerId))
            return false;
        if (_objectCache?.InPackWalk(item.Id) == true)
            return false;

        if (item.WieldedLocation > 0)
            return item.Wielder == 0 || item.Wielder == unchecked((int)playerId);

        // The slot reads 0 for gear wielded after login; the wielder still names the player.
        return _host.HasGetObjectWielderInfo
            && _host.TryGetObjectWielderInfo(unchecked((uint)item.Id), out uint wielder, out _)
            && wielder == playerId;
    }

    /// <summary>
    /// Counts an ammo wield about to go out; false once the same item was already sent
    /// <see cref="MAX_EQUIP_REPEATS"/> times inside <see cref="EQUIP_REPEAT_WINDOW_MS"/>.
    /// </summary>
    private bool NoteEquipSent(int targetId)
    {
        DateTime now = RynthClock.Now;
        if (targetId == _lastEquipSentId && (now - _lastEquipSentAt).TotalMilliseconds < EQUIP_REPEAT_WINDOW_MS)
            _equipRepeats++;
        else
            _equipRepeats = 1;

        _lastEquipSentId = targetId;
        _lastEquipSentAt = now;
        return _equipRepeats <= MAX_EQUIP_REPEATS;
    }

    private static bool LooksLikeMissileWeapon(WorldObject item)
    {
        if (item.ObjectClass == AcObjectClass.MissileWeapon)
            return true;

        if (string.IsNullOrWhiteSpace(item.Name))
            return false;

        return item.Name.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0
            || item.Name.IndexOf("atlatl", StringComparison.OrdinalIgnoreCase) >= 0
            || item.Name.IndexOf("bow", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static WeaponCategory GetWeaponCategory(string name)
    {
        if (name.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0) return WeaponCategory.Crossbow;
        if (name.IndexOf("atlatl", StringComparison.OrdinalIgnoreCase) >= 0) return WeaponCategory.Atlatl;
        return WeaponCategory.Bow;
    }

    /// <summary>
    /// Ammo for the launcher: by name as before, or (2026-11-07) an ammunition-slot item whose AmmoType
    /// fits the launcher's, or that has no AmmoType (servers' own ammo, often named nothing like
    /// arrows; its wield is refused by the server when it doesn't fit, and backs off).
    /// </summary>
    private bool IsLooseAmmo(WorldObject item, WeaponCategory category)
    {
        if (AmmoRecipes.IsLooseAmmoName(item.Name, category)) return true;
        int valid = item.Values(LongValueKey.Locations, 0);
        if ((valid & MissileAmmo.AmmoSlot) == 0) return false;
        int type = CombatManager.AmmoTypeOfItem(item);
        if (type == 0) return true;
        return _launcherType != 0 ? MissileAmmo.CanShoot(_launcherType, type) : MissileAmmo.CategoryOf(type) == category;
    }

    private static int GetAmmoPriority(WorldObject item, WeaponCategory category) =>
        AmmoRecipes.OutputRank(item.Name, category);

    private WorldObject? FindBestAmmoInInventory(WeaponCategory category, IEnumerable<WorldObject> inventory)
    {
        int bestPri = 0;
        WorldObject? best = null;
        foreach (var item in inventory)
        {
            if (!IsLooseAmmo(item, category)) continue;
            int pri = GetAmmoPriority(item, category);
            if (pri > bestPri) { bestPri = pri; best = item; }
        }
        return best;
    }


    private static string GetCategoryLabel(WeaponCategory cat) => cat switch
    {
        WeaponCategory.Bow      => "Arrow",
        WeaponCategory.Crossbow => "Quarrel",
        WeaponCategory.Atlatl   => "Dart",
        _                       => "Ammo",
    };

    private void SetState(CraftState newState)
    {
        State = newState;
        _phaseStart = RynthClock.Now;
    }

    private void Reset(string reason)
    {
        if (_topOff)
            FinishTopOff(reason);   // the top-off's own summary line (crafting's are quiet for it)
        else if (State != CraftState.Idle || reason.IndexOf("bundle", StringComparison.OrdinalIgnoreCase) >= 0)
            ChatLog(reason);

        StatusMessage = reason;
        State = CraftState.Idle;
        _currentRecipe = null;
        _headBundleId = 0;
        _shaftBundleId = 0;
        _equipTargetId = 0;
        _allCombinesDone = false;
        _combinesCompleted = 0;
        _totalCombines = 0;
        _craftIsUpgrade = false;
        _pendingBundlePairs.Clear();
    }

    /// <summary>The plugin says a fight is on (<see cref="FightIsOn"/>); a throwing check is no fight.</summary>
    private bool InFight()
    {
        try { return FightIsOn?.Invoke() == true; }
        catch { return false; }
    }

    private void ChatLog(string msg)
    {
        if (_topOff || _topOffQuiet)
        {
            try { _host.Log($"[RynthAi] MissileCraft (Make ammo when low): {msg}"); } catch { }
            return;
        }
        try { _host.WriteToChat($"[RynthAi Craft] {msg}", 1); } catch { }
    }
}
