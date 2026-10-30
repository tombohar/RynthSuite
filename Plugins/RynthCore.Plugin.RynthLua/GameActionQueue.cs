using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// ActionError names (UtilityBelt's enum, same names). Scripts see them as strings:
/// <c>action.Error == "TooBusy"</c> or <c>action.Error == ActionError.TooBusy</c>.
/// </summary>
internal static class ActionErrors
{
    public const string None = "None";
    public const string TimedOut = "TimedOut";
    public const string ServerError = "ServerError";
    public const string NotLoggedIn = "NotLoggedIn";
    public const string CantUseOnItself = "CantUseOnItself";
    public const string TargetItemNotInInventoryOrLandscape = "TargetItemNotInInventoryOrLandscape";
    public const string SourceItemNotInInventory = "SourceItemNotInInventory";
    public const string InvalidTargetItem = "InvalidTargetItem";
    public const string InvalidSourceObject = "InvalidSourceObject";
    public const string ItemAlreadyWielded = "ItemAlreadyWielded";
    public const string TooEncumbered = "TooEncumbered";
    public const string MustBeInPeaceMode = "MustBeInPeaceMode";
    public const string YouCantPickThatUp = "YouCantPickThatUp";
    public const string InvalidInventoryLocation = "InvalidInventoryLocation";
    public const string YouDontKnowThatSpell = "YouDontKnowThatSpell";
    public const string MissingComponents = "MissingComponents";
    public const string UnknownSpellId = "UnknownSpellId";
    public const string SpellFizzled = "SpellFizzled";
    public const string InvalidTargetObject = "InvalidTargetObject";
    public const string Exception = "Exception";
    public const string SourceObjectTooFar = "SourceObjectTooFar";
    public const string Cancelled = "Cancelled";
    public const string NPCDoesntKnowWhatToDoWithThat = "NPCDoesntKnowWhatToDoWithThat";
    public const string TooBusy = "TooBusy";
    public const string CantSplitThisObject = "CantSplitThisObject";
    public const string ObjectSplitAmountTooBig = "ObjectSplitAmountTooBig";
    public const string InvalidCombatMode = "InvalidCombatMode";
    public const string OnCooldown = "OnCooldown";
    public const string PreconditionFailed = "PreconditionFailed";

    /// <summary>UtilityBelt's full list (numbering order), exposed as the ActionError global.</summary>
    public static readonly string[] All =
    {
        "None", "TimedOut", "ServerError", "NotLoggedIn", "NotEnoughUnassignedExperience", "AlreadyMaxed",
        "TooMuchSpendExperience", "NotYourCharacter", "NotTrained", "YouDoNotPassCraftingRequirements",
        "ItemBeingTraded", "YouHaveBeenInPKBattleTooRecently", "CantUseOnItself", "TargetItemNotInInventoryOrLandscape",
        "SourceItemNotInInventory", "SourceItemNotInInventoryOrLandscape", "InvalidTargetItem", "InvalidSourceObject",
        "ItemAlreadyWielded", "TooEncumbered", "MustBeInPeaceMode", "YouCantPickThatUp", "InvalidInventoryLocation",
        "YouDontKnowThatSpell", "MissingComponents", "UnknownSpellId", "SpellFizzled", "AlreadyInFellow",
        "MustBeInFellow", "NotTheLeader", "NotInFellow", "MemberDoesntExist", "FellowshipIsFull", "FellowshipClosed",
        "AlreadyRecruited", "FellowshipRecruitBusy", "FellowshipLocked", "InvalidTargetObject", "UnknownCommand",
        "CharacterDoesntExist", "CharacterPendingDelete", "NotAtCharacterSelect", "PreconditionFailed", "NoUst",
        "UnsalvageableItem", "NoTradePartner", "Exception", "AlreadyTrained", "CantAdvanceSkill",
        "NotEnoughAvailableCredits", "SkillDoesntExist", "NotInAnAllegiance", "AlreadyInAnAllegiance",
        "SourceObjectTooFar", "NoActions", "Cancelled", "NPCDoesntKnowWhatToDoWithThat", "TooBusy", "VendorNotOpen",
        "VendorDoesntHaveThisItem", "VendorDoesntHaveEnoughOfThisItem", "CantSplitThisObject",
        "ObjectSplitAmountTooBig", "InvalidSpellLevel", "InvalidCombatMode", "OnCooldown",
    };

    /// <summary>Errors that mean "nothing happened, try again": retried up to MaxRetryCount.</summary>
    public static bool IsTemporary(string error) => error is TooBusy or SpellFizzled;
}

/// <summary>
/// AC's WeenieError codes (the server's UseDone, WeenieError, WeenieErrorWithString and
/// InventoryServerSaveFailed carry them; engine API v70+) mapped to UtilityBelt's ActionError
/// names. Numbers and names from ACE's WeenieError / WeenieErrorWithString enums. Codes not
/// listed here are left alone as refusals (many are notices: "You have entered the channel",
/// PK status, ...); a nonzero UseDone code that isn't listed is a ServerError.
/// </summary>
internal static class WeenieErrors
{
    public enum Kind : byte { General, Spell, Combat }

    private readonly record struct Entry(string Name, string Error, Kind Kind);

    private static readonly Dictionary<uint, Entry> Map = new()
    {
        // General
        [0x001D] = new("YoureTooBusy", ActionErrors.TooBusy, Kind.General),
        [0x001E] = new("_IsTooBusyToAcceptGifts", ActionErrors.TooBusy, Kind.General),
        [0x0020] = new("IllegalInventoryTransaction", ActionErrors.InvalidInventoryLocation, Kind.General),
        [0x002A] = new("YouAreTooEncumbered", ActionErrors.TooEncumbered, Kind.General),
        [0x002B] = new("_CannotCarryAnymore", ActionErrors.InvalidInventoryLocation, Kind.General),
        [0x0036] = new("ActionCancelled", ActionErrors.Cancelled, Kind.General),
        [0x0037] = new("ObjectGone", ActionErrors.InvalidSourceObject, Kind.General),
        [0x0038] = new("NoObject", ActionErrors.InvalidSourceObject, Kind.General),
        [0x0039] = new("CantGetThere", ActionErrors.SourceObjectTooFar, Kind.General),
        [0x003A] = new("Dead", ActionErrors.ServerError, Kind.General),
        [0x003E] = new("YouAreTooTiredToDoThat", ActionErrors.ServerError, Kind.General),
        [0x0045] = new("TooManyActions", ActionErrors.TooBusy, Kind.General),
        [0x03EE] = new("TheContainerIsClosed", ActionErrors.InvalidInventoryLocation, Kind.General),
        [0x03EF] = new("_IsNotAcceptingGiftsRightNow", ActionErrors.ServerError, Kind.General),
        [0x03F0] = new("InvalidInventoryLocation", ActionErrors.InvalidInventoryLocation, Kind.General),
        [0x03F1] = new("ChangeCombatModeFailure", ActionErrors.InvalidCombatMode, Kind.General),
        [0x03F2] = new("FullInventoryLocation", ActionErrors.InvalidInventoryLocation, Kind.General),
        [0x03F3] = new("ConflictingInventoryLocation", ActionErrors.InvalidInventoryLocation, Kind.General),
        [0x03F5] = new("BeWieldedFailure", ActionErrors.InvalidInventoryLocation, Kind.General),
        [0x03F6] = new("BeDroppedFailure", ActionErrors.ServerError, Kind.General),
        [0x0426] = new("AttunedItem", ActionErrors.ServerError, Kind.General),
        [0x0427] = new("YouCannotMergeDifferentStacks", ActionErrors.InvalidTargetItem, Kind.General),
        [0x0428] = new("YouCannotMergeEnchantedItems", ActionErrors.InvalidTargetItem, Kind.General),
        [0x0437] = new("YouDoNotPassCraftingRequirements", "YouDoNotPassCraftingRequirements", Kind.General),
        [0x043A] = new("YouMustBeInPeaceModeToTrade", ActionErrors.MustBeInPeaceMode, Kind.General),
        [0x043B] = new("YouAreNotTrainedInThatTradeSkill", "NotTrained", Kind.General),
        [0x043E] = new("YouHaveSolvedThisQuestTooRecently", ActionErrors.OnCooldown, Kind.General),
        [0x0453] = new("TradeItemBeingTraded", "ItemBeingTraded", Kind.General),
        [0x046A] = new("_DoesntKnowWhatToDoWithThat", ActionErrors.NPCDoesntKnowWhatToDoWithThat, Kind.General),
        [0x0498] = new("YouHaveMovedTooFar", ActionErrors.SourceObjectTooFar, Kind.General),
        [0x04A7] = new("YouHaveBeenTeleportedTooRecently", ActionErrors.OnCooldown, Kind.General),
        [0x04B2] = new("KeyDoesntFitThisLock", ActionErrors.InvalidTargetItem, Kind.General),
        [0x04B3] = new("LockUsedTooRecently", ActionErrors.OnCooldown, Kind.General),
        [0x04B4] = new("YouArentTrainedInLockpicking", "NotTrained", Kind.General),
        [0x04BE] = new("YouDoNotOwnThatItem", ActionErrors.SourceItemNotInInventory, Kind.General),
        [0x04BF] = new("The_WasNotSuitableForSalvaging", "UnsalvageableItem", Kind.General),
        [0x04C2] = new("ItemsAttemptingToSalvageIsInvalid", "UnsalvageableItem", Kind.General),
        [0x04C3] = new("YouCannotSalvageItemsInTrading", "ItemBeingTraded", Kind.General),
        [0x04CF] = new("_CannotAcceptStackedItems", ActionErrors.ServerError, Kind.General),
        [0x04FC] = new("YouArentTrainedInHealing", "NotTrained", Kind.General),
        [0x04FE] = new("YouCantHealThat", ActionErrors.InvalidTargetObject, Kind.General),
        [0x0500] = new("YouArentReadyToHeal", ActionErrors.OnCooldown, Kind.General),
        [0x0506] = new("CantDoThatTradeInProgress", "ItemBeingTraded", Kind.General),
        [0x058D] = new("YouCannotUseThatItem", ActionErrors.ServerError, Kind.General),
        // Spell (casting and its aftermath)
        [0x03FA] = new("YouveAttemptedAnImpossibleSpellPath", ActionErrors.ServerError, Kind.Spell),
        [0x03FC] = new("MagicInvalidSpellType", ActionErrors.UnknownSpellId, Kind.Spell),
        [0x03FE] = new("YouDontKnowThatSpell", ActionErrors.YouDontKnowThatSpell, Kind.Spell),
        [0x03FF] = new("IncorrectTargetType", ActionErrors.InvalidTargetObject, Kind.Spell),
        [0x0400] = new("YouDontHaveAllTheComponents", ActionErrors.MissingComponents, Kind.Spell),
        [0x0401] = new("YouDontHaveEnoughManaToCast", ActionErrors.ServerError, Kind.Spell),
        [0x0402] = new("YourSpellFizzled", ActionErrors.SpellFizzled, Kind.Spell),
        [0x0403] = new("YourSpellTargetIsMissing", ActionErrors.InvalidTargetObject, Kind.Spell),
        [0x0404] = new("YourProjectileSpellMislaunched", ActionErrors.ServerError, Kind.Spell),
        [0x0406] = new("MagicTargetOutOfRange", ActionErrors.ServerError, Kind.Spell),
        [0x0407] = new("YourSpellCannotBeCastOutside", ActionErrors.ServerError, Kind.Spell),
        [0x0408] = new("YourSpellCannotBeCastInside", ActionErrors.ServerError, Kind.Spell),
        [0x0409] = new("MagicGeneralFailure", ActionErrors.ServerError, Kind.Spell),
        [0x040A] = new("YouAreUnpreparedToCastASpell", ActionErrors.InvalidCombatMode, Kind.Spell),
        [0x042C] = new("TargetNotAcquired", ActionErrors.InvalidTargetObject, Kind.Spell),
        [0x04C5] = new("YourAllegianceRankIsTooLowToUseMagic", ActionErrors.ServerError, Kind.Spell),
        [0x04C7] = new("YourArcaneLoreIsTooLowToUseMagic", ActionErrors.ServerError, Kind.Spell),
        [0x04C8] = new("ItemDoesntHaveEnoughMana", ActionErrors.ServerError, Kind.Spell),
        [0x04CC] = new("YouHaveBeenInPKBattleTooRecently", "YouHaveBeenInPKBattleTooRecently", Kind.Spell),
        [0x04EB] = new("YouCantDoThatWhileInTheAir", ActionErrors.ServerError, Kind.Spell),
        [0x004E] = new("YouFailToAffect_YouCannotAffectAnyone", ActionErrors.ServerError, Kind.Spell),
        [0x004F] = new("YouFailToAffect_TheyCannotBeHarmed", ActionErrors.ServerError, Kind.Spell),
        [0x0050] = new("YouFailToAffect_WithBeneficialSpells", ActionErrors.ServerError, Kind.Spell),
        [0x0051] = new("YouFailToAffect_YouAreNotPK", ActionErrors.ServerError, Kind.Spell),
        [0x0052] = new("YouFailToAffect_TheyAreNotPK", ActionErrors.ServerError, Kind.Spell),
        [0x0053] = new("YouFailToAffect_NotSamePKType", ActionErrors.ServerError, Kind.Spell),
        [0x0054] = new("YouFailToAffect_AcrossHouseBoundary", ActionErrors.ServerError, Kind.Spell),
        [0x04FA] = new("_IsAnInvalidTarget", ActionErrors.InvalidTargetObject, Kind.Spell),
        [0x0509] = new("_HasNoSpellTargets", ActionErrors.InvalidTargetObject, Kind.Spell),
        [0x050A] = new("YouHaveNoTargetsForSpellOf_", ActionErrors.InvalidTargetObject, Kind.Spell),
        // Combat (never an action's failure: RynthAi's or the player's fighting)
        [0x003D] = new("YouChargedTooFar", ActionErrors.ServerError, Kind.Combat),
        [0x03F7] = new("YouAreTooFatiguedToAttack", ActionErrors.ServerError, Kind.Combat),
        [0x03F8] = new("YouAreOutOfAmmunition", ActionErrors.ServerError, Kind.Combat),
        [0x03F9] = new("YourAttackMisfired", ActionErrors.ServerError, Kind.Combat),
        [0x042A] = new("CurrentlyAttacking", ActionErrors.ServerError, Kind.Combat),
        [0x042B] = new("MissileAttackNotOk", ActionErrors.ServerError, Kind.Combat),
        [0x042D] = new("ImpossibleShot", ActionErrors.ServerError, Kind.Combat),
        [0x042E] = new("BadWeaponSkill", ActionErrors.ServerError, Kind.Combat),
        [0x042F] = new("UnwieldFailure", ActionErrors.ServerError, Kind.Combat),
        [0x0430] = new("LaunchFailure", ActionErrors.ServerError, Kind.Combat),
        [0x0431] = new("ReloadFailure", ActionErrors.ServerError, Kind.Combat),
        [0x0550] = new("MissileOutOfRange", ActionErrors.ServerError, Kind.Combat),
    };

    /// <summary>Whether this code is a known refusal, and of which kind.</summary>
    public static bool TryKind(uint code, out Kind kind)
    {
        if (Map.TryGetValue(code, out Entry e)) { kind = e.Kind; return true; }
        kind = Kind.General;
        return false;
    }

    /// <summary>The ActionError name for a refusal code (ServerError when it isn't mapped).</summary>
    public static string ActionError(uint code) => Map.TryGetValue(code, out Entry e) ? e.Error : ActionErrors.ServerError;

    /// <summary>"0x0402 YourSpellFizzled" (just the number when it isn't mapped).</summary>
    public static string Describe(uint code) => Map.TryGetValue(code, out Entry e) ? $"0x{code:X4} {e.Name}" : $"0x{code:X4}";
}

/// <summary>UtilityBelt's ActionType priorities (higher runs first).</summary>
internal static class ActionPriority
{
    public const int Immediate = int.MaxValue;
    public const int Wield = 20000, RestoreHealth = 19000, RestoreStamina = 18000, RestoreMana = 17000,
        LoginLogoff = 16000, Vendor = 15500, Trade = 15000, Combat = 14000, CastSpell = 13000, Navigation = 12000,
        Fellow = 11000, Allegiance = 10000, Inventory = 9000, Misc = 8000;

    public static readonly (string Name, int Value)[] All =
    {
        ("Immediate", Immediate), ("Wield", Wield), ("RestoreHealth", RestoreHealth), ("RestoreStamina", RestoreStamina),
        ("RestoreMana", RestoreMana), ("LoginLogoff", LoginLogoff), ("Vendor", Vendor), ("Trade", Trade),
        ("Combat", Combat), ("CastSpell", CastSpell), ("Navigation", Navigation), ("Fellow", Fellow),
        ("Allegiance", Allegiance), ("Inventory", Inventory), ("Misc", Misc),
    };

    public static bool TryParse(string name, out int value)
    {
        foreach (var (n, v) in All)
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = 0;
        return false;
    }
}

/// <summary>Per-call options (UB's ActionOptions): <c>{ Priority, TimeoutMilliseconds, MaxRetryCount, SkipChecks, Name }</c>.</summary>
internal struct ActionOptions
{
    public int? Priority;
    public int? TimeoutMs;
    public int? MaxRetryCount;
    public bool SkipChecks;
    public string? Name;
}

internal enum ActionPhase { Queued, Waiting, Sent, RetryWait, Finished }

/// <summary>What a chat line tells the running action.</summary>
internal enum ChatHint { None, Fail, CastOk, GiveOk }

/// <summary>
/// AC's own messages that end (or refuse) an action. Anchored at the start of the line where
/// the wording is known, and lines that are somebody talking are ignored first, so a player
/// saying "You're too busy!" doesn't fail anything.
/// </summary>
internal static class ActionChat
{
    private static readonly Regex CannotUseWith = new(@"^Cannot use the .+ with the ", RegexOptions.CultureInvariant);
    private static readonly Regex CannotBeUsedOn = new(@"^The .+ cannot be used on ", RegexOptions.CultureInvariant);
    private static readonly Regex CombinedWithItself = new(@"^The .+ cannot be combined with itself", RegexOptions.CultureInvariant);
    private static readonly Regex MustWield = new(@"^You must (wield|contain) the .+ to use it", RegexOptions.CultureInvariant);

    public static bool IsSpeech(string t) =>
        t.StartsWith('[') || t.Contains(" says, \"", StringComparison.Ordinal) || t.Contains(" tells you, \"", StringComparison.Ordinal)
        || t.StartsWith("You say, \"", StringComparison.Ordinal) || t.StartsWith("You tell ", StringComparison.Ordinal)
        || t.Contains(" says to you, \"", StringComparison.Ordinal);

    /// <summary>
    /// Classifies a line; for <see cref="ChatHint.Fail"/> also the ActionError, whether only a
    /// cast can cause it, and whether it is <paramref name="coded"/>: the text AC shows for a
    /// WeenieError code that engine API v70+ reports directly (see WeenieErrors). An action
    /// that reads those codes ignores coded lines, since the line may be about somebody
    /// else's action (RynthAi's cast, the player's own clicks); the rest are plain server text
    /// with no code behind them, so chat stays the only signal for those.
    /// </summary>
    public static ChatHint Classify(string t, out string error, out bool spellOnly, out bool coded)
    {
        error = ActionErrors.None;
        spellOnly = false;
        coded = true;

        if (Starts(t, "You're too busy") || Starts(t, "You are too busy")) return Fail(ActionErrors.TooBusy, out error);
        if (Starts(t, "Unable to move to object")) return Fail(ActionErrors.SourceObjectTooFar, out error);
        if (Starts(t, "You are too encumbered")) return Fail(ActionErrors.TooEncumbered, out error);
        if (Has(t, "doesn't know what to do with that") || Has(t, "does not know what to do with that"))
            return Fail(ActionErrors.NPCDoesntKnowWhatToDoWithThat, out error);
        if (Has(t, "cannot carry any more")) return Fail(ActionErrors.InvalidInventoryLocation, out error);

        coded = false;
        if (Starts(t, "You can't pick that up") || Starts(t, "You cannot pick that up") || Starts(t, "You can not pick that up"))
            return Fail(ActionErrors.YouCantPickThatUp, out error);
        if (Starts(t, "Cannot pick that up and wield it while not at peace") || Has(t, "must be in peace mode"))
            return Fail(ActionErrors.MustBeInPeaceMode, out error);
        if (CombinedWithItself.IsMatch(t)) return Fail(ActionErrors.CantUseOnItself, out error);
        if (CannotUseWith.IsMatch(t) || CannotBeUsedOn.IsMatch(t)) return Fail(ActionErrors.InvalidTargetItem, out error);
        if (MustWield.IsMatch(t)) return Fail(ActionErrors.SourceItemNotInInventory, out error);
        if (Has(t, "have used this item too recently")) return Fail(ActionErrors.OnCooldown, out error);
        if (Has(t, " is not accepting ")) return Fail(ActionErrors.ServerError, out error);
        if (Has(t, "enough pack space")) return Fail(ActionErrors.InvalidInventoryLocation, out error);

        if (Starts(t, "You give ") || Starts(t, "You hand over ") || Starts(t, "You allow ")) return ChatHint.GiveOk;

        // Casting (RynthAi's buff/combat code matches the same phrases).
        if (Starts(t, "You cast ") || Has(t, "resists your spell")) return ChatHint.CastOk;
        spellOnly = true;
        if (Starts(t, "You must specify")) return Fail(ActionErrors.InvalidTargetObject, out error);   // the client's own check
        coded = true;
        if (Has(t, "fizzle") || Has(t, "your spell failed")) return Fail(ActionErrors.SpellFizzled, out error);
        if (Has(t, "missing some required") || Has(t, "have all the components for this spell") || Starts(t, "You do not have the"))
            return Fail(ActionErrors.MissingComponents, out error);
        if (Has(t, "don't know that spell") || Has(t, "do not know that spell")) return Fail(ActionErrors.YouDontKnowThatSpell, out error);
        if (Has(t, "lack the mana") || Has(t, "enough mana to cast")) return Fail(ActionErrors.ServerError, out error);
        if (Has(t, "is out of range") || Starts(t, "You fail to affect")) return Fail(ActionErrors.ServerError, out error);
        spellOnly = false;
        coded = false;
        return ChatHint.None;
    }

    private static ChatHint Fail(string e, out string error) { error = e; return ChatHint.Fail; }
    private static bool Starts(string t, string p) => t.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    private static bool Has(string t, string p) => t.Contains(p, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One queued action and its Lua object. The Lua table's fields are RAW fields, rewritten on
/// every state change (<c>await</c> polls the raw <c>IsFinished</c>):
/// ActionId, ActionType, Name, ScriptName, Priority, Options, IsStarted, IsRunning, IsFinished,
/// Success, Error, ErrorDetails, Result, StartedAt, FinishedAt (clock() milliseconds),
/// CurrentRetryCount, plus the action's own arguments (ObjectId, TargetId, SpellId, ...).
///
/// Lifecycle: Queued → Waiting (gates: logged in, not in portal space, no cast/use gesture
/// animating) → checks → Send → Sent (watch completion signals) → Finished. A temporary error
/// (TooBusy, SpellFizzled) goes to RetryWait and back to Waiting, up to MaxRetryCount. The
/// timeout counts from the start of each attempt.
/// </summary>
internal abstract class LuaAction
{
    private static long _nextId;

    protected readonly RynthLuaPlugin P;
    protected RynthCoreHost Host => P.ActHost;
    public readonly long ActionId = ++_nextId;
    public readonly ScriptContext Ctx;
    public Table T = null!;
    public DynValue Callback = DynValue.Nil;

    public string Name = string.Empty;
    public int Priority;
    public int TimeoutMs;
    public int MaxRetryCount;
    public bool SkipChecks;

    public ActionPhase Phase { get; private set; } = ActionPhase.Queued;
    /// <summary>The RunAllOrdered action running this one (null: the queue runs it).</summary>
    public LuaAction? Owner;
    public int Retry;
    public long StartedAt, FinishedAt, AttemptStart, SentAt, RetryAt;
    public bool Success;
    public string Error = ActionErrors.None;
    public string ErrorDetails = string.Empty;
    public DynValue Result = DynValue.Nil;

    private bool _settling;
    private long _settleAt;
    private DynValue _settleResult = DynValue.Nil;

    protected LuaAction(RynthLuaPlugin p, ScriptContext ctx)
    {
        P = p;
        Ctx = ctx;
    }

    /// <summary>"ObjectUse", "CastSpell", ... (the game.Actions function that made it).</summary>
    public abstract string Kind { get; }
    protected virtual int DefaultPriority => ActionPriority.Misc;
    protected virtual int DefaultTimeoutMs => 5000;
    protected virtual int DefaultMaxRetryCount => 3;
    /// <summary>Runs beside the queue (Sleep, InvokeChat) instead of taking the one action slot.</summary>
    public virtual bool Immediate => false;
    /// <summary>UB's ActionType: the kind's priority category (Immediate for the ones beside the queue).</summary>
    public int Category => Immediate ? ActionPriority.Immediate : DefaultPriority;
    protected virtual bool NeedsLogin => true;
    /// <summary>Waits for AC to be free (no cast/use gesture animating, not in portal space) before sending.</summary>
    protected virtual bool NeedsIdle => false;

    /// <summary>Preconditions (skipped with SkipChecks). Returns an ActionError name, or null when fine.</summary>
    protected virtual string? Check(out string details) { details = string.Empty; return null; }
    /// <summary>Issues the command. Return false after calling Fail (or Succeed, when there was nothing to do).</summary>
    protected abstract bool Send(long now);
    /// <summary>Called every tick while Sent: look at the completion signals.</summary>
    protected virtual void Poll(long now) { }
    /// <summary>A chat line while Sent. Default: general failures fail the action (spell-only ones don't).</summary>
    protected virtual void OnChat(string text, ChatHint hint, string error, bool spellOnly)
    {
        if (hint == ChatHint.Fail && !spellOnly) Fail(error, text);
    }
    /// <summary>Extra raw fields of this action type (ObjectId, SpellId, ...).</summary>
    protected virtual void DescribeFields(Table t) { }

    public void ApplyOptions(ActionOptions o)
    {
        Priority = o.Priority ?? DefaultPriority;
        TimeoutMs = o.TimeoutMs ?? DefaultTimeoutMs;
        MaxRetryCount = Math.Max(0, o.MaxRetryCount ?? DefaultMaxRetryCount);
        SkipChecks = o.SkipChecks;
        Name = string.IsNullOrEmpty(o.Name) ? Kind : o.Name!;
        if (Immediate && o.Priority == null) Priority = ActionPriority.Immediate;
    }

    /// <summary>Makes the Lua object in <paramref name="s"/> (raw fields + Cancel/Await).</summary>
    public void CreateTable(Script s, DynValue makeAwait)
    {
        var t = new Table(s);
        T = t;
        t.Set("ActionId", DynValue.NewNumber(ActionId));
        // UB: ActionType is the priority category (ActionType.Inventory, ...); Kind is the
        // game.Actions function that made it ("ObjectUse").
        t.Set("ActionType", DynValue.NewNumber(Category));
        t.Set("Kind", DynValue.NewString(Kind));
        t.Set("Name", DynValue.NewString(Name));
        t.Set("ScriptName", DynValue.NewString(Ctx.Name));
        t.Set("__rynth_action", DynValue.True);

        var opts = new Table(s);
        opts.Set("Priority", DynValue.NewNumber(Priority));
        opts.Set("TimeoutMilliseconds", DynValue.NewNumber(TimeoutMs));
        opts.Set("MaxRetryCount", DynValue.NewNumber(MaxRetryCount));
        opts.Set("SkipChecks", DynValue.NewBoolean(SkipChecks));
        opts.Set("Name", DynValue.NewString(Name));
        t.Set("Options", DynValue.NewTable(opts));
        DescribeFields(t);

        t.Set("Cancel", DynValue.NewCallback((c, a) =>
        {
            Cancel("cancelled by the script");
            return DynValue.Nil;
        }));
        if (makeAwait.Type == DataType.Function)
        {
            try { t.Set("Await", s.Call(makeAwait, DynValue.NewTable(t))); }
            catch { }
        }

        var mt = new Table(s);
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString(ToString()));
        t.MetaTable = mt;
        Sync();
    }

    public override string ToString()
    {
        string state = Phase switch
        {
            ActionPhase.Queued => "queued",
            ActionPhase.Finished => Success ? "succeeded" : $"failed: {Error}",
            _ => "running",
        };
        return $"Action {Name} #{ActionId} ({state})";
    }

    /// <summary>Rewrites the state fields of the Lua table.</summary>
    protected void Sync()
    {
        Table? t = T;
        if (t == null) return;
        bool finished = Phase == ActionPhase.Finished;
        t.Set("Priority", DynValue.NewNumber(Priority));
        t.Set("IsStarted", DynValue.NewBoolean(Phase != ActionPhase.Queued));
        t.Set("IsRunning", DynValue.NewBoolean(Phase is ActionPhase.Waiting or ActionPhase.Sent or ActionPhase.RetryWait));
        t.Set("Success", DynValue.NewBoolean(Success));
        t.Set("Error", DynValue.NewString(Error));
        t.Set("ErrorDetails", DynValue.NewString(ErrorDetails));
        t.Set("Result", Result);
        t.Set("CurrentRetryCount", DynValue.NewNumber(Retry));
        t.Set("StartedAt", StartedAt > 0 ? DynValue.NewNumber(StartedAt) : DynValue.Nil);
        t.Set("FinishedAt", finished ? DynValue.NewNumber(FinishedAt) : DynValue.Nil);
        t.Set("IsFinished", DynValue.NewBoolean(finished));   // last: await reads this one
    }

    // ── Running ─────────────────────────────────────────────────────────────

    /// <summary>One tick of this action (the queue's current action, an immediate one, or a RunAllOrdered step).</summary>
    public void Step(long now)
    {
        if (Phase == ActionPhase.Finished) return;
        if (Phase == ActionPhase.Queued)
        {
            Phase = ActionPhase.Waiting;
            StartedAt = now;
            AttemptStart = now;
            Sync();
        }
        if (Phase == ActionPhase.RetryWait)
        {
            if (now < RetryAt) return;
            Phase = ActionPhase.Waiting;
            AttemptStart = now;
            Sync();
        }
        if (Phase == ActionPhase.Waiting)
        {
            if (NeedsLogin && !SkipChecks && (P.GameState != "InGame" || Host.GetPlayerId() == 0))
            {
                Fail(ActionErrors.NotLoggedIn, "not in game");
                return;
            }
            if (!Ready())
            {
                if (TimeoutMs > 0 && now - AttemptStart >= TimeoutMs)
                    Fail(ActionErrors.TimedOut, $"the client stayed busy for {TimeoutMs} ms, the action was never sent");
                return;
            }
            if (!SkipChecks)
            {
                string? err = Check(out string details);
                if (err != null) { Fail(err, details); return; }
            }
            _settling = false;
            SentAt = now;
            Phase = ActionPhase.Sent;
            MarkServerCodes();
            if (!Send(now)) return;
            Sync();
            return;   // signals are looked at from the next tick
        }
        if (Phase == ActionPhase.Sent)
        {
            if (CheckRefusal()) return;
            Poll(now);
            if (Phase != ActionPhase.Sent) return;
            if (_settling && now >= _settleAt) { Succeed(_settleResult); return; }
            if (TimeoutMs > 0 && now - AttemptStart >= TimeoutMs)
                Fail(ActionErrors.TimedOut, $"no completion signal within {TimeoutMs} ms");
        }
    }

    private bool Ready()
    {
        if (!NeedsIdle || SkipChecks) return true;
        if (Host.HasIsPortaling && Host.IsPortaling()) return false;
        if (Host.HasGetCastBusyState && Host.GetCastBusyState() != 0) return false;
        return true;
    }

    /// <summary>
    /// A chat line reached the queue: only a Sent action listens. An action that reads the
    /// server's codes (API v70+) skips the failure lines those codes already report
    /// (<paramref name="coded"/>): they may be about another action.
    /// </summary>
    public virtual void HandleChat(string text, ChatHint hint, string error, bool spellOnly, bool coded)
    {
        if (Phase != ActionPhase.Sent) return;
        if (hint == ChatHint.Fail && coded && _codes) return;
        OnChat(text, hint, error, spellOnly);
    }

    // ── Server outcome codes (engine API v70+) ──────────────────────────────
    // The engine keeps the last UseDone (seq + WeenieError code) and the last refusal
    // (WeenieError / WeenieErrorWithString / InventoryServerSaveFailed with its item). Both
    // counters are recorded when the action is sent; anything newer is about this attempt or
    // about something else running at the same time, which IsMyRefusal sorts out.

    private bool _codes;
    private int _useDoneSeq, _refusalSeq;

    /// <summary>This attempt reads the server's codes (the action wants them and the engine has them).</summary>
    protected bool ServerCodes => _codes;

    /// <summary>Whether the action reads the server's codes when the engine offers them.</summary>
    protected virtual bool WatchesServerCodes => false;

    /// <summary>
    /// Whether a refusal the server sent while this action ran is about it. eventType 0x028A /
    /// 0x028B (WeenieError, WithString) or 0x00A0 (InventoryServerSaveFailed, objectId = the item).
    /// </summary>
    protected virtual bool IsMyRefusal(uint eventType, uint code, uint objectId) => false;

    private void MarkServerCodes()
    {
        _codes = WatchesServerCodes && Host.HasGetLastUseDone && Host.HasGetLastWeenieError;
        if (!_codes) return;
        Host.TryGetLastUseDone(out _useDoneSeq, out _);
        Host.TryGetLastWeenieError(out _refusalSeq, out _, out _, out _);
    }

    /// <summary>A UseDone that arrived since the send (or the last call): true, with its code (0 = done).</summary>
    protected bool NextUseDone(out uint error)
    {
        error = 0;
        if (!_codes || !Host.TryGetLastUseDone(out int seq, out uint e) || seq == _useDoneSeq) return false;
        _useDoneSeq = seq;
        error = e;
        return true;
    }

    /// <summary>A new refusal that IsMyRefusal claims fails (or retries) the action.</summary>
    private bool CheckRefusal()
    {
        if (!_codes || !Host.TryGetLastWeenieError(out int seq, out uint code, out uint evt, out uint obj) || seq == _refusalSeq)
            return false;
        _refusalSeq = seq;
        if (code == 0 || !IsMyRefusal(evt, code, obj)) return false;
        FailWithCode(code, evt == 0x00A0 ? "the server refused it" : "the server said no");
        return true;
    }

    /// <summary>Fails with the ActionError for a WeenieError code; ErrorDetails names the code.</summary>
    protected void FailWithCode(uint code, string what) => Fail(WeenieErrors.ActionError(code), $"{what} ({WeenieErrors.Describe(code)})");

    /// <summary>A general (not spell, not combat) refusal code this table knows.</summary>
    protected static bool IsGeneralRefusal(uint code) => WeenieErrors.TryKind(code, out var k) && k == WeenieErrors.Kind.General;

    /// <summary>
    /// A completion signal arrived; succeed after <paramref name="ms"/> unless a failure line
    /// comes first (AC's error text and the UseDone can arrive in either order).
    /// </summary>
    protected void SucceedAfter(long now, int ms, DynValue? result = null)
    {
        if (_settling) return;
        _settling = true;
        _settleAt = now + ms;
        _settleResult = result ?? DynValue.Nil;
    }

    protected bool Settling => _settling;

    public void Succeed(DynValue? result = null) => Complete(true, ActionErrors.None, string.Empty, result ?? DynValue.Nil);

    public void Fail(string error, string details)
    {
        if (Phase == ActionPhase.Finished) return;
        if (ActionErrors.IsTemporary(error) && Retry < MaxRetryCount)
        {
            Retry++;
            _settling = false;
            Phase = ActionPhase.RetryWait;
            // Back off before trying again (AC stays busy for a moment after a refusal).
            RetryAt = Environment.TickCount64 + (error == ActionErrors.TooBusy ? 400L * Retry : 250L);
            ErrorDetails = details;
            Sync();
            return;
        }
        Complete(false, error, details, DynValue.Nil);
    }

    public void Cancel(string details)
    {
        if (Phase == ActionPhase.Finished) return;
        Complete(false, ActionErrors.Cancelled, details, DynValue.Nil);
    }

    protected virtual void Complete(bool success, string error, string details, DynValue result)
    {
        if (Phase == ActionPhase.Finished) return;
        Phase = ActionPhase.Finished;
        Success = success;
        Error = error;
        ErrorDetails = details ?? string.Empty;
        Result = result;
        FinishedAt = Environment.TickCount64;
        if (StartedAt == 0) StartedAt = FinishedAt;
        Sync();
        P.OnActionFinished(this);
        if (Callback.Type == DataType.Function && Ctx.Host.Loaded)
            Ctx.Host.Start(Callback, new[] { DynValue.NewTable(T) });
    }

    // ── Helpers for the action types ────────────────────────────────────────

    protected uint PlayerId => Host.GetPlayerId();

    protected bool Exists(uint id) => id != 0 && Host.TryGetObjectName(id, out string n) && n.Length > 0;

    protected bool TryOwnership(uint id, out uint container, out uint wielder)
    {
        container = wielder = 0;
        return Host.HasGetObjectOwnershipInfo && Host.TryGetObjectOwnershipInfo(id, out container, out wielder, out _);
    }

    protected uint ContainerOf(uint id) => TryOwnership(id, out uint c, out _) ? c : 0;
    protected uint WielderOf(uint id) => TryOwnership(id, out _, out uint w) ? w : 0;

    /// <summary>In the character's inventory (main pack, a side pack, or wielded). True when the engine can't tell.</summary>
    protected bool IsMine(uint id)
    {
        uint me = PlayerId;
        if (id == 0 || me == 0) return false;
        if (id == me) return true;
        if (!TryOwnership(id, out uint c, out uint w)) return Host.HasGetObjectOwnershipInfo ? false : true;
        if (w == me || c == me) return true;
        return c != 0 && TryOwnership(c, out uint c2, out _) && c2 == me;
    }

    protected int StackSize(uint id) => Host.TryGetObjectIntProperty(id, 12, out int v) && v > 0 ? v : 1;
    protected int MaxStackSize(uint id) => Host.TryGetObjectIntProperty(id, 11, out int v) ? v : 0;
    protected uint Wcid(uint id) => Host.TryGetObjectWcid(id, out uint w) ? w : 0;

    protected DynValue Wrap(uint id)
    {
        Script? s = Ctx.Host.Lua;
        return s == null || id == 0 ? DynValue.Nil : P.WrapObject(s, Ctx, id);
    }

    protected static string Hex(uint id) => $"0x{id:X8}";

    protected string Describe(uint id) => Host.TryGetObjectName(id, out string n) && n.Length > 0 ? $"{n} ({Hex(id)})" : Hex(id);
}
