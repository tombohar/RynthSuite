using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// What the sample metas (meta samples: PistolPete, S_Bulb/Caul/Colo/CountPhainor/Cows/Deewain,
// Willow; audited 2026-10-18 with MetaAuditTests) needed that RynthAi didn't do.
internal static class MetaSampleCompatTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("meta samples: vtsetsetting[] takes VTank distances like /vt opt set (6 yd, not the 50 yd default)", VtSetSettingDistances);
        r.Add("meta samples: the options they set map to RynthAi settings (pet range, blacklist, rebuff, auto attack power)", MappedOptions);
        r.Add("meta samples: /vt deletemonster is accepted", DeleteMonster);
        r.Add("meta samples: /ub close corpse closes the open corpse", UbCloseCorpse);
        r.Add("meta samples: /ub <expression> is evaluated (the /ub bc vtsetmetastate[] broadcast)", UbExpression);
        r.Add("meta samples: /mt face and /ub face take decimals and coordinates, and hold nav while turning", Face);
    }

    private static (LegacyUiSettings S, MetaManager M) Meta()
    {
        FakeHost.Reset();
        var s = new LegacyUiSettings();
        var m = new MetaManager(s, FakeHost.Create(), new PlayerVitalsCache());
        m.SetRaCommandHandler(_ => { });
        return (s, m);
    }

    private static void VtSetSettingDistances()
    {
        var (s, m) = Meta();
        m.Expressions.Evaluate("vtsetsetting[AttackDistance,`0.025`]");
        Check.Eq(s.MonsterRange, 6, "vtsetsetting AttackDistance 0.025 = 6 yd (was left at 50)");
        m.Expressions.Evaluate("vtsetsetting[ApproachDistance,`0.0208333`]");
        Check.Eq(s.ApproachRange, 5, "ApproachDistance 0.0208333 = 5 yd");
        Check.Eq(m.Expressions.Evaluate("vtgetsetting[AttackDistance]"), "0.025", "vtgetsetting reads it back in VTank units");
        m.TryHandleVtCommand("/vt opt set AttackDistance 0.2166");
        Check.Eq(s.MonsterRange, 52, "/vt opt set the same way");
        m.Expressions.Evaluate("vtsetsetting[IdlePeaceMode,`1`]");
        Check.True(s.PeaceModeWhenIdle, "a bool through vtsetsetting");
        m.Expressions.Evaluate("rasetsetting[MonsterRange,`7`]");
        Check.Eq(s.MonsterRange, 7, "rasetsetting keeps RynthAi's own names and units");
    }

    private static void MappedOptions()
    {
        var (s, m) = Meta();
        m.Expressions.Evaluate("vtsetsetting[PetCustomRange,`0.0625`]");
        Check.Eq(s.CustomPetRange, 15, "PetCustomRange 0.0625 = 15 yd");
        m.Expressions.Evaluate("vtsetsetting[BlacklistMonsterTimeoutSeconds,`45`]");
        Check.Eq(s.BlacklistTimeoutSec, 45, "BlacklistMonsterTimeoutSeconds");
        m.TryHandleVtCommand("/vt opt set rebufftimeremainingseconds 4500");
        Check.Eq(s.RebuffSecondsRemaining, 4500, "rebufftimeremainingseconds (VTank's spelling)");

        // AutoAttackPower: on = automatic (-1); off = the attack bar's value.
        m.TryHandleVtCommand("/vt setattackbar .9");
        m.Expressions.Evaluate("vtsetsetting[AutoAttackPower,`1`]");
        Check.True(s.MeleeAttackPower == -1 && s.MissileAttackPower == -1, "AutoAttackPower on: automatic");
        Check.Eq(m.Expressions.Evaluate("vtgetsetting[AutoAttackPower]"), "true", "reads back on");
        m.TryHandleVtCommand("/vt opt set autoattackpower false");
        Check.True(s.MeleeAttackPower == 90 && s.MissileAttackPower == 90, "AutoAttackPower off: the bar's 90%");
    }

    private static void DeleteMonster()
    {
        var (_, m) = Meta();
        Check.True(m.TryHandleVtCommand("/vt deletemonster"), "/vt deletemonster handled (not sent to AC as unknown)");
    }

    private sealed class World
    {
        public readonly RynthAiPlugin P = new();
        public World(bool closeCalls = true)
        {
            FakeHost.Reset();
            FakeHost.CloseContainerCalls = closeCalls;
            FakeHost.Create(playerId: 0x50000F31);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, new LegacyUiSettings());
            Set("_dashboard", dash);
            Set("_initialized", true);
            Set("_loginComplete", true);
        }
        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public bool Ub(string line) => (bool)typeof(RynthAiPlugin).GetMethod("HandleUbCommand", Any)!.Invoke(P, new object[] { line })!;
        public bool Mt(string line) => (bool)typeof(RynthAiPlugin).GetMethod("HandleMtCommand", Any)!.Invoke(P, new object[] { line })!;
    }

    private static void UbCloseCorpse()
    {
        var w = new World();
        Check.True(w.Ub("/ub close corpse"), "/ub close corpse handled with nothing open");
        Check.True(FakeHost.Chat.Any(c => c.Contains("No container is currently open")), "and says so, as ub-IT");
        FakeHost.GroundContainer = 0x80F00001;
        w.Ub("/ub close corpse");
        Check.True(FakeHost.Closes.Contains(0x80F00001), "closes the open container");
        Check.False(FakeHost.Chat.Any(c => c.Contains("Unrecognized") || c.Contains("Unknown /ub command")), "never 'Unknown /ub command'");
    }

    private static void UbExpression()
    {
        var w = new World();
        Check.True(w.Ub("/ub vtsetmetastate[NavigatingToFlowerTwo]"), "/ub <expression> handled");
        Check.False(w.Ub("/ub notaverb x"), "a plain unknown verb is still unknown");
    }

    private static void Face()
    {
        var w = new World();
        // No jumper (not logged in fully): one plain turn.
        Check.True(w.Mt("/mt face 135.5"), "/mt face 135.5 (decimal)");
        Check.True(Math.Abs(FakeHost.HeadingDeg - 135.5f) < 0.01f, "turned to 135.5");
        Check.True(w.Ub("/ub face 450"), "/ub face 450");
        Check.True(Math.Abs(FakeHost.HeadingDeg - 90f) < 0.01f, "450 wraps to 90");

        // A coordinate: from 10N 10E, 20N 10E is due north and 10N 20E due east.
        FakeHost.SetNavPosition(10, 10);
        Check.True(w.Mt("/mt face 20N, 10E"), "/mt face <coords>");
        Check.True(FakeHost.HeadingDeg < 0.5f || FakeHost.HeadingDeg > 359.5f, "north is 0");
        Check.True(w.Ub("/ub face 10N 20E"), "/ub face <coords> (no comma)");
        Check.True(Math.Abs(FakeHost.HeadingDeg - 90f) < 0.5f, "east is 90");
        Check.False(w.Mt("/mt face somewhere"), "not a heading or a coordinate: not handled");

        // With the jumper: nav is held while turning and given back once the heading is reached.
        var s = new LegacyUiSettings { EnableNavigation = true };
        var jumper = new Jumper(FakeHost.Create(playerId: 0x50000F31), s, _ => { });
        w.Set("_jumper", jumper);
        w.Mt("/mt face 200");
        Check.False(s.EnableNavigation, "nav held while turning");
        System.Threading.Thread.Sleep(120);
        jumper.Tick();
        Check.True(s.EnableNavigation, "nav back once facing 200");
        Check.False(jumper.IsBusy, "and the jumper is free");
    }
}
