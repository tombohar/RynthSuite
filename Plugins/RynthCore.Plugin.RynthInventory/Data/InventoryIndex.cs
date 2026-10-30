using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>One character in the merged view.</summary>
internal sealed class CharacterInfo
{
    public CharacterSnapshot Snapshot = null!;
    public string Server => Snapshot.Server;
    public string Account => Snapshot.Account;
    public string Name => Snapshot.Character;
    public string Key => Snapshot.Key;
    public DateTime ScannedUtc => Snapshot.ScannedUtc;
    public bool IsCurrentServer;
    public bool IsCurrentCharacter;
    public int ItemCount;
    public int StorageCount;
    /// <summary>Storage this character saw, merged over all its files (newest copy of each container).</summary>
    public List<StorageRecord> Storage = new();
    /// <summary>The same character's file under another account folder lost to this newer one.</summary>
    public List<string> ShadowedFiles = new();
}

/// <summary>One item in the merged view: whose it is and where.</summary>
internal sealed class InventoryRow
{
    public CharacterInfo Owner = null!;
    public ItemRecord Item = null!;
    /// <summary>The storage it is in, or null when carried.</summary>
    public StorageRecord? Storage;
    /// <summary>"Main pack > Sack", "Equipped: Head", "Storage near 42.1N, 33.6E > Sack".</summary>
    public string Where = string.Empty;
    /// <summary>Lower-case name (and, for the wide search, material, set and spell names).</summary>
    public string SearchName = string.Empty;
    public string SearchWide = string.Empty;
    /// <summary>When this row's data was seen (the scan, or the storage's last opening).</summary>
    public DateTime SeenUtc;
}

internal enum InventorySort { Name, Character, Count, Value, Burden, Category, Age }

internal sealed class InventoryQuery
{
    public string Text = string.Empty;
    /// <summary>Null = every server.</summary>
    public string? Server;
    /// <summary>A <see cref="CharacterSnapshot.Key"/>, or null = every character.</summary>
    public string? CharacterKey;
    /// <summary>An <see cref="ItemCategory"/> id, or null = every category.</summary>
    public string? Category;
    public bool IncludeEquipped = true;
    public bool IncludeStorage = true;
    /// <summary>Also match material, set and spell names, not just the item's name.</summary>
    public bool SearchSpells;
    public InventorySort Sort = InventorySort.Name;
    public bool Descending;
}

/// <summary>A total for one item name over the results: "Pyreal: 12,345 across 4 characters".</summary>
internal sealed class InventoryTotal
{
    public string Name = string.Empty;
    public long Count;
    public int Characters;
    public int Servers;
    public int Rows;
    public readonly List<InventoryRow> Holders = new();
}

internal sealed class InventoryResult
{
    public readonly List<InventoryRow> Rows = new();
    /// <summary>Per item name, biggest count first.</summary>
    public readonly List<InventoryTotal> Totals = new();
    public long TotalCount;
    public int CharacterCount;
}

/// <summary>
/// The merged view of every character file: one row per item (carried, worn, in storage).
/// Merging rules: a character (server + name) appears once, its newest snapshot wins (the
/// live one in this client always does); a storage container (server + object id) appears
/// once, from whichever character saw it open most recently. Pure: no files, no game.
/// </summary>
internal sealed class InventoryIndex
{
    public readonly List<CharacterInfo> Characters = new();
    public readonly List<InventoryRow> Rows = new();
    public readonly List<string> Servers = new();
    public string CurrentServer = string.Empty;

    public static InventoryIndex Build(IEnumerable<CharacterSnapshot> files, CharacterSnapshot? live, string currentServer, string currentCharacter)
    {
        var index = new InventoryIndex { CurrentServer = currentServer ?? string.Empty };
        string liveKey = live?.Key ?? CharacterSnapshot.MakeKey(currentServer ?? "", currentCharacter ?? "");

        // 1. One snapshot per character: the live one, else the newest scan.
        var byKey = new Dictionary<string, CharacterInfo>();
        var all = new List<CharacterSnapshot>(files);
        if (live != null) all.Add(live);
        foreach (CharacterSnapshot s in all)
        {
            if (string.IsNullOrWhiteSpace(s.Character)) continue;
            bool isLive = ReferenceEquals(s, live);
            if (byKey.TryGetValue(s.Key, out CharacterInfo? have))
            {
                bool haveLive = ReferenceEquals(have.Snapshot, live);
                bool replace = isLive || (!haveLive && s.ScannedUtc > have.Snapshot.ScannedUtc);
                CharacterSnapshot loser = replace ? have.Snapshot : s;
                if (!string.IsNullOrEmpty(loser.FilePath) && !ReferenceEquals(loser, live)) have.ShadowedFiles.Add(loser.FilePath);
                // Storage either copy saw is kept (newest copy of each container).
                MergeStorage(have.Storage, s.Storage);
                if (replace) have.Snapshot = s;
                continue;
            }
            var info = new CharacterInfo { Snapshot = s };
            MergeStorage(info.Storage, s.Storage);
            byKey[s.Key] = info;
        }

        // 2. One copy per storage container per server: the most recently seen.
        var storageOwner = new Dictionary<(string, uint), (CharacterInfo Owner, StorageRecord Storage)>();
        foreach (CharacterInfo c in byKey.Values)
        {
            string server = c.Server.Trim().ToLowerInvariant();
            foreach (StorageRecord st in c.Storage)
            {
                var key = (server, st.Id);
                if (!storageOwner.TryGetValue(key, out var have) || st.SeenUtc > have.Storage.SeenUtc)
                    storageOwner[key] = (c, st);
            }
        }

        // 3. Rows.
        foreach (CharacterInfo c in byKey.Values)
        {
            c.IsCurrentServer = SameName(c.Server, index.CurrentServer);
            c.IsCurrentCharacter = c.Key == liveKey;
            c.ItemCount = c.Snapshot.Items.Count;
            foreach (ItemRecord it in c.Snapshot.Items)
                index.Rows.Add(MakeRow(c, it, null, c.Snapshot.ScannedUtc));
            index.Characters.Add(c);
            if (!index.Servers.Exists(x => SameName(x, c.Server))) index.Servers.Add(c.Server);
        }
        foreach (var (owner, st) in storageOwner.Values)
        {
            owner.StorageCount++;
            foreach (ItemRecord it in st.Items)
                index.Rows.Add(MakeRow(owner, it, st, st.SeenUtc));
        }

        // Current server first, then by name.
        index.Servers.Sort((a, b) =>
        {
            int ca = SameName(a, index.CurrentServer) ? 0 : 1, cb = SameName(b, index.CurrentServer) ? 0 : 1;
            return ca != cb ? ca - cb : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        index.Characters.Sort(CompareCharacters);
        return index;
    }

    /// <summary>Adds the records of <paramref name="from"/> that <paramref name="into"/> lacks or has older. Snapshots are never changed.</summary>
    private static void MergeStorage(List<StorageRecord> into, List<StorageRecord> from)
    {
        foreach (StorageRecord st in from)
        {
            int i = into.FindIndex(x => x.Id == st.Id);
            if (i < 0) into.Add(st);
            else if (st.SeenUtc > into[i].SeenUtc) into[i] = st;
        }
    }

    private static int CompareCharacters(CharacterInfo a, CharacterInfo b)
    {
        if (a.IsCurrentServer != b.IsCurrentServer) return a.IsCurrentServer ? -1 : 1;
        int s = string.Compare(a.Server, b.Server, StringComparison.OrdinalIgnoreCase);
        if (s != 0) return s;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameName(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static InventoryRow MakeRow(CharacterInfo c, ItemRecord it, StorageRecord? st, DateTime seen)
    {
        string where;
        if (st != null)
        {
            string label = string.IsNullOrEmpty(st.Label) ? (string.IsNullOrEmpty(st.Name) ? "Storage" : st.Name) : st.Label;
            where = it.Path.Length > 0 ? label + " > " + it.PathText : label;
        }
        else if (it.Place == ItemPlace.Equipped)
        {
            string slot = ItemNames.Slot(it.Slot);
            where = slot.Length > 0 ? "Equipped: " + slot : "Equipped";
        }
        else where = it.Path.Length > 0 ? it.PathText : "Main pack";

        string name = it.Name.ToLowerInvariant();
        string wide = name;
        string material = ItemNames.Material(it.Material), set = ItemNames.Set(it.SetId);
        if (material.Length > 0) wide += "\n" + material.ToLowerInvariant();
        if (set.Length > 0) wide += "\n" + set.ToLowerInvariant();
        foreach (string sn in it.SpellNames) wide += "\n" + sn.ToLowerInvariant();

        return new InventoryRow { Owner = c, Item = it, Storage = st, Where = where, SearchName = name, SearchWide = wide, SeenUtc = seen };
    }

    /// <summary>Search terms: whitespace separated, each must appear; "quoted text" is one term.</summary>
    public static List<string> Terms(string text)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return terms;
        string t = text.Trim().ToLowerInvariant();
        int i = 0;
        while (i < t.Length)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i >= t.Length) break;
            if (t[i] == '"')
            {
                int end = t.IndexOf('"', i + 1);
                if (end < 0) end = t.Length;
                string q = t.Substring(i + 1, end - i - 1).Trim();
                if (q.Length > 0) terms.Add(q);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < t.Length && !char.IsWhiteSpace(t[i])) i++;
                terms.Add(t.Substring(start, i - start));
            }
        }
        return terms;
    }

    public InventoryResult Query(InventoryQuery q)
    {
        var result = new InventoryResult();
        List<string> terms = Terms(q.Text);
        foreach (InventoryRow r in Rows)
        {
            if (q.Server != null && !SameName(r.Owner.Server, q.Server)) continue;
            if (q.CharacterKey != null && r.Owner.Key != q.CharacterKey) continue;
            if (q.Category != null && r.Item.Category != q.Category) continue;
            if (!q.IncludeStorage && r.Storage != null) continue;
            if (!q.IncludeEquipped && r.Storage == null && r.Item.Place == ItemPlace.Equipped) continue;
            if (terms.Count > 0)
            {
                string hay = q.SearchSpells ? r.SearchWide : r.SearchName;
                bool all = true;
                foreach (string term in terms)
                    if (!hay.Contains(term, StringComparison.Ordinal)) { all = false; break; }
                if (!all) continue;
            }
            result.Rows.Add(r);
        }

        Comparison<InventoryRow> cmp = q.Sort switch
        {
            InventorySort.Character => (a, b) => Chain(string.Compare(a.Owner.Name, b.Owner.Name, StringComparison.OrdinalIgnoreCase), ByName(a, b)),
            InventorySort.Count => (a, b) => Chain(a.Item.Stack.CompareTo(b.Item.Stack), ByName(a, b)),
            InventorySort.Value => (a, b) => Chain(a.Item.Value.CompareTo(b.Item.Value), ByName(a, b)),
            InventorySort.Burden => (a, b) => Chain(a.Item.Burden.CompareTo(b.Item.Burden), ByName(a, b)),
            InventorySort.Category => (a, b) => Chain(string.CompareOrdinal(ItemCategory.Label(a.Item.Category), ItemCategory.Label(b.Item.Category)), ByName(a, b)),
            InventorySort.Age => (a, b) => Chain(b.SeenUtc.CompareTo(a.SeenUtc), ByName(a, b)),   // newest first
            _ => ByName,
        };
        bool desc = q.Descending;
        result.Rows.Sort((a, b) =>
        {
            // The server you're on comes first whatever the sort.
            if (a.Owner.IsCurrentServer != b.Owner.IsCurrentServer) return a.Owner.IsCurrentServer ? -1 : 1;
            int c = cmp(a, b);
            if (desc) c = -c;
            if (c != 0) return c;
            int o = string.Compare(a.Owner.Name, b.Owner.Name, StringComparison.OrdinalIgnoreCase);
            return o != 0 ? o : a.Item.Id.CompareTo(b.Item.Id);
        });

        // Totals per name.
        var byName = new Dictionary<string, InventoryTotal>(StringComparer.OrdinalIgnoreCase);
        var chars = new HashSet<string>();
        foreach (InventoryRow r in result.Rows)
        {
            if (!byName.TryGetValue(r.Item.Name, out InventoryTotal? t))
                byName[r.Item.Name] = t = new InventoryTotal { Name = r.Item.Name };
            t.Count += Math.Max(1, r.Item.Stack);
            t.Rows++;
            t.Holders.Add(r);
            result.TotalCount += Math.Max(1, r.Item.Stack);
            chars.Add(r.Owner.Key);
        }
        foreach (InventoryTotal t in byName.Values)
        {
            var cs = new HashSet<string>();
            var ss = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InventoryRow r in t.Holders) { cs.Add(r.Owner.Key); ss.Add(r.Owner.Server.Trim()); }
            t.Characters = cs.Count;
            t.Servers = ss.Count;
            result.Totals.Add(t);
        }
        result.Totals.Sort((a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.CharacterCount = chars.Count;
        return result;
    }

    private static int ByName(InventoryRow a, InventoryRow b) => string.Compare(a.Item.Name, b.Item.Name, StringComparison.OrdinalIgnoreCase);
    private static int Chain(int first, int then) => first != 0 ? first : then;

    /// <summary>"Pyreal: 12,345 across 4 characters" (", 2 servers" when more than one).</summary>
    public static string TotalText(InventoryTotal t)
    {
        string s = $"{t.Name}: {t.Count.ToString("N0", CultureInfo.InvariantCulture)} across {t.Characters} character{(t.Characters == 1 ? "" : "s")}";
        if (t.Servers > 1) s += $" on {t.Servers} servers";
        return s;
    }

    /// <summary>"just now", "12 min", "5 h", "3 days" ago.</summary>
    public static string Age(DateTime utc, DateTime nowUtc)
    {
        if (utc == default) return "never";
        TimeSpan d = nowUtc - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        int days = (int)d.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }
}
