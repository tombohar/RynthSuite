using System;
using System.Buffers;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// game.World (UtilityBelt's World): object lookup and search, the selected object, the
/// player's inventory, and the world events (objects created/released, chat, tells,
/// selection). WorldObject properties live in GameWorldObject.cs.
///
/// The SDK has no "every object the client knows" call, so World keeps its own set of
/// known ids: OnCreateObject/OnDeleteObject, the player's inventory (seeded after login,
/// and after a hot reload), and any id a script or the selection proves exists.
/// Everything runs on the plugin pump thread; the SDK reads used here are served from the
/// engine's snapshots (names, types, positions) or guarded reads (ownership, bitfield).
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private readonly HashSet<uint> _worldKnown = new();
    /// <summary>Ids released recently (id → when): the engine's name snapshot can lag a delete.</summary>
    private readonly Dictionary<uint, long> _worldReleased = new();
    private bool _worldSeedPending = true;   // true at load too: a hot reload mid-session seeds the inventory
    private int _worldSeedTries;
    private long _worldNextSeedAt;
    private long _worldNextPruneAt;
    private long _worldNextSnapshotAt;
    private int _worldChangeSeq;             // bumped on create/delete: invalidates the inventory cache
    private readonly uint[] _worldContentsBuf = new uint[1024];
    private uint[]? _worldInventoryCache;
    private long _worldInventoryCacheTick = -1;
    private int _worldInventoryCacheSeq = -1;

    private const long WorldReleasedKeepMs = 10_000;
    private const int WorldMaxSeedTries = 15;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    partial void WorldOnLogin()
    {
        WorldReset();
        _worldSeedPending = true;
    }

    partial void WorldOnLogout()
    {
        WorldReset();
        _worldSeedPending = false;
    }

    private void WorldReset()
    {
        _worldKnown.Clear();
        _worldReleased.Clear();
        _worldClass.Clear();
        _worldInventoryCache = null;
        _worldSeedTries = 0;
        _worldNextSeedAt = 0;
        _worldChangeSeq++;
    }

    partial void WorldTick()
    {
        long now = Environment.TickCount64;

        // Seed the known set with the player and everything they carry. Retried: right
        // after login the engine's object snapshot may not be warm yet.
        if (_worldSeedPending && now >= _worldNextSeedAt)
        {
            _worldNextSeedAt = now + 1000;
            uint pid = Host.GetPlayerId();
            if (pid != 0)
            {
                _worldKnown.Add(pid);
                uint[] inv = WorldInventoryIds();
                foreach (uint id in inv) _worldKnown.Add(id);
                if (inv.Length > 0 || ++_worldSeedTries >= WorldMaxSeedTries) _worldSeedPending = false;
            }
        }

        // Engine v69+: pick up everything in the client's object table, so objects that
        // existed before this plugin loaded (hot reload, late start) are found too.
        if (now >= _worldNextSnapshotAt && Host.HasGetLiveObjectIds && Host.GetPlayerId() != 0)
        {
            _worldNextSnapshotAt = now + 1000;
            int before = _worldKnown.Count;
            foreach (uint id in Host.GetLiveObjectIds())
                if (!_worldReleased.ContainsKey(id)) _worldKnown.Add(id);
            if (_worldKnown.Count != before) _worldChangeSeq++;
        }

        WorldPollPositions();
        WorldPollOpenContainer();

        if (now >= _worldNextPruneAt && _worldReleased.Count > 0)
        {
            _worldNextPruneAt = now + 2000;
            List<uint>? old = null;
            foreach (var kv in _worldReleased)
                if (now - kv.Value > WorldReleasedKeepMs) (old ??= new()).Add(kv.Key);
            if (old != null)
                foreach (uint id in old) _worldReleased.Remove(id);
        }
    }

    // ── Engine events ───────────────────────────────────────────────────────

    partial void WorldOnCreateObject(uint objectId)
    {
        if (objectId == 0) return;
        _worldKnown.Add(objectId);
        _worldReleased.Remove(objectId);
        _worldClass.Remove(objectId);   // ids get reused: classify the new object afresh
        _worldChangeSeq++;
        FireWorld("World.OnObjectCreated", (s, ctx) =>
        {
            var t = new Table(s);
            t["ObjectId"] = (double)objectId;
            t["Object"] = WrapObject(s, ctx, objectId);
            return DynValue.NewTable(t);
        });
    }

    partial void WorldOnDeleteObject(uint objectId)
    {
        if (objectId == 0) return;
        _worldKnown.Remove(objectId);
        _worldClass.Remove(objectId);
        _worldReleased[objectId] = Environment.TickCount64;
        _worldChangeSeq++;
        FireAll("World.OnObjectReleased", s =>
        {
            var t = new Table(s);
            t["ObjectId"] = (double)objectId;
            return DynValue.NewTable(t);
        });
        WorldFireDestroyed(objectId);
    }

    partial void WorldOnChatText(string text, int chatType)
    {
        string message = text.TrimEnd('\r', '\n');
        ChatKind kind = ClassifyChat(message, chatType, out int room, out string senderName, out uint senderId, out string body);

        // UB's ChatEventArgs: SenderId, SenderName, Message, Room (ChatChannel), Type
        // (ChatMessageType), Eat. Incoming lines can't be eaten here (the engine hands them
        // over after AC showed them), so Eat is always false.
        DynValue Args(Script s, string msg)
        {
            var t = new Table(s);
            t["Message"] = msg;
            t["Type"] = (double)chatType;
            t["SenderName"] = senderName;
            t["SenderId"] = (double)senderId;
            t["Room"] = (double)room;
            t["Eat"] = false;
            return DynValue.NewTable(t);
        }

        FireAll("World.OnChatText", s => Args(s, message));
        string? specific = kind switch
        {
            ChatKind.Tell => "World.OnTell",
            ChatKind.Local => "World.OnLocalMessage",
            ChatKind.Channel => "World.OnChannelMessage",
            ChatKind.Fellow => "World.OnFellowMessage",
            ChatKind.Emote => "World.OnEmote",
            _ => null,
        };
        if (specific != null) FireAll(specific, s => Args(s, kind == ChatKind.Emote ? message : body));
    }

    partial void WorldOnSelected(uint currentId, uint previousId)
    {
        if (currentId != 0)
        {
            _worldKnown.Add(currentId);
            _worldReleased.Remove(currentId);
        }
        FireWorld("World.OnSelected", (s, ctx) =>
        {
            var t = new Table(s);
            t["ObjectId"] = (double)currentId;
            t["PreviousId"] = (double)previousId;
            t["Object"] = WrapObject(s, ctx, currentId);
            return DynValue.NewTable(t);
        });
    }

    /// <summary>
    /// Like FireAll, for args that hold WorldObjects (WrapObject needs the script's context).
    /// Cheap when nobody listens: the scripts are only copied once one has a handler.
    /// </summary>
    private void FireWorld(string key, Func<Script, ScriptContext, DynValue> makeArgs)
    {
        bool any = false;
        foreach (var c in _scripts.Values)
            if (c.HasHandlers(key)) { any = true; break; }
        if (!any) return;
        foreach (var ctx in new List<ScriptContext>(_scripts.Values))
            if (ctx.Host.Loaded && ctx.HasHandlers(key)) ctx.Fire(key, s => makeArgs(s, ctx));
    }

    /// <summary>
    /// "Bob tells you, \"hi\"" or "Bob says, \"hi\"" → sender and message. AC's tell lines
    /// may carry a name link, "&lt;Tell:IIDString:1342177281:Bob&gt;Bob&lt;\Tell&gt;", which gives the id too.
    /// </summary>
    internal static void ParseChatSender(string line, out string senderName, out uint senderId, out string body, out bool isTell)
    {
        senderName = string.Empty;
        senderId = 0;
        body = line;
        isTell = false;

        const string tellMark = " tells you, \"";
        const string sayMark = " says, \"";
        int at = line.IndexOf(tellMark, StringComparison.Ordinal);
        int markLen = tellMark.Length;
        if (at > 0) isTell = true;
        else
        {
            at = line.IndexOf(sayMark, StringComparison.Ordinal);
            markLen = sayMark.Length;
            if (at <= 0) return;
        }

        string who = line[..at];
        body = line[(at + markLen)..];
        if (body.EndsWith('"')) body = body[..^1];

        // <Tell:IIDString:ID:NAME>DISPLAY<\Tell>
        if (who.StartsWith("<Tell:", StringComparison.Ordinal))
        {
            int close = who.IndexOf('>');
            if (close > 0)
            {
                string[] parts = who[1..close].Split(':');
                if (parts.Length >= 3) uint.TryParse(parts[2], out senderId);
                int end = who.IndexOf("<\\Tell>", close, StringComparison.Ordinal);
                who = end > close ? who[(close + 1)..end] : (parts.Length >= 4 ? parts[3] : who[(close + 1)..]);
            }
        }
        senderName = who.Trim();
    }

    // ── game.World ──────────────────────────────────────────────────────────

    partial void RegisterWorldApi(Script s, ScriptContext ctx, Table game)
    {
        var world = new Table(s);

        world["OnObjectCreated"] = ctx.Event("World.OnObjectCreated").CreateTable(s);
        world["OnObjectReleased"] = ctx.Event("World.OnObjectReleased").CreateTable(s);
        world["OnChatText"] = ctx.Event("World.OnChatText").CreateTable(s);
        world["OnTell"] = ctx.Event("World.OnTell").CreateTable(s);
        world["OnLocalMessage"] = ctx.Event("World.OnLocalMessage").CreateTable(s);
        world["OnChannelMessage"] = ctx.Event("World.OnChannelMessage").CreateTable(s);
        world["OnFellowMessage"] = ctx.Event("World.OnFellowMessage").CreateTable(s);
        world["OnEmote"] = ctx.Event("World.OnEmote").CreateTable(s);
        world["OnChatInput"] = ctx.Event("World.OnChatInput").CreateTable(s);
        world["OnContainerOpened"] = ctx.Event("World.OnContainerOpened").CreateTable(s);
        world["OnContainerClosed"] = ctx.Event("World.OnContainerClosed").CreateTable(s);
        world["OnTick"] = ctx.Event("World.OnTick").CreateTable(s);
        world["Vendor"] = CreateVendorTable(s, ctx);
        ctx.OnStop.Add(() =>
        {
            foreach (var kv in _worldEatenCommands.ToArray())
                if (kv.Value.Equals(ctx.Name, StringComparison.OrdinalIgnoreCase)) _worldEatenCommands.TryRemove(kv.Key, out _);
        });
        DynValue selected = ctx.Event("World.OnSelected").CreateTable(s);
        world["OnSelected"] = selected;
        world["OnObjectSelected"] = selected;   // UtilityBelt's name for the same event

        world["Get"] = DynValue.NewCallback((c, a) =>
        {
            uint id = WorldIdArg(LuaArgs.At(LuaArgs.Of(a, world), 0), "Get(id)");
            return WorldExists(id) ? WrapObject(s, ctx, id) : DynValue.Nil;
        });

        // UB: local ok, wo = game.World.TryGet(id)
        world["TryGet"] = DynValue.NewCallback((c, a) =>
        {
            uint id = WorldIdArg(LuaArgs.At(LuaArgs.Of(a, world), 0), "TryGet(id)");
            return WorldExists(id)
                ? DynValue.NewTuple(DynValue.True, WrapObject(s, ctx, id))
                : DynValue.NewTuple(DynValue.False, DynValue.Nil);
        });

        world["Exists"] = DynValue.NewCallback((c, a) =>
            DynValue.NewBoolean(WorldExists(WorldIdArg(LuaArgs.At(LuaArgs.Of(a, world), 0), "Exists(id)"))));

        world["GetAll"] = DynValue.NewCallback((c, a) =>
        {
            var filter = WorldFilter.Parse(LuaArgs.At(LuaArgs.Of(a, world), 0), "GetAll");
            return WorldCollect(s, ctx, WorldKnownIds(), filter, landscapeOnly: false);
        });

        world["GetLandscape"] = DynValue.NewCallback((c, a) =>
        {
            var filter = WorldFilter.Parse(LuaArgs.At(LuaArgs.Of(a, world), 0), "GetLandscape");
            return WorldCollect(s, ctx, WorldKnownIds(), filter, landscapeOnly: true);
        });

        world["GetInventory"] = DynValue.NewCallback((c, a) =>
        {
            var filter = WorldFilter.Parse(LuaArgs.At(LuaArgs.Of(a, world), 0), "GetInventory");
            uint[] ids = WorldInventoryIds();
            return WorldCollect(s, ctx, new ArraySegment<uint>(ids), filter, landscapeOnly: false, pooled: false);
        });

        // GetNearest(filter [, DistanceType]): DistanceType.T2D (0) ignores height, T3D (1, the default) doesn't.
        world["GetNearest"] = DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, world);
            var filter = WorldFilter.Parse(LuaArgs.At(args, 0), "GetNearest");
            DynValue dt = LuaArgs.At(args, 1);
            bool flat = !dt.IsNil() && LuaArgs.Num(dt, 1) == 0;
            return WorldNearest(s, ctx, filter, flat);
        });

        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            DynValue key = a.Count > 1 ? a[1] : DynValue.Nil;
            if (key.Type == DataType.Number)   // game.World[id], like UB's indexer
            {
                uint id = (uint)key.Number;
                return WorldExists(id) ? WrapObject(s, ctx, id) : DynValue.Nil;
            }
            switch (key.CastToString())
            {
                case "Selected":
                    uint sel = Host.GetSelectedItemId();
                    return sel != 0 ? WrapObject(s, ctx, sel) : DynValue.Nil;
                case "OpenContainer":
                    uint open = Host.HasGetGroundContainerId ? Host.GetGroundContainerId() : 0;
                    return open != 0 ? WrapObject(s, ctx, open) : DynValue.Nil;
                default:
                    return DynValue.Nil;
            }
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString($"game.World ({_worldKnown.Count} known objects)"));
        world.MetaTable = mt;

        game["World"] = DynValue.NewTable(world);
    }

    private static uint WorldIdArg(DynValue v, string what)
    {
        uint id = LuaArgs.Id(v);
        if (id == 0 && v.Type != DataType.Number && v.Type != DataType.Nil)
        {
            // "12345" or "0x80001234" typed by hand
            string t = (v.CastToString() ?? "").Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                uint.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out id);
            else
                uint.TryParse(t, out id);
            if (id == 0) throw new ScriptRuntimeException($"World.{what}: '{t}' isn't an object id");
        }
        return id;
    }

    /// <summary>True when the client knows object <paramref name="id"/> (a lazy lookup adds it to the known set).</summary>
    internal bool WorldExists(uint id)
    {
        if (id == 0) return false;
        if (_worldKnown.Contains(id)) return true;
        if (_worldReleased.ContainsKey(id)) return false;
        if (Host.TryGetObjectName(id, out _) || Host.TryGetObjectPosition(id, out _, out _, out _, out _))
        {
            _worldKnown.Add(id);
            return true;
        }
        return false;
    }

    /// <summary>Known objects whose wielder is <paramref name="wielder"/> (worn and wielded items).</summary>
    internal List<uint> WorldWieldedIds(uint wielder)
    {
        var list = new List<uint>();
        if (wielder == 0 || !Host.HasGetObjectOwnershipInfo) return list;
        foreach (uint id in _worldKnown)
            if (id != wielder && Host.TryGetObjectOwnershipInfo(id, out _, out uint w, out _) && w == wielder)
                list.Add(id);
        return list;
    }

    /// <summary>A pooled copy of the known ids (a filter function may create or release objects meanwhile).</summary>
    private ArraySegment<uint> WorldKnownIds()
    {
        uint[] ids = ArrayPool<uint>.Shared.Rent(Math.Max(1, _worldKnown.Count));
        _worldKnown.CopyTo(ids);
        return new ArraySegment<uint>(ids, 0, _worldKnown.Count);
    }

    /// <summary>
    /// Everything the player owns: the main pack, side packs and their contents, and what
    /// they wield. Cached for the rest of the tick unless an object is created or released.
    /// </summary>
    internal uint[] WorldInventoryIds()
    {
        if (_worldInventoryCache != null && _worldInventoryCacheTick == LastTick && _worldInventoryCacheSeq == _worldChangeSeq)
            return _worldInventoryCache;

        uint pid = Host.GetPlayerId();
        if (pid == 0 || !Host.HasGetContainerContents) return Array.Empty<uint>();

        var ids = new List<uint>(128);
        List<uint>? packs = null;
        int n = Host.GetContainerContents(pid, _worldContentsBuf);
        for (int i = 0; i < n; i++)
        {
            uint id = _worldContentsBuf[i];
            ids.Add(id);
            if (Host.TryGetItemType(id, out uint type) && (type & ItemTypeContainer) != 0) (packs ??= new()).Add(id);
        }
        if (packs != null)
            foreach (uint pack in packs)
            {
                int m = Host.GetContainerContents(pack, _worldContentsBuf);
                for (int i = 0; i < m; i++) ids.Add(_worldContentsBuf[i]);
            }
        // Worn and wielded items aren't in any container list.
        var inPacks = new HashSet<uint>(ids);
        foreach (uint id in WorldWieldedIds(pid))
            if (inPacks.Add(id)) ids.Add(id);

        _worldInventoryCache = ids.ToArray();
        _worldInventoryCacheTick = LastTick;
        _worldInventoryCacheSeq = _worldChangeSeq;
        return _worldInventoryCache;
    }

    /// <summary>
    /// A world object lying in the world (not the player, not in a container, not wielded).
    /// Position first: pack items have none, and it comes from the engine's snapshot.
    /// </summary>
    private bool WorldIsLandscape(uint id, uint playerId)
    {
        if (id == playerId) return false;
        if (!Host.TryGetObjectPosition(id, out _, out _, out _, out _)) return false;
        if (Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _))
            return container == 0 && wielder == 0;
        return true;
    }

    private DynValue WorldCollect(Script s, ScriptContext ctx, ArraySegment<uint> ids, WorldFilter filter, bool landscapeOnly, bool pooled = true)
    {
        var result = new Table(s);
        try
        {
            uint pid = landscapeOnly ? Host.GetPlayerId() : 0;
            int n = 0;
            foreach (uint id in ids)
            {
                if (!filter.MatchesCheap(this, id)) continue;
                if (landscapeOnly && !WorldIsLandscape(id, pid)) continue;
                DynValue wo = WrapObject(s, ctx, id);
                if (!filter.MatchesFunction(s, wo)) continue;
                result.Set(++n, wo);
            }
        }
        finally
        {
            if (pooled && ids.Array != null) ArrayPool<uint>.Shared.Return(ids.Array);
        }
        return DynValue.NewTable(result);
    }

    /// <summary>The nearest landscape object matching the filter; a function filter is tried nearest-first.</summary>
    private DynValue WorldNearest(Script s, ScriptContext ctx, WorldFilter filter, bool flat)
    {
        uint pid = Host.GetPlayerId();
        if (!WorldTryGetPosition(pid, out WorldPos me)) return DynValue.Nil;

        var candidates = new List<(uint Id, double Dist)>();
        ArraySegment<uint> ids = WorldKnownIds();
        try
        {
            foreach (uint id in ids)
            {
                if (id == pid || !filter.MatchesCheap(this, id)) continue;
                if (!WorldTryGetPosition(id, out WorldPos p)) continue;
                if (Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _) && (container != 0 || wielder != 0))
                    continue;
                double d = flat ? me.DistanceTo2D(p) : me.DistanceTo(p);
                if (double.IsFinite(d)) candidates.Add((id, d));
            }
        }
        finally
        {
            if (ids.Array != null) ArrayPool<uint>.Shared.Return(ids.Array);
        }

        candidates.Sort((x, y) => x.Dist.CompareTo(y.Dist));
        foreach (var (id, _) in candidates)
        {
            DynValue wo = WrapObject(s, ctx, id);
            if (filter.MatchesFunction(s, wo)) return wo;
        }
        return DynValue.Nil;
    }

    /// <summary>
    /// A World filter: nothing, a name substring, an ObjectClass (number, or its exact name
    /// such as "Monster"), an ObjectType (UB's typed value: every flag must be set), or a Lua
    /// function(wo) → bool.
    /// </summary>
    private readonly struct WorldFilter
    {
        private readonly string? _name;
        private readonly int _class;
        private readonly DynValue? _fn;
        private readonly uint _type;

        private WorldFilter(string? name, int cls, DynValue? fn, uint type = 0)
        {
            _name = name;
            _class = cls;
            _fn = fn;
            _type = type;
        }

        public static WorldFilter Parse(DynValue v, string what)
        {
            switch (v.Type)
            {
                case DataType.Nil:
                case DataType.Void:
                    return new WorldFilter(null, -1, null);
                case DataType.Number:
                    return new WorldFilter(null, (int)v.Number, null);
                case DataType.String:
                {
                    string text = v.String;
                    int cls = Array.IndexOf(LuaEnums.ObjectClass, text);   // exact, case-sensitive: "Monster", "Portal"
                    return cls >= 0 ? new WorldFilter(null, cls, null) : new WorldFilter(text, -1, null);
                }
                case DataType.Function:
                case DataType.ClrFunction:
                    return new WorldFilter(null, -1, v);
                case DataType.Table when LuaEnumValues.TryRead(v, out string en, out long flags) && en == "ObjectType":
                    if (flags == 0) throw new ScriptRuntimeException($"World.{what}(ObjectType): pick at least one ObjectType flag");
                    return new WorldFilter(null, -1, null, (uint)flags);
                default:
                    throw new ScriptRuntimeException(
                        $"World.{what}(filter): the filter is a name, an ObjectClass, an ObjectType or a function(wo) returning true/false, not a {v.Type.ToString().ToLowerInvariant()}");
            }
        }

        /// <summary>The name and class parts (no Lua call).</summary>
        public bool MatchesCheap(RynthLuaPlugin p, uint id)
        {
            if (_class >= 0 && p.WorldClassOf(id) != _class) return false;
            if (_type != 0 && (!p.Host.TryGetItemType(id, out uint t) || (t & _type) != _type)) return false;
            if (_name != null)
            {
                if (!p.Host.TryGetObjectName(id, out string n) || n.IndexOf(_name, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        public bool MatchesFunction(Script s, DynValue wo)
        {
            if (_fn == null) return true;
            return s.Call(_fn, wo).CastToBool();
        }
    }
}
