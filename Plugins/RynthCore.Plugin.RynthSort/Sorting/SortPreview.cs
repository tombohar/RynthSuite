using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>
/// What a sort would do now, before anything moves: every item that would go to another pack
/// (from where, to where), the items left alone because the player touched them, what waits and
/// why, and how much tidying inside the packs follows. Planned on a planner of its own (no settle
/// clocks, no blocks), so it never disturbs the real one. Pure: the window builds one from the
/// last read, with the rules as they are or with a starter layout's rules.
/// </summary>
internal sealed class SortPreview
{
    /// <summary>Moves to another pack, in the order a sort would make them.</summary>
    public readonly List<SortMove> Moves = new();
    /// <summary>The view of this plan: the packs, what waits and why, the held items.</summary>
    public SortView View = new();
    /// <summary>How many items go into each pack: [0] the main pack, [n] side pack n.</summary>
    public int[] Into = Array.Empty<int>();
    /// <summary>How many items leave each pack, as <see cref="Into"/>.</summary>
    public int[] OutOf = Array.Empty<int>();
    /// <summary>Items out of place inside their packs now (put in order once every item is in its pack); 0 when ordering is off.</summary>
    public int ToOrder;

    public bool Empty => Moves.Count == 0;

    /// <summary>
    /// The preview for <paramref name="rules"/> on <paramref name="inv"/>: as Sort now (or Clean,
    /// with <paramref name="clean"/>) would go, holds included.
    /// </summary>
    public static SortPreview Build(SortInventory inv, IReadOnlyList<SortRule> rules, PlanOptions opt, Func<uint, bool>? held, bool clean = false)
    {
        var planner = new SortPlanner { Held = held };
        List<SortMove> moves = planner.Plan(inv, rules, clean ? SortScope.Clean : SortScope.All, force: true, 0, 0, opt);
        var p = new SortPreview { View = planner.View, Into = new int[inv.Packs.Count + 1], OutOf = new int[inv.Packs.Count + 1] };
        foreach (SortMove m in moves)
        {
            if (m.Kind != MoveKind.Route) continue;
            p.Moves.Add(m);
            p.Into[m.DestIndex]++;
            if (m.FromIndex >= 0 && m.FromIndex < p.OutOf.Length) p.OutOf[m.FromIndex]++;
        }
        if (p.View.Ordering)
        {
            p.ToOrder = p.View.Main.OutOfPlace;
            foreach (PackLine line in p.View.Packs) p.ToOrder += line.OutOfPlace;
        }
        else if (p.View.Handy) p.ToOrder = p.View.HandyBehind;   // ordering off: only the handy items move to the front
        return p;
    }
}
