using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RynthNav.TownNet;

/// <summary>
/// townnet.json, as gen_townnet.py writes it: an object whose lists hold one item per line. This
/// tool keeps everything it does not own and replaces "walks" and "walkInfo". The set id is the
/// first 16 hex digits of the SHA-256 of the file's text without its "setId" and "generated" lines
/// (carriage returns dropped, so a CRLF checkout gives the same id; gen_townnet.py computes it
/// the same way), so equal ids mean equal data.
/// </summary>
internal static class TownNetFile
{
    private static readonly string[] HeadKeys = { "version", "setId", "generated", "source", "landblocks", "counts", "notes", "walkInfo" };
    private static readonly string[] ListKeys = { "arrivals", "entries", "exits", "npcs", "walks" };

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject Load(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true })!.AsObject();

    public static string Serialize(JsonObject doc)
    {
        var keys = HeadKeys.Where(doc.ContainsKey).Concat(ListKeys.Where(doc.ContainsKey))
            .Concat(doc.Select(kv => kv.Key).Where(k => !HeadKeys.Contains(k) && !ListKeys.Contains(k))).ToList();
        var sb = new StringBuilder("{\n");
        for (int i = 0; i < keys.Count; i++)
        {
            string k = keys[i];
            bool last = i == keys.Count - 1;
            JsonNode? v = doc[k];
            if (ListKeys.Contains(k) && v is JsonArray arr)
            {
                sb.Append("  ").Append(JsonSerializer.Serialize(k)).Append(": [\n");
                for (int j = 0; j < arr.Count; j++)
                    sb.Append("    ").Append(arr[j]!.ToJsonString(Compact)).Append(j < arr.Count - 1 ? ",\n" : "\n");
                sb.Append("  ]").Append(last ? "\n" : ",\n");
            }
            else
            {
                sb.Append("  ").Append(JsonSerializer.Serialize(k)).Append(": ").Append(SpacedJson(v)).Append(last ? "\n" : ",\n");
            }
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Head values the way Python's json.dumps writes them (", " and ": " separators).</summary>
    private static string SpacedJson(JsonNode? v)
    {
        if (v == null) return "null";
        if (v is JsonArray a) return "[" + string.Join(", ", a.Select(SpacedJson)) + "]";
        if (v is JsonObject o) return "{" + string.Join(", ", o.Select(kv => JsonSerializer.Serialize(kv.Key) + ": " + SpacedJson(kv.Value))) + "}";
        return v.ToJsonString(Compact);
    }

    public static string SetId(string text)
    {
        var kept = text.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("  \"setId\":") && !l.StartsWith("  \"generated\":"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", kept)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static void Save(string path, JsonObject doc)
    {
        if (!doc.ContainsKey("setId")) doc["setId"] = "";
        string text = Serialize(doc);
        string id = SetId(text);
        doc["setId"] = id;
        text = Serialize(doc);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
