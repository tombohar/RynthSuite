// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The 100 exploration markers: rows of the Quests tab's merged list (QuestsTab.cs: MRK rows, the
/// markers found and the next one in the trackers' status, "Not done" for the ones still to find).
/// What is left here is the chat print a click on a marker row (or its Print button) gives.
/// </summary>
internal sealed partial class OracleView
{
    private void PrintMarker(Marker m) =>
        PrintEntry($"Marker {m.Number}: {m.Name}", "", m.IsFound(_c.Flags) ? "found" : "", m.Hint, m.Url);
}
