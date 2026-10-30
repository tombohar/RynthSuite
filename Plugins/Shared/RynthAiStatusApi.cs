// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiStatusApi.cs
//  The "RynthAi.Status" interface: a compact, read-only summary of what the bot
//  is doing, for plugins that share it (RynthNet's status to other clients).
//  Its own interface rather than a new field of RynthAi.Script, so RynthLua's
//  RynthAi.Script v1 request keeps working against any RynthAi build.
//
//  Rules: as RynthAi.Script (Shared/RynthAiScriptApi.cs): append-only, UTF-8,
//  a returned string is valid until the next call, pump thread only, resolve
//  the table again every tick.
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthAiStatusApiV1
{
    public const string InterfaceName = "RynthAi.Status";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table RynthAi filled
    public uint StructSize;   // sizeof(RynthAiStatusApiV1) as RynthAi built it

    /// <summary>
    /// The bot's state as one JSON object:
    /// {macro,action,state,combat,buff,nav,loot,meta,navFile,metaFile,lootFile}
    /// (action = what it is doing now: Default/Combat/Looting/Navigating/Buffing...; state = meta state).
    /// 1 = ok, 0 = not ready (not logged in yet).
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte**, int> GetStatusJson;
}
