using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Plugin.RynthAi.CreatureData;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Damage taken by shield and facing (2026-11-08: does a shield actually cut a boss's hits?).
/// Each monster hit line ("X slashes your leg for N points of slashing damage!") is learned in
/// monster_damage.txt as before (T rows) and, split, as TS rows: was a shield in the off hand in a
/// combat stance, and was the attacker in front of us or behind (ACE counts a shield only within
/// 90 degrees of the facing, Creature_Combat.GetShieldMod).
///
///   * Shield: the item in the Shield slot (<see cref="OffhandInSlot"/>, the engine's
///     EquipmentTracker when it has one) is a shield (<see cref="IsShieldItem"/>), and the stance
///     isn't peace. An off-hand item the cache doesn't know: the hit isn't split (no guess).
///   * Facing: the heading from the pose quaternion (compass, <see cref="ShieldFacing"/>) and the
///     attacker's position when its line arrives. The line names it, not its object: every live
///     monster of that name within 80 m is measured, and they must all agree, else unknown.
///   * Log: one [Taken] line per monster every 30 s while hits come (never one per hit).
/// </summary>
public partial class CombatManager
{
    private static readonly TimeSpan TakenSummaryEvery = TimeSpan.FromSeconds(30);

    /// <summary>Hits since the last [Taken] summary, for one monster.</summary>
    private sealed class TakenTally
    {
        public string Name = "";
        public int Hits;
        public double Damage;
        public int ShieldUnread;
        // index = (shield ? 0 : 3) + front 0 / behind 1 / unknown 2
        public readonly int[] N = new int[6];
        public readonly double[] Sum = new double[6];
        public readonly double[] Max = new double[6];
    }

    private readonly Dictionary<uint, TakenTally> _takenTally = new();
    private DateTime _takenTallySince = DateTime.MinValue;

    private static int TallyIndex(bool shield, HitFacing f)
        => (shield ? 0 : 3) + (f == HitFacing.Front ? 0 : f == HitFacing.Behind ? 1 : 2);

    /// <summary>
    /// A shield counts now: one in the off hand, in a combat stance. False with none (or a
    /// dual-wield weapon there, or peace); null when the off-hand item isn't known to the cache.
    /// </summary>
    internal bool? ShieldCountsNow()
    {
        int off = OffhandInSlot();
        if (off == 0) return false;
        var wo = _worldFilter[off];
        if (wo == null) return null;
        if (!IsShieldItem(wo)) return false;
        if (_host.HasGetCurrentCombatMode && _host.GetCurrentCombatMode() == CombatMode.NonCombat) return false;
        return true;
    }

    /// <summary>Where the monster named <paramref name="name"/> stands against our facing now (see the class comment).</summary>
    internal HitFacing AttackerFacing(string name)
    {
        if (string.IsNullOrEmpty(name) || !_host.HasGetPlayerPose) return HitFacing.Unknown;
        if (!_host.TryGetPlayerPose(out uint pc, out float px, out float py, out _, out float qw, out _, out _, out float qz))
            return HitFacing.Unknown;
        double heading = ShieldFacing.CompassFromQuaternion(qw, qz);
        var each = new List<HitFacing>(2);
        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            if (!string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (wo.Id == unchecked((int)_playerId)) continue;
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f) continue;   // the server said dead
            if (!_host.TryGetObjectPosition(unchecked((uint)wo.Id), out uint ac, out float ax, out float ay, out _))
            {
                each.Add(HitFacing.Unknown);
                continue;
            }
            double d = ShieldFacing.Distance2D(pc, px, py, ac, ax, ay);
            if (!double.IsNaN(d) && d > ShieldFacing.MaxAttackerRange) continue;   // too far to be the one
            each.Add(ShieldFacing.Classify(heading, pc, px, py, ac, ax, ay));
        }
        return ShieldFacing.Agree(each);
    }

    /// <summary>A monster's hit on us (LearnFromCombatChat): the split row, and the [Taken] summary's tally.</summary>
    private void LearnTakenSplit(uint wcid, string name, int amount)
    {
        if (_damageStore == null || amount <= 0) return;
        bool? shield = ShieldCountsNow();
        HitFacing facing = AttackerFacing(name);
        if (shield.HasValue) _damageStore.RecordTakenSplit(wcid, name, shield.Value, facing, amount);

        DateTime now = RynthClock.Now;
        if (_takenTally.Count == 0) _takenTallySince = now;
        if (!_takenTally.TryGetValue(wcid, out var t)) _takenTally[wcid] = t = new TakenTally();
        t.Name = name;
        t.Hits++;
        t.Damage += amount;
        if (!shield.HasValue) { t.ShieldUnread++; return; }
        int i = TallyIndex(shield.Value, facing);
        t.N[i]++;
        t.Sum[i] += amount;
        if (amount > t.Max[i]) t.Max[i] = amount;
    }

    /// <summary>Every 30 s (OnHeartbeat): one [Taken] line per monster that hit us since the last one.</summary>
    private void FlushTakenSummary()
    {
        if (_takenTally.Count == 0) return;
        DateTime now = RynthClock.Now;
        if (now - _takenTallySince < TakenSummaryEvery) return;
        foreach (string line in TakenSummaryLines(now)) _host.Log(line);
    }

    /// <summary>The [Taken] lines and a fresh tally (tests call it directly).</summary>
    internal List<string> TakenSummaryLines(DateTime now)
    {
        var lines = new List<string>(_takenTally.Count);
        double secs = Math.Max(0, (now - _takenTallySince).TotalSeconds);
        foreach (var kv in _takenTally) lines.Add(TakenSummaryLine(kv.Value, secs));
        _takenTally.Clear();
        _takenTallySince = now;
        return lines;
    }

    private static string TakenSummaryLine(TakenTally t, double secs)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(160);
        sb.Append("[Taken] ").Append(t.Name).Append(": ").Append(t.Hits.ToString(ic))
          .Append(t.Hits == 1 ? " hit" : " hits").Append(" in ").Append(secs.ToString("0", ic)).Append(" s, ")
          .Append(t.Damage.ToString("0", ic)).Append(" damage");
        string[] cell = { "shield front", "shield behind", "shield facing unknown", "no shield front", "no shield behind", "no shield facing unknown" };
        string sep = " - ";
        for (int i = 0; i < 6; i++)
        {
            if (t.N[i] == 0) continue;
            sb.Append(sep).Append(cell[i]).Append(' ').Append(t.N[i].ToString(ic))
              .Append(" avg ").Append((t.Sum[i] / t.N[i]).ToString("0", ic))
              .Append(" max ").Append(t.Max[i].ToString("0", ic));
            sep = ", ";
        }
        if (t.ShieldUnread > 0)
            sb.Append(sep).Append(t.ShieldUnread.ToString(ic)).Append(" with the off hand unreadable (not split)");
        return sb.ToString();
    }
}
