// ============================================================================
//  RynthSuite - Plugins/Shared/UseAudit.cs
//  One way for a plugin to use an object: host.UseFor(id, subsystem, why, kind).
//  Linked into each plugin that uses objects (RynthAi, RynthNav, RynthLua), so
//  every copy is the same and each plugin has its own state.
//
//  Why (2026-10-04): doors and corpses opened and closed "on their own" around
//  logins and hot reloads for months and nothing could say who did it - no use
//  was logged anywhere. Now every use is one log line:
//
//    [Use] RynthAi/Door 0x7860202C 'Door' (door) macro=on auto - nav detour asked for it
//
//  plugin/subsystem, the object id and name, what kind of thing it is, whether
//  the macro is on (n/a for a plugin without one), auto (the bot decided) or
//  asked (a typed command, a meta or script the player started), and why.
//
//  Guard: an AUTO use of a door or a corpse while the macro is off is refused
//  and logged as BLOCKED. With the macro off nothing may open a door or a
//  corpse unless the player asked for it.
//
//  Throttle: the same object from the same subsystem logs at most once per
//  RepeatMs; repeats in between are counted on its next line. Past
//  MaxLinesPerWindow lines in WindowMs the rest are only counted, and the next
//  window's first line says how many were left out.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal enum UseKind
{
    /// <summary>The bot decided on its own (looting, doors, nav, combat swaps...).</summary>
    Auto,
    /// <summary>The player asked: a typed command, a meta or a script the player started.</summary>
    Asked,
}

internal static class UseAudit
{
    private const long RepeatMs = 5000;
    private const long WindowMs = 10_000;
    private const int MaxLinesPerWindow = 20;
    private const uint BfDoor = 0x1000;
    private const uint BfCorpse = 0x2000;

    /// <summary>The plugin's name in every line. Set once at Init.</summary>
    public static string PluginName = "?";

    /// <summary>
    /// Reads the macro state at the moment of the use; null for a plugin without a macro
    /// (no guard then). Cleared at Shutdown so a reused module never holds the old plugin.
    /// </summary>
    public static Func<bool>? MacroOn;

    private static bool? MacroRunning
    {
        get { try { return MacroOn?.Invoke(); } catch { return false; } }
    }

    /// <summary>Clock (ms); tests replace it.</summary>
    public static Func<long> NowMs = () => Environment.TickCount64;

    private static readonly object Gate = new();
    private static readonly Dictionary<(uint Id, string Sub), (long LastMs, int Repeats)> Seen = new();
    private static long _windowStart = long.MinValue;
    private static int _windowLines;
    private static int _windowDropped;

    /// <summary>Back to the state of a fresh load (Init, tests).</summary>
    public static void Reset(string pluginName, Func<bool>? macroOn)
    {
        lock (Gate)
        {
            PluginName = pluginName;
            MacroOn = macroOn;
            Seen.Clear();
            _windowStart = long.MinValue;
            _windowLines = 0;
            _windowDropped = 0;
            _worldUseId = 0;
            _worldUseSentMs = 0;
            _worldUseSeqAtSend = 0;
            AfterSend = null;
            WorldUseHold = null;
        }
    }

    /// <summary>UseObject with a log line and the door/corpse guard. False when refused or the engine said no.</summary>
    public static bool UseFor(this RynthCoreHost host, uint objectId, string subsystem, string why, UseKind kind = UseKind.Auto)
    {
        string what = Describe(host, objectId, out bool doorOrCorpse);
        if (kind == UseKind.Auto && doorOrCorpse && MacroRunning == false)
        {
            Write(host, objectId, subsystem, $"BLOCKED 0x{objectId:X8} {what} macro=off auto - {why} (with the macro off nothing opens a door or corpse unless you ask)", force: true);
            return false;
        }
        if (kind == UseKind.Auto && doorOrCorpse)
        {
            string? hold = null;
            try { hold = WorldUseHold?.Invoke(); } catch { hold = null; }
            if (!string.IsNullOrEmpty(hold))
            {
                Write(host, objectId, subsystem, $"HELD 0x{objectId:X8} {what} auto - {why} ({hold})", force: false);
                return false;
            }
        }
        Write(host, objectId, subsystem, $"0x{objectId:X8} {what} macro={MacroText} {KindText(kind)} - {why}", force: false);
        int seqBefore = 0;
        bool track = TrackWorldUses && doorOrCorpse;
        var after = AfterSend;
        if (track || after != null)
        {
            try { seqBefore = host.HasUseDoneSeq ? host.GetUseDoneSeq() : 0; } catch { }
        }
        bool sent = host.UseObject(objectId);
        if (track && sent)
            MarkWorldUse(objectId, seqBefore);
        if (sent && after != null)
        {
            try { after(host, new SentUse(objectId, 0, subsystem, what, doorOrCorpse, seqBefore, kind)); } catch { }
        }
        return sent;
    }

    /// <summary>What went out (for <see cref="AfterSend"/>).</summary>
    internal readonly record struct SentUse(uint ObjectId, uint TargetId, string Subsystem, string What,
        bool DoorOrCorpse, int UseDoneSeqBefore, UseKind Kind);

    /// <summary>
    /// Called after every use / use-on this plugin sent (RynthAi records it as the server action it
    /// waits on, Plugins/Shared/ActionPacer.cs). Null in plugins that don't pace. Cleared by Reset.
    /// </summary>
    public static Action<RynthCoreHost, SentUse>? AfterSend;

    /// <summary>
    /// Asked before an automatic door or corpse use: a reason to hold it back (nothing is sent, the
    /// call returns false), or null/empty to send. RynthAi holds them while the salvage panel opens
    /// (the client swallows the use then). A use the player asked for is never held. Cleared by Reset.
    /// </summary>
    public static Func<string?>? WorldUseHold;

    // ── A door or corpse use the server hasn't answered yet (2026-10-06) ──────────
    // The server walks/turns the character to a used door or corpse, and any movement packet
    // from the client cancels that (ACE: a MoveToState stops the move-to chain, and a combat
    // mode change cancels the move-to too). MoveAudit holds the plugin's own movement while
    // one is in flight. Opt-in per plugin (TrackWorldUses); off = nothing changes.

    /// <summary>Opt-in: remember door/corpse uses so MoveAudit can hold movement until the server answers.</summary>
    public static volatile bool TrackWorldUses;

    /// <summary>Longest a use counts as in flight when the server doesn't answer (a long walk to it).</summary>
    public const long WorldUseInFlightMaxMs = 1500;

    private static uint _worldUseId;
    private static long _worldUseSentMs;
    private static int _worldUseSeqAtSend;

    /// <summary>Records a door/corpse use that went out (internal for the tests).</summary>
    internal static void MarkWorldUse(uint objectId, int useDoneSeqBefore)
    {
        lock (Gate)
        {
            _worldUseId = objectId;
            _worldUseSentMs = NowMs();
            _worldUseSeqAtSend = useDoneSeqBefore;
        }
    }

    /// <summary>
    /// True while the last door/corpse use is waiting for the server: sent less than
    /// WorldUseInFlightMaxMs ago and no UseDone has arrived since (when the engine counts them;
    /// without the count, the time alone). Always false unless TrackWorldUses.
    /// </summary>
    public static bool WorldUseInFlight(RynthCoreHost host, out uint objectId)
    {
        objectId = 0;
        if (!TrackWorldUses) return false;
        long sentMs; int seqAtSend; uint id;
        lock (Gate)
        {
            sentMs = _worldUseSentMs;
            seqAtSend = _worldUseSeqAtSend;
            id = _worldUseId;
        }
        if (sentMs == 0 || NowMs() - sentMs >= WorldUseInFlightMaxMs) return false;
        try
        {
            if (host.HasUseDoneSeq && host.GetUseDoneSeq() != seqAtSend) return false;
        }
        catch { }
        objectId = id;
        return true;
    }

    /// <summary>Forget the in-flight use (Reset, tests).</summary>
    internal static void ClearWorldUse()
    {
        lock (Gate)
        {
            _worldUseId = 0;
            _worldUseSentMs = 0;
            _worldUseSeqAtSend = 0;
        }
    }

    /// <summary>UseObjectOn (item on a target) with a log line and the same guard on the target.</summary>
    public static bool UseOnFor(this RynthCoreHost host, uint sourceId, uint targetId, string subsystem, string why, UseKind kind = UseKind.Auto)
    {
        string src = Describe(host, sourceId, out _);
        string tgt = Describe(host, targetId, out bool doorOrCorpse);
        if (kind == UseKind.Auto && doorOrCorpse && MacroRunning == false)
        {
            Write(host, targetId, subsystem, $"BLOCKED 0x{sourceId:X8} {src} on 0x{targetId:X8} {tgt} macro=off auto - {why}", force: true);
            return false;
        }
        Write(host, targetId, subsystem, $"0x{sourceId:X8} {src} on 0x{targetId:X8} {tgt} macro={MacroText} {KindText(kind)} - {why}", force: false);
        var after = AfterSend;
        int seqBefore = 0;
        if (after != null)
        {
            try { seqBefore = host.HasUseDoneSeq ? host.GetUseDoneSeq() : 0; } catch { }
        }
        bool sent = host.UseObjectOn(sourceId, targetId);
        if (sent && after != null)
        {
            try { after(host, new SentUse(sourceId, targetId, subsystem, $"{src} on {tgt}", doorOrCorpse, seqBefore, kind)); } catch { }
        }
        return sent;
    }

    private static string MacroText => MacroRunning switch { true => "on", false => "off", null => "n/a" };
    private static string KindText(UseKind k) => k == UseKind.Auto ? "auto" : "asked";

    /// <summary>'Name' (door|corpse|portal) - what the object is, from its weenie flags, else its name.</summary>
    internal static string Describe(RynthCoreHost host, uint id, out bool doorOrCorpse)
    {
        string name = "?";
        try { if (host.HasGetObjectName && host.TryGetObjectName(id, out string n) && !string.IsNullOrWhiteSpace(n)) name = n; }
        catch { }

        string kind = "";
        uint bf = 0;
        bool haveBf = false;
        try { haveBf = host.HasGetObjectBitfield && host.TryGetObjectBitfield(id, out bf); }
        catch { }
        if (haveBf && (bf & BfDoor) != 0) kind = "door";
        else if (haveBf && (bf & BfCorpse) != 0) kind = "corpse";
        else if (LooksLikeCorpse(name)) kind = "corpse";
        else if (id < 0x80000000u && LooksLikeDoor(name)) kind = "door";   // doors have static ids
        doorOrCorpse = kind.Length > 0;
        return kind.Length > 0 ? $"'{name}' ({kind})" : $"'{name}'";
    }

    private static bool LooksLikeCorpse(string name) =>
        name.StartsWith("Corpse of ", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Corpse", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDoor(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("door") || n.Contains("gate") || n.Contains("hatch") || n.Contains("portcullis");
    }

    private static void Write(RynthCoreHost host, uint id, string subsystem, string body, bool force)
    {
        string? line = null;
        lock (Gate)
        {
            long now = NowMs();
            if (_windowStart == long.MinValue || now - _windowStart >= WindowMs)
            {
                if (_windowDropped > 0)
                    line = $"[Use] {PluginName}: {_windowDropped} more use line(s) left out in the last {WindowMs / 1000}s (busy)\n";
                _windowStart = now;
                _windowLines = 0;
                _windowDropped = 0;
            }

            var key = (id, subsystem);
            int repeats = 0;
            if (!force && Seen.TryGetValue(key, out var seen))
            {
                if (now - seen.LastMs < RepeatMs)
                {
                    Seen[key] = (seen.LastMs, seen.Repeats + 1);
                    return;
                }
                repeats = seen.Repeats;
            }
            if (!force && _windowLines >= MaxLinesPerWindow)
            {
                _windowDropped++;
                return;
            }
            if (Seen.Count > 512) Seen.Clear();
            Seen[key] = (now, 0);
            _windowLines++;
            string more = repeats > 0 ? $" (+{repeats} repeat(s) since its last line)" : "";
            line += $"[Use] {PluginName}/{subsystem} {body}{more}";
        }
        try
        {
            foreach (string l in line.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                host.Log(l);
        }
        catch { }
    }
}
