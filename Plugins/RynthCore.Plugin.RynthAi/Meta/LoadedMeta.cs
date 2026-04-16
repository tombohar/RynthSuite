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
}
