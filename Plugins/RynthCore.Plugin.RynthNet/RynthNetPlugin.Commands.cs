// ============================================================================
//  RynthNet - RynthNetPlugin.Commands.cs
//  /rn chat commands, and UtilityBelt's /ub bc, /ub bct, /ub netclients
//  (forwarded by RynthAi's /ub layer, or typed when RynthAi isn't loaded).
//  Pump thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthNet.Transport;

namespace RynthCore.Plugin.RynthNet;

public sealed partial class RynthNetPlugin
{
    private void HandleLocal(NetNode node, string line)
    {
        string t = line.Trim();
        if (t.StartsWith("/ub", StringComparison.OrdinalIgnoreCase))
        {
            HandleUb(node, t.Length > 3 ? t.Substring(3).Trim() : "");
            return;
        }
        string rest = t.Length > 3 ? t.Substring(3).Trim() : "";
        string verb = FirstWord(rest, out string args).ToLowerInvariant();
        switch (verb)
        {
            case "":
            case "help":
                Help();
                break;
            case "bc":      // every other client
            case "bca":     // every client, this one too
            {
                CommandParser.DelayAndCommand(args, out int delay, out string command);
                Report(SendCommand(NetTarget.All, command, delay, includeSelf: verb == "bca"), command, "every client");
                break;
            }
            case "bct":     // clients with any of the tags (this one too when it has one)
            {
                if (!CommandParser.TagsAndRest(args, out List<string> tags, out string after))
                {
                    ChatFail("usage: /rn bct <tag[,tag]> [delayMs] <command>");
                    break;
                }
                CommandParser.DelayAndCommand(after, out int delay, out string command);
                var target = new NetTarget(TargetKind.Tags, string.Join(",", tags));
                Report(SendCommand(target, command, delay, includeSelf: true), command, "tag " + string.Join(",", tags));
                break;
            }
            case "tell":    // one client, by character name
            {
                var known = node.GetPeers().Select(p => p.Identity.Character).Where(c => c.Length > 0);
                if (!CommandParser.NameAndCommand(args, known, out string name, out string command))
                {
                    ChatFail("usage: /rn tell <character>[,] <command>");
                    break;
                }
                if (!node.GetPeers().Any(p => string.Equals(p.Identity.Character, name, StringComparison.OrdinalIgnoreCase)))
                {
                    ChatFail($"no client is playing {name} (/rn list).");
                    break;
                }
                Report(SendCommand(new NetTarget(TargetKind.Character, name), command, 0, includeSelf: false), command, name);
                break;
            }
            case "list":
            case "clients":
                ListClients(node, args);
                break;
            case "tag":
            case "tags":
                TagCommand(args);
                break;
            case "status":
                StatusCommand(node);
                break;
            case "remote":
                RemoteCommand(args);
                break;
            case "allow":
                ListSetting(p => p.Allow, "allow", args, "remote commands run only if they start with one of these (empty = all)");
                break;
            case "deny":
                ListSetting(p => p.Deny, "deny", args, "remote commands starting with one of these never run");
                break;
            case "trust":
                ListSetting(p => p.TrustedSenders, "trust", args, "only these characters' commands run here (empty = any of your clients)");
                break;
            case "followme":
            {
                if (!_loggedIn) { ChatFail("log in first."); break; }
                NetTarget target = args.Length > 0 && CommandParser.TagsAndRest(args, out List<string> tags, out _)
                    ? new NetTarget(TargetKind.Tags, string.Join(",", tags)) : NetTarget.All;
                string command = "/ra follow " + _character;
                Report(SendCommand(target, command, 0, includeSelf: false), command, target.Kind == TargetKind.All ? "every client" : "tag " + target.Value);
                break;
            }
            case "usesel":
            case "useselected":
            {
                uint sel = Host.GetSelectedItemId();
                if (sel == 0) { ChatFail("select something first."); break; }
                NetTarget target = args.Length > 0 && CommandParser.TagsAndRest(args, out List<string> tags, out _)
                    ? new NetTarget(TargetKind.Tags, string.Join(",", tags)) : NetTarget.All;
                // The same command UtilityBelt's network window sends (RynthAi's meta expressions run it).
                string command = $"/ub mexec actiontryuseitem[wobjectfindbyid[{(int)sel}]]";
                Report(SendCommand(target, command, 0, includeSelf: false), command, target.Kind == TargetKind.All ? "every client" : "tag " + target.Value);
                break;
            }
            default:
                ChatFail($"unknown command '{verb}'. /rn help lists them.");
                break;
        }
    }

    private void HandleUb(NetNode node, string rest)
    {
        string verb = FirstWord(rest, out string args).ToLowerInvariant();
        switch (verb)
        {
            case "bc":
            {
                // UtilityBelt: runs on every client, this one included, the n-th remote after n x delay.
                CommandParser.DelayAndCommand(args, out int delay, out string command);
                Report(SendCommand(NetTarget.All, command, delay, includeSelf: true), command, "every client");
                break;
            }
            case "bct":
            {
                if (!CommandParser.TagsAndRest(args, out List<string> tags, out string after))
                {
                    ChatFail("usage: /ub bct <tag[,tag]> [millisecondDelay] <command>");
                    break;
                }
                CommandParser.DelayAndCommand(after, out int delay, out string command);
                var target = new NetTarget(TargetKind.Tags, string.Join(",", tags));
                Report(SendCommand(target, command, delay, includeSelf: true), command, "tag " + string.Join(",", tags));
                break;
            }
            case "netclients":
                ListClients(node, args);
                break;
            default:
                ChatFail($"/ub {verb} isn't a network command.");
                break;
        }
    }

    private void Report(List<string>? names, string command, string to)
    {
        if (names == null) return;
        if (names.Count == 0) { ChatFail($"nobody to send '{command}' to ({to}). /rn list shows the clients."); return; }
        ChatLine($"'{command}' -> {string.Join(", ", names)}");
    }

    private static string FirstWord(string s, out string rest)
    {
        s = (s ?? "").Trim();
        int sp = s.IndexOf(' ');
        if (sp < 0) { rest = ""; return s; }
        rest = s.Substring(sp + 1).Trim();
        return s.Substring(0, sp);
    }

    private void Help()
    {
        ChatLine("=== RynthNet: your clients on this PC ===");
        ChatLine("/rn bc [delayMs] <command> - run on every other client (delay staggers them)");
        ChatLine("/rn bca [delayMs] <command> - run on every client, this one too");
        ChatLine("/rn bct <tag[,tag]> [delayMs] <command> - run on clients with a tag (this one too if tagged)");
        ChatLine("/rn tell <character>[,] <command> - run on one client");
        ChatLine("/rn list [tag] - clients, tags, vitals, bot state");
        ChatLine("/rn tag [list | add <tag> | remove <tag> | clear] - this character's tags");
        ChatLine("/rn followme [tag] - the others /ra follow you;  /rn usesel [tag] - they use your selection");
        ChatLine("/rn status - this client;  /rn remote on|off|default - run others' commands on this character");
        ChatLine("/rn allow|deny|trust [list | add <x> | remove <x> | clear] - what may run here");
        ChatLine("UtilityBelt's /ub bc, /ub bct and /ub netclients work too.");
    }

    // ── /rn list ────────────────────────────────────────────────────────────

    private void ListClients(NetNode node, string args)
    {
        List<string> filter = new();
        if (args.Length > 0) CommandParser.TagsAndRest(args, out filter, out _);
        if (!node.IsRunning) { ChatFail("offline: " + (_offlineReason.Length > 0 ? _offlineReason : "stopped")); return; }

        var peers = new List<PeerInfo>(node.GetPeers());
        peers.Sort((a, b) => string.Compare(a.Identity.Character, b.Identity.Character, StringComparison.OrdinalIgnoreCase));
        if (filter.Count > 0) peers.RemoveAll(p => !p.Identity.HasAnyTag(filter.ToArray()));

        string mine = MyTags.Count > 0 ? " [" + string.Join(",", MyTags) + "]" : "";
        ChatLine($"{peers.Count} other client(s){(filter.Count > 0 ? " tagged " + string.Join(",", filter) : "")}; you: {(_loggedIn ? _character : "not logged in")}{mine}");
        long now = NetNode.NowMs;
        foreach (PeerInfo p in peers)
            ChatLine("  " + DescribePeer(p, now));
    }

    private static string DescribePeer(PeerInfo p, long now)
    {
        var sb = new StringBuilder();
        NodeIdentity id = p.Identity;
        sb.Append(id.Character.Length > 0 ? id.Character : "(not logged in)");
        if (id.Tags.Length > 0) sb.Append(" [").Append(string.Join(",", id.Tags)).Append(']');
        if (p.StatusJson.Length > 2 && now - p.StatusAtMs < 15_000)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(p.StatusJson);
                JsonElement s = doc.RootElement;
                if (Num(s, "hpMax") > 0)
                    sb.Append(CultureInfo.InvariantCulture, $" - H {Num(s, "hp"):0}/{Num(s, "hpMax"):0} S {Num(s, "st"):0}/{Num(s, "stMax"):0} M {Num(s, "mp"):0}/{Num(s, "mpMax"):0}");
                if (s.TryGetProperty("ns", out JsonElement ns) && s.TryGetProperty("ew", out JsonElement ew)
                    && ns.ValueKind == JsonValueKind.Number && ew.ValueKind == JsonValueKind.Number)
                {
                    double n = ns.GetDouble(), e = ew.GetDouble();
                    sb.Append(CultureInfo.InvariantCulture, $" @ {Math.Abs(n):0.0}{(n >= 0 ? "N" : "S")} {Math.Abs(e):0.0}{(e >= 0 ? "E" : "W")}");
                }
                if (s.TryGetProperty("portal", out JsonElement portal) && portal.ValueKind == JsonValueKind.True) sb.Append(" (portal space)");
                if (s.TryGetProperty("bot", out JsonElement bot) && bot.ValueKind == JsonValueKind.Object)
                {
                    bool macro = bot.TryGetProperty("macro", out JsonElement m) && m.ValueKind == JsonValueKind.True;
                    string action = bot.TryGetProperty("action", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                    sb.Append(" - bot ").Append(macro ? "on" : "off");
                    if (macro && action.Length > 0 && action != "Default") sb.Append(": ").Append(action);
                }
            }
            catch (JsonException) { }
        }
        else if (id.LoggedIn)
        {
            // Its pipe still answers (the transport has its own threads) but its game stopped
            // ticking: a hung client shows here instead of vanishing.
            sb.Append(p.StatusAtMs > 0
                ? string.Create(CultureInfo.InvariantCulture, $" - no status for {(now - p.StatusAtMs) / 1000} s")
                : " - no status yet");
        }
        if (p.RttMs >= 0) sb.Append(CultureInfo.InvariantCulture, $" - {p.RttMs} ms");
        return sb.ToString();
    }

    private static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // ── Tags and settings (per character, saved with merge: see RynthNetSettings) ──

    private void TagCommand(string args)
    {
        string verb = FirstWord(args, out string value).ToLowerInvariant();
        if (!_loggedIn) { ChatFail("log in first: tags belong to a character."); return; }
        switch (verb)
        {
            case "":
            case "list":
                ChatLine(MyTags.Count > 0 ? $"{_character}'s tags: {string.Join(", ", MyTags)}" : $"{_character} has no tags. /rn tag add <tag>");
                break;
            case "add":
                if (SetTag(value, on: true, out string whyAdd)) ChatLine($"tagged {_character}: {string.Join(", ", MyTags)}");
                else ChatFail(whyAdd);
                break;
            case "remove":
            case "rm":
            case "del":
                if (!SetTag(value, on: false, out string whyRemove)) ChatFail(whyRemove);
                else ChatLine(MyTags.Count > 0 ? $"{_character}'s tags: {string.Join(", ", MyTags)}" : $"{_character} has no tags now.");
                break;
            case "clear":
                string key = CharacterKey;
                if (UpdateSettings(s => s.Character(key).Tags.Clear())) ChatLine($"{_character} has no tags now.");
                break;
            default:
                ChatFail("usage: /rn tag [list | add <tag> | remove <tag> | clear]");
                break;
        }
    }

    /// <summary>Adds or removes one of this character's tags, saves, tells the others.</summary>
    internal bool SetTag(string tag, bool on, out string why)
    {
        why = "";
        tag = (tag ?? "").Trim().Trim('"');
        if (!_loggedIn) { why = "log in first: tags belong to a character."; return false; }
        if (tag.Length == 0 || tag.Length > Wire.MaxTagLength || tag.Contains(',') || tag.Any(char.IsWhiteSpace))
        {
            why = $"a tag is 1-{Wire.MaxTagLength} characters, no spaces or commas.";
            return false;
        }
        bool has = MyTags.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
        if (on && has) { why = $"{_character} already has tag {tag}."; return false; }
        if (on && MyTags.Count >= Wire.MaxTags) { why = $"at most {Wire.MaxTags} tags."; return false; }
        if (!on && !has) { why = $"{_character} has no tag {tag}."; return false; }
        string key = CharacterKey;
        bool saved = UpdateSettings(s =>
        {
            List<string> tags = s.Character(key).Tags;
            tags.RemoveAll(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
            if (on) tags.Add(tag);
        });
        if (!saved) why = "couldn't save the tag.";
        return saved;
    }

    private void StatusCommand(NetNode node)
    {
        if (!node.IsRunning)
        {
            ChatFail("offline: " + (_offlineReason.Length > 0 ? _offlineReason : "stopped"));
            return;
        }
        RynthNetPolicy p = MyPolicy;
        bool own = _loggedIn && _settings.Characters.TryGetValue(CharacterKey, out RynthNetCharacter? c) && c.Policy != null;
        ChatLine($"online as {node.NodeId} ({node.GetPeers().Length} other client(s)); pipe {node.PipeName}");
        ChatLine($"remote commands: {(p.AcceptRemoteCommands ? "on" : "off")} ({(own ? "this character's own policy" : "defaults")}), " +
                 $"allow {p.Allow.Count}, deny {p.Deny.Count}, trusted {(p.TrustedSenders.Count == 0 ? "any" : p.TrustedSenders.Count.ToString(CultureInfo.InvariantCulture))}; " +
                 $"logged to chat: {(_settings.LogRemoteCommands ? "yes" : "no")}; status every {_settings.StatusIntervalMs} ms; RynthAi {(_rynthAiPresent ? "found" : "not loaded")}");
        ChatLine("settings: " + RynthNetSettings.FilePath);
        if (node.DroppedEvents > 0) ChatLine($"{node.DroppedEvents} network event(s) dropped (queue full).");
    }

    private void RemoteCommand(string args)
    {
        string a = string.Join(" ", args.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        string key = CharacterKey;
        if (a is "log on" or "log off")
        {
            bool log = a == "log on";
            UpdateSettings(s => s.LogRemoteCommands = log);
        }
        else if (a is "on" or "off" or "default")
        {
            if (!_loggedIn) { ChatFail("log in first: the policy belongs to a character (the file's \"defaults\" cover the rest)."); return; }
            if (a == "default")
                UpdateSettings(s => { if (s.Characters.TryGetValue(key, out RynthNetCharacter? c)) c.Policy = null; });
            else
            {
                bool on = a == "on";
                UpdateSettings(s =>
                {
                    RynthNetCharacter c = s.Character(key);
                    c.Policy ??= s.Defaults.Clone();
                    c.Policy.AcceptRemoteCommands = on;
                });
            }
        }
        else if (a.Length > 0)
        {
            ChatFail("usage: /rn remote on|off|default   /rn remote log on|off");
            return;
        }
        ChatLine($"commands from your other clients {(MyPolicy.AcceptRemoteCommands ? "run here" : "are refused here")}" +
                 $"{(_settings.LogRemoteCommands ? ", each one written to chat" : "")}.");
    }

    /// <summary>/rn allow|deny|trust: one of this character's policy lists (a copy of the defaults until changed).</summary>
    private void ListSetting(Func<RynthNetPolicy, List<string>> select, string name, string args, string meaning)
    {
        string verb = FirstWord(args, out string value).ToLowerInvariant();
        value = value.Trim();
        if (verb is "" or "list")
        {
            List<string> list = select(MyPolicy);
            ChatLine(list.Count == 0 ? $"{name}: empty ({meaning})." : $"{name}: {string.Join(" | ", list)}");
            return;
        }
        if (verb is not ("add" or "remove" or "rm" or "clear") || (verb != "clear" && value.Length == 0))
        {
            ChatFail($"usage: /rn {name} [list | add <x> | remove <x> | clear] - {meaning}");
            return;
        }
        if (!_loggedIn) { ChatFail("log in first: the policy belongs to a character (the file's \"defaults\" cover the rest)."); return; }
        bool had = select(MyPolicy).Exists(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        string key = CharacterKey;
        bool saved = UpdateSettings(s =>
        {
            RynthNetCharacter c = s.Character(key);
            c.Policy ??= s.Defaults.Clone();
            List<string> list = select(c.Policy);
            if (verb == "clear") list.Clear();
            else list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
            if (verb == "add") list.Add(value);
        });
        if (!saved) return;
        List<string> now = select(MyPolicy);
        if (verb is "remove" or "rm" && !had) ChatFail($"{name} has no '{value}'.");
        else ChatLine(now.Count == 0 ? $"{name}: empty." : $"{name}: {string.Join(" | ", now)}");
    }
}
