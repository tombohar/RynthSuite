// RynthAiPlugin.ProfileFiles.cs - the Loaded files drawer's profile buttons and their /ra verbs
// (2026-11-11).
//
// "I can't find a way to save profiles": saving, copying and making profiles was chat commands
// (/vt nav save, /ra settings savechar ...) and a new loot profile needed the external editor. The
// dashboard's Loaded files drawer now has a "..." key on each row (PROFILE, NAV, LOOT, META, BUFFS)
// that opens that row's file buttons: Save, Save As (a name, and a yes before replacing another
// file), New, Rename, Move to Deleted and Open folder; LOOT opens the Loot Editor (New opens it on a
// new, empty profile that its Save creates) and BUFFS the Spells window, where buff profiles are
// made. Each button runs RunProfileAction on the tick, as the matching /ra verb does
// (/ra nav|meta|settings|loot ...), so both say the same thing in chat.
//
// The file work is LegacyDashboardRenderer.ProfileFiles.cs and LegacyUi/ProfileFileOps.cs; the
// loads and saves are the ones the pickers and the older commands use. Formats and folders are
// unchanged; nothing is deleted (a profile removed from a list goes to the Deleted folder beside it).
using System;
using System.IO;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The Loot Editor has unsaved edits in the loot profile in use (tick writes, render reads).</summary>
    private volatile bool _dashLootEditorDirty;

    internal bool DashLootEditorDirty => _dashLootEditorDirty;

    /// <summary>The Spells window has unsaved edits (render thread, where it draws).</summary>
    internal bool DashSpellsDirty => _spellsUi?.Dirty == true;

    /// <summary>Plugin tick, twice a second while the dashboard shows.</summary>
    private void RefreshDashProfileState()
    {
        string loot = _dashboard?.Settings?.CurrentLootPath ?? string.Empty;
        _dashLootEditorDirty = _lootEditor != null && loot.Length > 0 && _lootEditor.IsDirtyFor(loot);
    }

    /// <summary>
    /// One profile button or verb (plugin tick): <paramref name="name"/> is the name typed for
    /// Save As, New and Rename; <paramref name="replace"/> is the button's yes to replacing another
    /// file with Save As. The result is the chat line.
    /// </summary>
    internal ProfileResult RunProfileAction(ProfileKind kind, ProfileVerb verb, string? name, bool replace = false)
    {
        var dash = _dashboard;
        if (dash == null || !dash.ProfileLoaded)
            return ProfileResult.Failed("Settings not ready (log in first).");

        if (kind == ProfileKind.Loot && verb is ProfileVerb.SaveAs or ProfileVerb.Rename or ProfileVerb.Delete)
        {
            string inUse = dash.ProfilePath(ProfileKind.Loot);
            if (_lootEditor != null && inUse.Length > 0 && _lootEditor.IsDirtyFor(inUse))
                return ProfileResult.Failed($"The Loot Editor has unsaved changes in {Path.GetFileName(inUse)}. Save or discard them there first.");
        }

        switch (verb)
        {
            case ProfileVerb.Save:
                return dash.SaveProfileNow(kind);
            case ProfileVerb.SaveAs:
                return dash.SaveProfileAs(kind, name, replace);
            case ProfileVerb.New:
                return kind == ProfileKind.Loot ? NewLootProfile(dash, name) : dash.NewProfile(kind, name);
            case ProfileVerb.Rename:
            {
                string before = dash.ProfilePath(kind);
                ProfileResult r = dash.RenameProfile(kind, name);
                if (r.Ok && kind == ProfileKind.Loot) ForgetLootProfile(before);
                return r;
            }
            case ProfileVerb.Delete:
            {
                string before = dash.ProfilePath(kind);
                ProfileResult r = dash.DeleteProfile(kind);
                if (r.Ok && kind == ProfileKind.Loot) ForgetLootProfile(before);
                return r;
            }
            case ProfileVerb.Folder:
                return dash.OpenProfileFolder(kind);
            default:
                return OpenProfileEditor(kind);
        }
    }

    /// <summary>
    /// New loot profile: the Loot Editor opened on LootProfiles\&lt;name&gt;.utl, empty; its Save
    /// writes the file (nothing is written before). RynthAi keeps looting with the profile it has
    /// until the new one is picked under LOOT.
    /// </summary>
    private ProfileResult NewLootProfile(LegacyDashboardRenderer dash, string? name)
    {
        string? why = dash.NewLootProfilePath(name, out string path);
        if (why != null) return ProfileResult.Failed(why);
        if (!Host.HasSetPanelOpen)
            return ProfileResult.Failed("This RynthCore can't open the Loot Editor from here; open it from the LOOT row's chest button.");
        if (!LootEditor.OpenFile(path, out string message))
            return ProfileResult.Failed($"Loot Editor: {message}");
        Host.SetPanelOpen("Loot Editor", RynthPanelOpen.Open);
        return ProfileResult.Done($"New loot profile {Path.GetFileName(path)} is open in the Loot Editor: add rules, then Save there to create it. Pick it under LOOT to loot with it.");
    }

    /// <summary>/ra loot edit: the Loot Editor on the loot profile in use.</summary>
    private ProfileResult OpenProfileEditor(ProfileKind kind)
    {
        if (kind != ProfileKind.Loot) return ProfileResult.Failed($"The {LegacyDashboardRenderer.KindWord(kind)} is edited in its own window.");
        if (!Host.HasSetPanelOpen) return ProfileResult.Failed("This RynthCore can't open the Loot Editor from here.");
        string inUse = _dashboard?.ProfilePath(ProfileKind.Loot) ?? string.Empty;
        if (inUse.Length > 0) LootEditor.OpenFile(inUse, out _);
        Host.SetPanelOpen("Loot Editor", RynthPanelOpen.Open);
        return ProfileResult.Done(inUse.Length > 0
            ? $"Loot Editor opened on {Path.GetFileName(inUse)}."
            : "Loot Editor opened. No loot profile is in use: New makes one, or pick one under LOOT.");
    }

    /// <summary>A profile verb's reply: what it did, or why not plus <paramref name="usage"/>.</summary>
    private void ReplyProfile(ProfileResult r, string usage)
    {
        if (r.Ok) ReplyOk(r.Text);
        else ReplyFail(r.NeedsReplace
            ? r.Text.Replace(" Replace it?", " Not saved: commands never replace a file; pick another name (the Save As button asks first).", StringComparison.Ordinal)
            : r.Text + " Usage: " + usage);
    }

    /// <summary>
    /// The profile verbs shared by /ra nav, /ra meta, /ra settings and /ra loot: saveas, rename,
    /// delete (to Deleted), folder, and save / new where the kind has them. False when
    /// <paramref name="sub"/> isn't one of them.
    /// </summary>
    private bool TryProfileVerb(ProfileKind kind, string sub, string rest, string usage)
    {
        ProfileVerb verb;
        switch (sub)
        {
            case "saveas": verb = ProfileVerb.SaveAs; break;
            case "rename": verb = ProfileVerb.Rename; break;
            case "delete": verb = ProfileVerb.Delete; break;
            case "folder": verb = ProfileVerb.Folder; break;
            case "save" when kind is ProfileKind.Meta or ProfileKind.Settings: verb = ProfileVerb.Save; break;
            case "new" when kind is ProfileKind.Nav or ProfileKind.Loot: verb = ProfileVerb.New; break;
            case "edit" when kind == ProfileKind.Loot: verb = ProfileVerb.Edit; break;
            default: return false;
        }
        if ((verb is ProfileVerb.SaveAs or ProfileVerb.Rename or ProfileVerb.New) && rest.Trim().Length == 0)
        {
            ReplyFail($"[RynthAi] {sub} needs a name. Usage: {usage}");
            return true;
        }
        ReplyProfile(RunProfileAction(kind, verb, rest, replace: false), usage);
        return true;
    }
}
