// PetStats.cs — Mag-Tools / UtilityBelt-style stats for one carried pet essence (the Pets tab's roster).
// Originally SilentKelpie's IltPetStats (ILT Hub); stayed in RynthAi with the universal Pets tab (2026-10-13).
//
//   Line 1: Name, Bond N, Lvl N, Craft N
//   Line 2: Sex, Growth, Mut N, Potency S (A act +P%), Mastery, DamageType
//   Line 3: [D, DR, C, CD, CR, CDR]  Uses cur/max  Breed Ready|n/m|Rec 4h|Juv|Neut
//
// Property ids are ACECustom (ILT) pet-device qualities, the same ones UtilityBelt's Pets
// tab reads (UB Lib\IltPetAceConstants.cs). Bond, level, craft and ratings are appraisal
// data: they read 0 until the essence has been ID'd, which HasId reports.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

/// <summary>Pet summon category (UB Pets "Type"). Persisted as int in <see cref="PetAssignment.Kind"/>.</summary>
internal enum PetKind { Combat = 0, Healing = 1, Cosmetic = 2 }

/// <summary>Roster sort modes (persisted as int in <see cref="PetTabState.RosterSort"/>).</summary>
internal enum PetSort { Priority = 0, Bond = 1, Potency = 2, BreedReady = 3, Level = 4, Uses = 5, Name = 6 }

/// <summary>One carried essence with its decoded stats. Built on the pump thread, read by the UI.</summary>
internal sealed class PetStats
{
    public int Id;
    public string Name = string.Empty;
    public uint Wcid;
    public PetKind Kind;
    /// <summary>Kind came from a user assignment rather than auto-detection.</summary>
    public bool KindAssigned;

    public bool HasId;
    public int Bond;
    public int Level;
    public int Craft;
    public string Sex = "?";
    public string Growth = string.Empty;
    public int Mutations;
    public int StoredPotency;
    public int ActivePotency;
    public int DamageBonusPercent;
    public string Mastery = string.Empty;
    public string DamageType = string.Empty;
    public int D, DR, C, CD, CR, CDR;
    public int UsesCur;
    public int UsesMax;
    public string Breed = "-";
    public bool BreedReady;

    /// <summary>Chosen for its kind: in the combat summon list, the heal pet, or the cosmetic pet.</summary>
    public bool Selected;
    /// <summary>Combat summon priority (list index), or int.MaxValue when not in the list.</summary>
    public int Priority = int.MaxValue;

    public string Line1 = string.Empty;
    public string Line2 = string.Empty;
    public string Line3 = string.Empty;
}

/// <summary>Reads and formats <see cref="PetStats"/>. Pump thread only (host reads).</summary>
internal static class PetStatsReader
{
    // ── ACECustom / ACE property ids (UB IltPetAceConstants, PetEssenceItemInfoFormatter) ──
    private const uint IntDamageType            = 45;
    private const uint IntMaxStructure          = 91;
    private const uint IntStructure             = 92;
    private const uint IntWorkmanship           = 105;
    private const uint IntSummoningMastery      = 362;
    private const uint IntUseLevelRequirement   = 364;
    private const uint IntUseRequiresLevel      = 369;
    private const uint IntGearDamage            = 370;
    private const uint IntGearDamageResist      = 371;
    private const uint IntGearCrit              = 372;
    private const uint IntGearCritResist        = 373;
    private const uint IntGearCritDamage        = 374;
    private const uint IntGearCritDamageResist  = 375;
    private const uint IntDamageRating          = 307;
    private const uint IntDamageResistRating    = 308;
    private const uint IntCritRating            = 313;
    private const uint IntCritDamageRating      = 314;
    private const uint IntCritResistRating      = 315;
    private const uint IntCritDamageResistRating = 316;
    private const uint IntBondLevel             = 9053;
    private const uint IntCapturedDamageType    = 9054;
    private const uint IntPotencyStored         = 9056;
    private const uint IntMaleBreedingCharges   = 9057;
    private const uint IntMutDamage             = 9070;
    private const uint IntMutDamageResist       = 9071;
    private const uint IntMutCrit               = 9072;
    private const uint IntMutVitality           = 9073;
    private const uint IntMutPotency            = 9074;
    private const uint IntMutationCount         = 9075;
    private const uint IntMaturityKills         = 9077;
    private const uint FloatNextBreedingTime    = 9056;
    private const uint BoolNeutered             = 9051;
    private const uint BoolMaleOverride         = 50053;
    private const uint BoolJuvenile             = 50054;

    // ACE PetPotencyMath defaults (UB PetEssencePotencyMath).
    private const int PotencyActiveCap   = 150;
    private const int PotencyBondDivisor = 10;
    private const double PotencyDamagePerLevelPercent = 2.0;
    /// <summary>ACE pet_breeding_male_max_charges default.</summary>
    private const int MaleMaxBreedingCharges = 10;

    /// <summary>Decodes every stat for <paramref name="wo"/> and fills the three display lines.</summary>
    public static PetStats Read(RynthCoreHost host, MiniRemoteInventory inv, WorldObject wo, long unixNow)
    {
        uint id = unchecked((uint)wo.Id);
        var s = new PetStats
        {
            Id      = wo.Id,
            Name    = wo.Name,
            Wcid    = inv.Wcid(wo),
            HasId   = inv.HasAppraisal(wo),
            UsesCur = inv.Int(wo, IntStructure),
            UsesMax = inv.Int(wo, IntMaxStructure),
        };

        s.Bond  = inv.Int(wo, IntBondLevel);
        s.Level = inv.Int(wo, IntUseRequiresLevel);
        if (s.Level <= 0) s.Level = inv.Int(wo, IntUseLevelRequirement);
        s.Craft = inv.Int(wo, IntWorkmanship);

        // Sex: explicit override when the server set one, else ACE PetDevice.IsMale's GUID hash.
        bool isMale = TryBool(host, id, BoolMaleOverride, out bool male) ? male : DeriveIsMale(wo.Id);
        bool neutered = TryBool(host, id, BoolNeutered, out bool n) && n;
        bool juvenile = TryBool(host, id, BoolJuvenile, out bool j) && j;
        s.Sex = (isMale ? "M" : "F") + (neutered ? "n" : juvenile ? "j" : string.Empty);

        // Growth only exists on bred essences (9077 present, even when 0).
        if (TryInt(host, id, IntMaturityKills, out int kills))
            s.Growth = !juvenile ? "Adult" : kills > 0 ? "Juv " + kills.ToString(CultureInfo.InvariantCulture) : "Juv";

        int total = inv.Int(wo, IntMutationCount);
        s.Mutations = total > 0 ? total
            : inv.Int(wo, IntMutDamage) + inv.Int(wo, IntMutDamageResist) + inv.Int(wo, IntMutCrit)
              + inv.Int(wo, IntMutVitality) + inv.Int(wo, IntMutPotency);

        s.StoredPotency      = Math.Max(0, inv.Int(wo, IntPotencyStored));
        s.ActivePotency      = ActivePotency(s.StoredPotency, s.Bond);
        s.DamageBonusPercent = (int)Math.Round(s.ActivePotency * PotencyDamagePerLevelPercent);

        s.Mastery = inv.Int(wo, IntSummoningMastery) switch
        {
            1 => "Primalist",
            2 => "Necromancer",
            3 => "Naturalist",
            _ => string.Empty,
        };

        int dmg = inv.Int(wo, IntCapturedDamageType);
        if (dmg <= 0) dmg = inv.Int(wo, IntDamageType);
        s.DamageType = DamageTypeName(dmg);

        // Gear ratings first (what Mag ItemInfo shows), else the base rating ids.
        s.D   = inv.Int(wo, IntGearDamage);
        s.DR  = inv.Int(wo, IntGearDamageResist);
        s.C   = inv.Int(wo, IntGearCrit);
        s.CR  = inv.Int(wo, IntGearCritResist);
        s.CD  = inv.Int(wo, IntGearCritDamage);
        s.CDR = inv.Int(wo, IntGearCritDamageResist);
        if (s.D <= 0 && s.DR <= 0 && s.C <= 0 && s.CR <= 0 && s.CD <= 0 && s.CDR <= 0)
        {
            s.D   = inv.Int(wo, IntDamageRating);
            s.DR  = inv.Int(wo, IntDamageResistRating);
            s.C   = inv.Int(wo, IntCritRating);
            s.CD  = inv.Int(wo, IntCritDamageRating);
            s.CR  = inv.Int(wo, IntCritResistRating);
            s.CDR = inv.Int(wo, IntCritDamageResistRating);
        }

        int charges = inv.Int(wo, IntMaleBreedingCharges);
        double nextBreed = TryDouble(host, id, FloatNextBreedingTime, out double nb) ? nb : 0;
        (s.Breed, s.BreedReady) = FormatBreed(isMale, neutered, juvenile, charges, nextBreed, unixNow);

        FormatLines(s);
        return s;
    }

    /// <summary>Builds Line1..Line3 from the decoded fields (Kind/Selected don't affect the text).</summary>
    public static void FormatLines(PetStats s)
    {
        var l1 = new StringBuilder(s.Name);
        if (s.Bond > 0)  l1.Append(", Bond ").Append(s.Bond.ToString(CultureInfo.InvariantCulture));
        if (s.Level > 0) l1.Append(", Lvl ").Append(s.Level.ToString(CultureInfo.InvariantCulture));
        if (s.Craft > 0) l1.Append(", Craft ").Append(s.Craft.ToString(CultureInfo.InvariantCulture));
        s.Line1 = l1.ToString();

        var l2 = new StringBuilder(s.Sex);
        if (s.Growth.Length > 0) l2.Append(", Growth ").Append(s.Growth);
        l2.Append(", Mut ").Append(s.Mutations.ToString(CultureInfo.InvariantCulture));
        l2.Append(", Potency ").Append(s.StoredPotency.ToString(CultureInfo.InvariantCulture));
        if (s.StoredPotency > 0)
            l2.Append(" (").Append(s.ActivePotency.ToString(CultureInfo.InvariantCulture))
              .Append(" act +").Append(s.DamageBonusPercent.ToString(CultureInfo.InvariantCulture)).Append("%)");
        if (s.Mastery.Length > 0)    l2.Append(", ").Append(s.Mastery);
        if (s.DamageType.Length > 0) l2.Append(", ").Append(s.DamageType);
        s.Line2 = l2.ToString();

        var parts = new List<string>(4);
        string ratings = RatingsBracket(s);
        if (ratings.Length > 0) parts.Add(ratings);
        if (s.UsesMax > 0) parts.Add($"Uses {s.UsesCur}/{s.UsesMax}");
        else if (s.UsesCur > 0) parts.Add($"Uses {s.UsesCur}");
        if (s.Breed != "-") parts.Add("Breed " + s.Breed);
        if (!s.HasId) parts.Add("ID pending");
        s.Line3 = parts.Count > 0 ? string.Join("  ", parts) : "-";
    }

    /// <summary>Orders rows for display; ties keep a stable name order.</summary>
    public static PetStats[] Sort(IEnumerable<PetStats> rows, PetSort mode)
    {
        var list = new List<PetStats>(rows);
        Comparison<PetStats> byName = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        Comparison<PetStats> cmp = mode switch
        {
            // Selected first, then combat priority, then name.
            PetSort.Priority   => (a, b) => Chain(b.Selected.CompareTo(a.Selected), a.Priority.CompareTo(b.Priority), byName(a, b)),
            PetSort.Bond       => (a, b) => Chain(b.Bond.CompareTo(a.Bond), byName(a, b)),
            PetSort.Potency    => (a, b) => Chain(b.StoredPotency.CompareTo(a.StoredPotency), b.ActivePotency.CompareTo(a.ActivePotency), byName(a, b)),
            PetSort.BreedReady => (a, b) => Chain(b.BreedReady.CompareTo(a.BreedReady), b.Bond.CompareTo(a.Bond), byName(a, b)),
            PetSort.Level      => (a, b) => Chain(b.Level.CompareTo(a.Level), byName(a, b)),
            PetSort.Uses       => (a, b) => Chain(b.UsesCur.CompareTo(a.UsesCur), byName(a, b)),
            _                     => byName,
        };
        list.Sort(cmp);
        return list.ToArray();
    }

    private static int Chain(params int[] results)
    {
        foreach (int r in results) if (r != 0) return r;
        return 0;
    }

    /// <summary>ACE PetPotencyMath: min(stored, ceil(bond / 10)), at least 1 with any bond, capped at 150.</summary>
    private static int ActivePotency(int stored, int bond)
    {
        if (stored <= 0 || bond <= 0) return 0;
        int cap = Math.Max(1, (bond + PotencyBondDivisor - 1) / PotencyBondDivisor);
        return Math.Min(stored, Math.Min(cap, PotencyActiveCap));
    }

    /// <summary>ACE PetDevice.IsMale when no override is set (murmur3 fmix32 on the object GUID).</summary>
    private static bool DeriveIsMale(int objectId)
    {
        uint h = unchecked((uint)objectId);
        h ^= h >> 16;
        h = unchecked(h * 0x7feb352d);
        h ^= h >> 15;
        h = unchecked(h * 0x846ca68b);
        h ^= h >> 16;
        return (h & 1) == 0;
    }

    /// <summary>Breeding cell: Neut / Juv / male charges n/max / female "Rec Nh" or "Ready".</summary>
    private static (string Text, bool Ready) FormatBreed(bool isMale, bool neutered, bool juvenile,
        int maleCharges, double nextBreedingUnix, long unixNow)
    {
        if (neutered) return ("Neut", false);
        if (juvenile) return ("Juv", false);
        if (isMale)
        {
            int ch = Math.Max(0, maleCharges);
            return ($"{ch}/{MaleMaxBreedingCharges}", ch > 0);
        }
        if (nextBreedingUnix > 0 && unixNow < (long)nextBreedingUnix)
        {
            long remain = (long)nextBreedingUnix - unixNow;
            int hours = (int)(remain / 3600);
            return (hours > 0 ? $"Rec {hours}h" : $"Rec {(int)(remain % 3600 / 60)}m", false);
        }
        return ("Ready", true);
    }

    private static string RatingsBracket(PetStats s)
    {
        var sb = new StringBuilder();
        void Add(string label, int v)
        {
            if (v <= 0) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(label).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture));
        }
        Add("D", s.D); Add("DR", s.DR); Add("C", s.C); Add("CD", s.CD); Add("CR", s.CR); Add("CDR", s.CDR);
        return sb.Length > 0 ? "[" + sb + "]" : string.Empty;
    }

    /// <summary>Single AC DamageType flag → name (combined flags return empty).</summary>
    private static string DamageTypeName(int flags) => flags switch
    {
        1    => "Slash",
        2    => "Pierce",
        4    => "Bludgeon",
        8    => "Cold",
        16   => "Fire",
        32   => "Acid",
        64   => "Lightning",
        1024 => "Nether",
        _    => string.Empty,
    };

    private static bool TryBool(RynthCoreHost host, uint id, uint stype, out bool v)
    {
        v = false;
        return host.HasGetObjectBoolProperty && host.TryGetObjectBoolProperty(id, stype, out v);
    }

    private static bool TryInt(RynthCoreHost host, uint id, uint stype, out int v)
    {
        v = 0;
        return host.HasGetObjectIntProperty && host.TryGetObjectIntProperty(id, stype, out v);
    }

    private static bool TryDouble(RynthCoreHost host, uint id, uint stype, out double v)
    {
        v = 0;
        return host.HasGetObjectDoubleProperty && host.TryGetObjectDoubleProperty(id, stype, out v);
    }
}
