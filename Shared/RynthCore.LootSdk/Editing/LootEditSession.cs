using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

/// <summary>
/// One open loot profile being edited (RynthCore docs/IMGUI_LOOT_EDITOR.md):
/// load, edit commands, validation and a save that never clobbers a file that
/// changed on disk. Pure data and file I/O, no plugin or AC types, so it runs
/// in RynthAi and in the offline tests alike. Not thread-safe: the caller
/// serialises calls (RynthAi's bridge holds a lock).
///
/// .utl files are edited in the VTank model, which keeps every line it parsed,
/// and written back in the file's own encoding, line endings and BOM, so an
/// unedited profile saves byte-identical. A file the model can't reproduce
/// exactly opens read-only, as do .json profiles (their only writer is lossy).
/// </summary>
public sealed class LootEditSession
{
    private VTankLootProfile? _utl;
    private LootProfile? _json;
    private string _path = string.Empty;
    private FileStamp _stamp;
    private Encoding _encoding = new UTF8Encoding(false);
    private bool _bom, _crlf, _finalNewline = true;
    private bool _dirty, _changedOnDisk;
    private string _readOnlyReason = string.Empty;
    private string _message = string.Empty;
    private bool _messageOk = true;
    private long _messageSeq;
    private int _focus = -1;

    /// <summary>Bumped on every change a face could show.</summary>
    public long Revision { get; private set; } = 1;
    public string Path => _path;
    public bool IsOpen => _path.Length > 0;
    public bool IsDirty => _dirty;
    public bool IsReadOnly => _utl == null;
    public int RuleCount => Shown?.Rules.Count ?? _json?.Rules.Count ?? 0;
    /// <summary>The edited model (null for JSON / nothing open). Tests read it.</summary>
    public VTankLootProfile? Profile => _utl;
    /// <summary>The open .json profile (native format), else null.</summary>
    public LootProfile? JsonProfile => _json;
    /// <summary>"utl", "json", or empty when nothing is open.</summary>
    public string Format => !IsOpen ? string.Empty : _json != null ? "json" : "utl";
    /// <summary>Why edits are refused (empty when they aren't).</summary>
    public string ReadOnlyReason => _readOnlyReason;
    /// <summary>True when InsertAndSave can add a rule: an editable .utl, or a .json profile.</summary>
    public bool CanInsert => _utl != null || _json != null;
    /// <summary>The last message (what the last command or load did).</summary>
    public string Message => _message;
    public bool MessageOk => _messageOk;

    // =====================================================================
    //  Load
    // =====================================================================

    /// <summary>
    /// Opens <paramref name="path"/>: a .utl or .json file, or a .utl path that
    /// doesn't exist yet (a new, empty profile that Save creates). Unsaved edits
    /// block it unless <paramref name="force"/>. False (with a message) on failure.
    /// </summary>
    public bool Open(string path, bool force = false)
    {
        path = (path ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0) return Fail("No loot profile selected.");
        if (_dirty && !force) return Fail($"Unsaved changes in {FileName(_path)}. Save them or discard them first.");
        try { path = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) { return Fail($"Bad path {path}: {ex.Message}"); }

        bool json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (!json && !path.EndsWith(".utl", StringComparison.OrdinalIgnoreCase))
            return Fail($"{FileName(path)}: not a .utl or .json loot profile.");

        try
        {
            if (!File.Exists(path))
            {
                if (json) return Fail($"{FileName(path)} not found.");
                SetLoaded(path, new VTankLootProfile { FileVersion = 1 }, null, default, readOnlyReason: string.Empty);
                return Ok($"New profile. Save creates {FileName(path)}.");
            }

            byte[] bytes = File.ReadAllBytes(path);
            FileStamp stamp = FileStamp.Of(path, bytes);
            if (json)
            {
                LootProfile? jp = System.Text.Json.JsonSerializer.Deserialize(Decode(bytes, out _, out _), LootJsonContext.Default.LootProfile);
                SetLoaded(path, null, jp ?? new LootProfile(), stamp,
                    "JSON profiles are read-only here (saving JSON loses conditions). Use the external editor.");
                return Ok($"Opened {FileName(path)} ({RuleCount} rules), read-only.");
            }

            string text = Decode(bytes, out Encoding encoding, out bool bom);
            VTankLootProfile profile = VTankLootParser.LoadFromText(text);
            bool finalNewline = text.Length == 0 || text.EndsWith('\n');
            string reason = RoundTrips(text, profile, finalNewline) ? string.Empty
                : "This file has lines the editor would not write back the same way; it is read-only here so nothing is lost. Use the external editor.";
            SetLoaded(path, profile, null, stamp, reason);
            _encoding = encoding;
            _bom = bom;
            _crlf = text.Contains("\r\n", StringComparison.Ordinal);
            _finalNewline = finalNewline;
            return Ok(reason.Length == 0 ? $"Opened {FileName(path)} ({RuleCount} rules)." : $"Opened {FileName(path)} ({RuleCount} rules), read-only.");
        }
        catch (Exception ex)
        {
            return Fail($"Could not open {FileName(path)}: {ex.Message}");
        }
    }

    private void SetLoaded(string path, VTankLootProfile? utl, LootProfile? json, FileStamp stamp, string readOnlyReason)
    {
        _path = path;
        _utl = readOnlyReason.Length == 0 ? utl : null;
        _json = json;
        _readOnlyUtl = readOnlyReason.Length == 0 ? null : utl;
        _stamp = stamp;
        _encoding = new UTF8Encoding(false);
        _bom = false;
        _crlf = false;
        _finalNewline = true;
        _dirty = false;
        _changedOnDisk = false;
        _readOnlyReason = readOnlyReason;
        _focus = -1;
        Revision++;
    }

    // A .utl opened read-only (it doesn't round-trip): shown, never saved.
    private VTankLootProfile? _readOnlyUtl;

    private VTankLootProfile? Shown => _utl ?? _readOnlyUtl;

    /// <summary>
    /// True when writing the parsed profile gives the file's text back exactly,
    /// CRLF and a missing final newline aside (Encode restores both).
    /// </summary>
    private static bool RoundTrips(string text, VTankLootProfile profile, bool finalNewline)
    {
        string a = Norm(text);
        if (!finalNewline) a += "\n";
        return string.Equals(a, Norm(VTankLootWriter.Serialize(profile)), StringComparison.Ordinal);
    }

    private static string Norm(string s) => s.Replace("\r\n", "\n");

    /// <summary>UTF-8 when the bytes are valid UTF-8 (BOM noted), else Latin-1, which maps every byte to one char and back.</summary>
    internal static string Decode(byte[] bytes, out Encoding encoding, out bool bom)
    {
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        int start = bom ? 3 : 0;
        try
        {
            string s = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
            encoding = new UTF8Encoding(false);
            return s;
        }
        catch (DecoderFallbackException)
        {
            bom = false;
            encoding = Encoding.Latin1;
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // =====================================================================
    //  Disk watch
    // =====================================================================

    /// <summary>
    /// Looks at the file on disk (a stat, no read). Changed with no unsaved edits:
    /// reload it. Changed under unsaved edits: flag it (Save then needs Force).
    /// Returns true when anything changed. Call now and then (every couple of seconds).
    /// </summary>
    public bool CheckDisk()
    {
        if (!IsOpen || _changedOnDisk) return false;
        FileStamp now;
        try { now = FileStamp.Stat(_path); }
        catch { return false; }
        if (now.SameStat(_stamp)) return false;
        if (!_dirty)
        {
            if (!now.Exists) return false;   // deleted under a clean view: keep showing it; Save recreates
            string path = _path;
            Open(path, force: true);
            SetMessage($"{FileName(path)} changed on disk; reloaded ({RuleCount} rules).", ok: true);
            return true;
        }
        _changedOnDisk = true;
        SetMessage($"{FileName(_path)} changed on disk since you opened it. Reload (drops your edits) or Save anyway.", ok: false);
        return true;
    }

    // =====================================================================
    //  Commands
    // =====================================================================

    /// <summary>What a command did, for the bridge (Saved: hot-reload RynthAi).</summary>
    public enum Outcome { Refused, Changed, Saved, Opened }

    /// <summary>
    /// Applies one command. open and save take the path/flags in the command;
    /// the caller resolves an empty open path first. Every call sets the message.
    /// </summary>
    public Outcome Apply(LootEditCommand cmd)
    {
        switch (cmd.Op)
        {
            case "open":
                return Open(cmd.Path, cmd.Force) ? Outcome.Opened : Outcome.Refused;
            case "reload":
                if (!IsOpen) { Fail("No profile open."); return Outcome.Refused; }
                return Open(_path, cmd.Force) ? Outcome.Opened : Outcome.Refused;
            case "save":
                return Save(cmd.Force) ? Outcome.Saved : Outcome.Refused;
        }

        if (_utl == null)
        {
            Fail(IsOpen ? _readOnlyReason : "No profile open.");
            return Outcome.Refused;
        }
        List<VTankLootRule> rules = _utl.Rules;

        if (cmd.Op == "add")
        {
            int at = cmd.Index < 0 || cmd.Index >= rules.Count ? rules.Count : cmd.Index + 1;
            rules.Insert(at, NewRule("New Rule"));
            return Changed($"Added a rule at {at + 1}.", at);
        }

        if (cmd.Index < 0 || cmd.Index >= rules.Count)
        {
            Fail("That rule is no longer there. The list has been refreshed.");
            return Outcome.Refused;
        }
        VTankLootRule rule = rules[cmd.Index];
        if (cmd.Expect != null && !string.Equals(cmd.Expect, rule.Name, StringComparison.Ordinal))
        {
            Fail("The list changed under that edit; it was not applied. Try again.");
            return Outcome.Refused;
        }

        switch (cmd.Op)
        {
            case "set_enabled":
            {
                bool on = string.Equals(cmd.Value, "true", StringComparison.OrdinalIgnoreCase);
                if (rule.Enabled == on) return Changed(string.Empty, cmd.Index, quiet: true);
                SetEnabled(rule, on);
                return Changed($"{(on ? "Enabled" : "Disabled")} \"{rule.Name}\".", cmd.Index);
            }
            case "move":
            {
                int to = Math.Clamp(cmd.To, 0, rules.Count - 1);
                if (to == cmd.Index) return Changed(string.Empty, to, quiet: true);
                rules.RemoveAt(cmd.Index);
                rules.Insert(to, rule);
                return Changed($"Moved \"{rule.Name}\" to {to + 1}.", to);
            }
            case "duplicate":
            {
                VTankLootRule copy = CloneRule(rule);
                copy.Name = rule.Name + " (copy)";
                rules.Insert(cmd.Index + 1, copy);
                return Changed($"Duplicated \"{rule.Name}\".", cmd.Index + 1);
            }
            case "delete":
                rules.RemoveAt(cmd.Index);
                return Changed($"Deleted \"{rule.Name}\".", Math.Min(cmd.Index, rules.Count - 1));
            case "rename":
            {
                // Kept as typed (no trim): real profiles have unnamed rules and trailing spaces.
                string name = OneLine(cmd.Value);
                rule.Name = name;
                return Changed($"Renamed to \"{name}\".", cmd.Index);
            }
            case "update_rule":
            {
                if (cmd.Rule == null) { Fail("No rule in the update."); return Outcome.Refused; }
                string? error = ApplyRule(rule, cmd.Rule);
                if (error != null) { Fail(error); return Outcome.Refused; }
                return Changed($"Updated \"{rule.Name}\" (not saved yet).", cmd.Index);
            }
        }
        Fail($"Unknown loot edit command '{cmd.Op}'.");
        return Outcome.Refused;
    }

    private VTankLootRule NewRule(string name) => new()
    {
        Name = name,
        CustomExpression = _utl != null && _utl.FileVersion >= 1 ? string.Empty : null,
        Action = VTankLootAction.Keep,
    };

    private static VTankLootRule CloneRule(VTankLootRule src)
    {
        var copy = new VTankLootRule
        {
            Name = src.Name, CustomExpression = src.CustomExpression, Priority = src.Priority,
            Action = src.Action, KeepCount = src.KeepCount,
        };
        foreach (VTankLootCondition c in src.Conditions)
            copy.Conditions.Add(new VTankLootCondition(c.NodeType, c.LengthCode, c.DataLines));
        return copy;
    }

    /// <summary>
    /// Disabling sets an existing DisabledRule node to "true" where it is (else
    /// appends one); enabling removes them, as the SDK and VTank do.
    /// </summary>
    public static void SetEnabled(VTankLootRule rule, bool enabled)
    {
        if (enabled)
        {
            rule.Enabled = true;
            return;
        }
        foreach (VTankLootCondition c in rule.Conditions)
            if (c.NodeType == VTankNodeTypes.DisabledRule)
            {
                if (c.DataLines.Count == 0) c.DataLines.Add("true");
                else c.DataLines[0] = "true";
                return;
            }
        rule.Conditions.Add(new VTankLootCondition(VTankNodeTypes.DisabledRule, "0", new[] { "true" }));
    }

    /// <summary>Replaces <paramref name="rule"/> with <paramref name="dto"/> after checking it. Null, or what is wrong.</summary>
    private string? ApplyRule(VTankLootRule rule, LootEditRule dto)
    {
        string name = OneLine(dto.Name);   // unnamed rules are common (LootSnobV4 has 152)
        if (!LootRuleText.IsKnownAction(dto.Action)) return $"Unknown action {dto.Action}.";
        var action = (VTankLootAction)dto.Action;
        if (action == VTankLootAction.KeepUpTo && dto.KeepCount < 0) return "Keep # needs a count of 0 or more.";

        var conditions = new List<VTankLootCondition>(dto.Conditions.Count);
        for (int i = 0; i < dto.Conditions.Count; i++)
        {
            LootEditCondition c = dto.Conditions[i];
            var lines = new List<string>(c.Lines.Count);
            foreach (string line in c.Lines) lines.Add(OneLine(line));
            int arity = VTankNodeTypes.GetDataLineCount(c.NodeType);
            bool unchanged = HasSame(rule, c.NodeType, lines);
            if (arity < 0 && !unchanged)
                return $"Condition {i + 1}: unknown node type {c.NodeType}.";
            if (arity >= 0 && lines.Count != arity)
            {
                if (lines.Count > arity) return $"Condition {i + 1} ({LootRuleText.NodeName(c.NodeType)}) has {lines.Count} lines; it takes {arity}.";
                List<string> defaults = LootRuleText.DefaultLines(c.NodeType);
                while (lines.Count < arity) lines.Add(defaults[lines.Count]);
            }
            if (!unchanged && LootRuleText.EditorFor(c.NodeType).Editor != "raw")
            {
                string? bad = CheckNumbers(c.NodeType, lines);
                if (bad != null) return $"Condition {i + 1} ({LootRuleText.NodeName(c.NodeType)}): {bad}";
            }
            conditions.Add(new VTankLootCondition(c.NodeType, OneLine(c.LengthCode ?? "0"), lines));
        }

        rule.Name = name;
        rule.Action = action;
        rule.KeepCount = action == VTankLootAction.KeepUpTo ? dto.KeepCount : null;
        rule.Priority = dto.Priority;
        if (rule.CustomExpression != null || _utl!.FileVersion >= 1)
            rule.CustomExpression = OneLine(dto.CustomExpression ?? string.Empty);
        rule.Conditions = conditions;
        if (rule.Enabled != dto.Enabled) SetEnabled(rule, dto.Enabled);
        return null;
    }

    private static bool HasSame(VTankLootRule rule, int nodeType, List<string> lines)
    {
        foreach (VTankLootCondition c in rule.Conditions)
        {
            if (c.NodeType != nodeType || c.DataLines.Count != lines.Count) continue;
            bool same = true;
            for (int i = 0; i < lines.Count && same; i++) same = string.Equals(c.DataLines[i], lines[i], StringComparison.Ordinal);
            if (same) return true;
        }
        return false;
    }

    private static string? CheckNumbers(int type, List<string> lines)
    {
        string[] labels = LootRuleText.LineLabels(type);
        for (int i = 0; i < lines.Count; i++)
        {
            if (LootRuleText.IsTextLine(type, i)) continue;
            string v = lines[i].Trim();
            string label = i < labels.Length ? labels[i] : $"line {i + 1}";
            if (LootRuleText.IsIntLine(type, i))
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return $"{label} must be a whole number (got \"{v}\").";
            }
            else if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return $"{label} must be a number (got \"{v}\").";
            lines[i] = v;
        }
        return null;
    }

    /// <summary>Newlines would break the line-based file: they become spaces.</summary>
    private static string OneLine(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    // =====================================================================
    //  Save
    // =====================================================================

    /// <summary>
    /// Writes the profile: refuses when the file changed on disk since it was
    /// loaded (unless <paramref name="force"/>), writes a temp file and swaps it
    /// in (the old file kept as .bak).
    /// </summary>
    public bool Save(bool force = false)
    {
        _writeError = null;
        if (!IsOpen) return Fail("No profile open.");
        if (_utl == null) return Fail(_readOnlyReason);
        try
        {
            FileStamp now = FileStamp.Read(_path);
            if (!force && !now.SameContent(_stamp))
            {
                _changedOnDisk = true;
                return Fail($"{FileName(_path)} changed on disk since you opened it. Reload (drops your edits) or Save anyway.");
            }

            WriteFile(_path, Encode());
            _stamp = FileStamp.Read(_path);
            _dirty = false;
            _changedOnDisk = false;
            return Ok($"Saved {FileName(_path)} ({RuleCount} rules).");
        }
        catch (Exception ex)
        {
            _writeError = ex;
            return Fail($"Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The exception of the last save that threw (folder not writable, file in use, ...), once:
    /// the in-game bridge hands it to RynthAi's save-failure report (2026-10-13). Null when
    /// the last save failed for another reason or succeeded.
    /// </summary>
    public Exception? TakeWriteError()
    {
        Exception? e = _writeError;
        _writeError = null;
        return e;
    }

    private Exception? _writeError;

    /// <summary>
    /// Clears the ReadOnly attribute of a file we are about to replace (a read-only profile,
    /// its .bak or a stale .tmp can't be overwritten). Never throws.
    /// </summary>
    internal static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            FileAttributes a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    /// <summary>Writes a temp file and swaps it in, the old file kept as .bak.</summary>
    private static void WriteFile(string path, byte[] bytes)
    {
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        ClearReadOnly(tmp);
        ClearReadOnly(path);
        ClearReadOnly(path + ".bak");
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); }
            catch (PlatformNotSupportedException) { File.Copy(tmp, path, overwrite: true); File.Delete(tmp); }
            catch (IOException) { File.Copy(path, path + ".bak", overwrite: true); File.Copy(tmp, path, overwrite: true); File.Delete(tmp); }
        }
        else File.Move(tmp, path);
    }

    // =====================================================================
    //  Add one rule and save (the "Add to loot profile" of a clicked item)
    // =====================================================================

    /// <summary>
    /// Inserts <paramref name="rule"/> at <paramref name="index"/> (clamped; the
    /// end when out of range) and saves at once, the old file kept as .bak.
    /// .utl: through the edited model, refused while there are unsaved edits (so
    /// it never saves someone's half-done changes with it) or when the file opens
    /// read-only. .json: <paramref name="native"/> is spliced into the file's
    /// Rules array as text, so every other byte stays as it was (the JSON writer
    /// itself is lossy). Both check before writing that the result reads back
    /// with every other rule unchanged; nothing is written when it doesn't.
    /// </summary>
    public bool InsertAndSave(VTankLootRule rule, LootRule? native, int index)
    {
        if (!IsOpen) return Fail("No profile open.");
        if (_dirty) return Fail($"Unsaved changes in {FileName(_path)}. Save or discard them in the Loot Editor first.");
        if (_json != null)
            return native == null ? Fail("That rule can't be written in the native format.") : InsertJson(native, index);
        if (_utl == null) return Fail(_readOnlyReason);
        if (!_stamp.Exists) return Fail($"{FileName(_path)} doesn't exist yet. Save it once in the Loot Editor first.");

        List<VTankLootRule> rules = _utl.Rules;
        int at = index < 0 || index > rules.Count ? rules.Count : index;
        if (_utl.FileVersion < 1) rule.CustomExpression = null;
        else rule.CustomExpression ??= string.Empty;

        var before = new List<string>(rules.Count);
        foreach (VTankLootRule r in rules) before.Add(RuleText(_utl, r));
        rules.Insert(at, rule);
        string? bad = VerifyUtl(before, at);
        if (bad != null)
        {
            rules.RemoveAt(at);
            return Fail("Not added (the profile would not read back the same): " + bad);
        }
        _dirty = true;
        if (!Save())
        {
            string why = _message;
            rules.RemoveAt(at);
            _dirty = false;
            if (_changedOnDisk)
            {
                // Someone else wrote the file: show theirs (nothing of ours is pending).
                Open(_path, force: true);
                return Fail($"Not added: {FileName(_path)} changed on disk; reloaded it. Try again.");
            }
            return Fail("Not added. " + why);
        }
        _focus = at;
        return Ok($"Added \"{rule.Name}\" at {at + 1} and saved {FileName(_path)} ({RuleCount} rules).");
    }

    private static string RuleText(VTankLootProfile owner, VTankLootRule r) =>
        VTankLootWriter.Serialize(new VTankLootProfile { FileVersion = owner.FileVersion, Rules = { r } });

    /// <summary>Null when the model, written and parsed back, has the old rules unchanged and the new one at <paramref name="at"/>.</summary>
    private string? VerifyUtl(List<string> before, int at)
    {
        VTankLootProfile back;
        try { back = VTankLootParser.LoadFromText(Decode(Encode(), out _, out _)); }
        catch (Exception ex) { return ex.Message; }
        if (back.Rules.Count != before.Count + 1) return $"{back.Rules.Count} rules instead of {before.Count + 1}.";
        if (!SameSalvage(back.SalvageCombine, _utl!.SalvageCombine)) return "the salvage combine settings changed.";
        for (int i = 0, j = 0; i < back.Rules.Count; i++)
        {
            string text = RuleText(back, back.Rules[i]);
            if (i == at)
            {
                if (text != RuleText(_utl, _utl.Rules[at])) return "the new rule reads back differently.";
                continue;
            }
            if (text != before[j++]) return $"rule {i + 1} would change.";
        }
        return null;
    }

    private static bool SameSalvage(SalvageCombineSettings? a, SalvageCombineSettings? b)
    {
        if (a == null || b == null) return a == null && b == null;
        string Text(SalvageCombineSettings s) => VTankLootWriter.Serialize(new VTankLootProfile { FileVersion = 1, SalvageCombine = s });
        return Text(a) == Text(b);
    }

    private bool InsertJson(LootRule native, int index)
    {
        _writeError = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(_path);
            if (!FileStamp.Of(_path, bytes).SameContent(_stamp))
            {
                Open(_path, force: true);
                return Fail($"{FileName(_path)} changed on disk; reloaded it. Try again.");
            }
            byte[]? spliced = LootJsonSplice.Insert(bytes, native, index, out int at, out string error);
            if (spliced == null) return Fail("Not added: " + error);
            WriteFile(_path, spliced);

            _json = System.Text.Json.JsonSerializer.Deserialize(Decode(spliced, out _, out _), LootJsonContext.Default.LootProfile) ?? new LootProfile();
            _stamp = FileStamp.Read(_path);
            _changedOnDisk = false;
            _focus = at;
            Revision++;
            return Ok($"Added \"{native.Name}\" at {at + 1} and saved {FileName(_path)} ({RuleCount} rules).");
        }
        catch (Exception ex)
        {
            _writeError = ex;
            return Fail($"Not added: {ex.Message}");
        }
    }

    /// <summary>The file's bytes as Save writes them (its own encoding, line endings, BOM).</summary>
    public byte[] Encode()
    {
        if (_utl == null) return Array.Empty<byte>();
        string text = VTankLootWriter.Serialize(_utl);
        if (!_finalNewline && text.EndsWith('\n')) text = text.Substring(0, text.Length - 1);
        if (_crlf) text = text.Replace("\n", "\r\n");
        byte[] body = _encoding.GetBytes(text);
        if (!_bom) return body;
        byte[] withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        Array.Copy(body, 0, withBom, 3, body.Length);
        return withBom;
    }

    // =====================================================================
    //  What the face sees
    // =====================================================================

    /// <summary>The header and one row per rule. <paramref name="inUsePath"/>: RynthAi's loot profile.</summary>
    public LootEditState BuildState(string inUsePath, List<LootEditFile>? files)
    {
        var s = new LootEditState
        {
            Revision = Revision,
            Path = _path,
            FileName = FileName(_path),
            Format = !IsOpen ? string.Empty : _json != null ? "json" : "utl",
            Exists = _stamp.Exists,
            ReadOnly = IsOpen && _utl == null,
            ReadOnlyReason = _readOnlyReason,
            Dirty = _dirty,
            ChangedOnDisk = _changedOnDisk,
            InUsePath = inUsePath ?? string.Empty,
            InUse = IsOpen && SamePath(_path, inUsePath),
            Message = _message,
            MessageOk = _messageOk,
            MessageSeq = _messageSeq,
            Focus = _focus,
        };
        if (files != null) s.Files = files;

        if (Shown is VTankLootProfile p)
        {
            s.Rules.Capacity = p.Rules.Count;
            foreach (VTankLootRule r in p.Rules)
                s.Rules.Add(new LootEditRow
                {
                    Name = r.Name, Enabled = r.Enabled, Action = (int)r.Action, KeepCount = r.KeepCount ?? 0,
                    Summary = LootRuleText.Summary(r), Conditions = CountConditions(r),
                });
        }
        else if (_json != null)
        {
            foreach (LootRule r in _json.Rules)
                s.Rules.Add(new LootEditRow
                {
                    Name = r.Name, Enabled = r.Enabled, Action = (int)JsonAction(r.Action), KeepCount = r.KeepCount,
                    Summary = r.Conditions.Count == 0 ? "Matches every item" : string.Join(" AND ", r.Conditions),
                    Conditions = r.Conditions.Count,
                });
        }
        return s;
    }

    private static int CountConditions(VTankLootRule r)
    {
        int n = 0;
        foreach (VTankLootCondition c in r.Conditions) if (c.NodeType != VTankNodeTypes.DisabledRule) n++;
        return n;
    }

    /// <summary>The whole rule at <paramref name="index"/>, or null.</summary>
    public LootEditRule? BuildRule(int index)
    {
        if (Shown is VTankLootProfile p)
        {
            if (index < 0 || index >= p.Rules.Count) return null;
            VTankLootRule r = p.Rules[index];
            var dto = new LootEditRule
            {
                Index = index, Name = r.Name, Enabled = r.Enabled, Action = (int)r.Action, KeepCount = r.KeepCount ?? 0,
                Priority = r.Priority, CustomExpression = r.CustomExpression,
            };
            foreach (VTankLootCondition c in r.Conditions)
                dto.Conditions.Add(new LootEditCondition { NodeType = c.NodeType, LengthCode = c.LengthCode, Lines = new List<string>(c.DataLines) });
            return dto;
        }
        if (_json != null && index >= 0 && index < _json.Rules.Count)
        {
            LootRule r = _json.Rules[index];
            var dto = new LootEditRule
            {
                Index = index, Name = r.Name, Enabled = r.Enabled, Action = (int)JsonAction(r.Action), KeepCount = r.KeepCount,
            };
            foreach (LootCondition c in r.Conditions)
                dto.Conditions.Add(new LootEditCondition { NodeType = -1, Lines = new List<string> { c.ToString() ?? string.Empty } });
            return dto;
        }
        return null;
    }

    private static VTankLootAction JsonAction(LootAction a) => a switch
    {
        LootAction.Sell => VTankLootAction.Sell,
        LootAction.Salvage => VTankLootAction.Salvage,
        LootAction.Read => VTankLootAction.Read,
        LootAction.KeepUpTo => VTankLootAction.KeepUpTo,
        _ => VTankLootAction.Keep,
    };

    // =====================================================================
    //  Helpers
    // =====================================================================

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(System.IO.Path.GetFullPath(a.Trim().Trim('"')), System.IO.Path.GetFullPath(b.Trim().Trim('"')), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string FileName(string path) => path.Length == 0 ? string.Empty : System.IO.Path.GetFileName(path);

    private Outcome Changed(string message, int focus, bool quiet = false)
    {
        if (!quiet)
        {
            _dirty = true;
            _focus = focus;
            SetMessage(message, ok: true);
        }
        return quiet ? Outcome.Refused : Outcome.Changed;
    }

    private bool Ok(string message)
    {
        SetMessage(message, ok: true);
        return true;
    }

    private bool Fail(string message)
    {
        SetMessage(message, ok: false);
        return false;
    }

    private void SetMessage(string message, bool ok)
    {
        _message = message;
        _messageOk = ok;
        _messageSeq++;
        Revision++;
    }

    /// <summary>What we know of the file: size and write time (a stat), plus a content hash when read.</summary>
    private readonly record struct FileStamp(bool Exists, long Length, DateTime WriteUtc, ulong Hash)
    {
        public static FileStamp Stat(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new FileStamp(true, fi.Length, fi.LastWriteTimeUtc, 0) : default;
        }

        public static FileStamp Read(string path)
        {
            if (!File.Exists(path)) return default;
            return Of(path, File.ReadAllBytes(path));
        }

        public static FileStamp Of(string path, byte[] bytes)
        {
            var fi = new FileInfo(path);
            return new FileStamp(true, bytes.Length, fi.LastWriteTimeUtc, Fnv(bytes));
        }

        public bool SameStat(FileStamp other) => Exists == other.Exists && (!Exists || (Length == other.Length && WriteUtc == other.WriteUtc));

        public bool SameContent(FileStamp other) => Exists == other.Exists && (!Exists || (Length == other.Length && Hash == other.Hash));

        private static ulong Fnv(byte[] bytes)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in bytes) { h ^= b; h *= 1099511628211UL; }
            return h;
        }
    }
}
