using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Views;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthSort;

/// <summary>
/// The controller's view of the game through the engine host. Plugin pump thread only (the
/// plugin's tick), which is also where RynthAi's interfaces may be called.
/// </summary>
internal sealed unsafe class HostSortWorld : ISortWorld
{
    private readonly RynthCoreHost _host;
    private readonly InventoryReader _reader;
    private readonly LootWatch _loot;
    private readonly Func<bool> _vendorOpenByEvent;
    private uint[] _groundBuf = new uint[256];

    public HostSortWorld(RynthCoreHost host, InventoryReader reader, LootWatch loot, Func<bool> vendorOpenByEvent)
    {
        _host = host;
        _reader = reader;
        _loot = loot;
        _vendorOpenByEvent = vendorOpenByEvent;
    }

    public SortInventory? ReadInventory() => _reader.Read(_host);

    /// <summary>The item selected in the game, looked up in the packs as they are now (for "Add selected").</summary>
    public SelectedItem ReadSelected()
    {
        if (!_host.HasGetSelectedItemId) return SelectedItem.Of(SelectionKind.Unavailable);
        uint id = _host.GetSelectedItemId();
        if (id == 0) return SelectedItem.Of(SelectionKind.Nothing);
        SortInventory? inv = _reader.Read(_host);
        if (inv == null) return SelectedItem.Of(SelectionKind.NoInventory, id);
        return SelectionLookup.Find(inv, id, _host.TryGetObjectName(id, out string name) ? name : string.Empty);
    }

    // PutItemInContainer answered "not bound" once: plain moves from then on.
    private bool _putUnbound;

    public SortAbilities Abilities => new()
    {
        ReadOrder = _host.HasContainerOrder,
        PlaceMoves = _host.HasPutItemInContainer && !_putUnbound,
        Merge = _host.HasMergeStackInternal,
    };

    // Diagnostics: a "looting" pause that lasts says what the open container looked like.
    private long _lootPauseSince = -1;
    private bool _lootPauseLogged;
    private int _lootPauseChanges, _lootPauseLastCount = int.MinValue;
    private void NoteLongLootPause(bool looting, uint ground, int count, long nowMs)
    {
        if (!looting) { _lootPauseSince = -1; _lootPauseLogged = false; _lootPauseChanges = 0; _lootPauseLastCount = int.MinValue; return; }
        if (_lootPauseSince < 0) _lootPauseSince = nowMs;
        if (count != _lootPauseLastCount) { _lootPauseChanges++; _lootPauseLastCount = count; }
        if (!_lootPauseLogged && nowMs - _lootPauseSince > 20_000)
        {
            _lootPauseLogged = true;
            try { _host.Log($"[RynthSort] paused for looting over 20 s: open container 0x{ground:X8}, item count now {count}, {_lootPauseChanges} count change(s) in that time"); } catch { }
        }
    }

    public GateInputs ReadGate(bool sortInCombat, long nowMs)
    {
        var g = new GateInputs { SortInCombat = sortInCombat };
        g.InPortalSpace = _host.HasIsPortaling && _host.IsPortaling();

        uint ground = _host.HasGetGroundContainerId ? _host.GetGroundContainerId() : 0;
        int count = 0;
        if (ground != 0)
        {
            // One way of counting only: mixing the two (the item count when it reads, else the
            // contents list) could flip between two different numbers and look like looting.
            count = _host.HasGetNumContainedItems ? _host.GetNumContainedItems(ground)
                                                  : _host.GetContainerContents(ground, _groundBuf);
        }
        g.Looting = _loot.Update(ground, count, nowMs);
        NoteLongLootPause(g.Looting, ground, count, nowMs);

        g.VendorOpen = _host.HasVendorTrade ? _host.TryGetVendorInfo(out _) : _vendorOpenByEvent();
        g.TradeOpen = _host.HasTrade && _host.TryGetTradeState(out TradeState trade) && trade.IsOpen;
        g.ClientBusy = _host.HasGetBusyState && _host.GetBusyState() != 0;
        g.Animating = _host.HasGetCastBusyState && _host.GetCastBusyState() != 0;
        g.Bot = ReadRynthAi(_host);
        return g;
    }

    public bool SendMove(in SortMove move)
    {
        uint id = move.Item.Id;
        if (move.MergeInto != 0 && _host.HasMergeStackInternal)
            return _host.MergeStackInternal(id, move.MergeInto);
        if (move.Kind == MoveKind.Stack) return false;   // a join in place needs the merge
        if (move.Placement >= 0 && _host.HasPutItemInContainer && !_putUnbound)
        {
            int rc = _host.PutItemInContainer(id, move.Dest.Id, move.Placement);
            if (rc != -1) return rc == 1;
            _putUnbound = true;
            try { _host.Log("[RynthSort] this client can't send a move to a slot; items are moved to their packs but not put in order"); } catch { }
        }
        if (move.Kind == MoveKind.Reorder) return false;  // a slot change in place needs PutItemInContainer
        return _host.HasMoveItemInternal && _host.MoveItemInternal(id, move.Dest.Id, 0, Math.Max(1, move.Item.Stack));
    }

    public MoveObservation Observe(in SortMove move)
    {
        uint itemId = move.Item.Id;
        var o = new MoveObservation { Slot = -1 };
        if (_host.HasGetObjectOwnershipInfo && _host.TryGetObjectOwnershipInfo(itemId, out uint container, out uint wielder, out _))
        {
            o.Exists = true;
            o.Container = container;
            o.Wielder = wielder;
        }
        else
        {
            // No ownership info: the object is gone (merged into a stack) when it has no name either.
            o.Exists = _host.TryGetObjectName(itemId, out string n) && n.Length > 0;
            o.Container = 0xFFFFFFFF;
        }
        o.Stack = _host.TryGetObjectIntProperty(itemId, 12, out int s) && s > 0 ? s : 1;
        if (move.Kind == MoveKind.Reorder && o.Exists && o.Container == move.Dest.Id)
            o.Slot = _reader.SlotOf(_host, move.Dest.Id, itemId);
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int seq, out uint error, out uint type, out uint obj))
        {
            o.ErrorSeq = seq;
            o.Error = error;
            o.ErrorEventType = type;
            o.ErrorObject = obj;
        }
        return o;
    }

    public int ErrorSeqNow() =>
        _host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int seq, out _, out _, out _) ? seq : 0;

    // ── Pack jobs: RynthAi's answers (RynthAi.Hub, read only) ─────────────

    /// <summary>How long RynthAi's answers are used before they are asked again (the items the same).</summary>
    private const long JobAskMs = 3000;
    private JobAnswers? _jobAnswers;
    private long _jobAskedMs = long.MinValue / 2;
    private string _jobKey = string.Empty;

    /// <summary>
    /// RynthAi's answers for the jobs <paramref name="wanted"/>: asked again every few seconds, or at
    /// once when the items carried, the jobs or the mule change. RynthAi not loaded, too old (no
    /// RynthAi.Hub answers), or with no profile: that job is unknown and says why. Pump thread.
    /// </summary>
    public JobAnswers? AskJobs(SortInventory inv, IReadOnlyList<JobKind> wanted, string mule, long nowMs)
    {
        var ids = new List<uint>(inv.Main.Items.Count + 64);
        foreach (SortItem it in inv.Main.Items) if (SortPlanner.Sortable(it)) ids.Add(it.Id);
        foreach (SortPack p in inv.Packs)
            foreach (SortItem it in p.Items) if (SortPlanner.Sortable(it)) ids.Add(it.Id);
        var key = new System.Text.StringBuilder();
        foreach (JobKind k in wanted) key.Append((int)k).Append(',');
        key.Append(mule).Append('|').Append(ids.Count);
        uint sum = 0;
        foreach (uint id in ids) sum = unchecked(sum * 31 + id);
        key.Append('|').Append(sum);
        string k2 = key.ToString();
        if (_jobAnswers != null && k2 == _jobKey && nowMs - _jobAskedMs < JobAskMs) return _jobAnswers;
        _jobKey = k2;
        _jobAskedMs = nowMs;
        _jobAnswers = AskRynthAi(_host, ids.ToArray(), wanted, mule);
        return _jobAnswers;
    }

    /// <summary>Asks RynthAi.Hub for each job (resolved again every call: a RynthAi reload frees nothing but changes the table's version).</summary>
    internal static JobAnswers AskRynthAi(RynthCoreHost host, uint[] ids, IReadOnlyList<JobKind> wanted, string mule)
    {
        var answers = new JobAnswers { Mule = mule };
        RynthAiHubApiV1* hub = null;
        string? why = null;
        if (!host.HasGetPluginInterface) why = "This RynthCore can't reach RynthAi";
        else
        {
            try { hub = (RynthAiHubApiV1*)host.GetPluginInterface(RynthAiHub.PluginName, RynthAiHubApiV1.InterfaceName, RynthAiHubApiV1.InterfaceVersion); }
            catch { hub = null; }
            if (hub == null || hub->Version == 0) { hub = null; why = "RynthAi isn't loaded"; }
        }
        foreach (JobKind kind in wanted)
        {
            if (hub == null) { answers.ByKind[kind] = JobAnswer.Unknown(why!); continue; }
            int offset = kind switch
            {
                JobKind.Salvage => RynthAiHubApiV1.LootActionsOffset,
                JobKind.Sell => RynthAiHubApiV1.VendorSellsOffset,
                _ => RynthAiHubApiV1.TradeGivesOffset,
            };
            if (!RynthAiHubApiV1.Has(hub, offset)) { answers.ByKind[kind] = JobAnswer.Unknown("This RynthAi is too old to say which items (update RynthAi)"); continue; }
            answers.ByKind[kind] = Ask(hub, kind, ids, mule);
        }
        return answers;
    }

    private static JobAnswer Ask(RynthAiHubApiV1* hub, JobKind kind, uint[] ids, string mule)
    {
        var result = new byte[Math.Max(1, ids.Length)];
        byte* text = stackalloc byte[256];
        text[0] = 0;
        int ok;
        fixed (uint* pi = ids)
        fixed (byte* pr = result)
        {
            switch (kind)
            {
                case JobKind.Salvage:
                    ok = hub->LootActions == null ? 0 : hub->LootActions(pi, ids.Length, pr, text, 256);
                    break;
                case JobKind.Sell:
                    ok = hub->VendorSells == null ? 0 : hub->VendorSells(pi, ids.Length, pr, text, 256);
                    break;
                default:
                {
                    byte[] who = System.Text.Encoding.UTF8.GetBytes(mule + "\0");
                    fixed (byte* pw = who)
                        ok = hub->TradeGives == null ? 0 : hub->TradeGives(pw, pi, ids.Length, pr, text, 256);
                    break;
                }
            }
        }
        string said = Marshal.PtrToStringUTF8((IntPtr)text) ?? string.Empty;
        if (ok != 1) return JobAnswer.Unknown(said.Length > 0 ? said.TrimEnd('.') : "RynthAi gave no answer");
        var a = new JobAnswer { Known = true, Text = said };
        const byte Salvage = 2;   // VTankLootAction.Salvage
        for (int i = 0; i < ids.Length; i++)
            if (kind == JobKind.Salvage ? result[i] == Salvage : result[i] == 1) a.Marked.Add(ids[i]);
        return a;
    }

    // ── RynthAi ─────────────────────────────────────────────────────────────

    /// <summary>
    /// RynthAi's activity bits: its "RynthAi.Activity" interface; on an older RynthAi without it,
    /// the bot state from "RynthAi.Status" (macro and action); null when RynthAi isn't loaded or not
    /// ready. Resolved again every call: a RynthAi reload frees the tables.
    /// </summary>
    internal static RynthAiActivity? ReadRynthAi(RynthCoreHost host)
    {
        if (!host.HasGetPluginInterface) return null;
        try
        {
            var act = (RynthAiActivityApiV1*)host.GetPluginInterface("RynthAi", RynthAiActivityApiV1.InterfaceName, RynthAiActivityApiV1.InterfaceVersion);
            if (act != null && act->Version >= 1 && act->StructSize >= (uint)sizeof(RynthAiActivityApiV1) && act->GetActivityFlags != null)
            {
                uint flags = 0;
                return act->GetActivityFlags(&flags) != 0 ? (RynthAiActivity)flags : null;
            }
            var status = (RynthAiStatusApiV1*)host.GetPluginInterface("RynthAi", RynthAiStatusApiV1.InterfaceName, RynthAiStatusApiV1.InterfaceVersion);
            if (status != null && status->Version >= 1 && status->StructSize >= (uint)sizeof(RynthAiStatusApiV1) && status->GetStatusJson != null)
            {
                byte* r = null;
                if (status->GetStatusJson(&r) != 0 && r != null)
                    return FromStatusJson(Marshal.PtrToStringUTF8((IntPtr)r));
            }
        }
        catch
        {
            // A RynthAi mid-reload: treat as not there this tick.
        }
        return null;
    }

    /// <summary>The bits an older RynthAi's status JSON ({macro, action, ...}) gives (no server-wait bit).</summary>
    internal static RynthAiActivity? FromStatusJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || json[0] != '{') return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            RynthAiActivity f = RynthAiActivity.None;
            if (root.TryGetProperty("macro", out JsonElement m) && m.ValueKind == JsonValueKind.True) f |= RynthAiActivity.MacroRunning;
            string action = root.TryGetProperty("action", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            if ((f & RynthAiActivity.MacroRunning) != 0)
            {
                // The display label: Buffing shows as Healing / Restoring mana / Restoring stamina too.
                f |= action switch
                {
                    "" or "Default" => RynthAiActivity.None,
                    "Combat" => RynthAiActivity.Combat,
                    "Looting" => RynthAiActivity.Looting,
                    "Salvaging" => RynthAiActivity.Salvaging,
                    "Navigating" or "Following" => RynthAiActivity.Navigating,
                    _ => RynthAiActivity.Buffing,
                };
            }
            return f;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
