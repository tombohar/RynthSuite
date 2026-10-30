// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Fellowship: name, leader, open/locked, experience sharing and the members with their levels
/// (upstream's status list). Read from the client's fellowship object through RynthAi's
/// FellowshipTracker (shared source). The Mini Remote's Fellowship view links here ("/ro fellowship").
/// A server's own fellowship rules and commands (Infinite Leaftide's larger fellowships and /fship
/// buttons) are its server plugin's (RynthILT's Fellowship section), not drawn here. The upstream
/// recruiting tools are not ported (see the port report).
/// </summary>
internal sealed partial class OracleView
{
    private const int MaxFellows = 9; // retail

    private void DrawFellowship(UiWindow w)
    {
        FellowshipTracker f = _c.Fellowship;
        if (!f.IsInFellowship)
        {
            w.Text("Not in a fellowship.");
            return;
        }

        // One snapshot for every row: the index getters could each see a different refresh.
        List<(int Id, string Name, int Level)> members = f.GetMembers();
        int leaderId = f.LeaderId;
        int count = System.Math.Max(f.MemberCount, members.Count);
        string leaderName = "";
        foreach ((int id, string name, int _) in members)
            if (id == leaderId) leaderName = name;

        // A server that allows more than retail's nine: no limit is shown (its server plugin knows it).
        bool overRetail = count > MaxFellows;
        w.SeparatorText(f.FellowshipName.Length > 0 ? f.FellowshipName : "Fellowship");
        Field(w, "Leader", leaderName.Length > 0 ? leaderName : "-", 0f, 110f);
        Field(w, "Members", overRetail ? count.ToString() : $"{count} / {MaxFellows}", 300f, 400f);
        Field(w, "Open", f.IsOpen ? "yes" : "no", 0f, 110f);
        Field(w, "Locked", f.IsLocked ? "yes" : "no", 300f, 400f);
        Field(w, "Experience", f.ShareXP ? "shared" : "not shared", 0f, 110f);
        bool mayRecruit = leaderId == (int)_c.Character.PlayerId || f.IsOpen;
        Field(w, "Recruit", !overRetail && count >= MaxFellows ? "fellowship full" : mayRecruit ? "you can recruit" : "must be open or leader", 300f, 400f);

        w.SeparatorText("Members");
        if (members.Count == 0) w.TextDisabled("Reading the member list...");
        // Leader first, then everyone else by name; the level (0 = not read yet) in its own column.
        members.Sort((a, b) => a.Id == leaderId ? -1 : b.Id == leaderId ? 1
            : string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
        foreach ((int id, string name, int level) in members)
        {
            if (name.Length == 0) continue;
            if (id == leaderId) w.TextColored(UiColors.Gold, name + " (leader)");
            else w.Text(name);
            if (level > 0)
            {
                w.SameLine(260f);
                w.TextDisabled("level " + level);
            }
        }
    }
}
