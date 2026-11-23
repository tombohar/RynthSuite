// The hunt fence (2026-11-17): a circle the outdoor hunt picks its monsters inside and combat fights inside.
//
// DreamWeave's open-world Threads are a circle: 15 m inside its edge the server warns ("The Thread thins
// here. Turn back toward its heart."), outside it the Unraveling hurts more every second, and the Thread's
// monsters are held to the circle (a hit from a player outside it is refused). The hunt picked monsters by
// distance alone and walked out past the edge after them ("In outdoor threads he is going outside of
// the thread boundary"). A script reads the circle off the ring of wisps the server sets on its edge and
// fences the hunt a margin inside it: /ra hunt fence <x> <y> <radius> (global metres: landblock x * 192 +
// local x, as the hunt's own positions), /ra hunt fence off. Not saved: a script sets it for the place it
// is in, and clears it when it leaves.
//
// A walk is fenced too (same day: "here and there nav takes him outside of the thread boundary"): to a
// monster inside the fence, the way round a building can swing out past the edge. An optional fourth number
// is how far out a walk may go (default the fence and 15 m); a walk whose corners go out past it, farther
// out than where the character stands, is no walk (the monster is left out a while, as one with no way to it).
// From outside it a walk back in is still a walk.
//
// The hunt (OutdoorHunt.Fence) leaves a monster outside the fence out of its picks; combat
// (CombatManager.SkipTarget) leaves one out of its targets, unless it is the one
// being fought or the player's own target lock.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The fence (centre and radius, global metres), or null: none.</summary>
    internal (double X, double Y, double R)? HuntFence { get; private set; }
    /// <summary>How far from the fence's centre a walk may go (metres).</summary>
    internal double HuntFenceWalkR { get; private set; }

    /// <summary>A walk may go this much past the fence when the fence is given without its walk radius.</summary>
    internal const double HuntFenceWalkDefaultM = 15.0;

    internal const string HuntFenceUsage = "[RynthAi] Usage: /ra hunt fence <x> <y> <radius> [walk radius]  (global metres: landblock x * 192 + local x) or /ra hunt fence off";

    /// <summary>OutdoorHunt.WalkFence: no corner out past the walk radius farther out than where it starts (me).</summary>
    internal bool WalkInHuntFence(OutdoorHunt.Vec me, IReadOnlyList<OutdoorHunt.Vec> corners)
    {
        if (FenceHere() is not { } f || corners.Count == 0) return true;
        double Out(OutdoorHunt.Vec v) => Math.Sqrt((v.X - f.X) * (v.X - f.X) + (v.Y - f.Y) * (v.Y - f.Y));
        double start = Out(me);
        foreach (var c in corners)
        {
            double d = Out(c);
            if (d > HuntFenceWalkR && d > start + 2.0) return false;
        }
        return true;
    }

    /// <summary>True when (x, y) (global metres) is inside the fence, or there is none (or it was for another place).</summary>
    internal bool InHuntFence(double x, double y)
        => FenceHere() is not { } f || (x - f.X) * (x - f.X) + (y - f.Y) * (y - f.Y) <= f.R * f.R;

    /// <summary>A monster this near the character is fought, fence or not.</summary>
    internal const double FenceFightNearM = 10.0;
    /// <summary>Outside the fence, a monster no more than this farther from its middle than the character (up to the walk radius) is fought.</summary>
    internal const double FenceFightOutM = 5.0;
    /// <summary>The character this far past the walk radius: the fence was for another place, and is lifted.</summary>
    internal const double FenceForgetM = 300.0;

    /// <summary>
    /// CombatManager.SkipTarget's fence half: a monster outside the fence is left alone, unless it is near
    /// the character or no farther out than the character stands (FenceFightOutM, up to the walk radius).
    /// 2026-11-17 16:40, DreamWeave Thread 32D9: the character stood 127 m from the middle of a 124 m fence,
    /// among the Thread's monsters, all outside it; combat took none of them ("possible=0"), and it stood
    /// there being hit until it died ("he's just standing there not attacking").
    /// </summary>
    internal bool FenceSkipsTarget(int id)
    {
        if (FenceHere() is not { } f) return false;
        if (!Host.TryGetObjectPosition(unchecked((uint)id), out uint c, out float x, out float y, out _)) return false;
        double gx = ((c >> 24) & 0xFF) * 192.0 + x, gy = ((c >> 16) & 0xFF) * 192.0 + y;
        bool hasMe = TryHuntPosition(out _, out var me);
        return FenceSkips(f, HuntFenceWalkR, gx, gy, hasMe, me.X, me.Y);
    }

    /// <summary>The pure half of <see cref="FenceSkipsTarget"/>.</summary>
    internal static bool FenceSkips((double X, double Y, double R) f, double walkR, double gx, double gy, bool hasMe, double mx, double my)
    {
        double dMon = Math.Sqrt((gx - f.X) * (gx - f.X) + (gy - f.Y) * (gy - f.Y));
        if (dMon <= f.R) return false;
        if (!hasMe) return true;
        if (Math.Sqrt((gx - mx) * (gx - mx) + (gy - my) * (gy - my)) <= FenceFightNearM) return false;
        double dMe = Math.Sqrt((mx - f.X) * (mx - f.X) + (my - f.Y) * (my - f.Y));
        return dMon > Math.Min(dMe + FenceFightOutM, Math.Max(walkR, f.R));
    }

    private long _fenceCheckedAt;

    /// <summary>
    /// The fence, for where the character is: null when there is none, and lifted when the character is in a
    /// dungeon or far past it (FenceForgetM). 2026-11-17 16:42, the fence of Thread 32D9 outlived the Thread
    /// (the script lost it with the Thread at a death): in the next Thread, a dungeon, every monster was
    /// outside it, and combat fought none ("he died because he isn't fighting").
    /// </summary>
    internal (double X, double Y, double R)? FenceHere()
    {
        if (HuntFence is not { } f) return null;
        long now = Environment.TickCount64;
        if (now - _fenceCheckedAt < 500) return f;
        _fenceCheckedAt = now;
        if (!TryHuntPosition(out uint cell, out var me)) return f;
        double d = Math.Sqrt((me.X - f.X) * (me.X - f.X) + (me.Y - f.Y) * (me.Y - f.Y));
        bool dungeon = OutdoorHunt.IsIndoorCell(cell) && InDungeon(cell);
        if (!dungeon && d <= Math.Max(HuntFenceWalkR, f.R) + FenceForgetM) return f;
        HuntFence = null;
        HuntFenceWalkR = 0;
        Host.Log(string.Format(CultureInfo.InvariantCulture, "[RynthAi] Hunt fence: lifted ({0}): it was for another place.",
            dungeon ? "in a dungeon" : $"{d:0} m from its middle"));
        return null;
    }

    /// <summary>/ra hunt fence &lt;x&gt; &lt;y&gt; &lt;radius&gt; | off (parts[3..]).</summary>
    private void SetHuntFence(string[] parts)
    {
        string a = parts.Length > 3 ? parts[3].Trim().ToLowerInvariant() : "";
        if (a is "off" or "none" or "clear")
        {
            bool had = HuntFence != null;
            HuntFence = null;
            HuntFenceWalkR = 0;
            ChatLine(had ? "[RynthAi] Hunt fence: off." : "[RynthAi] Hunt fence: there was none.");
            return;
        }
        var inv = CultureInfo.InvariantCulture;
        if (parts.Length < 6
            || !double.TryParse(parts[3], NumberStyles.Float, inv, out double x)
            || !double.TryParse(parts[4], NumberStyles.Float, inv, out double y)
            || !double.TryParse(parts[5], NumberStyles.Float, inv, out double r)
            || double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(r) || r <= 0)
        {
            ChatLine(a == "" && HuntFence is { } f
                ? string.Format(inv, "[RynthAi] Hunt fence: {0:0.0} m round {1:0.0}, {2:0.0}.", f.R, f.X, f.Y)
                : HuntFenceUsage);
            return;
        }
        double walk = r + HuntFenceWalkDefaultM;
        if (parts.Length > 6 && double.TryParse(parts[6], NumberStyles.Float, inv, out double w) && w >= r) walk = w;
        HuntFence = (x, y, r);
        HuntFenceWalkR = walk;
        string where = "";
        if (TryHuntPosition(out _, out var me))
        {
            double d = Math.Sqrt((me.X - x) * (me.X - x) + (me.Y - y) * (me.Y - y));
            where = string.Format(inv, "; you are {0:0} m from its middle{1}", d, d > r ? " (outside it)" : "");
        }
        ChatLine(string.Format(inv, "[RynthAi] Hunt fence: {0:0.0} m round {1:0.0}, {2:0.0}, walks to {3:0.0} m{4}.", r, x, y, walk, where));
    }
}
