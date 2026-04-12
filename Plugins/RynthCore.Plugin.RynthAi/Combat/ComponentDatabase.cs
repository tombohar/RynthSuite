using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Spell component data read from client_portal.dat (SpellComponentTable, ID 0x0E00000F).
///
/// Binary format (little-endian):
///   Header:  uint32 fileId  |  uint16 count  |  uint16 skip
///   Per entry:
///     uint32  key              (component ID)
///     uint16  nameLen
///     byte[]  nameBytes        (nibble-swapped ASCII, length = nameLen)
///     byte[]  pad              (pad name to 4-byte boundary from nameLen field start)
///     uint32  category         (SpellComponentCategory: 0=Scarab,1=Herb,2=PowderedGem,…)
///     uint32  icon
///     uint32  type             (ComponentType / SpellComponentCategory as enum value)
///     uint32  gesture
///     float   time             (gesture speed)
///     uint16  textLen
///     byte[]  textBytes        (nibble-swapped ASCII, length = textLen)
///     byte[]  pad              (pad text to 4-byte boundary from textLen field start)
///     float   cdm              (Component Destruction Modifier = BurnRate)
///
/// String decoding: each byte → nibble-swap → ((b << 4) | (b >> 4)) & 0xFF
/// </summary>
internal static class ComponentDatabase
{
    public sealed class ComponentRecord
    {
        public uint   Id;
        public string Name        = "";
        public uint   Category;
        public uint   IconId;
        public uint   Type;
        public uint   GestureId;
        public float  GestureSpeed;
        public string Word        = "";
        public float  BurnRate;

        public string CategoryName => Category switch
        {
            0 => "Scarab",
            1 => "Herb",
            2 => "PowderedGem",
            3 => "AlchemicalSubstance",
            4 => "Talisman",
            5 => "Taper",
            6 => "Pea",
            _ => $"Unknown({Category})"
        };

        public string TypeName => Type switch
        {
            1 => "Scarab",
            2 => "Herb",
            3 => "Powder",
            4 => "Potion",
            5 => "Talisman",
            6 => "Taper",
            7 => "Pea",
            _ => $"Unknown({Type})"
        };
    }

    private static readonly Dictionary<uint, ComponentRecord> _cache = new();
    private static bool _loaded;
    private static readonly object _lock = new();
    private static Action<string>? _log;

    public static void SetLog(Action<string> log) => _log = log;

    public static bool IsLoaded => _loaded;

    public static void EnsureLoaded(Action<string>? log = null)
    {
        lock (_lock)
        {
            if (_loaded) return;
            LoadFromDat(log);
        }
    }

    private static void LoadFromDat(Action<string>? log)
    {
        log ??= _log;
        string? datPath = FindPortalDat();
        if (datPath == null)
        {
            log?.Invoke("[ComponentDB] Could not find portal.dat");
            return;
        }

        try
        {
            log?.Invoke($"[ComponentDB] Opening {Path.GetFileName(datPath)}");
            using var dat = new DatDatabase();
            if (!dat.Open(datPath))
            {
                log?.Invoke($"[ComponentDB] Failed to open dat: {string.Join(", ", dat.DiagLog)}");
                return;
            }

            byte[]? raw = dat.GetFileData(0x0E00000F);
            if (raw == null || raw.Length < 8)
            {
                log?.Invoke("[ComponentDB] SpellComponentTable (0x0E00000F) not found");
                return;
            }

            int found = Parse(raw, log);
            _loaded = found > 0;
            log?.Invoke($"[ComponentDB] Loaded {found} component(s) from dat.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ComponentDB] Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int Parse(byte[] raw, Action<string>? log)
    {
        using var ms = new MemoryStream(raw);
        using var r = new BinaryReader(ms);

        uint fileId = r.ReadUInt32();   // 0x0E00000F
        ushort count = r.ReadUInt16();
        r.ReadUInt16();                 // skip unknown uint16

        log?.Invoke($"[ComponentDB] fileId=0x{fileId:X8} count={count}");

        int found = 0;
        for (int i = 0; i < count; i++)
        {
            if (r.BaseStream.Position + 4 > raw.Length) break;

            uint key = r.ReadUInt32();
            string name = ReadNibbleString(r);
            uint category = r.ReadUInt32();
            uint icon = r.ReadUInt32();
            uint type = r.ReadUInt32();
            uint gesture = r.ReadUInt32();
            float time = r.ReadSingle();
            string text = ReadNibbleString(r);
            float cdm = r.ReadSingle();

            if (!string.IsNullOrEmpty(name))
            {
                _cache[key] = new ComponentRecord
                {
                    Id           = key,
                    Name         = name,
                    Category     = category,
                    IconId       = icon,
                    Type         = type,
                    GestureId    = gesture,
                    GestureSpeed = time,
                    Word         = text,
                    BurnRate     = cdm,
                };
                found++;
            }
        }
        return found;
    }

    /// <summary>
    /// Reads a nibble-swapped string: uint16 len, len bytes (each byte nibble-swapped), pad to 4-byte.
    /// </summary>
    private static string ReadNibbleString(BinaryReader r)
    {
        ushort len = r.ReadUInt16();
        byte[] b = r.ReadBytes(len);
        // Pad to next 4-byte boundary from the start of the uint16 field (2 + len bytes used so far)
        int pad = (4 - (2 + len) % 4) % 4;
        if (pad > 0) r.ReadBytes(pad);

        for (int i = 0; i < b.Length; i++)
            b[i] = (byte)((b[i] << 4) | (b[i] >> 4));

        return Encoding.ASCII.GetString(b).TrimEnd('\0');
    }

    private static string? FindPortalDat()
    {
        // 1. Process directory (we're injected into acclient.exe — dat is alongside it)
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                string? dir = Path.GetDirectoryName(exePath);
                if (dir != null)
                {
                    foreach (var name in new[] { "client_portal.dat", "portal.dat" })
                    {
                        string full = Path.Combine(dir, name);
                        if (File.Exists(full)) return full;
                    }
                }
            }
        }
        catch { }

        // 2. Common install paths
        string[] searchDirs =
        {
            @"C:\Turbine\Asheron's Call",
            @"C:\Program Files\Turbine\Asheron's Call",
            @"C:\Program Files (x86)\Turbine\Asheron's Call",
            @"C:\Games\Asheron's Call",
            @"C:\AC",
            @"C:\Asheron's Call",
            @"D:\Turbine\Asheron's Call",
            @"D:\Games\Asheron's Call",
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var name in new[] { "client_portal.dat", "portal.dat" })
            {
                string full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }

        // 3. Registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Turbine\Asheron's Call");
            if (key != null)
            {
                string? installPath = key.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(installPath))
                {
                    foreach (var name in new[] { "client_portal.dat", "portal.dat" })
                    {
                        string full = Path.Combine(installPath, name);
                        if (File.Exists(full)) return full;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public static string GetComponentName(uint id)
    {
        EnsureLoaded();
        return _cache.TryGetValue(id, out var r) ? r.Name : $"Unknown Component ({id})";
    }

    public static bool TryGetRecord(uint id, out ComponentRecord? rec)
    {
        EnsureLoaded();
        return _cache.TryGetValue(id, out rec);
    }
}
