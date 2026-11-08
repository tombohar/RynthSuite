// ============================================================================
//  RynthSuite - Plugins/RynthCore.Plugin.RynthAi/Loot/AetheriaUnboxer.cs
//  Unbox Aetheria When Idle (a player's request, 2026-11-01: "auto aetheria
//  unbox when idle option"), default off; also /ra aetheria unbox by hand.
//
//  ACE's rule (ACE.Server Entity/Aetheria.cs UseObjectOnTarget, reached from
//  CraftTool.HandleActionUseOnTarget): an Aetheria Mana Stone (wcid 42645) used
//  on a Coalesced Aetheria (42635 blue, 42636 red, 42637 yellow) whose name is
//  still "Coalesced Aetheria", both in the player's packs. Busy: UseDone
//  YoureTooBusy. Otherwise the server switches the player to peace mode, claps,
//  then ActivateSigil: chat "A sigil rises to the surface as you bathe the
//  aetheria in mana.", the item's name becomes "Aetheria", UseDone (no error),
//  and the server stays busy for the return-to-ready animation. The mana stone
//  is NOT consumed (ActivateSigil never touches the source), so one stone
//  unboxes every coalesced piece. Already revealed: "... because the sigil is
//  already visible." and UseDone YouDoNotPassCraftingRequirements.
//
//  One unbox at a time, each answered (the sigil line, the new name, or a
//  refusal) before the next goes out. A refusal waits 10 s, then 30 s, 60 s,
//  120 s; a piece refused three times (not counting "too busy") is left alone
//  for the session. It only starts at a safe moment (RynthAiPlugin.Aetheria.cs:
//  the scroll reader's gate, no fight), and the bot holds still while the clap
//  plays. Combat sets its own stance when the next fight starts.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Loot;

internal sealed class AetheriaUnboxer
{
    internal const uint WcidBlue = 42635, WcidRed = 42636, WcidYellow = 42637, WcidManaStone = 42645;
    internal const string CoalescedName = "Coalesced Aetheria";
    internal const string StoneName = "Aetheria Mana Stone";
    internal const string SigilLine = "A sigil rises to the surface as you bathe the aetheria in mana.";
    /// <summary>ACE's YoureTooBusy, as a UseDone error.</summary>
    internal const uint TooBusyErr = 0x1D;

    /// <summary>Peace (up to ~1 s) + clap + the reveal is ~2-3 s on ACE; no answer in this long is a failed try.</summary>
    internal const long UnboxTimeoutMs = 10_000;
    /// <summary>A UseDone with no error and the piece still coalesced: its name may lag the UseDone this long.</summary>
    internal const long UseDoneGraceMs = 2_000;
    /// <summary>
    /// Pause between two unboxes, from the last one's end. ACE sends the UseDone before the
    /// return-to-ready animation and stays busy until it ends: sooner is "too busy".
    /// </summary>
    internal const long GapMs = 1_500;
    /// <summary>Waits after a refusal (or no answer): one step longer for each in a row.</summary>
    internal static readonly long[] BackoffMs = { 10_000, 30_000, 60_000, 120_000 };
    /// <summary>Refusals (not "too busy") before a piece is left alone for the session.</summary>
    internal const int MaxRefusalsPerItem = 3;
    /// <summary>How often the pack is looked through while the last look found nothing to unbox.</summary>
    internal const long SweepMs = 5_000;
    /// <summary>How often while there is work (the next piece after an unbox).</summary>
    internal const long StartCheckMs = 1_000;

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache? _cache;

    /// <summary>Clock (ms); tests replace it.</summary>
    public Func<long> NowMs = () => Environment.TickCount64;
    /// <summary>The player's id (0 before login).</summary>
    public Func<uint> PlayerId = () => 0;
    /// <summary>A chat line for the player.</summary>
    public Action<string>? Chat;

    // The unbox in flight.
    private int _targetId;
    private int _stoneId;
    private string _targetColor = "";
    private long _sentAt;
    private bool _sigilSeen;
    private bool _busyChat;
    private bool _alreadyVisible;
    private bool _targetDeleted;
    private bool _useDoneWatch;
    private int _useDoneSeqAtSend;
    private long _useDoneAt;
    private uint _useDoneErr;

    private long _lastEndAt = long.MinValue / 2;
    private long _notBefore;
    private int _backoffStep;
    private long _lastWalkAt = long.MinValue / 2;
    private bool _workSeen;
    private bool _noStoneSaid;
    private bool _manual;
    private int _manualRevealed;
    private readonly Dictionary<int, int> _refusals = new();
    private readonly HashSet<int> _givenUp = new();

    public AetheriaUnboxer(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache? cache)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
    }

    public bool IsUnboxing => _targetId != 0;
    /// <summary>A run asked for by hand (/ra aetheria unbox) is under way.</summary>
    public bool ManualRun => _manual;
    /// <summary>Pieces revealed this session.</summary>
    public int Revealed { get; private set; }
    public int GivenUpCount => _givenUp.Count;
    public long BackoffLeftMs => Math.Max(0, _notBefore - NowMs());

    // ── Facts ─────────────────────────────────────────────────────────────────

    private uint Wcid(int id)
    {
        try { return _host.HasGetObjectWcid && _host.TryGetObjectWcid(unchecked((uint)id), out uint w) ? w : 0; }
        catch { return 0; }
    }

    /// <summary>The client's name for it now (a reveal renames it), else the cache's.</summary>
    private string LiveName(int id, string? cached = null)
    {
        try
        {
            if (_host.HasGetObjectName && _host.TryGetObjectName(unchecked((uint)id), out string n) && !string.IsNullOrWhiteSpace(n))
                return n;
        }
        catch { }
        return cached ?? _cache?[id]?.Name ?? "";
    }

    private static bool IsAetheriaWcid(uint w) => w == WcidBlue || w == WcidRed || w == WcidYellow;

    /// <summary>
    /// A piece the server will unbox: an aetheria wcid (or none readable) still named "Coalesced
    /// Aetheria". A revealed one is named "Aetheria" and is left alone.
    /// </summary>
    internal bool IsCoalesced(WorldObject wo)
    {
        uint w = Wcid(wo.Id);
        if (w != 0 && !IsAetheriaWcid(w)) return false;
        return LiveName(wo.Id, wo.Name).Equals(CoalescedName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An Aetheria Mana Stone: its wcid, or its name when no wcid is readable.</summary>
    internal bool IsStone(WorldObject wo)
    {
        uint w = Wcid(wo.Id);
        if (w != 0) return w == WcidManaStone;
        return LiveName(wo.Id, wo.Name).Equals(StoneName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ColorOf(uint w) => w switch
    {
        WcidBlue => "blue",
        WcidRed => "red",
        WcidYellow => "yellow",
        _ => "",
    };

    /// <summary>The pack, freshly walked: GetInventory can still hold items that are gone (2026-11-03).</summary>
    private IReadOnlyList<WorldObject> Inventory()
    {
        IReadOnlyList<WorldObject> all;
        try { all = _cache?.GetDirectInventory(forceRefresh: true) ?? Array.Empty<WorldObject>(); }
        catch { return Array.Empty<WorldObject>(); }

        // Without the engine's container walk the cache falls back to every non-landscape
        // object it knows, corpse contents included (ScrollLearner.Inventory). The server wants
        // both items in the player's own packs: leave out what is known to sit elsewhere.
        int player = unchecked((int)PlayerId());
        if (player == 0 || _host.HasGetContainerContents) return all;
        var mine = new List<WorldObject>(all.Count);
        foreach (WorldObject wo in all)
        {
            int c = wo.Container;
            if (c == 0 || c == player || _cache?[c]?.Container == player) mine.Add(wo);
        }
        return mine;
    }

    /// <summary>What the pack holds: the coalesced pieces (left-alone ones too) and the first mana stone.</summary>
    internal (List<WorldObject> Coalesced, WorldObject? Stone, int Stones) Look()
    {
        var coalesced = new List<WorldObject>();
        WorldObject? stone = null;
        int stones = 0;
        foreach (WorldObject wo in Inventory())
        {
            if (IsStone(wo))
            {
                stones++;
                stone ??= wo;
            }
            else if (IsCoalesced(wo)) coalesced.Add(wo);
        }
        return (coalesced, stone, stones);
    }

    // ── Ticking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One tick. <paramref name="safe"/>: no fight, no corpse, not buffing, salvaging or in a portal
    /// (RynthAiPlugin.IsSafeToUnboxAetheria). Returns true while an unbox is in flight with the macro
    /// on and it is still safe: the caller then holds the bot still.
    /// </summary>
    public bool Tick(bool safe, int busyCount)
    {
        long now = NowMs();
        PollUseDone(now);
        if (IsUnboxing)
        {
            if (Resolve(now)) return false;
            return safe && _settings.IsMacroRunning;
        }

        if (!_manual && !(_settings.UnboxAetheriaWhenIdle && _settings.IsMacroRunning)) return false;
        if (!_host.HasUseObjectOn)
        {
            FinishManual("this engine can't use an item on another");
            return false;
        }
        if (!safe || busyCount > 0) return false;
        if (now < _notBefore || now - _lastEndAt < GapMs) return false;
        if (now - _lastWalkAt < (_workSeen ? StartCheckMs : SweepMs)) return false;
        _lastWalkAt = now;

        var (coalesced, stone, _) = Look();
        WorldObject? pick = null;
        foreach (WorldObject wo in coalesced)
            if (!_givenUp.Contains(wo.Id)) { pick = wo; break; }
        _workSeen = pick != null;
        if (pick == null)
        {
            _noStoneSaid = false;
            FinishManual(coalesced.Count > 0 ? $"{coalesced.Count} left alone after refusals" : "");
            return false;
        }
        if (stone == null)
        {
            // Said once, until a stone turns up again or nothing is left to unbox.
            if (!_noStoneSaid)
            {
                LogLine($"[Aetheria] no mana stone in the pack, {coalesced.Count} coalesced aetheria left");
                if (!_manual)
                    Say($"[RynthAi] Aetheria: {coalesced.Count} coalesced aetheria in your pack, but no Aetheria Mana Stone to unbox them with.");
            }
            _noStoneSaid = true;
            FinishManual($"no Aetheria Mana Stone left for the other {coalesced.Count}");
            return false;
        }
        _noStoneSaid = false;
        return Send(pick, stone, now) && _settings.IsMacroRunning;
    }

    private bool Send(WorldObject target, WorldObject stone, long now)
    {
        int seqBefore = 0;
        bool haveSeq = false;
        try { if (_host.HasUseDoneSeq) { seqBefore = _host.GetUseDoneSeq(); haveSeq = true; } } catch { }
        bool sent = false;
        try
        {
            sent = _host.UseOnFor(unchecked((uint)stone.Id), unchecked((uint)target.Id), "Aetheria",
                _manual ? "unbox a coalesced aetheria (/ra aetheria unbox)" : "Unbox Aetheria When Idle",
                _manual ? UseKind.Asked : UseKind.Auto);
        }
        catch { }
        if (!sent)
        {
            // Nothing went out (the engine said no): the same wait as a refusal, so it isn't sent every second.
            Refused(target.Id, "the use didn't go out", busy: true, now);
            return false;
        }
        _targetId = target.Id;
        _stoneId = stone.Id;
        _targetColor = ColorOf(Wcid(target.Id));
        _sentAt = now;
        _sigilSeen = _busyChat = _alreadyVisible = _targetDeleted = false;
        _useDoneWatch = haveSeq;
        _useDoneSeqAtSend = seqBefore;
        _useDoneAt = 0;
        _useDoneErr = 0;
        LogLine($"[Aetheria] unboxing 0x{(uint)target.Id:X8} '{CoalescedName}'{ColorText} with 0x{(uint)stone.Id:X8} '{LiveName(stone.Id, stone.Name)}'");
        return true;
    }

    private string ColorText => _targetColor.Length > 0 ? $" ({_targetColor})" : "";

    /// <summary>Notes the first UseDone after the send, and its error.</summary>
    private void PollUseDone(long now)
    {
        if (!_useDoneWatch) return;
        try
        {
            int seq = _host.GetUseDoneSeq();
            if (unchecked(seq - _useDoneSeqAtSend) <= 0) return;
            _useDoneWatch = false;
            _useDoneAt = now;
            _useDoneErr = _host.TryGetLastUseDone(out int lastSeq, out uint err) && lastSeq == seq ? err : 0;
        }
        catch { _useDoneWatch = false; }
    }

    /// <summary>True when the unbox in flight is over (revealed, refused, gone, or timed out).</summary>
    private bool Resolve(long now)
    {
        int id = _targetId;
        string name = LiveName(id);
        bool renamed = name.Length > 0 && !name.Equals(CoalescedName, StringComparison.OrdinalIgnoreCase);

        if (_sigilSeen || (renamed && !_alreadyVisible))
        {
            Revealed++;
            if (_manual) _manualRevealed++;
            _backoffStep = 0;
            _refusals.Remove(id);
            string how = _sigilSeen ? "the sigil rose" : $"now named '{name}'";
            LogLine($"[Aetheria] revealed 0x{(uint)id:X8}{ColorText} ({how}; {Revealed} this session)");
            EndUnbox(now);
            return true;
        }
        if (_alreadyVisible)
        {
            LogLine($"[Aetheria] 0x{(uint)id:X8} was already revealed - left alone");
            _givenUp.Add(id);
            EndUnbox(now);
            return true;
        }
        if (_targetDeleted)
        {
            LogLine($"[Aetheria] 0x{(uint)id:X8} left the pack during its unbox");
            EndUnbox(now);
            return true;
        }

        long age = now - _sentAt;
        string? why = null;
        bool busy = false;
        if (_useDoneAt != 0 && _useDoneErr == TooBusyErr) { why = "too busy"; busy = true; }
        else if (_useDoneAt != 0 && _useDoneErr != 0) why = $"refused, error 0x{_useDoneErr:X}";
        else if (_busyChat) { why = "too busy"; busy = true; }
        else if (_useDoneAt != 0 && now - _useDoneAt >= UseDoneGraceMs) why = "answered, but no sigil rose";
        else if (age >= UnboxTimeoutMs) why = $"no answer in {UnboxTimeoutMs / 1000} s";
        if (why == null) return false;

        EndUnbox(now);
        Refused(id, why, busy, now);
        return true;
    }

    private void Refused(int id, string why, bool busy, long now)
    {
        long wait = BackoffMs[Math.Min(_backoffStep, BackoffMs.Length - 1)];
        _backoffStep++;
        _notBefore = now + wait;
        if (!busy)
        {
            _refusals.TryGetValue(id, out int n);
            _refusals[id] = ++n;
            if (n >= MaxRefusalsPerItem)
            {
                _givenUp.Add(id);
                LogLine($"[Aetheria] unbox of 0x{(uint)id:X8} failed ({why}) {n} times - left alone this session");
                return;
            }
        }
        LogLine($"[Aetheria] unbox of 0x{(uint)id:X8} not done ({why}) - trying again in {wait / 1000} s");
    }

    private void EndUnbox(long now)
    {
        _targetId = 0;
        _stoneId = 0;
        _targetColor = "";
        _sigilSeen = _busyChat = _alreadyVisible = _targetDeleted = false;
        _useDoneWatch = false;
        _useDoneAt = 0;
        _useDoneErr = 0;
        _lastEndAt = now;
        _lastWalkAt = long.MinValue / 2;   // the next piece is looked for at once (past the gap)
    }

    /// <summary>A run asked for by hand ends: one line saying how it went.</summary>
    private void FinishManual(string why)
    {
        if (!_manual) return;
        _manual = false;
        string line = $"[RynthAi] Aetheria: revealed {_manualRevealed}.";
        if (why.Length > 0) line = $"[RynthAi] Aetheria: revealed {_manualRevealed} ({why}).";
        _manualRevealed = 0;
        Say(line);
    }

    // ── By hand ───────────────────────────────────────────────────────────────

    /// <summary>
    /// /ra aetheria unbox: unbox every coalesced piece in the pack, one at a time, whatever the
    /// setting (with the macro on, still only at a safe moment). False with the reason when there
    /// is nothing to do. A wait after a refusal is skipped; pieces left alone are tried again.
    /// </summary>
    public bool RequestManual(out string reply)
    {
        if (!_host.HasUseObjectOn) { reply = "[RynthAi] Aetheria: this engine can't use an item on another (UseObjectOn)."; return false; }
        var (coalesced, stone, _) = Look();
        if (coalesced.Count == 0) { reply = "[RynthAi] Aetheria: no Coalesced Aetheria in your pack."; return false; }
        if (stone == null) { reply = $"[RynthAi] Aetheria: {coalesced.Count} coalesced aetheria in your pack, but no Aetheria Mana Stone to unbox them with."; return false; }
        _givenUp.Clear();
        _refusals.Clear();
        _notBefore = 0;
        _backoffStep = 0;
        _lastWalkAt = long.MinValue / 2;
        _workSeen = true;
        if (!_manual) _manualRevealed = 0;
        _manual = true;
        reply = $"[RynthAi] Aetheria: unboxing {coalesced.Count} coalesced aetheria, one at a time.";
        return true;
    }

    /// <summary>/ra aetheria status: the setting, the pack, and what it is doing.</summary>
    public string Describe()
    {
        var (coalesced, _, stones) = Look();
        string state = IsUnboxing ? $"; unboxing 0x{(uint)_targetId:X8} now"
            : BackoffLeftMs > 0 ? $"; waiting {(BackoffLeftMs + 999) / 1000} s after a refusal"
            : "";
        string left = _givenUp.Count > 0 ? $"; {_givenUp.Count} left alone after refusals" : "";
        string manual = _manual ? "; an unbox by hand is under way" : "";
        return $"[RynthAi] Aetheria: Unbox When Idle {(_settings.UnboxAetheriaWhenIdle ? "on" : "off")}; in your pack {coalesced.Count} coalesced, " +
               $"{stones} Aetheria Mana Stone{(stones == 1 ? "" : "s")}; revealed {Revealed} this session{state}{left}{manual}.";
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>ACE's answers to an unbox (Aetheria.ActivateSigil, VerifyUseRequirements, the busy refusal).</summary>
    public void OnChat(string? text)
    {
        if (!IsUnboxing || string.IsNullOrEmpty(text)) return;
        string t = text.Trim();
        if (t == SigilLine) _sigilSeen = true;
        else if (t.EndsWith("because the sigil is already visible.", StringComparison.Ordinal)) _alreadyVisible = true;
        else if (t.StartsWith("You're too busy", StringComparison.Ordinal)) _busyChat = true;
    }

    public void OnObjectDeleted(uint objectId)
    {
        int id = unchecked((int)objectId);
        if (IsUnboxing && (id == _targetId || id == _stoneId)) _targetDeleted = true;
        _refusals.Remove(id);
        _givenUp.Remove(id);
    }

    /// <summary>Logout: the session memory goes with the character.</summary>
    public void Reset()
    {
        _refusals.Clear();
        _givenUp.Clear();
        _notBefore = 0;
        _backoffStep = 0;
        _noStoneSaid = false;
        _manual = false;
        _manualRevealed = 0;
        _workSeen = false;
        Revealed = 0;
        EndUnbox(NowMs());
    }

    private void Say(string line)
    {
        try { Chat?.Invoke(line); } catch { }
    }

    private void LogLine(string line)
    {
        try { _host.Log(line); } catch { }
    }
}
