using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.RynthSort.Views;

namespace RynthSort.Tests
{
    // Pack numbers against the game's pack column: which containers are packs, the column's order
    // with foci, wielded and unloaded containers in it, why an order isn't known, and what each
    // pack's header says and does (icon, Select in game, its place in the column).
    internal static partial class Program
    {
        private static void PackColumnKinds()
        {
            Check(InventoryReader.IsSidePack(false, true, 24, 0), "a pack whose capacity reads (empty): a pack");
            Check(!InventoryReader.IsSidePack(false, true, 0, 0), "a focus (capacity 0): not a pack");
            Check(!InventoryReader.IsSidePack(true, true, 24, 3), "a wielded container: not a pack");
            Check(InventoryReader.IsSidePack(false, false, 0, 5), "capacity not read yet but items inside: a pack (else every pack below it is numbered one too high)");
            Check(!InventoryReader.IsSidePack(false, false, 0, 0), "capacity not read and nothing inside: not guessed");
        }

        private static void PackColumnOrder()
        {
            // Column, top first: pack 3, a focus, pack 1, pack 2. The main pack itself in the list changes nothing.
            SortInventory inv = Inv(3);
            Check(inv.ApplyPackOrder(new uint[] { 0x40000003, 0x40000F01, 0x40000001, Me, 0x40000002 }, 5), "foci and the main pack's own id in the list");
            Check(inv.PackOrderKnown && inv.OrderSource == PackOrderSource.Known, "known");
            Eq(string.Join(",", inv.Packs.Select(p => p.Id & 0xF)), "3,1,2", "pack 1 is the top pack");
            Eq(string.Join(",", inv.Packs.Select(p => p.ColumnSlot)), "1,3,5", "each pack's slot in the column, foci counted");
            Eq(inv.ColumnEntries, 5, "the column's entries");

            // A wielded container is never read as a pack, so the column (main pack only) needn't have it.
            SortInventory worn = Inv(2);
            Check(worn.ApplyPackOrder(new uint[] { 0x40000002, 0x40000001 }, 2), "only carried packs need be in the column");

            // An unloaded pack (read through its items) that the engine's list lacks.
            SortInventory miss = Inv(3);
            Check(!miss.ApplyPackOrder(new uint[] { 0x40000003, 0x40000001 }, 2), "a pack the list lacks: not known");
            Check(miss.OrderSource == PackOrderSource.Mismatch && miss.MissingFromColumn == 1, "says it is a mismatch, one pack short");
            Check(miss.Packs.All(p => p.ColumnSlot == 0), "no column slots claimed");
            Eq(string.Join(",", miss.Packs.Select(p => p.Id & 0xF)), "1,2,3", "object-id order kept (as the engine's Inventory panel falls back to)");

            // A later good read clears the mismatch.
            Check(miss.ApplyPackOrder(new uint[] { 0x40000002, 0x40000003, 0x40000001 }, 3) && miss.MissingFromColumn == 0, "a full list again: known");

            // The log lines (one when the source changes).
            Check(InventoryReader.OrderSourceLine(inv).Contains("follow the pack column"), "log: known");
            SortInventory notSeen = Inv(2);
            notSeen.OrderSource = PackOrderSource.NotSeen;
            Check(InventoryReader.OrderSourceLine(notSeen).Contains("no pack column list"), "log: not seen");
            Check(InventoryReader.OrderSourceLine(new SortInventory()).Contains("API 80"), "log: no engine support");
            Check(InventoryReader.OrderSourceLine(Inv(3).With(i => i.ApplyPackOrder(new uint[] { 0x40000001 }, 1))).Contains("lacks 2 of 3"), "log: a mismatch, counted");
        }

        private static SortInventory With(this SortInventory inv, Action<SortInventory> a)
        {
            a(inv);
            return inv;
        }

        private static SortView ViewOf(SortInventory inv)
        {
            var p = new SortPlanner();
            p.Plan(inv, SortRules.Defaults(), SortScope.Main, true, 0, 0);
            return p.View;
        }

        private static void PackColumnLabels()
        {
            Eq(string.Join(",", new[] { 1, 2, 3, 4, 11, 12, 13, 21, 22, 23, 101, 111 }.Select(SortPanel.Ordinal)),
                "1st,2nd,3rd,4th,11th,12th,13th,21st,22nd,23rd,101st,111th", "ordinals");

            SortInventory inv = Inv(3);
            for (uint i = 0; i < 14; i++) inv.Packs[2].Items.Add(It(500 + i, "Salvaged Iron", ObjectClasses.Salvage));
            inv.ApplyPackOrder(new uint[] { 0x40000003, 0x40000F01, 0x40000001, 0x40000002 }, 4);
            SortView v = ViewOf(inv);
            Check(v.PackOrderKnown && v.OrderSource == PackOrderSource.Known, "the view carries the order");
            Eq(v.Packs[0].ColumnSlot, 1, "the view carries each pack's slot");
            Eq(SortPanel.WhereText(v, v.Packs[0], 1), "1st pack from the top of the pack column - 14/24 items", "the top pack");
            Eq(SortPanel.WhereText(v, v.Packs[1], 2), "2nd pack from the top of the pack column - 0/24 items", "a pack below a focus: its pack number");
            Check(SortPanel.PackHeaderTooltip(v, 2).Contains("also shows the focus above it, so there it is the 3rd entry"), "and the tooltip says where the retail window has it");
            Check(!SortPanel.PackHeaderTooltip(v, 1).Contains("also shows"), "no focus above: nothing extra");
            string tip = SortPanel.PackHeaderTooltip(v, 1);
            Check(tip.StartsWith("Your Pack 3: 14 of 24 items (Salvage).", StringComparison.Ordinal), "header tooltip: which pack, its count and contents");
            Check(tip.Contains("It is the 1st pack from the top of your inventory's pack column."), "header tooltip: where it is");
            Check(tip.Contains("Select in game"), "header tooltip: how to see it in the game");
            Eq(SortPanel.NumberingNote(v), "", "known: no note above the packs");

            SortInventory unknown = Inv(2);
            unknown.OrderSource = PackOrderSource.NotSeen;
            SortView u = ViewOf(unknown);
            Eq(SortPanel.WhereText(u, u.Packs[1], 2), "Place in the pack column not known - 0/24 items", "unknown: says so");
            Check(SortPanel.PackHeaderTooltip(u, 2).Contains("isn't known right now") && !SortPanel.PackHeaderTooltip(u, 2).Contains("from the top"), "unknown: the tooltip claims no place");
            Check(SortPanel.NumberingNote(u).Contains("hasn't seen your pack list") && SortPanel.NumberingNote(u).Contains("Log out and back in"), "not seen: why and the fix");
            unknown.ApplyPackOrder(new uint[] { 0x40000002 }, 1);
            Check(SortPanel.NumberingNote(ViewOf(unknown)).Contains("missing a pack"), "mismatch: why");
            Check(SortPanel.NumberingNote(ViewOf(Inv(2))).Contains("plugin API 80"), "no engine support: why");
            Eq(SortPanel.NumberingNote(ViewOf(Inv(0))), "", "no side packs: no note");
        }

        private static void PackColumnWindow()
        {
            var c = new SortController();
            var selected = new List<uint>();
            var panel = new SortPanel(c, () => { })
            {
                Character = () => "Someone",
                Icons = () => true,
                CanSelect = () => true,
                SelectInGame = id => { selected.Add(id); return true; },
            };
            var host = new UiHost();
            UiWindow w = host.Add(new UiWindow("RynthSort/Main", "RynthSort") { Visible = true });
            SortInventory inv = Inv(3);
            Fill(inv.Packs[1], 5, 1000);
            inv.ApplyPackOrder(new uint[] { 0x40000002, 0x40000F01, 0x40000003, 0x40000001 }, 4);
            c.Planner.Plan(inv, c.Data.Rules, SortScope.Main, true, 0, 0);

            Draw(w, panel);
            ParseAll(host, "pack headers with icons and Select", out int ops);
            Check(ops > 10 && ops < 1500, $"ops in range ({ops})");
            string body = BodyText(w);
            Check(body.Contains("Pack 1: Pack 2 (5/24)"), "the top pack (id 2) is pack 1");
            Check(body.Contains("Select in game###select.40000002") && body.Contains("Select in game###select.40000001"), "a Select in game button per pack");
            Check(body.Contains("1st pack from the top of the pack column - 5/24 items"), "where it is, with its item count");
            Check(body.Contains("2nd pack from the top of the pack column - 0/24 items"), "the next pack, below a focus");
            Check(body.Contains("so there it is the 3rd entry"), "the focus above it: said in its tooltip");
            Check(body.Contains("Selects this pack in the game"), "the button says what it does");
            Check(!body.Contains("oldest pack first, not"), "known: no numbering note");
            Check(ContainsOp(w.Body, UiFormat.OpImage), "the pack's icon drawn");
            w.MarkSubmitted(true);

            Apply(host, Event(1, 1, w.Hash, w.KeyOf("select.40000003"), Click));
            Eq(string.Join(",", selected.Select(x => x.ToString("X8"))), "40000003", "Select in game selects that pack's object");
            Draw(w, panel);
            ParseAll(host, "after Select", out _);
            Check(BodyText(w).Contains("the game has your Pack 3 selected now"), "and says so under it");

            // Without icon support or selection, neither is drawn (the layout doesn't shift on old engines).
            panel.Icons = () => false;
            panel.CanSelect = () => false;
            Draw(w, panel);
            ParseAll(host, "old engine", out _);
            Check(!BodyText(w).Contains("Select in game###") && !ContainsOp(w.Body, UiFormat.OpImage), "no icon or Select button when the engine can't");
            Check(BodyText(w).Contains("pack from the top of the pack column"), "the place is still said");

            // Order unknown: the note above the packs, and no place claimed.
            SortInventory lost = Inv(2);
            lost.OrderSource = PackOrderSource.NotSeen;
            c.Planner.Plan(lost, c.Data.Rules, SortScope.Main, true, 0, 0);
            Draw(w, panel);
            ParseAll(host, "order unknown", out _);
            body = BodyText(w);
            Check(body.Contains("Pack numbers here go oldest pack first") && body.Contains("Log out and back in"), "the note says why and the fix");
            Check(body.Contains("Place in the pack column not known"), "each pack says its place isn't known");
        }

        /// <summary>Whether the display list has an op of this kind (each op: u8 code, u8 flags, u16 payload length, payload).</summary>
        private static bool ContainsOp(ReadOnlySpan<byte> body, byte op)
        {
            int at = 0;
            while (at + 4 <= body.Length)
            {
                if (body[at] == op) return true;
                at += 4 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 2)..]);
            }
            return false;
        }
    }
}
