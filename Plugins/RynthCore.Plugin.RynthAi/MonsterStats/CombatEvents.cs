// ============================================================================
//  RynthAi - MonsterStats/CombatEvents.cs
//  Melee and missile attacks between you and a monster, from the server's own
//  combat game events (plugin API v78 server messages, opcode 0xF7B0), which
//  name the monster. The client turns the same events into chat lines ("X
//  evaded your attack.", "You evaded X!", "You slash X for 12 points of ..."),
//  which are not read: the typed event is exact and can't be imitated.
//
//    0x01B1 AttackerNotification          you hit it (name, type, share, damage, crit, conditions)
//    0x01B2 DefenderNotification          it hit you (name, type, share, damage, location, crit, conditions)
//    0x01B3 EvasionAttackerNotification   it evaded your attack (name)
//    0x01B4 EvasionDefenderNotification   you evaded its attack (name)
//
//  The killing blow sends no 0x01B1: the kill notice comes instead (ACE
//  Player_Combat.cs; MonsterCombatStats.OnKill counts it). Spells send none of
//  these (CombatLines.cs reads theirs). Pure; uses the SDK's bounds-checked reader.
// ============================================================================
using System;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

/// <summary>One melee or missile attack, from whose side.</summary>
internal enum PhysicalEvent
{
    /// <summary>Your attack hit it.</summary>
    YouHit,
    /// <summary>It evaded your attack.</summary>
    ItEvaded,
    /// <summary>Its attack hit you.</summary>
    ItHit,
    /// <summary>You evaded its attack.</summary>
    YouEvaded,
}

internal static class CombatEvents
{
    public const uint AttackerNotification = 0x01B1;
    public const uint DefenderNotification = 0x01B2;
    public const uint EvasionAttackerNotification = 0x01B3;
    public const uint EvasionDefenderNotification = 0x01B4;

    /// <summary>The game events to ask the engine for (RynthAiPlugin.Initialize).</summary>
    public static readonly uint[] GameEvents =
        { AttackerNotification, DefenderNotification, EvasionAttackerNotification, EvasionDefenderNotification };

    public static bool IsCombatEvent(uint eventType) =>
        eventType is AttackerNotification or DefenderNotification or EvasionAttackerNotification or EvasionDefenderNotification;

    /// <summary>
    /// Reads one combat game event's body (the bytes after its event type). True with what
    /// happened, the monster's name and the damage of a hit (0 for an evade); false for another
    /// event or a body too short to hold its fields.
    /// </summary>
    public static bool TryParse(uint eventType, ReadOnlySpan<byte> body, out PhysicalEvent kind, out string name, out int damage)
    {
        kind = default;
        name = string.Empty;
        damage = 0;
        var r = new NetReader(body);
        switch (eventType)
        {
            case AttackerNotification:
            case DefenderNotification:
                if (!r.String16L(out name) || !r.U32(out _) || !r.F64(out _) || !r.U32(out uint dmg)) return false;
                damage = dmg > int.MaxValue ? int.MaxValue : (int)dmg;
                kind = eventType == AttackerNotification ? PhysicalEvent.YouHit : PhysicalEvent.ItHit;
                break;
            case EvasionAttackerNotification:
                if (!r.String16L(out name)) return false;
                kind = PhysicalEvent.ItEvaded;
                break;
            case EvasionDefenderNotification:
                if (!r.String16L(out name)) return false;
                kind = PhysicalEvent.YouEvaded;
                break;
            default:
                return false;
        }
        name = name.Trim();
        return name.Length > 0;
    }
}
