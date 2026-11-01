using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// Every write of the loaded meta's file (2026-11-01). Both editors save through here: the engine's
/// Meta panel (rule commands, TryAutoSaveMetaCmd; Save As, save_file) and RynthAi's own Macro Rules
/// window (TryAutoSaveMeta, SaveMacroFile).
///
/// "Still having trouble editing metas and the changes not saving": none of the meta saves logged
/// anything, a save that didn't happen (no file, a rule index the meta no longer has, a command
/// that threw) returned without a word, and the engine panel showed "Saved" two and a half seconds
/// after every change whatever happened. Now each attempt has one result:
///  - saved: one log line (path, rule and state count) and "Saved name (N rules)";
///  - failed or refused: one log line and a status that says why (the file couldn't be written,
///    the meta has no file, the rule isn't there any more...).
/// The engine panel reads the last result from the meta snapshot ("saveResult", by sequence number),
/// so its indicator says what really happened; the Macro Rules window shows it in its status line.
/// A failed write also marks the profile's copy of the rules as the newer one
/// (<see cref="LegacyUiSettings.MetaUnsaved"/>; see LegacyDashboardRenderer.SyncMetaWithFile).
/// </summary>
internal sealed class MetaSaver
{
    private readonly LegacyUiSettings _settings;
    private readonly Action<string>? _log;

    public MetaSaver(LegacyUiSettings settings, Action<string>? log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>Counts results (0 = nothing tried yet this session).</summary>
    public long Seq { get; private set; }
    public bool LastOk { get; private set; } = true;
    /// <summary>What the last attempt did, in a few words ("Saved farm.af (12 rules)", "NOT saved: ...").</summary>
    public string LastText { get; private set; } = string.Empty;
    /// <summary>The file the last attempt wrote or meant to write (empty: none).</summary>
    public string LastPath { get; private set; } = string.Empty;

    /// <summary>
    /// Writes the loaded meta (<see cref="LegacyUiSettings.CurrentMetaPath"/>) after an edit. A .met is
    /// saved as the .af beside it (the .met is never written) and the meta switches to that .af.
    /// <paramref name="change"/> names the edit for the log ("update_rule 4").
    /// </summary>
    public bool SaveCurrent(string change)
    {
        string path = _settings.CurrentMetaPath ?? string.Empty;
        if (path.Length == 0)
            return Refuse("Not saved: this meta has no file yet. Save As names one; after that every change saves on its own.", change);
        string note = string.Empty;
        if (Path.GetExtension(path).Equals(".met", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.ChangeExtension(path, ".af");
            _settings.CurrentMetaPath = path;
            note = $" (converted from the .met, which is left untouched)";
        }
        return SaveTo(path, change, note);
    }

    /// <summary>Writes the rules to <paramref name="path"/>. <paramref name="note"/> is added to the status.
    /// <paramref name="callerShows"/>: the caller puts a failure in chat itself (DataWriteGuard queues nothing).</summary>
    public bool SaveTo(string path, string change, string note = "", bool callerShows = false)
    {
        int rules = 0, states = 0;
        bool ok;
        string why;
        lock (_settings.MetaRulesLock)
        {
            rules = _settings.MetaRules.Count;
            states = _settings.MetaRules.Select(r => r.State ?? "Default").Distinct(StringComparer.OrdinalIgnoreCase).Count();
            ok = DataWriteGuard.TrySave("the meta", path,
                () => AfFileWriter.Save(path, _settings.MetaRules, _settings.EmbeddedNavs), out why, callerShows);
        }
        string name = Path.GetFileName(path);
        if (ok)
        {
            _settings.MetaUnsaved = false;
            Record(true, $"Saved {name} ({rules} rule{(rules == 1 ? "" : "s")}){note}", path);
            Log($"[RynthAi] Meta saved: {path} ({rules} rules, {states} states; {change}){note}");
            return true;
        }
        _settings.MetaUnsaved = true;
        Record(false, $"NOT saved to {name}: {why}", path);
        Log($"[RynthAi] Meta NOT saved: {path} ({change}): {why}");
        return false;
    }

    /// <summary>An edit that wasn't applied or written, and why. Logged and shown like a failed save.</summary>
    public bool Refuse(string text, string change)
    {
        Record(false, text, _settings.CurrentMetaPath ?? string.Empty);
        Log($"[RynthAi] Meta {change}: {text}");
        return false;
    }

    /// <summary>A command that needed no write (a move at the end of its state) or a load: a result,
    /// so the engine panel isn't left waiting for one. Not logged.</summary>
    public void Note(string text, string path) => Record(true, text, path);

    private void Record(bool ok, string text, string path)
    {
        Seq++;
        LastOk = ok;
        LastText = text;
        LastPath = path;
    }

    private void Log(string line)
    {
        try { _log?.Invoke(line); } catch { }
    }
}
