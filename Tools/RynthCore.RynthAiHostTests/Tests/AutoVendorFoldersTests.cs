using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Loot.Editing;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.Vendor;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-04 (Discord): "Autovendor creates folders for each server, but it only reads from the
// parent folder". A vendor's run looked in the character, server and main folders (UB's order),
// but everything a player sees or picks named the main folder only: the Loot Editor's picker listed
// AutoVendor and its server folders as one tree and never the character folder, the dashboard's
// Edit on a vendor with no profile made the new one in the main folder, beside the server folder
// AutoVendor had just made, and "No vendor profile exists" gave the main folder's path alone.
//
// AutoVendorManager.MainProfileDir points at the scratch folder here; FakeHost.WorldName is the server.
internal static unsafe class AutoVendorFoldersTests
{
    private const uint Player = 0x50000B01;
    private const uint Vendor = 0x70000B01;
    private const string VendorFile = "Town Vendor.utl";

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("autovendor folders: a profile only in AutoVendor\\<server> is found, for the run and the Edit button", ServerFolderFound);
        r.Add("autovendor folders: character folder, then server, then main (named profiles before default.utl)", UbOrder);
        r.Add("autovendor folders: the folders a vendor open makes are the ones read (a server name with a bad file character)", MadeFoldersAreRead);
        r.Add("autovendor folders: a vendor with no profile gets its new one in the server folder (main before the server is known)", NewProfileInServerFolder);
        r.Add("autovendor folders: the Loot Editor lists character, server and main profiles in that order, marked by folder", PickerListsEveryFolder);
        r.Add("autovendor folders: the plugin's Loot Editor picker reads AutoVendor's folders", PluginPickerWired);
        r.Add("autovendor folders: 'No vendor profile exists' names every folder it looked in", MissingProfileNamesEveryFolder);
    }

    private sealed class Rig
    {
        public AutoVendorManager M = null!;
        public string Main = "", Char = "", CharAv = "";
        public RynthCoreHost Host;
    }

    /// <summary>A fresh main and character folder; <paramref name="world"/> is the server ("" = not known).</summary>
    private static Rig World(string world, FakeHost.ApiHook? api = null)
    {
        FakeHost.Reset();
        FakeHost.ConfigureApi = api;
        FakeHost.WorldName = world;
        FakeHost.Names[Player] = "Test Seller";
        FakeHost.Names[Vendor] = "Town Vendor";
        string root = Path.Combine(Program.TempRoot, "avfolders-" + Guid.NewGuid().ToString("N")[..8]);
        var rig = new Rig { Main = Path.Combine(root, "AutoVendor"), Char = Path.Combine(root, "SettingsProfiles", "Srv", "Char") };
        rig.CharAv = Path.Combine(rig.Char, AutoVendorManager.ProfileSubfolder);
        AutoVendorManager.MainProfileDirOverride = rig.Main;
        rig.Host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(rig.Host);
        cache.SetPlayerId(Player);
        // Off: the open only resolves the Edit button's profile, no session.
        var settings = new LegacyUiSettings { AutoVendorEnabled = false, AutoVendorShowMerchantInfo = false };
        string charFolder = rig.Char;
        rig.M = new AutoVendorManager(rig.Host, settings, cache, Player, () => charFolder);
        return rig;
    }

    private static void Scoped(Action body)
    {
        string? old = AutoVendorManager.MainProfileDirOverride;
        try { body(); }
        finally { AutoVendorManager.MainProfileDirOverride = old; FakeHost.WorldName = ""; }
    }

    private static string Put(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, "UTL\n1\n0\n");
        return p;
    }

    private static bool Same(string? a, string b)
        => a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void ServerFolderFound() => Scoped(() =>
    {
        var rig = World("Conquest");
        string server = Path.Combine(rig.Main, "Conquest");
        string p = Put(server, VendorFile);
        Put(rig.Main, "Other Vendor.utl");   // something in the main folder that isn't this vendor's

        Check.True(Same(rig.M.ResolveProfilePath(VendorFile, false), p), "the run's lookup finds AutoVendor\\Conquest\\" + VendorFile);
        rig.M.OnVendorOpen(Vendor);
        Check.True(Same(rig.M.OpenVendorProfilePath, p), "the Edit button opens the server folder's profile: " + rig.M.OpenVendorProfilePath);
        Check.True(Same(rig.M.ResolveProfilePath("Town Vendor", true), p), "/ub autovendor Town Vendor (no .utl) finds it too");
    });

    private static void UbOrder() => Scoped(() =>
    {
        var rig = World("Conquest");
        string server = Path.Combine(rig.Main, "Conquest");
        string inChar = Put(rig.CharAv, VendorFile);
        string inServer = Put(server, VendorFile);
        string inMain = Put(rig.Main, VendorFile);

        Check.True(Same(rig.M.ResolveProfilePath(VendorFile, false), inChar), "the character folder wins");
        File.Delete(inChar);
        Check.True(Same(rig.M.ResolveProfilePath(VendorFile, false), inServer), "then the server folder");
        File.Delete(inServer);
        Check.True(Same(rig.M.ResolveProfilePath(VendorFile, false), inMain), "the main folder last");

        // UB: the vendor's own profile in any folder before default.utl in any folder.
        string serverDefault = Put(server, "default.utl");
        Check.True(Same(rig.M.ResolveProfilePath(VendorFile, false), inMain), "a named profile in main beats default.utl in the server folder");
        File.Delete(inMain);
        Put(rig.Main, "default.utl");
        Check.True(Same(rig.M.ResolveProfilePath(VendorFile, false), serverDefault), "the server's default.utl before main's");

        List<string> dirs = rig.M.ProfileDirs();
        Check.Eq(dirs.Count, 3, "three folders");
        Check.True(dirs.Count == 3 && Same(dirs[0], rig.CharAv) && Same(dirs[1], server) && Same(dirs[2], rig.Main),
            "ProfileDirs: character, server, main: " + string.Join(" | ", dirs));
    });

    private static void MadeFoldersAreRead() => Scoped(() =>
    {
        var rig = World("Dream:Weave ");
        rig.M.OnVendorOpen(Vendor);   // EnsureProfileFolders
        string made = Path.Combine(rig.Main, "Dream_Weave");
        Check.True(Directory.Exists(made), "the server folder is made with the name's bad character replaced");
        Check.True(Directory.Exists(rig.CharAv), "the character's AutoVendor folder is made");
        var dirs = rig.M.ProfileDirs();
        Check.True(dirs.Any(d => Same(d, made)) && dirs.Any(d => Same(d, rig.CharAv)), "and both are read: " + string.Join(" | ", dirs));
        string p = Put(made, VendorFile);
        Check.True(Same(rig.M.ResolveProfilePath(VendorFile, false), p), "a profile put in the made folder is found");
    });

    private static void NewProfileInServerFolder() => Scoped(() =>
    {
        var rig = World("Conquest");
        rig.M.OnVendorOpen(Vendor);
        string expect = Path.Combine(rig.Main, "Conquest", VendorFile);
        Check.True(Same(rig.M.OpenVendorProfilePath, expect), "Edit with no profile anywhere: a new one in AutoVendor\\Conquest: " + rig.M.OpenVendorProfilePath);
        Check.True(Directory.Exists(Path.GetDirectoryName(expect)!), "its folder exists, so Save can create it");

        var unknown = World("");
        Check.True(Same(unknown.M.ResolveProfilePath(VendorFile, false), Path.Combine(unknown.Main, VendorFile)),
            "no server name: the main folder, as UB");
    });

    private static List<LootEditFile> VendorFiles(LootEditorBridge b)
    {
        LootEditState s = JsonSerializer.Deserialize(b.StateJson(), LootEditJsonContext.Default.LootEditState)!;
        return s.Files.Where(f => f.Display.StartsWith("AutoVendor", StringComparison.Ordinal)).ToList();
    }

    private static void PickerListsEveryFolder() => Scoped(() =>
    {
        var rig = World("Conquest");
        string inChar = Put(rig.CharAv, VendorFile);
        string inServer = Put(Path.Combine(rig.Main, "Conquest"), "default.utl");
        string inMain = Put(rig.Main, "default.utl");
        string otherServer = Put(Path.Combine(rig.Main, "DreamWeave"), "Elder Vendor.utl");

        var b = new LootEditorBridge(() => string.Empty, _ => { }, () => rig.M.ProfileDirs());
        List<LootEditFile> files = VendorFiles(b);
        string shown = string.Join(" | ", files.Select(f => f.Display));
        Check.Eq(files.Count, 4, "four AutoVendor profiles: " + shown);
        if (files.Count != 4) return;
        Check.True(Same(files[0].Path, inChar), "the character folder's first");
        Check.Eq(files[0].Display, "AutoVendor (character): " + VendorFile, "marked as the character's");
        Check.True(Same(files[1].Path, inServer), "then this server's");
        Check.Eq(files[1].Display, "AutoVendor (Conquest): default.utl", "marked by the server's folder");
        Check.True(Same(files[2].Path, inMain), "then the main folder's");
        Check.Eq(files[2].Display, "AutoVendor: default.utl", "the main folder keeps the plain mark");
        Check.True(Same(files[3].Path, otherServer), "other servers' folders last");
        Check.Eq(files[3].Display, "AutoVendor (DreamWeave): Elder Vendor.utl", "marked by their folder");
        Check.False(shown.Contains("Srv") || shown.Contains("Char"), "the character's name and folder path aren't in the picker");
    });

    private static void PluginPickerWired() => Scoped(() =>
    {
        var rig = World("Conquest");
        string inChar = Put(rig.CharAv, VendorFile);
        string inServer = Put(Path.Combine(rig.Main, "Conquest"), VendorFile);

        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(plugin, new object[] { FakeHost.LastApi });
        typeof(RynthAiPlugin).GetField("_autoVendor", Any)!.SetValue(plugin, rig.M);
        List<LootEditFile> files = VendorFiles(plugin.LootEditor);
        Check.True(files.Any(f => Same(f.Path, inChar)), "the character folder's profile is listed: " + string.Join(" | ", files.Select(f => f.Display)));
        Check.True(files.Any(f => Same(f.Path, inServer)), "the server folder's profile is listed");
    });

    // The engine's vendor calls, present but with no vendor to show: enough for a session to start.
    private static void VendorCalls(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 82u);
        api.GetVendorInfoFn        = (IntPtr)(delegate* unmanaged[Cdecl]<VendorInfoNative*, int>)&NoVendorInfo;
        api.GetVendorItemsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<VendorItemNative*, int, int>)&NoVendorItems;
        api.VendorBuyFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, VendorTradeEntryNative*, int, uint>)&NoBuy;
        api.VendorSellFn           = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, uint>)&NoSell;
        api.GetVendorTradeStatusFn = (IntPtr)(delegate* unmanaged[Cdecl]<VendorTradeStatusNative*, int>)&NoTradeStatus;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NoVendorInfo(VendorInfoNative* info) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NoVendorItems(VendorItemNative* items, int max) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint NoBuy(uint vendor, VendorTradeEntryNative* entries, int count) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint NoSell(uint vendor, uint* ids, int count) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NoTradeStatus(VendorTradeStatusNative* status) => 0;

    private static void MissingProfileNamesEveryFolder() => Scoped(() =>
    {
        try
        {
            var rig = World("Conquest", VendorCalls);
            var settings = new LegacyUiSettings { AutoVendorEnabled = true, AutoVendorShowMerchantInfo = false };
            string charFolder = rig.Char;
            var m = new AutoVendorManager(rig.Host, settings, new WorldObjectCache(rig.Host), Player, () => charFolder);
            m.OnVendorOpen(Vendor);
            string line = FakeHost.Chat.FirstOrDefault(c => c.Contains("No vendor profile exists")) ?? string.Empty;
            Check.True(line.Length > 0, "a vendor with no profile says so: " + string.Join(" / ", FakeHost.Chat));
            Check.True(line.Contains(VendorFile) && line.Contains("default.utl"), "the names it looked for: " + line);
            Check.True(line.Contains(rig.CharAv) && line.Contains(Path.Combine(rig.Main, "Conquest")) && line.Contains(rig.Main),
                "the character, server and main folders: " + line);
        }
        finally { FakeHost.ConfigureApi = null; }
    });
}
