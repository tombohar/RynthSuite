// RynthAiPlugin.FellowOpen.cs - Open fellowship on create (2026-11-04).
//
// AC makes a new fellowship closed (only the leader recruits). VTank's auto fellow management opens
// the ones it makes, and players moving over asked for the same. With Auto Fellowship Management and
// Open fellowship on create on, a create RynthAi sends (/mt fellow create, typed or from a meta, or a
// meta's own /fellowship create line) waits for the fellowship to show up with this character leading
// it, then sends one "/fellowship open". Nothing is waited for when the character was already in a
// fellowship (the create fails), and nothing is sent for a fellowship made or closed by hand.
using System;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>How long a create waits for the server to make the fellowship.</summary>
    internal const long FellowOpenWaitMs = 15_000;

    // Environment.TickCount64 the wait ends at; 0 = no create waiting.
    private long _fellowOpenUntil;

    /// <summary>Tests: where the fellowship stands, in place of the tracker's read of AC's memory.</summary>
    internal Func<(bool InFellowship, bool IsLeader, bool IsOpen)>? FellowStateOverride;

    private (bool InFellowship, bool IsLeader, bool IsOpen) FellowState()
    {
        if (FellowStateOverride != null) return FellowStateOverride();
        var t = _fellowshipTracker;
        if (t == null || !t.IsInFellowship) return (false, false, false);
        return (true, t.IsLeader, t.IsOpen);
    }

    /// <summary>A meta's chat line that creates a fellowship through the client ("/fellowship create name").</summary>
    private static bool IsFellowshipCreateLine(string cmd)
    {
        string c = cmd.TrimStart();
        if (c.Length < 2 || (c[0] != '/' && c[0] != '@')) return false;
        string[] w = c.Substring(1).Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return w.Length >= 3
            && w[0].Equals("fellowship", StringComparison.OrdinalIgnoreCase)
            && w[1].Equals("create", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>RynthAi just sent a fellowship create: wait for it, to open it, when the settings say so.</summary>
    private void NoteFellowshipCreate()
    {
        var s = _dashboard?.Settings;
        if (s == null || !s.AutoFellowMgmt || !s.OpenFellowOnCreate) return;
        if (FellowState().InFellowship) return;   // already in one: the create fails, and this one isn't ours
        _fellowOpenUntil = Environment.TickCount64 + FellowOpenWaitMs;
    }

    /// <summary>Plugin tick: the fellowship RynthAi created is here and ours to lead: open it, once.</summary>
    private void TickFellowOpen()
    {
        if (_fellowOpenUntil == 0) return;
        var s = _dashboard?.Settings;
        if (s == null || !s.AutoFellowMgmt || !s.OpenFellowOnCreate) { _fellowOpenUntil = 0; return; }   // switched off meanwhile
        if (Environment.TickCount64 > _fellowOpenUntil)
        {
            _fellowOpenUntil = 0;
            Log("[RynthAi] Open fellowship on create: no fellowship came of the create; nothing opened.");
            return;
        }
        var (inFellowship, isLeader, isOpen) = FellowState();
        if (!inFellowship) return;
        _fellowOpenUntil = 0;
        if (!isLeader || isOpen) return;   // someone else's, or already open
        if (!Host.HasInvokeChatParser) return;
        Host.InvokeChatParser("/fellowship open");
        Log("[RynthAi] Open fellowship on create: sent /fellowship open.");
    }
}
