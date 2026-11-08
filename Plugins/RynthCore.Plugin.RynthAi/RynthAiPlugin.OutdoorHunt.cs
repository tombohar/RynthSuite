using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;
using Ui = RynthCore.Plugin.Shared.RynthUi.RynthUi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Outdoor hunting (OutdoorHunt, 2026-10-23): /ra hunt, the dashboard's Hunt button and Settings >
/// Navigation > Outdoor Hunt. Each walk is a Once route along RynthNav's path (RynthNav.Path v2:
/// portals and walls kept clear of), walked by the navigation engine like any route, with its stuck
/// recovery and doors; combat, looting and buffing take the character over as usual, and the hunt is
/// asked again only when the arbiter's last decision was navigation or nothing. Outdoors and in the
/// buildings outdoors (2026-10-25: BuildingMap, in through a door and out again); the Dungeon Patrol
/// hunts in dungeons, and starting one stops the other.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private readonly OutdoorHunt _hunt = new();
    private readonly List<OutdoorHunt.Candidate> _huntCandidates = new();
    private NavRouteParser? _huntRoute;      // the route the hunt installed; another one loaded ends the hunt
    private NavRecoveryPlanner? _huntDungeonCheck;
    private BuildingMap? _huntBuildings;
    private long _huntPortalsAt;
    private string _huntNoteLogged = "";
    private const int HuntMaxCorners = 128;
    private const int HuntPathFlags = RynthNavPathApiV1.PathAvoidPortals | RynthNavPathApiV1.PathWallClearance
                                    | RynthNavPathApiV1.PathAllowPartial | RynthNavPathApiV1.PathQuiet;
    private const uint HuntItemTypePortal = 0x00010000;
    private const long HuntPortalRefreshMs = 1000;

    internal bool HuntActive => _hunt.Active;

    private void HandleHuntCommand(string[] parts)
    {
        // parts[0] = "ra", parts[1] = "hunt", parts[2] = what (before 10-23 evening this read parts[1],
        // so "/ra hunt status" and "/ra hunt stop" toggled the hunt instead).
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        switch (arg)
        {
            case "start": case "on": StartHunt(); break;
            case "stop": case "off": StopHunt("stopped", say: true); break;
            case "status":
                ChatLine(_hunt.Active
                    ? $"[RynthAi] Outdoor hunt: on, {(_hunt.WalkingOut ? "walking back out of a building" : _hunt.TargetId != 0 ? $"walking to 0x{(uint)_hunt.TargetId:X8}" : "waiting")}. {_hunt.Note}"
                    : "[RynthAi] Outdoor hunt: off. /ra hunt to start it here.");
                if (_dashboard?.Settings is { } st) ChatLine("[RynthAi] " + HuntSettingsText(st));
                break;
            // Typed by hand, so they answer (/mt opt set outdoorhunt... is silent: metas send it all the time).
            case "range":
            case "radius":
                SetHuntSetting(arg, parts.Length > 3 ? parts[3] : "");
                break;
            case "pick":
                SetHuntPick(parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3) : "");
                break;
            case "panel":
            case "settings":
                ToggleHuntPanel();
                break;
            case "":
                if (_hunt.Active) StopHunt("stopped", say: true); else StartHunt();
                break;
            default:
                ChatLine("[RynthAi] /ra hunt [start|stop|status|panel] | /ra hunt range <yd> | /ra hunt radius <yd, 0 = anywhere> | " + HuntPickUsage);
                break;
        }
    }

    internal const string HuntPickUsage = "/ra hunt pick [nearest|pack|xp]";

    /// <summary>The setting's words (OutdoorHuntPick 0 / 1 / 2): the short name, and what it does.</summary>
    internal static (string Name, string Does) HuntPickWords(int pick) => Math.Clamp(pick, 0, 2) switch
    {
        1 => ("Biggest pack", "goes for the biggest pack of monsters (a near pack wins unless a farther one is clearly bigger)"),
        2 => ("Most XP", "goes for the pack worth the most experience (the XP per kill it has learned; by size until it knows)"),
        _ => ("Nearest", "goes for the nearest monster (the shortest walk)"),
    };

    /// <summary>"/ra hunt pick [nearest|pack|xp]": no word says the choice now and the usage; a word sets it and says so.</summary>
    private void SetHuntPick(string value)
    {
        var s = _dashboard?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string v = value.Trim().ToLowerInvariant();
        if (v.Length == 0)
        {
            var (name, does) = HuntPickWords(s.OutdoorHuntPick);
            ReplyOk($"[RynthAi] Outdoor hunt pick: {name}, it {does}. {HuntPickUsage} to change it.");
            return;
        }
        int? pick = v switch
        {
            "nearest" or "near" or "closest" or "0" => 0,
            "pack" or "biggest" or "biggest pack" or "dense" or "densest" or "most dense" or "1" => 1,
            "xp" or "exp" or "experience" or "most xp" or "highest experience" or "2" => 2,
            _ => null,
        };
        if (pick is not int p)
        {
            ReplyFail($"[RynthAi] Outdoor hunt pick: '{value.Trim()}' isn't one of nearest, pack or xp (it is {HuntPickWords(s.OutdoorHuntPick).Name} now). Usage: {HuntPickUsage}");
            return;
        }
        s.OutdoorHuntPick = p;
        var (n, d) = HuntPickWords(p);
        ReplyOk($"[RynthAi] Outdoor hunt pick set: {n}, it {d}." + (_hunt.Active ? " In use from the next monster." : ""));
    }

    private void SetHuntSetting(string what, string value)
    {
        var s = _dashboard?.Settings;
        if (s == null) { ChatLine("[RynthAi] Settings not ready."); return; }
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double yd))
        {
            ChatLine("[RynthAi] Outdoor hunt: " + HuntSettingsText(s) + $" /ra hunt {what} <yd> to change it.");
            return;
        }
        double max = what == "range" ? OutdoorHuntRangeMax : OutdoorHuntRadiusMax;
        double set = Math.Clamp(yd, what == "range" ? OutdoorHuntRangeMin : 0, max);
        if (what == "range") s.OutdoorHuntRange = (float)set; else s.OutdoorHuntRadius = (float)set;
        string capped = set != yd ? $" ({yd:0} is outside {(what == "range" ? OutdoorHuntRangeMin : 0):0}-{max:0})" : "";
        ChatLine("[RynthAi] Outdoor hunt set" + capped + ": " + HuntSettingsText(s) + (_hunt.Active ? " In use now." : ""));
    }

    /// <summary>The two distances, each with what it does (Tom set Hunt Radius 2000 and saw no farther hunting: that one is the roaming limit).</summary>
    private static string HuntSettingsText(LegacyUiSettings s) =>
        $"looks for monsters out to {s.OutdoorHuntRange:0} yd (Hunt Range), "
        + (s.OutdoorHuntRadius > 0 ? $"and only within {s.OutdoorHuntRadius:0} yd of where it started (Hunt Radius)." : "anywhere it goes (Hunt Radius 0).")
        + $" Pick: {HuntPickWords(s.OutdoorHuntPick).Name}.";

    // ── The Outdoor Hunt window (Tom 10-23: "we need a small panel to change the settings") ──
    // Right-click the dashboard's Hunt button, or /ra hunt panel. Render thread: the sliders write the
    // two settings straight into the live settings (as the other RynthAi windows do; the autosave
    // picks them up), Start/Stop goes to the plugin tick.
    private volatile bool _huntPanelOpen;

    public void ToggleHuntPanel() => _huntPanelOpen = !_huntPanelOpen;

    private void RenderHuntPanel()
    {
        if (!_huntPanelOpen) return;
        var s = _dashboard?.Settings;
        if (s == null) return;
        // Resizable (the owner 11-01: "very small, hard to read, no way to resize it"): RynthUi's
        // colours and 14 pt text, a remembered size, text that wraps to the width, full-width sliders.
        float k = Math.Max(0.5f, Ui.Font(RynthUiFont.Ui14).FontSize / 14f);
        ImGui.SetNextWindowSize(new Vector2(460 * k, 360 * k), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(320 * k, 240 * k), new Vector2(1600 * k, 1200 * k));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Ui.WithAlpha(Ui.Background, 0xF2));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Ui.Color(RynthUiColor.TitleBar));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Ui.Color(RynthUiColor.TitleBar));
        ImGui.PushStyleColor(ImGuiCol.Border, Ui.Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, Ui.Text);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12 * k, 10 * k));
        bool open = true;
        bool visible = ImGui.Begin("Outdoor Hunt##rynthhunt", ref open, ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        if (!open) _huntPanelOpen = false;
        if (!visible) { ImGui.End(); return; }
        try
        {
            using (Ui.PushFont(RynthUiFont.Ui14))
                HuntPanelBody(s, k);
        }
        finally { ImGui.End(); }
    }

    private void HuntPanelBody(LegacyUiSettings s, float k)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8 * k, 5 * k));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8 * k, 6 * k));
        try
        {
            bool on = _hunt.Active;
            if (ImGui.Button(on ? "Stop hunting" : "Start hunting here", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                QueueDashAction(ToggleHunt);

            string status = !on ? "Off."
                : _hunt.WalkingOut ? "Walking back out of a building."
                : _hunt.TargetId != 0 ? "Walking to a monster."
                : "Waiting for a monster in range.";
            using (Ui.PushFont(RynthUiFont.UiBold14))
                Wrapped(status, !on ? Ui.Mute : _hunt.TargetId != 0 || _hunt.WalkingOut ? Ui.Green : Ui.Highlight);
            string note = _hunt.Note;
            if (on && note.Length > 0) Wrapped(note, Ui.Text);
            ImGui.Separator();

            float range = s.OutdoorHuntRange;
            if (HuntSlider("Hunt Range", "##huntrange", ref range, (float)OutdoorHuntRangeMin, (float)OutdoorHuntRangeMax,
                    "%.0f yd", "Max", (float)OutdoorHuntRangeMax, k))
                s.OutdoorHuntRange = range;
            Wrapped("How far it goes for the next monster. It only knows the ones the game has shown you "
                + "(roughly the landblocks round you), so past a few hundred yards it means all of them. "
                + "Monsters in buildings too: in through a door, out by the nearest one.", Ui.Text);
            ImGui.Spacing();

            float radius = s.OutdoorHuntRadius;
            if (HuntSlider("Hunt Radius", "##huntradius", ref radius, 0f, (float)OutdoorHuntRadiusMax,
                    radius < 1f ? "anywhere" : "%.0f yd", "Anywhere", 0f, k))
                s.OutdoorHuntRadius = radius < 1f ? 0f : radius;
            Wrapped("How far from where it started it may roam. Anywhere: no limit.", Ui.Text);
            ImGui.Spacing();

            // Go for (2026-11-08): Nearest, Biggest pack or Most XP (OutdoorHuntPick).
            using (Ui.PushFont(RynthUiFont.UiBold14)) ImGui.TextUnformatted("Go for");
            int pick = Math.Clamp(s.OutdoorHuntPick, 0, 2);
            float third = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3f;
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine();
                bool chosen = pick == i;
                if (chosen) ImGui.PushStyleColor(ImGuiCol.Button, Ui.Accent);
                if (ImGui.Button(HuntPickWords(i).Name + "##huntpick" + i, new Vector2(Math.Max(40 * k, third), 0))) s.OutdoorHuntPick = i;
                if (chosen) ImGui.PopStyleColor();
            }
            Wrapped("It " + HuntPickWords(pick).Does + ".", Ui.Text);
            ImGui.Spacing();
            Wrapped("Ctrl+click a slider to type a number.", Ui.Mute);
        }
        finally { ImGui.PopStyleVar(2); }
    }

    /// <summary>A label, then a slider across the window with one preset button at its end. True when the value changed.</summary>
    private static bool HuntSlider(string label, string id, ref float value, float min, float max, string format,
        string presetLabel, float preset, float k)
    {
        using (Ui.PushFont(RynthUiFont.UiBold14)) ImGui.TextUnformatted(label);
        float presetW = ImGui.CalcTextSize(presetLabel).X + 20 * k;
        ImGui.SetNextItemWidth(Math.Max(80 * k, ImGui.GetContentRegionAvail().X - presetW - ImGui.GetStyle().ItemSpacing.X));
        bool changed = ImGui.SliderFloat(id, ref value, min, max, format, ImGuiSliderFlags.Logarithmic);
        ImGui.SameLine();
        if (ImGui.Button(presetLabel + id + "_preset", new Vector2(presetW, 0))) { value = preset; changed = true; }
        return changed;
    }

    private static void Wrapped(string text, uint color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>The dashboard's Hunt button (on the plugin tick).</summary>
    public void ToggleHunt()
    {
        if (_hunt.Active) StopHunt("stopped", say: true);
        else StartHunt();
    }

    internal void StartHunt()
    {
        var s = _dashboard?.Settings;
        if (s == null) { ChatLine("[RynthAi] Settings not ready."); return; }
        if (!TryHuntPosition(out uint cell, out var me)) { ChatLine("[RynthAi] Outdoor hunt: no position yet."); return; }
        if (InDungeon(cell)) { ChatLine("[RynthAi] Outdoor hunt is for outdoors; in a dungeon the Dungeon Patrol hunts (Patrol)."); return; }
        // In a building: it can start when it knows the building's doors (and RynthNav plans from a doorstep).
        if (OutdoorHunt.IsIndoorCell(cell) && !HuntKnowsBuilding(cell))
        {
            ChatLine("[RynthAi] Outdoor hunt: start it outdoors (it doesn't know this building's doors; it finds its own way out of a building it went into).");
            return;
        }
        if (HuntPath(me, me).Status == OutdoorHunt.PathStatus.Unavailable)
        {
            ChatLine("[RynthAi] Outdoor hunt needs RynthNav 2026.10.23 or newer (its map shows the way round obstacles).");
            return;
        }
        if (!s.IsMacroRunning && RefuseMacroStartForVTank()) return;   // one bot per client (Decal bridge)

        _dunPatrolActive = false;   // the hunt's route replaces the patrol's
        CopyHuntSettings(s);
        _hunt.Start(RynthClock.Now, me);
        _huntRoute = new NavRouteParser { RouteType = NavRouteType.Once, OutdoorHuntWalk = true };
        s.CurrentNavPath = string.Empty;
        s.CurrentRoute = _huntRoute;
        s.ActiveNavIndex = 0;
        s.EnableNavigation = true;
        if (!s.IsMacroRunning)
        {
            s.IsMacroRunning = true;
            _macroWasRunning = true;   // no macro-start "nearest waypoint" pick: the hunt plans its own walks
        }
        ChatLine("[RynthAi] Outdoor hunt on: " + HuntSettingsText(s) + " /ra hunt range <yd> to change.");
    }

    private void StopHunt(string why, bool say = false)
    {
        if (!_hunt.Active) { if (say) ChatLine("[RynthAi] Outdoor hunt is off."); return; }
        _hunt.Stop();
        var s = _dashboard?.Settings;
        if (s != null && ReferenceEquals(s.CurrentRoute, _huntRoute))
        {
            s.CurrentRoute = new NavRouteParser();
            s.ActiveNavIndex = 0;
            _navigationEngine?.Stop();
        }
        _huntRoute = null;
        Host.Log($"[RynthAi] Hunt: off ({why})");
        if (say) ChatLine($"[RynthAi] Outdoor hunt off ({why}).");
    }

    /// <summary>Each plugin tick while logged in: the hunt's next walk, when nothing else wants the character.</summary>
    private void TickOutdoorHunt()
    {
        if (!_hunt.Active) return;
        var s = _dashboard?.Settings;
        if (s == null) return;
        if (!s.IsMacroRunning) { StopHunt("the macro stopped"); return; }
        if (!ReferenceEquals(s.CurrentRoute, _huntRoute) || s.CurrentNavPath.Length > 0)
        {
            StopHunt("another route was loaded", say: true);
            return;
        }
        if (!TryHuntPosition(out uint cell, out var me)) return;
        if (InDungeon(cell)) { StopHunt("went into a dungeon", say: true); return; }
        // A building's rooms (InDungeon said it isn't a dungeon): off RynthNav's landscape map. The way in
        // is kept on every tick, a fight's chase included, for the walk back out of a building whose
        // doors aren't known.
        bool indoors = OutdoorHunt.IsIndoorCell(cell);
        _hunt.Track(me, indoors);
        // Only when the arbiter's last decision left the character free (a fight, loot, buffs, salvage come first).
        if (_activity != BotActivity.Navigating && _activity != BotActivity.Idle) return;

        CopyHuntSettings(s);
        var route = s.CurrentRoute;
        bool routeDone = route.Points.Count == 0 || !s.EnableNavigation || s.ActiveNavIndex >= route.Points.Count;
        // The buildings round it (cell.dat): into them through their doors, out of the one it is in.
        // From inside, RynthNav must plan from a doorstep (RynthNav.Path v3); an older one: the way back out.
        var buildings = HuntBuildings();
        if (buildings != null) RefreshHuntPortals(buildings);
        OutdoorHunt.Decision d;
        if (indoors && (buildings == null || HuntNavVersion() < 3))
            d = _hunt.Indoors(RynthClock.Now, me, routeDone);
        else
        {
            // Out to twice Hunt Range: new picks stay inside it, the one being walked to is kept (OutdoorHunt.KeepFactor).
            _combatManager?.HuntCandidates(_hunt.RangeYd * OutdoorHunt.KeepFactor, _huntCandidates);
            d = _hunt.Tick(RynthClock.Now, me, cell, routeDone, _huntCandidates, HuntPath, buildings);
        }
        foreach (string note in _hunt.PendingNotes)
        {
            if (note == _huntNoteLogged) continue;
            _huntNoteLogged = note;
            Host.Log("[RynthAi] Hunt: " + note);
        }
        _hunt.ClearNotes();
        switch (d.Act)
        {
            case OutdoorHunt.Act.Walk when d.Corners != null:
                InstallHuntRoute(s, d.Corners);
                break;
            case OutdoorHunt.Act.Idle when route.Points.Count > 0:
                InstallHuntRoute(s, Array.Empty<OutdoorHunt.Vec>());
                break;
        }
    }

    private void InstallHuntRoute(LegacyUiSettings s, IReadOnlyList<OutdoorHunt.Vec> corners)
    {
        // Marked as the hunt's walk: Boost Nav Priority does not count it as a nav route (BoostNavRule).
        var route = new NavRouteParser { RouteType = NavRouteType.Once, OutdoorHuntWalk = true };
        foreach (var c in corners)
        {
            route.Points.Add(new NavPoint
            {
                Type = NavPointType.Point,
                EW = (c.X / 24.0 - 1019.5) / 10.0,
                NS = (c.Y / 24.0 - 1019.5) / 10.0,
                Z = c.Z / 240.0,
                Doorway = c.Door,   // a building's door or a doorway inside: reached tightly, walked through straight
            });
        }
        _huntRoute = route;
        s.CurrentNavPath = string.Empty;
        s.CurrentRoute = route;
        s.ActiveNavIndex = 0;
        if (route.Points.Count > 0) s.EnableNavigation = true;
    }

    private void CopyHuntSettings(LegacyUiSettings s)
    {
        _hunt.RangeYd = Math.Clamp(s.OutdoorHuntRange, OutdoorHuntRangeMin, OutdoorHuntRangeMax);
        _hunt.RadiusYd = Math.Clamp(s.OutdoorHuntRadius, 0, OutdoorHuntRadiusMax);
        _hunt.Pick = (OutdoorHunt.PickMode)Math.Clamp(s.OutdoorHuntPick, 0, 2);
        _hunt.XpLookup ??= XpPerKill;   // the learned XP per kill (MonsterXpTable), for Most XP
    }

    // Range up to 2000 (Tom 10-23): past what the client knows of (the landblocks round you) it means "every monster in sight".
    internal const double OutdoorHuntRangeMin = 10, OutdoorHuntRangeMax = 2000, OutdoorHuntRadiusMax = 2000;

    /// <summary>The character's cell and world position (x = east, y = north, as RynthNav's map).</summary>
    private bool TryHuntPosition(out uint cell, out OutdoorHunt.Vec at)
    {
        at = default;
        cell = 0;
        if (!Host.HasGetPlayerPose || !Host.TryGetPlayerPose(out cell, out float lx, out float ly, out float lz, out _, out _, out _, out _))
            return false;
        at = new OutdoorHunt.Vec(((cell >> 24) & 0xFF) * 192.0 + lx, ((cell >> 16) & 0xFF) * 192.0 + ly, lz);
        return true;
    }

    /// <summary>In a dungeon (not a building's rooms, which a walk outdoors may pass through).</summary>
    private bool InDungeon(uint cell)
    {
        if ((cell & 0xFFFF) < 0x0100) return false;
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null || !cellDat.IsLoaded) return true;   // can't tell: an indoor cell counts as a dungeon
        _huntDungeonCheck ??= new NavRecoveryPlanner(Host, () => _raycast, () => _objectCache?.GetHazardCells());
        return _huntDungeonCheck.IsDungeonLandblock(cell >> 16, cellDat);
    }

    /// <summary>The building map on the client's cell.dat (null until the dats are open).</summary>
    private BuildingMap? HuntBuildings()
    {
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null || !cellDat.IsLoaded) return null;
        if (_huntBuildings == null || !ReferenceEquals(_huntBuildings.CellDat, cellDat))
        {
            _huntDungeonCheck ??= new NavRecoveryPlanner(Host, () => _raycast, () => _objectCache?.GetHazardCells());
            var check = _huntDungeonCheck;
            _huntBuildings = new BuildingMap(cellDat, () => _raycast?.GeometryLoader?.DungeonLOS, lb => check.IsDungeonLandblock(lb, cellDat));
        }
        return _huntBuildings;
    }

    /// <summary>The character's building is known, with a door, and RynthNav plans from a doorstep.</summary>
    private bool HuntKnowsBuilding(uint cell)
    {
        var map = HuntBuildings();
        if (map == null || HuntNavVersion() < 3) return false;
        int b = map.BuildingOf(cell);
        return b != 0 && map.Doors(b).Count > 0;
    }

    /// <summary>
    /// The portal objects the client knows (static and summoned: object class Portal, or an unclassified
    /// object whose item type says portal), for the walks inside buildings to keep out of and the doors
    /// to drop (a portal by a door's step out: RynthNav's way there may end in its circle, since its
    /// goal is always allowed); between doors RynthNav keeps out of portals itself. Read again at most
    /// once a second.
    /// </summary>
    private void RefreshHuntPortals(BuildingMap map)
    {
        long now = Environment.TickCount64;
        if (now < _huntPortalsAt) return;
        _huntPortalsAt = now + HuntPortalRefreshMs;
        var list = new List<BuildingMap.PortalSpot>();
        if (_objectCache != null && Host.HasGetObjectPosition)
        {
            foreach (var wo in _objectCache.GetLandscapeObjects())
            {
                uint uid = unchecked((uint)wo.Id);
                bool portal = wo.ObjectClass == AcObjectClass.Portal
                    || (wo.ObjectClass == AcObjectClass.Unknown && Host.TryGetItemType(uid, out uint type) && (type & HuntItemTypePortal) != 0);
                if (!portal) continue;
                if (!Host.TryGetObjectPosition(uid, out uint cell, out float x, out float y, out float z) || cell == 0) continue;
                list.Add(new BuildingMap.PortalSpot(uid, ((cell >> 24) & 0xFF) * 192.0 + x, ((cell >> 16) & 0xFF) * 192.0 + y, z));
            }
        }
        list.Sort((a, b) => a.Id.CompareTo(b.Id));   // the same portals in the same order: the walks worked out stay
        map.Portals = list;
    }

    /// <summary>The "RynthNav.Path" version RynthNav serves (0: not loaded). 3 plans from a building's doorstep while inside.</summary>
    private uint HuntNavVersion()
    {
        if (!Host.HasGetPluginInterface) return 0;
        unsafe
        {
            var t = (RynthNavPathApiV1*)Host.GetPluginInterface(RynthNavPathApiV1.PluginName, RynthNavPathApiV1.InterfaceName, 2);
            return t == null ? 0 : t->Version;
        }
    }

    /// <summary>RynthNav's way from <paramref name="a"/> to <paramref name="b"/> (RynthNav.Path v2), the start left off.
    /// From v3 every start counts as on the landscape (the hunt plans from doorsteps, never from inside a building).</summary>
    private OutdoorHunt.PathResult HuntPath(OutdoorHunt.Vec a, OutdoorHunt.Vec b)
    {
        var none = new OutdoorHunt.PathResult(OutdoorHunt.PathStatus.Unavailable, Array.Empty<OutdoorHunt.Vec>(), 0, 0);
        if (!Host.HasGetPluginInterface) return none;
        double[] buf = new double[HuntMaxCorners * 3];
        double[] info = new double[RynthNavPathApiV1.InfoLength];
        int count = 0;
        int result;
        unsafe
        {
            // Resolved on every call: RynthNav may be missing, or reloaded since last time.
            IntPtr p = Host.GetPluginInterface(RynthNavPathApiV1.PluginName, RynthNavPathApiV1.InterfaceName, 2);
            var t = (RynthNavPathApiV1*)p;
            if (t == null || t->Version < 2 || t->StructSize < (uint)sizeof(RynthNavPathApiV1) || t->FindPathEx == null)
                return none;
            int flags = HuntPathFlags | (t->Version >= 3 ? RynthNavPathApiV1.PathStartOutdoors : 0);
            fixed (double* pts = buf)
            fixed (double* inf = info)
                result = t->FindPathEx(a.X, a.Y, a.Z, b.X, b.Y, b.Z, flags, 0, pts, HuntMaxCorners, &count, inf);
        }
        var status = result switch
        {
            RynthNavPathApiV1.ResultOk => OutdoorHunt.PathStatus.Ok,
            RynthNavPathApiV1.ResultPartial => OutdoorHunt.PathStatus.Partial,
            RynthNavPathApiV1.ResultNoMesh => OutdoorHunt.PathStatus.NoMesh,
            _ => OutdoorHunt.PathStatus.NoPath,
        };
        var corners = new List<OutdoorHunt.Vec>(Math.Max(0, count - 1));
        for (int i = 1; i < count; i++)   // corner 0 is the start
            corners.Add(new OutdoorHunt.Vec(buf[i * 3], buf[i * 3 + 1], buf[i * 3 + 2]));
        return new OutdoorHunt.PathResult(status, corners, info[0], info[5]);
    }
}
