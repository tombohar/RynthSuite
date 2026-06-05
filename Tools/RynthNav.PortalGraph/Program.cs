using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using RynthNav.Routing;

namespace RynthNav.PortalGraph;

// Offline tool: parse the vendored GoArrow GAlocations.xml into a clean portals.json
// travel-edge list that the RynthNav plugin can consume.
//
// A GoArrow "Town Portal" entry is a directed travel edge:
//   stand at (latitude, longitude)  -> teleport -> arrive (arrival_latitude, arrival_longitude)
// Coordinates are AC /loc decimal degrees (NS, EW) -- the same frame the plugin parses.
//
// Usage:
//   RynthNav.PortalGraph [--in <GAlocations.xml>] [--out <portals.json>] [--min-arrival 1.0]
//                        [--include-retired]
internal static class Program
{
    private static int Main(string[] args)
    {
        string exeDir = AppContext.BaseDirectory;
        string inPath = Path.Combine(exeDir, "Data", "GAlocations.xml");
        string outPath = Path.Combine(exeDir, "Data", "portals.json");
        bool includeRetired = false;
        // Arrival coords are stored to 0.1 deg; treat |lat|+|lon| below this as "no real destination".
        double minArrival = 0.5;
        string? routeFrom = null, routeTo = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in": inPath = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--include-retired": includeRetired = true; break;
                case "--min-arrival": minArrival = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--route": routeFrom = args[++i]; routeTo = args[++i]; break;
                case "-h" or "--help":
                    Console.WriteLine("RynthNav.PortalGraph [--in <GAlocations.xml>] [--out <portals.json>] " +
                                      "[--min-arrival 0.5] [--include-retired]");
                    Console.WriteLine("  --route \"<ns,ew>\" \"<ns,ew>\"   plan a portal route between two /loc coords");
                    return 0;
            }
        }

        if (!File.Exists(inPath))
        {
            Console.Error.WriteLine($"Input not found: {inPath}");
            return 2;
        }

        Console.WriteLine($"Reading {inPath}");
        XDocument doc = XDocument.Load(inPath);

        int total = 0, skippedNoArrival = 0, skippedRetired = 0, skippedNotPortal = 0;
        var portals = new List<PortalEdge>();

        foreach (XElement loc in doc.Descendants("location"))
        {
            total++;

            // A location may carry several <type> tags (city + portal + dungeon).
            // We only keep ones that act as a portal.
            var types = loc.Elements("type").Select(t => (t.Value ?? "").Trim()).ToList();
            bool isPortal = types.Any(t => t.Contains("Portal", StringComparison.OrdinalIgnoreCase));
            if (!isPortal) { skippedNotPortal++; continue; }

            string retired = (loc.Element("retired")?.Value ?? "").Trim();
            if (!includeRetired && retired.Equals("Y", StringComparison.OrdinalIgnoreCase))
            {
                skippedRetired++;
                continue;
            }

            // ⚠ GoArrow's latitude is INVERTED vs AC /loc: it stores North as negative
            // (e.g. Holtburg, actually 42.3N, is latitude -42.3 in GAlocations.xml).
            // Negate to get true AC NS. Longitude (EW) matches AC already.
            double srcNs = -ParseD(loc.Element("latitude")?.Value);
            double srcEw = ParseD(loc.Element("longitude")?.Value);
            double dstNs = -ParseD(loc.Element("arrival_latitude")?.Value);
            double dstEw = ParseD(loc.Element("arrival_longitude")?.Value);

            // Need a real destination to be a usable travel edge.
            if (Math.Abs(dstNs) + Math.Abs(dstEw) < minArrival)
            {
                skippedNoArrival++;
                continue;
            }

            portals.Add(new PortalEdge
            {
                Id = (int)ParseD(loc.Element("id")?.Value),
                Name = (loc.Element("name")?.Value ?? "").Trim(),
                Type = types.FirstOrDefault(t => t.Contains("Portal", StringComparison.OrdinalIgnoreCase)) ?? "Portal",
                SrcNs = srcNs,
                SrcEw = srcEw,
                DstNs = dstNs,
                DstEw = dstEw,
                // GoArrow restrictions = recommended level band, e.g. "1-6". Advisory only.
                Restrictions = NullIfEmpty(loc.Element("restrictions")?.Value),
                PkRequired = !string.IsNullOrWhiteSpace(loc.Element("pk_req")?.Value),
            });
        }

        var output = new PortalFile
        {
            Source = "GoArrow GAlocations.xml (Roogon/CoD DB)",
            GeneratedNote = "Coordinates are AC /loc decimal degrees (NS, EW). Each entry is a directed " +
                            "travel edge: walk to Src, take the portal, arrive at Dst.",
            IncludesRetired = includeRetired,
            Count = portals.Count,
            Portals = portals.OrderBy(p => p.Id).ToList(),
        };

        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(output, jsonOpts));

        // Also emit a flat TSV the AOT plugin reads with plain string.Split (no JSON
        // dependency in the NativeAOT runtime). Columns: srcNs srcEw dstNs dstEw name
        string tsvPath = Path.ChangeExtension(outPath, ".tsv");
        var tsv = new System.Text.StringBuilder(portals.Count * 48);
        foreach (var p in portals)
        {
            string safeName = p.Name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            tsv.Append(p.SrcNs.ToString(CultureInfo.InvariantCulture)).Append('\t')
               .Append(p.SrcEw.ToString(CultureInfo.InvariantCulture)).Append('\t')
               .Append(p.DstNs.ToString(CultureInfo.InvariantCulture)).Append('\t')
               .Append(p.DstEw.ToString(CultureInfo.InvariantCulture)).Append('\t')
               .Append(safeName).Append('\n');
        }
        File.WriteAllText(tsvPath, tsv.ToString());

        Console.WriteLine($"Locations scanned : {total}");
        Console.WriteLine($"  not a portal    : {skippedNotPortal}");
        Console.WriteLine($"  retired skipped : {skippedRetired}");
        Console.WriteLine($"  no destination  : {skippedNoArrival}");
        Console.WriteLine($"Portal edges kept : {portals.Count}");
        Console.WriteLine($"Wrote {outPath}");
        Console.WriteLine($"Wrote {tsvPath}");

        if (routeFrom != null && routeTo != null)
        {
            if (!TryParseLoc(routeFrom, out double fNs, out double fEw) ||
                !TryParseLoc(routeTo, out double tNs, out double tEw))
            {
                Console.Error.WriteLine("Bad --route coords. Use e.g. --route \"42.5N,33.6E\" \"2.7N,18.9E\"");
                return 3;
            }
            var links = new List<PortalLink>(portals.Count);
            foreach (var p in portals) links.Add(new PortalLink(p.SrcNs, p.SrcEw, p.DstNs, p.DstEw, p.Name));
            var steps = PortalRoute.Plan(links, null, fNs, fEw, tNs, tEw, out double est, out int used);

            double directUnits = Math.Sqrt(((fNs - tNs) * (fNs - tNs) + (fEw - tEw) * (fEw - tEw))) * PortalRoute.UnitsPerDegree;
            Console.WriteLine();
            Console.WriteLine($"ROUTE  {Fmt(fNs, 'N', 'S')} {Fmt(fEw, 'E', 'W')}  ->  {Fmt(tNs, 'N', 'S')} {Fmt(tEw, 'E', 'W')}");
            Console.WriteLine($"  direct walk : {directUnits:F0}u");
            Console.WriteLine($"  planned     : {est:F0}u via {used} portal hop(s), {steps.Count} leg(s)");
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                string act = s.UseRecall ? $"RECALL ({s.Label})" : s.UsePortal ? $"walk to + take portal '{s.Label}'" : "walk to goal/exit";
                Console.WriteLine($"   {i + 1,2}. {Fmt(s.Ns, 'N', 'S')} {Fmt(s.Ew, 'E', 'W')}  {act}");
            }
        }
        return 0;
    }

    private static string Fmt(double v, char pos, char neg) => $"{Math.Abs(v):F1}{(v >= 0 ? pos : neg)}";

    private static bool TryParseLoc(string s, out double ns, out double ew)
    {
        ns = 0; ew = 0;
        var parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        return TryCoord(parts[0], out ns) && TryCoord(parts[1], out ew);
    }

    private static bool TryCoord(string tok, out double val)
    {
        val = 0;
        tok = tok.Trim().ToUpperInvariant();
        if (tok.Length == 0) return false;
        int sign = 1;
        char last = tok[^1];
        if (last is 'N' or 'S' or 'E' or 'W') { if (last is 'S' or 'W') sign = -1; tok = tok[..^1]; }
        if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return false;
        val *= sign;
        return true;
    }

    private static double ParseD(string? s)
        => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

internal sealed class PortalFile
{
    public string Source { get; set; } = "";
    public string GeneratedNote { get; set; } = "";
    public bool IncludesRetired { get; set; }
    public int Count { get; set; }
    public List<PortalEdge> Portals { get; set; } = new();
}

internal sealed class PortalEdge
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";

    // Source: where the portal stands (you walk here).
    public double SrcNs { get; set; }
    public double SrcEw { get; set; }

    // Destination: where the portal drops you.
    public double DstNs { get; set; }
    public double DstEw { get; set; }

    public string? Restrictions { get; set; }
    public bool PkRequired { get; set; }
}
