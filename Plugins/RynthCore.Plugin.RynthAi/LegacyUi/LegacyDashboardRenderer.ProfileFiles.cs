using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>The kinds of profile the Loaded files drawer lists (the numbers of <see cref="LegacyDashboardRenderer.SelectProfileAtIndex"/>).</summary>
internal enum ProfileKind { Nav = 0, Loot = 1, Meta = 2, Settings = 3, Buffs = 4 }

/// <summary>What a profile button or its /ra verb does.</summary>
internal enum ProfileVerb { Save, SaveAs, New, Rename, Delete, Folder, Edit }

/// <summary>
/// The result of a profile button or verb: the chat line ("[RynthAi] ..."), whether it worked, and
/// for Save As onto another file, that it needs the player's yes first (nothing was written).
/// </summary>
internal readonly record struct ProfileResult(bool Ok, string Text, bool NeedsReplace = false)
{
    public static ProfileResult Done(string text) => new(true, "[RynthAi] " + text);
    public static ProfileResult Failed(string text) => new(false, "[RynthAi] " + text);
}

/// <summary>What the profile row shows: the name in use, its file, unsaved changes, the folder.</summary>
internal sealed record ProfileFileInfo(string Name, string Path, bool HasFile, bool Unsaved, string Folder);

// The Loaded files drawer's profile buttons (2026-11-11): Save, Save As, New, Rename, Move to
// Deleted and Open folder for the settings profile, the nav, the meta and the loot profile, and
// the /ra verbs that do the same. The loads and saves are the ones the pickers and the commands
// already use (SaveRoute, LoadNavByName, LoadMetaByName, SaveAsProfile, the meta's Save As);
// ProfileFileOps does the renames and the moves into Deleted. Plugin tick, except ProfileInfo.
internal sealed partial class LegacyDashboardRenderer
{
    internal static readonly string[] NavExtensions = NewProfileFiles.NavExtensions;
    internal static readonly string[] MetaExtensions = NewProfileFiles.MetaExtensions;
    internal static readonly string[] LootExtensions = { ".utl", ".json" };
    internal static readonly string[] SettingsExtensions = { ".json" };

    /// <summary>The settings profile everyone has: it can't be renamed or moved to Deleted.</summary>
    internal const string DefaultProfileName = "Default";

    /// <summary>
    /// Files in the character's folder that aren't settings profiles (RefreshProfilesList skips
    /// them): a settings profile can't take one of these names.
    /// </summary>
    internal static bool IsReservedSettingsName(string name) =>
        name.Equals("settings", StringComparison.OrdinalIgnoreCase)
        || name.Equals("monsters", StringComparison.OrdinalIgnoreCase)
        || name.Equals("metamanager", StringComparison.OrdinalIgnoreCase);

    internal static string KindWord(ProfileKind k) => k switch
    {
        ProfileKind.Nav => "nav",
        ProfileKind.Loot => "loot profile",
        ProfileKind.Meta => "meta",
        ProfileKind.Settings => "settings profile",
        _ => "buff profile",
    };

    internal static IReadOnlyList<string> ExtensionsOf(ProfileKind k) => k switch
    {
        ProfileKind.Nav => NavExtensions,
        ProfileKind.Loot => LootExtensions,
        ProfileKind.Meta => MetaExtensions,
        _ => SettingsExtensions,
    };

    /// <summary>The folder the kind's picker lists (settings: this character's folder; empty before login).</summary>
    internal string ProfileFolder(ProfileKind k) => k switch
    {
        ProfileKind.Nav => _navFolder,
        ProfileKind.Loot => _lootFolder,
        ProfileKind.Meta => _metaFolder,
        ProfileKind.Settings => _charFolder ?? string.Empty,
        _ => BuffProfileStore.Folder,
    };

    /// <summary>A route from a file (not a meta's embedded nav, not /ra navheading's, not none).</summary>
    private static bool IsNavFile(string? path) => !string.IsNullOrEmpty(path) && path[0] != '<';

    /// <summary>The file the kind uses now; empty when there is none.</summary>
    internal string ProfilePath(ProfileKind k) => k switch
    {
        ProfileKind.Nav => IsNavFile(_settings.CurrentNavPath) ? _settings.CurrentNavPath : string.Empty,
        ProfileKind.Loot => _settings.CurrentLootPath ?? string.Empty,
        ProfileKind.Meta => _settings.CurrentMetaPath ?? string.Empty,
        ProfileKind.Settings => _settingsFilePath ?? string.Empty,
        _ => _settings.CurrentBuffProfilePath ?? string.Empty,
    };

    /// <summary>
    /// The row's header: the name in use and whether it has unsaved changes. Any thread (plain
    /// field reads, as ReadDashboardFiles). Settings profiles, navs with a file and metas with a file
    /// save every change on their own, so they are unsaved only when there is no file to save to
    /// (or the meta's last save failed). The Loot Editor's and the Spells window's edits are added by
    /// the plugin.
    /// </summary>
    internal ProfileFileInfo ProfileInfo(ProfileKind k)
    {
        string path = ProfilePath(k);
        bool hasFile = path.Length > 0;   // no disk look: the menu reads this every frame (a missing file is said by the button)
        string name = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : "None";
        bool unsaved = false;
        switch (k)
        {
            case ProfileKind.Settings:
                name = _settings.SelectedProfile ?? DefaultProfileName;
                break;
            case ProfileKind.Nav:
            {
                string nav = _settings.CurrentNavPath ?? string.Empty;
                int points = _settings.CurrentRoute?.Points.Count ?? 0;
                if (nav.StartsWith("<embedded:", StringComparison.Ordinal) && nav.EndsWith('>'))
                    name = nav[10..^1] + " (in the meta)";
                else if (nav.Length > 0 && nav[0] == '<')
                    name = nav.Trim('<', '>') + " route";
                else if (nav.Length == 0 && points > 0)
                    name = "Unsaved route";
                unsaved = nav.Length == 0 && points > 0;
                break;
            }
            case ProfileKind.Meta:
                if (path.Length == 0 && _settings.MetaRules.Count > 0) name = "Unsaved meta";
                unsaved = _settings.MetaUnsaved || (path.Length == 0 && _settings.MetaRules.Count > 0);
                break;
            case ProfileKind.Buffs:
                if (path.Length == 0) name = BuiltInBuffProfileLabel;
                break;
        }
        return new ProfileFileInfo(name, path, hasFile, unsaved, ProfileFolder(k));
    }

    // ── Save ─────────────────────────────────────────────────────────────

    /// <summary>The Save button and /ra &lt;kind&gt; save: writes the profile in use to its file now.</summary>
    internal ProfileResult SaveProfileNow(ProfileKind k)
    {
        switch (k)
        {
            case ProfileKind.Settings:
            {
                if (string.IsNullOrEmpty(_settingsFilePath)) return ProfileResult.Failed("Not logged in: no settings profile to save.");
                string name = _settings.SelectedProfile ?? DefaultProfileName;
                return SaveSettingsNow(out string reason, callerShows: true)
                    ? ProfileResult.Done($"Saved settings profile '{name}' ({Path.GetFileName(_settingsFilePath)}). It also saves every change on its own.")
                    : ProfileResult.Failed($"Couldn't save settings profile '{name}': {reason}");
            }
            case ProfileKind.Nav:
            {
                string msg = SaveRoute(null);
                return new ProfileResult(!RynthAiPlugin.ResultFailed(msg), msg);
            }
            case ProfileKind.Meta:
            {
                if (string.IsNullOrEmpty(_settings.CurrentMetaPath))
                    return ProfileResult.Failed("This meta has no file yet: type a name and use Save As (or /ra meta saveas <name>).");
                bool ok = _metaSaver.SaveCurrent("Save button");
                if (ok) { RefreshMetaFiles(); SaveSettings(); }   // a .met is saved as the .af beside it
                return ok ? ProfileResult.Done(_metaSaver.LastText + ".") : ProfileResult.Failed("Meta " + _metaSaver.LastText);
            }
            case ProfileKind.Loot:
                return ProfileResult.Failed("Loot profiles are saved in the Loot Editor (its Save button).");
            default:
                return ProfileResult.Failed("Buff profiles are saved in the Spells window (its Save button).");
        }
    }

    // ── Save As ──────────────────────────────────────────────────────────

    /// <summary>
    /// The Save As button and /ra &lt;kind&gt; saveas &lt;name&gt;: the profile in use saved under a
    /// new name, which is then the one in use (a loot profile: a copy of its file). Onto another
    /// file only with <paramref name="replace"/> (the button's confirm); that file's old copy is
    /// kept in Deleted first. Without it the result asks (<see cref="ProfileResult.NeedsReplace"/>).
    /// </summary>
    internal ProfileResult SaveProfileAs(ProfileKind k, string? raw, bool replace)
    {
        if (k == ProfileKind.Buffs) return ProfileResult.Failed("Buff profiles: change the name in the Spells window and Save to make a copy.");
        string? why = ProfileFileOps.CleanName(raw, ExtensionsOf(k), out string name);
        if (why != null) return ProfileResult.Failed($"Can't use '{name}' as a {KindWord(k)} name: {why}.");
        if (k == ProfileKind.Settings && IsReservedSettingsName(name))
            return ProfileResult.Failed($"Can't use '{name}' as a settings profile name: RynthAi keeps its own {name}.json there.");

        string current = ProfilePath(k), target, folder = ProfileFolder(k);
        switch (k)
        {
            case ProfileKind.Settings:
                if (string.IsNullOrEmpty(_charFolder)) return ProfileResult.Failed("Not logged in: settings profiles are per character.");
                target = GetProfileFilePath(name);
                break;
            case ProfileKind.Nav:
                target = Path.Combine(folder, name + ".nav");
                break;
            case ProfileKind.Meta:
                target = Path.Combine(folder, name + ".af");   // new metas are .af (a .met is never written)
                break;
            default:   // Loot: a copy of the file in use
                if (current.Length == 0 || !File.Exists(current))
                    return ProfileResult.Failed("No loot profile in use to copy. New makes a blank one in the Loot Editor.");
                string ext = Path.GetExtension(current);
                target = Path.Combine(folder, name + (ext.Length > 0 ? ext : ".utl"));
                if (ProfileFileOps.SamePath(target, current))
                    return ProfileResult.Failed($"{Path.GetFileName(target)} is the loot profile in use already; pick another name.");
                break;
        }

        if (ProfileFileOps.WouldReplace(target, current))
        {
            if (!replace)
                return new ProfileResult(false,
                    $"[RynthAi] {Path.GetFileName(target)} already exists. Replace it? (its old copy is kept in {ProfileFileOps.DeletedFolderName})",
                    NeedsReplace: true);
            if (!ProfileFileOps.CopyToDeleted(target, RynthClock.Now, out _, out string keepError))
                return ProfileResult.Failed($"Not saved: couldn't keep a copy of {Path.GetFileName(target)} first ({keepError}).");
        }

        switch (k)
        {
            case ProfileKind.Settings:
            {
                string r = SaveAsProfile(name);
                return r.StartsWith("Saved profile", StringComparison.Ordinal)
                    ? ProfileResult.Done($"Saved these settings as profile '{name}'; it is the profile in use now.")
                    : ProfileResult.Failed(r);
            }
            case ProfileKind.Nav:
            {
                string msg = SaveRoute(name);
                return new ProfileResult(!RynthAiPlugin.ResultFailed(msg), msg);
            }
            case ProfileKind.Meta:
                return SaveMetaAs(target)
                    ? ProfileResult.Done($"Saved the meta as {Path.GetFileName(target)}; every change now saves to it.")
                    : ProfileResult.Failed("Meta " + _metaSaver.LastText);
            default:
            {
                string from = current;
                if (!DataWriteGuard.TrySave("the loot profile", target, () => File.Copy(from, target, overwrite: true), out string copyWhy, callerShows: true))
                    return ProfileResult.Failed($"Couldn't copy {Path.GetFileName(from)} to {Path.GetFileName(target)}: {copyWhy}");
                _settings.CurrentLootPath = target;
                RefreshLootFiles();
                SaveSettings();
                return ProfileResult.Done($"Copied {Path.GetFileName(from)} to {Path.GetFileName(target)}; RynthAi loots with {Path.GetFileName(target)} now.");
            }
        }
    }

    /// <summary>The meta's Save As (the Meta panel's save_file and the drawer's button): written to <paramref name="path"/>, which is then its file.</summary>
    private bool SaveMetaAs(string path)
    {
        if (!_metaSaver.SaveTo(path, "Save As", callerShows: true)) return false;
        _settings.CurrentMetaPath = path;
        RefreshMetaFiles();   // the picker lists (and selects) the new file
        SaveSettings();
        return true;
    }

    // ── New ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The New button and /ra nav|meta new &lt;name&gt;: a blank nav (Circular, no points) or meta (one
    /// state "Default", no rules), loaded. Never over an existing one (that is picked in the list).
    /// Loot profiles: <see cref="NewLootProfilePath"/>, opened in the Loot Editor by the plugin.
    /// </summary>
    internal ProfileResult NewProfile(ProfileKind k, string? raw)
    {
        switch (k)
        {
            case ProfileKind.Nav:
            {
                string? why = ProfileFileOps.CleanName(raw, NavExtensions, out string name);
                if (why != null) return ProfileResult.Failed($"Can't use '{name}' as a nav name: {why}.");
                if (File.Exists(Path.Combine(_navFolder, name + ".nav")))
                    return ProfileResult.Failed($"Nav '{name}' already exists. Pick it in the NAV list to load it, or use another name.");
                string msg = LoadNavByName(name);
                return new ProfileResult(msg.Contains("Created new nav", StringComparison.Ordinal), msg);
            }
            case ProfileKind.Meta:
            {
                string msg = LoadMetaByName(raw ?? string.Empty, createOnly: true);
                return new ProfileResult(msg.Contains("Created new meta", StringComparison.Ordinal), msg);
            }
            case ProfileKind.Settings:
                return ProfileResult.Failed("Save As makes a new settings profile (a copy of these settings).");
            case ProfileKind.Buffs:
                return ProfileResult.Failed("New buff profiles are made in the Spells window (its New button).");
            default:
                return ProfileResult.Failed("New loot profiles are made in the Loot Editor.");
        }
    }

    /// <summary>
    /// LootProfiles\&lt;name&gt;.utl for a new loot profile: null when it can be made (nothing is
    /// written: the Loot Editor's Save creates it), else why not.
    /// </summary>
    internal string? NewLootProfilePath(string? raw, out string path)
    {
        path = string.Empty;
        string? why = ProfileFileOps.CleanName(raw, LootExtensions, out string name);
        if (why != null) return $"Can't use '{name}' as a loot profile name: {why}.";
        string utl = Path.Combine(_lootFolder, name + ".utl");
        if (File.Exists(utl) || File.Exists(Path.Combine(_lootFolder, name + ".json")))
            return $"Loot profile '{name}' already exists. Pick it in the LOOT list, or use another name.";
        path = utl;
        return null;
    }

    // ── Rename ───────────────────────────────────────────────────────────

    /// <summary>The Rename button and /ra &lt;kind&gt; rename &lt;name&gt;: the file in use gets the new name and stays in use.</summary>
    internal ProfileResult RenameProfile(ProfileKind k, string? raw)
    {
        if (k == ProfileKind.Buffs) return ProfileResult.Failed("Buff profiles are renamed in the Spells window.");
        string current = ProfilePath(k);
        if (current.Length == 0 || !File.Exists(current))
            return ProfileResult.Failed($"No {KindWord(k)} file in use to rename.");
        if (k == ProfileKind.Settings && string.Equals(_settings.SelectedProfile, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            return ProfileResult.Failed("The Default settings profile keeps its name; Save As makes a copy under another name.");

        string? why = ProfileFileOps.CleanName(raw, ExtensionsOf(k), out string name);
        if (why != null) return ProfileResult.Failed($"Can't use '{name}' as a {KindWord(k)} name: {why}.");
        if (k == ProfileKind.Settings && (IsReservedSettingsName(name) || name.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase)))
            return ProfileResult.Failed($"Can't rename a settings profile to '{name}': that name is taken by RynthAi.");

        string target = Path.Combine(Path.GetDirectoryName(current) ?? ProfileFolder(k), name + Path.GetExtension(current));
        string oldName = Path.GetFileName(current);
        if (!ProfileFileOps.Rename(current, target, out string error))
            return ProfileResult.Failed($"Couldn't rename {oldName} to {Path.GetFileName(target)}: {error}.");

        switch (k)
        {
            case ProfileKind.Settings:
                _settings.SelectedProfile = name;
                _settingsFilePath = target;
                WriteActiveProfile(name);
                RefreshProfilesList();
                break;
            case ProfileKind.Nav:
                _settings.CurrentNavPath = target;
                RefreshNavFiles();
                SaveSettings();
                break;
            case ProfileKind.Meta:
                _settings.CurrentMetaPath = target;
                RefreshMetaFiles();
                SaveSettings();
                break;
            default:
                _settings.CurrentLootPath = target;
                RefreshLootFiles();
                SaveSettings();
                break;
        }
        string note = k is ProfileKind.Nav or ProfileKind.Loot or ProfileKind.Meta
            ? " Metas that load it by its old name need the new one." : string.Empty;
        return ProfileResult.Done($"Renamed {oldName} to {Path.GetFileName(target)}; it is still the {KindWord(k)} in use.{note}");
    }

    // ── Move to Deleted ──────────────────────────────────────────────────

    /// <summary>
    /// The Delete button and /ra &lt;kind&gt; delete: the file in use is moved to the Deleted folder
    /// beside it (never deleted) and is no longer used: the nav, meta and loot profile become None,
    /// the settings switch to Default first.
    /// </summary>
    internal ProfileResult DeleteProfile(ProfileKind k)
    {
        if (k == ProfileKind.Buffs) return ProfileResult.Failed("Buff profiles are removed in the Spells window (its Delete button).");
        string current = ProfilePath(k);
        if (current.Length == 0 || !File.Exists(current))
            return ProfileResult.Failed($"No {KindWord(k)} file in use to move to {ProfileFileOps.DeletedFolderName}.");
        string file = Path.GetFileName(current);
        string folderName = Path.GetFileName(Path.GetDirectoryName(current) ?? string.Empty);

        string after;
        if (k == ProfileKind.Settings)
        {
            if (string.Equals(_settings.SelectedProfile, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                return ProfileResult.Failed("The Default settings profile can't be moved to Deleted; it is the one every character falls back to.");
            SwitchProfile(DefaultProfileName);   // saves this profile's last changes first
            after = "the settings are the Default profile now";
        }
        else after = $"no {KindWord(k)} is in use now";

        if (!ProfileFileOps.MoveToDeleted(current, RynthClock.Now, out string moved, out string error))
            return ProfileResult.Failed($"Couldn't move {file} to {ProfileFileOps.DeletedFolderName}: {error}."
                + (k == ProfileKind.Settings ? " The settings are the Default profile now." : string.Empty));

        switch (k)
        {
            case ProfileKind.Settings:
                RefreshProfilesList();
                break;
            case ProfileKind.Nav:
                RefreshNavFiles();
                _selectedNavIdx = 0;   // "None": the route is cleared, as picking None does
                LoadSelectedNav();
                break;
            case ProfileKind.Meta:
                _metaUi.LoadMacroFile(string.Empty);   // as picking None
                RefreshMetaFiles();
                SaveSettings();
                break;
            default:
                _settings.CurrentLootPath = string.Empty;
                RefreshLootFiles();
                _settings.LootProfileIdx = 0;
                SaveSettings();
                break;
        }
        return ProfileResult.Done($"Moved {file} to {folderName}\\{ProfileFileOps.DeletedFolderName}\\{Path.GetFileName(moved)}; {after}. Move it back out of {ProfileFileOps.DeletedFolderName} to restore it.");
    }

    // ── Open folder ──────────────────────────────────────────────────────

    /// <summary>The Open folder button and /ra &lt;kind&gt; folder: Explorer on the kind's folder, the file in use selected.</summary>
    internal ProfileResult OpenProfileFolder(ProfileKind k)
    {
        string folder = ProfileFolder(k);
        if (string.IsNullOrEmpty(folder)) return ProfileResult.Failed("Not logged in: settings profiles are per character.");
        string current = ProfilePath(k);
        try
        {
            Directory.CreateDirectory(folder);
            string args = current.Length > 0 && File.Exists(current) ? $"/select,\"{current}\"" : $"\"{folder}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            return ProfileResult.Done($"Opened the {KindWord(k)} folder: {folder}");
        }
        catch (Exception ex)
        {
            return ProfileResult.Failed($"Couldn't open {folder}: {ex.Message}");
        }
    }
}
