// ============================================================================
//  RynthSuite - Plugins/Shared/RynthNetApi.cs
//  The "RynthNet.Net" interface: what RynthNet offers other plugins (RynthLua's
//  future game.Network / UB-style UBNet module, RynthAi's netclients[]) through
//  the host's GetPluginInterface (API v68). Compiled into every plugin that uses
//  it (linked source), so the layout can't drift.
//
//  Rules (same as RynthAi.Script):
//  - Append-only: new functions go at the end and Version goes up. A caller
//    checks StructSize before using a field past the ones it knows.
//  - Strings are UTF-8, NUL-terminated. A string handed back through a byte**
//    belongs to RynthNet and stays valid only until the next call into this
//    table: copy it at once.
//  - Call only from the plugin pump thread (OnTick and the events it drains).
//    Resolve the table again every tick: never keep the pointer across ticks
//    (RynthNet can be reloaded or unloaded between them).
//  - Nothing here calls back into the caller: messages are polled
//    (Subscribe + PollChannel), so no function pointer of another plugin is
//    ever held across an unload.
//  - Every function returns safely (no exception crosses the boundary).
//
//  Targets (kind): 0 = every other client, 1 = clients with any of the tags in
//  target (comma separated), 2 = the client playing the character named target.
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthNetApiV1
{
    public const string PluginName = "RynthNet";
    public const string InterfaceName = "RynthNet.Net";
    public const uint InterfaceVersion = 1;

    public const int TargetAll = 0;
    public const int TargetTags = 1;
    public const int TargetCharacter = 2;

    /// <summary>SendCommand flag: also run the command on this client when the target matches it.</summary>
    public const int FlagIncludeSelf = 1;

    public uint Version;      // InterfaceVersion of the table RynthNet filled
    public uint StructSize;   // sizeof(RynthNetApiV1) as RynthNet built it

    /// <summary>1 while this client is on the local network (listening), else 0.</summary>
    public delegate* unmanaged[Cdecl]<int> IsOnline;

    /// <summary>
    /// The other clients as a JSON array: [{node,pid,char,world,login,tags[],rtt,status:{...}|null}].
    /// status (when the client sent one): hp,hpMax,st,stMax,mp,mpMax,cell,ns,ew,z,heading,portal,busy,vitae,
    /// bot:{macro,action,state,...}|null, age (ms since it arrived). 1 = ok.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte**, int> GetClientsJson;

    /// <summary>This client: {node,pipe,char,world,login,tags[],online}. 1 = ok.</summary>
    public delegate* unmanaged[Cdecl]<byte**, int> GetSelfJson;

    /// <summary>
    /// Runs a chat command (e.g. "/say hi", "/ra follow Bob") on the target clients; delayMs staggers
    /// them (the n-th gets n x delayMs). flags: FlagIncludeSelf. Returns how many clients it went to
    /// (this one included when it ran here), or -1 (bad arguments / RynthNet off).
    /// Args: (int kind, byte* target, byte* command, int delayMs, int flags).
    /// </summary>
    public delegate* unmanaged[Cdecl]<int, byte*, byte*, int, int, int> SendCommand;

    /// <summary>
    /// Sends a message on a channel to the target clients. Clients that subscribed to the channel keep it
    /// for PollChannel. Returns how many clients it went to, or -1.
    /// Args: (byte* channel, byte* message, int kind, byte* target).
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, byte*, int, byte*, int> Publish;

    /// <summary>Starts (1) or stops (0) keeping messages that arrive on a channel. 1 = ok.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> Subscribe;

    /// <summary>
    /// Takes the messages kept for a channel since the last poll, oldest first:
    /// [{from,node,msg,at}] (at = ms tick when it arrived). At most 256 are kept per channel.
    /// 1 = ok ("[]" when none arrived), 0 = not subscribed to that channel.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, byte**, int> PollChannel;

    /// <summary>This character's tags, newline separated. 1 = ok.</summary>
    public delegate* unmanaged[Cdecl]<byte**, int> GetTags;

    /// <summary>Adds (on = 1) or removes (on = 0) a tag on this character and saves it. 1 = changed.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> SetTag;
}
