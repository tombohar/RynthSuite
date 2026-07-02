using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// UtilityBelt /ub command compatibility — thin shim that forwards to the
/// matching /ra handlers. Partial class of RynthAiPlugin.
/// </summary>
public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// Handle a /ub command. Returns true if recognized and handled.
    /// </summary>
    internal bool HandleUbCommand(string fullCommand)
    {
        string[] parts = fullCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        string cmd = parts[1].ToLower();

        // /ub jump[swzxc] [heading] [holdtime] — see Jumper.cs
        if (cmd.StartsWith("jump", StringComparison.Ordinal))
        {
            HandleJumpCommand(cmd, parts);
            return true;
        }

        // Most remaining /ub verbs (use / usep / uselp / useip / face / give /
        // givep / combatstate / cast …) share semantics with the natively
        // implemented /mt verbs, which talk to host primitives (UseObject /
        // TurnToHeading / settings) — NOT to Mag-Tools or Decal. Translate the
        // prefix and reuse that handler so the command works with no external
        // plugin loaded. Verbs /mt doesn't know (mexec, ig, prepclick, …) fall
        // through to HandleMtCommand returning false → "Unrecognized /ub".
        int sp = fullCommand.IndexOf(' ');

        // Verbs /mt doesn't implement but /ra does — route to the /ra dispatcher
        // (VTank-meta migration). "/ub follow X" → "/ra follow X"; myquests → /ra
        // quest-flag refresh.
        if (sp > 0 && cmd is "follow" or "ig" or "mexec" or "myquests")
        {
            HandleRaCommand("/ra" + fullCommand.Substring(sp));
            return true;
        }

        // /ub clearbugged is RynthAi's clearbusy (force-reset busy state) under a
        // different name.
        if (cmd == "clearbugged")
        {
            HandleRaCommand("/ra clearbusy");
            return true;
        }

        if (sp > 0)
        {
            string translated = "/mt" + fullCommand.Substring(sp);   // "/ub use X" → "/mt use X"
            if (HandleMtCommand(translated))
                return true;
        }

        return false;
    }
}
