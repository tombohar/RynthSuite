// ============================================================================
//  RynthDefective - Corpses/CorpseRows.cs
//  The Corpses window's rows, built on the plugin tick from the book, the other
//  characters' files and where the player stands. Pure (tests build rows).
//
//  A row: the place (the dungeon's name, or the map coordinates outdoors), then
//  how long ago, the rot estimate and, when the player can be measured against
//  it (outdoors, or in the same dungeon), how far and which way, with an arrow
//  from where the player faces.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthDefective.Corpses;

internal sealed record CorpseRow(
    string Place,
    string Detail,
    string Tip,
    float Relative,          // degrees from where the player faces (0 ahead, + to the right); NaN = no arrow
    CorpseRecord? Record,    // this character's corpse (click: select it; X: forget it); null for another character's
    uint ObjectId);

internal sealed record CorpsesView(bool Ready, IReadOnlyList<CorpseRow> Mine, IReadOnlyList<CorpseRow> Others)
{
    public static readonly CorpsesView NotReady = new(false, Array.Empty<CorpseRow>(), Array.Empty<CorpseRow>());
}

/// <summary>Where the player is, for distances and the arrow (Cell 0 = unknown).</summary>
internal readonly record struct PlayerSpot(uint Cell, float X, float Y, float Z, double FacingDeg);

internal static class CorpseRows
{
    /// <summary>Beyond this (metres) a corpse gets no distance: the map coordinates say it better.</summary>
    public const double MaxShownDistance = 5000;

    public static CorpsesView Build(IReadOnlyList<CorpseRecord> mine, IReadOnlyList<OtherCorpses> others, PlayerSpot me, DateTime now)
    {
        var rows = new List<CorpseRow>(mine.Count);
        foreach (CorpseRecord c in mine)
        {
            float rel = float.NaN;
            var detail = new List<string>(3) { Age(c, now) };
            string rot = Rot(c, now);
            if (rot.Length > 0) detail.Add(rot);
            if (Offset(c, me, out double dist, out double bearing))
            {
                detail.Add(Distance(dist) + " " + Compass(bearing));
                if (dist >= 1.0)
                {
                    double r = bearing - me.FacingDeg;
                    while (r > 180) r -= 360;
                    while (r < -180) r += 360;
                    rel = (float)r;
                }
            }
            rows.Add(new CorpseRow(Place(c), string.Join(" · ", detail), Tip(c, null, now), rel, c, c.ObjectId));
        }
        var other = new List<CorpseRow>();
        foreach (OtherCorpses o in others)
            foreach (CorpseRecord c in o.Corpses)
            {
                string rot = Rot(c, now);
                string detail = o.Character + " · " + Age(c, now) + (rot.Length > 0 ? " · " + rot : "");
                other.Add(new CorpseRow(Place(c), detail, Tip(c, o, now), float.NaN, null, 0));
            }
        return new CorpsesView(true, rows, other);
    }

    /// <summary>"Matron Hive East", "41.2N, 33.6E", "41.2N, 33.6E (indoors)", "Dungeon 6146".</summary>
    public static string Place(CorpseRecord c)
    {
        if (c.Indoors)
        {
            PlaceNames.Dungeon? d = PlaceNames.DungeonAt(c.Landblock);
            if (d != null) return d.Name;
            if (PlaceNames.IsMapLandblock(c.Landblock) && c.TryCoords(out double bns, out double bew)) return Coords(bns, bew) + " (indoors)";
            return "Dungeon " + c.Landblock.ToString("X4", CultureInfo.InvariantCulture);
        }
        return c.TryCoords(out double ns, out double ew) ? Coords(ns, ew) : "Somewhere";
    }

    public static string Coords(double ns, double ew) =>
        Math.Abs(ns).ToString("0.0", CultureInfo.InvariantCulture) + (ns >= 0 ? "N" : "S") + ", "
        + Math.Abs(ew).ToString("0.0", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");

    /// <summary>"died 25m ago", "found 3m ago" (a corpse seen with no death seen).</summary>
    public static string Age(CorpseRecord c, DateTime now) =>
        c.DiedUtc != DateTime.MinValue ? "died " + Span(now - c.DiedUtc) + " ago"
        : c.SeenUtc != DateTime.MinValue ? "seen " + Span(now - c.SeenUtc) + " ago" : "";

    /// <summary>"rots in ~2h 5m", "may have rotted", or "" when unknown.</summary>
    public static string Rot(CorpseRecord c, DateTime now)
    {
        if (c.RotsUtc == DateTime.MinValue) return "";
        TimeSpan left = c.RotsUtc - now;
        return left > TimeSpan.Zero ? "rots in ~" + Span(left) : "may have rotted";
    }

    /// <summary>"40s", "25m", "3h 10m", "2d 4h".</summary>
    public static string Span(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        if (t.TotalHours < 1) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        if (t.TotalDays < 1) return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
        return ((int)t.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + t.Hours.ToString(CultureInfo.InvariantCulture) + "h";
    }

    public static string Distance(double metres) =>
        metres < 10 ? metres.ToString("0.0", CultureInfo.InvariantCulture) + " yd"
        : metres.ToString("N0", CultureInfo.InvariantCulture) + " yd";

    private static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    public static string Compass(double bearing) => Points[(int)Math.Round(((bearing % 360) + 360) % 360 / 45.0) % 8];

    /// <summary>How far and which way (degrees clockwise from north) the corpse is from the player, when they can be compared.</summary>
    public static bool Offset(CorpseRecord c, PlayerSpot me, out double dist, out double bearing)
    {
        dist = bearing = 0;
        if (me.Cell == 0) return false;
        double dx, dy, dz = 0;
        if (c.Cell != 0)
        {
            bool aIn = c.Indoors, bIn = (me.Cell & 0xFFFF) >= 0x100;
            if ((aIn || bIn) && c.Landblock != (me.Cell >> 16)) return false;
            dx = CorpseBook.Global(c.Cell, c.X, true) - CorpseBook.Global(me.Cell, me.X, true);
            dy = CorpseBook.Global(c.Cell, c.Y, false) - CorpseBook.Global(me.Cell, me.Y, false);
            dz = c.Z - me.Z;
        }
        else
        {
            if (!c.ServerCoords || (me.Cell & 0xFFFF) >= 0x100 || !CorpseBook.MapCoords(me.Cell, me.X, me.Y, out double ns, out double ew))
                return false;
            dx = (c.Ew - ew) * 240.0;
            dy = (c.Ns - ns) * 240.0;
        }
        dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist > MaxShownDistance) return false;
        bearing = Math.Atan2(dx, dy) * 180.0 / Math.PI;
        if (bearing < 0) bearing += 360.0;
        return true;
    }

    private static string Tip(CorpseRecord c, OtherCorpses? owner, DateTime now)
    {
        var sb = new StringBuilder();
        if (owner != null) sb.Append(owner.Character).Append(owner.Server.Length > 0 ? " on " + owner.Server : "").Append('\n');
        if (c.DiedUtc != DateTime.MinValue)
            sb.Append("Died ").Append(c.DiedUtc.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture))
              .Append(" (").Append(Span(now - c.DiedUtc)).Append(" ago)").Append('\n');
        else if (c.SeenUtc != DateTime.MinValue)
            sb.Append("Found in the world (no death seen); last seen ").Append(Span(now - c.SeenUtc)).Append(" ago").Append('\n');
        if (c.RotsUtc != DateTime.MinValue)
            sb.Append(c.RotsUtc > now ? "Should rot in about " + Span(c.RotsUtc - now) : "Should have rotted by now")
              .Append(" (level x 5 min, at least an hour; emptied, 15 s)").Append('\n');
        PlaceNames.Dungeon? d = c.Indoors ? PlaceNames.DungeonAt(c.Landblock) : null;
        sb.Append("Where: ").Append(Place(c));
        if (c.Cell != 0)
            sb.Append(" - landblock ").Append((c.Cell >> 16).ToString("X4", CultureInfo.InvariantCulture))
              .Append(", cell ").Append((c.Cell & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture))
              .Append(" (").Append(c.X.ToString("0.0", CultureInfo.InvariantCulture)).Append(", ")
              .Append(c.Y.ToString("0.0", CultureInfo.InvariantCulture)).Append(", ")
              .Append(c.Z.ToString("0.0", CultureInfo.InvariantCulture)).Append(')');
        if (d != null && d.HasEntrance) sb.Append("\nEntrance: ").Append(Coords(d.Ns, d.Ew));
        if (owner == null)
            sb.Append(c.ObjectId != 0 ? "\nClick: select it (when it's in view). X: forget it." : "\nX: forget it.");
        return sb.ToString();
    }
}
