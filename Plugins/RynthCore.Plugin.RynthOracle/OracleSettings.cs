using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthOracle;

/// <summary>
/// RynthOracle's settings, in %APPDATA%\RynthCore\rynthoracle.json. Written by hand and read
/// with JsonDocument (no reflection serializer: plugin types must never go through
/// System.Text.Json's serializer, which roots them across hot reloads).
/// </summary>
internal sealed class OracleSettings
{
    /// <summary>Send /myquests once after login (its reply hidden) so the quest tabs have data.</summary>
    public bool ReadQuestsAtLogin = true;
    /// <summary>Hide the server's reply to RynthOracle's own /myquests (a typed /myquests always shows).</summary>
    public bool HideOwnQuestReplies = true;
    /// <summary>Check once a day for a newer master quest list at upstream's address.</summary>
    public bool CheckQuestListDaily;
    public DateTime LastQuestListCheckUtc = DateTime.MinValue;
    /// <summary>Show the Void target window by itself for characters with Void Magic.</summary>
    public bool AutoShowVoidWindow = true;
    public bool MainWindowOpen;
    /// <summary>The Quest Timers window (tracked quests) was left open; it opens again at login.</summary>
    public bool QuestTimersOpen;
    /// <summary>The selected tab, by name (the set of tabs depends on the server).</summary>
    public string TabName = "Quests";
    /// <summary>Say so in chat when a tracked quest's timer comes up.</summary>
    public bool AlertTimers = true;
    /// <summary>false: only tracked quests are watched; true: every quest the character holds.</summary>
    public bool AlertAllTimers;

    /// <summary>Where the tests keep the settings file (a scratch path); null means the rynthoracle.json in RynthCore's %APPDATA% folder.</summary>
    internal static string? PathOverride;

    internal static string FilePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthoracle.json");

    /// <summary>The names this version reads and writes; any other value in the file is kept as it was.</summary>
    private static readonly string[] KnownNames =
    {
        "readQuestsAtLogin", "hideOwnQuestReplies", "checkQuestListDaily", "lastQuestListCheck", "autoShowVoidWindow",
        "mainWindowOpen", "questTimersOpen", "alertTimers", "alertAllTimers", "tabName",
    };
    // "conquestAutoDeposit" moved to RynthConquest (2026-10-20), which takes it over from this file once;
    // it is kept here (as any value this version doesn't use) until then.

    /// <summary>
    /// Values in the file this version doesn't use (a newer version's, or a setting that moved to
    /// another plugin, which reads it from here once), written back unchanged so a save never loses them.
    /// </summary>
    private readonly System.Collections.Generic.List<(string Name, string Raw)> _kept = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return;
            _kept.Clear();
            foreach (JsonProperty p in r.EnumerateObject())
                if (Array.IndexOf(KnownNames, p.Name) < 0 && !_kept.Exists(k => k.Name == p.Name)) _kept.Add((p.Name, p.Value.GetRawText()));
            ReadQuestsAtLogin = Bool(r, "readQuestsAtLogin", ReadQuestsAtLogin);
            HideOwnQuestReplies = Bool(r, "hideOwnQuestReplies", HideOwnQuestReplies);
            CheckQuestListDaily = Bool(r, "checkQuestListDaily", CheckQuestListDaily);
            AutoShowVoidWindow = Bool(r, "autoShowVoidWindow", AutoShowVoidWindow);
            MainWindowOpen = Bool(r, "mainWindowOpen", MainWindowOpen);
            QuestTimersOpen = Bool(r, "questTimersOpen", QuestTimersOpen);
            AlertTimers = Bool(r, "alertTimers", AlertTimers);
            AlertAllTimers = Bool(r, "alertAllTimers", AlertAllTimers);
            if (r.TryGetProperty("tabName", out JsonElement t) && t.ValueKind == JsonValueKind.String) TabName = t.GetString() ?? TabName;
            if (r.TryGetProperty("lastQuestListCheck", out JsonElement c) && c.ValueKind == JsonValueKind.String
                && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when))
                LastQuestListCheckUtc = when.ToUniversalTime();
        }
        catch
        {
            // A broken file means defaults; the next save rewrites it.
        }
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"readQuestsAtLogin\": ").Append(ReadQuestsAtLogin ? "true" : "false").Append(",\n");
            sb.Append("  \"hideOwnQuestReplies\": ").Append(HideOwnQuestReplies ? "true" : "false").Append(",\n");
            sb.Append("  \"checkQuestListDaily\": ").Append(CheckQuestListDaily ? "true" : "false").Append(",\n");
            sb.Append("  \"lastQuestListCheck\": \"").Append(LastQuestListCheckUtc.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
            sb.Append("  \"autoShowVoidWindow\": ").Append(AutoShowVoidWindow ? "true" : "false").Append(",\n");
            sb.Append("  \"mainWindowOpen\": ").Append(MainWindowOpen ? "true" : "false").Append(",\n");
            sb.Append("  \"questTimersOpen\": ").Append(QuestTimersOpen ? "true" : "false").Append(",\n");
            sb.Append("  \"alertTimers\": ").Append(AlertTimers ? "true" : "false").Append(",\n");
            sb.Append("  \"alertAllTimers\": ").Append(AlertAllTimers ? "true" : "false").Append(",\n");
            sb.Append("  \"tabName\": \"").Append(JsonEncodedText.Encode(TabName).ToString()).Append('"');
            foreach ((string name, string raw) in _kept)
                sb.Append(",\n  \"").Append(JsonEncodedText.Encode(name).ToString()).Append("\": ").Append(raw);
            sb.Append("\n}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Settings are a convenience; a failed write keeps the old file.
        }
    }

    private static bool Bool(JsonElement r, string name, bool fallback) =>
        r.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;
}
