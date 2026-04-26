using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Plugin.RynthAi.Maps;

/// <summary>
/// Persistent per-prefab map patches. Each entry says "for prefab (envId, cellStructIdx),
/// add a 0.5u floor cell at local-space (gx, gy)" or "remove one". Keyed per-prefab so a
/// fix to one corridor cell propagates to every dungeon that uses the same prefab.
///
/// File format (text, one entry per line, NativeAOT-safe):
///   envIdHex,cellStructIdx,gx,gy,type    (add)
///   envIdHex,cellStructIdx,gx,gy,-       (remove)
///   # leading-hash lines are comments
///
/// Default file: %CSIDL_PROGRAM_FILES%\..\..\Games\RynthSuite\RynthAi\MapPatches.txt
/// (resolved against the running game install at runtime).
/// </summary>
internal sealed class MapPatchStore
{
    public enum PatchKind : byte { AddFlat = 0, AddSlopeUp = 1, AddSlopeDown = 2, Remove = 255 }

    private readonly Dictionary<long, Dictionary<(int gx, int gy), PatchKind>> _patches = new();
    private readonly string _filePath;
    private bool _dirty;

    public MapPatchStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public bool IsDirty => _dirty;
    public int  TotalPatchCount
    {
        get
        {
            int n = 0;
            foreach (var kv in _patches) n += kv.Value.Count;
            return n;
        }
    }

    private static long Key(uint envId, uint cellStructIdx) => ((long)envId << 32) | cellStructIdx;

    /// <summary>Add or update a patch entry.</summary>
    public void Set(uint envId, uint cellStructIdx, int gx, int gy, PatchKind kind)
    {
        long k = Key(envId, cellStructIdx);
        if (!_patches.TryGetValue(k, out var dict))
            _patches[k] = dict = new Dictionary<(int, int), PatchKind>();
        var coord = (gx, gy);
        if (dict.TryGetValue(coord, out var existing) && existing == kind) return;
        dict[coord] = kind;
        _dirty = true;
    }

    /// <summary>Remove a patch entry entirely (revert to polygon-derived behavior).</summary>
    public void Clear(uint envId, uint cellStructIdx, int gx, int gy)
    {
        long k = Key(envId, cellStructIdx);
        if (!_patches.TryGetValue(k, out var dict)) return;
        if (dict.Remove((gx, gy))) _dirty = true;
        if (dict.Count == 0) _patches.Remove(k);
    }

    /// <summary>Get every patch for a prefab. Returns null if none.</summary>
    public Dictionary<(int gx, int gy), PatchKind>? GetPatches(uint envId, uint cellStructIdx)
    {
        _patches.TryGetValue(Key(envId, cellStructIdx), out var dict);
        return dict;
    }

    public void Load()
    {
        _patches.Clear();
        _dirty = false;
        if (!File.Exists(_filePath)) return;

        try
        {
            foreach (var raw in File.ReadAllLines(_filePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split(',');
                if (parts.Length != 5) continue;

                if (!TryParseUInt(parts[0], out uint envId)) continue;
                if (!TryParseUInt(parts[1], out uint csIdx)) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gx)) continue;
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gy)) continue;

                PatchKind kind;
                if (parts[4] == "-") kind = PatchKind.Remove;
                else if (parts[4] == "0") kind = PatchKind.AddFlat;
                else if (parts[4] == "1") kind = PatchKind.AddSlopeUp;
                else if (parts[4] == "2") kind = PatchKind.AddSlopeDown;
                else continue;

                long k = Key(envId, csIdx);
                if (!_patches.TryGetValue(k, out var dict))
                    _patches[k] = dict = new Dictionary<(int, int), PatchKind>();
                dict[(gx, gy)] = kind;
            }
        }
        catch { /* corrupt file — ignore, treat as empty */ }
    }

    public void SaveIfDirty()
    {
        if (!_dirty) return;
        Save();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var lines = new List<string>(_patches.Count * 4 + 4)
            {
                "# RynthAi map patches — per-prefab floor cell overrides.",
                "# Format: envIdHex,cellStructIdx,gx,gy,type    (type: 0=Flat,1=SlopeUp,2=SlopeDown,-=Remove)",
                "# 0.5u local-cell grid; gx/gy range roughly [-10,+9] within each 10×10 prefab.",
            };

            foreach (var (k, dict) in _patches)
            {
                uint envId = (uint)((k >> 32) & 0xFFFFFFFF);
                uint csIdx = (uint)(k & 0xFFFFFFFF);
                foreach (var ((gx, gy), kind) in dict)
                {
                    string typeStr = kind switch
                    {
                        PatchKind.AddFlat      => "0",
                        PatchKind.AddSlopeUp   => "1",
                        PatchKind.AddSlopeDown => "2",
                        _                      => "-",
                    };
                    lines.Add($"0x{envId:X8},{csIdx},{gx.ToString(CultureInfo.InvariantCulture)},{gy.ToString(CultureInfo.InvariantCulture)},{typeStr}");
                }
            }

            File.WriteAllLines(_filePath, lines);
            _dirty = false;
        }
        catch { /* disk full / permission denied — keep dirty in-memory state */ }
    }

    private static bool TryParseUInt(string s, out uint value)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
