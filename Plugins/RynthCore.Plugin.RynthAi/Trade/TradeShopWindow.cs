// The Trade Shop window: the shop's switch, the sale waiting for Accept sale, the catalog and its
// prices, what payment it takes, advertising and the last sales.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here. RynthCore hosts it
// as a plugin window (the sections manifest's "tradeshop" window, plugin API v89) with no bar button:
// the RynthAi window's options menu (Trade shop) and /ra tradeshop open it.
//
// AC's render thread. Settings edits go straight in (the catalog under its lock); anything that
// trades, tells or prints is queued for the tick through the manager or the callbacks.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Trade;

internal sealed class TradeShopWindow
{
    private readonly Func<TradeShopSettings?> _settings;
    private readonly Func<TradeShopManager?> _shop;
    private readonly Action<long> _addSelected;     // queued: the selected item at this price
    private readonly Action _changed;               // a setting changed: save the profile

    private int _newPrice = 100_000;
    private string _newName = "";
    private string _newDesc = "";
    private string _newHeldBy = "";

    private const string HelpText =
        "A shop run from tells. Buyers tell you !list (prices), !rates, !price <#slot|name> and !buy <#slot|name>.\n\n" +
        "Mode 1 (items in this character's packs): after !buy, the buyer opens a trade with you, the shop puts the item " +
        "in the window and checks their payment. Mode 2 (an item another character holds): the shop checks the payment " +
        "and you accept by hand, then hand the item over.\n\n" +
        "Payment counts by item type only: pyreals, trade notes at face value, and single-use Legendary Keys at your rate. " +
        "Anything else in the window is worth 0, and an item the game hasn't shown yet is never counted.\n\n" +
        "Confirm each sale (on by default): the shop stops before accepting and asks you. Off: it accepts a checked " +
        "Mode 1 sale itself. Either way it checks both sides again the moment it accepts, and withdraws its accept " +
        "if the trade changes.\n\n" +
        "It never accepts a trade nobody asked to !buy in, is off at every login, and pauses in combat, while the macro " +
        "or a hunt runs, and while AutoTrade is on. Sales go to chat, the log and TradeShopSales.log.\n\n" +
        "/ra tradeshop on | off | status | list | add <price> [name] | remove <#> | price <#> <py> | accept | decline | " +
        "mode1 | mode2 | confirm on|off | advertise ... | help";

    public TradeShopWindow(Func<TradeShopSettings?> settings, Func<TradeShopManager?> shop, Action<long> addSelected, Action changed)
    {
        _settings = settings;
        _shop = shop;
        _addSelected = addSelected;
        _changed = changed;
    }

    private float _k = 1f;
    private float Px(float v) => v * _k;

    /// <summary>Draws the window body and returns the size it would like. Render thread.</summary>
    public Vector2 Draw()
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        var want = new Vector2(Px(560), Px(520));
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        if (size.X < 60 || size.Y < 40) return want;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        TradeShopSettings? cfg = _settings();
        TradeShopManager? shop = _shop();
        using (PushFont(RynthUiFont.Ui11))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            ImGui.PushStyleColor(ImGuiCol.Text, Text);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
            ImGui.PushStyleColor(ImGuiCol.Button, ButtonFill);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, 0u);
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGui.BeginChild("##ts_body", size - new Vector2(12, 12));
            if (cfg == null || shop == null) Label("The Trade Shop is ready once you are in the world.", Mute);
            else DrawBody(cfg, shop);
            ImGui.EndChild();
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar();
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return want;
    }

    private void DrawBody(TradeShopSettings cfg, TradeShopManager shop)
    {
        // ── On / off, status, help ──
        bool on = cfg.Enabled;
        if (ImGui.Checkbox("Shop on##ts_on", ref on)) shop.SetEnabled(on);
        SafeImGui.SetItemTooltip("Off at every login. Turning it on says in chat what it does.");
        ImGui.SameLine();
        Label(shop.Status, Mute);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - Px(22));
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34);
            ImGui.TextUnformatted(HelpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        // ── The sale waiting for the player ──
        TradeShopPrompt? p = shop.Prompt;
        if (p != null)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, AccentFill);
            ImGui.BeginChild("##ts_prompt", new Vector2(-1, Px(96)), ImGuiChildFlags.Borders);
            Label("Sale ready" + (p.Mode == TradeShopMode.Mode2Text ? " (Mode 2: you hand the item over after)" : ""), Accent);
            Label($"Buyer: {p.Buyer}", Text);
            Label($"Item: {p.Item}    Price: {TradeShopRates.N(p.PricePyreals)} py ({p.Price})", Text);
            ImGui.PushTextWrapPos(0);
            Label($"Paid: {p.Paid}", Green);
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Accept sale##ts_accept")) shop.RequestAccept();
            SafeImGui.SetItemTooltip("Checks both sides of the trade again, then accepts.");
            ImGui.SameLine();
            if (ImGui.Button("Decline##ts_decline")) shop.RequestDecline();
            SafeImGui.SetItemTooltip("Calls the sale off and closes the trade window.");
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        // ── Mode and Confirm each sale ──
        ImGui.Spacing();
        int mode = (int)cfg.Mode;
        bool modeChanged = ImGui.RadioButton("Mode 1: items in my packs##ts_m1", ref mode, 0);
        ImGui.SameLine();
        modeChanged |= ImGui.RadioButton("Mode 2: text listings (I accept by hand)##ts_m2", ref mode, 1);
        if (modeChanged) { cfg.Mode = (TradeShopMode)mode; _changed(); }
        bool confirm = cfg.ConfirmEachSale;
        if (ImGui.Checkbox("Confirm each sale##ts_confirm", ref confirm)) { shop.SetConfirmEachSale(confirm); _changed(); }
        SafeImGui.SetItemTooltip("On: the shop asks you before every accept (Accept sale / Decline here).\nOff: it accepts a checked Mode 1 sale itself.");
        if (!cfg.ConfirmEachSale)
        {
            ImGui.SameLine();
            Label(cfg.Mode == TradeShopMode.Mode1Stock ? "Warning: Mode 1 sales are accepted without asking you." : "(Mode 2 always asks.)", Red);
        }

        // ── Catalog ──
        ImGui.Spacing();
        ImGui.Separator();
        Label("For sale", Accent);
        DrawCatalog(cfg);

        // ── Payment ──
        ImGui.Spacing();
        ImGui.Separator();
        Label("Payment the shop takes", Accent);
        TradeShopRates r = cfg.Rates ??= new TradeShopRates();
        bool changed = ImGui.Checkbox("Pyreals##ts_py", ref r.AcceptPyreals);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Trade notes##ts_notes", ref r.AcceptTradeNotes);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Legendary Keys (single-use)##ts_lk", ref r.AcceptLegendaryKeys);
        if (r.AcceptLegendaryKeys)
        {
            int lk = (int)Math.Clamp(r.PyrealsPerLegendaryKey, 0, int.MaxValue);
            ImGui.SetNextItemWidth(Px(120));
            if (ImGui.InputInt("pyreals per key##ts_lkrate", ref lk, 0, 0)) { r.PyrealsPerLegendaryKey = Math.Max(0, lk); changed = true; }
        }
        if (changed) _changed();
        ImGui.PushTextWrapPos(0);
        Label(r.RatesHelpLine() + " Anything else is worth 0.", Mute);
        ImGui.PopTextWrapPos();

        // ── Advertising ──
        ImGui.Spacing();
        ImGui.Separator();
        Label("Advertising", Accent);
        DrawAdvertise(cfg, shop);

        // ── Last sales ──
        List<string> sales = shop.RecentSales();
        if (sales.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            Label($"Last sales ({sales.Count})", Accent);
            ImGui.PushTextWrapPos(0);
            for (int i = sales.Count - 1; i >= 0; i--) Label(sales[i], Mute);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawCatalog(TradeShopSettings cfg)
    {
        TradeShopRates rates = cfg.Rates ?? new TradeShopRates();
        int removeAt = -1;
        bool changed = false;
        lock (cfg.Sync)
        {
            List<TradeShopListing> list = cfg.Listings ??= new List<TradeShopListing>();
            if (list.Count == 0)
                Label(cfg.Mode == TradeShopMode.Mode1Stock
                    ? "Nothing listed. Select an item in your inventory, set a price and press Add selected item."
                    : "Nothing listed. Type a name, a price and what the buyer is told, then press Add listing.", Mute);
            else if (ImGui.BeginTable("##ts_cat", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, Px(26));
                ImGui.TableSetupColumn("#  Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Price (py)", ImGuiTableColumnFlags.WidthFixed, Px(100));
                ImGui.TableSetupColumn("Buyer pays", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Px(46));
                ImGui.TableHeadersRow();
                for (int i = 0; i < list.Count; i++)
                {
                    TradeShopListing? row = list[i];
                    if (row == null) continue;
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    changed |= ImGui.Checkbox("##en", ref row.Enabled);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"#{i + 1}  {row.Label()}");
                    if (row.HeldBy.Length > 0 || row.Description.Length > 0)
                        SafeImGui.SetItemTooltip((row.HeldBy.Length > 0 ? "Held by " + row.HeldBy + "\n" : "") + row.Description);
                    ImGui.TableNextColumn();
                    int price = (int)Math.Clamp(row.PricePyreals, 0, int.MaxValue);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##price", ref price, 0, 0)) { row.PricePyreals = Math.Max(0, price); changed = true; }
                    ImGui.TableNextColumn();
                    SafeImGui.TextDisabled(rates.FormatAlternatives(row.PricePyreals));
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton("Remove")) removeAt = i;
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            if (removeAt >= 0 && removeAt < list.Count) { list.RemoveAt(removeAt); changed = true; }
        }
        if (changed) _changed();

        ImGui.SetNextItemWidth(Px(110));
        ImGui.InputInt("price (py)##ts_newprice", ref _newPrice, 0, 0);
        if (_newPrice < 1) _newPrice = 1;
        ImGui.SameLine();
        if (cfg.Mode == TradeShopMode.Mode1Stock)
        {
            if (ImGui.Button("Add selected item##ts_addsel")) _addSelected(_newPrice);
            SafeImGui.SetItemTooltip("Lists the item selected in your inventory at this price (/ra tradeshop add <price>).");
            return;
        }
        ImGui.SetNextItemWidth(Px(180));
        ImGui.InputText("name##ts_newname", ref _newName, 96);
        ImGui.SetNextItemWidth(Px(300));
        ImGui.InputText("told after paying##ts_newdesc", ref _newDesc, 160);
        ImGui.SetNextItemWidth(Px(140));
        ImGui.InputText("held by (you see it)##ts_newheld", ref _newHeldBy, 48);
        ImGui.SameLine();
        if (ImGui.Button("Add listing##ts_addtext") && _newName.Trim().Length > 0)
        {
            lock (cfg.Sync)
                (cfg.Listings ??= new List<TradeShopListing>()).Add(new TradeShopListing
                {
                    Name = _newName.Trim(), Description = _newDesc.Trim(), HeldBy = _newHeldBy.Trim(), PricePyreals = _newPrice, Enabled = true,
                });
            _newName = _newDesc = _newHeldBy = "";
            _changed();
        }
    }

    private void DrawAdvertise(TradeShopSettings cfg, TradeShopManager shop)
    {
        bool changed = ImGui.Checkbox("Advertise the price sheet##ts_ad", ref cfg.AdvertiseEnabled);
        SafeImGui.SetItemTooltip("Off by default. Posts one line with your items on a timer, only while the shop is on.");
        ImGui.SameLine();
        TradeShopAdvertiseChannel[] all = TradeShopAdvertise.All;
        int at = Math.Max(0, Array.IndexOf(all, cfg.AdvertiseChannel));
        ImGui.SetNextItemWidth(Px(130));
        if (ImGui.BeginCombo("##ts_adch", TradeShopAdvertise.DisplayName(all[at])))
        {
            for (int i = 0; i < all.Length; i++)
                if (ImGui.Selectable(TradeShopAdvertise.DisplayName(all[i]), i == at)) { cfg.AdvertiseChannel = all[i]; changed = true; }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        int minutes = TradeShopSettings.ClampAdvertiseSec(cfg.AdvertiseIntervalSec) / 60;
        ImGui.SetNextItemWidth(Px(60));
        if (ImGui.InputInt("min between##ts_adevery", ref minutes, 0, 0))
        {
            cfg.AdvertiseIntervalSec = TradeShopSettings.ClampAdvertiseSec(minutes * 60);
            changed = true;
        }
        SafeImGui.SetItemTooltip($"{TradeShopSettings.MinAdvertiseSec / 60} to {TradeShopSettings.MaxAdvertiseSec / 60} minutes.");
        ImGui.SetNextItemWidth(Px(200));
        if (ImGui.InputText("starts with##ts_adprefix", ref cfg.AdvertisePrefix, 48)) changed = true;
        ImGui.SameLine();
        if (ImGui.Button("Advertise now##ts_adnow")) shop.RequestAdvertise();
        if (changed) _changed();
    }
}
