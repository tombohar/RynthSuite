using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The engine refuses RequestId around a death or a portal (it returns false). A caller that marks an item
// "already asked" before the call, or counts the refusal as an attempt, never asks again; these callers
// record the request only when the engine took it.
internal static class IdRefusalTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("id refusal: the worn-gear appraisal asks again after the engine refused, and once it is taken", EquipmentAppraisalRetried);
        r.Add("id refusal: the weapon identify pump does not count a refused request", WeaponPumpRetried);
    }

    private static void EquipmentAppraisalRetried()
    {
        const uint Player = 0x50000F01, Helm = 0x80000A01;
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;   // wires RequestId / HasAppraisalData
        FakeHost.PlayerIdValue = Player;
        FakeHost.Names[Helm] = "Helm";
        FakeHost.Containers[Helm] = Player;
        FakeHost.ItemTypes[Helm] = 0x2;
        var host = FakeHost.Create(playerId: Player);
        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(plugin, new object[] { FakeHost.LastApi });
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        typeof(RynthAiPlugin).GetField("_objectCache", Any)!.SetValue(plugin, cache);
        cache.OnCreateObject(Helm);
        cache.Tick();
        var equip = new List<(string Name, uint Id, int Slot)> { ("Helm", Helm, 1) };
        void Pass() => typeof(RynthAiPlugin).GetMethod("AppraiseEquipment", Any)!.Invoke(plugin, new object[] { equip });

        FakeHost.RequestIdAccepts = false;   // a portal in progress
        Pass();
        Check.True(FakeHost.RefusedIdRequests >= 1, "the engine refused the first request");
        Check.Eq(FakeHost.IdRequests.Count, 0, "so nothing went out");
        FakeHost.RequestIdAccepts = true;
        Pass();
        Check.Eq(FakeHost.IdRequests.Count(i => i == Helm), 1, "the next pass asked again and it was taken");
        Pass();
        Check.Eq(FakeHost.IdRequests.Count(i => i == Helm), 1, "a taken request is not repeated");
    }

    private static void WeaponPumpRetried()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        var host = FakeHost.Create();
        DateTime t = new(2026, 10, 18, 12, 0, 0);
        var tr = new WeaponElementTracker(host, () => t);
        int[] ids = { 0x60000051 };

        FakeHost.RequestIdAccepts = false;
        Check.Eq(tr.Pump(ids), 0, "refused: nothing asked for");
        Check.Eq(tr.RequestsSent, 0, "and not counted");
        FakeHost.RequestIdAccepts = true;
        t = t.AddMilliseconds(500);
        Check.Eq(tr.Pump(ids), 0, "inside the gap the retry is paced");
        t = t.AddMilliseconds(2000);
        Check.Eq(tr.Pump(ids), ids[0], "after the gap it is asked again");
        t = t.AddMilliseconds(5100);
        Check.Eq(tr.Pump(ids), ids[0], "unanswered: the second (and last) ask still available, the refusal cost none");
        t = t.AddMilliseconds(5100);
        Check.Eq(tr.Pump(ids), 0, "at most twice per weapon");
    }
}
