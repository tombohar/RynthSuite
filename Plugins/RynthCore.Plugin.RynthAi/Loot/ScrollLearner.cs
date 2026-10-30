// ============================================================================
//  RynthSuite - Plugins/RynthCore.Plugin.RynthAi/Loot/ScrollLearner.cs
//  VTank's "ReadUnknownScrolls" ("Loots and reads unknown scrolls (looting must
//  be enabled)", default on): loot scrolls of spells the character doesn't know
//  and can learn, then read them at a safe moment. Also reads such scrolls that
//  are already in the pack, and every item a loot profile's Read action looted.
//
//  "Can learn" is ACE's own rule (ACE.Server Scroll.ActOnUse +
//  Player.CanReadScroll), so nothing is looted that the server will refuse:
//    - the spell must not be known ("You already know that spell!");
//    - power < 50 (level I) or power >= 300 (VII, VIII): anyone may read it;
//    - otherwise the spell's school must be trained or specialized and the
//      buffed skill at least power - 50 ("You are not trained in ..." /
//      "You are not skilled enough in ... to learn this spell.").
//  On top of that RynthAi always wants the school trained (VTank's rule: an
//  untrained school can't cast the spell anyway).
//
//  Data: the scroll's spell is data id 28 (Spell) from its CreateObject header
//  (no appraisal needed); school and power from the portal.dat SpellTable
//  (ComponentDatabase); known spells from the spellbook snapshot; skills read
//  straight from the host (CharacterSkills' "assume capable" fallback would
//  loot scrolls we can't read).
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Loot;

internal enum ScrollVerdict
{
    Learnable,
    /// <summary>No spell id on it: not a spell scroll (or the client doesn't know yet).</summary>
    NoSpell,
    Known,
    /// <summary>The spellbook can't be read yet (cold snapshot, no engine call).</summary>
    KnownUnreadable,
    /// <summary>The spell isn't in the dat's SpellTable (or the dat couldn't be read).</summary>
    SchoolUnknown,
    SkillUnreadable,
    Untrained,
    TooDifficult,
    /// <summary>A read of this spell failed earlier this session.</summary>
    FailedBefore,
}

internal readonly record struct ScrollJudgement(
    ScrollVerdict Verdict, int SpellId, string SpellName, string SkillName, int Training, int Skill, int Power)
{
    public bool Learnable => Verdict == ScrollVerdict.Learnable;
    public int MinSkill => Power - ScrollLearner.AceSkillMargin;
    public string TrainingName => Training >= 3 ? "specialized" : Training == 2 ? "trained" : "untrained";

    /// <summary>Why, in a few plain words (log lines).</summary>
    public string Why => Verdict switch
    {
        ScrollVerdict.Learnable       => $"unknown, {SkillName} {TrainingName}",
        ScrollVerdict.NoSpell         => "no spell on it",
        ScrollVerdict.Known           => "already known",
        ScrollVerdict.KnownUnreadable => "spellbook not readable yet",
        ScrollVerdict.SchoolUnknown   => "the spell's school is unknown",
        ScrollVerdict.SkillUnreadable => $"{SkillName} not readable yet",
        ScrollVerdict.Untrained       => $"{SkillName} untrained",
        ScrollVerdict.TooDifficult    => $"{SkillName} {Skill}, needs {MinSkill}",
        ScrollVerdict.FailedBefore    => "a read failed earlier this session",
        _                             => Verdict.ToString(),
    };
}

internal sealed class ScrollLearner
{
    /// <summary>ACE Player_Skills.magicSkillCheckMargin: the skill may be this far below the spell's power.</summary>
    internal const int AceSkillMargin = 50;
    /// <summary>PropertyDataId.Spell: the inscribed spell (CreateObject header, SpellDID).</summary>
    internal const uint STypeDidSpell = 28;
    /// <summary>The loot rule label corpse looting shows for the built-in rule.</summary>
    internal const string RuleLabel = "Learn unknown spell";
    internal const string ReadAction = "Read";

    /// <summary>How long a looted scroll may take to show up in the pack.</summary>
    internal const long ArrivalGraceMs = 15_000;
    /// <summary>Read animation + 1 s read + return to ready is ~3-4 s on ACE; give up after this.</summary>
    internal const long ReadTimeoutMs = 8_000;
    /// <summary>
    /// Pause between two reads, from the read's end (its chat answer), when the read's UseDone is not
    /// seen. ACE sends "The scroll is destroyed." before the return-to-ready animation and the UseDone
    /// after it (Scroll.ActOnUse, Player.TryUseItem): about 0.7 s apart in a client log.
    /// </summary>
    internal const long ReadGapMs = 1_500;
    /// <summary>Pause between two reads, from the last read's UseDone (the server is free then).</summary>
    internal const long ReadGapAfterUseDoneMs = 250;
    /// <summary>How often the pack is looked through for unknown scrolls.</summary>
    internal const long PackSweepMs = 5_000;
    /// <summary>A scroll approved for looting holds its spell this long (one copy per spell).</summary>
    internal const long ApprovedTtlMs = 60_000;
    /// <summary>Reads that time out or are refused as busy before the scroll is given up.</summary>
    internal const int MaxReadAttempts = 2;
    /// <summary>A spell counts as learned from the spellbook only after this long (a stale snapshot).</summary>
    internal const long KnownConfirmMinMs = 1_500;
    /// <summary>A pending scroll whose facts can't be read yet is retried this long, then dropped.</summary>
    internal const long UnreadableGiveUpMs = 120_000;

    private const long LogWindowMs = 10_000;
    private const int MaxLogLinesPerWindow = 30;

    private enum ChatOutcome { None, Learned, AlreadyKnown, Untrained, TooDifficult, Busy }

    private sealed class Pending
    {
        public string Name = "";
        public int SpellId;
        public bool FromProfile;   // a loot profile's Read action (read even with the setting off)
        public long NotedAt;
        public bool SeenInPack;
        public int Attempts;
        public long NotBefore;
    }

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache? _cache;

    /// <summary>Clock (ms); tests replace it.</summary>
    public Func<long> NowMs = () => Environment.TickCount64;
    /// <summary>Is the spell in the character's spellbook? null = can't tell.</summary>
    public Func<int, bool?> KnownSpell = _ => null;
    /// <summary>The player's id (0 before login).</summary>
    public Func<uint> PlayerId = () => 0;
    /// <summary>A chat line for the player (success only).</summary>
    public Action<string>? Chat;

    private readonly HashSet<int> _failedSpells = new();
    private readonly Dictionary<int, (int ItemId, long At)> _approved = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly Dictionary<int, string> _lastLine = new();
    private long _lastSweepAt = long.MinValue / 2;
    private long _lastReadEndedAt = long.MinValue / 2;
    private long _logWindowStart = long.MinValue / 2;
    private int _logWindowLines;
    private Dictionary<string, List<int>>? _nameToIds;

    // The read in flight.
    private int _readItemId;
    private int _readSpellId;
    private string _readName = "";
    private string _readSpellName = "";
    private long _readStartedAt;
    private ChatOutcome _chat;
    private string _chatText = "";
    private bool _readItemDeleted;

    // The UseDone of the last read sent: the first UseDone after its send. 0 = not seen (yet).
    private bool _readUseDoneWatch;
    private int _readUseDoneSeqAtSend;
    private long _readUseDoneAt;

    public ScrollLearner(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache? cache)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
    }

    public bool IsReading => _readItemId != 0;
    public int PendingCount => _pending.Count;
    public bool IsPending(int itemId) => _pending.ContainsKey(itemId);
    public bool HasFailed(int spellId) => _failedSpells.Contains(spellId);

    // ── Facts ─────────────────────────────────────────────────────────────────

    /// <summary>Could this be a spell scroll? RynthAi files writables as Book; Decal calls them Scroll.</summary>
    internal static bool LooksLikeScroll(WorldObject? item)
    {
        if (item == null) return false;
        if (item.ObjectClass == AcObjectClass.Scroll || item.ObjectClass == AcObjectClass.Book) return true;
        return item.Name != null && item.Name.StartsWith("Scroll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decal's ObjectClass.Scroll (42). RynthAi files every writable as Book (33); Decal splits
    /// out scrolls, and VTank loot profiles say "ObjectClass == Scroll". A writable with an
    /// inscribed spell (data id 28), or named "Scroll ...", is a scroll.
    /// </summary>
    internal static bool IsDecalScroll(WorldObject? item)
    {
        if (item == null) return false;
        if (item.ObjectClass == AcObjectClass.Scroll) return true;
        if (item.ObjectClass != AcObjectClass.Book) return false;
        if ((item.Cache?.GetDataIdProperty(item.Id, STypeDidSpell) ?? 0) != 0) return true;
        return item.Name != null && item.Name.StartsWith("Scroll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ObjectClass test for loot rules: Decal's Scroll class matches RynthAi's scroll Books.</summary>
    /// Spell scrolls are classed Scroll at the source now (2026-10-04); a rule written for Book, from
    /// when they were filed as Book, still matches them.
    internal static bool ClassMatches(WorldObject item, int objectClass)
        => (int)item.ObjectClass == objectClass
           || (objectClass == (int)AcObjectClass.Scroll && IsDecalScroll(item))
           || (objectClass == (int)AcObjectClass.Book && item.ObjectClass == AcObjectClass.Scroll);

    /// <summary>
    /// The scroll's spell id: data id 28 from its CreateObject header (no appraisal needed),
    /// else the one spell an appraisal lists, else a name ("Scroll of X" / "X") that only one
    /// spell has. 0 when none.
    /// </summary>
    internal int ReadSpellId(WorldObject item)
    {
        uint id = unchecked((uint)item.Id);
        try
        {
            if (_host.TryGetObjectDataIdProperty(id, STypeDidSpell, out uint did) && did != 0 && did < 0x10000)
                return (int)did;
        }
        catch { }

        try
        {
            if (_host.HasGetObjectSpellIds)
            {
                uint[] buf = new uint[8];
                if (_host.GetObjectSpellIds(id, buf, buf.Length) == 1 && buf[0] != 0)
                    return (int)buf[0];
            }
        }
        catch { }

        string name = item.Name ?? "";
        if (name.StartsWith("Scroll of ", StringComparison.OrdinalIgnoreCase))
            name = name.Substring("Scroll of ".Length);
        if (name.Length == 0) return 0;
        _nameToIds ??= SpellDatabase.BuildNameToIdsMap();
        return _nameToIds.TryGetValue(name.Trim(), out var ids) && ids.Count == 1 ? ids[0] : 0;
    }

    internal static (uint SType, string Name) SchoolSkill(int school) => school switch
    {
        1 => (34u, "War Magic"),
        2 => (33u, "Life Magic"),
        3 => (32u, "Item Enchantment"),
        4 => (31u, "Creature Enchantment"),
        5 => (43u, "Void Magic"),
        _ => (0u, "an unknown school"),
    };

    /// <summary>ACE Player.CanReadScroll, plus RynthAi's "school trained" (see the file note).</summary>
    internal static bool AceCanRead(int training, int buffedSkill, int power)
    {
        if (training < 2) return false;                 // RynthAi: the school must be trained
        if (power < 50 || power >= 300) return true;    // ACE: level I and VII/VIII, anyone
        return buffedSkill >= power - AceSkillMargin;   // ACE: trained and Current >= power - 50
    }

    internal ScrollJudgement Judge(WorldObject item) => Judge(ReadSpellId(item));

    internal ScrollJudgement Judge(int spellId)
    {
        if (spellId <= 0) return new(ScrollVerdict.NoSpell, 0, "", "", 0, 0, 0);
        string spellName = SpellDatabase.GetSpellName(spellId);
        if (_failedSpells.Contains(spellId)) return new(ScrollVerdict.FailedBefore, spellId, spellName, "", 0, 0, 0);

        bool? known = null;
        try { known = KnownSpell(spellId); } catch { }
        if (known == true) return new(ScrollVerdict.Known, spellId, spellName, "", 0, 0, 0);
        if (known == null) return new(ScrollVerdict.KnownUnreadable, spellId, spellName, "", 0, 0, 0);

        if (!ComponentDatabase.TryGetSpellSchoolAndPower(spellId, out int school, out int power))
            return new(ScrollVerdict.SchoolUnknown, spellId, spellName, "", 0, 0, 0);
        var (stype, skillName) = SchoolSkill(school);
        if (stype == 0) return new(ScrollVerdict.SchoolUnknown, spellId, spellName, skillName, 0, 0, power);

        uint player = PlayerId();
        if (player == 0 || !_host.HasGetObjectSkill
            || !_host.TryGetObjectSkill(player, stype, out int buffed, out int training)
            || (training >= 2 && buffed <= 0))   // engine qualities artifact (see CharacterSkills)
            return new(ScrollVerdict.SkillUnreadable, spellId, spellName, skillName, 0, 0, power);

        if (training < 2) return new(ScrollVerdict.Untrained, spellId, spellName, skillName, training, buffed, power);
        if (!AceCanRead(training, buffed, power))
            return new(ScrollVerdict.TooDifficult, spellId, spellName, skillName, training, buffed, power);
        return new(ScrollVerdict.Learnable, spellId, spellName, skillName, training, buffed, power);
    }

    // ── Looting ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The built-in loot rule (VTank treats it as one, ahead of the loot profile): with the
    /// setting on, a scroll on a corpse whose spell the character doesn't know and can learn.
    /// One copy per spell.
    /// </summary>
    public bool ShouldLootFromCorpse(WorldObject item, out string ruleLabel) =>
        ShouldLootFromCorpse(item, out ruleLabel, out _, preview: false);

    // preview: the same answer with nothing recorded or logged (the loot popup asking why).
    // why: the spell and why it is taken, in a few words.
    public bool ShouldLootFromCorpse(WorldObject item, out string ruleLabel, out string why, bool preview)
    {
        ruleLabel = string.Empty;
        why = string.Empty;
        if (!_settings.ReadUnknownScrolls || !LooksLikeScroll(item)) return false;

        ScrollJudgement j = Judge(item);
        if (j.Verdict == ScrollVerdict.NoSpell) return false;   // a book, a note: not ours to judge
        if (!j.Learnable)
        {
            if (!preview) LogItem(item.Id, $"[Learn] leaving '{item.Name}' ({j.SpellName}: {j.Why})");
            return false;
        }

        long now = NowMs();
        if (OtherCopyTaken(j.SpellId, item.Id, now))
        {
            if (!preview) LogItem(item.Id, $"[Learn] leaving '{item.Name}' ({j.SpellName}: another copy is already on its way)");
            return false;
        }
        if (!preview)
        {
            _approved[j.SpellId] = (item.Id, now);
            LogItem(item.Id, $"[Learn] looting '{item.Name}' ({j.SpellName}: {j.Why})");
        }
        ruleLabel = RuleLabel;
        why = $"{j.SpellName}: {j.Why}";
        return true;
    }

    private bool OtherCopyTaken(int spellId, int itemId, long now)
    {
        if (_approved.TryGetValue(spellId, out var a) && a.ItemId != itemId && now - a.At < ApprovedTtlMs
            && (_cache == null || _cache[a.ItemId] != null))
            return true;
        foreach (var kv in _pending)
            if (kv.Key != itemId && kv.Value.SpellId == spellId) return true;
        return false;
    }

    /// <summary>
    /// A Read item's pickup was sent (the built-in rule, or a loot profile's Read action).
    /// It is read once it is in the pack.
    /// </summary>
    public void NoteLooted(int itemId, string? name, bool fromProfile)
    {
        if (itemId == 0) return;
        WorldObject? wo = _cache?[itemId];
        if (!_pending.TryGetValue(itemId, out Pending? p))
        {
            p = new Pending { NotedAt = NowMs() };
            _pending[itemId] = p;
        }
        p.Name = !string.IsNullOrWhiteSpace(name) ? name! : wo?.Name ?? p.Name;
        p.FromProfile |= fromProfile;
        if (p.SpellId == 0 && wo != null) p.SpellId = ReadSpellId(wo);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One tick. <paramref name="safe"/>: macro on, no monster within MonsterRange, not
    /// buffing, looting a corpse, salvaging or vendoring. Returns true while a read is in
    /// flight and it is still safe: the caller then holds the bot still.
    /// </summary>
    public bool Tick(bool safe, int busyCount)
    {
        long now = NowMs();
        PollReadUseDone(now);
        if (IsReading)
        {
            if (ResolveRead(now)) return false;
            return safe;
        }

        if (_seriesOpen)
        {
            if (!_settings.IsMacroRunning) CloseSeries(CurrentMode() == CombatMode.NonCombat ? "the macro stopped" : "");
            else TickRestore(now, safe, busyCount);
        }

        if (!_settings.IsMacroRunning) return false;
        if (!_settings.ReadUnknownScrolls && _pending.Count == 0) return false;

        IReadOnlyList<WorldObject>? inv = null;
        if (now - _lastSweepAt >= PackSweepMs)
        {
            _lastSweepAt = now;
            inv = Inventory();
            SweepPack(inv, now);
        }

        if (!safe || busyCount > 0 || _pending.Count == 0) return false;
        if (!ReadGapOver(now)) return false;
        if (inv == null)
        {
            if (now - _lastStartCheckAt < StartCheckMs) return false;   // one pack walk a second at most
            inv = Inventory();
        }
        _lastStartCheckAt = now;
        return TryStartRead(inv, now);
    }

    // ── Stance restore ────────────────────────────────────────────────────────
    // Using a scroll puts the character in Peace. The mode it was in before the FIRST read of a
    // run of reads is remembered, and requested once after the last one, when it was a combat
    // mode and Peace Mode When Idle is off, so the next fight starts in stance. Nothing is sent
    // when something else owns the moment (not safe: a fight or buff), when anything else already
    // changed the mode, or while the client is busy.

    /// <summary>Quiet time after a read before the stance goes back: a next scroll starts within ReadGapMs + StartCheckMs.</summary>
    internal const long RestoreSettleMs = 3_500;
    internal const long RestoreRetryMs = 1_000;
    internal const int RestoreMaxRequests = 3;
    /// <summary>
    /// How long after the settle time a restore may still go out. The learner is not ticked during a
    /// death hold, a buff hold, vendoring or crafting, and the client can stay busy: a restore that
    /// late is a stance from before the interruption (a previous life's Melee after a respawn), so
    /// the series is dropped instead.
    /// </summary>
    internal const long RestoreGiveUpMs = 15_000;

    private bool _seriesOpen;
    private int _restoreMode;        // 0 = nothing to restore
    private int _restoreWeapon;      // the main-hand weapon at the first read; ACE reverts a mode its weapon doesn't fit
    private long _restoreNextAt;
    private int _restoreRequests;

    private int CurrentMode()
    {
        try { return _host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : 0; }
        catch { return 0; }
    }

    private static string ModeName(int mode) => mode switch
    {
        CombatMode.NonCombat => "Peace",
        CombatMode.Melee => "Melee",
        CombatMode.Missile => "Missile",
        CombatMode.Magic => "Magic",
        _ => mode.ToString(),
    };

    /// <summary>The melee/missile weapon or caster in the main hand (0 = none readable); CombatManager.InHandWeaponId's rules.</summary>
    private int WieldedWeaponId()
    {
        try
        {
            foreach (var wo in _cache?.GetDirectInventory(forceRefresh: true) ?? Array.Empty<WorldObject>())
            {
                if (WorldObjectCache.IsAmmo(wo)) continue;
                if ((wo.Values(LongValueKey.CurrentWieldedLocation, 0) & OffhandRules.ShieldSlot) != 0) continue;   // the off hand
                bool weapon = wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon || WeaponList.IsWand(wo);
                if (weapon && WorldObjectCache.IsWieldedByPlayer(_host, wo)) return wo.Id;
            }
        }
        catch { }
        return 0;
    }

    private void OpenSeries()
    {
        if (_seriesOpen) return;   // a later scroll of the same run: the first read's mode stands
        _seriesOpen = true;
        _restoreRequests = 0;
        _restoreNextAt = 0;
        int m = CurrentMode();
        _restoreMode = !_settings.PeaceModeWhenIdle && (m == CombatMode.Melee || m == CombatMode.Missile || m == CombatMode.Magic) ? m : 0;
        _restoreWeapon = _restoreMode != 0 ? WieldedWeaponId() : 0;
    }

    private void CloseSeries(string why)
    {
        if (_restoreMode != 0 && why.Length > 0) LogLine($"[Learn] stance not restored ({why})");
        _seriesOpen = false;
        _restoreMode = 0;
    }

    private void TickRestore(long now, bool safe, int busyCount)
    {
        if (_settings.PeaceModeWhenIdle) { CloseSeries(""); return; }                     // switched on since: Peace is wanted
        long age = now - _lastReadEndedAt;
        if (_restoreMode == 0) { if (age >= RestoreSettleMs) CloseSeries(""); return; }
        // The next scroll may be about to start: wait the settle time, unless the last read's UseDone
        // is in and no scroll is pending (nothing left to read, so the stance can go back now).
        bool lastReadDone = LastReadUseDoneSeen && _pending.Count == 0;
        if (age < RestoreSettleMs && !lastReadDone) return;
        if (age > RestoreSettleMs + RestoreGiveUpMs) { CloseSeries("too late"); return; }
        int cur = CurrentMode();
        if (cur == _restoreMode) { CloseSeries(""); return; }                              // back in stance
        if (cur != CombatMode.NonCombat) { CloseSeries("something else set the mode"); return; }
        if (!safe) { CloseSeries("a fight or another task has the tick"); return; }
        if (busyCount > 0 || now < _restoreNextAt) return;
        if (_restoreRequests >= RestoreMaxRequests) { CloseSeries("the client did not change mode"); return; }
        if (WieldedWeaponId() != _restoreWeapon) { CloseSeries("the weapon changed"); return; }
        // Only a request that went out counts: a pacing hold (another action unanswered) is retried next tick.
        if (_host.ChangeCombatModeFor(_restoreMode, "Learn", "back to the stance from before the scroll read"))
        {
            _restoreRequests++;
            _restoreNextAt = now + RestoreRetryMs;
            LogLine($"[Learn] restoring {ModeName(_restoreMode)} mode after reading");
        }
    }
    /// <summary>How often a waiting scroll is looked for in the pack.</summary>
    internal const long StartCheckMs = 1_000;
    private long _lastStartCheckAt = long.MinValue / 2;

    /// <summary>The pack, freshly walked (the cache's unforced list can be seconds old).</summary>
    private IReadOnlyList<WorldObject> Inventory()
    {
        IReadOnlyList<WorldObject> all;
        try { all = _cache?.GetDirectInventory(forceRefresh: true) ?? Array.Empty<WorldObject>(); }
        catch { return Array.Empty<WorldObject>(); }

        // Without the engine's container walk the cache falls back to every non-landscape
        // object it knows, corpse contents included. Leave out what is known to sit elsewhere.
        int player = unchecked((int)PlayerId());
        if (player == 0 || _host.HasGetContainerContents) return all;   // the engine's walk is the player's packs only
        var mine = new List<WorldObject>(all.Count);
        foreach (WorldObject wo in all)
        {
            int c = wo.Container;
            if (c == 0 || c == player || _cache?[c]?.Container == player) mine.Add(wo);
        }
        return mine;
    }

    /// <summary>Unknown, learnable scrolls already in the pack join the queue (setting on).</summary>
    private void SweepPack(IReadOnlyList<WorldObject> inv, long now)
    {
        if (!_settings.ReadUnknownScrolls) return;
        foreach (WorldObject wo in inv)
        {
            if (!LooksLikeScroll(wo) || _pending.ContainsKey(wo.Id) || wo.Id == _readItemId) continue;
            ScrollJudgement j = Judge(wo);
            if (j.Verdict == ScrollVerdict.NoSpell) continue;
            if (!j.Learnable)
            {
                LogItem(wo.Id, $"[Learn] skipped '{wo.Name}' in the pack ({j.SpellName}: {j.Why})");
                continue;
            }
            if (OtherCopyTaken(j.SpellId, wo.Id, now))
                continue;   // a second copy of a spell already queued: the first one teaches it
            _pending[wo.Id] = new Pending { Name = wo.Name, SpellId = j.SpellId, NotedAt = now, SeenInPack = true };
            LogItem(wo.Id, $"[Learn] found '{wo.Name}' in the pack ({j.SpellName}: {j.Why}) - will read it");
        }
    }

    private bool TryStartRead(IReadOnlyList<WorldObject> inv, long now)
    {
        var inPack = new Dictionary<int, WorldObject>(inv.Count);
        foreach (WorldObject wo in inv) inPack[wo.Id] = wo;

        List<int>? drop = null;
        int pickId = 0;
        WorldObject? pick = null;
        foreach (var kv in _pending)
        {
            Pending p = kv.Value;
            if (!p.FromProfile && !_settings.ReadUnknownScrolls) { (drop ??= new()).Add(kv.Key); continue; }
            if (!inPack.TryGetValue(kv.Key, out WorldObject? wo))
            {
                if (!p.SeenInPack && now - p.NotedAt < ArrivalGraceMs) continue;   // still on its way
                LogItem(kv.Key, $"[Learn] '{p.Name}' never reached the pack (or left it) - dropped");
                (drop ??= new()).Add(kv.Key);
                continue;
            }
            p.SeenInPack = true;
            if (now < p.NotBefore) continue;
            pickId = kv.Key;
            pick = wo;
            break;
        }
        if (drop != null) foreach (int id in drop) _pending.Remove(id);
        if (pick == null) return false;

        Pending pp = _pending[pickId];
        int spellId = ReadSpellId(pick);
        if (spellId == 0 && !LooksLikeScroll(pick))
        {
            // VTank: "Lootplugin attempted to classify item ... for reading, but that item does
            // not appear to be a scroll. Ignoring it."
            LogItem(pickId, $"[Learn] '{pick.Name}' was looted to read, but it isn't a scroll - kept, not read");
            _pending.Remove(pickId);
            return false;
        }

        ScrollJudgement j = Judge(spellId);
        if (!j.Learnable)
        {
            bool waitable = j.Verdict is ScrollVerdict.KnownUnreadable or ScrollVerdict.SkillUnreadable or ScrollVerdict.SchoolUnknown;
            if (waitable && now - pp.NotedAt < UnreadableGiveUpMs)
            {
                pp.NotBefore = now + PackSweepMs;
                LogItem(pickId, $"[Learn] waiting to read '{pick.Name}' ({j.SpellName}: {j.Why})");
                return false;
            }
            LogItem(pickId, $"[Learn] skipped '{pick.Name}' ({(j.SpellName.Length > 0 ? j.SpellName + ": " : "")}{j.Why}) - kept in the pack");
            _pending.Remove(pickId);
            return false;
        }

        OpenSeries();
        pp.Attempts++;
        _readItemId = pickId;
        _readSpellId = j.SpellId;
        _readName = pick.Name;
        _readSpellName = j.SpellName;
        _readStartedAt = now;
        _chat = ChatOutcome.None;
        _chatText = "";
        _readItemDeleted = false;

        int seqBefore = 0;
        bool haveSeq = false;
        try { if (_host.HasUseDoneSeq) { seqBefore = _host.GetUseDoneSeq(); haveSeq = true; } } catch { }
        bool sent = false;
        try { sent = _host.UseFor(unchecked((uint)pickId), "Learn", $"read the scroll to learn {j.SpellName} ({j.Why})"); }
        catch { }
        if (!sent)
        {
            EndRead(now);
            if (pp.Attempts >= MaxReadAttempts) Fail(pickId, pp, "the use was refused");
            else pp.NotBefore = now + 3_000;
            return false;
        }
        _readUseDoneWatch = haveSeq;
        _readUseDoneSeqAtSend = seqBefore;
        _readUseDoneAt = 0;
        LogLine($"[Learn] reading '{pick.Name}' ({j.SpellName}: {j.Why})");
        return true;
    }

    /// <summary>Notes the first UseDone after the last read's send (the server's end of that read).</summary>
    private void PollReadUseDone(long now)
    {
        if (!_readUseDoneWatch) return;
        try
        {
            if (unchecked(_host.GetUseDoneSeq() - _readUseDoneSeqAtSend) > 0)
            {
                _readUseDoneWatch = false;
                _readUseDoneAt = now;
            }
        }
        catch { _readUseDoneWatch = false; }
    }

    /// <summary>The last read's UseDone was seen (after its chat answer, or at any time for this read).</summary>
    private bool LastReadUseDoneSeen => _readUseDoneAt != 0;

    /// <summary>
    /// May the next read start? ReadGapAfterUseDoneMs after the last read's UseDone, or ReadGapMs
    /// after its end when no UseDone is seen (an engine without UseDone, or a lost one).
    /// </summary>
    private bool ReadGapOver(long now) =>
        (LastReadUseDoneSeen && now - _readUseDoneAt >= ReadGapAfterUseDoneMs)
        || now - _lastReadEndedAt >= ReadGapMs;

    /// <summary>True when the read in flight is over (learned, refused, or timed out).</summary>
    private bool ResolveRead(long now)
    {
        _pending.TryGetValue(_readItemId, out Pending? p);
        long age = now - _readStartedAt;

        bool knownNow = false;
        if (age >= KnownConfirmMinMs)
        {
            try { knownNow = KnownSpell(_readSpellId) == true; } catch { }
        }

        if (_chat == ChatOutcome.Learned || _readItemDeleted || knownNow)
        {
            string how = _chat == ChatOutcome.Learned ? "the scroll is destroyed" : _readItemDeleted ? "the scroll is gone" : "it is in the spellbook";
            LogLine($"[Learn] read '{_readName}' - learned {_readSpellName} ({how})");
            try { Chat?.Invoke($"[RynthAi] Learned {_readSpellName} from a scroll."); } catch { }
            _pending.Remove(_readItemId);
            EndRead(now);
            return true;
        }

        if (_chat is ChatOutcome.AlreadyKnown or ChatOutcome.Untrained or ChatOutcome.TooDifficult)
        {
            if (p != null) Fail(_readItemId, p, $"the server said: {_chatText}");
            else _failedSpells.Add(_readSpellId);
            EndRead(now);
            return true;
        }

        if (_chat == ChatOutcome.Busy || age >= ReadTimeoutMs)
        {
            string why = _chat == ChatOutcome.Busy ? "too busy" : $"no answer in {ReadTimeoutMs / 1000} s";
            if (p != null)
            {
                if (p.Attempts >= MaxReadAttempts) Fail(_readItemId, p, why);
                else
                {
                    p.NotBefore = now + 3_000;
                    LogLine($"[Learn] read of '{_readName}' not confirmed ({why}) - will try again");
                }
            }
            EndRead(now);
            return true;
        }
        return false;
    }

    private void Fail(int itemId, Pending p, string why)
    {
        int spellId = p.SpellId != 0 ? p.SpellId : _readSpellId;
        if (spellId != 0) _failedSpells.Add(spellId);
        _pending.Remove(itemId);
        LogLine($"[Learn] read '{p.Name}' failed ({why}) - not looted or read again this session");
    }

    private void EndRead(long now)
    {
        _readItemId = 0;
        _readSpellId = 0;
        _readName = "";
        _readSpellName = "";
        _chat = ChatOutcome.None;
        _chatText = "";
        _readItemDeleted = false;
        _lastReadEndedAt = now;
    }

    /// <summary>ACE's answers to a scroll read (Scroll.ActOnUse, Player.LearnSpellWithNetworking).</summary>
    public void OnChat(string? text)
    {
        if (!IsReading || string.IsNullOrEmpty(text)) return;
        string t = text.Trim();
        ChatOutcome o =
            t == "The scroll is destroyed." ? ChatOutcome.Learned
            : t == "You already know that spell!" ? ChatOutcome.AlreadyKnown
            : t.StartsWith("You are not trained in ", StringComparison.Ordinal) && t.EndsWith("!", StringComparison.Ordinal) ? ChatOutcome.Untrained
            : t.StartsWith("You are not skilled enough in ", StringComparison.Ordinal) && t.EndsWith(" to learn this spell.", StringComparison.Ordinal) ? ChatOutcome.TooDifficult
            : t.StartsWith("You're too busy", StringComparison.Ordinal) ? ChatOutcome.Busy
            : ChatOutcome.None;
        if (o == ChatOutcome.None) return;
        _chat = o;
        _chatText = t;
    }

    /// <summary>The scroll is consumed on success (TryConsumeFromInventoryWithNetworking).</summary>
    public void OnObjectDeleted(uint objectId)
    {
        int id = unchecked((int)objectId);
        if (IsReading && id == _readItemId) _readItemDeleted = true;
        else if (!IsReading) _pending.Remove(id);
        _lastLine.Remove(id);
    }

    /// <summary>Logout: the session memory goes with the character.</summary>
    public void Reset()
    {
        _seriesOpen = false;
        _restoreMode = 0;
        _failedSpells.Clear();
        _approved.Clear();
        _pending.Clear();
        _lastLine.Clear();
        EndRead(NowMs());
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    /// <summary>One line per scroll decision: an item's line is written again only when it changes.</summary>
    private void LogItem(int itemId, string line)
    {
        if (_lastLine.TryGetValue(itemId, out string? last) && last == line) return;
        if (_lastLine.Count > 2000) _lastLine.Clear();
        _lastLine[itemId] = line;
        LogLine(line);
    }

    private void LogLine(string line)
    {
        long now = NowMs();
        if (now - _logWindowStart >= LogWindowMs) { _logWindowStart = now; _logWindowLines = 0; }
        if (++_logWindowLines > MaxLogLinesPerWindow) return;
        try { _host.Log(line); } catch { }
    }
}
