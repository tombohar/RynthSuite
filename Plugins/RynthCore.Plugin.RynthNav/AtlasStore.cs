using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthNav;

/// <summary>A saved place: favorites and recent arrow targets. Kept by name and coordinates,
/// not Atlas id, so they survive the Atlas being regenerated.</summary>
internal sealed class SavedPlace
{
    public string Name = "", Type = "";
    public double Ns, Ew;

    public bool SameAs(SavedPlace o) =>
        Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase) && Math.Abs(Ns - o.Ns) < 0.05 && Math.Abs(Ew - o.Ew) < 0.05;
}

/// <summary>
/// Favorites and Recent (NavData\atlas.txt). The tick thread owns it; the plugin saves
/// a copy of the text off the tick thread when Version moves. No host calls: the offline
/// tests compile this file.
/// </summary>
internal sealed class AtlasStore
{
    public const int MaxRecent = 20, MaxFavorites = 200;

    public List<SavedPlace> Favorites { get; } = new();
    public List<SavedPlace> Recent { get; } = new();
    public int Version { get; private set; }

    // What this client changed since its last save. Every game client runs RynthNav and saves the
    // whole file, so a save first re-reads it and applies only these (MergeInto): a favorite added on
    // one client is no longer wiped by another client's next recent place (2026-11-01).
    private readonly List<SavedPlace> _addedFavs = new(), _removedFavs = new(), _touched = new();
    private bool _recentCleared;

    public bool IsFavorite(SavedPlace p) => Favorites.Exists(f => f.SameAs(p));

    /// <summary>Adds it if it isn't there, removes it if it is. Returns true when it is now a favorite.</summary>
    public bool ToggleFavorite(SavedPlace p)
    {
        int i = Favorites.FindIndex(f => f.SameAs(p));
        Version++;
        if (i >= 0) { NoteRemoved(Favorites[i]); Favorites.RemoveAt(i); return false; }
        if (Favorites.Count >= MaxFavorites) { NoteRemoved(Favorites[^1]); Favorites.RemoveAt(Favorites.Count - 1); }
        Favorites.Add(p);
        _removedFavs.RemoveAll(f => f.SameAs(p));
        _addedFavs.Add(p);
        return true;
    }

    public bool RemoveFavorite(string name)
    {
        int i = Favorites.FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        NoteRemoved(Favorites[i]);
        Favorites.RemoveAt(i);
        Version++;
        return true;
    }

    private void NoteRemoved(SavedPlace p)
    {
        _addedFavs.RemoveAll(f => f.SameAs(p));
        _removedFavs.Add(p);
    }

    /// <summary>
    /// The favorite called <paramref name="name"/> (any case), or null. A favorite named with a tag in
    /// brackets ("Abandoned Experiment (Conquest)") answers to the name without it too.
    /// </summary>
    public SavedPlace? FindFavorite(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        foreach (var f in Favorites)
            if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        foreach (var f in Favorites)
            if (Untagged(f.Name).Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    private static string Untagged(string name)
    {
        string n = name.TrimEnd();
        if (!n.EndsWith(')')) return n;
        int open = n.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 ? n.Substring(0, open).TrimEnd() : n;
    }

    /// <summary>Puts it first in Recent (an arrow target or a trip).</summary>
    public void Touch(SavedPlace p)
    {
        int i = Recent.FindIndex(r => r.SameAs(p));
        if (i == 0) return;
        if (i > 0) Recent.RemoveAt(i);
        Recent.Insert(0, p);
        while (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);
        _touched.RemoveAll(r => r.SameAs(p));
        _touched.Insert(0, p);
        Version++;
    }

    public void ClearRecent()
    {
        if (Recent.Count == 0) return;
        Recent.Clear();
        _touched.Clear();
        _recentCleared = true;
        Version++;
    }

    /// <summary>
    /// Before a save: makes this store the file on disk (<paramref name="disk"/>, what the other clients
    /// saved) with this client's changes since its last save applied — favorites it added or removed,
    /// the places it went to (first in Recent), a Clear — then forgets those changes. Afterwards this
    /// client also has the other clients' favorites.
    /// </summary>
    public void MergeInto(AtlasStore disk)
    {
        var favs = new List<SavedPlace>();
        foreach (var f in disk.Favorites)
            if (!_removedFavs.Exists(r => r.SameAs(f)) && !favs.Exists(x => x.SameAs(f))) favs.Add(f);
        foreach (var f in _addedFavs)
            if (!favs.Exists(x => x.SameAs(f))) favs.Add(f);
        while (favs.Count > MaxFavorites) favs.RemoveAt(0);

        var recent = new List<SavedPlace>(_touched);
        if (!_recentCleared)
            foreach (var r in disk.Recent)
                if (!recent.Exists(x => x.SameAs(r))) recent.Add(r);
        while (recent.Count > MaxRecent) recent.RemoveAt(recent.Count - 1);

        Favorites.Clear(); Favorites.AddRange(favs);
        Recent.Clear(); Recent.AddRange(recent);
        _addedFavs.Clear(); _removedFavs.Clear(); _touched.Clear();
        _recentCleared = false;
    }

    // ── atlas.txt ────────────────────────────────────────────────────────────
    // fav\tname\tns\tew\ttype   /   recent\tname\tns\tew\ttype (newest first)

    public string Serialize()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# RynthNav Atlas: favorites and recent places. kind<TAB>name<TAB>NS<TAB>EW<TAB>type\r\n");
        foreach (var f in Favorites) Line(sb, "fav", f, ci);
        foreach (var r in Recent) Line(sb, "recent", r, ci);
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string kind, SavedPlace p, CultureInfo ci) =>
        sb.Append(kind).Append('\t').Append(p.Name.Replace('\t', ' ')).Append('\t')
          .Append(p.Ns.ToString("F2", ci)).Append('\t').Append(p.Ew.ToString("F2", ci)).Append('\t')
          .Append(p.Type.Replace('\t', ' ')).Append("\r\n");

    public static AtlasStore Parse(string text)
    {
        var s = new AtlasStore();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            if (f.Length < 4) continue;
            if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ns)) continue;
            if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double ew)) continue;
            var p = new SavedPlace { Name = f[1], Ns = ns, Ew = ew, Type = f.Length > 4 ? f[4] : "" };
            if (f[0] == "fav" && s.Favorites.Count < MaxFavorites && !s.IsFavorite(p)) s.Favorites.Add(p);
            else if (f[0] == "recent" && s.Recent.Count < MaxRecent) s.Recent.Add(p);
        }
        return s;
    }
}
