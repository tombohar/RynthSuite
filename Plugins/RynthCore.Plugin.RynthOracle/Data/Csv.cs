// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>The bundled lists (embedded CSVs) and a small RFC 4180 line parser.</summary>
internal static class Csv
{
    /// <summary>Opens an embedded resource by its logical name (RynthOracle.&lt;file&gt;).</summary>
    public static StreamReader OpenEmbedded(string fileName)
    {
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RynthOracle." + fileName)
            ?? throw new FileNotFoundException("Embedded resource not found: " + fileName);
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    }

    /// <summary>Header names (lower case, trimmed) to column index.</summary>
    public static Dictionary<string, int> MapColumns(string header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] names = ParseLine(header);
        for (int i = 0; i < names.Length; i++)
        {
            string n = names[i].Trim().TrimStart('﻿');
            if (n.Length > 0 && !map.ContainsKey(n)) map[n] = i;
        }
        return map;
    }

    public static int Column(Dictionary<string, int> map, params string[] names)
    {
        foreach (string n in names)
            if (map.TryGetValue(n, out int i)) return i;
        return -1;
    }

    public static string Field(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index].Trim() : string.Empty;

    /// <summary>Splits one CSV line: commas separate, double quotes enclose, "" is a quote.</summary>
    public static string[] ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    public static bool IsTrue(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || s == "1" || s.Equals("verified", StringComparison.OrdinalIgnoreCase);
}
