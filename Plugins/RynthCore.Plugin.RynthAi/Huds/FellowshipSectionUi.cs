// FellowshipSectionUi.cs - RynthCore's Fellowship section (RynthPluginRenderEmbed
// "section:fellowship") and the Mini Remote's Fellowship tab: the fellowship you are in, from
// FellowshipTracker. Render thread only.
//
// Name, leader, open / locked / XP sharing, and the members with their levels (you and the
// leader marked). The tracker reads AC's fellowship object, which AC's main thread (the render
// thread here) owns, and caches the member list for two seconds; this view re-reads it once a
// second. The Mini Remote tab also draws the active server's fellowship commands
// (ServerCommandModel, e.g. RynthILT's /fship); RynthCore's section shows the server plugin's
// own pane for those, so the embedded view leaves them out.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class FellowshipSectionUi
{
    /// <summary>The section id server plugins send fellowship commands for.</summary>
    public const string CommandSection = "fellowship";

    private const long RefreshMs = 1000;

    private static readonly Vector4 ColLeader = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 ColMute = new(0.80f, 0.84f, 0.90f, 1f);

    private readonly Func<FellowshipTracker?> _tracker;
    private readonly Func<uint> _playerId;
    private readonly Func<string, bool> _sendCommand;
    private readonly ServerCommandPanel _commands = new("fellow");

    // The last read (render thread only).
    private long _readAt = long.MinValue / 2;
    private bool _inFellowship;
    private string _name = string.Empty;
    private int _leaderId;
    private bool _open, _locked, _shareXp;
    private readonly List<(int Id, string Name, int Level)> _members = new();

    /// <param name="sendCommand">Queues a chat command for the plugin tick; false when it can't.</param>
    public FellowshipSectionUi(Func<FellowshipTracker?> tracker, Func<uint> playerId, Func<string, bool> sendCommand)
    {
        _tracker = tracker;
        _playerId = playerId;
        _sendCommand = sendCommand;
    }

    /// <summary>
    /// Draws the view into the current window. <paramref name="withServerCommands"/> adds the
    /// active server's fellowship commands above the member list. Render thread.
    /// </summary>
    public void Render(bool withServerCommands)
    {
        FellowshipTracker? t = _tracker();
        if (t == null)
        {
            ImGui.TextDisabled("Fellowship tracking starts at login.");
            return;
        }
        Refresh(t);

        if (_inFellowship)
        {
            SafeImGui.TextColored(ColLeader, _name.Length > 0 ? _name : "(unnamed fellowship)");
            SafeImGui.TextDisabled($"{_members.Count} member(s)   {(_open ? "Open" : "Closed")}   {(_locked ? "Locked" : "Unlocked")}   XP {(_shareXp ? "shared" : "not shared")}");
        }
        else
        {
            ImGui.TextDisabled("Not in a fellowship.");
        }

        // Shown out of a fellowship too: that's when "create" is wanted.
        if (withServerCommands) RenderServerCommands();
        if (!_inFellowship) return;
        ImGui.Separator();

        uint me = SafePlayerId();
        if (ImGui.BeginTable("##fellows", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, MathF.Max(60f, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("Member", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach ((int id, string name, int level) in _members)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool leader = id == _leaderId;
                if (leader) SafeImGui.TextColored(ColLeader, name);
                else SafeImGui.Text(name);
                ImGui.TableNextColumn();
                SafeImGui.TextColored(ColMute, level > 0 ? level.ToString() : string.Empty);
                ImGui.TableNextColumn();
                string role = (leader ? "Leader" : string.Empty) + ((uint)id == me && me != 0 ? (leader ? ", you" : "You") : string.Empty);
                SafeImGui.TextColored(ColMute, role);
            }
            ImGui.EndTable();
        }
    }

    // The fellowship commands the active server's plugin sent, if any.
    private void RenderServerCommands()
    {
        ServerCommandSet? set = ServerCommandModel.For(CommandSection);
        if (set == null || set.Commands.Count == 0) return;
        ImGui.Separator();
        SafeImGui.TextColored(ColMute, (set.ServerName.Length > 0 ? set.ServerName : "Server") + " fellowship commands");
        _commands.Render(set, _sendCommand);
    }

    private void Refresh(FellowshipTracker t)
    {
        long now = Environment.TickCount64;
        if (now - _readAt < RefreshMs) return;
        _readAt = now;
        _members.Clear();
        _inFellowship = t.IsInFellowship;
        if (!_inFellowship) return;
        _name = t.FellowshipName ?? string.Empty;
        _leaderId = t.LeaderId;
        _open = t.IsOpen;
        _locked = t.IsLocked;
        _shareXp = t.ShareXP;
        foreach ((int id, string name, int level) in t.GetMembers())
            _members.Add((id, name.Length > 0 ? name : $"0x{id:X8}", level));
        int leaderId = _leaderId;
        _members.Sort((a, b) =>
        {
            // Leader first, then by name.
            int byLeader = (b.Id == leaderId).CompareTo(a.Id == leaderId);
            return byLeader != 0 ? byLeader : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private uint SafePlayerId()
    {
        try { return _playerId(); }
        catch { return 0; }
    }
}
