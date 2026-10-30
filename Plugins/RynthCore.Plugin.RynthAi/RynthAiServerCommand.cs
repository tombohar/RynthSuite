using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>RynthCore's server list and the active server owner's profile, read on demand for /ra server.</summary>
    private readonly ServerStateClient _serverState = new();

    /// <summary>
    /// /ra server: which server RynthCore says this is and why, every server it knows with this
    /// character's override, the plugin that owns the active server and what its settings say,
    /// and whether a server plugin's hub (the Mini Remote's server tabs) is connected. One check that every
    /// plugin is reading the same server identity. Plain ASCII (the AC chat can't draw more).
    /// </summary>
    private void HandleServerCommand()
    {
        _serverState.Poll(Host, Environment.TickCount64, force: true);
        EngineServerState? st = _serverState.State;
        string world = Host.HasGetWorldName && Host.TryGetWorldName(out string w) ? w.Trim() : "";
        if (st == null)
        {
            ChatLine(_serverState.Supported
                ? "[RynthAi] Server: RynthCore didn't answer GetServerStateJson."
                : $"[RynthAi] Server: this engine (plugin API v{Host.Version}) doesn't report its server list (v86 does). World name: '{world}'.");
            return;
        }

        ChatLine(st.HasActive
            ? $"[RynthAi] Server: {st.ActiveName} ({st.Active}), {(st.Picked ? "picked by hand" : "detected or per-server override")}; settings from {(st.ActivePlugin.Length > 0 ? st.ActivePlugin : "no loaded plugin")}."
            : $"[RynthAi] Server: none active (retail features only){(st.Picked ? ", picked by hand" : "")}.");
        ChatLine($"[RynthAi] Host '{st.Host}', announced world '{st.World}', client world name '{world}', pick '{st.Pick}'.");
        foreach (EngineServer s in st.Servers)
        {
            string flags = (s.Active ? "ACTIVE" : "off") + (s.Detected ? ", detected" : "") + (s.Force != "auto" ? ", forced " + s.Force : "");
            ChatLine($"[RynthAi]   {s.Id} ({s.Name}): {flags}; owner {(s.Plugin.Length > 0 ? s.Plugin : "-")}{(s.Reason.Length > 0 ? "; " + s.Reason : "")}");
        }

        if (st.ActivePlugin.Length > 0)
        {
            ServerProfileView? p = ServerProfileView.Parse(_serverState.ProfileJson);
            ChatLine(p != null
                ? $"[RynthAi] {_serverState.ProfilePlugin} {RynthServerProfileApiV1.InterfaceName}: {p.Summary()}"
                : $"[RynthAi] {st.ActivePlugin} sent no server settings ({RynthServerProfileApiV1.InterfaceName} not answered - an older plugin?).");
        }

        var hub = _serverHub;
        if (hub == null || !hub.Loaded)
            ChatLine("[RynthAi] Mini Remote server tabs: no server plugin hub connected (none loaded for this world).");
        else
        {
            string id = hub.Text(RynthServerHub.TextServerId);
            ChatLine($"[RynthAi] Mini Remote server tabs: connected{(id.Length > 0 ? " (server '" + id + "')" : string.Empty)}, "
                     + (hub.ServerActive ? "its server is this world." : "its server is not this world (nothing shown)."));
        }
        ChatLine("[RynthAi] Change the server: RynthCore Options > Diagnostics > Server, or /rc server pick <id>|auto.");
    }
}
