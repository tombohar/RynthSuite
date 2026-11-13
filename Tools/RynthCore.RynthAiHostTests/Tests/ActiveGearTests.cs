using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ActiveGear;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Active Gear window and /ra gear (RynthAiPlugin.ActiveGear.cs) on the fake engine: what is
// worn (the v87 tracker), each item's appraisal (spells, mana, rate, when identified), the
// player's endless enchantments, the summary line /ra gear says, Check items' identifies, and the
// window in the sections manifest without a bar button.
internal static class ActiveGearTests
{
    private const uint Player = 0x50000F31, Ring = 0x80000F01, Amulet = 0x80000F02, Coat = 0x80000F03, Sword = 0x80000F04;
    private const int LeftRing = 0x00040000, Neck = 0x00008000, CoatMask = 0x00000200 | 0x00000400 | 0x00000800, MeleeMask = 0x00100000;
    private const uint MajorStrength = 2576, Impen = 1483;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("active gear: /ra gear says the summary and opens the window", Summary);
        r.Add("active gear: /ra gear list, check (unidentified first, gently) and a wrong option", ListCheckAndUsage);
        r.Add("active gear: the window is in the manifest with no bar button", Manifest);
    }

    private static readonly List<(string Key, int Mode)> PanelCalls = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint FakeUiColor(int token) => 0xFFFFFFFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr FakeUiFont(int font) => IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int FakeSetPanelOpen(byte* key, int mode)
    {
        PanelCalls.Add((Marshal.PtrToStringUTF8((IntPtr)key) ?? "", mode));
        return mode == 0 ? 0 : 1;
    }

    private static unsafe void PluginWindows(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 89u);
        api.GetUiColorFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, uint>)&FakeUiColor;
        api.GetUiFontFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr>)&FakeUiFont;
        api.SetPanelOpenFn = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&FakeSetPanelOpen;
    }

    private sealed class Rig
    {
        public readonly RynthAiPlugin P = new();

        public Rig()
        {
            FakeHost.Reset();
            PanelCalls.Clear();
            EquippedState.Invalidate();
            FakeHost.ConfigureApi = PluginWindows;
            FakeHost.EquippedCalls = true;
            FakeHost.WeaponCalls = true;   // RequestId, HasAppraisalData
            FakeHost.TargetCalls = true;   // GetLastIdTime
            FakeHost.Names[Player] = "Tester";
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // A ring whose cantrip is on the player: 370 mana at 0.5 a second is 12 minutes and some.
            Item(Ring, "Ring of Testing", LeftRing, spells: new[] { MajorStrength }, cur: 370, max: 2000, rate: -0.5, idAt: now);
            FakeHost.Enchantments.Add((MajorStrength, double.MaxValue));
            // A coat out of mana.
            Item(Coat, "Test Coat", CoatMask, spells: new[] { Impen }, cur: 0, max: 1000, rate: -0.2, idAt: now);
            // An amulet never identified.
            FakeHost.Names[Amulet] = "Test Amulet";
            FakeHost.Equipped[Amulet] = Neck;
            // A plain sword: identified, no spells, no mana: not listed.
            FakeHost.Names[Sword] = "Plain Sword";
            FakeHost.Equipped[Sword] = MeleeMask;
            FakeHost.Appraised.Add(Sword);

            RynthCoreHost host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            foreach (uint id in new[] { Ring, Amulet, Coat, Sword }) _ = cache[unchecked((int)id)];
            Set("_objectCache", cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
        }

        private static void Item(uint id, string name, int slot, uint[] spells, int cur, int max, double rate, long idAt)
        {
            FakeHost.Names[id] = name;
            FakeHost.Equipped[id] = unchecked((uint)slot);
            FakeHost.Appraised.Add(id);
            FakeHost.SpellIds[id] = spells;
            FakeHost.Ints[(id, 107)] = cur;
            FakeHost.Ints[(id, 108)] = max;
            FakeHost.Doubles[(id, 5)] = rate;
            FakeHost.IdTimes[id] = idAt;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Call(string method, params object?[] args) => typeof(RynthAiPlugin).GetMethod(method, Any)!.Invoke(P, args);

        public List<string> Typed(string line)
        {
            int before = FakeHost.Chat.Count;
            int eat = 0;
            P.OnChatBarEnter(line, ref eat);
            return FakeHost.Chat.Skip(before).ToList();
        }
    }

    private static string Show(List<string> lines) => string.Join(" | ", lines);

    private static void Summary()
    {
        var rig = new Rig();
        List<string> lines = rig.Typed("/ra gear");
        Check.Eq(lines.Count, 1, "one line: " + Show(lines));
        Check.True(lines.Count == 1 && lines[0].Contains("1 of 3 items active, 1 unknown, off: Chest (out of mana), lowest: Left Ring 12 min"),
            "the summary: " + Show(lines));
        Check.True(lines.Count == 1 && lines[0].Contains("Active Gear window open"), "says the window opened: " + Show(lines));
        Check.True(PanelCalls.Count > 0 && PanelCalls[^1] == (RynthAiPlugin.ActiveGearTitle, (int)RynthPanelOpen.Open), "opened by its panel key");

        ActiveGearView view = rig.P.ActiveGearViewForTests;
        Check.Eq(view.Rows.Length, 3, "ring, coat, amulet; the plain sword isn't listed");
        Check.True(view.Rows.All(r => r.Id != unchecked((int)Sword)), "no sword row");
        ActiveGearRow ring = view.Rows.Single(r => r.Id == unchecked((int)Ring));
        Check.Eq(ring.Verdict.Active, GearActive.Yes, "ring on");
        Check.Eq(ring.SlotName, "Left Ring", "ring slot");
        Check.Eq(ActiveGearText.Mana(ring), "370 / 2,000", "ring mana");
        ActiveGearRow amulet = view.Rows.Single(r => r.Id == unchecked((int)Amulet));
        Check.Eq(amulet.Verdict.Active, GearActive.Unknown, "amulet unknown");
        Check.Eq(ActiveGearText.Mana(amulet), "?", "amulet mana unknown");
        Check.Eq(view.Rows[0].Id, unchecked((int)Coat), "armor listed first");

        lines = rig.Typed("/ra gear hide");
        Check.True(lines.Count == 1 && lines[0].Contains("closed"), "hide replies: " + Show(lines));
    }

    private static void ListCheckAndUsage()
    {
        var rig = new Rig();
        List<string> lines = rig.Typed("/ra gear list");
        Check.Eq(lines.Count, 4, "summary + 3 items: " + Show(lines));
        Check.True(lines.Any(l => l.Contains("Left Ring, Ring of Testing: Yes") && l.Contains("12 min left") && l.Contains("1/1 spells on")), Show(lines));
        Check.True(lines.Any(l => l.Contains("Chest, Test Coat: No (out of mana)")), Show(lines));
        Check.True(lines.Any(l => l.Contains("Neck, Test Amulet: ? (not identified yet)")), Show(lines));

        rig.Call("TickActiveGear");   // the worn items tracked
        FakeHost.IdRequests.Clear();
        lines = rig.Typed("/ra gear check");
        Check.True(lines.Count == 1 && lines[0].Contains("identifying 3 worn items"), "check replies: " + Show(lines));
        rig.Call("TickActiveGear");
        Check.Eq(FakeHost.IdRequests.Count, 1, "one identify at a time");
        Check.True(FakeHost.IdRequests.Count == 1 && FakeHost.IdRequests[0] == Amulet, "the unidentified one first");
        rig.Call("TickActiveGear");
        Check.Eq(FakeHost.IdRequests.Count, 1, "the next waits its 1.5 s");

        lines = rig.Typed("/ra gear sideways");
        Check.True(lines.Count == 1 && lines[0].Contains("unknown option") && lines[0].Contains(RynthAiPlugin.ActiveGearUsage), "usage: " + Show(lines));

        // Without the object cache (before the world loads) it says why instead of an empty summary.
        rig.Set("_objectCache", null);
        lines = rig.Typed("/ra gear");
        Check.True(lines.Count == 1 && lines[0].Contains("not in the world"), "not ready: it says why: " + Show(lines));
    }

    private static void Manifest()
    {
        string json = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, dashboard: true, monsterStats: true, activeGear: true);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, string.Empty, "no parse error");
        Check.Eq(m.Windows.Length, 3, "dashboard, Monster Stats, Active Gear");
        SectionWindowDecl w = m.Windows.Single(x => x.Id == "activegear");
        Check.Eq(w.Title, RynthAiPlugin.ActiveGearTitle, "title (its panel key)");
        Check.Eq(w.Surface, "window:activegear", "the surface PluginExports.RenderEmbed draws");
        Check.False(w.Bar, "no bar button");
        Check.False(w.TakeOver || w.Frameless, "the engine's frame");
        Check.True(w.Width >= w.MinWidth && w.Height >= w.MinHeight, "sizes");

        SectionManifest alone = SectionManifestParser.Parse("RynthAi",
            SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, activeGear: true), out error);
        Check.True(error.Length == 0 && alone.Windows.Length == 1 && alone.Windows[0].Id == "activegear", "alone");
    }
}
