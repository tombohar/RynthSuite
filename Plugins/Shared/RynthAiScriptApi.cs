// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiScriptApi.cs
//  The "RynthAi.Script" interface: what RynthAi offers a scripting plugin
//  (RynthLua) through the host's GetPluginInterface (API v68). Compiled into
//  both plugins (linked source), so the layout can't drift between them.
//
//  Rules:
//  - Append-only: new functions go at the end, and Version goes up. A caller
//    checks StructSize before using a field past the ones it knows.
//  - Strings are UTF-8, NUL-terminated. A string handed back through a byte**
//    belongs to RynthAi and stays valid only until the next call into this
//    table: copy it at once.
//  - Call only from the plugin pump thread (OnTick and the events it drains),
//    never from OnChatBarEnter (AC's thread) or Render.
//  - Every function returns safely (no exception crosses the boundary).
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthAiScriptApiV1
{
    public const string InterfaceName = "RynthAi.Script";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table RynthAi filled
    public uint StructSize;   // sizeof(RynthAiScriptApiV1) as RynthAi built it

    /// <summary>Evaluates a meta expression. 1 = ok (result in *result), 0 = error (message in *result).</summary>
    public delegate* unmanaged[Cdecl]<byte*, byte**, int> Evaluate;

    /// <summary>A setting from the meta settings map (getsetting names). 1 = found.</summary>
    public delegate* unmanaged[Cdecl]<byte*, byte**, int> GetSetting;

    /// <summary>Sets a setting and saves the profile. 1 = ok, 0 = no such setting.</summary>
    public delegate* unmanaged[Cdecl]<byte*, byte*, int> SetSetting;

    /// <summary>Every setting name, newline separated. 1 = ok.</summary>
    public delegate* unmanaged[Cdecl]<byte**, int> GetSettingNames;

    /// <summary>1 while the macro is running.</summary>
    public delegate* unmanaged[Cdecl]<int> IsMacroRunning;

    /// <summary>Starts (1) or stops (0) the macro.</summary>
    public delegate* unmanaged[Cdecl]<int, void> SetMacroRunning;

    /// <summary>Walk to a spot: a one-point route, macro started. Coordinates are signed (S/W negative). 1 = ok.</summary>
    public delegate* unmanaged[Cdecl]<double, double, int> NavGoTo;

    /// <summary>Stop navigation (and the macro when it's running).</summary>
    public delegate* unmanaged[Cdecl]<void> NavStop;

    /// <summary>Nearest object with a position whose name contains the text. flags: 1 = portals and NPCs only.
    /// 1 = found (*id, *distance in metres).</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, uint*, double*, int> FindNearest;

    /// <summary>Metres from the player to an object, -1 when unknown.</summary>
    public delegate* unmanaged[Cdecl]<uint, double> DistanceTo;

    /// <summary>Line of sight to an object: 1 clear, 0 blocked (1 when raycasting isn't ready).</summary>
    public delegate* unmanaged[Cdecl]<uint, int> IsPathClear;

    /// <summary>A chat line as if typed: /ra, /ub, /mt, /vt run in RynthAi now; anything else goes to AC's parser. 1 = sent.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> SubmitCommand;
}
