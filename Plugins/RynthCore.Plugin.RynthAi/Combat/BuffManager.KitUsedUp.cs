// ============================================================================
//  RynthSuite - Combat/BuffManager.KitUsedUp.cs
//  A kit its own result line says is used up is not picked again.
//
//  A kit's last use ends "... Your Treated Healing Kit is used up." (ACE Healer.DoHealing), and the
//  server removes the kit. A use of an item the player no longer has is answered with a bare UseDone
//  and no chat, so no heal lands: a used-up kit picked again is a heal lost.
//
//  Kit picks read the client's live pack walk (ConsumableLookup.Resolve, BestPackHealingKit,
//  BestPackStaminaKit: WorldObjectCache.GetDirectInventory), which drops a removed kit, but a walk is
//  reused for up to a second (DirectInventoryCooldownMs) and the next kit goes out KitLandGraceMs
//  (700 ms) after the line, inside that reuse. An engine without GetContainerContents has no walk at
//  all: its inventory view is the object cache, which can keep a removed kit for good.
//
//  So: the line names the kit in flight -> every kit pick skips that kit. With the live walk the skip
//  lasts UsedUpKitSkipMs, long enough for a fresh walk to have dropped it; without the walk it lasts
//  for the session (the server deleted the object, and its id never comes back). Only a line naming
//  the kit in flight counts: a late line from a kit whose wait ended can land while the next kit is
//  out, and must not mark that one. Kit use itself is unchanged.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    /// <summary>How long a kit its result line called used up is skipped while the live pack walk runs
    /// (the walk's 1 s reuse, with margin). Without the walk the skip does not end.</summary>
    internal const double UsedUpKitSkipMs = 3000;

    // The kit in flight (its id and name), from the use until the next one.
    private int _kitInFlightId;
    private string _kitInFlightName = "";
    // Kits called used up by their result line, and until when kit picks skip each.
    private readonly Dictionary<int, DateTime> _usedUpKitUntil = new();

    /// <summary>Kits skipped because their result line said they are used up (tests, status).</summary>
    internal int KitsUsedUp { get; private set; }

    /// <summary>Records which kit went out (AttemptHealthKitUse, AttemptStaminaKitUse).</summary>
    private void NoteKitInFlight(WorldObject kit)
    {
        _kitInFlightId = kit.Id;
        _kitInFlightName = kit.Name ?? "";
    }

    /// <summary>True while <paramref name="id"/> is a kit its result line called used up (see the header).</summary>
    internal bool KitUsedUp(int id)
    {
        if (id == 0 || !_usedUpKitUntil.TryGetValue(id, out DateTime until)) return false;
        if (until > Now) return true;
        _usedUpKitUntil.Remove(id);
        return false;
    }

    /// <summary>A kit result line arrived (OnChatWindowText): an "is used up" line naming the kit in flight marks it.</summary>
    private void NoteKitResultLine(string text, string lower)
    {
        if (!lower.Contains(" is used up", StringComparison.Ordinal) || _kitInFlightId == 0) return;
        if (_kitInFlightName.Length > 0 && lower.Contains(_kitInFlightName.ToLowerInvariant(), StringComparison.Ordinal))
        {
            bool walk = _host.HasGetContainerContents;
            _usedUpKitUntil[_kitInFlightId] = walk ? Now.AddMilliseconds(UsedUpKitSkipMs) : DateTime.MaxValue;
            KitsUsedUp++;
            _host.Log($"[RynthAi] Vitals: kit 0x{(uint)_kitInFlightId:X8} '{_kitInFlightName}' is used up (its result line): " +
                      (walk ? $"kit picks skip it for {UsedUpKitSkipMs / 1000:0} s, until the pack walk has dropped it."
                            : "kit picks skip it from now on (no pack walk on this engine)."));
        }
        else
            _host.Log($"[RynthAi] Vitals: an 'is used up' line that doesn't name the kit in flight " +
                      $"(0x{(uint)_kitInFlightId:X8} '{_kitInFlightName}') ignored: \"{text}\"");
    }
}
