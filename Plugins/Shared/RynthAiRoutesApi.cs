// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiRoutesApi.cs
//  The "RynthAi.Routes" interface (2026-10-30): another plugin (an AI layer
//  that drives characters, a route panel) has RynthAi make dungeon nav routes
//  and gets JSON answers. The same work as the chat commands, one
//  implementation (RynthAiPlugin.DunRoutes.cs, DungeonRouteMaker.cs):
//    /ra dunroute <name> to exit|far|portal <name>|<x> <y> <z>|room <n> [overwrite]
//    /ra dunpatrol-save <name> [overwrite]
//    /ra dunroute info <name>      /ra dunroute where
//  Routes are written as VTank .nav files in RynthAi's NavProfiles folder,
//  where RynthAi's Nav picker lists them and /ra nav load <name> runs them.
//  RynthAi does not load or start a route it saves. The points are the ones
//  DungeonPathfinder lays for the patrol and dunnav (centred on doorways,
//  straight across rooms). The .nav format (VTank's, unchanged) has no field
//  for the doorway flag, so the doorway points are listed beside the routes
//  in NavProfiles\.rynthai-routes.json (file, SHA-256, dungeon landblock,
//  point count, doorway indexes; DunRouteSidecar.cs). When RynthAi loads an
//  unchanged route-maker file in its dungeon it puts the flags back, so the
//  route walks doorways as dunnav-patrol does; an edited file runs with the
//  ordinary point reach. Other tools ignore the list.
//
//  Rules:
//  - Append-only: new functions go at the end, and Version goes up. A caller
//    checks StructSize before using a field past the ones it knows.
//  - Strings are UTF-8, NUL-terminated. The string handed back through a
//    byte** belongs to RynthAi and stays valid only until the next call into
//    this table: copy it at once.
//  - Call only from the plugin pump thread (OnTick and the events it drains),
//    never from Render. Resolve the table again every tick (a RynthAi reload
//    frees it). Every function returns safely.
//  - Threads: the work is not done off the game thread. DungeonPathfinder
//    keeps static per-landblock caches (the cell graph, the map geometry) and
//    DungeonGeometry a clearance memo, none of them thread-safe, and every
//    caller of them (the dungeon patrol, nav recovery detours) runs on the
//    plugin pump thread. So Request only checks the JSON and queues the job;
//    RynthAi works one job per tick in its own OnTick, on that thread, file
//    write included (a route file is a few KB).
//
//  ── The two calls ──────────────────────────────────────────────────────────
//  Request(json, &answer) -> job id
//    Checks the request and queues it. Returns the job id (1 and up) with
//    answer = {"id":7,"state":"queued"}; or 0, refused at once, with
//    answer = {"error":"<why>"} (bad JSON, a wrong field, RynthAi not logged
//    in, 32 jobs still unfinished).
//  Poll(id, &json) -> 1 known job | 0 no such job
//    json = {"id":7,"op":"dunroute","state":"queued","detail":"...","result":null}
//      state  queued   waiting its turn (one job a RynthAi tick)
//             working  started, waiting on the client (portal space, the
//                      dungeon data loading); detail says what. It gives up
//                      after 30 s and fails.
//             done     result holds the answer (below)
//             failed   detail says why (the same line chat would show);
//                      result is null
//      detail one line, the one the chat command answers with.
//    An id RynthAi doesn't have (never given, dropped, or a RynthAi reload
//    since): 0, with json = {"id":7,"state":"unknown","detail":"..."}.
//    The last 32 jobs are kept: a new one drops the oldest finished one. Ids
//    are never reused while RynthAi stays loaded.
//
//  ── Requests (one JSON object; unknown fields and wrong types are refused) ─
//  {"op":"dunroute","name":"Hive exit","to":"exit"}
//  {"op":"dunroute","name":"To depths","to":{"portal":"Portal to the Hive Depths"},"overwrite":true}
//  {"op":"dunroute","name":"Spot","to":{"x":110.5,"y":-3.25,"z":-24}}
//  {"op":"dunroute","name":"Far end","to":"far"}
//  {"op":"dunroute","name":"Room 12","to":{"room":12}}
//  {"op":"dunpatrol","name":"Hive patrol","overwrite":false}
//  {"op":"info","name":"Hive patrol"}
//  {"op":"where"}
//    op         "dunroute" | "dunpatrol" | "info" | "where" (required)
//    name       the route's name (dunroute, dunpatrol, info): a file name in
//               NavProfiles without ".nav" (".nav" on the end is dropped);
//               no folders, no \ / : * ? " < > |, no "..", not ending in a dot
//               or a space, at most 120 characters, not a name Windows
//               reserves (CON, NUL, COM1...)
//    to         dunroute only (required):
//               "exit"           the exit portal (picked as below)
//               "far"            the reachable spot farthest from the
//                                character on foot
//               {"portal":"n"}   a portal by name: the exact name (any case),
//                                else the names containing it; the nearest
//                                when several match
//               {"x":..,"y":..,"z":..}  a spot in dungeon coordinates: metres
//                                in the landblock's own frame, the numbers
//                                where's x/y/z and /ra navdebug's "local=" show
//               {"room":n}       a room by its number (where's "room", the
//                                rooms the dungeon map joins cells into;
//                                needs the map loaded)
//    overwrite  dunroute, dunpatrol: true replaces a file of that name; without
//               it an existing file is never replaced (the job fails, saying so)
//
//  "exit": a dungeon is left by a portal (or a recall). The client is not told
//  where a portal goes, so the exit is the first that fits of: a portal whose
//  appraised destination (string property 38) is a spot on the landscape
//  ("12.3N, 45.6E"); a portal named like an exit (exit, surface, leave,
//  outside, out, return); the portal nearest where the character came into
//  this dungeon (within 40 m; RynthAi saw it arrive); the only portal known.
//  Nothing fits: the job fails and lists the portals known (then ask for one
//  by name). A one-way drop or a trap that teleports is the server's and
//  can't be seen by the client.
//
//  ── Results (state done) ───────────────────────────────────────────────────
//  Positions: "x","y","z" are landblock-local metres, as in the requests;
//  "cell"/"id"/"landblock" are hex strings ("028902A5", "7028905D", "0289").
//  "yards" are RynthAi's yards (metres in the world). "leftOut" says what the
//  route does not cover or keeps out of; "notes" what a reader should know
//  (the map not loaded yet, portals the client hasn't been sent...).
//
//  dunroute (a Once route: the character's spot first, the target last; a
//  portal target ends with a Portal step that uses the portal):
//  {"file":"C:\\Games\\RynthSuite\\RynthAi\\NavProfiles\\Hive exit.nav","name":"Hive exit",
//   "type":"once","points":14,"waypoints":13,"yards":96.4,"landblock":"0289",
//   "from":{"cell":"028902A5","x":110.0,"y":92.0,"z":-30.0},
//   "to":{"kind":"exit","portal":"Surface Portal","id":"7028905D","cell":"028902BA",
//         "x":110.0,"y":-3.78,"z":-24.0,"why":"named like an exit"},
//   "cells":9,"doorways":4,"doorwayPoints":3,"overwrote":false,
//   "leftOut":["3 lava/acid cells kept out of"],
//   "notes":["2 portals known in this dungeon (the client only knows the ones it has been sent)"]}
//    to.kind is "exit" | "portal" | "position" | "room" | "far"; to.portal,
//    to.id are there for a portal (null otherwise), to.room for a room.
//    points: every point in the file; waypoints: the ones walked to.
//    cells: cells the path goes through; doorways: narrow openings it lines
//    up on; doorwayPoints: the points flagged for them (recorded in
//    .rynthai-routes.json; a problem writing that list is in "notes").
//
//  dunpatrol (the circular patrol /ra dunnav-patrol would walk from here, from
//  the waypoint it would start at; one lap):
//  {"file":"...\\Hive patrol.nav","name":"Hive patrol","type":"circular","points":355,
//   "waypoints":355,"yards":2210.5,"landblock":"0289",
//   "from":{"cell":"028902A5","x":110.0,"y":92.0,"z":-30.0},"startCell":"028902A5",
//   "cells":524,"reachable":480,"covered":402,"doorwayPoints":120,"overwrote":false,
//   "coverage":"coverage whole dungeon, skip dead ends <= 10, lava/acid floors avoided, walls 1.0 m, look into rooms >= 10 m",
//   "leftOut":["44 of 524 cells can't be walked to from here (behind drops or lava/acid, or not joined)",
//              "78 reachable cells left out by the coverage (whole dungeon, dead ends of up to 10 cells; Settings > Dungeon Patrol)",
//              "beyond portal 'Surface Portal': 1 opening(s) closed (a portal fires at a touch)"],
//   "notes":["..."]}
//    cells: the dungeon's cells; reachable: those walkable from here;
//    covered: those the patrol's coverage keeps (Settings > Navigation >
//    Dungeon Patrol, as dunnav-patrol). startCell: the cell the lap is laid
//    from. doorwayPoints: as for dunroute. The walk out of a hazard or a
//    portal's reach that dunnav-patrol puts first (only needed from where
//    the character stands) is not saved.
//    The patrol is refused where a server's owners asked for it to be off
//    (ServerPolicy), exactly as dunnav-patrol is.
//
//  info (a saved route described; any .nav in NavProfiles):
//  {"file":"...","name":"Hive patrol","type":"circular","points":355,"waypoints":355,
//   "steps":{"portal":0,"recall":0,"pause":0,"chat":0,"jump":0,"npc":0,"vendor":0,"checkpoint":0},
//   "yards":2210.5,"landblock":"0289","landblocks":["0289"],"dungeon":true,
//   "cells":201,"startCell":"028902A5","endCell":"028902A5",
//   "forThisDungeon":"yes","pointsHere":355,"notes":[]}
//    landblock: the dungeon its points are in: the character's when they
//    are there, else the dungeon (of the landblocks round the ones its
//    points fall in) whose cells hold at least half of them, else the
//    landblock most points fall in (a landscape route); null with no points.
//    landblocks: the 192 m squares its points' coordinates fall in, most
//    first (a dungeon's cells can lie outside its own square: Tiny Hive
//    0x0289's are in 0x0288's). dungeon: that landblock is a dungeon (null:
//    the dats aren't loaded); cells: distinct cells its points are in (null
//    when not a dungeon); startCell/endCell: the first and last point's.
//    forThisDungeon: "yes" (every point in the dungeon the character is in,
//    as NavigationEngine's area hold judges it), "partly", "no", or
//    "unknown" (the character isn't in a dungeon); pointsHere: how many are.
//
//  where (where the character is; no file):
//  {"inDungeon":true,"landblock":"0289","cell":"028902A5","x":110.0,"y":92.0,"z":-30.0,
//   "ns":-0.1,"ew":-99.9,"cells":524,"reachable":480,"map":true,"rooms":120,"room":12,
//   "explored":0.35,"hazards":3,
//   "arrival":{"cell":"028902A5","x":110.2,"y":91.0,"z":-30.0},
//   "portals":[{"name":"Surface Portal","id":"7028905D","cell":"028902BA","x":110.0,
//               "y":-3.78,"z":-24.0,"yards":96.1,"walkYards":101.0,
//               "exit":"named like an exit","destination":null}]}
//    map: the dungeon map geometry (doorways, floors) is loaded; rooms/room
//    need it (null without). explored: the share of the floor the radar has
//    painted as visited this visit (null when the radar hasn't drawn this
//    dungeon). arrival: where RynthAi saw the character come in (null:
//    RynthAi was loaded after). portals: every portal the client knows here,
//    nearest first; walkYards along the cell path (null: no walkable way);
//    exit: why it would be the exit (null: it wouldn't); destination: its
//    appraised destination text, when the client has it.
//    Outside a dungeon: {"inDungeon":false,"landblock":..,"cell":..,"x","y","z","ns","ew"}.
//
//  ── A caller ───────────────────────────────────────────────────────────────
//    var t = (RynthAiRoutesApiV1*)host.GetPluginInterface("RynthAi",
//              RynthAiRoutesApiV1.InterfaceName, RynthAiRoutesApiV1.InterfaceVersion);
//    if (t == null || t->Version < 1 || t->StructSize < (uint)sizeof(RynthAiRoutesApiV1)) return;
//    byte* answer;
//    uint id = t->Request(jsonUtf8, &answer);      // 0: refused, answer says why
//    ... on later ticks: t->Poll(id, &json) until state is done or failed.
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthAiRoutesApiV1
{
    public const string InterfaceName = "RynthAi.Routes";
    public const uint InterfaceVersion = 1;

    /// <summary>Jobs kept: a new one drops the oldest finished one; with this many unfinished, Request refuses.</summary>
    public const int MaxJobs = 32;

    /// <summary>The longest request accepted, in UTF-8 bytes.</summary>
    public const int MaxRequestBytes = 8192;

    public uint Version;      // InterfaceVersion of the table RynthAi filled
    public uint StructSize;   // sizeof(RynthAiRoutesApiV1) as RynthAi built it

    /// <summary>
    /// Queues a request (UTF-8 JSON, see the header). Returns the job id (1 and up) with *answer =
    /// {"id":N,"state":"queued"}, or 0 when refused at once with *answer = {"error":"..."}.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, byte**, uint> Request;

    /// <summary>
    /// A job's state: 1 with *json = {"id","op","state","detail","result"}; 0 for an id RynthAi doesn't
    /// have, with *json = {"id":N,"state":"unknown","detail":"..."}.
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, byte**, int> Poll;
}
