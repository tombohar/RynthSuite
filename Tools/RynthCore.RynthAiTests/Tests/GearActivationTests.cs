using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.ActiveGear;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// The Active Gear window's logic (ActiveGear/GearActivation.cs): whether a worn item's magic is on
// (its spell on the player with no end time, or marked on the item in its appraisal), why not
// (out of mana, Arcane Lore, activation skill), unknown when it can't be told, and the time its
// mana lasts at its rate less the read's age.
internal static class GearActivationTests
{
    private const int MajorStrength = 2576, MajorEndurance = 2577, Impen = 1483;
    private const int LeftRing = 0x00040000, Neck = 0x00008000, CoatMask = 0x00000200 | 0x00000400 | 0x00000800 | 0x00001000;

    public static void Register(Runner r)
    {
        r.Add("active gear: a spell on the player with no end time is active", ActiveOnPlayer);
        r.Add("active gear: a spell marked on the item itself (bit 31) is active", ActiveOnItem);
        r.Add("active gear: mana 0 and no spell seen is off, out of mana", ManaZero);
        r.Add("active gear: too little Arcane Lore is off, with the numbers", ArcaneLore);
        r.Add("active gear: too little activation skill is off, named", ActivationSkill);
        r.Add("active gear: a fresh appraisal from while worn with no spell seen is off", InactiveFresh);
        r.Add("active gear: not identified, an appraisal from before it was worn, or no enchantment read is unknown", Unknown);
        r.Add("active gear: a spell seen only in an old appraisal that has since run dry is unknown", StaleItemSide);
        r.Add("active gear: time left is mana over the rate, less the read's age", TimeLeftMath);
        r.Add("active gear: time words", TimeWords);
        r.Add("active gear: slots, listing, and the summary line", SummaryLine);
        r.Add("active gear: a proc spell (cloak, aetheria, cast on strike) isn't looked for on the wearer", ProcSpells);
    }

    private static GearFacts Ring(params uint[] spells) => new()
    {
        Name = "Ring",
        Slot = LeftRing,
        Identified = true,
        RawSpellIds = spells,
        CurMana = 1800,
        MaxMana = 2000,
        ManaRate = -0.5,
        ReadAgeSeconds = 60,
        WornForSeconds = 3600,
        EnchantmentsReadable = true,
        PlayerItemSpells = new HashSet<int>(),
    };

    private static void ActiveOnPlayer()
    {
        GearFacts f = Ring(MajorStrength, MajorEndurance) with { PlayerItemSpells = new HashSet<int> { MajorStrength, MajorEndurance } };
        GearVerdict v = GearActivation.Judge(f);
        Check.Eq(v.Active, GearActive.Yes, "both spells on the player");
        Check.Eq(v.Reason, string.Empty, "no reason when every spell is seen");
        Check.Eq(v.SpellsOn, 2, "two on");

        v = GearActivation.Judge(f with { PlayerItemSpells = new HashSet<int> { MajorStrength } });
        Check.Eq(v.Active, GearActive.Yes, "one of two is enough");
        Check.Eq(v.Reason, "1 of 2 spells seen", v.Reason);
        Check.True(v.SpellOn[0] && !v.SpellOn[1], "which one is on");

        // A stale mana read of 0 doesn't beat a spell seen live on the player (a stone refilled it since).
        v = GearActivation.Judge(f with { CurMana = 0 });
        Check.Eq(v.Active, GearActive.Yes, "live enchantment wins over a stale 0");
        Check.True(v.Reason.Contains("older"), v.Reason);

        // The enchantment list is only trusted when it could be read.
        v = GearActivation.Judge(f with { EnchantmentsReadable = false });
        Check.Eq(v.Active, GearActive.Unknown, "unreadable list: unknown");
    }

    private static void ActiveOnItem()
    {
        // Armor: its own spell (Impenetrability) is on the item: the book lists it, and again with bit 31.
        GearFacts f = Ring(Impen, Impen | GearActivation.ActiveOnItemBit) with { Slot = CoatMask, Name = "Coat" };
        GearVerdict v = GearActivation.Judge(f);
        Check.Eq(v.Active, GearActive.Yes, "on the item");
        Check.Eq(v.SpellIds.Count, 1, "the marked copy isn't a second spell");
        Check.Eq(v.SpellIds[0], Impen, "bit 31 stripped");

        // An enchantment someone else cast on the item isn't one of its spells and doesn't make it active.
        v = GearActivation.Judge(Ring(Impen, 1616u | GearActivation.ActiveOnItemBit) with { CurMana = 0 });
        Check.Eq(v.Active, GearActive.No, "a foreign enchantment on the item doesn't count");
        Check.Eq(v.SpellIds.Count, 1, "only its own spell listed");
    }

    private static void ManaZero()
    {
        GearVerdict v = GearActivation.Judge(Ring(MajorStrength) with { CurMana = 0 });
        Check.Eq(v.Active, GearActive.No, "mana 0");
        Check.Eq(v.Reason, "out of mana", v.Reason);
        Check.Eq(v.ManaPercent, 0, "0%");
        Check.Eq(v.SecondsLeft, 0.0, "no time left");
    }

    private static void ArcaneLore()
    {
        GearVerdict v = GearActivation.Judge(Ring(MajorStrength) with { ArcaneLoreNeeded = 300, ArcaneLore = 250 });
        Check.Eq(v.Active, GearActive.No, "lore too low");
        Check.Eq(v.Reason, "needs Arcane Lore 300, you have 250", v.Reason);
        // Enough lore: no reason to be off from it (and the item is fresh with nothing seen: off for that reason).
        v = GearActivation.Judge(Ring(MajorStrength) with { ArcaneLoreNeeded = 300, ArcaneLore = 320 });
        Check.Eq(v.Reason, "none of its spells is on you or on it", v.Reason);
        // Lore unread: not used.
        v = GearActivation.Judge(Ring(MajorStrength) with { ArcaneLoreNeeded = 300, ArcaneLore = null, WornForSeconds = null });
        Check.Eq(v.Active, GearActive.Unknown, "lore unread, worn time unknown: unknown");
    }

    private static void ActivationSkill()
    {
        GearVerdict v = GearActivation.Judge(Ring(MajorStrength) with { ActivationSkillName = "Heavy Weapons", ActivationLevel = 375, ActivationSkill = 300 });
        Check.Eq(v.Active, GearActive.No, "skill too low");
        Check.Eq(v.Reason, "needs Heavy Weapons 375, you have 300", v.Reason);
    }

    private static void InactiveFresh()
    {
        // Identified a minute ago, worn for an hour, nothing on: off.
        GearVerdict v = GearActivation.Judge(Ring(MajorStrength));
        Check.Eq(v.Active, GearActive.No, "fresh read, nothing seen");
        Check.Eq(v.SpellsOn, 0, "none on");
    }

    private static void Unknown()
    {
        GearVerdict v = GearActivation.Judge(new GearFacts { Name = "Amulet", Slot = Neck, Identified = false, EnchantmentsReadable = true });
        Check.Eq(v.Active, GearActive.Unknown, "not identified");
        Check.Eq(v.Reason, "not identified yet", v.Reason);
        Check.Eq(v.SecondsLeft, null, "no time left for an unread item");
        Check.Eq(v.ManaPercent, null, "no mana for an unread item");

        // Identified in the pack an hour ago, worn for ten minutes: its on-the-item spells weren't listed then.
        v = GearActivation.Judge(Ring(Impen) with { ReadAgeSeconds = 3600, WornForSeconds = 600 });
        Check.Eq(v.Active, GearActive.Unknown, "appraisal older than wearing it");
        Check.True(v.Reason.Contains("Check items"), v.Reason);

        v = GearActivation.Judge(Ring(MajorStrength) with { ReadAgeSeconds = null });
        Check.Eq(v.Active, GearActive.Unknown, "no appraisal time: unknown");

        v = GearActivation.Judge(Ring(MajorStrength) with { EnchantmentsReadable = false });
        Check.Eq(v.Reason, "your enchantments can't be read yet", v.Reason);

        v = GearActivation.Judge(Ring() with { MaxMana = 500 });
        Check.Eq(v.Reason, "no spells read", v.Reason);
    }

    private static void StaleItemSide()
    {
        // Seen only via the appraisal's bit-31 list, and the estimate says its mana ran out since.
        GearFacts f = Ring(Impen, Impen | GearActivation.ActiveOnItemBit) with { CurMana = 100, ManaRate = -0.1, ReadAgeSeconds = 5000 };
        GearVerdict v = GearActivation.Judge(f);
        Check.Eq(v.Active, GearActive.Unknown, "may have run dry");
        Check.True(v.Reason.Contains("run out"), v.Reason);
        // Seen live on the player: still yes whatever the estimate.
        v = GearActivation.Judge(Ring(MajorStrength) with { CurMana = 100, ManaRate = -0.1, ReadAgeSeconds = 5000, PlayerItemSpells = new HashSet<int> { MajorStrength } });
        Check.Eq(v.Active, GearActive.Yes, "live wins");
    }

    private static void TimeLeftMath()
    {
        Check.Near(GearActivation.SecondsLeft(1800, -0.5, 0) ?? -1, 3600, 1e-9, "1800 mana at 0.5/s = 1 h");
        Check.Near(GearActivation.SecondsLeft(1800, -0.5, 600) ?? -1, 3000, 1e-9, "less the read's 10 minutes");
        Check.Near(GearActivation.SecondsLeft(1800, 0.5, null) ?? -1, 3600, 1e-9, "a positive rate counts the same; unknown age = 0");
        Check.Eq(GearActivation.SecondsLeft(100, -0.5, 1000), 0.0, "never below 0");
        Check.Eq(GearActivation.SecondsLeft(1800, 0, 0), null, "no rate: no estimate");
        Check.Eq(GearActivation.SecondsLeft(1800, null, 0), null, "rate unread: no estimate");
        Check.Eq(GearActivation.SecondsLeft(null, -0.5, 0), null, "mana unread: no estimate");
        Check.Eq(GearActivation.SecondsLeft(1800, double.NaN, 0), null, "NaN rate: no estimate");

        GearVerdict v = GearActivation.Judge(Ring(MajorStrength) with { PlayerItemSpells = new HashSet<int> { MajorStrength } });
        Check.Near(v.SecondsLeft ?? -1, 1800 / 0.5 - 60, 1e-9, "the verdict carries it");
        Check.Eq(v.ManaPercent, 90, "1800 of 2000");
    }

    private static void TimeWords()
    {
        Check.Eq(GearActivation.TimeText(null), "-", "none");
        Check.Eq(GearActivation.TimeText(0), "0 min", "zero");
        Check.Eq(GearActivation.TimeText(30), "<1 min", "under a minute");
        Check.Eq(GearActivation.TimeText(12 * 60 + 59), "12 min", "minutes, floored");
        Check.Eq(GearActivation.TimeText(3 * 3600), "3 h", "whole hours");
        Check.Eq(GearActivation.TimeText(3 * 3600 + 5 * 60), "3 h 5 min", "hours and minutes");
        Check.Eq(GearActivation.TimeText(52 * 3600), "2 d 4 h", "days");
    }

    private static void SummaryLine()
    {
        Check.Eq(GearActivation.SlotName(LeftRing), "Left Ring", "ring");
        Check.Eq(GearActivation.SlotName(CoatMask), "Chest", "a coat is chest armor");
        Check.Eq(GearActivation.SlotName(0), "Worn", "no slot");
        Check.True(GearActivation.SlotOrder(CoatMask) < GearActivation.SlotOrder(LeftRing), "armor before jewelry");

        Check.True(GearActivation.Listed(new GearFacts { Identified = false }), "unidentified items are listed (they may have spells)");
        Check.False(GearActivation.Listed(new GearFacts { Identified = true }), "no spells, no mana: not listed");
        Check.True(GearActivation.Listed(Ring(MajorStrength)), "spells: listed");
        Check.False(GearActivation.Listed(new GearFacts { Identified = false, Slot = GearActivation.AmmoSlot }), "arrows: never listed");

        var on = new HashSet<int> { MajorStrength };
        GearFacts ring = Ring(MajorStrength) with { PlayerItemSpells = on, CurMana = 360, ManaRate = -0.5, ReadAgeSeconds = 0 };   // 12 min
        GearFacts neck = Ring(MajorStrength) with { Slot = Neck, PlayerItemSpells = on, CurMana = 1800 };                      // ~1 h
        var items = new List<(GearFacts, GearVerdict)> { (ring, GearActivation.Judge(ring)), (neck, GearActivation.Judge(neck)) };
        Check.Eq(GearActivation.Summary(items), "2 of 2 items active, lowest: Left Ring 12 min", "all on");

        GearFacts dry = Ring(MajorEndurance) with { Slot = CoatMask, CurMana = 0 };
        GearFacts unid = new() { Name = "Amulet", Slot = Neck };
        items.Add((dry, GearActivation.Judge(dry)));
        items.Add((unid, GearActivation.Judge(unid)));
        Check.Eq(GearActivation.Summary(items), "2 of 4 items active, 1 unknown, off: Chest (out of mana), lowest: Left Ring 12 min", "mixed");

        Check.Eq(GearActivation.Summary(new List<(GearFacts, GearVerdict)>()), "No worn items with spells.", "nothing worn");

        // Without rates the lowest is by mana.
        GearFacts noRate = Ring(MajorStrength) with { PlayerItemSpells = on, ManaRate = null, CurMana = 100 };
        Check.Eq(GearActivation.Summary(new List<(GearFacts, GearVerdict)> { (noRate, GearActivation.Judge(noRate)) }),
            "1 of 1 item active, lowest: Left Ring 5% mana", "by mana");
    }

    private static void ProcSpells()
    {
        const int Surge = 5753, CastOnStrike = 3020, Cloak = 0x08000000, BlueAetheria = 0x10000000, MeleeSlot = 0x00100000;
        // A cloak: its proc in the spell book, no mana, nothing on the wearer. On.
        GearFacts cloak = Ring((uint)Surge) with { Name = "Cloak", Slot = Cloak, CurMana = null, MaxMana = null, ManaRate = null };
        GearVerdict v = GearActivation.Judge(cloak);
        Check.Eq(v.Active, GearActive.Yes, "cloak by slot");
        Check.Eq(v.Reason, "casts its spell when it triggers", v.Reason);
        Check.True(v.SpellOn.Count == 1 && v.SpellOn[0], "the proc shows on");

        // Aetheria with ProcSpell read.
        v = GearActivation.Judge(cloak with { Slot = BlueAetheria, ProcSpellId = Surge });
        Check.Eq(v.Active, GearActive.Yes, "aetheria by ProcSpell");

        // Requirements still count: too little Arcane Lore is off.
        v = GearActivation.Judge(cloak with { ArcaneLoreNeeded = 300, ArcaneLore = 200 });
        Check.Eq(v.Active, GearActive.No, "lore still counts");

        // A weapon with a cast on strike and a wield spell: the wield spell decides, the proc isn't counted.
        GearFacts weapon = Ring((uint)MajorStrength, (uint)CastOnStrike) with { Name = "Sword", Slot = MeleeSlot, ProcSpellId = CastOnStrike,
            PlayerItemSpells = new HashSet<int> { MajorStrength } };
        v = GearActivation.Judge(weapon);
        Check.Eq(v.Active, GearActive.Yes, "weapon on");
        Check.Eq(v.Reason, string.Empty, "every spell that can be seen is seen: " + v.Reason);
        Check.True(v.SpellOn[0] && v.SpellOn[1], "both show on");
        v = GearActivation.Judge(weapon with { PlayerItemSpells = new HashSet<int>() });
        Check.Eq(v.Active, GearActive.No, "the wield spell not on is still off");

        // Not a proc slot and no ProcSpell: a lone spell is still looked for.
        v = GearActivation.Judge(Ring((uint)Surge));
        Check.Eq(v.Active, GearActive.No, "a ring's lone spell isn't a proc");
        // A ProcSpell that isn't in the book is ignored.
        v = GearActivation.Judge(Ring((uint)MajorStrength) with { ProcSpellId = Surge });
        Check.Eq(v.Active, GearActive.No, "a ProcSpell not in the book changes nothing");
    }
}
