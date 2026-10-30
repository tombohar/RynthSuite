// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiActivityApi.cs
//  The "RynthAi.Activity" interface: what the bot is doing right now, as a few
//  bits, for plugins that act on the character's inventory and must stay out
//  of RynthAi's way (RynthSort's pack moves, 2026-10-11).
//  Its own interface rather than a new field of RynthAi.Status, so a plugin
//  built against the old RynthAi.Status v1 layout keeps working against any
//  RynthAi build (the same reason RynthAi.Status is not part of RynthAi.Script).
//
//  Rules: as RynthAi.Script (Shared/RynthAiScriptApi.cs): append-only, pump
//  thread only (OnTick and the events it drains), resolve the table again
//  every tick (a RynthAi reload frees it). Read only: nothing here changes
//  what RynthAi does.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthAiActivityApiV1
{
    public const string InterfaceName = "RynthAi.Activity";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table RynthAi filled
    public uint StructSize;   // sizeof(RynthAiActivityApiV1) as RynthAi built it

    /// <summary>
    /// What RynthAi is doing now (<see cref="RynthAiActivity"/> bits in *flags).
    /// 1 = ok, 0 = not ready (not logged in yet; *flags is 0).
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint*, int> GetActivityFlags;
}

/// <summary>The bits <see cref="RynthAiActivityApiV1.GetActivityFlags"/> reports.</summary>
[Flags]
public enum RynthAiActivity : uint
{
    None = 0,
    /// <summary>The macro is running.</summary>
    MacroRunning = 1u << 0,
    /// <summary>The bot's tick belongs to buffing: buffs, heals, mana or stamina (casts and kits).</summary>
    Buffing = 1u << 1,
    /// <summary>The bot's tick belongs to combat (attacks and casts at a monster).</summary>
    Combat = 1u << 2,
    /// <summary>Looting: the tick belongs to looting, or RynthAi has a corpse open or claimed.</summary>
    Looting = 1u << 3,
    /// <summary>Salvaging: the tick belongs to salvage, or the salvage manager has work queued or under way.</summary>
    Salvaging = 1u << 4,
    /// <summary>The tick belongs to navigation (a route or following).</summary>
    Navigating = 1u << 5,
    /// <summary>
    /// RynthAi is waiting on the server: its busy-count mirror is above zero, or a use, cast or
    /// stance change it sent has no answer yet (Shared/ActionPacer.cs).
    /// </summary>
    ServerAction = 1u << 6,
    /// <summary>Missile ammunition is being made (it moves and combines items).</summary>
    Crafting = 1u << 7,
    /// <summary>The AutoCram setting is on: RynthAi itself moves loose items out of the main pack.</summary>
    AutoCram = 1u << 8,
    /// <summary>
    /// A use, cast or stance change RynthAi sent has no answer from the server yet: its pacer's one
    /// outstanding action (Shared/ActionPacer.cs). Unlike <see cref="ServerAction"/>, not the client's
    /// busy count, which can stay up with nothing in flight (a death, a login, a corpse open).
    /// Reported by a RynthAi that has RynthAi.Yield (2026-10-29), with its pacing on.
    /// </summary>
    PacedAction = 1u << 9,
}
