using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// "Save as nav route": a planned trip as a RynthAi nav route (uTank2 NAV 1.2, the VTank format
/// RynthAi reads and writes; RynthAi's own NavRouteParser, linked in, does the writing), so RynthAi
/// can load it from NavProfiles and run it. Route type Once.
///
///   a walk step      its path's waypoints (Point), thinned (TripPathBuilder.Thin): not every
///                    navmesh corner, a point at least every 60 yd on straight ground
///   a portal step    the walk up to PortalStopShort yd before the entrance, then a Portal step
///                    (VTank "Portal", type 6) naming the portal object, at the entrance: RynthAi
///                    uses it by name (the nearest one) and walks the last yards itself
///   a recall spell   a Recall step (type 2) with the spell id, cast where the trip stands
///   a command recall (@lifestone, @house recall...): a Chat step with the command, then a Pause
///                    of CommandRecallPauseMs (RynthAi doesn't wait for a chat command's teleport)
///   the Town Network the walk inside (townnet.json) as waypoints, then a Portal step for the exit
///   a dungeon goto   ends with the Portal step into the dungeon's entrance portal
/// A step it can't write (a recall the plan doesn't know, a Town Network walk townnet.json doesn't
/// have) stops the route there: what comes before is kept, and the reason is given.
/// Host-free: the route tests convert made-up trips and read the file back with RynthAi's reader.
/// </summary>
internal static class NavRouteExport
{
    /// <summary>The walk to a portal stops this far short of its entrance (the Portal step walks the rest).</summary>
    public const double PortalStopShort = 5.0;
    /// <summary>Inside the Town Network the stored walk ends at the exit portal: stop this far short.</summary>
    public const double HubStopShort = 2.0;
    /// <summary>After a command recall's chat step: wait this long for its teleport (ms).</summary>
    public const int CommandRecallPauseMs = 20000;
    /// <summary>VTank's object class for a portal (the Portal step's second trailer line).</summary>
    public const int PortalObjectClass = 14;

    public sealed class Result
    {
        public NavRouteParser Route = new() { RouteType = NavRouteType.Once };
        /// <summary>The step (0-based) the route stops at, short of the trip's end; -1 = the whole trip.</summary>
        public int StoppedAt = -1;
        public string StopReason = "";
        public int Waypoints, Portals, Recalls, Chats;
    }

    /// <summary>The trip as nav points (see the class notes). <paramref name="path"/> must be this trip's.</summary>
    public static Result Convert(PlannedTrip trip, TripPath path, TownNet? net)
    {
        var r = new Result();
        if (path.TripId != trip.Id) { r.StoppedAt = 0; r.StopReason = "the path was worked out for another trip"; return r; }
        WorldPt here = trip.Start;           // where action steps happen when nothing better is known
        double lastH = double.IsNaN(trip.Start.H) ? 0 : trip.Start.H;

        void Stop(int i, string why) { r.StoppedAt = i; r.StopReason = $"step {i + 1}: {why}"; }
        void AddPoint(WorldPt p)
        {
            if (!double.IsNaN(p.H)) lastH = p.H;
            r.Route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = p.Ns, EW = p.Ew, Z = lastH / 240.0 });
            r.Waypoints++;
            here = p;
        }
        void AddPortal(string name, WorldPt at)
        {
            if (!double.IsNaN(at.H)) lastH = at.H;
            double ns = at.Ns, ew = at.Ew, z = lastH / 240.0;
            r.Route.Points.Add(new NavPoint
            {
                Type = NavPointType.PortalNPC, NS = ns, EW = ew, Z = z,
                TargetName = name, ObjectClass = PortalObjectClass, IsTie = true,
                PortalExitNS = ns, PortalExitEW = ew, PortalExitZ = z,
            });
            r.Portals++;
        }
        void Land(RouteStep s)
        {
            if (!double.IsNaN(s.LandNs)) here = WorldPt.FromMap(s.LandNs, s.LandEw, double.NaN);
        }

        for (int i = 0; i < trip.Count; i++)
        {
            RouteStep s = trip.Steps[i];
            if (s.UseRecall)
            {
                TripRecall? rec = trip.RecallOf(i);
                if (rec is not TripRecall o) { Stop(i, $"the recall {s.Label} isn't one the plan knows how to cast"); return r; }
                WorldPt at = trip.Point[i] ?? here;
                double z = (double.IsNaN(at.H) ? lastH : at.H) / 240.0;
                if (o.SpellId != 0)
                {
                    r.Route.Points.Add(new NavPoint { Type = NavPointType.Recall, NS = at.Ns, EW = at.Ew, Z = z, SpellId = o.SpellId });
                    r.Recalls++;
                }
                else if (o.Command.Length > 0)
                {
                    r.Route.Points.Add(new NavPoint { Type = NavPointType.Chat, NS = at.Ns, EW = at.Ew, Z = z, ChatCommand = o.Command });
                    r.Route.Points.Add(new NavPoint { Type = NavPointType.Pause, NS = at.Ns, EW = at.Ew, Z = z, PauseTimeMs = CommandRecallPauseMs });
                    r.Chats++;
                }
                else { Stop(i, $"{o.Name} has neither a spell nor a command"); return r; }
                Land(s);
                continue;
            }
            if (s.IsHub)
            {
                WorldPt[]? inside = path.Hub[i];
                WorldPt? exit = PlannedTrip.HubExitPoint(net, s);
                if (inside == null || exit == null || net == null)
                {
                    trip.HubWalk(i, net, out string why);
                    Stop(i, $"the walk inside the Town Network isn't known ({(why.Length > 0 ? why : "no townnet.json")})");
                    return r;
                }
                List<WorldPt> walk = TripPathBuilder.StopShort(inside, HubStopShort);
                for (int k = 1; k < walk.Count; k++) AddPoint(walk[k]);
                AddPortal(net.Exits[s.HubExit].Name, exit.Value);
                Land(s);
                continue;
            }
            WorldPt[]? pts = path.Walks[i];
            if (pts == null || pts.Length == 0)
            {
                if (trip.WalkFrom[i] == null) { Stop(i, "it starts where the plan has no map position (inside a dungeon)"); return r; }
                Stop(i, "its walk wasn't worked out");
                return r;
            }
            if (s.UsePortal)
            {
                List<WorldPt> walk = TripPathBuilder.StopShort(pts, PortalStopShort);
                for (int k = 1; k < walk.Count; k++) AddPoint(walk[k]);
                WorldPt at = trip.Point[i] is WorldPt p ? p with { H = pts[^1].H } : pts[^1];
                string name = trip.PortalName(i);
                if (name.Length == 0) { Stop(i, "the portal has no name to look for"); return r; }
                AddPortal(name, at);
                Land(s);
                continue;
            }
            for (int k = 1; k < pts.Length; k++) AddPoint(pts[k]);
        }
        return r;
    }

    // ── The file ─────────────────────────────────────────────────────────

    /// <summary>
    /// A nav file name from what was typed: trimmed, without ".nav". "" with <paramref name="error"/>
    /// when it can't be one (empty, characters a file name can't have, a path).
    /// </summary>
    public static string FileName(string typed, out string error)
    {
        error = "";
        string n = (typed ?? "").Trim();
        if (n.EndsWith(".nav", StringComparison.OrdinalIgnoreCase)) n = n[..^4].TrimEnd();
        if (n.Length == 0) { error = "give the route a name"; return ""; }
        if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.Contains("..") || n.Length > 100)
        { error = $"'{typed}' can't be used as a file name"; return ""; }
        return n;
    }

    /// <summary>A file name made from a place's name (characters a file can't have become spaces).</summary>
    public static string SafeName(string text)
    {
        var chars = (text ?? "").ToCharArray();
        char[] bad = Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++) if (Array.IndexOf(bad, chars[i]) >= 0) chars[i] = ' ';
        string s = string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 60 ? s[..60].TrimEnd() : s;
    }

    /// <summary>
    /// Writes <paramref name="route"/> to &lt;folder&gt;\&lt;name&gt;.nav. An existing file is only replaced
    /// with <paramref name="overwrite"/>; otherwise nothing is written and false comes back with
    /// <paramref name="exists"/> set. Written to a temporary file first, then moved into place.
    /// </summary>
    public static bool Save(string folder, string name, NavRouteParser route, bool overwrite, out string path, out bool exists, out string error)
    {
        error = "";
        path = Path.Combine(folder, name + ".nav");
        exists = File.Exists(path);
        if (exists && !overwrite) return false;
        string tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllLines(tmp, route.ToLines());
            File.Move(tmp, path, overwrite);
            return true;
        }
        catch (IOException) when (!overwrite && File.Exists(path))
        {
            exists = true;   // made by someone else in the meantime
            try { File.Delete(tmp); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }
}
