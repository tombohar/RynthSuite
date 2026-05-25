using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthTracker;

public sealed class RynthTrackerPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthTracker");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");

    private uint _playerId;
    private bool _loginComplete;
    private DateTime _sessionStart;

    private long _xpTotal;
    private long _lumTotal;
    private int  _killsTotal;
    private int  _deathsTotal;

    // Deduplication: track IDs already counted as dead this session to prevent
    // double-counting if the server sends multiple health=0 packets for the same object.
    private readonly HashSet<uint> _killedIds = new();

    private int _tickCounter;

    // Written on the game thread every ~0.5s, read on the Avalonia UI thread.
    // Reference reads are atomic on x86 so volatile is sufficient; no lock needed.
    private volatile string _snapshot = EmptySnapshot;

    private const string EmptySnapshot =
        "{\"ss\":0.0,\"xt\":0,\"xh\":0,\"lt\":0,\"lh\":0,\"kt\":0,\"kh\":0.0,\"xk\":0,\"dt\":0}";

    public override int Initialize()
    {
        Host.Log("[RynthTracker] Plugin initialized.");
        return 0;
    }

    public override void OnLoginComplete()
    {
        _playerId = Host.GetPlayerId();
        ResetSession();
        _loginComplete = true;
        Host.Log("[RynthTracker] Session started.");
    }

    public override void OnLogout()
    {
        _loginComplete = false;
        Host.Log("[RynthTracker] Session ended.");
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (!_loginComplete || string.IsNullOrEmpty(text)) return;

        // XP:  "You've earned N experience."
        // Lum: "You've earned N Luminance."
        int earnedIdx = text.IndexOf("earned ", StringComparison.OrdinalIgnoreCase);
        if (earnedIdx < 0) return;

        int numStart = earnedIdx + 7; // "earned ".Length
        long amount = ParseLeadingNumber(text, numStart);
        if (amount <= 0) return;

        int afterNum = SkipPastNumber(text, numStart);
        if (text.IndexOf("experience", afterNum, StringComparison.OrdinalIgnoreCase) >= 0)
            _xpTotal += amount;
        else if (text.IndexOf("luminance", afterNum, StringComparison.OrdinalIgnoreCase) >= 0)
            _lumTotal += amount;
    }

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (!_loginComplete) return;

        if (targetId == _playerId)
        {
            // maxHealth > 0 guards against spurious 0/0 packets during login.
            if (currentHealth == 0 && maxHealth > 0)
                _deathsTotal++;
            return;
        }

        if (currentHealth == 0 && maxHealth > 0 && _killedIds.Add(targetId))
            _killsTotal++;
    }

    public override void OnDeleteObject(uint objectId)
    {
        _killedIds.Remove(objectId);
    }

    public override void OnTick()
    {
        if (++_tickCounter < 30) return; // rebuild every ~0.5s at 60Hz
        _tickCounter = 0;
        _snapshot = BuildSnapshot();
    }

    internal string GetSnapshot() => _snapshot;

    internal void Reset()
    {
        ResetSession();
        Host.Log("[RynthTracker] Session reset.");
    }

    private void ResetSession()
    {
        _sessionStart = DateTime.UtcNow;
        _xpTotal    = 0;
        _lumTotal   = 0;
        _killsTotal  = 0;
        _deathsTotal = 0;
        _killedIds.Clear();
        _snapshot = EmptySnapshot;
    }

    private string BuildSnapshot()
    {
        double ss   = _loginComplete ? (DateTime.UtcNow - _sessionStart).TotalSeconds : 0.0;
        double hrs  = ss / 3600.0;
        long   xpHr   = hrs > 0.0 ? (long)(_xpTotal  / hrs) : 0;
        long   lumHr  = hrs > 0.0 ? (long)(_lumTotal  / hrs) : 0;
        double killHr = hrs > 0.0 ? _killsTotal / hrs : 0.0;
        long   xpKill = _killsTotal > 0 ? _xpTotal / _killsTotal : 0;

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"ss\":"  + ss.ToString("F1", ic)
             + ",\"xt\":"  + _xpTotal
             + ",\"xh\":"  + xpHr
             + ",\"lt\":"  + _lumTotal
             + ",\"lh\":"  + lumHr
             + ",\"kt\":"  + _killsTotal
             + ",\"kh\":"  + killHr.ToString("F1", ic)
             + ",\"xk\":"  + xpKill
             + ",\"dt\":"  + _deathsTotal
             + "}";
    }

    // Parse the leading run of digit+comma characters starting at 'start'.
    private static long ParseLeadingNumber(string text, int start)
    {
        long result   = 0;
        bool hasDigit = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '0' && c <= '9')     { result = result * 10 + (c - '0'); hasDigit = true; }
            else if (c == ',' && hasDigit) { }   // skip thousands separator
            else if (hasDigit)             break; // first non-number char ends the parse
        }
        return hasDigit ? result : 0;
    }

    // Return the index of the first character after the leading number at 'start'.
    private static int SkipPastNumber(string text, int start)
    {
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c != ',' && (c < '0' || c > '9')) return i;
        }
        return text.Length;
    }
}
