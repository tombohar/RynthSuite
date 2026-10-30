// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiYieldApi.cs
//  The "RynthAi.Yield" interface (2026-10-29): a plugin acting for the player
//  (RynthDefective's Shortcuts bar: a key press, a click) asks RynthAi to start
//  nothing new for a moment, so the player's action goes next instead of
//  waiting behind RynthAi's stream of uses and casts. After a death or a login
//  RynthAi rebuffs and re-wields, every action of its moves the client's busy
//  count, and the bar's press waited until it gave up ("not used, the
//  character is busy"). What RynthAi already sent still finishes; nothing is
//  cancelled, and its moving and turning are not held.
//
//  Its own interface: RynthAi.Activity stays read only.
//
//  Rules: as RynthAi.Script (Shared/RynthAiScriptApi.cs): append-only, pump
//  thread only (OnTick and the events it drains), resolve the table again
//  every tick (a RynthAi reload frees it).
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthAiYieldApiV1
{
    public const string InterfaceName = "RynthAi.Yield";
    public const uint InterfaceVersion = 1;

    /// <summary>
    /// The longest one ask holds RynthAi, in ms. The asker asks again every tick while the player's
    /// action waits or is in flight, so one that stops asking (unloaded, stuck) frees RynthAi within this.
    /// </summary>
    public const uint MaxHoldMs = 3000;

    public uint Version;      // InterfaceVersion of the table RynthAi filled
    public uint StructSize;   // sizeof(RynthAiYieldApiV1) as RynthAi built it

    /// <summary>
    /// RynthAi starts no use, cast, stance change, attack or item move for <c>ms</c> from now (at most
    /// <see cref="MaxHoldMs"/>); the newest ask sets the end. 1 = held. 0 = RynthAi can't: not logged in,
    /// or its action pacing is off (then it doesn't know what it has in flight either).
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, int> HoldForPlayer;

    /// <summary>Ends the hold now (the player's action went and was answered, or was dropped). 1 = ok.</summary>
    public delegate* unmanaged[Cdecl]<int> ReleaseHold;
}
