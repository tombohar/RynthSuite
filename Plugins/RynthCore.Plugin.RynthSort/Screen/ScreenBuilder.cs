using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Views;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort.Screen;

/// <summary>What the builder needs besides the controller (the plugin fills it; the tests make one).</summary>
internal sealed class ScreenInputs
{
    public string Character = string.Empty;
    public string Server = string.Empty;
    /// <summary>The plugin's own warnings (the RynthSort Lua script set to start too).</summary>
    public IReadOnlyList<string> Warnings = Array.Empty<string>();
    public bool CanAddSelected;
    public bool CanSelect;
    /// <summary>The Layouts popup is open: fill <see cref="ScreenModel.Layouts"/>.</summary>
    public bool WantLayouts;
    public ScreenSession? Session;
}

/// <summary>
/// Builds the window's <see cref="ScreenModel"/> from the controller: the packs as last read, the
/// rules, what a sort would move now (<see cref="SortPreview"/>, as Sort now would go), what is
/// left alone, what settles, the feed. Pure (no host calls): the plugin calls it on its tick while
/// the window shows and publishes the result; the tests call it on hand-made inventories.
/// </summary>
internal static class ScreenBuilder
{
    /// <summary>The most Next moves and Left alone rows listed (the rest are counted).</summary>
    public const int MaxRows = 30;
    private static int _version;

    public static ScreenModel Build(SortController c, ScreenInputs input)
    {
        if (input.Character.Length == 0) return ScreenModel.Empty;
        SortInventory? inv = c.LastInventory;
        string who = input.Server.Length > 0 ? input.Character + " · " + input.Server : input.Character;
        if (inv == null)
            return new ScreenModel { Who = who, NotReady = "Reading your packs...", Version = ++_version, Toast = input.Session?.Toast };

        List<SortRule> rules = c.Data.Rules;
        SortSettings s = c.Data.Settings;
        bool clean = c.Job == SortScope.Clean;
        SortPreview p = SortPreview.Build(inv, rules, c.CurrentOptions, c.IsHeld, clean);
        SortView view = p.View;           // the packs as numbered now (the preview planned on this read)
        SortView real = c.Planner.View;    // the real planner's: what settles, what waits
        bool busy = c.Job != null || c.HasMoveOut || c.Status == "sorting" || c.Send != null;
        JobPlan? jobs = c.Jobs;

        var m = new ScreenModel
        {
            Ready = true,
            Who = who,
            Auto = s.Auto,
            Busy = busy,
            PutBack = c.PutBackCount,
            NoRules = rules.Count == 0,
            CanAddSelected = input.CanAddSelected,
            CanSelect = input.CanSelect,
            Toast = input.Session?.Toast,
            Version = ++_version,
        };
        m.PutBackTip = $"Undo the last sort: the {SortPanel.Count(m.PutBack, "item")} it moved go back where they were. "
                       + "Turns auto-sort off first, so nothing moves again. Also: /rsort back";

        var outgoing = new HashSet<uint>();
        foreach (SortMove mv in p.Moves) outgoing.Add(mv.Item.Id);
        uint flying = c.InFlightItem;

        TileModel Tile(SortItem it, int where)
        {
            var t = new TileModel
            {
                Id = it.Id,
                Name = it.Name.Length > 0 ? it.Name : "(not known yet)",
                Stack = Math.Max(1, it.Stack),
                Qty = Kinds.Qty(it.Stack),
                Cls = it.ObjectClass,
                KindColor = Kinds.Color(it.ObjectClass),
                KindLabel = Kinds.Label(it.ObjectClass),
                Initials = Kinds.Initials(it.Name),
                Where = where,
                Outgoing = outgoing.Contains(it.Id),
                Settling = real.Settling.Contains(it.Id),
            };
            t.Moving = it.Id == flying || (busy && t.Outgoing);
            SortRule? r = SortRules.RuleFor(rules, it);
            if (r != null)
            {
                int to = SortPanel.ShownPack(view, r);
                t.Rule = to > 0 ? TileRule.Pack : TileRule.Missing;
                t.RulePack = to;
                t.ByName = SortRules.MatchesName(r, it);
                t.RuleText = t.ByName ? Kinds.NameChip(MatchedName(r, it)) : Kinds.Label(it.ObjectClass);
            }
            if (jobs != null && !t.ByName)
            {
                // A pack whose job picks it comes before a kind rule (a name rule beats both).
                foreach (int jp in jobs.PacksFor(it))
                {
                    t.Rule = TileRule.Job;
                    t.RulePack = jp;
                    t.RuleText = PackJobs.Label(jobs.Packs[jp]);
                    break;
                }
            }
            if (c.Touch.TryGetHeld(it.Id, c.Clock, c.HoldMs, out long at, out TouchKind kind))
            {
                t.Held = true;
                t.HeldWhy = Capital(TouchWatch.Words(kind)) + " " + SortPanel.Ago(c.Clock - at);
            }
            m.TilesById[it.Id] = t;
            return t;
        }

        // Main pack.
        int mainCap = SortPlanner.CapacityOf(inv.Main, SortPlanner.MainCapacityFallback);
        var mainTiles = new TileModel[inv.Main.Items.Count];
        for (int i = 0; i < mainTiles.Length; i++) mainTiles[i] = Tile(inv.Main.Items[i], 0);
        m.Main = new MainModel
        {
            Tiles = mainTiles,
            Used = mainTiles.Length,
            Capacity = mainCap,
            Count = $"{mainTiles.Length} / {mainCap}",
            Fill = mainCap > 0 ? Math.Clamp(mainTiles.Length / (float)mainCap, 0f, 1f) : 0f,
            NearlyFull = mainCap > 0 && mainTiles.Length > mainCap * 0.88f,
            Full = mainCap > 0 && mainTiles.Length >= mainCap,
            Wells = MainWells(mainTiles.Length, mainCap),
            Handy = s.HandyFront && c.Abilities.ReadOrder && c.Abilities.PlaceMoves
                ? "Handy at the front: " + HandyItems.Describe(s.HandyClasses, s.HandyNames) + "."
                : string.Empty,
            Collapsed = s.MainCollapsed,
        };

        // Side packs.
        int packs = inv.Packs.Count;
        m.Packs = new PackModel[packs];
        m.PacksNote = packs == 0 ? "none carried" : packs + " carried";
        var chips = ChipsByPack(rules, view, packs, out List<ChipModel> orphans);
        m.Orphans = orphans.ToArray();
        for (int i = 0; i < packs; i++)
        {
            SortPack sp = inv.Packs[i];
            int n = i + 1;
            int cap = SortPlanner.CapacityOf(sp, SortPlanner.SideCapacityFallback);
            var tiles = new TileModel[sp.Items.Count];
            for (int k = 0; k < tiles.Length; k++) tiles[k] = Tile(sp.Items[k], n);
            var ghosts = new List<GhostModel>();
            foreach (SortMove mv in p.Moves)
                if (mv.DestIndex == n) ghosts.Add(new GhostModel { ItemId = mv.Item.Id, Tip = $"{ItemName(mv.Item)} is coming here" });
            var warnings = new List<string>();
            int waiting = 0;
            foreach (NoFitLine l in view.NoFit) if (l.Pack == n) waiting++;
            if (waiting > 0) warnings.Add($"Full: {SortPanel.Count(waiting, "item")} for it {(waiting == 1 ? "waits" : "wait")} where {(waiting == 1 ? "it is" : "they are")}.");
            else if (view.Full.Contains(n) || real.Full.Contains(n)) warnings.Add("Full: the next pack with the same rule takes the rest.");
            warnings.AddRange(SortPanel.FallbackWarnings(view, n));
            float fill = cap > 0 ? Math.Clamp(tiles.Length / (float)cap, 0f, 1f) : 0f;
            m.Packs[i] = new PackModel
            {
                Number = n,
                Id = sp.Id,
                Name = sp.Name.Length > 0 ? sp.Name : "Pack " + n,
                Label = PackNames.Label(inv, n),
                Badge = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Where = WhereWords(view, n, packs),
                Used = tiles.Length,
                Capacity = cap,
                Count = $"{tiles.Length}/{cap}",
                Fill = fill,
                NearlyFull = fill > 0.85f,
                Full = cap > 0 && tiles.Length >= cap,
                Incoming = ghosts.Count > 0 ? $"+{ghosts.Count} coming" : string.Empty,
                Tiles = tiles,
                Ghosts = ghosts.ToArray(),
                Chips = chips[n].ToArray(),
                Warnings = warnings.ToArray(),
                Tip = SortPanel.PackHeaderTooltip(view, n),
                Collapsed = s.CollapsedPacks.Contains(sp.Id),
            };
            JobOnCard(m.Packs[i], c, inv, n);
        }

        // The plan column.
        m.MovesCount = p.Moves.Count;
        var moves = new List<RowModel>(Math.Min(p.Moves.Count, MaxRows));
        for (int i = 0; i < p.Moves.Count && i < MaxRows; i++)
        {
            SortMove mv = p.Moves[i];
            SortRule? r = SortRules.RuleFor(rules, mv.Item);
            int jobAt = JobPackFor(jobs, mv.Item, r);
            string ruleText = jobAt > 0 ? "Job: " + PackJobs.Label(jobs!.Packs[jobAt])
                : r == null ? "No rule: back to the main pack"
                : "Rule: " + (SortRules.MatchesName(r, mv.Item) ? Kinds.NameChip(MatchedName(r, mv.Item)) : Kinds.Label(mv.Item.ObjectClass));
            string to = mv.DestIndex == 0 ? "main pack" : m.PackName(mv.DestIndex);
            moves.Add(new RowModel
            {
                ItemId = mv.Item.Id,
                KindColor = Kinds.Color(mv.Item.ObjectClass),
                Name = ItemName(mv.Item),
                Sub = ruleText,
                Right = to,
                Tip = $"{ItemName(mv.Item)} goes from the {m.PackName(Math.Max(mv.FromIndex, 0))} to the {to}.",
            });
        }
        m.Moves = moves.ToArray();
        if (p.Moves.Count > 0 && !s.Auto && c.Job == null)
            m.MovesHint = "Nothing moves until you press Sort now or turn on Auto-sort.";
        if (p.ToOrder > 0 && !view.Ordering && view.Handy)
            m.OrderHint = p.Moves.Count > 0
                ? "Then handy items go to the front of the main pack."
                : $"{SortPanel.Count(p.ToOrder, "handy item")} still to go to the front of the main pack.";
        else if (p.ToOrder > 0)
            m.OrderHint = p.Moves.Count > 0
                ? "Then stacks are joined and items put in order inside each pack."
                : $"{SortPanel.Count(p.ToOrder, "item")} still to put in order inside packs.";

        var held = new List<RowModel>();
        for (int i = 0; i < view.Held.Count && i < MaxRows; i++)
        {
            HeldLine h = view.Held[i];
            m.TilesById.TryGetValue(h.Item.Id, out TileModel? t);
            string why = t != null && t.HeldWhy.Length > 0 ? t.HeldWhy : "You used it";
            string to = h.To != h.From ? " · would go to " + (h.To == 0 ? "the main pack" : m.PackName(h.To)) : string.Empty;
            held.Add(new RowModel
            {
                ItemId = h.Item.Id,
                KindColor = Kinds.Color(h.Item.ObjectClass),
                Name = ItemName(h.Item),
                Sub = why + to,
                Tip = "Left alone: a sort doesn't move it until the time is up. Sort it lets RynthSort move it now.",
            });
        }
        m.Held = held.ToArray();
        m.HoldNote = s.HoldMin > 0 ? s.HoldMin + " min" : "off";

        var feed = new RowModel[c.Feed.Count];
        for (int i = 0; i < feed.Length; i++)
        {
            FeedLine f = c.Feed[i];
            m.TilesById.TryGetValue(f.ItemId, out TileModel? t);
            feed[i] = new RowModel { ItemId = f.ItemId, KindColor = t?.KindColor ?? 0, Name = f.Text, Right = f.Time };
        }
        m.Feed = feed;

        m.KindChoices = KindChoices(rules, view);
        m.Status = Status(c, real, p, rules.Count == 0, view.Held.Count, busy);
        m.Warnings = Warnings(c, real, view, input.Warnings);
        m.NoFit = NoFitLines(view, inv);
        m.Settings = new SettingsModel
        {
            SettleSec = s.SettleSec,
            HoldMin = s.HoldMin,
            GapMs = s.GapMs,
            SortInCombat = s.SortInCombat,
            OrderInPacks = s.OrderInPacks,
            KindFirst = s.OrderKindFirst,
            CanOrder = c.Abilities.ReadOrder && c.Abilities.PlaceMoves,
            HandyOn = s.HandyFront,
            HandyChips = HandyChips(s),
            HandyChoices = HandyChoices(s),
        };
        if (input.WantLayouts) m.Layouts = Layouts(c, inv, rules);
        return m;
    }

    /// <summary>The empty wells after <paramref name="used"/> main-pack tiles: at least three rows of six, and a row past the last item, never past the capacity.</summary>
    internal static int MainWells(int used, int capacity)
    {
        int shown = Math.Max(18, (used + 1 + 5) / 6 * 6);
        if (capacity > 0) shown = Math.Min(shown, Math.Max(capacity, used));
        return Math.Max(0, shown - used);
    }

    /// <summary>"Top of your pack column", "Pack 2 from the top", "Bottom of your pack column".</summary>
    internal static string WhereWords(SortView view, int number, int packs)
    {
        if (!view.PackOrderKnown) return "Place in the pack column not known";
        if (packs == 1) return "Your only side pack";
        if (number == 1) return "Top of your pack column";
        if (number == packs) return "Bottom of your pack column";
        return $"Pack {number} from the top";
    }

    /// <summary>The name part of <paramref name="r"/> that <paramref name="it"/>'s name contains (the first), or "".</summary>
    internal static string MatchedName(SortRule r, SortItem it)
    {
        foreach (string n in r.Names)
            if (n.Length > 0 && it.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) return n;
        return string.Empty;
    }

    /// <summary>Each side pack's chips ([1..packs]): its rules' kinds (A-Z) then names (A-Z), rule by rule; rules for packs not carried in <paramref name="orphans"/>.</summary>
    internal static List<ChipModel>[] ChipsByPack(List<SortRule> rules, SortView view, int packs, out List<ChipModel> orphans)
    {
        var byPack = new List<ChipModel>[packs + 1];
        for (int i = 0; i <= packs; i++) byPack[i] = new List<ChipModel>();
        var ordered = new List<SortRule>(rules);
        ordered.Sort(SortRules.CompareForList);
        // The rule (that is on) each kind follows first; another rule with the same kind is an overflow pack's.
        var firstFor = new Dictionary<int, SortRule>();
        foreach (SortRule r in rules)
            if (r.On)
                foreach (int cls in r.Classes) firstFor.TryAdd(cls, r);
        foreach (SortRule r in ordered)
        {
            int n = SortPanel.ShownPack(view, r);
            if (n > packs) n = 0;
            List<ChipModel> list = byPack[n];
            foreach (int cls in SortRules.SortedClasses(r))
            {
                SortRule? first = firstFor.GetValueOrDefault(cls);
                int firstPack = first != null ? SortPanel.ShownPack(view, first) : 0;
                bool overflow = r.On && n > 0 && first != null && first != r && firstPack > 0 && firstPack != n;
                list.Add(new ChipModel
                {
                    Pack = n, RuleUid = r.Uid, Cls = cls, Dot = Kinds.Color(cls), Off = !r.On, Overflow = overflow,
                    Label = overflow ? Kinds.Label(cls) + " (overflow)" : Kinds.Label(cls),
                    Tip = !r.On ? "This rule is turned off: click to turn it on. The x takes it out."
                        : overflow ? $"{Kinds.Label(cls)} come here when {RuleEdits.PackName(view, firstPack)} is full. The x takes it out (Undo brings it back)."
                        : n > 0 ? $"Every {Kinds.Label(cls).ToLowerInvariant()} item goes here. The x takes it out (Undo brings it back)."
                        : $"{Kinds.Label(cls)}: its pack ({(r.PackNote.Length > 0 ? r.PackNote : "pack " + r.Pack)}) isn't carried, so these stay where they are.",
                });
            }
            foreach (string name in SortRules.SortedNames(r))
                list.Add(new ChipModel
                {
                    Pack = n, RuleUid = r.Uid, IsName = true, Name = name, Label = Kinds.NameChip(name), Off = !r.On,
                    Tip = !r.On ? "This rule is turned off: click to turn it on. The x takes it out."
                        : n > 0 ? $"Items with \"{name}\" in their name go here, whatever their kind (a name beats a kind)."
                        : $"\"{name}\": its pack ({(r.PackNote.Length > 0 ? r.PackNote : "pack " + r.Pack)}) isn't carried, so these stay where they are.",
                });
        }
        orphans = byPack[0];
        return byPack;
    }

    /// <summary>Every kind the picker offers, and which pack has it now.</summary>
    internal static KindChoice[] KindChoices(List<SortRule> rules, SortView view)
    {
        var a = new KindChoice[ObjectClasses.Choices.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int cls = ObjectClasses.Choices[i];
            int at = 0;
            foreach (SortRule r in rules)
            {
                if (!r.On || !r.HasClass(cls)) continue;
                int n = SortPanel.ShownPack(view, r);
                at = n > 0 ? n : -1;
                break;
            }
            a[i] = new KindChoice { Cls = cls, Label = Kinds.Label(cls), Dot = Kinds.Color(cls), InPack = at };
        }
        return a;
    }

    /// <summary>The job pack an item goes to (a name rule beats a job), or 0.</summary>
    internal static int JobPackFor(JobPlan? jobs, SortItem it, SortRule? r)
    {
        if (jobs == null || (r != null && SortRules.MatchesName(r, it))) return 0;
        foreach (int n in jobs.PacksFor(it)) return n;
        return 0;
    }

    /// <summary>A side pack's job as its card says it: the pill, the line under the bar, and whether it works now.</summary>
    internal static void JobOnCard(PackModel pk, SortController c, SortInventory inv, int number)
    {
        PackJob? job = PackJobs.On(c.Data.Jobs, inv, number);
        if (job == null) return;
        pk.Job = job.Kind;
        pk.JobMule = job.Mule;
        pk.JobLabel = job.Kind == JobKind.Mule && job.Mule.Length > 0 ? "Mule: " + job.Mule : PackJobs.Label(job.Kind);
        JobAnswer? a = c.Jobs?.Answers.Of(job.Kind);
        if (a == null)
        {
            pk.JobLine = "Asking RynthAi which items belong here...";
            return;
        }
        if (!a.Known)
        {
            pk.JobLine = $"{PackJobs.Label(job.Kind)} can't work now: {a.Text}. What is in this pack stays put until it can.";
            pk.JobProblem = true;
            return;
        }
        string profile = a.Text.Length > 0 ? " (" + a.Text + ")" : string.Empty;
        pk.JobLine = job.Kind switch
        {
            JobKind.Salvage => $"Takes what RynthAi's loot profile{profile} marks Salvage.",
            JobKind.Sell => $"Takes what RynthAi's AutoVendor would sell{profile}.",
            _ => $"Takes what RynthAi's AutoTrade would give {(job.Mule.Length > 0 ? job.Mule : "a mule")}{profile}.",
        };
    }

    /// <summary>The handy list as chips: its kinds (in the list's order), then its names.</summary>
    internal static ChipModel[] HandyChips(SortSettings s)
    {
        var list = new List<ChipModel>(s.HandyClasses.Count + s.HandyNames.Count);
        foreach (int cls in s.HandyClasses)
            list.Add(new ChipModel { Cls = cls, Label = Kinds.Label(cls), Dot = Kinds.Color(cls), Tip = $"{Kinds.Label(cls)} sit at the front of the main pack. The x takes the kind off the list." });
        foreach (string n in s.HandyNames)
            list.Add(new ChipModel { IsName = true, Name = n, Label = Kinds.NameChip(n), Tip = $"Items with \"{n}\" in their name sit at the front of the main pack, whatever their kind. The x takes it off the list." });
        return list.ToArray();
    }

    /// <summary>The kinds the handy list's picker offers: every kind the rule picker offers that isn't on the list.</summary>
    internal static KindChoice[] HandyChoices(SortSettings s)
    {
        var list = new List<KindChoice>();
        foreach (int cls in ObjectClasses.Choices)
            if (!s.HandyClasses.Contains(cls))
                list.Add(new KindChoice { Cls = cls, Label = Kinds.Label(cls), Dot = Kinds.Color(cls) });
        return list.ToArray();
    }

    /// <summary>The status line and its pill (the design's Off / Moving / Waiting / All tidy).</summary>
    internal static StatusModel Status(SortController c, SortView v, SortPreview? p, bool noRules, int held, bool busy)
    {
        int moves = p?.Moves.Count ?? 0;
        string line;
        string pill;
        PillTone tone;
        if (c.Paused)
        {
            (tone, pill) = (PillTone.Waiting, "Waiting");
            line = "Paused: " + SortPanel.PausedWords(c.Status["paused: ".Length..]) + ".";
        }
        else if (c.Job is SortScope job)
        {
            (tone, pill) = (PillTone.Moving, "Moving");
            line = job switch
            {
                SortScope.Unpack => "Emptying side packs...",
                SortScope.PutBack => "Putting items back...",
                SortScope.Clean => "Cleaning packs...",
                _ => "Moving items to their packs...",
            };
        }
        else if (c.Send != null || (c.HasMoveOut && !c.Data.Settings.Auto))
        {
            (tone, pill) = (PillTone.Moving, "Moving");
            line = "Moving the item you sent...";
        }
        else if (!c.Data.Settings.Auto)
        {
            (tone, pill) = (PillTone.Off, "Off");
            line = moves > 0 ? $"Nothing moves by itself. A sort would move {SortPanel.Count(moves, "item")}." : "Nothing moves by itself.";
        }
        else if (c.Status == "sorting" || c.HasMoveOut)
        {
            (tone, pill) = (PillTone.Moving, "Moving");
            line = "Moving items to their packs...";
        }
        else if (c.Status is "starting" or "reading your packs" or "waiting for the inventory")
        {
            (tone, pill) = (PillTone.Waiting, "Reading");
            line = "Reading your packs...";
        }
        else if (v.WaitFull + v.WaitNoPack + v.WaitMainFull > 0)
        {
            (tone, pill) = (PillTone.Waiting, "Waiting");
            var why = new List<string>(3);
            if (v.WaitFull > 0) why.Add("a pack is full");
            if (v.WaitNoPack > 0) why.Add("a rule's pack isn't carried");
            if (v.WaitMainFull > 0) why.Add("the main pack is full");
            line = $"{SortPanel.Count(v.WaitFull + v.WaitNoPack + v.WaitMainFull, "item")} can't move: {string.Join(", ", why)}.";
        }
        else if (v.WaitSettle > 0)
        {
            (tone, pill) = (PillTone.Waiting, "Waiting");
            line = v.WaitSettle == 1
                ? "1 new item settling, then it goes to its pack."
                : $"{v.WaitSettle} new items settling, then they go to their packs.";
        }
        else if (noRules)
        {
            (tone, pill) = (PillTone.Waiting, "No rules");
            line = "No rules yet: pick a layout, or add a kind to a pack.";
        }
        else if (moves > 0)
        {
            (tone, pill) = (PillTone.Waiting, "On");
            line = $"{moves} to move.";
        }
        else
        {
            (tone, pill) = (PillTone.Tidy, "All tidy");
            line = $"New loot goes to its pack {c.Data.Settings.SettleSec} s after it lands.";
        }
        if (held > 0 && !busy) line += $" {held} left alone.";
        return new StatusModel
        {
            Line = line,
            Pill = pill,
            Tone = tone,
            Tip = line + "\n\n" + SortPanel.AutoTooltip,
        };
    }

    /// <summary>The red banner: per full pack, what didn't fit ("Satchel is full: Black Opal and Fire Opal stay where they are.").</summary>
    internal static string[] NoFitLines(SortView view, SortInventory inv)
    {
        var packs = new List<int>();
        foreach (NoFitLine l in view.NoFit) if (!packs.Contains(l.Pack)) packs.Add(l.Pack);
        var lines = new string[packs.Count];
        for (int i = 0; i < packs.Count; i++)
            lines[i] = SortController.NoFitText(view.NoFit.FindAll(l => l.Pack == packs[i]), inv);
        return lines;
    }

    /// <summary>The amber lines under the header.</summary>
    internal static string[] Warnings(SortController c, SortView real, SortView view, IReadOnlyList<string> own)
    {
        var list = new List<string>();
        foreach (string w in own) list.Add(w);
        if (real.MainFull) list.Add("The main pack is full.");
        if (c.Data.Settings.Auto && c.LastBot is RynthAiActivity bot && (bot & RynthAiActivity.AutoCram) != 0 && (bot & RynthAiActivity.MacroRunning) != 0)
            list.Add("RynthAi's AutoCram also moves loot out of the main pack; turn one of them off.");
        string numbering = SortPanel.NumberingNote(view);
        if (numbering.Length > 0) list.Add(numbering);
        return list.ToArray();
    }

    /// <summary>The starter layouts fitted to these packs, with what each would move.</summary>
    internal static LayoutCard[] Layouts(SortController c, SortInventory inv, List<SortRule> rules)
    {
        if (inv.Packs.Count == 0) return Array.Empty<LayoutCard>();
        // The kinds carried (any pack), in the order met: what a layout leaves in the main pack.
        var carried = new List<int>();
        void See(SortItem it)
        {
            if (SortPlanner.Sortable(it) && !carried.Contains(it.ObjectClass)) carried.Add(it.ObjectClass);
        }
        foreach (SortItem it in inv.Main.Items) See(it);
        foreach (SortPack sp in inv.Packs) foreach (SortItem it in sp.Items) See(it);

        var cards = new LayoutCard[StarterLayouts.All.Length];
        for (int k = 0; k < cards.Length; k++)
        {
            StarterLayout l = StarterLayouts.All[k];
            LayoutPlan plan = StarterLayouts.Fit(l, inv);
            SortPreview pv = SortPreview.Build(inv, plan.Rules, c.CurrentOptions, c.IsHeld);
            var bars = new uint[inv.Packs.Count][];
            var rows = new LayoutRow[inv.Packs.Count];
            var used = new HashSet<int>();
            for (int n = 1; n <= inv.Packs.Count; n++)
            {
                StarterGroup? g = null;
                foreach (var (group, pack) in plan.Placed) if (pack == n) { g = group; break; }
                int[] classes = g?.Classes ?? Array.Empty<int>();
                bars[n - 1] = new uint[classes.Length];
                var chips = new ChipModel[classes.Length];
                for (int j = 0; j < classes.Length; j++)
                {
                    used.Add(classes[j]);
                    bars[n - 1][j] = Kinds.Color(classes[j]);
                    chips[j] = new ChipModel { Pack = n, Cls = classes[j], Label = Kinds.Label(classes[j]), Dot = Kinds.Color(classes[j]) };
                }
                SortPack sp = inv.Packs[n - 1];
                rows[n - 1] = new LayoutRow
                {
                    Pack = n,
                    Badge = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    PackName = sp.Name.Length > 0 ? sp.Name : "Pack " + n,
                    Chips = chips,
                };
            }
            var left = new List<string>();
            foreach (int cls in carried)
                if (!used.Contains(cls))
                {
                    string label = Kinds.Label(cls).ToLowerInvariant();
                    if (!left.Contains(label)) left.Add(label);
                }
            cards[k] = new LayoutCard
            {
                Key = l.Key,
                Name = l.Name,
                Blurb = l.Blurb,
                Bars = bars,
                Meta = pv.Moves.Count > 0 ? $"{pv.Moves.Count} would move" : "nothing to move",
                Rows = rows,
                Left = left.Count > 0 ? "Stay in the main pack: " + string.Join(", ", left) + "." : "Every kind you carry has a pack.",
                Replaces = rules.Count > 0 ? " This replaces your rules; Undo brings them back." : string.Empty,
            };
        }
        return cards;
    }

    private static string ItemName(SortItem it) => it.Name.Length > 0 ? it.Name : $"(item 0x{it.Id:X8})";

    private static string Capital(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}

/// <summary>The words the detail popup builds when it opens (pure, so the tests read them).</summary>
internal static class ScreenText
{
    /// <summary>One run of the "why" line: its text, and whether it is bold.</summary>
    internal readonly record struct Run(string Text, bool Bold);

    /// <summary>
    /// Why <paramref name="t"/> is where it is: in its pack by which rule, goes to X by rule Y, or
    /// no rule names it so it stays in the main pack.
    /// </summary>
    public static Run[] Why(TileModel t, ScreenModel m)
    {
        switch (t.Rule)
        {
            case TileRule.Pack when t.RulePack == t.Where:
                return new[] { new Run("In its pack: ", false), new Run(m.PackName(t.RulePack), true), new Run(", by the rule ", false), new Run(t.RuleText, true), new Run(".", false) };
            case TileRule.Pack:
                return new[]
                {
                    new Run("Goes to ", false), new Run(m.PackName(t.RulePack), true), new Run(" by the rule ", false), new Run(t.RuleText, true),
                    new Run(t.Held ? ", once it's no longer left alone." : ".", false),
                };
            case TileRule.Job when t.RulePack == t.Where:
                return new[] { new Run("In its pack: ", false), new Run(m.PackName(t.RulePack), true), new Run(", by its job ", false), new Run(t.RuleText, true), new Run(".", false) };
            case TileRule.Job:
                return new[]
                {
                    new Run("Goes to ", false), new Run(m.PackName(t.RulePack), true), new Run(": its job is ", false), new Run(t.RuleText, true),
                    new Run(t.Held ? ", once it's no longer left alone." : ", and RynthAi says this item is one.", false),
                };
            case TileRule.Missing:
                return new[] { new Run("Its rule ", false), new Run(t.RuleText, true), new Run(" names a pack you don't carry, so it stays where it is.", false) };
            default:
                return t.Where == 0
                    ? new[] { new Run("No rule names it, so it stays in the main pack.", false) }
                    : new[] { new Run("No rule names it. It stays in ", false), new Run(m.PackName(t.Where), true), new Run("; Clean sends it back to the main pack.", false) };
        }
    }

    /// <summary>"Gems · in Satchel": the detail popup's line under the name.</summary>
    public static string KindAndPlace(TileModel t, ScreenModel m) =>
        t.KindLabel + " · in " + (t.Where == 0 ? "the main pack" : m.PackName(t.Where));

    /// <summary>Whether <paramref name="t"/>'s name has <paramref name="query"/> in it (any case); an empty query matches nothing (no highlight).</summary>
    public static bool Matches(TileModel t, string query) =>
        query.Length > 0 && t.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
}
