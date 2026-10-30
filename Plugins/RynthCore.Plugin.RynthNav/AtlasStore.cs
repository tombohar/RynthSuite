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

    public bool IsFavorite(SavedPlace p) => Favorites.Exists(f => f.SameAs(p));

    /// <summary>Adds it if it isn't there, removes it if it is. Returns true when it is now a favorite.</summary>
    public bool ToggleFavorite(SavedPlace p)
    {
        int i = Favorites.FindIndex(f => f.SameAs(p));
        Version++;
        if (i >= 0) { Favorites.RemoveAt(i); return false; }
        if (Favorites.Count >= MaxFavorites) Favorites.RemoveAt(Favorites.Count - 1);
        Favorites.Add(p);
        return true;
    }

    public bool RemoveFavorite(string name)
    {
        int i = Favorites.FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        Favorites.RemoveAt(i);
        Version++;
        return true;
    }

    /// <summary>Puts it first in Recent (an arrow target or a trip).</summary>
    public void Touch(SavedPlace p)
    {
        int i = Recent.FindIndex(r => r.SameAs(p));
        if (i == 0) return;
        if (i > 0) Recent.RemoveAt(i);
        Recent.Insert(0, p);
        while (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);
        Version++;
    }

    public void ClearRecent()
    {
        if (Recent.Count == 0) return;
        Recent.Clear();
        Version++;
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
