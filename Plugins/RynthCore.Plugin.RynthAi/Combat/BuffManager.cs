using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public class BuffManager : IDisposable
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly SpellManager _spellManager;
    private readonly PlayerVitalsCache _vitals;

    private CharacterSkills? _charSkills;
    private WorldObjectCache? _worldObjectCache;

    private DateTime _lastCastAttempt = DateTime.MinValue;
    private bool _isForceRebuffing = false;
    private int _pendingSpellId = 0;

    private class RamTimerInfo
    {
        public DateTime Expiration;
        public int SpellLevel;
        public string SpellName = "";
    }

    /// <summary>
    /// Buff timer for an enchantment on a specific equipped item (armor/weapon).
    /// </summary>
    private class ItemBuffTimerInfo
    {
        public uint ObjectId;
        public string ObjectName = "";
        public DateTime Expiration;
        public int SpellLevel;
        public string SpellName = "";
    }

    /// <summary>
    /// Timer for an item spell (armor bane / Impenetrability) cast by this plugin.
    /// Recorded immediately on cast — independent of chat parsing and enchantment hooks.
    /// </summary>
    private class ItemSpellRecord
    {
        public DateTime CastAt;
        public DateTime ExpiresAt;
        public string SpellName = "";
        public int SpellLevel;
    }

    private readonly Dictionary<int, RamTimerInfo> _ramBuffTimers = new();
    /// <summary>Keyed by (objectId, spellFamily) packed as long.</summary>
    private readonly Dictionary<long, ItemBuffTimerInfo> _itemBuffTimers = new();
    /// <summary>Keyed by spell family. Tracks item-enchantment casts (armor banes, Impenetrability) directly.</summary>
    private readonly Dictionary<int, ItemSpellRecord> _itemSpellTimers = new();
    private string _buffTimerPath = "";

    private bool _isRechargingMana = false;
    private bool _isRechargingStamina = false;
    private bool _isHealingSelf = false;

    /// <summary>Current combat mode — updated from OnCombatModeChange.</summary>
    public int CurrentCombatMode { get; set; } = CombatMode.NonCombat;

    /// <summary>Client busy count — when > 0, don't send any game actions.</summary>
    public int BusyCount { get; set; }

    private readonly List<string> BaseCreatureBuffs = new()
    {
        "Strength Self", "Endurance Self", "Coordination Self",
        "Quickness Self", "Focus Self", "Willpower Self",
        "Magic Resistance Self", "Invulnerability Self", "Impregnability Self"
    };

    private readonly Dictionary<AcSkillType, string> CreatureSkillBuffs = new()
    {
        { AcSkillType.MeleeDefense, "Invulnerability Self" },
        { AcSkillType.MissileDefense, "Impregnability Self" },
        { AcSkillType.MagicDefense, "Magic Resistance Self" },
        { AcSkillType.HeavyWeapons, "Heavy Weapon Mastery" },
        { AcSkillType.LightWeapons, "Light Weapon Mastery" },
        { AcSkillType.FinesseWeapons, "Finesse Weapon Mastery" },
        { AcSkillType.TwoHandedCombat, "Two Handed Combat Mastery" },
        { AcSkillType.Shield, "Shield Mastery" },
        { AcSkillType.DualWield, "Dual Wield Mastery" },
        { AcSkillType.Recklessness, "Recklessness Mastery" },
        { AcSkillType.SneakAttack, "Sneak Attack Mastery" },
        { AcSkillType.DirtyFighting, "Dirty Fighting Mastery" },
        { AcSkillType.AssessCreature, "Monster Attunement" },
        { AcSkillType.AssessPerson, "Person Attunement" },
        { AcSkillType.ArcaneLore, "Arcane Enlightenment" },
        { AcSkillType.ArmorTinkering, "Armor Tinkering Expertise" },
        { AcSkillType.ItemTinkering, "Item Tinkering Expertise" },
        { AcSkillType.MagicItemTinkering, "Magic Item Tinkering Expertise" },
        { AcSkillType.WeaponTinkering, "Weapon Tinkering Expertise" },
        { AcSkillType.Salvaging, "Arcanum Salvaging" },
        { AcSkillType.Run, "Sprint" },
        { AcSkillType.Jump, "Jumping Mastery" },
        { AcSkillType.Loyalty, "Fealty" },
        { AcSkillType.Leadership, "Leadership Mastery" },
        { AcSkillType.Deception, "Deception Mastery" },
        { AcSkillType.Healing, "Healing Mastery" },
        { AcSkillType.Lockpick, "Lockpick Mastery" },
        { AcSkillType.Cooking, "Cooking Mastery" },
        { AcSkillType.Fletching, "Fletching Mastery" },
        { AcSkillType.Alchemy, "Alchemy Mastery" },
        { AcSkillType.ManaConversion, "Mana Conversion Mastery" },
        { AcSkillType.CreatureEnchantment, "Creature Enchantment Mastery" },
        { AcSkillType.ItemEnchantment, "Item Enchantment Mastery" },
        { AcSkillType.LifeMagic, "Life Magic Mastery" },
        { AcSkillType.WarMagic, "War Magic Mastery" },
        { AcSkillType.VoidMagic, "Void Magic Mastery" },
        { AcSkillType.Summoning, "Summoning Mastery" },
    };

    public BuffManager(RynthCoreHost host, LegacyUiSettings settings, SpellManager spellManager, PlayerVitalsCache vitals)
    {
        _host = host;
        _settings = settings;
        _spellManager = spellManager;
        _vitals = vitals;
    }

    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;
    public void SetWorldObjectCache(WorldObjectCache cache) => _worldObjectCache = cache;

    public void SetTimerPath(string charFolder)
    {
        _buffTimerPath = System.IO.Path.Combine(charFolder, "bufftimers.txt");
        LoadBuffTimers();
    }

    public void SaveBuffTimers()
    {
        if (string.IsNullOrEmpty(_buffTimerPath)) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_buffTimerPath)!);
            var lines = new List<string>();
            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                lines.Add($"ram|{kvp.Key}|{info.Expiration.Ticks}|{info.SpellLevel}|{info.SpellName}");
            }
            foreach (var kvp in _itemSpellTimers)
            {
                var info = kvp.Value;
                lines.Add($"item|{kvp.Key}|{info.CastAt.Ticks}|{info.ExpiresAt.Ticks}|{info.SpellLevel}|{info.SpellName}");
            }
            System.IO.File.WriteAllLines(_buffTimerPath, lines);
        }
        catch { }
    }

    public void LoadBuffTimers()
    {
        if (string.IsNullOrEmpty(_buffTimerPath)) return;
        if (!System.IO.File.Exists(_buffTimerPath)) return;
        try
        {
            _ramBuffTimers.Clear();
            _itemSpellTimers.Clear();
            foreach (string line in System.IO.File.ReadAllLines(_buffTimerPath))
            {
                string[] p = line.Split('|');

                if (p[0] == "item" && p.Length >= 6)
                {
                    DateTime castAt    = new DateTime(long.Parse(p[2]));
                    DateTime expiresAt = new DateTime(long.Parse(p[3]));
                    int level = int.Parse(p[4]);
                    string name = p[5];
                    if (expiresAt > DateTime.Now)
                    {
                        int family = new SpellInfo(0, name).Family;
                        _itemSpellTimers[family] = new ItemSpellRecord
                        {
                            CastAt = castAt, ExpiresAt = expiresAt,
                            SpellLevel = level, SpellName = name,
                        };
                    }
                    continue;
                }

                // Legacy single-prefix "family|ticks|level|name" or new "ram|family|ticks|level|name"
                int offset = (p[0] == "ram") ? 1 : 0;
                if (p.Length < offset + 4) continue;
                {
                    DateTime expiration = new DateTime(long.Parse(p[offset + 1]));
                    int level = int.Parse(p[offset + 2]);
                    string name = p[offset + 3];
                    int family = new SpellInfo(0, name).Family;
                    if (expiration > DateTime.Now)
                        _ramBuffTimers[family] = new RamTimerInfo { Expiration = expiration, SpellLevel = level, SpellName = name };
                }
            }
            int total = _ramBuffTimers.Count + _itemSpellTimers.Count;
            if (total > 0)
                _host.WriteToChat($"[RynthAi] Restored {_ramBuffTimers.Count} player + {_itemSpellTimers.Count} item spell timer(s).", 1);
        }
        catch { }
    }

    public void Dispose() { }

    public void ForceFullRebuff()
    {
        _isForceRebuffing = true;
        _ramBuffTimers.Clear();
        _itemSpellTimers.Clear();
        SaveBuffTimers();
        _host.WriteToChat("[RynthAi] Starting Force Rebuff...", 5);
    }

    public void CancelBuffing()
    {
        _isForceRebuffing = false;
        _isRechargingMana = false;
        _isRechargingStamina = false;
        _isHealingSelf = false;
        _pendingSpellId = 0;
        _host.WriteToChat("[RynthAi] Sequence cancelled.", 5);
    }

    public void OnHeartbeat()
    {
        if (!_settings.IsMacroRunning) return;

        if ((DateTime.Now - _lastCastAttempt).TotalMilliseconds < _settings.SpellCastIntervalMs)
        {
            _settings.BotAction = "Buffing";
            return;
        }

        // Client is busy — don't queue CastSpell/UseObject or we'll cause hourglass hang
        if (BusyCount > 0) return;

        if (CheckVitals())
        {
            _settings.BotAction = "Buffing";
            return;
        }

        if (_settings.EnableBuffing)
        {
            if (CheckAndCastSelfBuffs())
            {
                _settings.BotAction = "Buffing";
                return;
            }

            if (_isForceRebuffing)
            {
                _isForceRebuffing = false;
                _host.WriteToChat("[RynthAi] Force Rebuff Complete.", 1);
            }
        }

        if (_settings.BotAction == "Buffing")
            _settings.BotAction = "Default";
    }

    private bool CheckVitals()
    {
        int curHealthPct = _vitals.HealthPct;
        int curManaPct   = _vitals.ManaPct;
        int curStamPct   = _vitals.StaminaPct;

        if (curHealthPct <= 30 && curStamPct > 20) return AttemptVitalCast("Stamina to Health Self");

        if (curHealthPct <= _settings.HealAt)    _isHealingSelf = true;
        if (curHealthPct >= _settings.TopOffHP)  _isHealingSelf = false;
        if (curManaPct <= _settings.GetManaAt)   _isRechargingMana = true;
        if (curManaPct >= _settings.TopOffMana)  _isRechargingMana = false;
        if (curStamPct <= _settings.RestamAt)    _isRechargingStamina = true;
        if (curStamPct >= _settings.TopOffStam)  _isRechargingStamina = false;

        if (_isHealingSelf)
        {
            if (AttemptHealthKitUse()) return true;
            return AttemptVitalCast("Heal Self");
        }
        if (_isRechargingMana && curStamPct > 15) return AttemptVitalCast("Stamina to Mana Self");
        if (_isRechargingStamina) return AttemptVitalCast("Revitalize Self");

        return false;
    }

    private bool AttemptVitalCast(string baseName)
    {
        int spellId = FindBestSpellId(baseName, AcSkillType.LifeMagic);
        if (spellId == 0) return false;
        if (!EnsureMagicMode()) return true;
        _host.CastSpell((uint)_host.GetPlayerId(), spellId);
        _lastCastAttempt = DateTime.Now;
        return true;
    }

    private bool AttemptHealthKitUse()
    {
        if (_worldObjectCache == null) return false;

        foreach (var rule in _settings.ConsumableRules)
        {
            if (!rule.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_worldObjectCache[rule.Id] == null)
                continue;

            uint playerId = _host.GetPlayerId();
            if (playerId == 0) return false;
            _host.UseObjectOn(unchecked((uint)rule.Id), playerId);
            _lastCastAttempt = DateTime.Now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if any self-buff in the current buff list is missing/expired,
    /// without actually casting. Used by NeedToBuff meta condition.
    /// </summary>
    public bool NeedsAnyBuff()
    {
        if (!_settings.EnableBuffing) return false;
        List<string> desiredBuffs = BuildDynamicBuffList();
        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillForBuff(buffBaseName);
            if (!IsSkillUsable(castSkill)) continue;
            int spellId = FindBestSpellId(buffBaseName, castSkill);
            if (spellId == 0) continue;
            if (!IsBuffActive(spellId)) return true;
        }
        return false;
    }

    private bool CheckAndCastSelfBuffs()
    {
        List<string> desiredBuffs = BuildDynamicBuffList();

        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillForBuff(buffBaseName);
            if (!IsSkillUsable(castSkill)) continue;

            int spellId = FindBestSpellId(buffBaseName, castSkill);
            if (spellId == 0) continue;

            if (!IsBuffActive(spellId))
            {
                if (!EnsureMagicMode()) return true;

                _pendingSpellId = spellId;

                var spellInfo = SpellTableStub.GetById(spellId);
                if (spellInfo != null)
                {
                    _host.WriteToChat($"[RynthAi] Casting: {spellInfo.Name}", 5);

                    // Record item spell timers immediately on cast — don't rely on chat parsing
                    // or enchantment hooks (item enchantments fire on the item, not the player).
                    if (IsArmorEnchantment(spellInfo.Name))
                        RecordItemSpellCast(spellInfo);
                }

                _host.CastSpell((uint)_host.GetPlayerId(), spellId);
                _lastCastAttempt = DateTime.Now;
                return true;
            }
        }
        return false;
    }

    private int FindBestSpellId(string baseName, AcSkillType skill)
        => _spellManager.GetDynamicSelfBuffId(baseName, skill);

    private static bool IsArmorEnchantment(string name)
    {
        string[] armorSpells = {
            "Impenetrability", "Brogard's Defiance", "Acid Bane", "Olthoi's Bane",
            "Blade Bane", "Swordsman's Bane", "Swordman's Bane", "Bludgeoning Bane", "Tusker's Bane",
            "Flame Bane", "Inferno's Bane", "Frost Bane", "Gelidite's Bane",
            "Lightning Bane", "Astyrrian's Bane", "Piercing Bane", "Archer's Bane"
        };
        foreach (string s in armorSpells)
            if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// Called immediately when CheckAndCastSelfBuffs decides to cast an item spell.
    /// Records the timer optimistically — removed on fizzle.
    /// </summary>
    private void RecordItemSpellCast(SpellInfo spellInfo)
    {
        double duration = GetCustomSpellDuration(GetSpellLevel(spellInfo));
        var now = DateTime.Now;
        _itemSpellTimers[spellInfo.Family] = new ItemSpellRecord
        {
            CastAt    = now,
            ExpiresAt = now.AddSeconds(duration),
            SpellName = spellInfo.Name,
            SpellLevel = GetSpellLevel(spellInfo),
        };
        SaveBuffTimers();
    }

    private bool IsBuffActive(int spellId)
    {
        var targetSpell = SpellTableStub.GetById(spellId);
        if (targetSpell == null) return false;

        int targetLevel = GetSpellLevel(targetSpell);
        int rebufferMin = 5; // recast when < 5 min remaining

        // Item spells (armor banes, Impenetrability) tracked in their own dictionary.
        // Always check the timer first — RecordItemSpellCast() writes it immediately on cast
        // so it exists by the next heartbeat. _isForceRebuffing only matters when there is no
        // timer (i.e. hasn't been cast yet this session), and in that case both paths return false.
        if (IsArmorEnchantment(targetSpell.Name))
        {
            if (_itemSpellTimers.TryGetValue(targetSpell.Family, out ItemSpellRecord? itemTimer))
                if (itemTimer.SpellLevel >= targetLevel &&
                    DateTime.Now < itemTimer.ExpiresAt.AddMinutes(-rebufferMin))
                    return true;
            return false;
        }

        // Player buffs — use RAM timers
        if (_ramBuffTimers.TryGetValue(targetSpell.Family, out RamTimerInfo? timer))
        {
            if (DateTime.Now < timer.Expiration.AddMinutes(-rebufferMin))
            {
                if (timer.SpellLevel >= targetLevel) return true;
            }
        }

        if (_isForceRebuffing) return false;

        return false;
    }

    private static int GetSpellLevel(SpellInfo spell)
    {
        string n = spell.Name;
        if (n.StartsWith("Incantation")) return 8;
        if (n.Contains(" VII")) return 7;
        if (n.Contains(" VI")) return 6;
        if (n.Contains(" V")) return 5;
        if (n.Contains(" IV")) return 4;
        if (n.Contains(" III")) return 3;
        if (n.Contains(" II")) return 2;
        if (n.EndsWith(" I") || n.Contains(" I ")) return 1;

        if (n.Contains("Mastery") || n.Contains("Blessing") || n.Contains("Aura of") ||
            n.Contains("Intervention") || n.Contains("Trance") || n.Contains("Recovery") ||
            n.Contains("Robustify") || n.Contains("Persistence") || n.Contains("Robustification") ||
            n.Contains("Might of the Lugians") || n.Contains("Preservance") || n.Contains("Perseverance") ||
            n.Contains("Honed Control") || n.Contains("Hastening") || n.Contains("Inner Calm") ||
            n.Contains("Mind Blossom") || n.Contains("Infected Caress") || n.Contains("Elysa's Sight") ||
            n.Contains("Infected Spirit") || n.Contains("Atlan's Alacrity") || n.Contains("Cragstone's Will") ||
            n.Contains("Brogard's Defiance") || n.Contains("Olthoi's Bane") || n.Contains("Swordsman's Bane") ||
            n.Contains("Swordman's Bane") || n.Contains("Tusker's Bane") || n.Contains("Inferno's Bane") ||
            n.Contains("Gelidite's Bane") || n.Contains("Astyrrian's Bane") || n.Contains("Archer's Bane"))
            return 7;

        return 1;
    }

    private int GetArchmageEnduranceCount()
    {
        // TODO: Read augmentation count from AC object memory (key 238 on player object)
        return 0; // STUB
    }

    private double GetCustomSpellDuration(int spellLevel)
    {
        double baseSeconds = 1800;
        if (spellLevel == 6) baseSeconds = 2700;
        else if (spellLevel == 7) baseSeconds = 3600;
        else if (spellLevel == 8) baseSeconds = 5400;

        int augs = GetArchmageEnduranceCount();
        return baseSeconds * (1.0 + (augs * 0.20));
    }

    /// <summary>
    /// Reads active enchantments directly from client memory and refreshes _ramBuffTimers.
    /// Requires both HasReadPlayerEnchantments and HasGetServerTime to be available.
    /// Returns the number of timers updated, or -1 if the API is unavailable.
    /// </summary>
    public int RefreshFromLiveMemory()
    {
        if (!_host.HasReadPlayerEnchantments || !_host.HasGetServerTime)
            return -1;

        double serverNow = _host.GetServerTime();
        if (serverNow <= 0)
            return -1; // No time sync received yet

        const int MaxEnchantments = 512;
        uint[] spellIds    = new uint[MaxEnchantments];
        double[] expiryTimes = new double[MaxEnchantments];

        int count = _host.ReadPlayerEnchantments(spellIds, expiryTimes, MaxEnchantments);
        if (count < 0)
            return -1;

        // Preserve item enchantment timers (armor banes, Impenetrability, etc.)
        // that live on the item, not the player — ReadPlayerEnchantments won't
        // return them, so clearing would lose them every login.
        var preservedItemTimers = new Dictionary<int, RamTimerInfo>();
        foreach (var kvp in _ramBuffTimers)
        {
            if (kvp.Value.Expiration > DateTime.Now && IsArmorEnchantment(kvp.Value.SpellName))
                preservedItemTimers[kvp.Key] = kvp.Value;
        }

        _ramBuffTimers.Clear();
        for (int i = 0; i < count; i++)
        {
            var spellInfo = SpellTableStub.GetById((int)spellIds[i]);
            if (spellInfo == null) continue;

            double remainingSeconds = expiryTimes[i] - serverNow;
            if (remainingSeconds <= 0) continue;

            // Skip permanent enchantments — they don't need timers and
            // double.MaxValue would overflow DateTime.AddSeconds.
            if (remainingSeconds > 86400 * 365) continue;

            int level = GetSpellLevel(spellInfo);
            _ramBuffTimers[spellInfo.Family] = new RamTimerInfo
            {
                Expiration = DateTime.Now.AddSeconds(remainingSeconds),
                SpellLevel = level,
                SpellName  = spellInfo.Name,
            };
        }

        // Restore item enchantment timers that weren't covered by player enchantments
        foreach (var kvp in preservedItemTimers)
            _ramBuffTimers.TryAdd(kvp.Key, kvp.Value);

        SaveBuffTimers();
        return _ramBuffTimers.Count;
    }

    /// <summary>
    /// Scans all equipped items and reads their enchantment registries.
    /// Tracks item-specific buffs (Impenetrability, Banes, etc.) separately from player buffs.
    /// </summary>
    private void RefreshEquippedItemEnchantments(double serverNow)
    {
        _itemBuffTimers.Clear();

        if (_worldObjectCache == null || !_host.HasReadObjectEnchantments)
            return;

        try
        {
            // Snapshot the inventory to avoid iterating a changing collection
            var inventorySnapshot = new List<WorldObject>();
            foreach (var item in _worldObjectCache.GetDirectInventory())
                inventorySnapshot.Add(item);

            const int MaxEnchantments = 64;
            uint[] spellIds    = new uint[MaxEnchantments];
            double[] expiryTimes = new double[MaxEnchantments];
            int equippedCount = 0;

            foreach (var item in inventorySnapshot)
            {
                int wieldedSlot = item.WieldedLocation;
                if (wieldedSlot == 0) continue; // not equipped

                equippedCount++;
                uint objectId = unchecked((uint)item.Id);
                int count;
                try
                {
                    count = _host.ReadObjectEnchantments(objectId, spellIds, expiryTimes, MaxEnchantments);
                }
                catch
                {
                    continue; // skip items whose weenie can't be read
                }
                if (count <= 0) continue;

                for (int i = 0; i < count; i++)
                {
                    var spellInfo = SpellTableStub.GetById((int)spellIds[i]);
                    if (spellInfo == null) continue;

                    double remainingSeconds = expiryTimes[i] - serverNow;
                    if (remainingSeconds <= 0) continue;
                    if (remainingSeconds > 86400 * 365) continue; // permanent

                    long key = ((long)objectId << 32) | (uint)spellInfo.Family;
                    _itemBuffTimers[key] = new ItemBuffTimerInfo
                    {
                        ObjectId = objectId,
                        ObjectName = item.Name,
                        Expiration = DateTime.Now.AddSeconds(remainingSeconds),
                        SpellLevel = GetSpellLevel(spellInfo),
                        SpellName  = spellInfo.Name,
                    };
                }
            }

            _host.Log($"[RynthAi] Item enchant scan: {equippedCount} equipped, {_itemBuffTimers.Count} timed buffs");
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] Item enchant scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the number of tracked item buff timers (equipped items only).
    /// </summary>
    public int ItemBuffCount => _itemBuffTimers.Count;

    /// <summary>
    /// Checks whether a specific equipped item has a given buff family active.
    /// </summary>
    public bool HasItemBuff(uint objectId, string spellBaseName)
    {
        int family = new SpellInfo(0, spellBaseName).Family;
        long key = ((long)objectId << 32) | (uint)family;
        return _itemBuffTimers.TryGetValue(key, out var info) && info.Expiration > DateTime.Now;
    }

    public void PrintBuffDebug()
    {
        int augs = GetArchmageEnduranceCount();
        _host.WriteToChat($"[RynthAi] Archmage endurance count: {augs}", 1);

        // Try a live refresh of player enchantments
        double serverNow = _host.HasGetServerTime ? _host.GetServerTime() : 0;
        if (serverNow > 0 && _host.HasReadPlayerEnchantments)
        {
            int refreshed = RefreshFromLiveMemory();
            _host.WriteToChat($"[RynthAi] Live read: {(refreshed >= 0 ? $"{refreshed} enchantments" : "unavailable (no qualities ptr)")}", 1);

            // TODO: Item enchantment scanning disabled — crashes when reading inventory item
            // memory via ReadObjectEnchantments. Needs investigation (AV in native InqInt or
            // GetWeenieObject for certain inventory items). See memory note for details.
        }
        else
        {
            _host.WriteToChat($"[RynthAi] ServerTime={Math.Round(serverNow)} (no live read: serverTime=0 or API missing)", 1);
        }

        if (_ramBuffTimers.Count == 0 && _itemSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No buff timers active.", 1);
            return;
        }

        if (_itemSpellTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
            PrintItemSpellTimers();
        }

        if (_ramBuffTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Player buffs ({_ramBuffTimers.Count}) --", 1);
            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                double total = GetCustomSpellDuration(info.SpellLevel);
                TimeSpan left = info.Expiration - DateTime.Now;
                double passed = total - left.TotalSeconds;
                _host.WriteToChat(
                    $"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): {Math.Round(passed / 60, 1)}m passed, " +
                    $"{Math.Round(left.TotalMinutes, 1)}m left. Total: {total / 60}m", 1);
            }
        }
    }

    public void PrintItemBuffDebug()
    {
        if (_itemSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No item spell timers recorded yet.", 1);
            return;
        }
        _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
        PrintItemSpellTimers();
    }

    private void PrintItemSpellTimers()
    {
        foreach (var kvp in _itemSpellTimers)
        {
            var info = kvp.Value;
            TimeSpan left  = info.ExpiresAt - DateTime.Now;
            TimeSpan spent = DateTime.Now - info.CastAt;
            double totalMin = (info.ExpiresAt - info.CastAt).TotalMinutes;
            if (left.TotalSeconds <= 0)
                _host.WriteToChat($"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): EXPIRED", 1);
            else
                _host.WriteToChat(
                    $"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): " +
                    $"{Math.Round(spent.TotalMinutes, 1)}m elapsed, {Math.Round(left.TotalMinutes, 1)}m left / {Math.Round(totalMin, 0)}m total", 1);
        }
    }

    private List<string> BuildDynamicBuffList()
    {
        var step1_CreatureMastery = new List<string>();
        var step2_Focus           = new List<string>();
        var step3_Willpower       = new List<string>();
        var step4_OtherCreature   = new List<string>();
        var step5_LifeAndItem     = new List<string>();
        var step6_WeaponAuras     = new List<string>();
        var step7_ArmorBanes      = new List<string>();

        foreach (string attr in BaseCreatureBuffs)
        {
            if (attr == "Focus Self") step2_Focus.Add(attr);
            else if (attr == "Willpower Self") step3_Willpower.Add(attr);
            else step4_OtherCreature.Add(attr);
        }

        foreach (var kvp in CreatureSkillBuffs)
        {
            if (IsSkillUsable(kvp.Key))
            {
                if (kvp.Value.Contains("Creature Enchantment Mastery"))
                    step1_CreatureMastery.Add(kvp.Value);
                else if (!BaseCreatureBuffs.Contains(kvp.Value))
                    step4_OtherCreature.Add(kvp.Value);
            }
        }

        step5_LifeAndItem.AddRange(new List<string> {
            "Regeneration Self", "Rejuvenation Self", "Mana Renewal Self",
            "Armor Self", "Acid Protection Self", "Fire Protection Self",
            "Cold Protection Self", "Lightning Protection Self",
            "Blade Protection Self", "Piercing Protection Self",
            "Bludgeoning Protection Self", "Impregnability Self"
        });

        step6_WeaponAuras.AddRange(new List<string> {
            "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self",
            "Spirit Drinker Self", "Swift Killer Self", "Defender Self"
        });

        step7_ArmorBanes.AddRange(new List<string> {
            "Impenetrability", "Acid Bane", "Blade Bane", "Bludgeoning Bane",
            "Flame Bane", "Frost Bane", "Lightning Bane", "Piercing Bane"
        });

        var final = new List<string>();
        final.AddRange(step1_CreatureMastery);
        final.AddRange(step2_Focus);
        final.AddRange(step3_Willpower);
        final.AddRange(step4_OtherCreature);
        final.AddRange(step5_LifeAndItem);
        final.AddRange(step6_WeaponAuras);
        final.AddRange(step7_ArmorBanes);
        return final;
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment applied to the player.
    /// Refreshes timers from live memory when possible; falls back to the hook-supplied duration.
    /// </summary>
    public void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        var spellInfo = SpellTableStub.GetById((int)spellId);
        if (spellInfo == null)
            return;

        // Only clear pending if this enchantment matches what we were casting.
        // Armor/item enchantments fire on the item, not the player — clearing
        // _pendingSpellId unconditionally would lose track of pending item casts
        // and prevent the chat handler from recording their timers.
        if (_pendingSpellId != 0)
        {
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            if (pendingSpell != null && pendingSpell.Family == spellInfo.Family)
                _pendingSpellId = 0;
        }

        // Prefer live memory read — gives accurate remaining time for all enchantments.
        // But only trust it if the just-added buff actually shows up there yet.
        if (RefreshFromLiveMemory() >= 0 &&
            _ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? liveTimer) &&
            liveTimer.Expiration > DateTime.Now)
        {
            return;
        }

        RecordSpellTimer(spellInfo, durationSeconds);
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment removed from the player.
    /// Clears the buff timer for the corresponding spell family.
    /// </summary>
    public void OnEnchantmentRemoved(uint enchantmentId)
    {
        var spellInfo = SpellTableStub.GetById((int)enchantmentId);
        if (spellInfo == null) return;

        if (_ramBuffTimers.Remove(spellInfo.Family))
            SaveBuffTimers();
    }

    /// <summary>
    /// Call from RynthAiPlugin.OnChatWindowText when chat arrives.
    /// Handles fizzle/fail → resets pending cast state.
    /// </summary>
    public void OnChatWindowText(string text, int chatType)
    {
        string lower = text.ToLowerInvariant();

        if (lower.Contains("fizzle") || lower.Contains("fail") ||
            lower.Contains("component") || lower.Contains("lack the mana"))
        {
            // If we optimistically recorded an item spell timer, roll it back on failure
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null && IsArmorEnchantment(pendingSpell.Name))
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
            }
            _lastCastAttempt = DateTime.MinValue;
            _pendingSpellId = 0;
            return;
        }

        // "You cast Incantation of Flame Bane on Gelidite Robe, refreshing ..."
        // "You cast Strength Self VIII"
        if (!lower.StartsWith("you cast "))
            return;

        // Extract spell name: everything after "You cast " up to " on " or ","
        string afterCast = text.Substring(9); // skip "You cast "
        int onIdx = afterCast.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        int commaIdx = afterCast.IndexOf(',');
        int endIdx = afterCast.Length;
        if (onIdx > 0) endIdx = onIdx;
        if (commaIdx > 0 && commaIdx < endIdx) endIdx = commaIdx;
        string spellName = afterCast.Substring(0, endIdx).Trim();

        // Try to find the spell by name first (authoritative — this is what was actually cast)
        int spellId = SpellDatabase.GetIdByName(spellName);
        SpellInfo? spellInfo = spellId > 0 ? SpellTableStub.GetById(spellId) : null;

        // Fall back to pending spell if name lookup fails
        if (spellInfo == null && _pendingSpellId != 0)
            spellInfo = SpellTableStub.GetById(_pendingSpellId);

        if (spellInfo != null)
            RecordSpellTimer(spellInfo);

        _pendingSpellId = 0;
    }

    private void RecordSpellTimer(SpellInfo spellInfo, double durationSeconds = -1)
    {
        if (durationSeconds < 0)
            durationSeconds = GetCustomSpellDuration(GetSpellLevel(spellInfo));

        if (durationSeconds <= 0)
            return;

        int level = GetSpellLevel(spellInfo);
        _ramBuffTimers[spellInfo.Family] = new RamTimerInfo
        {
            Expiration = DateTime.Now.AddSeconds(durationSeconds),
            SpellLevel = level,
            SpellName = spellInfo.Name,
        };
        SaveBuffTimers();
    }

    // TODO: Replace with real skill training lookup from AC memory
    private bool IsSkillUsable(AcSkillType s) => _charSkills != null ? _charSkills[s].Training >= 2 : true;

    private static AcSkillType SkillForBuff(string name)
    {
        if (name.Contains("Impregnability") || name.Contains("Blood Drinker") ||
            name.Contains("Hermetic Link") || name.Contains("Heart Seeker") ||
            name.Contains("Spirit Drinker") || name.Contains("Swift Killer") ||
            name.Contains("Defender") || name.Contains("Impenetrability") ||
            name.Contains("Bane"))
            return AcSkillType.ItemEnchantment;

        if (name.Contains("Protection") || name.Contains("Armor") ||
            name.Contains("Regeneration") || name.Contains("Rejuvenation") ||
            name.Contains("Renewal") || name == "Harlune's Blessing" ||
            name.Contains("Stamina to Mana") || name.Contains("Revitalize") ||
            name.Contains("Stamina to Health") || name == "Heal Self")
            return AcSkillType.LifeMagic;

        return AcSkillType.CreatureEnchantment;
    }

    private bool EnsureMagicMode()
    {
        if (CurrentCombatMode == CombatMode.Magic) return true;

        int wandId = FindWandInItems();
        if (wandId == 0)
        {
            // No wand available — try to switch mode anyway (bare-handed magic).
            _host.ChangeCombatMode(CombatMode.Magic);
            _lastCastAttempt = DateTime.Now;
            return false;
        }

        // Check whether the wand is already wielded by the player (matches
        // CombatManager.EquipWeaponAndSetStance — PublicWeenieDesc._wielderID).
        bool alreadyWielded = false;
        if (_host.HasGetObjectWielderInfo)
        {
            uint playerId = _host.GetPlayerId();
            if (playerId != 0 &&
                _host.TryGetObjectWielderInfo((uint)wandId, out uint wielder, out _) &&
                wielder == playerId)
            {
                alreadyWielded = true;
            }
        }

        if (!alreadyWielded)
        {
            _host.UseObject((uint)wandId);
            _lastCastAttempt = DateTime.Now;
            return false; // Yield — let the server equip the wand
        }

        // Wand is wielded — safe to switch stance
        _host.ChangeCombatMode(CombatMode.Magic);
        _lastCastAttempt = DateTime.Now;
        return false; // Yield — let stance animation finish
    }

    /// <summary>
    /// Locates a wand to equip. Prefers a configured ItemRule classified as WandStaffOrb,
    /// falling back to the first WandStaffOrb found in the inventory cache.
    /// Mirrors CombatManager.FindWandInItems.
    /// </summary>
    private int FindWandInItems()
    {
        if (_worldObjectCache == null) return 0;

        // Prefer an explicitly configured wand from item rules
        foreach (var rule in _settings.ItemRules)
        {
            var wo = _worldObjectCache[rule.Id];
            if (wo != null && wo.ObjectClass == AcObjectClass.WandStaffOrb)
                return rule.Id;
        }

        // Fall back to inventory cache scan
        foreach (var wo in _worldObjectCache.GetInventory())
        {
            if (wo.ObjectClass == AcObjectClass.WandStaffOrb)
                return wo.Id;
        }

        return 0;
    }
}
