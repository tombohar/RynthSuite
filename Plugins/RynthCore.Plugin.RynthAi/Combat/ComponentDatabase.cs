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

    // spellId → required component ids, decrypted account-INDEPENDENTLY from
    // the SpellTable (0x0E00000E) via the Name+Desc key (see ParseSpellTable).
    // The per-account scramble (GetSpellFormula) only re-rolls the taper slots,
    // which we treat as "any taper", so the account name is never needed.
    private static readonly Dictionary<int, uint[]> _spellComps = new();
    private static bool _spellCompsLoaded;

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

            // Same open dat: pull the SpellTable so the combat resolver can
            // predictively skip a spell whose components the char lacks.
            try
            {
                byte[]? spellRaw = dat.GetFileData(0x0E00000E);
                if (spellRaw == null || spellRaw.Length < 8)
                {
                    log?.Invoke("[ComponentDB] SpellTable (0x0E00000E) not found");
                }
                else
                {
                    int spells = ParseSpellTable(spellRaw, log);
                    _spellCompsLoaded = spells > 0;
                    log?.Invoke($"[ComponentDB] Loaded comp formulas for {spells} spell(s).");
                    LogSpellCompSample(log);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ComponentDB] SpellTable parse error: {ex.GetType().Name}: {ex.Message}");
            }
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

    // ── SpellTable (0x0E00000E) → required components ────────────────────────
    //
    // Layout per ACE.DatLoader (SpellTable.cs / SpellBase.cs): file id u32,
    // then a PackedHashTable<u32 spellId, SpellBase>. SpellBase fields are read
    // in order; the component SET is recovered from the 8 raw comp slots minus
    // a key derived from the spell's (de-obfuscated) Name+Desc bytes — this is
    // ACCOUNT-INDEPENDENT. The further per-account GetSpellFormula scramble
    // only re-rolls taper slots, so we never need the account name. We must
    // read every SpellBase field (including the variable meta block) so the
    // sequential hash table stays aligned to the next entry.

    private static int ParseSpellTable(byte[] raw, Action<string>? log)
    {
        using var ms = new MemoryStream(raw);
        using var r = new BinaryReader(ms);

        r.ReadUInt32();                  // file id 0x0E00000E
        ushort total = r.ReadUInt16();   // PackedHashTable: totalObjects
        r.ReadUInt16();                  // bucket size (unused in C#)

        int n = 0;
        for (int i = 0; i < total; i++)
        {
            if (r.BaseStream.Position + 4 > raw.Length) break;
            uint spellId = r.ReadUInt32();
            uint[] comps = ReadSpellBaseFormula(r);
            _spellComps[(int)spellId] = comps;
            n++;
        }
        return n;
    }

    private static uint[] ReadSpellBaseFormula(BinaryReader r)
    {
        byte[] nameB = ReadObf(r); Align(r);
        byte[] descB = ReadObf(r); Align(r);

        r.ReadUInt32(); // School
        r.ReadUInt32(); // Icon
        r.ReadUInt32(); // Category
        r.ReadUInt32(); // Bitfield
        r.ReadUInt32(); // BaseMana
        r.ReadSingle(); // BaseRangeConstant
        r.ReadSingle(); // BaseRangeMod
        r.ReadUInt32(); // Power
        r.ReadSingle(); // SpellEconomyMod
        r.ReadUInt32(); // FormulaVersion
        r.ReadSingle(); // ComponentLoss
        uint metaType = r.ReadUInt32(); // MetaSpellType (SpellType enum)
        r.ReadUInt32(); // MetaSpellId

        // SpellType: Enchantment=1, PortalSummon=7, FellowEnchantment=12.
        if (metaType == 1 || metaType == 12)
            r.ReadBytes(16);            // Duration(double)+DegradeModifier(f)+DegradeLimit(f)
        else if (metaType == 7)
            r.ReadBytes(8);             // PortalLifetime(double)

        var rawComps = new List<uint>(8);
        for (int j = 0; j < 8; j++)
        {
            uint c = r.ReadUInt32();
            if (c > 0) rawComps.Add(c);
        }

        // Trailing SpellBase fields — consume so the next entry stays aligned.
        r.ReadUInt32(); // CasterEffect
        r.ReadUInt32(); // TargetEffect
        r.ReadUInt32(); // FizzleEffect
        r.ReadDouble(); // RecoveryInterval
        r.ReadSingle(); // RecoveryAmount
        r.ReadUInt32(); // DisplayOrder
        r.ReadUInt32(); // NonComponentTargetType
        r.ReadUInt32(); // ManaMod

        return DecryptFormula(rawComps, nameB, descB);
    }

    private static byte[] ReadObf(BinaryReader r)
    {
        ushort len = r.ReadUInt16();
        byte[] b = r.ReadBytes(len);
        for (int i = 0; i < b.Length; i++)
            b[i] = (byte)((b[i] >> 4) | (b[i] << 4));
        return b;
    }

    private static void Align(BinaryReader r)
    {
        long d = r.BaseStream.Position % 4;
        if (d != 0) r.BaseStream.Position += (4 - d);
    }

    // ACE SpellTable.ComputeHash, operating directly on the de-obfuscated
    // CP1252 bytes (identity vs. ACE's 1252 round-trip for printable spell
    // text) so there is no CodePages/encoding dependency under NativeAOT.
    private static uint Hash(byte[] bytes)
    {
        long result = 0;
        foreach (byte bb in bytes)
        {
            sbyte c = (sbyte)bb;
            result = c + (result << 4);
            if ((result & 0xF0000000) != 0)
                result = (result ^ ((result & 0xF0000000) >> 24)) & 0x0FFFFFFF;
        }
        return (uint)result;
    }

    private const uint HIGHEST_COMP_ID = 198; // "Essence of Kemeroi" (Void); per ACE

    private static uint[] DecryptFormula(List<uint> rawComps, byte[] nameB, byte[] descB)
    {
        uint key = (Hash(nameB) % 0x12107680u) + (Hash(descB) % 0xBEADCF45u);
        var comps = new uint[rawComps.Count];
        for (int i = 0; i < rawComps.Count; i++)
        {
            uint comp = unchecked(rawComps[i] - key);
            if (comp > HIGHEST_COMP_ID) comp &= 0xFF;
            comps[i] = comp;
        }
        return comps;
    }

    private static void LogSpellCompSample(Action<string>? log)
    {
        if (log == null) return;
        // 4439 = "Incantation of Flame Bolt" (the spell that triggered this).
        // Eyeball the resolved comp names in-game to confirm the parse.
        foreach (int sid in new[] { 4439, 2 })
        {
            if (!_spellComps.TryGetValue(sid, out var ids)) continue;
            var names = new List<string>();
            foreach (uint cid in ids)
                names.Add(_cache.TryGetValue(cid, out var rc) ? rc.Name : $"#{cid}");
            log($"[ComponentDB] sample spell {sid} comps: {string.Join(", ", names)}");
        }
    }

    public static bool TryGetRequiredComponentIds(int spellId, out uint[]? ids)
    {
        EnsureLoaded();
        return _spellComps.TryGetValue(spellId, out ids);
    }

    /// <summary>
    /// Predictive component gate. Returns false ONLY when a required non-taper
    /// component this spell needs is positively absent from
    /// <paramref name="inventoryNamesLower"/>. Unknown spell formula,
    /// unresolvable comp record, parse unavailable, or taper slots (account-
    /// scrambled → treated as "any taper present") never block — those defer
    /// to the empirical no-components safety net so a parse gap can't wedge
    /// combat.
    /// </summary>
    public static bool HasAllComponents(int spellId, HashSet<string> inventoryNamesLower)
    {
        EnsureLoaded();
        if (!_spellCompsLoaded) return true;
        if (!_spellComps.TryGetValue(spellId, out var ids) || ids.Length == 0) return true;

        bool taperChecked = false, hasAnyTaper = false;
        foreach (uint cid in ids)
        {
            if (!_cache.TryGetValue(cid, out var rec) || rec == null) continue;

            // Category 5 = Taper. The per-account scramble re-rolls which
            // taper; a presence check only needs "the char carries tapers".
            if (rec.Category == 5)
            {
                if (!taperChecked)
                {
                    foreach (var nm in inventoryNamesLower)
                        if (nm.Contains("taper")) { hasAnyTaper = true; break; }
                    taperChecked = true;
                }
                if (!hasAnyTaper) return false;
                continue;
            }

            if (string.IsNullOrEmpty(rec.Name)) continue;
            if (!inventoryNamesLower.Contains(rec.Name.ToLowerInvariant()))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Scarab-only predictive gate. Returns false ONLY when this spell's
    /// required SCARAB (SpellComponentCategory 0) is positively absent from
    /// <paramref name="inventoryNamesLower"/>. Every other category (herb,
    /// powder, potion, talisman, taper, pea) is ignored — ACE war casting
    /// does not require the dat's full historical recipe, and demanding it
    /// over-rejects every tier (verified pid 42260, 2026-05-17). Unknown
    /// formula / unresolved comp record / parse-unavailable never block
    /// (defer to the live cast rather than wedge selection).
    /// </summary>
    public static bool HasRequiredScarab(int spellId, HashSet<string> inventoryNamesLower)
    {
        EnsureLoaded();
        if (!_spellCompsLoaded) return true;
        if (!_spellComps.TryGetValue(spellId, out var ids) || ids.Length == 0) return true;

        foreach (uint cid in ids)
        {
            if (!_cache.TryGetValue(cid, out var rec) || rec == null) continue;
            if (rec.Category != 0) continue;              // only scarabs gate
            if (string.IsNullOrEmpty(rec.Name)) continue;
            if (!inventoryNamesLower.Contains(rec.Name.ToLowerInvariant()))
                return false;                             // required scarab absent
        }
        return true;
    }
}
