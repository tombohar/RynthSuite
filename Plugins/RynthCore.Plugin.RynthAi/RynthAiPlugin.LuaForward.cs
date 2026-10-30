namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Lua moved to its own plugin, RynthLua (2026-09-29). "/ra lua ..." (typed, or a meta's chat
/// action) is forwarded to it as the plugin command ("lua", "run X"), so metas that start
/// scripts keep working. RynthLua's own command is /lua.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private void ForwardLuaCommand(string[] parts)
    {
        string rest = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2) : "";
        if (Host.HasSendPluginCommand && Host.SendPluginCommand("RynthLua", "lua", rest))
        {
            Reply.Answered();   // RynthLua answers its own /lua verbs
            return;
        }
        ReplyFail("[RynthAi] Lua scripting is now the RynthLua plugin. Add RynthCore.Plugin.RynthLua.dll in the launcher's Plugins tab, then use /lua (or /ra lua).");
    }
}
