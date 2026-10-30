using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The rest of Mag-Tools' <c>/mt</c> verbs (2026-10-17; checked against Mag-Tools PluginCore and
/// ub-IT's MagToUbCommandTranslator): send (keys), jump / sjump, trade, vendor buy/sell lists,
/// autopack, dumpspells, click, get xy, movement, client, plus ub-IT's config and strafe.
/// Partial class of RynthAiPlugin; dispatched from <see cref="HandleMtCommand"/>.
/// </summary>
public sealed partial class RynthAiPlugin
{
    // /mt vendor addbuy/addsell lists, sent by "/mt vendor buy" / "sell" (Mag-Tools keeps them in Decal).
    private readonly List<(uint Id, string Name, int Amount)> _mtBuyList = new();
    private readonly List<(uint Id, string Name)> _mtSellList = new();

    // ── Safety gates (2026-10-19) ───────────────────────────────────────────────
    //
    // Two kinds of /mt verb can do real damage if a shared meta (or a stray chat line) drives them:
    //   * raw input into the game window (send, click, movement, strafe, client, the jump fallback):
    //     off unless the setting "Allow metas to send keystrokes (/mt send)" is on (default off);
    //   * acting on a vendor or a trade (vendor buy / sell, trade accept): only while a meta that is
    //     running executes the line. A typed line, a delayed line that did not come from a meta, or
    //     any other path is refused.

    // Nesting count of the meta chat-command handler (HandleMetaChatCommand) on the call stack.
    private int _metaCommandDepth;

    // Verbs already told "off" this session (one chat line and one log line each, not one per meta pulse).
    private readonly HashSet<string> _keystrokeRefusalSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while a running meta (macro on, Meta on) is executing the line being handled:
    /// inside one of its actions (MetaManager.IsExecutingAction) or a line replayed on its behalf.</summary>
    internal bool MetaCommandActive
        => (_metaCommandDepth > 0 || _metaManager?.IsExecutingAction == true)
           && _dashboard?.Settings is { IsMacroRunning: true, EnableMeta: true };

    /// <summary>Runs <paramref name="action"/> as a line a meta executes (see <see cref="MetaCommandActive"/>).</summary>
    private T RunAsMeta<T>(Func<T> action)
    {
        _metaCommandDepth++;
        try { return action(); }
        finally { _metaCommandDepth--; }
    }

    /// <summary>
    /// The setting "Allow metas to send keystrokes (/mt send)". False (and said once per verb) when
    /// it is off, which is the default: the verb then does nothing.
    /// </summary>
    private bool KeystrokesAllowed(string verb)
    {
        if (_dashboard?.Settings.AllowMetaKeystrokes == true) return true;
        string why = $"[RynthAi] {verb} ignored: it sends keys or clicks to the game window, and 'Allow metas to send keystrokes (/mt send)' is off (Settings, Advanced).";
        // Typed: said every time (a typed command always answers). A meta: said once per verb.
        if (!Reply.FromScript) ReplyFail(why);
        else if (_keystrokeRefusalSaid.Add(verb)) ReplyFail(why + " Said once.");
        return false;
    }

    /// <summary>
    /// True only when a running meta executes this line. Otherwise says why not and returns false,
    /// so <paramref name="verb"/> (an action on a vendor or a trade) is never run from a typed or
    /// unrelated line.
    /// </summary>
    private bool RequireRunningMeta(string verb)
    {
        if (MetaCommandActive) return true;
        ReplyFail($"[RynthAi] {verb} only runs from a meta that is running (macro and Meta on); refused.");
        return false;
    }

    /// <summary>The verbs this file handles, from <see cref="HandleMtCommand"/>'s default branch.</summary>
    private bool TryHandleMtExtra(string cmd, string[] parts, string fullCommand)
    {
        // jump, sjump, jumpw, sjumpwz ... (Mag-Tools matches "jump" anywhere after /mt).
        if (cmd.StartsWith("jump", StringComparison.Ordinal) || cmd.StartsWith("sjump", StringComparison.Ordinal))
            return HandleMtJump(cmd, parts);

        switch (cmd)
        {
            case "test":
                ChatLine("[RynthAi] /mt test: Mag-Tools' debug command - nothing to do.");
                return true;
            case "trade":      return HandleMtTrade(parts);
            case "vendor":     return HandleMtVendor(parts);
            case "autopack":
                // Mag-Tools packs by its own rules; RynthAi's equivalent is a cram + stack pass.
                _inventoryManager?.RunManual(cram: true, stack: true);
                ReplyOk("[RynthAi] Autopack: cramming items into side packs and stacking them.");
                return true;
            case "dumpspells": return HandleMtDumpSpells();
            case "click":      return HandleMtClick(parts);
            case "get":
                if (parts.Length >= 3 && parts[2].Equals("xy", StringComparison.OrdinalIgnoreCase)) return HandleMtCursor();
                ReplyFail("[RynthAi] Usage: /mt get xy");
                return true;
            case "cursor":     return HandleMtCursor();
            case "movement":   return HandleMtMovement(parts);
            case "strafe":     return HandleMtStrafe(parts);
            case "client":     return HandleMtClient(parts);
            case "config":
                // ub-IT: /mt config = /mt opt (list when bare).
                return parts.Length < 3 ? HandleMtOpt(new[] { "/mt", "opt", "list" }) : HandleMtOpt(parts);
            default:
                return false;
        }
    }

    // ── send: keys, as Mag-Tools ────────────────────────────────────────────────

    /// <summary>
    /// /mt send enter|pause|del|space|f4|f12|cntrl+&lt;letter&gt;|msg &lt;text&gt;: presses keys in the
    /// game window, as Mag-Tools. A line starting with "/" is still sent as a chat command (what
    /// this verb did before, which some metas use).
    /// </summary>
    private bool HandleMtSend(string[] parts, string fullCommand)
    {
        const string usage = "[RynthAi] Usage: /mt send enter|pause|del|space|f4|f12|cntrl+<letter>|msg <text>";
        if (parts.Length < 3) { ReplyFail(usage); return true; }
        string arg = parts[2].ToLowerInvariant();
        if (parts[2].StartsWith('/'))
        {
            // The command answers for itself.
            if (Host.HasInvokeChatParser) { Host.InvokeChatParser(string.Join(" ", parts, 2, parts.Length - 2)); Reply.Answered(); }
            else ReplyFail("[RynthAi] /mt send: the chat parser isn't available.");
            return true;
        }
        if (!KeystrokesAllowed("/mt send")) return true;
        if (!GameInput.Available) { ReplyFail("[RynthAi] /mt send: the game window wasn't found."); return true; }
        string what = string.Join(" ", parts, 2, Math.Min(parts.Length - 2, 2));
        Log($"[RynthAi] /mt send {what}: keys posted to the game window");
        string sent = $"[RynthAi] /mt send {what}: sent to the game window.";
        switch (arg)
        {
            case "enter": GameInput.SendEnter(); ReplyOk(sent); return true;
            case "pause": GameInput.SendPause(); ReplyOk(sent); return true;
            case "del":   GameInput.SendDel(); ReplyOk(sent); return true;
            case "space": GameInput.SendSpace(); ReplyOk(sent); return true;
            case "f4":    GameInput.SendF4(); ReplyOk(sent); return true;
            case "f12":   GameInput.SendF12(); ReplyOk(sent); return true;
            case "msg":
                int at = fullCommand.IndexOf(" msg ", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || at + 5 >= fullCommand.Length) { ReplyFail("[RynthAi] Usage: /mt send msg <text>"); return true; }
                GameInput.SendMsg(fullCommand[(at + 5)..]);
                ReplyOk("[RynthAi] /mt send msg: typed into the game window.");
                return true;
        }
        if (arg.StartsWith("cntrl+", StringComparison.Ordinal) && arg.Length >= 7)
        {
            GameInput.SendCntrl(arg[6]);
            ReplyOk(sent);
            return true;
        }
        ReplyFail(usage);
        return true;
    }

    // ── jump ─────────────────────────────────────────────────────────────────────

    /// <summary>/mt jump[w|z|x|c] [ms] and /mt sjump... (shift): RynthAi's jumper (the engine's own
    /// jump), else the keys as Mag-Tools.</summary>
    private bool HandleMtJump(string cmd, string[] parts)
    {
        bool shift = cmd.StartsWith("sjump", StringComparison.Ordinal);
        string suffix = cmd[(shift ? 5 : 4)..];
        bool w = suffix.Contains('w'), z = suffix.Contains('z'), x = suffix.Contains('x'), c = suffix.Contains('c');
        int ms = 0;
        if (parts.Length >= 3) int.TryParse(parts[2], out ms);
        ms = Math.Clamp(ms, 0, 1000);

        string letters = (w ? "w" : "") + (x ? "x" : "") + (z ? "z" : "") + (c ? "c" : "") + (shift ? "s" : "");
        if (_jumper != null && _jumper.Start(letters, null, ms)) { ReplyOk(JumpLine(letters, null, ms)); return true; }
        if (_jumper?.IsBusy == true) return true;   // it said so in chat
        if (!KeystrokesAllowed("/mt jump (keys)")) return true;
        if (!GameInput.Available) { ReplyFail("[RynthAi] /mt jump: no jump hook and no game window."); return true; }
        GameInput.SendSpace(ms, shift, w, z, x, c);
        ReplyOk(JumpLine(letters, null, ms) + " (the space key)");
        return true;
    }

    // ── trade ────────────────────────────────────────────────────────────────────

    /// <summary>/mt trade add|addp &lt;item&gt; | accept | decline | reset | end.</summary>
    private bool HandleMtTrade(string[] parts)
    {
        if (parts.Length < 3) { ReplyFail("[RynthAi] Usage: /mt trade add|addp <item> | accept | decline | reset | end"); return true; }
        string sub = parts[2].ToLowerInvariant();
        // Completing a trade is only ever a running meta's doing (checked before anything else).
        if (sub == "accept" && !RequireRunningMeta("/mt trade accept")) return true;
        if (!Host.HasTrade) { ReplyFail("[RynthAi] Trading needs a newer RynthCore engine."); return true; }
        switch (sub)
        {
            case "add":
            case "addp":
            {
                if (parts.Length < 4) { ReplyFail($"[RynthAi] Usage: /mt trade {sub} <item>"); return true; }
                string name = string.Join(" ", parts, 3, parts.Length - 3);
                var item = FindObject(name, inv: true, land: false, partial: sub == "addp");
                if (item == null) { ReplyFail($"[RynthAi] Item not found: '{name}'"); return true; }
                if (Host.TradeAdd(unchecked((uint)item.Id))) ReplyOk($"[RynthAi] Added {item.Name} to the trade.");
                else ReplyFail("[RynthAi] Could not add it to the trade.");
                return true;
            }
            case "accept":
                Log("[RynthAi] /mt trade accept (from the running meta)");
                TradeSaid(Host.TradeAccept(), "Accepting the trade.", "accept");
                return true;
            case "decline": TradeSaid(Host.TradeDecline(), "Declining the trade.", "decline"); return true;
            case "reset":   TradeSaid(Host.TradeReset(), "Clearing the trade window.", "reset"); return true;
            case "end":     TradeSaid(Host.TradeClose(), "Closing the trade.", "close"); return true;
            default:
                ReplyFail("[RynthAi] Usage: /mt trade add|addp <item> | accept | decline | reset | end");
                return true;
        }
    }

    /// <summary>A trade action's answer: what it does, or that it couldn't be sent.</summary>
    private void TradeSaid(bool sent, string ok, string what)
    {
        if (sent) ReplyOk("[RynthAi] " + ok);
        else ReplyFail($"[RynthAi] Could not send the trade {what}.");
    }

    // ── vendor ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// /mt vendor addbuy|addbuyp &lt;item&gt; [count] | addsell|addsellp &lt;item&gt; | buy | clearbuy | sell |
    /// clearsell, with a vendor open: lists kept here, sent in one transaction by buy / sell.
    /// </summary>
    private bool HandleMtVendor(string[] parts)
    {
        const string usage = "[RynthAi] Usage: /mt vendor addbuy|addbuyp <item> [count] | addsell|addsellp <item> | buy | clearbuy | sell | clearsell";
        if (parts.Length < 3) { ReplyFail(usage); return true; }
        // Spending or selling is only ever a running meta's doing (checked before anything else).
        string verb = parts[2].ToLowerInvariant();
        if (verb == "buy" && !RequireRunningMeta("/mt vendor buy")) return true;
        if (verb == "sell" && !RequireRunningMeta("/mt vendor sell")) return true;
        if (!Host.HasVendorTrade) { ReplyFail("[RynthAi] Vendor trading needs a newer RynthCore engine."); return true; }
        if (!Host.TryGetVendorInfo(out VendorInfo info) || info.VendorId == 0)
        {
            ReplyFail("[RynthAi] No vendor is open.");
            return true;
        }
        string sub = parts[2].ToLowerInvariant();
        switch (sub)
        {
            case "addbuy":
            case "addbuyp":
            {
                if (parts.Length < 4) { ReplyFail($"[RynthAi] Usage: /mt vendor {sub} <item> [count]"); return true; }
                var words = parts.Skip(3).ToList();
                int count = 1;
                if (words.Count > 1 && int.TryParse(words[^1], out int n) && n > 0) { count = n; words.RemoveAt(words.Count - 1); }
                string name = string.Join(" ", words);
                bool partial = sub == "addbuyp";
                VendorItem? item = Host.GetVendorItems().FirstOrDefault(v => partial
                    ? v.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (item == null) { ReplyFail($"[RynthAi] {info.Name} doesn't sell '{name}'."); return true; }
                _mtBuyList.Add((item.ObjectId, item.Name, count));
                ReplyOk($"[RynthAi] Buy list: {count} x {item.Name} ({_mtBuyList.Count} entr{(_mtBuyList.Count == 1 ? "y" : "ies")}).");
                return true;
            }
            case "addsell":
            case "addsellp":
            {
                if (parts.Length < 4) { ReplyFail($"[RynthAi] Usage: /mt vendor {sub} <item>"); return true; }
                string name = string.Join(" ", parts, 3, parts.Length - 3);
                var item = FindObject(name, inv: true, land: false, partial: sub == "addsellp");
                if (item == null) { ReplyFail($"[RynthAi] Item not found: '{name}'"); return true; }
                uint id = unchecked((uint)item.Id);
                if (!_mtSellList.Any(s => s.Id == id)) _mtSellList.Add((id, item.Name));
                ReplyOk($"[RynthAi] Sell list: {item.Name} ({_mtSellList.Count} item(s)).");
                return true;
            }
            case "buy":
            {
                if (_mtBuyList.Count == 0) { ReplyFail("[RynthAi] The buy list is empty (/mt vendor addbuy <item> [count])."); return true; }
                var entries = _mtBuyList.Select(b => new VendorTradeEntryNative(b.Id, b.Amount)).ToList();
                Log($"[RynthAi] /mt vendor buy (from the running meta): {_mtBuyList.Count} entr{(_mtBuyList.Count == 1 ? "y" : "ies")} from {info.Name} 0x{info.VendorId:X8}: "
                    + string.Join(", ", _mtBuyList.Select(b => $"{b.Amount} x {b.Name}")));
                uint sent = Host.VendorBuy(info.VendorId, entries);
                if (sent != 0) ReplyOk($"[RynthAi] Buying {_mtBuyList.Count} entr{(_mtBuyList.Count == 1 ? "y" : "ies")} from {info.Name}.");
                else ReplyFail("[RynthAi] The buy could not be sent.");
                if (sent != 0) _mtBuyList.Clear();
                return true;
            }
            case "clearbuy":
                _mtBuyList.Clear();
                ReplyOk("[RynthAi] Buy list cleared.");
                return true;
            case "sell":
            {
                if (_mtSellList.Count == 0) { ReplyFail("[RynthAi] The sell list is empty (/mt vendor addsell <item>)."); return true; }
                // Checked again now, not only when the item was listed: it may have been worn since,
                // and RynthAi's own gear and consumables are never sold by a meta.
                var toSell = new List<(uint Id, string Name)>();
                foreach (var (sid, sname) in _mtSellList)
                {
                    WorldObject? so = _objectCache?[unchecked((int)sid)];
                    string? why = so == null ? null : ItemProtectionReason(so, includeLootKeep: false);
                    if (why != null) Log($"[RynthAi] /mt vendor sell: skipped {sname} 0x{sid:X8} ({why}).");
                    else toSell.Add((sid, sname));
                }
                if (toSell.Count == 0)
                {
                    _mtSellList.Clear();
                    ReplyFail("[RynthAi] Nothing left to sell: every listed item is protected.");
                    return true;
                }
                Log($"[RynthAi] /mt vendor sell (from the running meta): {toSell.Count} item(s) to {info.Name} 0x{info.VendorId:X8}: "
                    + string.Join(", ", toSell.Select(s => s.Name)));
                uint sent = Host.VendorSell(info.VendorId, toSell.Select(s => s.Id).ToList());
                if (sent != 0) ReplyOk($"[RynthAi] Selling {toSell.Count} item(s) to {info.Name}.");
                else ReplyFail("[RynthAi] The sell could not be sent.");
                if (sent != 0) _mtSellList.Clear();
                return true;
            }
            case "clearsell":
                _mtSellList.Clear();
                ReplyOk("[RynthAi] Sell list cleared.");
                return true;
            default:
                ReplyFail(usage);
                return true;
        }
    }

    /// <summary>
    /// Why a meta verb must not give or sell this item, or null when it may. Equipped gear, the
    /// items RynthAi itself uses (the Weapons and Consumables lists, every monster rule's weapon and
    /// off hand) and items the server won't let go (attuned, retained) are always protected.
    /// <paramref name="includeLootKeep"/> adds the active loot profile's Keep / Keep # rules.
    /// </summary>
    internal string? ItemProtectionReason(WorldObject wo, bool includeLootKeep)
    {
        if (WorldObjectCache.IsWieldedByPlayer(Host, wo)) return "equipped";
        var s = _dashboard?.Settings;
        if (s != null)
        {
            try
            {
                foreach (var r in s.ItemRules.ToList()) if (r.Id == wo.Id) return "on RynthAi's Weapons list";
                foreach (var c in s.ConsumableRules.ToList()) if (c.Id == wo.Id) return "on RynthAi's Consumables list";
                foreach (var m in s.MonsterRules.ToList())
                    if (m.WeaponId == wo.Id || m.OffhandId == wo.Id) return "a monster rule's weapon";
            }
            catch { return "RynthAi's item lists could not be read"; }
        }
        if (wo.Values(114 /* Attuned */, 0) > 0) return "attuned";
        uint id = unchecked((uint)wo.Id);
        if (Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(id, out uint bf) && (bf & 0x01000000u) != 0) return "retained";
        if (Host.HasGetObjectBoolProperty && Host.TryGetObjectBoolProperty(id, 91 /* Retained */, out bool retained) && retained) return "retained";
        if (includeLootKeep && IsKeptByLootProfile(wo)) return "kept by the loot profile";
        return null;
    }

    /// <summary>The vendor closed: its buy / sell lists no longer mean anything.</summary>
    private void ClearMtVendorLists()
    {
        _mtBuyList.Clear();
        _mtSellList.Clear();
    }

    // ── dumpspells ───────────────────────────────────────────────────────────────

    /// <summary>/mt dumpspells: the spell table to RynthAi's folder (spelldump.txt: id, name, family, difficulty).</summary>
    private bool HandleMtDumpSpells()
    {
        try
        {
            Dictionary<string, int> byName = SpellDatabase.BuildNameToIdMap();
            var rows = byName.Select(kv => (Id: kv.Value, Name: kv.Key)).Distinct().OrderBy(r => r.Id).ToList();
            string path = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "spelldump.txt");
            using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
            {
                w.WriteLine("Id\tName\tFamily\tDifficulty");
                foreach (var (id, name) in rows)
                {
                    SpellDatabase.TryGetSpellMeta(id, out int family, out int difficulty);
                    w.WriteLine($"{id}\t{name}\t{family}\t{difficulty}");
                }
            }
            ReplyOk($"[RynthAi] {rows.Count} spells written to {path}");
        }
        catch (Exception ex) { ReplyFail($"[RynthAi] /mt dumpspells failed: {ex.Message}"); }
        return true;
    }

    // ── /ra equipfor (VTank /vt equipitemsfor) ───────────────────────────────────

    /// <summary>
    /// /ra equipfor &lt;monster&gt; (VTank's /vt equipitemsfor): wield the weapon and off hand set on that
    /// monster's rule (the rule named exactly, else one whose name the monster's contains, else
    /// Default). Items already wielded are left; a rule with no set weapon says so (combat picks
    /// one itself when it fights).
    /// </summary>
    private void HandleEquipForCommand(string[] parts)
    {
        if (parts.Length < 3) { ReplyFail("[RynthAi] Usage: /ra equipfor <monster name>"); return; }
        var rules = _dashboard?.Settings?.MonsterRules;
        if (rules == null || rules.Count == 0) { ReplyFail("[RynthAi] No monster rules loaded."); return; }
        string monster = string.Join(" ", parts, 2, parts.Length - 2).Trim();

        var rule = rules.FirstOrDefault(r => string.Equals(r.Name, monster, StringComparison.OrdinalIgnoreCase))
                   ?? rules.Where(r => !string.Equals(r.Name, "Default", StringComparison.OrdinalIgnoreCase)
                                       && r.Name.Length > 0 && monster.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                           .OrderByDescending(r => r.Name.Length).FirstOrDefault()
                   ?? rules.FirstOrDefault(r => string.Equals(r.Name, "Default", StringComparison.OrdinalIgnoreCase));
        if (rule == null) { ReplyFail($"[RynthAi] No monster rule for '{monster}'."); return; }

        var ids = new List<int>();
        if (rule.WeaponId != 0) ids.Add(rule.WeaponId);
        if (rule.OffhandId != 0) ids.Add(rule.OffhandId);
        if (ids.Count == 0)
        {
            ReplyOk($"[RynthAi] '{rule.Name}' has no set weapon (combat picks one when it fights '{monster}').");
            return;
        }
        if (!Host.HasUseObject) { ReplyFail("[RynthAi] UseObject not available."); return; }

        int sent = 0;
        foreach (int id in ids)
        {
            var wo = _objectCache?[id];
            if (wo == null) { ReplyFail($"[RynthAi] '{rule.Name}': item 0x{(uint)id:X8} isn't carried."); continue; }
            if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) > 0) continue;   // already wielded
            Host.UseFor(unchecked((uint)id), "Command", "/ra equipfor", UseKind.Asked);
            sent++;
        }
        ReplyOk(sent > 0 ? $"[RynthAi] Equipping for '{monster}' ({rule.Name}): {sent} item(s)."
                          : $"[RynthAi] '{rule.Name}' gear for '{monster}' is already wielded.");
    }

    // ── click, cursor, movement, strafe, client ─────────────────────────────────

    /// <summary>/mt click ok|yes|no|&lt;x&gt; &lt;y&gt;: a click in the game window (window coordinates).</summary>
    private bool HandleMtClick(string[] parts)
    {
        if (parts.Length < 3) { ReplyFail("[RynthAi] Usage: /mt click ok|yes|no|<x> <y>"); return true; }
        if (!KeystrokesAllowed("/mt click")) return true;
        if (!GameInput.Available) { ReplyFail("[RynthAi] /mt click: the game window wasn't found."); return true; }
        switch (parts[2].ToLowerInvariant())
        {
            case "ok":  GameInput.ClickOK(); ReplyOk("[RynthAi] Clicked OK."); return true;
            case "yes": GameInput.ClickYes(); ReplyOk("[RynthAi] Clicked Yes."); return true;
            case "no":  GameInput.ClickNo(); ReplyOk("[RynthAi] Clicked No."); return true;
        }
        if (parts.Length >= 4 && int.TryParse(parts[2], out int x) && int.TryParse(parts[3], out int y))
        {
            GameInput.SendMouseClick(x, y);
            ReplyOk($"[RynthAi] Clicked at {x},{y} in the game window.");
            return true;
        }
        ReplyFail("[RynthAi] Usage: /mt click ok|yes|no|<x> <y>");
        return true;
    }

    /// <summary>/mt get xy (and ub-IT's /mt cursor): the cursor relative to the game window.</summary>
    private bool HandleMtCursor()
    {
        ChatLine(GameInput.TryCursorInWindow(out int x, out int y)
            ? $"[RynthAi] Current cursor position: {x},{y}"
            : "[RynthAi] The cursor position couldn't be read.");
        return true;
    }

    /// <summary>/mt movement &lt;key&gt; &lt;ms&gt;: hold a movement key (AC's keymap letter) for ms.</summary>
    private bool HandleMtMovement(string[] parts)
    {
        if (parts.Length != 4 || parts[2].Length != 1 || !int.TryParse(parts[3], out int ms) || ms < 0)
        {
            ReplyFail("[RynthAi] Usage: /mt movement <key> <ms>   (e.g. /mt movement w 1500)");
            return true;
        }
        if (!KeystrokesAllowed("/mt movement")) return true;
        if (!GameInput.Available) { ReplyFail("[RynthAi] /mt movement: the game window wasn't found."); return true; }
        GameInput.SendMovement(parts[2][0], ms);
        ReplyOk($"[RynthAi] Holding '{parts[2][0]}' for {ms} ms.");
        return true;
    }

    /// <summary>ub-IT's /mt strafe left|l|right|r &lt;ms&gt;: hold the side-step key (Z / C) for ms.</summary>
    private bool HandleMtStrafe(string[] parts)
    {
        if (parts.Length != 4 || !int.TryParse(parts[3], out int ms) || ms < 0)
        {
            ReplyFail("[RynthAi] Usage: /mt strafe left|l|right|r <ms>");
            return true;
        }
        char key = parts[2].ToLowerInvariant() switch { "left" or "l" => 'z', "right" or "r" => 'c', _ => '\0' };
        if (key == '\0') { ReplyFail("[RynthAi] Usage: /mt strafe left|l|right|r <ms>"); return true; }
        if (!KeystrokesAllowed("/mt strafe")) return true;
        if (!GameInput.Available) { ReplyFail("[RynthAi] /mt strafe: the game window wasn't found."); return true; }
        GameInput.SendMovement(key, ms);
        ReplyOk($"[RynthAi] Strafing {(key == 'z' ? "left" : "right")} for {ms} ms.");
        return true;
    }

    /// <summary>/mt client minimize|restore (ub-IT's frame apply|restore points to Settings > Display).</summary>
    private bool HandleMtClient(string[] parts)
    {
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        switch (sub)
        {
            case "minimize": if (KeystrokesAllowed("/mt client")) { GameInput.Minimize(); ReplyOk("[RynthAi] Game window minimized."); } return true;
            case "restore":  if (KeystrokesAllowed("/mt client")) { GameInput.Restore(); ReplyOk("[RynthAi] Game window restored."); } return true;
            case "frame":
                ChatLine("[RynthAi] The window frame is RynthCore's: Settings > Display, Borderless Windowed (there is no chat command for it).");
                return true;
            default:
                ReplyFail("[RynthAi] Usage: /mt client minimize | restore");
                return true;
        }
    }
}
