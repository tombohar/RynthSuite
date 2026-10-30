using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The Meta Manager's glue (Docs\META_MANAGER.md): feeds MetaScheduler the bot's state and
/// chat on the pump, sends its /myquests polls, loads the metas it picks, saves its rules
/// per character, and serves /ra metamgr and the engine panel's mm_* ops.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private MetaScheduler _metaScheduler = new();
    private string _metaScheduleFolder = string.Empty;   // character folder the rules were loaded from
    private volatile string _metaScheduleJson = string.Empty;
    private long _metaScheduleSnapAt;
    // Chat-bar events and /ra metamgr may arrive off the pump: flags and a queue, drained on it.
    private int _mmTypedQuery;      // a "/myquests" passed through the chat bar (maybe our own echo)
    private int _mmShownQuery;      // RynthAi's quest refresh sent /myquests: show that reply
    // FromScript: a meta (or script) sent the line, so its answer is quiet when it works (CommandReply.cs).
    private readonly ConcurrentQueue<(string[] Parts, bool FromScript)> _metaMgrCommands = new();

    /// <summary>OnLoginComplete: hook the dashboard (panel snapshot and ops) and the quest refresh.</summary>
    private void WireMetaSchedule()
    {
        if (_dashboard != null)
        {
            _dashboard.ScheduleJsonProvider = () => _metaScheduleJson;
            _dashboard.ScheduleCommandHandler = ApplyScheduleCommand;
        }
        if (_questTracker != null)
            _questTracker.RefreshSent = () => Interlocked.Exchange(ref _mmShownQuery, 1);
    }

    /// <summary>TeardownSession: save, then forget this character's rules and quest timers.</summary>
    private void TeardownMetaSchedule()
    {
        SaveMetaScheduleIfDirty();
        _metaScheduler = new MetaScheduler();
        _metaScheduleFolder = string.Empty;
        _metaScheduleJson = string.Empty;
    }

    private void DrainMetaScheduleQueryFlags(DateTime now)
    {
        if (Interlocked.Exchange(ref _mmShownQuery, 0) != 0) _metaScheduler.NoteShownQuery(now);
        if (Interlocked.Exchange(ref _mmTypedQuery, 0) != 0) _metaScheduler.NoteTypedQuery(now);
    }

    /// <summary>OnChatBarEnter (any thread): note a /myquests going out; its reply is not ours to eat.</summary>
    private void NoteChatBarLineForMetaSchedule(string trimmed)
    {
        if (trimmed.Equals("/myquests", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/myquests ", StringComparison.OrdinalIgnoreCase))
            Interlocked.Exchange(ref _mmTypedQuery, 1);
    }

    /// <summary>OnChatWindowText (pump): true to eat the line (it answers our own /myquests poll).</summary>
    private bool MetaScheduleChat(string text)
    {
        var now = DateTime.UtcNow;
        DrainMetaScheduleQueryFlags(now);
        return _metaScheduler.OnChatLine(text, now);
    }

    /// <summary>Why a meta switch must wait now, or null when it is safe.</summary>
    private string? MetaSwitchBlocker(LegacyUiSettings s)
    {
        if (_activity == BotActivity.Combat || _combatManager?.IsUnderCloseAttack == true
            || (s.EnableCombat && _combatManager?.HasEngageableTarget == true))
            return "combat";
        if (_activity == BotActivity.Looting || _targetCorpseId != 0 || _openedContainerId != 0)
            return "looting";
        if (_activity == BotActivity.Buffing || (_buffManager?.PendingSpellId ?? 0) != 0)
            return "buffing";
        if (_navigationEngine?.IsInPortalAction == true)
            return "a portal";
        if (_autoVendor?.HoldsBot == true || _autoTrade?.HoldsBot == true)
            return "a vendor or trade";
        if (_missileCraftingManager?.IsCrafting == true)
            return "crafting";
        return null;
    }

    /// <summary>Every in-world tick, after the arbiter's decision (so it runs even while buffing holds the tick).</summary>
    private void TickMetaSchedule(LegacyUiSettings s)
    {
        var dash = _dashboard;
        if (dash == null) return;
        string folder = dash.CharFolder ?? string.Empty;
        if (!string.Equals(folder, _metaScheduleFolder, StringComparison.OrdinalIgnoreCase))
        {
            SaveMetaScheduleIfDirty();
            // Another character: start clean. From "no folder yet" (login) keep what the login
            // /myquests already taught it, so the first own poll isn't a second one at once.
            if (_metaScheduleFolder.Length > 0) _metaScheduler = new MetaScheduler();
            _metaScheduleFolder = folder;
            if (folder.Length > 0) LoadMetaSchedule();
        }
        if (folder.Length == 0) return;   // no character settings yet: nothing to save to

        while (_metaMgrCommands.TryDequeue(out var queued))
        {
            try { Reply.RunQueued(queued.FromScript, () => HandleMetaMgrCommand(queued.Parts)); }
            catch (Exception ex) { Host.Log($"[RynthAi] /ra metamgr failed: {ex.Message}"); }
        }

        var now = DateTime.UtcNow;
        DrainMetaScheduleQueryFlags(now);
        var inputs = new ScheduleInputs(
            InWorld: _loginComplete && _playerId != 0,
            MacroRunning: s.IsMacroRunning,
            CurrentMetaPath: s.CurrentMetaPath ?? string.Empty,
            Blocker: MetaSwitchBlocker(s),
            CanPoll: Host.HasInvokeChatParser);
        ScheduleAction act = _metaScheduler.Tick(now, inputs);
        switch (act.Kind)
        {
            case ScheduleActionKind.SendPoll:
                _questTracker?.ExpectReply();   // quest expressions read the same reply
                Host.InvokeChatParser("/myquests");
                break;
            case ScheduleActionKind.LoadMeta:
                string? error = dash.LoadMetaForSchedule(act.Meta);
                if (error == null)
                    ChatLine($"[RynthAi] Meta Manager: loading {act.Meta} ({act.Reason})");
                else
                {
                    _metaScheduler.ReportLoadFailed(act.RuleIndex, error);
                    ChatLine($"[RynthAi] Meta Manager: can't load {act.Meta} for rule #{act.RuleIndex + 1}: {error}. The rule is paused until you edit or reset it.");
                }
                break;
        }

        SaveMetaScheduleIfDirty();
        long t = Environment.TickCount64;
        if (t >= _metaScheduleSnapAt || act.Kind != ScheduleActionKind.None)
        {
            _metaScheduleSnapAt = t + 500;
            _metaScheduleJson = _metaScheduler.BuildSnapshotJson(now, s.CurrentMetaPath ?? string.Empty);
        }
    }

    private string MetaScheduleFile => Path.Combine(_metaScheduleFolder, MetaScheduler.ConfigFileName);

    private void LoadMetaSchedule()
    {
        MetaScheduleConfig? cfg = null;
        try
        {
            if (File.Exists(MetaScheduleFile))
                cfg = JsonSerializer.Deserialize(File.ReadAllText(MetaScheduleFile), RynthAiJsonContext.Default.MetaScheduleConfig);
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] Meta Manager: {MetaScheduleFile} unreadable ({ex.Message}); starting empty.");
        }
        _metaScheduler.Load(cfg);
    }

    private void SaveMetaScheduleIfDirty()
    {
        if (!_metaScheduler.ConsumeDirty() || _metaScheduleFolder.Length == 0) return;
        try
        {
            Directory.CreateDirectory(_metaScheduleFolder);
            string json = JsonSerializer.Serialize(_metaScheduler.Config, RynthAiJsonContext.Default.MetaScheduleConfig);
            string tmp = MetaScheduleFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, MetaScheduleFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] Meta Manager: save failed: {ex.Message}");
        }
    }

    // =====================================================================
    //  /ra metamgr (queued to the pump by DispatchRaCommand)
    // =====================================================================

    private void QueueMetaMgrCommand(string[] parts) => _metaMgrCommands.Enqueue((parts, Reply.FromScript));

    private void HandleMetaMgrCommand(string[] parts)
    {
        var now = DateTime.UtcNow;
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "status";
        string meta = _dashboard?.Settings.CurrentMetaPath ?? string.Empty;
        switch (sub)
        {
            case "on":
            case "off":
                _metaScheduler.SetEnabled(sub == "on");
                ReplyOk(sub == "on"
                    ? $"[RynthAi] Meta Manager ON ({_metaScheduler.Config.Rules.Count} rule(s))."
                    : "[RynthAi] Meta Manager OFF.");
                break;
            case "poll":
                if (parts.Length >= 4)
                {
                    if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
                    {
                        _metaScheduler.SetPollMinutes(m);
                        ReplyOk($"[RynthAi] Meta Manager: /myquests every {_metaScheduler.Config.PollMinutes} min.");
                    }
                    else ReplyFail("[RynthAi] Usage: /ra metamgr poll [minutes]");
                    break;
                }
                _metaScheduler.RequestPoll();
                ChatLine(!_metaScheduler.Config.Enabled ? "[RynthAi] Meta Manager is off; it polls only while on."
                    : !_metaScheduler.HasQuestRules ? "[RynthAi] Meta Manager has no quest rules; nothing to poll for."
                    : "[RynthAi] Meta Manager: checking /myquests now.");
                break;
            case "start":
            case "stop":
            case "reset":
            {
                if (parts.Length < 4) { ReplyFail($"[RynthAi] Usage: /ra metamgr {sub} <rule #>{(sub == "start" ? " [minutes]" : sub == "reset" ? "|all" : "")}"); break; }
                if (sub == "reset" && parts[3].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < _metaScheduler.Config.Rules.Count; i++) _metaScheduler.ResetRule(i);
                    ReplyOk("[RynthAi] Meta Manager: every rule re-armed.");
                    break;
                }
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    || n < 1 || n > _metaScheduler.Config.Rules.Count)
                {
                    ReplyFail($"[RynthAi] Meta Manager: no rule #{parts[3]} (there are {_metaScheduler.Config.Rules.Count}).");
                    break;
                }
                int idx = n - 1;
                if (sub == "start")
                {
                    double? mins = null;
                    if (parts.Length >= 5 && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) && mm > 0) mins = mm;
                    ReplyOk(_metaScheduler.StartCountdown(idx, mins, now)
                        ? $"[RynthAi] Meta Manager: rule #{n} countdown started ({(mins ?? _metaScheduler.Config.Rules[idx].Minutes).ToString("0.##", CultureInfo.InvariantCulture)} min)."
                        : $"[RynthAi] Meta Manager: rule #{n} is not a countdown.");
                }
                else if (sub == "stop")
                    ReplyOk(_metaScheduler.StopCountdown(idx)
                        ? $"[RynthAi] Meta Manager: rule #{n} countdown stopped."
                        : $"[RynthAi] Meta Manager: rule #{n} has no countdown running.");
                else
                {
                    _metaScheduler.ResetRule(idx);
                    ReplyOk($"[RynthAi] Meta Manager: rule #{n} re-armed.");
                }
                break;
            }
            case "status":
                foreach (string line in _metaScheduler.StatusLines(now, meta)) ChatLine("[RynthAi] " + line);
                break;
            default:
                ReplyFail("[RynthAi] Usage: /ra metamgr on|off|status|poll [minutes]|start <#> [min]|stop <#>|reset <#|all>");
                break;
        }
        SaveMetaScheduleIfDirty();
    }

    // =====================================================================
    //  Engine panel ops (RynthPluginSendMetaCommand "mm_*", drained on the pump)
    // =====================================================================

    private void ApplyScheduleCommand(MetaCommand cmd)
    {
        // Before this character's rules are loaded an edit would land in a throwaway scheduler.
        if (_metaScheduleFolder.Length == 0) return;
        var now = DateTime.UtcNow;
        bool on = string.Equals(cmd.Value, "true", StringComparison.OrdinalIgnoreCase);
        int.TryParse(cmd.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
        switch (cmd.Op)
        {
            case "mm_enabled":         _metaScheduler.SetEnabled(on); break;
            case "mm_poll_minutes":    if (n > 0) _metaScheduler.SetPollMinutes(n); break;
            case "mm_max_wait":        _metaScheduler.SetMaxWaitSeconds(n); break;
            case "mm_min_gap":         _metaScheduler.SetMinGapSeconds(n); break;
            case "mm_allow_stopped":   _metaScheduler.SetAllowWhileStopped(on); break;
            case "mm_add":             if (cmd.ScheduleRule != null) _metaScheduler.AddRule(cmd.ScheduleRule); break;
            case "mm_update":          if (cmd.ScheduleRule != null) _metaScheduler.UpdateRule(cmd.Index, cmd.ScheduleRule); break;
            case "mm_delete":          _metaScheduler.DeleteRule(cmd.Index); break;
            case "mm_move":            _metaScheduler.MoveRule(cmd.Index, n); break;
            case "mm_rule_enabled":    _metaScheduler.SetRuleEnabled(cmd.Index, on); break;
            case "mm_countdown_start":
                _metaScheduler.StartCountdown(cmd.Index,
                    double.TryParse(cmd.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 ? m : null, now);
                break;
            case "mm_countdown_stop":  _metaScheduler.StopCountdown(cmd.Index); break;
            case "mm_reset":
                if (cmd.Index < 0) for (int i = 0; i < _metaScheduler.Config.Rules.Count; i++) _metaScheduler.ResetRule(i);
                else _metaScheduler.ResetRule(cmd.Index);
                break;
            case "mm_poll_now":        _metaScheduler.RequestPoll(); break;
        }
        SaveMetaScheduleIfDirty();
        _metaScheduleSnapAt = 0;   // the panel's next read shows the change
        _metaScheduleJson = _metaScheduler.BuildSnapshotJson(now, _dashboard?.Settings.CurrentMetaPath ?? string.Empty);
    }
}
