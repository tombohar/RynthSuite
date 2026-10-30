using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthDefective.Tests
{
    // Spell slots (2026-10-28): the spell table and search, whom a cast goes to, a slot's cast through
    // the use controller (Magic mode first, the refusals), putting a spell in a slot, the file.
    internal static partial class Program
    {
        private const uint HealSelf = 6, HealOther = 5, FlameBolt = 27, FrostBolt = 28, LifestoneRecall = 1635;

        private static void SpellTableAndSearch()
        {
            Check(SpellTable.Count > 5000, "the table is in the plugin: " + SpellTable.Count);
            Check(SpellTable.TryGet(HealSelf, out SpellInfo hs) && hs.Name == "Heal Self I" && hs.SelfTargeted && !hs.Harmful, "Heal Self I: on yourself, not harmful");
            Check(SpellTable.TryGet(HealOther, out SpellInfo ho) && ho.Name == "Heal Other I" && !ho.SelfTargeted && !ho.Harmful, "Heal Other I: targeted, helpful");
            Check(SpellTable.TryGet(FlameBolt, out SpellInfo fb) && fb.Name == "Flame Bolt I" && fb.Harmful && !fb.SelfTargeted, "Flame Bolt I: harmful");
            Check(SpellTable.TryGet(LifestoneRecall, out SpellInfo ls) && ls.SelfTargeted, "Lifestone Recall: on yourself");
            Eq(SpellTable.Name(999_999), "Spell 999999", "a spell the table doesn't have goes by number");

            Dictionary<uint, SpellInfo> parsed = SpellTable.Parse(new StringReader(
                "x\ty\n0\tZero\n7\t \n12\tSome Spell\t1\t1\tw\t-1\t1\tFalse\tTrue\tTrue\n13\tShort\n"));
            Check(parsed.Count == 2 && parsed[12].Harmful && parsed[12].SelfTargeted && !parsed[13].Harmful && !parsed[13].SelfTargeted,
                "odd lines are skipped; a short line has no flags");

            SpellInfo[] known = SpellTable.Known(new uint[] { FlameBolt, HealSelf, HealOther, HealSelf, 0, 999_999 });
            Eq(string.Join("|", known.Select(k => k.Name)), "Flame Bolt I|Heal Other I|Heal Self I|Spell 999999", "the character's spells A-Z, once each");

            SpellInfo[] list = SpellTable.Known(new uint[] { HealSelf, HealOther, FlameBolt, FrostBolt, LifestoneRecall });
            List<SpellInfo> r = SpellTable.Search(list, "heal self i", 10, out int total);
            Check(total == 1 && r[0].Id == HealSelf, "the exact name, any case");
            r = SpellTable.Search(list, "Heal", 10, out total);
            Eq(string.Join("|", r.Select(x => x.Name)), "Heal Other I|Heal Self I", "names starting with it");
            r = SpellTable.Search(list, "bolt", 10, out total);
            Eq(string.Join("|", r.Select(x => x.Name)), "Flame Bolt I|Frost Bolt I", "a word anywhere");
            r = SpellTable.Search(list, "frost i", 10, out total);
            Check(total == 1 && r[0].Id == FrostBolt, "every word of it");
            r = SpellTable.Search(list, "", 3, out total);
            Check(r.Count == 3 && total == 5, "blank: all of them, at most the limit");
            SpellTable.Search(list, "zzz", 10, out total);
            Eq(total, 0, "nothing matches");
        }

        private static void SpellCastTarget()
        {
            var self = new SpellInfo(HealSelf, "Heal Self I", true, false);
            var helpful = new SpellInfo(HealOther, "Heal Other I", false, false);
            var harmful = new SpellInfo(FlameBolt, "Flame Bolt I", false, true);
            const uint mob = 0x8000_1234u;

            Eq(SpellTargeting.Decide(self, mob, Me, out uint t, out _), CastAt.Yourself, "a self spell goes on you, whatever is selected");
            Eq(t, Me, "on you");
            Eq(SpellTargeting.Decide(helpful, mob, Me, out t, out _), CastAt.Selection, "a targeted spell goes on the selection");
            Eq(t, mob, "the selected object");
            Eq(SpellTargeting.Decide(helpful, 0, Me, out t, out _), CastAt.Yourself, "a helpful one with nothing selected: on you");
            Eq(t, Me, "on you");
            Eq(SpellTargeting.Decide(helpful, Me, Me, out t, out _), CastAt.Yourself, "with yourself selected: on you");
            Eq(SpellTargeting.Decide(harmful, mob, Me, out t, out _), CastAt.Selection, "a harmful one on the selected monster");
            Eq(t, mob, "the monster");
            Eq(SpellTargeting.Decide(harmful, 0, Me, out t, out string why), CastAt.Refused, "a harmful one with nothing selected: refused");
            Check(t == 0 && why.Contains("select a target"), "and says to select one: " + why);
            Eq(SpellTargeting.Decide(harmful, Me, Me, out _, out why), CastAt.Refused, "a harmful one at yourself: refused");
            Check(why.Contains("monster"), "says to select a monster: " + why);
            Eq(SpellTargeting.Decide(null, mob, Me, out t, out _), CastAt.Selection, "a spell the table doesn't know: on the selection");
            Eq(SpellTargeting.Decide(null, 0, Me, out t, out _), CastAt.Yourself, "else on you");
            Eq(SpellTargeting.Decide(self, 0, 0, out _, out why), CastAt.Refused, "no character yet: refused");
            Check(SpellTargeting.Describe(self) == "cast on you" && SpellTargeting.Describe(harmful).Contains("selected target")
                  && SpellTargeting.Describe(helpful).Contains("or on you"), "the tooltip words");
        }

        private static Harness Caster(params uint[] known)
        {
            Harness h = With();
            h.World.Known = new HashSet<uint>(known);
            return h;
        }

        private static bool Cast(Harness h, uint spell, string name) =>
            h.Use.Request(UseRequest.Cast(spell, name, "slot", h.World.NowMs));

        private static void SpellUse()
        {
            // From peace: Magic mode first, the cast once the stance has settled, answered by UseDone.
            Harness h = Caster(HealSelf, HealOther, FlameBolt);
            Check(Cast(h, HealSelf, "Heal Self I"), "queued");
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "mode 8", "at peace: Magic mode first");
            Check(h.Use.InFlight && h.Said.Last().Contains("Magic mode"), "in flight, and says so: " + string.Join(";", h.Said));
            h.Tick(500);
            h.Tick(500);
            Eq(h.World.Sent.Count, 1, "the stance settles first");
            h.Tick(400);
            Eq(string.Join("|", h.World.Sent), "mode 8|cast 6 me", "then the cast, on you");
            Eq(h.Said.Last(), "Casting Heal Self I...", "status");
            Check(h.Use.InFlight && h.Use.InvolvesSpell(HealSelf), "waiting for the server's answer");
            Check(!Cast(h, HealSelf, "Heal Self I") || h.Use.HasPending, "pressed again during the cast: one more, once");
            Check(!Cast(h, HealSelf, "Heal Self I"), "a held key queues once");
            h.World.Answer();
            h.Tick(100);
            Check(!h.Use.InFlight, "answered");
            h.Tick(300);
            Eq(h.World.Sent.Last(), "cast 6 me", "the second press casts again, Magic mode already in place");

            // Already in Magic: cast at once. A harmful spell on the selected monster.
            h = Caster(FlameBolt);
            h.World.CombatMode = Stance.Magic;
            h.World.SelectedId = 0x1234;
            Cast(h, FlameBolt, "Flame Bolt I");
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "cast 27 1234", "in Magic already: cast on the selection at once");
            Eq(h.Said.Last(), "Casting Flame Bolt I on the selection...", "status");

            // Refusals: nothing selected for a harmful spell, a spell the character doesn't know, no casting at all.
            h = Caster(FlameBolt);
            h.World.CombatMode = Stance.Magic;
            Cast(h, FlameBolt, "Flame Bolt I");
            h.Tick();
            Check(h.World.Sent.Count == 0 && !h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0] == "Flame Bolt I: not cast, select a target first.",
                "a war spell with nothing selected: refused, said: " + string.Join(";", h.Warned));
            h = Caster(HealSelf);
            Cast(h, FlameBolt, "Flame Bolt I");
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Warned.Count == 1 && h.Warned[0] == "Flame Bolt I: not cast, you don't know that spell.",
                "a spell the character doesn't know: refused, no mode change: " + string.Join(";", h.Warned));
            h = With();
            h.World.Known = null;   // the spellbook can't be read: the server decides
            h.World.CombatMode = Stance.Magic;
            Cast(h, HealSelf, "Heal Self I");
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "cast 6 me", "spellbook unreadable: cast anyway");
            h = Caster(HealSelf);
            h.World.CanCast = false;
            Cast(h, HealSelf, "Heal Self I");
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Warned.Count == 1 && h.Warned[0].Contains("can't cast"), "an engine that can't cast: said");
            h = Caster(HealSelf);
            h.World.CombatMode = Stance.Magic;
            h.World.RefuseSends = true;
            Cast(h, HealSelf, "Heal Self I");
            h.Tick();
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("didn't take the cast"), "the client turns the cast away: said, not waited on");

            // Magic mode never comes (a sword in hand): asked once, then said, nothing cast.
            h = Caster(HealSelf);
            h.World.CombatMode = Stance.Melee;
            h.World.ModeRefused = true;
            Cast(h, HealSelf, "Heal Self I");
            h.Tick();
            h.Ticks(3, 1000);
            Check(h.Use.InFlight && h.Warned.Count == 0, "still waiting for the stance");
            h.Tick(1100);
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("didn't go into magic mode (melee now)"), "the stance never came: said: " + string.Join(";", h.Warned));
            Eq(string.Join("|", h.World.Sent), "mode 8", "asked once, never cast");

            // The busy rules hold a cast like a use; "too busy" from the server gets one more try.
            h = Caster(HealSelf);
            h.World.CombatMode = Stance.Magic;
            h.Tick();
            h.World.BusyCount = 1;
            Cast(h, HealSelf, "Heal Self I");
            h.Ticks(2, 1000);
            Eq(h.World.Sent.Count, 0, "a busy count that just rose holds the cast");
            h.World.BusyCount = 0;
            h.Tick(100);
            Eq(string.Join("|", h.World.Sent), "cast 6 me", "and it goes once the character is free");
            h.World.Answer(UseController.TooBusyError);
            h.Tick(100);
            h.Tick(1100);
            Eq(string.Join("|", h.World.Sent), "cast 6 me|cast 6 me", "'too busy': cast once more a second later");
            Check(h.Logged.Any(l => l.Contains("the one more try")), "logged as the retry");
            h.World.Answer(UseController.TooBusyError);
            h.Tick(100);
            Check(h.Warned.Count == 1 && h.Warned[0].Contains("not cast, the server says the character is busy"), "twice: said: " + string.Join(";", h.Warned));
        }

        private static void SpellSession()
        {
            var rig = new Rig("spells");
            IReadOnlyList<SpellInfo> known = SpellTable.Known(new uint[] { HealSelf, HealOther, FlameBolt, FrostBolt, LifestoneRecall });
            rig.S.KnownSpells = () => known;

            string m = rig.S.SetSpell(3, HealSelf);
            DefSlot s3 = rig.S.Data.Slots[2];
            Check(s3.IsSpell && s3.SpellId == HealSelf && s3.SpellName == "Heal Self I" && !s3.Empty && s3.Item == null, "slot 3 casts Heal Self I");
            Check(m.Contains("Slot 3: Heal Self I") && m.Contains("cast on you"), "and says how: " + m);
            Check(rig.Saves > 0, "saved");
            Eq(s3.Label, "Heal Self I", "its label");

            m = rig.S.SetSpell(4, 2000);
            Check(rig.S.Data.Slots[3].Empty && m.Contains("don't know"), "a spell the character doesn't know: refused: " + m);
            rig.S.KnownSpells = () => Array.Empty<SpellInfo>();
            m = rig.S.SetSpell(4, 2000);
            Check(rig.S.Data.Slots[3].SpellId == 2000, "the spellbook unreadable: taken (the cast is refused then if it must be): " + m);
            rig.S.KnownSpells = () => known;

            m = rig.S.SetSpellFromName(5, "frost bolt i");
            Check(rig.S.Data.Slots[4].SpellId == FrostBolt, "by name, any case: " + m);
            m = rig.S.SetSpellFromName(6, "bolt");
            Check(rig.S.Data.Slots[5].Empty && m.Contains("2 of your spells match 'bolt'") && m.Contains("Flame Bolt I, Frost Bolt I"), "two match: says which: " + m);
            m = rig.S.SetSpellFromName(6, "lifestone");
            Check(rig.S.Data.Slots[5].SpellId == LifestoneRecall, "one starts with it: " + m);
            m = rig.S.SetSpellFromName(7, "nothing like it");
            Check(rig.S.Data.Slots[6].Empty && m.Contains("no spell named"), "none: " + m);
            rig.S.KnownSpells = () => Array.Empty<SpellInfo>();
            Check(rig.S.SetSpellFromName(7, "heal").Contains("can't be read"), "by name needs the spellbook");
            rig.S.KnownSpells = () => known;

            Check(rig.S.UseSlot(3) == null && rig.S.Use.Pending?.Spell == HealSelf && rig.S.Use.Pending.Label == "Heal Self I", "pressing a spell slot asks for its cast");
            Check(rig.S.UseSlot(8)!.Contains("empty"), "an empty slot says so");

            m = rig.S.BindKey(5, "F9", byName: true);
            Check(m.Contains("holds a spell") && rig.Chat.Count == 0, "a spell slot binds by slot only: " + m);

            rig.Inv = new List<CarriedItem> { Kit(10) };
            rig.S.SetSlot(3, Kit(10));
            Check(rig.S.Data.Slots[2].Item?.Id == 10 && rig.S.Data.Slots[2].SpellId == 0 && !rig.S.Data.Slots[2].IsSpell, "an item over a spell: the spell is gone");
            DefSlot clone = rig.S.Data.Slots[4].Clone();
            Check(clone.SpellId == FrostBolt && clone.SpellName == "Frost Bolt I", "a copy keeps the spell");
            rig.S.ClearSlot(5);
            Check(rig.S.Data.Slots[4].Empty, "cleared");

            DefCommand c = DefCommandParser.Parse("spell 3 Heal Self VI");
            Check(c.Verb == DefVerb.Spell && c.Slot == 3 && c.Text == "Heal Self VI", "/rdef spell 3 Heal Self VI");
            Check(DefCommandParser.Parse("spell 3").Verb == DefVerb.Error && DefCommandParser.Parse("spell x Heal").Verb == DefVerb.Error
                  && DefCommandParser.Parse("spell").Verb == DefVerb.Error, "a slot and a name are needed");
        }

        private static void SpellWindow()
        {
            // The script-window bar (engines without the Shortcuts bar): a spell slot is a picture, else its initials.
            Windows w = Stocked("bar-spell");
            w.S.Data.Slots[5] = new DefSlot { SpellId = HealSelf, SpellName = "Heal Self I" };
            w.DrawBar();
            ParsedWindow bar = ParseAll(w.Host, "bar with a spell", out _).First(p => p.Key == "RynthDefective/Bar");
            Eq(CountOps(bar, ScriptOp.ImageButton), 5, "the four items and the spell drawn as pictures");
            Eq(CountOps(bar, ScriptOp.Button), 3 + 3, "three empty slots and the toolbar");
            Check(w.BarText.Contains("Heal Self I, a spell.") && w.BarText.Contains("cast on you"), "its tooltip says what it casts");

            Windows t = Stocked("bar-spell-text", icons: false);
            t.S.Data.Slots[5] = new DefSlot { SpellId = HealSelf, SpellName = "Heal Self I" };
            t.DrawBar();
            ParseAll(t.Host, "bar with a spell, no pictures", out _);
            Check(t.BarText.Contains("HSI"), "no pictures: its initials");

            // The editor on the spell slot.
            w.Bar.EditOpen = true;
            w.Bar.Editor.Select(6);
            w.DrawBar();
            ParseAll(w.Host, "editor on a spell slot", out _);
            Check(w.BarText.Contains("Slot 6: Heal Self I") && w.BarText.Contains("6.  Heal Self I") && w.BarText.Contains("A spell: pressing the slot has it cast on you"),
                "the editor names the spell and how it is cast");
            Check(!w.BarText.Contains("Pressing it") && w.BarText.Contains("Bind to this slot"), "no item use setting; the key controls stay");
        }

        private static void SpellStore()
        {
            var st = new DefStore(Path.Combine(_temp, "spells"));
            var d = new DefData();
            d.Slots[0].Item = ItemRef.From(Kit(0x7100_0001u));
            d.Slots[1] = new DefSlot { SpellId = HealSelf, SpellName = "Heal Self I" };
            d.Slots[2] = new DefSlot { SpellId = 987_654, SpellName = "A Server's Own Spell" };
            Check(st.Save("S", "Spells", d) == null, "saved");
            DefData r = st.Load("S", "Spells", out string? problem);
            Check(problem == null, "read");
            Check(r.Slots[1].IsSpell && r.Slots[1].SpellId == HealSelf && r.Slots[1].Label == "Heal Self I" && r.Slots[1].Mode == UseMode.Auto, "a spell slot comes back");
            Check(r.Slots[2].SpellId == 987_654 && r.Slots[2].Label == "A Server's Own Spell", "a spell the table doesn't know keeps its saved name");
            Check(r.Slots[0].Item?.Name == "Peerless Healing Kit" && r.Slots[0].SpellId == 0 && r.Slots[3].Empty, "the item slot and the empty ones as they were");
            Eq(DefStore.ToJson(r), DefStore.ToJson(d), "writing what was read gives the same file");

            // A file from before spell slots: read unchanged, written back the same.
            const string old = "{\n  \"version\": 1,\n  \"slots\": [\n    {\n      \"item\": {\n        \"id\": 5,\n        \"name\": \"Kit\"\n      },\n      \"mode\": 2\n    },\n    {\n      \"mode\": 0\n    }\n  ]\n}";
            DefData o = DefStore.Parse(System.Text.Json.JsonDocument.Parse(old).RootElement);
            Check(o.Slots[0].Item?.Name == "Kit" && o.Slots[0].Mode == UseMode.OnSelf && o.Slots[0].SpellId == 0 && o.Slots.Skip(1).All(x => x.Empty),
                "an old file: the item, its mode, no spells");
            Check(!DefStore.ToJson(o).Contains("\"spell\""), "and no spell is written for it");

            // Hand-edited oddities: an item wins over a spell; a spell with no id, or not an object, is nothing.
            const string odd = "{\"slots\":[{\"item\":{\"id\":5,\"name\":\"Kit\"},\"spell\":{\"id\":6,\"name\":\"Heal Self I\"}},"
                               + "{\"spell\":{\"id\":0,\"name\":\"X\"}},{\"spell\":6},{\"spell\":{\"id\":6}},{\"spell\":{\"id\":-3,\"name\":\"Y\"}}]}";
            DefData x = DefStore.Parse(System.Text.Json.JsonDocument.Parse(odd).RootElement);
            Check(x.Slots[0].Item?.Name == "Kit" && x.Slots[0].SpellId == 0, "an item and a spell in one slot: the item");
            Check(x.Slots[1].Empty && x.Slots[2].Empty && x.Slots[4].Empty, "no id, not an object, a negative id: empty");
            Check(x.Slots[3].SpellId == HealSelf && x.Slots[3].Label == "Heal Self I", "an id with no name: named by the table");
        }
    }
}
