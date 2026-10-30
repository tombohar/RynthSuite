using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthInventory;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Scanning;
using RynthCore.Plugin.RynthInventory.Ui;
using RynthCore.Plugin.RynthInventory.Views;

// The engine parser's only outside dependency: the registry's limits (the engine's values).
namespace RynthCore.Engine.UI.ScriptWindows
{
    internal static class ScriptWindowRegistry
    {
        public const int MaxOpsPerWindow = 1500;
        public const int MaxBytesPerWindow = 64 * 1024;
        public const int MaxWindowsPerOwner = 32;
        public const int MaxBytesPerSubmit = 256 * 1024;
    }
}

namespace RynthInventory.Tests
{
    internal static partial class Program
    {
        private static int _pass, _fail;
        private static string _temp = "";

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Write")]
        private static extern int Write(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_submit")]
        private static extern ref byte[] SubmitBuffer(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Apply")]
        private static extern void Apply(UiHost h, ReadOnlySpan<byte> data);

        private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        private static int Main()
        {
            _temp = Path.Combine(Path.GetTempPath(), "RynthInventory.Tests." + Environment.ProcessId);
            Directory.CreateDirectory(_temp);
            InventorySettings.PathOverride = Path.Combine(_temp, "settings.json");
            try
            {
                Run("json round trip", JsonRoundTrip);
                Run("json rejects and tolerates", JsonRejects);
                Run("path segments", Segments);
                Run("store save/load/list/forget", StoreFiles);
                Run("store with concurrent writers and readers", StoreConcurrent);
                Run("merge rules", Merge);
                Run("search, filters, sort, totals", Search);
                Run("categories and names", Names);
                Run("scheduler: login scans", SchedulerLogin);
                Run("scheduler: loot spree is debounced", SchedulerSpree);
                Run("scheduler: unchanged content is not rewritten", SchedulerUnchanged);
                Run("window display lists parse in the engine", Pages);
                Run("window events", Events);
                Run("banking: no Banking tab (it is RynthILT's own, 2026-10-21)", NoBankingTab);
                Run("storage from other plugins (StorageFeed)", StorageFeedTests);
                Run("replies: every /ginv word answers", Replies);
            }
            finally
            {
                try { Directory.Delete(_temp, true); } catch { }
            }
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            if (_pass == 0) { Console.WriteLine("ABORT: zero assertions ran."); return 1; }
            return _fail == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            int before = _fail;
            try { test(); }
            catch (Exception ex) { _fail++; Console.WriteLine($"FAIL: {name} threw {ex}"); }
            Console.WriteLine($"  {(before == _fail ? "ok  " : "FAIL")} {name}");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Console.WriteLine("FAIL: " + what); }
        }

        private static void Eq<T>(T actual, T expected, string what) =>
            Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

        // ── Sample data ─────────────────────────────────────────────────────

        private static ItemRecord Item(uint id, string name, string category = ItemCategory.Misc, int stack = 1,
            string place = ItemPlace.Pack, string[]? path = null, uint slot = 0) => new()
        {
            Id = id,
            Name = name,
            Category = category,
            Stack = stack,
            Place = place,
            Path = path ?? (place == ItemPlace.Pack ? new[] { "Main pack" } : Array.Empty<string>()),
            Slot = slot,
            Icon = 0x06001000 + id % 100,
            Value = (int)(id * 10),
            Burden = 5,
        };

        private static CharacterSnapshot Snap(string server, string account, string name, DateTime scanned, params ItemRecord[] items) => new()
        {
            Server = server,
            Account = account,
            Character = name,
            CharacterId = (uint)name.GetHashCode() | 0x50000000u,
            ScannedUtc = scanned,
            PluginVersion = "test",
            Items = items.ToList(),
        };

        private static CharacterSnapshot RichSnapshot()
        {
            var s = Snap("Aelrynth", "tom", "Buffy \"the\" Mage", Now,
                Item(1, "Pyreal", ItemCategory.Money, 12345),
                Item(2, "Gold Ring", ItemCategory.Jewelry, place: ItemPlace.Equipped, slot: 0x40000),
                Item(3, "Sack", ItemCategory.Container),
                Item(4, "Iron Salvage (w7)", ItemCategory.Salvage, path: new[] { "Main pack", "Sack" }));
            ItemRecord ring = s.Items[1];
            ring.Wcid = 297; ring.Underlay = 0x060011CB; ring.Overlay = 0x06006C34; ring.Material = 60; ring.Workmanship = 8;
            ring.ArmorLevel = 0; ring.SetId = 14; ring.Spells = new uint[] { 2, 5 }; ring.SpellNames = new[] { "Strength Self I", "Heal Other I" };
            ring.Identified = true; ring.MaxStack = 1; ring.ItemType = 8;
            s.Items[3].Uses = 3; s.Items[3].MaxUses = 10;
            s.Items[0].Name = "Pyreal\u2028line\nnext \\ \u00e9";   // escaping
            s.Storage.Add(new StorageRecord
            {
                Id = 0x7A000001, Name = "Storage", Label = "Storage near 42.1N, 33.6E", Cell = 0xA9B40031, SeenUtc = Now.AddDays(-2),
                Items = { Item(10, "Mana Stone", ItemCategory.ManaStone, place: ItemPlace.Storage, path: Array.Empty<string>()),
                          Item(11, "Peas", ItemCategory.Food, 30, ItemPlace.Storage, new[] { "Backpack" }) },
            });
            return s;
        }

        // ── Format ──────────────────────────────────────────────────────────

        private static void JsonRoundTrip()
        {
            CharacterSnapshot a = RichSnapshot();
            string json = InventoryJson.Write(a);
            CharacterSnapshot? b = InventoryJson.Read(json, out string? error);
            Check(b != null, "round trip parses: " + error);
            if (b == null) return;
            Eq(b.ContentHash(), a.ContentHash(), "content hash survives the round trip");
            Eq(b.Character, a.Character, "name with quotes");
            Eq(b.Items[0].Name, a.Items[0].Name, "escaped name");
            Eq(b.ScannedUtc, a.ScannedUtc, "scan time");
            Eq(b.Storage.Count, 1, "storage kept");
            Eq(b.Storage[0].SeenUtc, Now.AddDays(-2), "storage seen time");
            Eq(b.Storage[0].Items[1].PathText, "Backpack", "storage item path");
            Eq(string.Join(",", b.Items[1].SpellNames), "Strength Self I,Heal Other I", "spell names");
            Eq(b.Items[3].PathText, "Main pack > Sack", "side pack path");
            Check(!json.Contains('\u2028'), "U+2028 is escaped (it would break some readers)");
            Check(json.Split('\n').Length < 30, "one item per line");
            Check(a.ContentHash() != Snap("Aelrynth", "tom", "Buffy \"the\" Mage", Now).ContentHash(), "hash sees the items");
            CharacterSnapshot c = RichSnapshot();
            c.ScannedUtc = Now.AddHours(5);
            Eq(c.ContentHash(), a.ContentHash(), "hash ignores the scan time");
            c.Items[0].Stack++;
            Check(c.ContentHash() != a.ContentHash(), "hash sees a stack change");
        }

        private static void JsonRejects()
        {
            Check(InventoryJson.Read("{\"format\":\"something\"}", out _) == null, "a foreign file is refused");
            Check(InventoryJson.Read("{\"format\":\"rynthinventory\",\"server\":\"A\"", out string? e) == null && e!.StartsWith("broken"), "a torn file is refused");
            Check(InventoryJson.Read("{\"format\":\"rynthinventory\",\"server\":\"A\"}", out _) == null, "no character: refused");
            var s = InventoryJson.Read("{\"format\":\"rynthinventory\",\"server\":\"A\",\"character\":\"B\",\"future\":{\"x\":1}," +
                "\"items\":[{\"id\":-5,\"name\":\"Thing\",\"type\":64,\"newField\":true},{\"id\":3},{\"name\":\"\"}]}", out _);
            Check(s != null, "unknown fields are ignored");
            Eq(s?.Items.Count ?? -1, 1, "items without a name are skipped");
            Eq(s?.Items[0].Id ?? 0, unchecked((uint)-5), "a signed id is read as unsigned");
            Eq(s?.Items[0].Category ?? "", ItemCategory.Money, "a missing category comes from the type");
            Eq(s?.Items[0].Stack ?? 0, 1, "a missing stack is 1");
        }

        private static void Segments()
        {
            Eq(InventoryStore.Segment("Aelrynth"), "Aelrynth", "plain");
            Eq(InventoryStore.Segment(""), "unknown", "empty");
            Eq(InventoryStore.Segment("  "), "unknown", "blank");
            Eq(InventoryStore.Segment("a/b\\c:d*e?"), "a_b_c_d_e_", "bad characters");
            Eq(InventoryStore.Segment(".."), "unknown", "dot dot");
            Eq(InventoryStore.Segment("CON"), "_CON", "device name");
            Eq(InventoryStore.Segment("com1.txt"), "_com1.txt", "device name with extension");
            Eq(InventoryStore.Segment("name."), "name", "trailing dot");
            Eq(InventoryStore.Segment("_forgotten"), "-_forgotten", "can't collide with the forgotten folder");
            Check(InventoryStore.Segment(new string('x', 200)).Length == 64, "long names are cut");
        }

        private static void StoreFiles()
        {
            var store = new InventoryStore(Path.Combine(_temp, "files"));
            CharacterSnapshot a = RichSnapshot();
            string path = store.Save(a);
            Check(File.Exists(path), "saved");
            Check(path.EndsWith(Path.Combine("Aelrynth", "tom", "Buffy _the_ Mage.json")), "server\\account\\character.json: " + path);
            Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "no temp file left");
            CharacterSnapshot? b = InventoryStore.Load(path, out string? err);
            Check(b != null && b.ContentHash() == a.ContentHash(), "loads back: " + err);
            Eq(b?.FilePath ?? "", path, "file path noted");

            store.Save(Snap("Conquest", "unknown", "Alt", Now));
            List<InventoryFileStamp> files = store.ListFiles();
            Eq(files.Count, 2, "two files listed");
            File.WriteAllText(Path.Combine(store.Root, "Conquest", "unknown", "junk.json"), "not json");
            store.LoadAll(out int broken);
            Eq(broken, 1, "a broken file is counted, not fatal");

            Check(store.Forget(path, out _), "forget moves the file");
            Check(!File.Exists(path), "gone from its place");
            Check(File.Exists(Path.Combine(store.Root, InventoryStore.ForgottenFolder, "Aelrynth", "tom", "Buffy _the_ Mage.json")), "kept under _forgotten");
            Eq(store.ListFiles().Count(f => f.Path.Contains(InventoryStore.ForgottenFolder)), 0, "forgotten files are not listed");
            Check(!store.Forget(Path.Combine(_temp, "elsewhere.json"), out string? why) && why == "not an inventory file", "never moves files outside the folder");
            Check(InventoryStore.Load(Path.Combine(store.Root, "nope.json"), out string? missing) == null && missing == "missing", "missing file");
        }

        private static void StoreConcurrent()
        {
            // Several clients writing their own files (and, to be hard on it, two writing the same
            // one) while readers keep loading: a reader must always get a whole file.
            var store = new InventoryStore(Path.Combine(_temp, "concurrent"));
            var errors = new List<string>();
            int reads = 0, writes = 0;
            using var stop = new CancellationTokenSource();
            var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
            {
                var rnd = new Random(w);
                // At least 60 saves each, and on until the readers have had a real go at it.
                for (int n = 0; n < 60 || (Volatile.Read(ref reads) < 300 && n < 3000); n++)
                {
                    string name = w < 3 ? "Char" + w : "Char0";   // writer 3 fights writer 0
                    var items = Enumerable.Range(0, 200 + rnd.Next(200)).Select(i => Item((uint)i, "Item " + i + " of " + n)).ToArray();
                    try { store.Save(Snap("Srv", "acct", name, Now.AddSeconds(n), items)); Interlocked.Increment(ref writes); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { lock (errors) errors.Add("write: " + ex.Message); }
                }
            })).ToArray();
            var readers = Enumerable.Range(0, 3).Select(r => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (InventoryFileStamp f in store.ListFiles())
                    {
                        CharacterSnapshot? s = InventoryStore.Load(f.Path, out string? e);
                        Interlocked.Increment(ref reads);
                        if (s == null && e != "missing") lock (errors) errors.Add("read: " + e);
                    }
                }
            })).ToArray();
            Task.WaitAll(writers);
            stop.Cancel();
            Task.WaitAll(readers);
            int writeErrors = errors.Count(x => x.StartsWith("write"));
            Check(errors.Count(x => x.StartsWith("read")) == 0, "readers never saw a torn file: " + string.Join("; ", errors.Take(3)));
            // Three readers re-reading every file non-stop is far harsher than other clients
            // checking every 5 s; a write that gives up is simply retried at the next save.
            Check(writeErrors * 20 <= writes + writeErrors, $"writers rarely give up ({writeErrors} of {writes + writeErrors}): " + string.Join("; ", errors.Take(3)));
            Check(reads >= 300, $"readers ran ({reads} reads, {writes} writes)");
            Eq(store.LoadAll(out int broken).Count, 3, "three characters at the end");
            Eq(broken, 0, "nothing broken at the end");
            Eq(Directory.GetFiles(store.Root, "*.tmp", SearchOption.AllDirectories).Length, 0, "no temp files left");
        }

        // ── Merge and search ────────────────────────────────────────────────

        private static InventoryIndex SampleIndex(out CharacterSnapshot live)
        {
            var files = new List<CharacterSnapshot>
            {
                Snap("Aelrynth", "tom", "Buffy", Now.AddDays(-1), Item(1, "Pyreal", ItemCategory.Money, 1000), Item(2, "Iron Salvage", ItemCategory.Salvage)),
                // An older copy of Buffy under "unknown": loses to the newer one.
                Snap("Aelrynth", "unknown", "Buffy", Now.AddDays(-5), Item(99, "Old Junk")),
                Snap("Aelrynth", "tom", "Willow", Now.AddDays(-10), Item(3, "Pyreal", ItemCategory.Money, 500),
                    Item(4, "Gold Ring", ItemCategory.Jewelry, place: ItemPlace.Equipped, slot: 0x40000)),
                Snap("Conquest", "tom2", "Spike", Now.AddHours(-2), Item(5, "Pyreal", ItemCategory.Money, 10000), Item(6, "Mana Stone", ItemCategory.ManaStone)),
                // Same name on another server: a different character.
                Snap("Conquest", "tom2", "Buffy", Now.AddHours(-3), Item(7, "Pyreal", ItemCategory.Money, 845)),
            };
            files[0].Items[1].SpellNames = new[] { "Legendary Strength" };
            files[0].Items[1].Spells = new uint[] { 1 };
            // Storage seen by two characters: Willow's copy is newer and wins.
            files[0].Storage.Add(new StorageRecord { Id = 77, Name = "Storage", Label = "Storage near 1N, 1E", SeenUtc = Now.AddDays(-3), Items = { Item(50, "Pyreal", ItemCategory.Money, 1, ItemPlace.Storage, Array.Empty<string>()) } });
            files[2].Storage.Add(new StorageRecord { Id = 77, Name = "Storage", Label = "Storage near 1N, 1E", SeenUtc = Now.AddDays(-1), Items = { Item(51, "Pyreal", ItemCategory.Money, 2, ItemPlace.Storage, Array.Empty<string>()) } });
            // Buffy's old unknown-account file saw a vault the newer file doesn't have: kept.
            files[1].Storage.Add(new StorageRecord { Id = 88, Name = "Vault", Label = "Vault", SeenUtc = Now.AddDays(-5), Items = { Item(60, "Diamond Scarab", ItemCategory.Misc) } });

            live = Snap("Aelrynth", "tom", "Buffy", Now, Item(1, "Pyreal", ItemCategory.Money, 1234), Item(2, "Iron Salvage", ItemCategory.Salvage));
            live.Items[1].SpellNames = new[] { "Legendary Strength" };
            return InventoryIndex.Build(files, live, "Aelrynth", "Buffy");
        }

        private static void Merge()
        {
            InventoryIndex index = SampleIndex(out CharacterSnapshot live);
            Eq(index.Characters.Count, 4, "Buffy (Aelrynth) once, Willow, Spike, Buffy (Conquest)");
            CharacterInfo buffy = index.Characters.First(c => c.Name == "Buffy" && c.Server == "Aelrynth");
            Check(ReferenceEquals(buffy.Snapshot, live), "the live snapshot wins");
            Check(buffy.IsCurrentCharacter && buffy.IsCurrentServer, "marked current");
            Eq(buffy.ShadowedFiles.Count, 0, "files without a path are not listed as shadowed");
            Check(!index.Rows.Any(r => r.Item.Name == "Old Junk"), "the older copy's items are gone");
            Check(index.Rows.Any(r => r.Item.Name == "Diamond Scarab" && r.Owner == buffy), "the older copy's storage is kept");
            var storageRows = index.Rows.Where(r => r.Storage?.Id == 77).ToList();
            Eq(storageRows.Count, 1, "a container seen by two characters appears once");
            Eq(storageRows.FirstOrDefault()?.Owner.Name ?? "", "Willow", "the most recent sighting wins");
            Eq(storageRows.FirstOrDefault()?.Where ?? "", "Storage near 1N, 1E", "storage row says where");
            Eq(string.Join(",", index.Servers), "Aelrynth,Conquest", "current server first");
            Check(index.Characters[0].IsCurrentServer && !index.Characters[^1].IsCurrentServer, "current server's characters first");
            InventoryRow ring = index.Rows.First(r => r.Item.Name == "Gold Ring");
            Eq(ring.Where, "Equipped: Left ring", "equipped slot");

            // Without a live snapshot the newest file wins.
            var files = new List<CharacterSnapshot>
            {
                Snap("A", "x", "Z", Now.AddDays(-2), Item(1, "Old")) ,
                Snap("a ", "y", "z", Now.AddDays(-1), Item(2, "New")),
            };
            files[0].FilePath = "old.json";
            files[1].FilePath = "new.json";
            InventoryIndex i2 = InventoryIndex.Build(files, null, "", "");
            Eq(i2.Characters.Count, 1, "server and name match ignoring case and spaces");
            Eq(i2.Rows.Single().Item.Name, "New", "newest file wins");
            Eq(string.Join(",", i2.Characters[0].ShadowedFiles), "old.json", "the losing file is noted (so Forget moves both)");
        }

        private static void Search()
        {
            InventoryIndex index = SampleIndex(out _);
            InventoryResult r = index.Query(new InventoryQuery { Text = "pyreal" });
            // Buffy 1234 + Willow 500 + storage 2 + Spike 10000 + Buffy(Conquest) 845
            Eq(r.Rows.Count, 5, "pyreal rows");
            Eq(r.Totals.Count, 1, "one name");
            Eq(r.Totals[0].Count, 12581L, "total pyreals");
            Eq(InventoryIndex.TotalText(r.Totals[0]), "Pyreal: 12,581 across 4 characters on 2 servers", "total text");
            Check(r.Rows.Take(3).All(x => x.Owner.IsCurrentServer) && r.Rows.Skip(3).All(x => !x.Owner.IsCurrentServer), "current server first");

            InventoryResult only = index.Query(new InventoryQuery { Text = "pyreal", Server = "Aelrynth" });
            Eq(InventoryIndex.TotalText(only.Totals[0]), "Pyreal: 1,736 across 2 characters", "one server's total");
            Eq(index.Query(new InventoryQuery { Text = "pyreal", IncludeStorage = false }).Rows.Count, 4, "storage can be left out");
            Eq(index.Query(new InventoryQuery { Text = "ring", IncludeEquipped = false }).Rows.Count, 0, "equipped can be left out");
            Eq(index.Query(new InventoryQuery { Category = ItemCategory.ManaStone }).Rows.Count, 1, "category filter");
            string spike = index.Characters.First(c => c.Name == "Spike").Key;
            Eq(index.Query(new InventoryQuery { CharacterKey = spike }).Rows.Count, 2, "character filter");
            Eq(index.Query(new InventoryQuery { Text = "strength" }).Rows.Count, 0, "names only by default");
            Eq(index.Query(new InventoryQuery { Text = "strength", SearchSpells = true }).Rows.Count, 1, "spells when asked");
            Eq(index.Query(new InventoryQuery { Text = "iron salv" }).Rows.Count, 1, "all terms must match");
            Eq(index.Query(new InventoryQuery { Text = "\"iron salvage\"" }).Rows.Count, 1, "quoted phrase");
            Eq(index.Query(new InventoryQuery { Text = "\"salvage iron\"" }).Rows.Count, 0, "quoted phrase is one term");
            Eq(index.Query(new InventoryQuery { Text = "PYREAL" }).Rows.Count, 5, "case doesn't matter");

            InventoryResult byCount = index.Query(new InventoryQuery { Text = "pyreal", Sort = InventorySort.Count, Descending = true });
            Eq(byCount.Rows[0].Item.Stack, 1234, "descending count, current server first");
            Eq(byCount.Rows[3].Item.Stack, 10000, "then the other server, descending");
            InventoryResult all = index.Query(new InventoryQuery());
            Eq(all.Rows.Count, index.Rows.Count, "empty search shows everything");
            Eq(all.CharacterCount, 4, "character count");

            Eq(InventoryIndex.Age(Now.AddSeconds(-20), Now), "just now", "age seconds");
            Eq(InventoryIndex.Age(Now.AddMinutes(-12), Now), "12 min ago", "age minutes");
            Eq(InventoryIndex.Age(Now.AddHours(-5), Now), "5 h ago", "age hours");
            Eq(InventoryIndex.Age(Now.AddDays(-1.5), Now), "1 day ago", "age a day");
            Eq(InventoryIndex.Age(Now.AddDays(-9), Now), "9 days ago", "age days");
            Eq(InventoryIndex.Age(default, Now), "never", "never");
        }

        private static void Names()
        {
            Eq(ItemCategory.FromItemType(0x40), ItemCategory.Money, "money");
            Eq(ItemCategory.FromItemType(0x40000), ItemCategory.Money, "trade note");
            Eq(ItemCategory.FromItemType(0x40000000), ItemCategory.Salvage, "salvage");
            Eq(ItemCategory.FromItemType(0x8000 | 0x1), ItemCategory.Caster, "caster before melee");
            Eq(ItemCategory.FromItemType(0x100), ItemCategory.Weapon, "missile weapon");
            Eq(ItemCategory.FromItemType(0x2 | 0x4), ItemCategory.Armor, "armor before clothing");
            Eq(ItemCategory.FromItemType(0x200), ItemCategory.Container, "pack");
            Eq(ItemCategory.FromItemType(0x1000), ItemCategory.Components, "components");
            Eq(ItemCategory.FromItemType(0), ItemCategory.Misc, "unknown");
            Eq(ItemNames.Slot(0x200 | 0x800 | 0x1000), "Chest, Upper arms, Lower arms", "multi-slot armor");
            Eq(ItemNames.Slot(0), "", "no slot");
            Eq(ItemNames.Material(62), "Pyreal", "material");
            Eq(ItemNames.Material(0), "", "no material");
            Eq(ItemNames.Set(14), "Adept's set", "set name");
            Eq(ItemNames.Set(200), "Set 200", "unknown set");
            Check(SpellTable.Count > 5000, "spell table loads");
            Eq(SpellTable.Name(2), "Strength Self I", "spell name");
        }

        // ── Timing ──────────────────────────────────────────────────────────

        private static void SchedulerLogin()
        {
            var s = new ScanScheduler();
            s.Login(0);
            Check(!s.ShouldFullScan(2999) && !s.ShouldPoll(2999), "nothing before 3 s");
            Check(s.ShouldFullScan(3000), "first scan at 3 s");
            s.FullScanDone(3000, 111, 1);
            Check(s.ShouldWrite(3000 + ScanScheduler.WriteQuietMs), "first scan's content is written after the quiet time");
            s.Written(111);
            Check(!s.ShouldFullScan(11_999), "no scan between login scans without a change");
            Check(s.ShouldFullScan(12_000), "second login scan at 12 s");
            s.FullScanDone(12_000, 111, 1);
            Check(!s.ShouldWrite(20_000), "same content: no write");
            Check(s.ShouldFullScan(40_000), "third login scan at 40 s");
            s.FullScanDone(40_000, 112, 1);
            Check(!s.ShouldFullScan(99_999) && s.ShouldFullScan(100_000), "then every minute");
            s.Logout();
            Check(!s.ShouldFullScan(200_000) && !s.ShouldWrite(200_000), "nothing after logout");
        }

        private static void SchedulerSpree()
        {
            // A loot spree: the fingerprint changes every second for two minutes.
            var s = new ScanScheduler();
            s.Login(0);
            s.FullScanDone(3000, 1, 1);
            s.Written(1);
            int scans = 0, writes = 0;
            ulong content = 1, fp = 1;
            for (long t = 3001; t < 3000 + 120_000; t += 100)
            {
                if (t % 1000 == 1) { fp++; s.InventoryEvent(t); }
                if (s.ShouldFullScan(t)) { scans++; content++; s.FullScanDone(t, content, fp); }
                else if (s.ShouldPoll(t)) s.PollResult(t, fp);
                if (s.ShouldWrite(t)) { writes++; s.Written(content); }
            }
            Check(scans >= 10 && scans <= 20, $"full scans during the spree: {scans} (about every 10 s, plus the login scans)");
            Check(writes >= 3 && writes <= 6, $"writes during two minutes of looting: {writes} (at most one per 30 s)");

            // Then it stops: one settled scan and one write shortly after.
            long end = 3000 + 120_000;
            int scansAfter = 0, writesAfter = 0;
            for (long t = end; t < end + 20_000; t += 100)
            {
                if (s.ShouldFullScan(t)) { scansAfter++; s.FullScanDone(t, content, fp); }
                else if (s.ShouldPoll(t)) s.PollResult(t, fp);
                if (s.ShouldWrite(t)) { writesAfter++; s.Written(content); }
            }
            Check(scansAfter <= 2, $"scans after the spree: {scansAfter}");
            Check(writesAfter <= 1 && !s.Dirty, $"the last change is written once ({writesAfter})");

            // A single change: scanned once it has settled (2 s), written 5 s later.
            var q = new ScanScheduler();
            q.Login(0);
            q.FullScanDone(3000, 1, 1);
            q.Written(1);
            q.FullScanDone(12_000, 1, 1);
            q.FullScanDone(40_000, 1, 1);
            q.InventoryEvent(50_000);
            Check(q.ShouldPoll(50_000 + ScanScheduler.EventPollDelayMs), "an event brings the poll forward");
            q.PollResult(50_400, 2);
            Check(!q.ShouldFullScan(52_399) && q.ShouldFullScan(52_400), "full scan once settled");
            q.FullScanDone(52_400, 2, 2);
            Check(!q.ShouldWrite(57_399) && q.ShouldWrite(57_400), "written after 5 quiet seconds");
        }

        private static void SchedulerUnchanged()
        {
            var s = new ScanScheduler();
            s.Login(0);
            s.KnownSaved(42);
            s.FullScanDone(3000, 42, 1);
            Check(!s.Dirty, "content equal to the file on disk: nothing to write");
            s.ContentChanged(4000, 43);
            Check(s.Dirty, "a storage read changed it");
            s.ContentChanged(4500, 42);
            Check(!s.Dirty, "changed back before the write: nothing to write");
            s.ContentChanged(5000, 44);
            s.WriteFailed(9000);
            Check(!s.ShouldWrite(13_999) && s.ShouldWrite(14_000), "a failed write is retried after the quiet time");
            s.RequestFullScan();
            Check(s.ShouldFullScan(14_000), "/ginv scan asks for a scan now");
        }

        // ── Window ──────────────────────────────────────────────────────────

        private static InventoryContext Context(bool big)
        {
            var c = new InventoryContext { InWorld = true, Server = "Aelrynth", Character = "Buffy", Account = "tom", IconsAvailable = true, UtcNow = Now };
            c.Store = new InventoryStore(Path.Combine(_temp, "ui"));
            InventoryIndex index = SampleIndex(out CharacterSnapshot live);
            if (big)
            {
                // 40 characters x 400 items, long names, every category.
                var files = new List<CharacterSnapshot>();
                for (int ch = 0; ch < 40; ch++)
                {
                    var items = new List<ItemRecord>();
                    for (int i = 0; i < 400; i++)
                    {
                        var (cat, _) = ItemCategory.All[i % ItemCategory.All.Length];
                        ItemRecord it = Item((uint)(ch * 1000 + i), $"Superlative Item Of Great Length Number {i}", cat, i % 7 + 1,
                            i % 9 == 0 ? ItemPlace.Equipped : ItemPlace.Pack, i % 3 == 0 ? new[] { "Main pack", "Bag of Holding" } : null, i % 9 == 0 ? 0x200u : 0u);
                        it.Underlay = 0x060011CB; it.Overlay = i % 2 == 0 ? 0x06006C34u : 0u;
                        it.SpellNames = new[] { "Legendary Strength", "Legendary Endurance" }; it.Spells = new uint[] { 1, 2 };
                        items.Add(it);
                    }
                    files.Add(Snap(ch % 3 == 0 ? "Conquest" : "Aelrynth", "acct" + ch, "Character Number " + ch, Now.AddDays(-ch), items.ToArray()));
                }
                index = InventoryIndex.Build(files, live, "Aelrynth", "Buffy");
            }
            c.Live = live;
            c.Index = index;
            c.IndexRevision = 1;
            c.LastSavedUtc = Now.AddMinutes(-1);
            return c;
        }

        private static void Pages()
        {
            foreach (bool big in new[] { false, true })
            {
                InventoryContext c = Context(big);
                var view = new InventoryView(c);
                var host = new UiHost();
                UiWindow w = host.Add(new UiWindow("RynthInventory/Main", "Global Inventory") { Visible = true });
                string tag = big ? "big" : "small";

                foreach (bool grouped in new[] { false, true })
                {
                    c.Settings.GroupByName = grouped;
                    foreach (int tab in new[] { InventoryView.TabSearch, InventoryView.TabCharacters })
                    {
                        view.Tab = tab;
                        Draw(w, view);
                        ParseAll(host, $"{tag} tab {tab} grouped {grouped}", out int ops);
                        Check(ops > 5 && ops <= 1500, $"{tag} tab {tab} grouped {grouped}: {ops} ops");
                        if (big && tab == InventoryView.TabSearch)
                            Console.WriteLine($"    {tag} search page (grouped {grouped}): {ops} ops, {w.Body.Length} bytes");
                        w.MarkSubmitted(true);
                    }
                }
                c.Settings.GroupByName = false;

                // A selected row shows its details (with the spells) and still fits.
                view.Tab = InventoryView.TabSearch;
                Draw(w, view);
                Apply(host, Event(1, 10, w.Hash, w.KeyOf("r." + RowKeyOfFirst(c)), new byte[] { 1, 0 }));
                Draw(w, view);
                ParseAll(host, $"{tag} details", out int dops);
                Check(dops <= 1500 && BodyText(w).Contains("Owner:"), $"{tag} details page ({dops} ops)");
                w.MarkSubmitted(true);

                // No icons on an older engine: still valid, no image ops.
                c.IconsAvailable = false;
                Draw(w, view);
                ParseAll(host, $"{tag} no icons", out _);
                c.IconsAvailable = true;

                // Logged out, no index yet.
                c.Index = null;
                c.InWorld = false;
                Draw(w, view);
                ParseAll(host, $"{tag} no index", out _);
            }
        }

        private static string RowKeyOfFirst(InventoryContext c)
        {
            InventoryResult r = c.Index!.Query(new InventoryQuery
            {
                IncludeStorage = c.Settings.IncludeStorage,
                IncludeEquipped = c.Settings.IncludeEquipped,
                Sort = (InventorySort)c.Settings.Sort,
                Descending = c.Settings.Descending,
            });
            InventoryRow row = r.Rows[0];
            return row.Owner.Key + "|" + (row.Storage?.Id ?? 0) + "|" + row.Item.Id;
        }

        private static void Events()
        {
            InventoryContext c = Context(false);
            c.Settings.GroupByName = false;
            c.Settings.Sort = 0;
            c.Settings.Descending = false;
            CharacterInfo? forgotten = null;
            c.Forget = ci => { forgotten = ci; return "Forgot " + ci.Name; };
            var view = new InventoryView(c);
            var host = new UiHost();
            UiWindow w = host.Add(new UiWindow("RynthInventory/Main", "Global Inventory") { Visible = true });

            // Type a search.
            Draw(w, view);
            Apply(host, Event(5, 1, w.Hash, w.KeyOf("q"), TextPayload("mana")));
            Check(w.NeedsPass, "typing asks for a pass");
            Draw(w, view);
            string body = BodyText(w);
            Check(body.Contains("Mana Stone") && !body.Contains("Iron Salvage"), "the list is filtered");
            Check(body.Contains("Mana Stone: 1 across 1 character"), "the total shows");
            Check(body.Contains("[Conquest] Spike"), "another server's character is labelled");

            // Server filter button.
            Apply(host, Event(5, 2, w.Hash, w.KeyOf("q"), TextPayload("pyreal")));
            Draw(w, view);
            Apply(host, Event(1, 3, w.Hash, w.KeyOf("srv.1"), new byte[] { 1, 0 }));
            Draw(w, view);
            body = BodyText(w);
            Check(body.Contains("Pyreal: 10,845 across 2 characters") && !body.Contains("Willow"), "server filter (Conquest only)");
            Apply(host, Event(1, 4, w.Hash, w.KeyOf("srv.all"), new byte[] { 1, 0 }));
            Draw(w, view);
            Check(BodyText(w).Contains("Pyreal: 12,581 across 4 characters on 2 servers"), "back to all servers");

            // Category combo: index 0 = all.
            int manaIndex = Array.FindIndex(ItemCategory.All, x => x.Id == ItemCategory.ManaStone) + 1;
            Apply(host, Event(5, 5, w.Hash, w.KeyOf("q"), TextPayload("")));
            Apply(host, Event(3, 6, w.Hash, w.KeyOf("f.cat"), BitConverter.GetBytes(manaIndex)));
            Draw(w, view);
            Check(BodyText(w).Contains("1 results"), "category filter from the combo");
            Apply(host, Event(3, 7, w.Hash, w.KeyOf("f.cat"), BitConverter.GetBytes(0)));

            // Characters page: Forget asks first, then calls the action.
            Apply(host, Event(1, 8, w.Hash, w.KeyOf("tab.chars"), new byte[] { 1, 0 }));
            Check(view.Tab == InventoryView.TabCharacters, "characters tab");
            Draw(w, view);
            Check(BodyText(w).Contains("(stale)"), "Willow (10 days) is stale");
            CharacterInfo willow = c.Index!.Characters.First(x => x.Name == "Willow");
            Apply(host, Event(1, 9, w.Hash, w.KeyOf("forget." + willow.Key), new byte[] { 1, 0 }));
            Check(forgotten == null, "one click doesn't forget");
            Draw(w, view);
            Check(BodyText(w).Contains("Forget Willow on Aelrynth?"), "it asks");
            Apply(host, Event(1, 10, w.Hash, w.KeyOf("forget.yes." + willow.Key), new byte[] { 1, 0 }));
            Check(forgotten == willow, "confirmed: forgotten");
            Draw(w, view);
            Check(BodyText(w).Contains("Forgot Willow"), "says so");
            ParseAll(host, "after forget", out _);
        }

        // ── Banking: RynthILT's own tab since 2026-10-21 ───────────────────────

        /// <summary>
        /// Every character's Infinite Leaftide bank was a tab of this window (SilentKelpie's, reading RynthILT's
        /// data); it moved to RynthILT's Banking tab with its tests (RynthILT.Tests BankRosterTests). The window
        /// has its two pages on every world, and RynthInventory reads nothing of RynthILT's.
        /// </summary>
        private static void NoBankingTab()
        {
            const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (string field in new[] { "IltData", "Bank", "BankCommand" })
                Check(typeof(InventoryContext).GetField(field, All) == null, "InventoryContext has no " + field);
            Check(typeof(InventoryView).GetField("TabBanking", All) == null && typeof(InventoryView).GetMethod("ShowBanking", All) == null, "no Banking tab");
            var iltTypes = typeof(InventoryView).Assembly.GetTypes().Where(t => t.Name.StartsWith("Ilt", StringComparison.Ordinal)
                || t.Name.StartsWith("RynthIlt", StringComparison.Ordinal) || t.Name.StartsWith("BankRoster", StringComparison.Ordinal)).Select(t => t.Name).ToList();
            Check(iltTypes.Count == 0, "no RynthILT reader or bank roster in RynthInventory: " + string.Join(",", iltTypes));

            InventoryContext c = Context(false);
            var view = new InventoryView(c);
            var host = new UiHost();
            UiWindow w = host.Add(new UiWindow("RynthInventory/Main", "Global Inventory") { Visible = true });
            Draw(w, view);
            string body = BodyText(w);
            Check(!body.Contains("###tab.bank") && body.Contains("###tab.search") && body.Contains("###tab.chars"), "Search and Characters, no Banking button");
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void Draw(UiWindow w, InventoryView view)
        {
            w.Begin();
            view.Draw(w);
            w.End();
        }

        private static string BodyText(UiWindow w) => Encoding.UTF8.GetString(w.Body);

        private static byte[] TextPayload(string text)
        {
            byte[] t = Encoding.UTF8.GetBytes(text);
            var payload = new byte[3 + t.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)t.Length);
            t.CopyTo(payload, 3);
            return payload;
        }

        private static unsafe void ParseAll(UiHost host, string what, out int maxOps)
        {
            int n = Write(host);
            byte[] buf = SubmitBuffer(host);
            int rc;
            List<ParsedWindow> windows;
            string error;
            fixed (byte* p = buf) rc = DisplayListParser.Parse(p, n, 4000f, out _, out windows, out error);
            Check(rc == 0, $"{what}: engine parser refused the submit ({rc}): {error}");
            maxOps = windows.Where(x => x.List != null).Select(x => x.List!.OpCount).DefaultIfEmpty(0).Max();
        }

        private static byte[] Event(ushort type, uint seq, uint window, uint key, byte[] payload)
        {
            var e = new byte[16 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(e, type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)e.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), window);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), key);
            payload.CopyTo(e, 16);
            return e;
        }
    }
}
