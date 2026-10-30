using System;
using System.IO;
using System.Threading.Tasks;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthNav;

// "Save as nav route": the last trip RynthNav planned (Route or Go) written as a RynthAi nav route
// (NavRouteExport) into RynthAi's NavProfiles folder, where RynthAi's route picker lists it
// (RynthAi re-reads the folder when it changes) and /ra nav load <name> loads it.
//   /rnav saveroute [name] [overwrite]     (the panel: the name box and Save on the Go tab)
// No name: "To <the place>". An existing file is never replaced without "overwrite" (the panel
// then offers Replace). The path is the one the markers show; if it isn't worked out yet, it is
// first, and the save follows when it's done. Tick thread, except the file write (a worker).
public sealed partial class RynthNavPlugin
{
    /// <summary>RynthAi's route folder (what its Nav picker lists): &lt;RynthSuite&gt;\RynthAi\NavProfiles.</summary>
    private static string NavProfilesDir => Path.Combine(RynthInstallPaths.RynthAiDir, "NavProfiles");

    private (int TripId, string Name, bool Overwrite)? _pendingSave;
    // For the panel (any thread reads them).
    private volatile string _saveDefault = "", _saveConflict = "", _saveLast = "";
    private volatile bool _saveBusy;

    /// <summary>/rnav saveroute [name] [overwrite]</summary>
    private void CmdSaveRoute(string rest)
    {
        PlannedTrip? trip = _trip;
        if (trip == null) { Note("saveroute: plan a trip first (Route or Go), then save it"); return; }
        string text = rest.Trim();
        bool overwrite = false;
        if (text.Equals("overwrite", StringComparison.OrdinalIgnoreCase)) { overwrite = true; text = ""; }
        else if (text.EndsWith(" overwrite", StringComparison.OrdinalIgnoreCase)) { overwrite = true; text = text[..^10].TrimEnd(); }
        if (text.Length == 0) text = _saveDefault.Length > 0 ? _saveDefault : NavRouteExport.SafeName("To " + trip.Goal);
        string name = NavRouteExport.FileName(text, out string error);
        if (name.Length == 0) { Note("saveroute: " + error); return; }

        if (!overwrite && File.Exists(Path.Combine(NavProfilesDir, name + ".nav")))
        {
            _saveConflict = name;
            Note($"saveroute: {name}.nav is already in RynthAi's NavProfiles; /rnav saveroute {name} overwrite replaces it");
            return;
        }
        _saveConflict = "";
        if (_tripPath != null && _tripPath.TripId == trip.Id) { WriteRoute(trip, _tripPath, name, overwrite); return; }
        _pendingSave = (trip.Id, name, overwrite);
        StartPathBuild();
        _saveBusy = true;
        Note($"saveroute: working out the path to {trip.Goal} first…");
    }

    /// <summary>The trip's path is in (or failed: null): a save waiting for it goes ahead. Tick thread.</summary>
    private void OnPathReady(PlannedTrip? trip, TripPath? path)
    {
        if (_pendingSave is not { } ps || trip == null || ps.TripId != trip.Id) return;
        _pendingSave = null;
        _saveBusy = false;
        if (path == null) { Note("saveroute: the path couldn't be worked out, so nothing was saved (see the log)"); return; }
        WriteRoute(trip, path, ps.Name, ps.Overwrite);
    }

    private void WriteRoute(PlannedTrip trip, TripPath path, string name, bool overwrite)
    {
        NavRouteExport.Result r = NavRouteExport.Convert(trip, path, _townNet);
        if (r.Route.Points.Count == 0)
        {
            Note($"saveroute: nothing to save: {(r.StopReason.Length > 0 ? r.StopReason : "the trip has no steps")}");
            return;
        }
        string folder = NavProfilesDir;
        string goal = trip.Goal;
        int notes = path.Notes.Count;
        string firstNote = notes > 0 ? path.Notes[0] : "";
        _saveBusy = true;
        Task.Run(() =>
        {
            string line;
            bool ok = NavRouteExport.Save(folder, name, r.Route, overwrite, out string file, out bool exists, out string error);
            if (ok)
            {
                line = $"saveroute: saved {name}.nav to {folder}: {r.Route.Points.Count} steps ({r.Waypoints} waypoints"
                     + (r.Portals > 0 ? $", {r.Portals} portal(s)" : "") + (r.Recalls + r.Chats > 0 ? $", {r.Recalls + r.Chats} recall(s)" : "")
                     + $"). RynthAi: pick it in Nav, or /ra nav load {name}.";
                if (r.StoppedAt >= 0) line += $" It stops short of {goal}: {r.StopReason}.";
                if (notes > 0) line += $" Check it: {firstNote}{(notes > 1 ? $" (+{notes - 1} more in the log)" : "")}.";
                _saveConflict = "";
                Host.Log($"[RynthNav] {line} ({file})");
            }
            else if (exists)
            {
                _saveConflict = name;
                line = $"saveroute: {name}.nav is already there; /rnav saveroute {name} overwrite replaces it";
            }
            else line = $"saveroute: couldn't write {name}.nav: {error}";
            _saveLast = line;
            _saveBusy = false;
            Say(line);
        });
    }
}
