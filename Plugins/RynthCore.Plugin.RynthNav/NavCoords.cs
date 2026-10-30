using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// Map coordinates (the /loc numbers: NS and EW in decimal degrees, north and east
/// positive) and RynthNav's world units (x = east, y = north, 24 units per tenth of
/// a degree, landblock 192 units). No host calls: the offline tests compile this file.
/// </summary>
internal static class NavCoords
{
    public const double UnitsPerDegree = 240.0;

    private static readonly string[] Compass16 =
        { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

    public static double WorldX(double ew) => (ew * 10.0 + 1019.5) * 24.0;
    public static double WorldY(double ns) => (ns * 10.0 + 1019.5) * 24.0;
    public static double EwFromWorld(double wx) => (wx / 24.0 - 1019.5) / 10.0;
    public static double NsFromWorld(double wy) => (wy / 24.0 - 1019.5) / 10.0;

    /// <summary>Map coordinates of a cell-relative position (NavCoordinateHelper's formula).</summary>
    public static void FromCell(uint cell, double x, double y, out double ns, out double ew)
    {
        int lbX = (int)((cell >> 24) & 0xFF), lbY = (int)((cell >> 16) & 0xFF);
        ew = (lbX * 8.0 + x / 24.0 - 1019.5) / 10.0;
        ns = (lbY * 8.0 + y / 24.0 - 1019.5) / 10.0;
    }

    /// <summary>Straight-line distance in world units (yards).</summary>
    public static double Distance(double ns1, double ew1, double ns2, double ew2)
    {
        double dn = (ns2 - ns1) * UnitsPerDegree, de = (ew2 - ew1) * UnitsPerDegree;
        return Math.Sqrt(dn * dn + de * de);
    }

    /// <summary>Compass bearing from one point to another: 0 = north, clockwise, 0..360.</summary>
    public static double Bearing(double fromNs, double fromEw, double toNs, double toEw)
    {
        double b = Math.Atan2(toEw - fromEw, toNs - fromNs) * 180.0 / Math.PI;
        return b < 0 ? b + 360.0 : b;
    }

    public static string CompassPoint(double bearing) => Compass16[(int)Math.Round(((bearing % 360.0) + 360.0) % 360.0 / 22.5) & 15];

    /// <summary>"42.1N, 33.6E".</summary>
    public static string Fmt(double ns, double ew) => FmtOne(ns, 'N', 'S') + ", " + FmtOne(ew, 'E', 'W');

    public static string FmtOne(double v, char pos, char neg) =>
        Math.Abs(v).ToString("F1", CultureInfo.InvariantCulture) + (v >= 0 ? pos : neg);

    /// <summary>"350 yd" up close, "2.4 km" (well, thousands of yards) far away.</summary>
    public static string FmtDistance(double units) =>
        units < 1000 ? units.ToString("0", CultureInfo.InvariantCulture) + " yd"
                     : (units / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k yd";

    /// <summary>
    /// "42.5N, 33.6E" (the /loc order). Also takes "42.5 N, 33.6 E", and the other order
    /// when the letters say so ("33.6E 42.5N"). Two N/S (or two E/W) values are refused.
    /// (Moved here from RynthNavPlugin unchanged, so the tests can reach it.)
    /// </summary>
    public static bool TryParseLoc(string? s, out double ns, out double ew)
    {
        ns = 0; ew = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = new List<string>();
        foreach (var t in s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            bool loneLetter = t.Length == 1 && "NSEWnsew".IndexOf(t[0]) >= 0;
            if (loneLetter && parts.Count > 0 && char.IsDigit(parts[^1][^1])) parts[^1] += t;
            else parts.Add(t);
        }
        if (parts.Count < 2) return false;
        if (!TryCoord(parts[0], out double a, out char axisA) || !TryCoord(parts[1], out double b, out char axisB)) return false;
        if (axisA != ' ' && axisA == axisB) return false;
        if (axisA == 'E' || axisB == 'N') { ns = b; ew = a; }
        else { ns = a; ew = b; }
        return true;
    }

    /// <summary>
    /// A coordinate pair followed by an optional name: "42.1N, 33.6E Holtburg Lifestone"
    /// gives 42.1, 33.6 and "Holtburg Lifestone" (the panel and chat send targets this way).
    /// </summary>
    public static bool TrySplitCoordsAndName(string? s, out double ns, out double ew, out string name)
    {
        ns = 0; ew = 0; name = "";
        if (!TryParseLoc(s, out ns, out ew)) return false;
        // Skip the tokens TryParseLoc used: two numbers, each maybe with a letter after a space.
        string t = s!.Trim();
        int pos = 0;
        for (int n = 0; n < 2; n++)
        {
            while (pos < t.Length && (t[pos] == ' ' || t[pos] == ',')) pos++;
            while (pos < t.Length && t[pos] != ' ' && t[pos] != ',') pos++;
            int save = pos;
            while (pos < t.Length && t[pos] == ' ') pos++;
            if (pos < t.Length && "NSEWnsew".IndexOf(t[pos]) >= 0 && (pos + 1 == t.Length || t[pos + 1] == ' ' || t[pos + 1] == ','))
                pos++;
            else
                pos = save;
        }
        name = pos < t.Length ? t.Substring(pos).Trim(' ', ',') : "";
        return true;
    }

    /// <summary>
    /// True when <paramref name="s"/> starts with a coordinate pair ("42.1N 33.6E",
    /// "42.1, 33.6") rather than a name ("Holtburg", "Area 51").
    /// </summary>
    public static bool LooksLikeCoords(string? s) => TryParseLoc(s, out _, out _);

    // axis: 'N' for an N/S letter, 'E' for an E/W letter, ' ' for none.
    private static bool TryCoord(string tok, out double val, out char axis)
    {
        val = 0; axis = ' ';
        tok = tok.Trim().ToUpperInvariant();
        if (tok.Length == 0) return false;
        int sign = 1;
        char last = tok[^1];
        if (last is 'N' or 'S' or 'E' or 'W')
        {
            if (last is 'S' or 'W') sign = -1;
            axis = last is 'N' or 'S' ? 'N' : 'E';
            tok = tok[..^1];
        }
        if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return false;
        if (double.IsNaN(val) || double.IsInfinity(val)) return false;
        val *= sign;
        return true;
    }
}
