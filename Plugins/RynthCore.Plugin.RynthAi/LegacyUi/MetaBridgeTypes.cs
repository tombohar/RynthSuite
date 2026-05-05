using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class MetaRuleDto
{
    public string State { get; set; } = "Default";
    public int Condition { get; set; }
    public string ConditionData { get; set; } = string.Empty;
    public int Action { get; set; }
    public string ActionData { get; set; } = string.Empty;
    public List<MetaRuleDto> Children { get; set; } = new();
    public List<MetaRuleDto> ActionChildren { get; set; } = new();
}

internal sealed class MetaCommand
{
    public string Op { get; set; } = string.Empty;
    public int Index { get; set; } = -1;
    public string Value { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public MetaRuleDto? Rule { get; set; }
}
