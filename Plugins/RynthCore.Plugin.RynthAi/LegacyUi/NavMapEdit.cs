// NavMapEdit.cs - route edits from the engine's Dungeon Map (click on the map to add a point,
// drag a point to move it, "Set as next target"). The map sends coordinates it worked out from
// the floor under the cursor, so these commands carry NS / EW / Z instead of using the player's
// position like addWaypoint does:
//
//   addPointAt  {NS, EW, Z, AddMode, InsertAt}   a Point; AddMode/InsertAt as addWaypoint
//                                                (0 = end; 2 = after InsertAt; 1 = before it)
//   movePointTo {Index, NS, EW, Z}               moves step Index; its type and every other
//                                                field (spell, pause, chat text, portal) stay
//   setActive   {Index}                          the step navigation goes to next
//
// NS / EW are the usual nav coordinates (EW = (worldX / 24 - 1019.5) / 10, worldX = landblock
// X * 192 + local X); Z is nav Z (world Z / 240), as addWaypoint stores it.
//
// Compatibility: the nav JSON says MapEdit = Version, so the engine only offers map editing
// when RynthAi understands these commands. Older RynthAi builds ignore unknown commands.
// The logic is static over the settings so the host tests can run it without a renderer.
using System;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal static class NavMapEdit
{
    /// <summary>What RynthPluginGetNavJson reports as MapEdit (0 / missing = no map editing).</summary>
    public const int Version = 1;

    /// <summary>The nav coordinate range of Dereth (and a little more); anything else is refused.</summary>
    private const double MaxCoord = 110.0;

    /// <summary>Nav Z range accepted (world Z of +-24000, far beyond any real floor).</summary>
    private const double MaxZ = 100.0;

    public static bool Handles(string? cmd) => cmd is "addPointAt" or "movePointTo" or "setActive";

    public enum Result { None, RouteChanged, ActiveChanged }

    /// <summary>
    /// Applies one map command to <paramref name="s"/>'s route. RouteChanged: the caller saves
    /// the route file (TryAutoSaveNav); ActiveChanged: the caller saves the settings.
    /// <paramref name="status"/> says what happened (or why nothing did), for the log.
    /// </summary>
    public static Result Apply(LegacyUiSettings s, NavCommand cmd, out string status)
    {
        var pts = s.CurrentRoute.Points;
        switch (cmd.Cmd)
        {
            case "addPointAt":
            {
                if (!ValidCoords(cmd.NS, cmd.EW, cmd.Z, out status)) return Result.None;
                var pt = new NavPoint { Type = NavPointType.Point, NS = cmd.NS, EW = cmd.EW, Z = cmd.Z };
                int at = InsertIndex(pts.Count, cmd.AddMode, cmd.InsertAt);
                pts.Insert(at, pt);
                // Keep navigation heading for the same step: a point put in before it shifts it down.
                if (at <= s.ActiveNavIndex && pts.Count > 1) s.ActiveNavIndex++;
                status = $"Added point {at}.";
                return Result.RouteChanged;
            }
            case "movePointTo":
            {
                if (cmd.Index < 0 || cmd.Index >= pts.Count) { status = $"No step {cmd.Index}."; return Result.None; }
                if (!ValidCoords(cmd.NS, cmd.EW, cmd.Z, out status)) return Result.None;
                NavPoint p = pts[cmd.Index];
                p.NS = cmd.NS; p.EW = cmd.EW; p.Z = cmd.Z;
                status = $"Moved step {cmd.Index}.";
                return Result.RouteChanged;
            }
            case "setActive":
                if (cmd.Index < 0 || cmd.Index >= pts.Count) { status = $"No step {cmd.Index}."; return Result.None; }
                s.ActiveNavIndex = cmd.Index;
                status = $"Active waypoint: {cmd.Index}.";
                return Result.ActiveChanged;
        }
        status = string.Empty;
        return Result.None;
    }

    /// <summary>Where a new point goes: 0 = the end; 1 = before InsertAt; 2 = after it (end when InsertAt &lt; 0).</summary>
    internal static int InsertIndex(int count, int addMode, int insertAt)
    {
        if (addMode == 0 || count == 0 || insertAt < 0) return count;
        return addMode == 1 ? Math.Min(insertAt, count) : Math.Min(insertAt + 1, count);
    }

    private static bool ValidCoords(double ns, double ew, double z, out string status)
    {
        if (!double.IsFinite(ns) || !double.IsFinite(ew) || !double.IsFinite(z)
            || Math.Abs(ns) > MaxCoord || Math.Abs(ew) > MaxCoord || Math.Abs(z) > MaxZ)
        {
            status = "Refused: coordinates out of range.";
            return false;
        }
        status = string.Empty;
        return true;
    }
}
