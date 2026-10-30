using System;
using System.IO;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>About: the credit to Oracle of Dereth's author, the settings and the quest list's source.</summary>
internal sealed partial class OracleView
{
    private void DrawAbout(UiWindow w)
    {
        w.TextColored(UiColors.Gold, "Based on Oracle of Dereth by Advis Eveldan - thank you!");
        w.TextDisabled("github.com/advis61/OracleOfDereth (MIT licence). The quest, augmentation, society, title and marker lists are Advis's.");
        w.Text($"RynthOracle {RynthOraclePlugin.Version}");

        OracleSettings s = _c.Settings;
        w.SeparatorText("Settings");
        w.Checkbox("Read quest flags when you log in", "o.readlogin", s.ReadQuestsAtLogin, v => { s.ReadQuestsAtLogin = v; s.Save(); });
        w.Tooltip("Sends /myquests once after login so the quest pages have data.");
        w.Checkbox("Hide the replies to RynthOracle's own requests", "o.hide", s.HideOwnQuestReplies, v => { s.HideOwnQuestReplies = v; s.Save(); });
        w.Tooltip("/myquests. A command you type yourself always shows its reply.");
        w.Checkbox("Show the Void window for characters with Void Magic", "o.void", s.AutoShowVoidWindow, v => { s.AutoShowVoidWindow = v; s.Save(); });
        w.Checkbox("Say in chat when a quest timer comes up", "o.alert", s.AlertTimers, v => { s.AlertTimers = v; s.Save(); });
        w.Tooltip("Watches the quests you track (the * on the Quests tab) and prints a line the moment a cooldown ends.");
        w.Checkbox("...for every quest you hold, not just tracked ones", "o.alertall", s.AlertAllTimers, v => { s.AlertAllTimers = v; s.Save(); });
        w.Checkbox("Check once a day for a newer quest list", "o.daily", s.CheckQuestListDaily, v => { s.CheckQuestListDaily = v; s.Save(); });
        w.Tooltip("Downloads Oracle of Dereth's master quest list from GitHub, as the original plugin does.");

        w.SeparatorText("Server");
        ServerIdentity sv = _c.Server;
        QuestCatalog pc = _c.Catalog;
        w.Text(sv.ServerId.Length == 0 ? "No server active (retail quests only)."
            : pc.PackServer.Length > 0 ? $"{sv.DisplayName}: its quest pack is on ({pc.ServerCount} {pc.PackServer} quests, {pc.PackSource})."
            : $"{sv.DisplayName}: no quest pack ({(sv.Plugin.Length > 0 ? sv.Plugin + " offers none" : "no server plugin loaded for it")}; retail quests only).");
        w.TextDisabled("A server's quest pack comes from its server plugin, and its own pages are that plugin's: the Mini Remote's Server section, or /rserver.");
        w.TextDisabled($"How it was decided: {sv.Source}");
        // RynthCore's server list (API v86) and the active server's settings from the plugin that owns it.
        RynthCore.Plugin.Shared.ServerStateClient st = _c.ServerState;
        if (st.State is { } es)
        {
            w.TextDisabled(es.HasActive
                ? $"RynthCore: {es.ActiveName} ({es.Active}){(es.Picked ? ", picked by hand" : "")}; settings from {(es.ActivePlugin.Length > 0 ? es.ActivePlugin : "no plugin")}"
                : "RynthCore: no server active" + (es.Picked ? " (picked by hand)" : ""));
            if (RynthCore.Plugin.Shared.ServerProfileView.Parse(st.ProfileJson) is { } prof)
                w.TextDisabled($"{st.ProfilePlugin} ({RynthCore.Plugin.Shared.RynthServerProfileApiV1.InterfaceName}): {prof.Summary()}");
            else if (es.ActivePlugin.Length > 0)
                w.TextDisabled($"{es.ActivePlugin} hasn't sent its server settings (an older plugin?).");
            w.TextDisabled("Change the server: RynthCore Options > Diagnostics > Server, or /rc server pick <id>|auto.");
        }
        else if (_c.Host.Version > 0 && _c.Host.Version < 86)
            w.TextDisabled($"This engine is API v{_c.Host.Version}; server quest packs need v86 (RynthCore's server list).");
        if (_c.Host.Version > 0 && _c.Host.Version < 75) w.TextDisabled($"This engine is API v{_c.Host.Version}; v75 adds the titles you hold.");

        w.SeparatorText("Quest list");
        QuestCatalog cat = _c.Catalog;
        w.Text($"{cat.RetailCount} retail quests from the {cat.Source}" +
               (cat.PackServer.Length > 0 ? $", plus {cat.ServerCount} {cat.PackServer} quests ({cat.PackSource})" : ""));
        if (cat.HiddenCount > 0) w.TextDisabled($"{cat.HiddenCount} retail quests are hidden: {cat.PackServer}'s /myquests can't show their flags.");
        if (s.LastQuestListCheckUtc != DateTime.MinValue)
            w.TextDisabled($"Last checked {s.LastQuestListCheckUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (_c.Updater.Running) w.TextColored(UiColors.Yellow, "Checking...");
        else w.Button("Check for a newer list now", "o.check", () => StartQuestListCheck());
        if (!_c.Catalog.UsingBundled)
        {
            w.SameLine();
            w.Button("Go back to the bundled list", "o.bundled", UseBundledList);
        }
        if (_c.UpdaterMessage.Length > 0) w.TextWrapped(_c.UpdaterMessage);

        w.SeparatorText("Commands");
        w.BulletText("/ro  open or close this window");
        w.BulletText("/ro character  the Character tab (status, session)");
        w.BulletText("/ro skills  the Skills tab (raise and train); /ro attributes, /ro progress likewise");
        w.BulletText("/ro timers  open or close the Quest Timers window");
        w.BulletText("/ro fellowship  the Fellowship tab");
        w.BulletText("/ro trackers  the Quests tab, trackers only (Legendary, markers)");
        w.BulletText("/ro legendary  John's Legendary Key quests: have, missing, repeat timers");
        w.BulletText("/ro markers  the 100 exploration markers: found and still to find");
        w.BulletText("/ro about  this tab (settings, server, quest list)");
        w.BulletText("/ro void  open or close the Void window");
        w.BulletText("/ro quests  read your quest flags now");
        w.BulletText("/ro help  list the commands in chat");
        w.TextDisabled("A server quest pack can be replaced without a rebuild: put quests.<server id>.csv in %APPDATA%\\RynthCore\\RynthOracle\\packs.");
    }

    private void StartQuestListCheck()
    {
        _c.Settings.LastQuestListCheckUtc = DateTime.UtcNow;
        _c.Settings.Save();
        _c.UpdaterMessage = "";
        _c.Updater.Start();
    }

    private void UseBundledList()
    {
        try { File.Delete(QuestCatalog.DownloadedPath); }
        catch (Exception ex) { _c.UpdaterMessage = "Couldn't remove the downloaded list: " + ex.Message; return; }
        _c.Catalog.Reload(null);
        _c.Catalog.SyncDiscoveries(_c.Flags);
        _c.UpdaterMessage = "Using the bundled quest list.";
    }
}
