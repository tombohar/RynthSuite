using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// WorldObject properties (UtilityBelt's WorldObject): WrapObject makes the table, World gives
/// it one shared metatable per script whose <c>__index</c> reads every property live through
/// the SDK, plus <c>__tostring</c> and <c>__eq</c> (same Id). Methods (IntValue, DistanceTo,
/// Items, ...) are bound on first use and stored on the object, so wrapping hundreds of
/// objects in GetAll costs one table each. Action methods (Use, Give, ...) are Actions'.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    /// <summary>The WorldObject metatable of each running script's Lua state.</summary>
    private readonly Dictionary<Script, Table> _worldObjectMeta = new();
    /// <summary>ObjectClass per id, once it was read from reliable data (see WorldClassify).</summary>
    private readonly Dictionary<uint, int> _worldClass = new();

    // AC ITEM_TYPE flags (WorldObject.ObjectType)
    private const uint ItemTypeMeleeWeapon = 0x00000001;
    private const uint ItemTypeArmor = 0x00000002;
    private const uint ItemTypeClothing = 0x00000004;
    private const uint ItemTypeJewelry = 0x00000008;
    private const uint ItemTypeCreature = 0x00000010;
    private const uint ItemTypeFood = 0x00000020;
    private const uint ItemTypeMoney = 0x00000040;
    private const uint ItemTypeMisc = 0x00000080;
    private const uint ItemTypeMissileWeapon = 0x00000100;
    private const uint ItemTypeContainer = 0x00000200;
    private const uint ItemTypeGem = 0x00000800;
    private const uint ItemTypeSpellComponents = 0x00001000;
    private const uint ItemTypeWritable = 0x00002000;
    private const uint ItemTypeKey = 0x00004000;
    private const uint ItemTypeCaster = 0x00008000;
    private const uint ItemTypePortal = 0x00010000;
    private const uint ItemTypePromissoryNote = 0x00040000;
    private const uint ItemTypeManaStone = 0x00080000;
    private const uint ItemTypeService = 0x00100000;
    private const uint ItemTypeCraftCookingBase = 0x00400000;
    private const uint ItemTypeCraftAlchemyBase = 0x00800000;
    private const uint ItemTypeCraftFletchingBase = 0x02000000;
    private const uint ItemTypeCraftAlchemyIntermediate = 0x04000000;
    private const uint ItemTypeCraftFletchingIntermediate = 0x08000000;
    private const uint ItemTypeLifeStone = 0x10000000;
    private const uint ItemTypeTinkeringTool = 0x20000000;
    private const uint ItemTypeTinkeringMaterial = 0x40000000;

    // PublicWeenieDesc._bitfield (ObjectDescriptionFlag)
    private const uint BfPlayer = 0x8;
    private const uint BfAttackable = 0x10;
    private const uint BfVendor = 0x200;
    private const uint BfDoor = 0x1000;
    private const uint BfCorpse = 0x2000;
    private const uint BfLifestone = 0x4000;
    private const uint BfFood = 0x8000;
    private const uint BfHealer = 0x10000;
    private const uint BfLockpick = 0x20000;
    private const uint BfPortal = 0x40000;

    // STypeInt ids behind the named properties
    private const uint IntBurden = 5;          // ENCUMB_VAL_INT
    private const uint IntCurrentWieldedLocation = 10;
    private const uint IntStackSize = 12;
    private const uint IntValueProp = 19;

    partial void DecorateObject(Script s, ScriptContext ctx, uint id, Table obj)
    {
        obj.MetaTable = WorldObjectMeta(s, ctx);
    }

    private Table WorldObjectMeta(Script s, ScriptContext ctx)
    {
        if (_worldObjectMeta.TryGetValue(s, out Table? mt)) return mt;

        mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            if (a.Count < 2 || a[0].Type != DataType.Table) return DynValue.Nil;
            return WorldObjectIndex(s, ctx, a[0].Table, a[1].CastToString() ?? string.Empty);
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
        {
            uint id = a.Count > 0 ? LuaArgs.Id(a[0]) : 0;
            string name = Host.TryGetObjectName(id, out string n) ? n : "?";
            return DynValue.NewString($"{name} [0x{id:X8}]");
        });
        mt["__eq"] = DynValue.NewCallback((c, a) =>
        {
            uint x = a.Count > 0 ? LuaArgs.Id(a[0]) : 0;
            uint y = a.Count > 1 ? LuaArgs.Id(a[1]) : 0;
            return DynValue.NewBoolean(x != 0 && x == y);
        });

        _worldObjectMeta[s] = mt;
        ctx.OnStop.Add(() => _worldObjectMeta.Remove(s));
        return mt;
    }

    private DynValue WorldObjectIndex(Script s, ScriptContext ctx, Table obj, string key)
    {
        DynValue idv = obj.Get("Id");
        if (idv.Type != DataType.Number) return DynValue.Nil;
        uint id = (uint)idv.Number;

        switch (key)
        {
            case "Name":
                return DynValue.NewString(Host.TryGetObjectName(id, out string name) ? name : string.Empty);
            case "WeenieClassId":
                return DynValue.NewNumber(Host.TryGetObjectWcid(id, out uint wcid) ? wcid : 0);
            case "ObjectClass":
                return DynValue.NewNumber(WorldClassOf(id));
            case "ObjectClassName":
            {
                int oc = WorldClassOf(id);
                return DynValue.NewString(oc >= 0 && oc < LuaEnums.ObjectClass.Length ? LuaEnums.ObjectClass[oc] : "Unknown");
            }
            case "ObjectType":   // a typed ObjectType value (UB: wo.ObjectType == ObjectType.Creature)
                return LuaEnumValues.Get(s, "ObjectType", Host.TryGetItemType(id, out uint type) ? type : 0);
            // Enum-valued int properties, as numbers (compare with the enum globals).
            case "ValidWieldedLocations": return IntProp(id, 9);
            case "ClothingPriority": return IntProp(id, 4);
            case "Material": return IntProp(id, 131);
            case "AmmoType": return IntProp(id, 50);
            case "TargetType": return IntProp(id, 94);
            case "CombatUse": return IntProp(id, 51);
            case "RadarColor": return IntProp(id, 95);
            case "RadarBehavior": return IntProp(id, 133);
            case "IconEffects": return IntProp(id, 18);
            case "ObjectDescriptionFlag":   // PublicWeenieDesc bitfield (compare with ObjectDescriptionFlag.X)
                return DynValue.NewNumber(Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(id, out uint bits) ? bits : 0);
            case "PhysicsState":            // the last SetState the server sent (PhysicsState flags), 0 when none seen
                return DynValue.NewNumber(Host.HasGetObjectState && Host.TryGetObjectState(id, out uint ps) ? ps : 0);
            case "CombatMode":
                return id == Host.GetPlayerId() ? DynValue.NewNumber(Host.GetCurrentCombatMode()) : IntProp(id, 40);
            case "SpellId":
                if (Host.TryGetObjectDataIdProperty(id, 28, out uint spell) && spell != 0) return DynValue.NewNumber(spell);
                return DynValue.NewNumber(WorldSpellIds(id) is { Count: > 0 } sp ? sp[0] : 0);
            case "SpellIds":
                return LayeredList(s, WorldSpellIds(id));
            case "EnchantmentIds":
                return LayeredList(s, WorldEnchantmentIds(id));
            // Lists (UB: properties; Items is also still callable, wo:Items()).
            case "ItemIds": return IdList(s, WorldChildIds(id, containers: false));
            case "Items": return ObjectList(s, ctx, WorldChildIds(id, containers: false));
            case "ContainerIds": return IdList(s, WorldChildIds(id, containers: true));
            case "Containers": return ObjectList(s, ctx, WorldChildIds(id, containers: true));
            case "AllItemIds": return IdList(s, WorldAllItemIds(id));
            case "AllItems": return ObjectList(s, ctx, WorldAllItemIds(id));
            case "EquipmentIds": return IdList(s, WorldWieldedIds(id));
            case "Equipment": return ObjectList(s, ctx, WorldWieldedIds(id));
            case "ServerPosition":
                return WorldServerPosition(s, id);
            case "Vitals":
            case "Skills":
            case "Attributes":
            {
                // The player's own stats (UB keeps them on game.Character.Weenie); other
                // objects' stats aren't readable, so theirs are empty.
                if (id == Host.GetPlayerId())
                {
                    DynValue t = s.Registry.Get("rynth.character." + key);
                    if (t.Type == DataType.Table) return t;
                }
                return DynValue.NewTable(new Table(s));
            }
            case "OnPositionChanged":
                // Baseline now, so a move right after subscribing is seen.
                if (!_worldWatchedPos.ContainsKey(id) && WorldTryGetPosition(id, out WorldPos now)) _worldWatchedPos[id] = now;
                return ctx.Event($"WorldObject.{key}:{id}").CreateTable(s);
            case "OnDestroyed":
                return ctx.Event($"WorldObject.{key}:{id}").CreateTable(s);
            case "ContainerId":
                return DynValue.NewNumber(Host.TryGetObjectOwnershipInfo(id, out uint container, out _, out _) ? container : 0);
            case "WielderId":
                return DynValue.NewNumber(Host.TryGetObjectOwnershipInfo(id, out _, out uint wielder, out _) ? wielder : 0);
            case "Container":
                return Host.TryGetObjectOwnershipInfo(id, out uint cid, out _, out _) ? WrapObject(s, ctx, cid) : DynValue.Nil;
            case "Wielder":
                return Host.TryGetObjectOwnershipInfo(id, out _, out uint wid, out _) ? WrapObject(s, ctx, wid) : DynValue.Nil;
            case "CurrentWieldedLocation":
                if (Host.TryGetObjectOwnershipInfo(id, out _, out _, out uint loc)) return DynValue.NewNumber(loc);
                return DynValue.NewNumber(Host.TryGetObjectIntProperty(id, IntCurrentWieldedLocation, out int l) ? l : 0);
            case "Burden":
                return DynValue.NewNumber(Host.TryGetObjectIntProperty(id, IntBurden, out int burden) ? burden : 0);
            case "StackSize":
                return DynValue.NewNumber(Host.TryGetObjectIntProperty(id, IntStackSize, out int stack) && stack > 0 ? stack : 1);
            case "HasAppraisalData":
                return DynValue.NewBoolean(Host.HasAppraisalData(id));
            case "Position":
                return WorldPositionTable(s, id);

            // Methods: bound to this object on first use and kept as a raw field.
            case "IntValue":
            case "FloatValue":
            case "StringValue":
            case "BoolValue":
            case "DataValue":
            case "Int64Value":
            case "DistanceTo":
            case "DistanceTo2D":
            case "DistanceTo2d":
            case "DistanceTo3D":
            case "Value":
            case "HasValue":
            case "GetTopmostParent":
            {
                DynValue fn = WorldObjectMethod(s, ctx, obj, id, key);
                obj.Set(key, fn);
                return fn;
            }
            default:
                return DynValue.Nil;
        }
    }

    private DynValue WorldObjectMethod(Script s, ScriptContext ctx, Table obj, uint id, string key)
    {
        switch (key)
        {
            case "IntValue":
                return DynValue.NewCallback((c, a) =>
                {
                    var args = LuaArgs.Of(a, obj);
                    uint stype = PropertyIdArg(args, "IntValue");
                    return Host.TryGetObjectIntProperty(id, stype, out int v) ? DynValue.NewNumber(v) : DefaultArg(args, DynValue.NewNumber(0));
                });
            case "Int64Value":
                return DynValue.NewCallback((c, a) =>
                {
                    var args = LuaArgs.Of(a, obj);
                    uint stype = PropertyIdArg(args, "Int64Value");
                    return Host.TryGetObjectQuadProperty(id, stype, out long v) ? DynValue.NewNumber(v) : DefaultArg(args, DynValue.NewNumber(0));
                });
            case "FloatValue":
                return DynValue.NewCallback((c, a) =>
                {
                    var args = LuaArgs.Of(a, obj);
                    uint stype = PropertyIdArg(args, "FloatValue");
                    return Host.TryGetObjectDoubleProperty(id, stype, out double v) ? DynValue.NewNumber(v) : DefaultArg(args, DynValue.NewNumber(0));
                });
            case "StringValue":
                return DynValue.NewCallback((c, a) =>
                {
                    var args = LuaArgs.Of(a, obj);
                    uint stype = PropertyIdArg(args, "StringValue");
                    return Host.TryGetObjectStringProperty(id, stype, out string v) ? DynValue.NewString(v) : DefaultArg(args, DynValue.NewString(string.Empty));
                });
            case "BoolValue":
                return DynValue.NewCallback((c, a) =>
                {
                    var args = LuaArgs.Of(a, obj);
                    uint stype = PropertyIdArg(args, "BoolValue");
                    return Host.TryGetObjectBoolProperty(id, stype, out bool v) ? DynValue.NewBoolean(v) : DefaultArg(args, DynValue.False);
                });
            case "DataValue":
                return DynValue.NewCallback((c, a) =>
                {
                    var args = LuaArgs.Of(a, obj);
                    uint stype = PropertyIdArg(args, "DataValue");
                    return Host.TryGetObjectDataIdProperty(id, stype, out uint v) ? DynValue.NewNumber(v) : DefaultArg(args, DynValue.NewNumber(0));
                });
            case "DistanceTo":
            case "DistanceTo3D":
            case "DistanceTo2D":
            case "DistanceTo2d":   // UB's id overload
            {
                bool flat = key is "DistanceTo2D" or "DistanceTo2d";
                return DynValue.NewCallback((c, a) =>
                {
                    DynValue other = LuaArgs.At(LuaArgs.Of(a, obj), 0);
                    uint otherId = LuaArgs.Id(other);
                    if (otherId == 0)
                        throw new ScriptRuntimeException($"{key}(other) needs a WorldObject or an object id");
                    if (!WorldTryGetPosition(id, out WorldPos p1) || !WorldTryGetPosition(otherId, out WorldPos p2))
                        return DynValue.NewNumber(double.PositiveInfinity);   // unknown position: never "in range"
                    double d = flat ? p1.DistanceTo2D(p2) : p1.DistanceTo(p2);
                    return DynValue.NewNumber(double.IsFinite(d) ? d : double.PositiveInfinity);
                });
            }
            case "Value":
                // UB: wo:Value(IntId.X [, default]); the key's enum picks the property kind (a
                // plain number is an IntId). wo:Value() with no key is the object's value in pyreals.
                return DynValue.NewCallback((c, a) =>
                {
                    var args = LuaArgs.Of(a, obj);
                    if (args.Length == 0 || args[0].IsNil())
                        return DynValue.NewNumber(Host.TryGetObjectIntProperty(id, IntValueProp, out int pv) ? pv : 0);
                    return WorldReadValue(id, args[0], out DynValue v, "Value") ? v : DefaultArg(args, WorldValueDefault(args[0]));
                });
            case "HasValue":
                return DynValue.NewCallback((c, a) =>
                    DynValue.NewBoolean(WorldReadValue(id, LuaArgs.At(LuaArgs.Of(a, obj), 0), out _, "HasValue")));
            case "GetTopmostParent":
                return DynValue.NewCallback((c, a) => WrapObject(s, ctx, WorldTopmostParent(id)));
            default:
                return DynValue.Nil;
        }
    }

    private static uint PropertyIdArg(DynValue[] args, string what)
    {
        DynValue v = LuaArgs.At(args, 0);
        double d = LuaArgs.Num(v, -1);
        if (d < 0 || v.IsNil())
            throw new ScriptRuntimeException($"{what}(propertyId [, default]) needs a property id number, e.g. wo:{what}(5)");
        return (uint)d;
    }

    /// <summary>The caller's default (even an explicit nil) when given, else UtilityBelt's (0, "", false).</summary>
    private static DynValue DefaultArg(DynValue[] args, DynValue fallback) => args.Length > 1 ? args[1] : fallback;

    // ── UtilityBelt's WorldObject extras ────────────────────────────────────

    private DynValue IntProp(uint id, uint stype) =>
        DynValue.NewNumber(Host.TryGetObjectIntProperty(id, stype, out int v) ? v : 0);

    /// <summary>
    /// wo:Value(key) / wo:HasValue(key): the property kind comes from the key's enum (typed
    /// Int64Id, FloatId, StringId, BoolId, DataId, InstanceId); a plain number is an IntId.
    /// </summary>
    private bool WorldReadValue(uint id, DynValue key, out DynValue value, string what)
    {
        value = DynValue.Nil;
        if (LuaEnumValues.TryRead(key, out string kind, out long n))
        {
            uint stype = (uint)n;
            switch (kind)
            {
                case "StringId":
                    if (!Host.TryGetObjectStringProperty(id, stype, out string sv)) return false;
                    value = DynValue.NewString(sv);
                    return true;
                case "FloatId":
                    if (!Host.TryGetObjectDoubleProperty(id, stype, out double dv)) return false;
                    value = DynValue.NewNumber(dv);
                    return true;
                case "BoolId":
                    if (!Host.TryGetObjectBoolProperty(id, stype, out bool bv)) return false;
                    value = DynValue.NewBoolean(bv);
                    return true;
                case "DataId":
                    if (!Host.TryGetObjectDataIdProperty(id, stype, out uint did)) return false;
                    value = DynValue.NewNumber(did);
                    return true;
                case "Int64Id":
                    if (!Host.TryGetObjectQuadProperty(id, stype, out long qv)) return false;
                    value = DynValue.NewNumber(qv);
                    return true;
                case "InstanceId":
                {
                    // API v73 engines answer every PropertyInstanceId (Monarch, Allegiance,
                    // Patron, PetOwner, HouseOwner, ...); older ones serve ownership only:
                    // Container (2) and Wielder (3).
                    uint iv;
                    if (Host.HasGetObjectInstanceIdProperty)
                    {
                        if (!Host.TryGetObjectInstanceIdProperty(id, stype, out iv)) return false;
                    }
                    else
                    {
                        if (stype is not (2 or 3) || !Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _)) return false;
                        iv = stype == 2 ? container : wielder;
                    }
                    if (iv == 0) return false;
                    value = DynValue.NewNumber(iv);
                    return true;
                }
                default:
                    throw new ScriptRuntimeException($"{what}(key): {kind} isn't a property id (use IntId, Int64Id, FloatId, StringId, BoolId, DataId or InstanceId)");
            }
        }
        if (key.Type == DataType.Number)
        {
            if (!Host.TryGetObjectIntProperty(id, (uint)key.Number, out int iv)) return false;
            value = DynValue.NewNumber(iv);
            return true;
        }
        throw new ScriptRuntimeException($"{what}(key [, default]) needs a property id, e.g. wo:{what}(IntId.Value) or wo:{what}(StringId.Name)");
    }

    /// <summary>UtilityBelt's default for a missing property: "" for strings, false for bools, else 0.</summary>
    private static DynValue WorldValueDefault(DynValue key)
    {
        if (LuaEnumValues.TryRead(key, out string kind, out _))
            return kind switch { "StringId" => DynValue.NewString(string.Empty), "BoolId" => DynValue.False, _ => DynValue.NewNumber(0) };
        return DynValue.NewNumber(0);
    }

    /// <summary>What <paramref name="id"/> directly holds: items (<paramref name="containers"/> false) or side packs.</summary>
    private List<uint> WorldChildIds(uint id, bool containers)
    {
        var list = new List<uint>();
        if (id == 0 || !Host.HasGetContainerContents) return list;
        uint[] buf = new uint[512];
        int n = Host.GetContainerContents(id, buf);
        for (int i = 0; i < n; i++)
        {
            bool isPack = Host.TryGetItemType(buf[i], out uint t) && (t & ItemTypeContainer) != 0;
            if (isPack == containers) list.Add(buf[i]);
        }
        return list;
    }

    /// <summary>Every item <paramref name="id"/> holds, side packs' contents included (not the packs).</summary>
    private List<uint> WorldAllItemIds(uint id)
    {
        var list = WorldChildIds(id, containers: false);
        foreach (uint pack in WorldChildIds(id, containers: true))
            list.AddRange(WorldChildIds(pack, containers: false));
        return list;
    }

    private List<uint> WorldSpellIds(uint id)
    {
        var list = new List<uint>();
        if (!Host.HasGetObjectSpellIds) return list;
        uint[] buf = new uint[64];
        int n = Host.GetObjectSpellIds(id, buf, buf.Length);
        for (int i = 0; i < Math.Min(n, buf.Length); i++) if (buf[i] != 0) list.Add(buf[i]);
        return list;
    }

    private List<uint> WorldEnchantmentIds(uint id)
    {
        var list = new List<uint>();
        if (!Host.HasReadObjectEnchantments) return list;
        uint[] ids = new uint[256];
        double[] exp = new double[256];
        int n = Host.ReadObjectEnchantments(id, ids, exp, ids.Length);
        for (int i = 0; i < Math.Min(n, ids.Length); i++) if (ids[i] != 0) list.Add(ids[i]);
        return list;
    }

    /// <summary>UB's LayeredSpellId list: <c>{ Id = spellId }</c> per spell (the layer isn't known here).</summary>
    private static DynValue LayeredList(Script s, List<uint> spells)
    {
        var t = new Table(s);
        for (int i = 0; i < spells.Count; i++)
        {
            var e = new Table(s);
            e["Id"] = (double)spells[i];
            t.Set(i + 1, DynValue.NewTable(e));
        }
        return LuaLists.Wrap(s, t);
    }

    internal static DynValue IdList(Script s, List<uint> ids)
    {
        var t = new Table(s);
        for (int i = 0; i < ids.Count; i++) t.Set(i + 1, DynValue.NewNumber(ids[i]));
        return LuaLists.Wrap(s, t);
    }

    internal DynValue ObjectList(Script s, ScriptContext ctx, List<uint> ids)
    {
        var t = new Table(s);
        int n = 0;
        foreach (uint id in ids) t.Set(++n, WrapObject(s, ctx, id));
        return LuaLists.Wrap(s, t);
    }

    /// <summary>The outermost container or wielder holding <paramref name="id"/> (itself when nothing holds it).</summary>
    private uint WorldTopmostParent(uint id)
    {
        uint cur = id;
        for (int depth = 0; depth < 8; depth++)
        {
            if (!Host.TryGetObjectOwnershipInfo(cur, out uint container, out uint wielder, out _)) break;
            uint parent = container != 0 ? container : wielder;
            if (parent == 0 || parent == cur) break;
            cur = parent;
        }
        return cur;
    }

    /// <summary>
    /// UB's ServerPosition: <c>{ Landcell, Frame = { Origin = {X, Y, Z}, Orientation = {W, X, Y, Z} } }</c>
    /// with <c>DistanceTo2D(pos)</c> / <c>DistanceTo3D(pos)</c>; nil when the object has no
    /// position (in a pack, wielded, or not in view).
    /// </summary>
    internal DynValue WorldServerPosition(Script s, uint id)
    {
        if (!WorldTryGetPosition(id, out WorldPos p)) return DynValue.Nil;
        float qw = 1, qx = 0, qy = 0, qz = 0;
        if (id == Host.GetPlayerId() && Host.HasGetPlayerPose)
            Host.TryGetPlayerPose(out _, out _, out _, out _, out qw, out qx, out qy, out qz);
        else if (Host.HasGetObjectHeading && Host.TryGetObjectHeading(id, out float heading))
        {
            double half = -heading * Math.PI / 360.0;
            qw = (float)Math.Cos(half);
            qz = (float)Math.Sin(half);
        }
        var origin = new Table(s);
        origin["X"] = (double)p.X; origin["Y"] = (double)p.Y; origin["Z"] = (double)p.Z;
        var orient = new Table(s);
        orient["W"] = (double)qw; orient["X"] = (double)qx; orient["Y"] = (double)qy; orient["Z"] = (double)qz;
        var frame = new Table(s);
        frame["Origin"] = DynValue.NewTable(origin);
        frame["Orientation"] = DynValue.NewTable(orient);
        var t = new Table(s);
        t["Landcell"] = (double)p.Cell;
        t["Frame"] = DynValue.NewTable(frame);
        t.MetaTable = PositionMeta(s);
        return DynValue.NewTable(t);
    }

    private static Table PositionMeta(Script s)
    {
        DynValue cached = s.Registry.Get("rynth.positionmeta");
        if (cached.Type == DataType.Table) return cached.Table;
        var methods = new Table(s);
        DynValue Dist(bool flat) => DynValue.NewCallback((c, a) =>
        {
            if (a.Count < 2 || !TryPosition(a[0], out WorldPos p1) || !TryPosition(a[1], out WorldPos p2))
                throw new ScriptRuntimeException($"{(flat ? "DistanceTo2D" : "DistanceTo3D")}(position) needs a Position (wo.ServerPosition)");
            return DynValue.NewNumber(flat ? p1.DistanceTo2D(p2) : p1.DistanceTo(p2));
        });
        methods["DistanceTo2D"] = Dist(true);
        methods["DistanceTo3D"] = Dist(false);
        var mt = new Table(s);
        mt["__index"] = DynValue.NewTable(methods);
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
            DynValue.NewString(TryPosition(a[0], out WorldPos p) ? $"Position 0x{p.Cell:X8} [{p.X:0.00} {p.Y:0.00} {p.Z:0.00}]" : "Position"));
        s.Registry.Set("rynth.positionmeta", DynValue.NewTable(mt));
        return mt;
    }

    private static bool TryPosition(DynValue v, out WorldPos pos)
    {
        pos = default;
        if (v.Type != DataType.Table) return false;
        DynValue cell = v.Table.Get("Landcell");
        DynValue frame = v.Table.Get("Frame");
        if (cell.Type != DataType.Number || frame.Type != DataType.Table) return false;
        DynValue origin = frame.Table.Get("Origin");
        if (origin.Type != DataType.Table) return false;
        pos = new WorldPos((uint)cell.Number, (float)LuaArgs.Num(origin.Table.Get("X")),
            (float)LuaArgs.Num(origin.Table.Get("Y")), (float)LuaArgs.Num(origin.Table.Get("Z")));
        return true;
    }

    // ── Per-object events (OnPositionChanged, OnDestroyed) ──────────────────

    private readonly Dictionary<uint, WorldPos> _worldWatchedPos = new();

    /// <summary>Polls the positions of objects some script watches with wo.OnPositionChanged.</summary>
    private void WorldPollPositions()
    {
        const string prefix = "WorldObject.OnPositionChanged:";
        HashSet<uint>? watched = null;
        foreach (var ctx in _scripts.Values)
        {
            if (!ctx.Host.Loaded) continue;
            foreach (string key in ctx.EventKeys(prefix))
                if (uint.TryParse(key.AsSpan(prefix.Length), out uint id)) (watched ??= new()).Add(id);
        }
        if (watched == null) { _worldWatchedPos.Clear(); return; }
        foreach (uint id in new List<uint>(_worldWatchedPos.Keys))
            if (!watched.Contains(id)) _worldWatchedPos.Remove(id);
        foreach (uint id in watched)
        {
            if (!WorldTryGetPosition(id, out WorldPos p)) continue;
            bool had = _worldWatchedPos.TryGetValue(id, out WorldPos old);
            _worldWatchedPos[id] = p;
            if (!had || (old.Cell == p.Cell && old.X == p.X && old.Y == p.Y && old.Z == p.Z)) continue;
            uint oid = id;
            FireWorld(prefix + oid, (s, ctx) =>
            {
                var t = new Table(s);
                t["Weenie"] = WrapObject(s, ctx, oid);
                t["Position"] = WorldServerPosition(s, oid);
                return DynValue.NewTable(t);
            });
        }
    }

    /// <summary>wo.OnDestroyed for a released object (then its per-object events are dropped).</summary>
    private void WorldFireDestroyed(uint id)
    {
        string key = "WorldObject.OnDestroyed:" + id;
        FireAll(key, s =>
        {
            var t = new Table(s);
            t["ObjectId"] = (double)id;
            return DynValue.NewTable(t);
        });
        foreach (var ctx in _scripts.Values)
        {
            ctx.DropEvent(key);
            ctx.DropEvent("WorldObject.OnPositionChanged:" + id);
        }
        _worldWatchedPos.Remove(id);
    }

    // ── Position ────────────────────────────────────────────────────────────

    internal readonly struct WorldPos
    {
        public readonly uint Cell;
        public readonly float X, Y, Z;

        public WorldPos(uint cell, float x, float y, float z)
        {
            Cell = cell;
            X = x;
            Y = y;
            Z = z;
        }

        // Landblock-local → global: each landblock is 192 m across (same as RynthAi's distance).
        private double GX => ((Cell >> 24) & 0xFF) * 192.0 + X;
        private double GY => ((Cell >> 16) & 0xFF) * 192.0 + Y;

        public double DistanceTo2D(in WorldPos o)
        {
            double dx = GX - o.GX, dy = GY - o.GY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public double DistanceTo(in WorldPos o)
        {
            double dx = GX - o.GX, dy = GY - o.GY, dz = (double)Z - o.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>An object's position; the player's from its pose (fresher than the object snapshot).</summary>
    internal bool WorldTryGetPosition(uint id, out WorldPos pos)
    {
        pos = default;
        if (id == 0) return false;
        if (id == Host.GetPlayerId() && Host.HasGetPlayerPose
            && Host.TryGetPlayerPose(out uint pc, out float px, out float py, out float pz, out _, out _, out _, out _) && pc != 0)
        {
            pos = new WorldPos(pc, px, py, pz);
            return true;
        }
        if (!Host.TryGetObjectPosition(id, out uint cell, out float x, out float y, out float z) || cell == 0) return false;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        pos = new WorldPos(cell, x, y, z);
        return true;
    }

    private DynValue WorldPositionTable(Script s, uint id)
    {
        if (!WorldTryGetPosition(id, out WorldPos p)) return DynValue.Nil;
        var t = new Table(s);
        t["Landcell"] = (double)p.Cell;
        t["X"] = (double)p.X;
        t["Y"] = (double)p.Y;
        t["Z"] = (double)p.Z;
        if (NavCoordinateHelper.TryConvertPoseToCoords(p.Cell, p.X, p.Y, out double ns, out double ew))
        {
            t["NS"] = ns;
            t["EW"] = ew;
        }
        return DynValue.NewTable(t);
    }

    // ── ObjectClass ─────────────────────────────────────────────────────────

    /// <summary>The object's ObjectClass (LuaEnums numbering); cached once it came from reliable data.</summary>
    internal int WorldClassOf(uint id)
    {
        if (_worldClass.TryGetValue(id, out int cls)) return cls;
        cls = WorldClassify(id, out bool certain);
        if (certain) _worldClass[id] = cls;
        return cls;
    }

    private static readonly int OcUnknown = LuaEnums.ObjectClassOf("Unknown");
    private static readonly int OcMeleeWeapon = LuaEnums.ObjectClassOf("MeleeWeapon");
    private static readonly int OcArmor = LuaEnums.ObjectClassOf("Armor");
    private static readonly int OcClothing = LuaEnums.ObjectClassOf("Clothing");
    private static readonly int OcJewelry = LuaEnums.ObjectClassOf("Jewelry");
    private static readonly int OcMonster = LuaEnums.ObjectClassOf("Monster");
    private static readonly int OcFood = LuaEnums.ObjectClassOf("Food");
    private static readonly int OcMoney = LuaEnums.ObjectClassOf("Money");
    private static readonly int OcMisc = LuaEnums.ObjectClassOf("Misc");
    private static readonly int OcMissileWeapon = LuaEnums.ObjectClassOf("MissileWeapon");
    private static readonly int OcContainer = LuaEnums.ObjectClassOf("Container");
    private static readonly int OcGem = LuaEnums.ObjectClassOf("Gem");
    private static readonly int OcSpellComponent = LuaEnums.ObjectClassOf("SpellComponent");
    private static readonly int OcKey = LuaEnums.ObjectClassOf("Key");
    private static readonly int OcPortal = LuaEnums.ObjectClassOf("Portal");
    private static readonly int OcTradeNote = LuaEnums.ObjectClassOf("TradeNote");
    private static readonly int OcManaStone = LuaEnums.ObjectClassOf("ManaStone");
    private static readonly int OcBaseCooking = LuaEnums.ObjectClassOf("BaseCooking");
    private static readonly int OcBaseAlchemy = LuaEnums.ObjectClassOf("BaseAlchemy");
    private static readonly int OcBaseFletching = LuaEnums.ObjectClassOf("BaseFletching");
    private static readonly int OcCraftedAlchemy = LuaEnums.ObjectClassOf("CraftedAlchemy");
    private static readonly int OcCraftedFletching = LuaEnums.ObjectClassOf("CraftedFletching");
    private static readonly int OcPlayer = LuaEnums.ObjectClassOf("Player");
    private static readonly int OcVendor = LuaEnums.ObjectClassOf("Vendor");
    private static readonly int OcDoor = LuaEnums.ObjectClassOf("Door");
    private static readonly int OcCorpse = LuaEnums.ObjectClassOf("Corpse");
    private static readonly int OcLifestone = LuaEnums.ObjectClassOf("Lifestone");
    private static readonly int OcHealingKit = LuaEnums.ObjectClassOf("HealingKit");
    private static readonly int OcLockpick = LuaEnums.ObjectClassOf("Lockpick");
    private static readonly int OcWandStaffOrb = LuaEnums.ObjectClassOf("WandStaffOrb");
    private static readonly int OcBook = LuaEnums.ObjectClassOf("Book");
    private static readonly int OcNpc = LuaEnums.ObjectClassOf("Npc");
    private static readonly int OcFoci = LuaEnums.ObjectClassOf("Foci");
    private static readonly int OcSalvage = LuaEnums.ObjectClassOf("Salvage");
    private static readonly int OcUst = LuaEnums.ObjectClassOf("Ust");
    private static readonly int OcServices = LuaEnums.ObjectClassOf("Services");
    private static readonly int OcScroll = LuaEnums.ObjectClassOf("Scroll");

    /// <summary>
    /// Decal/UtilityBelt-style ObjectClass from the PublicWeenieDesc bitfield (player, vendor,
    /// corpse, door, portal, lifestone, attackable) and the ItemType flags, names only as a
    /// fallback. <paramref name="certain"/> is false when the data it needs wasn't readable yet
    /// (a fresh object), so the caller retries later instead of caching a guess.
    /// </summary>
    private int WorldClassify(uint id, out bool certain)
    {
        certain = false;
        uint pid = Host.GetPlayerId();
        if (id == pid && pid != 0) { certain = true; return OcPlayer; }

        uint bf = 0;
        bool gotBf = Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(id, out bf) && bf != 0;
        if (!gotBf) bf = 0;
        bool gotType = Host.TryGetItemType(id, out uint type) && type != 0;
        Host.TryGetObjectName(id, out string name);

        if (gotBf)
        {
            if ((bf & BfPlayer) != 0) { certain = true; return OcPlayer; }
            if ((bf & BfVendor) != 0) { certain = true; return OcVendor; }
            if ((bf & BfCorpse) != 0) { certain = true; return OcCorpse; }
            if ((bf & BfDoor) != 0) { certain = true; return OcDoor; }
            if ((bf & BfPortal) != 0) { certain = true; return OcPortal; }
            if ((bf & BfLifestone) != 0) { certain = true; return OcLifestone; }
        }

        if (gotType && (type & ItemTypeCreature) != 0)
        {
            if (gotBf) { certain = true; return (bf & BfAttackable) != 0 ? OcMonster : OcNpc; }
            // No bitfield yet: the engine's attackable snapshot is the next best signal.
            return Host.HasObjectIsAttackable && Host.ObjectIsAttackable(id) ? OcMonster : OcNpc;
        }

        if (!gotBf && IsCorpseName(name)) return OcCorpse;

        if (gotBf)
        {
            if ((bf & BfHealer) != 0) { certain = true; return OcHealingKit; }
            if ((bf & BfLockpick) != 0) { certain = true; return OcLockpick; }
        }

        if (gotType)
        {
            certain = true;
            int cls = ClassifyByItemType(type);
            if (cls == OcBook && name.Contains("Scroll", StringComparison.OrdinalIgnoreCase)) return OcScroll;
            if (name.StartsWith("Foci of", StringComparison.OrdinalIgnoreCase)) return OcFoci;
            if (cls == OcMisc && gotBf && (bf & BfFood) != 0) return OcFood;
            return cls;
        }

        if (gotBf && (bf & BfFood) != 0) { certain = true; return OcFood; }
        return OcUnknown;
    }

    private static int ClassifyByItemType(uint t)
    {
        // Most specific first (the order RynthAi's cache uses).
        if ((t & ItemTypePromissoryNote) != 0) return OcTradeNote;
        if ((t & ItemTypeManaStone) != 0) return OcManaStone;
        if ((t & ItemTypeSpellComponents) != 0) return OcSpellComponent;
        if ((t & ItemTypeGem) != 0) return OcGem;
        if ((t & ItemTypeKey) != 0) return OcKey;
        if ((t & ItemTypeLifeStone) != 0) return OcLifestone;
        if ((t & ItemTypeTinkeringMaterial) != 0) return OcSalvage;
        if ((t & ItemTypeTinkeringTool) != 0) return OcUst;
        if ((t & ItemTypeCraftCookingBase) != 0) return OcBaseCooking;
        if ((t & ItemTypeCraftAlchemyBase) != 0) return OcBaseAlchemy;
        if ((t & ItemTypeCraftFletchingBase) != 0) return OcBaseFletching;
        if ((t & ItemTypeCraftAlchemyIntermediate) != 0) return OcCraftedAlchemy;
        if ((t & ItemTypeCraftFletchingIntermediate) != 0) return OcCraftedFletching;
        if ((t & ItemTypeService) != 0) return OcServices;
        if ((t & ItemTypePortal) != 0) return OcPortal;
        if ((t & ItemTypeWritable) != 0) return OcBook;
        if ((t & ItemTypeContainer) != 0) return OcContainer;
        if ((t & ItemTypeCaster) != 0) return OcWandStaffOrb;
        if ((t & ItemTypeMissileWeapon) != 0) return OcMissileWeapon;
        if ((t & ItemTypeArmor) != 0) return OcArmor;
        if ((t & ItemTypeMeleeWeapon) != 0) return OcMeleeWeapon;
        if ((t & ItemTypeClothing) != 0) return OcClothing;
        if ((t & ItemTypeJewelry) != 0) return OcJewelry;
        if ((t & ItemTypeFood) != 0) return OcFood;
        if ((t & ItemTypeMoney) != 0) return OcMoney;
        if ((t & ItemTypeMisc) != 0) return OcMisc;
        return OcUnknown;
    }

    private static bool IsCorpseName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.EndsWith(" corpse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("'s corpse", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("corpse of ", StringComparison.OrdinalIgnoreCase));
}
