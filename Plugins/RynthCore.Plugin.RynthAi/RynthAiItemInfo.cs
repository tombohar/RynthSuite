using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.ItemInfo;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// /ra iteminfo — Mag-style item info (ratings, damage, spells, slayer, imbues …) as one line or
/// as pet-roster style lines, printed on demand, on select or on a left/right mouse click;
/// ported from UtilityBelt ub-IT / Mag-Tools ItemInfo. Partial class of RynthAiPlugin.
///
///   /ra iteminfo                      describe the selected item (IDs it first when needed)
///   /ra iteminfo &lt;item name&gt;          describe an inventory item by name (exact, else partial)
///   /ra iteminfo me                   your own ratings (also on selecting / clicking yourself)
///   /ra iteminfo on|off               describe every item you select (yourself: your ratings)
///   /ra iteminfo settings             open/close the Item Info settings window
///   /ra iteminfo fields               list fields and ratings with their on/off state
///   /ra iteminfo field &lt;name&gt; on|off  show/hide one field (ratings, slayer, spells, …)
///   /ra iteminfo rating &lt;tag&gt; on|off  show/hide one rating (D, CD, CDR, …)
///   /ra iteminfo value on|off         append value + burden
///   /ra iteminfo verbose on|off       list every spell instead of Mag's filter
///   /ra iteminfo click left|right|off print the selected item after a mouse click
///   /ra iteminfo layout pet|line      pet-roster style lines or the Mag one-liner
///   /ra iteminfo loot on|off          describe what the looter takes in its "Looted" line
///   /ra iteminfo loot salvage on|off  salvage too (material and workmanship only)
///   /ra iteminfo color &lt;name&gt;|#RRGGBB the lines' colour in RynthCore's chat, whatever their chat type ("default": the type's)
///   /ra iteminfo reset                restore defaults
/// Every option is also editable in the "RynthAi Item Info" window (LegacyItemInfoUi).
/// </summary>
public sealed partial class RynthAiPlugin
{
    // One pending describe at a time: the newest request replaces the previous one.
    private int  _itemInfoPendingId;
    private long _itemInfoPendingSince;

    // Automatic triggers (select + click) can both fire for one click; the same item inside
    // this window prints once.
    private const long AutoDedupeMs = 1500;
    private int  _itemInfoLastAutoId;
    private long _itemInfoLastAutoAt;

    // Click trigger. A press counts as a click only if it started outside every overlay window,
    // was released within ClickMaxHoldMs and moved at most ClickMaxMovePx — so item drags and
    // right-button mouse-look never print. The client updates the selection after it handles
    // the click, so the selection is read ClickSettleMs after the release, on the tick thread.
    private const long  ClickMaxHoldMs = 600;
    private const float ClickMaxMovePx = 6f;
    private const long  ClickSettleMs  = 150;
    private bool    _iiClickPrevDown;
    private bool    _iiClickArmed;
    private long    _iiClickPressAt;
    private Vector2 _iiClickPressPos;
    private long    _iiClickResolveAt; // TickCount64 to read the selection at; 0 = none (Interlocked)

    /// <summary>Character settings when loaded, else null (commands then use defaults / refuse edits).</summary>
    private MagItemInfoSettings? ItemInfoSettings => _dashboard?.Settings.ItemInfoSettings;

    private void HandleItemInfoCommand(string[] parts)
    {
        var s = ItemInfoSettings;
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : string.Empty;
        string arg3 = parts.Length >= 4 ? parts[3] : string.Empty;
        string arg4 = parts.Length >= 5 ? parts[4] : string.Empty;

        if (sub.Length == 0)
        {
            PrintSelectedItemInfo();
            return;
        }
        if (s == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return; }

        switch (sub)
        {
            case "me":
            case "self":
            case "ratings":
                QueueSelfRatings(auto: false);
                return;

            case "settings":
            case "gui":
            case "window":
                s.ShowWindow = !s.ShowWindow;
                ReplyOk($"[RynthAi] Item info window {(s.ShowWindow ? "opened" : "closed")}.");
                return;

            case "value":
                if (ParseOnOff(arg3) is bool v) s.ShowValueAndBurden = v;
                ReplyOk($"[RynthAi] Item info value/burden: {OnOff(s.ShowValueAndBurden)}");
                return;

            case "verbose":
                if (ParseOnOff(arg3) is bool all)
                    s.SpellMode = all ? MagItemInfoSettings.SpellModeAll : MagItemInfoSettings.SpellModeMag;
                ReplyOk($"[RynthAi] Item info all spells: {OnOff(s.SpellMode == MagItemInfoSettings.SpellModeAll)}");
                return;

            case "field":
                if (!MagItemInfoCatalog.TryFindField(arg3, out MagItemInfoField field))
                {
                    ReplyFail($"[RynthAi] Unknown field '{arg3}'. /ra iteminfo fields lists them.");
                    return;
                }
                if (ParseOnOff(arg4) is bool fieldOn) s.SetHidden(field, !fieldOn);
                ReplyOk($"[RynthAi] Item info field {field}: {OnOff(!s.IsHidden(field))}");
                return;

            case "rating":
                int r = MagItemInfoCatalog.FindRating(arg3);
                if (r < 0)
                {
                    ReplyFail($"[RynthAi] Unknown rating '{arg3}'. /ra iteminfo fields lists them.");
                    return;
                }
                if (ParseOnOff(arg4) is bool ratingOn) s.SetRatingHidden(r, !ratingOn);
                var rating = MagItemInfoCatalog.Ratings[r];
                ReplyOk($"[RynthAi] Item info rating {rating.Tag} ({rating.Name}): {OnOff(!s.IsRatingHidden(r))}");
                return;

            case "fields":
                ListItemInfoFields(s);
                return;

            case "click":
                switch (arg3.ToLowerInvariant())
                {
                    case "left":  case "l":   s.ClickTrigger = MagItemInfoSettings.ClickLeft;  break;
                    case "right": case "r":   s.ClickTrigger = MagItemInfoSettings.ClickRight; break;
                    case "off":   case "none": s.ClickTrigger = MagItemInfoSettings.ClickOff;  break;
                    case "": break;
                    default:
                        ReplyFail("[RynthAi] Usage: /ra iteminfo click left|right|off");
                        return;
                }
                ReplyOk($"[RynthAi] Item info on click: {MagItemInfoCatalog.ClickTriggers[s.ClickTrigger]}");
                return;

            case "layout":
                switch (arg3.ToLowerInvariant())
                {
                    case "pet": case "pets": case "lines": s.Layout = MagItemInfoSettings.LayoutPetLines; break;
                    case "line": case "mag": case "one":   s.Layout = MagItemInfoSettings.LayoutOneLine;  break;
                    case "": break;
                    default:
                        ReplyFail("[RynthAi] Usage: /ra iteminfo layout pet|line");
                        return;
                }
                ReplyOk($"[RynthAi] Item info layout: {MagItemInfoCatalog.Layouts[s.Layout]}");
                return;

            case "loot":
                if (arg3.Equals("salvage", StringComparison.OrdinalIgnoreCase))
                {
                    if (ParseOnOff(arg4) is bool salvageOn) s.LootChatSalvage = salvageOn;
                    else if (arg4.Length > 0) { ReplyFail("[RynthAi] Usage: /ra iteminfo loot salvage on|off"); return; }
                    ReplyOk($"[RynthAi] Item info on loot, salvage: {OnOff(s.LootChatSalvage)}");
                    return;
                }
                if (ParseOnOff(arg3) is bool lootOn) s.LootChat = lootOn;
                else if (arg3.Length > 0) { ReplyFail("[RynthAi] Usage: /ra iteminfo loot on|off, or /ra iteminfo loot salvage on|off"); return; }
                ReplyOk($"[RynthAi] Item info on loot: {OnOff(s.LootChat)} (salvage {OnOff(s.LootChatSalvage)})");
                return;

            case "color":
            case "colour":
                if (arg3.Length > 0)
                {
                    string? color = MagItemInfoCatalog.ParseTextColor(arg3);
                    if (color == null)
                    {
                        ReplyFail("[RynthAi] Usage: /ra iteminfo color " + TextColorWords());
                        return;
                    }
                    s.TextColor = color;
                }
                ReplyOk(s.TextColor.Length == 0
                    ? "[RynthAi] Item info text colour: the chat type's own colour. Others: /ra iteminfo color " + TextColorWords()
                    : $"[RynthAi] Item info text colour: {MagItemInfoCatalog.TextColorLabel(s.TextColor)} in RynthCore's chat (same tab; AC's chat window shows the chat type's colour)");
                return;

            case "reset":
                s.ResetToDefaults();
                ReplyOk("[RynthAi] Item info settings reset to defaults.");
                return;
        }

        if (ParseOnOff(sub) is bool onSelect)
        {
            s.OnSelect = onSelect;
            ReplyOk($"[RynthAi] Item info on select: {OnOff(onSelect)}");
            return;
        }
        if (sub is not ("help" or "?"))
        {
            // Anything else is an item name: the inventory item called that, else one containing it.
            string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
            uint found = FindInventoryItemByName(name);
            if (found != 0) { QueueItemInfo(unchecked((int)found), requestId: true); return; }
            ReplyFail($"[RynthAi] Item info: no \"{name}\" in your inventory.");
        }
        ReplyFail("[RynthAi] Usage: /ra iteminfo [<item name>|me|on|off|settings|fields|field <name> on|off|rating <tag> on|off|value on|off|verbose on|off|click left|right|off|layout pet|line|loot on|off|color <name>|#RRGGBB|reset]");
    }

    /// <summary>"default|allegiance|...|#RRGGBB": what /ra iteminfo color takes.</summary>
    private static string TextColorWords()
    {
        var sb = new StringBuilder();
        foreach (var c in MagItemInfoCatalog.TextColors) sb.Append(c.Word).Append('|');
        return sb.Append("#RRGGBB").ToString();
    }

    /// <summary>
    /// An inventory item (carried or worn) whose name is <paramref name="name"/>, ignoring case;
    /// else the first whose name contains it. 0 when none.
    /// </summary>
    private uint FindInventoryItemByName(string name)
    {
        if (_objectCache == null || name.Length == 0) return 0;
        uint partial = 0;
        foreach (WorldObject wo in _objectCache.GetInventory())
        {
            if (wo.Name.Length == 0) continue;
            if (wo.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return unchecked((uint)wo.Id);
            if (partial == 0 && wo.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) partial = unchecked((uint)wo.Id);
        }
        return partial;
    }

    /// <summary>
    /// " | Loot: [Keep] (A) MajorSkill Set": the looter's own verdict on the item, as a preview
    /// (nothing recorded), so it says exactly what [LootEval] would. Item info replaces the
    /// separate verdict line, so this is the only place it shows. "" when there's no loot
    /// profile, or /ra lootcheck off.
    /// </summary>
    private string LootVerdictSuffix(int itemId)
    {
        try
        {
            LegacyUi.LegacyUiSettings? settings = _dashboard?.Settings;
            WorldObject? wo = _objectCache?[itemId];
            bool rares = settings != null && Loot.LootModes.IsRares(settings.LootMode);
            if (!_lootInspectMode || settings == null || wo == null || (!rares && string.IsNullOrEmpty(settings.CurrentLootPath))) return "";
            bool taken = ClassifyLootItem(wo, settings, preview: true, out string action, out _, out string rule, out string builtIn);
            if (!taken && rares) return " | Loot: leave" + (builtIn.Length > 0 ? " - " + builtIn : "");
            if (!taken)
                // A rule can match and still not take it (its Keep # reached, a Read rule on a non-scroll).
                return rule.Length > 0 ? $" | Loot: leave ({rule} matched, not taken)" : " | Loot: no rule matched";
            if (builtIn.Length > 0) return $" | Loot: [{action}] built-in - {builtIn}";
            return $" | Loot: [{action}] {(rule.Length > 0 ? rule : "(unnamed rule)")}";
        }
        catch (Exception ex)
        {
            ItemInfoLog($"0x{(uint)itemId:X8}: loot verdict failed: {ex.GetType().Name}: {ex.Message}");
            return "";
        }
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    /// <summary>"/ra iteminfo fields" — current state of every field and rating.</summary>
    private void ListItemInfoFields(MagItemInfoSettings s)
    {
        var sb = new StringBuilder("[RynthAi] Fields: ");
        bool first = true;
        foreach (var f in MagItemInfoCatalog.Fields)
        {
            if (!first) sb.Append(", ");
            sb.Append(f.Field).Append(s.IsHidden(f.Field) ? " off" : " on");
            first = false;
        }
        ChatLine(sb.ToString());

        sb.Clear().Append("[RynthAi] Ratings: ");
        var ratings = MagItemInfoCatalog.Ratings;
        for (int i = 0; i < ratings.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ratings[i].Tag).Append(s.IsRatingHidden(i) ? " off" : " on");
        }
        ChatLine(sb.ToString());
    }

    /// <summary>/ra iteminfo and the window's "Print to chat" button.</summary>
    private void PrintSelectedItemInfo()
    {
        if (!Host.HasGetSelectedItemId) { ReplyFail("[RynthAi] Host does not expose the selected item."); return; }
        uint selected = Host.GetSelectedItemId();
        if (selected == 0) { ReplyFail("[RynthAi] No item selected. Click an item first."); return; }
        if (IsSelf(selected)) { QueueSelfRatings(auto: false); return; }
        QueueItemInfo(unchecked((int)selected), requestId: true);
    }

    /// <summary>On-select class filters from the Item Info window.</summary>
    private static bool ItemInfoWantsClass(MagItemInfoSettings s, AcObjectClass cls) => cls switch
    {
        AcObjectClass.MeleeWeapon or AcObjectClass.MissileWeapon or AcObjectClass.WandStaffOrb => s.OnSelectWeapons,
        AcObjectClass.Armor or AcObjectClass.Clothing => s.OnSelectArmor,
        AcObjectClass.Jewelry => s.OnSelectJewelry,
        _ => s.OnSelectOther,
    };

    /// <summary>
    /// Describe <paramref name="itemId"/> now if its appraisal is cached, otherwise (optionally)
    /// request an ID and print from <see cref="TickItemInfo"/> once it lands or times out.
    /// </summary>
    internal void QueueItemInfo(int itemId, bool requestId)
    {
        if (itemId == 0) return;
        uint uid = unchecked((uint)itemId);
        if (!Host.HasHasAppraisalData || Host.HasAppraisalData(uid))
        {
            _itemInfoPendingId = 0;
            PrintItemInfo(itemId, identified: true);
            return;
        }
        if (requestId && Host.HasRequestId) Host.RequestId(uid);
        ItemInfoLog($"0x{uid:X8}: waiting for ID data (requested={requestId && Host.HasRequestId})");
        Reply.Answered();   // a command's item info follows when the ID data arrives
        _itemInfoPendingId = itemId;
        _itemInfoPendingSince = Environment.TickCount64;
    }

    /// <summary>One "[ItemInfo]" line in the engine log and the daily RynthAi file per trigger decision.</summary>
    private static void ItemInfoLog(string message) => RynthLog.Write(LogCat.UI, "[ItemInfo] " + message);

    /// <summary>
    /// Select / click triggers: <see cref="QueueItemInfo"/> unless the same item was queued
    /// automatically within <see cref="AutoDedupeMs"/> (one click can raise both triggers).
    /// </summary>
    internal void QueueAutoItemInfo(int itemId, bool requestId)
    {
        long now = Environment.TickCount64;
        if (itemId == _itemInfoLastAutoId && now - _itemInfoLastAutoAt < AutoDedupeMs)
        {
            ItemInfoLog($"0x{(uint)itemId:X8}: already queued by the other trigger");
            return;
        }
        _itemInfoLastAutoId = itemId;
        _itemInfoLastAutoAt = now;
        QueueItemInfo(itemId, requestId);
    }

    /// <summary>
    /// Render-thread click watcher (OnRender / OnRenderOverlay, ImGui context current). The ImGui
    /// backend sees every mouse message on the game window, including the ones the game handles,
    /// so the raw button state is edge-detected here; the selection is resolved in
    /// <see cref="TickItemInfo"/>.
    /// </summary>
    private void DetectItemInfoClick()
    {
        int trigger = ItemInfoSettings?.ClickTrigger ?? MagItemInfoSettings.ClickOff;
        int button = trigger switch
        {
            MagItemInfoSettings.ClickLeft  => 0,
            MagItemInfoSettings.ClickRight => 1,
            _ => -1,
        };
        if (button < 0)
        {
            _iiClickPrevDown = false;
            _iiClickArmed = false;
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        bool down = io.MouseDown[button];
        long now = Environment.TickCount64;

        if (down && !_iiClickPrevDown)
        {
            // A press on an overlay window belongs to the overlay, not to an item in the game.
            _iiClickArmed = !io.WantCaptureMouse;
            _iiClickPressAt = now;
            _iiClickPressPos = io.MousePos;
        }
        else if (down && _iiClickArmed)
        {
            bool held  = now - _iiClickPressAt > ClickMaxHoldMs;
            bool moved = Vector2.DistanceSquared(io.MousePos, _iiClickPressPos) > ClickMaxMovePx * ClickMaxMovePx;
            if (held || moved) _iiClickArmed = false; // drag or mouse-look, not a click
        }
        else if (!down && _iiClickPrevDown && _iiClickArmed)
        {
            _iiClickArmed = false;
            Interlocked.Exchange(ref _iiClickResolveAt, now + ClickSettleMs);
        }
        _iiClickPrevDown = down;
    }

    /// <summary>Tick-thread half of the click trigger: describe whatever item the click selected.</summary>
    private void ResolveItemInfoClick()
    {
        long at = Interlocked.Read(ref _iiClickResolveAt);
        if (at == 0 || Environment.TickCount64 < at) return;
        if (Interlocked.CompareExchange(ref _iiClickResolveAt, 0, at) != at) return;

        var s = ItemInfoSettings;
        if (s == null || s.ClickTrigger == MagItemInfoSettings.ClickOff || !Host.HasGetSelectedItemId) return;
        string button = MagItemInfoCatalog.ClickTriggers[s.ClickTrigger];
        uint selected = Host.GetSelectedItemId();
        if (selected == 0) { ItemInfoLog($"{button}: nothing selected"); return; }
        if (IsSelf(selected))
        {
            ItemInfoLog($"{button}: 0x{selected:X8} (yourself): ratings queue");
            QueueSelfRatings(auto: true);
            return;
        }

        int id = unchecked((int)selected);
        WorldObject? obj = _objectCache?[id];
        if (obj == null) { ItemInfoLog($"{button}: 0x{selected:X8} not in the object cache"); return; }
        if (!IsLootableClass(obj.ObjectClass) || !ItemInfoWantsClass(s, obj.ObjectClass))
        {
            ItemInfoLog($"{button}: '{obj.Name}' ({obj.ObjectClass}) skipped by the class filters");
            return;
        }
        ItemInfoLog($"{button}: 0x{selected:X8} '{obj.Name}': click queue");
        QueueAutoItemInfo(id, requestId: true);
    }

    /// <summary>Plugin-tick poll for a pending describe (no appraisal-arrived event in the SDK).</summary>
    private void TickItemInfo()
    {
        ResolveItemInfoClick();
        TickSelfRatings();

        int id = _itemInfoPendingId;
        if (id == 0) return;
        bool ready = Host.HasAppraisalData(unchecked((uint)id));
        long timeoutMs = (long)((ItemInfoSettings?.IdTimeoutSec ?? 3f) * 1000f);
        bool timedOut = Environment.TickCount64 - _itemInfoPendingSince > timeoutMs;
        if (!ready && !timedOut) return;
        _itemInfoPendingId = 0;
        PrintItemInfo(id, identified: ready);
    }

    /// <summary>
    /// Builds the output lines (no prefix / indent) in the configured layout, or returns null
    /// with <paramref name="error"/> set. The one-line layout returns a single line.
    /// </summary>
    private List<string>? BuildItemInfoLines(int itemId, out string? error)
    {
        error = null;
        WorldObject? wo = _objectCache?[itemId];
        if (wo == null) { error = $"Item 0x{(uint)itemId:X8} is not in the object cache."; return null; }
        try
        {
            var s = ItemInfoSettings;
            MagItemDescription d = MagItemDescriber.Build(new CacheItemPropertySource(Host, wo), MagItemInfoOptions.From(s));
            return (s?.Layout ?? MagItemInfoSettings.LayoutPetLines) == MagItemInfoSettings.LayoutOneLine
                ? new List<string> { d.ToOneLine() }
                : d.ToPetLines();
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.General, ex, $"item info 0x{(uint)itemId:X8}");
            error = $"Item info failed for {wo.Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Chat output. Pet-style: line 1 carries the prefix in <see cref="MagItemInfoSettings.ChatType"/>,
    /// later lines are indented like the pet roster and use <see cref="MagItemInfoSettings.DetailChatType"/>.
    /// </summary>
    private void PrintItemInfo(int itemId, bool identified)
    {
        List<string>? lines = BuildItemInfoLines(itemId, out string? error);
        if (lines == null || lines.Count == 0)
        {
            ItemInfoLog($"0x{(uint)itemId:X8}: not printed: {error}");
            ChatLine("[RynthAi] " + error);
            return;
        }
        if (!identified) lines[0] += " (not identified — stats may be incomplete)";
        lines[0] += LootVerdictSuffix(itemId);
        ItemInfoLog($"0x{(uint)itemId:X8}: printed {lines.Count} line(s), identified={identified}: {lines[0]}");

        var s = ItemInfoSettings;
        string prefix = s?.Prefix ?? "[RynthAi Item] ";
        int headerType = s?.ChatType ?? 1;
        int detailType = s == null || s.DetailChatType == MagItemInfoSettings.SameAsHeader ? headerType : s.DetailChatType;

        WriteItemInfoLine(prefix + lines[0], headerType);
        for (int i = 1; i < lines.Count; i++)
            WriteItemInfoLine(prefix + "  " + lines[i], detailType);
    }

    /// <summary>The engine's name for a line's own colour in RynthCore's chat (engine UI/Data/ChatOwnColors.cs).</summary>
    internal const string ChatColorTarget = "RynthCore.Chat";

    // An engine from before 2026-11-05 answered "no such plugin" once: item info lines then go
    // out exactly as before, without asking again.
    private bool _infoColorUnsupported;

    /// <summary>
    /// One item info line, in <paramref name="chatType"/> (which picks its chat tab and AC's chat
    /// window). With Info text colour set (<see cref="MagItemInfoSettings.TextColor"/>) the engine
    /// is asked to draw the next line in that colour just before it is written: RynthCore's chat
    /// shows it so, in the same tab. Unset, or an older engine: the plain WriteToChat, as before.
    /// </summary>
    internal void WriteItemInfoLine(string text, int chatType)
    {
        string color = ItemInfoSettings?.TextColor ?? string.Empty;
        if (color.Length > 0 && text.Length > 0 && !_infoColorUnsupported && Host.HasWriteToChat && Host.HasSendPluginCommand
            && !Host.SendPluginCommand(ChatColorTarget, "nextcolor", color))
        {
            _infoColorUnsupported = true;
            ItemInfoLog("the engine has no line colours (before 2026-11-05): item info keeps its chat type's colour");
        }
        Host.WriteToChat(text, chatType);
    }

    /// <summary>Item Info window preview: the selected item's lines, or null when nothing is selected.</summary>
    private string? DescribeSelectedItemForPreview()
    {
        if (!Host.HasGetSelectedItemId) return null;
        uint selected = Host.GetSelectedItemId();
        if (selected == 0) return null;
        int id = unchecked((int)selected);
        if (IsSelf(selected))
        {
            // Cache only: the window redraws every frame, so it never asks for an identify.
            bool known = !Host.HasHasAppraisalData || Host.HasAppraisalData(selected);
            List<string> self = ItemInfo.SelfRatings.Build(_objectCache?[id]?.Name ?? string.Empty,
                                                          stype => ReadSelfInt(id, stype), known, oneLine: false);
            return string.Join("\n  ", self);
        }
        List<string>? lines = BuildItemInfoLines(id, out string? error);
        if (lines == null || lines.Count == 0) return error;
        if (Host.HasHasAppraisalData && !Host.HasAppraisalData(selected))
            lines[0] += " (not identified yet — Print to chat IDs it)";
        // The window adds the prefix to the first line; indent the rest like chat.
        return string.Join("\n  ", lines);
    }

    /// <summary>
    /// The "Looted" line for a pickup, described when the Item Info loot setting wants it
    /// (<see cref="LootChatLine"/>), with its chat type; null for the plain line. Corpse loot builds it
    /// when the pickup goes out (the item and its ID are surely there then), ground loot on arrival.
    /// </summary>
    private (string Line, int ChatType)? DescribeLootPickup(int itemId, string action, string name, string rule,
        bool isSalvage, bool stackable, string where = "")
    {
        MagItemInfoSettings? s = ItemInfoSettings;
        WorldObject? wo = _objectCache?[itemId];
        if (s == null || wo == null) return null;
        LootChatLine.Detail detail = LootChatLine.DetailFor(s, action, isSalvage, stackable, wo.ObjectClass);
        if (detail == LootChatLine.Detail.None) return null;
        try
        {
            bool identified = !Host.HasHasAppraisalData || Host.HasAppraisalData(unchecked((uint)itemId));
            string line = LootChatLine.Format(detail, s, new CacheItemPropertySource(Host, wo), action, name, rule, where, identified);
            return (line, LootChatLine.ChatTypeOf(s));
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.General, ex, $"loot line 0x{(uint)itemId:X8}");
            return null;
        }
    }

    /// <summary>Item Info window "Test" button: sample line in the chosen chat type (and the Info text colour).</summary>
    private void TestItemInfoChatType(int chatType)
    {
        string prefix = ItemInfoSettings?.Prefix ?? "[RynthAi Item] ";
        string color = ItemInfoSettings?.TextColor ?? string.Empty;
        WriteItemInfoLine($"{prefix}Item info chat test (type {chatType}{(color.Length > 0 ? ", colour " + color : "")})", chatType);
    }
}
