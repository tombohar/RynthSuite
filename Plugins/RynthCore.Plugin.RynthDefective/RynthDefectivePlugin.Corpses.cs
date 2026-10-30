// ============================================================================
//  RynthDefective - RynthDefectivePlugin.Corpses.cs
//  The Corpses window (2026-10-23): where your corpses are (Corpses/CorpseBook.cs
//  has the rules, Corpses/CorpsesWindow.cs draws it).
//
//  Always on while logged in (a death has to be noted whether the window shows
//  or not), and cheap: the health is read 10 times a second, the server's two
//  corpse lines are picked out of the chat, and each object that comes into
//  view is checked once for the name "Corpse of <you>". A corpse leaving view
//  counts as gone (CorpseBook.VanishMeansGone) when you had opened it, it was due
//  to rot, or it went from beside you, and not while portalling; otherwise you
//  only walked away from it or lost sight of it.
//
//  This character's corpses are saved in their own file, the other characters'
//  read from theirs every 10 s while the window shows.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Corpses;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective;

public sealed partial class RynthDefectivePlugin
{
    internal const string CorpsesSurface = "window:corpses";
    private const string CorpsePrefix = "Corpse of ";
    private const uint LevelProperty = 25;          // PropertyInt.Level

    private readonly CorpseBook _corpseBook = new();
    private CorpseStore? _corpseStore;
    private CorpsesWindow? _corpsesWindow;                                   // render thread
    private volatile CorpsesView _corpsesView = CorpsesView.NotReady;        // tick writes, render reads
    private readonly ConcurrentQueue<uint> _createdIds = new(), _deletedIds = new(), _openedIds = new();
    private readonly ConcurrentQueue<string> _corpseLines = new();
    private readonly ConcurrentQueue<Action> _corpseActions = new();
    private readonly List<(uint Id, long AtMs)> _corpsePending = new();      // seen, no position yet
    private readonly List<(uint Id, long AtMs)> _corpseDeletes = new();      // left view: gone?
    private List<OtherCorpses> _otherCorpses = new();
    private bool _corpsesStarted, _deadSeen;
    private string? _corpseAlias;                                            // test hook: another name's corpses count as yours
    private long _corpsesDrawnMs = long.MinValue / 2, _nextCorpseViewMs, _nextDeathCheckMs, _nextCorpseExpireMs, _nextOthersMs, _corpseSaveDueMs, _nextAbsentCheckMs;
    private int _corpseRevisionSaved, _corpseRevisionShown = -1, _corpsesFailures;

    /// <summary>RynthPluginRenderEmbed "window:corpses". 1 drawn, 0 not ready, -2 failed (three in a row). Render thread.</summary>
    internal int RenderCorpses(out float wantHeight)
    {
        wantHeight = 0;
        if (!UsesShortcutBar) return 0;
        System.Threading.Volatile.Write(ref _corpsesDrawnMs, Environment.TickCount64);
        try
        {
            _corpsesWindow ??= new CorpsesWindow(Host,
                row => _corpseActions.Enqueue(() => SelectCorpse(row)),
                row => _corpseActions.Enqueue(() => { if (row.Record != null) _corpseBook.Forget(row.Record); }));
            wantHeight = _corpsesWindow.Draw(_corpsesView);
            _corpsesFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthDefective] corpses draw failed: {ex.GetType().Name}: {ex.Message}");
            return ++_corpsesFailures >= 3 ? -2 : 0;
        }
    }

    public override void OnCreateObject(uint objectId)
    {
        if (_createdIds.Count < 4096) _createdIds.Enqueue(objectId);
    }

    public override void OnDeleteObject(uint objectId)
    {
        if (_deletedIds.Count < 4096) _deletedIds.Enqueue(objectId);
    }

    public override void OnViewObjectContents(uint objectId)
    {
        if (_openedIds.Count < 256) _openedIds.Enqueue(objectId);
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (text == null || text.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) < 0) return;
        if (CorpseLines.IsCorpseLine(text) && _corpseLines.Count < 64) _corpseLines.Enqueue(text);
    }

    /// <summary>The character is known: its corpses from its file, and any of them already in view.</summary>
    private void StartCorpses()
    {
        _corpseStore ??= new CorpseStore(_store.Root);
        PlaceNames.EnsureLoading();
        _corpseBook.Load(_corpseStore.Load(_server, _character, out string? problem));
        if (problem != null) Host.Log($"[RynthDefective] couldn't read the corpses ({problem}); starting empty.");
        _corpseRevisionSaved = _corpseBook.Revision;
        _corpseRevisionShown = -1;
        _corpsePending.Clear();
        _corpseDeletes.Clear();
        _deadSeen = false;
        _nextOthersMs = 0;
        _corpsesStarted = true;
        if (Host.HasGetLiveObjectIds)
            foreach (uint id in Host.GetLiveObjectIds()) CheckCreated(id, Environment.TickCount64);
    }

    private void StopCorpses()
    {
        SaveCorpsesNow();
        _corpsesStarted = false;
        _corpsesView = CorpsesView.NotReady;
        while (_createdIds.TryDequeue(out _)) { }
        while (_deletedIds.TryDequeue(out _)) { }
        while (_openedIds.TryDequeue(out _)) { }
        while (_corpseLines.TryDequeue(out _)) { }
        _corpsePending.Clear();
        _corpseDeletes.Clear();
    }

    /// <summary>Plugin tick: the death, the server's lines, corpses coming and going, the save and the window's view.</summary>
    private void TickCorpses(long now)
    {
        if (!_corpsesStarted)
        {
            while (_createdIds.TryDequeue(out _)) { }   // nothing to match them to yet
            while (_deletedIds.TryDequeue(out _)) { }
            while (_openedIds.TryDequeue(out _)) { }
            return;
        }
        DateTime utc = DateTime.UtcNow;
        while (_corpseActions.TryDequeue(out Action? act))
        {
            try { act(); } catch (Exception ex) { Host.Log($"[RynthDefective] corpses: {ex.Message}"); }
        }

        if (now >= _nextDeathCheckMs)
        {
            _nextDeathCheckMs = now + 100;
            CheckDeath(utc);
        }
        while (_corpseLines.TryDequeue(out string? line))
        {
            if (CorpseLines.TryLocated(line, out double ns, out double ew)) _corpseBook.ServerLine(utc, ns, ew, PlayerLevel());
            else if (CorpseLines.IsNothingDropped(line)) _corpseBook.NothingDropped(utc);
        }
        // A portal into town brings hundreds at once: a few hundred a tick, the rest on the next.
        for (int n = 0; n < 256 && _createdIds.TryDequeue(out uint id); n++) CheckCreated(id, now);
        while (_openedIds.TryDequeue(out uint opened)) _corpseBook.Opened(opened, utc);
        for (int i = _corpsePending.Count - 1; i >= 0; i--)
        {
            (uint id, long until) = _corpsePending[i];
            if (TrySeen(id, utc) || now > until) _corpsePending.RemoveAt(i);
        }
        while (_deletedIds.TryDequeue(out uint id))
            foreach (CorpseRecord c in _corpseBook.Corpses)
                if (c.ObjectId == id) { _corpseDeletes.Add((id, now + 2500)); break; }
        for (int i = _corpseDeletes.Count - 1; i >= 0; i--)
        {
            if (now < _corpseDeletes[i].AtMs) continue;
            uint id = _corpseDeletes[i].Id;
            _corpseDeletes.RemoveAt(i);
            CheckGone(id);
        }
        if (now >= _nextAbsentCheckMs)
        {
            _nextAbsentCheckMs = now + 1000;
            CheckNotThere(utc);
        }
        if (now >= _nextCorpseExpireMs)
        {
            _nextCorpseExpireMs = now + 60_000;
            _corpseBook.Expire(utc);
        }

        if (_corpseBook.Revision != _corpseRevisionSaved && _corpseSaveDueMs == 0) _corpseSaveDueMs = now + 1000;
        if (_corpseSaveDueMs != 0 && now >= _corpseSaveDueMs) SaveCorpsesNow();

        bool shown = now - System.Threading.Volatile.Read(ref _corpsesDrawnMs) < 2000;
        if (!shown) return;
        if (now >= _nextOthersMs && _corpseStore != null)
        {
            _nextOthersMs = now + 10_000;
            _otherCorpses = _corpseStore.LoadOthers(_server, _character, utc);
            _corpseRevisionShown = -1;
        }
        if (now >= _nextCorpseViewMs || _corpseBook.Revision != _corpseRevisionShown)
        {
            _nextCorpseViewMs = now + 250;
            _corpseRevisionShown = _corpseBook.Revision;
            _corpsesView = CorpseRows.Build(_corpseBook.Corpses, _otherCorpses, PlayerSpotNow(), utc);
        }
    }

    /// <summary>Health 0 (the edge): where the player stands is where the corpse will lie.</summary>
    private void CheckDeath(DateTime utc)
    {
        if (!Host.TryGetPlayerVitals(out uint hp, out uint maxHp, out _, out _, out _, out _) || maxHp == 0) return;
        bool dead = hp == 0;
        if (dead && !_deadSeen)
        {
            _deadSeen = true;
            if (Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _) && cell != 0)
            {
                _corpseBook.Died(utc, cell, x, y, z, PlayerLevel());
                Host.Log($"[RynthDefective] died at 0x{cell:X8} ({x:0.0}, {y:0.0}, {z:0.0}): noted for the Corpses window.");
            }
        }
        else if (!dead) _deadSeen = false;
    }

    private int PlayerLevel()
    {
        uint player = Host.GetPlayerId();
        return player != 0 && Host.TryGetObjectIntProperty(player, LevelProperty, out int level) ? level : 0;
    }

    /// <summary>An object came into view: one of this character's corpses?</summary>
    private void CheckCreated(uint id, long now)
    {
        if (id == 0 || !Host.TryGetObjectName(id, out string name) || !name.StartsWith(CorpsePrefix, StringComparison.Ordinal)) return;
        string of = name.Substring(CorpsePrefix.Length);
        if (of != _character && (_corpseAlias == null || of != _corpseAlias)) return;
        if (!TrySeen(id, DateTime.UtcNow)) _corpsePending.Add((id, now + 5000));
    }

    private bool TrySeen(uint id, DateTime utc)
    {
        if (!Host.TryGetObjectPosition(id, out uint cell, out float x, out float y, out float z) || cell == 0) return false;
        _corpseBook.Seen(utc, id, cell, x, y, z);
        return true;
    }

    /// <summary>A corpse left view 2.5 s ago: gone for good, or only out of sight (CorpseBook.VanishMeansGone)?</summary>
    private void CheckGone(uint id)
    {
        if (Host.HasIsPortaling && Host.IsPortaling()) return;
        if (Host.TryGetObjectPosition(id, out uint back, out _, out _, out _) && back != 0) return;   // in view again
        CorpseRecord? c = null;
        foreach (CorpseRecord r in _corpseBook.Corpses) if (r.ObjectId == id) { c = r; break; }
        if (c == null) return;
        PlayerSpot me = PlayerSpotNow();
        if (me.Cell == 0) return;
        double d = CorpseBook.Distance(c, me.Cell, me.X, me.Y, me.Z);
        bool opened = c.OpenedUtc != DateTime.MinValue;
        if (CorpseBook.VanishMeansGone(c, d, DateTime.UtcNow) && _corpseBook.Gone(id))
            Host.Log($"[RynthDefective] your corpse 0x{id:X8} is gone ({d:0} m away{(opened ? ", opened" : "")}).");
        else
            Host.Log($"[RynthDefective] your corpse 0x{id:X8} left view ({d:0} m away); kept.");
    }

    /// <summary>Standing where a corpse should be without it in view: after 10 s it's dropped.</summary>
    private void CheckNotThere(DateTime utc)
    {
        if (_corpseBook.Corpses.Count == 0 || (Host.HasIsPortaling && Host.IsPortaling())) return;
        PlayerSpot me = PlayerSpotNow();
        if (me.Cell == 0) return;
        CorpseRecord? drop = null;
        double dropAt = 0;
        foreach (CorpseRecord c in _corpseBook.Corpses)
        {
            double d = CorpseBook.Distance(c, me.Cell, me.X, me.Y, me.Z);
            bool inView = c.ObjectId != 0 && Host.TryGetObjectPosition(c.ObjectId, out uint oc, out _, out _, out _) && oc != 0;
            if (CorpseBook.NotThere(c, d, inView, utc)) { drop = c; dropAt = d; }
        }
        if (drop != null && _corpseBook.Forget(drop))
            Host.Log($"[RynthDefective] a corpse isn't where it was ({dropAt:0} m from its place for {CorpseBook.AbsentFor.TotalSeconds:0} s): dropped.");
    }

    private PlayerSpot PlayerSpotNow()
    {
        if (!Host.TryGetPlayerPose(out uint pc, out float px, out float py, out float pz, out float qw, out _, out _, out float qz) || pc == 0)
            return default;
        double facing = ((-2.0 * Math.Atan2(qz, qw) * 180.0 / Math.PI) % 360.0 + 720.0) % 360.0;
        return new PlayerSpot(pc, px, py, pz, facing);
    }

    private void SelectCorpse(CorpseRow row)
    {
        if (row.ObjectId != 0 && Host.TryGetObjectName(row.ObjectId, out _) && Host.SelectItem(row.ObjectId)) return;
        Print($"That corpse isn't in view. It's at {row.Place}.");
    }

    /// <summary>"/rdef corpses die" and "/rdef corpses as &lt;name&gt;" (tests on a server where dying is slow).</summary>
    private string CorpseTestHook(string arg)
    {
        if (!_corpsesStarted) return "Log in first.";
        if (arg.Equals("die", StringComparison.OrdinalIgnoreCase))
        {
            if (!Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _) || cell == 0)
                return "No position.";
            _corpseBook.Died(DateTime.UtcNow, cell, x, y, z, PlayerLevel());
            return $"Test: a death noted at 0x{cell:X8} ({x:0.0}, {y:0.0}, {z:0.0}).";
        }
        if (arg.StartsWith("as ", StringComparison.OrdinalIgnoreCase))
        {
            _corpseAlias = arg.Substring(3).Trim();
            if (Host.HasGetLiveObjectIds)
                foreach (uint id in Host.GetLiveObjectIds()) CheckCreated(id, Environment.TickCount64);
            return $"Test: \"{CorpsePrefix}{_corpseAlias}\" counts as yours until the plugin reloads.";
        }
        return "Usage: /rdef corpses [clear]";
    }

    private void SaveCorpsesNow()
    {
        _corpseSaveDueMs = 0;
        if (!_corpsesStarted || _corpseStore == null || _corpseBook.Revision == _corpseRevisionSaved) return;
        _corpseRevisionSaved = _corpseBook.Revision;
        string? error = _corpseStore.Save(_server, _character, _corpseBook.Corpses);
        if (error != null) Host.Log("[RynthDefective] corpses save failed: " + error);
    }
}
