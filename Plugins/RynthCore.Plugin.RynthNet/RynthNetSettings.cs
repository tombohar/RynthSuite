// ============================================================================
//  RynthNet - RynthNetSettings.cs
//  C:\Games\RynthSuite\RynthNet\settings.json (RYNTHNET_DIR overrides the
//  folder), read and written by hand with JsonDocument / Utf8JsonWriter (no
//  reflection serializer: NativeAOT, and a collectible plugin must not leave
//  its types in a process-wide cache).
//
//  Every client on the PC shares the file, so a save never writes a stale copy:
//  Update() takes a lock file (released by the OS if the process dies), reads
//  the file again, applies one change and writes it back. Tags and the command
//  policy are per character; "defaults" is the policy of characters that have
//  none of their own.
//
//  {
//    "logRemoteCommands": true,
//    "statusIntervalMs": 1000,
//    "defaults": { "acceptRemoteCommands": true, "allow": [], "deny": [], "trustedSenders": [] },
//    "characters": { "World|Name": { "tags": ["healer"], "policy": { ...same as defaults... } } }
//  }
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace RynthCore.Plugin.RynthNet;

/// <summary>What another client's command must pass here (see CommandPolicy).</summary>
internal sealed class RynthNetPolicy
{
    /// <summary>Master switch: run commands other clients send.</summary>
    public bool AcceptRemoteCommands = true;
    /// <summary>When not empty, a remote command runs only if it starts with one of these.</summary>
    public List<string> Allow = new();
    /// <summary>A remote command starting with one of these never runs.</summary>
    public List<string> Deny = new();
    /// <summary>When not empty, only these characters' commands run.</summary>
    public List<string> TrustedSenders = new();

    public RynthNetPolicy Clone() => new()
    {
        AcceptRemoteCommands = AcceptRemoteCommands,
        Allow = new List<string>(Allow),
        Deny = new List<string>(Deny),
        TrustedSenders = new List<string>(TrustedSenders),
    };
}

internal sealed class RynthNetCharacter
{
    public List<string> Tags = new();
    /// <summary>This character's own policy; null = the defaults.</summary>
    public RynthNetPolicy? Policy;
}

internal sealed class RynthNetSettings
{
    public static readonly string DefaultFolder = Path.Combine(RynthCore.Install.RynthInstallPaths.SuiteDir, "RynthNet"); // C:\Games\RynthSuite\RynthNet by default
    /// <summary>The settings folder; RYNTHNET_DIR overrides it (tests, portable setups).</summary>
    public static string Folder => Environment.GetEnvironmentVariable("RYNTHNET_DIR") is { Length: > 0 } dir ? dir : DefaultFolder;
    public static string FilePath => Path.Combine(Folder, "settings.json");
    private static string LockPath => Path.Combine(Folder, "settings.lock");

    /// <summary>Write every command run for another client to chat, with who sent it.</summary>
    public bool LogRemoteCommands = true;
    /// <summary>How often this client's vitals, position and bot state go to the others (250..10000 ms).</summary>
    public int StatusIntervalMs = 1000;
    public RynthNetPolicy Defaults = new();
    /// <summary>"world|character" -> tags and policy.</summary>
    public Dictionary<string, RynthNetCharacter> Characters = new(StringComparer.OrdinalIgnoreCase);

    public static string CharacterKey(string world, string character) => (world ?? "") + "|" + (character ?? "");

    public RynthNetPolicy PolicyFor(string key)
        => Characters.TryGetValue(key, out RynthNetCharacter? c) && c.Policy != null ? c.Policy : Defaults;

    public IReadOnlyList<string> TagsFor(string key)
        => Characters.TryGetValue(key, out RynthNetCharacter? c) ? c.Tags : Array.Empty<string>();

    /// <summary>The character's entry, created when missing.</summary>
    public RynthNetCharacter Character(string key)
    {
        if (!Characters.TryGetValue(key, out RynthNetCharacter? c))
        {
            c = new RynthNetCharacter();
            Characters[key] = c;
        }
        return c;
    }

    /// <summary>The file's last write time (UTC ticks), 0 when there is none.</summary>
    public static long FileStamp()
    {
        try { return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath).Ticks : 0; }
        catch { return 0; }
    }

    // ── Load / update ───────────────────────────────────────────────────────

    public static RynthNetSettings Load(out string? error)
    {
        error = null;
        try
        {
            return File.Exists(FilePath) ? Parse(File.ReadAllBytes(FilePath)) : new RynthNetSettings();
        }
        catch (Exception ex)
        {
            error = $"{FilePath}: {ex.Message} (using defaults)";
            return new RynthNetSettings();
        }
    }

    /// <summary>
    /// Re-reads the file under the lock, applies <paramref name="change"/>, writes it back, and
    /// returns what is now on disk (other clients' changes included). False with an error when
    /// the file couldn't be read or written; nothing is written then.
    /// </summary>
    public static bool Update(Action<RynthNetSettings> change, out RynthNetSettings current, out string? error)
    {
        error = null;
        current = new RynthNetSettings();
        try
        {
            Directory.CreateDirectory(Folder);
            using FileStream? gate = TakeLock();
            RynthNetSettings s = File.Exists(FilePath) ? Parse(File.ReadAllBytes(FilePath)) : new RynthNetSettings();
            change(s);
            string tmp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, s.Serialize());
            File.Move(tmp, FilePath, overwrite: true);
            current = s;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>An exclusive lock file (gone when closed or when the process dies). Null after ~1 s of trying.</summary>
    private static FileStream? TakeLock()
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(20);   // a lock file being deleted by its previous holder
            }
        }
        return null;   // someone holds it far too long: go ahead rather than lose the change
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    private static RynthNetSettings Parse(byte[] bytes)
    {
        var s = new RynthNetSettings();
        using JsonDocument doc = JsonDocument.Parse(bytes,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
        JsonElement r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return s;
        s.LogRemoteCommands = Bool(r, "logRemoteCommands", s.LogRemoteCommands);
        if (r.TryGetProperty("statusIntervalMs", out JsonElement si) && si.ValueKind == JsonValueKind.Number && si.TryGetInt32(out int ms))
            s.StatusIntervalMs = Math.Clamp(ms, 250, 10_000);
        if (r.TryGetProperty("defaults", out JsonElement d) && d.ValueKind == JsonValueKind.Object)
            s.Defaults = ParsePolicy(d);
        if (r.TryGetProperty("characters", out JsonElement chars) && chars.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in chars.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                var c = new RynthNetCharacter { Tags = Strings(p.Value, "tags") };
                if (p.Value.TryGetProperty("policy", out JsonElement pol) && pol.ValueKind == JsonValueKind.Object)
                    c.Policy = ParsePolicy(pol);
                s.Characters[p.Name] = c;
            }
        }
        return s;
    }

    private static RynthNetPolicy ParsePolicy(JsonElement e) => new()
    {
        AcceptRemoteCommands = Bool(e, "acceptRemoteCommands", true),
        Allow = Strings(e, "allow"),
        Deny = Strings(e, "deny"),
        TrustedSenders = Strings(e, "trustedSenders"),
    };

    private byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteBoolean("logRemoteCommands", LogRemoteCommands);
            w.WriteNumber("statusIntervalMs", StatusIntervalMs);
            w.WritePropertyName("defaults");
            WritePolicy(w, Defaults);
            w.WriteStartObject("characters");
            foreach (var kv in Characters)
            {
                if (kv.Value.Tags.Count == 0 && kv.Value.Policy == null) continue;
                w.WriteStartObject(kv.Key);
                WriteList(w, "tags", kv.Value.Tags);
                if (kv.Value.Policy != null)
                {
                    w.WritePropertyName("policy");
                    WritePolicy(w, kv.Value.Policy);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void WritePolicy(Utf8JsonWriter w, RynthNetPolicy p)
    {
        w.WriteStartObject();
        w.WriteBoolean("acceptRemoteCommands", p.AcceptRemoteCommands);
        WriteList(w, "allow", p.Allow);
        WriteList(w, "deny", p.Deny);
        WriteList(w, "trustedSenders", p.TrustedSenders);
        w.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter w, string name, List<string> items)
    {
        w.WriteStartArray(name);
        foreach (string i in items) w.WriteStringValue(i);
        w.WriteEndArray();
    }

    private static bool Bool(JsonElement r, string name, bool fallback)
        => r.TryGetProperty(name, out JsonElement v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback } : fallback;

    private static List<string> Strings(JsonElement r, string name)
    {
        var list = new List<string>();
        if (r.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in a.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                    list.Add(e.GetString()!.Trim());
        return list;
    }
}
