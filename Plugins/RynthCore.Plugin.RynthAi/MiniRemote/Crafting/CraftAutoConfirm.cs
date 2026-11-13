// CraftAutoConfirm.cs - "Auto-confirm crafting": say Yes to the server's craft-chance prompt when
// the chance is high enough, for crafts and tinkers the player starts by hand.
//
// When a player combines two items (salvage on an item, a crafting recipe) the server asks first:
// "You determine that you have a N percent chance to succeed." with Yes / No. With the setting on
// (LegacyUiSettings.AutoConfirmCraft, off by default) RynthAi keeps the engine's sticky
// CraftInteraction answer armed (plugin API v97, RynthCoreHost.ArmConfirmationLeaveBelow): a prompt
// at or above the threshold (AutoConfirmCraftPercent, 50-100, default 100) is answered Yes without
// the dialog opening; a lower one shows AC's own dialog as usual. It never says No.
//
// The engine's arm lasts ArmTimeoutMs; it is re-armed every RearmAfterMs, so it stays up while the
// setting is on and goes away by itself at most a minute after RynthAi stops ticking.
//
// The tinker runner (TinkerRunner) arms the engine's ONE-SHOT answer for each of its own steps (Yes
// at the tab's Min chance %, else No). The engine keeps the two in separate slots and asks the
// one-shot first, and a one-shot's arm or disarm never touches the sticky arm, so the runner's
// steps keep its own rule and the sticky arm is still there when the run ends: the two never fight.
// A Yes from the sticky arm is outcome AcceptedSticky, so only those get the chat line.
//
// Pump thread (CraftingTab.Tick, OnLogout). The UI reads Status / Supported only.
using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

internal sealed class CraftAutoConfirm
{
    public const int MinThreshold = 50;
    public const int MaxThreshold = 100;
    public const int DefaultThreshold = 100;
    /// <summary>The engine arm's life (the engine caps it at 120 s).</summary>
    public const int ArmTimeoutMs = 60_000;
    /// <summary>Re-arm this often while on (well inside <see cref="ArmTimeoutMs"/>); also the retry gap after a refusal.</summary>
    public const long RearmAfterMs = 20_000;
    /// <summary>Why the setting can't work on this engine (the checkbox's tooltip and the command's reply).</summary>
    public const string NeedsEngineText = "needs a newer RynthCore (core update with plugin API 97)";

    private readonly Func<RynthCoreHost> _host;
    private readonly Func<LegacyUiSettings?> _settings;
    private readonly Action<string> _chat;

    // Pump thread.
    private bool _armed;
    private int _armedPercent;
    private long _armedAt;
    private long _lastTryAt = long.MinValue / 2;
    private int _seenSeq = -1;
    private volatile string _status = "off";
    private volatile int _accepted;

    public CraftAutoConfirm(Func<RynthCoreHost> host, Func<LegacyUiSettings?> settings, Action<string> chat)
    {
        _host = host;
        _settings = settings;
        _chat = chat;
    }

    /// <summary>The engine can keep a sticky answer (plugin API v97). Any thread.</summary>
    public bool Supported => _host().HasConfirmationLeaveBelow;

    /// <summary>RynthAi's sticky arm is up (pump thread's view).</summary>
    public bool Armed => _armed;

    /// <summary>What it is doing, for the tab and /ra autoconfirm status.</summary>
    public string Status => _status;

    /// <summary>Prompts answered Yes by the sticky arm this session.</summary>
    public int AcceptedCount => _accepted;

    public static int ClampPercent(int percent) => Math.Clamp(percent, MinThreshold, MaxThreshold);

    /// <summary>The chat line after an auto-accepted prompt.</summary>
    public static string AcceptedLine(int percent) => $"[RynthAi] Auto-confirmed: {(percent >= 0 ? percent + "%" : "the")} chance";

    /// <summary>One pump tick: report what the arm answered, then keep it armed while the setting is on.</summary>
    public void Tick(long nowMs)
    {
        RynthCoreHost host = _host();
        ReportAnswers(host);

        LegacyUiSettings? s = _settings();
        if (s == null || !s.AutoConfirmCraft)
        {
            Release(host, "off");
            return;
        }
        if (!host.HasConfirmationLeaveBelow)
        {
            _armed = false;
            _status = "off: " + NeedsEngineText;
            return;
        }

        int percent = ClampPercent(s.AutoConfirmCraftPercent);
        if (_armed && percent == _armedPercent && nowMs - _armedAt < RearmAfterMs)
            return;
        if (!_armed && nowMs - _lastTryAt < RearmAfterMs && percent == _armedPercent)
            return;   // refused a moment ago: try again later, not every tick

        _lastTryAt = nowMs;
        _armedPercent = percent;
        int r = host.ArmConfirmationLeaveBelow(RynthCoreHost.ConfirmationKind.CraftInteraction, percent, ArmTimeoutMs);
        if (r == 1)
        {
            _armed = true;
            _armedAt = nowMs;
            _status = $"on: Yes at {percent}% or better";
            return;
        }
        _armed = false;
        _status = r == 0
            ? "waiting: this client can't answer prompts yet (the engine's sender or hook is missing)"
            : "off: the engine refused the arm (" + NeedsEngineText + ")";
    }

    /// <summary>Drops RynthAi's sticky arm (setting off, logout, teardown). Pump thread.</summary>
    public void Release(string status = "off")
    {
        Release(_host(), status);
        _seenSeq = -1;
    }

    private void Release(RynthCoreHost host, string status)
    {
        if (_armed && host.HasConfirmationLeaveBelow)
            host.DisarmConfirmationLeaveBelow(RynthCoreHost.ConfirmationKind.CraftInteraction);
        _armed = false;
        _armedPercent = 0;
        _status = status;
    }

    /// <summary>One chat line for each prompt the sticky arm said Yes to since the last look.</summary>
    private void ReportAnswers(RynthCoreHost host)
    {
        if (!host.HasConfirmationAnswers
            || !host.TryGetLastConfirmation(out int seq, out uint type, out int percent, out int outcome, out _))
            return;
        if (_seenSeq < 0)
        {
            _seenSeq = seq;   // what came before RynthAi was looking is not reported
            return;
        }
        if (seq == _seenSeq) return;
        _seenSeq = seq;
        if (type != RynthCoreHost.ConfirmationKind.CraftInteraction || outcome != RynthCoreHost.ConfirmationOutcome.AcceptedSticky)
            return;
        _accepted++;
        _chat(AcceptedLine(percent));
    }
}
