using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Ui;

namespace RynthCore.Plugin.RynthInventory.Views;

/// <summary>
/// The Global Inventory window: a search page (every character on every server, filters,
/// totals, results, details) and a characters page (snapshot ages, forget). Recorded on the pump
/// thread, replayed by the engine. (A server's own pages are its server plugin's: the Banking page
/// that showed RynthILT's bank here is RynthILT's own Banking tab since 2026-10-21.)
/// </summary>
internal sealed partial class InventoryView
{
    public const int TabSearch = 0, TabCharacters = 1;
    private const int PageSize = 100;
    private const float RowIcon = 18f, DetailIcon = 32f, DetailsHeight = 190f;

    private static readonly string[] SortNames = { "Name", "Character", "Count", "Value", "Burden", "Category", "Last seen" };

    private readonly InventoryContext _c;
    public int Tab;

    private string _search = string.Empty;
    private string? _server;          // null = all
    private string? _characterKey;    // null = all
    private string? _category;        // null = all
    private int _page;
    private string? _selectedKey;
    private string? _confirmForget;
    private string _message = string.Empty;

    private InventoryResult? _result;
    private int _resultRevision = -1;
    private bool _resultDirty = true;

    public InventoryView(InventoryContext c) => _c = c;

    /// <summary>Opens on the search page with this text (from "/ginv text").</summary>
    public void SetSearch(string text)
    {
        _search = text ?? string.Empty;
        Tab = TabSearch;
        _page = 0;
        _resultDirty = true;
    }

    private void Invalidate()
    {
        _resultDirty = true;
        _page = 0;
    }

    public void Draw(UiWindow w)
    {
        w.ToggleButton("Search", "tab.search", Tab == TabSearch, () => Tab = TabSearch, small: true);
        w.SameLine();
        int chars = _c.Index?.Characters.Count ?? 0;
        w.ToggleButton($"Characters ({chars})", "tab.chars", Tab == TabCharacters, () => Tab = TabCharacters, small: true);
        w.SameLine();
        w.TextDisabled(StatusLine());
        w.Separator();

        if (_c.Index == null)
        {
            w.TextDisabled(_c.Loading ? "Reading the inventory files..." : "No inventory read yet.");
            return;
        }
        if (Tab == TabCharacters) DrawCharacters(w, _c.Index);
        else DrawSearch(w, _c.Index);
    }

    private string StatusLine()
    {
        if (!_c.InWorld) return "Not logged in.";
        if (_c.OpenStorage != 0) return "Recording " + _c.OpenStorageLabel;
        if (_c.Live == null) return "Scanning " + _c.Character + "...";
        if (_c.LastSaveError.Length > 0) return "Couldn't save: " + _c.LastSaveError;
        if (_c.LastSavedUtc == default) return _c.Character + ": not saved yet";
        return _c.Character + " saved " + InventoryIndex.Age(_c.LastSavedUtc, _c.UtcNow);
    }

    // ── Search page ─────────────────────────────────────────────────────────

    private void DrawSearch(UiWindow w, InventoryIndex index)
    {
        InventorySettings s = _c.Settings;
        w.InputText("Search", "q", _search, 128, v => { _search = v; Invalidate(); }, UiFormat.InputAutoSelectAll);

        // Servers: the one you're on first, the others labelled.
        w.ToggleButton("All servers", "srv.all", _server == null, () => { _server = null; _characterKey = null; Invalidate(); }, small: true);
        for (int i = 0; i < index.Servers.Count; i++)
        {
            string srv = index.Servers[i];
            bool here = InventoryIndex.SameName(srv, index.CurrentServer);
            w.SameLine();
            w.ToggleButton(here ? srv + " (here)" : srv, "srv." + i, _server != null && InventoryIndex.SameName(_server, srv),
                () => { _server = srv; _characterKey = null; Invalidate(); }, small: true);
        }

        w.Checkbox("Storage", "opt.storage", s.IncludeStorage, v => { s.IncludeStorage = v; s.Save(); Invalidate(); });
        w.SameLine();
        w.Checkbox("Equipped", "opt.equipped", s.IncludeEquipped, v => { s.IncludeEquipped = v; s.Save(); Invalidate(); });
        w.SameLine();
        w.Checkbox("Spells and material too", "opt.spells", s.SearchSpells, v => { s.SearchSpells = v; s.Save(); Invalidate(); });
        w.Tooltip("Also match the search against material, armor set and spell names (e.g. \"legendary strength\").");
        w.SameLine();
        w.Checkbox("Group by name", "opt.group", s.GroupByName, v => { s.GroupByName = v; s.Save(); Invalidate(); _selectedKey = null; });

        // Character filter (characters of the chosen server).
        var charKeys = new List<string?> { null };
        var charNames = new List<string> { "All characters" };
        foreach (CharacterInfo ci in index.Characters)
        {
            if (_server != null && !InventoryIndex.SameName(ci.Server, _server)) continue;
            charKeys.Add(ci.Key);
            charNames.Add(ci.IsCurrentServer ? ci.Name : $"{ci.Name} [{ci.Server}]");
        }
        int charIndex = Math.Max(0, charKeys.IndexOf(_characterKey));
        w.Combo("Character", "f.char", charIndex, charNames, i => { _characterKey = i > 0 && i < charKeys.Count ? charKeys[i] : null; Invalidate(); });

        var catNames = new List<string> { "All categories" };
        int catIndex = 0;
        for (int i = 0; i < ItemCategory.All.Length; i++)
        {
            catNames.Add(ItemCategory.All[i].Label);
            if (ItemCategory.All[i].Id == _category) catIndex = i + 1;
        }
        w.Combo("Category", "f.cat", catIndex, catNames, i => { _category = i > 0 && i <= ItemCategory.All.Length ? ItemCategory.All[i - 1].Id : null; Invalidate(); });

        w.Combo("Sort", "f.sort", Math.Clamp(s.Sort, 0, SortNames.Length - 1), SortNames, i => { s.Sort = i; s.Save(); Invalidate(); });
        w.SameLine();
        w.Checkbox("Descending", "f.desc", s.Descending, v => { s.Descending = v; s.Save(); Invalidate(); });

        InventoryResult result = Result(index);
        w.Separator();
        if (result.Rows.Count == 0)
        {
            w.TextDisabled(index.Rows.Count == 0 ? "No items recorded yet. Each character is scanned when it logs in with RynthInventory loaded." : "Nothing found.");
            return;
        }

        // Totals: "Pyreal: 12,345 across 4 characters".
        int shownTotals = Math.Min(3, result.Totals.Count);
        for (int i = 0; i < shownTotals; i++)
            w.TextColored(UiColors.Gold, InventoryIndex.TotalText(result.Totals[i]));
        if (result.Totals.Count > shownTotals)
            w.TextDisabled($"...and {result.Totals.Count - shownTotals} more kinds of item; {result.TotalCount.ToString("N0", CultureInfo.InvariantCulture)} items in all.");

        bool grouped = s.GroupByName;
        int count = grouped ? result.Totals.Count : result.Rows.Count;
        int pages = Math.Max(1, (count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        string what = grouped ? $"{count} item names" : $"{count} results";
        w.TextDisabled($"{what} on {result.CharacterCount} character{(result.CharacterCount == 1 ? "" : "s")}");
        if (pages > 1)
        {
            w.SameLine();
            if (_page > 0) { w.SmallButton("< Prev", "pg.prev", () => _page--); w.SameLine(); }
            w.TextDisabled($"page {_page + 1} of {pages}");
            if (_page < pages - 1) { w.SameLine(); w.SmallButton("Next >", "pg.next", () => _page++); }
        }

        object? selected = FindSelected(result, grouped);
        w.BeginChild("results", 0f, selected != null ? -DetailsHeight : 0f, border: false);
        int start = _page * PageSize, end = Math.Min(count, start + PageSize);
        for (int i = start; i < end; i++)
        {
            if (grouped) DrawGroupRow(w, result.Totals[i]);
            else DrawRow(w, result.Rows[i]);
        }
        w.EndChild();

        if (selected != null)
        {
            w.Separator();
            w.BeginChild("details", 0f, 0f, border: false);
            if (selected is InventoryRow row) DrawDetails(w, row);
            else if (selected is InventoryTotal total) DrawGroupDetails(w, total);
            w.EndChild();
        }
    }

    private InventoryResult Result(InventoryIndex index)
    {
        if (_result != null && !_resultDirty && _resultRevision == _c.IndexRevision) return _result;
        InventorySettings s = _c.Settings;
        _result = index.Query(new InventoryQuery
        {
            Text = _search,
            Server = _server,
            CharacterKey = _characterKey,
            Category = _category,
            IncludeEquipped = s.IncludeEquipped,
            IncludeStorage = s.IncludeStorage,
            SearchSpells = s.SearchSpells,
            Sort = (InventorySort)Math.Clamp(s.Sort, 0, SortNames.Length - 1),
            Descending = s.Descending,
        });
        _resultRevision = _c.IndexRevision;
        _resultDirty = false;
        return _result;
    }

    private static string RowKey(InventoryRow r) => r.Owner.Key + "|" + (r.Storage?.Id ?? 0).ToString(CultureInfo.InvariantCulture) + "|" + r.Item.Id.ToString(CultureInfo.InvariantCulture);
    private static string GroupKey(InventoryTotal t) => "g|" + t.Name.ToLowerInvariant();

    private object? FindSelected(InventoryResult result, bool grouped)
    {
        if (_selectedKey == null) return null;
        if (grouped)
        {
            foreach (InventoryTotal t in result.Totals) if (GroupKey(t) == _selectedKey) return t;
        }
        else
        {
            foreach (InventoryRow r in result.Rows) if (RowKey(r) == _selectedKey) return r;
        }
        return null;
    }

    private void Select(string key) => _selectedKey = _selectedKey == key ? null : key;

    /// <summary>
    /// Clicking one of the logged-in character's own items also selects it in the game (the bag and
    /// the selection panel), as clicking it in the pack would. Another character's item can't be.
    /// A problem report asked for this (R-20261016-213300-1737).
    /// </summary>
    private void SelectInGame(InventoryRow? r)
    {
        if (r == null || !r.Owner.IsCurrentCharacter || r.Item.Id == 0 || !_c.Host.HasSelectItem) return;
        _c.Host.SelectItem(r.Item.Id);
    }

    private void DrawRow(UiWindow w, InventoryRow r)
    {
        ItemRecord it = r.Item;
        bool icons = _c.Settings.ShowIcons && _c.IconsAvailable;
        if (icons) { DrawIcon(w, it, RowIcon); w.SameLine(); }

        string owner = r.Owner.IsCurrentServer ? r.Owner.Name : $"[{r.Owner.Server}] {r.Owner.Name}";
        string label = $"{it.Name}{StackText(it)}  -  {owner}: {r.Where}";
        if (IsStale(r.SeenUtc)) label += $"  (seen {InventoryIndex.Age(r.SeenUtc, _c.UtcNow)})";
        string key = RowKey(r);
        uint? color = !r.Owner.IsCurrentServer ? UiColors.OtherServer : IsStale(r.SeenUtc) ? UiColors.Stale : null;
        if (color != null) w.PushStyleColor(UiFormat.ColText, color.Value);
        w.Selectable(label, "r." + key, _selectedKey == key, () => { Select(key); SelectInGame(r); }, 0f, icons ? RowIcon : 0f);
        if (color != null) w.PopStyleColor();
    }

    private void DrawGroupRow(UiWindow w, InventoryTotal t)
    {
        bool icons = _c.Settings.ShowIcons && _c.IconsAvailable;
        if (icons && t.Holders.Count > 0) { DrawIcon(w, t.Holders[0].Item, RowIcon); w.SameLine(); }
        string label = $"{t.Name}  x{t.Count.ToString("N0", CultureInfo.InvariantCulture)}  -  {t.Characters} character{(t.Characters == 1 ? "" : "s")}";
        if (t.Servers > 1) label += $", {t.Servers} servers";
        string key = GroupKey(t);
        // A total across characters: select the logged-in character's own stack, if it has one.
        w.Selectable(label, "g." + key, _selectedKey == key,
            () => { Select(key); SelectInGame(t.Holders.Find(h => h.Owner.IsCurrentCharacter)); }, 0f, icons ? RowIcon : 0f);
    }

    /// <summary>
    /// The item's picture: underlay, icon and overlay drawn over each other. Each layer after
    /// the first goes back to the row's start (SameLine at x 0.01: a borderless child has no
    /// padding, so that is where the row began). Only at the start of a row in a borderless child.
    /// </summary>
    private static void DrawIcon(UiWindow w, ItemRecord it, float size)
    {
        bool drew = false;
        if (it.Underlay != 0) { w.Image(UiFormat.IconKindIcon, it.Underlay, size, size); drew = true; }
        if (it.Icon != 0)
        {
            if (drew) w.SameLine(0.01f);
            w.Image(UiFormat.IconKindIcon, it.Icon, size, size);
            drew = true;
        }
        if (it.Overlay != 0)
        {
            if (drew) w.SameLine(0.01f);
            w.Image(UiFormat.IconKindIcon, it.Overlay, size, size);
            drew = true;
        }
        if (!drew) w.Dummy(size, size);
    }

    private static string StackText(ItemRecord it) =>
        it.Stack > 1 ? " x" + it.Stack.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;

    private bool IsStale(DateTime seenUtc) =>
        seenUtc != default && (_c.UtcNow - seenUtc).TotalDays >= Math.Max(1, _c.Settings.StaleDays);

    private void DrawDetails(UiWindow w, InventoryRow r)
    {
        ItemRecord it = r.Item;
        if (_c.Settings.ShowIcons && _c.IconsAvailable) { DrawIcon(w, it, DetailIcon); w.SameLine(); }
        w.TextColored(UiColors.Gold, it.Name + StackText(it));
        w.SameLine();
        w.SmallButton("Close", "d.close", () => _selectedKey = null);

        string who = r.Owner.Name + " on " + r.Owner.Server;
        if (!string.IsNullOrEmpty(r.Owner.Account) && r.Owner.Account != "unknown") who += $" (account {r.Owner.Account})";
        if (r.Owner.IsCurrentCharacter) who += " - this character";
        else if (!r.Owner.IsCurrentServer) who += " - another server";
        w.Text("Owner: " + who);
        w.Text("Where: " + r.Where);
        if (r.Storage != null)
            w.TextDisabled($"Storage seen open {InventoryIndex.Age(r.Storage.SeenUtc, _c.UtcNow)}; it is only updated when a character opens it.");
        else
            w.Text("Seen: " + InventoryIndex.Age(r.SeenUtc, _c.UtcNow) + (IsStale(r.SeenUtc) ? " (stale: log that character in to refresh)" : ""));

        var parts = new List<string> { ItemCategory.Label(it.Category) };
        if (it.Value != 0) parts.Add("value " + it.Value.ToString("N0", CultureInfo.InvariantCulture));
        if (it.Burden != 0) parts.Add("burden " + it.Burden.ToString("N0", CultureInfo.InvariantCulture));
        if (it.MaxStack > 1) parts.Add($"stack {it.Stack.ToString("N0", CultureInfo.InvariantCulture)}/{it.MaxStack.ToString("N0", CultureInfo.InvariantCulture)}");
        w.Text(string.Join(", ", parts));

        var more = new List<string>();
        string material = ItemNames.Material(it.Material);
        if (material.Length > 0) more.Add(material);
        if (it.Workmanship > 0) more.Add("workmanship " + it.Workmanship.ToString(CultureInfo.InvariantCulture));
        if (it.ArmorLevel > 0) more.Add("armor level " + it.ArmorLevel.ToString(CultureInfo.InvariantCulture));
        if (it.MaxUses > 0) more.Add($"uses {it.Uses}/{it.MaxUses}");
        string set = ItemNames.Set(it.SetId);
        if (set.Length > 0) more.Add(set);
        if (more.Count > 0) w.Text(string.Join(", ", more));
        if (it.Place == ItemPlace.Equipped && it.Slot != 0) w.Text("Slot: " + ItemNames.Slot(it.Slot));
        if (it.SpellNames.Length > 0) w.TextWrapped("Spells: " + string.Join(", ", it.SpellNames));
        else if (it.Spells.Length > 0) w.TextWrapped("Spells: " + string.Join(", ", Array.ConvertAll(it.Spells, x => "#" + x.ToString(CultureInfo.InvariantCulture))));
        if (!it.Identified && it.Category is ItemCategory.Weapon or ItemCategory.Armor or ItemCategory.Clothing or ItemCategory.Jewelry or ItemCategory.Caster)
            w.TextDisabled("Not identified when scanned: spells, workmanship and set may be missing.");
        w.TextDisabled($"Item 0x{it.Id:X8}" + (it.Wcid != 0 ? $", class {it.Wcid}" : ""));
    }

    private void DrawGroupDetails(UiWindow w, InventoryTotal t)
    {
        if (_c.Settings.ShowIcons && _c.IconsAvailable && t.Holders.Count > 0) { DrawIcon(w, t.Holders[0].Item, DetailIcon); w.SameLine(); }
        w.TextColored(UiColors.Gold, InventoryIndex.TotalText(t));
        w.SameLine();
        w.SmallButton("Close", "d.close", () => _selectedKey = null);
        int shown = 0;
        foreach (InventoryRow r in t.Holders)
        {
            if (shown++ >= 60) { w.TextDisabled($"...and {t.Holders.Count - 60} more."); break; }
            string owner = r.Owner.IsCurrentServer ? r.Owner.Name : $"[{r.Owner.Server}] {r.Owner.Name}";
            string line = $"{owner}: {r.Where}{StackText(r.Item)}";
            if (IsStale(r.SeenUtc)) w.TextColored(UiColors.Stale, line + $" (seen {InventoryIndex.Age(r.SeenUtc, _c.UtcNow)})");
            else if (!r.Owner.IsCurrentServer) w.TextColored(UiColors.OtherServer, line);
            else w.Text(line);
        }
    }

    // ── Characters page ─────────────────────────────────────────────────────

    private void DrawCharacters(UiWindow w, InventoryIndex index)
    {
        w.SmallButton("Rescan now", "c.rescan", () => { _c.RequestRescan(); _message = "Rescanning this character; it saves within a few seconds."; });
        w.SameLine();
        w.SmallButton("Reload files", "c.reload", () => { _c.RequestReload(); _message = "Reading the inventory files again."; });
        w.SameLine();
        w.Checkbox("Icons", "c.icons", _c.Settings.ShowIcons, v => { _c.Settings.ShowIcons = v; _c.Settings.Save(); });
        if (!_c.IconsAvailable) { w.SameLine(); w.TextDisabled("(this engine can't draw icons in plugin windows)"); }
        if (_message.Length > 0) w.TextColored(UiColors.Green, _message);
        if (_c.BrokenFiles > 0) w.TextColored(UiColors.Red, $"{_c.BrokenFiles} file(s) couldn't be read and were skipped.");
        w.TextDisabled($"Snapshots older than {_c.Settings.StaleDays} days are shown as stale. Files: {_c.Store.Root}");

        w.BeginChild("chars", 0f, 0f, border: false);
        string? lastServer = null;
        foreach (CharacterInfo ci in index.Characters)
        {
            if (lastServer == null || !InventoryIndex.SameName(lastServer, ci.Server))
            {
                lastServer = ci.Server;
                w.SeparatorText(ci.IsCurrentServer ? ci.Server + " (this server)" : ci.Server);
            }
            DrawCharacter(w, ci);
        }
        if (index.Characters.Count == 0) w.TextDisabled("No characters recorded yet.");
        w.EndChild();
    }

    private void DrawCharacter(UiWindow w, CharacterInfo ci)
    {
        bool stale = IsStale(ci.ScannedUtc);
        string line = $"{ci.Name}{(ci.IsCurrentCharacter ? " (you)" : "")} - {ci.ItemCount} items"
                      + (ci.StorageCount > 0 ? $", {ci.StorageCount} storage" : "")
                      + $" - scanned {InventoryIndex.Age(ci.ScannedUtc, _c.UtcNow)}"
                      + (stale ? " (stale)" : "");
        if (stale) w.TextColored(UiColors.Stale, line);
        else w.Text(line);
        if (!string.IsNullOrEmpty(ci.Account) && ci.Account != "unknown") w.Tooltip("Account: " + ci.Account);
        w.SameLine();
        string key = ci.Key;
        w.SmallButton("Forget", "forget." + key, () => _confirmForget = key);
        if (_confirmForget == key)
        {
            string warn = ci.IsCurrentCharacter
                ? $"Forget {ci.Name}? You're logged into it, so it comes back at the next scan."
                : $"Forget {ci.Name} on {ci.Server}? Use this for deleted or renamed characters. The file is moved to the _forgotten folder, not deleted.";
            w.TextColored(UiColors.Red, warn);
            w.SmallButton("Yes, forget", "forget.yes." + key, () =>
            {
                _message = _c.Forget(ci);
                _confirmForget = null;
                _selectedKey = null;
            });
            w.SameLine();
            w.SmallButton("Cancel", "forget.no." + key, () => _confirmForget = null);
        }
        foreach (StorageRecord st in ci.Storage)
            w.BulletText($"{(string.IsNullOrEmpty(st.Label) ? st.Name : st.Label)}: {st.Items.Count} items, seen {InventoryIndex.Age(st.SeenUtc, _c.UtcNow)}");
    }
}
