// ============================================================================
//  RynthNet - Characters/CharactersModel.cs
//  The Characters window's logic (moved from RynthCore's UI/Data, 2026-10-21:
//  RynthNet draws the window itself now), pure so Tools/RynthNet.Tests can
//  compile it in: parsing RynthNet's snapshot (RynthPluginGetSnapshotJson),
//  where another character is relative to you (distance, compass point, or the
//  dungeon / map coordinates and "elsewhere"), what it is doing, and the row
//  order. RynthCore keeps a copy for its maps' other-characters layer until the
//  world map moves to its plugin.
//
//  Snapshot shape (RynthNet 0.1.x, Docs/RYNTHNET.md in RynthSuite):
//    {version, online, offline?, node, char, world, login, tags, acceptRemote,
//     clients:[{node, pid, char, world, login, tags, rtt,
//               status:{hp,hpMax,st,stMax,mp,mpMax, cell,x,y,z, ns,ew, heading,
//                       portal, busy, vitae, id, cmode, bot:{macro, action, ...}} | null,
//               age}]}
//  Every field is optional: an older client's status lacks id/cmode (0.1.1),
//  a client without RynthAi has no bot, one at character select has no status.
//
//  Distances: outdoors (and in surface buildings) the landblock grid is one
//  space, 192 m per landblock step. A dungeon is its own space: a distance is
//  given only when both stand in the same landblock, or both in the
//  overworld (SenseData's rule). Which indoor landblocks are overworld is the
//  caller's call (isOverworldBlock): RynthNet's is the Atlas's surface landblocks
//  (AtlasNames.cs).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace RynthCore.Plugin.RynthNet.Characters;

/// <summary>One client from RynthNet's snapshot, as sent (nothing derived yet).</summary>
internal sealed class NetCharEntry
{
    public string Node = "";
    public int Pid;
    public string Name = "";
    public string World = "";
    public bool LoggedIn;
    public string[] Tags = Array.Empty<string>();

    /// <summary>A status object was present (the client has logged in and sent one).</summary>
    public bool HasStatus;
    /// <summary>Milliseconds since that status arrived (-1 = unknown).</summary>
    public long AgeMs = -1;

    public bool HasVitals;
    public uint Hp, HpMax, St, StMax, Mp, MpMax;

    public bool HasPose;
    public uint Cell;
    public float X, Y, Z;

    public bool HasCoords;
    public double Ns, Ew;

    public bool Portal;
    /// <summary>The character's object id (RynthNet 0.1.1+), 0 when not sent.</summary>
    public uint Id;
    /// <summary>AC combat mode (1 peace, 2 melee, 4 missile, 8 magic); 0 when not sent.</summary>
    public int CombatMode;

    public bool HasBot;
    public bool Macro;
    public string Action = "";
}

internal sealed class NetCharSnapshot
{
    public static readonly NetCharSnapshot Empty = new();

    public bool Online;
    public string Offline = "";
    public string Version = "";
    public string MyName = "";
    public string MyWorld = "";
    public List<NetCharEntry> Clients = new();
}

internal enum CharStateKind { None, Idle, Botting, Fighting, CombatMode, Dead, Portal, NoStatus, LoggedOut }

/// <summary>Where another character is, relative to you.</summary>
internal readonly record struct CharPlace(bool Measured, float Distance, float Bearing, string Compass, string Location)
{
    public string DistanceText => Measured ? Distance.ToString("0", CultureInfo.InvariantCulture) + " yd" : "";
}

internal static class CharactersModel
{
    public const float LandblockSize = 192f;

    /// <summary>A status older than this is shown as stale (the client stopped ticking or sending).</summary>
    public const long StaleMs = 5000;

    private static readonly string[] Compass8 = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    // ── Parsing ─────────────────────────────────────────────────────────

    /// <summary>Parses one snapshot. Null for null/empty/invalid JSON or a non-object root.</summary>
    public static NetCharSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var snap = new NetCharSnapshot
            {
                Online = Bool(r, "online"),
                Offline = Str(r, "offline"),
                Version = Str(r, "version"),
                MyName = Str(r, "char"),
                MyWorld = Str(r, "world"),
            };
            if (r.TryGetProperty("clients", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement c in arr.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Object) snap.Clients.Add(ParseClient(c));
            return snap;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NetCharEntry ParseClient(JsonElement c)
    {
        var e = new NetCharEntry
        {
            Node = Str(c, "node"),
            Pid = (int)Num(c, "pid"),
            Name = Str(c, "char").Trim(),
            World = Str(c, "world"),
            LoggedIn = Bool(c, "login"),
        };
        if (c.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (JsonElement t in tags.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s) list.Add(s);
            e.Tags = list.ToArray();
        }
        if (c.TryGetProperty("status", out JsonElement s0) && s0.ValueKind == JsonValueKind.Object)
        {
            e.HasStatus = true;
            e.AgeMs = c.TryGetProperty("age", out JsonElement age) && age.ValueKind == JsonValueKind.Number ? (long)age.GetDouble() : -1;
            if (Has(s0, "hp") && Has(s0, "hpMax"))
            {
                e.HasVitals = true;
                e.Hp = U(s0, "hp"); e.HpMax = U(s0, "hpMax");
                e.St = U(s0, "st"); e.StMax = U(s0, "stMax");
                e.Mp = U(s0, "mp"); e.MpMax = U(s0, "mpMax");
            }
            if (Has(s0, "cell"))
            {
                e.Cell = U(s0, "cell");
                e.X = (float)Num(s0, "x"); e.Y = (float)Num(s0, "y"); e.Z = (float)Num(s0, "z");
                e.HasPose = (e.Cell >> 16) != 0;
            }
            if (Has(s0, "ns") && Has(s0, "ew"))
            {
                e.HasCoords = true;
                e.Ns = Num(s0, "ns"); e.Ew = Num(s0, "ew");
            }
            e.Portal = Bool(s0, "portal");
            e.Id = U(s0, "id");
            e.CombatMode = (int)Num(s0, "cmode");
            if (s0.TryGetProperty("bot", out JsonElement bot) && bot.ValueKind == JsonValueKind.Object)
            {
                e.HasBot = true;
                e.Macro = Bool(bot, "macro");
                e.Action = Str(bot, "action");
            }
        }
        return e;
    }

    private static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static uint U(JsonElement e, string name)
    {
        double d = Num(e, name);
        return d <= 0 ? 0 : d >= uint.MaxValue ? uint.MaxValue : (uint)d;
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ── Location ────────────────────────────────────────────────────────

    public static bool IsOutdoorCell(uint cell) => (cell & 0xFFFF) < 0x100;

    /// <summary>Compass bearing (0 = north, clockwise) to the nearest of 8 points.</summary>
    public static string Compass(float bearing)
    {
        float b = ((bearing % 360f) + 360f) % 360f;
        return Compass8[(int)MathF.Round(b / 45f) & 7];
    }

    /// <summary>"42.1N 33.6E" from a cell and its local x/y (the client's own coordinate basis).</summary>
    public static string Coords(uint cell, float x, float y)
    {
        int lbX = (int)((cell >> 24) & 0xFF), lbY = (int)((cell >> 16) & 0xFF);
        double ew = (lbX * 8.0 + x / 24.0 - 1019.5) / 10.0;
        double ns = (lbY * 8.0 + y / 24.0 - 1019.5) / 10.0;
        return CoordsText(ns, ew);
    }

    public static string CoordsText(double ns, double ew) =>
        Math.Abs(ns).ToString("0.0", CultureInfo.InvariantCulture) + (ns >= 0 ? "N " : "S ")
        + Math.Abs(ew).ToString("0.0", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");

    /// <summary>
    /// Where (<paramref name="cell"/>, x, y, z) is from you at (<paramref name="myCell"/>, mx, my, mz).
    /// <paramref name="isOverworldBlock"/>: whether an indoor cell's landblock (cell &gt;&gt; 16) is part of
    /// the overworld (a surface building) rather than a dungeon. <paramref name="dungeonName"/>: a
    /// landblock's dungeon name, "" when unknown. Either may be null.
    /// </summary>
    public static CharPlace Locate(uint myCell, float mx, float my, float mz,
        uint cell, float x, float y, float z,
        Func<uint, bool>? isOverworldBlock, Func<uint, string>? dungeonName)
    {
        uint lb = cell >> 16, myLb = myCell >> 16;
        bool theyOver = IsOutdoorCell(cell) || (isOverworldBlock?.Invoke(lb) ?? false);
        bool meOver = myLb != 0 && (IsOutdoorCell(myCell) || (isOverworldBlock?.Invoke(myLb) ?? false));
        string dn = theyOver ? "" : dungeonName?.Invoke(lb) ?? "";
        string where = theyOver ? Coords(cell, x, y)
            : dn.Length > 0 ? dn
            : "dungeon LB " + lb.ToString("X4", CultureInfo.InvariantCulture);

        bool measured = myLb != 0 && (lb == myLb || (theyOver && meOver));
        if (!measured)
            return new CharPlace(false, float.PositiveInfinity, float.NaN, "", where);

        int dxb = (int)((cell >> 24) & 0xFF) - (int)((myCell >> 24) & 0xFF);
        int dyb = (int)((cell >> 16) & 0xFF) - (int)((myCell >> 16) & 0xFF);
        float dx = x + dxb * LandblockSize - mx, dy = y + dyb * LandblockSize - my, dz = z - mz;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        float flat = MathF.Sqrt(dx * dx + dy * dy);
        if (flat < 0.5f) return new CharPlace(true, dist, float.NaN, "here", where);
        float b = MathF.Atan2(dx, dy) * (180f / MathF.PI);
        if (b < 0) b += 360f;
        return new CharPlace(true, dist, b, Compass(b), where);
    }

    // ── State ───────────────────────────────────────────────────────────

    /// <summary>What the character is doing, for the state column.</summary>
    public static (string Text, CharStateKind Kind) State(NetCharEntry e)
    {
        if (!e.LoggedIn || e.Name.Length == 0) return ("logged out", CharStateKind.LoggedOut);
        if (!e.HasStatus) return ("no status yet", CharStateKind.NoStatus);
        if (e.HasVitals && e.HpMax > 0 && e.Hp == 0) return ("dead", CharStateKind.Dead);
        if (e.Portal) return ("portal space", CharStateKind.Portal);
        if (e.HasBot && e.Macro)
        {
            string a = e.Action;
            if (a.Equals("Combat", StringComparison.OrdinalIgnoreCase)) return ("botting: fighting", CharStateKind.Fighting);
            if (a.Length == 0 || a.Equals("Default", StringComparison.OrdinalIgnoreCase)) return ("botting", CharStateKind.Botting);
            return ("botting: " + a.ToLowerInvariant(), CharStateKind.Botting);
        }
        string stance = CombatModeName(e.CombatMode);
        if (stance.Length > 0) return ("idle, " + stance, CharStateKind.CombatMode);
        return (e.HasBot ? "idle (macro off)" : "idle", CharStateKind.Idle);
    }

    /// <summary>"melee" / "missile" / "magic" for a combat stance, "" for peace or unknown.</summary>
    public static string CombatModeName(int mode) => mode switch
    {
        2 => "melee",
        4 => "missile",
        8 => "magic",
        _ => "",
    };

    public static bool IsStale(NetCharEntry e) => e.LoggedIn && e.HasStatus && e.AgeMs > StaleMs;

    // ── Order ───────────────────────────────────────────────────────────

    /// <summary>
    /// Row order: a stable one (Tom, 2026-10-13: distance order reshuffled rows several times a
    /// second for characters standing together). Logged-in characters alphabetically by name,
    /// then clients without a character, by pid. Distance is a column, never the order.
    /// </summary>
    public static int CompareRows(NetCharEntry a, NetCharEntry b)
    {
        bool an = a.LoggedIn && a.Name.Length > 0, bn = b.LoggedIn && b.Name.Length > 0;
        if (an != bn) return an ? -1 : 1;
        int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : a.Pid.CompareTo(b.Pid);
    }

    /// <summary>"1234/1500".</summary>
    public static string VitalText(uint cur, uint max) =>
        cur.ToString(CultureInfo.InvariantCulture) + "/" + max.ToString(CultureInfo.InvariantCulture);

    public static float Fraction(uint cur, uint max) => max == 0 ? 0f : Math.Clamp(cur / (float)max, 0f, 1f);
}
