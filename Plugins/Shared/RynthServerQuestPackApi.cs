// ============================================================================
//  RynthSuite - Plugins/Shared/RynthServerQuestPackApi.cs
//  The "RynthServer.QuestPack" interface: a server plugin's own quest list (its
//  quests, timers and the retail quests it can't show), for a quest reader such
//  as RynthOracle, which merges it into its retail list without knowing the
//  server. Added 2026-10-21, when the server packs left RynthOracle for their
//  server plugins (Plugins/Shared/ServerPack: Resources\quests.<id>.csv).
//  RynthCore says which server is active and which plugin owns it (plugin API
//  v86, GetServerStateJson: "active", "activePlugin"); the reader asks only that
//  plugin for this interface (host GetPluginInterface), so a pack shows only
//  while its server is the active one. No interface = no pack (retail quests).
//  Compiled into the server plugins and their readers (linked source), so the
//  layout can't drift.
//
//  Rules (as the other shared interfaces):
//  - Append-only: new functions go at the end, and Version goes up.
//  - Strings are UTF-8, NUL-terminated. The info JSON is plain ASCII.
//  - Every function returns safely (no exception crosses the boundary).
//  - The table is allocated once per loaded copy of the plugin and never freed;
//    shutdown sets Version to 0, and a reader re-queries when it sees that.
//
//  GetPackInfoJson payload (schema 1; read leniently, fields may be added):
//  {
//    "schema": 1,
//    "server": "conquest",      // the server's RynthCore id (the reader's override file: quests.<server>.csv)
//    "tag": "Conquest",         // what quest lists call the server: the master list's Server column,
//                               //  and the label a reader shows ("546 Conquest quests")
//    "plugin": "RynthConquest", "version": "0.2.0",
//    "rows": 546,               // data rows in the CSV
//    "bytes": 48939,            // the CSV's length in UTF-8 bytes (GetPackCsv's answer)
//    "revision": 1              // goes up when the plugin's pack changes (read the CSV again)
//  }
//
//  GetPackCsv payload: the pack as CSV text (UTF-8), the columns of RynthOracle's
//  quest list: QuestFlag, Server, Quest, Url, Info, Hint, Repeatable, plus the
//  optional MinDelta (seconds), MaxSolves, Source ("set by ...") and NotOnServer
//  (TRUE: the retail quest's flag can't be read on this server; hide it there).
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

/// <summary>Constants of the "RynthServer.QuestPack" interface.</summary>
public static class RynthServerQuestPack
{
    /// <summary>The info JSON's schema.</summary>
    public const int Schema = 1;

    /// <summary>The longest pack a reader takes (bytes); a bigger one is refused, not cut.</summary>
    public const int MaxCsvBytes = 8 * 1024 * 1024;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthServerQuestPackApiV1
{
    public const string InterfaceName = "RynthServer.QuestPack";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table the plugin filled; 0 once it has shut down
    public uint StructSize;   // sizeof(RynthServerQuestPackApiV1) as the plugin built it

    /// <summary>
    /// GetPackInfoJson(buffer, capacity): writes the info JSON (UTF-8, NUL-terminated) into buffer
    /// when it fits, and returns its length in bytes without the NUL. A return value of capacity or
    /// more means nothing was written: call again with a bigger buffer. 0 = no pack (or shutting
    /// down). Any thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> GetPackInfoJson;

    /// <summary>
    /// GetPackCsv(buffer, capacity): the pack CSV, with GetPackInfoJson's buffer rules (the info's
    /// "bytes" says how big a buffer it needs). 0 = no pack. Any thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> GetPackCsv;
}
