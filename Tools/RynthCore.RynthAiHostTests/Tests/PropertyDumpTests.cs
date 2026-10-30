using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Engine.Compatibility;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Diagnostics;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using EngineFlags = RynthCore.Engine.Compatibility.ObjectPropertyFlags;

namespace RynthCore.RynthAiHostTests.Tests;

// /ub propertydump (2026-10-13): a tester found it "isn't exposing all item properties" next to
// UtilityBelt's. It probed fixed key ranges one key at a time; now it lists every key the engine
// holds (plugin API v84 GetObjectProperties), identifies the object first, chats in chunks and
// writes the whole dump to RynthAi\Logs. On an older engine it probes every named key.
internal static class PropertyDumpTests
{
    private const uint Player = 0x50000A01, Coat = 0x80001234, Pack = 0x80001200;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("propertydump: every key the engine holds, by type, named, ids in hex, spells by name", ListsEverything);
        r.Add("propertydump: layout like UtilityBelt's (header, sections sorted by name, unknown keys as numbers)", Layout);
        r.Add("propertydump: an unidentified item is identified first and dumped when the answer arrives", WaitsForId);
        r.Add("propertydump: no identify answer in time - dumps what the client has, and says so", IdTimeout);
        r.Add("propertydump: waits a moment for the engine's main-thread qualities read", QualitiesPending);
        r.Add("propertydump: long dumps reach chat in chunks; the full dump goes to RynthAi\\Logs", ChunksAndFile);
        r.Add("propertydump: an engine before v84 probes every named key, past the old ranges", OlderEngineFallback);
        r.Add("propertydump: /ub, /vt and /ra all route to it; nothing selected = the character", Routes);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public long Now = 1_000_000;

        public Bot()
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            Set("_playerId", Player);
            Set("_loginComplete", true);
            Set("_initialized", true);   // OnChatBarEnter routes /ub, /vt and /ra only once initialized
            P.PropertyDumpClock = () => Now;
            // /vt propertydump goes through MetaManager.TryHandleVtCommand back to /ra.
            var meta = new RynthCore.Plugin.RynthAi.Meta.MetaManager(new RynthCore.Plugin.RynthAi.LegacyUi.LegacyUiSettings(),
                new RynthCoreHost(FakeHost.LastApi), new PlayerVitalsCache());
            var ra = typeof(RynthAiPlugin).GetMethod("HandleRaCommand", Any)!;
            meta.SetRaCommandHandler(cmd => ra.Invoke(P, new object[] { cmd }));
            Set("_metaManager", meta);
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public void Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public void Tick() => Call("TickPropertyDump", false);
        public void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(P, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }
        public void DrainAll() { for (int i = 0; i < 200; i++) Tick(); }
        public string AllChat => string.Join("\n", FakeHost.Chat);
    }

    private static string DumpFolder()
    {
        string dir = Path.Combine(Program.TempRoot, "propdump-" + Guid.NewGuid().ToString("N")[..8]);
        PropertyDump.FolderOverride = dir;
        return dir;
    }

    private static PropertyBag CoatBag()
    {
        var b = new PropertyBag();
        b.Ints[1] = 2;            // ItemType Armor
        b.Ints[19] = 15000;       // Value
        b.Ints[28] = 210;         // ArmorLevel
        b.Ints[9015] = 3;         // InventoryOrder: no fixed range reached it
        b.Ints[4242] = 7;         // a key with no name
        b.Int64s[4] = 123456789012L;   // ItemTotalXp: int64s had no names
        b.Bools[22] = true;       // Inscribable
        b.Floats[13] = 1.3;       // ArmorModVsSlash: floats had no names
        b.Floats[167] = 2.5;      // CooldownDuration
        b.Strings[1] = "Olthoi Amuli Coat";
        b.Strings[16] = "A heavy coat.";
        b.DataIds[8] = 0x06001F88;      // Icon
        b.DataIds[8044] = 0x1234;       // PCAPPhysicsDIDDataTemplatedFrom: past the old 0-80 range
        b.InstanceIds[2] = Pack;        // Container
        b.InstanceIds[38] = Player;     // AllowedWielder
        return b;
    }

    private static void Setup(Func<uint, byte[]?> records, bool appraised = true)
    {
        FakeHost.Reset();
        FakeHost.Names[Player] = "Tester";
        FakeHost.Names[Coat] = "Olthoi Amuli Coat";
        FakeHost.Names[Pack] = "Pack";
        FakeHost.Wcids[Coat] = 37191;
        FakeHost.WeaponCalls = true;   // RequestId + HasAppraisalData
        FakeHost.ObjectPropertiesCalls = true;
        FakeHost.PropertyRecord = records;
        if (appraised) FakeHost.Appraised.Add(Coat);
        FakeHost.Create(Player);
    }

    private static byte[] CoatRecord(EngineFlags flags = EngineFlags.HasIdData | EngineFlags.QualitiesRead) =>
        ObjectPropertyRecord.Encode(CoatBag(), new uint[] { 2783, 4400, 99999 }, flags, 1_760_000_000L);

    private static void ListsEverything()
    {
        DumpFolder();
        Setup(id => id == Coat ? CoatRecord() : null);
        var bot = new Bot();
        bot.Set("_currentTargetId", Coat);
        bot.Ra("/ra dumpprops");
        bot.DrainAll();
        string chat = bot.AllChat;
        Check.True(chat.Contains("Property Dump for Olthoi Amuli Coat"), "header");
        Check.True(chat.Contains("  InventoryOrder (9015) = 3"), "an int past the old 0-400 range");
        Check.True(chat.Contains("  4242 = 7"), "an unknown key as its number");
        Check.True(chat.Contains("  ItemTotalXp (4) = 123456789012"), "int64 named");
        Check.True(chat.Contains("  ArmorModVsSlash (13) = 1.3"), "float named");
        Check.True(chat.Contains("  Inscribable (22) = True"), "bool as True/False");
        Check.True(chat.Contains("  Icon (8) = 0x06001F88"), "data id in hex");
        Check.True(chat.Contains("  PCAPPhysicsDIDDataTemplatedFrom (8044) = 0x00001234"), "data id past the old 0-80 range");
        Check.True(chat.Contains("  Container (2) = 0x80001200 (Pack)"), "instance id in hex with the object's name");
        Check.True(chat.Contains("  AllowedWielder (38) = 0x50000A01 (Tester)"), "instance id 38");
        Check.True(chat.Contains("Spells (3):"), "spell book");
        Check.True(chat.Contains("(2783)") && chat.Contains("(4400)") && chat.Contains("(99999)"), "every spell by id");
        Check.True(chat.Contains("Total: 15 properties"), "total (" + chat.Split('\n').LastOrDefault(l => l.Contains("Total")) + ")");
        Check.False(chat.Contains("Older RynthCore engine"), "no older-engine note on v84");
        Check.Eq(FakeHost.IdRequests.Count, 0, "already identified: no new ID request");
    }

    private static void Layout()
    {
        var d = new PropertyDumpData { Id = Coat, Name = "Coat", Wcid = 37191, ObjectClass = "Armor", ObjectClassValue = 2, HasIdData = true, Complete = true };
        d.Ints[19] = 5; d.Ints[1] = 2; d.Ints[5000] = 1; d.Ints[4999] = 1;
        d.Strings[1] = "Coat";
        d.InstanceIds[2] = Pack;
        d.Spells.Add(2783);
        List<string> lines = PropertyDump.Format(d, s => "Spell" + s, id => id == Pack ? "Pack" : null);
        Check.Eq(lines[0], "Property Dump for Coat", "first line");
        Check.Eq(lines[1], $"Id = {Coat} (0x{Coat:X8})", "id line");
        Check.True(lines.Contains("Type (wcid) = 37191") && lines.Contains("ObjectClass = Armor (2)"), "wcid, object class");
        Check.True(lines.Contains("Container = 0x80001200 (Pack)") && lines.Contains("HasIdData = True"), "container, id data");
        int strings = lines.IndexOf("String Values:"), ints = lines.IndexOf("Int Values:"), iids = lines.IndexOf("InstanceId Values:"), spells = lines.IndexOf("Spells (1):");
        Check.True(strings > 0 && strings < ints && ints < iids && iids < spells, "sections in UB's order, spells last");
        Check.False(lines.Contains("Bool Values:"), "an empty section is left out");
        Check.Eq(lines[ints + 1], "  ItemType (1) = 2", "ints sorted by name: ItemType");
        Check.Eq(lines[ints + 2], "  Value (19) = 5", "then Value");
        Check.Eq(lines[ints + 3], "  4999 = 1", "unknown keys after the named ones, by number");
        Check.Eq(lines[spells + 1], "  Spell2783 (2783)", "spell line");
        Check.Eq(PropertyDump.FileName("Olthoi Amuli Coat", new DateTime(2026, 10, 13, 9, 5, 7)), "propertydump-Olthoi_Amuli_Coat-20261013-090507.txt", "file name");
        Check.Eq(PropertyDump.FileName("<>:|?*", DateTime.MinValue), "propertydump-object-00010101-000000.txt", "unsafe name");
    }

    private static void WaitsForId()
    {
        DumpFolder();
        bool idArrived = false;
        Setup(id => id != Coat ? null
            : idArrived ? CoatRecord()
            : ObjectPropertyRecord.Encode(new PropertyBag(), null, EngineFlags.HasCreateData, 0), appraised: false);
        var bot = new Bot();
        bot.Set("_currentTargetId", Coat);
        bot.Call("HandleRynthChatSubmit", "/ub propertydump");
        Check.True(FakeHost.IdRequests.Contains(Coat), "asked the server for an ID");
        Check.True(bot.AllChat.Contains("isn't identified yet"), "said so");
        Check.False(bot.AllChat.Contains("Property Dump for"), "no dump before the answer");
        bot.Now += 300; bot.Tick();
        Check.False(bot.AllChat.Contains("Property Dump for"), "still waiting");
        FakeHost.Appraised.Add(Coat); idArrived = true;
        bot.Now += 150; bot.DrainAll();
        Check.True(bot.AllChat.Contains("Property Dump for Olthoi Amuli Coat") && bot.AllChat.Contains("InventoryOrder (9015) = 3"), "the dump after the answer, in full");
        Check.Eq(FakeHost.IdRequests.Count(i => i == Coat), 1, "one ID request");
    }

    private static void IdTimeout()
    {
        DumpFolder();
        var bag = new PropertyBag(); bag.Ints[19] = 100; bag.Strings[1] = "Olthoi Amuli Coat";
        Setup(id => id == Coat ? ObjectPropertyRecord.Encode(bag, null, EngineFlags.HasCreateData, 0) : null, appraised: false);
        var bot = new Bot();
        bot.Ra($"/ra dumpprops 0x{Coat:X8}");
        bot.Now += 1000; bot.Tick();
        Check.False(bot.AllChat.Contains("Property Dump for"), "waiting inside the 4 s");
        bot.Now += 3500; bot.DrainAll();
        Check.True(bot.AllChat.Contains("No identify answer"), "the timeout is said");
        Check.True(bot.AllChat.Contains("  Value (19) = 100"), "what the client has is dumped");
        Check.True(bot.AllChat.Contains("Not identified"), "the not-identified note");
    }

    private static void QualitiesPending()
    {
        DumpFolder();
        int calls = 0;
        Setup(id =>
        {
            if (id != Coat) return null;
            calls++;
            return calls <= 2 ? CoatRecord(EngineFlags.HasIdData | EngineFlags.QualitiesPending) : CoatRecord();
        });
        var bot = new Bot();
        bot.Set("_currentTargetId", Coat);
        bot.Ra("/ra dumpprops");
        Check.False(bot.AllChat.Contains("Property Dump for"), "pending: not yet");
        bot.Now += 50; bot.Tick();
        Check.False(bot.AllChat.Contains("Property Dump for"), "inside the poll interval nothing is asked");
        bot.Now += 100; bot.Tick();   // second query: still pending
        bot.Now += 100; bot.DrainAll();
        Check.True(bot.AllChat.Contains("Property Dump for"), "dumped once the read is in");

        // An engine that never finishes the read: dumped anyway after the wait.
        Setup(id => id == Coat ? CoatRecord(EngineFlags.HasIdData | EngineFlags.QualitiesPending) : null);
        bot = new Bot();
        bot.Set("_currentTargetId", Coat);
        bot.Ra("/ra dumpprops");
        bot.Now += 2000; bot.DrainAll();
        Check.True(bot.AllChat.Contains("Property Dump for"), "never finished: dumped after the wait");
    }

    private static void ChunksAndFile()
    {
        string dir = DumpFolder();
        var big = CoatBag();
        for (uint k = 5000; k < 5200; k++) big.Ints[k] = (int)k;
        Setup(id => id == Coat ? ObjectPropertyRecord.Encode(big, null, EngineFlags.HasIdData, 0) : null);
        var bot = new Bot();
        bot.Set("_currentTargetId", Coat);
        int before = FakeHost.Chat.Count;
        bot.Ra("/ra dumpprops");
        int first = FakeHost.Chat.Count - before;
        Check.True(first <= PropertyDump.ChatLinesPerTick, $"first chunk {first} lines");
        bot.Tick();
        Check.True(FakeHost.Chat.Count - before <= 2 * PropertyDump.ChatLinesPerTick, "one more chunk per tick");
        bot.DrainAll();
        Check.True(bot.AllChat.Contains("  5199 = 5199"), "the last line reaches chat");
        string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir, "propertydump-Olthoi_Amuli_Coat-*.txt") : Array.Empty<string>();
        Check.Eq(files.Length, 1, "one file in RynthAi\\Logs");
        if (files.Length == 1)
        {
            string[] text = File.ReadAllLines(files[0]);
            Check.True(text[0] == "Property Dump for Olthoi Amuli Coat" && text.Contains("  5199 = 5199"), "the full dump, without chat prefixes");
            Check.True(bot.AllChat.Contains("written to " + files[0]), "chat says where");
        }
    }

    private static void OlderEngineFallback()
    {
        DumpFolder();
        FakeHost.Reset();
        FakeHost.Names[Player] = "Tester";
        FakeHost.Names[Coat] = "Olthoi Amuli Coat";
        FakeHost.Ints[(Coat, 19)] = 15000;
        FakeHost.Ints[(Coat, 9015)] = 3;          // named, past the old range: now probed
        FakeHost.Ints[(Coat, 4242)] = 7;          // unnamed, past the range: an older engine can't know it
        FakeHost.Doubles[(Coat, 167)] = 2.5;      // CooldownDuration
        FakeHost.Strings[(Coat, 1)] = "Olthoi Amuli Coat";
        FakeHost.DataIds[(Coat, 8)] = 0x06001F88;
        FakeHost.SpellIds[Coat] = new uint[] { 2783 };
        FakeHost.Create(Player);   // no ObjectPropertiesCalls: GetObjectProperties absent
        var bot = new Bot();
        PropertyDumpData? d = bot.P.CollectPropertyDump(Coat, out bool pending);
        Check.True(d != null && !pending && !d.Complete, "collected without v84");
        Check.True(d!.Ints.TryGetValue(9015, out int io) && io == 3 && d.Ints[19] == 15000, "named keys past the old ranges");
        Check.False(d.Ints.ContainsKey(4242), "an unnamed key beyond the ranges stays unknown (older engine)");
        Check.True(d.Floats.TryGetValue(167, out double cd) && cd == 2.5 && d.DataIds[8] == 0x06001F88, "float and data id probed");
        Check.True(d.Spells.SequenceEqual(new uint[] { 2783 }), "spells from GetObjectSpellIds");
        bot.Set("_currentTargetId", Coat);
        bot.Ra("/ra dumpprops");
        bot.DrainAll();
        Check.True(bot.AllChat.Contains("Older RynthCore engine"), "the older-engine note");
        Check.True(bot.AllChat.Contains("  CooldownDuration (167) = 2.5"), "names on the fallback too");
    }

    private static void Routes()
    {
        DumpFolder();
        var selfBag = new PropertyBag(); selfBag.Ints[25] = 275; selfBag.Strings[1] = "Tester";
        Setup(id => id == Player ? ObjectPropertyRecord.Encode(selfBag, null, EngineFlags.IsPlayer, 0)
                  : id == Coat ? CoatRecord() : null);
        var bot = new Bot();
        bot.Ra("/ra dumpprops");
        bot.DrainAll();
        Check.True(bot.AllChat.Contains("Nothing selected") && bot.AllChat.Contains("  Level (25) = 275"), "nothing selected: the character");
        Check.Eq(FakeHost.IdRequests.Count, 0, "the character isn't identified first");

        FakeHost.Chat.Clear();
        bot.Set("_currentTargetId", Coat);
        bot.Ra("/ra propertydump");
        bot.DrainAll();
        Check.True(bot.AllChat.Contains("Property Dump for Olthoi Amuli Coat"), "/ra propertydump (the hotkey's command)");

        FakeHost.Chat.Clear();
        bot.Call("HandleRynthChatSubmit", "/vt propertydump");
        bot.DrainAll();
        bool vt = bot.AllChat.Contains("Property Dump for Olthoi Amuli Coat");
        FakeHost.Chat.Clear();
        bot.Call("HandleRynthChatSubmit", "/ub propertydump");
        bot.DrainAll();
        bool ub = bot.AllChat.Contains("Property Dump for Olthoi Amuli Coat");
        Check.True(ub, "/ub propertydump");
        Check.True(vt, "/vt propertydump");
    }
}
