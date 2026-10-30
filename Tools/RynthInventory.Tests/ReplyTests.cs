using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthInventory;

namespace RynthInventory.Tests
{
    // Every /ginv command answers in chat (2026-10-28). RunCommand needs the plugin's host, so the parse
    // of every /ginv word and the lines it says are checked here.
    internal static partial class Program
    {
        private static void Replies()
        {
            var words = new (string Typed, InventoryVerb Verb, string Text)[]
            {
                ("", InventoryVerb.Window, ""), ("scan", InventoryVerb.Scan, ""), ("RESCAN", InventoryVerb.Scan, ""),
                ("keep", InventoryVerb.Keep, ""), ("reload", InventoryVerb.Reload, ""), ("status", InventoryVerb.Status, ""),
                ("help", InventoryVerb.Help, ""), ("?", InventoryVerb.Help, ""),
                ("Pyreal Mote", InventoryVerb.Search, "Pyreal Mote"), (" scan me ", InventoryVerb.Search, "scan me"),
            };
            var seen = new HashSet<InventoryVerb>();
            foreach (var (typed, verb, text) in words)
            {
                InventoryCommand c = InventoryCommand.Parse(typed);
                Eq(c.Verb, verb, $"'/ginv {typed}'");
                Eq(c.Text, text, $"'/ginv {typed}': the search keeps its case");
                seen.Add(c.Verb);
            }
            foreach (InventoryVerb v in Enum.GetValues<InventoryVerb>()) Check(seen.Contains(v), $"the table covers /ginv's {v}");
            Eq(InventoryCommand.Window(true), "Global Inventory shown.", "/ginv: shown");
            Eq(InventoryCommand.Window(false), "Global Inventory hidden.", "/ginv: hidden");
            Eq(InventoryCommand.Searching(" Pyreal Mote "), "Global Inventory: searching for 'Pyreal Mote'.", "/ginv <text>");
            Check(InventoryCommand.Usage.Contains("/ginv status"), "help: the usage");
            Check(InventoryCommand.NoWindows.Contains("can't show plugin windows"), "no plugin windows: why");
        }
    }
}
