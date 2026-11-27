// ============================================================================
//  RynthCore.Plugin.RynthAi - LosLineOverlay.cs
//  Show Line of Sight (2026-11-26, a testing aid, setting ShowLosLine, /ra losline):
//  a line in the world from the character's chest to the combat target's chest,
//  the path the targeting raycast tests, in the colour of the verdict the target
//  scan gave it (CombatManager.LosVerdicts.cs; no raycast here):
//    green          the raycast tested the line and says clear
//    dashed green   clear without a geometry test (raycasting off or warming up,
//                   no geometry near the path, too far)
//    red            the raycast says blocked; a yellow marker where it hit
//  Thin dim lines (green-grey / red-grey) go to the other monsters the scan tested,
//  nearest first, at most MaxOthers.
//
//  Drawn on the engine's Nav3D buffer like the nav markers (NavMarkerRenderer):
//  frame X = east metres, Y = height, Z = north metres, in the player's landblock.
//  Submitted from the plugin tick, before the nav markers, so a long route's lines
//  can't crowd it out of the 512-line buffer.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

internal sealed class LosLineOverlay
{
    // ARGB (D3D9).
    internal const uint ColorClear        = 0xFF20E040;
    internal const uint ColorBlocked      = 0xFFFF3030;
    internal const uint ColorHit          = 0xFFFFE020;
    internal const uint ColorOtherClear   = 0x9080B088;
    internal const uint ColorOtherBlocked = 0x90B07070;

    internal const float MainThick  = 0.05f;   // half-width of the line's ground quad, metres
    internal const float OtherThick = 0.02f;
    internal const float ChestHeight = 1.0f;   // TargetingFSM tests feet + 1 m on both ends
    internal const int   MaxOthers   = 12;
    internal const double StaleMs    = 3000;   // a verdict older than this isn't drawn (no scan lately)
    internal const float DashStep    = 0.8f;   // dashed: one dash per this many metres...
    internal const float DashFill    = 0.55f;  // ...of this fraction of the step
    internal const int   MaxDashes   = 40;
    internal const float HitRingRadius = 0.25f, HitRingHeight = 0.5f, HitSize = 0.18f;
    private static readonly float[] RimX = { 1f, 0f, -1f, 0f };
    private static readonly float[] RimZ = { 0f, 1f, 0f, -1f };

    private readonly RynthCoreHost _host;
    private readonly Func<LegacyUiSettings?> _settings;
    private readonly Func<CombatManager?> _combat;
    private readonly List<(int Id, CombatManager.LosVerdict V)> _others = new();

    /// <summary>Tests: the clock staleness is judged by.</summary>
    internal Func<DateTime> Clock { get; set; } = () => RynthClock.Now;

    public LosLineOverlay(RynthCoreHost host, Func<LegacyUiSettings?> settings, Func<CombatManager?> combat)
    {
        _host = host;
        _settings = settings;
        _combat = combat;
    }

    /// <summary>Lines drawn by the last Submit (tests and /ra losline status).</summary>
    internal int LastLines { get; private set; }

    /// <summary>The plugin tick: submit this tick's line(s). Nothing when the setting is off.</summary>
    public void Submit()
    {
        LastLines = 0;
        var s = _settings();
        if (s == null || !s.ShowLosLine || !_host.HasNav3D) return;
        var cm = _combat();
        if (cm == null || cm.LosVerdicts.Count == 0) return;
        try { Draw(cm); }
        catch (Exception ex) { _host.Log($"LosLine(3D): {ex.Message}"); }
    }

    private void Draw(CombatManager cm)
    {
        if (!_host.TryGetPlayerPose(out uint cell, out float px, out float py, out float pz, out _, out _, out _, out _))
            return;
        if ((cell >> 16) == 0) return;                           // portal space
        if (_host.HasIsPortaling && _host.IsPortaling()) return;

        float baseX = ((cell >> 24) & 0xFF) * 192f, baseY = ((cell >> 16) & 0xFF) * 192f;
        float ax = px, ay = pz + ChestHeight, az = py;           // the player's chest, Nav3D frame
        DateTime now = Clock();

        int target = cm.LosLineTargetId;
        if (target != 0 && cm.TryGetLosVerdict(target, out var tv) && Fresh(tv, now)
            && TargetChest(target, baseX, baseY, out float bx, out float by, out float bz))
        {
            uint color = tv.Blocked ? ColorBlocked : ColorClear;
            if (!tv.Blocked && !tv.Tested) Dashed(ax, ay, az, bx, by, bz, MainThick, color);
            else Line(ax, ay, az, bx, by, bz, MainThick, color);
            if (tv.Blocked && tv.HasHit) HitMarker(tv.HitX - baseX, tv.HitZ, tv.HitY - baseY);
        }

        // The other monsters the scan tested, nearest first.
        _others.Clear();
        foreach (var kv in cm.LosVerdicts)
            if (kv.Key != target && Fresh(kv.Value, now)) _others.Add((kv.Key, kv.Value));
        if (_others.Count == 0) return;
        _others.Sort((a, b) => a.V.Distance.CompareTo(b.V.Distance));
        int n = Math.Min(_others.Count, MaxOthers);
        for (int i = 0; i < n; i++)
        {
            var (id, v) = _others[i];
            if (!TargetChest(id, baseX, baseY, out float ox, out float oy, out float oz)) continue;
            Line(ax, ay, az, ox, oy, oz, OtherThick, v.Blocked ? ColorOtherBlocked : ColorOtherClear);
        }
    }

    private bool Fresh(in CombatManager.LosVerdict v, DateTime now) => (now - v.At).TotalMilliseconds <= StaleMs;

    /// <summary>A monster's chest in the player's Nav3D frame (its landblock can be the next one).</summary>
    private bool TargetChest(int id, float baseX, float baseY, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (!_host.TryGetObjectPosition(unchecked((uint)id), out uint tc, out float tx, out float ty, out float tz) || tc == 0)
            return false;
        x = ((tc >> 24) & 0xFF) * 192f + tx - baseX;
        z = ((tc >> 16) & 0xFF) * 192f + ty - baseY;
        y = tz + ChestHeight;
        return true;
    }

    private void Line(float x1, float y1, float z1, float x2, float y2, float z2, float thick, uint color)
    {
        _host.Nav3DAddLine(x1, y1, z1, x2, y2, z2, thick, color);
        LastLines++;
    }

    private void Dashed(float x1, float y1, float z1, float x2, float y2, float z2, float thick, uint color)
    {
        float dx = x2 - x1, dy = y2 - y1, dz = z2 - z1;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        int steps = Math.Clamp((int)MathF.Ceiling(len / DashStep), 2, MaxDashes);
        for (int i = 0; i < steps; i++)
        {
            float t0 = (float)i / steps, t1 = (i + DashFill) / steps;
            Line(x1 + dx * t0, y1 + dy * t0, z1 + dz * t0, x1 + dx * t1, y1 + dy * t1, z1 + dz * t1, thick, color);
        }
    }

    /// <summary>A short yellow post round the hit point, and a small diamond on it when triangles are there.</summary>
    private void HitMarker(float x, float y, float z)
    {
        _host.Nav3DAddRingEx(x, y - HitRingHeight * 0.5f, z, HitRingRadius, 0.04f, HitRingHeight, ColorHit);
        if (!_host.HasNav3DTriangle) return;
        float h = HitSize;
        // An octahedron: four faces up, four down, round the rim (h,0) (0,h) (-h,0) (0,-h).
        for (int i = 0; i < 4; i++)
        {
            float ax = RimX[i] * h, az = RimZ[i] * h, bx = RimX[(i + 1) % 4] * h, bz = RimZ[(i + 1) % 4] * h;
            _host.Nav3DAddTriangle(x + ax, y, z + az, x + bx, y, z + bz, x, y + h, z, ColorHit);
            _host.Nav3DAddTriangle(x + ax, y, z + az, x + bx, y, z + bz, x, y - h, z, ColorHit);
        }
    }
}
