using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthAi.Meta;

namespace RynthCore.Plugin.RynthAi.Diagnostics;

/// <summary>
/// What /ub propertydump (= /vt propertydump = /ra dumpprops, the PropertyDump hotkey) prints for
/// one object. Filled from the engine's v84 GetObjectProperties (every key the client holds), or on
/// an older engine from per-key reads of every named key plus the old fixed ranges.
/// </summary>
internal sealed class PropertyDumpData
{
    public uint Id;
    public string Name = "";
    public uint Wcid;
    public string ObjectClass = "";
    public int ObjectClassValue;
    public bool IsPlayer;
    public bool HasIdData;
    /// <summary>Unix seconds of the last identify; 0 never.</summary>
    public long LastIdTime;
    /// <summary>False: an engine before v84, so only the keys probed one by one are listed.</summary>
    public bool Complete;

    public readonly Dictionary<uint, int> Ints = new();
    public readonly Dictionary<uint, long> Int64s = new();
    public readonly Dictionary<uint, bool> Bools = new();
    public readonly Dictionary<uint, double> Floats = new();
    public readonly Dictionary<uint, string> Strings = new();
    public readonly Dictionary<uint, uint> DataIds = new();
    public readonly Dictionary<uint, uint> InstanceIds = new();
    public readonly List<uint> Spells = new();

    public int PropertyCount => Ints.Count + Int64s.Count + Bools.Count + Floats.Count + Strings.Count
                              + DataIds.Count + InstanceIds.Count;
}

/// <summary>
/// The dump's text, laid out like UtilityBelt's propertydump (a header, then one section per
/// property type, entries sorted by name, then the spell book) so the two can be compared line by
/// line: "  Name (key) = value", unknown keys as their number, data and instance ids in hex.
/// Pure: no host, no game; RynthAiHostTests checks it.
/// </summary>
internal static class PropertyDump
{
    /// <summary>Lines posted to chat per plugin tick (the rest follow on the next ticks).</summary>
    public const int ChatLinesPerTick = 25;

    /// <summary>Where the full dump is written (RynthAi\Logs); tests point it at a scratch folder.</summary>
    internal static string? FolderOverride;
    public static string Folder => FolderOverride ?? Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "Logs");

    public static List<string> Format(PropertyDumpData d, Func<uint, string> spellName, Func<uint, string?> objectName)
    {
        var lines = new List<string>();
        string name = d.Name.Length > 0 ? d.Name : (d.Strings.TryGetValue(1, out string? n) ? n : "?");
        lines.Add($"Property Dump for {name}");
        lines.Add($"Id = {d.Id} (0x{d.Id:X8})");
        if (d.Wcid != 0) lines.Add($"Type (wcid) = {d.Wcid}");
        lines.Add($"Name = {name}");
        if (d.ObjectClass.Length > 0) lines.Add($"ObjectClass = {d.ObjectClass} ({d.ObjectClassValue})");
        if (d.Ints.TryGetValue(1, out int itemType)) lines.Add($"Category = {itemType} (0x{itemType:X8})");
        if (d.InstanceIds.TryGetValue(2, out uint container)) lines.Add($"Container = 0x{container:X8}{Who(container, objectName)}");
        if (d.InstanceIds.TryGetValue(3, out uint wielder)) lines.Add($"Wielder = 0x{wielder:X8}{Who(wielder, objectName)}");
        lines.Add($"HasIdData = {(d.HasIdData ? "True" : "False")}");
        lines.Add(d.LastIdTime > 0
            ? $"LastIdTime = {d.LastIdTime} ({DateTimeOffset.FromUnixTimeSeconds(d.LastIdTime).ToLocalTime():yyyy-MM-dd HH:mm:ss})"
            : "LastIdTime = 0");
        lines.Add($"SpellCount = {d.Spells.Count}");

        Section(lines, "String Values", d.Strings, PropertyNames.GetStringName, v => v);
        Section(lines, "Int Values", d.Ints, PropertyNames.GetIntName, v => v.ToString(CultureInfo.InvariantCulture));
        Section(lines, "Int64 Values", d.Int64s, PropertyNames.GetInt64Name, v => v.ToString(CultureInfo.InvariantCulture));
        Section(lines, "Bool Values", d.Bools, PropertyNames.GetBoolName, v => v ? "True" : "False");
        Section(lines, "Float Values", d.Floats, PropertyNames.GetFloatName, v => v.ToString("R", CultureInfo.InvariantCulture));
        Section(lines, "DataId Values", d.DataIds, PropertyNames.GetDataIdName, v => $"0x{v:X8}");
        Section(lines, "InstanceId Values", d.InstanceIds, PropertyNames.GetInstanceIdName, v => $"0x{v:X8}{Who(v, objectName)}");

        if (d.Spells.Count > 0)
        {
            lines.Add($"Spells ({d.Spells.Count}):");
            foreach (uint s in d.Spells)
                lines.Add($"  {spellName(s)} ({s})");
        }

        lines.Add($"Total: {d.PropertyCount} properties ({d.Strings.Count} string, {d.Ints.Count} int, {d.Int64s.Count} int64, " +
                  $"{d.Bools.Count} bool, {d.Floats.Count} float, {d.DataIds.Count} data id, {d.InstanceIds.Count} instance id), " +
                  $"{d.Spells.Count} spell(s).");
        if (!d.Complete)
            lines.Add("Older RynthCore engine (before plugin API 84): only the keys asked one by one are listed. Update RynthCore for every key.");
        if (!d.HasIdData && !d.IsPlayer)
            lines.Add("Not identified: only what the client gets without an appraisal is listed.");
        return lines;
    }

    // One section, only when it has entries (as UtilityBelt): named keys by name, then unknown keys by number.
    private static void Section<T>(List<string> lines, string title, Dictionary<uint, T> values,
        Func<uint, string?> nameOf, Func<T, string> show)
    {
        if (values.Count == 0) return;
        lines.Add(title + ":");
        foreach (var kv in values
                     .Select(kv => (kv.Key, kv.Value, Name: nameOf(kv.Key)))
                     .OrderBy(e => e.Name == null ? 1 : 0)
                     .ThenBy(e => e.Name, StringComparer.Ordinal)
                     .ThenBy(e => e.Key))
            lines.Add(kv.Name != null
                ? $"  {kv.Name} ({kv.Key}) = {show(kv.Value)}"
                : $"  {kv.Key} = {show(kv.Value)}");
    }

    private static string Who(uint id, Func<uint, string?> objectName)
    {
        if (id == 0) return "";
        string? n = objectName(id);
        return string.IsNullOrEmpty(n) ? "" : $" ({n})";
    }

    /// <summary>propertydump-&lt;name&gt;-&lt;yyyyMMdd-HHmmss&gt;.txt, the name cut to safe characters.</summary>
    public static string FileName(string name, DateTime when)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (sb.Length >= 40) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : (c == ' ' ? '_' : '\0'));
        }
        string safe = sb.ToString().Replace("\0", "").Trim('_');
        if (safe.Length == 0) safe = "object";
        return $"propertydump-{safe}-{when:yyyyMMdd-HHmmss}.txt";
    }

    /// <summary>Probe lists for an engine before v84: every named key plus the old fixed ranges.</summary>
    public static IEnumerable<uint> ProbeKeys(IEnumerable<uint> named, uint rangeEnd)
    {
        var set = new SortedSet<uint>(named);
        for (uint i = 0; i <= rangeEnd; i++) set.Add(i);
        return set;
    }
}
