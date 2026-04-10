using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

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
        ImGui.TextColored(new Vector4(1, 1, 0, 1), $"Active Nav: {activeNavName}");

        if (!string.IsNullOrEmpty(_settings.NavStatusLine))
        {
            var statusColor = _settings.NavIsStuck
                ? new Vector4(0.91f, 0.70f, 0.20f, 1.00f)   // amber
                : new Vector4(0.25f, 0.85f, 0.45f, 1.00f);  // green
            ImGui.TextColored(statusColor, _settings.NavStatusLine);
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
                _settings.IsMacroRunning  = true;
                _settings.EnableNavigation = true;
                if (_settings.CurrentState != "Navigating")
                    _settings.CurrentState = "Idle";
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
            if (_host.HasGetPlayerPose && _host.TryGetPlayerPose(out _, out float x, out float y, out float z, out _, out _, out _, out _))
            {
                if (NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew))
                {
                    var newPt = new NavPoint
                    {
                        NS = ns,
                        EW = ew,
                                Z = z
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
                string name = $"Spell {ids[ri]}";
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
                                Z = z,
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
            if (!string.IsNullOrEmpty(_settings.CurrentNavPath))
            {
                try 
                { 
                    _settings.CurrentRoute.Save(_settings.CurrentNavPath); 
                    _host.Log("Route saved."); 
                } 
                catch (Exception ex)
                {
                    _host.Log($"Failed to save route: {ex.Message}");
                }
            }
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

                    if (_settings.ActiveNavIndex == i) _settings.ActiveNavIndex = 0;
                    else if (_settings.ActiveNavIndex > i) _settings.ActiveNavIndex--;

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

    public void TryAutoSaveNav()
    {
        if (!string.IsNullOrEmpty(_settings.CurrentNavPath))
        {
            try { _settings.CurrentRoute.Save(_settings.CurrentNavPath); } catch { }
        }
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
