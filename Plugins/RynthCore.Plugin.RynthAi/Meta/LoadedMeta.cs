using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// Result of loading a .af or .met file: rules plus any embedded NAV routes
/// that ship inside the macro. Embedded navs stay in memory — they are never
/// extracted to the NavProfiles folder.
/// </summary>
internal sealed class LoadedMeta
{
    public List<MetaRule> Rules { get; set; } = new();

    public Dictionary<string, List<string>> EmbeddedNavs { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Non-fatal parse problems (unknown keywords, parse exception). Surfaced
    /// to the user on load so a typo'd / unsupported meta is visible instead of
    /// silently producing zero or partial rules.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Every "STATE: {name}" the .af declares, in file order, including states with no rules
    /// (a .met has no empty states: its states only exist through rules, so this stays empty).
    /// </summary>
    public List<string> DeclaredStates { get; } = new();

    /// <summary>
    /// A blank meta: it declares a state, has no rules and parsed cleanly (what
    /// /ra meta load and /vt meta load create for a name that doesn't exist, 2026-10-13).
    /// It is loaded like any meta (the old rules are cleared), where a file with no rules
    /// and no state (a profile, an empty file) is still refused.
    /// </summary>
    public bool IsBlank => Rules.Count == 0 && DeclaredStates.Count > 0 && Warnings.Count == 0;

    /// <summary>
    /// The state a freshly loaded meta starts in. VTank always starts a meta in
    /// "Default"; RynthAi used the first rule's state, and its writer sorts
    /// states by name, so a saved meta began mid-sequence (e.g. ApostateFinal
    /// in '1.0 To Sparkling Apostate Shard'). Falls back to the first rule's
    /// state when there is no Default.
    /// </summary>
    public string StartState
    {
        get
        {
            foreach (var r in Rules)
                if (string.Equals(r.State, "Default", StringComparison.OrdinalIgnoreCase))
                    return r.State;
            if (Rules.Count > 0) return Rules[0].State;
            foreach (string s in DeclaredStates)
                if (string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
                    return s;
            return DeclaredStates.Count > 0 ? DeclaredStates[0] : "Default";
        }
    }
}
