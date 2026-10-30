using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthLua.CompatTests;

/// <summary>
/// The fake engine: a <see cref="RynthCoreApiNative"/> whose function pointers are the
/// static functions below, all served from <see cref="FakeWorld.W"/>. The plugin's own
/// SDK wrapper (RynthCoreHost) calls them exactly as it calls the real engine, so RynthLua
/// runs unmodified. Functions the plugin doesn't need stay null (the SDK's Has* checks
/// then say "not available", as on an older engine).
/// </summary>
internal static unsafe class FakeHost
{
    private static FakeWorld W => FakeWorld.W;
    private static readonly Dictionary<string, IntPtr> AnsiCache = new();

    private static IntPtr Ansi(string s)
    {
        if (!AnsiCache.TryGetValue(s, out IntPtr p)) AnsiCache[s] = p = Marshal.StringToHGlobalAnsi(s);
        return p;
    }

    public static RynthCoreApiNative Build()
    {
        var a = new RynthCoreApiNative { Version = 73 };
        a.LogFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&LogImpl;
        a.WriteToChatFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&WriteToChat;
        a.InvokeChatParserFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&InvokeChatParser;
        a.GetPlayerIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetPlayerId;
        a.GetWorldNameFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetWorldName;
        a.GetAccountNameFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetAccountName;
        a.GetObjectNameFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, IntPtr>)&GetObjectName;
        a.GetObjectWcidFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&GetObjectWcid;
        a.GetObjectBitfieldFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&GetObjectBitfield;
        a.GetItemTypeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int>)&GetItemType;
        a.GetObjectIntPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&GetInt;
        a.GetObjectQuadPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, long*, int>)&GetInt64;
        a.GetObjectDoublePropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, double*, int>)&GetFloat;
        a.GetObjectStringPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, IntPtr>)&GetString;
        a.GetObjectBoolPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&GetBool;
        a.GetObjectDataIdPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&GetDataId;
        a.GetObjectOwnershipInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, int>)&GetOwnership;
        a.GetObjectInstanceIdPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&GetInstanceId;
        a.GetObjectWielderInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, int>)&GetWielder;
        a.GetContainerContentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&GetContents;
        a.GetObjectPositionFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, float*, float*, float*, int>)&GetPosition;
        a.GetPlayerPoseFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, float*, float*, float*, float*, float*, float*, float*, int>)&GetPose;
        a.GetObjectHeadingFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float*, int>)&GetHeading;
        a.GetSelectedItemIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetSelected;
        a.GetPreviousSelectedItemIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetPrevSelected;
        a.SelectItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SelectItem;
        a.GetGroundContainerIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetGroundContainer;
        a.GetCurrentCombatModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetCombatMode;
        a.ChangeCombatModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&ChangeCombatMode;
        a.IsPortalingFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&IsPortaling;
        a.GetVitaeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float>)&GetVitae;
        a.GetPlayerVitalsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, uint*, uint*, uint*, int>)&GetVitals;
        a.GetPlayerBaseVitalsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)&GetBaseVitals;
        a.GetObjectAttribute2ndBaseLevelFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&GetVitalMax;
        a.GetObjectSkillFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)&GetSkill;
        a.GetObjectSkillBuffedFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int*, int>)&GetSkillLevel;
        a.GetObjectAttributeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, uint*, int>)&GetAttribute;
        a.ReadPlayerEnchantmentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, double*, int, int>)&ReadEnchantments;
        a.ReadObjectEnchantmentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, double*, int, int>)&ReadObjectEnchantments;
        a.ReadKnownSpellsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, int>)&ReadKnownSpells;
        a.GetServerTimeFn = (IntPtr)(delegate* unmanaged[Cdecl]<double>)&GetServerTime;
        a.GetObjectSpellIdsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&GetSpellIds;
        a.GetBusyStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetBusy;
        a.GetCastBusyStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetCastBusy;
        a.GetUseDoneSeqFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetUseDoneSeq;
        a.GetLastUseDoneFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, uint*, int>)&GetLastUseDone;
        a.GetLastWeenieErrorFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, uint*, uint*, uint*, int>)&GetLastWeenieError;
        a.UseObjectFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&UseObject;
        a.UseObjectOnFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&UseObjectOn;
        a.UseEquippedItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&UseEquippedItem;
        a.MoveItemExternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&MoveItemExternal;
        a.MoveItemInternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)&MoveItemInternal;
        a.SplitStackInternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)&SplitStack;
        a.MergeStackInternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&MergeStack;
        a.GiveObjectToFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&GiveObjectTo;
        a.WieldItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&WieldItem;
        a.CastSpellFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int>)&CastSpell;
        a.RequestIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&RequestId;
        a.HasAppraisalDataFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&HasAppraisal;
        a.GetLastIdTimeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, long>)&GetLastIdTime;
        a.ObjectIsAttackableFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&IsAttackable;
        a.SetAutoRunFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&SetAutoRun;
        a.SalvagePanelOpenFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SalvageOpen;
        a.SalvagePanelAddItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SalvageAdd;
        a.SalvagePanelExecuteFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&SalvageExecute;
        a.GetLiveObjectIdsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, int>)&GetLiveObjectIds;
        a.GetObjectStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int>)&GetObjectState;
        a.GetPluginInterfaceFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr>)&GetPluginInterface;
        a.GetVendorInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<VendorInfoNative*, int>)&GetVendorInfo;
        a.GetVendorItemsFn = (IntPtr)(delegate* unmanaged[Cdecl]<VendorItemNative*, int, int>)&GetVendorItems;
        a.VendorBuyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, VendorTradeEntryNative*, int, uint>)&VendorBuy;
        a.VendorSellFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, uint>)&VendorSell;
        a.GetVendorTradeStatusFn = (IntPtr)(delegate* unmanaged[Cdecl]<VendorTradeStatusNative*, int>)&GetVendorStatus;
        a.GetTradeStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<TradeStateNative*, int>)&GetTradeState;
        a.GetTradeItemsFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, uint*, int, int>)&GetTradeItems;
        a.TradeOpenFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&TradeOpen;
        a.TradeAddFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&TradeAdd;
        a.TradeAcceptFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeAccept;
        a.TradeDeclineFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeDecline;
        a.TradeResetFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeReset;
        a.TradeCloseFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&TradeClose;
        return a;
    }

    private static int B(bool b) => b ? 1 : 0;

    // ── Logging / chat ──────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void LogImpl(IntPtr text) => W.Log.Add(Marshal.PtrToStringAnsi(text) ?? string.Empty);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WriteToChat(IntPtr text, int type) { W.ChatOut.Add(Marshal.PtrToStringUni(text) ?? string.Empty); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InvokeChatParser(IntPtr text) { W.Invoked.Add(Marshal.PtrToStringUni(text) ?? string.Empty); return 1; }

    // ── Identity ────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetPlayerId() => FakeWorld.Player;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetWorldName() => Ansi("TestServer");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetAccountName() => Ansi("testaccount");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetPluginInterface(IntPtr plugin, IntPtr iface, uint version)
    {
        // RynthAi's combat hook when a test turned it on (FakeCombat); nothing else is served.
        string p = Marshal.PtrToStringAnsi(plugin) ?? string.Empty, i = Marshal.PtrToStringAnsi(iface) ?? string.Empty;
        return p == "RynthAi" && i == RynthCore.Plugin.Shared.RynthAiCombatApiV1.InterfaceName && version == 1 ? FakeCombat.Table() : IntPtr.Zero;
    }

    // ── Objects ─────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetObjectName(uint id) => W.Get(id) is { } o ? Ansi(o.Name) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetObjectWcid(uint id) => W.Get(id)?.Wcid ?? 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetObjectBitfield(uint id) => W.Get(id)?.Bitfield ?? 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetItemType(uint id, uint* type)
    {
        if (W.Get(id) is not { } o) return 0;
        *type = o.ItemType;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetInt(uint id, uint key, int* value)
    {
        if (W.Get(id) is not { } o) return 0;
        if (key == 1) { *value = (int)o.ItemType; return 1; }
        if (!o.Ints.TryGetValue(key, out int v)) return 0;
        *value = v;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetInt64(uint id, uint key, long* value)
    {
        if (W.Get(id) is not { } o || !o.Int64s.TryGetValue(key, out long v)) return 0;
        *value = v;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFloat(uint id, uint key, double* value)
    {
        if (W.Get(id) is not { } o || !o.Floats.TryGetValue(key, out double v)) return 0;
        *value = v;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetString(uint id, uint key) =>
        W.Get(id) is { } o && o.Strings.TryGetValue(key, out string? v) ? Ansi(v) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetBool(uint id, uint key, int* value)
    {
        if (W.Get(id) is not { } o || !o.Bools.TryGetValue(key, out bool v)) return 0;
        *value = B(v);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetDataId(uint id, uint key, uint* value)
    {
        if (W.Get(id) is not { } o || !o.DataIds.TryGetValue(key, out uint v)) return 0;
        *value = v;
        return 1;
    }

    // v73: like the engine, Container/Wielder from the object's ownership, others from its table; 0 = unknown.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetInstanceId(uint id, uint key, uint* value)
    {
        if (W.Get(id) is not { } o) return 0;
        uint v = key switch { 2 => o.Container, 3 => o.Wielder, _ => o.InstanceIds.TryGetValue(key, out uint x) ? x : 0 };
        if (v == 0) return 0;
        *value = v;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetOwnership(uint id, uint* container, uint* wielder, uint* location)
    {
        if (W.Get(id) is not { } o) return 0;
        *container = o.Container; *wielder = o.Wielder; *location = o.Location;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetWielder(uint id, uint* wielder, uint* location)
    {
        if (W.Get(id) is not { } o) return 0;
        *wielder = o.Wielder; *location = o.Location;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetContents(uint container, uint* buffer, int capacity)
    {
        var ids = W.Contents(container);
        for (int i = 0; i < Math.Min(ids.Count, capacity); i++) buffer[i] = ids[i];
        return ids.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPosition(uint id, uint* cell, float* x, float* y, float* z)
    {
        if (W.Get(id) is not { } o || !o.HasPosition) return 0;
        *cell = o.Cell; *x = o.X; *y = o.Y; *z = o.Z;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPose(uint* cell, float* x, float* y, float* z, float* qw, float* qx, float* qy, float* qz)
    {
        var me = W.Me;
        *cell = me.Cell; *x = me.X; *y = me.Y; *z = me.Z;
        double half = -me.Heading * Math.PI / 360.0;
        *qw = (float)Math.Cos(half); *qx = 0; *qy = 0; *qz = (float)Math.Sin(half);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetHeading(uint id, float* heading)
    {
        if (W.Get(id) is not { } o) return 0;
        *heading = o.Heading;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLiveObjectIds(uint* buffer, int capacity)
    {
        var ids = W.Objects.Keys.OrderBy(i => i).ToList();
        for (int i = 0; i < Math.Min(ids.Count, capacity); i++) buffer[i] = ids[i];
        return ids.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectState(uint id, uint* state)
    {
        if (W.Get(id) is not { } o || o.PhysicsState == 0) return 0;
        *state = o.PhysicsState;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsAttackable(uint id) => B(W.Get(id) is { } o && (o.Bitfield & 0x10) != 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int HasAppraisal(uint id) => B(W.Get(id)?.Appraised == true);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long GetLastIdTime(uint id) => W.Get(id)?.LastIdTime ?? 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSpellIds(uint id, uint* buffer, int capacity)
    {
        if (W.Get(id) is not { } o) return -1;
        for (int i = 0; i < Math.Min(o.Spells.Count, capacity); i++) buffer[i] = o.Spells[i];
        return o.Spells.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadObjectEnchantments(uint id, uint* spells, double* expiry, int capacity) => id == FakeWorld.Player ? ReadEnch(spells, expiry, capacity) : 0;

    // ── Selection / state ───────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetSelected() => W.Selected;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetPrevSelected() => W.PreviousSelected;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SelectItem(uint id)
    {
        W.Calls.Add($"SelectItem 0x{id:X8}");
        if (W.Get(id) == null) return 0;
        W.SetSelected(id);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetGroundContainer() => W.GroundContainer;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCombatMode() => W.CombatMode;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ChangeCombatMode(int mode) => B(W.ChangeCombat(mode));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsPortaling() => B(W.Portaling);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetBusy() => W.BusyState;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCastBusy() => W.CastBusy;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetUseDoneSeq() => W.UseDoneSeq;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLastUseDone(int* seq, uint* error)
    {
        *seq = W.UseDoneSeq; *error = W.UseDoneError;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLastWeenieError(int* seq, uint* error, uint* eventType, uint* objectId)
    {
        *seq = W.RefusalSeq; *error = W.RefusalError; *eventType = W.RefusalEvent; *objectId = W.RefusalObject;
        return 1;
    }

    // ── Character ───────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static float GetVitae(uint id) => W.Vitae;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVitals(uint* hp, uint* maxHp, uint* st, uint* maxSt, uint* mana, uint* maxMana)
    {
        *hp = W.Hp; *maxHp = W.MaxHp; *st = W.St; *maxSt = W.MaxSt; *mana = W.Mana; *maxMana = W.MaxMana;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetBaseVitals(uint* hp, uint* st, uint* mana)
    {
        *hp = W.BaseHp; *st = W.BaseSt; *mana = W.BaseMana;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVitalMax(uint id, uint stype2nd, uint* value)
    {
        if (id != FakeWorld.Player) return 0;
        *value = stype2nd switch { 1 => W.MaxHp, 3 => W.MaxSt, 5 => W.MaxMana, _ => 0 };
        return *value != 0 ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSkill(uint id, uint skill, int* buffed, int* training)
    {
        if (id != FakeWorld.Player) return 0;
        var s = W.Skills.TryGetValue((int)skill, out var v) ? v : (0, 0, 1);
        *buffed = s.Item1; *training = s.Item3;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSkillLevel(uint id, uint skill, int raw, int* level)
    {
        if (id != FakeWorld.Player) return 0;
        var s = W.Skills.TryGetValue((int)skill, out var v) ? v : (0, 0, 1);
        *level = raw != 0 ? s.Item2 : s.Item1;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetAttribute(uint id, uint attr, int raw, uint* value)
    {
        if (id != FakeWorld.Player || !W.Attributes.TryGetValue((int)attr, out var a)) return 0;
        *value = raw != 0 ? a.Base : a.Cur;
        return 1;
    }

    private static int ReadEnch(uint* spells, double* expiry, int capacity)
    {
        int n = W.Enchantments.Count;
        for (int i = 0; i < Math.Min(n, capacity); i++) { spells[i] = W.Enchantments[i].Spell; expiry[i] = W.Enchantments[i].Expiry; }
        return n;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadEnchantments(uint* spells, double* expiry, int capacity) => ReadEnch(spells, expiry, capacity);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadKnownSpells(uint* spells, int capacity)
    {
        for (int i = 0; i < Math.Min(W.KnownSpells.Count, capacity); i++) spells[i] = W.KnownSpells[i];
        return W.KnownSpells.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double GetServerTime() => W.ServerTime;

    // ── Actions ─────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int UseObject(uint id) => B(W.UseObject(id));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int UseObjectOn(uint id, uint target) => B(W.UseObject(id, target));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int UseEquippedItem(uint id, uint target)
    {
        W.Calls.Add($"UseEquippedItem 0x{id:X8} 0x{target:X8}");
        if (W.Get(id) == null) return 0;
        W.Later(() => W.UseDone());
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MoveItemExternal(uint id, uint target, int amount) => B(W.MoveExternal(id, target, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MoveItemInternal(uint id, uint container, int slot, int amount) => B(W.MoveInternal(id, container, slot, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SplitStack(uint id, uint container, int slot, int amount) => B(W.Split(id, container, slot, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MergeStack(uint src, uint dst) => B(W.Merge(src, dst));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GiveObjectTo(uint id, uint target, int amount) => B(W.Give(id, target, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WieldItem(uint id, uint mask) => B(W.Wield(id, mask));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CastSpell(uint target, int spell) => B(W.Cast(target, spell));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RequestId(uint id) => B(W.RequestId(id));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetAutoRun(int enabled)
    {
        W.Calls.Add($"SetAutoRun {enabled}");
        W.AutoRun = enabled != 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvageOpen(uint tool)
    {
        W.Calls.Add($"SalvagePanelOpen 0x{tool:X8}");
        if (W.Get(tool) == null) return 0;
        W.SalvageTool = tool;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvageAdd(uint item)
    {
        W.Calls.Add($"SalvagePanelAddItem 0x{item:X8}");
        if (W.SalvageTool == 0 || W.Get(item) == null) return 0;
        if (!W.SalvageItems.Contains(item)) W.SalvageItems.Add(item);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvageExecute() => B(W.Salvage());

    // ── Vendor ──────────────────────────────────────────────────────────────

    private static void WriteAnsi(byte* dest, int capacity, string text)
    {
        int n = Math.Min(text.Length, capacity - 1);
        for (int i = 0; i < n; i++) dest[i] = (byte)text[i];
        dest[n] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorInfo(VendorInfoNative* info)
    {
        if (W.VendorOpenId == 0) return 0;
        *info = default;
        info->VendorId = W.VendorOpenId;
        info->Generation = W.VendorGeneration;
        info->ItemTypes = 0x20 | 0x80000 | 0x800;
        info->MinValue = 0;
        info->MaxValue = 10000;
        info->DealsMagic = 1;
        info->BuyRate = 0.6f;
        info->SellRate = 1.15f;
        info->AltCurrencyServerCount = -1;
        info->AltCurrencyHave = -1;
        info->PlayerCoins = W.Me.Ints.TryGetValue(20, out int c) ? c : 0;
        info->ItemCount = W.VendorItems.Count;
        info->Flags = 1;
        WriteAnsi(info->Name, 64, W.Get(W.VendorOpenId)?.Name ?? "Vendor");
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorItems(VendorItemNative* items, int capacity)
    {
        if (W.VendorOpenId == 0) return 0;
        if (items == null) return W.VendorItems.Count;
        for (int i = 0; i < Math.Min(W.VendorItems.Count, capacity); i++)
        {
            var v = W.VendorItems[i];
            items[i] = default;
            items[i].ObjectId = v.Id;
            items[i].Wcid = v.Wcid;
            items[i].ItemType = v.ItemType;
            items[i].Amount = v.Amount;
            items[i].StackSize = 1;
            items[i].MaxStackSize = 100;
            items[i].UnitPrice = v.UnitPrice;
            items[i].Value = v.UnitPrice;
            items[i].UnitValue = v.UnitPrice;
            items[i].Flags = (uint)((v.Amount < 0 ? 1 : 0) | 2);
            WriteAnsi(items[i].Name, 64, v.Name);
        }
        return W.VendorItems.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint VendorBuy(uint vendorId, VendorTradeEntryNative* entries, int count)
    {
        var lines = new List<(uint, int)>();
        for (int i = 0; i < count; i++) lines.Add((entries[i].ObjectId, entries[i].Amount));
        return W.VendorTrade(buy: true, lines);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint VendorSell(uint vendorId, uint* ids, int count)
    {
        var lines = new List<(uint, int)>();
        for (int i = 0; i < count; i++) lines.Add((ids[i], 1));
        return W.VendorTrade(buy: false, lines);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorStatus(VendorTradeStatusNative* status)
    {
        *status = default;
        status->RequestId = W.VendorRequestId;
        status->State = W.VendorState;
        status->Result = W.VendorResult;
        status->IsBuy = W.VendorIsBuy;
        status->VendorId = W.VendorOpenId;
        status->EntryCount = W.VendorEntries;
        return 1;
    }

    // ── Trade ───────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTradeState(TradeStateNative* s)
    {
        uint size = s->Size;
        *s = default;
        s->Size = size;
        s->Flags = (uint)((W.TradeOpen ? 1 : 0) | (W.TradeYouAccepted ? 2 : 0) | (W.TradePartnerAccepted ? 4 : 0) | 8 | 16);
        s->Generation = W.TradeGeneration;
        s->Sequence = W.TradeSequence;
        s->PartnerId = W.TradeOpen ? W.TradePartner : 0;
        s->InitiatorId = W.TradeInitiator;
        s->SelfItemCount = W.TradeSelf.Count;
        s->PartnerItemCount = W.TradePartnerItems.Count;
        s->LastEventType = W.TradeLastEvent;
        s->LastCloseReason = W.TradeLastClose;
        s->LastAcceptedBy = W.TradeLastAcceptedBy;
        s->LastDeclinedBy = W.TradeLastDeclinedBy;
        s->LastResetBy = W.TradeLastResetBy;
        s->CompletedCount = W.TradeCompleted;
        s->PartnerAcceptCount = W.TradePartnerAccepts;
        return sizeof(TradeStateNative);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTradeItems(int side, uint* buffer, int capacity)
    {
        var list = side == 1 ? W.TradeSelf : W.TradePartnerItems;
        for (int i = 0; i < Math.Min(list.Count, capacity); i++) buffer[i] = list[i];
        return list.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeOpen(uint target)
    {
        W.Calls.Add($"TradeOpen 0x{target:X8}");
        W.Later(() => W.StartTrade(target));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeAdd(uint item, uint slot)
    {
        W.Calls.Add($"TradeAdd 0x{item:X8}");
        if (!W.TradeOpen) return 0;
        W.Later(() => W.TradeEvent(0x0200, () => { W.TradeSelf.Add(item); W.TradeYouAccepted = W.TradePartnerAccepted = false; }));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeAccept()
    {
        W.Calls.Add("TradeAccept");
        if (!W.TradeOpen) return 0;
        W.Later(() => W.TradeEvent(0x0202, () => { W.TradeYouAccepted = true; W.TradeLastAcceptedBy = FakeWorld.Player; }));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeDecline()
    {
        W.Calls.Add("TradeDecline");
        if (!W.TradeOpen) return 0;
        W.Later(() => W.TradeEvent(0x0203, () => { W.TradeYouAccepted = false; W.TradeLastDeclinedBy = FakeWorld.Player; }));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeReset()
    {
        W.Calls.Add("TradeReset");
        if (!W.TradeOpen) return 0;
        W.Later(() => W.TradeEvent(0x0205, () => { W.TradeSelf.Clear(); W.TradePartnerItems.Clear(); W.TradeLastResetBy = FakeWorld.Player; }));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TradeClose()
    {
        W.Calls.Add("TradeClose");
        if (!W.TradeOpen) return 0;
        W.Later(() => W.TradeEvent(0x01FF, () => { W.TradeOpen = false; W.TradeLastClose = 1; }));
        return 1;
    }
}
