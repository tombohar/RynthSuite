// NavRouteEditing.cs — route edit operations for the Navigation window (UB-IT nav builder
// parity plus a few extras): remove / duplicate / move selected, reverse, snap-insert on the
// path, simplify, nearest waypoint, trail → route.
//
// The nav engine reads CurrentRoute.Points on the plugin tick thread while the window edits
// on the render thread, so structural edits never mutate the live list: they build a new
// list and swap the reference (atomic), keeping the active waypoint on the same NavPoint.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal static class NavRouteEditing
{
    /// <summary>Nav map units → yards (the engine's distance scale).</summary>
    public const double NavToYards = 240.0;

    public static double DistanceYards(double ns1, double ew1, double ns2, double ew2)
    {
        double dn = ns2 - ns1, de = ew2 - ew1;
        return Math.Sqrt(dn * dn + de * de) * NavToYards;
    }

    /// <summary>
    /// Copy-on-write edit of the current route. <paramref name="edit"/> receives a private copy
    /// and returns the new list; the active waypoint follows its NavPoint (or is clamped when
    /// that point was removed).
    /// </summary>
    public static void Apply(LegacyUiSettings s, Func<List<NavPoint>, List<NavPoint>> edit)
    {
        var route = s.CurrentRoute;
        var old = route.Points;
        int oldActive = s.ActiveNavIndex;
        NavPoint? active = oldActive >= 0 && oldActive < old.Count ? old[oldActive] : null;

        var next = edit(new List<NavPoint>(old));
        route.Points = next;

        int ai = active != null ? next.IndexOf(active) : -1;
        s.ActiveNavIndex = ai >= 0 ? ai : Math.Clamp(oldActive, 0, Math.Max(0, next.Count - 1));
    }

    /// <summary>Field-for-field copy (NavPoint is a plain DTO).</summary>
    public static NavPoint Clone(NavPoint p) => new()
    {
        Type = p.Type, NS = p.NS, EW = p.EW, Z = p.Z,
        SpellId = p.SpellId, PauseTimeMs = p.PauseTimeMs, ChatCommand = p.ChatCommand,
        TargetName = p.TargetName, ObjectClass = p.ObjectClass, IsTie = p.IsTie, VendorId = p.VendorId,
        PortalExitNS = p.PortalExitNS, PortalExitEW = p.PortalExitEW, PortalExitZ = p.PortalExitZ,
    };

    public static List<NavPoint> Remove(List<NavPoint> list, HashSet<NavPoint> sel)
    {
        list.RemoveAll(sel.Contains);
        return list;
    }

    /// <summary>Each selected point gets a copy right after it; returns the copies via <paramref name="copies"/>.</summary>
    public static List<NavPoint> Duplicate(List<NavPoint> list, HashSet<NavPoint> sel, List<NavPoint> copies)
    {
        var result = new List<NavPoint>(list.Count + sel.Count);
        foreach (var p in list)
        {
            result.Add(p);
            if (!sel.Contains(p)) continue;
            var c = Clone(p);
            result.Add(c);
            copies.Add(c);
        }
        return result;
    }

    /// <summary>Moves every selected point one slot up (delta -1) or down (+1), keeping their order;
    /// a selected point already at the edge (or blocked by another selected one there) stays put.</summary>
    public static List<NavPoint> Move(List<NavPoint> list, HashSet<NavPoint> sel, int delta)
    {
        if (delta < 0)
        {
            for (int i = 1; i < list.Count; i++)
                if (sel.Contains(list[i]) && !sel.Contains(list[i - 1]))
                    (list[i - 1], list[i]) = (list[i], list[i - 1]);
        }
        else
        {
            for (int i = list.Count - 2; i >= 0; i--)
                if (sel.Contains(list[i]) && !sel.Contains(list[i + 1]))
                    (list[i + 1], list[i]) = (list[i], list[i + 1]);
        }
        return list;
    }

    public static List<NavPoint> Reverse(List<NavPoint> list)
    {
        list.Reverse();
        return list;
    }

    /// <summary>Nearest travel Point to the given position, or -1 when the route has none.</summary>
    public static int NearestPointIndex(IReadOnlyList<NavPoint> list, double ns, double ew)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p.Type != NavPointType.Point) continue;
            double d = DistanceYards(ns, ew, p.NS, p.EW);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// UB-IT "snap to path": projects the position onto every Point→Point leg (NS/EW plane),
    /// and inserts a Point at the closest spot (Z interpolated) before the leg's far end.
    /// Refuses when the spot lands within <paramref name="minGapYards"/> of an existing point.
    /// </summary>
    public static bool TrySnapInsert(List<NavPoint> list, double ns, double ew, double minGapYards,
        out NavPoint? inserted, out string error)
    {
        inserted = null;
        error = string.Empty;
        int bestLeg = -1;
        double bestDist = double.MaxValue, bestT = 0;
        for (int i = 0; i + 1 < list.Count; i++)
        {
            NavPoint a = list[i], b = list[i + 1];
            if (a.Type != NavPointType.Point || b.Type != NavPointType.Point) continue;
            double vn = b.NS - a.NS, ve = b.EW - a.EW;
            double len2 = vn * vn + ve * ve;
            double t = len2 < 1e-12 ? 0 : Math.Clamp(((ns - a.NS) * vn + (ew - a.EW) * ve) / len2, 0, 1);
            double d = DistanceYards(ns, ew, a.NS + vn * t, a.EW + ve * t);
            if (d < bestDist) { bestDist = d; bestLeg = i; bestT = t; }
        }
        if (bestLeg < 0) { error = "The route has no Point→Point leg to snap onto."; return false; }

        NavPoint pa = list[bestLeg], pb = list[bestLeg + 1];
        var p = new NavPoint
        {
            Type = NavPointType.Point,
            NS = pa.NS + (pb.NS - pa.NS) * bestT,
            EW = pa.EW + (pb.EW - pa.EW) * bestT,
            Z  = pa.Z  + (pb.Z  - pa.Z)  * bestT,
        };
        if (DistanceYards(p.NS, p.EW, pa.NS, pa.EW) < minGapYards || DistanceYards(p.NS, p.EW, pb.NS, pb.EW) < minGapYards)
        {
            error = $"Closest path spot is on waypoint {(bestT < 0.5 ? bestLeg : bestLeg + 1)} already.";
            return false;
        }
        list.Insert(bestLeg + 1, p);
        inserted = p;
        return true;
    }

    /// <summary>
    /// Douglas–Peucker over each run of consecutive Points (action steps split runs and are
    /// never touched; run endpoints are kept). Removes Points that deviate less than
    /// <paramref name="toleranceYards"/> from the simplified line. Returns the removed count.
    /// </summary>
    public static int Simplify(List<NavPoint> list, double toleranceYards, NavPoint? keep)
    {
        var drop = new HashSet<NavPoint>();
        int runStart = -1;
        for (int i = 0; i <= list.Count; i++)
        {
            bool isPoint = i < list.Count && list[i].Type == NavPointType.Point;
            if (isPoint && runStart < 0) runStart = i;
            if (!isPoint && runStart >= 0)
            {
                if (i - 1 - runStart >= 2)
                    DouglasPeucker(list, runStart, i - 1, toleranceYards, drop);
                runStart = -1;
            }
        }
        if (keep != null) drop.Remove(keep); // never remove the active waypoint
        list.RemoveAll(drop.Contains);
        return drop.Count;
    }

    private static void DouglasPeucker(List<NavPoint> list, int first, int last, double tol, HashSet<NavPoint> drop)
    {
        // Iterative stack so long recorded runs can't overflow the stack.
        var stack = new Stack<(int, int)>();
        stack.Push((first, last));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            double maxD = -1;
            int maxI = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegmentDistanceYards(list[i], list[a], list[b]);
                if (d > maxD) { maxD = d; maxI = i; }
            }
            if (maxD > tol)
            {
                stack.Push((a, maxI));
                stack.Push((maxI, b));
            }
            else
            {
                for (int i = a + 1; i < b; i++) drop.Add(list[i]);
            }
        }
    }

    /// <summary>Horizontal distance from <paramref name="p"/> to segment a–b, yards.</summary>
    private static double SegmentDistanceYards(NavPoint p, NavPoint a, NavPoint b)
    {
        double vn = b.NS - a.NS, ve = b.EW - a.EW;
        double len2 = vn * vn + ve * ve;
        double t = len2 < 1e-12 ? 0 : Math.Clamp(((p.NS - a.NS) * vn + (p.EW - a.EW) * ve) / len2, 0, 1);
        return DistanceYards(p.NS, p.EW, a.NS + vn * t, a.EW + ve * t);
    }

    /// <summary>Total walking length of the route's Point→Point legs, yards.</summary>
    public static double PathLengthYards(IReadOnlyList<NavPoint> list)
    {
        double total = 0;
        for (int i = 0; i + 1 < list.Count; i++)
        {
            NavPoint a = list[i], b = list[i + 1];
            if (a.Type == NavPointType.Point && b.Type == NavPointType.Point)
                total += DistanceYards(a.NS, a.EW, b.NS, b.EW);
        }
        return total;
    }
}
