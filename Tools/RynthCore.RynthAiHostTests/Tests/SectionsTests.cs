using System.Collections.Generic;
using System.Linq;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Mini Remote sections (RynthCore 2026.10.5.46+): RynthCore sends its server verdicts (V lines) and
// section rail (X lines) in the "servermenu" payload, and reads RynthAi's views from
// RynthPluginGetSectionsJson. RynthAi lists only its own views (Session Stats, Pets, Crafting, Spells,
// Fellowship): since 2026-10-20 RynthILT lists its ILT tabs (Gear, Charms, Banking, Games, Vaulted
// Dungeons) in its own manifest (RynthILT.Tests); Quests are RynthOracle's, and the Character tab moved
// out (RynthOracle's Character tab, Session Stats). The manifest is checked against the engine's own
// parser and catalog (UI/Sections, linked), so the two sides can't drift.
internal static class SectionsTests
{
    public static void Register(Runner r)
    {
        r.Add("sections: V and X lines parse; groups unchanged", ParsesVerdicts);
        r.Add("sections: the engine verdict is looked up case-insensitively, and goes away with an older payload", VerdictLookup);
        r.Add("sections: RynthAi's manifest passes the engine parser unchanged", ManifestRoundTrip);
        r.Add("sections: the engine catalog places RynthAi's views by server", CatalogPlacement);
        r.Add("sections: tab surfaces map back to their tabs; foreign ids are refused", SurfaceRoundTrip);
        r.Add("sections: Character redirects to RynthOracle; Charms and Banking stay RynthILT's tabs", MovedTabRedirects);
        r.Add("mini remote: Options left-click runs the Settings command with a chat parser, else the popup", OptionsClickRouting);
        r.Add("mini remote: the header row (V/H, Options, gear) fits the narrowest remote", HeaderRowFits);
    }

    private static void MovedTabRedirects()
    {
        foreach (string tab in new[] { "character", "Char", " rates " })
            Check.Eq(MovedTabs.CommandFor(tab), MovedTabs.OracleCharacter, tab + " -> RynthOracle's Character tab");
        foreach (string tab in new[] { "charms", "banking", "gear", "games", "pets", "crafting", "", null })
            Check.True(MovedTabs.CommandFor(tab) == null, (tab ?? "null") + " is still a Mini Remote tab (or none)");
        Check.Eq(MovedTabs.OracleCharacter, "/ro character", "the Oracle command");
        Check.Eq(MovedTabs.Diagnostics, "/rc diagnostics", "the engine's Diagnostics command");
    }

    private static void OptionsClickRouting()
    {
        Check.Eq(MiniRemoteHeader.OptionsLeftClickCommand(true), "/rc ui open Settings", "a chat parser: the engine opens RynthAi Settings");
        Check.Eq(MiniRemoteHeader.OptionsLeftClickCommand(true), MovedTabs.Settings, "through MovedTabs.Settings");
        Check.True(MiniRemoteHeader.OptionsLeftClickCommand(false) == null, "no chat parser: no command, the click opens the Mini Remote popup");
        Check.True(MovedTabs.CommandFor("settings") == null, "Settings is not a moved Mini Remote tab");
        Check.Eq(MovedTabs.HomeOf(MovedTabs.Settings), "RynthAi Settings (Advanced Settings)", "its words");
    }

    private static void HeaderRowFits()
    {
        // The remote's own item spacing is 4 (MiniRemoteHud pushes it); allow a theme up to 12.
        foreach (float spacing in new[] { 3f, 4f, 8f, 12f })
            Check.True(MiniRemoteHeader.RowWidth(spacing) <= MiniRemoteHud.MinColumnWidth,
                $"header row {MiniRemoteHeader.RowWidth(spacing)} fits {MiniRemoteHud.MinColumnWidth} at spacing {spacing}");
        Check.True(MiniRemoteHeader.GearButtonWidth >= 20f, "the gear stays clickable");
    }

    private const string Payload =
        "V\tilt\tInfinite Leaftide\t1\ton\t0\n" +
        "V\taelrynth\tAelrynth\t0\tauto\t0\n" +
        "X\tchar\tChar\n" +
        "X\tgames\tGames\n" +
        "S\tilt\tInfinite Leaftide\t1\t/rc server ilt on\n" +
        "I\tProgression\t/rc progression\tPlanners.\t\n";

    private static void ParsesVerdicts()
    {
        ServerMenuPayload p = ServerMenuModel.ParsePayload(Payload);
        Check.Eq(p.Servers.Length, 2, "two verdicts");
        Check.Eq(p.Servers[0].Id, "ilt", "first verdict id");
        Check.True(p.Servers[0].Active, "ILT active");
        Check.Eq(p.Servers[0].Force, "on", "ILT forced on");
        Check.False(p.Servers[0].Detected, "ILT not detected (forced)");
        Check.False(p.Servers[1].Active, "Aelrynth inactive");
        Check.Eq(p.Sections.Length, 2, "two sections");
        Check.Eq(p.Sections[1].Id, "games", "rail order kept");
        Check.Eq(p.Groups.Length, 1, "one server group");
        Check.Eq(p.Groups[0].Items.Single().Command, "/rc progression", "the group's item");

        ServerMenuPayload junk = ServerMenuModel.ParsePayload("V\tilt\n" + "X\t\t\n" + "V\tx\ty\tmaybe\tauto\t1\n");
        Check.Eq(junk.Groups.Length, 0, "no groups from junk");
        Check.True(junk.Servers.Length <= 1, "short verdict lines skipped");
    }

    private static void VerdictLookup()
    {
        try
        {
            ServerMenuModel.Apply(Payload.Replace("V\tilt\tInfinite Leaftide\t1\ton\t0", "V\tilt\tInfinite Leaftide\t0\toff\t1"));
            Check.Eq(ServerMenuModel.ServerActive("ILT"), false, "verdict lookup is case-insensitive");
            Check.Eq(ServerMenuModel.Server("ilt")?.Force, "off", "the override comes with it");

            ServerMenuModel.Apply(Payload);
            Check.Eq(ServerMenuModel.ServerActive("ilt"), true, "forced on");

            // An older RynthCore: groups only, no verdicts (a server is forced in RynthCore's
            // Options > Diagnostics, not from the Mini Remote, since 2026-10-13).
            ServerMenuModel.Apply("S\tilt\tInfinite Leaftide\t1\t/rc ilt on\n");
            Check.True(ServerMenuModel.ServerActive("ilt") == null, "no verdicts: none to follow");
        }
        finally { ServerMenuModel.Apply(string.Empty); }
    }

    private static List<SectionsManifest.View> SomeViews() => SectionsManifest.Views();

    private static void ManifestRoundTrip()
    {
        var views = SomeViews();
        Check.False(views.Any(v => v.Section == "quests"), "no Quests view: RynthOracle has them");
        Check.True(views.All(v => v.Server.Length == 0), "every view is every world's: no server's tab is listed by RynthAi");
        foreach (string tab in new[] { "gear", "charms", "banking", "games", "vaulted", "character" })
            Check.False(views.Any(v => v.Surface == SectionsManifest.SurfaceOf(tab)), tab + ": not RynthAi's (RynthILT lists its own tabs)");
        Check.Eq(SectionsManifest.OwnSections.Length, 0, "no section of RynthAi's own (Games went with RynthILT's tabs)");

        string json = SectionsManifest.Build(views, SectionsManifest.OwnSections);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, string.Empty, "no parse error");
        Check.Eq(m.Contributions.Length, 5, "Session stats, Pets, Crafting, Spells, Fellowship");
        Check.Eq(m.Sections.Length, 0, "no own sections");
        for (int i = 0; i < views.Count; i++)
        {
            SectionsManifest.View v = views[i];
            SectionContribution c = m.Contributions.Single(x => x.Surface == v.Surface && x.Section == v.Section);
            Check.Eq(c.Label, v.Label, "label " + v.Surface);
            Check.Eq(c.Server, v.Server, "server " + v.Surface);
            Check.Eq(c.Primary, v.Primary, "primary " + v.Surface);
            Check.Eq(c.Enabled, v.Enabled, "enabled " + v.Surface);
            Check.Eq(c.Order, v.Order, "order " + v.Surface);
        }
        Check.True(json.Length < SectionManifestParser.MaxJsonLength / 16, "small");

        SectionsManifest.View session = views.Single(v => v.Surface == SectionsManifest.SessionSurface);
        Check.Eq(session.Section, "char", "session stats in the char (Session Stats) section");
        Check.Eq(session.Server, string.Empty, "every world");
        Check.True(session.Primary, "a primary view (RynthCore drops its own overview)");
    }

    private static void CatalogPlacement()
    {
        SectionManifest m = SectionManifestParser.Parse("RynthAi", SectionsManifest.Build(SomeViews(), SectionsManifest.OwnSections), out _);
        var iltOn = new ServerState("ilt", "Infinite Leaftide", true, true, ServerForce.Auto, true, "");
        var iltOff = iltOn with { Active = false };

        List<SectionView> off = SectionCatalog.Build(new[] { m }, _ => false, new[] { iltOff });
        Check.True(off.Single(v => v.Id == "pets").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.PetsSurface),
            "Pets everywhere");
        Check.True(off.Single(v => v.Id == "crafting").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.CraftingSurface),
            "Crafting everywhere");
        Check.False(off.Any(v => v.Id == "quests"), "no Quests section of RynthAi's");
        Check.False(off.Any(v => v.Id == "games"), "no Games off ILT");
        SectionView charOff = off.Single(v => v.Id == "char");
        Check.Eq(charOff.Label, SectionCatalog.SessionStatsLabel, "the char section is Session Stats");
        Check.True(charOff.Panes.Length == 1 && charOff.Panes[0].Contribution?.Surface == SectionsManifest.SessionSurface,
            "Session Stats: RynthAi's session view replaces the engine overview");

        // On Infinite Leaftide RynthAi's own manifest still adds no ILT pane: RynthILT's manifest does.
        List<SectionView> on = SectionCatalog.Build(new[] { m }, _ => false, new[] { iltOn });
        SectionView charOn = on.Single(v => v.Id == "char");
        Check.True(charOn.Panes.Length == 1 && charOn.Panes[0].Contribution?.Surface == SectionsManifest.SessionSurface,
            "Session Stats on ILT: session stats only (no ILT Character tab)");
        SectionView inventoryOn = on.Single(v => v.Id == "inventory");
        Check.False(inventoryOn.Panes.Any(p => p.Contribution?.Plugin == "RynthAi" && p.Title is "Gear" or "Charms" or "Banking"),
            "Inventory on ILT: none of RynthILT's tabs come from RynthAi");
        Check.False(on.Any(v => v.Id == "games" && v.Panes.Any(p => p.Contribution?.Plugin == "RynthAi")), "no Games pane from RynthAi");
        Check.True(on.Single(v => v.Id == "spells").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.SpellsSurface), "Spells");
        Check.True(on.Single(v => v.Id == "fellowship").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.FellowshipSurface), "Fellowship");
    }

    private static void SurfaceRoundTrip()
    {
        Check.Eq(SectionManifestParser.Surface(SectionsManifest.SessionSurface), SectionsManifest.SessionSurface, "the engine accepts the session surface");
        Check.True(SectionsManifest.TryOwnTabOf(SectionsManifest.PetsSurface, out string pets) && pets == MiniRemoteTabs.Pets, "pets is RynthAi's");
        Check.True(SectionsManifest.TryOwnTabOf(SectionsManifest.CraftingSurface, out string craft) && craft == MiniRemoteTabs.Crafting, "crafting is RynthAi's");
        foreach (string tab in new[] { "gear", "charms", "banking", "games", "vaulted" })
            Check.False(SectionsManifest.TryOwnTabOf(SectionsManifest.SurfaceOf(tab), out _), tab + " isn't RynthAi's to draw");
        Check.False(SectionsManifest.TryOwnTabOf(SectionsManifest.SpellsSurface, out _), "spells isn't a tab");
        Check.False(SectionsManifest.TryOwnTabOf("section:nonsense", out _), "unknown");
    }
}
