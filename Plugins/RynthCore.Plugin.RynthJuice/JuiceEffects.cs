using System;
using System.Collections.Generic;
using System.Numerics;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthJuice;

/// <summary>
/// Owns the live combat-juice effects and renders them every tick through the
/// engine's Nav3D world-space API. Floating numbers are drawn as billboarded
/// vector-digit glyphs (quads built from <see cref="RynthCoreHost.Nav3DAddTriangle"/>);
/// kill bursts are expanding rings.
///
/// Coordinate frames (see GameMatrixCapture / RadarRingOverlay):
///   • Object/player positions are AC convention: x=EW(east), y=NS(north), z=height(up),
///     local to that object's LANDBLOCK (0–192), outdoors only.
///   • Nav3D and WorldToScreen want a Y-up player-landblock-local frame: (east, up, north).
///   • A target in a different landblock than the player is shifted by
///     (Δlandblock × 192) per axis before projecting — mirroring the engine's own
///     camera re-anchor in GameMatrixCapture.
/// </summary>
internal sealed class JuiceEffects
{
    public enum Kind { MobDamage, Heal, PlayerDamage }

    private sealed class Effect
    {
        public bool IsBurst;
        public uint CellId;
        public float E0, N0, U0;   // anchor in the effect's own landblock-local frame (AC: E,N,U)
        public string Text = "";
        public uint Color;
        public bool Crit;
        public Kind Kind;
        public long BirthMs;
        public float Life;         // seconds
    }

    private readonly List<Effect> _fx = new();
    private readonly object _lock = new();
    private EnvCellTransforms? _cells;

    private const int MaxEffects = 48;
    private const float CritScale = 1.5f;

    /// <summary>Wires the dungeon EnvCell transform reader (enables indoor numbers).</summary>
    public void SetCells(EnvCellTransforms cells) => _cells = cells;

    // Diagnostics (read by /juice diag): why Render last bailed + what it emitted.
    public string LastRenderStatus { get; private set; } = "(not ticked yet)";
    public int LastDrawnEffects { get; private set; }
    public int LastTriangles { get; private set; }
    private int _frameDrawn, _frameTris;

    // ── Spawning (called from event handlers) ────────────────────────────────

    public void SpawnDamage(uint cellId, float e, float n, float u, int amount, bool crit)
        => Add(cellId, e, n, u, FormatAmount(amount, crit), crit ? Gold : DamageColor(amount), crit, Kind.MobDamage);

    public void SpawnHeal(uint cellId, float e, float n, float u, int amount)
        => Add(cellId, e, n, u, "+" + amount.ToString(), Green, false, Kind.Heal);

    public void SpawnPlayerDamage(uint cellId, float e, float n, float u, int amount, bool crit)
        => Add(cellId, e, n, u, FormatAmount(amount, crit), crit ? Gold : PlayerRed, crit, Kind.PlayerDamage);

    public void SpawnKillBurst(uint cellId, float e, float n, float u, bool crit, long nowMs)
    {
        if (!Resolve(cellId, ref e, ref n, ref u)) return;
        lock (_lock)
        {
            TrimLocked();
            _fx.Add(new Effect
            {
                IsBurst = true,
                CellId = cellId, E0 = e, N0 = n, U0 = u,
                Color = 0xFF60FFFF, // bright cyan — max visibility for the "is it working?" test
                BirthMs = nowMs,
                Life = 1.6f,
            });
        }
    }

    /// <summary>Upgrade the most recent damage number to a crit (handles crit chat
    /// arriving just after the health packet). Returns true if one was upgraded.</summary>
    public bool UpgradeRecentToCrit(long nowMs, int windowMs)
    {
        lock (_lock)
        {
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var f = _fx[i];
                if (f.IsBurst || f.Kind == Kind.Heal || f.Crit) continue;
                if (nowMs - f.BirthMs > windowMs) break; // list is append-ordered; older beyond window
                f.Crit = true;
                f.Color = Gold;
                f.Text = EnsureBang(f.Text);
                f.Life = MathF.Max(f.Life, 1.7f);
                return true;
            }
        }
        return false;
    }

    /// <summary>Overwrite the most recent matching damage number with the exact
    /// amount parsed from the combat log (the health-packet anchor stays). Returns
    /// true if one was updated within the window.</summary>
    public bool ApplyExactToRecent(long nowMs, int windowMs, Kind kind, int amount, bool crit)
    {
        lock (_lock)
        {
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var f = _fx[i];
                if (nowMs - f.BirthMs > windowMs) break; // append-ordered → all earlier are older
                if (f.IsBurst || f.Kind != kind) continue;
                f.Crit = f.Crit || crit;
                f.Text = FormatAmount(amount, f.Crit);
                f.Color = f.Crit ? Gold : (kind == Kind.PlayerDamage ? PlayerRed : DamageColor(amount));
                if (f.Crit) f.Life = MathF.Max(f.Life, 1.7f);
                return true;
            }
        }
        return false;
    }

    public void Clear() { lock (_lock) _fx.Clear(); }
    public int Count { get { lock (_lock) return _fx.Count; } }

    private void Add(uint cellId, float e, float n, float u, string text, uint color, bool crit, Kind kind)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!Resolve(cellId, ref e, ref n, ref u)) return; // indoor w/o cell transform → skip
        lock (_lock)
        {
            TrimLocked();
            _fx.Add(new Effect
            {
                CellId = cellId, E0 = e, N0 = n, U0 = u,
                Text = text, Color = color, Crit = crit, Kind = kind,
                BirthMs = Environment.TickCount64,
                Life = crit ? 1.7f : (kind == Kind.Heal ? 1.15f : 1.35f),
            });
        }
    }

    private void TrimLocked()
    {
        // Drop the oldest non-burst effect if we're at the cap (keep bursts; they're brief).
        while (_fx.Count >= MaxEffects)
        {
            int idx = _fx.FindIndex(f => !f.IsBurst);
            _fx.RemoveAt(idx >= 0 ? idx : 0);
        }
    }

    // ── Rendering (called from OnTick) ───────────────────────────────────────

    public void Render(RynthCoreHost host, JuiceSettings cfg, long nowMs)
    {
        if (!host.HasNav3D || !host.HasNav3DTriangle)
        { LastRenderStatus = $"no-nav3d (nav={host.HasNav3D} tri={host.HasNav3DTriangle})"; return; }

        if (!host.TryGetPlayerPose(out uint pCell, out float pE, out float pN, out float pU,
                out _, out _, out _, out _))
        { LastRenderStatus = "no-pose"; return; }
        if ((pCell >> 16) == 0)
        { LastRenderStatus = "portalspace"; return; }
        // Indoors, convert the player's cell-local pose to landblock-relative so
        // distance math agrees with the (already landblock-relative) anchors.
        if (!Resolve(pCell, ref pE, ref pN, ref pU))
        { LastRenderStatus = "player-resolve-fail (indoor cell.dat unavailable?)"; return; }
        uint playerLb = pCell >> 16;

        _frameDrawn = 0; _frameTris = 0;
        int culFar = 0, culDist = 0;
        lock (_lock)
        {
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var f = _fx[i];
                float age = (nowMs - f.BirthMs) / 1000f;
                if (age >= f.Life) { _fx.RemoveAt(i); continue; }
                if (f.CellId >> 16 == 0) { _fx.RemoveAt(i); continue; }

                // Shift the effect's own-landblock anchor into the player's frame.
                int dXb = (int)(((f.CellId >> 16 >> 8) & 0xFF)) - (int)((playerLb >> 8) & 0xFF);
                int dYb = (int)(((f.CellId >> 16) & 0xFF)) - (int)(playerLb & 0xFF);
                if (Math.Abs(dXb) > 3 || Math.Abs(dYb) > 3) { culFar++; continue; } // too far / region junk
                float aE = f.E0 + dXb * 192f;
                float aN = f.N0 + dYb * 192f;

                // Distance cull (horizontal).
                float dE = aE - pE, dN = aN - pN;
                if (dE * dE + dN * dN > cfg.MaxDistance * cfg.MaxDistance) { culDist++; continue; }

                if (f.IsBurst) { RenderBurst(host, f, aE, aN, age); _frameDrawn++; }
                else RenderNumber(host, cfg, f, aE, aN, age); // self-counts _frameDrawn on success
            }
        }
        LastDrawnEffects = _frameDrawn;
        LastTriangles = _frameTris;
        LastRenderStatus = $"ok fx={_fx.Count} drawn={_frameDrawn} tris={_frameTris} culFar={culFar} culDist={culDist}";
    }

    private static void RenderBurst(RynthCoreHost host, Effect f, float aE, float aN, float age)
    {
        float t = age / f.Life;                       // 0→1
        float alpha = 1f - t;
        uint col = WithAlpha(f.Color, alpha);
        float baseU = f.U0 + 0.3f;

        // Big expanding ground ring.
        float groundR = 0.6f + 8f * t;
        host.Nav3DAddRingEx(aE, baseU, aN, groundR, 0.6f, 0.5f, col);

        // A tall bright PILLAR of stacked rings (~10 m), so a kill is unmistakable
        // even if the subtle stuff isn't landing yet. Tapers toward the top.
        const int rings = 11;
        for (int i = 0; i < rings; i++)
        {
            float h = baseU + i * 1.0f;
            float pr = 2.0f * (1f - i / (float)rings) + 0.4f;
            host.Nav3DAddRingEx(aE, h, aN, pr, 0.35f, 0.3f, col);
        }
    }

    private void RenderNumber(RynthCoreHost host, JuiceSettings cfg, Effect f, float aE, float aN, float age)
    {
        // Animation: rise in world-up, pop-scale early, fade late.
        float rise = 1.15f * age;
        float aU = f.U0 + cfg.HeightOffset + rise;

        float pop = 1f + (f.Crit ? 0.6f : 0.35f) * MathF.Max(0f, 1f - age / (f.Crit ? 0.14f : 0.10f));
        float kindScale = f.Kind == Kind.PlayerDamage ? 1.15f : 1f;
        float critScale = f.Crit ? CritScale : 1f;

        float alpha = 1f;
        float fadeStart = 0.55f * f.Life;
        if (age > fadeStart) alpha = MathF.Max(0f, 1f - (age - fadeStart) / (f.Life - fadeStart));
        alpha *= MathF.Min(1f, age / 0.06f); // quick fade-in
        uint col = WithAlpha(f.Color, alpha);

        // Size, then draw via the shared glyph routine.
        float heightPx = 22f * cfg.Scale * pop * kindScale * critScale;
        DrawGlyphs(host, aE, aU, aN, f.Text, heightPx, col);
    }

    /// <summary>Live monster-health readout: a "NN" percentage floating over each
    /// actively-damaged mob, green→red by ratio. Persistent (re-drawn every tick so
    /// it tracks the mob and updates as it's hit), unlike the transient numbers.</summary>
    public void RenderMobHealth(RynthCoreHost host, JuiceSettings cfg, List<MobHp> mobs)
    {
        if (mobs.Count == 0 || !host.HasNav3D || !host.HasNav3DTriangle) return;
        if (!host.TryGetPlayerPose(out uint pCell, out float pE, out float pN, out float pU, out _, out _, out _, out _)) return;
        if ((pCell >> 16) == 0) return;
        if (!Resolve(pCell, ref pE, ref pN, ref pU)) return;
        uint playerLb = pCell >> 16;

        foreach (MobHp m in mobs)
        {
            if ((m.CellId >> 16) == 0) continue;
            int dXb = (int)(((m.CellId >> 16 >> 8) & 0xFF)) - (int)((playerLb >> 8) & 0xFF);
            int dYb = (int)(((m.CellId >> 16) & 0xFF)) - (int)(playerLb & 0xFF);
            if (Math.Abs(dXb) > 3 || Math.Abs(dYb) > 3) continue;
            float aE = m.E0 + dXb * 192f;
            float aN = m.N0 + dYb * 192f;
            float dE = aE - pE, dN = aN - pN;
            if (dE * dE + dN * dN > cfg.MaxDistance * cfg.MaxDistance) continue;

            int pct = (int)MathF.Round(Math.Clamp(m.Ratio, 0f, 1f) * 100f);
            float aU = m.U0 + cfg.HeightOffset + 0.9f; // just above the damage-number lane
            DrawGlyphs(host, aE, aU, aN, pct.ToString(), 15f * cfg.Scale, HealthColor(m.Ratio));
        }
    }

    /// <summary>Billboarded vector-glyph text at a world anchor — shared by the
    /// damage numbers and the live monster-health %. False if it couldn't project.</summary>
    private bool DrawGlyphs(RynthCoreHost host, float aE, float aU, float aN, string text, float heightPx, uint col)
    {
        if (!ComputeBillboard(host, aE, aU, aN, out Vector3 uVec, out Vector3 vVec, out _, out _))
            return false;
        _frameDrawn++;

        float cell = heightPx / VectorFont.GlyphHeight;   // px per grid unit
        float halfW = MathF.Max(1.0f, cell * 0.16f);      // stroke half-thickness, px
        float startGridX = -VectorFont.MeasureWidth(text) / 2f;

        for (int ci = 0; ci < text.Length; ci++)
        {
            var strokes = VectorFont.Strokes(text[ci]);
            float charX = startGridX + ci * VectorFont.Advance;
            foreach (var s in strokes)
            {
                var p1 = new Vector2((charX + s.X) * cell, (s.Y - VectorFont.GlyphHeight / 2f) * cell);
                var p2 = new Vector2((charX + s.Z) * cell, (s.W - VectorFont.GlyphHeight / 2f) * cell);
                EmitStrokeQuad(host, aE, aU, aN, uVec, vVec, p1, p2, halfW, col);
                _frameTris += 2;
            }
        }
        return true;
    }

    public readonly record struct MobHp(uint CellId, float E0, float N0, float U0, float Ratio);

    private static uint HealthColor(float ratio)
    {
        if (ratio > 0.6f) return 0xFF40FF40;   // green
        if (ratio > 0.3f) return 0xFFFFF040;   // yellow
        if (ratio > 0.12f) return 0xFFFFA030;  // orange
        return 0xFFFF3030;                     // red
    }

    /// <summary>Builds one rounded-cap stroke as two world-space triangles.</summary>
    private static void EmitStrokeQuad(RynthCoreHost host, float aE, float aU, float aN,
        Vector3 u, Vector3 v, Vector2 p1, Vector2 p2, float halfW, uint col)
    {
        Vector2 d = p2 - p1;
        float len = d.Length();
        if (len < 0.0001f) return;
        d /= len;
        Vector2 perp = new(-d.Y, d.X);
        Vector2 ext = d * halfW;          // extend ends so segments overlap at joints
        perp *= halfW;

        Vector2 c0 = p1 - ext + perp;
        Vector2 c1 = p2 + ext + perp;
        Vector2 c2 = p2 + ext - perp;
        Vector2 c3 = p1 - ext - perp;

        // px-offset → world (Nav3D frame x=E, y=U, z=N)
        (float, float, float) W(Vector2 px) =>
            (aE + px.X * u.X + px.Y * v.X,
             aU + px.X * u.Y + px.Y * v.Y,
             aN + px.X * u.Z + px.Y * v.Z);

        var (w0e, w0u, w0n) = W(c0);
        var (w1e, w1u, w1n) = W(c1);
        var (w2e, w2u, w2n) = W(c2);
        var (w3e, w3u, w3n) = W(c3);

        host.Nav3DAddTriangle(w0e, w0u, w0n, w1e, w1u, w1n, w2e, w2u, w2n, col);
        host.Nav3DAddTriangle(w0e, w0u, w0n, w2e, w2u, w2n, w3e, w3u, w3n, col);
    }

    /// <summary>
    /// Finite-difference Jacobian of WorldToScreen at the anchor, then its
    /// 2×3 pseudo-inverse, giving world vectors u,v that move the projection by
    /// exactly +1px in screen x and y respectively. Constant on-screen size +
    /// automatic camera-facing billboard, with no engine-side camera basis.
    /// </summary>
    private static bool ComputeBillboard(RynthCoreHost host, float aE, float aU, float aN,
        out Vector3 u, out Vector3 v, out float sx, out float sy)
    {
        u = default; v = default; sx = sy = 0f;
        if (!host.HasWorldToScreen) return false;

        const float h = 0.1f;
        if (!host.WorldToScreen(aE, aU, aN, out float s0x, out float s0y)) return false;
        if (!host.WorldToScreen(aE + h, aU, aN, out float eX, out float eY)) return false;
        if (!host.WorldToScreen(aE, aU + h, aN, out float uX, out float uY)) return false;
        if (!host.WorldToScreen(aE, aU, aN + h, out float nX, out float nY)) return false;
        sx = s0x; sy = s0y;

        // Jacobian columns (screen px / metre) for the E, U, N axes.
        float jEx = (eX - s0x) / h, jEy = (eY - s0y) / h;
        float jUx = (uX - s0x) / h, jUy = (uY - s0y) / h;
        float jNx = (nX - s0x) / h, jNy = (nY - s0y) / h;

        // J Jᵀ (2×2, symmetric) and its inverse.
        float a = jEx * jEx + jUx * jUx + jNx * jNx;
        float b = jEx * jEy + jUx * jUy + jNx * jNy;
        float dd = jEy * jEy + jUy * jUy + jNy * jNy;
        float det = a * dd - b * b;
        if (MathF.Abs(det) < 1e-7f) return false;
        float i00 = dd / det, i01 = -b / det, i11 = a / det;

        // u = Jᵀ·(i00,i01)  (screen +x);  v = Jᵀ·(i01,i11)  (screen +y)
        u = new Vector3(jEx * i00 + jEy * i01, jUx * i00 + jUy * i01, jNx * i00 + jNy * i01);
        v = new Vector3(jEx * i01 + jEy * i11, jUx * i01 + jUy * i11, jNx * i01 + jNy * i11);
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Anchor coords pass through unchanged. The diag PROVED the indoor camera/view
    // matrix is CELL-LOCAL (the player's own raw cell-local pos projects to screen
    // centre @799/1598), so RAW object coords project correctly for objects in the
    // player's cell — the EnvCell→landblock transform actually moved them OFF
    // (projXform was wrong, projRaw was right). Outdoor coords are landblock-local
    // and the per-landblock shift in Render handles cross-landblock. Indoor
    // cross-cell mobs (a different EnvCell than the player) land far away in raw
    // coords and get distance-culled rather than mis-drawn — acceptable for v1.
    private bool Resolve(uint cellId, ref float e, ref float n, ref float u) => true;

    private const uint Gold        = 0xFFFFD700;
    private const uint Green       = 0xFF50FF50;
    private const uint PlayerRed   = 0xFFFF2828;
    private const uint BurstOrange = 0xFFFF8C20;

    private static uint DamageColor(int amt)
    {
        if (amt < 25) return 0xFFFFFFFF;   // white
        if (amt < 60) return 0xFFFFF080;   // pale yellow
        if (amt < 120) return 0xFFFFA030;  // orange
        return 0xFFFF3828;                 // red
    }

    private static uint WithAlpha(uint argb, float a)
    {
        uint aa = (uint)Math.Clamp(a * 255f, 0f, 255f);
        return (argb & 0x00FFFFFF) | (aa << 24);
    }

    private static string FormatAmount(int amount, bool crit) =>
        crit ? amount.ToString() + "!" : amount.ToString();

    private static string EnsureBang(string s) =>
        s.EndsWith("!", StringComparison.Ordinal) ? s : s + "!";
}
