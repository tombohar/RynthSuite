using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Install;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyMetaUi
{
    private readonly LegacyUiSettings _settings;
    private readonly List<string> _navFiles;

    private bool _showMetaEditor;
    private MetaRule _editingRule = new();

    // Indices of rules whose multi-action body is expanded inline. The user
    // toggles this via right-click on the row — left-click opens the editor.
    private readonly HashSet<int> _expandedRuleIndices = new();

    // ── Popped-out expression editor ─────────────────────────────────────
    // Right-clicking a condition/action expression input opens a much larger
    // multi-line window so long expressions are easy to read and edit.
    private bool                _exprPopupOpen;
    private string              _exprPopupTitle = "";
    private string              _exprPopupBuffer = "";
    private Action<string>?     _exprPopupApply;
    private bool                _exprPopupRequestFocus;
    private int _editingRuleIndex = -1;
    private string _newStateName = "";
    private bool _openCreateStatePopup;

    // ── Source view state ────────────────────────────────────────────────
    private bool _showSourceView;
    private string _sourceText = string.Empty;
    private string _sourceApplyMessage = "";
    private DateTime _sourceApplyTime = DateTime.MinValue;

    // ── Load/Save state ──────────────────────────────────────────────────
    private bool _openSavePopup;
    private List<(string Path, string Display)> _macroFiles = new();
    private int _selectedFileIdx = -1;
    private string _saveName = "macro";
    private string _statusMessage = "";
    private DateTime _statusTime = DateTime.MinValue;

    private static readonly string MetaFolder = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "MetaFiles");

    /// <summary>Warnings from the most recent LoadMacroFile, so the Avalonia
    /// bridge can surface them too (it doesn't see the LoadedMeta).</summary>
    internal IReadOnlyList<string> LastLoadWarnings { get; private set; } = System.Array.Empty<string>();

    // §3.3: labels come from the single source of truth (MetaSchema), indexed
    // by enum int — same strings as before, no longer a hand-synced copy.
    private readonly string[] _metaConditionNames = RynthCore.Plugin.RynthAi.Meta.MetaSchema.ConditionLabels;
    private readonly string[] _metaActionNames    = RynthCore.Plugin.RynthAi.Meta.MetaSchema.ActionLabels;

    public LegacyMetaUi(LegacyUiSettings settings, List<string> navFiles)
    {
        _settings = settings;
        _navFiles = navFiles;
        Saver = new MetaSaver(settings, null);
    }

    /// <summary>Writes the meta file and keeps the result (the dashboard passes its own, which logs).</summary>
    internal MetaSaver Saver { get; set; }

    /// <summary>The plugin log (the dashboard sets it). Null: nothing is logged.</summary>
    internal Action<string>? Log { get; set; }

    // ── CallState rules without a target (CallStateRepair) ───────────────
    // Chat lines wait here and go out on the plugin tick (DrainNotices), after the reply of the
    // command that loaded the meta: two lines in a row off the main thread lose the second.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingChat = new();
    private int _pendingTries;
    private const int MaxPendingChat = 8, MaxChatTries = 90;
    // The .met found for the loaded .af (looked up on load, not every frame).
    private string _callCheckPath = string.Empty;
    private string? _callCheckMet;
    private string? _repairFailText;   // the status line of a refused repair (shown in red)

    public void Render()
    {
        if (!DashWindows.ShowMacroRules) return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6.0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.04f, 0.06f, 0.08f, 0.97f));

        try
        {
            ImGui.SetNextWindowSize(new Vector2(600, 460), ImGuiCond.FirstUseEver);
            bool windowOpen = ImGui.Begin("Macro Rules##RynthAiMacroRules", ref DashWindows.ShowMacroRules);
            try
            {
                if (windowOpen)
                    RenderContents();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetaUi] RenderContents error: {ex.Message}");
            }
            finally
            {
                ImGui.End();
            }
        }
        finally
        {
            ImGui.PopStyleColor(1);
            ImGui.PopStyleVar(3);
        }
    }

    private void RenderContents()
    {
        // ── Load / Save bar ──────────────────────────────────────────────
        RenderLoadSaveBar();
        ImGui.Spacing();

        // ── Visual / Source toggle ───────────────────────────────────────
        // Always push/pop unconditionally — conditional push/pop is easy to misbalance
        // if an exception fires between push and pop.
        bool wasSource = _showSourceView;
        var defaultBtnCol = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        var activeBtnCol  = new Vector4(0.15f, 0.35f, 0.6f, 1f);

        ImGui.PushStyleColor(ImGuiCol.Button, wasSource ? defaultBtnCol : activeBtnCol);
        if (ImGui.Button("Visual", new Vector2(60, 0)) && _showSourceView)
            _showSourceView = false;
        ImGui.PopStyleColor();

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, wasSource ? activeBtnCol : defaultBtnCol);
        if (ImGui.Button("Source", new Vector2(60, 0)) && !_showSourceView)
        {
            // Switching to source: serialise current rules
            _sourceText = AfFileWriter.SaveToString(_settings.MetaRules, _settings.EmbeddedNavs);
            _showSourceView = true;
        }
        ImGui.PopStyleColor();

        ImGui.Spacing();

        // ── Source view ──────────────────────────────────────────────────
        if (_showSourceView)
        {
            float availH = ImGui.GetContentRegionAvail().Y - 30;
            ImGui.InputTextMultiline("##afSource", ref _sourceText, 1024 * 1024,
                new Vector2(-1, availH),
                ImGuiInputTextFlags.AllowTabInput);

            if (!string.IsNullOrEmpty(_sourceApplyMessage) &&
                (RynthClock.Now - _sourceApplyTime).TotalSeconds < 4)
            {
                SafeImGui.TextColored(new Vector4(0, 1, 0.4f, 1f), _sourceApplyMessage);
                ImGui.SameLine();
            }

            if (ImGui.Button("Apply", new Vector2(80, 0)))
            {
                try
                {
                    var loaded = AfFileParser.LoadFromText(_sourceText);
                    string warn = loaded.Warnings.Count == 0 ? ""
                        : $" — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}";
                    if (loaded.Rules.Count == 0)
                    {
                        _sourceApplyMessage = $"No rules parsed — check syntax.{warn}";
                    }
                    else
                    {
                        lock (_settings.MetaRulesLock)
                        {
                            _settings.MetaRules = loaded.Rules;
                            _settings.EmbeddedNavs.Clear();
                            foreach (var kvp in loaded.EmbeddedNavs)
                                _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
                        }
                        _settings.ForceStateReset = true;
                        TryAutoSaveMeta();
                        _sourceApplyMessage = $"Applied {loaded.Rules.Count} rules.{warn}";
                    }
                }
                catch (Exception ex)
                {
                    _sourceApplyMessage = $"Error: {ex.Message}";
                }
                _sourceApplyTime = RynthClock.Now;
            }

            ImGui.SameLine();
            if (ImGui.Button("Revert", new Vector2(80, 0)))
            {
                _sourceText = AfFileWriter.SaveToString(_settings.MetaRules, _settings.EmbeddedNavs);
                _sourceApplyMessage = "Reverted to current rules.";
                _sourceApplyTime = RynthClock.Now;
            }

            RenderMetaEditorPopup();
            return;
        }

        // ── Visual view ──────────────────────────────────────────────────
        // Snapshot under the lock (Think mutates MetaRules on the plugin-tick
        // thread). GroupBy+ToList fully materialises, so the render below works
        // off the snapshot, not the live list.
        List<IGrouping<string, MetaRule>> groupedRules;
        lock (_settings.MetaRulesLock)
        {
            groupedRules = _settings.MetaRules
                .GroupBy(r => r.State)
                .OrderBy(g => g.Key)
                .ToList();
        }

        if (ImGui.BeginChild("##metaRulesScroll", new Vector2(0, -35), ImGuiChildFlags.None))
        {
            foreach (var group in groupedRules)
            {
                var stateRules = group.ToList();
                bool anyFired = false;
                foreach (var r in stateRules)
                {
                    if ((RynthClock.Now - r.LastFiredAt).TotalMilliseconds < 1500)
                    {
                        anyFired = true;
                        break;
                    }
                }

                string headerLabel = anyFired
                    ? $"{group.Key}  ({stateRules.Count})  - firing##hdr_{group.Key}"
                    : $"{group.Key}  ({stateRules.Count})##hdr_{group.Key}";

                if (anyFired) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.2f, 0.2f, 1f));
                bool open = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
                if (anyFired) ImGui.PopStyleColor();

                if (!open) continue;

                if (!ImGui.BeginTable($"##tbl_{group.Key}", 5,
                    ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg))
                {
                    continue;
                }
                ImGui.TableSetupColumn("Up",        ImGuiTableColumnFlags.WidthFixed,   20);
                ImGui.TableSetupColumn("Dn",        ImGuiTableColumnFlags.WidthFixed,   20);
                ImGui.TableSetupColumn("Del",       ImGuiTableColumnFlags.WidthFixed,   25);
                ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Action",    ImGuiTableColumnFlags.WidthStretch);

                for (int i = 0; i < stateRules.Count; i++)
                {
                    var rule = stateRules[i];
                    int globalIdx = _settings.MetaRules.IndexOf(rule);

                    ImGui.TableNextRow();

                    double fadeMs = (RynthClock.Now - rule.LastFiredAt).TotalMilliseconds;
                    bool isFlashing = fadeMs < 1500;
                    if (isFlashing)
                    {
                        float t = fadeMs < 500 ? 0f : (float)((fadeMs - 500) / 1000.0);
                        if (t > 1f) t = 1f;
                        var red   = new Vector4(1f, 0.25f, 0.25f, 1f);
                        var white = new Vector4(1f, 1f, 1f, 1f);
                        var blended = new Vector4(
                            red.X * (1 - t) + white.X * t,
                            red.Y * (1 - t) + white.Y * t,
                            red.Z * (1 - t) + white.Z * t,
                            1f);
                        ImGui.PushStyleColor(ImGuiCol.Text, blended);
                    }

                    ImGui.TableNextColumn();
                    if (i > 0 && ImGui.Button($"^##up{globalIdx}"))
                    {
                        var prevRule = stateRules[i - 1];
                        lock (_settings.MetaRulesLock)
                        {
                            int a = _settings.MetaRules.IndexOf(rule);
                            int b = _settings.MetaRules.IndexOf(prevRule);
                            if (a >= 0 && b >= 0)
                            { _settings.MetaRules[a] = prevRule; _settings.MetaRules[b] = rule; }
                        }
                        TryAutoSaveMeta();
                    }

                    ImGui.TableNextColumn();
                    if (i < stateRules.Count - 1 && ImGui.Button($"v##dn{globalIdx}"))
                    {
                        var nextRule = stateRules[i + 1];
                        lock (_settings.MetaRulesLock)
                        {
                            int a = _settings.MetaRules.IndexOf(rule);
                            int b = _settings.MetaRules.IndexOf(nextRule);
                            if (a >= 0 && b >= 0)
                            { _settings.MetaRules[a] = nextRule; _settings.MetaRules[b] = rule; }
                        }
                        TryAutoSaveMeta();
                    }

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0, 0, 1));
                    if (ImGui.Button($"X##delm{globalIdx}"))
                    {
                        lock (_settings.MetaRulesLock)
                            _settings.MetaRules.Remove(rule);
                        TryAutoSaveMeta();
                    }
                    ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    string condText = rule.Condition.ToString() +
                        (string.IsNullOrEmpty(rule.ConditionData) ? "" : $": {rule.ConditionData}");

                    // Local helper — capture this row index so the action-cell right-click
                    // handler below toggles the same rule. SpanAllColumns + per-cell items
                    // means a single right-click can hit multiple IsItemClicked checks in
                    // one frame; the `_toggled` guard makes sure we flip exactly once.
                    int rowGlobalIdx = globalIdx;
                    bool _toggled = false;
                    void ToggleExpand()
                    {
                        if (_toggled) return;
                        _toggled = true;
                        if (!_expandedRuleIndices.Add(rowGlobalIdx))
                            _expandedRuleIndices.Remove(rowGlobalIdx);
                    }

                    if (ImGui.Selectable($"{condText}##edit{globalIdx}", false, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        _editingRule = new MetaRule
                        {
                            State = rule.State,
                            Condition = rule.Condition,
                            ConditionData = rule.ConditionData,
                            Action = rule.Action,
                            ActionData = rule.ActionData,
                            CallReturnState = rule.CallReturnState,
                            ChatColors = rule.ChatColors,
                            Name = rule.Name,
                            Children = CloneChildren(rule.Children),
                            ActionChildren = CloneChildren(rule.ActionChildren)
                        };
                        _editingRuleIndex = globalIdx;
                        _showMetaEditor = true;
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        ToggleExpand();

                    ImGui.TableNextColumn();
                    if (rule.Action == MetaActionType.All)
                    {
                        var actionList = (rule.ActionChildren != null && rule.ActionChildren.Count > 0)
                            ? rule.ActionChildren
                            : rule.Children;
                        int childCount = actionList?.Count ?? 0;
                        bool expanded = _expandedRuleIndices.Contains(globalIdx);
                        SafeImGui.TextColored(new Vector4(1, 1, 0, 1), $"All: [{childCount} actions] (right-click to expand)");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();

                        if (expanded && actionList != null)
                        {
                            for (int ai = 0; ai < actionList.Count; ai++)
                            {
                                var child = actionList[ai];
                                string childText = child.Action.ToString() +
                                    (string.IsNullOrEmpty(child.ActionData) ? "" : $": {child.ActionData}");
                                SafeImGui.TextDisabled($"   {ai + 1}. {childText}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                    ToggleExpand();
                            }
                        }
                    }
                    else if (rule.Action == MetaActionType.EmbeddedNavRoute &&
                             !string.IsNullOrEmpty(rule.ActionData) && rule.ActionData.Contains(';'))
                    {
                        var parts = rule.ActionData.Split(';');
                        SafeImGui.Text($"EmbeddedNavRoute: {parts[0]} ({parts[1]} pts)");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();
                    }
                    else
                    {
                        SafeImGui.Text(rule.Action.ToString() +
                            (string.IsNullOrEmpty(rule.ActionData) ? "" : $": {rule.ActionData}"));
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();
                    }

                    if (isFlashing) ImGui.PopStyleColor();
                }
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();

        ImGui.Separator();

        bool isMetaEnabled = _settings.EnableMeta;
        if (ImGui.Checkbox("Enable Meta", ref isMetaEnabled))
            _settings.EnableMeta = isMetaEnabled;

        ImGui.SameLine();
        bool metaDebug = _settings.MetaDebug;
        if (ImGui.Checkbox("Debug##MetaDbg", ref metaDebug))
            _settings.MetaDebug = metaDebug;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Log every rule execution to chat:\n[Meta] State | Condition → Action");

        ImGui.SameLine();
        ImGui.Text("Current State:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("##CurrentStateDropdown", _settings.CurrentState))
        {
            if (ImGui.Selectable("< Create New State >"))
                _openCreateStatePopup = true;

            ImGui.Separator();

            var uniqueStates = _settings.MetaRules.Select(r => r.State).Distinct().ToList();
            if (!uniqueStates.Contains("Default")) uniqueStates.Insert(0, "Default");

            foreach (var state in uniqueStates)
            {
                if (ImGui.Selectable(state, _settings.CurrentState == state))
                {
                    // Always a fresh entry (like SetState): the seconds-in-state timer
                    // and the old state's watchdog must not carry over to the new state.
                    _settings.ForceStateReset = true;
                    _settings.CurrentState = state;
                }
            }
            ImGui.EndCombo();
        }

        if (_openCreateStatePopup)
        {
            ImGui.OpenPopup("CreateStatePopup");
            _openCreateStatePopup = false;
        }

        if (ImGui.BeginPopup("CreateStatePopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter New State Name:");
            ImGui.InputText("##newStateIn", ref _newStateName, 64);
            ImGui.Spacing();
            if (ImGui.Button("Create", new Vector2(120, 0)))
            {
                if (!string.IsNullOrWhiteSpace(_newStateName))
                {
                    _settings.CurrentState = _newStateName;
                    _settings.ForceStateReset = true;
                    _newStateName = "";
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.SameLine(ImGui.GetWindowWidth() - 100);
        if (ImGui.Button("Create", new Vector2(80, 25)))
        {
            _editingRule = new MetaRule { State = _settings.CurrentState };
            _editingRuleIndex = -1;
            _showMetaEditor = true;
        }

        RenderMetaEditorPopup();
        RenderExpressionPopup();
    }

    private void RenderMetaEditorPopup()
    {
        if (!_showMetaEditor) return;

        // FirstUseEver — only set the default size if no persisted size is in
        // imgui.ini. ImGuiCond.Appearing was forcing the size every time the
        // popup re-opened, throwing away the user's resize.
        ImGui.SetNextWindowSize(new Vector2(650, 450), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Edit Meta Rule##RynthAiEditor", ref _showMetaEditor, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        float halfWidth = ImGui.GetContentRegionAvail().X / 2f - 4f;

        var uniqueStates = _settings.MetaRules.Select(r => r.State).Distinct().ToList();
        if (!uniqueStates.Contains("Default")) uniqueStates.Insert(0, "Default");

        ImGui.BeginChild("ConditionPane", new Vector2(halfWidth, -35), ImGuiChildFlags.Borders);

        ImGui.TextColored(new Vector4(0, 1, 1, 1), "Condition Type:");
        int condIdx = (int)_editingRule.Condition;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##CondCombo", ref condIdx, _metaConditionNames, _metaConditionNames.Length))
        {
            _editingRule.Condition = (MetaConditionType)condIdx;
            ClearConditionDataIfNeeded(_editingRule);
        }

        ImGui.TextColored(new Vector4(0, 1, 1, 1), "State Name:");
        ImGui.SetNextItemWidth(-30f);
        string tempState = _editingRule.State ?? "";
        if (ImGui.InputTextWithHint("##StateName", "State (e.g. Default)", ref tempState, 64))
            _editingRule.State = tempState;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##StatePicker", "", ImGuiComboFlags.NoPreview))
        {
            foreach (var state in uniqueStates)
            {
                if (ImGui.Selectable(state)) _editingRule.State = state;
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        RenderRuleConditionNode(_editingRule, "root_cond");
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("ActionPane", new Vector2(0, -35), ImGuiChildFlags.Borders);
        ImGui.TextColored(new Vector4(1, 1, 0, 1), "Action Type:");
        RenderRuleActionNode(_editingRule, "root_action");
        ImGui.EndChild();

        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - 30);
        if (ImGui.Button(_editingRuleIndex == -1 ? "Add Rule" : "Save Rule", new Vector2(100, 25)))
        {
            lock (_settings.MetaRulesLock)
            {
                if (_editingRuleIndex == -1 || _editingRuleIndex >= _settings.MetaRules.Count)
                    _settings.MetaRules.Add(_editingRule);
                else
                    _settings.MetaRules[_editingRuleIndex] = _editingRule;
            }
            _showMetaEditor = false;
            TryAutoSaveMeta();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 25))) _showMetaEditor = false;

        ImGui.End();
    }

    private void RenderRuleConditionNode(MetaRule rule, string idContext)
    {
        ImGui.PushID(idContext);

        if (rule.Condition == MetaConditionType.Any ||
            rule.Condition == MetaConditionType.All ||
            rule.Condition == MetaConditionType.Not)
        {
            if (rule.Condition != MetaConditionType.Not || rule.Children.Count == 0)
            {
                if (ImGui.Button("+##add"))
                    rule.Children.Add(new MetaRule { Condition = MetaConditionType.Always });
                ImGui.SameLine();
            }

            SafeImGui.TextColored(new Vector4(1, 1, 0, 1), $"{rule.Condition} Sub-Conditions");
            ImGui.Indent(15f);

            for (int i = 0; i < rule.Children.Count; i++)
            {
                var child = rule.Children[i];
                ImGui.PushID($"child_{i}");

                int childCondIdx = (int)child.Condition;
                ImGui.SetNextItemWidth(150);
                if (ImGui.Combo("##Cond", ref childCondIdx, _metaConditionNames, _metaConditionNames.Length))
                {
                    child.Condition = (MetaConditionType)childCondIdx;
                    ClearConditionDataIfNeeded(child);
                }

                ImGui.SameLine();
                float startX = ImGui.GetCursorPosX();
                float startY = ImGui.GetCursorPosY();
                ImGui.SameLine(ImGui.GetWindowWidth() - 35f);

                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.8f, 0.1f, 0.1f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f,   0.2f, 0.2f, 1f));
                bool deleteClicked = ImGui.Button("X");
                ImGui.PopStyleColor(2);

                ImGui.SetCursorPos(new Vector2(startX, startY));
                RenderRuleConditionNode(child, $"node_{i}");

                if (deleteClicked)
                {
                    rule.Children.RemoveAt(i);
                    i--;
                }

                ImGui.Dummy(new Vector2(0, 2f));
                ImGui.PopID();
            }

            ImGui.Unindent(15f);
        }
        else
        {
            bool needsData = true;
            string condHint = "Data...";

            if (rule.Condition == MetaConditionType.InventoryItemCount_LE ||
                rule.Condition == MetaConditionType.InventoryItemCount_GE)
            {
                string[] parts = (rule.ConditionData ?? "").Split(',');
                string itemName  = parts[0];
                string itemCount = parts.Length > 1 ? parts[1] : "0";

                ImGui.Text("Item Name:");
                ImGui.SetNextItemWidth(-28);
                if (ImGui.InputText("##InvName", ref itemName, 64))
                    rule.ConditionData = $"{itemName},{itemCount}";
                CopyButton("InvName", itemName);

                ImGui.Text("Item Count:");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##InvCount", ref itemCount, 16, ImGuiInputTextFlags.CharsDecimal))
                    rule.ConditionData = $"{itemName},{itemCount}";

                needsData = false;
            }
            else if (rule.Condition == MetaConditionType.TimeLeftOnSpell_GE ||
                     rule.Condition == MetaConditionType.TimeLeftOnSpell_LE)
            {
                string[] parts = (rule.ConditionData ?? "").Split(',');
                string spellId  = parts[0];
                string seconds  = parts.Length > 1 ? parts[1] : "0";

                ImGui.Text("Spell ID:");
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputText("##SpellId", ref spellId, 16, ImGuiInputTextFlags.CharsDecimal))
                    rule.ConditionData = $"{spellId},{seconds}";

                if (int.TryParse(spellId, out int id))
                {
                    string sName = SpellDatabase.GetSpellName(id);
                    if (!string.IsNullOrEmpty(sName))
                    {
                        ImGui.SameLine();
                        SafeImGui.TextColored(new Vector4(0, 1, 1, 1), $"({sName})");
                    }
                }

                ImGui.Text("Seconds Remaining:");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##SpellTime", ref seconds, 16, ImGuiInputTextFlags.CharsDecimal))
                    rule.ConditionData = $"{spellId},{seconds}";

                needsData = false;
            }
            else
            {
                switch (rule.Condition)
                {
                    case MetaConditionType.Never:
                    case MetaConditionType.Always:
                    case MetaConditionType.CharacterDeath:
                    case MetaConditionType.AnyVendorOpen:
                    case MetaConditionType.VendorClosed:
                    case MetaConditionType.NeedToBuff:
                    case MetaConditionType.PortalspaceEntered:
                    case MetaConditionType.PortalspaceExited:
                    case MetaConditionType.NavrouteEmpty:
                        needsData = false;
                        break;
                    case MetaConditionType.ChatMessage:
                    case MetaConditionType.ChatMessageCapture:
                        condHint = "Regex pattern...";
                        break;
                    case MetaConditionType.PackSlots_LE:
                        condHint = "Min slots (e.g. 5)";
                        break;
                    case MetaConditionType.BurdenPercentage_GE:
                        condHint = "Percentage (e.g. 250)";
                        break;
                    case MetaConditionType.SecondsInState_GE:
                    case MetaConditionType.SecondsInStateP_GE:
                        condHint = "Seconds (e.g. 10)";
                        break;
                    case MetaConditionType.NoMonstersWithinDistance:
                        condHint = "Distance in yards (e.g. 20)";
                        break;
                    case MetaConditionType.MonsterNameCountWithinDistance:
                        condHint = "name regex,distance,min count (e.g. Drudge,20,3)";
                        break;
                    case MetaConditionType.MonsterPriorityCountWithinDistance:
                        condHint = "min count,distance (e.g. 1,20)";
                        break;
                    case MetaConditionType.DistAnyRoutePT_GE:
                        condHint = "Distance in yards (e.g. 10)";
                        break;
                    case MetaConditionType.Landblock_EQ:
                    case MetaConditionType.Landcell_EQ:
                        condHint = "Hex value (e.g. A9B40000)";
                        break;
                    case MetaConditionType.VitaePHE:
                        condHint = "Vitae penalty % (e.g. 5)";
                        break;
                    case MetaConditionType.NoKillsWithinMinutes:
                        condHint = "Minutes without a kill (e.g. 5 or 2.5)";
                        break;
                }
            }

            if (needsData)
            {
                float availableSpace = ImGui.GetWindowWidth() - ImGui.GetCursorPosX() - 65f;
                ImGui.SetNextItemWidth(Math.Max(50f, availableSpace));

                string cData = rule.ConditionData ?? "";
                if (ImGui.InputTextWithHint("##Data", condHint, ref cData, 256))
                    rule.ConditionData = cData;
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    var localRule = rule;
                    OpenExpressionPopup("Condition Data", localRule.ConditionData ?? "", v => localRule.ConditionData = v);
                }
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(cData))
                    ImGui.SetTooltip("Right-click for a larger editor");
                CopyButton("CondData", cData);
            }
            else if (rule.Condition != MetaConditionType.InventoryItemCount_LE &&
                     rule.Condition != MetaConditionType.InventoryItemCount_GE &&
                     rule.Condition != MetaConditionType.TimeLeftOnSpell_GE &&
                     rule.Condition != MetaConditionType.TimeLeftOnSpell_LE)
            {
                ImGui.TextDisabled("(No extra data)");
            }
        }

        ImGui.PopID();
    }

    private void RenderRuleActionNode(MetaRule rule, string idContext)
    {
        ImGui.PushID(idContext);

        int actionIdx = (int)rule.Action;
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("##ActionType", ref actionIdx, _metaActionNames, _metaActionNames.Length))
        {
            rule.Action = (MetaActionType)actionIdx;
            if (rule.Action == MetaActionType.All) rule.ActionData = "";
            else rule.ActionChildren?.Clear();
        }

        ImGui.SameLine();

        if (rule.Action == MetaActionType.All)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.6f, 0.2f, 1f));
            if (ImGui.Button("+ Add Action"))
                rule.ActionChildren.Add(new MetaRule { Action = MetaActionType.ChatCommand });
            ImGui.PopStyleColor();

            ImGui.Indent(20f);
            for (int i = 0; i < rule.ActionChildren.Count; i++)
            {
                var child = rule.ActionChildren[i];
                ImGui.PushID($"act_child_{i}");

                if (ImGui.Button("X"))
                {
                    rule.ActionChildren.RemoveAt(i);
                    i--;
                }
                else
                {
                    ImGui.SameLine();
                    RenderRuleActionNode(child, $"child_node_{i}");
                }

                ImGui.PopID();
                if (i < rule.ActionChildren.Count - 1) ImGui.Separator();
            }
            ImGui.Unindent(20f);
        }
        else
        {
            switch (rule.Action)
            {
                case MetaActionType.ChatCommand:
                    string cmd = rule.ActionData ?? "";
                    ImGui.SetNextItemWidth(-28);
                    if (ImGui.InputTextWithHint("##Cmd", "e.g. /tell {1} hello!", ref cmd, 256))
                        rule.ActionData = cmd;
                    CopyButton("Cmd", cmd);
                    break;

                case MetaActionType.SetMetaState:
                case MetaActionType.CallMetaState:
                {
                    string state = rule.ActionData ?? "";
                    var stateList = _settings.MetaRules.Select(r => r.State).Distinct().ToList();
                    if (!stateList.Contains("Default")) stateList.Insert(0, "Default");
                    if (!stateList.Contains(state) && !string.IsNullOrEmpty(state)) stateList.Add(state);

                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.BeginCombo("##StateCombo", string.IsNullOrEmpty(state) ? "Select State..." : state))
                    {
                        foreach (var s in stateList)
                        {
                            if (ImGui.Selectable(s, state == s))
                                rule.ActionData = s;
                        }
                        ImGui.EndCombo();
                    }
                    break;
                }

                case MetaActionType.EmbeddedNavRoute:
                    if (ImGui.BeginCombo("##NavSelect",
                        string.IsNullOrEmpty(rule.ActionData) ? "Select Route..." : rule.ActionData))
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var embeddedName in _settings.EmbeddedNavs.Keys)
                        {
                            if (!seen.Add(embeddedName)) continue;
                            string label = $"{embeddedName} (embedded)";
                            if (ImGui.Selectable(label, rule.ActionData == embeddedName))
                                rule.ActionData = embeddedName;
                        }
                        foreach (var navFile in _navFiles)
                        {
                            if (navFile == "None") continue;
                            if (!seen.Add(navFile)) continue;
                            if (ImGui.Selectable(navFile, rule.ActionData == navFile))
                                rule.ActionData = navFile;
                        }
                        ImGui.EndCombo();
                    }
                    break;

                case MetaActionType.SetWatchdog:
                    string[] wdParts = (rule.ActionData ?? "Default;10;60").Split(';');
                    string wdState   = wdParts[0];
                    string wdMeters  = wdParts.Length > 1 ? wdParts[1] : "10";
                    string wdSeconds = wdParts.Length > 2 ? wdParts[2] : "60";

                    ImGui.Text("To State:");
                    ImGui.SetNextItemWidth(-1);

                    var wdStates = _settings.MetaRules.Select(r => r.State).Distinct().ToList();
                    if (!wdStates.Contains("Default")) wdStates.Insert(0, "Default");
                    if (!wdStates.Contains(wdState) && !string.IsNullOrEmpty(wdState)) wdStates.Add(wdState);

                    if (ImGui.BeginCombo("##WDStateCombo", wdState))
                    {
                        foreach (var s in wdStates)
                        {
                            if (ImGui.Selectable(s, wdState == s)) wdState = s;
                        }
                        ImGui.EndCombo();
                    }
                    ImGui.Spacing();

                    ImGui.Text("Meters in:");
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##WDMeters", ref wdMeters, 8, ImGuiInputTextFlags.CharsDecimal);
                    ImGui.Spacing();

                    ImGui.Text("Seconds:");
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##WDSecs", ref wdSeconds, 8, ImGuiInputTextFlags.CharsDecimal);

                    rule.ActionData = $"{wdState};{wdMeters};{wdSeconds}";
                    break;

                case MetaActionType.SetRAOption:
                    string[] raParts = (rule.ActionData ?? "EnableBuffing;False").Split(';');
                    string raO = raParts[0];
                    string raV = raParts.Length > 1 ? raParts[1] : "False";

                    ImGui.Text("Option:");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputText("##RAOpt", ref raO, 64)) rule.ActionData = $"{raO};{raV}";
                    CopyButton("RAOpt", raO);

                    ImGui.SameLine();
                    ImGui.Text("Val:");
                    ImGui.SetNextItemWidth(-28);
                    if (ImGui.InputText("##RAVal", ref raV, 64)) rule.ActionData = $"{raO};{raV}";
                    CopyButton("RAVal", raV);
                    break;

                case MetaActionType.ExpressionAction:
                case MetaActionType.ChatExpression:
                    string expr = rule.ActionData ?? "";
                    ImGui.SetNextItemWidth(-28);
                    if (ImGui.InputTextWithHint("##Expr", "Expression (e.g. [SlotCount] < 5)", ref expr, 256))
                        rule.ActionData = expr;
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        var localRule = rule;
                        OpenExpressionPopup("Action Expression", localRule.ActionData ?? "", v => localRule.ActionData = v);
                    }
                    if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(expr))
                        ImGui.SetTooltip("Right-click for a larger editor");
                    CopyButton("Expr", expr);
                    break;

                case MetaActionType.GetRAOption:
                case MetaActionType.CreateView:
                case MetaActionType.DestroyView:
                    string vData = rule.ActionData ?? "";
                    ImGui.SetNextItemWidth(-28);
                    if (ImGui.InputText("##ViewData", ref vData, 128))
                        rule.ActionData = vData;
                    CopyButton("ViewData", vData);
                    break;

                case MetaActionType.ReturnFromCall:
                case MetaActionType.ClearWatchdog:
                case MetaActionType.DestroyAllViews:
                    ImGui.TextDisabled("(No extra data required)");
                    break;

                case MetaActionType.None:
                    ImGui.TextDisabled("(Select an action type)");
                    break;
            }
        }

        ImGui.PopID();
    }

    /// <summary>
    /// Opens the popped-out expression editor for the given current value with
    /// a callback that writes the edited value back to its source.
    /// </summary>
    private void OpenExpressionPopup(string title, string current, Action<string> apply)
    {
        _exprPopupTitle        = title;
        _exprPopupBuffer       = current ?? string.Empty;
        _exprPopupApply        = apply;
        _exprPopupOpen         = true;
        _exprPopupRequestFocus = true;
    }

    /// <summary>
    /// Renders the popped-out editor window when one is open. Larger multi-line
    /// input than the inline meta editor field, with a hint at the right-click
    /// trigger and an Apply/Close pair.
    /// </summary>
    private void RenderExpressionPopup()
    {
        if (!_exprPopupOpen) return;

        ImGui.SetNextWindowSize(new Vector2(720, 380), ImGuiCond.FirstUseEver);
        if (_exprPopupRequestFocus)
            ImGui.SetNextWindowFocus();
        if (ImGui.Begin($"Expression Editor - {_exprPopupTitle}##ExpressionPopup", ref _exprPopupOpen))
        {
            ImGui.TextDisabled("Edit the expression here. Apply writes it back to the rule.");
            ImGui.Separator();

            if (_exprPopupRequestFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _exprPopupRequestFocus = false;
            }

            Vector2 inputSize = new Vector2(-1, ImGui.GetContentRegionAvail().Y - 36);
            ImGui.InputTextMultiline("##ExprPopupText", ref _exprPopupBuffer, 4096, inputSize);

            ImGui.Separator();
            if (ImGui.Button("Apply", new Vector2(120, 0)))
            {
                _exprPopupApply?.Invoke(_exprPopupBuffer);
                TryAutoSaveMeta();
                _exprPopupOpen = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _exprPopupOpen = false;
            }
        }
        ImGui.End();
    }

    private static void ClearConditionDataIfNeeded(MetaRule rule)
    {
        switch (rule.Condition)
        {
            case MetaConditionType.Always:
            case MetaConditionType.Never:
            case MetaConditionType.CharacterDeath:
            case MetaConditionType.AnyVendorOpen:
            case MetaConditionType.VendorClosed:
            case MetaConditionType.NeedToBuff:
            case MetaConditionType.PortalspaceEntered:
            case MetaConditionType.PortalspaceExited:
            case MetaConditionType.NavrouteEmpty:
            case MetaConditionType.All:
            case MetaConditionType.Any:
            case MetaConditionType.Not:
                rule.ConditionData = "";
                break;
        }
    }

    private static void CopyButton(string id, string text)
    {
        ImGui.SameLine();
        if (ImGui.SmallButton($"C##{id}"))
            ImGui.SetClipboardText(text ?? "");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy");
    }

    private static List<MetaRule> CloneChildren(List<MetaRule> source)
    {
        var result = new List<MetaRule>(source.Count);
        foreach (var r in source)
        {
            result.Add(new MetaRule
            {
                State          = r.State,
                Condition      = r.Condition,
                ConditionData  = r.ConditionData,
                Action         = r.Action,
                ActionData     = r.ActionData,
                CallReturnState = r.CallReturnState,
                ChatColors     = r.ChatColors,
                Name           = r.Name,
                Children       = CloneChildren(r.Children),
                ActionChildren = CloneChildren(r.ActionChildren)
            });
        }
        return result;
    }

    // ── Load / Save bar (inline at top of window) ─────────────────────────

    private void RenderLoadSaveBar()
    {
        // Lazy-init file list (always has at least the None entry after first refresh)
        if (_macroFiles.Count == 0)
            RefreshMacroFileList();

        // CallState rules that call nothing (an .af saved by an older RynthAi): a Repair button
        // when the .met it came from is there, and a line saying so either way.
        int emptyCalls = EmptyCallStatesNow(out string? repairMet);
        bool showRepair = emptyCalls > 0 && repairMet != null;

        // Combo label: show selected file, or the currently loaded file if selection is stale
        string comboLabel = (_selectedFileIdx >= 0 && _selectedFileIdx < _macroFiles.Count)
            ? _macroFiles[_selectedFileIdx].Display
            : (!string.IsNullOrEmpty(_settings.CurrentMetaPath)
                ? Path.GetFileName(_settings.CurrentMetaPath)
                : "-- None --");

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (showRepair ? 345 : 210));
        if (ImGui.BeginCombo("##MacroFileCombo", comboLabel))
        {
            for (int i = 0; i < _macroFiles.Count; i++)
            {
                if (ImGui.Selectable(_macroFiles[i].Display, _selectedFileIdx == i))
                    _selectedFileIdx = i;
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Load", new Vector2(55, 0)))
        {
            if (_selectedFileIdx >= 0 && _selectedFileIdx < _macroFiles.Count)
                LoadMacroFile(_macroFiles[_selectedFileIdx].Path);
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh", new Vector2(55, 0)))
            RefreshMacroFileList();

        ImGui.SameLine();
        if (ImGui.Button("Save", new Vector2(55, 0)))
            _openSavePopup = true;

        if (showRepair)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.35f, 0.05f, 1f));
            if (ImGui.Button("Repair from .met", new Vector2(127, 0)))
            {
                bool repaired = RepairFromMet(out string reply);
                _statusMessage = reply;
                _repairFailText = repaired ? null : reply;
                _statusTime = RynthClock.Now;
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                SafeImGui.SetTooltip($"Fills the {emptyCalls} CallState target(s) from {Path.GetFileName(repairMet)}, in order, state by state.\n" +
                                 "A backup copy of the .af is kept next to it first. Refused if the two files don't match.");
        }
        if (emptyCalls > 0)
            SafeImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f), repairMet != null
                ? $"{emptyCalls} CallState rule(s) call no state (saved by an older RynthAi). Repair from .met fills them."
                : $"{emptyCalls} CallState rule(s) call no state (saved by an older RynthAi). No .met of the same name to repair from.");

        // Status message
        // A save that failed (or an edit that wasn't written) stays up, in red, until the next result.
        bool unsaved = (!Saver.LastOk && Saver.Seq > 0 && ReferenceEquals(_statusMessage, Saver.LastText))
                       || (_repairFailText != null && ReferenceEquals(_statusMessage, _repairFailText));   // a refused repair stays up too
        if (unsaved)
            SafeImGui.TextColored(new Vector4(1, 0.35f, 0.35f, 1), _statusMessage);
        else if (!string.IsNullOrEmpty(_statusMessage) && (RynthClock.Now - _statusTime).TotalSeconds < 5)
            SafeImGui.TextColored(new Vector4(0, 1, 0, 1), _statusMessage);

        // Save popup
        if (_openSavePopup)
        {
            ImGui.OpenPopup("SaveMacroPopup");
            _openSavePopup = false;
        }

        if (ImGui.BeginPopup("SaveMacroPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("File name:");
            ImGui.SetNextItemWidth(250);
            ImGui.InputText("##SaveName", ref _saveName, 64);
            ImGui.SameLine();
            ImGui.TextDisabled(".af");
            ImGui.Spacing();
            if (ImGui.Button("Save", new Vector2(120, 0)))
            {
                SaveMacroFile();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void RefreshMacroFileList()
    {
        var fileEntries = new List<(string Path, string Display)>();

        Directory.CreateDirectory(MetaFolder);
        foreach (string f in Directory.GetFiles(MetaFolder, "*.met"))
        {
            string name = Path.GetFileName(f);
            if (name.StartsWith("--")) continue;
            fileEntries.Add((f, $"[met] {name}"));
        }
        foreach (string f in Directory.GetFiles(MetaFolder, "*.af"))
            fileEntries.Add((f, $"[af]  {Path.GetFileName(f)}"));

        fileEntries.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));

        _macroFiles.Clear();
        _macroFiles.Add(("", "-- None --")); // index 0 = clear
        foreach (var e in fileEntries)
            _macroFiles.Add(e);

        // Pre-select the currently loaded file so the combo label is correct after a session restart.
        _selectedFileIdx = 0;
        if (!string.IsNullOrEmpty(_settings.CurrentMetaPath))
        {
            for (int i = 1; i < _macroFiles.Count; i++)
            {
                if (_macroFiles[i].Path.Equals(_settings.CurrentMetaPath, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedFileIdx = i;
                    break;
                }
            }
        }
    }

    /// <summary>Loads a .met/.af (empty path clears). False when nothing was loaded; <see cref="LastLoadStatus"/> says why.</summary>
    internal bool LoadMacroFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            lock (_settings.MetaRulesLock)
            {
                _settings.MetaRules.Clear();
                _settings.EmbeddedNavs.Clear();
            }
            _settings.CurrentMetaPath = string.Empty;
            _settings.MetaUnsaved = false;
            Saver.Note("Macro cleared.", string.Empty);
            _settings.CurrentState = "Default";
            _settings.ForceStateReset = true;
            _statusMessage = "Macro cleared.";
            _statusTime = RynthClock.Now;
            return true;
        }

        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            LoadedMeta loaded;

            if (ext == ".met")
                loaded = MetFileParser.Load(filePath);
            else if (ext == ".af")
                loaded = AfFileParser.Load(filePath);
            else
            {
                _statusMessage = "Unsupported file type";
                _statusTime = RynthClock.Now;
                return false;
            }

            LastLoadWarnings = loaded.Warnings;
            // A blank meta (a state, no rules) loads: it clears the old rules. Anything else
            // without rules (a profile, an empty file) is refused as before.
            if (loaded.Rules.Count == 0 && !loaded.IsBlank)
            {
                _statusMessage = loaded.Warnings.Count > 0
                    ? $"No rules parsed — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}"
                    : "No rules found (profile?)";
                _statusTime = RynthClock.Now;
                return false;
            }

            lock (_settings.MetaRulesLock)
            {
                _settings.MetaRules = loaded.Rules;
                _settings.EmbeddedNavs.Clear();
                foreach (var kvp in loaded.EmbeddedNavs)
                    _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
            }
            _settings.CurrentState = loaded.StartState;
            _settings.ForceStateReset = true;
            _settings.MetaCallStackReset = true;
            _settings.CurrentMetaPath = filePath;
            _settings.MetaUnsaved = false;   // the rules are the file's now
            Saver.Note($"Loaded {Path.GetFileName(filePath)}", filePath);   // an old save failure isn't this meta's

            string name = Path.GetFileNameWithoutExtension(filePath);
            int stateCount = loaded.Rules.Select(r => r.State).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _statusMessage = loaded.Warnings.Count == 0
                ? $"Loaded {loaded.Rules.Count} rules / {stateCount} states / {loaded.EmbeddedNavs.Count} navs from {name}"
                : $"Loaded {loaded.Rules.Count} rules / {stateCount} states from {name} — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}";
            _statusTime = RynthClock.Now;
            NoteCallStates(filePath, loaded.Rules);
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Load error: {ex.Message}";
            _statusTime = RynthClock.Now;
            return false;
        }
    }

    /// <summary>The status line of the last load (why it failed, or what it loaded).</summary>
    internal string LastLoadStatus => _statusMessage ?? string.Empty;

    /// <summary>
    /// Lists MetaFiles again once the panel has listed it (a meta /ra meta load just created shows
    /// in the picker, selected). Before the panel's first draw it lists itself then.
    /// </summary>
    internal void RefreshFileListIfShown()
    {
        if (_macroFiles.Count > 0) RefreshMacroFileList();
    }

    // ── CallState rules without a target ─────────────────────────────────

    /// <summary>
    /// A meta just loaded (or read again at login): when it is an .af with CallState rules that
    /// call nothing, one chat line says so (queued, see <see cref="DrainNotices"/>) and the log names
    /// each state. Remembers the .met to repair from, for the Repair button.
    /// </summary>
    internal void NoteCallStates(string path, IReadOnlyList<MetaRule> rules)
    {
        _callCheckPath = path ?? string.Empty;
        _callCheckMet = null;
        if (!Path.GetExtension(_callCheckPath).Equals(".af", StringComparison.OrdinalIgnoreCase)) return;
        var empty = CallStateRepair.EmptyTargets(rules);
        if (empty.Count == 0) return;
        _callCheckMet = CallStateRepair.FindSourceMet(_callCheckPath);
        int count = 0;
        foreach (var (_, n) in empty) count += n;
        string name = Path.GetFileName(_callCheckPath);
        string line = CallStateRepair.WarningLine(name, count, _callCheckMet);
        if (_pendingChat.Count < MaxPendingChat) _pendingChat.Enqueue(line);
        WriteLog(line);
        foreach (var (state, n) in empty)
            WriteLog($"[RynthAi] {name}: state '{state}' has {n} CallState rule{(n == 1 ? "" : "s")} with no state to call.");
    }

    /// <summary>
    /// Posts the queued chat lines (plugin tick). <paramref name="write"/> is Host.WriteToChat: a line
    /// it refuses waits for the next tick (about 1.5 s at most, then it is only in the log).
    /// </summary>
    internal void DrainNotices(Func<string, bool> write)
    {
        while (_pendingChat.TryPeek(out string? line))
        {
            bool ok;
            try { ok = write(line); } catch { ok = false; }
            if (!ok && ++_pendingTries < MaxChatTries) return;
            _pendingChat.TryDequeue(out _);
            _pendingTries = 0;
        }
    }

    /// <summary>Chat lines waiting to be posted (tests).</summary>
    internal int PendingNotices => _pendingChat.Count;

    /// <summary>
    /// The loaded meta's CallState rules without a target, now (edits included), and the .met to
    /// repair from (null: none). 0 for a .met or no meta.
    /// </summary>
    internal int EmptyCallStatesNow(out string? repairMet)
    {
        repairMet = null;
        string path = _settings.CurrentMetaPath ?? string.Empty;
        if (!Path.GetExtension(path).Equals(".af", StringComparison.OrdinalIgnoreCase)) return 0;
        int n;
        lock (_settings.MetaRulesLock) n = CallStateRepair.CountEmpty(_settings.MetaRules);
        if (n == 0) return 0;
        if (!path.Equals(_callCheckPath, StringComparison.OrdinalIgnoreCase))
        {
            // The meta's file changed without a load here (a .met saved as its .af, a login): look once.
            _callCheckPath = path;
            _callCheckMet = CallStateRepair.FindSourceMet(path);
        }
        repairMet = _callCheckMet;
        return n;
    }

    /// <summary>
    /// Repair from .met (the Macro Rules button, /ra meta repair): fills the loaded .af's empty
    /// CallState targets and return states from the .met of the same name, in order, state by state;
    /// keeps a backup copy ("&lt;name&gt;.af.bak-yyyyMMdd-HHmmss"), writes the .af and loads it again.
    /// Refused, with nothing changed, when the two files don't match. <paramref name="reply"/> is the
    /// chat line either way.
    /// </summary>
    internal bool RepairFromMet(out string reply)
    {
        (bool ok, string text) = RepairFromMetCore();
        reply = text;
        return ok;
    }

    private (bool Ok, string Reply) RepairFromMetCore()
    {
        string path = _settings.CurrentMetaPath ?? string.Empty;
        string name = Path.GetFileName(path);
        (bool, string) Refuse(string why)
        {
            WriteLog($"[RynthAi] Meta repair of {(path.Length > 0 ? path : "(no meta)")} refused: {why}");
            return (false, $"[RynthAi] Can't repair {(name.Length > 0 ? name : "the meta")}: {why}");
        }

        if (path.Length == 0) return Refuse("no meta is loaded. Load the .af first.");
        if (!Path.GetExtension(path).Equals(".af", StringComparison.OrdinalIgnoreCase))
            return Refuse("only an .af can be repaired (a .met already has its CallState targets).");
        if (!File.Exists(path)) return Refuse("the file isn't there any more.");
        if (_settings.MetaUnsaved)
            return Refuse("your last change to it isn't saved to the file yet. Save it first, then repair.");

        string? met = CallStateRepair.FindSourceMet(path);
        if (met == null)
            return Refuse($"there is no {Path.GetFileNameWithoutExtension(path)}.met next to it. Load the original .met if you have it, or set each CallState's target in the editor.");
        string metName = Path.GetFileName(met);

        LoadedMeta af;
        LoadedMeta source;
        try
        {
            af = AfFileParser.Load(path);
            source = MetFileParser.Load(met);
        }
        catch (Exception ex) { return Refuse($"reading the files failed ({ex.Message})."); }
        if (af.Rules.Count == 0)
            return Refuse($"it didn't read ({(af.Warnings.Count > 0 ? af.Warnings[0] : "no rules")}).");
        if (CallStateRepair.CountEmpty(af.Rules) == 0)
        {
            string none = $"[RynthAi] No CallState rule in {name} is missing its target; nothing to repair.";
            WriteLog(none);
            return (false, none);
        }
        if (source.Rules.Count == 0)
            return Refuse($"{metName} didn't read ({(source.Warnings.Count > 0 ? source.Warnings[0] : "no rules")}).");

        CallStateRepair.Result r = CallStateRepair.Repair(af.Rules, source.Rules);
        if (!r.Ok) return Refuse($"{r.Why}.");

        string backup = CallStateRepair.BackupPath(path, RynthClock.Now);
        try { File.Copy(path, backup, overwrite: false); }
        catch (Exception ex) { return Refuse($"couldn't keep a backup copy ({ex.Message}); nothing was changed."); }

        if (!DataWriteGuard.TrySave("the meta", path,
                () => AfFileWriter.Save(path, af.Rules, af.EmbeddedNavs), out string why, callerShows: true))
            return Refuse($"couldn't write it: {why} The backup {Path.GetFileName(backup)} is kept.");

        WriteLog($"[RynthAi] Meta repaired: {path} from {met}: {r.Filled} CallState rule(s) in {r.States} state(s); backup {backup}");
        foreach (string d in r.Details) WriteLog($"[RynthAi]   {d}");

        if (!LoadMacroFile(path))
            return (false, $"[RynthAi] Couldn't load {name} again after repairing it ({LastLoadStatus}). The repaired file is written; the backup is {Path.GetFileName(backup)}.");
        string done = $"[RynthAi] Repaired {name}: {r.Filled} CallState rule{(r.Filled == 1 ? "" : "s")} in {r.States} state{(r.States == 1 ? "" : "s")} filled from {metName}, and loaded it again (it starts over in {_settings.CurrentState}). Backup: {Path.GetFileName(backup)}.";
        _statusMessage = done;
        _statusTime = RynthClock.Now;
        return (true, done);
    }

    private void WriteLog(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }


    /// <summary>Writes the meta file after an edit here (MetaSaver: logged); the status line says
    /// whether it was saved, or why not (used to say nothing either way).</summary>
    private void TryAutoSaveMeta()
    {
        _settings.MetaRulesStructuralVersion++;   // editor mutation → rebuild meta index
        // .met files can't be written: SaveCurrent saves the .af alongside and switches to it.
        Saver.SaveCurrent("Macro Rules edit");
        _statusMessage = Saver.LastText;
        _statusTime = RynthClock.Now;
    }

    /// <summary>The status line's text now (the last load, save or refusal; empty after a few seconds).</summary>
    internal string StatusLine => _statusMessage ?? string.Empty;

    private void SaveMacroFile()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_saveName))
            {
                _statusMessage = "Enter a file name";
                _statusTime = RynthClock.Now;
                return;
            }

            string filePath = Path.Combine(MetaFolder, _saveName + ".af");
            if (!Saver.SaveTo(filePath, "Macro Rules Save"))
            {
                _statusMessage = Saver.LastText;
                _statusTime = RynthClock.Now;
                return;
            }

            _statusMessage = Saver.LastText;
            _statusTime = RynthClock.Now;
            RefreshMacroFileList();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Save error: {ex.Message}";
            _statusTime = RynthClock.Now;
        }
    }
}
