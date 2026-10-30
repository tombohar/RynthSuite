using System;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;
using System.Threading;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// /ra swaptest [spell] (2026-10-22): the wand swap and one cast of a self spell, timed, by the
/// same EnsureMagicMode a heal or a buff goes through. With no spell named, the first known of a
/// few spells every caster school has (Lifestone Recall, Portal Recall, the level 1 item auras).
/// A fizzle is as good as a cast here: what it measures is the swap and the stance before it.
/// </summary>
public partial class BuffManager
{
    private string? _swapTestRequest;            // set on AC's thread by the command, taken on the pump
    private int _swapTestSpellId;
    private string _swapTestName = "";
    private DateTime _swapTestAt = DateTime.MinValue;
    private const double SwapTestGiveUpSec = 20;

    private static readonly string[] SwapTestDefaults =
    {
        "Lifestone Recall", "Portal Recall", "Aura of Blood Drinker Self I", "Aura of Swift Killer Self I",
        "Aura of Heart Seeker Self I", "Aura of Defender Self I", "Aura of Hermetic Link Self I",
    };

    /// <summary>The /ra swaptest command (any thread).</summary>
    internal void RequestSwapTest(string spell) => Interlocked.Exchange(ref _swapTestRequest, spell ?? "");

    /// <summary>Behind the same gates as the vitals step. True while the test holds the tick.</summary>
    private bool TickSwapTest()
    {
        string? req = Interlocked.Exchange(ref _swapTestRequest, null);
        if (req != null) StartSwapTest(req.Trim());
        if (_swapTestSpellId == 0) return false;
        if ((Now - _swapTestAt).TotalSeconds > SwapTestGiveUpSec)
        {
            _host.WriteToChat($"[RynthAi] Swap test: no Magic stance in {SwapTestGiveUpSec:0} s ({LastWandSwapTrail}).", 2);
            _host.Log($"[SwapTest] gave up after {SwapTestGiveUpSec:0} s");
            _swapTestSpellId = 0;
            return false;
        }
        if (!EnsureMagicMode()) return true;   // a swap step or the stance is on its way

        int id = _swapTestSpellId;
        _swapTestSpellId = 0;
        double ms = (Now - _swapTestAt).TotalMilliseconds;
        bool sent = _host.CastSpellFor((uint)_host.GetPlayerId(), id, "Buff", _swapTestName);
        _lastCastAttempt = Now;
        string swap = LastWandSwapTrail.Length > 0 ? $"; the swap took {LastWandSwapMs:0} ms ({LastWandSwapTrail})" : "; no swap needed";
        _host.Log($"[SwapTest] '{_swapTestName}' {(sent ? "cast" : "refused by the client")} {ms:0} ms after the request{swap}");
        _host.WriteToChat($"[RynthAi] Swap test: {_swapTestName} {(sent ? "cast" : "refused")} {ms / 1000:0.00} s after the request{swap}.", 1);
        LastWandSwapTrail = "";
        return true;
    }

    private void StartSwapTest(string spell)
    {
        if (spell.StartsWith("burst", StringComparison.OrdinalIgnoreCase))
        {
            SwapBurst = !spell.EndsWith("off", StringComparison.OrdinalIgnoreCase);
            _host.WriteToChat($"[RynthAi] Swap test: the burst is {(SwapBurst ? "on" : "off")} until the next reload (with Fast Weapon Swap it is on by itself).", 1);
            _host.Log($"[SwapTest] burst {(SwapBurst ? "on" : "off")}");
            return;
        }
        int id = 0;
        string name = "";
        foreach (string candidate in spell.Length > 0 ? new[] { spell } : SwapTestDefaults)
        {
            var e = SpellCatalog.GetByName(candidate);
            if (e == null || !_spellManager.IsKnownSpellId(e.Id)) continue;
            id = e.Id;
            name = e.Name;
            break;
        }
        if (id == 0)
        {
            _host.WriteToChat(spell.Length > 0
                ? $"[RynthAi] Swap test: '{spell}' isn't a spell this character knows."
                : "[RynthAi] Swap test: none of the default test spells is known; name one: /ra swaptest <spell>.", 2);
            return;
        }
        _swapTestSpellId = id;
        _swapTestName = name;
        _swapTestAt = Now;
        LastWandSwapTrail = "";
        _host.Log($"[SwapTest] start: '{name}' (mode {CurrentCombatMode})");
        _host.WriteToChat($"[RynthAi] Swap test: {name}; the result follows.", 1);   // /ra swaptest's answer
    }
}
