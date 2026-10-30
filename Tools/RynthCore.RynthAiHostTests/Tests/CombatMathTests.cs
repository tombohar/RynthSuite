using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Combat numbers: our damage read out of the combat log (CombatManager.TryParseOutgoingDamage,
// the incoming-hit pattern and the element words), weapon choice by the monster's weakness
// (CombatManager.ChooseWeapon, with the server resist table the plugin ships), and the target
// scan's range ring (ScanNearbyTargets: MonsterRange, the disengage distance, 3D and
// cross-landblock distances, the sort order and the filters).
internal static class CombatMathTests
{
    private const uint Player = 0x50000C01;

    public static void Register(Runner r)
    {
        r.Add("damage text: our hits, ACE and retail wording", OutgoingDamage);
        r.Add("damage text: lines that are not our damage", NotOurDamage);
        r.Add("damage text: incoming hits and element words", IncomingAndElements);
        r.Add("weapon choice: the Monsters rule's damage type first", WeaponByRule);
        r.Add("weapon choice: the element the monster is weakest to", WeaponByWeakness);
        r.Add("weapon choice: a bow character isn't switched to a wand", WeaponSameKind);
        r.Add("weapon choice: no weakness data, first usable weapon; casters need War or Void", WeaponFallbacks);
        r.Add("target range: only monsters within MonsterRange", ScanRing);
        r.Add("target range: the engaged target is kept out to the disengage distance", ScanDisengage);
        r.Add("target range: height counts, landblock edges don't", ScanDistanceMath);
        r.Add("target range: closest first, then the one in front", ScanOrder);
        r.Add("target range: dead, unattackable, never-attack and spell projectiles are skipped", ScanFilters);

        r.Add("damage text: a monster hitting our body part isn't our damage", BodyPartHitIsNotOurs);   // was a known failure; fixed by feature/warchain-fix (the line must start with "You")
        r.Add("weapon choice: a weapon the character no longer has is skipped", WeaponMissingSkipped);   // was a known failure; fixed by 996b2a5
    }

    // ── Damage text ───────────────────────────────────────────────────────────

    private static int? Parse(string text) => CombatManager.TryParseOutgoingDamage(text, out int n) ? n : null;

    private static void OutgoingDamage()
    {
        Check.Eq(Parse("You blast Drudge Skulker for 153 points with Whirling Blade VII."), 153, "ACE war magic");
        Check.Eq(Parse("You scorch Mosswart Worshipper for 1,234 points of fire damage!"), 1234, "retail, thousands comma");
        Check.Eq(Parse("Critical hit!  You blast Drudge Skulker for 88 points with Flame Bolt VI."), 88, "a critical hit line");
        Check.Eq(Parse("You slash Banderling Scout for 12 points of slashing damage!"), 12, "a melee hit");
        Check.Eq(Parse("You frost the Olthoi Worker for 1 point with Frost Bolt I."), null,
            "\"1 point\" (singular) isn't read: the parser anchors on \"points\" (pinned)");
        Check.Eq(Parse("You blast Tusker Guard for 999,999 points with Incantation of Force Bolt."), 999999, "just under the 1,000,000 cap");
    }

    private static void NotOurDamage()
    {
        Check.Eq(Parse("Drudge Skulker hits you for 12 points of bludgeoning damage!"), null, "a monster hitting us");
        Check.Eq(Parse("Mu-miyah Grave Robber scorches you for 30 points with Flame Bolt VI."), null, "a monster's spell hitting us");
        Check.Eq(Parse("You drain 45 points of mana from Drudge Skulker."), null, "a vital drain");
        Check.Eq(Parse("You gain 1,000 points of experience."), null, "experience");
        Check.Eq(Parse("You heal yourself for 64 points."), null, "a heal (no 'with'/'of ... damage')");
        Check.Eq(Parse("You blast Drudge Skulker for 0 points with Flame Bolt I."), null, "zero");
        Check.Eq(Parse("You blast Drudge Skulker for 1,000,001 points with Flame Bolt I."), null, "over the sanity cap");
        Check.Eq(Parse("You blast Drudge Skulker for many points with Flame Bolt I."), null, "no number");
        Check.Eq(Parse("Drudge Skulker evaded your attack."), null, "an evade");
        Check.Eq(Parse(""), null, "empty");
    }

    private static void BodyPartHitIsNotOurs()
    {
        Check.Eq(Parse("Olthoi Swarm Harvester blisters your lower leg for 12 points of acid damage!"), null, "a hit on our lower leg");
        Check.Eq(Parse("The Drudge Slinker bashes your head for 5 points of bludgeoning damage!"), null, "a hit on our head");
    }

    private static void IncomingAndElements()
    {
        var m = CombatManager.IncomingDamage.Match("Olthoi Swarm Harvester blisters your lower leg for 12 points of acid damage!");
        Check.True(m.Success, "a body-part hit matches the incoming pattern");
        Check.Eq(m.Groups["name"].Value, "Olthoi Swarm Harvester", "attacker");
        Check.Eq(m.Groups["n"].Value, "12", "amount");
        Check.Eq(m.Groups["type"].Value, "acid", "type");
        var the = CombatManager.IncomingDamage.Match("Critical hit! The Drudge Slinker slashes you for 20 points of slashing damage!");
        Check.Eq(the.Groups["name"].Value, "Drudge Slinker", "'Critical hit!' and 'The' are dropped from the name");
        Check.False(CombatManager.IncomingDamage.IsMatch("You slash Drudge Slinker for 20 points of slashing damage!"), "our own hit is not incoming");

        var words = new Dictionary<string, string>
        {
            ["slashing"] = "Slash", ["slash"] = "Slash", ["piercing"] = "Pierce", ["bludgeoning"] = "Bludgeon",
            ["fire"] = "Fire", ["cold"] = "Cold", ["acid"] = "Acid", ["electric"] = "Lightning", ["LIGHTNING"] = "Lightning",
            ["nether"] = "Nether", ["health"] = "Physical",
        };
        foreach (var (w, e) in words) Check.Eq(CombatManager.ElementFromWord(w), e, $"word '{w}'");

        var types = new (uint Flags, string Element)[]
        {
            (0x1, "Slash"), (0x2, "Pierce"), (0x4, "Bludgeon"), (0x8, "Cold"), (0x10, "Fire"), (0x20, "Acid"),
            (0x40, "Lightning"), (0x400, "Nether"), (0x0, "Physical"), (0x3, "Slash"), (0x14, "Bludgeon"), (0x30, "Fire"),
        };
        foreach (var (f, e) in types) Check.Eq(CombatManager.ElementFromDamageType(f), e, $"damage type 0x{f:X}");
    }

    // ── Weapon choice ─────────────────────────────────────────────────────────

    private const uint Sword = 0x60000011, Axe = 0x60000012, Mace = 0x60000013, Bow = 0x60000014, Wand = 0x60000015,
                       Missing = 0x60000099, Monster = 0x80000101;

    private sealed class WeaponRig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public WorldObject Target = new(unchecked((int)Monster), "Qzxv Blorpington", AcObjectClass.Monster);

        public WeaponRig(bool attackMagic = true)
        {
            FakeHost.Reset();
            void Weapon(uint id, string name, uint flags) { FakeHost.Names[id] = name; FakeHost.ItemTypes[id] = flags; }
            Weapon(Sword, "Rune Sword", 0x1);
            Weapon(Axe, "Battle Axe", 0x1);
            Weapon(Mace, "Heavy Mace", 0x1);
            Weapon(Bow, "Yew Longbow", 0x100);
            Weapon(Wand, "Ivory Wand", 0x8000);
            if (!attackMagic)
            {
                FakeHost.Skills[(Player, 34)] = (10, 1);   // War Magic untrained
                FakeHost.Skills[(Player, 43)] = (10, 1);   // Void Magic untrained
            }
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, new[] { Sword, Axe, Mace, Bow, Wand });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            if (!attackMagic)
            {
                var skills = new CharacterSkills(host);
                skills.SetPlayerId(Player);
                C.SetCharacterSkills(skills);
            }
        }

        public void Items(params (uint Id, string Element)[] items)
            => S.ItemRules = items.Select(i => new ItemRule { Id = unchecked((int)i.Id), Name = FakeHost.Names.GetValueOrDefault(i.Id, "?"), Element = i.Element }).ToList();

        /// <summary>Makes the target a monster from the shipped server table (wcid + name).</summary>
        public void ServerMonster(uint wcid, string name)
        {
            FakeHost.Wcids[Monster] = wcid;
            Target = new WorldObject(unchecked((int)Monster), name, AcObjectClass.Monster);
        }

        public (uint Id, string Desired, string Source) Choose(MonsterRule? rule = null)
        {
            string desired = "Auto";
            int id = C.ChooseWeapon(Target, rule, ref desired, out string source);
            return (unchecked((uint)id), desired, source);
        }
    }

    private static (uint Wcid, string Name) OlthoiWorker()
    {
        // The first row of the shipped creature_resists.tsv (wcid 3), found through the plugin itself.
        var rank = CreatureWeakness.Rank(3, "Olthoi Worker", 0, null);
        return rank?.Source == "server data" ? (3u, "Olthoi Worker") : (0u, "");
    }

    private static void WeaponByRule()
    {
        var rig = new WeaponRig();
        rig.Items((Sword, "Slash"), (Axe, "Fire"));
        var pick = rig.Choose(new MonsterRule { Name = "Qzxv", DamageType = "Fire" });
        Check.Eq(pick.Id, Axe, "the rule says Fire: the Fire weapon");
        Check.Eq(pick.Source, "Monsters rule Fire", "source");
        Check.Eq(pick.Desired, "Fire", "desired element");

        var auto = rig.Choose(new MonsterRule { Name = "Qzxv", DamageType = "Auto" });
        Check.Eq(auto.Id, Sword, "Auto: no rule element (and no weakness data): the first weapon");
        var none = rig.Choose(new MonsterRule { Name = "Qzxv", DamageType = "Cold" });
        Check.Eq(none.Id, Sword, "no Cold weapon listed: falls through to the first weapon");
    }

    private static void WeaponByWeakness()
    {
        var (wcid, name) = OlthoiWorker();
        Check.True(wcid != 0, "the shipped table has the Olthoi Worker");
        if (wcid == 0) return;
        var rank = CreatureWeakness.Rank(wcid, name, 0, null)!;

        var rig = new WeaponRig();
        rig.ServerMonster(wcid, name);
        // One melee weapon per element, listed from least to most damaging, so list order can't win.
        var byDamage = rank.Order.Select(o => o.Element).ToList();
        string worst = byDamage.Last(), best = byDamage.First(), second = byDamage[1];
        rig.Items((Sword, worst), (Axe, second), (Mace, best));
        var pick = rig.Choose();
        Check.Eq(pick.Id, Mace, $"the {best} weapon ({best} hurts it most)");
        Check.Eq(pick.Desired, best, "desired element");
        Check.Eq(pick.Source, $"weak to {best} (server data)", "source");

        rig.Items((Sword, worst), (Axe, second));
        Check.Eq(rig.Choose().Id, Axe, $"no {best} weapon: the {second} one, next down the list");

        rig.Items((Sword, "Electric"), (Axe, "Blade"));
        string electricAt = byDamage.IndexOf("Lightning") < byDamage.IndexOf("Slash") ? "Sword" : "Axe";
        uint expected = electricAt == "Sword" ? Sword : Axe;
        Check.Eq(rig.Choose().Id, expected, "Items elements are normalised (Electric = Lightning, Blade = Slash)");
    }

    private static void WeaponSameKind()
    {
        var (wcid, name) = OlthoiWorker();
        if (wcid == 0) { Check.True(false, "no server row"); return; }
        string best = CreatureWeakness.Rank(wcid, name, 0, null)!.Order[0].Element;
        string worst = CreatureWeakness.Rank(wcid, name, 0, null)!.Order[^1].Element;

        var rig = new WeaponRig();
        rig.ServerMonster(wcid, name);
        rig.Items((Bow, worst), (Wand, best));   // a bow character with a buff wand listed
        var pick = rig.Choose();
        Check.Eq(pick.Id, Bow, "the wand's element suits it better, but the character fights with a bow");
        Check.Eq(pick.Source, $"weak to {worst} (server data)", "the bow is picked for its own element");

        rig.Items((Sword, worst), (Bow, best), (Axe, best));
        Check.Eq(rig.Choose().Id, Axe, "a melee character: the melee weapon of the best element, not the bow");
    }

    private static void WeaponFallbacks()
    {
        var rig = new WeaponRig();
        rig.Items((Mace, "Bludgeon"), (Sword, "Slash"));
        var pick = rig.Choose();
        Check.Eq(pick.Id, Mace, "unknown monster: the first Items weapon");
        Check.Eq(pick.Source, "ItemRules", "source");

        rig.Items();
        Check.Eq(rig.Choose().Id, 0u, "an empty Items list: no weapon");

        var noMagic = new WeaponRig(attackMagic: false);
        noMagic.Items((Wand, "Fire"), (Sword, "Slash"));
        Check.Eq(noMagic.Choose().Id, Sword, "no War or Void Magic: the wand is skipped");
        var mage = new WeaponRig(attackMagic: true);
        mage.Items((Wand, "Fire"), (Sword, "Slash"));
        Check.Eq(mage.Choose().Id, Wand, "skills unread count as trained: the wand is used");
    }

    private static void WeaponMissingSkipped()
    {
        var rig = new WeaponRig();
        rig.Items((Missing, "Slash"), (Sword, "Slash"));
        var pick = rig.Choose();
        Check.Eq(pick.Id, Sword, "the traded-away weapon at the top of the list is skipped");
    }

    // ── Target range ──────────────────────────────────────────────────────────

    private const uint Lb = 0xA9B40000;

    private sealed class ScanRig
    {
        public readonly LegacyUiSettings S = new() { MonsterRange = 20 };
        public CombatManager C = null!;
        private readonly List<uint> _ids = new();
        private readonly Dictionary<uint, float> _health = new();
        private uint _next = 0x80000201;

        public ScanRig()
        {
            FakeHost.Reset();
            Place(Player, "Me", Lb | 0x1C, 96, 96, 0, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.HeadingDeg = 0;   // facing north
        }

        private void Place(uint id, string name, uint cell, float x, float y, float z, bool creature = true)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (cell, x, y, z);
            if (creature) FakeHost.ItemTypes[id] = 0x10;
        }

        /// <summary>A monster <paramref name="north"/>/<paramref name="east"/> m from the player (x east, y north).</summary>
        public uint Mob(string name, double north, double east, double up = 0, float? health = null, uint cell = Lb | 0x1C)
        {
            uint id = _next++;
            Place(id, name, cell, (float)(96 + east), (float)(96 + north), (float)up);
            _ids.Add(id);
            if (health != null) _health[id] = health.Value;
            return id;
        }

        public uint MobAt(string name, uint cell, float x, float y)
        {
            uint id = _next++;
            Place(id, name, cell, x, y, 0);
            _ids.Add(id);
            return id;
        }

        public List<CombatManager.ScannedTarget> Scan(Action<CombatManager>? before = null)
        {
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, _ids, _health);
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            before?.Invoke(C);
            C.ScanNearbyTargets();
            return C.ScannedTargets.ToList();
        }
    }

    private static string Names(IEnumerable<CombatManager.ScannedTarget> ts) => string.Join(",", ts.Select(t => t.Name));

    private static void ScanRing()
    {
        var rig = new ScanRig();
        rig.Mob("Near", 5, 0);
        rig.Mob("Inside", 19.9, 0);
        rig.Mob("Outside", 20.1, 0);
        var got = rig.Scan();
        Check.Eq(Names(got), "Near,Inside", "20 m range: 5 and 19.9 in, 20.1 out");
        Check.Near(got.FirstOrDefault().Distance, 5, 1e-3, "distance in metres");
        Check.Eq(rig.C.LastScanTotalMonsters, 3, "three monsters seen");
        Check.Eq(rig.C.LastScanInRing, 2, "two in the ring");
        Check.Eq(rig.C.LastScanPossible, 2, "two candidates");
    }

    private static void ScanDisengage()
    {
        var rig = new ScanRig();
        uint engaged = rig.Mob("Engaged", 22.5, 0);
        rig.Mob("Stranger", 22.5, 1);
        rig.Mob("Far", 23.5, 0);
        var got = rig.Scan(c => c.activeTargetId = unchecked((int)engaged));
        Check.Eq(Names(got), "Engaged", "the engaged target is kept to range + 3 (23 m); others are not");

        var far = new ScanRig();
        uint e2 = far.Mob("Engaged", 23.5, 0);
        Check.Eq(far.Scan(c => c.activeTargetId = unchecked((int)e2)).Count, 0, "past range + 3: dropped");

        var custom = new ScanRig();
        custom.S.MonsterDisengageRange = 30;
        uint e3 = custom.Mob("Engaged", 29, 0);
        Check.Eq(custom.Scan(c => c.activeTargetId = unchecked((int)e3)).Count, 1, "a disengage range of 30 keeps it at 29 m");

        var tooSmall = new ScanRig();
        tooSmall.S.MonsterDisengageRange = 15;   // not above MonsterRange: ignored, range + 3 applies
        uint e4 = tooSmall.Mob("Engaged", 22.5, 0);
        Check.Eq(tooSmall.Scan(c => c.activeTargetId = unchecked((int)e4)).Count, 1, "a disengage range under MonsterRange is ignored");
    }

    private static void ScanDistanceMath()
    {
        var rig = new ScanRig();
        rig.Mob("Ledge", 15, 0, up: 15);   // 21.2 m in 3D
        rig.Mob("Flat", 15, 0);
        Check.Eq(Names(rig.Scan()), "Flat", "15 m out and 15 m up is 21.2 m: out of a 20 m range");

        var edge = new ScanRig();
        FakeHost.Positions[Player] = (Lb | 0x39, 190f, 96f, 0f);
        FakeHost.PlayerPose = (Lb | 0x39, 190f, 96f, 0f);
        edge.MobAt("Next door", 0xAAB40001, 5f, 96f);   // the next landblock east, 7 m away
        var got = edge.Scan();
        Check.Eq(Names(got), "Next door", "a monster across the landblock edge is 7 m away");
        Check.Near(got.FirstOrDefault().Distance, 7, 1e-3, "distance across the edge");
    }

    private static void ScanOrder()
    {
        var rig = new ScanRig();
        rig.Mob("Behind", -9.9, 0);   // 9.9 m, 180 degrees
        rig.Mob("Ahead", 10, 0);      // 10 m, 0 degrees
        rig.Mob("Right", 0, 10.3);    // 10.3 m, 90 degrees
        rig.Mob("Close", 3, 0);
        var got = rig.Scan();
        Check.Eq(Names(got), "Close,Ahead,Right,Behind", "closest first; within 0.5 m, the one in front first");
        var ahead = got.FirstOrDefault(t => t.Name == "Ahead");
        var right = got.FirstOrDefault(t => t.Name == "Right");
        Check.Near(ahead.Angle, 0, 1e-3, "straight ahead: 0 degrees");
        Check.Near(right.Angle, 90, 1e-3, "to the right: 90 degrees");
    }

    private static void ScanFilters()
    {
        var rig = new ScanRig();
        rig.Mob("Drudge Skulker", 5, 0);
        rig.Mob("Dead Thing", 6, 0, health: 0f);
        rig.Mob("Wounded Thing", 7, 0, health: 0.4f);
        uint npc = rig.Mob("Town Crier", 8, 0);
        FakeHost.NotAttackable.Add(npc);
        rig.Mob("Flame Bolt", 4, 0);
        rig.Mob("Mosswart Worshipper", 9, 0);
        rig.S.MonsterNameBlacklist = new List<string> { " mosswart " };
        Check.Eq(Names(rig.Scan()), "Drudge Skulker,Wounded Thing",
            "dead (0 health), not attackable, a spell projectile and a never-attack name (trimmed, any case) are skipped");
    }
}
