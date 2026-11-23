// ============================================================================
//  RynthAi - RynthAiPlugin.LootPerDay.cs
//  "Keep # per day" (2026-11-22, RynthCore.Loot.LootPerDay): a Keep # rule with
//  a window (24 h by default) takes at most its count of matching items picked
//  up in any such window, counted by what this character picked up, not by
//  what the pack holds (treasure maps get used or sold). An item over the count
//  is left like any item no rule wants.
//
//  Flow: the classifier approves an item while the window's pickups plus the
//  items approved and not landed yet are under the count (_pendingPerDay);
//  the send moves it to _perDayInFlight; the landing (the pickup confirmed, a
//  late landing, a ground pickup) records it in the character's
//  LootPerDayStore (Loot/LootPerDayStore.cs), saved at once, so the count
//  survives reloads and relogs.
//
//  Shown: the Loot Editor's rule view (option, help line, today's count, a
//  reset button) and /ra loot today [clear <rule>|clear all].
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The time pickups are stamped with (UTC). Host tests move it.</summary>
    internal static Func<DateTime> PerDayClock = static () => DateTime.UtcNow;

    // Approved this corpse (or ground scan), not sent yet: counted while fresh.
    private readonly Dictionary<int, (string Key, int Units, long At)> _pendingPerDay = new();
    // Sent: counted until it lands (recorded) or the late-landing watch gives up on it.
    private readonly Dictionary<int, (string Key, int Units, long Until)> _perDayInFlight = new();
    private const long PerDayPendingMs = 120_000;
    private const long PerDayInFlightMs = 120_000;

    private LootPerDayStore? _lootPerDay;
    private string _lootPerDayFolder = string.Empty;

    /// <summary>
    /// The store of the character logged in (SettingsProfiles\&lt;server&gt;\&lt;character&gt;\loot_per_day.json),
    /// loaded on first use and again when the character changes. Before login: an in-memory one.
    /// </summary>
    internal LootPerDayStore PerDayStore()
    {
        string folder = _dashboard?.CharFolder ?? string.Empty;
        if (_lootPerDay != null && string.Equals(folder, _lootPerDayFolder, StringComparison.OrdinalIgnoreCase))
            return _lootPerDay;
        _lootPerDay ??= new LootPerDayStore();
        _lootPerDayFolder = folder;
        _lootPerDay.Load(folder.Length == 0 ? string.Empty : Path.Combine(folder, LootPerDayStore.FileName), PerDayClock());
        if (_lootPerDay.Problem.Length > 0)
            Host.Log($"[RynthAi] Keep # per day: could not read the pickup counts ({_lootPerDay.Problem}); starting from none.");
        return _lootPerDay;
    }

    /// <summary>
    /// Keep # per day: true while fewer than <paramref name="cap"/> units were picked up for
    /// <paramref name="key"/> in the last <paramref name="hours"/>, counting items approved or on
    /// their way that haven't landed yet. At the count the item is left (and said, once per item).
    /// </summary>
    private bool UnderDailyCap(string key, string ruleLabel, WorldObject item, int cap, int hours, bool preview = false)
    {
        long nowMs = CorpseNowMs;
        if (_pendingPerDay.TryGetValue(item.Id, out var mine) && nowMs - mine.At < PerDayPendingMs) return true;
        if (_perDayInFlight.ContainsKey(item.Id)) return true;

        DateTime now = PerDayClock();
        LootPerDayStore store = PerDayStore();
        int have = store.Count(key, hours, now) + NotLandedYet(key, item.Id, nowMs);
        if (preview) return LootKeepCap.UnderCap(have, cap);
        string window = LootPerDay.WindowText(hours);
        if (!LootKeepCap.UnderCap(have, cap))
        {
            _corpseCappedIds.Add(item.Id);
            TimeSpan? next = store.NextFree(key, hours, cap, now);
            ChatLine($"[RynthAi] Keep # per day '{ruleLabel}': {have}/{cap} picked up in the last {window}"
                     + (next is TimeSpan t && t > TimeSpan.Zero ? $" (the next in {LootPerDayStore.Duration(t)})" : string.Empty)
                     + $" — leaving {item.Name}.");
            return false;
        }
        int stack = LootKeepCap.StackOf(item);
        _pendingPerDay[item.Id] = (key, stack, nowMs);
        ChatLine($"[RynthAi] Keep # per day '{ruleLabel}': {have}/{cap} picked up in the last {window} — looting {item.Name}{(stack > 1 ? " x" + stack : "")}.");
        return true;
    }

    /// <summary>Units for <paramref name="key"/> approved (still fresh) or on their way, other than <paramref name="exceptId"/>.</summary>
    private int NotLandedYet(string key, int exceptId, long nowMs)
    {
        int n = 0;
        foreach (var kv in _perDayInFlight)
            if (kv.Key != exceptId && string.Equals(kv.Value.Key, key, StringComparison.OrdinalIgnoreCase) && nowMs < kv.Value.Until)
                n += kv.Value.Units;
        foreach (var kv in _pendingPerDay)
            if (kv.Key != exceptId && !_perDayInFlight.ContainsKey(kv.Key)
                && string.Equals(kv.Value.Key, key, StringComparison.OrdinalIgnoreCase) && nowMs - kv.Value.At < PerDayPendingMs)
                n += kv.Value.Units;
        return n;
    }

    /// <summary>The pickup of <paramref name="itemId"/> was sent: an approved Keep # per day item is now on its way.</summary>
    private void NotePerDaySent(int itemId)
    {
        if (!_pendingPerDay.TryGetValue(itemId, out var p)) return;
        _perDayInFlight[itemId] = (p.Key, p.Units, CorpseNowMs + PerDayInFlightMs);
    }

    /// <summary>
    /// <paramref name="itemId"/> left the corpse or the ground into the pack: if a Keep # per day
    /// rule took it, it counts now (once).
    /// </summary>
    private void NotePerDayPickedUp(int itemId)
    {
        string key;
        int units;
        if (_perDayInFlight.Remove(itemId, out var f)) { key = f.Key; units = f.Units; }
        else if (_pendingPerDay.TryGetValue(itemId, out var p)) { key = p.Key; units = p.Units; }
        else return;
        _pendingPerDay.Remove(itemId);
        LootPerDayStore store = PerDayStore();
        store.Record(key, units, PerDayClock());
        if (store.Problem.Length > 0)
            Host.Log($"[RynthAi] Keep # per day: the pickup of '{key}' is counted but could not be saved ({store.Problem}).");
    }

    /// <summary>Drops sends whose landing never came (the late-landing watch is over). Each corpse tick.</summary>
    private void PrunePerDay()
    {
        if (_perDayInFlight.Count == 0 && _pendingPerDay.Count == 0) return;
        long nowMs = CorpseNowMs;
        List<int>? gone = null;
        foreach (var kv in _perDayInFlight)
            if (nowMs >= kv.Value.Until) (gone ??= new()).Add(kv.Key);
        if (gone != null) foreach (int id in gone) _perDayInFlight.Remove(id);
        gone = null;
        foreach (var kv in _pendingPerDay)
            if (nowMs - kv.Value.At >= PerDayPendingMs && !_perDayInFlight.ContainsKey(kv.Key)) (gone ??= new()).Add(kv.Key);
        if (gone != null) foreach (int id in gone) _pendingPerDay.Remove(id);
    }

    /// <summary>The Loot Editor's line for a Keep # per day rule (LootEditorBridge).</summary>
    internal string PerDayStatusFor(string key, int hours, int cap) =>
        PerDayStore().Status(key, hours, cap, PerDayClock());

    /// <summary>
    /// The Loot Editor's revision part: the store's changes plus the minute, while any pickup is on
    /// record, so "next frees in" stays current.
    /// </summary>
    internal long PerDayRevision()
    {
        LootPerDayStore s = PerDayStore();
        long minute = 0;
        foreach (var _ in s.Rules()) { minute = (long)(PerDayClock() - DateTime.UnixEpoch).TotalMinutes; break; }
        return s.Revision * 1_000_000_000L + minute;
    }

    /// <summary>The Loot Editor's "Reset count": forgets the rule's pickups. The reply for the editor's message line.</summary>
    internal string PerDayReset(string key)
    {
        bool had = PerDayStore().Clear(key);
        return had ? $"Keep # per day '{key}': pickups forgotten; it counts from 0 again." : $"Keep # per day '{key}': no pickups on record.";
    }

    /// <summary>
    /// /ra loot today [clear &lt;rule&gt;|clear all]: each Keep # per day rule of the loot profile in use,
    /// with what was picked up in its window, and any other rule with pickups on record.
    /// </summary>
    private void HandleLootTodayCommand(string[] parts)
    {
        LootPerDayStore store = PerDayStore();
        DateTime now = PerDayClock();
        if (parts.Length >= 4)
        {
            if (!string.Equals(parts[3], "clear", StringComparison.OrdinalIgnoreCase))
            {
                ReplyFail($"[RynthAi] loot today: '{parts[3]}'? Usage: /ra loot today [clear <rule name>|clear all]");
                return;
            }
            string what = parts.Length > 4 ? string.Join(" ", parts, 4, parts.Length - 4).Trim() : string.Empty;
            if (what.Length == 0)
            {
                ReplyFail("[RynthAi] loot today clear: which rule? Usage: /ra loot today clear <rule name>|all");
                return;
            }
            bool all = string.Equals(what, "all", StringComparison.OrdinalIgnoreCase);
            bool had = store.Clear(all ? null : what);
            if (had) ReplyOk(all ? "[RynthAi] Keep # per day: every rule's pickups forgotten." : $"[RynthAi] Keep # per day '{what}': pickups forgotten; it counts from 0 again.");
            else ReplyFail(all ? "[RynthAi] Keep # per day: no pickups on record." : $"[RynthAi] Keep # per day: no pickups on record for '{what}'. /ra loot today lists them.");
            return;
        }

        var lines = new List<string>();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, int cap, int hours) in PerDayRulesInUse())
        {
            if (!shown.Add(key)) continue;
            lines.Add($"[RynthAi]   '{key}': {store.Status(key, hours, cap, now)}");
        }
        foreach ((string key, IReadOnlyList<LootPerDayStore.Pick> picks) in store.Rules())
        {
            if (shown.Contains(key)) continue;
            int units = 0;
            foreach (var p in picks) if (p.AtUtc > now.AddHours(-LootPerDay.DefaultHours)) units += p.Units;
            lines.Add($"[RynthAi]   '{key}' (not a Keep # per day rule of this profile): {units} picked up in the last 24 h");
        }
        if (lines.Count == 0)
        {
            ReplyOk("[RynthAi] Keep # per day: the loot profile in use has no such rule and no pickups are on record. "
                    + "Turn it on for a Keep # rule in the Loot Editor (rule view: Per day).");
            return;
        }
        ReplyOk($"[RynthAi] Keep # per day ({lines.Count} rule{(lines.Count == 1 ? "" : "s")}), counted for this character:");
        foreach (string l in lines) ChatLine(l);
        if (store.Problem.Length > 0) ChatLine("[RynthAi]   (" + store.Problem + ")");
    }

    /// <summary>The loot profile in use's Keep # per day rules: key, count, window.</summary>
    private List<(string Key, int Cap, int Hours)> PerDayRulesInUse()
    {
        var list = new List<(string, int, int)>();
        try
        {
            string path = (_dashboard?.Settings?.CurrentLootPath ?? string.Empty).Trim().Trim('"');
            if (path.Length == 0 || !File.Exists(path)) return list;
            if (TryLoadNativeLootProfile(out LootProfile native, out _))
            {
                for (int i = 0; i < native.Rules.Count; i++)
                {
                    LootRule r = native.Rules[i];
                    int h = LootPerDay.Clamp(r.PerDayHours);
                    if (r.Action == LootAction.KeepUpTo && h > 0) list.Add((LootPerDay.Key(r.Name, i), r.KeepCount, h));
                }
            }
            else if (TryLoadLootProfile(string.Empty, out VTankLootProfile utl, out _))
            {
                for (int i = 0; i < utl.Rules.Count; i++)
                {
                    VTankLootRule r = utl.Rules[i];
                    int h = LootPerDay.Clamp(r.PerDayHours);
                    if (r.Action == VTankLootAction.KeepUpTo && h > 0) list.Add((LootPerDay.Key(r.Name, i), r.KeepCount ?? 0, h));
                }
            }
        }
        catch { }
        return list;
    }
}
