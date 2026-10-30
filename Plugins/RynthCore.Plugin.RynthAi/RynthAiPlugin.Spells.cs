// RynthAiPlugin.Spells.cs - Spells window (/ra spells) and buff profile selection
// (/ra buffprofile, Loaded Files → Buffs). The window draws on the render thread; this file
// feeds it pump-thread data and applies the profile it picks on the pump thread.
using System;
using System.IO;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>How often the open Spells window gets a fresh copy of the known-spell set.</summary>
    private const long SpellsKnownPushMs = 2000;

    private LegacySpellsUi? _spellsUi;
    private long _spellsKnownPushedAt;

    // Profile path picked in the Spells window ("" = built-in list), applied on the next pump tick.
    private string? _pendingBuffProfilePath;

    /// <summary>Spells window, created once settings exist.</summary>
    private void EnsureSpellsUi()
    {
        if (_dashboard == null || _spellsUi != null) return;
        _spellsUi = new LegacySpellsUi(_dashboard.Settings);
        _spellsUi.SetHooks(
            path => Interlocked.Exchange(ref _pendingBuffProfilePath, path ?? string.Empty),
            () => _dashboard?.RefreshBuffProfileFiles());
    }

    /// <summary>Pump thread: applies the window's profile choice and feeds the open window.</summary>
    private void TickSpellsUi()
    {
        string? pending = Interlocked.Exchange(ref _pendingBuffProfilePath, null);
        if (pending != null) _dashboard?.SetActiveBuffProfile(pending);

        var ui = _spellsUi;
        if (ui == null || !ui.Visible) return;

        if (ui.TakeBuiltInRequest())
        {
            if (_buffManager != null) ui.SetBuiltInList(_buffManager.BuildBuiltInBuffList());
            else ChatLine("[RynthAi] Buffing is not ready yet (log in first).");
        }

        long now = Environment.TickCount64;
        if (_spellManager != null && now - _spellsKnownPushedAt >= SpellsKnownPushMs)
        {
            _spellsKnownPushedAt = now;
            ui.SetKnownSpells(_spellManager.CopyKnownSpellIds());
        }
    }

    /// <summary>/ra spells [show|hide|toggle] (also the dashboard's "spells" remote command).</summary>
    private void HandleSpellsCommand(string mode)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return; }
        EnsureSpellsUi();

        settings.ShowSpellsWindow = mode.ToLowerInvariant() switch
        {
            "show" or "open" or "on" => true,
            "hide" or "close" or "off" => false,
            _ => !settings.ShowSpellsWindow,
        };
        ReplyOk($"[RynthAi] Spells window {(settings.ShowSpellsWindow ? "opened" : "closed")}.");
    }

    /// <summary>
    /// /ra buffprofile [list] | none | &lt;name&gt;: shows or picks the buff profile buffing uses
    /// ("none" or "builtin" = the built-in list).
    /// </summary>
    private void HandleBuffProfileCommand(string[] parts)
    {
        var dash = _dashboard;
        if (dash == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return; }

        string arg = parts.Length >= 3 ? string.Join(" ", parts, 2, parts.Length - 2).Trim() : string.Empty;
        if (arg.Length == 0 || arg.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            string current = dash.Settings.CurrentBuffProfilePath;
            ChatLine("[RynthAi] Buff profile: " + (string.IsNullOrEmpty(current)
                ? LegacyDashboardRenderer.BuiltInBuffProfileLabel
                : Path.GetFileNameWithoutExtension(current)));
            var files = BuffProfileStore.ListFileNames();
            ChatLine(files.Count == 0
                ? "[RynthAi] No saved buff profiles. Build one with /ra spells."
                : "[RynthAi] Saved: " + string.Join(", ", files.ConvertAll(Path.GetFileNameWithoutExtension)));
            return;
        }

        if (arg.Equals("none", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("builtin", StringComparison.OrdinalIgnoreCase)
            || arg.Equals(LegacyDashboardRenderer.BuiltInBuffProfileLabel, StringComparison.OrdinalIgnoreCase))
        {
            dash.SetActiveBuffProfile(string.Empty);
            Reply.Answered();
            return; // BuffManager posts the "built-in list" chat line
        }

        foreach (string file in BuffProfileStore.ListFileNames())
        {
            if (!Path.GetFileNameWithoutExtension(file).Equals(arg, StringComparison.OrdinalIgnoreCase)) continue;
            dash.SetActiveBuffProfile(Path.Combine(BuffProfileStore.Folder, file));
            Reply.Answered();
            return; // BuffManager posts the "profile 'X'" chat line once it loads
        }
        ReplyFail($"[RynthAi] No buff profile named '{arg}'. /ra buffprofile list shows them.");
    }
}
