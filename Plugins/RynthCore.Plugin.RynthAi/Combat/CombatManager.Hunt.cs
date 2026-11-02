using System.Collections.Generic;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Outdoor hunting's monsters (OutdoorHunt, 2026-10-23): the ones combat would fight, by the same
/// filters as <see cref="ScanNearbyTargets"/> (the name list, the blacklist, ghosts, environment skips,
/// just killed, attackable, alive, Priority 0 ignored), out to <c>maxYd</c> instead of MonsterRange and
/// with no line of sight needed: the hunt walks round whatever is in the way. Ones in a building come
/// with their cell (since 2026-10-25 the hunt walks in through a door when it knows the building).
/// </summary>
public partial class CombatManager
{
    internal void HuntCandidates(double maxYd, List<OutdoorHunt.Candidate> into)
    {
        into.Clear();
        int playerId = (int)_playerId;
        if (playerId == 0) return;
        if (!_host.TryGetObjectPosition((uint)playerId, out uint meCell, out float meX, out float meY, out float meZ)) return;
        int forced = ForcedTargetId;
        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.Id == playerId || (int)wo.ObjectClass != (int)AcObjectClass.Monster) continue;
            if (IsSpellProjectileName(wo.Name)) continue;
            double dist = _worldFilter.DistanceFrom(meCell, meX, meY, meZ, wo.Id, out bool hasPos, out _, out _);
            if (!hasPos || dist > maxYd) continue;
            bool isForced = wo.Id != 0 && wo.Id == forced;
            if (!isForced && IsUserBlacklistedName(wo.Name)) continue;
            if (_blacklistManager.IsBlacklisted(wo.Id)) continue;
            if (Ghosts.IsGhost(wo.Id)) continue;
            if (IsEnvironmentSkipped(wo.Id)) continue;
            if (_recentlyKilled.ContainsKey(wo.Id)) continue;
            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id)) continue;
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f) continue;
            int priority = isForced ? int.MaxValue : PriorityOf(wo.Id);
            if (priority <= 0) continue;
            if (!_host.TryGetObjectPosition((uint)wo.Id, out uint cell, out float lx, out float ly, out float lz)) continue;
            // In a building's rooms (cell 0x0100 up): the hunt walks there through a door of the building
            // (BuildingMap), or leaves it alone when it doesn't know the building.
            var pos = new OutdoorHunt.Vec(((cell >> 24) & 0xFF) * 192.0 + lx, ((cell >> 16) & 0xFF) * 192.0 + ly, lz);
            into.Add(new OutdoorHunt.Candidate(wo.Id, wo.Name, pos, dist, priority, cell));
        }
        into.Sort((a, b) => a.Distance.CompareTo(b.Distance));
    }
}
