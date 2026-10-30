using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthLua;

/// <summary>An action on one world object (and maybe a target).</summary>
internal abstract class ObjectAction : LuaAction
{
    public readonly uint ObjectId;
    public readonly uint TargetId;

    protected ObjectAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId, uint targetId) : base(p, ctx)
    {
        ObjectId = objectId;
        TargetId = targetId;
    }

    protected override void DescribeFields(Table t)
    {
        t.Set("ObjectId", DynValue.NewNumber(ObjectId));
        t.Set("TargetId", DynValue.NewNumber(TargetId));
    }

    protected string? NeedObject(out string details)
    {
        details = string.Empty;
        if (Exists(ObjectId)) return null;
        details = $"no object {Hex(ObjectId)} (not known to the client)";
        return ActionErrors.InvalidSourceObject;
    }

    protected string? NeedMine(out string details)
    {
        details = string.Empty;
        if (IsMine(ObjectId)) return null;
        details = $"{Describe(ObjectId)} isn't in your inventory";
        return ActionErrors.SourceItemNotInInventory;
    }

    protected bool EngineRefused(string what)
    {
        Fail(ActionErrors.Exception, $"the engine didn't accept the {what} (missing SDK function, bad id or full pack)");
        return false;
    }

    // Object actions read the server's codes (API v70+): an inventory refusal names its
    // item, so that one is certain; general refusals (too busy, too encumbered, can't get
    // there, ...) count too. Spell and combat codes never do (RynthAi's casts and fights).
    protected override bool WatchesServerCodes => true;

    protected override bool IsMyRefusal(uint eventType, uint code, uint objectId)
        => eventType == 0x00A0 ? objectId == ObjectId : IsGeneralRefusal(code);
}

/// <summary>
/// ObjectUse / ObjectUseOn. Done on the server's UseDone (the counter moves), the item being
/// used up, or portal space; a failure line within 250 ms of it wins. On API v70+ the
/// UseDone's own code says whether it worked (nonzero fails with that code's ActionError).
/// </summary>
internal sealed class UseAction : ObjectAction
{
    private readonly bool _on;
    private int _seq;

    public UseAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId, uint targetId, bool on) : base(p, ctx, objectId, targetId) => _on = on;

    public override string Kind => _on ? "ObjectUseOn" : "ObjectUse";
    protected override bool NeedsIdle => true;
    protected override int DefaultTimeoutMs => 10000;   // using something far away walks there first

    protected override string? Check(out string details)
    {
        string? e = NeedObject(out details);
        if (e != null) return e;
        if (TargetId != 0)
        {
            if (TargetId == ObjectId) { details = "can't use an object on itself"; return ActionErrors.CantUseOnItself; }
            if (!Exists(TargetId)) { details = $"no target {Hex(TargetId)}"; return ActionErrors.InvalidTargetObject; }
        }
        return null;
    }

    protected override bool Send(long now)
    {
        _seq = Host.GetUseDoneSeq();
        bool ok = TargetId == 0
            ? Host.HasUseObject && Host.UseFor(ObjectId, "Script", $"{Ctx?.Name ?? "?"}: game.Actions.ObjectUse", UseKind.Asked)
            : Host.HasUseObjectOn && Host.UseOnFor(ObjectId, TargetId, "Script", $"{Ctx?.Name ?? "?"}: game.Actions.ObjectUse on a target", UseKind.Asked);
        return ok || EngineRefused("use");
    }

    protected override bool IsMyRefusal(uint eventType, uint code, uint objectId)
        => eventType == 0x00A0 ? objectId == ObjectId || (TargetId != 0 && objectId == TargetId) : IsGeneralRefusal(code);

    protected override void Poll(long now)
    {
        if (ServerCodes)
        {
            // Once settling, a later UseDone belongs to something else.
            if (!Settling && NextUseDone(out uint err))
            {
                if (err != 0) { FailWithCode(err, "the server's UseDone"); return; }
                SucceedAfter(now, 250);
                return;
            }
        }
        else if (Host.HasUseDoneSeq && Host.GetUseDoneSeq() != _seq) { SucceedAfter(now, 250); return; }
        if (!Exists(ObjectId)) { SucceedAfter(now, 250); return; }                 // used up (potion, food, kit)
        if (Host.HasIsPortaling && Host.IsPortaling()) { SucceedAfter(now, 0); return; }
        // Opening a chest / corpse: it's the open container now.
        if (TargetId == 0 && Host.HasGetGroundContainerId && Host.GetGroundContainerId() == ObjectId) { SucceedAfter(now, 150); return; }
        // No UseDone signal on this engine: the gesture finished and nothing complained.
        if (!Host.HasUseDoneSeq && now - SentAt >= 1000 && Host.GetCastBusyState() == 0) SucceedAfter(now, 250);
    }
}

/// <summary>ObjectGive: done when the item leaves the inventory (or its stack shrinks), or "You give ...".</summary>
internal sealed class GiveAction : ObjectAction
{
    private int _stack;

    public GiveAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId, uint targetId) : base(p, ctx, objectId, targetId) { }

    public override string Kind => "ObjectGive";
    protected override int DefaultPriority => ActionPriority.Inventory;
    protected override bool NeedsIdle => true;
    protected override int DefaultTimeoutMs => 10000;   // walks to the target first

    protected override string? Check(out string details)
    {
        string? e = NeedObject(out details) ?? NeedMine(out details);
        if (e != null) return e;
        if (TargetId == 0 || TargetId == PlayerId || !Exists(TargetId)) { details = $"no one to give to ({Hex(TargetId)})"; return ActionErrors.InvalidTargetObject; }
        return null;
    }

    protected override bool Send(long now)
    {
        _stack = StackSize(ObjectId);
        bool ok = Host.HasGiveObjectTo ? Host.GiveObjectTo(ObjectId, TargetId, 0)
                : Host.HasMoveItemExternal && Host.MoveItemExternal(ObjectId, TargetId, Math.Max(1, _stack));
        return ok || EngineRefused("give");
    }

    protected override void Poll(long now)
    {
        if (!Exists(ObjectId) || !IsMine(ObjectId) || StackSize(ObjectId) < _stack) SucceedAfter(now, 150);
    }

    protected override void OnChat(string text, ChatHint hint, string error, bool spellOnly)
    {
        if (hint == ChatHint.GiveOk) { Succeed(); return; }
        base.OnChat(text, hint, error, spellOnly);
    }
}

/// <summary>
/// ObjectMove: into a pack of yours (merging into a stack there when <c>stack</c>), picking
/// something up, or putting it into another container. Done when the item is in the target
/// container (or, for a merge, when the source stack shrinks or disappears).
/// </summary>
internal sealed class MoveAction : ObjectAction
{
    private readonly int _slot;
    private readonly bool _stack;
    private bool _merge;
    private int _stackBefore;
    private readonly bool _pickup;

    public MoveAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId, uint containerId, int slot, bool stack)
        : base(p, ctx, objectId, containerId)
    {
        _slot = Math.Max(0, slot);
        _stack = stack;
        _pickup = !IsMine(objectId);
    }

    public override string Kind => "ObjectMove";
    protected override int DefaultPriority => ActionPriority.Inventory;
    protected override bool NeedsIdle => true;
    protected override int DefaultTimeoutMs => _pickup ? 10000 : 5000;

    protected override void DescribeFields(Table t)
    {
        base.DescribeFields(t);
        t.Set("Slot", DynValue.NewNumber(_slot));
        t.Set("Stack", DynValue.NewBoolean(_stack));
    }

    protected override string? Check(out string details)
    {
        string? e = NeedObject(out details);
        if (e != null) return e;
        if (TargetId == 0) { details = "no container (use Drop() to put it on the ground)"; return ActionErrors.InvalidInventoryLocation; }
        if (TargetId != PlayerId && !Exists(TargetId)) { details = $"no container {Hex(TargetId)}"; return ActionErrors.TargetItemNotInInventoryOrLandscape; }
        return null;
    }

    protected override bool Send(long now)
    {
        if (TryOwnership(ObjectId, out uint c, out uint w) && c == TargetId && w == 0) { Succeed(Wrap(ObjectId)); return false; }

        uint me = PlayerId;
        bool mine = IsMine(ObjectId);
        bool targetMine = TargetId == me || IsMine(TargetId);
        _stackBefore = StackSize(ObjectId);
        bool ok;
        if (mine && targetMine)
        {
            uint into = _stack ? FindMergeStack() : 0;
            if (into != 0 && Host.HasMergeStackInternal)
            {
                _merge = true;
                ok = Host.MergeStackInternal(ObjectId, into);
            }
            else ok = Host.HasMoveItemInternal && Host.MoveItemInternal(ObjectId, TargetId, _slot, Math.Max(1, _stackBefore));
        }
        else if (!mine)
            ok = Host.HasMoveItemExternal && Host.MoveItemExternal(ObjectId, TargetId, 0);   // pick up (RynthAi's loot path)
        else
            ok = Host.HasMoveItemExternal && Host.MoveItemExternal(ObjectId, TargetId, Math.Max(1, _stackBefore));
        if (ok) return true;
        Fail(ActionErrors.InvalidInventoryLocation, "the engine refused the move (is the pack full?)");
        return false;
    }

    /// <summary>A stack of the same kind in the target container with room left.</summary>
    private uint FindMergeStack()
    {
        uint wcid = Wcid(ObjectId);
        if (wcid == 0 || MaxStackSize(ObjectId) <= 1) return 0;
        var buf = new uint[256];
        int n = Host.GetContainerContents(TargetId, buf);
        for (int i = 0; i < n; i++)
        {
            uint id = buf[i];
            if (id == ObjectId || Wcid(id) != wcid) continue;
            int max = MaxStackSize(id);
            if (max > 1 && StackSize(id) < max) return id;
        }
        return 0;
    }

    protected override void Poll(long now)
    {
        if (_merge)
        {
            if (!Exists(ObjectId) || StackSize(ObjectId) < _stackBefore) Succeed();
            return;
        }
        if (TryOwnership(ObjectId, out uint c, out uint w) && c == TargetId && w == 0) { Succeed(Wrap(ObjectId)); return; }
        if (!Exists(ObjectId)) Succeed();   // merged into a stack on arrival
    }
}

/// <summary>
/// ObjectSplit: a new stack of <c>amount</c> into a container. Done when the source stack
/// shrinks; Result is the new stack (found as a new object of the same kind in the container).
/// </summary>
internal sealed class SplitAction : ObjectAction
{
    private readonly int _amount;
    private readonly int _slot;
    private int _stackBefore;
    private uint _into;
    private HashSet<uint> _before = new();
    private long _splitSeenAt;

    public SplitAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId, uint containerId, int amount, int slot)
        : base(p, ctx, objectId, containerId)
    {
        _amount = amount;
        _slot = Math.Max(0, slot);
    }

    public override string Kind => "ObjectSplit";
    protected override int DefaultPriority => ActionPriority.Inventory;
    protected override bool NeedsIdle => true;

    protected override void DescribeFields(Table t)
    {
        base.DescribeFields(t);
        t.Set("NewStackSize", DynValue.NewNumber(_amount));
        t.Set("Slot", DynValue.NewNumber(_slot));
    }

    protected override string? Check(out string details)
    {
        string? e = NeedObject(out details) ?? NeedMine(out details);
        if (e != null) return e;
        int stack = StackSize(ObjectId);
        if (stack <= 1) { details = $"{Describe(ObjectId)} isn't a stack"; return ActionErrors.CantSplitThisObject; }
        if (_amount >= stack) { details = $"can't split {_amount} off a stack of {stack}"; return ActionErrors.ObjectSplitAmountTooBig; }
        return null;
    }

    protected override bool Send(long now)
    {
        _into = TargetId != 0 ? TargetId : ContainerOf(ObjectId);
        if (_into == 0) _into = PlayerId;
        _stackBefore = StackSize(ObjectId);
        _before = Contents(_into);
        bool ok = Host.HasSplitStackInternal && Host.SplitStackInternal(ObjectId, _into, _slot, _amount);
        return ok || EngineRefused("split");
    }

    private HashSet<uint> Contents(uint container)
    {
        var buf = new uint[256];
        int n = Host.GetContainerContents(container, buf);
        var set = new HashSet<uint>();
        for (int i = 0; i < n; i++) set.Add(buf[i]);
        return set;
    }

    protected override void Poll(long now)
    {
        if (_splitSeenAt == 0 && StackSize(ObjectId) < _stackBefore) _splitSeenAt = now;
        uint wcid = Wcid(ObjectId);
        foreach (uint id in Contents(_into))
        {
            if (id == ObjectId || _before.Contains(id)) continue;
            if (wcid != 0 && Wcid(id) != wcid) continue;
            T.Set("NewStackObjectId", DynValue.NewNumber(id));   // UB's field
            Succeed(Wrap(id));
            return;
        }
        // The stack shrank but the new one didn't show up in the container yet: give it a moment.
        if (_splitSeenAt != 0 && now - _splitSeenAt >= 1500) Succeed();
    }
}

/// <summary>ObjectDrop: done when the item isn't in the inventory anymore.</summary>
internal sealed class DropAction : ObjectAction
{
    public DropAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId) : base(p, ctx, objectId, 0) { }

    public override string Kind => "ObjectDrop";
    protected override int DefaultPriority => ActionPriority.Inventory;
    protected override bool NeedsIdle => true;

    protected override string? Check(out string details) => NeedObject(out details) ?? NeedMine(out details);

    protected override bool Send(long now)
        => (Host.HasMoveItemExternal && Host.MoveItemExternal(ObjectId, 0, 0)) || EngineRefused("drop");

    protected override void Poll(long now)
    {
        if (!Exists(ObjectId) || !IsMine(ObjectId)) Succeed();
    }
}

/// <summary>
/// ObjectWield: with a slot (an EquipMask number) the item goes into that slot, as dragging it
/// onto the paperdoll does (engine API v70+ WieldItem; the server checks the slot against the
/// item). Without a slot, or on an older engine, it uses the item and AC wields it in its
/// default slot. Done when you wield it; a refusal of the item fails it.
/// </summary>
internal sealed class WieldAction : ObjectAction
{
    private readonly uint _slot;

    public WieldAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId, uint slot) : base(p, ctx, objectId, 0) => _slot = slot;

    public override string Kind => "ObjectWield";
    protected override int DefaultPriority => ActionPriority.Wield;
    protected override bool NeedsIdle => true;

    protected override void DescribeFields(Table t)
    {
        base.DescribeFields(t);
        t.Set("Slot", DynValue.NewNumber(_slot));
    }

    protected override string? Check(out string details)
    {
        string? e = NeedObject(out details);
        if (e != null) return e;
        if (WielderOf(ObjectId) == PlayerId) { details = $"{Describe(ObjectId)} is already wielded"; return ActionErrors.ItemAlreadyWielded; }
        // ValidLocations (IntId 9): the slots the item can go into, when the client knows them.
        if (_slot != 0 && Host.HasWieldItem && Host.TryGetObjectIntProperty(ObjectId, 9, out int valid) && valid != 0
            && (unchecked((uint)valid) & _slot) == 0)
        {
            details = $"{Describe(ObjectId)} can't go in slot 0x{_slot:X8} (it fits 0x{unchecked((uint)valid):X8})";
            return ActionErrors.InvalidInventoryLocation;
        }
        return null;
    }

    protected override bool Send(long now)
    {
        if (_slot != 0 && Host.HasWieldItem)
            return Host.WieldItem(ObjectId, _slot) || EngineRefused("wield");
        return (Host.HasUseObject && Host.UseFor(ObjectId, "Script", $"{Ctx?.Name ?? "?"}: game.Actions.ObjectWield", UseKind.Asked)) || EngineRefused("wield");
    }

    protected override void Poll(long now)
    {
        if (WielderOf(ObjectId) == PlayerId) Succeed(Wrap(ObjectId));
    }
}

/// <summary>ObjectSelect: done when the client's selection is the object.</summary>
internal sealed class SelectAction : ObjectAction
{
    public SelectAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId) : base(p, ctx, objectId, 0) { }

    public override string Kind => "ObjectSelect";
    protected override bool WatchesServerCodes => false;   // no server round trip
    protected override string? Check(out string details) => NeedObject(out details);
    protected override bool Send(long now) => (Host.HasSelectItem && Host.SelectItem(ObjectId)) || EngineRefused("select");
    protected override void OnChat(string text, ChatHint hint, string error, bool spellOnly) { }   // no server round trip

    protected override void Poll(long now)
    {
        if (Host.GetSelectedItemId() == ObjectId) Succeed(Wrap(ObjectId));
    }
}

/// <summary>ObjectAppraise: RequestId; done when new appraisal data for the object arrives. Result is the object.</summary>
internal sealed class AppraiseAction : ObjectAction
{
    private long _idTime;
    private bool _had;

    public AppraiseAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId) : base(p, ctx, objectId, 0) { }

    public override string Kind => "ObjectAppraise";
    protected override bool WatchesServerCodes => false;
    protected override string? Check(out string details) => NeedObject(out details);
    protected override void OnChat(string text, ChatHint hint, string error, bool spellOnly) { }   // not a use: AC's "busy" lines aren't about it

    protected override bool Send(long now)
    {
        _idTime = Host.GetLastIdTime(ObjectId);
        _had = Host.HasAppraisalData(ObjectId);
        return (Host.HasRequestId && Host.RequestId(ObjectId)) || EngineRefused("appraisal request");
    }

    protected override void Poll(long now)
    {
        long t = Host.GetLastIdTime(ObjectId);
        if (t != 0 && t != _idTime) { Succeed(Wrap(ObjectId)); return; }
        if (!_had && Host.HasAppraisalData(ObjectId)) { Succeed(Wrap(ObjectId)); return; }
        // The id time has a one-second grain: a re-appraisal within the same second looks
        // unchanged. Data that was already there is good enough after a short wait.
        if (_had && t == _idTime && now - SentAt >= 1500) Succeed(Wrap(ObjectId));
    }
}

/// <summary>
/// CastSpell: done on the server's UseDone (or "You cast ..."); fizzles and "too busy" are
/// retried (MaxRetryCount), missing components / unknown spell fail at once. On API v70+ the
/// outcome comes from the server's codes instead of chat: the UseDone's code, and a spell
/// refusal (a fizzle is WeenieError 0x402 followed by UseDone(0)).
/// </summary>
internal sealed class CastSpellAction : LuaAction
{
    public readonly int SpellId;
    public readonly uint TargetId;
    private int _seq;
    private bool _sawBusy;

    public CastSpellAction(RynthLuaPlugin p, ScriptContext ctx, int spellId, uint targetId) : base(p, ctx)
    {
        SpellId = spellId;
        TargetId = targetId;
    }

    public override string Kind => "CastSpell";
    protected override int DefaultPriority => ActionPriority.CastSpell;
    protected override bool NeedsIdle => true;
    // A recall's gesture and teleport can run past 5 s before portal space even starts ("no
    // completion signal within 5000 ms" while the character did recall, 10-02). An ordinary cast
    // still finishes on its UseDone in a second or two; this only bounds a cast that never answers.
    protected override int DefaultTimeoutMs => 15000;
    private uint _startLandblock;

    protected override void DescribeFields(Table t)
    {
        t.Set("SpellId", DynValue.NewNumber(SpellId));
        t.Set("TargetId", DynValue.NewNumber(TargetId));
    }

    protected override string? Check(out string details)
    {
        details = string.Empty;
        if (SpellId <= 0) { details = $"spell id {SpellId}"; return ActionErrors.UnknownSpellId; }
        if (P.KnowsSpellIfLoaded((uint)SpellId) == false)
        { details = $"spell {SpellId} isn't in your spellbook"; return ActionErrors.YouDontKnowThatSpell; }
        if (Host.HasGetCurrentCombatMode && Host.GetCurrentCombatMode() != 8)
        { details = "not in magic mode (game.Actions.SetCombatMode(\"Magic\") first)"; return ActionErrors.InvalidCombatMode; }
        if (TargetId != 0 && !Exists(TargetId)) { details = $"no target {Hex(TargetId)}"; return ActionErrors.InvalidTargetObject; }
        return null;
    }

    protected override bool Send(long now)
    {
        _seq = Host.GetUseDoneSeq();
        _sawBusy = false;
        _startLandblock = Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) ? cell >> 16 : 0;
        if (Host.HasCastSpell && Host.CastSpell(TargetId, SpellId)) return true;
        // The engine holds one pending cast at a time: another one queued (RynthAi buffing right
        // after a recall, say) refuses this one for a moment. That's "busy, try again", not a
        // failure (10-02: "the engine didn't accept the cast"); TooBusy retries with a back-off.
        if (Host.HasCastSpell) { Fail(ActionErrors.TooBusy, "the engine's cast slot was busy (another cast was queued)"); return false; }
        Fail(ActionErrors.Exception, "this engine can't cast");
        return false;
    }

    protected override bool WatchesServerCodes => true;

    // Spell refusals, and "too busy" (which the server may also send as a WeenieError).
    protected override bool IsMyRefusal(uint eventType, uint code, uint objectId)
        => eventType != 0x00A0 && (code == 0x001D || (WeenieErrors.TryKind(code, out var k) && k == WeenieErrors.Kind.Spell));

    protected override void Poll(long now)
    {
        // A recall (Lifestone Recall, portal ties, Aphus Lassel...) ends in a teleport, and the
        // server's UseDone can come only after portal space - 6-7 s on Aelrynth, past the 5 s
        // timeout ("no completion signal within 5000 ms" on game.Actions.CastSpell(1635, ...),
        // 10-02). Portal space means the cast worked.
        if (Host.HasIsPortaling && Host.IsPortaling())
        {
            if (!Settling) SucceedAfter(now, 100);
            return;
        }
        // ...or already landed somewhere else (portal space missed between two ticks).
        if (_startLandblock != 0 && Host.TryGetPlayerPose(out uint cellNow, out _, out _, out _, out _, out _, out _, out _)
            && cellNow != 0 && (cellNow >> 16) != _startLandblock)
        {
            if (!Settling) SucceedAfter(now, 100);
            return;
        }
        if (ServerCodes)
        {
            // A fizzle's WeenieError comes before its UseDone(0), and refusals are checked
            // before Poll, so a short settle is enough.
            if (!Settling && NextUseDone(out uint err))
            {
                if (err != 0) FailWithCode(err, "the server's UseDone");
                else SucceedAfter(now, 100);
            }
            return;
        }
        if (Host.HasUseDoneSeq)
        {
            if (Host.GetUseDoneSeq() != _seq) SucceedAfter(now, 300);
            return;
        }
        // No UseDone on this engine: the cast gesture started and ended.
        int busy = Host.GetCastBusyState();
        if (busy != 0) _sawBusy = true;
        else if (_sawBusy) SucceedAfter(now, 500);
    }

    protected override void OnChat(string text, ChatHint hint, string error, bool spellOnly)
    {
        // With the server's codes, "You cast ..." may be somebody else's cast (RynthAi): the
        // UseDone decides. Coded failure lines never get here then (HandleChat drops them).
        if (hint == ChatHint.CastOk) { if (!ServerCodes) Succeed(); return; }
        if (hint == ChatHint.Fail) Fail(error, text);
    }
}

/// <summary>SetCombatMode: done when the client reports the mode.</summary>
internal sealed class CombatModeAction : LuaAction
{
    public readonly int Mode;

    public CombatModeAction(RynthLuaPlugin p, ScriptContext ctx, int mode) : base(p, ctx) => Mode = mode;

    public override string Kind => "SetCombatMode";
    protected override int DefaultPriority => ActionPriority.Combat;
    protected override bool NeedsIdle => true;

    protected override void DescribeFields(Table t)
    {
        t.Set("CombatMode", DynValue.NewNumber(Mode));
        t.Set("CombatModeName", DynValue.NewString(LuaEnums.CombatModeName(Mode)));
    }

    protected override bool Send(long now)
    {
        if (Host.GetCurrentCombatMode() == Mode) { Succeed(); return false; }
        if (Host.HasChangeCombatMode && Host.ChangeCombatMode(Mode)) return true;
        Fail(ActionErrors.Exception, "the engine didn't accept the combat mode change");
        return false;
    }

    protected override void Poll(long now)
    {
        if (Host.GetCurrentCombatMode() == Mode) Succeed();
    }
}

/// <summary>Sleep(ms): an immediate action that finishes after ms (handy inside RunAllOrdered).</summary>
internal sealed class SleepAction : LuaAction
{
    public readonly int Milliseconds;
    private long _until;

    public SleepAction(RynthLuaPlugin p, ScriptContext ctx, int ms) : base(p, ctx) => Milliseconds = Math.Max(0, ms);

    public override string Kind => "Sleep";
    public override bool Immediate => true;
    protected override bool NeedsLogin => false;
    protected override int DefaultTimeoutMs => 0;
    protected override int DefaultMaxRetryCount => 0;

    protected override void DescribeFields(Table t) => t.Set("Milliseconds", DynValue.NewNumber(Milliseconds));

    protected override bool Send(long now)
    {
        _until = now + Milliseconds;
        return true;
    }

    protected override void Poll(long now)
    {
        if (now >= _until) Succeed();
    }
}

/// <summary>InvokeChat(text): as if typed in the chat bar (commands too). Immediate.</summary>
internal sealed class InvokeChatAction : LuaAction
{
    public readonly string Text;

    public InvokeChatAction(RynthLuaPlugin p, ScriptContext ctx, string text) : base(p, ctx) => Text = text;

    public override string Kind => "InvokeChat";
    public override bool Immediate => true;
    protected override bool NeedsLogin => false;
    protected override int DefaultMaxRetryCount => 0;

    protected override void DescribeFields(Table t) => t.Set("Text", DynValue.NewString(Text));

    protected override bool Send(long now)
    {
        if (Host.HasInvokeChatParser && Host.InvokeChatParser(Text)) { Succeed(DynValue.True); return false; }
        Fail(ActionErrors.Exception, Host.HasInvokeChatParser ? "the chat parser didn't take the line" : "this engine has no chat parser function");
        return false;
    }
}

/// <summary>
/// RunAllOrdered(list): runs the given actions one after another as a single queue entry.
/// Actions still waiting in the queue are taken out of it; one that already started is waited
/// for. Stops at the first failure (the rest are cancelled) with that action's Error; Result
/// is the list.
/// </summary>
internal sealed class RunAllOrderedAction : LuaAction
{
    private readonly List<LuaAction?> _children;
    private readonly List<Table> _tables;
    private int _index;

    public RunAllOrderedAction(RynthLuaPlugin p, ScriptContext ctx, List<LuaAction?> children, List<Table> tables) : base(p, ctx)
    {
        _children = children;
        _tables = tables;
    }

    public override string Kind => "RunAllOrdered";
    protected override int DefaultTimeoutMs => 0;
    protected override int DefaultMaxRetryCount => 0;
    protected override bool NeedsLogin => false;

    protected override void DescribeFields(Table t)
    {
        var list = new Table(t.OwnerScript);
        for (int i = 0; i < _tables.Count; i++) list.Set(i + 1, DynValue.NewTable(_tables[i]));
        t.Set("Actions", DynValue.NewTable(list));
    }

    protected override bool Send(long now) => true;

    protected override void Poll(long now)
    {
        while (_index < _children.Count)
        {
            LuaAction? child = _children[_index];
            if (child == null)
            {
                // An action that had already finished when the list was made.
                DynValue ok = _tables[_index].RawGet("Success") ?? DynValue.False;
                if (!ok.CastToBool())
                {
                    string err = _tables[_index].RawGet("Error")?.CastToString() ?? ActionErrors.PreconditionFailed;
                    FailAt(err, "an action in the list had already failed");
                    return;
                }
                _index++;
                continue;
            }
            if (child.Phase != ActionPhase.Finished)
            {
                if (child.Owner != this) return;   // running in the queue: wait for it
                child.Step(now);
                if (child.Phase != ActionPhase.Finished) return;
            }
            if (!child.Success) { FailAt(child.Error, child.ErrorDetails); return; }
            _index++;
        }
        Result = DynValue.NewTable(ResultList());
        Succeed(Result);
    }

    private Table ResultList()
    {
        var list = new Table(T.OwnerScript);
        for (int i = 0; i < _tables.Count; i++) list.Set(i + 1, DynValue.NewTable(_tables[i]));
        return list;
    }

    private void FailAt(string error, string details)
        => Fail(error, $"step {_index + 1} of {_children.Count}: {details}");

    public override void HandleChat(string text, ChatHint hint, string error, bool spellOnly, bool coded)
    {
        if (Phase != ActionPhase.Sent || _index >= _children.Count) return;
        LuaAction? child = _children[_index];
        if (child != null && child.Owner == this) child.HandleChat(text, hint, error, spellOnly, coded);
    }

    protected override void Complete(bool success, string error, string details, DynValue result)
    {
        base.Complete(success, error, details, result);
        foreach (LuaAction? c in _children)
            if (c != null && c.Owner == this && c.Phase != ActionPhase.Finished) c.Cancel("RunAllOrdered stopped");
    }
}
