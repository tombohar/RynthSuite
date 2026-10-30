using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Loot.T11;

/// <summary>One "Wield requires: N &lt;counter&gt;" gate.</summary>
public readonly record struct T11WieldGate(T11Counter Counter, int Amount);

/// <summary>One line of the appraisal's "Modifiers:" section.</summary>
public sealed class T11Modifier
{
    /// <summary>Catalog key (T11Catalog.Modifiers), or 0 for a name the catalog doesn't know.</summary>
    public int Key { get; init; }
    /// <summary>The modifier name as printed.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>The rolled value (the first number after the name; 0 for value-less specials).</summary>
    public int Value { get; init; }
    /// <summary>Low end of the "[min-max]" roll band, or null when the line has none.</summary>
    public int? Min { get; init; }
    /// <summary>High end of the "[min-max]" roll band, or null when the line has none.</summary>
    public int? Max { get; init; }
    /// <summary>Where the value landed in its band, 0-100, or -1 without a usable band.</summary>
    public int RollPercent { get; init; } = -1;
    /// <summary>The retail Gear* rating key the modifier sets, or 0.</summary>
    public int RatingKey { get; init; }
    /// <summary>True for a one-per-slot special (Fortify Vitals, Battle Mending, ...).</summary>
    public bool IsSlotSpecial { get; init; }
    /// <summary>"(Built-in)": the line uses no property slot (Always Rolled resists, slot specials).</summary>
    public bool BuiltIn { get; init; }
    /// <summary>"(Locked)": a Gear Essence locked this line, so bag rerolls leave it alone.</summary>
    public bool Locked { get; init; }
    /// <summary>"(+N tinkered)": what a tinker adds on top of <see cref="Value"/>, 0 for none.</summary>
    public int Tinkered { get; init; }
}

/// <summary>
/// T11 properties the server sends as real appraisal values (ACECustom marked them
/// AssessmentProperty on 2026-09-29). 0 / -1 = not sent (older server, or not that kind of item).
/// </summary>
/// <param name="ZcTier">PropertyInt ZcTier (50109): the loot tier a Zone Control piece was stamped at.</param>
/// <param name="WeaponTier">PropertyInt WeaponAugScaleTier (9061): the loot tier a T11+ weapon was stamped at.</param>
/// <param name="WeaponQuality">PropertyInt WeaponAugScaleQuality (9060): the weapon's 0-1000 quality roll, -1 when absent.</param>
public readonly record struct T11ServerProps(int ZcTier, int WeaponTier, int WeaponQuality)
{
    /// <summary>Nothing sent. Use this, not <c>default</c>: a 0 quality is a real (F-) roll.</summary>
    public static readonly T11ServerProps None = new(0, 0, -1);

    /// <summary>The stamped loot tier (ZcTier first, then the weapon tier), or 0 when neither was sent.</summary>
    public int Tier => ZcTier > 0 ? ZcTier : WeaponTier > 0 ? WeaponTier : 0;
}

/// <summary>
/// What the client can learn about a T11 (tier 11+) ACECustom drop. Most T11 state (the
/// Zone Control record, the item-aug wield gate, grades, slot counts) only reaches the client
/// as description text, so this is parsed from the name, LongDesc (key 16) and Use (key 14).
/// The loot tier and the weapon quality roll are also sent as real properties
/// (<see cref="T11ServerProps"/>), which win over the text estimates when present. See ACECustom
/// AppraiseInfo (BuildProperties, BuildWeapon, PromoteZoneModifierLines) for the formats.
/// </summary>
public sealed class T11ItemInfo
{
    /// <summary>A non-T11 item: every field at its default.</summary>
    public static readonly T11ItemInfo None = new();

    /// <summary>True when the item is T11 gear (name prefix, or T11-only text in its description).</summary>
    public bool IsT11 { get; private set; }
    /// <summary>True when the name starts with "T11 - ".</summary>
    public bool HasNamePrefix { get; private set; }
    /// <summary>Weapon sub-grade label ("B+"), or null.</summary>
    public string? Grade { get; private set; }
    /// <summary>Sub-grade rank, S = 16 ... F- = 1, 0 = no grade.</summary>
    public int GradeRank { get; private set; }
    /// <summary>"N% of max damage" from the grade line, or -1.</summary>
    public int DamagePercent { get; private set; } = -1;
    /// <summary>Every wield gate, at most one per counter.</summary>
    public IReadOnlyList<T11WieldGate> WieldGates => _gates;
    /// <summary>The Zone Control modifier lines, in appraisal order.</summary>
    public IReadOnlyList<T11Modifier> Modifiers => _modifiers;
    /// <summary>"Cast on Strike" lines: spell name and proc chance percent.</summary>
    public IReadOnlyList<(string Name, double ChancePercent)> Procs => _procs;
    /// <summary>True when the appraisal said "Zone Locked (Power Reduced)" (depends on where the examiner stood).</summary>
    public bool ZoneLocked { get; private set; }
    /// <summary>True when the item had appraisal text (false for a T11 name seen before its ID).</summary>
    public bool HasText { get; private set; }
    /// <summary>Gear Grade label ("B+") of armor / jewelry / clothing / cloaks, or null.</summary>
    public string? GearGrade { get; private set; }
    /// <summary>Gear Grade rank, S = 16 ... F- = 1, 0 = no grade.</summary>
    public int GearGradeRank { get; private set; }
    /// <summary>How many rolled lines the Gear Grade averages, 0 when not shown.</summary>
    public int GearGradeLines { get; private set; }
    /// <summary>"Properties: N of M": property slots in use, -1 when the line isn't shown.</summary>
    public int PropertySlots { get; private set; } = -1;
    /// <summary>"Properties: N of M": the tier's slot limit, 0 when there is none or the line isn't shown.</summary>
    public int PropertySlotCap { get; private set; }
    /// <summary>"Tainted: bags no longer work on this item".</summary>
    public bool Tainted { get; private set; }
    /// <summary>The server-sent properties this item was parsed with.</summary>
    public T11ServerProps Server { get; private set; } = T11ServerProps.None;
    /// <summary>The weapon's 0-1000 quality roll (WeaponAugScaleQuality), -1 when not sent.</summary>
    public int WeaponQuality => Server.WeaponQuality;
    /// <summary>True when <see cref="Tier"/> is the server's stamped tier rather than an estimate.</summary>
    public bool TierIsExact => IsT11 && Server.Tier > 0;
    /// <summary>The loot tier: the server's stamped tier when sent, else <see cref="EstimatedTier"/>.</summary>
    public int Tier => TierIsExact ? Server.Tier : EstimatedTier;
    /// <summary>Free property slots (cap minus used), 0 without a known cap.</summary>
    public int FreePropertySlots => PropertySlotCap > 0 && PropertySlots >= 0 ? Math.Max(0, PropertySlotCap - PropertySlots) : 0;

    private readonly List<T11WieldGate> _gates = new();
    private readonly List<T11Modifier> _modifiers = new();
    private readonly List<(string, double)> _procs = new();

    /// <summary>The item-augmentation gate, or 0 when there is none.</summary>
    public int WieldItemAugs => GateAmount(T11Counter.ItemAugmentations);

    /// <summary>The amount required of <paramref name="counter"/>, or 0 when it isn't gated.</summary>
    public int GateAmount(T11Counter counter)
    {
        foreach (var g in _gates)
            if (g.Counter == counter) return g.Amount;
        return 0;
    }

    /// <summary>
    /// The estimated loot tier (11-25), or 0 for non-T11 items. T16 and up come from the
    /// Triune Weave gate; an item without one reads at most 15.
    /// </summary>
    public int EstimatedTier => T11Catalog.EstimateTier(WieldItemAugs, GateAmount(T11Counter.TriuneWeave), IsT11);

    /// <summary>Sum of modifier values that land on the TotalRatings keys.</summary>
    public int ModifierRatingTotal
    {
        get
        {
            int total = 0;
            foreach (var m in _modifiers)
                if (Array.IndexOf(T11Catalog.TotalRatingKeys, m.RatingKey) >= 0) total += m.Value;
            return total;
        }
    }

    /// <summary>Sum of modifier values that set <paramref name="ratingKey"/>, or 0.</summary>
    public int RatingFromModifiers(int ratingKey)
    {
        int total = 0;
        foreach (var m in _modifiers)
            if (m.RatingKey == ratingKey) total += m.Value;
        return total;
    }

    /// <summary>The value of the modifier with catalog key <paramref name="key"/>, or 0 when absent.</summary>
    public int ModifierValue(int key)
    {
        foreach (var m in _modifiers)
            if (m.Key == key) return m.Value;
        return 0;
    }

    /// <summary>Average roll position (0-100) of the banded modifiers, or 0 without any.</summary>
    public int AverageRollPercent
    {
        get
        {
            int sum = 0, n = 0;
            foreach (var m in _modifiers)
                if (m.RollPercent >= 0) { sum += m.RollPercent; n++; }
            return n == 0 ? 0 : (int)Math.Round(sum / (double)n, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>Best roll position (0-100) of the banded modifiers, or 0 without any.</summary>
    public int BestRollPercent
    {
        get
        {
            int best = 0;
            foreach (var m in _modifiers)
                if (m.RollPercent > best) best = m.RollPercent;
            return best;
        }
    }

    /// <summary>True when any modifier is a slot special.</summary>
    public bool HasSlotSpecial
    {
        get
        {
            foreach (var m in _modifiers)
                if (m.IsSlotSpecial) return true;
            return false;
        }
    }

    // ── Parsing ─────────────────────────────────────────────────────────────

    private const RegexOptions Opt = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // "- Weapon Grade: B+ (86% of max damage)" / "- Weapon Grade: S"
    private static readonly Regex GradeLine = new(
        @"^-?\s*Weapon Grade:\s*(?<g>S|[A-DF][+-]?)(?=\s|\(|$)(?:\s*\((?<pct>\d+)% of max damage\))?", Opt);

    // "- Wield requires: 2,000 Item Augmentations" (weapons) / "Wield requires: 2,000 Item Augmentations" (armor).
    // ACE prints N0, so the group separator follows the server's culture: keep digits only.
    private static readonly Regex WieldLine = new(
        @"^-?\s*Wield requires:\s*(?<n>\d[\d,.\s']*?)\s+(?<what>[A-Za-z' ]+?)\s*$", Opt);

    // "- Cast on Strike: Force Arc (13% proc chance)"
    private static readonly Regex ProcLine = new(
        @"^-\s*Cast on Strike:\s*(?<name>.+?)\s*\((?<pct>\d+(?:\.\d+)?)% proc chance\)\s*$", Opt);

    // A modifier's value: the first signed whole number after the name.
    private static readonly Regex FirstNumber = new(@"[+-]?\d+", RegexOptions.CultureInvariant);

    // "- Gear Grade: B+ (average of 4 rolled lines)": armor / jewelry / clothing / cloaks, leads the Modifiers block.
    private static readonly Regex GearGradeLine = new(
        @"^-?\s*Gear Grade:\s*(?<g>S|[A-DF][+-]?)(?=\s|\(|$)(?:\s*\(average of (?<n>\d+) rolled lines?\))?", Opt);

    // "- Properties: 3 of 5" (or "- Properties: 3" when the tier has no limit): Modifiers block or Property Details.
    private static readonly Regex PropertiesLine = new(@"^-?\s*Properties:\s*(?<n>\d+)(?:\s+of\s+(?<cap>\d+))?\s*$", Opt);

    // "- Tainted: bags no longer work on this item".
    private static readonly Regex TaintedLine = new(@"^-?\s*Tainted:", Opt);

    // The roll band of a modifier line: "[14-69]". Not anchored: markers and the jewelry proc follow it.
    private static readonly Regex Band = new(@"\[(?<min>\d+)\s*-\s*(?<max>\d+)\]", RegexOptions.CultureInvariant);

    // Markers the server appends to a modifier line, in this order after the value and band.
    private static readonly Regex TinkeredMark = new(@"\s*\((?<n>[+-]\d+) tinkered\)", Opt);
    private static readonly Regex BuiltInMark = new(@"\s*\(Built-in\)", Opt);
    private static readonly Regex LockedMark = new(@"\s*\(Locked\)", Opt);
    private static readonly Regex OffHereMark = new(@"\s*\(off here\)", Opt);
    // Jewelry Cast on Strike (key 54): "Cast on Strike 75% power [50-100] - Force Arc, 13% per hit".
    private static readonly Regex JewelProcTail = new(@"\s+-\s+(?<name>[^,\[\]]+?),\s*(?<pct>\d+(?:\.\d+)?)% per hit", Opt);

    // An uncatalogued modifier from a newer server: "Some Name +12 [5-20]" or "Modifier 60 +3 [1-5]".
    private static readonly Regex GenericModifier = new(@"^(?<name>.+?)\s+(?<val>[+-]?\d+)", RegexOptions.CultureInvariant);
    // Checked before GenericModifier, whose lazy name would stop at "Modifier" and read the key as the value.
    private static readonly Regex UnknownKeyName = new(@"^(?<name>Modifier (?<key>\d+))(?=\s|$)", RegexOptions.CultureInvariant);

    private const string ModifiersHeader = "Modifiers:";
    private const string ZoneLockedText = "Zone Locked (Power Reduced)";

    /// <summary>
    /// Parses an item. <paramref name="name"/>, <paramref name="longDesc"/> and
    /// <paramref name="use"/> may be null or empty (an item not yet appraised has no text;
    /// the name prefix alone still marks it T11). <paramref name="server"/> carries the tier and
    /// quality properties the appraisal sent (null = none). Never throws.
    /// </summary>
    public static T11ItemInfo Parse(string? name, string? longDesc, string? use, T11ServerProps? server = null)
    {
        T11ServerProps props = server ?? T11ServerProps.None;
        bool prefix = !string.IsNullOrEmpty(name) && name.StartsWith(T11Catalog.NamePrefix, StringComparison.Ordinal);
        bool serverT11 = props.Tier >= T11Catalog.MinTier;
        if (!prefix && !serverT11 && string.IsNullOrEmpty(longDesc) && string.IsNullOrEmpty(use)) return None;

        var info = new T11ItemInfo
        {
            HasNamePrefix = prefix,
            HasText = !string.IsNullOrEmpty(longDesc) || !string.IsNullOrEmpty(use),
            Server = props,
        };
        try
        {
            info.ParseText(longDesc);
            info.ParseText(use);
        }
        catch
        {
            // Malformed text never breaks loot evaluation; whatever parsed so far stands.
        }

        // Grades, gates, slot counts, banded modifiers and a stamped tier of 11+ only appear on
        // T11 gear; Cast on Strike, Tainted and the zone-lock line alone do not mark an item.
        info.IsT11 = prefix || serverT11 || info.GradeRank > 0 || info.GearGradeRank > 0 || info.PropertySlots >= 0
            || info._gates.Count > 0 || info._modifiers.Count > 0;
        return info.IsT11 ? info : None;
    }

    /// <summary>
    /// Reads a Gear Grade / Properties / Tainted line (with or without the leading dash).
    /// Returns false for any other line.
    /// </summary>
    private bool TryParseItemLine(string line)
    {
        Match m = GearGradeLine.Match(line);
        if (m.Success)
        {
            if (GearGradeRank == 0)
            {
                GearGrade = m.Groups["g"].Value.ToUpperInvariant();
                GearGradeRank = T11Catalog.GradeRank(GearGrade);
                if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    GearGradeLines = n;
            }
            return true;
        }

        m = PropertiesLine.Match(line);
        if (m.Success)
        {
            if (PropertySlots < 0 && int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int used))
            {
                PropertySlots = used;
                if (m.Groups["cap"].Success && int.TryParse(m.Groups["cap"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap))
                    PropertySlotCap = cap;
            }
            return true;
        }

        if (TaintedLine.IsMatch(line))
        {
            Tainted = true;
            return true;
        }
        return false;
    }

    private void ParseText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (line.Contains(ZoneLockedText, StringComparison.OrdinalIgnoreCase)) { ZoneLocked = true; continue; }

            if (string.Equals(line, ModifiersHeader, StringComparison.OrdinalIgnoreCase))
            {
                i = ParseModifierBlock(lines, i + 1) - 1;
                continue;
            }

            if (TryParseItemLine(line)) continue;

            Match m = GradeLine.Match(line);
            if (m.Success && GradeRank == 0)
            {
                Grade = m.Groups["g"].Value.ToUpperInvariant();
                GradeRank = T11Catalog.GradeRank(Grade);
                if (m.Groups["pct"].Success && int.TryParse(m.Groups["pct"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
                    DamagePercent = pct;
                continue;
            }

            m = WieldLine.Match(line);
            if (m.Success && T11Catalog.CounterByName(m.Groups["what"].Value) is T11Counter counter)
            {
                int amount = DigitsOnly(m.Groups["n"].Value);
                if (amount > 0) AddGate(counter, amount);
                continue;
            }

            m = ProcLine.Match(line);
            if (m.Success && double.TryParse(m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double chance))
                _procs.Add((m.Groups["name"].Value, chance));
        }
    }

    /// <summary>Reads "- ..." lines after a "Modifiers:" header; returns the index of the first line not consumed.</summary>
    private int ParseModifierBlock(string[] lines, int start)
    {
        int i = start;
        for (; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || !line.StartsWith("-", StringComparison.Ordinal)) break;
            string body = line.Substring(1).Trim();
            if (body.Length == 0) continue;
            if (body.Contains(ZoneLockedText, StringComparison.OrdinalIgnoreCase)) { ZoneLocked = true; continue; }
            // Gear Grade, Properties and Tainted lead the block but describe the item, not a modifier.
            if (TryParseItemLine(body)) continue;
            _modifiers.Add(ParseModifier(body, out var proc));
            if (proc is { } p) _procs.Add(p);
        }
        return i;
    }

    /// <summary>
    /// One modifier line. <paramref name="proc"/> is the jewelry Cast on Strike's spell and
    /// chance when the line carries one, else null.
    /// </summary>
    private static T11Modifier ParseModifier(string body, out (string Name, double ChancePercent)? proc)
    {
        proc = null;
        // Peel the server's markers off first so the name, value and band read as on a plain line.
        int tinkered = 0;
        Match tm = TinkeredMark.Match(body);
        if (tm.Success)
        {
            int.TryParse(tm.Groups["n"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out tinkered);
            body = body.Remove(tm.Index, tm.Length);
        }
        bool builtIn = Strip(ref body, BuiltInMark);
        bool locked = Strip(ref body, LockedMark);
        Strip(ref body, OffHereMark);
        Match jp = JewelProcTail.Match(body);
        if (jp.Success)
        {
            if (double.TryParse(jp.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double chance))
                proc = (jp.Groups["name"].Value.Trim(), chance);
            body = body.Remove(jp.Index, jp.Length);
        }
        body = body.Trim();

        T11ModifierDef? def = T11Catalog.MatchModifier(body);
        string name;
        string rest;
        int key = 0;
        if (def != null)
        {
            name = def.Name;
            key = def.Key;
            rest = body.Substring(def.Name.Length);
        }
        else if (UnknownKeyName.Match(body) is { Success: true } k
                 && int.TryParse(k.Groups["key"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedKey))
        {
            key = parsedKey;
            name = k.Groups["name"].Value;
            rest = body.Substring(name.Length);
            def = T11Catalog.ModifierByKey(parsedKey);
            if (def != null) name = def.Name;
        }
        else
        {
            Match g = GenericModifier.Match(body);
            name = g.Success ? g.Groups["name"].Value.Trim() : body;
            rest = g.Success ? body.Substring(g.Groups["name"].Length) : string.Empty;
        }

        int value = 0;
        Match num = FirstNumber.Match(rest);
        if (num.Success) int.TryParse(num.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        int? min = null, max = null;
        int roll = -1;
        Match band = Band.Match(rest);
        if (band.Success
            && int.TryParse(band.Groups["min"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
            && int.TryParse(band.Groups["max"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi))
        {
            min = lo;
            max = hi;
            if (hi > lo) roll = Math.Clamp((int)Math.Round((value - lo) * 100.0 / (hi - lo), MidpointRounding.AwayFromZero), 0, 100);
        }

        return new T11Modifier
        {
            Key = key,
            Name = name,
            Value = value,
            Min = min,
            Max = max,
            RollPercent = roll,
            RatingKey = def?.RatingKey ?? 0,
            IsSlotSpecial = def?.SlotSpecial ?? false,
            BuiltIn = builtIn,
            Locked = locked,
            Tinkered = tinkered,
        };
    }

    /// <summary>Removes the first match of <paramref name="mark"/>; true when there was one.</summary>
    private static bool Strip(ref string body, Regex mark)
    {
        Match m = mark.Match(body);
        if (!m.Success) return false;
        body = body.Remove(m.Index, m.Length);
        return true;
    }

    private void AddGate(T11Counter counter, int amount)
    {
        for (int i = 0; i < _gates.Count; i++)
        {
            if (_gates[i].Counter != counter) continue;
            if (amount > _gates[i].Amount) _gates[i] = new T11WieldGate(counter, amount);
            return;
        }
        _gates.Add(new T11WieldGate(counter, amount));
    }

    private static int DigitsOnly(string s)
    {
        long v = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') continue;
            v = v * 10 + (c - '0');
            if (v > int.MaxValue) return int.MaxValue;
        }
        return (int)v;
    }
}
