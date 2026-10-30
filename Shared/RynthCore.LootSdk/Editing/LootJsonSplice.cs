using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RynthCore.Loot.Editing;

/// <summary>
/// Adds one rule to a native (.json) loot profile by splicing its JSON into the
/// file's "Rules" array: every byte outside the insertion stays as it was
/// (LootProfile.Save would rewrite the whole file, and the JSON writer drops
/// what it doesn't know). The result is checked before it is returned: it must
/// load, have one more rule, and serialise exactly like the original once the
/// new rule is taken out again.
/// </summary>
public static class LootJsonSplice
{
    /// <summary>
    /// The file's bytes with <paramref name="rule"/> inserted at <paramref name="index"/>
    /// (the end when out of range; <paramref name="at"/> says where it went), or
    /// null with <paramref name="error"/>.
    /// </summary>
    public static byte[]? Insert(byte[] file, LootRule rule, int index, out int at, out string error)
    {
        at = -1;
        error = string.Empty;
        int start = file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF ? 3 : 0;
        LootProfile? before;
        try
        {
            // Strict UTF-8: a file that isn't is refused rather than re-encoded.
            string text = new UTF8Encoding(false, true).GetString(file, start, file.Length - start);
            before = JsonSerializer.Deserialize(text, LootJsonContext.Default.LootProfile);
        }
        catch (Exception ex)
        {
            error = "the profile doesn't load as it is (" + ex.Message + ").";
            return null;
        }
        if (before == null) { error = "the profile is empty (null)."; return null; }

        if (!FindRules(file, start, out int open, out int close, out List<(int Start, int End)> elements, out error))
            return null;
        if (elements.Count != before.Rules.Count)
        {
            error = $"found {elements.Count} rules in the text but {before.Rules.Count} when loaded.";
            return null;
        }

        string nl = Contains(file, start, "\r\n") ? "\r\n" : "\n";
        string ruleJson = JsonSerializer.Serialize(rule, LootJsonContext.Default.LootRule).Replace("\r\n", "\n");
        at = index < 0 || index > elements.Count ? elements.Count : index;

        string insert;
        int spliceAt, spliceEnd;
        if (elements.Count == 0)
        {
            // "[]" or "[ ... ]" with only whitespace: replace the inside.
            string outer = LineIndent(file, start, open);
            string indent = outer + "  ";
            insert = nl + indent + Reindent(ruleJson, indent, nl) + nl + outer;
            spliceAt = open + 1;
            spliceEnd = close;
        }
        else
        {
            string indent = LineIndent(file, start, elements[0].Start);
            if (at < elements.Count)
            {
                insert = Reindent(ruleJson, indent, nl) + "," + nl + indent;
                spliceAt = spliceEnd = elements[at].Start;
            }
            else
            {
                insert = "," + nl + indent + Reindent(ruleJson, indent, nl);
                spliceAt = spliceEnd = elements[^1].End;
            }
        }

        byte[] add = Encoding.UTF8.GetBytes(insert);
        var result = new byte[spliceAt + add.Length + (file.Length - spliceEnd)];
        Array.Copy(file, 0, result, 0, spliceAt);
        Array.Copy(add, 0, result, spliceAt, add.Length);
        Array.Copy(file, spliceEnd, result, spliceAt + add.Length, file.Length - spliceEnd);

        // Check: loads, one more rule, the new one where it should be, the rest unchanged.
        try
        {
            LootProfile? after = JsonSerializer.Deserialize(Encoding.UTF8.GetString(result, start, result.Length - start), LootJsonContext.Default.LootProfile);
            if (after == null || after.Rules.Count != before.Rules.Count + 1)
            {
                error = "the result doesn't have exactly one more rule.";
                return null;
            }
            if (JsonSerializer.Serialize(after.Rules[at], LootJsonContext.Default.LootRule) != JsonSerializer.Serialize(rule, LootJsonContext.Default.LootRule))
            {
                error = "the new rule reads back differently.";
                return null;
            }
            after.Rules.RemoveAt(at);
            if (JsonSerializer.Serialize(after, LootJsonContext.Default.LootProfile) != JsonSerializer.Serialize(before, LootJsonContext.Default.LootProfile))
            {
                error = "another part of the profile would change.";
                return null;
            }
        }
        catch (Exception ex)
        {
            error = "the result doesn't load (" + ex.Message + ").";
            return null;
        }
        return result;
    }

    /// <summary>Finds the root object's "Rules" array: its brackets and each element's byte range.</summary>
    private static bool FindRules(byte[] file, int start, out int open, out int close, out List<(int Start, int End)> elements, out string error)
    {
        open = close = -1;
        elements = new List<(int, int)>();
        error = string.Empty;
        var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(file, start, file.Length - start));
        bool found = false;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { error = "the file isn't a JSON object."; return false; }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals("Rules"u8))
                {
                    reader.Skip();
                    continue;
                }
                if (found) { error = "the file has two \"Rules\" lists."; return false; }
                found = true;
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) { error = "\"Rules\" isn't a list."; return false; }
                open = start + (int)reader.TokenStartIndex;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    int s = start + (int)reader.TokenStartIndex;
                    reader.Skip();
                    elements.Add((s, start + (int)reader.TokenStartIndex + 1));
                }
                close = start + (int)reader.TokenStartIndex;
            }
        }
        catch (JsonException ex)
        {
            error = "the file isn't valid JSON (" + ex.Message + ").";
            return false;
        }
        if (!found) { error = "the file has no \"Rules\" list."; return false; }
        return true;
    }

    /// <summary>The spaces/tabs from the start of the line holding <paramref name="pos"/> up to the first other character.</summary>
    private static string LineIndent(byte[] file, int start, int pos)
    {
        int lineStart = pos;
        while (lineStart > start && file[lineStart - 1] != (byte)'\n') lineStart--;
        int end = lineStart;
        while (end < pos && (file[end] == (byte)' ' || file[end] == (byte)'\t')) end++;
        return Encoding.ASCII.GetString(file, lineStart, end - lineStart);
    }

    /// <summary>The serializer's indented JSON (LF) shifted right by <paramref name="indent"/> after its first line.</summary>
    private static string Reindent(string json, string indent, string nl) => json.Replace("\n", nl + indent);

    private static bool Contains(byte[] file, int start, string ascii)
    {
        byte[] needle = Encoding.ASCII.GetBytes(ascii);
        return new ReadOnlySpan<byte>(file, start, file.Length - start).IndexOf(needle) >= 0;
    }
}
