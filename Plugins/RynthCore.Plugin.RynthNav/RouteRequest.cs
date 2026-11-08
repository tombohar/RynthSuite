using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthNav;

/// <summary>What the route maker can be asked (chat or the "RynthNav.Routes" interface).</summary>
internal enum RouteOp { Plan, MakeRoute, MakeLoop, MakePath, Info, Atlas, Go, Stop, Where }

/// <summary>The route maker's limits: a request outside them is refused, saying which and why.</summary>
internal static class RouteLimits
{
    /// <summary>A loop's radius, yards: under 20 it's a camp spot, over 250 its legs get long and it stops being readable.</summary>
    public const double MinRadius = 20, MaxRadius = 250;
    public const int MinLoopPoints = 3, MaxLoopPoints = 16;
    public const int MinPathPoints = 2, MaxPathPoints = 32;
    /// <summary>One makepath leg, straight-line yards (about 50 map degrees).</summary>
    public const double MaxLegYards = 12000;
    public const int DefaultAtlas = 10, MaxAtlas = 50;
    /// <summary>A request's JSON, characters.</summary>
    public const int MaxJson = 16384;
    /// <summary>Map coordinates beyond this (degrees, either way) are off Dereth.</summary>
    public const double MapLimit = 102.0;
    public const int MaxTextLength = 200;
}

/// <summary>
/// One route maker request, from a /rnav command (<see cref="FromCommand"/>) or the interface's JSON
/// (<see cref="FromJson"/>), checked against <see cref="RouteLimits"/> and the nav file name rules
/// (NavRouteExport.FileName). Both end up here, so the chat commands and the interface run the same
/// thing (RynthNavPlugin.RouteMaker.cs). Host-free: the route tests drive it.
/// </summary>
internal sealed class RouteRequest
{
    public RouteOp Op;
    /// <summary>makeroute, makeloop, makepath, info: the nav file's name (without .nav).</summary>
    public string Name = "";
    public bool Overwrite;
    /// <summary>plan, makeroute, go: a place name or coordinates, as /rnav go takes them.</summary>
    public string To = "";
    /// <summary>plan, makeroute: use portals and recalls (null: the "portals" switch).</summary>
    public bool? Portals;
    /// <summary>plan, makeroute: show the trip in the panel and on the ground, as /rnav route does (chat: always).</summary>
    public bool Show;
    /// <summary>makeloop: round the player (else round AtNs, AtEw).</summary>
    public bool Here = true;
    public double AtNs, AtEw;
    public double Radius;
    /// <summary>makeloop: how many points (0 = RouteShape.DefaultPoints of the radius).</summary>
    public int Points;
    public NavRouteType PathType = NavRouteType.Once;
    /// <summary>makepath: the points, in order.</summary>
    public readonly List<(double Ns, double Ew)> Path = new();
    public string Search = "";
    public int Max = RouteLimits.DefaultAtlas;
    /// <summary>Typed in chat: the answer goes to chat too.</summary>
    public bool FromChat;

    /// <summary>The op's name in the JSON ("plan", "makeroute", ...).</summary>
    public static string OpName(RouteOp op) => op switch
    {
        RouteOp.Plan => "plan",
        RouteOp.MakeRoute => "makeroute",
        RouteOp.MakeLoop => "makeloop",
        RouteOp.MakePath => "makepath",
        RouteOp.Info => "info",
        RouteOp.Atlas => "atlas",
        RouteOp.Go => "go",
        RouteOp.Stop => "stop",
        _ => "where",
    };

    /// <summary>The /rnav word for the op (what a chat reply starts with).</summary>
    public static string CommandName(RouteOp op) => op == RouteOp.Info ? "routeinfo" : OpName(op);

    public string Command => CommandName(Op);

    /// <summary>The usage line said with a refusal: the /rnav form for chat, the JSON form for the interface.</summary>
    public static string Usage(RouteOp op, bool chat) => chat ? op switch
    {
        RouteOp.MakeRoute => "/rnav makeroute <name> to <place|coords> [overwrite]",
        RouteOp.MakeLoop => $"/rnav makeloop <name> [at <coords>|here] radius <yards {RouteLimits.MinRadius:F0}-{RouteLimits.MaxRadius:F0}> [points <{RouteLimits.MinLoopPoints}-{RouteLimits.MaxLoopPoints}>] [overwrite]",
        RouteOp.MakePath => "/rnav makepath <name> [loop|line|once] <coords>; <coords>; ... [overwrite]",
        RouteOp.Info => "/rnav routeinfo <name>",
        _ => "/rnav help",
    } : op switch
    {
        RouteOp.Plan => "{\"op\":\"plan\",\"to\":\"<place>\"|{\"ns\":n,\"ew\":e},\"portals\":true|false}",
        RouteOp.MakeRoute => "{\"op\":\"makeroute\",\"name\":\"<name>\",\"to\":\"<place>\"|{\"ns\":n,\"ew\":e},\"overwrite\":false}",
        RouteOp.MakeLoop => $"{{\"op\":\"makeloop\",\"name\":\"<name>\",\"at\":\"here\"|{{\"ns\":n,\"ew\":e}},\"radius\":{RouteLimits.MinRadius:F0}-{RouteLimits.MaxRadius:F0},\"points\":{RouteLimits.MinLoopPoints}-{RouteLimits.MaxLoopPoints},\"overwrite\":false}}",
        RouteOp.MakePath => "{\"op\":\"makepath\",\"name\":\"<name>\",\"type\":\"loop|line|once\",\"points\":[{\"ns\":n,\"ew\":e},...],\"overwrite\":false}",
        RouteOp.Info => "{\"op\":\"info\",\"name\":\"<name>\"}",
        RouteOp.Atlas => $"{{\"op\":\"atlas\",\"search\":\"<text>\",\"max\":1-{RouteLimits.MaxAtlas}}}",
        RouteOp.Go => "{\"op\":\"go\",\"to\":\"<place>\"|{\"ns\":n,\"ew\":e}}",
        RouteOp.Stop => "{\"op\":\"stop\"}",
        _ => "{\"op\":\"where\"}",
    };

    /// <summary>Coordinates as text /rnav go reads back exactly ("42.1234N, 33.5E").</summary>
    public static string CoordText(double ns, double ew)
    {
        var ci = CultureInfo.InvariantCulture;
        return Math.Abs(ns).ToString("0.#####", ci) + (ns >= 0 ? "N" : "S") + ", " + Math.Abs(ew).ToString("0.#####", ci) + (ew >= 0 ? "E" : "W");
    }

    private static bool OnMap(double ns, double ew) =>
        double.IsFinite(ns) && double.IsFinite(ew) && Math.Abs(ns) <= RouteLimits.MapLimit && Math.Abs(ew) <= RouteLimits.MapLimit;

    // ── From a /rnav command ─────────────────────────────────────────────

    /// <summary>
    /// /rnav makeroute, makeloop, makepath or routeinfo (<paramref name="command"/>, lower case) with
    /// the rest of the line. Null with <paramref name="error"/> (no usage: the caller adds it).
    /// </summary>
    public static RouteRequest? FromCommand(string command, string rest, out string error)
    {
        error = "";
        string text = (rest ?? "").Trim();
        var r = new RouteRequest { FromChat = true, Show = true };
        switch (command)
        {
            case "makeroute": r.Op = RouteOp.MakeRoute; break;
            case "makeloop": r.Op = RouteOp.MakeLoop; break;
            case "makepath": r.Op = RouteOp.MakePath; break;
            case "routeinfo": r.Op = RouteOp.Info; break;
            default: error = $"'{command}' isn't a route maker command"; return null;
        }
        if (text.Length == 0) { error = "give the route a name"; return null; }
        if (text.Length > RouteLimits.MaxJson) { error = "that's too long"; return null; }

        // A name in quotes may hold anything a file name can ("Hunt at the lake").
        string? quoted = null;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            if (end < 0) { error = "the name's closing quote is missing"; return null; }
            quoted = text.Substring(1, end - 1);
            text = text.Substring(end + 1).Trim();
        }
        if (r.Op != RouteOp.Info && EndsWithWord(text, "overwrite")) { r.Overwrite = true; text = text[..^9].TrimEnd(); }

        switch (r.Op)
        {
            case RouteOp.Info:
                if (!SetName(r, quoted ?? text, out error)) return null;
                if (quoted != null && text.Length > 0) { error = $"'{text}' after the name: routeinfo takes only a name"; return null; }
                return r;

            case RouteOp.MakeRoute:
            {
                string where;
                if (quoted != null)
                {
                    if (!StartsWithWord(text, "to")) { error = "say where: <name> to <place|coords>"; return null; }
                    where = text.Substring(2).Trim();
                    if (!SetName(r, quoted, out error)) return null;
                }
                else
                {
                    // The first " to " ends the name: place names hold it more often ("Portal to Cragstone").
                    int at = text.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
                    if (at <= 0) { error = "say where: <name> to <place|coords>"; return null; }
                    if (!SetName(r, text[..at], out error)) return null;
                    where = text[(at + 4)..].Trim();
                }
                if (where.Length == 0) { error = "say where: <name> to <place|coords>"; return null; }
                if (where.Length > RouteLimits.MaxTextLength) { error = "the place is too long"; return null; }
                if (NavCoords.TryParseLoc(where, out double tns, out double tew) && !OnMap(tns, tew))
                { error = $"{where} is off the map"; return null; }
                r.To = where;
                return r;
            }

            case RouteOp.MakeLoop:
            {
                string[] t = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int optAt = -1;
                string firstError = "";
                for (int k = quoted != null ? 0 : 1; k < t.Length; k++)
                {
                    if (!IsLoopWord(t[k])) continue;
                    var trial = new RouteRequest();
                    if (TryLoopOptions(t, k, trial, out string why)) { optAt = k; break; }
                    if (firstError.Length == 0) firstError = why;
                }
                if (quoted == null && optAt < 0)
                {
                    error = firstError.Length > 0 ? firstError : "give a name and the radius";
                    return null;
                }
                if (quoted != null && optAt != 0)
                {
                    error = t.Length == 0 ? "give the radius" : firstError.Length > 0 ? firstError : $"'{t[0]}' isn't one of at, here, radius, points, overwrite";
                    return null;
                }
                if (!SetName(r, quoted ?? string.Join(' ', t, 0, optAt), out error)) return null;
                TryLoopOptions(t, optAt, r, out _);
                return CheckLoop(r, out error) ? r : null;
            }

            default:   // makepath
            {
                string[] segs = text.Split(';');
                string[] t = segs[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int coordAt = -1;
                for (int k = quoted != null ? 0 : 1; k < t.Length && coordAt < 0; k++)
                    if (NavCoords.TrySplitCoordsAndName(string.Join(' ', t, k, t.Length - k), out _, out _, out string left) && left.Length == 0)
                        coordAt = k;
                if (coordAt < 0) { error = "give a name, then the points: <coords>; <coords>; ..."; return null; }
                int nameEnd = coordAt;
                if (nameEnd >= 1 && TryPathType(t[nameEnd - 1], out NavRouteType type) && (quoted != null || nameEnd >= 2))
                {
                    r.PathType = type;
                    nameEnd--;
                }
                if (quoted != null && nameEnd != 0) { error = $"'{t[0]}' after the name isn't loop, line, once or coordinates"; return null; }
                if (!SetName(r, quoted ?? string.Join(' ', t, 0, nameEnd), out error)) return null;
                for (int s = 0; s < segs.Length; s++)
                {
                    string seg = s == 0 ? string.Join(' ', t, coordAt, t.Length - coordAt) : segs[s].Trim();
                    if (seg.Length == 0) continue;
                    if (!NavCoords.TrySplitCoordsAndName(seg, out double ns, out double ew, out string extra) || extra.Length > 0)
                    { error = $"point {r.Path.Count + 1}: can't read '{seg}' as coordinates (e.g. 42.1N, 33.6E)"; return null; }
                    r.Path.Add((ns, ew));
                }
                return CheckPath(r, out error) ? r : null;
            }
        }
    }

    private static bool EndsWithWord(string text, string word) =>
        text.Equals(word, StringComparison.OrdinalIgnoreCase)
        || (text.Length > word.Length && text.EndsWith(word, StringComparison.OrdinalIgnoreCase) && text[^(word.Length + 1)] == ' ');

    private static bool StartsWithWord(string text, string word) =>
        text.Equals(word, StringComparison.OrdinalIgnoreCase)
        || (text.Length > word.Length && text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && text[word.Length] == ' ');

    private static bool IsLoopWord(string w) =>
        w.Equals("at", StringComparison.OrdinalIgnoreCase) || w.Equals("here", StringComparison.OrdinalIgnoreCase)
        || w.Equals("radius", StringComparison.OrdinalIgnoreCase) || w.Equals("points", StringComparison.OrdinalIgnoreCase)
        || w.Equals("overwrite", StringComparison.OrdinalIgnoreCase);

    /// <summary>makeloop's options from token <paramref name="from"/> on: at &lt;coords&gt; | here, radius &lt;yd&gt;, points &lt;n&gt;.</summary>
    private static bool TryLoopOptions(string[] t, int from, RouteRequest r, out string error)
    {
        error = "";
        bool radius = false, centre = false;
        for (int i = from; i < t.Length; i++)
        {
            string w = t[i].ToLowerInvariant();
            switch (w)
            {
                case "here":
                    if (centre) { error = "say the centre once: at <coords> or here"; return false; }
                    r.Here = true; centre = true;
                    break;
                case "overwrite":
                    r.Overwrite = true;
                    break;
                case "radius":
                    if (i + 1 >= t.Length || !double.TryParse(t[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out r.Radius))
                    { error = "radius needs a number of yards"; return false; }
                    radius = true; i++;
                    break;
                case "points":
                    if (i + 1 >= t.Length || !int.TryParse(t[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out r.Points))
                    { error = "points needs a whole number"; return false; }
                    i++;
                    break;
                case "at":
                {
                    if (centre) { error = "say the centre once: at <coords> or here"; return false; }
                    int j = i + 1;
                    while (j < t.Length && !IsLoopWord(t[j])) j++;
                    string coords = string.Join(' ', t, i + 1, j - i - 1);
                    if (!NavCoords.TrySplitCoordsAndName(coords, out r.AtNs, out r.AtEw, out string extra) || extra.Length > 0)
                    { error = $"at needs coordinates (e.g. at 42.1N, 33.6E), not '{coords}'"; return false; }
                    r.Here = false; centre = true;
                    i = j - 1;
                    break;
                }
                default:
                    error = $"'{t[i]}' isn't one of at, here, radius, points, overwrite";
                    return false;
            }
        }
        if (!radius) { error = "give the radius: radius <yards>"; return false; }
        return true;
    }

    private static bool TryPathType(string w, out NavRouteType type)
    {
        switch (w.ToLowerInvariant())
        {
            case "loop": case "circular": type = NavRouteType.Circular; return true;
            case "line": case "linear": type = NavRouteType.Linear; return true;
            case "once": type = NavRouteType.Once; return true;
            default: type = NavRouteType.Once; return false;
        }
    }

    private static bool SetName(RouteRequest r, string typed, out string error)
    {
        r.Name = NavRouteExport.FileName(typed, out error);
        return r.Name.Length > 0;
    }

    private static bool CheckLoop(RouteRequest r, out string error)
    {
        error = "";
        if (!double.IsFinite(r.Radius) || r.Radius < RouteLimits.MinRadius || r.Radius > RouteLimits.MaxRadius)
        { error = $"radius {r.Radius.ToString("0.#", CultureInfo.InvariantCulture)} is out of range ({RouteLimits.MinRadius:F0}-{RouteLimits.MaxRadius:F0} yards)"; return false; }
        if (r.Points != 0 && (r.Points < RouteLimits.MinLoopPoints || r.Points > RouteLimits.MaxLoopPoints))
        { error = $"points {r.Points} is out of range ({RouteLimits.MinLoopPoints}-{RouteLimits.MaxLoopPoints})"; return false; }
        if (!r.Here && !OnMap(r.AtNs, r.AtEw)) { error = $"{CoordText(r.AtNs, r.AtEw)} is off the map"; return false; }
        return true;
    }

    private static bool CheckPath(RouteRequest r, out string error)
    {
        error = "";
        if (r.Path.Count < RouteLimits.MinPathPoints) { error = $"give at least {RouteLimits.MinPathPoints} points, separated by ;"; return false; }
        if (r.Path.Count > RouteLimits.MaxPathPoints) { error = $"{r.Path.Count} points is too many (at most {RouteLimits.MaxPathPoints})"; return false; }
        for (int i = 0; i < r.Path.Count; i++)
            if (!OnMap(r.Path[i].Ns, r.Path[i].Ew)) { error = $"point {i + 1} ({CoordText(r.Path[i].Ns, r.Path[i].Ew)}) is off the map"; return false; }
        for (int i = 1; i < r.Path.Count; i++)
        {
            double d = NavCoords.Distance(r.Path[i - 1].Ns, r.Path[i - 1].Ew, r.Path[i].Ns, r.Path[i].Ew);
            if (d > RouteLimits.MaxLegYards) { error = $"leg {i} is {NavCoords.FmtDistance(d)} long (at most {NavCoords.FmtDistance(RouteLimits.MaxLegYards)} a leg)"; return false; }
        }
        return true;
    }

    // ── From the interface's JSON ────────────────────────────────────────

    /// <summary>A request from the "RynthNav.Routes" interface (Shared/RynthNavRoutesApi.cs has the format). Null with <paramref name="error"/>.</summary>
    public static RouteRequest? FromJson(string? json, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(json)) { error = "the request is empty"; return null; }
        if (json.Length > RouteLimits.MaxJson) { error = $"the request is {json.Length} characters (at most {RouteLimits.MaxJson})"; return null; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException ex) { error = "not JSON: " + ex.Message; return null; }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "the request must be a JSON object"; return null; }
            if (!Str(root, "op", true, out string op, out error)) return null;
            var r = new RouteRequest();
            if (!TryOp(op, out r.Op)) { error = $"unknown op '{op}' ({OpList})"; return null; }
            bool ok = r.Op switch
            {
                RouteOp.Plan => ToField(root, r, out error) && OptBool(root, "portals", ref r.Portals, out error) && Bool(root, "show", ref r.Show, out error),
                RouteOp.Go => ToField(root, r, out error),
                RouteOp.MakeRoute => NameField(root, r, out error) && ToField(root, r, out error) && OptBool(root, "portals", ref r.Portals, out error)
                                     && Bool(root, "overwrite", ref r.Overwrite, out error) && Bool(root, "show", ref r.Show, out error),
                RouteOp.MakeLoop => NameField(root, r, out error) && At(root, r, out error) && Num(root, "radius", true, ref r.Radius, out error)
                                    && Int(root, "points", ref r.Points, out error) && Bool(root, "overwrite", ref r.Overwrite, out error) && CheckLoop(r, out error),
                RouteOp.MakePath => NameField(root, r, out error) && PathTypeOf(root, r, out error) && PathPoints(root, r, out error)
                                    && Bool(root, "overwrite", ref r.Overwrite, out error) && CheckPath(r, out error),
                RouteOp.Info => NameField(root, r, out error),
                RouteOp.Atlas => SearchField(root, r, out error),
                _ => true,
            };
            return ok ? r : null;
        }
    }

    private const string OpList = "plan, makeroute, makeloop, makepath, info, atlas, go, stop, where";

    private static bool TryOp(string text, out RouteOp op)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "plan": op = RouteOp.Plan; return true;
            case "makeroute": op = RouteOp.MakeRoute; return true;
            case "makeloop": op = RouteOp.MakeLoop; return true;
            case "makepath": op = RouteOp.MakePath; return true;
            case "info": case "routeinfo": op = RouteOp.Info; return true;
            case "atlas": op = RouteOp.Atlas; return true;
            case "go": op = RouteOp.Go; return true;
            case "stop": op = RouteOp.Stop; return true;
            case "where": op = RouteOp.Where; return true;
            default: op = RouteOp.Where; return false;
        }
    }

    /// <summary>The usage to give with a refused request: its op's JSON form when the op was readable, else the list of ops.</summary>
    public static string UsageFor(string? json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("op", out JsonElement v)
                && v.ValueKind == JsonValueKind.String && TryOp(v.GetString() ?? "", out RouteOp op))
                return Usage(op, chat: false);
        }
        catch (JsonException) { }
        return $"{{\"op\": one of {OpList}, ...}} (Plugins/Shared/RynthNavRoutesApi.cs has each op's fields)";
    }

    private static bool Str(JsonElement o, string name, bool required, out string value, out string error)
    {
        value = ""; error = "";
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) error = $"\"{name}\" is missing";
            return !required;
        }
        if (v.ValueKind != JsonValueKind.String) { error = $"\"{name}\" must be a string"; return false; }
        value = v.GetString() ?? "";
        if (value.Length > RouteLimits.MaxTextLength) { error = $"\"{name}\" is too long (at most {RouteLimits.MaxTextLength} characters)"; return false; }
        return true;
    }

    private static bool Bool(JsonElement o, string name, ref bool value, out string error)
    {
        bool? b = value;
        if (!OptBool(o, name, ref b, out error)) return false;
        value = b ?? value;
        return true;
    }

    private static bool OptBool(JsonElement o, string name, ref bool? value, out string error)
    {
        error = "";
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return true;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = $"\"{name}\" must be true or false"; return false; }
        value = v.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool Num(JsonElement o, string name, bool required, ref double value, out string error)
    {
        error = "";
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) error = $"\"{name}\" is missing";
            return !required;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d)) { error = $"\"{name}\" must be a number"; return false; }
        value = d;
        return true;
    }

    private static bool Int(JsonElement o, string name, ref int value, out string error)
    {
        error = "";
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return true;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) { error = $"\"{name}\" must be a whole number"; return false; }
        value = i;
        return true;
    }

    /// <summary>{"ns": n, "ew": e} as map coordinates on Dereth.</summary>
    private static bool Coords(JsonElement v, string what, out double ns, out double ew, out string error)
    {
        ns = ew = 0; error = "";
        if (v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("ns", out JsonElement n) || n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out ns)
            || !v.TryGetProperty("ew", out JsonElement e) || e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out ew))
        { error = $"{what} must be {{\"ns\": number, \"ew\": number}} (north and east positive)"; return false; }
        if (!OnMap(ns, ew)) { error = $"{what} ({CoordText(ns, ew)}) is off the map"; return false; }
        return true;
    }

    private static bool NameField(JsonElement o, RouteRequest r, out string error)
    {
        if (!Str(o, "name", true, out string name, out error)) return false;
        return SetName(r, name, out error);
    }

    private static bool ToField(JsonElement o, RouteRequest r, out string error)
    {
        error = "";
        if (!o.TryGetProperty("to", out JsonElement v) || v.ValueKind == JsonValueKind.Null) { error = "\"to\" is missing (a place name or {\"ns\", \"ew\"})"; return false; }
        if (v.ValueKind == JsonValueKind.Object)
        {
            if (!Coords(v, "\"to\"", out double ns, out double ew, out error)) return false;
            r.To = CoordText(ns, ew);
            return true;
        }
        if (!Str(o, "to", true, out string to, out error)) { error = "\"to\" must be a place name or {\"ns\", \"ew\"}"; return false; }
        to = to.Trim();
        if (to.Length == 0) { error = "\"to\" is empty"; return false; }
        if (NavCoords.TryParseLoc(to, out double tns, out double tew) && !OnMap(tns, tew)) { error = $"\"to\" ({to}) is off the map"; return false; }
        r.To = to;
        return true;
    }

    private static bool At(JsonElement o, RouteRequest r, out string error)
    {
        error = "";
        r.Here = true;
        if (!o.TryGetProperty("at", out JsonElement v) || v.ValueKind == JsonValueKind.Null) return true;
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = (v.GetString() ?? "").Trim();
            if (s.Equals("here", StringComparison.OrdinalIgnoreCase)) return true;
            if (!NavCoords.TrySplitCoordsAndName(s, out r.AtNs, out r.AtEw, out string extra) || extra.Length > 0)
            { error = "\"at\" must be \"here\", coordinates or {\"ns\", \"ew\"}"; return false; }
            r.Here = false;
            return true;
        }
        if (!Coords(v, "\"at\"", out r.AtNs, out r.AtEw, out error)) return false;
        r.Here = false;
        return true;
    }

    private static bool PathTypeOf(JsonElement o, RouteRequest r, out string error)
    {
        if (!Str(o, "type", false, out string type, out error)) return false;
        if (type.Length == 0) { r.PathType = NavRouteType.Once; return true; }
        if (TryPathType(type.Trim(), out r.PathType)) return true;
        error = $"\"type\" '{type}' isn't loop, line or once";
        return false;
    }

    private static bool PathPoints(JsonElement o, RouteRequest r, out string error)
    {
        error = "";
        if (!o.TryGetProperty("points", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        { error = "\"points\" must be an array of {\"ns\", \"ew\"} (or coordinate strings)"; return false; }
        int i = 0;
        foreach (JsonElement p in arr.EnumerateArray())
        {
            i++;
            if (i > RouteLimits.MaxPathPoints) { error = $"too many points (at most {RouteLimits.MaxPathPoints})"; return false; }
            double ns, ew;
            if (p.ValueKind == JsonValueKind.String)
            {
                string s = p.GetString() ?? "";
                if (!NavCoords.TrySplitCoordsAndName(s, out ns, out ew, out string extra) || extra.Length > 0)
                { error = $"point {i}: can't read '{s}' as coordinates"; return false; }
            }
            else if (!Coords(p, $"point {i}", out ns, out ew, out error)) return false;
            r.Path.Add((ns, ew));
        }
        return true;
    }

    private static bool SearchField(JsonElement o, RouteRequest r, out string error)
    {
        if (!Str(o, "search", true, out r.Search, out error)) return false;
        r.Search = r.Search.Trim();
        if (r.Search.Length == 0) { error = "\"search\" is empty"; return false; }
        if (!Int(o, "max", ref r.Max, out error)) return false;
        if (r.Max < 1 || r.Max > RouteLimits.MaxAtlas) { error = $"\"max\" must be 1 to {RouteLimits.MaxAtlas}"; return false; }
        return true;
    }
}
