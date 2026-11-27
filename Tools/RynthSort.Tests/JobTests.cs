using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // RynthSort 0.6.0: pack jobs. A side pack can hold what RynthAi picks: Salvage (its loot
    // profile's first matching rule says Salvage), Sell (its AutoVendor would sell it), Mule (its
    // AutoTrade would give it to the mule). RynthAi answers through RynthAi.Hub (read only); when it
    // can't (not loaded, too old, no profile), that pack changes nothing and the window says why.
    internal static partial class Program
    {
        private static JobAnswers Answers(JobKind kind, params uint[] marked)
        {
            var a = new JobAnswers();
            var ja = new JobAnswer { Known = true, Text = "Hunting.utl" };
            foreach (uint id in marked) ja.Marked.Add(id);
            a.ByKind[kind] = ja;
            return a;
        }

        private static PlanOptions JobOpt(SortInventory inv, List<PackJob> jobs, JobAnswers? answers) =>
            new() { Jobs = JobPlan.For(jobs, inv, answers) };

        private static void JobsPlan()
        {
            // Pack 3 salvages; armor goes to pack 2 by kind.
            SortInventory inv = Inv(3);
            inv.Packs[2].Name = "Salvage Sack";
            inv.Main.Items.Add(It(10, "Celdon Girth", ObjectClasses.Armor));      // the loot profile says Salvage
            inv.Main.Items.Add(It(11, "Amuli Coat", ObjectClasses.Armor));        // it says Keep
            inv.Main.Items.Add(It(12, "Black Opal", ObjectClasses.Gem));          // Salvage, but a name rule sends it to pack 1
            var rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Armor, 2), SortRule.ForName("Black Opal", 1) };
            var jobs = new List<PackJob>();
            Check(PackJobs.Set(jobs, inv, 3, JobKind.Salvage, null) && !PackJobs.Set(jobs, inv, 3, JobKind.Salvage, null), "a job set once");
            Check(jobs.Single().PackId == inv.Packs[2].Id && PackJobs.On(jobs, inv, 3)?.Kind == JobKind.Salvage, "kept by the pack's id");
            Check(!PackJobs.Set(jobs, inv, 7, JobKind.Sell, null), "a pack not carried");

            var planner = new SortPlanner();
            List<SortMove> moves = planner.Plan(inv, rules, SortScope.All, force: true, 0, 0, JobOpt(inv, jobs, Answers(JobKind.Salvage, 10, 12)));
            SortMove Of(uint id) => moves.Single(m => m.Item.Id == id);
            Eq(Of(10).DestIndex, 3, "marked Salvage: to the Salvage pack, before its kind's pack");
            Eq(Of(11).DestIndex, 2, "not marked: its kind's pack");
            Eq(Of(12).DestIndex, 1, "a name rule beats the job");

            // RynthAi can't answer: nothing new goes in, and what is in the pack stays (Clean too).
            inv.Packs[2].Items.Add(It(20, "Celdon Sleeves", ObjectClasses.Armor));
            foreach (JobAnswers? none in new[] { null, new JobAnswers { ByKind = { [JobKind.Salvage] = JobAnswer.Unknown("RynthAi isn't loaded") } } })
            {
                moves = planner.Plan(inv, rules, SortScope.Clean, force: true, 0, 0, JobOpt(inv, jobs, none));
                Check(!moves.Any(m => m.Item.Id == 20) && !moves.Any(m => m.DestIndex == 3), "unknown: the Salvage pack's items stay, nothing goes in");
                Eq(moves.Single(m => m.Item.Id == 10).DestIndex, 2, "the marked coat goes by its kind meanwhile");
            }
            // Known, and an item in the pack is no longer marked: it follows its rules again.
            moves = planner.Plan(inv, rules, SortScope.All, force: true, 0, 0, JobOpt(inv, jobs, Answers(JobKind.Salvage, 10)));
            Eq(moves.Single(m => m.Item.Id == 20).DestIndex, 2, "not salvage any more: to its kind's pack");
            inv.Packs[2].Items.Add(It(21, "Odd Thing", ObjectClasses.Misc));
            moves = planner.Plan(inv, rules, SortScope.Clean, force: true, 0, 0, JobOpt(inv, jobs, Answers(JobKind.Salvage, 10)));
            Eq(moves.Single(m => m.Item.Id == 21).DestIndex, 0, "Clean: an unmarked item no rule wants goes back to the main pack");
            moves = planner.Plan(inv, rules, SortScope.All, force: true, 0, 0, JobOpt(inv, jobs, Answers(JobKind.Salvage, 10, 21)));
            Check(!moves.Any(m => m.Item.Id == 21), "marked and in its job pack: it stays");

            // Held items aren't moved for a job; a full job pack says what didn't fit.
            var held = new SortPlanner { Held = id => id == 10 };
            moves = held.Plan(inv, rules, SortScope.All, force: true, 0, 0, JobOpt(inv, jobs, Answers(JobKind.Salvage, 10)));
            Check(!moves.Any(m => m.Item.Id == 10) && held.View.Held.Any(h => h.Item.Id == 10 && h.To == 3), "held: left alone, would go to the Salvage pack");
            SortInventory full = Inv(1, cap: 1);
            full.Packs[0].Name = "Salvage Sack";
            full.Packs[0].Items.Add(It(1, "Celdon Girth", ObjectClasses.Armor));
            full.Main.Items.Add(It(2, "Amuli Coat", ObjectClasses.Armor));
            var fj = new List<PackJob>();
            PackJobs.Set(fj, full, 1, JobKind.Salvage, null);
            moves = planner.Plan(full, new List<SortRule>(), SortScope.All, force: true, 0, 0, JobOpt(full, fj, Answers(JobKind.Salvage, 1, 2)));
            Check(moves.Count == 0 && planner.View.NoFit.Single().Pack == 1, "a full Salvage pack: it waits, and the banner names it");

            // Two packs with the same job: the second takes what the first can't.
            SortInventory two = Inv(2, cap: 1);
            two.Main.Items.Add(It(1, "A", ObjectClasses.Armor));
            two.Main.Items.Add(It(2, "B", ObjectClasses.Armor));
            var tj = new List<PackJob>();
            PackJobs.Set(tj, two, 1, JobKind.Sell, null);
            PackJobs.Set(tj, two, 2, JobKind.Sell, null);
            moves = planner.Plan(two, new List<SortRule>(), SortScope.All, force: true, 0, 0, JobOpt(two, tj, Answers(JobKind.Sell, 1, 2)));
            Check(moves.Count == 2 && moves[0].DestIndex == 1 && moves[1].DestIndex == 2, "Sell: the first pack, then the next with the same job");

            // What to ask RynthAi, and the mule.
            var mj = new List<PackJob>();
            PackJobs.Set(mj, two, 1, JobKind.Mule, "  Packmule ");
            PackJobs.Set(mj, two, 2, JobKind.Salvage, null);
            List<JobKind> wanted = PackJobs.Wanted(mj, two, out string mule);
            Check(wanted.SequenceEqual(new[] { JobKind.Mule, JobKind.Salvage }) && mule == "Packmule", "the jobs carried, and the mule's name trimmed");
            Check(PackJobs.Set(mj, two, 1, JobKind.Mule, "Other") && PackJobs.On(mj, two, 1)!.Mule == "Other", "another mule: changed");
            Check(PackJobs.Set(mj, two, 1, JobKind.None, null) && PackJobs.On(mj, two, 1) == null && !PackJobs.Set(mj, two, 1, JobKind.None, null), "taken off once");
            SortInventory moved = Inv(2);
            moved.Packs.RemoveAt(1);
            Eq(PackJobs.Wanted(mj, moved, out _).Count, 0, "a job's pack not carried: nothing to ask");
        }

        /// <summary>The fake world with RynthAi's answers (FakeWorld answers nothing, as with RynthAi not loaded).</summary>
        private sealed class JobWorld : ISortWorld
        {
            public readonly FakeWorld W = new();
            public JobAnswers? Answers;
            public int Asked;
            public SortInventory? ReadInventory() => W.ReadInventory();
            public GateInputs ReadGate(bool sortInCombat, long nowMs) => W.ReadGate(sortInCombat, nowMs);
            public bool SendMove(in SortMove move) => W.SendMove(in move);
            public MoveObservation Observe(in SortMove move) => W.Observe(in move);
            public int ErrorSeqNow() => W.ErrorSeqNow();
            public SortAbilities Abilities => W.Abilities;
            public JobAnswers? AskJobs(SortInventory inv, IReadOnlyList<JobKind> wanted, string mule, long nowMs) { Asked++; return Answers; }
        }

        private static void JobsLoopWindowStore()
        {
            var w = new JobWorld();
            w.W.Inv.Packs[2].Name = "Junk Bag";
            w.W.Inv.Main.Items.Add(It(10, "Plain Ring", ObjectClasses.Jewelry));
            SortController c = Controller(auto: true);
            c.Data.Rules.Clear();
            for (long t = 0; t <= 2000; t += 16) c.Tick(w, t);
            Eq(w.Asked, 0, "no job: RynthAi isn't asked");
            PackJobs.Set(c.Data.Jobs, w.W.Inv, 3, JobKind.Sell, null);
            for (long t = 2000; t <= 5000; t += 16) c.Tick(w, t);
            Check(w.Asked > 0 && w.W.Inv.Main.Items.Any(i => i.Id == 10), "asked; no answer (RynthAi not loaded): the ring stays");
            ScreenModel m = Screen(c);
            Check(m.Packs[2].JobLabel == "Sell" && m.Packs[2].JobLine == "Asking RynthAi which items belong here...", $"the card ({m.Packs[2].JobLine})");
            w.Answers = new JobAnswers { ByKind = { [JobKind.Sell] = JobAnswer.Unknown("RynthAi isn't loaded") } };
            for (long t = 5000; t <= 7000; t += 16) c.Tick(w, t);
            m = Screen(c);
            Check(m.Packs[2].JobProblem && m.Packs[2].JobLine == "Sell can't work now: RynthAi isn't loaded. What is in this pack stays put until it can.", $"says why ({m.Packs[2].JobLine})");
            w.Answers = Answers(JobKind.Sell, 10);
            w.Answers.ByKind[JobKind.Sell].Text = "default.utl";
            for (long t = 7000; t <= 10_000; t += 16) c.Tick(w, t);
            Check(w.W.Inv.Packs[2].Items.Any(i => i.Id == 10), "RynthAi says sell: the ring went to the Junk Bag");
            Check(c.Feed.Any(f => f.Text == "Plain Ring -> Junk Bag"), "the feed names it");
            m = Screen(c);
            Check(!m.Packs[2].JobProblem && m.Packs[2].JobLine == "Takes what RynthAi's AutoVendor would sell (default.utl).", m.Packs[2].JobLine);
            TileModel ring = m.TilesById[10];
            Check(ring.Rule == TileRule.Job && ring.RuleText == "Sell", "the tile knows its job");
            Eq(string.Concat(ScreenText.Why(ring, m).Select(r => r.Text)), "In its pack: Junk Bag, by its job Sell.", "why");

            // A move row for a job.
            w.W.Inv.Main.Items.Add(It(11, "Gold Ring", ObjectClasses.Jewelry));
            w.Answers.ByKind[JobKind.Sell].Marked.Add(11);
            c.SetAuto(false);
            for (long t = 10_000; t <= 12_000; t += 16) c.Tick(w, t);
            m = Screen(c);
            Check(m.Moves.Any(r => r.ItemId == 11 && r.Sub == "Job: Sell" && r.Right == "Junk Bag"), "Next moves: Job: Sell");
            Check(m.Packs[2].Ghosts.Any(g => g.ItemId == 11), "a ghost on the job pack");

            // Mule label.
            PackJobs.Set(c.Data.Jobs, w.W.Inv, 1, JobKind.Mule, "Packmule");
            Eq(Screen(c).Packs[0].JobLabel, "Mule: Packmule", "a mule's pill");

            // The file: kept by pack id; files from before have none; odd entries are dropped.
            var d = new CharacterSortData();
            d.Jobs.Add(new PackJob { PackId = 0x40000003, Pack = 3, Kind = JobKind.Salvage });
            d.Jobs.Add(new PackJob { PackId = 0x40000001, Pack = 1, Kind = JobKind.Mule, Mule = "Packmule" });
            using var doc = System.Text.Json.JsonDocument.Parse(SortStore.ToJson(d));
            CharacterSortData back = SortStore.Parse(doc.RootElement);
            Check(back.Jobs.Count == 2 && back.Jobs[0].Kind == JobKind.Salvage && back.Jobs[0].PackId == 0x40000003
                  && back.Jobs[1].Kind == JobKind.Mule && back.Jobs[1].Mule == "Packmule", "saved and read back");
            using var old = System.Text.Json.JsonDocument.Parse("{\"rules\":[]}");
            Eq(SortStore.Parse(old.RootElement).Jobs.Count, 0, "an older file: no jobs");
            using var odd = System.Text.Json.JsonDocument.Parse("{\"jobs\":[{\"job\":\"juggle\",\"pack\":1},7,{\"job\":\"SELL\",\"pack\":99,\"mule\":\"x\"}]}");
            CharacterSortData o = SortStore.Parse(odd.RootElement);
            Check(o.Jobs.Count == 1 && o.Jobs[0].Kind == JobKind.Sell && o.Jobs[0].Pack == SortStore.MaxPack && o.Jobs[0].Mule == "", "odd entries: unknown jobs dropped, numbers clamped, a mule only for Mule");
            using var none = System.Text.Json.JsonDocument.Parse(SortStore.ToJson(new CharacterSortData()));
            Check(!none.RootElement.TryGetProperty("jobs", out _), "no jobs: nothing written");
        }
    }
}
