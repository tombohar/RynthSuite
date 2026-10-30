using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The Refresh button every page of the Oracle window starts with, and the status line beside it.
/// What "refresh" means depends on where a page's data comes from:
///   - data the server owns (the quest flags)
///     is asked for again with the same command the page always used, and the line waits for the
///     reply;
///   - data read from the game client (level, buffs, titles, fellowship) is re-read at once instead
///     of at the next timer;
///   - files (the About page) are loaded again.
/// A page that needs both does both. Nothing here blocks: a request is one chat command, and the
/// same request is refused for <see cref="CooldownMs"/> after it went out (the quest read has its
/// own 5 s gap), so the button can't spam the server.
/// </summary>
internal sealed partial class OracleView
{
    private const long CooldownMs = 3000;
    private const long ReplyWaitMs = 15_000;

    /// <summary>What a Refresh did, and (when it asked the server) how to tell the reply has landed.</summary>
    private sealed record RefreshResult(string What, Func<bool>? Done = null);

    private string _refreshScope = "", _refreshWhat = "";
    private Func<bool>? _refreshDone;
    private bool _refreshDoneSeen;
    private long _refreshAtMs;
    private DateTime _refreshAtUtc, _refreshDoneUtc;
    private readonly Dictionary<string, long> _refreshSent = new();

    /// <summary>The page a status line belongs to (each tab is one page).</summary>
    private static string RefreshScope(string tab) => tab;

    private void DrawRefreshRow(UiWindow w, TabDef def)
    {
        string scope = RefreshScope(def.Name);
        w.Button("Refresh", "refresh." + scope, () => DoRefresh(def, scope), small: true);
        w.Tooltip(def.Help());
        if (_refreshScope != scope || _refreshWhat.Length == 0) return;

        w.SameLine();
        if (_refreshDone != null && !_refreshDoneSeen && _refreshDone())
        {
            _refreshDoneSeen = true;
            _refreshDoneUtc = _c.UtcNow;
        }
        if (_refreshDone != null && !_refreshDoneSeen)
        {
            if (_c.NowMs - _refreshAtMs > ReplyWaitMs) w.TextColored(UiColors.Red, "No reply yet. Press Refresh to ask again.");
            else w.TextColored(UiColors.Yellow, _refreshWhat + " Waiting for the reply...");
        }
        else if (_refreshDone != null)
            w.TextDisabled($"Updated {_refreshDoneUtc.ToLocalTime():HH:mm:ss}. {_refreshWhat}");
        else
            w.TextDisabled($"Refreshed {_refreshAtUtc.ToLocalTime():HH:mm:ss}. {_refreshWhat}");
    }

    private void DoRefresh(TabDef def, string scope)
    {
        RefreshResult r;
        try { r = def.Refresh(); }
        catch (Exception ex) { r = new RefreshResult("Couldn't refresh: " + ex.Message); }
        _refreshScope = scope;
        _refreshWhat = r.What;
        _refreshDone = r.Done;
        _refreshDoneSeen = false;
        _refreshAtMs = _c.NowMs;
        _refreshAtUtc = _c.UtcNow;
    }

    // ── What each page's Refresh does ───────────────────────────────────────

    /// <summary>The same request twice within <see cref="CooldownMs"/> is refused.</summary>
    private bool CoolingDown(string key)
    {
        if (_refreshSent.TryGetValue(key, out long t) && _c.NowMs - t < CooldownMs) return true;
        _refreshSent[key] = _c.NowMs;
        return false;
    }

    private const string TooSoon = "That was just asked for; wait a few seconds before asking again.";

    /// <summary>/myquests (refused for 5 s after the last one); the line waits for the new flags.</summary>
    private RefreshResult RefreshQuests(string before = "")
    {
        QuestFlagStore f = _c.Flags;
        int rev = f.Revision;
        if (_c.RequestQuests()) return new RefreshResult(before + "Asked the server for your quest flags (/myquests).", () => f.Revision != rev);
        if (f.Loading) return new RefreshResult(before + "Your quest flags are already being read.", () => f.Revision != rev);
        int wait = f.ThrottleSecondsLeft(_c.NowMs);
        if (wait > 0) return new RefreshResult(before + $"The server answers one /myquests a minute; try again in {wait}s (the flags shown are kept).");
        return new RefreshResult(before + "Your quest flags were asked for a moment ago; wait a few seconds before asking again.");
    }

    private RefreshResult RefreshCharacter()
    {
        _c.RereadCharacter();
        return new RefreshResult("Re-read your level, experience, buffs and skills from the game.");
    }

    /// <summary>Pages built from the character's properties and the quest flags: both are read again.</summary>
    private RefreshResult RefreshCharacterAndQuests()
    {
        _c.RereadCharacter();
        return RefreshQuests("Re-read your character from the game. ");
    }

    private RefreshResult RefreshTitles()
    {
        _c.RereadTitles();
        return new RefreshResult(_c.TitlesKnown
            ? $"Re-read your titles from the game: {_c.EarnedTitles.Count} held."
            : "The game hasn't sent this character's titles yet (they arrive at login).");
    }

    private RefreshResult RefreshVoid()
    {
        _c.RereadCharacter();
        return new RefreshResult("Re-read your buffs and dropped the spell timers that ran out. Timers on targets are learned from your casts.");
    }

    private RefreshResult RefreshFellowship()
    {
        _c.Fellowship.Invalidate();
        return new RefreshResult("Re-read the fellowship from the game.");
    }

    private RefreshResult RefreshAbout()
    {
        if (CoolingDown("about")) return new RefreshResult(TooSoon);
        _c.ReloadQuestList();
        QuestCatalog cat = _c.Catalog;
        string pack = cat.PackServer.Length > 0 ? $" + {cat.ServerCount} {cat.PackServer}" : "";
        return new RefreshResult($"Reloaded the quest list ({cat.RetailCount} retail{pack}) and asked RynthCore which server this is.");
    }
}
