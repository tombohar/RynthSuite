using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The shield's armor spells (BuffManager.Shield.cs, 2026-10-28). Impenetrability and the Banes go
// on the player, and ACE puts them on worn armor and a worn shield; but buffing holds a wand, ACE
// won't wield one beside a shield, and the wand swap puts the shield into a pack first, so the
// listed shield was never buffed. Now: the listed shield in use (in the off hand, or put away for
// the wand) gets the armor's spells aimed at it, after the armor, on the armor's timers. Weapon
// enchantments never go on it; an unlisted shield or a listed one that was in a pack is left alone;
// a shield being moved waits; a refusal at the shield leaves the armor alone.
internal static class ShieldBuffTests
{
    private const uint Player = 0x50000E01;
    private const uint STypeItem = 32;
    private const uint Sword = 0x80000701, Kite = 0x80000702, Round = 0x80000703;
    private const int MeleeSlot = 0x00100000, ShieldSlot = 0x00200000;

    private static readonly string[] Armor = { "Impenetrability VI", "Flame Bane VI", "Frost Bane VI" };
    private const string Aura = "Aura of Blood Drinker Self VI";

    public static void Register(Runner r)
    {
        r.Add("shield buffs: the armor spells, never a weapon enchantment", WhichSpells);
        r.Add("shield buffs: the listed shield put away for the wand gets Impenetrability and the banes, aimed at it, after the armor", StowedShieldBuffed);
        r.Add("shield buffs: a shield worn in the off hand (no wand) is buffed; a self-cast line naming it counts for it", WornShieldBuffed);
        r.Add("shield buffs: no shield listed, an unlisted shield, a listed one already in a pack, or the switch off: nothing goes on a shield", NothingForOtherShields);
        r.Add("shield buffs: put away for the wand, back in the off hand, then into a pack by combat: no longer the one in use", StowedForgottenWhenBackOn);
        r.Add("shield buffs: a shield being moved is not cast at until the move settles", MidSwapWaits);
        r.Add("shield buffs: renewed on the armor's timers (Rebuff With, Also Refresh Under, Force Rebuff)", RenewedLikeArmor);
        r.Add("shield buffs: a fizzle, a refusal or a late armor line at the shield leaves the armor alone", RefusalsLeaveArmor);
        r.Add("shield buffs: casts at the shield with no answer, or 'cannot be cast on', rest shield buffing; the armor carries on", NoAnswerParks);
        r.Add("shield buffs: the shield's timers are saved and loaded with the others", TimersSaved);
        r.Add("shield buffs: Buff the shield defaults on, loads, saves, copies, is in the settings map", Setting);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private static readonly MethodInfo CastSelfBuffs =
        typeof(BuffManager).GetMethod("CheckAndCastSelfBuffs", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;

    private static int Id(string name) => SpellTable.Named(name).Select(r => r.Id).FirstOrDefault();
    private static string NameOf(int id) => SpellTable.ById(id)?.Name ?? $"#{id}";
    private static int FamilyOf(string name) => SpellTableStub.GetById(Id(name))?.Family ?? 0;
    private static T Fld<T>(BuffManager b, string name) => (T)typeof(BuffManager).GetField(name, F)!.GetValue(b)!;
    private static void SetFld(BuffManager b, string name, object v) => typeof(BuffManager).GetField(name, F)!.SetValue(b, v);

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { EnableBuffing = true };
        public readonly BuffManager B;
        public readonly SpellManager Sm;
        public readonly RynthCoreHost Host;
        public readonly WeaponSwapGate Gate = new();
        public long GateMs;
        public int Stowed;

        /// <summary>Item Magic 300 (tier 6), in Magic stance, knowing Impenetrability, two banes and Blood Drinker.
        /// A sword and the Kite Shield listed; <paramref name="worn"/>: the Kite Shield in the off hand.</summary>
        public Rig(bool worn = false, bool listShield = true)
        {
            FakeHost.Reset();
            foreach (string n in Armor.Append(Aura)) foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeItem)] = (300, 2);
            FakeHost.WeaponCalls = true;   // GetCurrentCombatMode answers CombatModeValue
            FakeHost.CombatModeValue = CombatMode.Magic;
            Item(Sword, "Long Sword", 0x1, MeleeSlot, combatUse: 1);
            Item(Kite, "Kite Shield", 0x2, ShieldSlot, combatUse: 4);
            Item(Round, "Round Shield", 0x2, ShieldSlot, combatUse: 4);
            if (worn) Wear(Kite);
            Host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(Host, Player, new[] { Sword, Kite, Round });
            var skills = new CharacterSkills(Host);
            skills.SetPlayerId(Player);
            Sm = new SpellManager(Host, S);
            Sm.InitializeNatively();
            Sm.SetCharacterSkills(skills);
            B = new BuffManager(Host, S, Sm, new PlayerVitalsCache());
            B.SetCharacterSkills(skills);
            B.SetWorldObjectCache(cache);
            Gate.NowMs = () => GateMs;
            B.SetWeaponSwapGate(Gate);
            B.ShieldReadMs = 0;
            B.OffhandStowed = id => Stowed = id;
            FakeHost.ServerTime = 100_000;
            List(listShield ? new[] { Sword, Kite } : new[] { Sword });
        }

        public void List(params uint[] ids) =>
            S.ItemRules = ids.Select(i => new ItemRule
            {
                Id = unchecked((int)i), Name = FakeHost.Names[i], Element = "", ElementSource = "",
                Action = i == Kite || i == Round ? WeaponList.ShieldAction : "Weapon",
            }).ToList();

        /// <summary>The wand swap put the Kite Shield into a pack (BuffManager's MoveToPack step).</summary>
        public void PutAwayForWand()
        {
            B.OffhandPutAway(unchecked((int)Kite));
            Unwear(Kite);
        }

        /// <summary>
        /// The buff step until it casts nothing, every cast answered as the server would: a cast at the
        /// player names the armor (and, with <paramref name="alsoOnShield"/>, the worn shield too), a cast
        /// at a shield names it. Ends the run as the heartbeat does. Returns "spell@target" in order.
        /// </summary>
        public List<string> CastAll(Func<string, bool>? alsoOnShield = null, int max = 40)
        {
            var sent = new List<string>();
            for (int i = 0; i < max; i++)
            {
                int before = FakeHost.Casts.Count;
                CastSelfBuffs.Invoke(B, null);
                if (FakeHost.Casts.Count == before) break;
                var (target, id) = FakeHost.Casts.Last();
                string name = NameOf(id);
                sent.Add($"{name}@{Where(target)}");
                Answer(target, name, alsoOnShield?.Invoke(name) == true);
            }
            EndRun();
            return sent;
        }

        public void Answer(uint target, string name, bool alsoOnShield = false)
        {
            if (target == Player)
            {
                B.OnChatWindowText($"You cast {name} on {(BuffManager.IsItemEnchantment(name) ? "Rune Robe" : "yourself")}", 7);
                if (alsoOnShield) B.OnChatWindowText($"You cast {name} on Kite Shield, refreshing {name}", 7);
            }
            else B.OnChatWindowText($"You cast {name} on {FakeHost.Names[target]}", 7);
        }

        public void EndRun()
        {
            if (!(bool)typeof(BuffManager).GetField("_isForceRebuffing", F)!.GetValue(B)!) return;
            SetFld(B, "_isForceRebuffing", false);
            SetFld(B, "_isAutoBatchRebuff", false);
            typeof(BuffManager).GetMethod("ClearRunFamilies", F)!.Invoke(B, null);
        }

        public void SetShieldLeft(string spell, double seconds) =>
            SetLeft(Record("_shieldSpellTimers", BuffManager.ShieldKey(unchecked((int)Kite), FamilyOf(spell))), seconds);

        public void SetArmorLeft(string spell, double seconds) => SetLeft(Record("_itemSpellTimers", FamilyOf(spell)), seconds);

        private static void SetLeft(object rec, double seconds) => rec.GetType().GetField("ExpiresAt")!.SetValue(rec, DateTime.Now.AddSeconds(seconds));

        public object Record(string field, object key) => ((IDictionary)Fld<object>(B, field))[key]!;
        public bool HasShield(string spell) => ((IDictionary)Fld<object>(B, "_shieldSpellTimers")).Contains(BuffManager.ShieldKey(unchecked((int)Kite), FamilyOf(spell)));
        public bool HasArmor(string spell) => ((IDictionary)Fld<object>(B, "_itemSpellTimers")).Contains(FamilyOf(spell));
    }

    private static string Where(uint target) => target == Player ? "player" : FakeHost.Names.TryGetValue(target, out string? n) ? n : $"0x{target:X8}";

    private static void Item(uint id, string name, uint itemType, int validSlots, int combatUse = 0)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = itemType;
        FakeHost.Ints[(id, 9)] = validSlots;
        if (combatUse != 0) FakeHost.Ints[(id, 51)] = combatUse;
    }

    private static void Wear(uint id) => FakeHost.Ints[(id, 10)] = ShieldSlot;
    private static void Unwear(uint id) => FakeHost.Ints.Remove((id, 10));

    private static IEnumerable<string> At(List<string> sent, string target) =>
        sent.Where(s => s.EndsWith("@" + target, StringComparison.Ordinal)).Select(s => s.Substring(0, s.LastIndexOf('@')));

    private static string ArmorAtPlayer => string.Join(", ", Armor);

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void WhichSpells()
    {
        foreach (string n in new[] { "Impenetrability VI", "Brogard's Defiance", "Incantation of Impenetrability", "Flame Bane VI",
                                     "Swordsman's Bane", "Incantation of Bludgeon Bane", "Acid Bane", "Piercing Bane" })
            Check.True(BuffManager.IsArmorItemSpell(n), $"an armor spell: {n}");
        foreach (string n in new[] { "Blood Drinker Self", Aura, "Aura of Infected Caress", "Defender Self", "Aura of Cragstone's Will",
                                     "Heart Seeker Self", "Spirit Drinker Self", "Armor Self", "Impregnability Self VI", "Strength Self VI" })
            Check.False(BuffManager.IsArmorItemSpell(n), $"not an armor spell: {n}");

        Check.Eq(BuffManager.CastTarget("Flame Bane VI on Kite Shield, refreshing Flame Bane V"), "Kite Shield", "the target, before the comma");
        Check.Eq(BuffManager.CastTarget("Impenetrability VI on Kite Shield"), "Kite Shield", "the target alone");
        Check.Eq(BuffManager.CastTarget("Strength Self VI"), "", "no target");
        Check.True(BuffManager.ShieldKey(1, 2) != BuffManager.ShieldKey(2, 1), "the key keeps the shield and the family apart");
    }

    private static void StowedShieldBuffed()
    {
        var rig = new Rig(worn: true);
        rig.PutAwayForWand();   // what the wand swap does before the first cast
        Check.Eq(rig.Stowed, unchecked((int)Kite), "combat is still told, so it puts the shield back on");
        Check.Eq(rig.B.ShieldToBuff(out string where), unchecked((int)Kite), "the shield to buff: " + where);
        Check.True(rig.B.NeedsAnyBuff(), "nothing on: needs buffs");

        var sent = rig.CastAll();
        Check.Eq(string.Join(", ", At(sent, "player")), $"{Aura}, {ArmorAtPlayer}", "the character's buffs first, as before");
        Check.Eq(string.Join(", ", At(sent, "Kite Shield")), ArmorAtPlayer, "then Impenetrability and both banes at the shield");
        Check.True(sent.Last().EndsWith("@Kite Shield", StringComparison.Ordinal) && sent.FindIndex(s => s.EndsWith("@Kite Shield")) == 4,
            "the shield's after every cast on the player: " + string.Join(", ", sent));
        Check.False(FakeHost.Casts.Any(c => c.Target == Kite && NameOf(c.SpellId) == Aura), "Blood Drinker never at the shield");
        Check.Eq(rig.B.ShieldSpellCount, 3, "three timers on the shield");
        foreach (string a in Armor) Check.True(rig.HasArmor(a) && rig.HasShield(a), $"{a}: the armor's and the shield's timer");
        Check.False(rig.B.NeedsAnyBuff(), "all on: nothing due");
        Check.Eq(rig.CastAll().Count, 0, "and nothing more is cast");

        // Force Rebuff: the armor and the shield again, still no weapon enchantment at it.
        rig.B.ForceFullRebuff();
        sent = rig.CastAll();
        Check.Eq(string.Join(", ", At(sent, "Kite Shield")), ArmorAtPlayer, "Force Rebuff recasts the shield's too");
        Check.False(FakeHost.Casts.Any(c => c.Target == Kite && !BuffManager.IsArmorItemSpell(NameOf(c.SpellId))), "only armor spells ever at the shield");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Buff] shield: 0x") && l.Contains("put into a pack for the wand")), "the shield picked is logged");
    }

    private static void WornShieldBuffed()
    {
        // Magic without a wand: the shield stays on, the self-cast's lines don't name it.
        var rig = new Rig(worn: true);
        Check.Eq(rig.B.ShieldToBuff(out string where), unchecked((int)Kite), "the worn shield: " + where);
        Check.Eq(where, "in the off hand", "in the off hand");
        var sent = rig.CastAll();
        Check.Eq(string.Join(", ", At(sent, "Kite Shield")), ArmorAtPlayer, "each armor spell aimed at the worn shield");

        // The server's self-cast reached the worn shield: its line names it. Flame Bane's shield line
        // comes after the armor's, Impenetrability's before (the self-cast still resolves by it).
        var both = new Rig(worn: true);
        sent = both.CastAll(alsoOnShield: n => n == "Flame Bane VI");
        Check.Eq(string.Join(", ", At(sent, "Kite Shield")), "Impenetrability VI, Frost Bane VI", "Flame Bane's line named the shield: only the other two are cast at it");
        Check.True(both.HasShield("Flame Bane VI") && both.HasArmor("Flame Bane VI"), "that line counts for the shield, and the armor's line for the armor");

        var first = new Rig(worn: true);
        first.B.ForceFullRebuff();
        for (int i = 0; i < 10; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(first.B, null);
            if (FakeHost.Casts.Count == before) break;
            var (target, id) = FakeHost.Casts.Last();
            string name = NameOf(id);
            if (target == Player && name == "Impenetrability VI")
            {
                first.B.OnChatWindowText("You cast Impenetrability VI on Kite Shield", 7);   // the shield's line first
                Check.Eq(first.B.PendingSpellId, 0, "the self-cast is resolved by the shield's line too");
                first.B.OnChatWindowText("You cast Impenetrability VI on Rune Robe", 7);
            }
            else first.Answer(target, name);
        }
        first.EndRun();
        Check.True(first.HasShield("Impenetrability VI") && first.HasArmor("Impenetrability VI"), "both timers");
        Check.False(FakeHost.Casts.Any(c => c.Target == Kite && NameOf(c.SpellId) == "Impenetrability VI"), "Impenetrability isn't cast at the shield again");
    }

    private static void NothingForOtherShields()
    {
        // The baseline: no shield listed (a worn one), exactly the casts there always were.
        var none = new Rig(worn: true, listShield: false);
        var sent = none.CastAll();
        Check.Eq(string.Join(", ", sent), $"{Aura}@player, " + string.Join(", ", Armor.Select(a => a + "@player")), "no shield listed: the same casts as before");
        Check.Eq(none.B.ShieldToBuff(out _), 0, "no shield to buff");
        Check.False(none.B.NeedsAnyBuff(), "and nothing due");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[Buff] shield:")), "nothing logged about shields");

        // An unlisted shield worn, the listed one in a pack and never put away by buffing.
        var unlisted = new Rig();
        Wear(Round);
        sent = unlisted.CastAll();
        Check.Eq(At(sent, "Round Shield").Count() + At(sent, "Kite Shield").Count(), 0, "nothing at the unlisted worn shield, nor the listed one in a pack");
        Check.Eq(unlisted.B.ShieldToBuff(out string why), 0, "none to buff: " + why);
        Check.True(why.Contains("in a pack"), "the reason says it is in a pack");
        Check.False(unlisted.B.NeedsAnyBuff(), "nothing due: a shield in a pack doesn't hold Buffing");

        // A shield filed with the Weapon action is still a listed shield.
        var asWeapon = new Rig(worn: true);
        asWeapon.S.ItemRules = new List<ItemRule>
        {
            new() { Id = unchecked((int)Sword), Name = "Long Sword", Action = "Weapon", Element = "", ElementSource = "" },
            new() { Id = unchecked((int)Kite), Name = "Kite Shield", Action = "Weapon", Element = "", ElementSource = "" },
        };
        Check.Eq(asWeapon.B.ShieldToBuff(out _), unchecked((int)Kite), "a shield listed as a weapon is the listed shield");

        // Buff the shield off.
        var off = new Rig(worn: true);
        off.S.BuffShield = false;
        sent = off.CastAll();
        Check.Eq(At(sent, "Kite Shield").Count(), 0, "switch off: nothing at the shield");
        Check.Eq(string.Join(", ", At(sent, "player")), $"{Aura}, {ArmorAtPlayer}", "the armor as always");
        Check.False(off.B.NeedsAnyBuff(), "and nothing due");

        // Banes unticked for the armor are unticked for the shield.
        var unticked = new Rig(worn: true);
        unticked.S.SetBuffEnabled("Flame Bane", false);
        sent = unticked.CastAll();
        Check.Eq(string.Join(", ", At(sent, "Kite Shield")), "Impenetrability VI, Frost Bane VI", "an unticked bane goes on neither");
        Check.False(At(sent, "player").Contains("Flame Bane VI"), "(nor the armor)");
    }

    private static void StowedForgottenWhenBackOn()
    {
        var rig = new Rig(worn: true);
        rig.PutAwayForWand();
        Wear(Kite);                                                   // combat put it back on
        Check.Eq(rig.B.ShieldToBuff(out _), unchecked((int)Kite), "in the off hand");
        Unwear(Kite);                                                 // still within the grace: a stow just sent
        Check.Eq(rig.B.ShieldToBuff(out string where), unchecked((int)Kite), "a sighting right after the stow doesn't forget it: " + where);

        Wear(Kite);
        SetFld(rig.B, "_stowedShieldAt", DateTime.Now.AddSeconds(-30));
        Check.Eq(rig.B.ShieldToBuff(out _), unchecked((int)Kite), "back in the off hand for a while");
        Unwear(Kite);                                                 // combat took it off for a two-hander
        Check.Eq(rig.B.ShieldToBuff(out where), 0, "then in a pack by combat's hand: not the one in use (" + where + ")");
        rig.PutAwayForWand();
        Check.Eq(rig.B.ShieldToBuff(out _), unchecked((int)Kite), "put away for the wand again: buffed again");
    }

    private static void MidSwapWaits()
    {
        var rig = new Rig(worn: true);
        rig.PutAwayForWand();
        // Combat's off-hand move of the shield, just claimed (the gate waits MinIntervalMs for it).
        rig.GateMs = 1000;
        Check.True(rig.Gate.TryBeginSwap("combat-offhand"), "a swap claimed");
        rig.Gate.NoteMove(unchecked((int)Kite), wieldedNow: false);
        var sent = rig.CastAll();
        Check.Eq(At(sent, "Kite Shield").Count(), 0, "nothing at the shield while it moves");
        Check.Eq(string.Join(", ", At(sent, "player")), $"{Aura}, {ArmorAtPlayer}", "the character's buffs don't wait");
        Check.True((bool)CastSelfBuffs.Invoke(rig.B, null)!, "the step holds the tick (not done)");
        Check.True(rig.B.LastBuffSkipReason.Contains("being moved"), "and says why: " + rig.B.LastBuffSkipReason);
        rig.EndRun();
        Check.True(rig.B.NeedsAnyBuff(), "the shield is still due");

        rig.GateMs = 1000 + rig.Gate.MinIntervalMs + 1;               // the move has settled
        sent = rig.CastAll();
        Check.Eq(string.Join(", ", At(sent, "Kite Shield")), ArmorAtPlayer, "settled: the shield's spells go");

        // A multi-step swap holding the gate: wait; released: go.
        var held = new Rig(worn: true);
        held.PutAwayForWand();
        held.Gate.Hold("combat-fight-swap", 60_000);
        sent = held.CastAll();
        Check.Eq(At(sent, "Kite Shield").Count(), 0, "a held swap: nothing at the shield");
        held.Gate.Release("combat-fight-swap", 0);
        sent = held.CastAll();
        Check.Eq(string.Join(", ", At(sent, "Kite Shield")), ArmorAtPlayer, "released: the shield's spells go");
    }

    private static void RenewedLikeArmor()
    {
        var rig = new Rig(worn: true);
        rig.PutAwayForWand();
        rig.CastAll();
        foreach (string a in Armor)
        {
            object s = rig.Record("_shieldSpellTimers", BuffManager.ShieldKey(unchecked((int)Kite), FamilyOf(a)));
            object m = rig.Record("_itemSpellTimers", FamilyOf(a));
            double Len(object rec) => ((DateTime)rec.GetType().GetField("ExpiresAt")!.GetValue(rec)! - (DateTime)rec.GetType().GetField("CastAt")!.GetValue(rec)!).TotalSeconds;
            Check.Near(Len(s), Len(m), 0.5, $"{a}: the shield's timer is as long as the armor's");
            Check.Near(Len(s), 2700, 0.5, $"{a}: 45 minutes at level 6");
        }
        Check.False(rig.B.NeedsAnyBuff(), "all on");

        // The shield's Flame Bane under Rebuff With (300 s): due on its own, and only it is recast.
        rig.SetShieldLeft("Flame Bane VI", 100);
        Check.True(rig.B.NeedsAnyBuff(), "the shield's bane running out: due, like the armor's");
        var sent = rig.CastAll();
        Check.Eq(string.Join(", ", sent), "Flame Bane VI@Kite Shield", "only that one, at the shield");

        // The armor's Frost Bane under Rebuff With, the shield's under Also Refresh Under (1200 s): they land together.
        rig.SetArmorLeft("Frost Bane VI", 100);
        rig.SetShieldLeft("Frost Bane VI", 1000);
        sent = rig.CastAll();
        Check.Eq(string.Join(", ", sent), "Frost Bane VI@player, Frost Bane VI@Kite Shield", "the batch tops off the shield's with the armor's");

        // Under Also Refresh Under alone starts nothing, as for the armor.
        rig.SetShieldLeft("Impenetrability VI", 1000);
        Check.False(rig.B.NeedsAnyBuff(), "1000 s left is no reason to buff");
        rig.S.RebuffSecondsRemaining = 1100;
        Check.True(rig.B.NeedsAnyBuff(), "with Rebuff With 1100 it is");
        rig.S.RebuffSecondsRemaining = 300;

        // A death (the player's registry empties) forgets the shield's, as the armor's.
        FakeHost.Enchantments.Clear();
        FakeHost.Enchantments.Add(((uint)Id("Strength Self I"), FakeHost.ServerTime + 3000));   // something on, then nothing
        rig.B.RefreshFromLiveMemory();
        FakeHost.Enchantments.Clear();
        rig.B.RefreshFromLiveMemory();
        Check.Eq(rig.B.ShieldSpellCount, 0, "a death clears the shield's timers");
        Check.True(rig.B.NeedsAnyBuff(), "and they are due again");
    }

    /// <summary>Runs the step until a cast at the Kite Shield is pending (everything else answered); its spell.</summary>
    private static string ToFirstShieldCast(Rig rig)
    {
        for (int i = 0; i < 20; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(rig.B, null);
            if (FakeHost.Casts.Count == before) return "";
            var (target, id) = FakeHost.Casts.Last();
            if (target == Kite) return NameOf(id);
            rig.Answer(target, NameOf(id));
        }
        return "";
    }

    private static Dictionary<int, DateTime> ArmorParks(Rig rig) => Fld<Dictionary<int, DateTime>>(rig.B, "_buffFailCooldownUntil");

    private static void RefusalsLeaveArmor()
    {
        var rig = new Rig(worn: true);
        rig.PutAwayForWand();
        rig.B.ForceFullRebuff();
        string first = ToFirstShieldCast(rig);
        Check.Eq(first, "Impenetrability VI", "the first cast at the shield");

        // A late armor line of the same family while the shield's cast waits: the armor's, pending kept.
        rig.B.OnChatWindowText("You cast Impenetrability VI on Rune Robe", 7);
        Check.Eq(rig.B.PendingSpellId, Id("Impenetrability VI"), "the shield's cast still waits for its own line");
        Check.False(rig.HasShield("Impenetrability VI"), "the armor's line doesn't count for the shield");

        // A fizzle at the shield: the armor's timer stays, the shield's spell goes again.
        rig.B.OnChatWindowText("Your spell fizzled.", 7);
        Check.Eq(rig.B.PendingSpellId, 0, "the fizzle ends the cast");
        Check.True(rig.HasArmor("Impenetrability VI"), "the armor's Impenetrability is still on");
        Check.Eq(ToFirstShieldCast(rig), "Impenetrability VI", "and it is cast at the shield again");

        // No components, at the shield: it rests on the shield only; the next bane goes at it.
        rig.B.OnChatWindowText("You don't have all the components for this spell.", 7);
        Check.True(rig.HasArmor("Impenetrability VI"), "the armor's timer stays");
        Check.False(ArmorParks(rig).ContainsKey(FamilyOf("Impenetrability VI")), "the armor's family isn't parked");
        Check.True(FakeHost.Chat.Any(l => l.Contains("Skipping Impenetrability VI on the shield")), "the chat line says on the shield");
        Check.Eq(ToFirstShieldCast(rig), "Flame Bane VI", "the next spell goes at the shield");
        rig.Answer(Kite, "Flame Bane VI");

        // Not enough mana at the shield: the same, the armor's family untouched.
        Check.Eq(ToFirstShieldCast(rig), "Frost Bane VI", "Frost Bane at the shield");
        rig.B.OnChatWindowText("You don't have enough Mana to cast this spell.", 7);
        Check.Eq(rig.B.PendingSpellId, 0, "the mana refusal ends it");
        Check.True(rig.HasArmor("Frost Bane VI"), "the armor's Frost Bane stays");
        Check.False(ArmorParks(rig).ContainsKey(FamilyOf("Frost Bane VI")), "nor is the armor's family rested");

        // Too busy at the shield: the armor's timer stays.
        var busy = new Rig(worn: true);
        busy.PutAwayForWand();
        Check.Eq(ToFirstShieldCast(busy), "Impenetrability VI", "a cast at the shield");
        busy.B.OnChatWindowText("You're too busy!", 7);
        Check.Eq(busy.B.PendingSpellId, 0, "too busy: no longer pending");
        Check.True(busy.HasArmor("Impenetrability VI"), "the armor's timer stays");
    }

    private static void NoAnswerParks()
    {
        var rig = new Rig(worn: true);
        rig.PutAwayForWand();
        rig.S.IsMacroRunning = true;
        rig.S.SpellCastIntervalMs = 0;
        DateTime t0 = new DateTime(2026, 10, 28, 12, 0, 0), now = t0;
        rig.B.Clock = () => now;
        rig.B.MarkLiveBuffsReadyForTests();
        FakeHost.Enchantments.Add(((uint)Id("Strength Self I"), FakeHost.ServerTime + 3000));   // a registry with something on
        rig.B.RefreshFromLiveMemory();

        // The heartbeat, every cast at the player answered, the shield's never.
        int shieldSends = 0;
        for (int i = 0; i < 80; i++)
        {
            now = now.AddSeconds(3);
            int before = FakeHost.Casts.Count;
            rig.B.OnHeartbeat();
            if (FakeHost.Casts.Count == before)
            {
                if (rig.B.PendingSpellId != 0 && FakeHost.Casts.Count > 0 && FakeHost.Casts.Last().Target == Kite)
                    SetFld(rig.B, "_lastCastAttempt", DateTime.Now.AddSeconds(-6));   // its give-up reached
                continue;
            }
            var (target, id) = FakeHost.Casts.Last();
            if (target == Kite) { shieldSends++; continue; }
            rig.Answer(target, NameOf(id));
        }
        Check.Eq(shieldSends, BuffManager.ShieldNoShowsToPark, $"{BuffManager.ShieldNoShowsToPark} casts at the shield, no more");
        Check.True(FakeHost.Chat.Any(l => l.Contains("Not buffing the shield 'Kite Shield'")), "the rest is said in chat");
        Check.False(rig.B.NeedsAnyBuff(), "parked: the shield doesn't hold Buffing");
        Check.Eq(ArmorParks(rig).Count, 0, "no armor family parked");
        Check.Eq(Fld<Dictionary<int, int>>(rig.B, "_silentNoShowCounts").Count, 0, "no armor no-show strikes");
        Check.Eq(rig.Sm.UnresolvableSpellIds.Count, 0, "no spell blacklisted (it just went on the armor)");
        Check.True(Armor.All(rig.HasArmor), "the armor got everything");

        // "cannot be cast on" (ACE IsInvalidTarget) parks it at once.
        var invalid = new Rig(worn: true);
        invalid.PutAwayForWand();
        Check.Eq(ToFirstShieldCast(invalid), "Impenetrability VI", "a cast at the shield");
        invalid.B.OnChatWindowText("Impenetrability VI cannot be cast on Kite Shield.", 7);
        Check.Eq(invalid.B.PendingSpellId, 0, "it ends the cast");
        invalid.EndRun();
        Check.False(invalid.B.NeedsAnyBuff(), "the shield rests");
        Check.Eq(invalid.CastAll().Count, 0, "nothing more at it");
        invalid.B.ForceFullRebuff();
        Check.Eq(string.Join(", ", At(invalid.CastAll(), "Kite Shield")), ArmorAtPlayer, "Force Rebuff gives it a fresh try");

        // The same line with nothing aimed at a shield changes nothing.
        var other = new Rig(worn: true, listShield: false);
        other.B.OnChatWindowText("Impenetrability VI cannot be cast on Kite Shield.", 7);
        Check.True(other.B.NeedsAnyBuff(), "no shield cast pending: the line is not read");
    }

    private static void TimersSaved()
    {
        string folder = Path.Combine(Program.TempRoot, "shieldbuff");
        Directory.CreateDirectory(folder);
        var rig = new Rig(worn: true);
        rig.B.SetTimerPath(folder);
        rig.PutAwayForWand();
        rig.CastAll();
        string[] lines = File.ReadAllLines(Path.Combine(folder, "bufftimers.txt"));
        Check.Eq(lines.Count(l => l.StartsWith("shield|", StringComparison.Ordinal)), 3, "three shield lines");
        Check.Eq(lines.Count(l => l.StartsWith("item|", StringComparison.Ordinal)), 4, "and the four item lines (the armor's and the aura)");

        var again = new Rig(worn: true);
        again.PutAwayForWand();
        again.B.SetTimerPath(folder);
        Check.Eq(again.B.ShieldSpellCount, 3, "loaded back");
        Check.True(Armor.All(again.HasArmor), "with the armor's");
        Check.Eq(again.CastAll().Count, 0, "nothing due after the reload");
    }

    private static void Setting()
    {
        Check.True(new LegacyUiSettings().BuffShield, "a new profile: on");
        var old = JsonSerializer.Deserialize("{\"EnableBuffing\":true,\"RebuffSecondsRemaining\":300}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.BuffShield, "a profile saved before the setting: on");
        var off = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { BuffShield = false }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(off != null && !off.BuffShield, "off is saved and loaded");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { BuffShield = false }, dst });
        Check.False(dst.BuffShield, "a profile with it off loads off");
        copy.Invoke(null, new object[] { old!, dst });
        Check.True(dst.BuffShield, "an old profile loads on");

        var live = new LegacyUiSettings();
        var map = ExpressionTests.Engine(settings: live).BuildSettingsMapPublic();
        Check.True(map.TryGetValue("BuffShield", out var entry), "BuffShield is in the settings map");
        Check.Eq(entry.Get(), "1", "reads on");
        entry.Set("0");
        Check.False(live.BuffShield, "and sets off");
    }
}
