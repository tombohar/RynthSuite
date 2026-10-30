// ServerMenuModel.cs - The servers and sections RynthCore pushes for the Mini Remote's menus.
//
// RynthCore decides which servers the client is on (one world check for the engine and every
// plugin, with a per-character Auto / On / Off override) and what the engine and the installed
// plugins add for each (UI/ServerMenus.cs and UI/Sections in RynthCore). It sends that list as
// the "servermenu" remote command when it changes, and again every 30 s. Tab-separated lines:
//   V  id  name  active 0/1  force auto|on|off  detected 0/1   every server (RynthCore 2026.10.5.46+)
//   X  id  label                                               every Mini Remote section, rail order (.46+)
//   S  id  name  active 0/1  force command                     a server with items
//   I  label  command  tooltip  feature                        its items
// A "tab:<id>" feature is one of RynthAi's own Mini Remote tabs (older RynthCore); "section:<id>"
// opens that section in RynthCore's Mini Remote (its command does that).
//
// Groups stays null until the first push: an older RynthCore, or the ImGui shell off. The menu
// then falls back to RynthILT's own ILT entries (RynthILT.Hub section "server-menu"). Servers stays
// empty until a RynthCore that sends V lines: the world check is then RynthILT's own.
using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Plugin.RynthAi.Huds;

/// <summary>A server section item: an engine "/rc" command, or a Mini Remote tab ("tab:&lt;id&gt;" feature).</summary>
internal sealed record ServerMenuEntry(string Label, string Command, string Tooltip, string Feature);

/// <summary>One server's section: shown while <see cref="Active"/> (detected or forced in RynthCore).</summary>
internal sealed record ServerMenuGroup(string Id, string Name, bool Active, string ForceCommand, ServerMenuEntry[] Items);

/// <summary>RynthCore's verdict for one server: active now, its saved override ("auto", "on", "off"), what detection said.</summary>
internal sealed record ServerVerdictLine(string Id, string Name, bool Active, string Force, bool Detected);

/// <summary>One of RynthCore's Mini Remote sections.</summary>
internal sealed record SectionLine(string Id, string Label);

/// <summary>Everything one push carries.</summary>
internal sealed record ServerMenuPayload(ServerMenuGroup[] Groups, ServerVerdictLine[] Servers, SectionLine[] Sections);

internal static class ServerMenuModel
{
    /// <summary>Feature prefix for items that are Mini Remote tabs (drawn by RynthAi itself).</summary>
    public const string TabFeaturePrefix = "tab:";

    /// <summary>Feature prefix for items that open a section of RynthCore's Mini Remote.</summary>
    public const string SectionFeaturePrefix = "section:";

    private static ServerMenuGroup[]? _groups;
    private static ServerVerdictLine[] _servers = Array.Empty<ServerVerdictLine>();
    private static SectionLine[] _sections = Array.Empty<SectionLine>();

    /// <summary>The latest sections from RynthCore; null until it sends any. Any thread.</summary>
    public static ServerMenuGroup[]? Groups => Volatile.Read(ref _groups);

    /// <summary>The latest server verdicts (empty from a RynthCore older than 2026.10.5.46). Any thread.</summary>
    public static ServerVerdictLine[] Servers => Volatile.Read(ref _servers);

    /// <summary>RynthCore's Mini Remote sections, rail order (empty from an older RynthCore). Any thread.</summary>
    public static SectionLine[] Sections => Volatile.Read(ref _sections);

    /// <summary>Pump thread: stores a "servermenu" push. A malformed line is skipped, never thrown.</summary>
    public static void Apply(string payload)
    {
        ServerMenuPayload p = ParsePayload(payload);
        Volatile.Write(ref _servers, p.Servers);
        Volatile.Write(ref _sections, p.Sections);
        Volatile.Write(ref _groups, p.Groups);
    }

    /// <summary>
    /// RynthCore's verdict for server <paramref name="id"/>: true / false once it sends V lines,
    /// null before (an older RynthCore, or no push yet). Any thread.
    /// </summary>
    public static bool? ServerActive(string id)
    {
        foreach (ServerVerdictLine s in Servers)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return s.Active;
        return null;
    }

    /// <summary>RynthCore's line for server <paramref name="id"/>, or null.</summary>
    public static ServerVerdictLine? Server(string id)
    {
        foreach (ServerVerdictLine s in Servers)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>
    /// Parses a payload's server groups. Items before the first section line, short lines and
    /// unknown line kinds are skipped.
    /// </summary>
    public static ServerMenuGroup[] Parse(string? payload) => ParsePayload(payload).Groups;

    /// <summary>Parses a whole payload (groups, server verdicts, sections). Never throws.</summary>
    public static ServerMenuPayload ParsePayload(string? payload)
    {
        var groups = new List<ServerMenuGroup>();
        var servers = new List<ServerVerdictLine>();
        var sections = new List<SectionLine>();
        if (string.IsNullOrEmpty(payload))
            return new ServerMenuPayload(groups.ToArray(), servers.ToArray(), sections.ToArray());

        string id = "", name = "", force = "";
        bool active = false, open = false;
        var items = new List<ServerMenuEntry>();

        void Close()
        {
            if (open) groups.Add(new ServerMenuGroup(id, name, active, force, items.ToArray()));
            items.Clear();
            open = false;
        }

        foreach (string raw in payload.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f[0] == "S" && f.Length >= 5)
            {
                Close();
                id = f[1];
                name = f[2];
                active = f[3] == "1";
                force = f[4];
                open = id.Length > 0 && name.Length > 0;
            }
            else if (f[0] == "I" && f.Length >= 5 && open && f[1].Length > 0)
            {
                items.Add(new ServerMenuEntry(f[1], f[2], f[3], f[4]));
            }
            else if (f[0] == "V" && f.Length >= 6 && f[1].Length > 0)
            {
                servers.Add(new ServerVerdictLine(f[1], f[2].Length > 0 ? f[2] : f[1], f[3] == "1", f[4], f[5] == "1"));
            }
            else if (f[0] == "X" && f.Length >= 3 && f[1].Length > 0 && f[2].Length > 0)
            {
                sections.Add(new SectionLine(f[1], f[2]));
            }
        }
        Close();
        return new ServerMenuPayload(groups.ToArray(), servers.ToArray(), sections.ToArray());
    }

    /// <summary>The tab id of a "tab:&lt;id&gt;" feature, else null.</summary>
    public static string? TabOf(string feature) =>
        feature.StartsWith(TabFeaturePrefix, StringComparison.OrdinalIgnoreCase) && feature.Length > TabFeaturePrefix.Length
            ? feature.Substring(TabFeaturePrefix.Length)
            : null;

    /// <summary>The section id of a "section:&lt;id&gt;" feature, else null.</summary>
    public static string? SectionOf(string feature) =>
        feature.StartsWith(SectionFeaturePrefix, StringComparison.OrdinalIgnoreCase) && feature.Length > SectionFeaturePrefix.Length
            ? feature.Substring(SectionFeaturePrefix.Length)
            : null;
}
