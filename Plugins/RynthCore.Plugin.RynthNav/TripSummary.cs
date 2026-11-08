using System.Collections.Generic;
using System.Text.Json;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// A planned trip in figures (the "RynthNav.Routes" plan op): each step as walk, portal, recall or
/// town (the Town Network), with its name, where it is, the yards walked on it (the worked-out path
/// where there is one, else the straight line) and where a teleport lands; the yards in all, the
/// teleports, and a rough time: walking at PortalRoute.RunUnitsPerSecond, PortalRoute's overhead for
/// each teleport, and each recall's cast time. Host-free: the route tests build these.
/// </summary>
internal sealed class TripSummary
{
    public sealed class Step
    {
        public string Kind = "", Name = "";
        public double Ns = double.NaN, Ew = double.NaN, Yards;
        public double LandNs = double.NaN, LandEw = double.NaN;
    }

    public readonly List<Step> Steps = new();
    public double Yards;
    public int Teleports;
    public double CastSeconds;

    /// <summary>Rough minutes for the whole trip (see the class notes).</summary>
    public double Minutes =>
        (Yards / PortalRoute.RunUnitsPerSecond + Teleports * PortalRoute.TeleportOverheadUnits / PortalRoute.RunUnitsPerSecond + CastSeconds) / 60.0;

    private static double Length(WorldPt[]? pts)
    {
        double d = 0;
        if (pts != null) for (int i = 1; i < pts.Length; i++) d += pts[i - 1].FlatDistance(pts[i]);
        return d;
    }

    /// <summary>The trip's steps with their yards from <paramref name="path"/> (null: straight lines).</summary>
    public static TripSummary Of(PlannedTrip trip, TripPath? path)
    {
        var t = new TripSummary();
        bool own = path != null && path.TripId == trip.Id;
        for (int i = 0; i < trip.Count; i++)
        {
            RouteStep s = trip.Steps[i];
            var st = new Step();
            if (!double.IsNaN(s.LandNs)) { st.LandNs = s.LandNs; st.LandEw = s.LandEw; }
            if (s.UseRecall)
            {
                TripRecall? r = trip.RecallOf(i);
                st.Kind = "recall";
                st.Name = r?.Name ?? s.Label ?? "";
                if (trip.Point[i] is WorldPt at && (i > 0 || trip.StartOnMap)) { st.Ns = at.Ns; st.Ew = at.Ew; }   // a first recall in a dungeon: no map position
                t.CastSeconds += r?.CastSeconds ?? 0;
                t.Teleports++;
            }
            else if (s.IsHub)
            {
                st.Kind = "town";
                st.Name = s.Label ?? "";
                st.Ns = s.LandNs; st.Ew = s.LandEw;
                st.Yards = own && i < path!.Hub.Length ? Length(path.Hub[i]) : 0;
                t.Teleports++;
            }
            else
            {
                st.Kind = s.UsePortal ? "portal" : "walk";
                st.Name = s.UsePortal ? trip.PortalName(i) : i == trip.Count - 1 ? trip.Goal : (s.Label ?? "walk");
                st.Ns = s.Ns; st.Ew = s.Ew;
                WorldPt[]? w = own && i < path!.Walks.Length ? path.Walks[i] : null;
                st.Yards = w != null ? Length(w)
                    : trip.WalkFrom[i] is WorldPt f && trip.Point[i] is WorldPt p ? f.FlatDistance(p) : 0;
                if (s.UsePortal) t.Teleports++;
            }
            t.Yards += st.Yards;
            t.Steps.Add(st);
        }
        return t;
    }

    /// <summary>The steps and totals as the plan op's result fields (inside an object the caller opened).</summary>
    public void WriteFields(Utf8JsonWriter w)
    {
        w.WriteStartArray("steps");
        foreach (Step s in Steps)
        {
            w.WriteStartObject();
            w.WriteString("kind", s.Kind);
            w.WriteString("name", s.Name);
            RouteJson.Num(w, "ns", s.Ns, 4);
            RouteJson.Num(w, "ew", s.Ew, 4);
            RouteJson.Num(w, "yards", s.Yards, 0);
            if (double.IsFinite(s.LandNs)) RouteJson.Place(w, "land", s.LandNs, s.LandEw);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        RouteJson.Num(w, "yards", Yards, 0);
        w.WriteNumber("teleports", Teleports);
        RouteJson.Num(w, "minutes", Minutes, 1);
    }
}
