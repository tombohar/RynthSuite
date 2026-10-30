// LegacySpellsUi.cs - "RynthAi Spells" window (/ra spells).
//
// Left: ACECustom's spell catalog with school / level / target / effect / known filters and
// sortable columns (click a header; Shift+click adds a second sort key).
// Right: the buff profile editor. A saved profile chosen here ("Use for buffing") or under
// Loaded Files → Buffs replaces buffing's built-in list.
//
// Render thread only. The plugin hands in pump-thread data: the known-spell copy, the built-in
// buff list, and applies the chosen profile path.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed unsafe class LegacySpellsUi
{
    private const string WindowTitle = "RynthAi Spells##RynthAiSpells";
    private const string ConfirmPopupId = "Confirm##rynthaispells";

    /// <summary>Catalog table columns, in display order (the sort specs report these indexes).</summary>
    private enum Col { Add, Name, School, Level, Target, Effect, Family, Known }
    private const int ColumnCount = 8;

    private static readonly string[] TargetFilters = { "All targets", "Self", "Other", "Item", "Fellowship" };
    private static readonly string[] TargetOrder = { "Self", "Other", "Item", "Fellowship" };

    private static readonly Vector4 WarnColor = new(1f, 0.72f, 0.30f, 1f);
    private static readonly Vector4 GoodColor = new(0.45f, 0.85f, 0.45f, 1f);

    private readonly LegacyUiSettings _settings;
    private Action<string>? _requestActiveProfile; // plugin applies it on the pump thread
    private Action? _refreshProfileList;           // Loaded Files list after save / delete

    // Pump-thread handoffs (Volatile / Interlocked: written on the pump thread, read here).
    private HashSet<int>? _known;
    private List<string>? _builtInList;
    private int _builtInRequested;

    // Catalog filters.
    private string _search = "";
    private readonly bool[] _schoolOn = { true, true, true, true, true };
    private readonly bool[] _levelOn = { true, true, true, true, true, true, true, true };
    private string[] _effectItems = Array.Empty<string>();
    private int _effectIdx = -1; // -1 until the catalog is loaded, then "Buff"
    private int _targetIdx;
    private bool _knownOnly;

    // Filtered + sorted rows: rebuilt only when a filter, the sort or the known set changes.
    private readonly List<SpellCatalogEntry> _rows = new();
    private bool _rowsDirty = true;
    private HashSet<int>? _rowsKnown;
    private (Col Col, bool Asc)[] _sort = { (Col.School, true), (Col.Level, false) };

    // Profile editor.
    private BuffProfile _edit = NewProfile();
    private string? _editPath; // null = never saved
    private bool _dirty;
    private readonly HashSet<string> _editKeys = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _files = new();
    private string _status = "";
    private bool _statusIsWarning;

    private bool _wasVisible;
    private bool _rescueChecked;

    // Yes/No confirmation (opened at window level so the popup id stack matches).
    private string _confirmText = "";
    private Action? _confirmAction;
    private bool _openConfirm;

    public LegacySpellsUi(LegacyUiSettings settings) => _settings = settings;

    public void SetHooks(Action<string> requestActiveProfile, Action refreshProfileList)
    {
        _requestActiveProfile = requestActiveProfile;
        _refreshProfileList = refreshProfileList;
    }

    /// <summary>How long after its last embedded frame the body still counts as on screen.</summary>
    private const long EmbeddedVisibleMs = 1500;

    /// <summary>When RynthCore's Spells section last drew the body (TickCount64); render thread writes.</summary>
    private long _embeddedDrawnAt = long.MinValue / 2;

    /// <summary>
    /// True while the window is open or RynthCore's Spells section shows the body (the plugin only
    /// feeds it data then). Any thread.
    /// </summary>
    public bool Visible => _settings.ShowSpellsWindow
                           || Environment.TickCount64 - Interlocked.Read(ref _embeddedDrawnAt) < EmbeddedVisibleMs;

    // ── Pump-thread side ────────────────────────────────────────────────────

    /// <summary>Pump thread: a fresh copy of the character's known spell ids.</summary>
    public void SetKnownSpells(HashSet<int> ids) => Volatile.Write(ref _known, ids);

    /// <summary>Pump thread: true once per "Add built-in list" click.</summary>
    public bool TakeBuiltInRequest() => Interlocked.Exchange(ref _builtInRequested, 0) == 1;

    /// <summary>Pump thread: answer to <see cref="TakeBuiltInRequest"/>.</summary>
    public void SetBuiltInList(List<string> baseNames) => Volatile.Write(ref _builtInList, baseNames);

    // ── Window ──────────────────────────────────────────────────────────────

    public void Render()
    {
        if (!_settings.ShowSpellsWindow)
        {
            _wasVisible = false;
            _rescueChecked = false;
            return;
        }
        if (!_wasVisible)
        {
            _wasVisible = true;
            OnShown();
        }

        UiPlacement.CenterFirstUse(new Vector2(40, 40));
        ImGui.SetNextWindowSize(new Vector2(1140, 660), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(WindowTitle, ref _settings.ShowSpellsWindow))
        {
            UiPlacement.RescueOncePerShow(ref _rescueChecked);
            RenderBody();
        }
        ImGui.End();
    }

    /// <summary>
    /// RynthCore's Spells section (RynthPluginRenderEmbed "section:spells"): the window's body
    /// drawn into the engine's current window. Render thread.
    /// </summary>
    public void RenderEmbedded()
    {
        long now = Environment.TickCount64;
        // First frame after a gap: the same refresh as the window's open.
        if (now - Interlocked.Read(ref _embeddedDrawnAt) >= EmbeddedVisibleMs && !_settings.ShowSpellsWindow) OnShown();
        Interlocked.Exchange(ref _embeddedDrawnAt, now);
        RenderBody();
    }

    /// <summary>The catalog (left, resizable) and the profile editor (right), in the current window.</summary>
    private void RenderBody()
    {
        ApplyBuiltInList();

        if (!SpellCatalog.IsLoaded)
        {
            ImGui.TextDisabled("The spell catalog is not loaded yet (it loads at login; see chat for errors).");
        }
        else
        {
            EnsureEffectItems();
            float avail = ImGui.GetContentRegionAvail().X;
            float leftW = MathF.Max(460f, avail * 0.58f);

            if (ImGui.BeginChild("##spcatalog", new Vector2(leftW, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX))
            {
                RenderFilters();
                RenderCatalogTable();
            }
            ImGui.EndChild();

            ImGui.SameLine();
            if (ImGui.BeginChild("##spprofile", Vector2.Zero, ImGuiChildFlags.Borders))
                RenderProfileEditor();
            ImGui.EndChild();
        }

        RenderConfirmPopup();
    }

    /// <summary>Each time the window opens: refresh the file list and open the active profile.</summary>
    private void OnShown()
    {
        RefreshFiles();
        if (_editPath != null || _dirty) return;
        string active = _settings.CurrentBuffProfilePath ?? "";
        if (active.Length > 0 && File.Exists(active)) LoadProfile(active);
    }

    private void EnsureEffectItems()
    {
        if (_effectItems.Length > 0) return;
        var effects = SpellCatalog.Effects;
        _effectItems = new string[effects.Count + 1];
        _effectItems[0] = "All effects";
        for (int i = 0; i < effects.Count; i++) _effectItems[i + 1] = effects[i];
        // Buff profiles are about buffs, so the list starts there.
        _effectIdx = Math.Max(0, Array.FindIndex(_effectItems, s => s.Equals("Buff", StringComparison.OrdinalIgnoreCase)));
        _rowsDirty = true;
    }

    // ── Catalog: filters ────────────────────────────────────────────────────

    private void RenderFilters()
    {
        ImGui.SetNextItemWidth(190);
        if (ImGui.InputTextWithHint("##spsearch", "Search name or family", ref _search, 64)) _rowsDirty = true;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("##speffect", ref _effectIdx, _effectItems, _effectItems.Length)) _rowsDirty = true;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("##sptarget", ref _targetIdx, TargetFilters, TargetFilters.Length)) _rowsDirty = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Self: cast on yourself. Other: cast on a target (buffing casts the Self version).\nItem: lands on your weapon or armor.");

        ImGui.SameLine();
        bool knownReady = Volatile.Read(ref _known) != null;
        if (ImGui.Checkbox("Known only##spknown", ref _knownOnly)) _rowsDirty = true;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip(knownReady
                ? "Only spells in your spellbook."
                : "Waiting for your spellbook (it is read a few seconds after login).");

        ImGui.TextUnformatted("School:");
        for (int i = 0; i < SpellCatalog.Schools.Length; i++)
        {
            ImGui.SameLine();
            if (ImGui.Checkbox(SpellCatalog.ShortSchool(SpellCatalog.Schools[i]) + "##spsch" + i, ref _schoolOn[i])) _rowsDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("All##spschall")) SetAll(_schoolOn, true);
        ImGui.SameLine();
        if (ImGui.SmallButton("None##spschnone")) SetAll(_schoolOn, false);

        ImGui.TextUnformatted("Level: ");
        for (int i = 0; i < _levelOn.Length; i++)
        {
            ImGui.SameLine();
            if (ImGui.Checkbox((i + 1) + "##splvl" + i, ref _levelOn[i])) _rowsDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("All##splvlall")) SetAll(_levelOn, true);
        ImGui.SameLine();
        if (ImGui.SmallButton("None##splvlnone")) SetAll(_levelOn, false);
    }

    private void SetAll(bool[] flags, bool on)
    {
        for (int i = 0; i < flags.Length; i++) flags[i] = on;
        _rowsDirty = true;
    }

    // ── Catalog: table ──────────────────────────────────────────────────────

    private void RenderCatalogTable()
    {
        RebuildRowsIfNeeded();
        SafeImGui.TextDisabled($"{_rows.Count} of {SpellCatalog.All.Count} spells. Click a header to sort, Shift+click for a second sort. + or double-click adds a buff.");

        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.Hideable | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##spcattable", ColumnCount, flags, Vector2.Zero)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.WidthFixed, 22);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("School", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Lvl", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Effect", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Family", ImGuiTableColumnFlags.DefaultHide | ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Known", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableHeadersRow();

        if (ReadSortSpecs()) RebuildRowsIfNeeded();

        ImGuiListClipper* native = ImGuiNative.ImGuiListClipper_ImGuiListClipper();
        try
        {
            var clipper = new ImGuiListClipperPtr(native);
            clipper.Begin(_rows.Count);
            while (clipper.Step())
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    RenderCatalogRow(_rows[i]);
            clipper.End();
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_destroy(native);
        }
        ImGui.EndTable();
    }

    private void RenderCatalogRow(SpellCatalogEntry e)
    {
        string key = e.IsBuff ? SpellCatalog.BuffKeyFor(e) : "";
        bool inProfile = key.Length > 0 && _editKeys.Contains(key);

        ImGui.TableNextRow();
        ImGui.PushID(e.Id);

        ImGui.TableSetColumnIndex((int)Col.Add);
        if (!e.IsBuff) ImGui.TextDisabled("-");
        else if (inProfile) ImGui.TextColored(GoodColor, "*");
        else if (ImGui.SmallButton("+")) AddSpell(e);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip(!e.IsBuff ? "Only buffs can go in a buff profile."
                : inProfile ? $"Already in the profile (as {key})." : $"Add to the profile (buffing casts {key}).");

        ImGui.TableSetColumnIndex((int)Col.Name);
        if (ImGui.Selectable(e.Name, inProfile, ImGuiSelectableFlags.AllowDoubleClick)
            && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && e.IsBuff && !inProfile)
            AddSpell(e);
        if (ImGui.IsItemHovered()) SpellTooltip(e, key);

        ImGui.TableSetColumnIndex((int)Col.School);
        ImGui.TextUnformatted(SpellCatalog.ShortSchool(e.School));
        ImGui.TableSetColumnIndex((int)Col.Level);
        ImGui.TextUnformatted(e.Level > 0 ? e.Level.ToString() : "-");
        ImGui.TableSetColumnIndex((int)Col.Target);
        ImGui.TextUnformatted(e.Target);
        ImGui.TableSetColumnIndex((int)Col.Effect);
        ImGui.TextUnformatted(e.Effect);
        ImGui.TableSetColumnIndex((int)Col.Family);
        ImGui.TextUnformatted(e.FamilyName);
        ImGui.TableSetColumnIndex((int)Col.Known);
        if (_rowsKnown == null) ImGui.TextDisabled("?");
        else if (_rowsKnown.Contains(e.Id)) ImGui.TextColored(GoodColor, "yes");

        ImGui.PopID();
    }

    private static void SpellTooltip(SpellCatalogEntry e, string key)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted($"{e.Name}  (id {e.Id}, level {e.Level})");
        ImGui.TextUnformatted($"{e.School}, {e.Target}, {e.Effect}");
        if (e.FamilyName.Length > 0) ImGui.TextUnformatted($"Family: {e.FamilyName} (#{e.FamilyId}); power {e.Power}");
        string duration = e.DurationSec > 0 ? FormatDuration(e.DurationSec) : "none";
        ImGui.TextUnformatted($"Mana {e.Mana}, duration {duration}");
        if (key.Length > 0) ImGui.TextUnformatted($"Buffing casts: {key} (best level you know)");
        if (e.Description.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(e.Description);
        }
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static string FormatDuration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m"
        : seconds >= 60 ? $"{seconds / 60}m"
        : $"{seconds}s";

    /// <summary>Copies the table's sort specs into <see cref="_sort"/>. True when they changed.</summary>
    private bool ReadSortSpecs()
    {
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr == null || !specs.SpecsDirty) return false;

        var keys = new List<(Col, bool)>(specs.SpecsCount);
        for (int i = 0; i < specs.SpecsCount; i++)
        {
            ImGuiTableColumnSortSpecs s = specs.NativePtr->Specs[i];
            // SortDirection is one byte natively; mask off whatever follows it.
            bool descending = ((int)s.SortDirection & 0xFF) == (int)ImGuiSortDirection.Descending;
            keys.Add(((Col)s.ColumnIndex, !descending));
        }
        if (keys.Count > 0) _sort = keys.ToArray();
        specs.SpecsDirty = false;
        _rowsDirty = true;
        return true;
    }

    private void RebuildRowsIfNeeded()
    {
        HashSet<int>? known = Volatile.Read(ref _known);
        if (!_rowsDirty && ReferenceEquals(known, _rowsKnown)) return;
        _rowsDirty = false;
        _rowsKnown = known;

        string? effect = _effectIdx > 0 && _effectIdx < _effectItems.Length ? _effectItems[_effectIdx] : null;
        string? target = _targetIdx > 0 ? TargetFilters[_targetIdx] : null;
        string search = _search.Trim();

        _rows.Clear();
        foreach (var e in SpellCatalog.All)
        {
            int school = SchoolRank(e.School);
            if (school < _schoolOn.Length && !_schoolOn[school]) continue;
            if (e.Level >= 1 && e.Level <= _levelOn.Length && !_levelOn[e.Level - 1]) continue;
            if (effect != null && !e.Effect.Equals(effect, StringComparison.OrdinalIgnoreCase)) continue;
            if (target != null && !e.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            if (_knownOnly && known != null && !known.Contains(e.Id)) continue;
            if (search.Length > 0
                && e.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                && e.FamilyName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _rows.Add(e);
        }
        _rows.Sort(CompareRows);
    }

    private int CompareRows(SpellCatalogEntry a, SpellCatalogEntry b)
    {
        foreach (var (col, asc) in _sort)
        {
            int c = col switch
            {
                Col.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                Col.School => SchoolRank(a.School).CompareTo(SchoolRank(b.School)),
                Col.Level => a.Level.CompareTo(b.Level),
                Col.Target => TargetRank(a.Target).CompareTo(TargetRank(b.Target)),
                Col.Effect => string.Compare(a.Effect, b.Effect, StringComparison.OrdinalIgnoreCase),
                Col.Family => string.Compare(a.FamilyName, b.FamilyName, StringComparison.OrdinalIgnoreCase),
                // Ascending puts known spells first.
                Col.Known => IsKnown(b).CompareTo(IsKnown(a)),
                _ => 0,
            };
            if (c != 0) return asc ? c : -c;
        }
        int n = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return n != 0 ? n : a.Id.CompareTo(b.Id);
    }

    private bool IsKnown(SpellCatalogEntry e) => _rowsKnown != null && _rowsKnown.Contains(e.Id);

    /// <summary>Position in <see cref="SpellCatalog.Schools"/>; unknown schools sort last.</summary>
    private static int SchoolRank(string school)
    {
        int i = Array.IndexOf(SpellCatalog.Schools, school);
        return i < 0 ? SpellCatalog.Schools.Length : i;
    }

    /// <summary>Self, Other, Item, Fellowship, then anything else.</summary>
    private static int TargetRank(string target)
    {
        for (int i = 0; i < TargetOrder.Length; i++)
            if (TargetOrder[i].Equals(target, StringComparison.OrdinalIgnoreCase)) return i;
        return TargetOrder.Length;
    }

    // ── Profile editor ──────────────────────────────────────────────────────

    private void RenderProfileEditor()
    {
        string active = _settings.CurrentBuffProfilePath ?? "";
        bool editIsActive = _editPath != null && active.Equals(_editPath, StringComparison.OrdinalIgnoreCase);

        ImGui.TextUnformatted("Buffing uses: " + (active.Length == 0 ? BuiltInLabel : Path.GetFileNameWithoutExtension(active)));
        if (!_settings.EnableBuffing)
        {
            ImGui.SameLine();
            ImGui.TextColored(WarnColor, "(Buffing is off)");
        }

        // Open / New / Save / Delete.
        string preview = _editPath == null ? "(new, not saved)" : Path.GetFileNameWithoutExtension(_editPath);
        if (_dirty) preview += " *";
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("##spopen", preview, ImGuiComboFlags.HeightLarge))
        {
            if (_files.Count == 0) ImGui.TextDisabled("No saved profiles yet.");
            foreach (string file in _files)
            {
                string path = Path.Combine(BuffProfileStore.Folder, file);
                bool selected = path.Equals(_editPath, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(Path.GetFileNameWithoutExtension(file), selected) && !selected)
                    ConfirmIfDirty("open another profile", () => LoadProfile(path));
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open a saved profile (RynthAi\\BuffProfiles).");

        ImGui.SameLine();
        if (ImGui.Button("New##spnew")) ConfirmIfDirty("start a new profile", StartNewProfile);
        ImGui.SameLine();
        if (ImGui.Button("Save##spsave")) Save(thenActivate: false);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Saves to BuffProfiles\\<Name>.json. Change the name to save a copy.");
        ImGui.SameLine();
        ImGui.BeginDisabled(_editPath == null);
        if (ImGui.Button("Delete##spdel")) ConfirmDelete();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh##sprefresh")) RefreshFiles();

        string name = _edit.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##spname", "Profile name", ref name, 80))
        {
            _edit.Name = name;
            _dirty = true;
        }

        // Which list buffing uses.
        if (editIsActive)
        {
            SafeImGui.TextColored(GoodColor, _dirty ? "Buffing uses this profile (save to apply edits)." : "Buffing uses this profile.");
        }
        else if (ImGui.Button("Use for buffing##spuse"))
        {
            Save(thenActivate: true);
        }
        if (active.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Use built-in list##spbuiltin"))
            {
                _requestActiveProfile?.Invoke("");
                SetStatus("Buffing goes back to its built-in list.");
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Add built-in list##spaddbuiltin"))
        {
            Interlocked.Exchange(ref _builtInRequested, 1);
            SetStatus("Reading buffing's built-in list...");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds the spells buffing casts without a profile (for this character's skills), as a starting point.");
        ImGui.SameLine();
        ImGui.BeginDisabled(_edit.Entries.Count == 0);
        if (ImGui.Button("Clear##spclear")) Confirm("Remove every spell from this profile?", ClearEntries);
        ImGui.EndDisabled();
        ImGui.SameLine();
        int enabled = 0;
        foreach (var en in _edit.Entries) if (en.Enabled) enabled++;
        SafeImGui.TextDisabled($"{enabled} of {_edit.Entries.Count} on");

        float notesH = ImGui.GetTextLineHeightWithSpacing() * 3.2f;
        float statusH = ImGui.GetTextLineHeightWithSpacing() * 1.4f;
        RenderEntriesTable(MathF.Max(80f, ImGui.GetContentRegionAvail().Y - notesH - statusH - ImGui.GetStyle().ItemSpacing.Y * 2));

        string notes = _edit.Notes;
        if (ImGui.InputTextMultiline("##spnotes", ref notes, 2000, new Vector2(-1, notesH)))
        {
            _edit.Notes = notes;
            _dirty = true;
        }
        if (ImGui.IsItemHovered() && notes.Length == 0) ImGui.SetTooltip("Notes for this profile.");

        if (_status.Length > 0)
        {
            if (_statusIsWarning) SafeImGui.TextColored(WarnColor, _status);
            else SafeImGui.TextDisabled(_status);
        }
    }

    private void RenderEntriesTable(float height)
    {
        int moveFrom = -1, moveTo = -1, remove = -1;

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("##spentries", 7, flags, new Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Spell", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Casts", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("School", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Lvl", ImGuiTableColumnFlags.WidthFixed, 26);
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 46);
            ImGui.TableSetupColumn("##order", ImGuiTableColumnFlags.WidthFixed, 74);
            ImGui.TableHeadersRow();

            var entries = _edit.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                ImGui.TableNextRow();
                ImGui.PushID(i);

                ImGui.TableSetColumnIndex(0);
                bool on = entry.Enabled;
                if (ImGui.Checkbox("##on", ref on))
                {
                    entry.Enabled = on;
                    _dirty = true;
                }

                ImGui.TableSetColumnIndex(1);
                if (entry.Enabled) ImGui.TextUnformatted(entry.Name);
                else SafeImGui.TextDisabled(entry.Name);
                var spell = entry.SpellId != 0 ? SpellCatalog.Get(entry.SpellId) : null;
                if (spell != null && ImGui.IsItemHovered()) SpellTooltip(spell, entry.Key);

                ImGui.TableSetColumnIndex(2);
                SafeImGui.TextDisabled(entry.Key);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Buffing casts the highest level of this spell you know.");
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(SpellCatalog.ShortSchool(entry.School));
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(entry.Level > 0 ? entry.Level.ToString() : "-");
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(entry.Target);

                ImGui.TableSetColumnIndex(6);
                ImGui.BeginDisabled(i == 0);
                if (ImGui.ArrowButton("##up", ImGuiDir.Up)) { moveFrom = i; moveTo = i - 1; }
                ImGui.EndDisabled();
                ImGui.SameLine(0, 2);
                ImGui.BeginDisabled(i == entries.Count - 1);
                if (ImGui.ArrowButton("##down", ImGuiDir.Down)) { moveFrom = i; moveTo = i + 1; }
                ImGui.EndDisabled();
                ImGui.SameLine(0, 2);
                if (ImGui.SmallButton("X")) remove = i;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove");

                ImGui.PopID();
            }
            if (entries.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(1);
                ImGui.TextDisabled("Empty: add buffs from the list on the left.");
            }
            ImGui.EndTable();
        }

        // Applied after drawing so the loop never sees a list that changed under it.
        if (moveFrom >= 0)
        {
            var list = _edit.Entries;
            (list[moveFrom], list[moveTo]) = (list[moveTo], list[moveFrom]);
            _dirty = true;
        }
        if (remove >= 0)
        {
            _edit.Entries.RemoveAt(remove);
            RebuildEditKeys();
            _dirty = true;
        }
    }

    // ── Profile actions ─────────────────────────────────────────────────────

    private const string BuiltInLabel = "built-in list";

    private static BuffProfile NewProfile() => new() { Name = "New buff profile" };

    private void StartNewProfile()
    {
        _edit = NewProfile();
        _editPath = null;
        _dirty = false;
        RebuildEditKeys();
        SetStatus("New profile: add buffs from the list on the left, then Save.");
    }

    private void LoadProfile(string path)
    {
        if (!BuffProfileStore.TryLoad(path, out var profile, out string error))
        {
            SetStatus($"Could not open {Path.GetFileName(path)}: {error}", warning: true);
            return;
        }
        _edit = profile;
        _editPath = path;
        _dirty = false;
        RebuildEditKeys();
        SetStatus($"Opened {profile.Name} ({profile.Entries.Count} spells).");
    }

    private void AddSpell(SpellCatalogEntry e)
    {
        if (!e.IsBuff) return;
        var entry = BuffProfileStore.EntryFor(e);
        if (_editKeys.Contains(entry.Key))
        {
            SetStatus($"{entry.Key} is already in the profile.");
            return;
        }
        _edit.Entries.Add(entry);
        _editKeys.Add(entry.Key);
        _dirty = true;
        SetStatus($"Added {e.Name} (casts {entry.Key}).");
    }

    private void ClearEntries()
    {
        _edit.Entries.Clear();
        RebuildEditKeys();
        _dirty = true;
        SetStatus("Profile cleared.");
    }

    /// <summary>Adds buffing's built-in list once the pump thread has produced it.</summary>
    private void ApplyBuiltInList()
    {
        List<string>? names = Interlocked.Exchange(ref _builtInList, null);
        if (names == null) return;

        int added = 0, notInCatalog = 0;
        foreach (string baseName in names)
        {
            if (string.IsNullOrWhiteSpace(baseName) || _editKeys.Contains(baseName)) continue;
            var spell = BuffProfileStore.CatalogSpellForBaseName(baseName);
            BuffProfileEntry entry = spell != null ? BuffProfileStore.EntryFor(spell) : new BuffProfileEntry { Name = baseName };
            // Keep the built-in base name: it is exactly what buffing resolves today.
            entry.Key = baseName;
            if (spell == null) notInCatalog++;
            _edit.Entries.Add(entry);
            _editKeys.Add(baseName);
            added++;
        }
        if (added > 0) _dirty = true;
        SetStatus(notInCatalog > 0
            ? $"Added {added} spells from the built-in list ({notInCatalog} not in the catalog)."
            : $"Added {added} spells from the built-in list.");
    }

    /// <summary>Saves to BuffProfiles\&lt;Name&gt;.json, asking before overwriting another profile.</summary>
    private void Save(bool thenActivate)
    {
        string path = BuffProfileStore.PathFor(_edit.Name);
        bool otherFile = !path.Equals(_editPath, StringComparison.OrdinalIgnoreCase);
        if (otherFile && File.Exists(path))
            Confirm($"Replace the saved profile '{Path.GetFileNameWithoutExtension(path)}'?", () => SaveTo(path, thenActivate));
        else
            SaveTo(path, thenActivate);
    }

    private void SaveTo(string path, bool thenActivate)
    {
        if (!RynthCore.Plugin.Shared.DataWriteGuard.TrySave("the buff profile", path,
                () => BuffProfileStore.Save(_edit, path), out string why))
        {
            SetStatus($"Save failed: {why}", warning: true);
            return;
        }
        _editPath = path;
        _dirty = false;
        RefreshFiles();
        _refreshProfileList?.Invoke();

        string active = _settings.CurrentBuffProfilePath ?? "";
        if (thenActivate)
        {
            _requestActiveProfile?.Invoke(path);
            SetStatus(_edit.Entries.Count == 0
                ? $"Saved {_edit.Name}. Buffing uses it, but it has no spells yet."
                : $"Saved {_edit.Name}. Buffing uses it now.", warning: _edit.Entries.Count == 0);
        }
        else if (active.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"Saved {_edit.Name}. Buffing picks up the change within a few seconds.");
        }
        else
        {
            SetStatus($"Saved {_edit.Name}.");
        }
    }

    private void ConfirmDelete()
    {
        string? path = _editPath;
        if (path == null) return;
        Confirm($"Delete the profile '{Path.GetFileNameWithoutExtension(path)}'?", () =>
        {
            if (!BuffProfileStore.Delete(path))
            {
                SetStatus("Delete failed (file missing or in use).", warning: true);
                return;
            }
            if ((_settings.CurrentBuffProfilePath ?? "").Equals(path, StringComparison.OrdinalIgnoreCase))
                _requestActiveProfile?.Invoke("");
            RefreshFiles();
            _refreshProfileList?.Invoke();
            StartNewProfile();
            SetStatus($"Deleted {Path.GetFileNameWithoutExtension(path)}.");
        });
    }

    private void RefreshFiles() => _files = BuffProfileStore.ListFileNames();

    private void RebuildEditKeys()
    {
        _editKeys.Clear();
        foreach (var e in _edit.Entries) _editKeys.Add(e.Key);
    }

    private void SetStatus(string text, bool warning = false)
    {
        _status = text;
        _statusIsWarning = warning;
    }

    // ── Confirmation popup ──────────────────────────────────────────────────

    private void ConfirmIfDirty(string what, Action action)
    {
        if (_dirty) Confirm($"Discard unsaved changes and {what}?", action);
        else action();
    }

    private void Confirm(string text, Action action)
    {
        _confirmText = text;
        _confirmAction = action;
        _openConfirm = true;
    }

    private void RenderConfirmPopup()
    {
        if (_openConfirm)
        {
            ImGui.OpenPopup(ConfirmPopupId);
            _openConfirm = false;
        }

        bool keepOpen = true;
        if (!ImGui.BeginPopupModal(ConfirmPopupId, ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted(_confirmText);
        ImGui.Spacing();
        if (ImGui.Button("Yes##spconfirm", new Vector2(90, 0)))
        {
            Action? action = _confirmAction;
            _confirmAction = null;
            ImGui.CloseCurrentPopup();
            action?.Invoke();
        }
        ImGui.SameLine();
        if (ImGui.Button("No##spconfirm", new Vector2(90, 0)))
        {
            _confirmAction = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
