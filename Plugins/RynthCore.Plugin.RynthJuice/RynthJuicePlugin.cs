using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthJuice;

/// <summary>
/// Combat "juice" for RynthCore: floating vector-digit damage numbers over
/// monsters (white→orange→red by size, GOLD + bigger on crit), green heal
/// numbers, red numbers over your own character when hit, and an expanding ring
/// burst when a damaged monster dies. Pure Nav3D — no ImGui — so it runs in
/// EnableImGuiShell=false mode and stays fully isolated from RynthAi. Outdoor
/// only in v1 (dungeon coords are cell-local). Toggle with /juice.
/// </summary>
public sealed class RynthJuicePlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthJuice");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");

    private sealed class HState
    {
        public bool HasCurrent;
        public uint LastCurrent;
        public uint KnownMax;
        public float LastRatio;
        public uint Cell;
        public float E, N, U;       // last known position (own-landblock frame)
        public long LastDmgMs;
        public long LastSeenMs;
    }

    private readonly JuiceSettings _cfg = new();
    private readonly JuiceEffects _fx = new();
    private readonly EnvCellTransforms _cells = new();
    private readonly Dictionary<uint, HState> _health = new();
    private readonly HashSet<uint> _killedIds = new();
    private readonly List<JuiceEffects.MobHp> _mobHpScratch = new();
    private readonly HashSet<uint> _tracked = new();   // candidate mob ids (from OnCreateObject) to poll
    private readonly List<uint> _pollScratch = new();
    private int _pollLogCount;
    private int _createLogCount;
    private readonly Random _rng = new();

    private uint _playerId;
    private bool _loginComplete;
    private long _lastCritMs = long.MinValue;
    private int _tick;
    private bool _debugChat;
    private bool _autoDiagDone;
    private PendingExact _pending;

    private const int CritWindowMs = 450;
    private const int ExactWindowMs = 600;  // pair a combat-log amount with its health packet
    private const int HealMinDelta = 8;     // filters small regen ticks
    private const long DeleteKillRecentDamageMs = 8_000; // delete counts as a kill only if we hit it this recently
    private const long ForgetMs = 60_000;   // prune untouched health entries

    // An exact combat-log amount whose health packet hasn't arrived yet (reverse order).
    private struct PendingExact { public bool Has; public int Amount; public bool Crit; public bool Incoming; public long Ts; }

    public override int Initialize()
    {
        _cfg.Load();
        _fx.SetCells(_cells);
        Host.Log("[RynthJuice] Initialized. /juice [on|off|test|heals|playerdmg|scale N|height N|help]. " +
                 "Needs a clean (no-Decal) client; works with the ImGui shell on or off; outdoors + dungeons.");
        return 0;
    }

    public override void OnLoginComplete()
    {
        _playerId = Host.GetPlayerId();
        _loginComplete = true;
        _autoDiagDone = false;
        _health.Clear();
        _fx.Clear();
        if (!Host.HasNav3DTriangle)
            Host.Log("[RynthJuice] WARNING: engine has no Nav3D triangle API — numbers can't draw. Update the engine.");
        Host.Log($"[RynthJuice] Ready (enabled={_cfg.Enabled}).");
    }

    public override void OnLogout()
    {
        _loginComplete = false;
        _health.Clear();
        _killedIds.Clear();
        _tracked.Clear();
        _fx.Clear();
    }

    public override void Shutdown() { _fx.Clear(); _cells.Dispose(); }

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (!_loginComplete || !_cfg.Enabled) return;

        long now = Environment.TickCount64;
        bool isPlayer = targetId == _playerId;

        if (!_health.TryGetValue(targetId, out var st))
        {
            st = new HState();
            _health[targetId] = st;
        }

        uint prevCur = st.LastCurrent;
        float prevRatio = st.LastRatio;
        bool hadPrev = st.HasCurrent;

        // Update tracked state.
        if (maxHealth > 0 && maxHealth > st.KnownMax) st.KnownMax = maxHealth;
        st.LastRatio = healthRatio;
        st.LastCurrent = currentHealth;
        st.HasCurrent = true;
        st.LastSeenMs = now;

        // Cache the mob's current position every update — the live health readout and
        // the kill effect both need a spot, and the position cache can be empty by the
        // time the object is deleted.
        if (!isPlayer && Host.TryGetObjectPosition(targetId, out uint upCell, out float upE, out float upN, out float upU))
        { st.Cell = upCell; st.E = upE; st.N = upN; st.U = upU; }

        // Kill detection by HEALTH RATIO → 0. Fires at the killing blow (no
        // OnDeleteObject corpse-removal delay) and works for ALL mobs — ratio is the
        // reliable signal; absolute currentHealth reads 0 for unappraised mobs.
        // _killedIds dedups (fires once); OnDeleteObject stays as a fallback.
        if (!isPlayer && healthRatio < 0.001f)
        {
            if (_killedIds.Add(targetId))
            {
                int killAmt = (maxHealth > 0) ? (int)Math.Round(prevRatio * maxHealth) : 0;
                bool kcrit = (now - _lastCritMs) <= CritWindowMs;
                if (_pending.Has && (now - _pending.Ts) <= ExactWindowMs && !_pending.Incoming)
                {
                    killAmt = _pending.Amount;
                    kcrit = kcrit || _pending.Crit;
                    _pending.Has = false;
                }

                // Verification scaffolding — chat only in debug mode, or every
                // kill spams the chat window once this goes live.
                if (_debugChat) Reply("[RynthJuice] *** KILL ***");

                // Anchor: live position → last cached → player (so it ALWAYS fires).
                uint kc; float ke, kn, ku;
                bool gotPos = Host.TryGetObjectPosition(targetId, out kc, out ke, out kn, out ku);
                if (!gotPos && (st.Cell >> 16) != 0) { kc = st.Cell; ke = st.E; kn = st.N; ku = st.U; gotPos = true; }
                if (!gotPos) gotPos = Host.TryGetPlayerPose(out kc, out ke, out kn, out ku, out _, out _, out _, out _);

                if (gotPos)
                {
                    if (killAmt >= _cfg.MinDamage) _fx.SpawnDamage(kc, ke, kn, ku, killAmt, kcrit);
                    _fx.SpawnKillBurst(kc, ke, kn, ku, kcrit, now);
                    Host.Log($"[RynthJuice] KILL tgt=0x{targetId:X8} amt={killAmt} cell=0x{kc:X8} fx={_fx.Count}");
                }
            }
            _health.Remove(targetId);
            return;
        }

        if (!hadPrev) return; // first non-death packet — establish baseline only

        // Resolve a damage/heal amount, preferring absolute HP when the server
        // gave us real numbers, falling back to ratio × best-known max.
        int delta;
        bool isDamage;
        if (maxHealth > 0 && (currentHealth > 0 || prevCur > 0))
        {
            long ad = (long)prevCur - currentHealth;
            isDamage = ad > 0;
            delta = (int)Math.Abs(ad);
        }
        else
        {
            float rd = prevRatio - healthRatio;
            float baseMax = st.KnownMax > 0 ? st.KnownMax : 100f;
            isDamage = rd > 0;
            delta = (int)MathF.Round(Math.Abs(rd) * baseMax);
        }
        if (delta <= 0) return;

        // Capture anchor position (mob via object cache, player via pose).
        uint cell; float e, n, u;
        if (isPlayer)
        {
            if (!Host.TryGetPlayerPose(out cell, out e, out n, out u, out _, out _, out _, out _))
                return;
        }
        else if (!Host.TryGetObjectPosition(targetId, out cell, out e, out n, out u))
        {
            if (_debugChat) Host.Log($"[RynthJuice] dmg tgt=0x{targetId:X8} delta={delta} POS-FAIL (no anchor — skipped)");
            return;
        }
        st.Cell = cell; st.E = e; st.N = n; st.U = u;

        if (isDamage)
        {
            if (delta < _cfg.MinDamage) return;
            st.LastDmgMs = now;
            int amount = delta;
            bool crit = (now - _lastCritMs) <= CritWindowMs;
            // Prefer the exact combat-log value if its line already arrived (reverse order).
            if (_pending.Has && (now - _pending.Ts) <= ExactWindowMs && _pending.Incoming == isPlayer)
            {
                amount = _pending.Amount;
                crit = crit || _pending.Crit;
                _pending.Has = false;
            }
            if (isPlayer)
            {
                if (_cfg.ShowPlayerDamage) _fx.SpawnPlayerDamage(cell, e, n, u, amount, crit);
            }
            else
            {
                _fx.SpawnDamage(cell, e, n, u, amount, crit);
            }
            if (_debugChat) Host.Log($"[RynthJuice] dmg tgt=0x{targetId:X8} amt={amount} crit={crit} isPlayer={isPlayer} cell=0x{cell:X8} indoor={(cell & 0xFFFF) >= 0x100} fx={_fx.Count}");
        }
        else if (_cfg.ShowHeals && delta >= HealMinDelta)
        {
            _fx.SpawnHeal(cell, e, n, u, delta);
        }
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (!_loginComplete || !_cfg.Enabled || string.IsNullOrEmpty(text)) return;
        if (_debugChat) Host.Log($"[RynthJuice] chat[{chatType}]: {text}");

        long now = Environment.TickCount64;
        bool isCrit = text.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0;

        // Exact damage from the combat log: "... for N points of <type> damage".
        // The health packet anchors it to the right object; this overwrites the
        // estimate with the true value. Requires "damage" so heal/mana/stamina
        // "points of" lines are ignored.
        if (TryParseDamage(text, out int amount, out bool incoming))
        {
            bool crit = isCrit || (now - _lastCritMs) <= CritWindowMs;
            if (isCrit) _lastCritMs = now;
            var kind = incoming ? JuiceEffects.Kind.PlayerDamage : JuiceEffects.Kind.MobDamage;
            // Correct an already-spawned number, else stash for the health packet.
            if (!_fx.ApplyExactToRecent(now, ExactWindowMs, kind, amount, crit))
                _pending = new PendingExact { Has = true, Amount = amount, Crit = crit, Incoming = incoming, Ts = now };
            return;
        }

        // A standalone crit line (no parsable amount): mark window + retro-upgrade.
        if (isCrit)
        {
            _lastCritMs = now;
            _fx.UpgradeRecentToCrit(now, CritWindowMs);
        }
    }

    // Parses AC combat-log damage lines, e.g.:
    //   "You slash the Drudge for 47 points of slashing damage!"      (outgoing)
    //   "The Drudge Slinker hits you for 12 points of bludgeoning damage!" (incoming)
    // Extracts the integer before "points" and the direction. Returns false for
    // non-damage "points of" lines (heals/mana/stamina lack "damage").
    private static bool TryParseDamage(string text, out int amount, out bool incoming)
    {
        amount = 0; incoming = false;
        int pts = text.IndexOf("points of", StringComparison.OrdinalIgnoreCase);
        if (pts < 0) return false;
        if (text.IndexOf("damage", pts, StringComparison.OrdinalIgnoreCase) < 0) return false;

        int i = pts - 1;
        while (i >= 0 && text[i] == ' ') i--;
        long val = 0, mult = 1; bool any = false;
        while (i >= 0 && (char.IsDigit(text[i]) || text[i] == ','))
        {
            if (text[i] != ',') { val += (text[i] - '0') * mult; mult *= 10; any = true; }
            i--;
        }
        if (!any || val <= 0 || val > 1_000_000) return false;
        amount = (int)val;

        // Defender (incoming) lines target "you for"; attacker lines start "You ".
        incoming = text.IndexOf("you for", StringComparison.OrdinalIgnoreCase) >= 0;
        return true;
    }

    // Exact per-hit damage from the structured AC packet (AttackerNotification /
    // DefenderNotification), surfaced by the engine. This is the real damage value
    // even for one-shots/overkill. It carries no object id (the packet identifies
    // the target by name), so correlate it to the health packet — which has the id
    // + position — by recency, reusing the same _pending mechanism.
    public override void OnCombatDamage(uint damage, uint damageType, bool crit, bool isAttacker)
    {
        if (!_loginComplete || !_cfg.Enabled || damage == 0) return;
        long now = Environment.TickCount64;
        if (crit) _lastCritMs = now;
        var kind = isAttacker ? JuiceEffects.Kind.MobDamage : JuiceEffects.Kind.PlayerDamage;
        // Overwrite a number that already spawned from the health packet, else stash
        // the exact value for the imminent health update / kill to pick up.
        if (!_fx.ApplyExactToRecent(now, ExactWindowMs, kind, (int)damage, crit))
            _pending = new PendingExact { Has = true, Amount = (int)damage, Crit = crit, Incoming = !isAttacker, Ts = now };
        if (_debugChat) Host.Log($"[RynthJuice] COMBATDMG dmg={damage} type={damageType} crit={crit} atk={isAttacker}");
    }

    // Track dynamic objects (mobs/items) so we can poll their health each tick —
    // the streaming-health hook is dead on this client, so polling is how we read
    // monster HP. ObjectIsAttackable (in the poll) filters out non-creatures.
    public override void OnCreateObject(uint objectId)
    {
        // No login/enabled gate: the engine REPLAYS creates for pre-existing objects
        // at plugin init (possibly before OnLoginComplete), and mobs already in the
        // room must be tracked. Polling itself is gated by login/enabled in OnTick.
        if ((objectId & 0x80000000u) == 0) return;     // dynamic objects only
        if (_tracked.Count >= 800 || !_tracked.Add(objectId)) return;
        if (_createLogCount < 8) { _createLogCount++; Host.Log($"[RynthJuice] create 0x{objectId:X8} tracked={_tracked.Count}"); }
    }

    public override void OnDeleteObject(uint objectId)
    {
        _tracked.Remove(objectId);
        // Capture state BEFORE removing it — LastDmgMs is the recent-damage
        // gate below; the old order destroyed it first.
        bool hadState = _health.TryGetValue(objectId, out HState? st);
        _health.Remove(objectId);
        if (!_loginComplete || !_cfg.Enabled) return;

        // An appraised mob already got its effect from the health=0 path — don't double.
        if (_killedIds.Remove(objectId)) return;
        if (_killedIds.Count > 1024) _killedIds.Clear();

        // AC sends deletes for range-out / teleport / despawn too — only treat
        // a delete as a DEATH if we damaged the creature recently. Without this
        // gate, walking out of a spawn field or portaling fired a burst per
        // nearby mob (anchored at the PLAYER via the old pose fallback, since
        // the position cache was already empty).
        long now = Environment.TickCount64;
        if (!hadState || st!.LastDmgMs == 0 || now - st.LastDmgMs > DeleteKillRecentDamageMs)
            return;

        if (!Host.ObjectIsAttackable(objectId)) return;

        // Anchor at the mob's live or last-cached position only — never the
        // player. A delete with no known position gives a wrong-place burst.
        uint kc; float ke, kn, ku;
        if (!Host.TryGetObjectPosition(objectId, out kc, out ke, out kn, out ku))
        {
            if ((st.Cell >> 16) == 0) return;
            kc = st.Cell; ke = st.E; kn = st.N; ku = st.U;
        }
        if (_debugChat) Reply("[RynthJuice] *** KILL ***");
        _fx.SpawnKillBurst(kc, ke, kn, ku, crit: false, now);
    }

    public override void OnTick()
    {
        if (!_loginComplete) return;
        _tick++;

        // Auto-diagnostic: ~150 ticks after login, log the full pipeline state once
        // (no command / on-screen output needed). Fires even when disabled so the
        // log shows enabled=False if that's the cause.
        if (!_autoDiagDone && _tick >= 150) { _autoDiagDone = true; RunDiag(); }

        if (!_cfg.Enabled) return;

        long now = Environment.TickCount64;
        _fx.Render(Host, _cfg, now);
        if ((_tick & 1) == 0) PollMobHealth(now); // poll mob vitals every other tick

        if ((_tick & 0xFF) == 0) PruneHealth(now); // ~every 256 ticks
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/juice", StringComparison.OrdinalIgnoreCase))
            return;

        eat = 1; // consume — never send to the server as chat

        string rest = text.Length > 6 ? text.Substring(6).Trim() : "";
        string[] parts = rest.Length == 0 ? Array.Empty<string>() : rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        switch (cmd)
        {
            case "":
            case "toggle":
                _cfg.Enabled = !_cfg.Enabled;
                if (!_cfg.Enabled) _fx.Clear();
                _cfg.Save();
                Reply($"[RynthJuice] {(_cfg.Enabled ? "ON" : "OFF")}.");
                break;
            case "on":  _cfg.Enabled = true;  _cfg.Save(); Reply("[RynthJuice] ON."); break;
            case "off": _cfg.Enabled = false; _fx.Clear(); _cfg.Save(); Reply("[RynthJuice] OFF."); break;
            case "heals":
                _cfg.ShowHeals = !_cfg.ShowHeals; _cfg.Save();
                Host.Log($"[RynthJuice] heal numbers {(_cfg.ShowHeals ? "on" : "off")}.");
                break;
            case "playerdmg":
                _cfg.ShowPlayerDamage = !_cfg.ShowPlayerDamage; _cfg.Save();
                Host.Log($"[RynthJuice] player-damage numbers {(_cfg.ShowPlayerDamage ? "on" : "off")}.");
                break;
            case "scale":
                if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sc))
                { _cfg.Scale = Math.Clamp(sc, 0.3f, 4f); _cfg.Save(); Host.Log($"[RynthJuice] scale = {_cfg.Scale:0.##}."); }
                else Host.Log("[RynthJuice] usage: /juice scale 1.5");
                break;
            case "height":
                if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float hh))
                { _cfg.HeightOffset = Math.Clamp(hh, 0f, 8f); _cfg.Save(); Host.Log($"[RynthJuice] height = {_cfg.HeightOffset:0.##} m."); }
                else Host.Log("[RynthJuice] usage: /juice height 1.8");
                break;
            case "test": SpawnTest(); break;
            case "debug":
                _debugChat = !_debugChat;
                Host.Log($"[RynthJuice] combat-log debug {(_debugChat ? "ON" : "OFF")} (logs chat lines to RynthCore.log so we can verify the damage-text format).");
                break;
            case "help":
            default:
                Host.Log("[RynthJuice] /juice [on|off|toggle|test|heals|playerdmg|scale N|height N|debug|help]");
                break;
        }
    }

    // Command feedback to BOTH the unified log and the in-game chat window.
    private void Reply(string msg)
    {
        Host.Log(msg);
        if (Host.HasWriteToChat) Host.WriteToChat(msg, 1);
    }

    /// <summary>/juice diag — dumps the full render-pipeline state to chat + log so
    /// we can pinpoint why numbers aren't appearing (especially indoors). Run it
    /// outdoors AND inside a dungeon and compare.</summary>
    private void RunDiag()
    {
        bool nav = Host.HasNav3D, tri = Host.HasNav3DTriangle, w2s = Host.HasWorldToScreen;
        bool poseOk = Host.TryGetPlayerPose(out uint cell, out float e, out float n, out float u, out _, out _, out _, out _);
        Host.TryGetViewportSize(out uint vpw, out uint vph);
        bool indoor = poseOk && (cell & 0xFFFF) >= 0x100;

        // Project a point ~2m ABOVE the player (where numbers anchor — not the
        // player's feet, which sit at the camera and never project). Test BOTH the
        // raw cell-local coords and the EnvCell-transformed landblock-relative
        // coords; whichever projects tells us the frame the indoor camera uses.
        float upU = u + 2f;
        float rrx = 0, rry = 0, xsx = 0, xsy = 0;
        bool projRaw = poseOk && w2s && Host.WorldToScreen(e, upU, n, out rrx, out rry);
        float xe = e, xn = n, xu = upU;
        bool resolveOk = poseOk && _cells.ToLandblockRelative(cell, ref xe, ref xn, ref xu);
        bool projXform = resolveOk && w2s && Host.WorldToScreen(xe, xu, xn, out xsx, out xsy);

        Reply($"[RynthJuice] DIAG enabled={_cfg.Enabled} tick={_tick} fx={_fx.Count} nav={nav} tri={tri} w2s={w2s} vp={vpw}x{vph}");
        Reply($"[RynthJuice] DIAG pose={poseOk} cell=0x{cell:X8} indoor={indoor} cellsReady={_cells.Ready} resolve={resolveOk}");
        Reply($"[RynthJuice] DIAG raw=({e:F1},{n:F1},{u:F1}) projRaw={projRaw}@({rrx:F0},{rry:F0}) | xform=({xe:F1},{xn:F1},{xu:F1}) projXform={projXform}@({xsx:F0},{xsy:F0})");
        Reply($"[RynthJuice] DIAG render: {_fx.LastRenderStatus} | lastDrawn={_fx.LastDrawnEffects} lastTris={_fx.LastTriangles} writeToChat={Host.HasWriteToChat}");
        if (indoor && !_cells.Ready) Reply($"[RynthJuice] DIAG cell.dat status: {_cells.Status}");
    }

    // Polls each tracked mob's vitals directly (TryGetTargetVitals) — the streaming
    // health hook doesn't install on this client, so polling is how we read monster
    // HP. Drives both the live health % and instant (no-delay) death detection.
    private void PollMobHealth(long now)
    {
        if (_tracked.Count == 0)
        {
            if ((_tick & 0x7F) == 0) Host.Log("[RynthJuice] pollcycle tracked=0 (OnCreateObject delivered no ids)");
            return;
        }
        _mobHpScratch.Clear();
        _pollScratch.Clear();
        _pollScratch.AddRange(_tracked); // snapshot so we can remove during the walk

        int budget = 80, atk = 0, vit = 0;
        foreach (uint id in _pollScratch)
        {
            if (budget-- <= 0) break;
            if (id == _playerId) { _tracked.Remove(id); continue; }
            if (!Host.ObjectIsAttackable(id)) continue; // KEEP tracked — classification lags several ticks
            atk++;

            if (!Host.TryGetTargetVitals(id, out uint h, out uint mx, out _, out _, out _, out _) || (h == 0 && mx == 0))
                continue; // can't read this tick
            vit++;

            if (_pollLogCount < 12)
            {
                _pollLogCount++;
                Host.Log($"[RynthJuice] poll 0x{id:X8} h={h} mx={mx}");
            }

            if (!Host.TryGetObjectPosition(id, out uint cell, out float e, out float n, out float u))
                continue;

            // Death by poll: HP hit 0 — instant, no OnDeleteObject corpse-removal delay.
            if (mx > 0 && h == 0)
            {
                if (_killedIds.Add(id))
                {
                    if (_debugChat) Reply("[RynthJuice] *** KILL ***");
                    _fx.SpawnKillBurst(cell, e, n, u, crit: false, now);
                    Host.Log($"[RynthJuice] KILL(poll) tgt=0x{id:X8} cell=0x{cell:X8}");
                }
                _tracked.Remove(id);
                continue;
            }

            float ratio = mx > 0 ? Math.Clamp((float)h / mx, 0f, 1f) : 1f;
            _mobHpScratch.Add(new JuiceEffects.MobHp(cell, e, n, u, ratio));
        }

        if ((_tick & 0x3F) == 0)
            Host.Log($"[RynthJuice] pollcycle tracked={_tracked.Count} attackable={atk} vitals={vit} bars={_mobHpScratch.Count}");
        if (_mobHpScratch.Count > 0) _fx.RenderMobHealth(Host, _cfg, _mobHpScratch);
    }

    private void PruneHealth(long now)
    {
        // Allocation-light prune of stale targets.
        List<uint>? dead = null;
        foreach (var kv in _health)
            if (now - kv.Value.LastSeenMs > ForgetMs)
                (dead ??= new List<uint>()).Add(kv.Key);
        if (dead != null)
            foreach (uint id in dead) _health.Remove(id);
    }

    /// <summary>/juice test — spawn a spread of demo numbers + a burst at the
    /// player so the render path can be verified without combat.</summary>
    private void SpawnTest()
    {
        if (!Host.TryGetPlayerPose(out uint cell, out float e, out float n, out float u, out _, out _, out _, out _))
        {
            Host.Log("[RynthJuice] test: player pose unavailable (are you in the world, outdoors?).");
            return;
        }
        if ((cell >> 16) == 0)
        {
            Host.Log("[RynthJuice] test: not in the world yet.");
            return;
        }
        long now = Environment.TickCount64;
        _lastCritMs = long.MinValue; // don't accidentally crit the demo numbers
        _fx.SpawnDamage(cell, e + 2f, n + 2f, u, 18, crit: false);
        _fx.SpawnDamage(cell, e - 2f, n + 1f, u, 73, crit: false);
        _fx.SpawnDamage(cell, e + 1f, n - 2f, u, 142, crit: false);
        _fx.SpawnDamage(cell, e - 1f, n - 1f, u, 891, crit: true);
        if (_cfg.ShowHeals) _fx.SpawnHeal(cell, e + 3f, n, u, 64);
        _fx.SpawnKillBurst(cell, e, n + 3f, u, crit: true, now);
        Host.Log("[RynthJuice] test numbers spawned around you.");
    }
}
