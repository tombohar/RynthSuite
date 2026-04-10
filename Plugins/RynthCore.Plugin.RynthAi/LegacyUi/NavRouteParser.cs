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

public enum NavPointType
{
    Point = 0,
    Recall = 2,
    Pause = 3,
    Chat = 4,
    PortalNPC = 6
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
    public string TargetName { get; set; } = string.Empty;
    public int ObjectClass { get; set; }
    public bool IsTie { get; set; }

    public double PortalExitNS { get; set; }
    public double PortalExitEW { get; set; }
    public double PortalExitZ { get; set; }
    public double PortalLandNS { get; set; }
    public double PortalLandEW { get; set; }
    public double PortalLandZ { get; set; }

    public override string ToString()
    {
        return Type switch
        {
            NavPointType.Point => $"[Point] {NS:F3}, {EW:F3}",
            NavPointType.Recall => $"[Recall] Spell {SpellId} ({SpellId})",
            NavPointType.Pause => $"[Pause] {PauseTimeMs / 1000.0:F1}s",
            NavPointType.Chat => $"[Chat] {ChatCommand}",
            NavPointType.PortalNPC => $"[Portal] {TargetName}",
            _ => $"[Unknown] {Type}"
        };
    }
}

public sealed class NavRouteParser
{
    public NavRouteType RouteType { get; set; }
    public List<NavPoint> Points { get; set; } = new();

    public static NavRouteParser Load(string filePath)
    {
        var route = new NavRouteParser();
        if (!File.Exists(filePath))
            return route;

        string[] lines = File.ReadAllLines(filePath);
        if (lines.Length < 3 || !lines[0].Contains("uTank2 NAV 1.2", StringComparison.OrdinalIgnoreCase))
            return route;

        route.RouteType = (NavRouteType)int.Parse(lines[1], CultureInfo.InvariantCulture);
        int pointCount = int.Parse(lines[2], CultureInfo.InvariantCulture);

        int idx = 3;
        for (int i = 0; i < pointCount && idx < lines.Length; i++)
        {
            var pt = new NavPoint
            {
                Type = (NavPointType)int.Parse(lines[idx++], CultureInfo.InvariantCulture),
                EW = double.Parse(lines[idx++], CultureInfo.InvariantCulture),
                NS = double.Parse(lines[idx++], CultureInfo.InvariantCulture),
                Z = double.Parse(lines[idx++], CultureInfo.InvariantCulture)
            };

            idx++;

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
                case NavPointType.PortalNPC:
                    pt.TargetName = lines[idx++];
                    pt.ObjectClass = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.IsTie = bool.Parse(lines[idx++]);
                    pt.PortalExitEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalExitNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalExitZ = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    idx++;
                    pt.PortalLandEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalLandNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalLandZ = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    idx++;
                    break;
            }

            route.Points.Add(pt);
        }

        return route;
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
                case NavPointType.PortalNPC:
                    writer.WriteLine(point.TargetName);
                    writer.WriteLine(point.ObjectClass);
                    writer.WriteLine(point.IsTie);
                    writer.WriteLine(point.PortalExitEW.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.PortalExitNS.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.PortalExitZ.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("0");
                    writer.WriteLine(point.PortalLandEW.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.PortalLandNS.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(point.PortalLandZ.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("0");
                    break;
            }
        }
    }
}
