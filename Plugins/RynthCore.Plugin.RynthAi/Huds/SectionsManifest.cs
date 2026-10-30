// SectionsManifest.cs - What RynthAi adds to RynthCore's Mini Remote sections.
//
// RynthCore 2026.10.5.46+ shows the Mini Remote as a rail of sections: its core twelve (Session
// Stats - id "char" -, Map, Skills, Combat, Nav, Inventory, Crafting, Spells, Chat, Friends,
// Fellowship, Pets) and any a plugin adds. It reads each plugin's optional
// RynthPluginGetSectionsJson export every couple of seconds (schema: UI/Sections/SectionManifest.cs
// in RynthCore). RynthAi fills, on every world: Session Stats (the one session: RynthTracker,
// RynthOracle and RynthILT read the same numbers, and RynthCore drops its own overview for it),
// Pets, Crafting, Spells and Fellowship (the views of those core sections).
// Nothing server-specific is listed here: a server plugin lists its own views (since 2026-10-20
// RynthILT lists its Mini Remote tabs, Gear / Charms / Banking / Games / Vaulted Dungeons, and its
// Games section itself; RynthAi hosted them until then). Quests are RynthOracle's (since 2026-10-13).
// Each view is a surface "section:<name>" RynthCore draws through RynthPluginRenderEmbed.
//
// Pure, so the host tests build it offline.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.MiniRemote;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class SectionsManifest
{
    /// <summary>Prefix of every RynthAi section surface ("section:pets").</summary>
    public const string SurfacePrefix = "section:";

    public const string PetsSurface = SurfacePrefix + MiniRemoteTabs.Pets;
    public const string CraftingSurface = SurfacePrefix + MiniRemoteTabs.Crafting;
    public const string SpellsSurface = SurfacePrefix + "spells";
    public const string FellowshipSurface = SurfacePrefix + "fellowship";
    /// <summary>The session stats (RynthCore's Session Stats section, "char").</summary>
    public const string SessionSurface = SurfacePrefix + "session";

    /// <summary>One view RynthAi puts in a section.</summary>
    internal sealed record View(string Section, string Server, string Label, string Surface, bool Primary,
                                Vector2 Size, bool Enabled, string Note, string Tooltip, int Order);

    /// <summary>A section of RynthAi's own (beyond RynthCore's core twelve).</summary>
    internal sealed record Section(string Id, string Label, int Order);

    /// <summary>RynthAi's own sections beyond the core ones: none (Games went with RynthILT's tabs, 2026-10-20).</summary>
    public static readonly Section[] OwnSections = Array.Empty<Section>();

    /// <summary>The surface name of a tab's view ("section:pets").</summary>
    public static string SurfaceOf(string tabId) => SurfacePrefix + tabId;

    /// <summary>Every view RynthAi offers (the same on every world).</summary>
    public static List<View> Views()
    {
        var views = new List<View>
        {
            // Every world. Session Stats first: a universal primary view, so RynthCore drops its own overview.
            new("char", string.Empty, "Session stats", SessionSurface, true, new Vector2(360, 240), true, string.Empty,
                "XP, luminance and kills this session, per hour (RynthAi; RynthTracker, RynthOracle and RynthILT show the same session).", 100),
            new("pets", string.Empty, "Pets", PetsSurface, true, MiniRemoteTabs.Size(MiniRemoteTabs.Pets), true, string.Empty,
                "Combat pets: the summon out, essences, and the pet picker (RynthAi).", 100),
            new("crafting", string.Empty, "Crafting", CraftingSurface, true, MiniRemoteTabs.Size(MiniRemoteTabs.Crafting), true, string.Empty,
                "Tinkering benches: retail tinks, your own rows and the server plugin's salvage (RynthAi).", 100),
            new("spells", string.Empty, "Spells", SpellsSurface, true, new Vector2(900, 560), true, string.Empty,
                "The spell catalog and buff profiles (RynthAi).", 100),
            new("fellowship", string.Empty, "Fellowship", FellowshipSurface, true, new Vector2(380, 360), true, string.Empty,
                "Your fellowship: members, leader, XP sharing (RynthAi; also RynthOracle's Fellowship tab).", 100),
        };
        return views;
    }

    /// <summary>
    /// RynthAi's dashboard window (RynthAiPlugin.Dashboard.cs, 2026-10-21): the engine's "RynthAi" panel's
    /// place ("takeOver": its saved spot, bar button, hotkeys and /rc commands), drawn whole by RynthAi
    /// ("chrome": "none") and as tall as it draws ("fitHeight"), plugin API v90. The Robot icon is the
    /// engine bar's for RynthAi.
    /// </summary>
    public const string DashboardTitle = "RynthAi", DashboardSurface = "window:dashboard";

    /// <summary>
    /// The manifest JSON for <paramref name="views"/> (UTF-8 text, schema version 1), with the dashboard
    /// window when <paramref name="dashboard"/>.
    /// </summary>
    public static string Build(IReadOnlyList<View> views, IReadOnlyList<Section> sections, bool dashboard = false)
    {
        using var stream = new MemoryStream(4096);
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            if (dashboard)
            {
                w.WriteStartArray("windows");
                w.WriteStartObject();
                w.WriteString("id", "dashboard");
                w.WriteString("title", DashboardTitle);
                w.WriteString("surface", DashboardSurface);
                w.WriteString("icon", "");
                w.WriteNumber("width", 296);
                w.WriteNumber("height", 240);
                w.WriteNumber("minWidth", 280);
                w.WriteNumber("minHeight", 54);
                w.WriteBoolean("bar", true);
                w.WriteBoolean("edgeToEdge", true);
                w.WriteBoolean("takeOver", true);
                w.WriteString("chrome", "none");
                w.WriteBoolean("fitHeight", true);
                w.WriteEndObject();
                w.WriteEndArray();
            }
            w.WriteStartArray("sections");
            foreach (Section s in sections)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("label", s.Label);
                w.WriteNumber("order", s.Order);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("contributions");
            foreach (View v in views)
            {
                w.WriteStartObject();
                w.WriteString("section", v.Section);
                w.WriteString("server", v.Server);
                w.WriteString("label", v.Label);
                w.WriteString("surface", v.Surface);
                w.WriteBoolean("primary", v.Primary);
                if (v.Size.X > 0) w.WriteNumber("width", v.Size.X);
                if (v.Size.Y > 0) w.WriteNumber("height", v.Size.Y);
                w.WriteBoolean("enabled", v.Enabled);
                if (v.Note.Length > 0) w.WriteString("note", v.Note);
                if (v.Tooltip.Length > 0) w.WriteString("tooltip", v.Tooltip);
                w.WriteNumber("order", v.Order);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The Mini Remote tab of RynthAi's own a surface draws ("pets", "crafting"), if it is one.</summary>
    public static bool TryOwnTabOf(string surface, out string tabId)
    {
        tabId = surface switch
        {
            PetsSurface => MiniRemoteTabs.Pets,
            CraftingSurface => MiniRemoteTabs.Crafting,
            _ => string.Empty,
        };
        return tabId.Length > 0;
    }

}
