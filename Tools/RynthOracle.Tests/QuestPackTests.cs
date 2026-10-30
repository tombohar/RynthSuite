using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.Shared;

namespace RynthOracle.Tests
{
    /// <summary>
    /// The server plugins' quest packs (2026-10-21: Conquest's, Levistras's and Aelrynth's left RynthOracle
    /// for RynthConquest, RynthLevistras and RynthAelrynth), embedded into this test exe from those projects
    /// as the plugins offer them through "RynthServer.QuestPack".
    /// </summary>
    internal static class TestPacks
    {
        public static string Csv(string id)
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TestPack." + id + ".csv")
                ?? throw new FileNotFoundException("test pack " + id);
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        /// <summary>The pack as RynthOracle takes it from the active server's plugin.</summary>
        public static ServerQuestPack Offer(string id, string tag, string plugin) =>
            new(plugin, id, tag, 0, 1, Csv(id));

        public static QuestPackSource Source(string id, string tag, string plugin) => new(id, tag, Csv(id), plugin);

        public static QuestPackSource Conquest => Source("conquest", "Conquest", "RynthConquest");
        public static QuestPackSource Levistras => Source("levistras", "Levistras", "RynthLevistras");
        public static QuestPackSource Aelrynth => Source("aelrynth", "Aelrynth", "RynthAelrynth");

        /// <summary>The pack for a server id ("" or an unknown id: none, retail only).</summary>
        public static QuestPackSource? ById(string id) => id switch
        {
            "conquest" => Conquest,
            "levistras" => Levistras,
            "aelrynth" => Aelrynth,
            _ => null,
        };
    }

    internal static partial class Program
    {
        /// <summary>A RynthCore server list with one active server owned by <paramref name="plugin"/> (or none).</summary>
        private static EngineServerState Engine(string active, string plugin) =>
            EngineServerState.Parse(EngineJson(active, active.ToUpperInvariant(), plugin, "auto", "World",
                active.Length > 0 ? new[] { EngineRow(active, true, "auto", plugin) } : Array.Empty<string>()))!;

        /// <summary>
        /// The generic quest-pack hook: RynthOracle merges the pack the active server's plugin offers, only
        /// while that plugin owns the active server, under the pack's own tag, and names no server itself.
        /// </summary>
        private static void QuestPackHook()
        {
            // The info JSON a server plugin answers (ServerPackQuestPack.BuildInfoJson), read leniently.
            ServerQuestPackInfo? info = ServerQuestPackInfo.Parse(
                "{\"schema\":1,\"server\":\"conquest\",\"tag\":\"Conquest\",\"plugin\":\"RynthConquest\",\"version\":\"0.2.0\",\"rows\":546,\"bytes\":48939,\"revision\":1,\"later\":[1]}");
            Check(info is { ServerId: "conquest", Tag: "Conquest", Plugin: "RynthConquest", Rows: 546, Bytes: 48939, Revision: 1 }, "pack info: parsed");
            Check(ServerQuestPackInfo.Parse(null) == null && ServerQuestPackInfo.Parse("junk") == null && ServerQuestPackInfo.Parse("[1]") == null,
                  "pack info: junk is no pack");
            Check(ServerQuestPackInfo.Parse("{\"schema\":2,\"bytes\":5}") == null, "pack info: another schema is no pack");
            Check(ServerQuestPackInfo.Parse("{\"schema\":1,\"server\":\"x\"}") == null, "pack info: a pack without a size is no pack");

            // Whose pack counts: only the active server's plugin's.
            ServerQuestPack conquest = TestPacks.Offer("conquest", "Conquest", "RynthConquest");
            Check(ServerQuestPackClient.ForActive(Engine("conquest", "RynthConquest"), conquest) == conquest, "the active server's plugin's pack is merged");
            Check(ServerQuestPackClient.ForActive(Engine("conquest", "rynthconquest"), conquest) == conquest, "plugin names compare in any case");
            Check(ServerQuestPackClient.ForActive(Engine("ilt", "RynthILT"), conquest) == null, "another server active: its pack isn't");
            Check(ServerQuestPackClient.ForActive(Engine("conquest", ""), conquest) == null, "a server without its plugin loaded: no pack");
            Check(ServerQuestPackClient.ForActive(Engine("", ""), conquest) == null, "no server active: no pack");
            Check(ServerQuestPackClient.ForActive(null, conquest) == null, "no server list (an engine older than v86): no pack");
            Check(ServerQuestPackClient.ForActive(Engine("conquest", "RynthConquest"), null) == null, "nothing offered: nothing merged");
            Check(ServerQuestPackClient.OwnerOf(Engine("levistras", "RynthLevistras")) == "RynthLevistras" && ServerQuestPackClient.OwnerOf(Engine("", "")) == ""
                  && ServerQuestPackClient.OwnerOf(null) == "", "the plugin asked for a pack is the active server's owner");

            // What the catalog loads for the server it's on.
            var id = new ServerIdentity();
            Check(QuestCatalog.SourceFor(id, conquest) == null, "no server: retail only, whatever was offered");
            id.Force("conquest", "Conquest", "RynthConquest");
            QuestPackSource? src = QuestCatalog.SourceFor(id, conquest);
            Check(src is { ServerId: "conquest", Tag: "Conquest", From: "RynthConquest" } && src.Csv.Length > 40_000, "the offered pack, under its tag");
            src = QuestCatalog.SourceFor(id, null);
            Check(src is { ServerId: "conquest", Csv: "", From: "" }, "a server whose plugin offers no pack: only its id (an override file can still apply)");

            // End to end: RynthConquest's pack on Conquest; the same pack while Infinite Leaftide is active is not merged.
            var cat = new QuestCatalog();
            cat.Load(QuestCatalog.SourceFor(id, ServerQuestPackClient.ForActive(Engine("conquest", "RynthConquest"), conquest)), null);
            bool bundled = cat.UsingBundled;
            if (bundled)
                Check(cat.PackServer == "Conquest" && cat.ServerCount == 546 && cat.PackSource == "from RynthConquest" && cat.TryGet("trigram4", out _),
                      $"on Conquest with RynthConquest: its 546 quests ({cat.ServerCount}, {cat.PackSource})");
            var ilt = new ServerIdentity();
            ilt.Force("ilt", "InfiniteLeaftide", "RynthILT");
            cat.Load(QuestCatalog.SourceFor(ilt, ServerQuestPackClient.ForActive(Engine("ilt", "RynthILT"), conquest)), null);
            Check(cat.PackServer == "" && cat.ServerCount == 0 && !cat.TryGet("trigram4", out _), "on another server: none of Conquest's quests");
            Check(cat.LastSource is { ServerId: "ilt", Csv: "" }, "the catalog remembers what it was loaded for (Reload uses it)");

            // A pack that isn't one: retail only, nothing thrown.
            cat.Load(new QuestPackSource("odd", "Odd", "<html>not a quest list</html>", "RynthOdd"), null);
            Check(cat.PackServer == "" && cat.ServerCount == 0 && (!bundled || cat.RetailCount == 3565), "a junk pack: retail quests only");

            // The override file is named for the server's id, and an odd id can't leave the packs folder.
            Check(QuestCatalog.PackOverridePath("conquest").EndsWith(Path.Combine("packs", "quests.conquest.csv"), StringComparison.Ordinal), "override: packs\\quests.<id>.csv");
            Check(QuestCatalog.PackOverridePath("..\\x/y").EndsWith(Path.Combine("packs", "quests.___x_y.csv"), StringComparison.Ordinal), "override: an odd id stays in the folder");

            // RynthOracle embeds no server's pack any more.
            string[] embedded = typeof(QuestCatalog).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("RynthOracle.quests.", StringComparison.Ordinal) && n != "RynthOracle.quests.csv").ToArray();
            Check(embedded.Length == 0, "RynthOracle embeds no server's quest pack: " + string.Join(",", embedded));
        }

        /// <summary>
        /// RynthOracle's own code names no server (comments describing history may): the layer rule
        /// (CLAUDE.md, 2026-10-21). Read from the plugin's source files.
        /// </summary>
        private static void NoServerNames()
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "Plugins", "RynthCore.Plugin.RynthOracle"))) dir = Path.GetDirectoryName(dir);
            Check(dir != null, "the RynthOracle sources are found");
            if (dir == null) return;
            string root = Path.Combine(dir, "Plugins", "RynthCore.Plugin.RynthOracle");
            var names = new Regex(@"Ilt[A-Z]|RynthIlt|Leaftide|Conquest|Aelrynth|Levistras|DreamWeave", RegexOptions.CultureInvariant);
            int files = 0;
            var found = new System.Collections.Generic.List<string>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                files++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    Match m = names.Match(CodeOf(lines[i]));
                    if (m.Success) found.Add($"{Path.GetRelativePath(root, file)}:{i + 1} ({m.Value})");
                }
            }
            Check(files > 30, $"RynthOracle's sources were read ({files})");
            Check(found.Count == 0, "RynthOracle's code names no server: " + string.Join(", ", found));
            Check(CodeOf("x = 1; // Conquest's") == "x = 1; " && CodeOf("s = \"a // b\"; // c") == "s = \"a // b\"; ", "comments are told from code");
        }

        /// <summary>A line without its comment ("//" outside a string literal).</summary>
        internal static string CodeOf(string line)
        {
            bool str = false, chr = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (str) { if (c == '\\') i++; else if (c == '"') str = false; continue; }
                if (chr) { if (c == '\\') i++; else if (c == '\'') chr = false; continue; }
                if (c == '"') str = true;
                else if (c == '\'') chr = true;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
            }
            return line;
        }
    }
}
