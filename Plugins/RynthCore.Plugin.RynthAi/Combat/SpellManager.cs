using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public class SpellManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private CharacterSkills? _charSkills;
    private uint _playerId;

    // Spell ids a cast empirically proved the char does NOT know (AC sent zero
    // chat back → silently dropped). This is the trustworthy "unknown" signal:
    // the engine IsSpellKnown oracle is known-broken (hardcoded VA, returns
    // true for unknown spells), so tier-down can't rely on it. BuffManager
    // feeds these in on a no-chat timeout; TryGetId then skips them so
    // GetDynamicSelfBuffId falls to the next lower (known) tier and caches it.
    private readonly HashSet<int> _unresolvableSpellIds = new();

    // Authoritative known-spell inventory: snapshot of the char's actual
    // spellbook, read by the engine ON AC's main thread (the per-spell
    // IsSpellKnown function is mis-bound and lies "true" for unknown spells;
    // the off-thread pump can't read AC live). Populated via host
    // ReadKnownSpells. Empty = snapshot cold → TryGetId degrades to the
    // legacy path (no regression).
    private readonly HashSet<int> _knownSpellIds = new();
    private DateTime _lastKnownRefreshUtc = DateTime.MinValue;
    private const double KnownRefreshThrottleMs = 3000;

    public Dictionary<string, int> SpellDictionary { get; private set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // Map base spell names to their exact Level 7 Lore counterparts
    private readonly Dictionary<string, string[]> LoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Strength", new[] { "Might of the Lugians" } },
        { "Endurance", new[] { "Preservance", "Perseverance" } },
        { "Coordination", new[] { "Honed Control" } },
        { "Quickness", new[] { "Hastening" } },
        { "Focus", new[] { "Inner Calm" } },
        { "Willpower", new[] { "Mind Blossom" } },
        { "Invulnerability", new[] { "Aura of Defense" } },
        { "Impregnability", new[] { "Aura of Deflection" } },
        { "Magic Resistance", new[] { "Aura of Resistance" } },
        { "Armor", new[] { "Executor's Blessing" } },
        { "Acid Protection", new[] { "Caustic Blessing" } },
        { "Blade Protection", new[] { "Blessing of the Blade Turner" } },
        { "Bludgeoning Protection", new[] { "Blessing of the Mace Turner" } },
        { "Cold Protection", new[] { "Icy Blessing" } },
        { "Fire Protection", new[] { "Fiery Blessing" } },
        { "Lightning Protection", new[] { "Storm's Blessing" } },
        { "Mana Renewal", new[] { "Battlemage's Blessing" } },
        { "Piercing Protection", new[] { "Blessing of the Arrow Turner" } },
        { "Regeneration", new[] { "Robustify" } },
        { "Rejuvenation", new[] { "Unflinching Persistence", "Unfinching Persistance" } },
        { "Heal", new[] { "Adja's Intervention" } },
        { "Revitalize", new[] { "Robustification" } },
        { "Stamina to Mana", new[] { "Meditative Trance" } },
        { "Stamina to Health", new[] { "Rushed Recovery" } },
        { "Monster Attunement", new[] { "Topheron's Blessing" } },
        { "Person Attunement", new[] { "Kaluhc's Blessing" } },
        { "Arcane Enlightenment", new[] { "Aliester's Blessing" } },
        { "Armor Tinkering Expertise", new[] { "Jibril's Blessing" } },
        { "Item Tinkering Expertise", new[] { "Yoshi's Blessing" } },
        { "Weapon Tinkering Expertise", new[] { "Koga's Blessing" } },
        { "Mana Conversion Mastery", new[] { "Nuhmidura's Blessing" } },
        { "Sprint", new[] { "Saladur's Blessing" } },
        { "Jumping Mastery", new[] { "Jahannan's Blessing" } },
        { "Fealty", new[] { "Odif's Blessing", "Odif's Boon" } },
        { "Leadership Mastery", new[] { "Ar-Pei's Blessing" } },
        { "Deception Mastery", new[] { "Ketnan's Blessing" } },
        { "Healing Mastery", new[] { "Avalenne's Blessing" } },
        { "Lockpick Mastery", new[] { "Oswald's Blessing" } },
        { "Cooking Mastery", new[] { "Morimoto's Blessing" } },
        { "Fletching Mastery", new[] { "Lilitha's Blessing" } },
        { "Alchemy Mastery", new[] { "Silencia's Blessing" } },
        { "Creature Enchantment Mastery", new[] { "Adja's Blessing" } },
        { "Item Enchantment Mastery", new[] { "Celcynd's Blessing" } },
        { "Life Magic Mastery", new[] { "Harlune's Blessing" } },
        { "War Magic Mastery", new[] { "Hieromancer's Blessing" } },
        { "Blood Drinker", new[] { "Aura of Infected Caress" } },
        { "Hermetic Link", new[] { "Aura of Mystic's Blessing" } },
        { "Heart Seeker", new[] { "Aura of Elysa's Sight" } },
        { "Spirit Drinker", new[] { "Aura of Infected Spirit Carress", "Aura of Infected Spirit Caress" } },
        { "Swift Killer", new[] { "Aura of Atlan's Alacrity" } },
        { "Defender", new[] { "Aura of Cragstone's Will" } },
        { "Impenetrability", new[] { "Brogard's Defiance" } },
        { "Acid Bane", new[] { "Olthoi's Bane" } },
        { "Blade Bane", new[] { "Swordsman's Bane", "Swordman's Bane" } },
        { "Bludgeoning Bane", new[] { "Tusker's Bane" } },
        { "Flame Bane", new[] { "Inferno's Bane" } },
        { "Frost Bane", new[] { "Gelidite's Bane" } },
        { "Lightning Bane", new[] { "Astyrrian's Bane" } },
        { "Piercing Bane", new[] { "Archer's Bane" } },
    };

    public SpellManager(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;
    public void SetPlayerId(uint playerId) => _playerId = playerId;

    public void InitializeNatively()
    {
        try
        {
            if (!SpellDatabase.IsLoaded)
                SpellDatabase.Load(msg => _host.WriteToChat(msg, 2));
            SpellDictionary = SpellDatabase.BuildNameToIdMap();
            _host.WriteToChat($"[RynthAi] Magic System Online: {SpellDictionary.Count} spells loaded.", 1);
        }
        catch (Exception ex)
        {
            _host.WriteToChat("[RynthAi] Spell init error: " + ex.Message, 2);
        }
    }

    /// <summary>
    /// Returns the highest castable spell tier for combat (offensive/debuff) casts.
    /// Uses the combat thresholds in LegacyUiSettings (MinSkillLevelTier1..8) which are
    /// padded above AC's hard minimums (1/50/100/150/200/250/300/350) to avoid fizzles
    /// during fights.
    /// </summary>
    public int GetHighestSpellTier(AcSkillType skill)
        => ComputeTier(skill,
            _settings.MinSkillLevelTier1, _settings.MinSkillLevelTier2,
            _settings.MinSkillLevelTier3, _settings.MinSkillLevelTier4,
            _settings.MinSkillLevelTier5, _settings.MinSkillLevelTier6,
            _settings.MinSkillLevelTier7, _settings.MinSkillLevelTier8);

    /// <summary>
    /// Same as <see cref="GetHighestSpellTier"/> but uses the Buffing-specific thresholds
    /// (BuffMinSkillLevelTier1..8). Buffing fizzles are inexpensive — just retry next
    /// heartbeat — so these defaults sit closer to AC's hard minimums, ensuring a
    /// 390-buffed caster actually receives Incantation-tier self buffs.
    /// </summary>
    public int GetHighestBuffSpellTier(AcSkillType skill)
        => ComputeTier(skill,
            _settings.BuffMinSkillLevelTier1, _settings.BuffMinSkillLevelTier2,
            _settings.BuffMinSkillLevelTier3, _settings.BuffMinSkillLevelTier4,
            _settings.BuffMinSkillLevelTier5, _settings.BuffMinSkillLevelTier6,
            _settings.BuffMinSkillLevelTier7, _settings.BuffMinSkillLevelTier8);

    private int ComputeTier(AcSkillType skill, int t1, int t2, int t3, int t4, int t5, int t6, int t7, int t8)
    {
        int buffed = _charSkills != null ? _charSkills[skill].Buffed : 250;
        if (buffed >= t8) return 8;
        if (buffed >= t7) return 7;
        if (buffed >= t6) return 6;
        if (buffed >= t5) return 5;
        if (buffed >= t4) return 4;
        if (buffed >= t3) return 3;
        if (buffed >= t2) return 2;
        return 1;
    }

    private static string GetRomanNumeral(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV",
        5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => "I"
    };

    public int GetDynamicSelfBuffId(string baseSpellName, AcSkillType magicSkill)
    {
        RefreshKnownSpells(); // keep the authoritative inventory current (throttled)

        // Self-buffs use the buffing-specific thresholds so the user can tune
        // buffing tier independently from combat tier.
        int maxTier = GetHighestBuffSpellTier(magicSkill);
        string cleanBase = baseSpellName.Replace(" Self", "").Trim();

        for (int tier = maxTier; tier >= 1; tier--)
        {
            if (tier == 8)
            {
                if (TryGetId($"Incantation of {cleanBase} Self", out int id1)) return id1;
                if (TryGetId($"Incantation of {cleanBase}", out int id2)) return id2;
                if (TryGetId($"Aura of Incantation of {cleanBase} Self", out int id3)) return id3;
                if (TryGetId($"Aura of Incantation of {cleanBase}", out int id4)) return id4;
            }
            else if (tier == 7)
            {
                if (LoreNames.TryGetValue(cleanBase, out string[]? lores))
                    foreach (string lore in lores)
                        if (TryGetId(lore, out int idL)) return idL;

                if (TryGetId($"{cleanBase} Self VII", out int id7a)) return id7a;
                if (TryGetId($"{cleanBase} VII", out int id7b)) return id7b;
                if (TryGetId($"Aura of {cleanBase} Self VII", out int id7c)) return id7c;
                if (TryGetId($"Aura of {cleanBase} VII", out int id7d)) return id7d;
            }
            else
            {
                string numeral = GetRomanNumeral(tier);
                if (TryGetId($"{cleanBase} Self {numeral}", out int idNum1)) return idNum1;
                if (TryGetId($"{cleanBase} {numeral}", out int idNum2)) return idNum2;
                if (TryGetId($"Aura of {cleanBase} Self {numeral}", out int idNum3)) return idNum3;
                if (TryGetId($"Aura of {cleanBase} {numeral}", out int idNum4)) return idNum4;
            }
        }
        return 0;
    }

    /// <summary>
    /// Record that a spell id is not actually castable by this character
    /// (empirically: a cast of it got zero chat back). Resolution will skip
    /// it from now on so the family falls to the next lower tier.
    /// </summary>
    public void MarkSpellUnresolvable(int spellId)
    {
        if (spellId > 0) _unresolvableSpellIds.Add(spellId);
    }

    /// <summary>Persisted by BuffManager so the tier-7→6 drop is learned once
    /// (first rebuff per char) then instant on every later session/RL.</summary>
    public IReadOnlyCollection<int> UnresolvableSpellIds => _unresolvableSpellIds;

    /// <summary>
    /// Public, authoritative name→id resolver shared by the combat war/void
    /// path so it gets the SAME logic the buff path uses: unresolvable skip +
    /// the main-thread <c>_knownSpellIds</c> snapshot (the engine IsSpellKnown
    /// oracle is mis-bound and lies "true" for unknown spells). A char that
    /// doesn't know e.g. the tier-8 Incantation resolves false here, so the
    /// caller's tier loop drops to a tier it actually knows. Call
    /// <see cref="RefreshKnownSpells"/> on the consuming path first to keep the
    /// snapshot warm even when not buffing.
    /// </summary>
    public bool TryResolveSpellId(string exactName, out int spellId)
        => TryGetId(exactName, out spellId);

    /// <summary>
    /// Combat-facing resolver. Deterministic: name → id via the dictionary,
    /// then REQUIRES membership in the authoritative warm spellbook snapshot.
    /// Deliberately does NOT consult <c>_unresolvableSpellIds</c> (the
    /// empirical no-chat blacklist) or the mis-bound engine oracle — combat
    /// selection is purely predictive (known ∧ scarab ∧ tier ∧ shape), so
    /// nothing here can self-poison. Cold snapshot ⇒ false (combat waits for
    /// the snapshot to warm rather than blind-casting an unknown spell).
    /// </summary>
    public bool TryResolveKnownSpellId(string exactName, out int spellId)
    {
        spellId = 0;
        if (!SpellDictionary.TryGetValue(exactName, out int id)) return false;
        if (_knownSpellIds.Count == 0) return false;     // cold — don't guess
        if (!_knownSpellIds.Contains(id)) return false;  // char doesn't know it
        spellId = id;
        return true;
    }

    /// <summary>
    /// True once the authoritative main-thread spellbook snapshot has been
    /// populated. While false (cold: pre-login, fast cold-login before the
    /// qualities ptr seeds, or an engine without ReadKnownSpells) TryGetId
    /// degrades to the mis-bound IsSpellKnown oracle, which lies "true" for
    /// unknown spells. The combat resolver uses this to refuse to blind-pick
    /// the tier-8 Incantation during a cold window.
    /// </summary>
    public bool IsKnownSnapshotWarm => _knownSpellIds.Count > 0;

    /// <summary>
    /// True if the authoritative main-thread spellbook snapshot contains this
    /// id. Only meaningful when <see cref="IsKnownSnapshotWarm"/> is true.
    /// Used by the combat no-chat valve to refuse to blacklist a spell the
    /// char demonstrably KNOWS — no chat for a known spell means the cast
    /// didn't execute (off-thread busy-count leak), NOT "unknown/no comps".
    /// </summary>
    public bool IsKnownSpellId(int id) => _knownSpellIds.Contains(id);

    /// <summary>
    /// Refresh the known-spell inventory from the engine's main-thread
    /// spellbook snapshot (host ReadKnownSpells). Throttled. Safe no-op if the
    /// host API is absent (old engine) or the snapshot is still cold — the set
    /// just stays empty and TryGetId keeps using the legacy path.
    /// </summary>
    public void RefreshKnownSpells()
    {
        if ((DateTime.UtcNow - _lastKnownRefreshUtc).TotalMilliseconds < KnownRefreshThrottleMs)
            return;
        _lastKnownRefreshUtc = DateTime.UtcNow;
        try
        {
            if (!_host.HasReadKnownSpells) return;

            // Grow the buffer until it is NOT completely filled. A full buffer means
            // the spellbook was TRUNCATED — and because higher spell tiers carry
            // higher ids, the dropped entries are exactly the high-tier self-buffs
            // (Strength Self VI/VII/VIII, Incantations…). That collapses the tier
            // walk to the low-id tier that survived (Strength Self I = id 2). A full
            // buffing mage knows well over 2048 spells, so a fixed 2048 cap is the
            // real "casts level 1" bug. Cap at 65536 as a sanity bound.
            uint[] buf;
            int n;
            int cap = 8192;
            while (true)
            {
                buf = new uint[cap];
                n = _host.ReadKnownSpells(buf, buf.Length);
                if (n < cap || cap >= 65536) break; // got the whole spellbook (or hit sanity cap)
                cap *= 2;
            }
            if (n <= 0) return; // cold/unavailable — keep whatever we have
            var fresh = new HashSet<int>();
            int take = Math.Min(n, buf.Length);
            for (int i = 0; i < take; i++)
                if (buf[i] != 0) fresh.Add(unchecked((int)buf[i]));
            if (fresh.Count == 0) return;
            _knownSpellIds.Clear();
            foreach (int id in fresh) _knownSpellIds.Add(id);
            // Purge stale blacklist entries. The empirical blacklist accumulates
            // false positives from lag-induced no-chat timeouts and persists to
            // disk across sessions — higher tiers get permanently suppressed,
            // causing tier walk-down to level 1. Now that the snapshot is
            // authoritative, any blacklisted spell the char demonstrably knows
            // was a false positive; remove it so the correct tier resolves.
            _unresolvableSpellIds.RemoveWhere(id => _knownSpellIds.Contains(id));
        }
        catch { }
    }

    private bool TryGetId(string exactName, out int spellId)
    {
        if (!SpellDictionary.TryGetValue(exactName, out spellId))
            return false;

        // Authoritative snapshot wins unconditionally when warm: if the char
        // demonstrably knows the spell, trust that over the empirical blacklist
        // (which can be poisoned by lag-induced no-chat timeouts).
        if (_knownSpellIds.Count > 0)
        {
            if (_knownSpellIds.Contains(spellId)) return true;
            spellId = 0;
            return false;
        }

        // Snapshot cold — fall back to the empirical blacklist. The mis-bound
        // engine IsSpellKnown is still bounded by _unresolvableSpellIds so the
        // tier walk doesn't spin on silently-dropped unknown spells.
        if (_unresolvableSpellIds.Contains(spellId))
        {
            spellId = 0;
            return false;
        }

        if (_playerId != 0 && _host.HasIsSpellKnown)
        {
            _host.IsSpellKnown(_playerId, unchecked((uint)spellId), out bool known);
            if (!known) { spellId = 0; return false; }
            return true;
        }

        if (SpellDatabase.IsSpellKnown(spellId)) return true;
        spellId = 0;
        return false;
    }
}
