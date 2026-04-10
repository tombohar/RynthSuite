using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyMetaUi
{
    private readonly LegacyUiSettings _settings;
    private readonly List<string> _navFiles;

    private bool _showMetaEditor;
    private MetaRule _editingRule = new();
    private int _editingRuleIndex = -1;
    private string _newStateName = "";
    private bool _openCreateStatePopup;

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
        "Main Health <=", "Main Health % >=", "Main Mana <=", "Main Mana % >=", "Main Stam <="
    };

    private readonly string[] _metaActionNames =
    {
        "None", "Chat Command", "Set Meta State", "Embedded Nav Route", "All",
        "Call Meta State", "Return From Call", "Expression Action", "Chat Expression",
        "Set Watchdog", "Clear Watchdog", "Get NT Option", "Set NT Option",
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
        ImGui.PushStyleColor(ImGuiCol.PopupBg,  new Vector4(0.08f, 0.10f, 0.13f, 1.0f));

        ImGui.SetNextWindowSize(new Vector2(600, 460), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Macro Rules##RynthAiMacroRules", ref DashWindows.ShowMacroRules))
            RenderContents();
        ImGui.End();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    private void RenderContents()
    {
        if (ImGui.BeginTable("MetaRulesTable", 6,
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, -35)))
        {
            ImGui.TableSetupColumn("Up",        ImGuiTableColumnFlags.WidthFixed,   20);
            ImGui.TableSetupColumn("Dn",        ImGuiTableColumnFlags.WidthFixed,   20);
            ImGui.TableSetupColumn("Del",       ImGuiTableColumnFlags.WidthFixed,   25);
            ImGui.TableSetupColumn("State",     ImGuiTableColumnFlags.WidthFixed,  100);
            ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            var groupedRules = _settings.MetaRules
                .GroupBy(r => r.State)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in groupedRules)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(3);
                ImGui.TextColored(new Vector4(0, 1, 1, 1), $"--- State: {group.Key} ---");

                var stateRules = group.ToList();
                for (int i = 0; i < stateRules.Count; i++)
                {
                    var rule = stateRules[i];
                    int globalIdx = _settings.MetaRules.IndexOf(rule);

                    ImGui.TableNextRow();

                    bool isFired = rule.HasFired;
                    if (isFired) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.2f, 0.2f, 1f));

                    ImGui.TableNextColumn();
                    if (i > 0 && ImGui.Button($"^##up{globalIdx}"))
                    {
                        var prevRule = stateRules[i - 1];
                        int prevGlobalIdx = _settings.MetaRules.IndexOf(prevRule);
                        _settings.MetaRules[globalIdx] = prevRule;
                        _settings.MetaRules[prevGlobalIdx] = rule;
                    }

                    ImGui.TableNextColumn();
                    if (i < stateRules.Count - 1 && ImGui.Button($"v##dn{globalIdx}"))
                    {
                        var nextRule = stateRules[i + 1];
                        int nextGlobalIdx = _settings.MetaRules.IndexOf(nextRule);
                        _settings.MetaRules[globalIdx] = nextRule;
                        _settings.MetaRules[nextGlobalIdx] = rule;
                    }

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0, 0, 1));
                    if (ImGui.Button($"X##delm{globalIdx}"))
                        _settings.MetaRules.Remove(rule);
                    ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{rule.State}##edit{globalIdx}", false, ImGuiSelectableFlags.SpanAllColumns))
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

                    ImGui.TableNextColumn();
                    ImGui.Text(rule.Condition.ToString() +
                        (string.IsNullOrEmpty(rule.ConditionData) ? "" : $": {rule.ConditionData}"));

                    ImGui.TableNextColumn();
                    if (rule.Action == MetaActionType.All)
                    {
                        int childCount = rule.ActionChildren?.Count ?? 0;
                        if (childCount == 0) childCount = rule.Children?.Count ?? 0; // legacy fallback
                        ImGui.TextColored(new Vector4(1, 1, 0, 1), $"All: [{childCount} actions]");
                    }
                    else if (rule.Action == MetaActionType.EmbeddedNavRoute &&
                             !string.IsNullOrEmpty(rule.ActionData) && rule.ActionData.Contains(';'))
                    {
                        var parts = rule.ActionData.Split(';');
                        ImGui.Text($"EmbeddedNavRoute: {parts[0]} ({parts[1]} pts)");
                    }
                    else
                    {
                        ImGui.Text(rule.Action.ToString() +
                            (string.IsNullOrEmpty(rule.ActionData) ? "" : $": {rule.ActionData}"));
                    }

                    if (isFired) ImGui.PopStyleColor();
                }
            }
            ImGui.EndTable();
        }

        ImGui.Separator();

        bool isMetaEnabled = _settings.EnableMeta;
        if (ImGui.Checkbox("Enable Meta", ref isMetaEnabled))
            _settings.EnableMeta = isMetaEnabled;

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
    }

    private void RenderMetaEditorPopup()
    {
        if (!_showMetaEditor) return;

        ImGui.SetNextWindowSize(new Vector2(650, 450), ImGuiCond.Appearing);
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
            if (_editingRuleIndex == -1) _settings.MetaRules.Add(_editingRule);
            else _settings.MetaRules[_editingRuleIndex] = _editingRule;
            _showMetaEditor = false;
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
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##InvName", ref itemName, 64))
                    rule.ConditionData = $"{itemName},{itemCount}";

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
                    case MetaConditionType.NoMonstersWithinDistance:
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
                }
            }

            if (needsData)
            {
                float availableSpace = ImGui.GetWindowWidth() - ImGui.GetCursorPosX() - 40f;
                ImGui.SetNextItemWidth(Math.Max(50f, availableSpace));

                string cData = rule.ConditionData ?? "";
                if (ImGui.InputTextWithHint("##Data", condHint, ref cData, 256))
                    rule.ConditionData = cData;
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
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputTextWithHint("##Cmd", "e.g. /tell {1} hello!", ref cmd, 256))
                        rule.ActionData = cmd;
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
                        foreach (var navFile in _navFiles)
                        {
                            if (navFile == "None") continue;
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

                case MetaActionType.SetNTOption:
                    string[] ntParts = (rule.ActionData ?? "EnableBuffing;False").Split(';');
                    string ntO = ntParts[0];
                    string ntV = ntParts.Length > 1 ? ntParts[1] : "False";

                    ImGui.Text("Option:");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputText("##NTOpt", ref ntO, 64)) rule.ActionData = $"{ntO};{ntV}";

                    ImGui.SameLine();
                    ImGui.Text("Val:");
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("##NTVal", ref ntV, 64)) rule.ActionData = $"{ntO};{ntV}";
                    break;

                case MetaActionType.ExpressionAction:
                case MetaActionType.ChatExpression:
                    string expr = rule.ActionData ?? "";
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputTextWithHint("##Expr", "Expression (e.g. [SlotCount] < 5)", ref expr, 256))
                        rule.ActionData = expr;
                    break;

                case MetaActionType.GetNTOption:
                case MetaActionType.CreateView:
                case MetaActionType.DestroyView:
                    string vData = rule.ActionData ?? "";
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("##ViewData", ref vData, 128))
                        rule.ActionData = vData;
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

    private static void ClearConditionDataIfNeeded(MetaRule rule)
    {
        switch (rule.Condition)
        {
            case MetaConditionType.Always:
            case MetaConditionType.Never:
            case MetaConditionType.CharacterDeath:
            case MetaConditionType.AnyVendorOpen:
            case MetaConditionType.VendorClosed:
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
}
