using System;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// Which server this client is on, as RynthCore says (plugin API v86 GetServerStateJson, read by
/// ServerStateClient): the active server, with this character's overrides and the Diagnostics
/// page's pick applied, and the plugin that owns it. Re-read each second.
///
/// RynthOracle knows no server itself (since 2026-10-21): a server's own quests are its server
/// plugin's quest pack ("RynthServer.QuestPack", ServerQuestPackClient), which the catalog merges
/// under the pack's own tag, and a server's own pages are that plugin's. Before then RynthOracle
/// had its own server tags, detection and packs for three servers; they moved into those servers'
/// plugins. Without RynthCore's server list (an engine older than v86) no server is active here.
/// </summary>
internal sealed class ServerIdentity
{
    /// <summary>RynthCore's id of the active server ("" = none: retail quests only).</summary>
    public string ServerId { get; private set; } = "";
    /// <summary>Its name as RynthCore lists it ("" with none).</summary>
    public string ServerName { get; private set; } = "";
    /// <summary>The plugin that owns it, the one asked for the quest pack ("" when none is loaded).</summary>
    public string Plugin { get; private set; } = "";
    public string WorldName { get; private set; } = "";
    public string Source { get; private set; } = "not logged in";
    public bool Known => WorldName.Length > 0 || ServerId.Length > 0;
    public int Revision { get; private set; }

    /// <summary>The active server's name for the player: its name, its id, or "no server".</summary>
    public string DisplayName => ServerName.Length > 0 ? ServerName : ServerId.Length > 0 ? ServerId : "no server";

    public void Reset() => Set("", "", "", "", "not logged in");

    /// <param name="engine">RynthCore's server list (ServerStateClient); null on engines older than v86.</param>
    public void Refresh(RynthCoreHost host, EngineServerState? engine)
    {
        string fallbackWorld = host.HasGetWorldName && host.TryGetWorldName(out string fw) ? fw.Trim() : "";
        FromEngine(engine, fallbackWorld, out string id, out string name, out string plugin, out string world, out string source);
        Set(id, name, plugin, world, source);
    }

    /// <summary>
    /// The identity from RynthCore's server list: the active server decides (none active = no server,
    /// whatever the world is called). Pure: the tests drive it.
    /// </summary>
    internal static void FromEngine(EngineServerState? engine, string fallbackWorld,
                                    out string id, out string name, out string plugin, out string world, out string source)
    {
        if (engine == null)
        {
            id = name = plugin = "";
            world = (fallbackWorld ?? "").Trim();
            source = "RynthCore has no server list here (an engine older than API v86): retail quests only";
            return;
        }
        world = engine.World.Length > 0 ? engine.World : (fallbackWorld ?? "").Trim();
        if (!engine.HasActive)
        {
            id = name = plugin = "";
            source = "RynthCore: no server active (retail quests only)" + (engine.Picked ? ", picked by hand" : "");
            return;
        }
        id = engine.Active;
        name = engine.ActiveName.Length > 0 ? engine.ActiveName : engine.Active;
        plugin = engine.ActivePlugin;
        EngineServer? s = engine.Find(engine.Active);
        string how = engine.Picked ? "picked by hand"
            : s != null && s.Force.Equals("on", StringComparison.OrdinalIgnoreCase) ? "forced on for this character"
            : s != null && s.Reason.Length > 0 ? s.Reason : "detected";
        source = $"RynthCore: {name} ({how})" + (plugin.Length > 0 ? $"; its plugin {plugin}" : "; no plugin loaded for it");
    }

    private void Set(string id, string name, string plugin, string world, string source)
    {
        if (id == ServerId && name == ServerName && plugin == Plugin && world == WorldName && source == Source) return;
        ServerId = id;
        ServerName = name;
        Plugin = plugin;
        WorldName = world;
        Source = source;
        Revision++;
    }

    /// <summary>For tests: a fixed identity (an id, the world; name = id, no plugin unless given).</summary>
    internal void Force(string id, string world, string plugin = "") => Set(id ?? "", id ?? "", plugin, world ?? "", "forced");
}
