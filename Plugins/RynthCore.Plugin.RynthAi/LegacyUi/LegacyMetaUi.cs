using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Meta;

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

    private static readonly string MetaFolder = @"C:\Games\RynthSuite\RynthAi\MetaFiles";

    /// <summary>Warnings from the most recent LoadMacroFile, so the Avalonia
    /// bridge can surface them too (it doesn't see the LoadedMeta).</summary>
    internal IReadOnlyList<string> LastLoadWarnings { get; private set; } = System.Array.Empty<string>();

    private readonly string[] _metaConditionNames =
    {
        "Never", "Always", "All", "Any", "Chat Message", "Pack Slots <=",
        "Seconds in State >=", "Character Death", "Any Vendor Open",
        "Vendor Closed", "Inventory Item Count <=", "Inventory Item Count >=",
        "Monster Name Count Within Distance", "Monster Priority Count Within Distance",
        "Need To Buff", "No Monsters Within Distance", "Landblock ==",
        "Landcell ==", "Portalspace Entered", "Portalspace Exited", "Not",
        "Seconds in State (P) >=", "Time Left On Spell >=", "Time Left On Spell <=",
        "Burden Percentage >=", "Dist Any Route PT >=", "Expression",
        "Chat Message Capture", "Navroute Empty",
        "Main Health <=", "Main Health % >=", "Main Mana <=", "Main Mana % >=", "Main Stam <=",
        "Vitae % >="
    };

    private readonly string[] _metaActionNames =
    {
        "None", "Chat Command", "Set Meta State", "Embedded Nav Route", "All",
        "Call Meta State", "Return From Call", "Expression Action", "Chat Expression",
        "Set Watchdog", "Clear Watchdog", "Get RA Option", "Set RA Option",
        "Create View", "Destroy View", "Destroy All Views"
    };

    public LegacyMetaUi(LegacyUiSettings settings, List<string> navFiles)
    {
        _settings = settings;
        _navFiles = navFiles;
    }

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
                (DateTime.Now - _sourceApplyTime).TotalSeconds < 4)
            {
                ImGui.TextColored(new Vector4(0, 1, 0.4f, 1f), _sourceApplyMessage);
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
                _sourceApplyTime = DateTime.Now;
            }

            ImGui.SameLine();
            if (ImGui.Button("Revert", new Vector2(80, 0)))
            {
                _sourceText = AfFileWriter.SaveToString(_settings.MetaRules, _settings.EmbeddedNavs);
                _sourceApplyMessage = "Reverted to current rules.";
                _sourceApplyTime = DateTime.Now;
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
                    if ((DateTime.Now - r.LastFiredAt).TotalMilliseconds < 1500)
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

                    double fadeMs = (DateTime.Now - rule.LastFiredAt).TotalMilliseconds;
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
                        ImGui.TextColored(new Vector4(1, 1, 0, 1), $"All: [{childCount} actions] (right-click to expand)");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();

                        if (expanded && actionList != null)
                        {
                            for (int ai = 0; ai < actionList.Count; ai++)
                            {
                                var child = actionList[ai];
                                string childText = child.Action.ToString() +
                                    (string.IsNullOrEmpty(child.ActionData) ? "" : $": {child.ActionData}");
                                ImGui.TextDisabled($"   {ai + 1}. {childText}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                    ToggleExpand();
                            }
                        }
                    }
                    else if (rule.Action == MetaActionType.EmbeddedNavRoute &&
                             !string.IsNullOrEmpty(rule.ActionData) && rule.ActionData.Contains(';'))
                    {
                        var parts = rule.ActionData.Split(';');
                        ImGui.Text($"EmbeddedNavRoute: {parts[0]} ({parts[1]} pts)");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();
                    }
                    else
                    {
                        ImGui.Text(rule.Action.ToString() +
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
                    if (_settings.CurrentState == state) _settings.ForceStateReset = true;
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

            ImGui.TextColored(new Vector4(1, 1, 0, 1), $"{rule.Condition} Sub-Conditions");
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
                        ImGui.TextColored(new Vector4(0, 1, 1, 1), $"({sName})");
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

        // Combo label: show selected file, or the currently loaded file if selection is stale
        string comboLabel = (_selectedFileIdx >= 0 && _selectedFileIdx < _macroFiles.Count)
            ? _macroFiles[_selectedFileIdx].Display
            : (!string.IsNullOrEmpty(_settings.CurrentMetaPath)
                ? Path.GetFileName(_settings.CurrentMetaPath)
                : "-- None --");

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 210);
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

        // Status message
        if (!string.IsNullOrEmpty(_statusMessage) && (DateTime.Now - _statusTime).TotalSeconds < 5)
            ImGui.TextColored(new Vector4(0, 1, 0, 1), _statusMessage);

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

    internal void LoadMacroFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            lock (_settings.MetaRulesLock)
            {
                _settings.MetaRules.Clear();
                _settings.EmbeddedNavs.Clear();
            }
            _settings.CurrentMetaPath = string.Empty;
            _settings.CurrentState = "Default";
            _settings.ForceStateReset = true;
            _statusMessage = "Macro cleared.";
            _statusTime = DateTime.Now;
            return;
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
                _statusTime = DateTime.Now;
                return;
            }

            LastLoadWarnings = loaded.Warnings;
            if (loaded.Rules.Count == 0)
            {
                _statusMessage = loaded.Warnings.Count > 0
                    ? $"No rules parsed — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}"
                    : "No rules found (profile?)";
                _statusTime = DateTime.Now;
                return;
            }

            lock (_settings.MetaRulesLock)
            {
                _settings.MetaRules = loaded.Rules;
                _settings.EmbeddedNavs.Clear();
                foreach (var kvp in loaded.EmbeddedNavs)
                    _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
            }
            _settings.CurrentState = loaded.Rules[0].State;
            _settings.ForceStateReset = true;
            _settings.CurrentMetaPath = filePath;

            string name = Path.GetFileNameWithoutExtension(filePath);
            int stateCount = loaded.Rules.Select(r => r.State).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _statusMessage = loaded.Warnings.Count == 0
                ? $"Loaded {loaded.Rules.Count} rules / {stateCount} states / {loaded.EmbeddedNavs.Count} navs from {name}"
                : $"Loaded {loaded.Rules.Count} rules / {stateCount} states from {name} — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}";
            _statusTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Load error: {ex.Message}";
            _statusTime = DateTime.Now;
        }
    }

    private void TryAutoSaveMeta()
    {
        _settings.MetaRulesStructuralVersion++;   // editor mutation → rebuild meta index
        if (string.IsNullOrEmpty(_settings.CurrentMetaPath))
            return;

        try
        {
            string path = _settings.CurrentMetaPath;

            // .met files can't be written — save as .af alongside
            if (Path.GetExtension(path).Equals(".met", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.ChangeExtension(path, ".af");
                _settings.CurrentMetaPath = path;
                _statusMessage = $"Converted .met → {Path.GetFileName(path)} for editing (original .met left untouched)";
                _statusTime = DateTime.Now;
            }

            lock (_settings.MetaRulesLock)
                AfFileWriter.Save(path, _settings.MetaRules, _settings.EmbeddedNavs);
        }
        catch { }
    }

    private void SaveMacroFile()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_saveName))
            {
                _statusMessage = "Enter a file name";
                _statusTime = DateTime.Now;
                return;
            }

            Directory.CreateDirectory(MetaFolder);
            string filePath = Path.Combine(MetaFolder, _saveName + ".af");
            lock (_settings.MetaRulesLock)
                AfFileWriter.Save(filePath, _settings.MetaRules, _settings.EmbeddedNavs);

            _statusMessage = $"Saved to {_saveName}.af";
            _statusTime = DateTime.Now;
            RefreshMacroFileList();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Save error: {ex.Message}";
            _statusTime = DateTime.Now;
        }
    }
}
