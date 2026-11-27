// ============================================================================
//  RynthAi - CommandButtons/CommandButtons.cs
//  Command Buttons (2026-11-26, a player's ask: "a configurable window of blank
//  buttons you can program with simple 1 line chat commands"): a grid of
//  buttons, each a label and one chat line (/ra start, *snow angel*, @home,
//  /lua start threads ...) sent as if typed in the chat box.
//
//  Three lists, as the Timed Commands keep them: this character, every
//  character on this server, and every character. The window shows all three
//  (this character's first). Files beside the settings profiles, so switching
//  a settings profile keeps them:
//    SettingsProfiles\<server>\<character>\command_buttons.json (with the grid's
//      layout: columns, button width and height),
//    SettingsProfiles\<server>\command_buttons.json and
//    SettingsProfiles\command_buttons.json.
//  "Copy from" takes another character's buttons (same server) into this
//  character's list.
//
//  Pure (no host, no ImGui): the host tests drive it.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.CommandButtons;

/// <summary>Which list a button is in.</summary>
internal enum ButtonScope { Character = 0, Server = 1, All = 2 }

/// <summary>One button: what it says, the chat line it sends, its colour and whether it asks first.</summary>
internal sealed class CommandButton
{
    public const int MaxLabel = 32;

    public string Label = string.Empty;
    public string Command = string.Empty;
    /// <summary>The button's fill, ImGui ABGR with full alpha; 0 = the theme's button colour.</summary>
    public uint Color;
    /// <summary>Asks "Send ...?" before it sends (for a command that is hard to take back).</summary>
    public bool Confirm;

    /// <summary>What the button shows: its label, else its command, else "(empty)".</summary>
    public string Shown => Label.Length > 0 ? Label : Command.Length > 0 ? Command : "(empty)";

    public CommandButton Clone() => new() { Label = Label, Command = Command, Color = Color, Confirm = Confirm };

    /// <summary>A label: one line, no control characters, trimmed, at most <see cref="MaxLabel"/> characters.</summary>
    public static string CleanLabel(string? text)
    {
        string t = CommandLine.OneLine(text);
        return t.Length > MaxLabel ? t[..MaxLabel].TrimEnd() : t;
    }

    // ── Colours: "#RRGGBB" in the file, ImGui ABGR in memory ───────────

    /// <summary>"#RRGGBB" for an ABGR colour; "" for 0 (the theme's colour).</summary>
    public static string ColorText(uint abgr) => abgr == 0
        ? string.Empty
        : "#" + (abgr & 0xFF).ToString("X2", CultureInfo.InvariantCulture)
              + ((abgr >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)
              + ((abgr >> 16) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>"#RRGGBB" or "RRGGBB" to ABGR with full alpha. False (and 0) for anything else.</summary>
    public static bool TryParseColor(string? text, out uint abgr)
    {
        abgr = 0;
        string t = (text ?? string.Empty).Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length != 6 || !uint.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)) return false;
        uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        abgr = 0xFF000000u | (b << 16) | (g << 8) | r;
        return true;
    }

    /// <summary>Text that reads on <paramref name="fillAbgr"/>: near-black on a light fill, near-white on a dark one.</summary>
    public static uint TextOn(uint fillAbgr)
    {
        double r = fillAbgr & 0xFF, g = (fillAbgr >> 8) & 0xFF, b = (fillAbgr >> 16) & 0xFF;
        return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? 0xFF141414u : 0xFFF2F2F2u;
    }
}

/// <summary>The grid's shape (this character's file).</summary>
internal sealed class ButtonLayout
{
    /// <summary>Columns 0 = as many as fit the window's width.</summary>
    public const int ColumnsMin = 0, ColumnsMax = 12, WidthMin = 40, WidthMax = 320, HeightMin = 18, HeightMax = 96;
    public const int DefaultWidth = 110, DefaultHeight = 28;

    public int Columns;
    public int Width = DefaultWidth;
    public int Height = DefaultHeight;

    public void Clamp()
    {
        Columns = Math.Clamp(Columns, ColumnsMin, ColumnsMax);
        Width = Math.Clamp(Width, WidthMin, WidthMax);
        Height = Math.Clamp(Height, HeightMin, HeightMax);
    }

    public ButtonLayout Clone() => new() { Columns = Columns, Width = Width, Height = Height };

    /// <summary>
    /// How many columns the grid draws: <paramref name="columns"/> when set (1-12), else as many
    /// <paramref name="buttonWidth"/>-wide buttons with <paramref name="gap"/> between them as fit
    /// <paramref name="available"/> (at least one, at most 12).
    /// </summary>
    public static int ColumnsFor(int columns, float available, float buttonWidth, float gap)
    {
        if (columns > 0) return Math.Min(columns, ColumnsMax);
        if (!(buttonWidth > 0) || !float.IsFinite(available)) return 1;
        int fit = (int)Math.Floor((available + gap) / (buttonWidth + gap));
        return Math.Clamp(fit, 1, ColumnsMax);
    }

    /// <summary>"Auto" or "4".</summary>
    public static string ColumnsText(int columns) => columns > 0 ? columns.ToString(CultureInfo.InvariantCulture) : "Auto";
}

/// <summary>What a chat line is, as the chat box would send it.</summary>
internal enum CommandKind
{
    /// <summary>Nothing to send.</summary>
    Empty,
    /// <summary>A slash command: the game's own (/loc, /fillcomps), RynthCore's /rc, a plugin's (/ra, /lua, /rdef ...).</summary>
    Slash,
    /// <summary>An emote: *wave*, @e text, ; text.</summary>
    Emote,
    /// <summary>A server command: @home, @hp (the server runs it; nothing is said aloud).</summary>
    Server,
    /// <summary>Plain text: said aloud in local chat.</summary>
    Say,
}

/// <summary>A chat line checked: the line as it will be sent, what it is, and why it can't be sent (null: it can).</summary>
internal readonly record struct CommandCheck(string Line, CommandKind Kind, string? Problem)
{
    public bool CanSend => Problem == null && Kind != CommandKind.Empty;
}

/// <summary>The rules for a button's chat line.</summary>
internal static class CommandLine
{
    /// <summary>As long as a chat line the engine sends (its sections manifest's command limit, a timed command's).</summary>
    public const int MaxLength = 200;

    private static readonly string[] AtEmotes = { "@emotes", "@emote", "@em", "@e" };

    /// <summary>One line: line breaks and tabs become spaces, other control characters go, trimmed.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\r' or '\n' or '\t') sb.Append(' ');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>A command as stored: one line, at most <see cref="MaxLength"/> characters.</summary>
    public static string Clean(string? text)
    {
        string t = OneLine(text);
        return t.Length > MaxLength ? t[..MaxLength].TrimEnd() : t;
    }

    /// <summary>What <paramref name="line"/> (cleaned) is, as RynthCore's chat dispatcher routes it.</summary>
    public static CommandKind KindOf(string line)
    {
        if (line.Length == 0) return CommandKind.Empty;
        char first = line[0];
        if (first == '/') return CommandKind.Slash;
        if (first is ';' or ':') return line.Length > 1 ? CommandKind.Emote : CommandKind.Say;
        if (first == '*')
            return line.Length > 2 && line[^1] == '*' && line.AsSpan(1, line.Length - 2).Trim().Length > 0 ? CommandKind.Emote : CommandKind.Say;
        if (first == '@')
        {
            foreach (string e in AtEmotes)
                if (line.StartsWith(e, StringComparison.OrdinalIgnoreCase) && (line.Length == e.Length || char.IsWhiteSpace(line[e.Length])))
                    return CommandKind.Emote;
            return line.Length > 1 ? CommandKind.Server : CommandKind.Say;
        }
        return CommandKind.Say;
    }

    /// <summary>
    /// Checks a button's chat line. Refused: more than one line's worth (the cleaned line is what goes),
    /// longer than <see cref="MaxLength"/>, and a button that presses buttons ("/ra buttons press"),
    /// which could press itself forever. Empty is no problem while editing; it just sends nothing.
    /// </summary>
    public static CommandCheck Check(string? text)
    {
        string raw = text ?? string.Empty;
        string line = OneLine(raw);
        CommandKind kind = KindOf(line);
        if (kind == CommandKind.Empty) return new CommandCheck(line, kind, null);
        if (line.Length > MaxLength)
            return new CommandCheck(line[..MaxLength], kind, $"Too long: a chat line is at most {MaxLength} characters.");
        if (raw.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            return new CommandCheck(line, kind, "One line only: each button sends one chat line.");
        if (PressesButtons(line))
            return new CommandCheck(line, kind, "A button can't press buttons (it could press itself forever).");
        return new CommandCheck(line, kind, null);
    }

    /// <summary>"/ra buttons press ..." (or run), in any case and spacing.</summary>
    public static bool PressesButtons(string line)
    {
        string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 3 || !w[0].Equals("/ra", StringComparison.OrdinalIgnoreCase)) return false;
        if (!(w[1].Equals("buttons", StringComparison.OrdinalIgnoreCase) || w[1].Equals("button", StringComparison.OrdinalIgnoreCase))) return false;
        int at = 2;
        if (w[at].ToLowerInvariant() is "server" or "all" or "char" or "character" or "me") at++;
        return w.Length > at && w[at].ToLowerInvariant() is "press" or "run";
    }

    /// <summary>What pressing it does, in a few words (the edit row's tooltip).</summary>
    public static string Describe(CommandKind kind) => kind switch
    {
        CommandKind.Slash => "A chat command, as if typed (the game's, RynthCore's /rc, or a plugin's: /ra, /lua, /rdef ...).",
        CommandKind.Emote => "An emote.",
        CommandKind.Server => "A server command (the server runs it; nothing is said aloud).",
        CommandKind.Say => "Plain text: said aloud in local chat.",
        _ => "No command yet: pressing it does nothing.",
    };
}

/// <summary>The three lists, the layout and their files.</summary>
internal sealed class CommandButtonSet
{
    public const string FileName = "command_buttons.json";
    public const int MaxPerList = 60;
    public const int FileVersion = 1;

    /// <summary>Locks every read and edit (the window is on the render thread, presses and saves on the tick).</summary>
    public readonly object Sync = new();
    private readonly List<CommandButton>[] _lists = { new(), new(), new() };
    private readonly string[] _paths = { string.Empty, string.Empty, string.Empty };
    private readonly bool[] _dirty = new bool[3];

    /// <summary>The grid's shape, saved with this character's list. Lock <see cref="Sync"/>.</summary>
    public ButtonLayout Layout { get; private set; } = new();

    public string Character { get; private set; } = string.Empty;
    public string Server { get; private set; } = string.Empty;
    public bool Loaded { get; private set; }
    /// <summary>When the last edit was made (the tick saves a moment later).</summary>
    public long ChangedAtMs { get; private set; }

    /// <summary>The list for <paramref name="scope"/>; lock <see cref="Sync"/> around any use.</summary>
    public List<CommandButton> List(ButtonScope scope) => _lists[(int)scope];

    public static string ScopeName(ButtonScope s) => s switch
    {
        ButtonScope.Character => "this character",
        ButtonScope.Server => "this server",
        _ => "all characters",
    };

    /// <summary>
    /// Loads the three lists for the character whose settings folder is <paramref name="charFolder"/>
    /// (SettingsProfiles\&lt;server&gt;\&lt;character&gt;). A missing file is an empty list; one that can't be
    /// read is reported in <paramref name="problems"/> and left alone (it isn't saved over until edited).
    /// </summary>
    public void Load(string charFolder, List<string>? problems = null)
    {
        string? serverFolder = Path.GetDirectoryName(charFolder);
        string? baseFolder = serverFolder != null ? Path.GetDirectoryName(serverFolder) : null;
        lock (Sync)
        {
            Character = Path.GetFileName(charFolder);
            Server = serverFolder != null ? Path.GetFileName(serverFolder) : string.Empty;
            _paths[(int)ButtonScope.Character] = Path.Combine(charFolder, FileName);
            _paths[(int)ButtonScope.Server] = serverFolder != null ? Path.Combine(serverFolder, FileName) : string.Empty;
            _paths[(int)ButtonScope.All] = baseFolder != null ? Path.Combine(baseFolder, FileName) : string.Empty;
            Layout = new ButtonLayout();
            for (int i = 0; i < 3; i++)
            {
                _lists[i].Clear();
                _dirty[i] = false;
                if (_paths[i].Length == 0 || !File.Exists(_paths[i])) continue;
                try
                {
                    _lists[i].AddRange(Parse(File.ReadAllText(_paths[i]), out ButtonLayout? layout));
                    if (i == (int)ButtonScope.Character && layout != null) Layout = layout;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    problems?.Add($"{ScopeName((ButtonScope)i)} ({_paths[i]}): {ex.Message}");
                }
            }
            Loaded = true;
        }
    }

    /// <summary>Forgets the character (logout).</summary>
    public void Unload()
    {
        lock (Sync)
        {
            for (int i = 0; i < 3; i++) { _lists[i].Clear(); _paths[i] = string.Empty; _dirty[i] = false; }
            Layout = new ButtonLayout();
            Character = Server = string.Empty;
            Loaded = false;
        }
    }

    /// <summary>A list (or, for this character, the layout) was edited. Call under <see cref="Sync"/>.</summary>
    public void MarkChanged(ButtonScope scope, long nowMs)
    {
        _dirty[(int)scope] = true;
        ChangedAtMs = nowMs;
    }

    public bool AnyDirty { get { lock (Sync) return _dirty[0] || _dirty[1] || _dirty[2]; } }

    /// <summary>Writes every edited list (temp file, then replace). Problems go to <paramref name="problems"/>.</summary>
    public void SaveDirty(List<string>? problems = null)
    {
        lock (Sync)
        {
            for (int i = 0; i < 3; i++)
            {
                if (!_dirty[i] || _paths[i].Length == 0) continue;
                _dirty[i] = false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_paths[i])!);
                    string tmp = _paths[i] + ".tmp";
                    File.WriteAllText(tmp, Serialize(_lists[i], i == (int)ButtonScope.Character ? Layout : null));
                    File.Move(tmp, _paths[i], overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems?.Add($"{ScopeName((ButtonScope)i)} ({_paths[i]}): {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Adds a button to <paramref name="scope"/>'s list and marks it changed. Null when the list is full
    /// (<see cref="MaxPerList"/>). Call under <see cref="Sync"/>.
    /// </summary>
    public CommandButton? Add(ButtonScope scope, string? label, string? command, long nowMs)
    {
        List<CommandButton> list = List(scope);
        if (list.Count >= MaxPerList) return null;
        var b = new CommandButton { Label = CommandButton.CleanLabel(label), Command = CommandLine.Clean(command) };
        list.Add(b);
        MarkChanged(scope, nowMs);
        return b;
    }

    /// <summary>Moves button <paramref name="index"/> by <paramref name="delta"/> places in its list. False when it can't.</summary>
    public bool Move(ButtonScope scope, int index, int delta, long nowMs)
    {
        List<CommandButton> list = List(scope);
        int to = index + delta;
        if (index < 0 || index >= list.Count || to < 0 || to >= list.Count || delta == 0) return false;
        CommandButton b = list[index];
        list.RemoveAt(index);
        list.Insert(to, b);
        MarkChanged(scope, nowMs);
        return true;
    }

    // ── Copy from another character ─────────────────────────────────────

    /// <summary>
    /// The other characters on this server with buttons of their own (their folder names, sorted), for
    /// "Copy from". Empty before a load.
    /// </summary>
    public List<string> OtherCharacters()
    {
        var names = new List<string>();
        string own, serverFolder;
        lock (Sync)
        {
            own = _paths[(int)ButtonScope.Character];
            serverFolder = own.Length > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(own)) ?? string.Empty : string.Empty;
        }
        if (serverFolder.Length == 0 || !Directory.Exists(serverFolder)) return names;
        try
        {
            foreach (string dir in Directory.GetDirectories(serverFolder))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals(Character, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(dir, FileName))) names.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Appends <paramref name="character"/>'s own buttons (same server) to this character's list, as many as
    /// fit. Returns how many were added; -1 when that character's buttons can't be read (the reason in
    /// <paramref name="problem"/>).
    /// </summary>
    public int CopyFrom(string character, long nowMs, out string problem)
    {
        problem = string.Empty;
        string own;
        lock (Sync) own = _paths[(int)ButtonScope.Character];
        string? serverFolder = own.Length > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(own)) : null;
        if (serverFolder == null || string.IsNullOrWhiteSpace(character) || character.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || character is "." or "..")
        {
            problem = "no such character";
            return -1;
        }
        string path = Path.Combine(serverFolder, character, FileName);
        List<CommandButton> theirs;
        try { theirs = Parse(File.ReadAllText(path), out _); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = ex is FileNotFoundException or DirectoryNotFoundException ? "they have no buttons" : ex.Message;
            return -1;
        }
        int added = 0;
        lock (Sync)
        {
            List<CommandButton> list = List(ButtonScope.Character);
            foreach (CommandButton b in theirs)
            {
                if (list.Count >= MaxPerList) break;
                list.Add(b.Clone());
                added++;
            }
            if (added > 0) MarkChanged(ButtonScope.Character, nowMs);
        }
        return added;
    }

    // ── File format ─────────────────────────────────────────────────────
    // {"version":1,"layout":{"columns","width","height"},"buttons":[{"label","command","color":"#RRGGBB","confirm"}]}
    // "layout" only in a character's file; "color" left out for the theme's colour.

    public static string Serialize(IReadOnlyList<CommandButton> list, ButtonLayout? layout)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FileVersion);
            if (layout != null)
            {
                w.WriteStartObject("layout");
                w.WriteNumber("columns", layout.Columns);
                w.WriteNumber("width", layout.Width);
                w.WriteNumber("height", layout.Height);
                w.WriteEndObject();
            }
            w.WriteStartArray("buttons");
            foreach (CommandButton b in list)
            {
                w.WriteStartObject();
                w.WriteString("label", b.Label);
                w.WriteString("command", b.Command);
                if (b.Color != 0) w.WriteString("color", CommandButton.ColorText(b.Color));
                w.WriteBoolean("confirm", b.Confirm);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The buttons in a file (cleaned, at most <see cref="MaxPerList"/>), and its layout if it has one (clamped).</summary>
    public static List<CommandButton> Parse(string json, out ButtonLayout? layout)
    {
        layout = null;
        var list = new List<CommandButton>();
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return list;
        if (root.TryGetProperty("layout", out JsonElement l) && l.ValueKind == JsonValueKind.Object)
        {
            layout = new ButtonLayout
            {
                Columns = Int(l, "columns", 0),
                Width = Int(l, "width", ButtonLayout.DefaultWidth),
                Height = Int(l, "height", ButtonLayout.DefaultHeight),
            };
            layout.Clamp();
        }
        if (!root.TryGetProperty("buttons", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (list.Count >= MaxPerList) break;
            if (e.ValueKind != JsonValueKind.Object) continue;
            var b = new CommandButton
            {
                Label = CommandButton.CleanLabel(Str(e, "label")),
                Command = CommandLine.Clean(Str(e, "command")),
                Confirm = e.TryGetProperty("confirm", out JsonElement c) && c.ValueKind == JsonValueKind.True,
            };
            if (CommandButton.TryParseColor(Str(e, "color"), out uint color)) b.Color = color;
            list.Add(b);
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)
            ? (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue)
            : fallback;
}
