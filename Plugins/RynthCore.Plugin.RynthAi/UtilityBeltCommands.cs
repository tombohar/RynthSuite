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

        return false;
    }
}
