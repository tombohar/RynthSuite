using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

// A recall spell wants a wand in hand. Done the way RynthAi equips its wand: a caster already in
// hand is used as it is; otherwise whatever is in the hands (a weapon, a shield) goes into the pack
// first, then a caster from the pack is wielded into the held slot, and only then the magic stance.
// Before, the stance was tried bare-handed for 6 s and then the caster "used" from the pack, which
// the server refused while a weapon was still wielded (10-02 18:45, WeenieError 0x0468).
public sealed partial class RynthNavPlugin
{
    private const int HeldSlot = 0x01000000;                  // where a caster is wielded
    // Wield slots that hold something in your hands: melee, shield, missile, held, two-handed.
    private const uint HandSlots = 0x00100000 | 0x00200000 | 0x00400000 | 0x01000000 | 0x02000000;
    private const long WandStepMs = 1500;                     // between one move or wield and the next
    private const long WandGiveUpMs = 15000;                  // then cast with what's in hand

    private long _wandStartMs, _wandLastActMs;
    private bool _wandReady;

    private void BeginWand() { _wandReady = false; _wandStartMs = NowMs; _wandLastActMs = 0; }

    /// <summary>True once a caster is in hand (or there is none to wield, or it's taking too long).</summary>
    private bool PrepareWand(long now)
    {
        if (_wandReady) return true;
        uint me = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (me == 0 || !Host.HasGetLiveObjectIds) return _wandReady = true;
        if (now - _wandStartMs > WandGiveUpMs)
        {
            Host.Log("[RynthNav] recall: couldn't get a wand in hand in 15 s — casting with what's in hand");
            return _wandReady = true;
        }

        uint inHand = 0, caster = 0, packCaster = 0;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out uint loc)) continue;
            bool isCaster = Host.TryGetItemType(id, out uint type) && (type & ItemTypeCaster) != 0;
            if (wielder == me && (loc & HandSlots) != 0)
            {
                if (isCaster) caster = id; else if (inHand == 0) inHand = id;
            }
            else if (isCaster && packCaster == 0)
            {
                bool mine = container == me
                    || (container != 0 && Host.TryGetObjectOwnershipInfo(container, out uint outer, out _, out _) && outer == me);
                if (mine) packCaster = id;
            }
        }
        if (caster != 0) return _wandReady = true;            // a wand is in hand
        if (packCaster == 0) return _wandReady = true;        // none carried: bare hands it is
        if (now - _wandLastActMs < WandStepMs) return false;
        _wandLastActMs = now;

        if (inHand != 0)
        {
            if (Host.HasMoveItemInternal && Host.MoveItemInternal(inHand, me, 0, 1))
                Host.Log($"[RynthNav] recall: putting 0x{inHand:X8} away to wield a wand");
            else return _wandReady = true;
            return false;
        }
        bool sent = Host.HasWieldItem ? Host.WieldItem(packCaster, HeldSlot) : Host.HasUseObject && Host.UseFor(packCaster, "Recall", "wield a wand for the recall spell", UseKind.Asked);
        Host.Log($"[RynthNav] recall: wielding wand 0x{packCaster:X8}{(sent ? "" : " (not sent)")}");
        if (!sent) return _wandReady = true;
        return false;
    }
}
