using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using RynthCore.Plugin.RynthAi.ItemTool;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// The Item Tool (2026-10-17): two small tools in one engine panel (RynthCore's ItemToolFace), each
// picked, started and stopped by Tom himself.
//
//   Chest looter  - "Pick a chest. Pick a key. It just loots the chest based on the current loot
//                   profile until it's out of keys." ItemTool/ChestKeyLooter.cs is the state
//                   machine; this file is its host side. The key is used on the chest (AC's
//                   use-with-target, UseOnFor), the chest is opened (UseFor), and the open chest
//                   is handed to the corpse looter (RynthAiPlugin.ChestLoot.cs) the way /ra
//                   lootthis does it, so it runs with the macro off and Looting off too and
//                   uses the loot profile that is loaded.
//   Quick give    - ItemTool/QuickGive.cs. A rapid give of one picked item to one picked player
//                   or NPC. Separate from the profile give (actiontrygiveprofile / ItemGiver\ /
//                   /ra ig), which stays locked to NPCs and its folder: this one shares nothing
//                   with it but AC's give action (GiveObjectTo), and only starts from a source
//                   Tom controls (below).
//
// Commands (/ra itemtool ...; the panel sends the same words through RynthPluginItemToolCommand):
//   chest set | key | start | stop | status | clear
//   relock <seconds> (also "chest relock"): "Chest re-lock wait (s)", saved in the profile (2026-11-05)
//   giveitem | givetarget | give | givestop | givestatus | clear
//   stop (both)
//
// Threads: a chat command arrives on AC's thread and the panel's on the engine pump, but the tick
// is where everything runs: commands are queued and taken in TickItemTool.
//
// Who may start Quick give: only a line Tom typed (the chat bar, the RynthAi chat box) or the
// panel's button. A meta's /ra line, a script's SubmitCommand, a /ub delayed command and the
// remote "sendchat" run with _itemToolUntrusted set, and Start is refused for them (a shared meta
// must never hand a pack to someone's character). Stop is allowed from anywhere.
public sealed partial class RynthAiPlugin
{
    /// <summary>Clock (ms) of the Item Tool; tests replace it.</summary>
    internal static Func<long> ItemToolClock = static () => Environment.TickCount64;

    /// <summary>Set (this thread only) while a line that was not typed by the player runs.</summary>
    [ThreadStatic] private static bool _itemToolUntrusted;

    private ChestKeyLooter? _chestKey;
    private QuickGive? _quickGive;
    private readonly ConcurrentQueue<(string Args, bool Trusted)> _itemToolCmds = new();
    private int _chestKeyForceId;          // pump: the open chest to hand to the corpse looter
    private int _chestKeyOpenedId;         // OnViewObjectContents: the last non-corpse container the server opened
    private long _chestKeyOpenedAt;
    private long _itemToolBusySince;
    private int _giveTargetClass;          // what the picked target is (for the status line)

    private const long ItemToolBusyGraceMs = 2500;
    private const uint ItemToolBfPlayer = 0x00000008, ItemToolBfAttackable = 0x00000010, ItemToolBfVendor = 0x00000200;
    private const uint StypeBoolLocked = 3;   // ACE PropertyBool.Locked

    /// <summary>Runs <paramref name="action"/> as a line the player did not type (see the header).</summary>
    private static void RunItemToolUntrusted(Action action)
    {
        bool was = _itemToolUntrusted;
        _itemToolUntrusted = true;
        try { action(); }
        finally { _itemToolUntrusted = was; }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>"/ra itemtool ..." (DispatchRaCommand): queued, run on the next tick.</summary>
    private void HandleItemToolCommand(string[] parts)
    {
        string args = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2).Trim() : "";
        QueueItemToolCommand(args, trusted: !_itemToolUntrusted);
    }

    /// <summary>The panel's command (RynthPluginItemToolCommand): from a button Tom pressed.</summary>
    internal void QueueItemToolCommand(string args, bool trusted)
    {
        if (_itemToolCmds.Count > 32) return;   // a flood of commands is dropped, not queued
        _itemToolCmds.Enqueue((args ?? "", trusted));
    }

    private void EnsureItemTool()
    {
        _chestKey ??= new ChestKeyLooter(new ChestEnv(this));
        _quickGive ??= new QuickGive(new GiveEnv(this));
    }

    /// <summary>Every plugin tick: the queued commands, then both state machines.</summary>
    private void TickItemTool()
    {
        if (!_loginComplete || _objectCache == null || _playerId == 0) return;
        if (_chestKey == null && _quickGive == null && _itemToolCmds.IsEmpty) return;
        try
        {
            EnsureItemTool();
            while (_itemToolCmds.TryDequeue(out var cmd))
                RunItemToolCommand(cmd.Args, cmd.Trusted);
            if (_dashboard?.Settings is LegacyUiSettings s) _chestKey!.RelockWaitSec = s.ChestRelockWaitSec;
            _chestKey!.Tick();
            _quickGive!.Tick();
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] Item Tool tick threw {ex.GetType().Name}: {ex.Message}");
            try { _chestKey?.Stop("an internal error (see the log)"); } catch { }
            try { _quickGive?.Stop("an internal error (see the log)"); } catch { }
        }
    }

    private void ResetItemTool()
    {
        while (_itemToolCmds.TryDequeue(out _)) { }
        _chestKey = null;
        _quickGive = null;
        _chestKeyForceId = 0;
        _chestKeyOpenedId = 0;
        _chestKeyOpenedAt = 0;
    }

    private void RunItemToolCommand(string args, bool trusted)
    {
        EnsureItemTool();
        string[] t = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = t.Length > 0 ? t[0].ToLowerInvariant() : "";
        string sub = t.Length > 1 ? t[1].ToLowerInvariant() : "";

        switch (verb)
        {
            case "":
            case "status":
                ChatLine("[RynthAi] Item Tool - " + _chestKey!.Status + " | " + _quickGive!.Status);
                ChatLine("[RynthAi]   /ra itemtool chest set|key|start|stop|status   /ra itemtool relock <seconds>   and   /ra itemtool giveitem|givetarget|give|givestop|givestatus");
                return;
            case "stop":
                if (_chestKey!.Running) _chestKey.Stop("stopped by you");
                if (_quickGive!.Running) _quickGive.Stop("stopped by you");
                if (!_chestKey.Running && !_quickGive.Running) ChatLine("[RynthAi] Item Tool: nothing is running.");
                return;
            case "clear":
                _chestKey!.Clear();
                _quickGive!.Clear();
                ChatLine("[RynthAi] Item Tool: the picks are cleared (anything running keeps its own).");
                return;
            case "chest":
                if (sub == "relock") { RunRelockCommand(t.Length > 2 ? t[2] : ""); return; }
                RunChestCommand(sub);
                return;
            case "relock":
                RunRelockCommand(sub);
                return;
            case "giveitem": PickGiveItem(); return;
            case "givetarget": PickGiveTarget(); return;
            case "givestatus": ChatLine("[RynthAi] Quick give - " + _quickGive!.Status); return;
            case "givestop": _quickGive!.Stop("stopped by you"); return;
            case "give":
                if (!trusted)
                {
                    ChatLine("[RynthAi] Quick give: it starts only from the Item Tool panel or a command you type yourself, not from a meta, script or remote line.");
                    return;
                }
                StartQuickGive();
                return;
            default:
                ChatLine($"[RynthAi] Item Tool: unknown '{args}'. Try /ra itemtool status.");
                return;
        }
    }

    private void RunChestCommand(string sub)
    {
        var ck = _chestKey!;
        switch (sub)
        {
            case "set": PickChest(); return;
            case "key": PickKey(); return;
            case "start":
                if (!Host.HasUseObjectOn || !Host.HasGetSelectedItemId)
                {
                    ChatLine("[RynthAi] Chest looter: this engine can't use an item on a target (needs plugin API with UseObjectOn).");
                    return;
                }
                ck.Start(out _);
                return;
            case "stop": ck.Stop("stopped by you"); return;
            case "clear": ck.Clear(); ChatLine("[RynthAi] Chest looter: chest and key cleared."); return;
            case "":
            case "status":
                ChatLine("[RynthAi] Chest looter - " + ck.Status);
                return;
            default:
                ChatLine("[RynthAi] Usage: /ra itemtool chest set|key|start|stop|status   and   /ra itemtool relock <seconds>");
                return;
        }
    }

    private const string RelockUsage = "/ra itemtool relock <seconds>";

    /// <summary>
    /// "/ra itemtool relock [seconds]" (the panel's "Chest re-lock wait (s)" sends the same): how long
    /// after a chest was opened the next key goes on it. Saved in the settings profile; always answers
    /// with the value now.
    /// </summary>
    private void RunRelockCommand(string arg)
    {
        string range = $"{ChestKeyLooter.MinRelockWaitSec}-{ChestKeyLooter.MaxRelockWaitSec} s, default {ChestKeyLooter.DefaultRelockWaitSec}";
        var dash = _dashboard;
        LegacyUiSettings? s = dash?.Settings;
        if (dash == null || s == null || !dash.ProfileLoaded)
        {
            ReplyFail($"[RynthAi] Chest looter: the settings profile isn't loaded yet (log in first); the re-lock wait is unchanged. Usage: {RelockUsage} ({range}).");
            return;
        }
        if (arg.Length == 0)
        {
            ChatLine($"[RynthAi] Chest looter: re-lock wait = {s.ChestRelockWaitSec} s ({range}). Usage: {RelockUsage}.");
            return;
        }
        if (!int.TryParse(arg.TrimEnd('s', 'S'), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int want))
        {
            ReplyFail($"[RynthAi] Chest looter: '{arg}' isn't a number of seconds; the re-lock wait is still {s.ChestRelockWaitSec} s. Usage: {RelockUsage} ({range}).");
            return;
        }
        int now = ChestKeyLooter.ClampRelockWaitSec(want);
        s.ChestRelockWaitSec = now;
        if (_chestKey != null) _chestKey.RelockWaitSec = now;
        dash.SaveSettings();
        string clamped = now == want ? "" : now > want ? $" (the least is {now} s)" : $" (the most is {now} s)";
        ReplyOk($"[RynthAi] Chest looter: re-lock wait = {now} s{clamped}. The next key goes on the chest {now} s after it was opened, or sooner when it reads locked again.");
    }

    // ── Picking ──────────────────────────────────────────────────────────────

    private uint SelectedForItemTool(string what)
    {
        if (!Host.HasGetSelectedItemId) { ChatLine("[RynthAi] Item Tool: this engine can't read the selected item."); return 0; }
        uint sel = Host.GetSelectedItemId();
        if (sel == 0) ChatLine($"[RynthAi] Item Tool: select {what} in the game first.");
        return sel;
    }

    private string ItemToolName(int id)
    {
        string n = _objectCache?[id]?.Name ?? "";
        if (n.Length == 0 && Host.TryGetObjectName(unchecked((uint)id), out string hn)) n = hn;
        return n.Length > 0 ? n : $"0x{(uint)id:X8}";
    }

    private void PickChest()
    {
        var ck = _chestKey!;
        if (ck.Running) { ChatLine("[RynthAi] Chest looter: stop it before changing the chest."); return; }
        uint sel = SelectedForItemTool("the chest in the world");
        if (sel == 0) return;
        int id = unchecked((int)sel);
        if (IsCarriedByPlayer(id)) { ChatLine("[RynthAi] Chest looter: that is in your pack; select the chest in the world."); return; }
        string? refusal = LootThisRefusal(id);
        if (refusal == null && IsCorpseLikeObject(id)) refusal = "that is a corpse (the corpse looter handles those)";
        if (refusal != null) { ChatLine($"[RynthAi] Chest looter: not that - {refusal}."); return; }
        ck.SetChest(id, ItemToolName(id));
        ChatLine($"[RynthAi] Chest looter: chest = {ck.ChestName} (0x{sel:X8}).");
    }

    private void PickKey()
    {
        var ck = _chestKey!;
        if (ck.Running) { ChatLine("[RynthAi] Chest looter: stop it before changing the key."); return; }
        uint sel = SelectedForItemTool("the key in your pack");
        if (sel == 0) return;
        int id = unchecked((int)sel);
        if (_objectCache?[id] is not WorldObject wo || !IsCarriedByPlayer(id)) { ChatLine("[RynthAi] Chest looter: the key must be in your pack; select it there."); return; }
        if (WorldObjectCache.IsWieldedByPlayer(Host, wo)) { ChatLine("[RynthAi] Chest looter: that is worn or wielded, not a key."); return; }
        ck.SetKey(id, wo.Name);
        ChatLine($"[RynthAi] Chest looter: key = {wo.Name} (0x{sel:X8}). Any other '{wo.Name}' you carry is used when this one runs out.");
    }

    private void PickGiveItem()
    {
        var qg = _quickGive!;
        if (qg.Running) { ChatLine("[RynthAi] Quick give: stop it before changing the item."); return; }
        uint sel = SelectedForItemTool("the item in your pack");
        if (sel == 0) return;
        int id = unchecked((int)sel);
        if (_objectCache?[id] is not WorldObject wo || !IsCarriedByPlayer(id)) { ChatLine("[RynthAi] Quick give: the item must be in your pack; select it there."); return; }
        if (WorldObjectCache.IsWieldedByPlayer(Host, wo)) { ChatLine("[RynthAi] Quick give: that is worn or wielded; take it off first."); return; }
        qg.SetItem(id, new GiveItemKey(ItemToolWcid(id), wo.Name));
        ChatLine($"[RynthAi] Quick give: item = {wo.Name} (every '{wo.Name}' in your packs will go).");
    }

    private void PickGiveTarget()
    {
        var qg = _quickGive!;
        if (qg.Running) { ChatLine("[RynthAi] Quick give: stop it before changing the target."); return; }
        uint sel = SelectedForItemTool("the player or NPC in the world");
        if (sel == 0) return;
        int id = unchecked((int)sel);
        string? refusal = QuickGiveTargetRefusal(id, out int cls);
        if (refusal != null) { ChatLine($"[RynthAi] Quick give: not that - {refusal}."); return; }
        _giveTargetClass = cls;
        qg.SetTarget(id, ItemToolName(id));
        ChatLine($"[RynthAi] Quick give: target = {(cls == (int)AcObjectClass.Player ? "PLAYER " : "")}{qg.TargetName} (0x{sel:X8}).");
    }

    private uint ItemToolWcid(int id)
        => Host.HasGetObjectWcid && Host.TryGetObjectWcid(unchecked((uint)id), out uint w) ? w : 0;

    /// <summary>Null when <paramref name="id"/> may be a Quick give target (a player or an NPC in the world).</summary>
    internal string? QuickGiveTargetRefusal(int id, out int objectClass)
    {
        objectClass = 0;
        if (_objectCache == null || _playerId == 0) return "not ready";
        if (unchecked((uint)id) == _playerId) return "that is you";
        if (IsCarriedByPlayer(id)) return "that is in your pack; select the player or NPC in the world";
        if (_objectCache[id] is not WorldObject wo) return "it isn't loaded";
        objectClass = ItemToolTargetClass(wo);
        if (objectClass is (int)AcObjectClass.Player or (int)AcObjectClass.Npc or (int)AcObjectClass.Vendor) return null;
        return $"Quick give hands items to a player or an NPC; that is '{wo.Name}'";
    }

    /// <summary>The object's class, the weenie bitfield first (as the meta engine reads it): player, vendor, NPC or what the cache says.</summary>
    private int ItemToolTargetClass(WorldObject wo)
    {
        int cls = (int)wo.ObjectClass;
        uint uid = unchecked((uint)wo.Id);
        if (Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(uid, out uint bf))
        {
            if ((bf & ItemToolBfPlayer) != 0) return (int)AcObjectClass.Player;
            if ((bf & ItemToolBfVendor) != 0) return (int)AcObjectClass.Vendor;
            if (cls == (int)AcObjectClass.Monster && (bf & ItemToolBfAttackable) == 0) return (int)AcObjectClass.Npc;
        }
        else if (cls == (int)AcObjectClass.Monster && Host.HasObjectIsAttackable && !Host.ObjectIsAttackable(uid))
        {
            return (int)AcObjectClass.Npc;
        }
        return cls;
    }

    private void StartQuickGive()
    {
        var qg = _quickGive!;
        if (!Host.HasGiveObjectTo) { ChatLine("[RynthAi] Quick give: this engine has no give action (needs plugin API with GiveObjectTo)."); return; }
        if (qg.TargetId != 0)
        {
            // The picked object must still be a player/NPC (the id could be reused after a long wait).
            string? refusal = QuickGiveTargetRefusal(qg.TargetId, out int cls);
            if (refusal != null && _objectCache?[qg.TargetId] != null) { ChatLine($"[RynthAi] Quick give: can't start - {refusal}."); return; }
            _giveTargetClass = cls;
        }
        qg.Start(out _);
    }

    // ── Hooks from the rest of the plugin ───────────────────────────────────

    /// <summary>OnChatWindowText: the key's answers.</summary>
    private void ItemToolChat(string text) => _chestKey?.OnChat(text);

    /// <summary>OnViewObjectContents for a non-corpse container: the chest looter's proof the chest opened.</summary>
    private void NoteChestKeyOpened(int id)
    {
        _chestKeyOpenedId = id;
        _chestKeyOpenedAt = ItemToolClock();
    }

    /// <summary>
    /// TickCorpseOpening, every tick: the chest looter asked for its open chest to be looted. Like
    /// /ra lootthis: the container is flagged so the loot tick runs for it with the macro or
    /// Looting off, and taken as the loot container with no walk and no open.
    /// </summary>
    private void TakeChestKeyRequest()
    {
        int id = _chestKeyForceId;
        if (id == 0) return;
        _chestKeyForceId = 0;

        string? refusal = LootThisRefusal(id);
        if (refusal != null)
        {
            ChatLine($"[RynthAi] Chest looter: not looting {ItemToolName(id)} - {refusal}.");
            return;
        }
        ForceLootOpenChest(id);
    }

    /// <summary>
    /// Flags the open chest <paramref name="id"/> so the loot tick runs for it with the macro or
    /// Looting off, and takes it as the loot container with no walk and no open (the chest looter,
    /// Pickup with macro off). The caller checked <see cref="LootThisRefusal"/>.
    /// </summary>
    private void ForceLootOpenChest(int id)
    {
        _forcedLootContainerId = id;
        if (_openedContainerId == id) return;   // the macro's own chest check took it first

        long now = CorpseNowMs;
        if (_targetCorpseId != 0 && _targetCorpseId != id) ResetCorpseTarget();
        if (_chestCandidateId != id) { _chestCandidateSince = now; _chestCandidateInvSeenAt = 0; }
        TakeChestAsLootContainer(id, now);
        PauseNavigationForCorpse();
        StopCorpseMovement();
    }

    /// <summary>The Item Tool panel's data (RynthPluginGetItemToolJson).</summary>
    internal string BuildItemToolJson()
    {
        var ck = _chestKey;
        var qg = _quickGive;
        var sb = new StringBuilder(512);
        sb.Append("{\"chest\":{");
        AppendJson(sb, "name", ck?.ChestName ?? ""); sb.Append(',');
        AppendJson(sb, "key", ck?.KeyName ?? ""); sb.Append(',');
        AppendJson(sb, "status", ck?.Status ?? "Idle."); sb.Append(',');
        sb.Append("\"running\":").Append(ck?.Running == true ? "true" : "false").Append(',');
        sb.Append("\"looted\":").Append(ck?.Looted ?? 0).Append(',');
        // "Chest re-lock wait (s)": the panel's control (absent from an older RynthAi: the panel hides it).
        sb.Append("\"relockSec\":").Append(_dashboard?.Settings?.ChestRelockWaitSec ?? ChestKeyLooter.DefaultRelockWaitSec).Append(',');
        sb.Append("\"relockMin\":").Append(ChestKeyLooter.MinRelockWaitSec).Append(',');
        sb.Append("\"relockMax\":").Append(ChestKeyLooter.MaxRelockWaitSec).Append(',');
        sb.Append("\"relockReady\":").Append(_dashboard?.ProfileLoaded == true ? "true" : "false");
        sb.Append("},\"give\":{");
        AppendJson(sb, "item", qg?.Item.Name ?? ""); sb.Append(',');
        AppendJson(sb, "target", qg == null || qg.TargetId == 0 ? "" : (_giveTargetClass == (int)AcObjectClass.Player ? "PLAYER " : "") + qg.TargetName); sb.Append(',');
        AppendJson(sb, "status", qg?.Status ?? "Idle."); sb.Append(',');
        sb.Append("\"running\":").Append(qg?.Running == true ? "true" : "false").Append(',');
        sb.Append("\"given\":").Append(qg?.Given ?? 0);
        sb.Append("}}");
        return sb.ToString();
    }

    private static void AppendJson(StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default:
                    if (c < 0x20) sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ── The chest looter's host side ────────────────────────────────────────

    private sealed class ChestEnv : IChestKeyEnv
    {
        private readonly RynthAiPlugin _p;
        public ChestEnv(RynthAiPlugin p) => _p = p;

        private RynthCore.PluginSdk.RynthCoreHost Host => _p.Host;

        public long Now => ItemToolClock();
        public string NameOf(int id) => _p.ItemToolName(id);

        public bool ChestLoaded(int chestId, out double meters)
        {
            meters = -1;
            var cache = _p._objectCache;
            if (cache == null || cache[chestId] == null) return false;
            double d = cache.Distance(unchecked((int)_p._playerId), chestId);
            meters = d == double.MaxValue ? -1 : d;
            return true;
        }

        public int FindKey(int keyId, string keyName, int excludeId)
        {
            var cache = _p._objectCache;
            if (cache == null) return 0;
            if (keyId != 0 && keyId != excludeId && cache[keyId] is WorldObject cur && _p.IsCarriedByPlayer(keyId)
                && !WorldObjectCache.IsWieldedByPlayer(Host, cur))
                return keyId;
            foreach (var wo in cache.GetDirectInventory())
            {
                if (wo.Id == excludeId || wo.Id == keyId) continue;
                if (string.Equals(wo.Name, keyName, StringComparison.OrdinalIgnoreCase) && _p.IsCarriedByPlayer(wo.Id)
                    && !WorldObjectCache.IsWieldedByPlayer(Host, wo))
                    return wo.Id;
            }
            return 0;
        }

        public string? HoldReason() => _p.ItemToolHoldReason(yieldToLooter: true);

        public bool HasUseDone => Host.HasUseDoneSeq;
        public int UseDoneSeq() => Host.HasUseDoneSeq ? Host.GetUseDoneSeq() : 0;
        public bool LastUseDone(out int seq, out uint error)
        {
            seq = 0; error = 0;
            return Host.HasGetLastUseDone && Host.TryGetLastUseDone(out seq, out error);
        }

        public bool SendKey(int keyId, int chestId)
            => Host.UseOnFor(unchecked((uint)keyId), unchecked((uint)chestId), "ItemTool", "Chest looter: the key on the chest", UseKind.Asked);

        public bool SendOpen(int chestId)
            => Host.UseFor(unchecked((uint)chestId), "ItemTool", "Chest looter: open the chest", UseKind.Asked);

        public long ChestOpenedAt(int chestId) => _p._chestKeyOpenedId == chestId ? _p._chestKeyOpenedAt : 0;

        public void StartLooting(int chestId) => _p._chestKeyForceId = chestId;

        public bool Looting(int chestId)
            => _p._chestKeyForceId == chestId
               || (_p.IsChestLoot(chestId) && (_p._openedContainerId == chestId || _p._targetCorpseId == chestId));

        public void AbortLooting(int chestId)
        {
            if (_p._chestKeyForceId == chestId) _p._chestKeyForceId = 0;
            if (_p._forcedLootContainerId == chestId && !_p.IsChestLoot(chestId)) _p._forcedLootContainerId = 0;
            if (_p.IsChestLoot(chestId) && (_p._openedContainerId == chestId || _p._targetCorpseId == chestId))
                _p.FinishChestLoot(chestId, "the chest looter was stopped", close: true, sayNothingMatched: false);
        }

        public bool? ChestLockedHint(int chestId)
            => Host.HasGetObjectBoolProperty && Host.TryGetObjectBoolProperty(unchecked((uint)chestId), StypeBoolLocked, out bool locked) ? locked : null;

        public void Say(string text) => _p.ChatLine(text);
        public void Log(string text) => Host.Log(text);
    }

    /// <summary>Why the Item Tool may not send now (null = free): combat, portal space, an unanswered action, a busy client.</summary>
    private string? ItemToolHoldReason(bool yieldToLooter)
    {
        if (ActionPacer.IsPortaling(Host)) return "portal space";
        var settings = _dashboard?.Settings;
        if (settings != null && settings.IsMacroRunning && settings.EnableCombat && _combatManager?.HasTargets == true)
            return "combat";
        // Another loot session (a corpse) is open: let it finish.
        if (yieldToLooter && (_openedContainerId != 0 || _targetCorpseId != 0) && !IsChestLoot(_openedContainerId != 0 ? _openedContainerId : _targetCorpseId))
            return "the corpse looter is busy";
        if (!ActionPacer.MayStart(Host, "ItemTool", HealNeed.None, out string why))
            return why.Length > 0 ? why : "the server owes an answer";
        long now = ItemToolClock();
        if (_busyCount > 0)
        {
            if (_itemToolBusySince == 0) _itemToolBusySince = now;
            if (now - _itemToolBusySince < ItemToolBusyGraceMs) return $"the client is busy ({_busyCount})";
        }
        else _itemToolBusySince = 0;
        return null;
    }

    // ── Quick give's host side ──────────────────────────────────────────────

    private sealed class GiveEnv : IQuickGiveEnv
    {
        private readonly RynthAiPlugin _p;
        public GiveEnv(RynthAiPlugin p) => _p = p;

        private RynthCore.PluginSdk.RynthCoreHost Host => _p.Host;

        public long Now => ItemToolClock();

        public bool TargetLoaded(int targetId, out string name, out double meters)
        {
            name = ""; meters = -1;
            var cache = _p._objectCache;
            if (cache == null || cache[targetId] is not WorldObject wo) return false;
            name = _p.ItemToolName(targetId);
            double d = cache.Distance(unchecked((int)_p._playerId), targetId);
            meters = d == double.MaxValue ? -1 : d;
            return true;
        }

        public bool InCombat()
        {
            var settings = _p._dashboard?.Settings;
            return settings != null && settings.IsMacroRunning && settings.EnableCombat && _p._combatManager?.HasTargets == true;
        }

        public string? HoldReason() => _p.ItemToolHoldReason(yieldToLooter: false);

        /// <summary>The carried stacks (never worn or wielded) that are exactly the picked item.</summary>
        private IEnumerable<WorldObject> Matching(GiveItemKey key)
        {
            var cache = _p._objectCache;
            if (cache == null) yield break;
            var seen = new HashSet<int>();
            foreach (var wo in cache.GetDirectInventory(forceRefresh: true))
                if (Matches(wo, key) && _p.InPlayerInventory(wo.Container) && seen.Add(wo.Id)) yield return wo;
            foreach (var wo in cache.AllKnownObjects())
                if (!seen.Contains(wo.Id) && Matches(wo, key) && _p.IsCarriedByPlayer(wo.Id) && seen.Add(wo.Id)) yield return wo;
        }

        private bool Matches(WorldObject wo, GiveItemKey key)
        {
            if (!string.Equals(wo.Name, key.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (key.Wcid != 0 && _p.ItemToolWcid(wo.Id) != key.Wcid) return false;   // the same weenie, not just the same name
            return !WorldObjectCache.IsWieldedByPlayer(Host, wo);
        }

        public bool NextItem(GiveItemKey key, ISet<int> skip, out int itemId, out int count)
        {
            foreach (var wo in Matching(key))
            {
                if (skip.Contains(wo.Id)) continue;
                itemId = wo.Id;
                count = Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
                return true;
            }
            itemId = 0; count = 0;
            return false;
        }

        public int CountCarried(GiveItemKey key)
        {
            int n = 0;
            foreach (var _ in Matching(key)) n++;
            return n;
        }

        public bool Landed(int itemId, int count)
        {
            // As the give queue reads it (GiveLanded): gone from the cache, out of the packs, or a smaller stack.
            if (_p._objectCache?[itemId] is not WorldObject wo) return true;
            if (!_p.InPlayerInventory(wo.Container)) return true;
            return Math.Max(1, wo.Values(LongValueKey.StackCount, 1)) < count;
        }

        public bool Send(int itemId, int targetId)
            => Host.GiveObjectTo(unchecked((uint)itemId), unchecked((uint)targetId), 0);   // amount 0 = the whole object

        public int RefusalSeq()
            => Host.HasGetLastWeenieError && Host.TryGetLastWeenieError(out int seq, out _, out _, out _) ? seq : 0;

        public void Say(string text) => _p.ChatLine(text);
        public void Log(string text) => Host.Log(text);
    }
}
