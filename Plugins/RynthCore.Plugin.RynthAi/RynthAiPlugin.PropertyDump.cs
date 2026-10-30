using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.Diagnostics;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// /ra dumpprops [0xId], /ub propertydump, /vt propertydump and the PropertyDump hotkey: every
/// property of the given object, else the selected one, else the character, as UtilityBelt's
/// propertydump lists them.
///
/// The dump used to ask the engine for fixed key ranges one key at a time (int 0-400, bool 0-300,
/// string 0-60, int64 0-12, float 0-400, data id 0-80, instance id 0-50), so keys outside them were
/// never shown, there was no spell book, and int64 / float / data id / instance id keys had no
/// names. With engine API v84 the engine hands over every key it holds (GetObjectProperties); on an
/// older engine every named key is probed plus the old ranges. An object that isn't identified yet
/// is identified first (RequestId) and dumped when the answer arrives. Chat gets the dump a few
/// lines per tick; the whole dump is also written to RynthAi\Logs\propertydump-&lt;name&gt;-&lt;time&gt;.txt.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private sealed class PendingPropertyDump
    {
        public uint Id;
        public string Label = "";
        public bool WaitingForId;
        public long IdDeadline;       // stop waiting for the identify answer
        public long QualitiesDeadline; // stop waiting for the engine's main-thread qualities read
        public long NextCheck;
    }

    private const long PropertyDumpIdWaitMs = 4000, PropertyDumpQualitiesWaitMs = 1500, PropertyDumpPollMs = 100;

    private readonly object _propertyDumpLock = new();
    private PendingPropertyDump? _pendingPropertyDump;
    private readonly Queue<string> _propertyDumpChat = new();

    /// <summary>Clock for the dump's waits; tests replace it.</summary>
    internal Func<long> PropertyDumpClock = () => Environment.TickCount64;

    private void HandleDumpPropsCommand(string[] parts)
    {
        uint me = Host.GetPlayerId();
        if (me == 0)
        {
            ReplyFail("[RynthAi] Not logged in.");
            return;
        }
        uint target = me;
        if (parts.Length >= 3 && uint.TryParse(parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[2][2..] : parts[2],
                System.Globalization.NumberStyles.HexNumber, null, out uint given) && given != 0)
            target = given;
        else if (_currentTargetId != 0)
            target = _currentTargetId;
        StartPropertyDump(target, me);
    }

    internal void StartPropertyDump(uint target, uint me)
    {
        string label = (Host.TryGetObjectName(target, out string nm) && nm.Length > 0 ? nm : "?") + $" (0x{target:X8})";
        long now = PropertyDumpClock();
        var pending = new PendingPropertyDump
        {
            Id = target,
            Label = label,
            QualitiesDeadline = now + PropertyDumpQualitiesWaitMs,
        };

        // Not identified yet: ask, and dump when the answer is in (UtilityBelt shows the object as
        // Decal has it, which after an ID is the full appraisal).
        if (target != me && Host.HasHasAppraisalData && !Host.HasAppraisalData(target) && Host.HasRequestId)
        {
            Host.RequestId(target);
            pending.WaitingForId = true;
            pending.IdDeadline = now + PropertyDumpIdWaitMs;
            pending.QualitiesDeadline = pending.IdDeadline + PropertyDumpQualitiesWaitMs;
            ChatLine($"[RynthAi] {label} isn't identified yet - asked the server; the property dump follows when the answer arrives.");
        }
        else if (target == me)
            ChatLine($"[RynthAi] Nothing selected - dumping your character, {label}.");

        lock (_propertyDumpLock)
            _pendingPropertyDump = pending;
        TickPropertyDump(force: !pending.WaitingForId);
    }

    /// <summary>Every tick: the waiting dump, then a few lines of the dump's chat.</summary>
    private void TickPropertyDump(bool force = false)
    {
        PendingPropertyDump? p;
        lock (_propertyDumpLock) p = _pendingPropertyDump;
        long now = PropertyDumpClock();
        if (p != null && (force || now >= p.NextCheck))
        {
            p.NextCheck = now + PropertyDumpPollMs;
            string? note = null;
            bool ready = true;
            if (p.WaitingForId)
            {
                if (Host.HasAppraisalData(p.Id))
                    p.WaitingForId = false;
                else if (now >= p.IdDeadline)
                {
                    p.WaitingForId = false;
                    note = $"No identify answer for {p.Label} within {PropertyDumpIdWaitMs / 1000} s - showing what the client has.";
                }
                else
                    ready = false;
            }
            PropertyDumpData? data = null;
            if (ready)
            {
                data = CollectPropertyDump(p.Id, out bool qualitiesPending);
                if (qualitiesPending && now < p.QualitiesDeadline)
                    ready = false;   // the engine reads the client's tables at AC's next tick: ask again
            }
            if (ready)
            {
                lock (_propertyDumpLock)
                    if (ReferenceEquals(_pendingPropertyDump, p)) _pendingPropertyDump = null;
                EmitPropertyDump(p, data, note);
            }
        }

        DrainPropertyDumpChat();
    }

    private void DrainPropertyDumpChat()
    {
        for (int i = 0; i < PropertyDump.ChatLinesPerTick; i++)
        {
            string line;
            lock (_propertyDumpLock)
            {
                if (_propertyDumpChat.Count == 0) return;
                line = _propertyDumpChat.Dequeue();
            }
            ChatLine(line);
        }
    }

    /// <summary>Logout: nothing waits for an object of the session that ended.</summary>
    private void ClearPropertyDump()
    {
        lock (_propertyDumpLock)
        {
            _pendingPropertyDump = null;
            _propertyDumpChat.Clear();
        }
    }

    private void EmitPropertyDump(PendingPropertyDump p, PropertyDumpData? data, string? note)
    {
        if (data == null)
        {
            ChatLine($"[RynthAi] Nothing is known about {p.Label} (not in the client).");
            return;
        }
        List<string> lines = PropertyDump.Format(data,
            id => SpellDatabase.GetSpellName(unchecked((int)id)),
            id => Host.TryGetObjectName(id, out string n) && n.Length > 0 ? n : null);
        if (note != null) lines.Insert(1, note);

        string fileNote = "";
        try
        {
            string folder = PropertyDump.Folder;
            string path = Path.Combine(folder, PropertyDump.FileName(data.Name.Length > 0 ? data.Name : $"0x{data.Id:X8}", RynthClock.Now));
            if (DataWriteGuard.TrySave("Property dump", path, () =>
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllLines(path, lines);
                }, out string reason, callerShows: true))
                fileNote = $"[RynthAi] Full property dump ({lines.Count} lines) written to {path}";
            else
                fileNote = $"[RynthAi] The property dump couldn't be written to a file: {reason}";
        }
        catch (Exception ex)
        {
            fileNote = $"[RynthAi] The property dump couldn't be written to a file: {ex.Message}";
        }

        lock (_propertyDumpLock)
        {
            foreach (string l in lines) _propertyDumpChat.Enqueue("[RynthAi] " + l);
            _propertyDumpChat.Enqueue(fileNote);
        }
    }

    /// <summary>
    /// Every property of <paramref name="id"/>: the engine's whole set (API v84), else per-key reads
    /// of every named key and the old fixed ranges. Null when the client knows nothing about it.
    /// </summary>
    internal PropertyDumpData? CollectPropertyDump(uint id, out bool qualitiesPending)
    {
        qualitiesPending = false;
        var d = new PropertyDumpData { Id = id };
        uint me = Host.GetPlayerId();
        d.IsPlayer = id != 0 && id == me;
        if (Host.TryGetObjectName(id, out string name)) d.Name = name ?? "";
        if (Host.HasGetObjectWcid) Host.TryGetObjectWcid(id, out d.Wcid);
        var wo = _objectCache?[unchecked((int)id)];
        if (wo != null)
        {
            d.ObjectClass = wo.ObjectClass.ToString();
            d.ObjectClassValue = (int)wo.ObjectClass;
        }
        d.HasIdData = Host.HasHasAppraisalData && Host.HasAppraisalData(id);
        if (Host.HasGetLastIdTime) d.LastIdTime = Host.GetLastIdTime(id);

        if (Host.HasGetObjectProperties)
        {
            if (!Host.TryGetObjectProperties(id, out ObjectProperties? props) || props == null)
                return d.Name.Length > 0 ? d : null;
            qualitiesPending = props.QualitiesPending;
            d.Complete = true;
            d.HasIdData |= props.HasIdData;
            if (props.LastIdTime > 0) d.LastIdTime = props.LastIdTime;
            foreach (var kv in props.Ints) d.Ints[kv.Key] = kv.Value;
            foreach (var kv in props.Int64s) d.Int64s[kv.Key] = kv.Value;
            foreach (var kv in props.Bools) d.Bools[kv.Key] = kv.Value;
            foreach (var kv in props.Floats) d.Floats[kv.Key] = kv.Value;
            foreach (var kv in props.Strings) d.Strings[kv.Key] = kv.Value;
            foreach (var kv in props.DataIds) d.DataIds[kv.Key] = kv.Value;
            foreach (var kv in props.InstanceIds) d.InstanceIds[kv.Key] = kv.Value;
            d.Spells.AddRange(props.Spells);
            return d;
        }

        // An engine before v84: one key at a time.
        if (Host.HasGetObjectIntProperty)
            foreach (uint k in PropertyDump.ProbeKeys(PropertyNames.IntKeys, 400))
                if (Host.TryGetObjectIntProperty(id, k, out int v)) d.Ints[k] = v;
        if (Host.HasGetObjectQuadProperty)
            foreach (uint k in PropertyDump.ProbeKeys(PropertyNames.Int64Keys, 12))
                if (Host.TryGetObjectQuadProperty(id, k, out long v)) d.Int64s[k] = v;
        if (Host.HasGetObjectBoolProperty)
            foreach (uint k in PropertyDump.ProbeKeys(PropertyNames.BoolKeys, 300))
                if (Host.TryGetObjectBoolProperty(id, k, out bool v)) d.Bools[k] = v;
        foreach (uint k in PropertyDump.ProbeKeys(PropertyNames.FloatKeys, 400))
            if (Host.TryGetObjectDoubleProperty(id, k, out double v)) d.Floats[k] = v;
        if (Host.HasGetObjectStringProperty)
            foreach (uint k in PropertyDump.ProbeKeys(PropertyNames.StringKeys, 60))
                if (Host.TryGetObjectStringProperty(id, k, out string v)) d.Strings[k] = v;
        if (Host.HasGetObjectDataIdProperty)
            foreach (uint k in PropertyDump.ProbeKeys(PropertyNames.DataIdKeys, 80))
                if (Host.TryGetObjectDataIdProperty(id, k, out uint v)) d.DataIds[k] = v;
        if (Host.HasGetObjectInstanceIdProperty)
            foreach (uint k in PropertyDump.ProbeKeys(PropertyNames.InstanceIdKeys, 50))
                if (Host.TryGetObjectInstanceIdProperty(id, k, out uint v)) d.InstanceIds[k] = v;
        if (Host.HasGetObjectSpellIds)
        {
            var buf = new uint[256];
            int n = Host.GetObjectSpellIds(id, buf, buf.Length);
            for (int i = 0; i < Math.Min(n, buf.Length); i++)
                if (buf[i] != 0 && !d.Spells.Contains(buf[i])) d.Spells.Add(buf[i]);
        }
        return d.PropertyCount == 0 && d.Spells.Count == 0 && d.Name.Length == 0 ? null : d;
    }
}
