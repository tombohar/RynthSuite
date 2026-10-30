// ============================================================================
//  RynthDefective - Corpses/CorpseBook.cs
//  Where a character's corpses are (Tom 2026-10-23: "a small panel somewhere
//  that tells you where all your corpses are"). Pure: the plugin feeds it what
//  it sees (RynthDefectivePlugin.Corpses.cs), tests feed it directly.
//
//  What tells it:
//    the death      health 0: where the player stands is where the corpse lies
//                   (ACE puts it at the player's position).
//    the server     "Your corpse is located at (12.3N, 45.6E)." (ACE, outdoor deaths
//                   that dropped something) confirms it; "You have retained all your
//                   items. You do not need to recover your corpse!" means there is
//                   nothing to go back for, so that death is dropped. Either line
//                   may come before or after the health reaches 0.
//    the corpse     an object named "Corpse of <name>" in view: its id and exact
//                   place, matched to a death nearby or added as a corpse found.
//    it's gone      it left view (the server deletes it from the client) and
//                   VanishMeansGone says so: you had opened it (an emptied corpse
//                   rots 15 s later), or it was due to rot, or it went from right
//                   beside you. Otherwise it only left view (in a dungeon, around a
//                   corner: ACE drops what you can't see after about 25 s) and stays.
//    not there      you stand where it should be and it isn't (10 s, not in the
//                   first 2 minutes after the death, while the corpse appears).
//
//  How long one lasts (ACE's Corpse.RecalculateDecayTime): a player's corpse rots
//  after the player's level times 5 minutes, at least an hour; an emptied one 15
//  seconds after. Other servers may differ, so the time is an estimate and a
//  corpse is only dropped 10 minutes after it.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthDefective.Corpses;

/// <summary>One corpse: where, when the character died (or the corpse was found), when it rots.</summary>
internal sealed class CorpseRecord
{
    /// <summary>When the character died; MinValue for a corpse found in the world with no death seen.</summary>
    public DateTime DiedUtc;
    /// <summary>When it should rot (an estimate); MinValue when unknown.</summary>
    public DateTime RotsUtc;
    /// <summary>The landcell and the position in it; Cell 0 when only the server's map coordinates are known.</summary>
    public uint Cell;
    public float X, Y, Z;
    /// <summary>The server's map coordinates (outdoor deaths); otherwise worked out from Cell.</summary>
    public double Ns, Ew;
    public bool ServerCoords;
    /// <summary>The corpse object, once seen (0 before).</summary>
    public uint ObjectId;
    public DateTime SeenUtc;
    /// <summary>When the player last opened it (not saved: only the minutes after matter).</summary>
    public DateTime OpenedUtc;
    /// <summary>Since when the player has stood at its place without it there (not saved).</summary>
    public DateTime AbsentSinceUtc;

    public bool Indoors => Cell != 0 && (Cell & 0xFFFF) >= 0x100;
    public uint Landblock => Cell >> 16;

    /// <summary>The map coordinates: the server's, else from the landcell (meaningless in a dungeon).</summary>
    public bool TryCoords(out double ns, out double ew)
    {
        if (ServerCoords) { ns = Ns; ew = Ew; return true; }
        return CorpseBook.MapCoords(Cell, X, Y, out ns, out ew);
    }
}

internal sealed class CorpseBook
{
    public const int MaxCorpses = 20;
    /// <summary>A death and the server's lines about it arrive within this of each other.</summary>
    public static readonly TimeSpan DeathWindow = TimeSpan.FromMinutes(2);
    /// <summary>A corpse is dropped this long after its estimated rot time.</summary>
    public static readonly TimeSpan RotGrace = TimeSpan.FromMinutes(10);
    /// <summary>A corpse with no rot estimate is dropped this long after it was last known.</summary>
    public static readonly TimeSpan UnknownKeep = TimeSpan.FromHours(48);

    private readonly List<CorpseRecord> _list = new();
    private DateTime _nothingDroppedUtc = DateTime.MinValue;

    public IReadOnlyList<CorpseRecord> Corpses => _list;

    /// <summary>Bumped on every change (the plugin saves and redraws on it).</summary>
    public int Revision { get; private set; }

    public void Load(IEnumerable<CorpseRecord> records)
    {
        _list.Clear();
        _list.AddRange(records);
        Trim();
        Revision++;
    }

    /// <summary>ACE's rot time for a player's corpse: level x 5 minutes, at least an hour (level 0: unknown).</summary>
    public static DateTime RotTime(DateTime diedUtc, int level) =>
        level <= 0 ? DateTime.MinValue : diedUtc + TimeSpan.FromSeconds(Math.Max(3600, level * 300));

    /// <summary>
    /// The character's health reached 0 at this place. The server's corpse line may already have come
    /// (then this fills in the exact place), or "nothing to recover" (then there's no corpse to note).
    /// </summary>
    public void Died(DateTime now, uint cell, float x, float y, float z, int level)
    {
        if (now - _nothingDroppedUtc < DeathWindow) return;
        CorpseRecord? recent = Recent(now);
        if (recent != null && recent.Cell == 0)
        {
            recent.Cell = cell; recent.X = x; recent.Y = y; recent.Z = z;
            Changed();
            return;
        }
        Add(new CorpseRecord { DiedUtc = now, RotsUtc = RotTime(now, level), Cell = cell, X = x, Y = y, Z = z });
    }

    /// <summary>"Your corpse is located at (ns, ew)." The latest death, or a death the health didn't show.</summary>
    public void ServerLine(DateTime now, double ns, double ew, int level)
    {
        CorpseRecord? recent = Recent(now);
        if (recent != null)
        {
            recent.Ns = ns; recent.Ew = ew; recent.ServerCoords = true;
            Changed();
            return;
        }
        Add(new CorpseRecord { DiedUtc = now, RotsUtc = RotTime(now, level), Ns = ns, Ew = ew, ServerCoords = true });
    }

    /// <summary>"You do not need to recover your corpse": that death left nothing behind.</summary>
    public void NothingDropped(DateTime now)
    {
        _nothingDroppedUtc = now;
        CorpseRecord? recent = Recent(now);
        if (recent != null && recent.ObjectId == 0)
        {
            _list.Remove(recent);
            Changed();
        }
    }

    /// <summary>
    /// "Corpse of &lt;name&gt;" is in view: the corpse it is (by id, else the nearest death in the same
    /// place), or one found. Returns the record.
    /// </summary>
    public CorpseRecord Seen(DateTime now, uint objectId, uint cell, float x, float y, float z)
    {
        CorpseRecord? hit = null;
        foreach (CorpseRecord c in _list)
            if (c.ObjectId == objectId) { hit = c; break; }
        if (hit == null)
        {
            double best = double.MaxValue;
            foreach (CorpseRecord c in _list)
            {
                if (c.ObjectId != 0) continue;
                double d = Distance(c, cell, x, y, z);
                if (d <= (c.Cell != 0 ? MatchDistance : MatchDistanceCoords) && d < best) { best = d; hit = c; }
            }
        }
        if (hit == null)
        {
            hit = new CorpseRecord
            {
                DiedUtc = DateTime.MinValue, RotsUtc = DateTime.MinValue,
                ObjectId = objectId, Cell = cell, X = x, Y = y, Z = z, SeenUtc = now,
            };
            Add(hit);
            return hit;
        }
        bool moved = hit.ObjectId != objectId || hit.Cell != cell || MathF.Abs(hit.X - x) > 0.5f || MathF.Abs(hit.Y - y) > 0.5f;
        hit.ObjectId = objectId;
        hit.Cell = cell; hit.X = x; hit.Y = y; hit.Z = z;
        hit.SeenUtc = now;
        if (moved) Changed();
        return hit;
    }

    /// <summary>The player opened a corpse (its contents came): if it's one of these, noted.</summary>
    public void Opened(uint objectId, DateTime now)
    {
        foreach (CorpseRecord c in _list)
            if (c.ObjectId == objectId) { c.OpenedUtc = now; return; }
    }

    /// <summary>Opened this recently, its vanishing is the looting (an emptied corpse rots 15 s later).</summary>
    public static readonly TimeSpan LootedWindow = TimeSpan.FromMinutes(15);
    /// <summary>Vanishing this close to the player can't be leaving view.</summary>
    public const double BesideYou = 10.0;

    /// <summary>
    /// The corpse left view while the player was <paramref name="distance"/> metres off: gone for good
    /// (looted, rotted), or only out of sight? Pure.
    /// </summary>
    public static bool VanishMeansGone(CorpseRecord c, double distance, DateTime now)
    {
        if (c.OpenedUtc != DateTime.MinValue && now - c.OpenedUtc < LootedWindow) return true;
        if (c.RotsUtc != DateTime.MinValue && now >= c.RotsUtc - TimeSpan.FromMinutes(5)) return true;
        return distance <= BesideYou;
    }

    /// <summary>Standing at its place without it there this long: it's gone.</summary>
    public static readonly TimeSpan AbsentFor = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The player is <paramref name="distance"/> metres from it and it is (or isn't) in view: true when
    /// it has been missing from its place long enough to drop. Not in the first <see cref="DeathWindow"/>
    /// after the death (the corpse appears a moment after it). Pure but for the record's timer.
    /// </summary>
    public static bool NotThere(CorpseRecord c, double distance, bool inView, DateTime now)
    {
        if (inView || distance > BesideYou || (c.DiedUtc != DateTime.MinValue && now - c.DiedUtc < DeathWindow))
        {
            c.AbsentSinceUtc = DateTime.MinValue;
            return false;
        }
        if (c.AbsentSinceUtc == DateTime.MinValue) c.AbsentSinceUtc = now;
        return now - c.AbsentSinceUtc >= AbsentFor;
    }

    /// <summary>The corpse object is gone for good (looted and rotted, or rotted).</summary>
    public bool Gone(uint objectId)
    {
        int i = _list.FindIndex(c => c.ObjectId == objectId);
        if (i < 0) return false;
        _list.RemoveAt(i);
        Changed();
        return true;
    }

    public bool Forget(CorpseRecord record)
    {
        if (!_list.Remove(record)) return false;
        Changed();
        return true;
    }

    public void Clear()
    {
        if (_list.Count == 0) return;
        _list.Clear();
        Changed();
    }

    /// <summary>Drops the corpses that should have rotted by now. True when any went.</summary>
    public bool Expire(DateTime now)
    {
        int n = _list.RemoveAll(c => Expired(c, now));
        if (n > 0) Changed();
        return n > 0;
    }

    public static bool Expired(CorpseRecord c, DateTime now)
    {
        if (c.RotsUtc != DateTime.MinValue) return now > c.RotsUtc + RotGrace;
        DateTime known = c.DiedUtc > c.SeenUtc ? c.DiedUtc : c.SeenUtc;
        return known != DateTime.MinValue && now > known + UnknownKeep;
    }

    /// <summary>
    /// Within this (metres) a corpse seen is the death noted there: ACE drops it where the player
    /// stood, so only a knockback in the dying moment separates them. With only the server's map
    /// coordinates (rounded to a tenth, 24 m) the second, wider one.
    /// </summary>
    public const double MatchDistance = 15.0, MatchDistanceCoords = 25.0;

    /// <summary>Metres between a corpse and a place; +inf when they can't be compared (another dungeon).</summary>
    public static double Distance(CorpseRecord c, uint cell, float x, float y, float z)
    {
        if (c.Cell != 0)
        {
            bool aIn = c.Indoors, bIn = (cell & 0xFFFF) >= 0x100;
            // Two dungeon places compare only in the same landblock (dungeons share no space).
            if ((aIn || bIn) && c.Landblock != (cell >> 16)) return double.PositiveInfinity;
            double dx = Global(c.Cell, c.X, true) - Global(cell, x, true);
            double dy = Global(c.Cell, c.Y, false) - Global(cell, y, false);
            double dz = c.Z - z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        if (!c.ServerCoords || (cell & 0xFFFF) >= 0x100 || !MapCoords(cell, x, y, out double ns, out double ew))
            return double.PositiveInfinity;
        // Map coordinates: a tenth of a unit is 24 metres.
        double dn = (c.Ns - ns) * 240.0, de = (c.Ew - ew) * 240.0;
        return Math.Sqrt(dn * dn + de * de);
    }

    /// <summary>A landcell coordinate as a world one: 192 metres a landblock (the cell's high bytes).</summary>
    public static double Global(uint cell, float local, bool x) =>
        ((x ? cell >> 24 : cell >> 16) & 0xFF) * 192.0 + local;

    /// <summary>The map coordinates of a landcell position (the radar's formula: 0.1 = 24 metres).</summary>
    public static bool MapCoords(uint cell, float x, float y, out double ns, out double ew)
    {
        ns = ew = 0;
        if ((cell >> 16) == 0) return false;
        ew = (((cell >> 24) & 0xFF) * 8.0 + x / 24.0 - 1019.5) / 10.0;
        ns = (((cell >> 16) & 0xFF) * 8.0 + y / 24.0 - 1019.5) / 10.0;
        return true;
    }

    /// <summary>The last death, if it was just now (the server's lines belong to it).</summary>
    private CorpseRecord? Recent(DateTime now)
    {
        CorpseRecord? last = null;
        foreach (CorpseRecord c in _list)
            if (c.DiedUtc != DateTime.MinValue && now - c.DiedUtc < DeathWindow && (last == null || c.DiedUtc > last.DiedUtc))
                last = c;
        return last;
    }

    private void Add(CorpseRecord c)
    {
        _list.Add(c);
        Trim();
        Changed();
    }

    /// <summary>The newest first (by the death, or when a found one was first seen); at most <see cref="MaxCorpses"/>.</summary>
    private void Trim()
    {
        _list.Sort((a, b) => When(b).CompareTo(When(a)));
        if (_list.Count > MaxCorpses) _list.RemoveRange(MaxCorpses, _list.Count - MaxCorpses);
    }

    private static DateTime When(CorpseRecord c) => c.DiedUtc != DateTime.MinValue ? c.DiedUtc : c.SeenUtc;

    private void Changed() => Revision++;
}
