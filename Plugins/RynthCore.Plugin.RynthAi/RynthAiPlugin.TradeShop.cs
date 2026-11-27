// RynthAiPlugin.TradeShop.cs — the Trade Shop: a player-run shop driven by tells (Trade/TradeShopManager.cs
// has the sale flow and its safety rules). This file wires it into RynthAi: made at login, ticked on the
// plugin tick (TickStep.TradeShop), fed incoming tells, paused in combat / while the macro or a hunt runs /
// while AutoTrade is on, its window (a plugin window, API v89, no bar button: the RynthAi window's options
// menu > Trade shop and /ra tradeshop open it) and /ra tradeshop.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The window's title (its panel key) and surface (Huds/SectionsManifest.cs).</summary>
    internal const string TradeShopTitle = "Trade Shop", TradeShopSurface = "window:tradeshop";

    internal const string TradeShopUsage =
        "Usage: /ra tradeshop [on | off | status | list | add <price> [name] | remove <#> | price <#> <py> | accept | decline | "
        + "mode1 | mode2 | confirm on|off | keys <py per key> | advertise on|off|now|channel <name>|every <minutes>|prefix <text> | show | hide | help]";

    private TradeShopManager? _tradeShop;
    private TradeShopWindow? _tradeShopWindow;   // render thread
    private int _tradeShopFailures;              // render thread

    /// <summary>The shop's settings (the profile's TradeShop), never null once settings exist.</summary>
    private TradeShopSettings? TradeShopCfg
    {
        get
        {
            var s = _dashboard?.Settings;
            if (s == null) return null;
            return s.TradeShop ??= new TradeShopSettings();
        }
    }

    /// <summary>Login: the shop for this character (off until the player turns it on).</summary>
    private void CreateTradeShop()
    {
        if (_objectCache == null || _dashboard == null) return;
        var dash = _dashboard;
        var game = new TradeShopHostGame(Host, _objectCache, () => _playerId, TradeShopPauseReason,
            () => dash?.CharFolder ?? string.Empty);
        _tradeShop = new TradeShopManager(game, () => TradeShopCfg ?? new TradeShopSettings());
    }

    /// <summary>Why the shop must not trade now, or "" (plugin tick).</summary>
    internal string TradeShopPauseReason()
    {
        var s = _dashboard?.Settings;
        if (s == null) return "not ready";
        if (s.IsMacroRunning) return "the macro is running";
        if (HuntActive || _dunPatrolActive) return "a hunt is running";
        if (s.AutoTradeEnabled) return "AutoTrade is on (it would fill the same trade window)";
        int mode = _combatManager?.CurrentCombatMode ?? CombatMode.NonCombat;
        if (mode != CombatMode.NonCombat) return "in combat mode";
        return string.Empty;
    }

    /// <summary>The plugin tick: true while a sale holds the bot (inventory chores wait).</summary>
    private bool TickTradeShop()
    {
        var shop = _tradeShop;
        if (shop == null) return false;
        shop.Tick();
        return shop.HoldsBot;
    }

    /// <summary>Chat thread: a line in the chat window (only incoming tells matter).</summary>
    private void TradeShopChat(string text, int chatType)
    {
        try { _tradeShop?.OnTell(text, chatType == RynthCore.Plugin.Shared.ChatSource.Tell); }
        catch (Exception ex) { Host.Log($"[RynthAi] TradeShop chat failed: {ex.Message}"); }
    }

    // ── /ra tradeshop ────────────────────────────────────────────────────

    private void HandleTradeShopRaCommand(string[] parts)
    {
        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        string rest = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3).Trim() : string.Empty;
        TradeShopSettings? cfg = TradeShopCfg;
        TradeShopManager? shop = _tradeShop;
        if (cfg == null || shop == null)
        {
            ReplyFail("[RynthAi] Trade Shop: not ready yet (log in first). " + TradeShopUsage);
            return;
        }

        switch (verb)
        {
            case "":
            case "show":
            case "open":
            case "window":
            case "ui":
                ReplyOk("[RynthAi] Trade Shop: " + shop.Status + TradeShopWindowNote(RynthPanelOpen.Open));
                return;
            case "hide":
            case "close":
            {
                if (!Host.HasSetPanelOpen) { ReplyFail("[RynthAi] Trade Shop: this RynthCore has no plugin windows (plugin API 89+). " + TradeShopUsage); return; }
                int open = Host.SetPanelOpen(TradeShopTitle, RynthPanelOpen.Close);
                if (open < 0) ReplyFail("[RynthAi] The Trade Shop window isn't there yet (RynthCore adds it a few seconds after login).");
                else ReplyOk("[RynthAi] Trade Shop window closed.");
                return;
            }
            case "status":
                ChatLine($"[RynthAi] Trade Shop: {(cfg.Enabled ? "ON" : "OFF")}, {(cfg.Mode == TradeShopMode.Mode1Stock ? "Mode 1 (my packs)" : "Mode 2 (text, I accept by hand)")}, "
                         + $"confirm each sale {(cfg.ConfirmEachSale ? "ON" : "OFF")}, {cfg.ForSale().Count} for sale, "
                         + (cfg.AdvertiseEnabled ? $"advertising on {TradeShopAdvertise.DisplayName(cfg.AdvertiseChannel)} every {TradeShopSettings.ClampAdvertiseSec(cfg.AdvertiseIntervalSec) / 60} min." : "advertising off."));
                ChatLine("[RynthAi]   " + shop.Status);
                if (shop.Prompt is { } p)
                    ChatLine($"[RynthAi]   Sale ready: {p.Buyer}, {p.Item}, {TradeShopRates.N(p.PricePyreals)} py, paid {p.Paid}. /ra tradeshop accept | decline");
                return;
            case "on":
            case "enable":
                Reply.Answered();   // the shop says what it does on the tick
                shop.SetEnabled(true);
                return;
            case "off":
            case "disable":
                Reply.Answered();
                shop.SetEnabled(false);
                return;
            case "accept":
                if (shop.Prompt == null) { ReplyFail("[RynthAi] Trade Shop: no sale is waiting for your accept."); return; }
                ReplyOk("[RynthAi] Trade Shop: checking the trade again before accepting...");
                shop.RequestAccept();
                return;
            case "decline":
                Reply.Answered();   // the shop says what it called off on the tick
                shop.RequestDecline();
                return;
            case "mode1":
            case "stock":
                cfg.Mode = TradeShopMode.Mode1Stock;
                ScheduleDashSave();
                ReplyOk("[RynthAi] Trade Shop: Mode 1 (items in this character's packs; the shop puts the item in the trade window).");
                return;
            case "mode2":
            case "text":
                cfg.Mode = TradeShopMode.Mode2Text;
                ScheduleDashSave();
                ReplyOk("[RynthAi] Trade Shop: Mode 2 (text listings; the shop checks the payment and you accept every sale by hand).");
                return;
            case "confirm":
            {
                bool? on = OnOff(rest);
                if (on == null) { ReplyFail($"[RynthAi] Trade Shop: confirm each sale is {(cfg.ConfirmEachSale ? "ON" : "OFF")}. Usage: /ra tradeshop confirm on|off"); return; }
                Reply.Answered();   // the shop says it (and warns when off) on the tick
                shop.SetConfirmEachSale(on.Value);
                ScheduleDashSave();
                return;
            }
            case "list":
                ListTradeShop(cfg);
                return;
            case "add":
                AddTradeShopListing(cfg, rest);
                return;
            case "remove":
            case "del":
                RemoveTradeShopListing(cfg, rest);
                return;
            case "price":
                PriceTradeShopListing(cfg, rest);
                return;
            case "keys":
            case "lk":
            {
                if (!TryParsePyreals(rest, out long py) || py < 0) { ReplyFail("[RynthAi] Usage: /ra tradeshop keys <pyreals per Legendary Key> (0 = keys not taken)"); return; }
                cfg.Rates.PyrealsPerLegendaryKey = py;
                cfg.Rates.AcceptLegendaryKeys = py > 0;
                ScheduleDashSave();
                ReplyOk("[RynthAi] Trade Shop: " + cfg.Rates.RatesHelpLine());
                return;
            }
            case "rates":
                ReplyOk("[RynthAi] Trade Shop: " + cfg.Rates.RatesHelpLine());
                return;
            case "advertise":
            case "ad":
                HandleTradeShopAdvertise(cfg, shop, rest);
                return;
            case "help":
            case "?":
                ChatLine("[RynthAi] /ra tradeshop — a shop run from tells (!list, !price, !buy); opens the Trade Shop window.");
                ChatLine("[RynthAi] " + TradeShopUsage);
                return;
            default:
                ReplyFail($"[RynthAi] Trade Shop: unknown option '{verb}'. {TradeShopUsage}");
                return;
        }
    }

    private void HandleTradeShopAdvertise(TradeShopSettings cfg, TradeShopManager shop, string rest)
    {
        string[] bits = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string v = bits.Length > 0 ? bits[0].ToLowerInvariant() : "";
        string arg = bits.Length > 1 ? bits[1].Trim() : "";
        switch (v)
        {
            case "":
            case "status":
                ReplyOk(cfg.AdvertiseEnabled
                    ? $"[RynthAi] Trade Shop advertising ON: {TradeShopAdvertise.DisplayName(cfg.AdvertiseChannel)} every {TradeShopSettings.ClampAdvertiseSec(cfg.AdvertiseIntervalSec) / 60} min (while the shop is on)."
                    : "[RynthAi] Trade Shop advertising OFF. /ra tradeshop advertise on|off|now|channel <name>|every <minutes>|prefix <text>");
                return;
            case "on":
                cfg.AdvertiseEnabled = true;
                ScheduleDashSave();
                ReplyOk($"[RynthAi] Trade Shop advertising ON: {TradeShopAdvertise.DisplayName(cfg.AdvertiseChannel)} every {TradeShopSettings.ClampAdvertiseSec(cfg.AdvertiseIntervalSec) / 60} min, while the shop is on.");
                return;
            case "off":
                cfg.AdvertiseEnabled = false;
                ScheduleDashSave();
                ReplyOk("[RynthAi] Trade Shop advertising OFF.");
                return;
            case "now":
                Reply.Answered();   // the shop says whether it went on the tick
                shop.RequestAdvertise();
                return;
            case "channel":
                if (!TradeShopAdvertise.TryParseChannel(arg, out TradeShopAdvertiseChannel ch))
                {
                    ReplyFail("[RynthAi] Channels: Trade, General, LFG, Roleplay, Society, Local.");
                    return;
                }
                cfg.AdvertiseChannel = ch;
                ScheduleDashSave();
                ReplyOk($"[RynthAi] Trade Shop advertises on {TradeShopAdvertise.DisplayName(ch)}.");
                return;
            case "every":
            case "interval":
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int min))
                {
                    ReplyFail($"[RynthAi] Usage: /ra tradeshop advertise every <minutes> ({TradeShopSettings.MinAdvertiseSec / 60}-{TradeShopSettings.MaxAdvertiseSec / 60}).");
                    return;
                }
                cfg.AdvertiseIntervalSec = TradeShopSettings.ClampAdvertiseSec(min * 60);
                ScheduleDashSave();
                ReplyOk($"[RynthAi] Trade Shop advertises every {cfg.AdvertiseIntervalSec / 60} min.");
                return;
            case "prefix":
                cfg.AdvertisePrefix = arg.TrimStart('/', '@');
                ScheduleDashSave();
                ReplyOk(cfg.AdvertisePrefix.Length > 0 ? $"[RynthAi] Trade Shop adverts start with \"{cfg.AdvertisePrefix}\"." : "[RynthAi] Trade Shop advert prefix cleared.");
                return;
            default:
                ReplyFail("[RynthAi] Usage: /ra tradeshop advertise on|off|now|channel <name>|every <minutes>|prefix <text>");
                return;
        }
    }

    private void ListTradeShop(TradeShopSettings cfg)
    {
        List<TradeShopListing> rows;
        lock (cfg.Sync) rows = new List<TradeShopListing>(cfg.Listings ?? new List<TradeShopListing>());
        if (rows.Count == 0) { ChatLine("[RynthAi] Trade Shop: nothing listed. /ra tradeshop add <price> lists the selected item."); return; }
        ChatLine($"[RynthAi] Trade Shop ({rows.Count} listed):");
        for (int i = 0; i < rows.Count; i++)
        {
            TradeShopListing l = rows[i];
            ChatLine($"[RynthAi]   #{i + 1} {l.Label()} — {cfg.Rates.FormatAlternatives(l.PricePyreals)}{(l.Enabled ? "" : " [not for sale]")}");
        }
    }

    /// <summary>"/ra tradeshop add &lt;price&gt; [name]": the selected item (Mode 1), or a typed listing (a name given).</summary>
    private void AddTradeShopListing(TradeShopSettings cfg, string rest)
    {
        string[] bits = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length == 0 || !TryParsePyreals(bits[0], out long price) || price <= 0)
        {
            ReplyFail("[RynthAi] Usage: /ra tradeshop add <price in pyreals> [name]  (no name: the item selected in your inventory)");
            return;
        }
        string name = bits.Length > 1 ? bits[1].Trim() : "";
        if (name.Length > 0)
        {
            lock (cfg.Sync) (cfg.Listings ??= new List<TradeShopListing>()).Add(new TradeShopListing { Name = name, PricePyreals = price, Enabled = true });
            ScheduleDashSave();
            ReplyOk($"[RynthAi] Trade Shop: listed {name} at {TradeShopRates.N(price)} py (a text listing; Mode 1 sells only items picked from your inventory).");
            return;
        }
        // The packs are read on the plugin tick; the answer comes from there.
        Reply.Answered();
        QueueDashAction(() =>
        {
            string? fail = AddSelectedTradeShopItem(cfg, price, out string added);
            ChatLine("[RynthAi] Trade Shop: " + (fail ?? added));
        });
    }

    /// <summary>Lists the selected inventory item at <paramref name="price"/>; null when done, else why not.</summary>
    private string? AddSelectedTradeShopItem(TradeShopSettings cfg, long price, out string added)
    {
        added = "";
        if (!Host.HasGetSelectedItemId) return "this RynthCore can't read the selected item.";
        uint sel = Host.GetSelectedItemId();
        if (sel == 0) return "select an item in your inventory first.";
        if (!Host.HasGetObjectWcid || !Host.TryGetObjectWcid(sel, out uint wcid) || wcid == 0) return "the selected item can't be read yet; try again.";
        WorldObject? wo = _objectCache?[unchecked((int)sel)];
        bool mine = _objectCache?.GetDirectInventory(forceRefresh: true).Any(o => o != null && unchecked((uint)o.Id) == sel) == true;
        if (wo == null || !mine) return "the selected item isn't in your packs.";
        if (wo.WieldedLocation > 0 || wo.Values(LongValueKey.EquippedSlots, 0) > 0) return "that item is equipped; unequip it to sell it.";
        int stack = Host.TryGetObjectIntProperty(sel, 12, out int s) && s > 0 ? s : 0;
        string name = string.IsNullOrWhiteSpace(wo.Name) ? (Host.TryGetObjectName(sel, out string n) ? n : "") : wo.Name;
        if (string.IsNullOrWhiteSpace(name)) return "the selected item has no name yet; try again.";
        var row = new TradeShopListing { Name = name.Trim(), PricePyreals = price, ObjectId = sel, Wcid = wcid, Stack = stack > 1 ? stack : 0, Enabled = true };
        lock (cfg.Sync)
        {
            cfg.Listings ??= new List<TradeShopListing>();
            if (cfg.Listings.Any(l => l != null && l.ObjectId == sel)) return $"{row.Label()} is already listed.";
            cfg.Listings.Add(row);
        }
        ScheduleDashSave();
        added = $"listed {row.Label()} at {cfg.Rates.FormatAlternatives(price)}.";
        return null;
    }

    private void RemoveTradeShopListing(TradeShopSettings cfg, string rest)
    {
        if (!int.TryParse(rest.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
        {
            ReplyFail("[RynthAi] Usage: /ra tradeshop remove <#> (the number in /ra tradeshop list)");
            return;
        }
        TradeShopListing? gone = null;
        lock (cfg.Sync)
        {
            if (cfg.Listings != null && n <= cfg.Listings.Count) { gone = cfg.Listings[n - 1]; cfg.Listings.RemoveAt(n - 1); }
        }
        if (gone == null) { ReplyFail($"[RynthAi] Trade Shop: there is no #{n} (/ra tradeshop list)."); return; }
        ScheduleDashSave();
        ReplyOk($"[RynthAi] Trade Shop: removed #{n} {gone.Label()}.");
    }

    private void PriceTradeShopListing(TradeShopSettings cfg, string rest)
    {
        string[] bits = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length != 2 || !int.TryParse(bits[0].TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1
            || !TryParsePyreals(bits[1], out long price) || price <= 0)
        {
            ReplyFail("[RynthAi] Usage: /ra tradeshop price <#> <pyreals>");
            return;
        }
        TradeShopListing? row = null;
        lock (cfg.Sync)
        {
            if (cfg.Listings != null && n <= cfg.Listings.Count) { row = cfg.Listings[n - 1]; row.PricePyreals = price; }
        }
        if (row == null) { ReplyFail($"[RynthAi] Trade Shop: there is no #{n} (/ra tradeshop list)."); return; }
        ScheduleDashSave();
        ReplyOk($"[RynthAi] Trade Shop: #{n} {row.Label()} now {cfg.Rates.FormatAlternatives(price)}.");
    }

    private static bool TryParsePyreals(string text, out long py)
        => long.TryParse((text ?? "").Replace(",", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out py);

    private static bool? OnOff(string text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "yes" => true,
        "off" or "0" or "false" or "no" => false,
        _ => null,
    };

    // ── Window ───────────────────────────────────────────────────────────

    /// <summary>The window goes in the sections manifest on an engine with plugin windows (v89).</summary>
    private bool TradeShopWindowWanted => Host.HasUiTheme && Host.HasSetPanelOpen;

    /// <summary>" (window open)", or why it can't open.</summary>
    private string TradeShopWindowNote(RynthPanelOpen mode)
    {
        if (!Host.HasSetPanelOpen) return " (the window needs RynthCore with plugin windows; /ra tradeshop help for the commands)";
        int open = Host.SetPanelOpen(TradeShopTitle, mode);
        return open < 0 ? " (the window isn't there yet: RynthCore adds it a few seconds after login)"
             : open == 1 ? " (Trade Shop window open)" : " (Trade Shop window closed)";
    }

    /// <summary>The RynthAi window's ⋯ > Trade shop: the window open or closed. Any thread.</summary>
    internal void ToggleTradeShopWindow()
    {
        if (Host.HasSetPanelOpen) Host.SetPanelOpen(TradeShopTitle, RynthPanelOpen.Toggle);
    }

    /// <summary>RynthPluginRenderEmbed "window:tradeshop": 1 drawn, 0 not ready, -2 failed (three draws in a row threw).</summary>
    internal int RenderTradeShop(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || Host.ImGuiContext == IntPtr.Zero) return 0;
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _tradeShopWindow ??= new TradeShopWindow(() => TradeShopCfg, () => _tradeShop,
                price => QueueDashAction(() =>
                {
                    TradeShopSettings? cfg = TradeShopCfg;
                    if (cfg == null) return;
                    string? fail = AddSelectedTradeShopItem(cfg, price, out string added);
                    ChatLine("[RynthAi] Trade Shop: " + (fail ?? added));
                }),
                () => QueueDashAction(ScheduleDashSave));
            want = _tradeShopWindow.Draw();
            _tradeShopFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderTradeShop");
            return ++_tradeShopFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }
}
