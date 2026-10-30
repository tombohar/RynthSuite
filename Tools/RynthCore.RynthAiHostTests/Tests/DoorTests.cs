using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Doors (2026-10-13, a mage on DreamWeave, "doors aren't opening"): the use went out while the
// client was busy (portal space), a second use followed 3 s later with the first unanswered (the
// server's walk to the door took up to 3.7 s), and a door still closed after a use was called
// locked and sent to the lockpick cooldown although the server never said so. See the header of
// DoorInteractionController.cs for the rules these check.
internal static class DoorTests
{
    private const uint Player = 0x50000D01;
    private const uint Door = 0x701C9139;
    private const uint Pick = 0x80000D11;
    private const uint Kit = 0x80000D12;
    private const uint Closed = 0x00010018, Ethereal = 0x4;
    private const uint BfDoor = 0x1000;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x01C90231u, 60f, 10f, 0f);

    public static void Register(Runner r)
    {
        r.Add("door: no use while the client is busy or another use is unanswered; it goes when they clear", WaitsForBusyAndAnswer);
        r.Add("door: a slow server walk (3.7 s to the answer) is waited out - one use, no retry, opened", SlowWalkOneUse);
        r.Add("door: no second use while the first is unanswered; a lost use is re-sent only after the answer limit", NoDoubleUse);
        r.Add("door: still closed after an answered use, no 'locked' from the server: used again, never picked", NotLockedWithoutServer);
        r.Add("door: the server says locked: a lockpick goes on it, then the door is opened again", LockedUsesLockpick);
        r.Add("door: locked and no lockpick: cooldown, and the door is skipped a while", LockedNoPick);
        r.Add("door: nothing in portal space, nor just after it", NothingInPortalSpace);
        r.Add("door: a door that opens is released at once (no fixed waits)", OpensFast);
        r.Add("door: 'locked' from chat counts only as the server's line, not a player's", LockedChatLine);
        r.Add("door: Only Doors On My Path - a door beside the way is left shut, one straight ahead is opened", PathOnlyAhead);
        r.Add("door: Only Doors On My Path - on a route the door on the next leg goes, not a nearer one beside it", PathOnlyRoute);
        r.Add("door: Only Doors On My Path - not a door past the range along the path, nor one behind the route's direction", PathOnlyRange);
        r.Add("door: one created before its position was readable (just after a portal) is still found and opened", CreatedBeforePosition);
    }

    /// <summary>
    /// A dungeon's objects arrive while the character is still in portal space, often before their
    /// position reads: such a door was filed as inventory and never seen by the door scans (Dargoth
    /// Hera, Infested Mine, 2026-10-19: Open Doors on, a closed door on the route, no use at all).
    /// </summary>
    private static void CreatedBeforePosition() => With(() =>
    {
        foreach (uint? itemType in new uint?[] { 0x80u, null })   // a readable item type, and none at all
        {
            var b = new Bot();
            MoveDoor(Door, 40f, 0f);                     // the rig's door far out of range
            FakeHost.Names[Door2] = "Door";
            FakeHost.Bitfields[Door2] = BfDoor;
            FakeHost.ObjectStates[Door2] = Closed;
            if (itemType is uint t) FakeHost.ItemTypes[Door2] = t;
            b.Cache.OnCreateObject(Door2);                // no position yet
            for (int i = 0; i < 12; i++) b.Cache.Tick();  // classified (and any retries spent) without one
            Check.True(b.Cache.GetLandscapeObjects().Any(o => unchecked((uint)o.Id) == Door2),
                $"a static door stays a world object without a position (item type {(itemType is null ? "unreadable" : "0x80")})");
            Check.False(b.Cache.GetInventory().Any(o => unchecked((uint)o.Id) == Door2), "never filed as inventory");

            FakeHost.Positions[Door2] = (Me0.Cell, Me0.X + 2f, Me0.Y, Me0.Z);   // the position comes in
            b.Tick();
            b.Run(300);
            Check.Eq(Uses(Door2), 1, "the door is used once it has a position");
        }
    });

    // ── Rig ─────────────────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new() { IsMacroRunning = true, OpenDoors = true, OpenDoorRange = 5f, AutoUnlockDoors = true };
        public readonly WorldObjectCache Cache;
        public long Now = 50_000_000;
        public readonly RynthCoreHost Host;

        public Bot(bool lockpick = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;      // UseObject
            FakeHost.UseOnCalls = true;       // UseObjectOn (the lockpick)
            FakeHost.ServerGates = true;      // GetUseDoneSeq, GetCastBusyState
            FakeHost.LastUseDoneCalls = true;
            FakeHost.StopCalls = true;
            FakeHost.Names[Player] = "Mage";
            FakeHost.Positions[Player] = Me0;
            FakeHost.PlayerPose = Me0;
            FakeHost.Names[Door] = "Door";
            FakeHost.Positions[Door] = (Me0.Cell, Me0.X + 2f, Me0.Y, Me0.Z);
            FakeHost.Bitfields[Door] = BfDoor;
            FakeHost.ObjectStates[Door] = Closed;
            FakeHost.Names[Pick] = "Plain Lockpick";
            FakeHost.Names[Kit] = "Treated Healing Kit";
            Host = FakeHost.Create(Player);

            ActionPacer.Reset();
            ActionPacer.Enabled = true;
            ActionPacer.NowMs = () => Now;
            UseAudit.Reset("RynthAi", () => true);
            UseAudit.NowMs = () => Now;
            UseAudit.TrackWorldUses = true;
            MoveAudit.Reset();
            MoveAudit.NowMs = () => Now;
            var rec = typeof(RynthAiPlugin).GetMethod("RecordUseForPacing", Any)!;
            UseAudit.AfterSend = (h, u) => rec.Invoke(null, new object[] { h, u });
            RynthAiPlugin.DoorClock = () => Now;

            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(Host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Cache.OnCreateObject(Player);
            Cache.OnCreateObject(Door);
            _ = Cache[unchecked((int)Pick)];
            _ = Cache[unchecked((int)Kit)];
            Cache.Tick();
            if (lockpick)
                S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Pick), Name = "Plain Lockpick", Type = "Lockpick" });
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public int Busy { set => Set("_busyCount", value); }
        public string State => Get<object>("_doorState").ToString()!;

        /// <summary>One door tick at the current time; true while the door holds nav.</summary>
        public bool Tick() => (bool)typeof(RynthAiPlugin).GetMethod("TickDoorInteraction", Any)!.Invoke(P, null)!;

        /// <summary>Ticks every <paramref name="stepMs"/> for <paramref name="ms"/>.</summary>
        public void Run(long ms, long stepMs = 50)
        {
            for (long t = 0; t < ms; t += stepMs) { Now += stepMs; Tick(); }
        }

        public void Answer(bool open)
        {
            if (open) FakeHost.ObjectStates[Door] = Closed | Ethereal;
            FakeHost.UseDone(0);
        }

        public void Say(string line) => P.NoteDoorServerText(line);
    }

    private static int DoorUses() => FakeHost.Uses.Count(u => u == Door);
    private static int Picks() => FakeHost.UsesOn.Count(u => u.Source == Pick && u.Target == Door);

    private static void Cleanup()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        UseAudit.Reset("?", null);
        UseAudit.NowMs = () => Environment.TickCount64;
        UseAudit.TrackWorldUses = false;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
        RynthAiPlugin.DoorClock = static () => Environment.TickCount64;
    }

    private static void With(Action body)
    {
        try { body(); }
        finally { Cleanup(); }
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    private static void WaitsForBusyAndAnswer() => With(() =>
    {
        var b = new Bot();
        // A kit the server hasn't answered yet, and the client's busy count up.
        ActionPacer.Begin(b.Host, PacedKind.ItemUse, Kit, "'Treated Healing Kit'", "RynthAi/Buff", FakeHost.UseDoneSeq);
        b.Busy = 1;
        Check.True(b.Tick(), "targeting the door holds nav");
        b.Run(1000);
        Check.Eq(DoorUses(), 0, "no door use while the kit is unanswered (and the client busy)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Door: 0x701C9139 waits:")), "the wait is logged");

        FakeHost.UseDone(0);   // the kit's answer
        b.Run(500);
        Check.Eq(DoorUses(), 0, "the kit answered, but the client still reads busy");
        b.Busy = 0;
        b.Run(100);
        Check.Eq(DoorUses(), 1, "busy 0 and nothing unanswered: the use goes");
        Check.Eq(b.State, "Opening", "...and waits for the server");
    });

    private static void SlowWalkOneUse() => With(() =>
    {
        var b = new Bot();
        b.Tick();
        Check.Eq(FakeHost.Stops, 1, "the character is stopped first");
        Check.Eq(DoorUses(), 0, "...and the use waits for the stop to settle");
        b.Run(300);   // the stop settles
        Check.Eq(DoorUses(), 1, "the use went out once the stop settled");
        b.Run(3700, 100);   // the server walks the character to the door: 3.7 s in the 10-13 log
        Check.Eq(DoorUses(), 1, "no second use during the walk (the old 3 s timeout re-sent it)");
        Check.False(FakeHost.Logs.Any(l => l.Contains("is locked")), "nothing called locked");
        b.Answer(open: true);
        b.Now += 50;
        Check.False(b.Tick(), "opened: nav released");
        Check.True(FakeHost.Logs.Any(l => l.Contains("opened OK")), "opened OK logged");
        Check.Eq(b.State, "None", "back to idle");
        b.Run(2000);
        Check.Eq(DoorUses(), 1, "an opened door is not used again");
    });

    private static void NoDoubleUse() => With(() =>
    {
        var b = new Bot();
        b.Tick();
        b.Run(300);
        Check.Eq(DoorUses(), 1, "first use");
        b.Run(5500, 100);   // no answer at all
        Check.Eq(DoorUses(), 1, "no second use while the first is unanswered (5.8 s)");
        b.Run(1000, 100);   // past the 6 s answer limit
        Check.Eq(DoorUses(), 2, "a use never answered is sent again after the limit");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no answer from the server")), "the lost use is logged");
        Check.False(FakeHost.Logs.Any(l => l.Contains("went out while")), "the pacer never saw a use go out over an unanswered one");
    });

    private static void NotLockedWithoutServer() => With(() =>
    {
        var b = new Bot(lockpick: true);
        b.Tick();
        b.Run(300);
        Check.Eq(DoorUses(), 1, "first use");
        b.Run(2000, 100);
        b.Answer(open: false);   // answered (UseDone 0), the door stays shut, no locked text
        b.Run(1000, 100);
        Check.Eq(DoorUses(), 1, "the door gets a moment to swing after the answer");
        b.Run(500, 100);
        Check.Eq(DoorUses(), 2, "still closed: used again");
        Check.Eq(Picks(), 0, "no lockpick: the server never said locked");
        b.Answer(open: false);
        b.Run(1500, 100);
        b.Answer(open: false);
        b.Run(1500, 100);
        Check.Eq(DoorUses(), 3, "three uses in all");
        Check.Eq(b.State, "Cooldown", "then a cooldown");
        Check.Eq(Picks(), 0, "still no lockpick");
        Check.False(FakeHost.Logs.Any(l => l.Contains("is locked")), "never logged as locked");
    });

    private static void LockedUsesLockpick() => With(() =>
    {
        var b = new Bot(lockpick: true);
        b.Tick();
        b.Run(300);
        Check.Eq(DoorUses(), 1, "first use");
        b.Run(1500, 100);
        b.Say("The door is locked!");   // ACE Door.ActOnUse's transient string
        b.Answer(open: false);
        b.Run(300);
        Check.Eq(Picks(), 1, "the lockpick goes on the door");
        Check.Eq(DoorUses(), 1, "...and no open over it");
        b.Run(1000);
        Check.Eq(DoorUses(), 1, "nothing while the lockpick is unanswered");
        FakeHost.UseDone(0);   // "You have successfully picked the lock!"
        b.Run(300);
        Check.Eq(DoorUses(), 2, "opened again after the lockpick's answer");
        b.Run(800);
        b.Answer(open: true);
        b.Now += 50;
        Check.False(b.Tick(), "opened: nav released");
        Check.Eq(b.State, "None", "done");
    });

    private static void LockedNoPick() => With(() =>
    {
        var b = new Bot(lockpick: false);
        b.Tick();
        b.Run(300);
        b.Run(1000, 100);
        b.Say("The door is locked!");
        b.Answer(open: false);
        b.Run(100);
        Check.Eq(b.State, "Cooldown", "locked, no lockpick: cooldown");
        Check.True(FakeHost.Logs.Any(l => l.Contains("is locked but there's no lockpick")), "says why");
        b.Run(6000, 100);
        Check.Eq(b.State, "None", "cooldown over");
        b.Run(2000, 100);
        Check.Eq(DoorUses(), 1, "the locked door is skipped a while (not targeted again)");
    });

    private static void NothingInPortalSpace() => With(() =>
    {
        var b = new Bot();
        FakeHost.Portaling = true;
        b.Run(2000);
        Check.Eq(b.State, "None", "no door targeted in portal space");
        Check.Eq(DoorUses(), 0, "no use");
        FakeHost.Portaling = false;
        b.Run(1000);
        Check.Eq(b.State, "None", "nor in the first moment after it");
        b.Run(1000);
        Check.Eq(DoorUses(), 1, "then the door is opened as usual");

        // Portal space in the middle of a use: forgotten, not judged.
        FakeHost.Portaling = true;
        b.Run(200);
        Check.Eq(b.State, "None", "a use under way is dropped in portal space");
        Check.False(FakeHost.Logs.Any(l => l.Contains("is locked") || l.Contains(", cooldown")), "never called locked");
    });

    private static void OpensFast() => With(() =>
    {
        var b = new Bot();
        b.Tick();
        b.Run(200);
        Check.Eq(DoorUses(), 1, "use out within 0.2 s of targeting");
        b.Run(300);
        FakeHost.ObjectStates[Door] = Closed | Ethereal;   // swings open before the UseDone arrives
        b.Now += 50;
        Check.False(b.Tick(), "released the tick it reads open");
        Check.Eq(b.State, "None", "done without waiting for anything else");
    });

    // ── Only Doors On My Path (OpenDoorsOnPathOnly) ──────────────────────────────

    private const uint Door2 = 0x701C913A;

    private static int Uses(uint id) => FakeHost.Uses.Count(u => u == id);

    /// <summary>A second closed door at an offset from where the player stands (world units: east, north).</summary>
    private static void AddDoor(Bot b, uint id, float east, float north)
    {
        FakeHost.Names[id] = "Door";
        FakeHost.Positions[id] = (Me0.Cell, Me0.X + east, Me0.Y + north, Me0.Z);
        FakeHost.Bitfields[id] = BfDoor;
        FakeHost.ObjectStates[id] = Closed;
        b.Cache.OnCreateObject(id);
        b.Cache.Tick();
    }

    private static void MoveDoor(uint id, float east, float north) => FakeHost.Positions[id] = (Me0.Cell, Me0.X + east, Me0.Y + north, Me0.Z);

    /// <summary>A route of straight points from the player: each (east, north) offset in world units, as nav coordinates.</summary>
    private static void SetRoute(Bot b, params (float East, float North)[] points)
    {
        double ax = ((Me0.Cell >> 24) & 0xFF) * 192.0 + Me0.X, ay = ((Me0.Cell >> 16) & 0xFF) * 192.0 + Me0.Y;
        var route = new NavRouteParser { RouteType = NavRouteType.Linear };
        foreach (var (e, n) in points)
            route.Points.Add(new NavPoint { Type = NavPointType.Point, EW = ((ax + e) / 24.0 - 1019.5) / 10.0, NS = ((ay + n) / 24.0 - 1019.5) / 10.0 });
        b.S.CurrentRoute = route;
        b.S.ActiveNavIndex = 0;
        b.S.EnableNavigation = true;
    }

    private static void PathOnlyAhead() => With(() =>
    {
        // No route, the character faces north. A door 2 yd east is beside the way.
        FakeHost.HeadingDeg = 0;
        var off = new Bot(); off.S.OpenDoorsOnPathOnly = false;
        off.Tick(); off.Run(600);
        Check.Eq(Uses(Door), 1, "control: with the setting off the door beside the character is opened");

        FakeHost.Uses.Clear();
        var b = new Bot(); b.S.OpenDoorsOnPathOnly = true;
        b.Tick(); b.Run(600);
        Check.Eq(Uses(Door), 0, "with it on, a door 2 yd to the east of a character facing north is left shut");
        Check.Eq(b.State, "None", "and no door is being handled");

        MoveDoor(Door, 0f, 2f);   // now straight ahead
        b.Tick(); b.Run(600);
        Check.Eq(Uses(Door), 1, "a door 2 yd straight ahead is opened");
    });

    private static void PathOnlyRoute() => With(() =>
    {
        // The route runs east. Door2 stands 2.5 yd south of the character (nearer, beside the way); Door is 4 yd ahead on it.
        FakeHost.HeadingDeg = 0;
        MoveDoor(Door, 4f, 0f);
        var off = new Bot(); AddDoor(off, Door2, 0f, -2.5f); MoveDoor(Door, 4f, 0f); SetRoute(off, (12f, 0f));
        off.S.OpenDoorsOnPathOnly = false;
        off.Tick(); off.Run(600);
        Check.Eq(Uses(Door2), 1, "control: with the setting off the nearer door beside the route goes first");

        FakeHost.Uses.Clear();
        var b = new Bot(); AddDoor(b, Door2, 0f, -2.5f); MoveDoor(Door, 4f, 0f); SetRoute(b, (12f, 0f));
        b.S.OpenDoorsOnPathOnly = true;
        b.Tick(); b.Run(600);
        Check.Eq(Uses(Door2), 0, "with it on, the door beside the route is not touched");
        Check.Eq(Uses(Door), 1, "the door 4 yd along the route is opened");
    });

    private static void PathOnlyRange() => With(() =>
    {
        FakeHost.HeadingDeg = 0;
        // 9 yd along the route, range 5: not yet.
        var b = new Bot(); b.S.OpenDoorsOnPathOnly = true; MoveDoor(Door, 9f, 0f); SetRoute(b, (20f, 0f));
        b.Tick(); b.Run(600);
        Check.Eq(Uses(Door), 0, "a door 9 yd along the route is past the 5 yd range");
        // The character walks on: now 3 yd along.
        FakeHost.PlayerPose = (Me0.Cell, Me0.X + 6f, Me0.Y, Me0.Z);
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        b.Tick(); b.Run(600);
        Check.Eq(Uses(Door), 1, "3 yd along it, it is opened");

        // A route going west, the door east of the character: behind the way it is going.
        FakeHost.Uses.Clear();
        var c = new Bot(); c.S.OpenDoorsOnPathOnly = true; MoveDoor(Door, 3f, 0f); SetRoute(c, (-12f, 0f));
        c.Tick(); c.Run(600);
        Check.Eq(Uses(Door), 0, "a door 3 yd east of a route going west is not on the path");
    });

    private static void LockedChatLine() => With(() =>
    {
        var b = new Bot(lockpick: true);
        b.Tick();
        b.Run(300);
        b.Run(1000, 100);
        var chat = typeof(RynthAiPlugin).GetMethod("OnChatWindowText", Any)!;
        object?[] args = { "Bob says, \"The door is locked!\"", 2, 0 };
        try { chat.Invoke(b.P, args); } catch { }   // the rest of the chat pipeline isn't set up here
        b.Answer(open: false);
        b.Run(200);
        Check.Eq(Picks(), 0, "a player's line isn't the server's");
        b.Run(1500, 100);
        args = new object?[] { "The door is locked!", 0, 0 };
        try { chat.Invoke(b.P, args); } catch { }
        b.Run(100);
        b.Answer(open: false);
        b.Run(300);
        Check.Eq(Picks(), 1, "the server's line from chat sends the lockpick");
    });
}
