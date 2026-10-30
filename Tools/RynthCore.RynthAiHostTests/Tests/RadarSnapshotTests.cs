using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using EngineSnapshot = RynthCore.Engine.UI.Data.RadarSnapshot;
using EngineJson = RynthCore.Engine.UI.Data.RadarJsonContext;
using EngineKind = RynthCore.Engine.UI.Data.RadarKind;

namespace RynthCore.RynthAiHostTests.Tests;

// Radar snapshot format (2026-10-01): RynthAi's RynthPluginGetRadarSnapshot JSON, read by the
// engine's parser (RynthCore\src\RynthCore.Engine\UI\Data\RadarSnapshotJson.cs, linked into
// this project). Markers now carry the object id so the engine's radar click selects exactly
// that object. Both directions must keep working: a new engine reading an older RynthAi (no
// "id": Id 0, the engine falls back to its nearest match) and an older engine reading the new
// RynthAi (the extra "id" must be skipped, not break the parse).
internal static class RadarSnapshotTests
{
    private const uint Player = 0x50000C01;

    public static void Register(Runner r)
    {
        r.Add("radar json: a marker without an id (older RynthAi) parses with id 0", OldPluginNewEngine);
        r.Add("radar json: RynthAi's markers carry their object ids into the engine's parser", NewPluginNewEngine);
        r.Add("radar json: an older engine's parser skips the new id field", NewPluginOldEngine);
        r.Add("radar kinds: plugin and engine number the kinds the same", KindNumbersMatch);
        r.Add("radar kinds: players, fellows, pets, vendors, NPCs, corpses, lifestones, ground items", KindsClassified);
        r.Add("radar kinds: undead (CreatureType 14) are monsters, not NPCs", UndeadIsMonster);
        r.Add("radar kinds: a settled kind is kept, an unreadable bitfield is asked again", KindCaching);
        r.Add("radar kinds: fellowship membership is read live", FellowLive);
        r.Add("radar markers: objects over a landblock border show outdoors, not in a dungeon", AdjacentLandblocks);
    }

    // ── Marker kinds (2026-10-04) ─────────────────────────────────────────────

    private const string Me = "Tester";

    private static void KindNumbersMatch()
    {
        Check.Eq(RynthRadarUi.KindMonster, EngineKind.Monster, "monster");
        Check.Eq(RynthRadarUi.KindNpc, EngineKind.Npc, "npc");
        Check.Eq(RynthRadarUi.KindPortal, EngineKind.Portal, "portal");
        Check.Eq(RynthRadarUi.KindDoor, EngineKind.Door, "door");
        Check.Eq(RynthRadarUi.KindPlayer, EngineKind.Player, "player");
        Check.Eq(RynthRadarUi.KindFellow, EngineKind.Fellow, "fellow");
        Check.Eq(RynthRadarUi.KindPet, EngineKind.Pet, "pet");
        Check.Eq(RynthRadarUi.KindVendor, EngineKind.Vendor, "vendor");
        Check.Eq(RynthRadarUi.KindCorpse, EngineKind.Corpse, "corpse");
        Check.Eq(RynthRadarUi.KindOwnCorpse, EngineKind.OwnCorpse, "own corpse");
        Check.Eq(RynthRadarUi.KindLifestone, EngineKind.Lifestone, "lifestone");
        Check.Eq(RynthRadarUi.KindGroundItem, EngineKind.GroundItem, "ground item");
        Check.Eq(EngineKind.Count, 12, "kind count");
        Check.True(EngineKind.IsCreature(EngineKind.Pet) && !EngineKind.IsCreature(EngineKind.Corpse), "creature kinds");
    }

    /// <summary>Sets up the player (named <see cref="Me"/>) outdoors with GetPlayerId installed.</summary>
    private static (uint Cell, float X, float Y) Setup()
    {
        FakeHost.Reset();
        FakeHost.SetNavPosition(12.5, -40.25);
        FakeHost.Names[Player] = Me;
        return (FakeHost.PlayerPose.Cell, FakeHost.PlayerPose.X, FakeHost.PlayerPose.Y);
    }

    private static void Put(uint id, string name, uint itemType, (uint Cell, float X, float Y) at, float dx, uint? bitfield = null)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (at.Cell, at.X + dx, at.Y + 1f, 0f);
        FakeHost.ItemTypes[id] = itemType;
        if (bitfield is uint bf) FakeHost.Bitfields[id] = bf;
    }

    private static (RynthRadarUi Radar, Dictionary<uint, byte> Kinds) Snapshot(uint[] ids,
        Func<int, bool>? fellowId = null, Func<string, bool>? fellowName = null)
    {
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, ids);
        var radar = new RynthRadarUi(host, new LegacyUiSettings());
        radar.SetWorldObjectCache(cache);
        radar.IsFellowId = fellowId;
        radar.IsFellowName = fellowName;
        return (radar, Kinds(radar));
    }

    private static Dictionary<uint, byte> Kinds(RynthRadarUi radar)
    {
        var kinds = new Dictionary<uint, byte>();
        EngineSnapshot? s = EngineSnapshot.Parse(radar.BuildSnapshotJson(0));
        Check.NotNull(s, "parsed");
        if (s == null) return kinds;
        foreach (var m in s.Markers)
        {
            Check.False(kinds.ContainsKey(m.Id), $"one marker per object (0x{m.Id:X8})");
            kinds[m.Id] = m.Kind;
        }
        return kinds;
    }

    private static void Kind(Dictionary<uint, byte> kinds, uint id, byte want, string what)
        => Check.True(kinds.TryGetValue(id, out byte k) && k == want,
            $"{what}: kind {want} (got {(kinds.TryGetValue(id, out byte g) ? g.ToString() : "no marker")})");

    private static void KindsClassified()
    {
        var at = Setup();
        uint monster = 0x80001001, player = 0x50000D01, fellow = 0x50000D02, pet = 0x80001002,
             otherPet = 0x80001003, npc = 0x80001004, vendor = 0x80001005,
             myKill = 0x80003001, theirKill = 0x80003002, fellowKill = 0x80003003, myBody = 0x80003004,
             unknownCorpse = 0x80003005, lifestone = 0x7A9B4002, gem = 0x80002001, portal = 0x7A9B4001;
        Put(monster, "Drudge Skulker", 0x10, at, 3f, 0x10);
        Put(player, "Someone", 0x10, at, 4f, 0x8);
        Put(fellow, "Fellowbob", 0x10, at, 5f, 0x8);
        Put(pet, Me + "'s Wolf", 0x10, at, 6f, 0x10);
        Put(otherPet, "Someone's Wolf", 0x10, at, 7f, 0x10);
        Put(npc, "Town Crier", 0x10, at, 8f, 0x4);
        Put(vendor, "Shopkeeper Ulf", 0x10, at, 9f, 0x204);
        Put(myKill, "Corpse of Drudge Skulker", 0x200, at, -2f);
        FakeHost.Strings[(myKill, 16u)] = "Killed by " + Me + ".";
        Put(theirKill, "Corpse of Mosswart", 0x200, at, -3f);
        FakeHost.Strings[(theirKill, 16u)] = "Killed by Someone.";
        Put(fellowKill, "Corpse of Rat", 0x200, at, -4f);
        FakeHost.Strings[(fellowKill, 16u)] = "Killed by Fellowbob.";
        Put(myBody, "Corpse of " + Me, 0x200, at, -5f);
        Put(unknownCorpse, "Corpse of Banderling", 0x200, at, -6f); // not identified: no LongDesc
        Put(lifestone, "Life Stone", 0x10000000, at, -7f);
        Put(gem, "Black Opal", 0x800, at, -8f);
        Put(portal, "Portal to Holtburg", 0x10000, at, -9f);

        var (_, kinds) = Snapshot(
            new[] { monster, player, fellow, pet, otherPet, npc, vendor, myKill, theirKill, fellowKill, myBody, unknownCorpse, lifestone, gem, portal },
            fellowId: id => (uint)id == fellow,
            fellowName: n => n == "Fellowbob");

        Kind(kinds, monster, EngineKind.Monster, "monster");
        Kind(kinds, player, EngineKind.Player, "another player (was a red monster dot)");
        Kind(kinds, fellow, EngineKind.Fellow, "fellow");
        Kind(kinds, pet, EngineKind.Pet, "your pet");
        Kind(kinds, otherPet, EngineKind.Monster, "someone else's pet: not yours (attackable here, so a monster)");
        Kind(kinds, npc, EngineKind.Npc, "NPC (not attackable)");
        Kind(kinds, vendor, EngineKind.Vendor, "vendor");
        Kind(kinds, myKill, EngineKind.OwnCorpse, "your kill");
        Kind(kinds, theirKill, EngineKind.Corpse, "someone else's kill");
        Kind(kinds, fellowKill, EngineKind.OwnCorpse, "a fellow's kill");
        Kind(kinds, myBody, EngineKind.OwnCorpse, "your own corpse");
        Kind(kinds, unknownCorpse, EngineKind.Corpse, "a corpse not identified yet");
        Kind(kinds, lifestone, EngineKind.Lifestone, "lifestone");
        Kind(kinds, gem, EngineKind.GroundItem, "item on the ground");
        Kind(kinds, portal, EngineKind.Portal, "portal (unchanged)");
        Check.False(kinds.ContainsKey(Player), "you are not a marker");
    }

    private static void UndeadIsMonster()
    {
        var at = Setup();
        uint zombie = 0x80001101;
        Put(zombie, "Zombie", 0x10, at, 3f, 0x10);
        FakeHost.Ints[(zombie, 2u)] = 14; // CreatureType Undead
        var (_, kinds) = Snapshot(new[] { zombie });
        Kind(kinds, zombie, EngineKind.Monster, "undead");

        // No bitfield call at all (older engine): the attackable check decides.
        at = Setup();
        Put(zombie, "Zombie", 0x10, at, 3f);
        FakeHost.Ints[(zombie, 2u)] = 14;
        (_, kinds) = Snapshot(new[] { zombie });
        Kind(kinds, zombie, EngineKind.Monster, "undead without a bitfield");
    }

    private static void KindCaching()
    {
        var at = Setup();
        uint drudge = 0x80001201, unread = 0x80001202;
        Put(drudge, "Drudge Skulker", 0x10, at, 3f, 0x10);
        Put(unread, "Lugian", 0x10, at, 5f, 0); // bitfield not readable yet
        FakeHost.NotAttackable.Add(unread);
        var (radar, kinds) = Snapshot(new[] { drudge, unread });
        Kind(kinds, drudge, EngineKind.Monster, "first poll: monster");
        Kind(kinds, unread, EngineKind.Npc, "first poll: no bitfield, not attackable: NPC for now");

        FakeHost.Bitfields[drudge] = 0x8; // a settled answer isn't re-read
        FakeHost.Bitfields[unread] = 0x8; // an unsettled one is
        kinds = Kinds(radar);
        Kind(kinds, drudge, EngineKind.Monster, "second poll: kept");
        Kind(kinds, unread, EngineKind.Player, "second poll: bitfield now readable");
    }

    private static void FellowLive()
    {
        var at = Setup();
        uint bob = 0x50000D02;
        Put(bob, "Fellowbob", 0x10, at, 4f, 0x8);
        bool inFellow = false;
        var (radar, kinds) = Snapshot(new[] { bob }, fellowId: id => inFellow && (uint)id == bob);
        Kind(kinds, bob, EngineKind.Player, "not in your fellowship yet");
        inFellow = true;
        Kind(Kinds(radar), bob, EngineKind.Fellow, "joined");
        inFellow = false;
        Kind(Kinds(radar), bob, EngineKind.Player, "left");
    }

    // A snapshot as RynthAi wrote it before markers had ids.
    private const string OldPluginJson =
        "{\"mapVersion\":43690,\"geometryIncluded\":false,\"isIndoor\":false," +
        "\"player\":{\"cellId\":2863267857,\"landblock\":43690,\"x\":10,\"y\":20,\"z\":5,\"worldX\":32650,\"worldY\":32660,\"heading\":90}," +
        "\"ns\":12.5,\"ew\":-40.25,\"layerZs\":[],\"currentLayerZ\":0,\"walls\":[],\"fills\":[],\"visited\":[]," +
        "\"markers\":[{\"kind\":0,\"x\":32651,\"y\":32661,\"z\":5,\"label\":\"Drudge Skulker\"}," +
        "{\"kind\":2,\"x\":32700,\"y\":32700,\"z\":6,\"label\":\"Holtburg\"}]}";

    private static void OldPluginNewEngine()
    {
        EngineSnapshot? s = EngineSnapshot.Parse(OldPluginJson);
        Check.NotNull(s, "parsed");
        if (s == null) return;
        Check.Eq(s.Markers.Count, 2, "markers");
        Check.Eq(s.Markers[0].Id, 0u, "no id field: Id 0 (the click falls back to the nearest match)");
        Check.Eq(s.Markers[1].Id, 0u, "portal: Id 0");
        Check.Eq(s.Markers[0].Label, "Drudge Skulker", "label");
        Check.Eq(s.Markers[1].Kind, (byte)2, "portal kind");
        Check.Eq(s.Player.Landblock, 43690u, "player landblock");
        Check.Near(s.Ew, -40.25, 1e-9, "ew");
    }

    private static void NewPluginNewEngine()
    {
        string json = BuildPluginSnapshot(out uint a, out uint b, out uint portal);
        Check.True(json.Contains("\"id\":"), "the plugin writes an id per marker");

        EngineSnapshot? s = EngineSnapshot.Parse(json);
        Check.NotNull(s, "parsed");
        if (s == null) return;
        var byId = new Dictionary<uint, (byte Kind, string? Label, float X)>();
        foreach (var m in s.Markers) byId[m.Id] = (m.Kind, m.Label, m.X);
        Check.Eq(s.Markers.Count, 3, "markers: two creatures and a portal");
        Check.False(byId.ContainsKey(0), "no marker without an id");
        Check.True(byId.ContainsKey(a) && byId.ContainsKey(b), "both same-name monsters keep their own id (ids above 0x7FFFFFFF intact)");
        if (byId.TryGetValue(a, out var ma) && byId.TryGetValue(b, out var mb))
        {
            Check.Eq(ma.Label, "Drudge Skulker", "first monster's name");
            Check.Eq(mb.Label, "Drudge Skulker", "second monster's name");
            Check.Eq(ma.Kind, (byte)0, "first monster kind");
            Check.True(Math.Abs(ma.X - mb.X) > 0.5f, "each id sits at its own monster's spot");
        }
        Check.True(byId.TryGetValue(portal, out var mp) && mp.Kind == 2, "portal keeps its id and kind");
    }

    private static void NewPluginOldEngine()
    {
        // The engine's options must keep skipping unknown fields, or older-engine safety is luck.
        Check.Eq(EngineJson.Default.Options.UnmappedMemberHandling, JsonUnmappedMemberHandling.Skip,
            "engine parser skips unknown fields");

        string json = BuildPluginSnapshot(out _, out _, out _);
        OldEngineSnapshot? s = null;
        try { s = JsonSerializer.Deserialize(json, OldEngineJsonContext.Default.OldEngineSnapshot); }
        catch (Exception ex) { Check.True(false, "the older parser threw: " + ex.Message); }
        Check.NotNull(s, "parsed by the older engine's shape");
        if (s == null) return;
        Check.Eq(s.Markers.Count, 3, "older engine still sees every marker");
        Check.Eq(s.Markers[0].Label, "Drudge Skulker", "and its label");
    }

    // ── Neighbouring landblocks (2026-10-15) ──────────────────────────────────

    private static Dictionary<uint, (float X, float Y)> Positions(RynthRadarUi radar)
    {
        var at = new Dictionary<uint, (float X, float Y)>();
        EngineSnapshot? s = EngineSnapshot.Parse(radar.BuildSnapshotJson(0));
        Check.NotNull(s, "parsed");
        if (s == null) return at;
        foreach (var m in s.Markers) at[m.Id] = (m.X, m.Y);
        return at;
    }

    /// <summary>
    /// Outdoors, objects over a landblock border show at once, placed by their own landblock's origin
    /// (they used to appear only after you crossed). In a dungeon only your own landblock counts.
    /// </summary>
    private static void AdjacentLandblocks()
    {
        var at = Setup();
        uint lbx = (at.Cell >> 24) & 0xFF, lby = (at.Cell >> 16) & 0xFF;
        uint same = 0x80001103, east = 0x80001101, north = 0x80001102;
        Put(same, "Drudge Skulker", 0x10, at, 3f, 0x10);
        Put(east, "Mosswart", 0x10, at, 3f, 0x10);
        Put(north, "Olthoi", 0x10, at, 3f, 0x10);
        FakeHost.Positions[east] = (at.Cell + 0x01000000u, 5f, 40f, 0f);      // the landblock to the east
        FakeHost.Positions[north] = (at.Cell + 0x00010000u, 60f, 7f, 0f);     // and to the north

        var host = FakeHost.Create(Player);
        var radar = new RynthRadarUi(host, new LegacyUiSettings());
        radar.SetWorldObjectCache(FakeHost.MakeCache(host, Player, new[] { same, east, north }));
        var pos = Positions(radar);
        Check.True(pos.ContainsKey(same), "an object in your own landblock shows");
        Check.True(pos.TryGetValue(east, out var pe), "an object in the landblock to the east shows");
        Check.True(pos.TryGetValue(north, out var pn), "an object in the landblock to the north shows");
        Check.Near(pe.X, 5f + (lbx + 1) * 192f, 1e-3, "east object: x is local + the east landblock's origin");
        Check.Near(pe.Y, 40f + lby * 192f, 1e-3, "east object: y is local + this landblock's origin");
        Check.Near(pn.X, 60f + lbx * 192f, 1e-3, "north object: x");
        Check.Near(pn.Y, 7f + (lby + 1) * 192f, 1e-3, "north object: y is local + the north landblock's origin");

        // In a dungeon (an interior cell of your landblock) the neighbours' objects are other places: left out.
        FakeHost.PlayerPose = ((at.Cell & 0xFFFF0000u) | 0x0105u, at.X, at.Y, 0f);
        FakeHost.Positions[same] = ((at.Cell & 0xFFFF0000u) | 0x0105u, 4f, 6f, 0f);
        var indoor = Positions(radar);
        Check.True(indoor.ContainsKey(same), "in a dungeon, an object in your landblock shows");
        Check.False(indoor.ContainsKey(east) || indoor.ContainsKey(north), "in a dungeon, other landblocks' objects stay out");
    }

    /// <summary>
    /// RynthAi's real BuildSnapshotJson on the fake host: the player outdoors, two monsters of
    /// the same name a couple of metres apart, and a portal, all in the player's landblock.
    /// </summary>
    private static string BuildPluginSnapshot(out uint a, out uint b, out uint portal)
    {
        FakeHost.Reset();
        FakeHost.SetNavPosition(12.5, -40.25);
        uint cell = FakeHost.PlayerPose.Cell;
        float px = FakeHost.PlayerPose.X, py = FakeHost.PlayerPose.Y;
        a = 0x80001234; b = 0x80001235; portal = 0x7A9B4001;
        FakeHost.Names[a] = "Drudge Skulker";
        FakeHost.Names[b] = "Drudge Skulker";
        FakeHost.Names[portal] = "Portal to Holtburg";
        FakeHost.Positions[a] = (cell, px + 3f, py, 0f);
        FakeHost.Positions[b] = (cell, px + 5f, py + 1f, 0f);
        FakeHost.Positions[portal] = (cell, px - 8f, py + 4f, 0f);
        FakeHost.ItemTypes[a] = 0x10;
        FakeHost.ItemTypes[b] = 0x10;
        FakeHost.ItemTypes[portal] = 0x10000;

        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { a, b, portal });
        var radar = new RynthRadarUi(host, new LegacyUiSettings());
        radar.SetWorldObjectCache(cache);
        return radar.BuildSnapshotJson(0);
    }
}

// The engine's radar shape before markers had ids (RadarData.cs up to 2026-09-30), parsed with
// the same default source-generated options it used.
internal sealed class OldEngineSnapshot
{
    [JsonPropertyName("mapVersion")] public uint MapVersion { get; set; }
    [JsonPropertyName("markers")]    public List<OldEngineMarker> Markers { get; set; } = new();
}

internal sealed class OldEngineMarker
{
    [JsonPropertyName("kind")]  public byte    Kind  { get; set; }
    [JsonPropertyName("x")]     public float   X     { get; set; }
    [JsonPropertyName("y")]     public float   Y     { get; set; }
    [JsonPropertyName("z")]     public float   Z     { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
}

[JsonSerializable(typeof(OldEngineSnapshot))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class OldEngineJsonContext : JsonSerializerContext { }
