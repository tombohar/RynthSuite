// ============================================================================
//  RynthSuite - Plugins/Shared/RynthNavRoutesApi.cs
//  The "RynthNav.Routes" interface (2026-10-30): RynthNav's route maker offered
//  to other plugins through the host's GetPluginInterface (API v68). Plan trips,
//  make RynthAi nav routes that follow real walkable paths (a trip, a hunting
//  loop, a path through points), describe a saved one, search the Atlas, walk
//  somewhere and stop, and ask where the player is. Everything is JSON: a
//  caller sends a request, gets a job id at once, and polls the job until it
//  is done or failed. The /rnav makeroute, makeloop, makepath and routeinfo
//  commands are the same jobs (RynthNav's RynthNavPlugin.RouteMaker.cs).
//  Compiled into RynthNav and into every plugin that calls it (linked source),
//  so the layout can't drift.
//
//  Rules (same as RynthAi.Script and RynthNav.Path):
//  - Append-only: new functions go at the end, and Version goes up. A caller
//    checks StructSize before using a field past the ones it knows.
//  - Strings are UTF-8, NUL-terminated. A string handed back through a byte**
//    belongs to RynthNav and stays valid only until the next call into this
//    table: copy it at once.
//  - Call only from the plugin pump thread (OnTick and the events it drains).
//    Resolve the table again every tick: never keep the pointer across ticks
//    (RynthNav can be reloaded or unloaded between them; a reload forgets the
//    jobs, and Poll then answers "unknown").
//  - Every function returns safely (no exception crosses the boundary).
//  - The work itself runs on a RynthNav worker, never on the caller's thread;
//    Request returns at once. Poll once a tick or slower.
//
//  ── Calls ───────────────────────────────────────────────────────────────────
//  Request(json, &reply) -> job id > 0, reply = the job as Poll shows it
//                           ({"id":..,"op":..,"state":"queued","detail":"queued","result":null});
//                        -> 0, reply = {"error": why, "usage": the op's JSON form} for a refused request
//                           (not JSON, over 16384 characters, an unknown op, a missing or wrong field,
//                           out of range, a bad name, or 32 jobs already queued or working).
//  Poll(id, &json)       -> 1, json = {"id":n,"op":"makeloop","state":"queued|working|done|failed",
//                                      "detail":"one line saying where it is or why it failed",
//                                      "result": the op's result below (null until there is one;
//                                                {"error": why, ...} for most failed jobs)}
//                        -> 0, json = {"id":n,"state":"unknown","error":...}: not a job RynthNav keeps
//                           (only the last 32 are kept; a reload forgets them).
//  Job ids are positive and go up; they start from the clock, so a reload doesn't reuse old ones.
//  Jobs that work out walks (plan, makeroute, makeloop, makepath, info) run one at a time, in the
//  order asked; atlas, where, stop and go run on RynthNav's next tick.
//
//  ── Requests ────────────────────────────────────────────────────────────────
//  Every request is a JSON object with "op". Unknown fields are ignored; a known field of the wrong
//  type is refused. Coordinates are map coordinates: {"ns": 42.1, "ew": 33.6}, north and east
//  positive, at most 102 either way. A place is a name from RynthNav's Atlas ("Holtburg") or
//  coordinates as text ("42.1N, 33.6E"), exactly what /rnav go takes. A route "name" is a nav file
//  name: no path, no characters a file name can't hold, at most 100 characters, ".nav" optional; it
//  goes in RynthAi's NavProfiles folder, and a file already there is only replaced with
//  "overwrite": true.
//
//  plan       {"op":"plan", "to":"Holtburg" | {"ns":42.1,"ew":33.6}, "portals":true|false, "show":false}
//             Plans a trip from where the player stands, as /rnav route (portals and recalls when
//             "portals" is true; left out = the player's "portals" switch), and works out its walks.
//             "show": true also shows it in RynthNav's panel and on the ground.
//             result {"to":{"name","ns","ew"}, "portals":true,
//                     "steps":[{"kind":"walk|portal|recall|town","name":"...","ns":..,"ew":..,"yards":412,
//                               "land":{"ns","ew"} (portal, recall, town: where it lands)}, ...],
//                     "yards":3312, "teleports":2, "minutes":6.4, "notes":["step 2: ..."]}
//             kind town = through the Town Network to its exit "name". minutes is rough: running at
//             10 yd/s, 36 s for each teleport, and each recall's cast time.
//
//  makeroute  {"op":"makeroute", "name":"To Holtburg", "to":"Holtburg" | {"ns","ew"}, "portals":true|false,
//              "overwrite":false, "show":false}
//             plan, then save it as a RynthAi nav route (type once), as /rnav route then /rnav saveroute.
//             result {"name","file":"C:\\...\\NavProfiles\\To Holtburg.nav","type":"once","points":47,
//                     "yards":3290,"dropped":0,"droppedPoints":[],"to":{"name","ns","ew"},"waypoints":44,
//                     "portals":1,"recalls":1,"teleports":2,"stoppedShort":null | "step 3: why",
//                     "notes":[...]}
//
//  makeloop   {"op":"makeloop", "name":"Holtburg loop", "at":"here" | {"ns","ew"} | "42.1N, 33.6E",
//              "radius":80, "points":8, "overwrite":false}
//             A hunting loop, saved as a circular nav route: "points" walkable spots round the centre
//             ("at"; left out = here) within "radius" yards (20 to 250; points 3 to 16, left out =
//             about one per 45 yd of the circle), out of buildings, 20 yd or more from any portal, off
//             walls, each reachable from the centre; walked in order round the centre with no two legs
//             crossing, each leg a real path that never enters a portal's circle, pushed off walls by
//             RynthNav's wall clearance, round buildings where it can. Spots that can't be used are
//             dropped and listed.
//             result {"name","file","type":"circular","points":38,"yards":612,"dropped":1,
//                     "droppedPoints":[{"ns","ew","why":"not reachable from the centre"}],
//                     "centre":{"ns","ew"},"radius":80,"loopPoints":7,"asked":8,"notes":[...]}
//             points = the nav file's points (waypoints along the legs); loopPoints = the chosen spots.
//
//  makepath   {"op":"makepath", "name":"Ridge walk", "type":"loop|line|once",
//              "points":[{"ns":42.1,"ew":33.6}, "42.3N, 33.9E", ...], "overwrite":false}
//             A nav route through the points in that order (2 to 32; type loop = circular, line =
//             linear, once; left out = once), each leg a real path (RynthNav's portal and wall avoid
//             settings), a loop also back from the last to the first. Fails, naming the leg, when one
//             can't be walked; fails naming the point when one has no ground within 12 yd or stands in
//             a portal's circle.
//             result {"name","file","type":"once","points":31,"yards":450,"dropped":0,"droppedPoints":[],
//                     "given":3,"legs":2,"notes":[]}
//
//  info       {"op":"info", "name":"Holtburg loop"}
//             What a nav file in NavProfiles is (/rnav routeinfo).
//             result {"name","file","type":"circular|linear|once|follow","points":38,
//                     "counts":{"waypoints","portals","recalls","chats","pauses","other"},
//                     "yards":612 (walking; a circular route's lap; not across teleports),
//                     "box":{"minNs","maxNs","minEw","maxEw"},"centre":{"ns","ew"},
//                     "start":{"ns","ew","yards" (from the player, null when not on the map)},
//                     "nearest":{"index","ns","ew","yards"} | null (the waypoint nearest the player),
//                     "landblocks":["A9B4",...],"dungeon":false,"dungeonLandblocks":[],
//                     "portals":["Portal to ..."],"recalls":["Lifestone Recall"],"chats":["@lifestone"],
//                     "warning": RynthAi's reader's warning, when it gave one}
//
//  atlas      {"op":"atlas", "search":"holt", "max":10}
//             result {"search","count":3,"places":[{"name":"Holtburg","kind":"Town","onMap":true,
//                     "ns":42.1,"ew":33.6,"yards":1234 (from the player; null when not on the map)},
//                     {"name","kind":"Dungeon","onMap":false,"ns":null,"ew":null,"yards":null,"where":"..."}]}
//             max 1 to 50 (left out = 10).
//
//  go         {"op":"go", "to":"Holtburg" | {"ns","ew"}}
//             Walks there as /rnav go does (a dungeon walks into its entrance portal). The job stays
//             "working" while it walks (detail: "walking to Holtburg, 1.2k yd left") and ends:
//             done with outcome "arrived" (or "stopped" when stopped by stop, /rnav stop, the panel,
//             or another goto taking over), failed with the reason (no progress, a portal on the way,
//             no route, logged out...).
//             result {"started":true,"to":{"name","ns","ew"},"enter":"<dungeon>"|null,"steps":3,
//                     "teleports":2,"outcome":"walking|arrived|stopped|failed","reason":"..."}
//             A walk that couldn't start: failed, {"started":false,"outcome":"failed","reason":...}.
//
//  stop       {"op":"stop"}  stops a walk (any, as /rnav stop). result {"wasWalking":true}
//
//  where      {"op":"where"}
//             result {"onMap":true,"ns":42.1,"ew":33.6 (null in a dungeon),"landblock":"A9B4",
//                     "cell":"A9B40031","indoors":false,"dungeon":false,"walking":false,
//                     "nearest":{"name","kind","ns","ew","yards"} | null (the nearest Atlas place)}
//
//  Ops that need the player (plan, makeroute, go, where, makeloop "here") fail with "no player
//  position yet" before login; makeloop "at", makepath, info and atlas don't need one.
//
//  ── Example ─────────────────────────────────────────────────────────────────
//    var t = (RynthNavRoutesApiV1*)Host.GetPluginInterface("RynthNav", "RynthNav.Routes", 1);
//    byte* reply;
//    int id = t->Request(utf8("{\"op\":\"makeloop\",\"name\":\"Hunt 1\",\"radius\":80}"), &reply);
//    ... a later tick (resolve t again) ...
//    byte* json; t->Poll(id, &json);   // {"id":..,"op":"makeloop","state":"done","detail":"saved Hunt 1.nav: 8 points, 612 yd a lap","result":{...}}
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthNavRoutesApiV1
{
    public const string PluginName = "RynthNav";
    public const string InterfaceName = "RynthNav.Routes";
    public const uint InterfaceVersion = 1;

    /// <summary>How many jobs RynthNav keeps (finished ones are dropped oldest first).</summary>
    public const int MaxJobs = 32;

    public uint Version;      // InterfaceVersion of the table RynthNav filled
    public uint StructSize;   // sizeof(RynthNavRoutesApiV1) as RynthNav built it

    /// <summary>
    /// Queues a request (UTF-8 JSON, see the header). Returns its job id (&gt; 0) with *reply = the job's
    /// JSON, or 0 with *reply = {"error", "usage"} when the request is refused.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, byte**, int> Request;

    /// <summary>
    /// A job's state: 1 with *json = {id, op, state, detail, result}; 0 with *json = {id, state: "unknown",
    /// error} for an id RynthNav doesn't keep.
    /// </summary>
    public delegate* unmanaged[Cdecl]<int, byte**, int> Poll;
}
