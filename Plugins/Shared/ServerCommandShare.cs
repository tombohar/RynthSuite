// ============================================================================
//  RynthSuite - Plugins/Shared/ServerCommandShare.cs
//  Server chat commands one plugin hands another for a RynthCore section, through the
//  plugin-command broker (Host.SendPluginCommand, action "servercommands").
//
//  A server plugin (RynthILT for Infinite Leaftide's /fship) knows its server's extra
//  commands and keeps them as a ServerCommandSet; it draws them itself (ServerCommandPanel.cs).
//  For another plugin's view of that section (SilentKelpie's RynthAi Fellowship tab, not in
//  this tree yet) the sender re-sends every few seconds while its server is active and sends
//  an empty list when it isn't, so the receiver drops a list that stops arriving (the sender
//  unloaded).
//
//  Payload: ANSI text, one record per line, fields split by tabs:
//    S <tab> section <tab> serverId <tab> serverName
//    C <tab> label <tab> command <tab> flags <tab> tooltip      (0..MaxCommands lines)
//  Flags: 'a' takes an optional argument, 'A' needs one, 'c' asks before it runs.
//  The receiver checks every field again: a command is one /word line of printable text.
//  Pure (no ImGui, no host); compiled into each plugin that uses it and into the tests.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.Shared;

/// <summary>One server command: a button label, the chat command and how it takes an argument.</summary>
internal sealed record ServerCommand(string Label, string Command, string Flags, string Tooltip)
{
    /// <summary>True when the command can take an argument (the name box).</summary>
    public bool TakesArgument => Flags.IndexOf('a') >= 0 || Flags.IndexOf('A') >= 0;

    /// <summary>True when the command can't run without an argument.</summary>
    public bool NeedsArgument => Flags.IndexOf('A') >= 0;

    /// <summary>True when a click asks before running (the second click runs it).</summary>
    public bool Confirm => Flags.IndexOf('c') >= 0;
}

/// <summary>The commands one server plugin gives a section; empty Commands means none.</summary>
internal sealed record ServerCommandSet(string Section, string ServerId, string ServerName, IReadOnlyList<ServerCommand> Commands);

internal static class ServerCommandShare
{
    /// <summary>The broker action the receiver handles.</summary>
    public const string Action = "servercommands";

    /// <summary>Most commands a set carries.</summary>
    public const int MaxCommands = 24;

    /// <summary>Longest section id, server id and flags field.</summary>
    public const int MaxId = 32;

    /// <summary>Longest label and server name.</summary>
    public const int MaxLabel = 48;

    /// <summary>Longest command (before its argument).</summary>
    public const int MaxCommand = 96;

    /// <summary>Longest tooltip.</summary>
    public const int MaxTooltip = 300;

    /// <summary>Longest argument (a player or fellowship name).</summary>
    public const int MaxArgument = 64;

    /// <summary>The payload for <paramref name="set"/>; commands that don't pass <see cref="IsValid"/> are left out.</summary>
    public static string Build(ServerCommandSet set)
    {
        var sb = new StringBuilder(256);
        sb.Append('S').Append('\t').Append(Clean(set.Section, MaxId))
          .Append('\t').Append(Clean(set.ServerId, MaxId))
          .Append('\t').Append(Clean(set.ServerName, MaxLabel)).Append('\n');
        int n = 0;
        foreach (ServerCommand c in set.Commands)
        {
            if (n >= MaxCommands) break;
            ServerCommand clean = Normalize(c);
            if (!IsValid(clean)) continue;
            sb.Append('C').Append('\t').Append(clean.Label)
              .Append('\t').Append(clean.Command)
              .Append('\t').Append(clean.Flags)
              .Append('\t').Append(clean.Tooltip).Append('\n');
            n++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The set in <paramref name="payload"/>, or null when it has no S line with a section.
    /// Bad C lines are skipped; at most <see cref="MaxCommands"/> are kept.
    /// </summary>
    public static ServerCommandSet? Parse(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > 32_768) return null;
        string section = string.Empty, serverId = string.Empty, serverName = string.Empty;
        bool haveHeader = false;
        var commands = new List<ServerCommand>();
        foreach (string raw in payload.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[1] != '\t') continue;
            string[] f = line.Split('\t');
            if (line[0] == 'S' && !haveHeader && f.Length >= 4)
            {
                section = Clean(f[1], MaxId).ToLowerInvariant();
                serverId = Clean(f[2], MaxId).ToLowerInvariant();
                serverName = Clean(f[3], MaxLabel);
                haveHeader = section.Length > 0;
            }
            else if (line[0] == 'C' && f.Length >= 5 && commands.Count < MaxCommands)
            {
                ServerCommand c = Normalize(new ServerCommand(f[1], f[2], f[3], f[4]));
                if (IsValid(c)) commands.Add(c);
            }
        }
        return haveHeader ? new ServerCommandSet(section, serverId, serverName, commands) : null;
    }

    /// <summary>
    /// True when <paramref name="c"/> (normalized) can run: a label, and a command that is one
    /// "/word ..." line of printable text with no quotes.
    /// </summary>
    public static bool IsValid(ServerCommand c)
    {
        if (c.Label.Length == 0 || c.Command.Length < 2 || c.Command.Length > MaxCommand) return false;
        if (c.Command[0] != '/' || !char.IsLetter(c.Command[1])) return false;
        foreach (char ch in c.Command)
            if (char.IsControl(ch) || ch == '"') return false;
        return true;
    }

    /// <summary>
    /// The chat line for <paramref name="c"/> with <paramref name="argument"/>, or null when the
    /// command needs an argument and has none. A multi-word argument is quoted (ACE's command
    /// parser joins "two words"); quotes inside it are dropped.
    /// </summary>
    public static string? Compose(ServerCommand c, string? argument)
    {
        string arg = CleanArgument(argument);
        if (arg.Length == 0) return c.NeedsArgument ? null : c.Command;
        if (!c.TakesArgument) return c.Command;
        return c.Command + " " + (arg.IndexOf(' ') >= 0 ? "\"" + arg + "\"" : arg);
    }

    /// <summary>The argument with control characters and quotes removed, spaces collapsed and trimmed, at most <see cref="MaxArgument"/> characters.</summary>
    public static string CleanArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return string.Empty;
        var sb = new StringBuilder(argument.Length);
        bool space = false;
        foreach (char ch in argument)
        {
            if (char.IsControl(ch) || ch == '"') continue;
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
            if (sb.Length >= MaxArgument) break;
        }
        return sb.ToString();
    }

    /// <summary>One field: tabs, newlines and control characters become spaces, trimmed, cut to <paramref name="max"/>.</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(Math.Min(text.Length, max));
        foreach (char ch in text)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }

    // Every field cleaned; flags keep only the known letters. The command is cut one past its
    // limit so an over-long one fails IsValid instead of running cut short.
    private static ServerCommand Normalize(ServerCommand c)
    {
        var flags = new StringBuilder(4);
        foreach (char ch in c.Flags ?? string.Empty)
            if ((ch == 'a' || ch == 'A' || ch == 'c') && flags.ToString().IndexOf(ch) < 0) flags.Append(ch);
        return new ServerCommand(Clean(c.Label, MaxLabel), Clean(c.Command, MaxCommand + 1), flags.ToString(), Clean(c.Tooltip, MaxTooltip));
    }
}
