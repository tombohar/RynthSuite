using System;
using System.Collections.Generic;
using System.Globalization;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Avoidance settings, one per kind of thing the walk keeps away from, each on/off with a distance.
// Saved in rynthnav.json (beside navDataDir and the portals/recalls switches); /rnav avoid changes them.
//   portals: keep this many metres from every portal that isn't the step's own (default 4).
//   walls:   push each path corner this many metres away from the building or wall it turns round (default 1).
//   corners: a path corner counts as reached this close (default 1.5; the old 7 cut every corner).
public sealed partial class RynthNavPlugin
{
    private bool _avoidPortals = true;
    private bool _avoidWalls = true;
    private double _wallClearance = 1.0;
    private double _cornerReach = 1.5;
    private string _builtAvoidKey = "";
    private bool _preferAphus = true;      // the Town Network only by Recall Aphus Lassel when it's known (rynthnav.json preferAphus)   // the status JSON's last avoid settings (rebuild when they change)

    private void LoadAvoidSettings()
    {
        _avoidPortals = NavDataConfig.ReadSwitch("avoidPortals", true);
        PortalAvoid.LiveRadius = Math.Clamp(NavDataConfig.ReadNumber("avoidPortalMetres", 4.0), 1.0, 15.0);
        _avoidWalls = NavDataConfig.ReadSwitch("avoidWalls", true);
        _wallClearance = Math.Clamp(NavDataConfig.ReadNumber("avoidWallMetres", 1.0), 0.0, 5.0);
        _cornerReach = Math.Clamp(NavDataConfig.ReadNumber("cornerReachMetres", 1.5), 0.5, 7.0);
        _preferAphus = NavDataConfig.ReadSwitch("preferAphus", true);
    }

    private string AvoidSummary() =>
        $"avoid: portals {(_avoidPortals ? $"on, {PortalAvoid.LiveRadius:0.#} m" : "off")}; " +
        $"walls {(_avoidWalls ? $"on, corners {_wallClearance:0.#} m out" : "off")}; corners reached at {_cornerReach:0.#} m";

    /// <summary>
    /// The Town Network entered only by Recall Aphus Lassel, whose landing is a few yards from a
    /// network portal, when that recall is known: town network portals stand among other portals
    /// and are harder to take (Tom's call, 10-01). Without the recall, every entry stays.
    /// </summary>
    private static HubLinks ViaAphus(HubLinks hub, List<RecallOption> recalls, List<string> notes)
    {
        RecallOption? aphus = recalls.Find(r => r.Name.IndexOf("Aphus", StringComparison.OrdinalIgnoreCase) >= 0);
        if (aphus == null) return hub;
        var only = new HubLinks { Name = hub.Name, Arrivals = hub.Arrivals };
        foreach (HubEntry e in hub.Entries)
            if (Math.Abs(e.SrcNs - aphus.Ns) < 0.3 && Math.Abs(e.SrcEw - aphus.Ew) < 0.3) only.Entries.Add(e);
        if (only.Entries.Count == 0) return hub;   // no entry beside the landing: keep them all
        only.Exits.AddRange(hub.Exits);
        notes.Add($"Town Network: entered by {aphus.Name} only (prefer Aphus)");
        return only;
    }

    /// <summary>/rnav aphus on|off: the Town Network only by Recall Aphus Lassel when it's known.</summary>
    private void CmdAphus(string rest)
    {
        if (rest is "on" or "off") { _preferAphus = rest == "on"; NavDataConfig.WriteSwitch("preferAphus", _preferAphus); }
        Say($"Town Network by Recall Aphus Lassel: {(_preferAphus ? "on (when you know it)" : "off (the nearest network portal)")}");
    }

    /// <summary>/rnav avoid [portals on|off|&lt;m&gt; | walls on|off|&lt;m&gt; | corners &lt;m&gt;]</summary>
    private void CmdAvoid(string rest)
    {
        string[] a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length < 2) { Say(AvoidSummary() + " — /rnav avoid portals on|off|<m>, walls on|off|<m>, corners <m>"); return; }
        string what = a[0].ToLowerInvariant(), val = a[1].ToLowerInvariant();
        bool isNum = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double m);
        switch (what)
        {
            case "portals":
                if (val is "on" or "off") { _avoidPortals = val == "on"; NavDataConfig.WriteSwitch("avoidPortals", _avoidPortals); }
                else if (isNum) { PortalAvoid.LiveRadius = Math.Clamp(m, 1.0, 15.0); NavDataConfig.WriteNumber("avoidPortalMetres", PortalAvoid.LiveRadius); _avoidPortals = true; NavDataConfig.WriteSwitch("avoidPortals", true); }
                else { Say("/rnav avoid portals on|off|<metres>"); return; }
                _avoidAtlas = null; _nextAvoidMs = 0;   // rebuild the circles at the new size
                break;
            case "walls":
                if (val is "on" or "off") { _avoidWalls = val == "on"; NavDataConfig.WriteSwitch("avoidWalls", _avoidWalls); }
                else if (isNum) { _wallClearance = Math.Clamp(m, 0.0, 5.0); NavDataConfig.WriteNumber("avoidWallMetres", _wallClearance); _avoidWalls = true; NavDataConfig.WriteSwitch("avoidWalls", true); }
                else { Say("/rnav avoid walls on|off|<metres>"); return; }
                break;
            case "corners":
                if (!isNum) { Say("/rnav avoid corners <metres>"); return; }
                _cornerReach = Math.Clamp(m, 0.5, 7.0); NavDataConfig.WriteNumber("cornerReachMetres", _cornerReach);
                break;
            default:
                Say("/rnav avoid portals on|off|<m>, walls on|off|<m>, corners <m>"); return;
        }
        lock (_gotoGate) _gotoPath = null;   // re-plan with the new settings
        Say(AvoidSummary());
    }
}
