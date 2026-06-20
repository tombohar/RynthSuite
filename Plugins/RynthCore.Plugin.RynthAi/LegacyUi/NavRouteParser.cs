using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

public enum NavRouteType
{
    Circular = 1,
    Linear = 2,
    Follow = 3,
    Once = 4
}

// Waypoint types as serialized by uTank2 / VirindiTank ".nav" files. Trailer
// layouts below were reverse-engineered from 933 real VTank routes (2026-06-15);
// see Docs/Nav_DeepDive_2026-06-15.md. Type 1 (plain Portal) and any other type
// are intentionally absent — they never appear in real routes here and would
// need their on-disk layout confirmed before being parsed.
public enum NavPointType
{
    Point      = 0,
    Recall     = 2,   // trailer: spellId
    Pause      = 3,   // trailer: pauseMs
    Chat       = 4,   // trailer: command string
    OpenVendor = 5,   // trailer: vendorId(int), vendorName(string)
    PortalNPC  = 6,   // VTank "Portal": use a portal object by name. trailer: name, class, tie, ew, ns, z
    Npc        = 7    // VTank "NPC": use/talk an NPC by name.        trailer: name, class, tie, ew, ns, z
}

public sealed class NavPoint
{
    public NavPointType Type { get; set; }
    public double NS { get; set; }
    public double EW { get; set; }
    public double Z { get; set; }

    public int SpellId { get; set; }
    public int PauseTimeMs { get; set; }
    public string ChatCommand { get; set; } = string.Empty;

    // Portal (6) / Npc (7) target name; for OpenVendor (5) the vendor's name.
    public string TargetName { get; set; } = string.Empty;
    public int ObjectClass { get; set; }
    public bool IsTie { get; set; }

    // OpenVendor (5): the captured vendor object id from the route.
    public uint VendorId { get; set; }

    // Portal/Npc (6/7): the captured world location of the portal/NPC object,
    // usable to disambiguate same-named objects. Stored in the legacy
    // PortalExit* fields so existing references keep compiling.
    public double PortalExitNS { get; set; }
    public double PortalExitEW { get; set; }
    public double PortalExitZ { get; set; }

    // Retained for source compatibility only; no longer read or written (real
    // VTank Portal waypoints carry one object location, not exit+land coords).
    public double PortalLandNS { get; set; }
    public double PortalLandEW { get; set; }
    public double PortalLandZ { get; set; }

    public override string ToString()
    {
        return Type switch
        {
            NavPointType.Point      => $"[Point] {NS:F3}, {EW:F3}",
            NavPointType.Recall     => $"[Recall] Spell {SpellId}",
            NavPointType.Pause      => $"[Pause] {PauseTimeMs / 1000.0:F1}s",
            NavPointType.Chat       => $"[Chat] {ChatCommand}",
            NavPointType.OpenVendor => $"[Vendor] {TargetName}",
            NavPointType.PortalNPC  => $"[Portal] {TargetName}",
            NavPointType.Npc        => $"[NPC] {TargetName}",
            _                       => $"[Unknown] {(int)Type}"
        };
    }
}

public sealed class NavRouteParser
{
    public NavRouteType RouteType { get; set; }
    public List<NavPoint> Points { get; set; } = new();

    /// <summary>
    /// Non-null if the last load hit an unknown waypoint type or a malformed
    /// line. The route still holds whatever points parsed cleanly before that.
    /// </summary>
    public string? LoadWarning { get; private set; }

    /// <summary>
    /// Trailer line count (lines AFTER the 5-line type/EW/NS/Z/flag prologue)
    /// for each known waypoint type. Single source of truth so the parser and
    /// any external line-counter (e.g. AfFileParser) cannot drift apart — the
    /// drift between the two is what corrupted Portal routes pre-2026-06-15.
    /// </summary>
    public static int TrailerLineCount(NavPointType t) => t switch
    {
        NavPointType.Recall     => 1,
        NavPointType.Pause      => 1,
        NavPointType.Chat       => 1,
        NavPointType.OpenVendor => 2,
        NavPointType.PortalNPC  => 6,
        NavPointType.Npc        => 6,
        _                       => 0   // Point (and any zero-trailer type)
    };

    public static bool IsKnownType(int t) =>
        t is 0 or 2 or 3 or 4 or 5 or 6 or 7;

    public static NavRouteParser Load(string filePath, Action<string>? warn = null)
    {
        try
        {
            if (!File.Exists(filePath))
                return new NavRouteParser();
            return LoadFromLines(File.ReadAllLines(filePath), warn);
        }
        catch (Exception ex)
        {
            var route = new NavRouteParser { LoadWarning = $"Nav: failed to read '{filePath}': {ex.Message}" };
            warn?.Invoke(route.LoadWarning);
            return route;
        }
    }

    public static NavRouteParser LoadFromLines(IList<string> lines, Action<string>? warn = null)
    {
        var route = new NavRouteParser();
        if (lines.Count < 3 || !lines[0].Contains("uTank2 NAV 1.2", StringComparison.OrdinalIgnoreCase))
            return route;

        if (!int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int routeType) ||
            !int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pointCount))
        {
            route.LoadWarning = "Nav: malformed header (route-type / point-count not integers)";
            warn?.Invoke(route.LoadWarning);
            return route;
        }
        route.RouteType = (NavRouteType)routeType;

        int idx = 3;
        for (int i = 0; i < pointCount && idx < lines.Count; i++)
        {
            // Each point spans several lines. Localize any parse failure to this
            // single point so one bad/unknown waypoint cannot desync and corrupt
            // every following waypoint in the file.
            int pointStart = idx;
            try
            {
                int typeRaw = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                if (!IsKnownType(typeRaw))
                {
                    route.LoadWarning =
                        $"Nav: unknown waypoint type {typeRaw} at point {i + 1} (line {pointStart}); " +
                        $"stopped to avoid desync ({route.Points.Count} pts loaded). " +
                        "Report this type so support can be added.";
                    warn?.Invoke(route.LoadWarning);
                    break;
                }

                var pt = new NavPoint
                {
                    Type = (NavPointType)typeRaw,
                    EW   = double.Parse(lines[idx++], CultureInfo.InvariantCulture),
                    NS   = double.Parse(lines[idx++], CultureInfo.InvariantCulture),
                    Z    = double.Parse(lines[idx++], CultureInfo.InvariantCulture)
                };
                idx++; // skip the flag / colour line

                switch (pt.Type)
                {
                    case NavPointType.Recall:
                        pt.SpellId = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        break;
                    case NavPointType.Pause:
                        pt.PauseTimeMs = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        break;
                    case NavPointType.Chat:
                        pt.ChatCommand = lines[idx++];
                        break;
                    case NavPointType.OpenVendor:
                        pt.VendorId   = uint.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.TargetName = lines[idx++];
                        break;
                    case NavPointType.PortalNPC:
                    case NavPointType.Npc:
                        pt.TargetName   = lines[idx++];
                        pt.ObjectClass  = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.IsTie        = ParseTieFlag(lines[idx++]);
                        pt.PortalExitEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.PortalExitNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.PortalExitZ  = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        break;
                }

                route.Points.Add(pt);
            }
            catch (Exception ex)
            {
                route.LoadWarning =
                    $"Nav: parse error at point {i + 1} (line {pointStart}): {ex.Message}; " +
                    $"stopped ({route.Points.Count} pts loaded)";
                warn?.Invoke(route.LoadWarning);
                break;
            }
        }

        return route;
    }

    private static bool ParseTieFlag(string s)
    {
        if (bool.TryParse(s, out bool b)) return b;     // "True"/"False" (VTank wire format)
        return s != null && s.Trim() == "1";            // tolerate numeric tie flags
    }

    public void Save(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");

        using var writer = new StreamWriter(filePath, false);
        writer.WriteLine("uTank2 NAV 1.2");
        writer.WriteLine((int)RouteType);
        writer.WriteLine(Points.Count);

        foreach (NavPoint point in Points)
        {
            writer.WriteLine((int)point.Type);
            writer.WriteLine(point.EW.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(point.NS.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(point.Z.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine("0");

            switch (point.Type)
            {
                case NavPointType.Recall:
                    writer.WriteLine(point.SpellId);
                    break;
                case NavPointType.Pause:
                    writer.WriteLine(point.PauseTimeMs);
                    break;
                case NavPointType.Chat:
                    writer.WriteLine(point.ChatCommand);
                    break;
                case NavPointType.OpenVendor:
                    writer.WriteLine(point.VendorId.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.TargetName);
                    break;
                case NavPointType.PortalNPC:
                case NavPointType.Npc:
                    writer.WriteLine(point.TargetName);
                    writer.WriteLine(point.ObjectClass.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.IsTie ? "True" : "False");
                    writer.WriteLine(point.PortalExitEW.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.PortalExitNS.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.PortalExitZ.ToString(CultureInfo.InvariantCulture));
                    break;
            }
        }
    }
}
