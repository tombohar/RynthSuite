using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// RynthAi's side of the engine's ImGui Loot Editor (RynthCore
/// docs/IMGUI_LOOT_EDITOR.md): one LootEditSession, the profile paths, and the
/// hot reload after a save. The engine calls the exports (PluginExports, "Loot
/// editor bridge") from its UI data hub on the plugin pump thread, the same
/// thread as the plugin tick; the lock only guards against any other caller.
/// Nothing here touches AC: what it needs about items (the "Add to loot
/// profile" popup, /ra loot add) comes through <see cref="Items"/>.
/// </summary>
internal sealed class LootEditorBridge
{
    public static readonly string LootFolder = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "LootProfiles");

    private readonly object _sync = new();
    private readonly LootEditSession _session = new();
    private readonly Func<string> _inUsePath;
    private readonly Action<string> _onSaved;
    private readonly string[] _vendorFolders;

    // Follow RynthAi's loot profile while the editor shows it and has no edits.
    private bool _followInUse = true;
    private long _nextDiskCheck;
    private string? _vocabJson;

    // "Add to loot profile": the popup's current rule, and a second session for a
    // target profile the editor doesn't have open (so the editor's view never changes).
    private LootEditItemDraft? _draft;
    private long _draftRevision;
    private LootEditSession? _other;

    /// <summary>RynthAi's item lookups for "Add to loot profile". Set once by the plugin.</summary>
    public LootItemHooks Items { get; set; } = new();

    public LootEditorBridge(Func<string> inUsePath, Action<string> onSaved, params string[] vendorFolders)
    {
        _inUsePath = inUsePath;
        _onSaved = onSaved;
        _vendorFolders = vendorFolders;
    }

    private string InUse => (_inUsePath() ?? string.Empty).Trim().Trim('"');

    /// <summary>
    /// Cheap: the session's revision. Every ~2 s it also looks at the file on
    /// disk and follows a change of RynthAi's loot profile.
    /// </summary>
    public int Revision()
    {
        lock (_sync)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= _nextDiskCheck)
            {
                _nextDiskCheck = now + Stopwatch.Frequency * 2;
                _session.CheckDisk();
                string inUse = InUse;
                if (_followInUse && !_session.IsDirty && inUse.Length > 0 && !LootEditSession.SamePath(inUse, _session.Path))
                    _session.Open(inUse);
            }
            return unchecked((int)StateRevision);
        }
    }

    // The session's revision plus the popup draft's: both only grow, so the sum moves when either does.
    private long StateRevision => _session.Revision + _draftRevision;

    public string StateJson()
    {
        lock (_sync)
        {
            string inUse = InUse;
            if (!_session.IsOpen && _followInUse && inUse.Length > 0) _session.Open(inUse);
            LootEditState state = _session.BuildState(inUse, ListFiles());
            state.Revision = StateRevision;
            state.ItemDraft = _draft;
            return JsonSerializer.Serialize(state, LootEditJsonContext.Default.LootEditState);
        }
    }

    public string RuleJson(int index)
    {
        lock (_sync)
        {
            LootEditRule? rule = _session.BuildRule(index);
            return rule == null ? "{\"Index\":-1}" : JsonSerializer.Serialize(rule, LootEditJsonContext.Default.LootEditRule);
        }
    }

    public string VocabJson() =>
        _vocabJson ??= JsonSerializer.Serialize(LootRuleText.BuildVocab(), LootEditJsonContext.Default.LootEditVocab);

    public void Command(string json)
    {
        LootEditCommand? cmd = JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditCommand);
        if (cmd == null) return;
        if (cmd.Op is "item_preview" or "item_add" or "item_close")
        {
            ItemCommand(cmd);
            return;
        }
        string? saved = null;
        lock (_sync)
        {
            if (cmd.Op == "open")
            {
                string inUse = InUse;
                string path = cmd.Path.Trim().Trim('"');
                if (path.Length == 0) path = inUse;
                // Showing the panel again on the profile already open keeps its edits.
                if (_session.IsOpen && !cmd.Force && LootEditSession.SamePath(path, _session.Path))
                    return;
                cmd.Path = path;
                if (path.Length == 0)
                {
                    _followInUse = true;
                    _session.Apply(cmd);   // "No loot profile selected."
                    return;
                }
                if (_session.Apply(cmd) == LootEditSession.Outcome.Opened)
                    _followInUse = LootEditSession.SamePath(path, inUse);
                return;
            }
            if (_session.Apply(cmd) == LootEditSession.Outcome.Saved)
                saved = _session.Path;
            ReportWriteError(_session);
        }
        if (saved != null) _onSaved(saved);
    }

    // =====================================================================
    //  Add to loot profile (a clicked item)
    // =====================================================================

    private void ItemCommand(LootEditCommand cmd)
    {
        LootEditItemDraft? added = null;
        lock (_sync)
        {
            if (cmd.Op == "item_close")
            {
                if (_draft == null) return;
                _draft = null;
                _draftRevision++;
                return;
            }
            LootEditItemDraft d = BuildDraft(cmd.Item ?? new LootEditItemRequest(), cmd.Op == "item_add");
            _draft = d;
            _draftRevision++;
            if (d.Added) added = d;
        }
        if (added != null) Items.Added(added);
    }

    /// <summary>
    /// /ra loot add: the popup's preview (<paramref name="add"/> false) or Add,
    /// without touching the popup's draft. After an add, Items.Added has run
    /// (chat line, reload) before this returns.
    /// </summary>
    public LootEditItemDraft AddItem(LootEditItemRequest request, bool add)
    {
        LootEditItemDraft d;
        lock (_sync) d = BuildDraft(request, add);
        if (d.Added) Items.Added(d);
        return d;
    }

    /// <summary>The rule the item makes, where it goes, and (add) the insert and save. Under the lock.</summary>
    private LootEditItemDraft BuildDraft(LootEditItemRequest req, bool add)
    {
        var d = new LootEditItemDraft { Seq = req.Seq };
        uint id = req.ItemId != 0 ? req.ItemId : Items.SelectedItemId();
        if (id == 0) return Refuse(d, "No item selected. Click an item first.");
        d.ItemId = id;
        var match = (LootItemMatch)Math.Clamp(req.Match, 0, 2);
        d.IncludeT11 = req.IncludeT11;
        d.AttributesSupported = true;
        // The checklist shows every attribute whatever the match, so the item is always read in full.
        LootItemFacts? facts = Items.Facts(id, true, req.IncludeT11 || req.Choices != null);
        if (facts == null) return Refuse(d, $"RynthAi doesn't know item 0x{id:X8} yet. Select it again in a moment.");
        d.IsT11 = facts.T11?.IsT11 == true;
        d.ItemName = facts.Name;
        d.ClassName = LootItemRules.ClassName(facts.ObjectClass);
        d.Stackable = facts.Stackable;
        d.BuiltInReason = BuiltInSentence(Items.BuiltInKeep(id));

        string inUse = InUse;
        string target = req.ToOpenProfile && _session.IsOpen ? _session.Path : inUse;
        if (target.Length == 0) return Refuse(d, "No loot profile selected. Pick one in RynthAi first.");
        d.TargetPath = target;
        d.TargetFile = Path.GetFileName(target);
        d.TargetInUse = LootEditSession.SamePath(target, inUse);
        if (!File.Exists(target)) return Refuse(d, $"Loot profile not found: {target}");
        LootEditSession s = SessionFor(target);
        if (!s.IsOpen || !LootEditSession.SamePath(s.Path, target)) return Refuse(d, s.Message);
        d.Format = s.Format;
        if (!s.CanInsert) return Refuse(d, $"{d.TargetFile} is read-only here: {s.ReadOnlyReason}");

        var options = new LootItemRuleOptions
        {
            Match = match,
            Action = req.Action == 0 ? null : (VTankLootAction)req.Action,
            KeepCount = req.KeepCount < 0 ? null : req.KeepCount,
            RuleName = req.RuleName,
            IncludeT11 = req.IncludeT11,
            Choices = req.Choices,
        };
        LootItemRuleDraft? draft = LootItemRules.Build(facts, options, out string error);
        if (draft == null) return Refuse(d, error);
        foreach (LootItemAttribute row in draft.Attributes) d.Attributes.Add(row.ToWire());
        d.Match = (int)draft.Match;
        d.Action = (int)draft.Action;
        d.KeepCount = draft.KeepCount;
        d.RuleName = draft.Rule.Name;
        d.DefaultRuleName = draft.DefaultName;
        d.Preview = LootItemRules.PreviewLines(draft.Rule);
        d.Notes.AddRange(draft.Notes);

        bool json = s.Format == "json";
        LootRule? native = null;
        if (json)
        {
            var dropped = new List<string>();
            native = LootItemRules.ToNative(draft.Rule, dropped);
            foreach (string c in dropped) d.Notes.Add($"Left out (the native format has no such condition): {c}");
            if (native == null) return Refuse(d, "The native format can't hold any of this rule's conditions.");
        }

        // Just before the first rule that decides this item now (first match wins), else the end.
        int count = s.RuleCount;
        int first = json ? Items.FirstMatchNative(id, s.JsonProfile!) : Items.FirstMatchUtl(id, s.Profile!);
        if (first >= count) first = -1;
        int at = first >= 0 ? first : count;
        d.InsertAt = at;
        d.RuleCount = count;
        if (first >= 0)
        {
            string name = json ? s.JsonProfile!.Rules[first].Name : s.Profile!.Rules[first].Name;
            name = string.IsNullOrWhiteSpace(name) ? $"#{first + 1}" : name.Trim();
            string action = json ? s.JsonProfile!.Rules[first].Action.ToString() : LootRuleText.ActionName(s.Profile!.Rules[first].Action);
            bool same = json ? LootItemRules.SameRule(native!, s.JsonProfile!.Rules[first]) : LootItemRules.SameRule(draft.Rule, s.Profile!.Rules[first]);
            if (same) return Refuse(d, $"Already there: rule {first + 1} '{name}' does exactly this.");
            d.OrderNote = $"Goes in at {at + 1} of {count + 1}, just before rule {first + 1} '{name}' ({action}), which takes this item now. "
                + "Rules are checked top to bottom and the first match wins, so this is the lowest spot where the new rule still decides; the rules above it are untouched.";
        }
        else
            d.OrderNote = $"Goes in at the end ({at + 1} of {count + 1}): no rule takes this item now, so at the end it only gets items no other rule wants.";
        AppendBuiltIn(d);

        if (s.IsDirty) return Refuse(d, $"The Loot Editor has unsaved changes in {d.TargetFile}. Save or discard them first.");
        d.Ok = true;
        if (!add) return d;

        if (!s.InsertAndSave(draft.Rule, native, at))
        {
            ReportWriteError(s);
            return Refuse(d, s.Message);
        }
        d.Added = true;
        d.Message = $"Added loot rule '{draft.Rule.Name}' to {d.TargetFile}";
        return d;
    }

    /// <summary>
    /// A save the disk refused (folder not writable, file in use, ...): the editor shows the
    /// session's message; this adds the chat line (once per folder) and the log line with the
    /// reason and the fix (Plugins/Shared/DataWriteGuard.cs, 2026-10-13).
    /// </summary>
    private static void ReportWriteError(LootEditSession session)
    {
        Exception? error = session.TakeWriteError();
        if (error != null)
            RynthCore.Plugin.Shared.DataWriteGuard.Report("the loot profile", session.Path, error);
    }

    private static LootEditItemDraft Refuse(LootEditItemDraft d, string error)
    {
        d.Ok = false;
        d.Error = error;
        AppendBuiltIn(d);
        return d;
    }

    /// <summary>
    /// The popup's sentence for one of RynthAi's built-in keeps. "No rule matches" alone
    /// misled: a wand Mana Tap had looted read as looted for no reason (2026-10-05).
    /// </summary>
    internal static string BuiltInSentence(LootBuiltInKeep? keep)
    {
        if (keep == null || keep.Reason.Length == 0) return string.Empty;
        return keep.BeforeProfile
            ? $"RynthAi takes it before any loot profile rule: {keep.Reason}."
            : $"No rule in the loot profile in use takes it, but RynthAi keeps it anyway: {keep.Reason}.";
    }

    // OrderNote is what every engine's popup (and /ra loot add) shows, so the reason rides on it;
    // BuiltInReason carries it on its own for a face that wants it separately.
    private static void AppendBuiltIn(LootEditItemDraft d)
    {
        if (d.BuiltInReason.Length == 0 || d.OrderNote.EndsWith(d.BuiltInReason, StringComparison.Ordinal)) return;
        d.OrderNote = d.OrderNote.Length == 0 ? d.BuiltInReason : d.OrderNote + " " + d.BuiltInReason;
    }

    /// <summary>The editor's session when it has <paramref name="target"/> open, else a second one (fresh from disk).</summary>
    private LootEditSession SessionFor(string target)
    {
        if (_session.IsOpen && LootEditSession.SamePath(_session.Path, target))
        {
            _session.CheckDisk();
            return _session;
        }
        _other ??= new LootEditSession();
        if (_other.IsOpen && LootEditSession.SamePath(_other.Path, target)) _other.CheckDisk();
        else _other.Open(target, force: true);
        return _other;
    }

    /// <summary>LootProfiles (*.utl, *.json), then AutoVendor profiles, then the open file if it is elsewhere.</summary>
    private List<LootEditFile> ListFiles()
    {
        var files = new List<LootEditFile>();
        try
        {
            if (Directory.Exists(LootFolder))
            {
                var names = new List<string>();
                names.AddRange(Directory.GetFiles(LootFolder, "*.utl"));
                names.AddRange(Directory.GetFiles(LootFolder, "*.json"));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in names) files.Add(new LootEditFile { Path = f, Display = Path.GetFileName(f) });
            }
            foreach (string folder in _vendorFolders)
            {
                if (!Directory.Exists(folder)) continue;
                var names = new List<string>(Directory.GetFiles(folder, "*.utl", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in names)
                    files.Add(new LootEditFile { Path = f, Display = "AutoVendor: " + Path.GetRelativePath(folder, f) });
            }
        }
        catch { }
        if (_session.IsOpen && !files.Exists(f => LootEditSession.SamePath(f.Path, _session.Path)))
            files.Add(new LootEditFile { Path = _session.Path, Display = Path.GetFileName(_session.Path) });
        return files;
    }
}

/// <summary>What the loot editor bridge asks RynthAi about items (the plugin fills these in).</summary>
internal sealed class LootItemHooks
{
    /// <summary>The item selected in the game, or 0.</summary>
    public Func<uint> SelectedItemId { get; set; } = () => 0;
    /// <summary>
    /// The item's facts from the object cache, or null. The bools: "items like this" and
    /// "include T11 attributes"; either asks for an ID when the item isn't identified.
    /// </summary>
    public Func<uint, bool, bool, LootItemFacts?> Facts { get; set; } = (_, _, _) => null;
    /// <summary>The first rule of the .utl profile that matches the item now, or -1.</summary>
    public Func<uint, VTankLootProfile, int> FirstMatchUtl { get; set; } = (_, _) => -1;
    /// <summary>The first rule of the native profile that matches the item now, or -1.</summary>
    public Func<uint, LootProfile, int> FirstMatchNative { get; set; } = (_, _) => -1;
    /// <summary>A rule was added and saved: say so and reload the profile. Outside the bridge's lock.</summary>
    public Action<LootEditItemDraft> Added { get; set; } = _ => { };
    /// <summary>
    /// One of RynthAi's built-in keeps that takes the item now with the loot profile in use
    /// (the looter's own classification, run as a preview), or null.
    /// </summary>
    public Func<uint, LootBuiltInKeep?> BuiltInKeep { get; set; } = _ => null;
}

/// <summary>
/// A built-in keep: <see cref="Reason"/> says what and where to change it (ASCII);
/// <see cref="BeforeProfile"/> when it is checked ahead of the loot profile's rules
/// (Learn unknown spells, mana stones) rather than only when no rule matches (Mana Tap).
/// </summary>
internal sealed record LootBuiltInKeep(string Reason, bool BeforeProfile);
