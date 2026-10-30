// ============================================================================
//  RynthDefective - ShortcutBar/BarMapping.cs
//  Which slot a shortcut bar slot stands for, pure (the tests run it offline).
//
//    usual        bar slots 1-18 are AC's own shortcut slots (the retail
//                 toolbar's, saved on the server; keys AC's UseQuickSlot_N),
//                 bar slot 18+N is RynthDefective's own slot N.
//    independent  (DefSettings.IndependentBar) every bar slot N is
//                 RynthDefective's own slot N: AC's 18 are not on the bar,
//                 clicks go through the use controller, keys are the engine's
//                 hotkeys running /rdef use N.
//
//  Also: the healing kit in one of AC's slots that a click uses on yourself
//  (RetailKit), and copying AC's 18 slots into the own ones (CopyRetail).
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;

namespace RynthCore.Plugin.RynthDefective.ShortcutBar;

/// <summary>What copying AC's 18 slots into RynthDefective's own did (<see cref="BarMapping.CopyRetail"/>).</summary>
internal sealed record BarCopyResult(int Copied, int Skipped, IReadOnlyList<(int From, int To)> Moved, string? Refused);

internal static class BarMapping
{
    /// <summary>Bar slot <paramref name="number"/> (1-based) is one of AC's own shortcut slots.</summary>
    public static bool IsRetail(int number, bool independent) =>
        !independent && number >= 1 && number <= DefSettings.RetailSlots;

    /// <summary>RynthDefective's own slot (1-based) bar slot <paramref name="number"/> is; 0 for one of AC's.</summary>
    public static int OwnSlot(int number, bool independent) =>
        IsRetail(number, independent) ? 0 : independent ? number : number - DefSettings.RetailSlots;

    /// <summary>The bar slot RynthDefective's own slot <paramref name="own"/> shows in.</summary>
    public static int BarNumber(int own, bool independent) => independent ? own : own + DefSettings.RetailSlots;

    /// <summary>What a bar slot's keys run: AC's keymap action for one of AC's slots, the engine command for an own one.</summary>
    public static string KeyAction(int number, bool independent) =>
        IsRetail(number, independent) ? "UseQuickSlot_" + number : DefCommandParser.UseCommand(OwnSlot(number, independent));

    /// <summary>
    /// A click on one of AC's slots holding <paramref name="objectId"/>: the healing (or stamina, mana)
    /// kit to use on yourself through the use controller, or null for AC's own use of the slot (a potion,
    /// a weapon, a spell, an item not in the pack). The kit test is the use controller's own
    /// (ItemKinds.Classify: the healer bit, else RynthAi's " Kit" name test).
    /// </summary>
    public static CarriedItem? RetailKit(uint objectId, IReadOnlyList<CarriedItem> items)
    {
        if (objectId == 0) return null;
        foreach (CarriedItem it in items)
            if (it.Id == objectId) return ItemKinds.Classify(it) == ItemKind.Kit ? it : null;
        return null;
    }

    /// <summary>
    /// Copies AC's 18 slots (<paramref name="ids"/>, <paramref name="spells"/>, slot 1 first) into the own
    /// slots 1-18, so an independent bar looks as the usual one did: the own slots that were after AC's
    /// (own N at bar 18+N) move to own 18+N, where an independent bar shows them. <see cref="BarCopyResult.Moved"/>
    /// lists the moves of the slots that held something (their "/rdef use N" keys go with them). A spell goes
    /// in as a spell slot (2026-10-28); an item <paramref name="resolve"/> can't name can't go in an own slot:
    /// that slot stays empty (Skipped).
    /// Refused (nothing changed) when an own slot past 24 holds something: there is no room to move it.
    /// </summary>
    public static BarCopyResult CopyRetail(List<DefSlot> own, ReadOnlySpan<uint> ids, ReadOnlySpan<uint> spells, Func<uint, ItemRef?> resolve)
    {
        const int retail = DefSettings.RetailSlots, room = DefSettings.SlotsMax - DefSettings.RetailSlots;
        for (int n = room + 1; n <= own.Count; n++)
            if (!own[n - 1].Empty)
                return new BarCopyResult(0, 0, Array.Empty<(int, int)>(), $"Your slot {n} holds {own[n - 1].Label}: there is no room to move "
                    + $"slots {room + 1}-{DefSettings.SlotsMax} after the game's {retail}. Empty them (or copy by hand) first.");

        var next = DefData.NewSlots();
        var moved = new List<(int, int)>();
        for (int n = 1; n <= Math.Min(room, own.Count); n++)
        {
            if (own[n - 1].Empty) continue;
            next[retail + n - 1] = own[n - 1];
            moved.Add((n, retail + n));
        }
        int copied = 0, skipped = 0;
        for (int i = 0; i < retail; i++)
        {
            uint id = i < ids.Length ? ids[i] : 0, spell = i < spells.Length ? spells[i] : 0;
            if (id == 0)
            {
                if (spell != 0)
                {
                    next[i] = new DefSlot { SpellId = spell, SpellName = SpellTable.Name(spell) };
                    copied++;
                }
                continue;
            }
            ItemRef? r = resolve(id);
            if (r == null || string.IsNullOrWhiteSpace(r.Name)) { skipped++; continue; }
            next[i] = new DefSlot { Item = r, Mode = UseMode.Auto };
            copied++;
        }
        for (int i = 0; i < own.Count && i < next.Count; i++) own[i] = next[i];
        return new BarCopyResult(copied, skipped, moved, null);
    }

    /// <summary>How many of AC's slots hold an item or a spell (what a copy would bring over).</summary>
    public static int RetailShortcuts(ReadOnlySpan<uint> ids, ReadOnlySpan<uint> spells)
    {
        int n = 0;
        for (int i = 0; i < ids.Length; i++)
            if (ids[i] != 0 || (i < spells.Length && spells[i] != 0)) n++;
        return n;
    }

    /// <summary>"F9, Ctrl+1" as its keys.</summary>
    public static string[] SplitKeys(string keys) =>
        string.IsNullOrEmpty(keys) ? Array.Empty<string>() : keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
