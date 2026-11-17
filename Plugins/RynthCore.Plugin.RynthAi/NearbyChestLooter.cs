// ============================================================================
//  NearbyChestLooter.cs - Loot Nearby Chests (2026-11-14).
//
//  The owner's words: while the macro runs, Looting is on and he isn't fighting, the character
//  walks to a chest within loot range, opens it and loots it by the current loot profile. Then it
//  leaves the chest alone until it respawns or relocks. A locked chest: a carried key that fits is
//  used; else, with a lockpick and a Lockpick skill good enough for that chest, it is picked; else
//  it is skipped and tried again in about 10 minutes. Off by default (LootNearbyChests).
//
//  A pure state machine: everything it touches goes through INearbyChestEnv (the plugin implements
//  it in RynthAiPlugin.NearbyChests.cs; the host tests fake it). It never walks: every step is a
//  use the server answers, and ACE walks the character to the chest first for each of them
//  (Player.HandleActionUseItem / HandleActionUseWithTarget -> CreateMoveToChain), the way the
//  corpse looter's far open works. Once the chest's ViewContents arrives, the corpse looter takes
//  the open chest as its loot container (RynthAiPlugin.ChestLoot.cs: the loot profile's rules, the
//  same TickOpenCorpseLooting as a corpse) and this waits for it to finish.
//
//  One chest:
//    Appraise  - an ID of the chest (one request), for its lock: PropertyBool Locked (3),
//                ResistLockpick (int 38) and the server's own AppraisalLockpickSuccessPercent
//                (int 173), all three AssessmentProperty in ACE (AppraiseInfo.cs 135-163).
//    Key       - a locked chest: each carried key not yet tried on it, one at a time. The client
//                can't know up front which key fits: a key's KeyCode and a chest's LockCode
//                (PropertyString 13/12) are not sent to clients (no AssessmentProperty,
//                PropertyString.cs 30-31). So a key is tried and the server's answer decides:
//                0 = unlocked (one use spent), 0x4AF already unlocked, 0x4B2 KeyDoesntFitThisLock
//                (nothing spent: LockHelper.Unlock returns IncorrectKey, Lock.cs 248-281).
//                At most MaxKeyTries keys per chest per attempt; a key that didn't fit is never
//                tried on that chest again.
//    Pick      - no key fitted: a lockpick, when the Lockpick skill is good enough (CanPick).
//                ACE's roll (LockHelper.Unlock, Lock.cs 282-313) is SkillCheck.GetSkillChance(
//                skill, ResistLockpick) = 1 - 1/(1 + e^(0.03 (skill - resist))) (SkillCheck.cs 7).
//                "Good enough" = at least MinPickPercent (50 %, skill >= resistance). A failed
//                pick spends one use of the lockpick, so at most MaxPicks a chest per attempt.
//    Open      - the chest is used; its ViewContents is the proof.
//    Looting   - the corpse looter has the open chest; this waits for it to let go.
//  Then the chest is Done (left alone) until it leaves the world, reads locked again, or the
//  re-lock wait passes (the Item Tool's "Chest re-lock wait (s)", 125 s: ACE's Chest.Reset runs
//  ResetInterval, default 120 s, after the open; it re-locks a DefaultLocked chest and
//  regenerates its contents, Chest.cs 184-266). A chest that was locked is appraised before it is
//  used again: still unlocked means it hasn't reset, so it waits another round without a use.
//  Skipped (no key fits, can't pick, won't open...): tried again after SkipMs (10 minutes).
//
//  Never spams the server: one request at a time, each waits for its answer (UseDone / the ID),
//  nothing goes out while HoldReason says so (the arbiter's turn, a busy client, a pending action),
//  and every step is bounded.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi;

/// <summary>A chest in loot range that may be looted (the host has checked what it is).</summary>
internal readonly record struct NearbyChest(int Id, string Name, double Meters);

/// <summary>A carried key or lockpick.</summary>
internal readonly record struct ChestTool(int Id, string Name);

internal interface INearbyChestEnv
{
    long Now { get; }
    /// <summary>The setting is on and the bot may loot chests now (macro, Looting, nothing else owns chests).</summary>
    bool Enabled { get; }
    /// <summary>A fight is on (combat has targets in range): no chest is started, one not yet sent to is dropped.</summary>
    bool Fighting { get; }
    /// <summary>Eligible chests in loot range now (not house storage, a vendor, a corpse, a player or a pack).</summary>
    IReadOnlyList<NearbyChest> ScanChests();
    /// <summary>The object is still in the client's world.</summary>
    bool InWorld(int id);
    /// <summary>The chest is still a chest to loot and within reach (the loot range, with slack).</summary>
    bool StillEligible(int id, out string why);
    /// <summary>Why nothing may be sent now (null = free).</summary>
    string? HoldReason();

    // The chest's lock, as the client reads it.
    bool CanAppraise { get; }
    bool RequestAppraisal(int chestId);
    /// <summary>Changes when appraisal data for the object arrives (0 = none yet).</summary>
    long AppraisalStamp(int chestId);
    bool? Locked(int chestId);
    int? LockResistance(int chestId);
    int? PickSuccessPercent(int chestId);

    // The character.
    /// <summary>Lockpick skill (buffed) and whether it is trained; false = unreadable.</summary>
    bool LockpickSkill(out int buffed, out bool trained);
    IReadOnlyList<ChestTool> Keys();
    ChestTool? Lockpick();

    // The server.
    bool HasUseDone { get; }
    int UseDoneSeq();
    bool LastUseDone(out int seq, out uint error);
    bool SendUseWith(int toolId, int chestId, string why);
    bool SendOpen(int chestId);
    /// <summary>When the server last opened this chest for us (its ViewContents); 0 = not seen.</summary>
    long OpenedAt(int chestId);
    /// <summary>The corpse looter has the chest (open and being looted, or about to be taken).</summary>
    bool Looting(int chestId);
    void Log(string text);
}

internal sealed class NearbyChestLooter
{
    public enum Phase { Idle, Appraise, AppraiseOut, Key, KeyOut, Pick, PickOut, Open, OpenOut, Looting }

    // ACE WeenieError codes (WeenieError.cs; UnlockerHelper.UseUnlocker, Lock.cs 119-196).
    internal const uint ErrTooBusy = 0x1D, ErrLockAlreadyUnlocked = 0x4AF, ErrCannotLockOrUnlock = 0x4B0,
        ErrCannotLockWhatIsOpen = 0x4B1, ErrKeyDoesntFit = 0x4B2, ErrNotTrainedInLockpicking = 0x4B4,
        ErrCannotUseThatItem = 0x58D;

    /// <summary>A skipped chest (no key fits, can't pick it, won't open) is tried again after this.</summary>
    public const long SkipMs = 600_000;
    /// <summary>"Skill good enough": the chance ACE rolls is at least this (50 % = skill equal to the lock's resistance).</summary>
    public const int MinPickPercent = 50;
    /// <summary>Lockpick uses per chest per attempt (each failed pick spends one use of the lockpick).</summary>
    public const int MaxPicks = 3;
    /// <summary>... when the lock's resistance can't be read (no appraisal): a couple of tries, then skip.</summary>
    public const int MaxPicksUnknown = 2;
    /// <summary>Different keys tried on one chest per attempt.</summary>
    public const int MaxKeyTries = 4;
    /// <summary>A use waits this long for the server's answer (the walk to the chest is part of it).</summary>
    public const long AnswerMaxMs = 12_000;
    /// <summary>The chest's appraisal gets this long.</summary>
    public const long AppraiseMaxMs = 4_000;
    /// <summary>After an answer, the chat line / lock update that goes with it gets this long.</summary>
    public const long SettleMs = 300;
    /// <summary>After the open's answer, the chest gets this long to show its contents.</summary>
    public const long OpenSettleMs = 1_500;
    /// <summary>The corpse looter has this long to take the open chest, and the loot this long at most.</summary>
    public const long LootStartMs = 8_000, LootMaxMs = 300_000;
    /// <summary>A chest that reads locked again this soon after its open is a candidate again.</summary>
    public const long MinRelockMs = 15_000;
    /// <summary>Between scans for a new chest.</summary>
    public const long ScanMs = 500;
    /// <summary>Nothing could be sent this long (the server busy, the turn elsewhere): the chest is dropped for now.</summary>
    public const long HoldMaxMs = 30_000;
    public const int MaxTimeouts = 2, MaxBusy = 3, MaxOpenFails = 2;

    private static readonly Regex PickedText = new(@"successfully picked the lock", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PickFailedText = new(@"failed to pick the lock", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KeyUnlockedText = new(@"\bhas been unlocked\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly INearbyChestEnv _env;
    public NearbyChestLooter(INearbyChestEnv env) => _env = env;

    /// <summary>"Chest re-lock wait (s)" (the host sets it from the settings): a looted chest is left this long at most.</summary>
    public int RelockWaitSec { get; set; } = ItemTool.ChestKeyLooter.DefaultRelockWaitSec;
    private long RelockWaitMs => ItemTool.ChestKeyLooter.ClampRelockWaitSec(RelockWaitSec) * 1000L;

    public Phase State { get; private set; } = Phase.Idle;
    public int ChestId { get; private set; }
    public string ChestName { get; private set; } = "";
    /// <summary>Past picking: a request is out or the chest is being opened/looted (the corpse looter must wait).</summary>
    public bool Busy => State != Phase.Idle;
    public int Looted { get; private set; }
    public string LastResult { get; private set; } = "";

    // ── Which objects are chests ──────────────────────────────────────────

    internal const uint BfStuck = 0x00000004, BfPlayer = 0x00000008, BfVendor = 0x00000200, BfDoor = 0x00001000, BfCorpse = 0x00002000;

    /// <summary>
    /// Why an object in loot range is not a chest to walk to and loot (null = it is one). A chest is
    /// a container lying in the world that can't be picked up: ACE's chests are Stuck (the
    /// PublicWeenieDesc bitfield's 0x4); a pack someone dropped is not, so it is never looted here.
    /// Never a corpse (the corpse looter's), a vendor, a player, a door, house storage (HouseOwner,
    /// or named Storage / a hook, as RynthAiPlugin.ChestLoot.cs's JudgeChest), or anything held.
    /// </summary>
    internal static string? ChestRefusal(RynthCore.Loot.AcObjectClass cls, bool bitfieldKnown, uint bitfield,
        uint houseOwner, string? name, bool ownershipKnown, uint container, uint wielder)
    {
        if (cls == RynthCore.Loot.AcObjectClass.Corpse) return "a corpse";
        if (cls != RynthCore.Loot.AcObjectClass.Container) return "not a container";
        if (!bitfieldKnown) return "the engine can't tell a chest from a dropped pack";
        if ((bitfield & BfCorpse) != 0) return "a corpse";
        if ((bitfield & BfVendor) != 0) return "a vendor";
        if ((bitfield & BfPlayer) != 0) return "a player";
        if ((bitfield & BfDoor) != 0) return "a door";
        if (houseOwner != 0) return $"house storage (house owner 0x{houseOwner:X8})";
        if (RynthAiPlugin.IsHouseStorageName(name)) return $"house storage (named '{name}')";
        if ((bitfield & BfStuck) == 0) return "it can be picked up (a pack, not a chest)";
        if (!ownershipKnown) return "who holds it isn't readable";
        if (container != 0 || wielder != 0) return "someone holds it";
        return null;
    }

    // ── What happened to each chest ────────────────────────────────────────

    private sealed class Memory
    {
        public string Name = "";
        public long LeaveUntil;           // left alone until then (Done or Skipped)
        public bool Done;                 // looted (else skipped)
        public bool WasLocked;            // it was locked when we got to it: it is ready again when it re-locks
        public long OpenedAt;
        public string Why = "";
        public readonly HashSet<string> KeysThatDontFit = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<int, Memory> _memory = new();

    /// <summary>The chest is being left alone now (looted and not reset yet, or skipped).</summary>
    public bool IsLeftAlone(int id, long now)
    {
        if (!_memory.TryGetValue(id, out var m) || now >= m.LeaveUntil) return false;
        // Looted and locked when we found it: ready again as soon as it reads locked (it has reset).
        if (m.Done && m.WasLocked && now - m.OpenedAt >= MinRelockMs && _env.Locked(id) == true) return false;
        return true;
    }

    /// <summary>For the status line: the chests left alone and why.</summary>
    public IEnumerable<(int Id, string Name, bool Done, long LeftMs, string Why)> LeftAlone()
    {
        long now = _env.Now;
        foreach (var (id, m) in _memory)
            if (IsLeftAlone(id, now))
                yield return (id, m.Name, m.Done, m.LeaveUntil - now, m.Why);
    }

    private Memory Mem(int id)
    {
        if (!_memory.TryGetValue(id, out var m)) _memory[id] = m = new Memory();
        return m;
    }

    // ── The current chest ─────────────────────────────────────────────────

    private long _lastScan, _sentAt, _answeredAt, _phaseSince, _holdSince, _appraiseStamp, _lootStartedAt;
    private int _seqAtSend, _toolId, _keyTries, _picks, _pickLimit, _timeouts, _busy, _openFails;
    private string _toolName = "";
    private bool _answered, _unlocked, _triedUnlock, _lockedAtStart, _appraisedFresh, _pickedSaid, _pickFailSaid, _keyUnlockSaid, _lootSeen;
    private uint _answerErr;
    private int _fightLoggedFor;

    /// <summary>A chest to start on now (HasLootWork asks): nearest eligible one not left alone. 0 = none.</summary>
    public int Candidate()
    {
        if (!_env.Enabled || _env.Fighting) return 0;
        if (Busy) return ChestId;
        long now = _env.Now;
        if (now - _lastScan < ScanMs && _pick == 0) return 0;
        if (now - _lastScan >= ScanMs)
        {
            _lastScan = now;
            Prune();
            _pick = 0;
            double best = double.MaxValue;
            foreach (var c in _env.ScanChests())
            {
                if (IsLeftAlone(c.Id, now) || c.Meters >= best) continue;
                _pick = c.Id;
                _pickName = c.Name;
                best = c.Meters;
            }
        }
        else if (_pick != 0 && IsLeftAlone(_pick, now)) _pick = 0;
        return _pick;
    }

    private int _pick;
    private string _pickName = "";

    private void Prune()
    {
        List<int>? gone = null;
        foreach (int id in _memory.Keys)
            if (!_env.InWorld(id)) (gone ??= new()).Add(id);
        if (gone == null) return;
        foreach (int id in gone)
        {
            _memory.Remove(id);
            _env.Log($"[RynthAi] Nearby chests: chest 0x{(uint)id:X8} left the world; forgotten (a new one is looted when it comes).");
        }
    }

    /// <summary>The setting went off, the macro stopped, Looting went off...: drop the chest (nothing is marked).</summary>
    public void StandDown(string why)
    {
        if (State == Phase.Idle) { _pick = 0; return; }
        _env.Log($"[RynthAi] Nearby chests: stopped on chest 0x{(uint)ChestId:X8} '{ChestName}' - {why}; it will be tried again.");
        Reset();
        _pick = 0;
    }

    private void Reset()
    {
        State = Phase.Idle;
        ChestId = 0;
        ChestName = "";
        _holdSince = 0;
    }

    /// <summary>A server chat line (the lockpick's and the key's answers).</summary>
    public void OnChat(string? text)
    {
        if (State is not (Phase.KeyOut or Phase.PickOut) || string.IsNullOrEmpty(text) || text.Length > 200) return;
        if (PickedText.IsMatch(text)) _pickedSaid = true;
        else if (PickFailedText.IsMatch(text)) _pickFailSaid = true;
        else if (KeyUnlockedText.IsMatch(text)) _keyUnlockSaid = true;
    }

    /// <summary>
    /// Every loot tick, whoever has the tick (answers and timeouts are read even while a fight holds
    /// the sends). <paramref name="mayStart"/>: a new chest may be started (the host passes true only
    /// where no corpse is waiting: corpses go first).
    /// </summary>
    public void Tick(bool mayStart = false)
    {
        long now = _env.Now;
        if (State == Phase.Idle)
        {
            if (!mayStart) return;
            int id = Candidate();
            if (id == 0) return;
            if (_env.HoldReason() != null) return;   // its turn has not come yet (nothing is claimed)
            Begin(id, _pickName, now);
        }

        if (!_env.InWorld(ChestId)) { Leave(now, "it left the world", skip: false); return; }

        // A fight: a chest nothing has been sent to is dropped (picked again when the fight is over);
        // an answer that is owed is waited for, and the corpse looter decides about an open chest.
        if (_env.Fighting && State is Phase.Appraise or Phase.Key or Phase.Pick or Phase.Open)
        {
            if (_fightLoggedFor != ChestId)
            {
                _fightLoggedFor = ChestId;
                _env.Log($"[RynthAi] Nearby chests: leaving chest 0x{(uint)ChestId:X8} '{ChestName}' for the fight; it is tried again when the fight is over.");
            }
            Reset();
            return;
        }
        if (State is Phase.Appraise or Phase.Key or Phase.Pick or Phase.Open && !_env.StillEligible(ChestId, out string why))
        {
            Leave(now, why, skip: false);
            return;
        }

        switch (State)
        {
            case Phase.Appraise: TickAppraise(now); break;
            case Phase.AppraiseOut: TickAppraiseOut(now); break;
            case Phase.Key: TickKey(now); break;
            case Phase.KeyOut: TickKeyOut(now); break;
            case Phase.Pick: TickPick(now); break;
            case Phase.PickOut: TickPickOut(now); break;
            case Phase.Open: TickOpen(now); break;
            case Phase.OpenOut: TickOpenOut(now); break;
            case Phase.Looting: TickLooting(now); break;
        }
    }

    private void Begin(int id, string name, long now)
    {
        ChestId = id;
        ChestName = name.Length > 0 ? name : "the chest";
        _keyTries = _picks = _timeouts = _busy = _openFails = 0;
        _pickLimit = 0;
        _unlocked = _triedUnlock = _lockedAtStart = _appraisedFresh = false;
        _holdSince = 0;
        _pick = 0;
        Mem(id).Name = ChestName;
        _env.Log($"[RynthAi] Nearby chests: going to chest 0x{(uint)id:X8} '{ChestName}' (in loot range, not looted since it last reset).");
        Enter(_env.CanAppraise ? Phase.Appraise : Phase.Open, now);
    }

    private void Enter(Phase p, long now)
    {
        State = p;
        _phaseSince = now;
        _holdSince = 0;
    }

    /// <summary>True while nothing may go out; the chest is dropped (not marked) after HoldMaxMs of it.</summary>
    private bool Held(long now)
    {
        string? why = _env.HoldReason();
        if (why == null) { _holdSince = 0; return false; }
        if (_holdSince == 0) _holdSince = now;
        else if (now - _holdSince >= HoldMaxMs)
        {
            _env.Log($"[RynthAi] Nearby chests: chest 0x{(uint)ChestId:X8} '{ChestName}' waited {HoldMaxMs / 1000} s to send ({why}); dropped for now.");
            Reset();
        }
        return true;
    }

    // ── Appraise ──────────────────────────────────────────────────────────

    private void TickAppraise(long now)
    {
        if (Held(now)) return;
        _appraiseStamp = _env.AppraisalStamp(ChestId);
        if (!_env.RequestAppraisal(ChestId))
        {
            _env.Log($"[RynthAi] Nearby chests: the client wouldn't appraise chest 0x{(uint)ChestId:X8}; trying it as it is.");
            AfterAppraisal(now, fresh: false);
            return;
        }
        _sentAt = now;
        Enter(Phase.AppraiseOut, now);
    }

    private void TickAppraiseOut(long now)
    {
        bool arrived = _env.AppraisalStamp(ChestId) != _appraiseStamp;
        if (!arrived && now - _sentAt < AppraiseMaxMs) return;
        if (!arrived)
            _env.Log($"[RynthAi] Nearby chests: no appraisal of chest 0x{(uint)ChestId:X8} in {AppraiseMaxMs / 1000} s; going by what the client knows.");
        AfterAppraisal(now, arrived);
    }

    private void AfterAppraisal(long now, bool fresh)
    {
        _appraisedFresh = fresh;
        bool? locked = _env.Locked(ChestId);
        var m = Mem(ChestId);
        // Looted before, locked then, unlocked now: it hasn't reset yet (ACE re-locks it as it regenerates).
        if (locked == false && m.Done && m.WasLocked)
        {
            Leave(now, "still unlocked since we looted it: it hasn't reset yet", skip: false, again: true);
            return;
        }
        _lockedAtStart = locked == true;
        if (_lockedAtStart)
        {
            _env.Log($"[RynthAi] Nearby chests: chest 0x{(uint)ChestId:X8} '{ChestName}' is locked{LockText()}; trying carried keys first.");
            _triedUnlock = true;
            Enter(Phase.Key, now);
            return;
        }
        Enter(Phase.Open, now);
    }

    private string LockText()
    {
        int? resist = _env.LockResistance(ChestId);
        int? pct = _env.PickSuccessPercent(ChestId);
        if (resist == null) return "";
        return pct != null ? $" (lock resistance {resist}, the server gives your Lockpick skill {pct}%)" : $" (lock resistance {resist})";
    }

    // ── Keys ──────────────────────────────────────────────────────────────

    private ChestTool? NextKey()
    {
        var m = Mem(ChestId);
        foreach (var k in _env.Keys())
            if (!m.KeysThatDontFit.Contains(k.Name)) return k;
        return null;
    }

    private void TickKey(long now)
    {
        ChestTool? key = _keyTries < MaxKeyTries ? NextKey() : null;
        if (key == null)
        {
            if (_keyTries >= MaxKeyTries)
                _env.Log($"[RynthAi] Nearby chests: {MaxKeyTries} keys tried on chest 0x{(uint)ChestId:X8}; none fitted.");
            Enter(Phase.Pick, now);
            return;
        }
        if (Held(now)) return;
        if (!Send(now, key.Value, $"Loot Nearby Chests: try {key.Value.Name} on the locked chest")) return;
        _keyTries++;
        Enter(Phase.KeyOut, now);
    }

    private bool Send(long now, ChestTool tool, string why)
    {
        _seqAtSend = _env.UseDoneSeq();
        _pickedSaid = _pickFailSaid = _keyUnlockSaid = false;
        if (!_env.SendUseWith(tool.Id, ChestId, why))
        {
            Leave(now, $"the client wouldn't send the use of {tool.Name}", skip: true);
            return false;
        }
        _toolId = tool.Id;
        _toolName = tool.Name;
        _sentAt = now;
        _answered = false;
        return true;
    }

    /// <summary>The server answered the last use (a UseDone since it went out), after the settle time for its chat line.</summary>
    private bool Answer(long now, out uint err, out bool timedOut)
    {
        err = 0;
        timedOut = false;
        if (!_answered)
        {
            bool done;
            if (!_env.HasUseDone) done = now - _sentAt >= 3000;
            else done = _env.UseDoneSeq() != _seqAtSend;
            if (!done)
            {
                if (now - _sentAt >= AnswerMaxMs) { timedOut = true; return true; }
                return false;
            }
            _answered = true;
            _answeredAt = now;
            _answerErr = 0;
            if (_env.HasUseDone && _env.LastUseDone(out int seq, out uint e) && seq == _seqAtSend + 1) _answerErr = e;
            return false;
        }
        if (now - _answeredAt < SettleMs) return false;
        err = _answerErr;
        return true;
    }

    private void TickKeyOut(long now)
    {
        if (!Answer(now, out uint err, out bool timedOut)) return;
        if (timedOut) { _keyTries--; Timeout(now, Phase.Key, $"no answer to {_toolName}"); return; }
        var m = Mem(ChestId);
        switch (err)
        {
            case 0:
            case ErrLockAlreadyUnlocked:
                _env.Log(err == 0
                    ? $"[RynthAi] Nearby chests: {_toolName} unlocked chest 0x{(uint)ChestId:X8}{(_keyUnlockSaid ? "" : " (the answer had no 'has been unlocked' line)")}; opening it."
                    : $"[RynthAi] Nearby chests: chest 0x{(uint)ChestId:X8} is already unlocked; opening it.");
                _unlocked = true;
                Enter(Phase.Open, now);
                return;
            case ErrKeyDoesntFit:
            case ErrCannotUseThatItem:
                m.KeysThatDontFit.Add(_toolName);
                _env.Log(err == ErrKeyDoesntFit
                    ? $"[RynthAi] Nearby chests: {_toolName} doesn't fit chest 0x{(uint)ChestId:X8}; it won't be tried on it again."
                    : $"[RynthAi] Nearby chests: the server says {_toolName} can't be used; trying the next key.");
                Enter(Phase.Key, now);
                return;
            case ErrCannotLockOrUnlock:
                _env.Log($"[RynthAi] Nearby chests: the server says chest 0x{(uint)ChestId:X8} can't be unlocked with a key; a lockpick next.");
                Enter(Phase.Pick, now);
                return;
            case ErrCannotLockWhatIsOpen:
                Leave(now, "it is open (someone else is using it)", skip: true);
                return;
            case ErrTooBusy:
                _keyTries--;
                TooBusy(now, Phase.Key);
                return;
            default:
                m.KeysThatDontFit.Add(_toolName);
                _env.Log($"[RynthAi] Nearby chests: the server answered {_toolName} on chest 0x{(uint)ChestId:X8} with error 0x{err:X}; trying the next key.");
                Enter(Phase.Key, now);
                return;
        }
    }

    // ── Lockpick ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether to pick this chest, and how many tries: null = no (why says it). Untrained can't pick
    /// (ACE refuses: UseUnlocker, Lock.cs 125-131). A lock with no resistance, or 9999 and up,
    /// can't be picked (LockHelper.IsPickable, Lock.cs 203-216). Else the chance the server rolls:
    /// its own AppraisalLockpickSuccessPercent when the appraisal has it, or GetSkillChance from the
    /// skill and the resistance; at least MinPickPercent picks MaxPicks times. Unknown resistance (no
    /// appraisal): MaxPicksUnknown tries.
    /// </summary>
    internal static int? PickTries(bool skillKnown, int skill, bool trained, bool appraised, bool? locked, int? resist, int? serverPercent, out string why)
    {
        why = "";
        if (skillKnown && !trained) { why = "your Lockpick skill isn't trained"; return null; }
        if (appraised && locked == true && resist == null) { why = "its lock can't be picked (no lockpick resistance)"; return null; }
        if (resist is int r && r >= 9999) { why = "its lock can't be picked (resistance 9999)"; return null; }
        if (resist is int res)
        {
            int pct = serverPercent ?? (skillKnown ? (int)(PickChance(skill, res) * 100) : -1);
            if (pct < 0) { why = "your Lockpick skill can't be read"; return null; }
            if (pct < MinPickPercent)
            {
                why = $"your Lockpick skill{(skillKnown ? $" ({skill})" : "")} is too low for it: {pct}% against resistance {res} (it picks at {MinPickPercent}% or better)";
                return null;
            }
            why = $"{pct}% against resistance {res}";
            return MaxPicks;
        }
        why = "its lock resistance is unknown";
        return MaxPicksUnknown;
    }

    /// <summary>ACE SkillCheck.GetSkillChance (factor 0.03), clamped 0..1.</summary>
    internal static double PickChance(int skill, int difficulty)
        => Math.Clamp(1.0 - 1.0 / (1.0 + Math.Exp(0.03 * (skill - difficulty))), 0.0, 1.0);

    private void TickPick(long now)
    {
        if (_pickLimit == 0)
        {
            bool skillKnown = _env.LockpickSkill(out int skill, out bool trained);
            // Only a fresh appraisal counts: ACE leaves the resistance out of an unlocked chest's.
            int? tries = PickTries(skillKnown, skill, trained, _appraisedFresh, _env.Locked(ChestId),
                _env.LockResistance(ChestId), _env.PickSuccessPercent(ChestId), out string why);
            if (tries == null) { Leave(now, $"no key fits and {why}", skip: true); return; }
            if (_env.Lockpick() == null) { Leave(now, "no key fits and you carry no lockpick (Consumable Items or your pack)", skip: true); return; }
            _pickLimit = tries.Value;
            _env.Log($"[RynthAi] Nearby chests: picking chest 0x{(uint)ChestId:X8} '{ChestName}' ({why}; up to {_pickLimit} tries).");
        }
        if (_picks >= _pickLimit) { Leave(now, $"the lock held after {_picks} lockpick tries", skip: true); return; }
        ChestTool? pick = _env.Lockpick();
        if (pick == null) { Leave(now, "the lockpicks ran out", skip: true); return; }
        if (Held(now)) return;
        if (!Send(now, pick.Value, "Loot Nearby Chests: pick the locked chest")) return;
        _picks++;
        Enter(Phase.PickOut, now);
    }

    private void TickPickOut(long now)
    {
        if (!Answer(now, out uint err, out bool timedOut)) return;
        if (timedOut) { _picks--; Timeout(now, Phase.Pick, "no answer to the lockpick"); return; }
        switch (err)
        {
            case 0:
                // ACE answers a success and a failure alike (ConsumeUnlocker sends UseDone 0); the chat
                // line, or the chest's Locked update, says which.
                bool picked = _pickedSaid || (!_pickFailSaid && _env.Locked(ChestId) == false);
                if (picked)
                {
                    _env.Log($"[RynthAi] Nearby chests: picked the lock of chest 0x{(uint)ChestId:X8} (try {_picks}); opening it.");
                    _unlocked = true;
                    Enter(Phase.Open, now);
                    return;
                }
                _env.Log($"[RynthAi] Nearby chests: the lockpick failed on chest 0x{(uint)ChestId:X8} (try {_picks} of {_pickLimit}).");
                Enter(Phase.Pick, now);
                return;
            case ErrLockAlreadyUnlocked:
                _unlocked = true;
                Enter(Phase.Open, now);
                return;
            case ErrNotTrainedInLockpicking:
                Leave(now, "the server says your Lockpick skill isn't trained", skip: true);
                return;
            case ErrCannotLockOrUnlock:
                Leave(now, "the server says its lock can't be picked", skip: true);
                return;
            case ErrCannotLockWhatIsOpen:
                Leave(now, "it is open (someone else is using it)", skip: true);
                return;
            case ErrTooBusy:
                _picks--;
                TooBusy(now, Phase.Pick);
                return;
            default:
                Leave(now, $"the server refused the lockpick (error 0x{err:X})", skip: true);
                return;
        }
    }

    // ── Open and loot ─────────────────────────────────────────────────────

    private void TickOpen(long now)
    {
        if (Held(now)) return;
        _seqAtSend = _env.UseDoneSeq();
        if (!_env.SendOpen(ChestId)) { Leave(now, "the client wouldn't send the open", skip: true); return; }
        _sentAt = now;
        _answered = false;
        Enter(Phase.OpenOut, now);
    }

    private void TickOpenOut(long now)
    {
        if (_env.OpenedAt(ChestId) >= _sentAt || _env.Looting(ChestId)) { OnOpened(now); return; }
        if (!_answered)
        {
            bool done = _env.HasUseDone ? _env.UseDoneSeq() != _seqAtSend : now - _sentAt >= 3000;
            if (done) { _answered = true; _answeredAt = now; }
            else if (now - _sentAt < AnswerMaxMs) return;
        }
        if (_answered && now - _answeredAt < OpenSettleMs) return;

        // Answered (or not at all) and nothing opened.
        if (!_triedUnlock && !_unlocked)
        {
            // The client didn't read it as locked, but it wouldn't open: take it as locked.
            _env.Log($"[RynthAi] Nearby chests: chest 0x{(uint)ChestId:X8} '{ChestName}' didn't open; taking it as locked (keys, then a lockpick).");
            _triedUnlock = true;
            _lockedAtStart = true;
            Enter(Phase.Key, now);
            return;
        }
        if (++_openFails >= MaxOpenFails) { Leave(now, $"it didn't open ({_openFails} tries)", skip: true); return; }
        _env.Log($"[RynthAi] Nearby chests: chest 0x{(uint)ChestId:X8} didn't open (try {_openFails} of {MaxOpenFails}); trying again.");
        Enter(Phase.Open, now);
    }

    private void OnOpened(long now)
    {
        var m = Mem(ChestId);
        m.OpenedAt = now;
        m.WasLocked = _lockedAtStart;
        _lootStartedAt = now;
        _lootSeen = _env.Looting(ChestId);
        _env.Log($"[RynthAi] Nearby chests: chest 0x{(uint)ChestId:X8} '{ChestName}' is open; the loot profile decides what is taken.");
        Enter(Phase.Looting, now);
    }

    private void TickLooting(long now)
    {
        if (_env.Looting(ChestId))
        {
            _lootSeen = true;
            if (now - _lootStartedAt >= LootMaxMs) Leave(now, $"looting it took more than {LootMaxMs / 60000} minutes", skip: false, done: true);
            return;
        }
        if (!_lootSeen && now - _lootStartedAt < LootStartMs) return;
        if (!_lootSeen) { Leave(now, "the looter never took it", skip: true); return; }
        Looted++;
        Leave(now, "looted", skip: false, done: true);
    }

    // ── Endings ───────────────────────────────────────────────────────────

    private void Timeout(long now, Phase back, string why)
    {
        if (++_timeouts >= MaxTimeouts) { Leave(now, $"{why} ({_timeouts} times)", skip: true); return; }
        _env.Log($"[RynthAi] Nearby chests: {why} on chest 0x{(uint)ChestId:X8}; trying again ({_timeouts} of {MaxTimeouts}).");
        Enter(back, now);
    }

    private void TooBusy(long now, Phase back)
    {
        if (++_busy >= MaxBusy) { Leave(now, "the server keeps answering 'too busy'", skip: true); return; }
        Enter(back, now);
    }

    /// <summary>
    /// Ends the chest. done: looted, left alone until it resets (the re-lock wait, or it reads
    /// locked again). skip: left alone SkipMs (10 minutes). Neither: nothing marked (it is picked
    /// again on a later scan). again: looted before and not reset yet - another re-lock wait.
    /// </summary>
    private void Leave(long now, string why, bool skip, bool done = false, bool again = false)
    {
        int id = ChestId;
        string name = ChestName;
        var m = Mem(id);
        m.Why = why;
        if (done || again)
        {
            m.Done = true;
            if (done) m.OpenedAt = m.OpenedAt == 0 ? now : m.OpenedAt;
            m.LeaveUntil = now + RelockWaitMs;
            LastResult = $"{name}: {why}";
            _env.Log(again
                ? $"[RynthAi] Nearby chests: chest 0x{(uint)id:X8} '{name}' {why}; left alone another {RelockWaitMs / 1000} s."
                : $"[RynthAi] Nearby chests: done with chest 0x{(uint)id:X8} '{name}' ({why}); left alone until it resets " +
                  $"({(m.WasLocked ? "it reads locked again, or " : "")}{RelockWaitMs / 1000} s, the chest re-lock wait).");
        }
        else if (skip)
        {
            m.Done = false;
            m.LeaveUntil = now + SkipMs;
            LastResult = $"{name}: skipped - {why}";
            _env.Log($"[RynthAi] Nearby chests: skipping chest 0x{(uint)id:X8} '{name}' - {why}; trying it again in {SkipMs / 60000} minutes.");
        }
        else
        {
            // Nothing marked; a chest that left the world is forgotten (its keys too).
            if (!_env.InWorld(id) || (!m.Done && m.KeysThatDontFit.Count == 0)) _memory.Remove(id);
            _env.Log($"[RynthAi] Nearby chests: left chest 0x{(uint)id:X8} '{name}' - {why}.");
        }
        Reset();
    }

    /// <summary>One line for /ra chests.</summary>
    public string Status
    {
        get
        {
            string tail = $"{Looted} looted";
            switch (State)
            {
                case Phase.Idle: return LastResult.Length > 0 ? $"idle - {tail}; last: {LastResult}." : $"idle - {tail}.";
                case Phase.Appraise or Phase.AppraiseOut: return $"looking at {ChestName}'s lock - {tail}.";
                case Phase.Key or Phase.KeyOut: return $"trying keys on {ChestName} - {tail}.";
                case Phase.Pick or Phase.PickOut: return $"picking {ChestName} (try {_picks} of {_pickLimit}) - {tail}.";
                case Phase.Open or Phase.OpenOut: return $"opening {ChestName} - {tail}.";
                default: return $"looting {ChestName} - {tail}.";
            }
        }
    }
}
