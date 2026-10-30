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
    public bool Enabled { get; set; } = true;
    /// <summary>The rule's name; null from an engine without rule names (an update then keeps the
    /// name the rule has, so a mismatched engine never wipes names).</summary>
    public string? Name { get; set; }
}

internal sealed class MetaCommand
{
    public string Op { get; set; } = string.Empty;
    public int Index { get; set; } = -1;
    public string Value { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public MetaRuleDto? Rule { get; set; }
    /// <summary>The Meta Manager's mm_add / mm_update rule.</summary>
    public RynthCore.Plugin.RynthAi.Meta.MetaScheduleRule? ScheduleRule { get; set; }
}
