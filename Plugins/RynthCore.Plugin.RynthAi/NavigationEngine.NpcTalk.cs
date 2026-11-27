// ============================================================================
//  RynthSuite - NavigationEngine.NpcTalk.cs
//  VTank "NPC" nav points (type 7): walk to the point, use the NPC, then go on (2026-11-23).
//
//  #rynth-meta-and-expression-issues (a meta author, 11-23): "NPC 'talk' nav nodes do nothing
//  (breaks all 7 metas)". NavRouteParser read type 7 and its NPC name, but the point-type dispatch
//  handled only Pause, Chat, Recall and Portal, so an NPC point was walked like a plain waypoint
//  and passed without the NPC being used. Their navs do every task pickup and turn-in this way
//  (56 NPC points); with no talk there are no "X tells you" lines, no task flags, and the meta
//  loops round the NPC area or "finishes" without the tasks.
//
//  Now an NPC point is walked to like any waypoint; on arriving (or passing it) the bot stops and
//  uses the NPC by name, the nearest one with that name, as a Portal point uses its portal
//  (FirePortalNpcUse: AC walks the character over and uses it), then waits for the use to finish
//  and the NPC to answer (NpcTalkMinMs, while the client is busy up to NpcTalkMaxMs) before it goes
//  on. An NPC that can't be found in NpcFindTimeoutMs is said once in chat and the route goes on.
// ============================================================================

using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

internal sealed partial class NavigationEngine
{
    /// <summary>After the use, the least wait before going on: the walk over and the NPC's lines.</summary>
    internal const long NpcTalkMinMs = 3_000;

    /// <summary>After the use, the most wait (a long walk over, a busy client).</summary>
    internal const long NpcTalkMaxMs = 20_000;

    /// <summary>How long the NPC is looked for before the route goes on without it.</summary>
    internal const long NpcFindTimeoutMs = 15_000;

    private int _npcTalkIdx = -1;               // the NPC point being handled (-1: none)
    private NavRouteParser? _npcTalkRoute;      // on this route
    private long _npcTalkStart, _npcTalkTriedAt, _npcTalkFiredAt;
    private bool _npcTalkFired;

    /// <summary>
    /// Arrived at (or passed) waypoint <paramref name="idx"/>: true when it is an NPC point, which now
    /// stops and uses its NPC (HandleNpcTalk) instead of advancing.
    /// </summary>
    private bool BeginNpcTalk(NavPoint pt, NavRouteParser route, int idx)
    {
        if (pt.Type != NavPointType.Npc || string.IsNullOrWhiteSpace(pt.TargetName)) return false;
        StopMovement();
        _npcTalkIdx = idx;
        _npcTalkRoute = route;
        _npcTalkStart = Now;
        _npcTalkTriedAt = 0;
        _npcTalkFiredAt = 0;
        _npcTalkFired = false;
        _settings.NavStatusLine = $"Nav: NPC '{pt.TargetName.Trim()}'...";
        _host.Log($"Nav: at NPC point [{idx}] '{pt.TargetName.Trim()}' - using the NPC before going on");
        return true;
    }

    private void HandleNpcTalk(NavPoint pt, NavRouteParser route)
    {
        long now = Now;
        string name = pt.TargetName.Trim();
        if (!_npcTalkFired)
        {
            ClearTurnMotions();
            if (now - _npcTalkStart < (long)SettleDelayMs) return;   // stand still first, as before a portal
            if (now - _npcTalkStart > NpcFindTimeoutMs)
            {
                string msg = $"Nav: the NPC '{name}' at waypoint [{_npcTalkIdx}] wasn't found in {NpcFindTimeoutMs / 1000} s - going on without it.";
                _host.Log(msg);
                _host.WriteToChat(msg, 1);
                EndNpcTalk(route);
                return;
            }
            if (_npcTalkTriedAt == 0 || now - _npcTalkTriedAt >= (long)PortalNpcRetryMs)
            {
                _npcTalkTriedAt = now;
                if (FirePortalNpcUse(pt, "NPC"))
                {
                    _npcTalkFired = true;
                    _npcTalkFiredAt = now;
                }
            }
            return;
        }

        // Used: AC walks the character over and uses the NPC, which answers. No motions are cleared
        // now, or the walk over would be cancelled (as for a portal).
        long since = now - _npcTalkFiredAt;
        int busy = _host.HasGetBusyState ? _host.GetBusyState() : 0;
        if ((since >= NpcTalkMinMs && busy <= 0) || since >= NpcTalkMaxMs)
        {
            _host.Log($"Nav: NPC '{name}' used {since} ms ago{(busy > 0 ? " (the client still reads busy)" : "")} - going on");
            EndNpcTalk(route);
        }
    }

    private void EndNpcTalk(NavRouteParser route)
    {
        _npcTalkIdx = -1;
        _npcTalkRoute = null;
        _closestDist = double.MaxValue;
        Advance(route);
    }
}
