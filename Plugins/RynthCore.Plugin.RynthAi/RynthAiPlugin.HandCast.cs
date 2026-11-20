using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// ============================================================================
//  The player casting by hand (2026-11-18, R-20261118-214609-a9a8, R-20261118-205826-9c70).
//
//  A cast from AC's own spell bar or a spell hotkey prints "Casting <spell>" in the chat (type 26,
//  AC's own client status lines: "Using the Corpse of ...", "You're too busy!", "In Portal Space -
//  Please Wait..."). RynthAi's casts go through the engine (FreeHandsAndCastSpell / CastSpell with
//  no UI) and never print it: in both reports and in the local logs, hundreds of RynthAi casts, no
//  such line, while the player's clicks each printed one and drew "You're too busy!" 35-50 ms later.
//  Each line goes to ActionPacer.NoteHandCast: the player has the character for a moment (see the
//  ActionPacer section "The player casting by hand").
// ============================================================================

public sealed partial class RynthAiPlugin
{
    /// <summary>AC's chat type for its own client status lines ("Casting ...", "You're too busy!").</summary>
    internal const int AcStatusChatType = 26;

    /// <summary>
    /// True for AC's own "Casting <paramref name="spell"/>" line (a cast from the spell bar or a hotkey).
    /// Not RynthAi's "[RynthAi] Casting: ..." lines, not another plugin's "Casting X..." notice, not speech.
    /// </summary>
    internal static bool TryParseHandCast(string? text, int chatType, out string spell)
    {
        spell = "";
        if (chatType != AcStatusChatType || string.IsNullOrEmpty(text)) return false;
        const string Prefix = "Casting ";
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string rest = text.Substring(Prefix.Length).Trim();
        if (rest.Length == 0 || rest.Length > 80) return false;
        if (rest.EndsWith("...", StringComparison.Ordinal) || rest.IndexOf(':') >= 0 || rest.IndexOf('"') >= 0) return false;
        if (!char.IsLetter(rest[0])) return false;
        spell = rest;
        return true;
    }

    /// <summary>A chat line: AC's "Casting ..." marks the player's own cast by hand (ActionPacer.NoteHandCast).</summary>
    private void NoteHandCastChat(string text, int chatType)
    {
        if (TryParseHandCast(text, chatType, out string spell))
            ActionPacer.NoteHandCast(Host, spell);
    }
}
