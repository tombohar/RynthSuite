// RynthAiPlugin.HubReadOnly.cs - read-only answers for other plugins through RynthAi.Hub
// (HubInterface.cs; appended 2026-11-26 for RynthSort's pack jobs): what the loot profile in use
// says about items in the packs, what AutoVendor would sell, what AutoTrade would give a partner.
// Nothing here changes what RynthAi does: each answer uses its own copy of the profile, writes no
// chat or log line, and sets nothing (the looter's loaders say "Loaded loot profile" in chat and
// can set CurrentLootPath, so they aren't used). Pump thread only.
using System;
using System.IO;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private VTankLootProfile? _askUtl;
    private LootProfile? _askJson;
    private string _askLootPath = string.Empty;
    private DateTime _askLootTime;

    /// <summary>
    /// The first matching rule's action of the loot profile in use, per item (VTank numbers:
    /// <see cref="VTankLootAction"/>; 0 = no rule matches, or the item isn't known yet). The .json
    /// and .utl profiles both; the profile's rules only, as the looter's order has them (no Keep #
    /// counts, no built-in keeps). False with <paramref name="error"/>: not logged in, no profile, or
    /// Loot Mode "Rares only" (the profile isn't used). <paramref name="profileFile"/>: its file name.
    /// </summary>
    internal bool LootActionsForHub(uint[] ids, byte[] actions, out string profileFile, out string? error)
    {
        Array.Clear(actions);
        profileFile = string.Empty;
        error = null;
        var settings = _dashboard?.Settings;
        var cache = _objectCache;
        if (settings == null || cache == null || _playerId == 0) { error = "RynthAi isn't ready (log in first)"; return false; }
        if (LootModes.IsRares(settings.LootMode)) { error = "RynthAi's Loot Mode is rares only, so its loot profile isn't used"; return false; }
        string path = (settings.CurrentLootPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0) { error = "RynthAi has no loot profile picked"; return false; }
        profileFile = Path.GetFileName(path);
        if (!File.Exists(path)) { error = "RynthAi's loot profile " + profileFile + " isn't there"; return false; }
        bool json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        try
        {
            DateTime t = File.GetLastWriteTimeUtc(path);
            if (!string.Equals(_askLootPath, path, StringComparison.OrdinalIgnoreCase) || _askLootTime != t || (json ? _askJson == null : _askUtl == null))
            {
                _askUtl = null;
                _askJson = null;
                if (json) _askJson = LootProfile.Load(path);
                else _askUtl = VTankLootParser.Load(path);
                _askLootPath = path;
                _askLootTime = t;
            }
        }
        catch (Exception ex) { error = "RynthAi's loot profile " + profileFile + " could not be read: " + ex.Message; return false; }

        var ctx = new VTankLootContext(Host, _playerId) { Cache = cache };
        for (int i = 0; i < ids.Length && i < actions.Length; i++)
        {
            WorldObject? wo = cache[unchecked((int)ids[i])];
            if (wo == null || wo.Id == 0) continue;
            if (_askJson != null)
            {
                var (action, rule) = LootEvaluator.Classify(_askJson, wo, _charSkills);
                if (rule != null) actions[i] = (byte)ToVTank(action);
            }
            else if (_askUtl != null)
            {
                VTankLootRule? rule = VTankLootEvaluator.FirstMatch(_askUtl, wo, ctx, out _);
                if (rule != null) actions[i] = (byte)rule.Action;
            }
        }
        return true;
    }

    private static VTankLootAction ToVTank(LootAction a) => a switch
    {
        LootAction.Keep => VTankLootAction.Keep,
        LootAction.Sell => VTankLootAction.Sell,
        LootAction.Salvage => VTankLootAction.Salvage,
        LootAction.Read => VTankLootAction.Read,
        LootAction.KeepUpTo => VTankLootAction.KeepUpTo,
        _ => 0,
    };

    /// <summary>AutoVendor's answer (<see cref="Vendor.AutoVendorManager.WouldSell"/>); false before login.</summary>
    internal bool VendorSellsForHub(uint[] ids, byte[] sells, out string profileFile, out string? error)
    {
        Array.Clear(sells);
        profileFile = string.Empty;
        var av = _autoVendor;
        if (av == null) { error = "RynthAi isn't ready (log in first)"; return false; }
        return av.WouldSell(ids, sells, out profileFile, out error);
    }

    /// <summary>AutoTrade's answer for <paramref name="partner"/> (<see cref="Trade.AutoTradeManager.WouldGive"/>); false before login.</summary>
    internal bool TradeGivesForHub(string partner, uint[] ids, byte[] gives, out string profileFile, out string? error)
    {
        Array.Clear(gives);
        profileFile = string.Empty;
        var at = _autoTrade;
        if (at == null) { error = "RynthAi isn't ready (log in first)"; return false; }
        return at.WouldGive(partner, ids, gives, out profileFile, out error);
    }
}
