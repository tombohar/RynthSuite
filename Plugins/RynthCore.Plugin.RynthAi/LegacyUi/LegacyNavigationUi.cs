using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyNavigationUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;

    public Action? OnSettingsChanged;

    private readonly string[] _routeTypes = { "Once", "Circular", "Linear", "Follow" };
    private readonly string[] _addModes = { "End", "Above", "Below" };
    private int _addModeIdx = 0;
    private int _selectedRouteIndex = -1;

    public LegacyNavigationUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    public void Render()
    {
        ImGui.SetNextWindowSize(new Vector2(400, 480), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Navigation##RynthAiNav", ref DashWindows.ShowNavigation))
        {
            ImGui.End();
            return;
        }

        string activeNavName = string.IsNullOrEmpty(_settings.CurrentNavPath) ? "None (Unsaved)" : Path.GetFileName(_settings.CurrentNavPath);
        SafeImGui.TextColored(new Vector4(1, 1, 0, 1), $"Active Nav: {activeNavName}");

        if (!string.IsNullOrEmpty(_settings.NavStatusLine))
        {
            var statusColor = _settings.NavIsStuck
                ? new Vector4(0.91f, 0.70f, 0.20f, 1.00f)   // amber
                : new Vector4(0.25f, 0.85f, 0.45f, 1.00f);  // green
            SafeImGui.TextColored(statusColor, _settings.NavStatusLine);
        }

        ImGui.Spacing();
        bool navActive = _settings.IsMacroRunning && _settings.EnableNavigation;
        if (navActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.50f, 0.10f, 0.10f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.35f, 0.08f, 0.08f, 1f));
            if (ImGui.Button("Stop Navigation", new Vector2(-1, 28)))
                _settings.EnableNavigation = false;
            ImGui.PopStyleColor(3);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.10f, 0.38f, 0.10f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f, 0.55f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.08f, 0.28f, 0.08f, 1f));
            if (ImGui.Button("Start Navigation", new Vector2(-1, 28)))
            {
                _settings.RearmFinishedNavRoute();   // a finished Once route runs again
                _settings.IsMacroRunning  = true;
                _settings.EnableNavigation = true;
                if (_settings.BotAction != "Navigating")
                    _settings.BotAction = "Default";
            }
            ImGui.PopStyleColor(3);
        }

        ImGui.Separator();

        int rTypeIdx = 0;
        if (_settings.CurrentRoute.RouteType == NavRouteType.Circular) rTypeIdx = 1;
        else if (_settings.CurrentRoute.RouteType == NavRouteType.Linear) rTypeIdx = 2;
        else if (_settings.CurrentRoute.RouteType == NavRouteType.Follow) rTypeIdx = 3;

        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("Route Type", ref rTypeIdx, _routeTypes, _routeTypes.Length))
        {
            if (rTypeIdx == 0) _settings.CurrentRoute.RouteType = NavRouteType.Once;
            else if (rTypeIdx == 1) _settings.CurrentRoute.RouteType = NavRouteType.Circular;
            else if (rTypeIdx == 2) _settings.CurrentRoute.RouteType = NavRouteType.Linear;
            else if (rTypeIdx == 3) _settings.CurrentRoute.RouteType = NavRouteType.Follow;

            TryAutoSaveNav();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.Combo("Insert", ref _addModeIdx, _addModes, _addModes.Length);

        ImGui.Spacing();

        if (ImGui.Button("Add Waypoint", new Vector2(100, 25)))
        {
            // Stop any active turn motion so the character doesn't keep spinning
            _host.SetMotionBy("UI", 0x6500000D, false); // TurnRight
            _host.SetMotionBy("UI", 0x6500000E, false); // TurnLeft

            if (_host.HasGetPlayerPose && _host.TryGetPlayerPose(out _, out float x, out float y, out float z, out _, out _, out _, out _))
            {
                if (NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew))
                {
                    var newPt = new NavPoint
                    {
                        NS = ns,
                        EW = ew,
                                Z = z / 240.0
                    };
                    InsertPoint(newPt);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Add Portal", new Vector2(100, 25)))
        {
            _host.Log("Compat: Add Portal not yet implemented in RynthCore.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Add Recall", new Vector2(100, 25)))
        {
            ImGui.OpenPopup("RecallPopup");
        }

        if (ImGui.BeginPopup("RecallPopup"))
        {
            int[] ids = {
                48, 2645, 2647,
                1635, 1636,
                157, 158, 1637,
                2648, 2649, 2650,
                2931, 2023, 2041, 2358, 2813, 2941, 2943,
                3865, 3929, 3930, 4084, 4198, 4213,
                4907, 4908, 4909,
                5175, 5330, 5541, 6150, 6321, 6322
            };

            for (int ri = 0; ri < ids.Length; ri++)
            {
                string name = NavPoint.RecallName(ids[ri]);
                if (ImGui.Selectable($"{name} ({ids[ri]})"))
                {
                    if (_host.HasGetPlayerPose && _host.TryGetPlayerPose(out _, out float x, out float y, out float z, out _, out _, out _, out _))
                    {
                        if (NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew))
                        {
                            var newPt = new NavPoint
                            {
                                Type = NavPointType.Recall,
                                NS = ns,
                                EW = ew,
                                Z = z / 240.0,
                                SpellId = ids[ri]
                            };
                            InsertPoint(newPt);
                            _host.Log($"Added Recall: {name} ({ids[ri]})");
                        }
                    }
                }
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear Route", new Vector2(100, 25)))
        {
            _settings.CurrentRoute.Points.Clear();
            _settings.ActiveNavIndex = 0;
            TryAutoSaveNav();
            DisposeRouteGraphics();
            OnSettingsChanged?.Invoke();
        }

        ImGui.SameLine();
        if (ImGui.Button("Save Route", new Vector2(100, 25)))
        {
            _host.WriteToChat(SaveRoute(), 1);
            OnSettingsChanged?.Invoke();
        }

        if (ImGui.BeginListBox("##RoutePoints", new Vector2(-1, 200)))
        {
            for (int i = 0; i < _settings.CurrentRoute.Points.Count; i++)
            {
                ImGui.PushID($"route_pt_{i}");

                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                if (ImGui.Button("X", new Vector2(20, 20)))
                {
                    _settings.CurrentRoute.Points.RemoveAt(i);

                    if (_selectedRouteIndex == i) _selectedRouteIndex = -1;
                    else if (_selectedRouteIndex > i) _selectedRouteIndex--;

                    _settings.ActiveNavIndex = IndexAfterDelete(_settings.ActiveNavIndex, i, _settings.CurrentRoute.Points.Count);

                    TryAutoSaveNav();
                    UpdateRouteGraphics();
                    OnSettingsChanged?.Invoke();

                    ImGui.PopStyleColor();
                    ImGui.PopID();
                    break;
                }
                ImGui.PopStyleColor();

                ImGui.SameLine();

                string prefix = "   ";
                if (i == _settings.ActiveNavIndex) prefix = "==>";

                bool isSelected = (_selectedRouteIndex == i);
                if (ImGui.Selectable($"{prefix} [{i}] {_settings.CurrentRoute.Points[i]}", isSelected))
                {
                    _selectedRouteIndex = i;
                }

                ImGui.PopID();
            }
            ImGui.EndListBox();
        }

        ImGui.End();
    }

    public void InsertPoint(NavPoint newPt)
    {
        // The selection is a row of whatever route was showing when it was clicked; a
        // meta or /ra nav load since then can have swapped in a shorter one, and
        // Insert past the end threw in the middle of the window.
        if (_selectedRouteIndex >= _settings.CurrentRoute.Points.Count) _selectedRouteIndex = -1;

        if (_addModeIdx == 0 || _settings.CurrentRoute.Points.Count == 0)
            _settings.CurrentRoute.Points.Add(newPt);
        else if (_addModeIdx == 1 && _selectedRouteIndex >= 0)
            _settings.CurrentRoute.Points.Insert(_selectedRouteIndex, newPt);
        else if (_addModeIdx == 2 && _selectedRouteIndex >= 0)
            _settings.CurrentRoute.Points.Insert(_selectedRouteIndex + 1, newPt);
        else
            _settings.CurrentRoute.Points.Add(newPt);

        TryAutoSaveNav();
        UpdateRouteGraphics();
        OnSettingsChanged?.Invoke();
    }

    /// <summary>
    /// The active waypoint after deleting waypoint <paramref name="deleted"/> (the route now
    /// has <paramref name="newCount"/> points). Deleting the active one moves on to the one
    /// that followed it; this used to jump back to [0], which on a Once route re-ran its
    /// opening recall/portal and could walk the bot back across the map.
    /// </summary>
    internal static int IndexAfterDelete(int active, int deleted, int newCount)
    {
        if (newCount <= 0) return 0;
        if (active > deleted) active--;
        return Math.Clamp(active, 0, newCount - 1);
    }

    /// <summary>NavProfiles in the resolved RynthSuite folder. It was hard-coded to C:\Games, so with a
    /// moved install /ra nav save &lt;name&gt; wrote where the nav picker never looks (2026-10-13).</summary>
    public static string NavFolder => Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "NavProfiles");

    public void TryAutoSaveNav()
    {
        string path = _settings.CurrentNavPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (TryGetEmbeddedKey(path, out string key))
                _settings.EmbeddedNavs[key] = _settings.CurrentRoute.ToLines();
            else
            {
                NavRouteParser route = _settings.CurrentRoute;
                RynthCore.Plugin.Shared.DataWriteGuard.TrySave("the nav", path, () => route.Save(path));
            }
        }
        catch (Exception ex) { _host.Log($"[RynthAi] Nav auto-save to {path} failed: {ex.Message}"); }
    }

    /// <summary>
    /// Saves the current route and returns the line to show in chat. With a name it
    /// writes NavProfiles\&lt;name&gt;.nav and makes that the current nav. Without one it
    /// writes the current nav file; a nav from the loaded meta is updated in the meta
    /// (saving the meta keeps it); a route never saved gets a new "Route N" file.
    /// These used to do nothing: the button only wrote to an existing file, and a
    /// meta's nav path ("&lt;embedded:name&gt;") failed without a word.
    /// </summary>
    public string SaveRoute(string? name = null)
    {
        NavRouteParser route = _settings.CurrentRoute;
        string path = _settings.CurrentNavPath ?? string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string file = name.Trim();
                if (file.EndsWith(".nav", StringComparison.OrdinalIgnoreCase)) file = file[..^4].TrimEnd();
                if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return $"[RynthAi] '{name}' can't be used as a file name.";
                path = Path.Combine(NavFolder, file + ".nav");
            }
            else if (TryGetEmbeddedKey(path, out string key))
            {
                _settings.EmbeddedNavs[key] = route.ToLines();
                return $"[RynthAi] '{key}' is a nav inside the loaded meta; updated it there ({route.Points.Count} waypoints). Save the meta to keep it, or /ra nav save <name> for a separate file.";
            }
            else if (path.Length == 0)
            {
                if (route.Points.Count == 0) return "[RynthAi] No waypoints to save.";
                int n = 1;
                do path = Path.Combine(NavFolder, $"Route {n++}.nav"); while (File.Exists(path));
            }

            string target = path;
            if (!RynthCore.Plugin.Shared.DataWriteGuard.TrySave("the nav", target, () => route.Save(target), out string why, callerShows: true))
                return $"[RynthAi] Nav NOT saved to {target}: {why}";
            _settings.CurrentNavPath = path;
            return $"[RynthAi] Saved {route.Points.Count} waypoints to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] Nav save to {path} failed: {ex}");
            return $"[RynthAi] Nav save failed: {ex.Message}";
        }
    }

    private static bool TryGetEmbeddedKey(string path, out string key)
    {
        key = string.Empty;
        if (!path.StartsWith("<embedded:", StringComparison.Ordinal) || !path.EndsWith('>')) return false;
        key = path.Substring(10, path.Length - 11);
        return key.Length > 0;
    }

    public void UpdateRouteGraphics()
    {
        // 3D rendering not yet ported to RynthCore
    }

    public void DisposeRouteGraphics()
    {
        // 3D rendering not yet ported to RynthCore
    }
}
