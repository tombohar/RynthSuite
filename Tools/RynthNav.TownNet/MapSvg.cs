using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace RynthNav.TownNet;

/// <summary>
/// A floor plan of the network as SVG, for looking the walks over: floors (grey, one shade per
/// height), walls and solid objects (dark), portals (blue rings: the reach that uses them), arrival
/// points (green), and the walks from one arrival (or all).
/// </summary>
internal static class MapSvg
{
    public static string Draw(Indoor w, IEnumerable<uint> cells, JsonObject doc, string? onlyFrom, float level)
    {
        var ci = CultureInfo.InvariantCulture;
        var cellList = cells.Select(id => w.Cells[id]).Where(c => c.Floors.Any(f => MathF.Abs(f[0].Z - level) < 3f)).ToList();
        var all = cellList.SelectMany(c => c.Floors).SelectMany(f => f).ToList();
        float minX = all.Min(v => v.X) - 3, maxX = all.Max(v => v.X) + 3, minY = all.Min(v => v.Y) - 3, maxY = all.Max(v => v.Y) + 3;
        const float S = 6f;   // pixels per metre
        string P(float x, float y) => ((x - minX) * S).ToString("F1", ci) + "," + ((maxY - y) * S).ToString("F1", ci);
        var sb = new StringBuilder();
        sb.Append(ci, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{(maxX - minX) * S:F0}\" height=\"{(maxY - minY) * S:F0}\" style=\"background:#fff\">\n");
        foreach (var c in cellList)
            foreach (var f in c.Floors.Where(f => MathF.Abs(f[0].Z - level) < 3f))
                sb.Append($"<polygon points=\"{string.Join(" ", f.Select(v => P(v.X, v.Y)))}\" fill=\"{(MathF.Abs(f[0].Z - level) < 0.5f ? "#ddd" : "#eee")}\" stroke=\"#ccc\" stroke-width=\"0.5\"/>\n");
        foreach (var s in w.Solids.Where(s => s.Blocks(level)))
        {
            if (s.Poly.Length == 0)
                sb.Append(ci, $"<circle cx=\"{(s.Center.X - minX) * S:F1}\" cy=\"{(maxY - s.Center.Y) * S:F1}\" r=\"{s.Radius * S:F1}\" fill=\"#a33\"/>\n");
            else
                sb.Append($"<polygon points=\"{string.Join(" ", s.Poly.Select(v => P(v.X, v.Y)))}\" fill=\"{(s.Filled ? "#a33" : "none")}\" stroke=\"{(s.Source.StartsWith("wall") ? "#222" : "#a33")}\" stroke-width=\"1.5\"/>\n");
        }
        foreach (var p in w.Portals)
        {
            sb.Append(ci, $"<circle cx=\"{(p.Pos.X - minX) * S:F1}\" cy=\"{(maxY - p.Pos.Y) * S:F1}\" r=\"{(p.Radius + w.AgentRadius) * S:F1}\" fill=\"none\" stroke=\"#36c\" stroke-width=\"1\"/>\n");
            sb.Append(ci, $"<text x=\"{(p.Pos.X - minX) * S:F1}\" y=\"{(maxY - p.Pos.Y) * S + 3:F1}\" font-size=\"8\" text-anchor=\"middle\" fill=\"#036\">{System.Security.SecurityElement.Escape(p.Name.Replace("Portal to ", "").Replace(" Portal", ""))}</text>\n");
        }
        foreach (var wk in doc["walks"]?.AsArray() ?? new JsonArray())
        {
            if (wk!["points"] is not JsonArray pts || (onlyFrom != null && (string?)wk["from"] != onlyFrom)) continue;
            var xy = pts.Select(p => P((float)(double)p![1]!, (float)(double)p[2]!));
            sb.Append($"<polyline points=\"{string.Join(" ", xy)}\" fill=\"none\" stroke=\"#2a2\" stroke-width=\"1.2\" opacity=\"0.7\"/>\n");
        }
        foreach (var a in doc["arrivals"]!.AsArray())
        {
            float x = (float)(double)a!["x"]!, y = (float)(double)a["y"]!;
            sb.Append(ci, $"<circle cx=\"{(x - minX) * S:F1}\" cy=\"{(maxY - y) * S:F1}\" r=\"5\" fill=\"#2a2\"/>\n");
            sb.Append(ci, $"<text x=\"{(x - minX) * S + 7:F1}\" y=\"{(maxY - y) * S - 6:F1}\" font-size=\"11\" fill=\"#060\">{(string)a["id"]!}</text>\n");
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}
