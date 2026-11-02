// ============================================================================
//  ChestKeyLooter.cs - the Item Tool's chest looter.
//
//  "Pick a chest. Pick a key. It just loots the chest based on the current loot profile until it's
//  out of keys." A pure state machine: everything it touches goes through IChestKeyEnv (the plugin
//  implements it, RynthAiPlugin.ItemTool.cs; the host tests fake it).
//
//  One cycle:
//    Ready -> KeyPending -> KeyOut -> OpenPending -> OpenOut -> Looting -> Waiting -> Ready ...
//    * KeyPending/KeyOut: the key is used on the chest (AC's use-with-target). ACE walks the
//      character to the chest first (Player.HandleActionUseWithTarget -> CreateMoveToChain), so
//      there is no walking code here; the chest just has to be loaded and near (MaxStartMeters).
//      The server's answer (UseDone + its WeenieError) says what happened: 0 and "The X has been
//      unlocked." = unlocked (one use of the key is spent); 0x4AF LockAlreadyUnlocked (nothing
//      spent); 0x4B2 KeyDoesntFitThisLock; 0x4B1 the chest is open; 0x4B0 not a lock.
//    * OpenPending/OpenOut: the chest is used (opened). The server's ViewContents for it is the
//      proof (ChestOpenedAt).
//    * Looting: the chest is handed to the corpse looter (the CURRENT loot profile's rules, the
//      same TickOpenCorpseLooting as a corpse) and this waits for it to finish and close it.
//    * Waiting: a chest re-locks and regenerates on a timer after it was opened (ACE Chest.Reset,
//      ResetInterval, default 120 s). The next cycle starts after RegenWaitMs; where the client
//      reads the chest as locked it starts earlier. A key use that answers "already unlocked" on
//      a later cycle means the timer has not run yet: it waits ProbeMs and asks again, at most
//      MaxProbes times.
//
//  Out of keys: the key item is gone (used up: "Your key is used up."; no other carried item has
//  its name) after a cycle, or before one. A key with unlimited uses never runs out: MaxCycles
//  ends the run.
//
//  Never spams the server: one request at a time, each waits for its UseDone (AnswerMaxMs), the
//  client must be free (HoldReason) and a failed step is retried a few times at most.
// ============================================================================

using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.ItemTool;

internal interface IChestKeyEnv
{
    /// <summary>Clock (ms).</summary>
    long Now { get; }
    string NameOf(int id);
    /// <summary>The chest is in the client's object cache; meters = distance from the player (-1 unknown).</summary>
    bool ChestLoaded(int chestId, out double meters);
    /// <summary>
    /// The key to use: <paramref name="keyId"/> while it is still carried, else another carried item
    /// named <paramref name="keyName"/> (never worn, never <paramref name="excludeId"/>). 0 = none.
    /// </summary>
    int FindKey(int keyId, string keyName, int excludeId);
    /// <summary>Why nothing may be sent now (combat, the client is busy, portal space, the server owes an answer); null = free.</summary>
    string? HoldReason();
    bool HasUseDone { get; }
    int UseDoneSeq();
    bool LastUseDone(out int seq, out uint error);
    bool SendKey(int keyId, int chestId);
    bool SendOpen(int chestId);
    /// <summary>When the server last opened this chest for us (its ViewContents arrived); 0 = not since the plugin started.</summary>
    long ChestOpenedAt(int chestId);
    /// <summary>Hands the open chest to the corpse looter (the current loot profile).</summary>
    void StartLooting(int chestId);
    /// <summary>The corpse looter still has the chest (asked for, or its loot session is open).</summary>
    bool Looting(int chestId);
    /// <summary>Ends the loot session and closes the chest.</summary>
    void AbortLooting(int chestId);
    /// <summary>The client reads the chest as locked (true), unlocked (false) or doesn't know (null).</summary>
    bool? ChestLockedHint(int chestId);
    void Say(string text);
    void Log(string text);
}

internal sealed class ChestKeyLooter
{
    public enum Phase { Idle, Ready, KeyPending, KeyOut, OpenPending, OpenOut, Looting, Waiting }

    // ACE WeenieError codes of the key answers (UnlockerHelper.UseUnlocker).
    internal const uint ErrTooBusy = 0x1D, ErrLockAlreadyUnlocked = 0x4AF, ErrCannotLockOrUnlock = 0x4B0,
        ErrCannotLockWhatIsOpen = 0x4B1, ErrKeyDoesntFit = 0x4B2, ErrCannotUseThatItem = 0x58D;

    /// <summary>The chest must be loaded and no farther than this to start (the server walks the rest).</summary>
    public const double MaxStartMeters = 25;
    /// <summary>A step waits this long for the server's answer (the walk to the chest is part of it).</summary>
    public const long AnswerMaxMs = 12_000;
    /// <summary>After an answer, the chat line that goes with it gets this long to arrive.</summary>
    public const long ChatSettleMs = 250;
    /// <summary>After the chest's answer, it gets this long to read open before the open counts as failed.</summary>
    public const long OpenSettleMs = 1500;
    /// <summary>The next cycle starts this long after the chest was opened (ACE's default chest reset is 120 s).</summary>
    public const long RegenWaitMs = 125_000;
    /// <summary>A chest the client reads as locked is tried again this soon after it was opened.</summary>
    public const long MinRelockMs = 15_000;
    /// <summary>Asking a chest that was still unlocked again.</summary>
    public const long ProbeMs = 30_000;
    public const int MaxProbes = 8;
    /// <summary>Nothing may go out for this long (combat, busy client...) before the run stops.</summary>
    public const long HoldMaxMs = 90_000;
    /// <summary>The loot session may take this long.</summary>
    public const long LootMaxMs = 300_000;
    /// <summary>The looter has this long to take the chest.</summary>
    public const long LootStartMs = 8_000;
    /// <summary>The chest is out of the object cache this long: it is gone.</summary>
    public const long GoneMs = 5_000;
    public const int MaxCycles = 300;
    public const int MaxTimeouts = 3, MaxBusy = 8, MaxOpenFails = 3, MaxSendFails = 5, MaxRefusals = 3, MaxOpenElsewhere = 3;
    private const long ThinkMs = 100, RetryMs = 1500, SendRetryMs = 1000, BusyBackoffMs = 2000;
    private const long NoUseDoneFallbackMs = 3000;

    private static readonly Regex UnlockedText = new(@"\bhas been unlocked\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KeyUsedUpText = new(@"^\s*Your key (?:is used up|has (?<n>\d+) uses? left)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IChestKeyEnv _env;

    public ChestKeyLooter(IChestKeyEnv env) => _env = env;

    public int ChestId { get; private set; }
    public string ChestName { get; private set; } = "";
    public int KeyId { get; private set; }
    public string KeyName { get; private set; } = "";
    public Phase State { get; private set; } = Phase.Idle;
    public bool Running => State != Phase.Idle;
    /// <summary>Chests looted since the run started.</summary>
    public int Looted { get; private set; }
    /// <summary>The key's uses left, as the server last said (-1 unknown).</summary>
    public int UsesLeft { get; private set; } = -1;
    /// <summary>What the run did last, for the status line when it is not running.</summary>
    public string LastResult { get; private set; } = "";

    // Run state.
    private long _lastThink, _nextTryAt, _goneSince, _holdSince;
    private long _keySentAt, _openSentAt, _answeredAt, _lootStartedAt, _waitUntil, _openedAt;
    private int _seqAtSend;
    private bool _answerSeen, _sawUnlocked, _keyUsedUp, _lootSeen, _relockExpected;
    private int _timeouts, _busy, _openFails, _sendFails, _probes, _refusals, _openElsewhere, _cycles, _keyUsesThisCycle;
    private string _holdWhy = "";

    public void SetChest(int id, string name) { if (!Running) { ChestId = id; ChestName = name; } }
    public void SetKey(int id, string name) { if (!Running) { KeyId = id; KeyName = name; UsesLeft = -1; } }
    public void Clear() { if (!Running) { ChestId = 0; ChestName = ""; KeyId = 0; KeyName = ""; UsesLeft = -1; LastResult = ""; } }

    /// <summary>Begins the run. False (with why, already said in chat) when it can't.</summary>
    public bool Start(out string why)
    {
        why = "";
        if (Running) { why = "the chest looter is already running"; return false; }
        if (ChestId == 0) { why = "pick a chest first (select it in the world, then 'Use selected as chest')"; return Refuse(why); }
        if (KeyId == 0) { why = "pick a key first (select it in your pack, then 'Use selected as key')"; return Refuse(why); }
        if (!_env.ChestLoaded(ChestId, out double meters)) { why = $"{ChestName} isn't loaded (out of view?); walk closer"; return Refuse(why); }
        if (meters > MaxStartMeters) { why = $"{ChestName} is {meters:0} m away (more than {MaxStartMeters:0} m); walk closer"; return Refuse(why); }
        int key = _env.FindKey(KeyId, KeyName, 0);
        if (key == 0) { why = $"no '{KeyName}' in your packs"; return Refuse(why); }
        KeyId = key;

        Looted = 0; _cycles = 0; _probes = 0; _relockExpected = false; _goneSince = 0; _holdSince = 0;
        _timeouts = _busy = _openFails = _sendFails = _refusals = _openElsewhere = 0;
        _lastThink = 0; _nextTryAt = 0; LastResult = "";
        State = Phase.Ready;
        _env.Say($"[RynthAi] Chest looter: starting on {ChestName} with {KeyName}. It uses the key, opens the chest, loots it with the current loot profile and repeats until the key is used up. Stop: /ra itemtool chest stop.");
        _env.Log($"[RynthAi] Chest looter: start chest 0x{(uint)ChestId:X8} '{ChestName}' key 0x{(uint)KeyId:X8} '{KeyName}' ({meters:0.0} m)");
        return true;
    }

    private bool Refuse(string why)
    {
        _env.Say($"[RynthAi] Chest looter: can't start - {why}.");
        return false;
    }

    /// <summary>The player asked to stop (always works).</summary>
    public void Stop(string why = "stopped")
    {
        if (!Running) { _env.Say("[RynthAi] Chest looter: not running."); return; }
        Finish(why);
    }

    private void Finish(string why)
    {
        Phase was = State;
        State = Phase.Idle;
        if (was == Phase.Looting && ChestId != 0)
        {
            try { _env.AbortLooting(ChestId); } catch { /* the stop must not throw */ }
        }
        LastResult = $"stopped: {why} ({Looted} chest(s) looted)";
        _env.Say($"[RynthAi] Chest looter {LastResult}.");
        _env.Log($"[RynthAi] Chest looter: finish - {why}; looted {Looted}, cycles {_cycles}");
    }

    /// <summary>A chat line from the server (the key's answers).</summary>
    public void OnChat(string? text)
    {
        if (!Running || string.IsNullOrEmpty(text) || text.Length > 160) return;
        if (State is not (Phase.KeyOut or Phase.KeyPending)) return;
        if (UnlockedText.IsMatch(text)) { _sawUnlocked = true; return; }
        Match m = KeyUsedUpText.Match(text);
        if (!m.Success) return;
        if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out int n)) UsesLeft = n;
        else { _keyUsedUp = true; UsesLeft = 0; }
    }

    /// <summary>One line for the panel.</summary>
    public string Status
    {
        get
        {
            long now = _env.Now;
            string key = KeyName.Length == 0 ? "no key" : UsesLeft >= 0 ? $"{KeyName} ({UsesLeft} use{(UsesLeft == 1 ? "" : "s")} left)" : KeyName;
            string tail = $"{Looted} looted";
            if (_holdWhy.Length > 0 && State is Phase.KeyPending or Phase.OpenPending)
                return $"Waiting: {_holdWhy} - {tail}.";
            switch (State)
            {
                case Phase.Idle: return LastResult.Length > 0 ? $"Chest looter {LastResult}." : "Idle.";
                case Phase.Waiting:
                    return $"Waiting for {ChestName} to re-lock ({Math.Max(0, (_waitUntil - now + 999) / 1000)} s) - {key}, {tail}.";
                case Phase.Looting: return $"Looting {ChestName} - {key}, {tail}.";
                case Phase.KeyPending or Phase.KeyOut: return $"Using {KeyName} on {ChestName} - {tail}.";
                case Phase.OpenPending or Phase.OpenOut: return $"Opening {ChestName} - {key}, {tail}.";
                default: return $"Starting - {tail}.";
            }
        }
    }

    /// <summary>Call every plugin tick.</summary>
    public void Tick()
    {
        if (State == Phase.Idle) return;
        long now = _env.Now;
        if (now - _lastThink < ThinkMs) return;
        _lastThink = now;

        if (!_env.ChestLoaded(ChestId, out double meters))
        {
            if (_goneSince == 0) _goneSince = now;
            else if (now - _goneSince >= GoneMs) Finish($"{ChestName} is gone (out of view, or you left)");
            return;
        }
        _goneSince = 0;

        switch (State)
        {
            case Phase.Ready: TickReady(now, meters); break;
            case Phase.KeyPending: TickKeyPending(now); break;
            case Phase.KeyOut: TickKeyOut(now); break;
            case Phase.OpenPending: TickOpenPending(now); break;
            case Phase.OpenOut: TickOpenOut(now); break;
            case Phase.Looting: TickLooting(now); break;
            case Phase.Waiting: TickWaiting(now); break;
        }
    }

    // ── Phases ───────────────────────────────────────────────────────────────

    private void TickReady(long now, double meters)
    {
        if (_cycles >= MaxCycles) { Finish($"reached {MaxCycles} cycles (a key with unlimited uses never runs out)"); return; }
        int key = _env.FindKey(KeyId, KeyName, 0);
        if (key == 0) { Finish($"out of keys - no '{KeyName}' left"); return; }
        if (key != KeyId)
        {
            _env.Log($"[RynthAi] Chest looter: key 0x{(uint)KeyId:X8} is gone; using 0x{(uint)key:X8} '{KeyName}'");
            KeyId = key;
            UsesLeft = -1;
        }
        if (meters > MaxStartMeters) { Finish($"{ChestName} is {meters:0} m away (more than {MaxStartMeters:0} m)"); return; }
        _keyUsesThisCycle = 0;
        _openFails = 0;
        _sawUnlocked = false;
        _keyUsedUp = false;
        State = Phase.KeyPending;
    }

    /// <summary>True while something holds the send; the run stops after HoldMaxMs of it.</summary>
    private bool Held(long now)
    {
        string? why = _env.HoldReason();
        if (why == null) { _holdSince = 0; _holdWhy = ""; return false; }
        _holdWhy = why;
        if (_holdSince == 0) { _holdSince = now; _env.Log($"[RynthAi] Chest looter: waits - {why}"); }
        else if (now - _holdSince >= HoldMaxMs) Finish($"waited {HoldMaxMs / 1000} s: {why}");
        return true;
    }

    private bool Answered(long sentAt, out uint error)
    {
        error = 0;
        long now = _env.Now;
        if (!_env.HasUseDone) return now - sentAt >= NoUseDoneFallbackMs;
        if (_env.UseDoneSeq() == _seqAtSend) return false;
        if (_env.LastUseDone(out int seq, out uint err) && seq == _seqAtSend + 1) error = err;
        return true;
    }

    private void TickKeyPending(long now)
    {
        if (now < _nextTryAt) return;
        if (Held(now)) return;
        int key = _env.FindKey(KeyId, KeyName, _keyUsedUp ? KeyId : 0);
        if (key == 0) { Finish($"out of keys - no '{KeyName}' left"); return; }
        KeyId = key;
        _keyUsedUp = false;

        _seqAtSend = _env.UseDoneSeq();
        _sawUnlocked = false;
        if (!_env.SendKey(KeyId, ChestId))
        {
            if (++_sendFails >= MaxSendFails) Finish("the client wouldn't send the key use");
            else _nextTryAt = now + SendRetryMs;
            return;
        }
        _sendFails = 0;
        _keySentAt = now;
        _answerSeen = false;
        State = Phase.KeyOut;
    }

    private void TickKeyOut(long now)
    {
        if (!_answerSeen)
        {
            if (!Answered(_keySentAt, out uint e0))
            {
                if (now - _keySentAt >= AnswerMaxMs) Retry(now, Phase.KeyPending, "no answer from the server to the key use");
                return;
            }
            _answerSeen = true;
            _answeredAt = now;
            _lastErr = e0;
            return;   // the chat line of this answer gets ChatSettleMs
        }
        if (now - _answeredAt < ChatSettleMs) return;
        _answerSeen = false;
        OnKeyAnswer(now, _lastErr);
    }

    private uint _lastErr;

    private void OnKeyAnswer(long now, uint err)
    {
        switch (err)
        {
            case 0:
                _keyUsesThisCycle++;
                _busy = 0;
                if (!_sawUnlocked)
                    _env.Log("[RynthAi] Chest looter: the key use was answered with no 'has been unlocked' line; trying the chest anyway");
                State = Phase.OpenPending;
                _nextTryAt = now + 400;
                return;

            case ErrLockAlreadyUnlocked:
                // Nothing was spent. First time: the chest simply isn't locked - open it. After a
                // looted chest: it has not re-locked (and regenerated) yet - ask again later.
                if (!_relockExpected)
                {
                    State = Phase.OpenPending;
                    _nextTryAt = now + 400;
                    return;
                }
                if (++_probes > MaxProbes)
                {
                    Finish($"{ChestName} hasn't re-locked after {MaxProbes} tries ({(MaxProbes * ProbeMs + RegenWaitMs) / 60000} min): its reset timer may be longer, or it doesn't lock");
                    return;
                }
                _env.Log($"[RynthAi] Chest looter: {ChestName} is still unlocked (not reset yet); asking again in {ProbeMs / 1000} s (try {_probes} of {MaxProbes})");
                EnterWaiting(now + ProbeMs);
                return;

            case ErrKeyDoesntFit:
                Finish($"that key doesn't fit {ChestName}");
                return;
            case ErrCannotLockOrUnlock:
                Finish($"the server says {ChestName} can't be unlocked with a key");
                return;
            case ErrCannotLockWhatIsOpen:
                if (++_openElsewhere >= MaxOpenElsewhere) { Finish($"{ChestName} stays open (someone else has it?)"); return; }
                _env.Log($"[RynthAi] Chest looter: {ChestName} is open; trying again in 5 s");
                EnterWaiting(now + 5000);
                return;
            case ErrCannotUseThatItem:
                // A key with no uses left: ACE deletes it.
                _keyUsedUp = true;
                _env.Log("[RynthAi] Chest looter: the server says the key can't be used (spent); looking for another");
                State = Phase.KeyPending;
                _nextTryAt = now + SendRetryMs;
                return;
            case ErrTooBusy:
                if (++_busy >= MaxBusy) { Finish("the server keeps answering 'too busy'"); return; }
                _nextTryAt = now + BusyBackoffMs;
                State = Phase.KeyPending;
                return;
            default:
                if (++_refusals >= MaxRefusals) { Finish($"the server refused the key use (0x{err:X})"); return; }
                _env.Log($"[RynthAi] Chest looter: the server answered the key use with error 0x{err:X}; trying again");
                _nextTryAt = now + RetryMs;
                State = Phase.KeyPending;
                return;
        }
    }

    private void Retry(long now, Phase back, string why)
    {
        if (++_timeouts >= MaxTimeouts) { Finish(why); return; }
        _env.Log($"[RynthAi] Chest looter: {why}; trying again ({_timeouts} of {MaxTimeouts})");
        State = back;
        _nextTryAt = now + RetryMs;
    }

    private void TickOpenPending(long now)
    {
        if (now < _nextTryAt) return;
        // Another opener already put it in front of us (the player, or the looter's own take).
        if (_env.ChestOpenedAt(ChestId) > _keySentAt) { OnOpened(now); return; }
        if (Held(now)) return;
        _seqAtSend = _env.UseDoneSeq();
        if (!_env.SendOpen(ChestId))
        {
            if (++_sendFails >= MaxSendFails) Finish("the client wouldn't send the open");
            else _nextTryAt = now + SendRetryMs;
            return;
        }
        _sendFails = 0;
        _openSentAt = now;
        _answerSeen = false;
        State = Phase.OpenOut;
    }

    private void TickOpenOut(long now)
    {
        if (_env.ChestOpenedAt(ChestId) >= _openSentAt) { OnOpened(now); return; }
        if (!_answerSeen && Answered(_openSentAt, out _)) { _answerSeen = true; _answeredAt = now; }
        bool failed = _answerSeen ? now - _answeredAt >= OpenSettleMs : now - _openSentAt >= AnswerMaxMs;
        if (!failed) return;
        if (++_openFails >= MaxOpenFails)
        {
            Finish($"{ChestName} wouldn't open ({MaxOpenFails} tries: still locked, or someone else is using it)");
            return;
        }
        _env.Log($"[RynthAi] Chest looter: {ChestName} didn't open (try {_openFails} of {MaxOpenFails})");
        State = _keyUsesThisCycle == 0 || !_sawUnlocked ? Phase.KeyPending : Phase.OpenPending;
        _nextTryAt = now + 2000;
    }

    private void OnOpened(long now)
    {
        _openedAt = _env.ChestOpenedAt(ChestId);
        if (_openedAt == 0) _openedAt = now;
        _env.StartLooting(ChestId);
        _lootSeen = _env.Looting(ChestId);
        _lootStartedAt = now;
        _timeouts = 0;
        State = Phase.Looting;
    }

    private void TickLooting(long now)
    {
        if (_env.Looting(ChestId))
        {
            _lootSeen = true;
            if (now - _lootStartedAt >= LootMaxMs) Finish($"looting {ChestName} took more than {LootMaxMs / 60000} minutes");
            return;
        }
        if (!_lootSeen && now - _lootStartedAt < LootStartMs) return;
        if (!_lootSeen) { Finish($"the loot window never took {ChestName}"); return; }

        // The loot session ended and the chest is closed.
        Looted++;
        _cycles++;
        _probes = 0;
        _relockExpected = true;
        _env.Say($"[RynthAi] Chest looter: looted {ChestName} ({Looted} so far).");
        int key = _env.FindKey(KeyId, KeyName, _keyUsedUp ? KeyId : 0);
        if (key == 0) { Finish($"out of keys after {Looted} chest(s)"); return; }
        if (_cycles >= MaxCycles) { Finish($"reached {MaxCycles} cycles (a key with unlimited uses never runs out)"); return; }
        KeyId = key;
        _keyUsedUp = false;   // that was the old key; the one chosen now has not been used
        EnterWaiting(Math.Max(now + 2000, _openedAt + RegenWaitMs));
    }

    private void EnterWaiting(long until)
    {
        _waitUntil = until;
        State = Phase.Waiting;
    }

    private void TickWaiting(long now)
    {
        // The key may leave the pack meanwhile (traded, dropped): nothing to wait for then.
        if (_env.FindKey(KeyId, KeyName, _keyUsedUp ? KeyId : 0) == 0) { Finish($"out of keys - no '{KeyName}' left"); return; }
        if (now >= _waitUntil) { State = Phase.Ready; return; }
        if (_probes == 0 && now >= _openedAt + MinRelockMs && _env.ChestLockedHint(ChestId) == true)
        {
            _env.Log($"[RynthAi] Chest looter: {ChestName} reads locked again; next cycle");
            State = Phase.Ready;
        }
    }
}
