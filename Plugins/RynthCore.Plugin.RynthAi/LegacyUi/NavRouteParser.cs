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
    Npc        = 7,   // VTank "NPC": use/talk an NPC by name.        trailer: name, class, tie, ew, ns, z
    Checkpoint = 8,   // walked like a Point; no trailer (confirmed from a real route, 2026-09-28)
    Jump       = 9    // walk here, face heading, jump. trailer: heading, shift ("true"/"false"), ms
                      // (order from UtilityBelt's VTNJump.Parse/Write)
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
    public double JumpHeading { get; set; }   // Jump: heading to face (degrees)
    public bool   JumpShift   { get; set; }   // Jump: shift (walk) jump
    public double JumpMs      { get; set; }   // Jump: spacebar hold time = power (0-1000)

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

    /// <summary>
    /// A point the dungeon pathfinder put centred in front of (or beyond) a narrow opening, so
    /// the character goes through straight. The engine reaches it within at most 1 yd and does
    /// not cut the corner at it. Built in memory only: never read from or written to .nav files.
    /// </summary>
    public bool Doorway { get; set; }

    /// <summary>"Lifestone Recall": a recall step by its spell's name (it showed "Spell 1635",
    /// 2026-10-08); the id when the spell table doesn't know it.</summary>
    internal static string RecallName(int spellId)
    {
        string? name = SpellNameLookup?.Invoke(spellId);
        return string.IsNullOrEmpty(name) ? $"Spell {spellId}" : name;
    }

    /// <summary>The spell table's name for an id; RynthAi sets it (this file is also compiled into
    /// tools without the spell table, which keep "Spell N").</summary>
    internal static Func<int, string?>? SpellNameLookup { get; set; }

    public override string ToString()
    {
        return Type switch
        {
            NavPointType.Point      => $"[Point] {NS:F3}, {EW:F3}",
            NavPointType.Recall     => $"[Recall] {RecallName(SpellId)}",
            NavPointType.Pause      => $"[Pause] {PauseTimeMs / 1000.0:F1}s",
            NavPointType.Chat       => $"[Chat] {ChatCommand}",
            NavPointType.OpenVendor => $"[Vendor] {TargetName}",
            NavPointType.PortalNPC  => $"[Portal] {TargetName}",
            NavPointType.Npc        => $"[NPC] {TargetName}",
            NavPointType.Checkpoint => $"[Checkpoint] {NS:F3}, {EW:F3}",
            NavPointType.Jump       => $"[Jump] {JumpHeading:F0}° {JumpMs:F0}ms{(JumpShift ? " shift" : "")}",
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
        NavPointType.Jump       => 3,
        NavPointType.Recall     => 1,
        NavPointType.Pause      => 1,
        NavPointType.Chat       => 1,
        NavPointType.OpenVendor => 2,
        NavPointType.PortalNPC  => 6,
        NavPointType.Npc        => 6,
        _                       => 0   // Point (and any zero-trailer type)
    };

    public static bool IsKnownType(int t) =>
        t is 0 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9;

    /// <summary>A waypoint that is simply walked to (Point, Checkpoint).</summary>
    public static bool IsPlainWaypoint(NavPointType t) => t is NavPointType.Point or NavPointType.Checkpoint;

    /// <summary>
    /// Whether a freshly loaded route starts at [0] rather than at its nearest waypoint.
    /// Follow and Once routes always do, so their opening Recall/Portal/Chat steps fire;
    /// with <c>NavResumeNearest</c> off every route does.
    /// </summary>
    public static bool StartsAtFirstPoint(NavRouteType type, bool resumeNearest)
        => !resumeNearest || type is NavRouteType.Follow or NavRouteType.Once;

    /// <summary>
    /// Follow-type nav files (route type 3) name a character to follow instead of
    /// listing points: "uTank2 NAV 1.2", 3, name, id. Empty for other routes.
    /// </summary>
    public string FollowTargetName { get; set; } = string.Empty;
    public uint   FollowTargetId   { get; set; }

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

        // Follow navs: name + id of the character to follow, no points. Line 2 is
        // the name, not a point count, so this has to come before the header check.
        if (int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rt0)
            && rt0 == (int)NavRouteType.Follow && !int.TryParse(lines[2], out _))
        {
            route.RouteType = NavRouteType.Follow;
            route.FollowTargetName = lines[2].Trim();
            if (lines.Count > 3 && uint.TryParse(lines[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint fid))
                route.FollowTargetId = fid;
            return route;
        }

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
                // Z is in nav units (metres / 240). Waypoints added from the UI before
                // 2026-09-29 stored metres, so they drew hundreds of metres up; no real
                // point is 5 units (1.2 km) high, so anything past that is converted.
                if (Math.Abs(pt.Z) > 5.0) pt.Z /= 240.0;
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
                    case NavPointType.Jump:
                        pt.JumpHeading = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.JumpShift   = lines[idx++].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        pt.JumpMs      = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
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
        // A .nav marked read-only can't be overwritten: clear that first (no dependencies here:
        // this file is linked into RynthNav and test tools; RynthAi reports failures itself).
        try
        {
            if (File.Exists(filePath))
            {
                FileAttributes attrs = File.GetAttributes(filePath);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch { /* the write below fails and the caller reports why */ }

        using var writer = new StreamWriter(filePath, false);
        WriteTo(writer);
    }

    /// <summary>The route as uTank2 nav lines, the form a meta's embedded navs are kept in.</summary>
    public List<string> ToLines()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteTo(writer);
        var lines = new List<string>(writer.ToString().Split('\n'));
        for (int i = 0; i < lines.Count; i++) lines[i] = lines[i].TrimEnd('\r');
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private void WriteTo(TextWriter writer)
    {
        writer.WriteLine("uTank2 NAV 1.2");
        writer.WriteLine((int)RouteType);
        if (RouteType == NavRouteType.Follow && FollowTargetName.Length > 0)
        {
            writer.WriteLine(FollowTargetName);
            writer.WriteLine(FollowTargetId.ToString(CultureInfo.InvariantCulture));
            return;
        }
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
                case NavPointType.Jump:
                    writer.WriteLine(point.JumpHeading.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.JumpShift ? "True" : "False");
                    writer.WriteLine(point.JumpMs.ToString(CultureInfo.InvariantCulture));
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
