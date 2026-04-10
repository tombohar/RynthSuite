using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyLuaUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;

    // Delegates for the main plugin to hook into
    public Action? OnSettingsChanged;
    public Action<string>? OnExecuteRequested;
    public Action? OnStopRequested;

    // Internal State
    private readonly string _luaFolder = @"C:\Games\RynthSuite\RynthAi\LuaScripts";
    private string _newLuaFileName = "MyScript";
    private int _selectedLuaIdx = 0;
    private readonly List<string> _luaFiles = new();

    private readonly string _selectedSnippetKey = "--- Select Snippet ---";
    private readonly Dictionary<string, string> _luaSnippets = new()
    {
        { "--- Select Snippet ---", "" },
        { "📍 Move to Coord", "RynthAi:GoTo(\"0.0N\", \"0.0E\")" },
        { "🌀 Use Portal/NPC", "RynthAi:UsePortal(\"Portal Name\")" },
        { "📏 Get Distance", "local dist = RynthAi:GetDistance(\"Object Name\")\nprint(\"Distance: \" .. dist)" },
        { "🛑 Emergency Stop", "RynthAi:Stop()" }
    };

    public LegacyLuaUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
        RefreshLuaFiles();
    }

    public void RefreshLuaFiles()
    {
        _luaFiles.Clear();
        _luaFiles.Add("None");
        if (Directory.Exists(_luaFolder))
        {
            foreach (string file in Directory.GetFiles(_luaFolder, "*.lua"))
                _luaFiles.Add(Path.GetFileNameWithoutExtension(file));
        }
    }

    public void Render()
    {
        if (!DashWindows.ShowLua)
            return;

        ImGui.SetNextWindowSize(new Vector2(600, 500), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Lua Scripts##RynthAiLua", ref DashWindows.ShowLua))
        {
            ImGui.End();
            return;
        }

        // Safety Checks
        _settings.LuaScript ??= "";
        _settings.LuaConsoleOutput ??= "";

        // 1. File & Snippet Bar
        RenderTopBar();

        ImGui.Separator();

        // 2. Toolbar (Run, Stop, Clear)
        RenderToolbar();

        ImGui.Separator();

        // 3. Editor & Console
        RenderEditorAndConsole();

        ImGui.End();
    }

    private void RenderTopBar()
    {
        ImGui.SetNextItemWidth(120);
        string preview = (_selectedLuaIdx < _luaFiles.Count) ? _luaFiles[_selectedLuaIdx] : "None";
        if (ImGui.BeginCombo("##LoadLua", preview))
        {
            for (int i = 0; i < _luaFiles.Count; i++)
            {
                if (ImGui.Selectable(_luaFiles[i], _selectedLuaIdx == i))
                {
                    _selectedLuaIdx = i;
                    if (_luaFiles[i] != "None")
                    {
                        string path = Path.Combine(_luaFolder, _luaFiles[i] + ".lua");
                        if (File.Exists(path))
                        {
                            _settings.LuaScript = File.ReadAllText(path);
                            OnSettingsChanged?.Invoke();
                        }
                    }
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("🔄")) RefreshLuaFiles();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("##SaveAs", ref _newLuaFileName, 64);

        ImGui.SameLine();
        if (ImGui.Button("💾"))
        {
            if (!Directory.Exists(_luaFolder)) Directory.CreateDirectory(_luaFolder);
            File.WriteAllText(Path.Combine(_luaFolder, _newLuaFileName + ".lua"), _settings.LuaScript);
            _host.Log($"Saved Lua script: {_newLuaFileName}.lua");
            RefreshLuaFiles();
        }

        ImGui.SameLine();
        RenderSnippets();
    }

    private void RenderSnippets()
    {
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("##Snippets", _selectedSnippetKey))
        {
            foreach (var s in _luaSnippets)
            {
                if (ImGui.Selectable(s.Key))
                {
                    if (!string.IsNullOrEmpty(_settings.LuaScript) && !_settings.LuaScript.EndsWith('\n'))
                        _settings.LuaScript += "\n";

                    _settings.LuaScript += s.Value;
                    OnSettingsChanged?.Invoke();
                }
            }
            ImGui.EndCombo();
        }
    }

    private void RenderToolbar()
    {
        if (ImGui.Button("▶ Run Script")) OnExecuteRequested?.Invoke(_settings.LuaScript);

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.1f, 0.1f, 1.0f));
        if (ImGui.Button("🛑 Stop")) OnStopRequested?.Invoke();
        ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.Button("🧹 Clear Console"))
        {
            _settings.LuaConsoleOutput = "--- Console Cleared ---";
            OnSettingsChanged?.Invoke();
        }
    }

    private void RenderEditorAndConsole()
    {
        float editorH = ImGui.GetContentRegionAvail().Y - 150;

        ImGui.InputTextMultiline("##LuaEditor", ref _settings.LuaScript, 50000, new Vector2(-1, editorH), ImGuiInputTextFlags.AllowTabInput);

        ImGui.TextDisabled("Console Output:");
        if (ImGui.BeginChild("LuaConsole", new Vector2(-1, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.TextUnformatted(_settings.LuaConsoleOutput);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                ImGui.SetScrollHereY(1.0f);
        }
        ImGui.EndChild();
    }
}