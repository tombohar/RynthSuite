// RynthAiPlugin.BuffItems.cs - /ra buffitems and the Spells window's Buff Items tab (pump side).
// The tab draws on the render thread (LegacySpellsUi); its two Add buttons come here through the
// remote command queue, and the open tab gets a fresh copy of the list every 2 s.
using System;
using System.Collections.Generic;
using System.Text;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    internal const string BuffItemsUsage =
        "/ra buffitems [list] | on | off | open | allow <name> | block <name> | add <name> | addselected | remove <name> | repeat <minutes> <name>";

    private long _buffItemRowsPushedAt;

    /// <summary>/ra buffitems ... (also the Buff Items tab's Add buttons, through the remote command queue). Always answers.</summary>
    private void HandleBuffItemsCommand(string[] parts)
    {
        var s = _dashboard?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Buff items: settings not ready (log in first)."); return; }
        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "list";
        string rest = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3).Trim() : string.Empty;

        switch (verb)
        {
            case "list":
            case "status":
                ListBuffItems(s);
                break;

            case "on":
            case "off":
                s.UseBuffItems = verb == "on";
                ReplyOk($"[RynthAi] Use Buff Items is {(s.UseBuffItems ? "on" : "off")}.");
                break;

            case "open":
            case "panel":
            case "window":
                EnsureSpellsUi();
                s.ShowSpellsWindow = true;
                _spellsUi?.ShowBuffItemsTab();
                PushBuffItemRows(force: true);
                ReplyOk("[RynthAi] Spells window opened on Buff Items.");
                break;

            case "allow":
            case "block":
            {
                if (rest.Length == 0) { ReplyFail($"[RynthAi] Buff items: which item? Usage: {BuffItemsUsage}"); break; }
                bool allow = verb == "allow";
                var r = s.SetBuffItemAllowed(rest, allow, out string shown);
                if (r == BuffItemRules.EditResult.NotFound)
                    ReplyFail($"[RynthAi] Buff items: '{rest}' isn't in the list ({ListedNames(s)}). /ra buffitems add <name> adds one.");
                else
                    ReplyOk($"[RynthAi] Buff items: {shown} is {(allow ? "allowed" : "blocked")}{(r == BuffItemRules.EditResult.Unchanged ? " (it already was)" : "")}."
                        + (allow && !s.UseBuffItems ? " Use Buff Items is off, so nothing is used until it is on." : ""));
                PushBuffItemRows(force: true);
                break;
            }

            case "add":
            {
                if (rest.Length == 0) { ReplyFail($"[RynthAi] Buff items: no name given (or select the item and use addselected). Usage: {BuffItemsUsage}"); break; }
                if (_buffManager == null) { ReplyFail("[RynthAi] Buff items: buffing isn't ready yet (log in first)."); break; }
                bool ok = _buffManager.AddBuffItemByName(rest, out string reply);
                SayBuffItems(ok, reply);
                break;
            }

            case "addselected":
            {
                if (_buffManager == null) { ReplyFail("[RynthAi] Buff items: buffing isn't ready yet (log in first)."); break; }
                bool ok = _buffManager.AddSelectedBuffItem(out string reply);
                SayBuffItems(ok, reply);
                break;
            }

            case "remove":
            case "delete":
            {
                if (rest.Length == 0) { ReplyFail($"[RynthAi] Buff items: which item? Usage: {BuffItemsUsage}"); break; }
                var r = s.RemoveCustomBuffItem(rest, out string shown);
                if (r == BuffItemRules.EditResult.Removed)
                    ReplyOk($"[RynthAi] Buff items: removed {shown}.");
                else if (r == BuffItemRules.EditResult.BuiltIn)
                    ReplyFail($"[RynthAi] Buff items: {shown} is built in and can't be removed; /ra buffitems block {shown} stops its use.");
                else
                    ReplyFail($"[RynthAi] Buff items: '{rest}' isn't one of your items ({ListedNames(s)}).");
                PushBuffItemRows(force: true);
                break;
            }

            case "repeat":
            {
                int sp = rest.IndexOf(' ');
                if (sp < 0 || !int.TryParse(rest.Substring(0, sp), out int minutes))
                {
                    ReplyFail($"[RynthAi] Buff items: /ra buffitems repeat <minutes> <name> sets how often an item with no spell to check is used. Usage: {BuffItemsUsage}");
                    break;
                }
                var item = BuffItemRules.FindCustom(s.CustomBuffItems, rest.Substring(sp + 1));
                if (item == null) { ReplyFail($"[RynthAi] Buff items: '{rest.Substring(sp + 1).Trim()}' isn't one of your items ({ListedNames(s)})."); break; }
                int set = BuffItemRules.ClampRepeatMinutes(minutes);
                item.RepeatMinutes = set;
                ReplyOk($"[RynthAi] Buff items: {item.Name} is used every {set} min when it casts no spell RynthAi can check"
                    + (set != minutes ? $" ({minutes} is outside {BuffItemRules.MinRepeatMinutes}-{BuffItemRules.MaxRepeatMinutes})" : "")
                    + (item.SpellsRead && item.SpellIds.Count > 0 ? "; it does cast one, so its buff is what counts." : "."));
                PushBuffItemRows(force: true);
                break;
            }

            default:
                ReplyFail($"[RynthAi] Buff items: unknown '{parts[2]}'. Usage: {BuffItemsUsage}");
                break;
        }
    }

    /// <summary>A reply for the player and, when the window is open, its status line.</summary>
    private void SayBuffItems(bool ok, string reply)
    {
        if (ok) ReplyOk(reply); else ReplyFail(reply);
        _spellsUi?.SetBuffItemsStatus(reply.StartsWith("[RynthAi] ", StringComparison.Ordinal) ? reply.Substring(10) : reply, !ok);
        PushBuffItemRows(force: true);
    }

    private static string ListedNames(LegacyUiSettings s)
    {
        var names = new List<string>();
        foreach (var e in BuffItemEntry.Build(s.BlockedBuffItems, s.CustomBuffItems)) names.Add(e.Name);
        return string.Join(", ", names);
    }

    private void ListBuffItems(LegacyUiSettings s)
    {
        ChatLine($"[RynthAi] Use Buff Items: {(s.UseBuffItems ? "on" : "off")}. Buff items (one used at a time when its buff isn't on):");
        List<BuffItemRow> rows = _buffManager?.DescribeBuffItems() ?? RowsWithoutBuffing(s);
        foreach (var r in rows)
        {
            var sb = new StringBuilder("  ");
            sb.Append(r.Allowed ? "[x] " : "[ ] ").Append(r.Name).Append(r.BuiltIn ? " (built in)" : "");
            if (r.InPack > 0) sb.Append(", ").Append(r.InPack).Append(" in pack");
            sb.Append(": ").Append(r.Status);
            if (r.Spells.Length > 0) sb.Append(". Checks: ").Append(r.Spells);
            ChatLine(sb.ToString());
        }
        ChatLine($"[RynthAi] {BuffItemsUsage}. The list is in the Spells window, Buff Items tab (/ra buffitems open).");
    }

    private static List<BuffItemRow> RowsWithoutBuffing(LegacyUiSettings s)
    {
        var rows = new List<BuffItemRow>();
        foreach (var e in BuffItemEntry.Build(s.BlockedBuffItems, s.CustomBuffItems))
            rows.Add(new BuffItemRow { Name = e.Name, BuiltIn = e.BuiltIn, Allowed = e.Allowed, Status = e.Allowed ? "allowed" : "blocked", UsesTimer = e.UsesTimer, RepeatMinutes = e.RepeatMinutes });
        return rows;
    }

    /// <summary>Pump thread: the open Spells window's Buff Items tab gets the list every 2 s (at once after a change).</summary>
    private void PushBuffItemRows(bool force = false)
    {
        var ui = _spellsUi;
        if (ui == null || !ui.Visible || _buffManager == null) return;
        long now = Environment.TickCount64;
        if (!force && now - _buffItemRowsPushedAt < SpellsKnownPushMs) return;
        _buffItemRowsPushedAt = now;
        ui.SetBuffItemRows(_buffManager.DescribeBuffItems());
    }
}
