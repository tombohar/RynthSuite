using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// What the engine's RynthNav panel shows beyond the basic status (RynthNav 0.6.1+): the
// status line in plain words ("walking to Holtburg, 1.2k yd left", "taking portal ...",
// "arrived inside Matron Hive") with the current step's distance left; the "here" and
// NavData facts (the Info section); and, in the travel JSON, the character's own recall
// spots and a portal tie waiting for "which tie was it?".
//
// Threads: the tick builds these (UpdateGoLine, RefreshInfo) and the engine's pump thread
// reads them in BuildStatusJson: the strings under _gate, the numbers volatile.
public sealed partial class RynthNavPlugin
{
    private const long InfoRefreshMs = 30000;     // tile files on disk: counted this often while the panel polls
    private const long PanelPollWindowMs = 3000;  // the panel counts as open this long after its last status poll

    // Tick thread: how the last goto (or the last panel-visible command) ended, as said in chat.
    private string _goNote = "";
    // Read under _gate.
    private string _goLine = "", _goPhase = "";
    private double _goLeft = -1;
    // Rebuild only when these change (tick thread).
    private string _glPhase = "", _glNote = "";
    private int _glIdx = -2;
    private long _glLeftKey = -1;
    private int _glSub;
    private bool _glActive;

    // Info (tick writes, any thread reads).
    private volatile int _tileHere = -1;          // a tile file for the current landblock: 1 yes, 0 no, -1 not checked
    private volatile int _tilesOnDisk = -1;
    private int _infoBusy;
    private long _nextInfoMs, _lastStatusPollMs;

    /// <summary>Says <paramref name="text"/> in chat and shows it on the panel's status line. Tick thread.</summary>
    private void Note(string text)
    {
        Say(text);
        _goNote = text;
    }

    /// <summary>The panel's status line and the current step's distance left. Tick thread, every tick.</summary>
    private void UpdateGoLine()
    {
        bool active = _gotoActive;
        string phase = "";
        double left = -1;
        int idx = active && _route != null ? _routeIdx : -1;
        if (active)
        {
            if (_recallWait) phase = "recall";
            else if (_portalWait) phase = "portal";
            else if (CurrentStepIsHub) { phase = "town"; left = HubLeft(); }
            else
            {
                phase = "walk";
                if (_hasPose)
                {
                    double dx = _gotoTew - _wx, dy = _gotoTns - _wy;
                    left = _coarse != null ? _coarse.Remaining(_wx, _wy) : Math.Sqrt(dx * dx + dy * dy);
                }
            }
        }
        // Whole yards under 1000, tens of yards above (FmtDistance shows 0.1k there).
        long leftKey = left < 0 ? -1 : left < 1000 ? (long)left : 100000 + (long)(left / 100);
        int sub = phase == "town" ? (_hubLanded ? 1 : 0) + (HubIsWaiting ? 2 : 0) : 0;   // the hub step's line changes with these
        if (active == _glActive && phase == _glPhase && idx == _glIdx && leftKey == _glLeftKey && sub == _glSub && ReferenceEquals(_goNote, _glNote))
            return;
        _glActive = active; _glPhase = phase; _glIdx = idx; _glLeftKey = leftKey; _glSub = sub; _glNote = _goNote;

        string line = active ? GoLine(phase, left) : _goNote;
        lock (_gate) { _goLine = line; _goPhase = phase; _goLeft = left; }
    }

    private string GoLine(string phase, double left)
    {
        bool hasStep = _route != null && _routeIdx < _route.Count;
        RouteStep step = hasStep ? _route![_routeIdx] : default;
        string label = hasStep ? step.Label ?? "" : "";
        bool last = _route == null || _routeIdx >= _route.Count - 1;
        string name = _gotoName.Length > 0 ? _gotoName : "the target";
        switch (phase)
        {
            case "recall":
            {
                RecallOption? o = CurrentRecallOption();
                return "casting " + (o != null ? o.Name : label.Length > 0 ? label : "a recall");
            }
            case "portal":
                if (NextStepIsHub) return $"taking the Town Network to {HubWhere(HubExitOf(_routeIdx + 1))}";
                return last && _enterName.Length > 0 ? "going into " + _enterName : "taking portal " + label;
            case "town":
                return HubLine(left);
        }
        string far = left >= 0 ? ", " + NavCoords.FmtDistance(left) + " left" : "";
        if (hasStep && step.UseRecall) return "getting ready to cast " + label;
        if (hasStep && step.UsePortal && NextStepIsHub)
            return $"walking to the Town Network portal{far} (to {HubWhere(HubExitOf(_routeIdx + 1))})";
        if (hasStep && step.UsePortal)
            return last && _enterName.Length > 0 ? $"walking to {_enterName}'s portal{far}" : $"walking to the portal {label}{far}";
        return $"walking to {name}{far}";
    }

    /// <summary>Tile file here, and how many are on disk; the count only while the panel polls. Tick thread.</summary>
    private void RefreshInfo(bool landblockChanged)
    {
        long now = NowMs;
        if (landblockChanged && _hasPose)
        {
            // A dungeon's tile is its landblock's surface: no navmesh here (RynthNavPlugin.InDungeon).
            try { _tileHere = !InDungeon(_cellId) && File.Exists(Path.Combine(NavDataDir, $"nav_{CurrentLandblock:X4}.tile")) ? 1 : 0; } catch { _tileHere = -1; }
        }
        if (now < _nextInfoMs || now - Interlocked.Read(ref _lastStatusPollMs) > PanelPollWindowMs) return;
        _nextInfoMs = now + InfoRefreshMs;
        if (Interlocked.Exchange(ref _infoBusy, 1) != 0) return;
        uint lb = CurrentLandblock;
        bool hasPose = _hasPose, dungeon = InDungeon(_cellId);
        Task.Run(() =>
        {
            try
            {
                int n = 0;
                if (Directory.Exists(NavDataDir))
                    foreach (string _ in Directory.EnumerateFiles(NavDataDir, "nav_*.tile")) n++;
                _tilesOnDisk = n;
                if (hasPose) _tileHere = !dungeon && File.Exists(Path.Combine(NavDataDir, $"nav_{lb:X4}.tile")) ? 1 : 0;
            }
            catch { }
            finally { Interlocked.Exchange(ref _infoBusy, 0); }
        });
    }

    /// <summary>The live panel fields of the status JSON ("go", "info"). Any thread.</summary>
    private void AppendPanelJson(StringBuilder sb)
    {
        Interlocked.Exchange(ref _lastStatusPollMs, NowMs);
        var ci = CultureInfo.InvariantCulture;
        string line, phase; double left;
        lock (_gate) { line = _goLine; phase = _goPhase; left = _goLeft; }
        sb.Append(",\"v\":\"").Append(PluginVersion).Append('"');
        sb.Append(",\"go\":{\"line\":\"").Append(Esc(line)).Append("\",\"phase\":\"").Append(phase)
          .Append("\",\"left\":").Append(left < 0 ? "-1" : left.ToString("F0", ci)).Append('}');
        uint cell = _cellId;
        sb.Append(",\"info\":{\"dir\":\"").Append(Esc(NavDataDir)).Append("\",\"source\":\"").Append(Esc(NavConfig.Source))
          .Append("\",\"graph\":").Append(_graph != null ? 1 : 0).Append(",\"graphStatus\":\"").Append(Esc(_graphStatus))
          .Append("\",\"tileHere\":").Append(_tileHere).Append(",\"indoors\":").Append(_hasPose && (cell & 0xFFFF) >= 0x100 ? 1 : 0)
          .Append(",\"onDisk\":").Append(_tilesOnDisk)
          .Append(",\"townNet\":").Append(_townNet != null ? 1 : 0).Append(",\"townNetStatus\":\"").Append(Esc(_townNetStatus)).Append("\"}");
        // Path markers (RynthNavPlugin.Markers.cs) and "save as nav route" (RynthNavPlugin.SaveRoute.cs), 0.6.6+. The mesh view (RynthNavPlugin.MeshView.cs), 0.6.7+.
        sb.Append(",\"mesh\":{\"on\":").Append(_meshOn ? 1 : 0).Append(",\"floor\":").Append(_meshFloor ? 1 : 0)
          .Append(",\"walls\":").Append(_meshWalls ? 1 : 0).Append(",\"radius\":").Append(_meshRadius.ToString("F0", ci))
          .Append(",\"triangles\":").Append(_meshBudget).Append(",\"drawn\":").Append(_meshDrawn).Append('}');
        PlannedTrip? trip = _trip;
        sb.Append(",\"markers\":{\"on\":").Append(_markersOn ? 1 : 0).Append(",\"labels\":").Append(_markerLabels ? 1 : 0)
          .Append(",\"guide\":").Append(_markerGuide ? 1 : 0).Append(",\"shown\":").Append(trip != null && _tripShown ? 1 : 0)
          .Append(",\"status\":\"").Append(Esc(_markerStatus)).Append("\"}");
        string pathJson = PathJson();                       // the walking path, for the engine's World Map
        if (pathJson.Length > 0) sb.Append(",\"path\":").Append(pathJson);
        sb.Append(",\"save\":{\"can\":").Append(trip != null ? 1 : 0).Append(",\"for\":\"").Append(Esc(trip?.Goal ?? ""))
          .Append("\",\"name\":\"").Append(Esc(_saveDefault)).Append("\",\"conflict\":\"").Append(Esc(_saveConflict))
          .Append("\",\"busy\":").Append(_saveBusy ? 1 : 0).Append(",\"last\":\"").Append(Esc(_saveLast)).Append("\"}");
    }

    // ── The character's own recall spots, for the Recalls section (travel JSON) ──

    private static readonly (RecallSlot Slot, string Name)[] MySlots =
    {
        (RecallSlot.Tie1, "Primary portal tie"),
        (RecallSlot.Tie2, "Secondary portal tie"),
        (RecallSlot.Lifestone, "Lifestone Recall (tied lifestone)"),
        (RecallSlot.Sanctuary, "@lifestone (last lifestone used)"),
        (RecallSlot.LastPortal, "Portal Recall (last portal)"),
        (RecallSlot.House, "House recall"),
        (RecallSlot.Mansion, "Allegiance mansion recall"),
        (RecallSlot.Hometown, "Allegiance hometown recall"),
    };

    /// <summary>"mine" (every recall spot learned for this character), "ties", "tiePending". Tick thread.</summary>
    private void AppendMyRecalls(StringBuilder sb, CultureInfo ci)
    {
        sb.Append(",\"mine\":[");
        bool first = true;
        if (_charKey.Length > 0)
            foreach (var (slot, name) in MySlots)
            {
                RecallSpot? s = _memory.Get(_charKey, slot);
                if (s == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"s\":\"").Append(RecallMemory.SlotName(slot)).Append("\",\"n\":\"").Append(Esc(name))
                  .Append("\",\"on\":").Append(s.OnMap ? 1 : 0)
                  .Append(",\"ns\":").Append(s.Ns.ToString("F2", ci)).Append(",\"ew\":").Append(s.Ew.ToString("F2", ci))
                  .Append(",\"at\":\"").Append(Esc(s.Label)).Append("\"}");
            }
        sb.Append(']');
        // Which tie spells the character knows: 1 yes, 0 no, -1 the spellbook isn't read yet.
        int k1 = _known == null ? -1 : _known.Contains(RecallPlanner.PrimaryPortalTie) ? 1 : 0;
        int k2 = _known == null ? -1 : _known.Contains(RecallPlanner.SecondaryPortalTie) ? 1 : 0;
        sb.Append(",\"ties\":{\"t1\":").Append(k1).Append(",\"t2\":").Append(k2).Append('}');
        RecallSpot? p = _pendingTie;
        sb.Append(",\"tiePending\":\"").Append(p == null ? "" : Esc(p.OnMap ? $"{p.Label} ({NavCoords.Fmt(p.Ns, p.Ew)})" : p.Label)).Append('"');
    }

    // ── UTF-8 status (RynthNavGetStatusJsonUtf8) ─────────────────────────────

    /// <summary>The status JSON as UTF-8 bytes, NUL-terminated. Any thread.</summary>
    internal byte[] BuildStatusUtf8()
    {
        string json = BuildStatusJson();
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(json) + 1];
        Encoding.UTF8.GetBytes(json, 0, json.Length, bytes, 0);
        return bytes;
    }
}
