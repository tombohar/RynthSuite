using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Make kits (MiniRemote/Crafting/KitRunner.cs) on the fake engine: the pack read from the object
// cache (WCIDs, stack sizes), the plan the Kits tab shows, and a run that combines one pair at a
// time against a fake server that does what ACE's RecipeManager does (one of each item used, the
// product on a success, the recipe's chat line last). Only with the macro off; it stops when the
// macro turns on, the character moves, takes damage or leaves peace mode, and prints one summary.
internal static unsafe class KitRunnerTests
{
    public static void Register(Runner r)
    {
        r.Add("kits run: the pack and plan from the cache (WCIDs, stacks, Healing chance)", PackAndPlan);
        r.Add("kits run: 2 kits from raw herbs - herbs, kit, herbs, kit, one combine at a time, then the summary", MakesTwo);
        r.Add("kits run: a failure uses both items and is counted; the run goes on", FailureCounted);
        r.Add("kits run: Kits to make stops at the count", StopsAtCount);
        r.Add("kits run: refused with the macro on; the macro, moving, damage or combat mode stop it", Gates);
        r.Add("kits run: the result line after the counts finished the step isn't counted again", LateLine);
        r.Add("kits run: Crafting tab state - floor 90% and count 0 by default, saved", StateDefaults);
        r.Add("kits when low: StartAuto runs only with the macro on; one summary line with the next try; the end is heard", AutoRuns);
        r.Add("kits when low: the macro off, moving, damage, leaving peace and Stop end it, each with its kind", AutoStopKinds);
        r.Add("kits when low: AC's own craft dialog ends it (nobody to click); a run by hand still waits for the click", AutoDialog);
        r.Add("kits when low: the run by hand is unchanged (refused with the macro on, its own summary, Stop is the player's)", ManualUnchanged);
    }

    private const uint Player = 0x50000A01;
    private const uint Lb = 0xA9B40000;
    private const uint Bandages = 0x80500001, Hyssop = 0x80500002, Mandrake = 0x80500003;
    private const uint StackKey = 12;

    private sealed class Rig : IDisposable
    {
        public readonly LegacyUiSettings S = new();
        public readonly WorldObjectCache Cache;
        public readonly MiniRemoteContext Ctx;
        public readonly KitRunner K;
        public long Now = 5_000_000;
        public uint NextId = 0x80600001;
        /// <summary>Fail the next N combines (the fake server's roll).</summary>
        public int FailNext;
        /// <summary>Fail the combines with these numbers (1 = the first sent).</summary>
        public readonly HashSet<int> FailAt = new();
        private int _served;
        /// <summary>Send the result line before (true) or never (false) - ACE sends it last.</summary>
        public bool SendLines = true;
        private readonly List<(uint Src, uint Tgt)> _pending = new();
        private readonly bool _pacer;
        /// <summary>Instead of the combine, AC's own craft dialog comes up (the fake engine reports Shown).</summary>
        public bool DialogInstead;

        public Rig(int bandages, int hyssop, int mandrake, int healing = 350, bool dialogEngine = false)
        {
            FakeHost.Reset();
            DialogSeq = 0;
            if (dialogEngine)
                FakeHost.ConfigureApi = (ref RynthCoreApiNative api) =>
                {
                    api.Version = Math.Max(api.Version, 85u);
                    api.ArmConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int, int>)&FakeArm;
                    api.GetLastConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, int*, int*, int*, int>)&FakeLast;
                };
            _pacer = ActionPacer.Enabled;
            ActionPacer.Enabled = false;
            FakeHost.WeaponCalls = true;          // combat mode reads (peace)
            FakeHost.UseOnCalls = true;
            FakeHost.CombatModeValue = 1;         // NonCombat
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.Vitals = (200, 200, 200, 200, 200, 200);
            FakeHost.Skills[(Player, KitPlanner.SkillHealing)] = (healing, 2);
            var ids = new List<uint>();
            Carry(ids, Bandages, KitPlanner.SoftBandages, bandages);
            Carry(ids, Hyssop, KitPlanner.TreatedHyssop, hyssop);
            Carry(ids, Mandrake, KitPlanner.TreatedMandrake, mandrake);
            FakeHost.OnUseObjectOn = (src, tgt) => _pending.Add((src, tgt));
            var host = FakeHost.Create(Player);
            FakeHost.ConfigureApi = null;
            Cache = FakeHost.MakeCache(host, Player, ids);
            string dir = Path.Combine(Program.TempRoot, "kits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new MiniRemoteStore(dir);
            var cache = Cache;
            Ctx = new MiniRemoteContext(host, store.Load(), store, new MiniRemoteInventory(host, () => cache), new ServerHubClient(host), () => S);
            K = new KitRunner(Ctx);
        }

        private static void Carry(List<uint> ids, uint id, KitPart part, int count)
        {
            if (count <= 0) return;
            FakeHost.Names[id] = part.Name;
            FakeHost.Wcids[id] = part.Wcid;
            FakeHost.Ints[(id, StackKey)] = count;
            ids.Add(id);
        }

        public int Count(KitPart part) => KitPlanner.Count(part, K.Pack());

        /// <summary>One pump tick, then the fake server answers what it was sent.</summary>
        public void Tick(long advanceMs = 200)
        {
            Now += advanceMs;
            K.Tick(Now);
            var sent = _pending.ToList();
            _pending.Clear();
            foreach (var (src, tgt) in sent) Serve(src, tgt);
        }

        public void Run(int ticks) { for (int i = 0; i < ticks && (K.Running || i == 0); i++) Tick(); }

        /// <summary>ACE: destroy one target, one source, create the product on a success, then the line.</summary>
        private void Serve(uint src, uint tgt)
        {
            var step = KitPlanner.Retail[0].Steps.FirstOrDefault(s =>
                FakeHost.Wcids.GetValueOrDefault(src) == s.Source.Wcid && FakeHost.Wcids.GetValueOrDefault(tgt) == s.Target.Wcid);
            if (step == null) { K.OnChat("The item cannot be used on the other item."); return; }
            if (DialogInstead) { DialogSeq++; return; }   // AC shows its dialog; nothing happens until a click
            _served++;
            bool ok = FailNext-- <= 0 && !FailAt.Contains(_served);
            Take(tgt);
            Take(src);
            if (ok) Give(step.Product);
            if (SendLines) K.OnChat(ok ? step.SuccessText : step.FailText);
        }

        private void Take(uint id)
        {
            int n = FakeHost.Ints.GetValueOrDefault((id, StackKey), 1) - 1;
            if (n > 0) { FakeHost.Ints[(id, StackKey)] = n; return; }
            FakeHost.Ints.Remove((id, StackKey));
            Cache.OnDeleteObject(id);
        }

        private void Give(KitPart part)
        {
            // Stackable parts join the stack carried; kits are one object each.
            uint stack = FakeHost.Wcids.Where(kv => kv.Value == part.Wcid && Cache[unchecked((int)kv.Key)] != null
                                                   && Cache.GetInventory().Any(w => w.Id == unchecked((int)kv.Key)))
                                     .Select(kv => kv.Key).FirstOrDefault();
            if (stack != 0 && part.Wcid != KitPlanner.PlentifulHealingKit.Wcid)
            {
                FakeHost.Ints[(stack, StackKey)] = FakeHost.Ints.GetValueOrDefault((stack, StackKey), 1) + 1;
                return;
            }
            uint id = NextId++;
            FakeHost.Names[id] = part.Name;
            FakeHost.Wcids[id] = part.Wcid;
            FakeHost.Ints[(id, StackKey)] = 1;
            _ = Cache[unchecked((int)id)];
        }

        public List<(uint Src, uint Tgt)> Uses => FakeHost.UsesOn.ToList();
        public List<string> Summaries => FakeHost.Chat.Where(c => c.Contains("[RynthAi] Make kits:") || c.Contains("[RynthAi] Make kits when low:")).ToList();

        public void Dispose() => ActionPacer.Enabled = _pacer;
    }

    private static void PackAndPlan()
    {
        using var rig = new Rig(bandages: 3, hyssop: 5, mandrake: 2, healing: 300);
        var pack = rig.K.Pack();
        Check.Eq(pack.Count, 3, "three stacks read");
        Check.Eq(rig.Count(KitPlanner.TreatedHyssop), 5, "the hyssop stack's size");
        var rows = rig.K.RefreshPlan();
        Check.Eq(rows.Length, 1, "one kit row");
        Check.Eq(rows[0].CanMake, 2, "mandrake 2 limits it to 2");
        Check.Eq(KitPlanner.ChancesText(rows[0]), "Combine the herbs 100%, Pack the kit 95%", "Healing 300 read from the engine");
        Check.Eq(rows[0].Blocked, "", "can start");
        Check.Eq(rig.K.Floor, 90, "the floor starts at 90%");
    }

    private static void MakesTwo()
    {
        using var rig = new Rig(bandages: 2, hyssop: 2, mandrake: 2);
        string why = rig.K.Start(KitPlanner.Retail[0], 0);
        Check.Eq(why, "", "it starts");
        rig.Run(200);
        Check.False(rig.K.Running, "it ends by itself");
        var uses = rig.Uses;
        Check.Eq(uses.Count, 4, "four combines, one at a time");
        Check.True(uses.Count == 4 && uses[0] == (Mandrake, Hyssop) && uses[2] == (Mandrake, Hyssop), "herbs: mandrake on hyssop");
        Check.True(uses.Count == 4 && FakeHost.Wcids[uses[1].Src] == KitPlanner.CombinedHerbs.Wcid && uses[1].Tgt == Bandages, "then the combined herbs on the bandages");
        Check.Eq(rig.Count(KitPlanner.PlentifulHealingKit), 2, "two kits in the pack");
        Check.Eq(rig.K.MadeCount, 2, "two made");
        Check.Eq(rig.Summaries.Count, 1, "one summary line");
        Check.True(rig.Summaries.FirstOrDefault()?.Contains("made 2 Plentiful Healing Kits, no combine failed. Stopped: out of") == true,
            "the summary: " + string.Join(" | ", rig.Summaries));
    }

    private static void FailureCounted()
    {
        using var rig = new Rig(bandages: 2, hyssop: 3, mandrake: 3);
        rig.FailNext = 1;   // the first herb combine fails
        rig.K.Start(KitPlanner.Retail[0], 0);
        rig.Run(300);
        Check.Eq(rig.K.FailedCount, 1, "one failure");
        Check.Eq(rig.K.MadeCount, 2, "two kits anyway (a third herb pair was carried)");
        Check.Eq(rig.Uses.Count, 5, "1 failed + 2 herb + 2 kit combines");
        string line = rig.Summaries.FirstOrDefault() ?? "";
        Check.True(line.Contains("1 combine failed (lost 1 Treated Mandrake, 1 Treated Hyssop)"), "what was lost: " + line);
    }

    private static void StopsAtCount()
    {
        using var rig = new Rig(bandages: 5, hyssop: 5, mandrake: 5);
        rig.K.Start(KitPlanner.Retail[0], 1);
        rig.Run(200);
        Check.Eq(rig.K.MadeCount, 1, "one kit");
        Check.Eq(rig.Uses.Count, 2, "two combines");
        Check.True(rig.Summaries.FirstOrDefault()?.Contains("Stopped: made the 1 asked for.") == true, "says why it stopped");
    }

    private static void Gates()
    {
        using (var rig = new Rig(bandages: 2, hyssop: 2, mandrake: 2))
        {
            rig.S.IsMacroRunning = true;
            string why = rig.K.Start(KitPlanner.Retail[0], 0);
            Check.True(why.Contains("turn the macro off first"), "macro on: refused - " + why);
            Check.False(rig.K.Running, "not running");
            rig.S.IsMacroRunning = false;
            Check.Eq(rig.K.Start(KitPlanner.Retail[0], 0), "", "macro off: starts");
            rig.Tick();
            rig.S.IsMacroRunning = true;
            rig.Tick();
            Check.False(rig.K.Running, "the macro turned on: stopped");
            Check.True(rig.Summaries.LastOrDefault()?.EndsWith("Stopped: the macro turned on.") == true, "and says so");
        }
        using (var rig = new Rig(bandages: 3, hyssop: 3, mandrake: 3))
        {
            rig.K.Start(KitPlanner.Retail[0], 0);
            rig.Tick();
            FakeHost.PlayerPose = (Lb | 0x1C, 99f, 96f, 0f);
            rig.Tick();
            Check.False(rig.K.Running, "moved 3 m: stopped");
            Check.True(rig.Summaries.LastOrDefault()?.EndsWith("Stopped: you moved.") == true, "says you moved");
        }
        using (var rig = new Rig(bandages: 3, hyssop: 3, mandrake: 3))
        {
            rig.K.Start(KitPlanner.Retail[0], 0);
            rig.Tick();
            FakeHost.Vitals = (150, 200, 200, 200, 200, 200);
            rig.Tick();
            Check.False(rig.K.Running, "health dropped: stopped");
            Check.True(rig.Summaries.LastOrDefault()?.EndsWith("Stopped: you took damage.") == true, "says you took damage");
        }
        using (var rig = new Rig(bandages: 3, hyssop: 3, mandrake: 3))
        {
            FakeHost.CombatModeValue = 2;   // melee
            Check.True(rig.K.Start(KitPlanner.Retail[0], 0).Contains("peace mode"), "not in peace mode: refused");
            FakeHost.CombatModeValue = 1;
            rig.K.Start(KitPlanner.Retail[0], 0);
            rig.Tick();
            FakeHost.CombatModeValue = 2;
            rig.Tick();
            Check.False(rig.K.Running, "combat mode: stopped");
            Check.True(rig.Summaries.LastOrDefault()?.EndsWith("Stopped: you left peace mode.") == true, "says so");
        }
        using (var rig = new Rig(bandages: 3, hyssop: 3, mandrake: 3, healing: 200))
        {
            string why = rig.K.Start(KitPlanner.Retail[0], 0);
            Check.True(why.Contains("under your 90% floor"), "Healing 200 (kit 50%): refused at the 90% floor - " + why);
            Check.Eq(rig.Uses.Count, 0, "nothing combined");
        }
    }

    private static void LateLine()
    {
        // The counts finish each step; its line comes late, after the next combine went out. That
        // next combine (the first kit) fails: the late success line must not be taken as its result.
        using var rig = new Rig(bandages: 3, hyssop: 3, mandrake: 3);
        rig.SendLines = false;
        rig.FailAt.Add(2);
        rig.K.Start(KitPlanner.Retail[0], 0);
        int guard = 0, seenUses = 0;
        while (rig.K.Running && guard++ < 400)
        {
            rig.Tick();
            if (rig.Uses.Count > seenUses)
            {
                if (seenUses > 0) rig.K.OnChat(KitPlanner.Retail[0].Final.SuccessText);   // the previous step's line, late
                seenUses = rig.Uses.Count;
            }
        }
        Check.Eq(rig.K.FailedCount, 1, "the failed kit combine is counted as failed (before the fix: a success)");
        Check.Eq(rig.K.MadeCount, rig.Count(KitPlanner.PlentifulHealingKit), "kits counted = kits in the pack");
        Check.Eq(rig.K.MadeCount, 2, "two kits from three sets, one lost");
    }
    private static void StateDefaults()
    {
        var s = new CraftingTabState();
        Check.Eq(s.KitMinChancePercent, 90, "floor 90%");
        Check.Eq(s.KitMakeCount, 0, "count 0 = as many as the pack makes");
        string dir = Path.Combine(Program.TempRoot, "kits-state");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var store = new MiniRemoteStore(dir);
        var st = store.Load();
        st.Crafting.KitMinChancePercent = 75;
        st.Crafting.KitMakeCount = 4;
        store.SaveIfDirty(st);
        var back = new MiniRemoteStore(dir).Load();
        Check.Eq(back.Crafting.KitMinChancePercent, 75, "the floor is saved");
        Check.Eq(back.Crafting.KitMakeCount, 4, "the count is saved");
    }

    // ── Make kits when low (StartAuto) ──────────────────────────────────────

    /// <summary>The fake engine: never armed (as without the chance-dialog answer), AC's dialog after each use.</summary>
    private static int DialogSeq;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FakeArm(uint type, int minPercent, int timeoutMs) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FakeLast(uint* type, uint* contextId, int* percent, int* outcome, int* ageMs)
    {
        if (type != null) *type = RynthCoreHost.ConfirmationKind.CraftInteraction;
        if (contextId != null) *contextId = 0x1234;
        if (percent != null) *percent = 95;
        if (outcome != null) *outcome = DialogSeq > 0 ? RynthCoreHost.ConfirmationOutcome.Shown : RynthCoreHost.ConfirmationOutcome.None;
        if (ageMs != null) *ageMs = 0;
        return DialogSeq;
    }

    /// <summary>What the "when low" end callback heard, and the tail it adds to the summary.</summary>
    private sealed class Ends
    {
        public readonly List<(KitStopKind Kind, int Made)> Heard = new();
        public string Tail = " Next try: later.";
        public string Hear(KitStopKind kind, int made) { Heard.Add((kind, made)); return Tail; }
    }

    private static void AutoRuns()
    {
        using var rig = new Rig(bandages: 2, hyssop: 2, mandrake: 2);
        var ends = new Ends();
        rig.S.IsMacroRunning = false;
        string why = rig.K.StartAuto(KitPlanner.Retail[0], 5, ends.Hear);
        Check.Eq(why, "the macro is off", "the macro off: a when-low run is refused");
        Check.False(rig.K.Running, "not running");
        rig.S.IsMacroRunning = true;
        Check.Eq(rig.K.StartAuto(KitPlanner.Retail[0], 5, ends.Hear), "", "the macro on: it starts");
        Check.True(rig.K.Running && rig.K.Auto, "running, as a when-low run");
        Check.Eq(rig.K.Limit, 5, "up to 5");
        Check.True(rig.K.Status.Contains("Make kits when low"), "the status says so: " + rig.K.Status);
        rig.Run(300);
        Check.False(rig.K.Running, "it ends by itself");
        Check.False(rig.K.Auto, "and is no longer a when-low run");
        Check.Eq(rig.K.MadeCount, 2, "two kits (two sets carried)");
        Check.Eq(rig.Count(KitPlanner.PlentifulHealingKit), 2, "two kits in the pack");
        Check.Eq(ends.Heard.Count, 1, "the end is heard once");
        Check.True(ends.Heard.Count == 1 && ends.Heard[0] == (KitStopKind.Finished, 2), "finished, 2 made");
        Check.Eq(rig.K.LastStopKind, KitStopKind.Finished, "LastStopKind");
        var lines = rig.Summaries;
        Check.Eq(lines.Count, 1, "one summary line: " + string.Join(" | ", lines));
        string line = lines.FirstOrDefault() ?? "";
        Check.True(line.StartsWith("[RynthAi] Make kits when low: made 2 Plentiful Healing Kits"), "the when-low summary: " + line);
        Check.True(line.EndsWith(" Next try: later."), "with what the waits add: " + line);
        Check.Eq(rig.Uses.Count, 4, "four combines");
    }

    private static void AutoStopKinds()
    {
        void Case(string name, Action<Rig> act, KitStopKind want, string text)
        {
            using var rig = new Rig(bandages: 3, hyssop: 3, mandrake: 3);
            var ends = new Ends();
            rig.S.IsMacroRunning = true;
            Check.Eq(rig.K.StartAuto(KitPlanner.Retail[0], 5, ends.Hear), "", name + ": starts");
            rig.Tick();
            act(rig);
            rig.Tick();
            Check.False(rig.K.Running, name + ": stopped");
            Check.True(ends.Heard.Count == 1 && ends.Heard[0].Kind == want, $"{name}: heard {want} ({string.Join(",", ends.Heard)})");
            Check.True(rig.Summaries.LastOrDefault()?.Contains("Stopped: " + text) == true, $"{name}: says why - {rig.Summaries.LastOrDefault()}");
        }
        Case("the macro turned off", r => r.S.IsMacroRunning = false, KitStopKind.MacroOff, "the macro turned off.");
        Case("moved", _ => FakeHost.PlayerPose = (Lb | 0x1C, 99f, 96f, 0f), KitStopKind.Danger, "you moved.");
        Case("damage", _ => FakeHost.Vitals = (150, 200, 200, 200, 200, 200), KitStopKind.Danger, "you took damage.");
        Case("combat mode", _ => FakeHost.CombatModeValue = 2, KitStopKind.Danger, "you left peace mode.");
        Case("the player's Stop", r => r.K.Stop("stopped by you"), KitStopKind.User, "stopped by you.");
        Case("a monster (the plugin's stop)", r => r.K.Stop("a monster came within 20 yd", KitStopKind.Danger), KitStopKind.Danger, "a monster came within 20 yd.");

        // The macro staying on doesn't stop a when-low run (it does a run by hand).
        using var keep = new Rig(bandages: 2, hyssop: 2, mandrake: 2);
        keep.S.IsMacroRunning = true;
        keep.K.StartAuto(KitPlanner.Retail[0], 0, null);
        keep.Run(300);
        Check.Eq(keep.K.MadeCount, 2, "macro on all along: both kits made");
        Check.False(keep.Summaries.Any(l => l.Contains("macro turned")), "no macro stop");
    }

    private static void AutoDialog()
    {
        using (var rig = new Rig(bandages: 2, hyssop: 2, mandrake: 2, dialogEngine: true))
        {
            rig.DialogInstead = true;
            var ends = new Ends();
            rig.S.IsMacroRunning = true;
            Check.Eq(rig.K.StartAuto(KitPlanner.Retail[0], 5, ends.Hear), "", "starts");
            for (int i = 0; i < 10 && rig.K.Running; i++) rig.Tick();
            Check.False(rig.K.Running, "AC's dialog came up: stopped");
            Check.True(ends.Heard.Count == 1 && ends.Heard[0] == (KitStopKind.Refused, 0), "heard Refused, 0 made: " + string.Join(",", ends.Heard));
            Check.True(rig.Summaries.LastOrDefault()?.Contains("AC's craft dialog came up") == true, "says so: " + rig.Summaries.LastOrDefault());
            Check.Eq(rig.Uses.Count, 1, "nothing more sent");
        }
        using (var rig = new Rig(bandages: 2, hyssop: 2, mandrake: 2, dialogEngine: true))
        {
            rig.DialogInstead = true;
            Check.Eq(rig.K.Start(KitPlanner.Retail[0], 0), "", "a run by hand starts");
            for (int i = 0; i < 10; i++) rig.Tick();
            Check.True(rig.K.Running, "a run by hand waits for the click");
            Check.True(rig.K.Status.Contains("click Yes"), "and asks for it: " + rig.K.Status);
            rig.K.Stop("stopped by you");
        }
    }

    private static void ManualUnchanged()
    {
        using var rig = new Rig(bandages: 2, hyssop: 2, mandrake: 2);
        rig.S.IsMacroRunning = true;
        Check.True(rig.K.Start(KitPlanner.Retail[0], 0).Contains("turn the macro off first"), "by hand with the macro on: refused, as before");
        rig.S.IsMacroRunning = false;
        Check.Eq(rig.K.Start(KitPlanner.Retail[0], 0), "", "macro off: starts");
        Check.False(rig.K.Auto, "not a when-low run");
        Check.True(rig.K.StartAuto(KitPlanner.Retail[0], 5, null).Contains("already going"), "a when-low start waits for it");
        rig.Run(300);
        string line = rig.Summaries.LastOrDefault() ?? "";
        Check.True(line.StartsWith("[RynthAi] Make kits: made 2 Plentiful Healing Kits, no combine failed. Stopped: out of"), "its own summary: " + line);
        Check.False(line.Contains("Next try"), "nothing added");
        Check.Eq(rig.K.LastStopKind, KitStopKind.Finished, "finished");
    }
}
