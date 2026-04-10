using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;

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
/// </summary>
public class MissileCraftingManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private WorldObjectCache? _objectCache;
    private CharacterSkills? _charSkills;

    public bool IsBusy { get; set; } = false;
    public int CurrentCombatMode { get; set; } = 1; // 1=peace, 2=melee, 4=missile, 8=magic

    public void SetObjectCache(WorldObjectCache cache) => _objectCache = cache;
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

    private int _headBundleId = 0;
    private int _shaftBundleId = 0;
    private AmmoRecipe? _currentRecipe = null;
    private int _equipTargetId = 0;
    private bool _allCombinesDone = false;
    private WeaponCategory _lastWeaponCategory = WeaponCategory.Bow;

    private readonly Queue<int[]> _pendingBundlePairs = new();
    private int _totalCombines = 0;
    private int _combinesCompleted = 0;

    // ══════════════════════════════════════════════════════════════════
    //  RECIPE DATABASE
    // ══════════════════════════════════════════════════════════════════

    public enum WeaponCategory { Bow, Crossbow, Atlatl }

    public class AmmoRecipe
    {
        public string HeadBundleName = "";
        public string ShaftBundleName = "";
        public string OutputName = "";
        public WeaponCategory Category;
        public bool RequiresSpecialized;
        public int Priority;
    }

    public static readonly List<AmmoRecipe> AllRecipes = new List<AmmoRecipe>
    {
        // ── ARROWS (Bow) ───────────────────────────────────────────
        new() { HeadBundleName = "Wrapped Bundle of Lethal Prismatic Arrowheads",   ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Lethal Prismatic Arrow",   Category = WeaponCategory.Bow, RequiresSpecialized = true,  Priority = 40 },
        new() { HeadBundleName = "Wrapped Bundle of Deadly Prismatic Arrowheads",   ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Deadly Prismatic Arrow",   Category = WeaponCategory.Bow, RequiresSpecialized = true,  Priority = 30 },
        new() { HeadBundleName = "Wrapped Bundle of Greater Prismatic Arrowheads",  ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Greater Prismatic Arrow",  Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 20 },
        new() { HeadBundleName = "Wrapped Bundle of Prismatic Arrowheads",          ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Prismatic Arrow",          Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 10 },
        new() { HeadBundleName = "Wrapped Bundle of Armor Piercing Arrowheads",    ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Armor Piercing Arrow",    Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 6  },
        new() { HeadBundleName = "Wrapped Bundle of Broad Arrowheads",             ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Broad Head Arrow",        Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 5  },
        new() { HeadBundleName = "Wrapped Bundle of Blunt Arrowheads",             ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Blunt Arrow",             Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 4  },
        new() { HeadBundleName = "Wrapped Bundle of Frog Crotch Arrowheads",       ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Frog Crotch Arrow",       Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 3  },
        new() { HeadBundleName = "Wrapped Bundle of Arrowheads",                   ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Arrow",                   Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 1  },

        // ── QUARRELS (Crossbow) ────────────────────────────────────
        new() { HeadBundleName = "Wrapped Bundle of Lethal Prismatic Quarrelheads",  ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Lethal Prismatic Quarrel",  Category = WeaponCategory.Crossbow, RequiresSpecialized = true,  Priority = 40 },
        new() { HeadBundleName = "Wrapped Bundle of Deadly Prismatic Quarrelheads",  ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Deadly Prismatic Quarrel",  Category = WeaponCategory.Crossbow, RequiresSpecialized = true,  Priority = 30 },
        new() { HeadBundleName = "Wrapped Bundle of Greater Prismatic Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Greater Prismatic Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 20 },
        new() { HeadBundleName = "Wrapped Bundle of Prismatic Quarrelheads",         ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Prismatic Quarrel",         Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 10 },
        new() { HeadBundleName = "Wrapped Bundle of Armor Piercing Quarrelheads",   ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Armor Piercing Quarrel",   Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 6  },
        new() { HeadBundleName = "Wrapped Bundle of Broad Quarrelheads",            ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Broad Head Quarrel",       Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 5  },
        new() { HeadBundleName = "Wrapped Bundle of Blunt Quarrelheads",            ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Blunt Quarrel",            Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 4  },
        new() { HeadBundleName = "Wrapped Bundle of Quarrelheads",                  ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Quarrel",                  Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 1  },

        // ── DARTS (Atlatl) ─────────────────────────────────────────
        new() { HeadBundleName = "Wrapped Bundle of Lethal Prismatic Atlatl Dart Heads",  ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Lethal Prismatic Atlatl Dart",  Category = WeaponCategory.Atlatl, RequiresSpecialized = true,  Priority = 40 },
        new() { HeadBundleName = "Wrapped Bundle of Deadly Prismatic Atlatl Dart Heads",  ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Deadly Prismatic Atlatl Dart",  Category = WeaponCategory.Atlatl, RequiresSpecialized = true,  Priority = 30 },
        new() { HeadBundleName = "Wrapped Bundle of Greater Prismatic Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Greater Prismatic Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 20 },
        new() { HeadBundleName = "Wrapped Bundle of Prismatic Atlatl Dart Heads",         ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Prismatic Atlatl Dart",         Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 10 },
        new() { HeadBundleName = "Wrapped Bundle of Armor Piercing Atlatl Dart Heads",   ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Armor Piercing Atlatl Dart",   Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 6  },
        new() { HeadBundleName = "Wrapped Bundle of Broad Atlatl Dart Heads",            ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Broad Head Atlatl Dart",       Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 5  },
        new() { HeadBundleName = "Wrapped Bundle of Blunt Atlatl Dart Heads",            ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Blunt Atlatl Dart",            Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 4  },
        new() { HeadBundleName = "Wrapped Bundle of Atlatl Dart Heads",                  ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Atlatl Dart",                  Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 1  },
    };

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
        if (!_settings.EnableMissileCrafting) return;
        if (IsBusy) return;

        switch (State)
        {
            case CraftState.Idle:          ProcessIdle();          break;
            case CraftState.Evaluating:    ProcessEvaluating();    break;
            case CraftState.Combining:     ProcessCombining();     break;
            case CraftState.EquippingAmmo: ProcessEquippingAmmo(); break;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CHAT HANDLER — called from RynthAiPlugin.OnChatWindowText
    // ══════════════════════════════════════════════════════════════════

    public void HandleChat(string text)
    {
        if (State != CraftState.Combining) return;
        if (string.IsNullOrEmpty(text)) return;
        if (text.IndexOf("You make", StringComparison.OrdinalIgnoreCase) < 0) return;

        _combinesCompleted++;
        ChatLog($"Craft confirmed ({_combinesCompleted}/{_totalCombines})");

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
        if ((DateTime.Now - _lastAmmoCheck).TotalMilliseconds < AMMO_CHECK_INTERVAL_MS) return;
        _lastAmmoCheck = DateTime.Now;

        var inv = GetInventory(forceRefresh: true);
        if (!TryGetWieldedMissileCategory(inv, out WeaponCategory category))
            return;

        _lastWeaponCategory = category;
        if (HasWieldedAmmo(inv, category))
            return;

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
        int trainingLevel = _charSkills?[AcSkillType.Fletching].Training ?? 2;

        AmmoRecipe? bestCraftable = null;
        foreach (var recipe in AllRecipes
            .Where(r => r.Category == category)
            .OrderByDescending(r => r.Priority))
        {
            if (recipe.RequiresSpecialized && trainingLevel < 3) continue;
            if (!recipe.RequiresSpecialized && trainingLevel < 2) continue;

            bool hasHead  = inv.Any(i => MatchesRecipeComponent(i.Name, recipe.HeadBundleName));
            bool hasShaft = inv.Any(i => MatchesRecipeComponent(i.Name, recipe.ShaftBundleName));
            if (hasHead && hasShaft) { bestCraftable = recipe; break; }
        }

        int bestExistingPriority = 0;
        WorldObject? bestExistingAmmo = null;
        foreach (var item in inv)
        {
            if (!IsLooseAmmo(item, category)) continue;
            int pri = GetAmmoPriority(item, category);
            if (pri > bestExistingPriority) { bestExistingPriority = pri; bestExistingAmmo = item; }
        }

        if (bestCraftable != null && bestCraftable.Priority > bestExistingPriority)
        { StartCrafting(bestCraftable, inv); return; }

        if (bestExistingAmmo != null)
        {
            _equipTargetId = bestExistingAmmo.Id;
            ChatLog($"Equipping {bestExistingAmmo.Name}");
            SetState(CraftState.EquippingAmmo);
            return;
        }

        if (bestCraftable != null)
        { StartCrafting(bestCraftable, inv); return; }

        Reset("No ammo and no bundles available");
    }

    private void StartCrafting(AmmoRecipe recipe, List<WorldObject> inv)
    {
        _currentRecipe = recipe;
        _pendingBundlePairs.Clear();
        _combinesCompleted = 0;
        _allCombinesDone = false;

        try { _host.ChangeCombatMode(CombatMode.NonCombat); } catch { }

        var headItems  = inv.Where(i => MatchesRecipeComponent(i.Name, recipe.HeadBundleName)).ToList();
        var shaftItems = inv.Where(i => MatchesRecipeComponent(i.Name, recipe.ShaftBundleName)).ToList();

        if (headItems.Count == 0 || shaftItems.Count == 0)
        {
            Reset($"No bundles available for {recipe.OutputName} ({headItems.Count} head, {shaftItems.Count} shaft)");
            return;
        }

        // Check stack sizes to decide how many combines (1 or 2)
        int availableHeads = headItems[0].Values(LongValueKey.StackCount, 1);
        int availableShafts = shaftItems[0].Values(LongValueKey.StackCount, 1);
        int pairCount = Math.Min(2, Math.Min(availableHeads, availableShafts));

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

    private void ProcessCombining()
    {
        if (_allCombinesDone)
        {
            StatusMessage = $"Equipping {_currentRecipe!.OutputName}...";
            _equipTargetId = 0;
            SetState(CraftState.EquippingAmmo);
            return;
        }

        if ((DateTime.Now - _phaseStart).TotalMilliseconds > CRAFT_TIMEOUT_MS)
        {
            if (_combinesCompleted > 0)
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

        if ((DateTime.Now - _lastApplyAttempt).TotalMilliseconds >= APPLY_RETRY_MS)
        {
            try
            {
                if (!_host.UseObjectOn((uint)_headBundleId, (uint)_shaftBundleId))
                {
                    ChatLog($"UseObjectOn returned false for 0x{_headBundleId:X8} -> 0x{_shaftBundleId:X8}");
                    _lastApplyAttempt = DateTime.Now;
                    return;
                }

                _lastApplyAttempt = DateTime.Now;
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
        if ((DateTime.Now - _phaseStart).TotalMilliseconds < EQUIP_DELAY_MS) return;

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
                _host.UseObject((uint)targetId);
                ChatLog($"Equipped ammo (id=0x{targetId:X8})");
                Reset($"Done: {_currentRecipe?.OutputName ?? "ammo"}");
            }
            else
            {
                if ((DateTime.Now - _phaseStart).TotalMilliseconds < CRAFT_TIMEOUT_MS)
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

    private WeaponCategory ResolveWeaponCategory(IEnumerable<WorldObject> inventory)
    {
        if (TryGetWieldedMissileCategory(inventory, out WeaponCategory category))
        {
            _lastWeaponCategory = category;
            return category;
        }

        return _lastWeaponCategory;
    }

    private bool TryGetWieldedMissileCategory(IEnumerable<WorldObject> inventory, out WeaponCategory category)
    {
        foreach (var item in inventory)
        {
            if (!IsPlayerWielded(item) || !LooksLikeMissileWeapon(item))
                continue;

            category = GetWeaponCategory(item.Name);
            return true;
        }

        category = WeaponCategory.Bow;
        return false;
    }

    private bool HasWieldedAmmo(IEnumerable<WorldObject> inventory, WeaponCategory category)
    {
        foreach (var item in inventory)
        {
            if (!IsPlayerWielded(item))
                continue;

            if (IsLooseAmmo(item, category))
                return true;
        }

        return false;
    }

    private bool IsPlayerWielded(WorldObject item)
    {
        if (item.WieldedLocation <= 0)
            return false;

        uint playerId = _host.GetPlayerId();
        if (playerId == 0)
            return false;

        return item.Wielder == 0 || item.Wielder == unchecked((int)playerId);
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

    private static bool IsLooseAmmo(WorldObject item, WeaponCategory category)
    {
        string n = item.Name;
        if (string.IsNullOrEmpty(n)) return false;
        string normalized = NormalizeItemName(n);
        if (normalized.Contains("bundle") || normalized.Contains("wrapped")) return false;
        if (normalized.Contains("arrowhead") || normalized.Contains("arrowshaft")) return false;
        if (normalized.Contains("quarrelhead") || normalized.Contains("quarrelshaft")) return false;
        if (normalized.Contains("darthead") || normalized.Contains("dartshaft")) return false;
        return category switch
        {
            WeaponCategory.Bow      => normalized.Contains("arrow"),
            WeaponCategory.Crossbow => normalized.Contains("quarrel") || normalized.Contains("bolt"),
            WeaponCategory.Atlatl   => normalized.Contains("dart"),
            _                       => false,
        };
    }

    private static int GetAmmoPriority(WorldObject item, WeaponCategory category)
    {
        if (string.IsNullOrEmpty(item.Name)) return 0;
        foreach (var recipe in AllRecipes.Where(r => r.Category == category).OrderByDescending(r => r.Priority))
            if (item.Name.Equals(recipe.OutputName, StringComparison.OrdinalIgnoreCase)) return recipe.Priority;
        return 1;
    }

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

    private static bool MatchesRecipeComponent(string itemName, string recipeName)
    {
        if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(recipeName))
            return false;

        string itemNorm = NormalizeItemName(itemName);
        string recipeNorm = NormalizeItemName(recipeName);
        if (itemNorm == recipeNorm)
            return true;

        return itemNorm.Contains(recipeNorm) || recipeNorm.Contains(itemNorm);
    }

    private static string NormalizeItemName(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int count = 0;
        foreach (char ch in value)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;

            buffer[count++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..count]);
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
        _phaseStart = DateTime.Now;
    }

    private void Reset(string reason)
    {
        if (State != CraftState.Idle || reason.IndexOf("bundle", StringComparison.OrdinalIgnoreCase) >= 0)
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
        _pendingBundlePairs.Clear();
    }

    private void ChatLog(string msg)
    {
        try { _host.WriteToChat($"[RynthAi Craft] {msg}", 1); } catch { }
    }
}
