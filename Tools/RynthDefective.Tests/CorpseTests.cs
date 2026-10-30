using System;
using System.IO;
using RynthCore.Plugin.RynthDefective.Corpses;

namespace RynthDefective.Tests
{
    internal static partial class Program
    {
        private static readonly DateTime T0 = new(2026, 10, 23, 22, 0, 0, DateTimeKind.Utc);

        // An outdoor landcell: landblock 0xA9B4 (Holtburg), outdoor cell 0x0016; a dungeon one: 0x6146 cell 0x0258.
        private const uint Outdoor = 0xA9B40016u, Dungeon = 0x61460258u;

        private static void CorpseLinesParse()
        {
            Check(CorpseLines.TryLocated("Your corpse is located at (42.1N, 33.6E).", out double ns, out double ew) && ns == 42.1 && ew == 33.6, "N/E");
            Check(CorpseLines.TryLocated("Your corpse is located at (0.5S, 12.0W).", out ns, out ew) && ns == -0.5 && ew == -12.0, "S/W");
            Check(CorpseLines.IsCorpseLine("Your corpse is located at (42.1N, 33.6E)."), "a corpse line");
            Check(CorpseLines.IsNothingDropped("You have retained all your items. You do not need to recover your corpse!"), "nothing dropped");
            Check(!CorpseLines.IsCorpseLine("Corpse of Olthoi Slayer"), "a corpse's name isn't a line");
        }

        private static void CorpseDeaths()
        {
            var b = new CorpseBook();
            b.Died(T0, Outdoor, 100f, 50f, 20f, level: 30);
            Eq(b.Corpses.Count, 1, "a death is a corpse");
            Eq(b.Corpses[0].RotsUtc, T0.AddSeconds(30 * 300), "rots after level x 5 min");
            b.ServerLine(T0.AddSeconds(1), 42.1, 33.6, 30);
            Eq(b.Corpses.Count, 1, "the server's line is the same death");
            Check(b.Corpses[0].ServerCoords && b.Corpses[0].Ns == 42.1 && b.Corpses[0].Cell == Outdoor, "the server's coordinates added to the place");

            // The line first, then the health.
            var b2 = new CorpseBook();
            b2.ServerLine(T0, 42.1, 33.6, 5);
            Check(b2.Corpses.Count == 1 && b2.Corpses[0].Cell == 0, "the line alone: map coordinates only");
            Eq(b2.Corpses[0].RotsUtc, T0.AddHours(1), "low level: an hour");
            b2.Died(T0.AddSeconds(1), Outdoor, 100f, 50f, 20f, 5);
            Check(b2.Corpses.Count == 1 && b2.Corpses[0].Cell == Outdoor, "the death fills in the place");

            // Nothing dropped, after and before the death.
            var b3 = new CorpseBook();
            b3.Died(T0, Dungeon, 10f, 20f, -6f, 100);
            b3.NothingDropped(T0.AddSeconds(1));
            Eq(b3.Corpses.Count, 0, "nothing dropped: no corpse to note");
            b3.NothingDropped(T0.AddMinutes(10));
            b3.Died(T0.AddMinutes(10).AddSeconds(1), Dungeon, 10f, 20f, -6f, 100);
            Eq(b3.Corpses.Count, 0, "the line before the health: still nothing noted");
            b3.Died(T0.AddMinutes(20), Dungeon, 10f, 20f, -6f, 100);
            Eq(b3.Corpses.Count, 1, "a later death is noted again");

            // Two deaths, minutes apart: two corpses, the newest first.
            var b4 = new CorpseBook();
            b4.Died(T0, Outdoor, 100f, 50f, 20f, 50);
            b4.Died(T0.AddMinutes(5), Dungeon, 10f, 20f, -6f, 50);
            b4.ServerLine(T0.AddMinutes(5).AddSeconds(1), 1, 1, 50);
            Check(b4.Corpses.Count == 2 && b4.Corpses[0].Cell == Dungeon && !b4.Corpses[1].ServerCoords, "the line goes to the latest death only");
        }

        private static void CorpseSeen()
        {
            var b = new CorpseBook();
            b.Died(T0, Dungeon, 10f, 20f, -6f, 100);
            CorpseRecord r = b.Seen(T0.AddMinutes(3), 0x80001234u, Dungeon, 11f, 21f, -6f);
            Check(b.Corpses.Count == 1 && r.ObjectId == 0x80001234u && r.X == 11f, "the corpse seen is the death next to it");
            Eq(r.DiedUtc, T0, "it keeps the death's time");
            b.Seen(T0.AddMinutes(4), 0x80001234u, Dungeon, 11f, 21f, -6f);
            Eq(b.Corpses.Count, 1, "seen again by id");

            // Another dungeon, and the same dungeon far away: found corpses of their own.
            b.Seen(T0.AddMinutes(5), 0x80005555u, 0x01E90100u, 1f, 1f, 0f);
            Eq(b.Corpses.Count, 2, "another dungeon: a corpse found");
            Check(b.Corpses[1].DiedUtc == DateTime.MinValue || b.Corpses[0].DiedUtc == DateTime.MinValue, "found: no death time");
            var b2 = new CorpseBook();
            b2.Died(T0, Dungeon, 10f, 20f, -6f, 100);
            b2.Seen(T0.AddMinutes(1), 0x80000001u, Dungeon, 10f + 80f, 20f, -6f);
            Eq(b2.Corpses.Count, 2, "80 m off: not that death");
            // Two corpses near a death (as on the test client): the one 30 m off is found, the one beside it is the death.
            var b2b = new CorpseBook();
            b2b.Died(T0, Dungeon, 30f, -202.8f, -24f, 100);
            b2b.Seen(T0.AddSeconds(3), 0x8000383Au, Dungeon, 48.2f, -178.9f, -18f);
            b2b.Seen(T0.AddSeconds(3), 0x80003841u, Dungeon, 28.3f, -204.8f, -24f);
            CorpseRecord death = b2b.Corpses[0].DiedUtc == T0 ? b2b.Corpses[0] : b2b.Corpses[1];
            Check(b2b.Corpses.Count == 3 - 1 && death.ObjectId == 0x80003841u, "the death goes to the corpse beside it, not the first seen");

            // Outdoors, the server's coordinates only: matched by map distance.
            var b3 = new CorpseBook();
            CorpseBook.MapCoords(Outdoor, 100f, 50f, out double ns, out double ew);
            b3.ServerLine(T0, Math.Round(ns, 1), Math.Round(ew, 1), 100);
            b3.Seen(T0.AddMinutes(2), 0x80000002u, Outdoor, 100f, 50f, 20f);
            Check(b3.Corpses.Count == 1 && b3.Corpses[0].Cell == Outdoor && b3.Corpses[0].ObjectId == 0x80000002u, "matched to the server's line");
        }

        private static void CorpseGoneAndExpiry()
        {
            // Leaving view: gone only when opened, due to rot, or right beside you.
            var v = new CorpseRecord { DiedUtc = T0, RotsUtc = T0.AddHours(5), Cell = Dungeon, ObjectId = 1 };
            Check(!CorpseBook.VanishMeansGone(v, 30, T0.AddMinutes(20)), "30 m off, not opened: only out of sight");
            Check(CorpseBook.VanishMeansGone(v, 8, T0.AddMinutes(20)), "beside you: gone");
            var vb = new CorpseBook();
            vb.Seen(T0, 0x80000010u, Dungeon, 10f, 20f, -6f);
            vb.Opened(0x80000010u, T0.AddMinutes(1));
            Check(CorpseBook.VanishMeansGone(vb.Corpses[0], 30, T0.AddMinutes(2)), "opened: its vanishing is the looting");
            Check(!CorpseBook.VanishMeansGone(vb.Corpses[0], 30, T0.AddMinutes(30)), "opened long ago: no longer");
            Check(CorpseBook.VanishMeansGone(v, 500, T0.AddHours(5).AddMinutes(-2)), "due to rot: gone");

            // Standing at its place without it there.
            var n = new CorpseRecord { DiedUtc = T0, Cell = Dungeon, ObjectId = 5 };
            Check(!CorpseBook.NotThere(n, 3, false, T0.AddSeconds(30)) && !CorpseBook.NotThere(n, 3, false, T0.AddSeconds(50)), "not in the first 2 minutes");
            Check(!CorpseBook.NotThere(n, 3, false, T0.AddMinutes(3)), "missing: the timer starts");
            Check(!CorpseBook.NotThere(n, 3, false, T0.AddMinutes(3).AddSeconds(5)), "5 s: not yet");
            Check(!CorpseBook.NotThere(n, 3, true, T0.AddMinutes(3).AddSeconds(6)), "in view: the timer stops");
            Check(!CorpseBook.NotThere(n, 3, false, T0.AddMinutes(3).AddSeconds(7)) && CorpseBook.NotThere(n, 3, false, T0.AddMinutes(3).AddSeconds(17)), "10 s missing: dropped");
            var far = new CorpseRecord { DiedUtc = T0, Cell = Dungeon };
            Check(!CorpseBook.NotThere(far, 30, false, T0.AddMinutes(3)) && !CorpseBook.NotThere(far, 30, false, T0.AddMinutes(4)), "30 m off: never");

            var b = new CorpseBook();
            b.Died(T0, Dungeon, 10f, 20f, -6f, 12);   // rots after an hour (level 12 x 5 min is less)
            b.Seen(T0.AddMinutes(1), 0x80001234u, Dungeon, 10f, 20f, -6f);
            Check(!b.Gone(0x80009999u) && b.Gone(0x80001234u) && b.Corpses.Count == 0, "gone by its id");

            b.Died(T0, Dungeon, 10f, 20f, -6f, 12);
            Check(!b.Expire(T0.AddMinutes(65)), "kept through the grace after its rot time");
            Check(b.Expire(T0.AddMinutes(71)) && b.Corpses.Count == 0, "dropped 10 min after it");

            b.Seen(T0, 0x80000003u, Dungeon, 10f, 20f, -6f);
            Check(!b.Expire(T0.AddHours(47)) && b.Expire(T0.AddHours(49)), "a found corpse: 48 h after it was last seen");

            for (int i = 0; i < CorpseBook.MaxCorpses + 5; i++) b.Died(T0.AddMinutes(10 * i), Dungeon, i * 100f, 0f, 0f, 200);
            Eq(b.Corpses.Count, CorpseBook.MaxCorpses, "at most 20");
            Eq(b.Corpses[0].DiedUtc, T0.AddMinutes(10 * (CorpseBook.MaxCorpses + 4)), "the newest first, the oldest dropped");
            Check(b.Forget(b.Corpses[0]) && b.Corpses.Count == CorpseBook.MaxCorpses - 1, "forget");
        }

        private static void CorpseStoreFiles()
        {
            var st = new CorpseStore(Path.Combine(_temp, "corpses-store"));
            var b = new CorpseBook();
            b.Died(T0, Outdoor, 100.25f, 50.5f, 20f, 60);
            b.ServerLine(T0.AddSeconds(1), 42.1, -33.6, 60);
            b.Seen(T0.AddMinutes(1), 0x80001234u, Outdoor, 100.25f, 50.5f, 20f);
            b.Seen(T0.AddMinutes(2), 0x80005678u, Dungeon, 1f, 2f, 3f);
            Check(st.Save("Aelrynth", "Drak/kon", b.Corpses) == null, "saved");
            Check(File.Exists(Path.Combine(_temp, "corpses-store", "corpses", "Aelrynth_Drak_kon.json")), "the file name is safe");
            var back = st.Load("Aelrynth", "Drak/kon", out string? problem);
            Check(problem == null && back.Count == 2, "loaded both");
            CorpseRecord a = back.Find(c => c.ObjectId == 0x80001234u)!;
            Check(a != null && a.DiedUtc == T0 && a.RotsUtc == T0.AddSeconds(60 * 300) && a.Cell == Outdoor && a.X == 100.25f
                  && a.ServerCoords && a.Ns == 42.1 && a.Ew == -33.6, "a death's corpse round-trips");
            CorpseRecord f = back.Find(c => c.ObjectId == 0x80005678u)!;
            Check(f != null && f.DiedUtc == DateTime.MinValue && f.SeenUtc == T0.AddMinutes(2), "a found corpse round-trips");

            st.Save("Aelrynth", "Aryze", new[] { new CorpseRecord { DiedUtc = T0, RotsUtc = T0.AddHours(2), Cell = Dungeon } });
            st.Save("Aelrynth", "Old", new[] { new CorpseRecord { DiedUtc = T0.AddDays(-3), RotsUtc = T0.AddDays(-2), Cell = Dungeon } });
            File.WriteAllText(Path.Combine(_temp, "corpses-store", "corpses", "broken.json"), "{ not json");
            var others = st.LoadOthers("Aelrynth", "Drak/kon", T0.AddMinutes(5));
            Check(others.Count == 1 && others[0].Character == "Aryze" && others[0].Corpses.Count == 1, "others: rotted and broken files skipped, own left out");
            Check(st.Load("Aelrynth", "Nobody", out problem).Count == 0 && problem == null, "no file: no corpses");
        }

        private static void CorpseRowText()
        {
            PlaceNames.LoadForTests("{\"locations\":[{\"name\":\"Matron Hive East\",\"type\":\"Dungeon\",\"ns\":-49.78,\"ew\":61.97,\"cell\":\"CC410000\",\"lb\":\"6146\"},"
                                    + "{\"name\":\"Holtburg\",\"type\":\"Town\",\"ns\":42.1,\"ew\":33.6,\"cell\":\"A9B40016\"}]}");
            var dungeon = new CorpseRecord { DiedUtc = T0, RotsUtc = T0.AddHours(3), Cell = Dungeon, X = 10f, Y = 20f, Z = -6f };
            Eq(CorpseRows.Place(dungeon), "Matron Hive East", "a dungeon by its name");
            Eq(CorpseRows.Place(new CorpseRecord { Cell = 0x01E90100u }), "Dungeon 01E9", "an unnamed dungeon by its landblock");
            CorpseBook.MapCoords(Outdoor, 100f, 50f, out double ns, out double ew);
            Eq(CorpseRows.Place(new CorpseRecord { Cell = Outdoor, X = 100f, Y = 50f }), CorpseRows.Coords(ns, ew), "outdoors: the map coordinates");
            Eq(CorpseRows.Place(new CorpseRecord { Cell = 0xA9B40105u, X = 100f, Y = 50f }), CorpseRows.Coords(ns, ew) + " (indoors)", "a building in town");
            Eq(CorpseRows.Coords(-49.78, 61.97), "49.8S, 62.0E", "coordinates");

            Eq(CorpseRows.Span(TimeSpan.FromSeconds(40)), "40s", "seconds");
            Eq(CorpseRows.Span(TimeSpan.FromMinutes(25.5)), "25m", "minutes");
            Eq(CorpseRows.Span(TimeSpan.FromMinutes(190)), "3h 10m", "hours");
            Eq(CorpseRows.Span(TimeSpan.FromHours(52)), "2d 4h", "days");
            Eq(CorpseRows.Age(dungeon, T0.AddMinutes(25)), "died 25m ago", "age");
            Eq(CorpseRows.Rot(dungeon, T0.AddMinutes(25)), "rots in ~2h 35m", "rot estimate");
            Eq(CorpseRows.Rot(dungeon, T0.AddHours(4)), "may have rotted", "past its time");

            // Standing in the same dungeon 30 m south of it, facing north: ahead, 30 yd N.
            var me = new PlayerSpot(Dungeon, 10f, -10f, -6f, 0);
            CorpsesView v = CorpseRows.Build(new[] { dungeon }, Array.Empty<OtherCorpses>(), me, T0.AddMinutes(25));
            Eq(v.Mine[0].Detail, "died 25m ago · rots in ~2h 35m · 30 yd N", "the detail line");
            Check(MathF.Abs(v.Mine[0].Relative) < 0.01f, "the arrow points ahead");
            // Facing east: it's to the left.
            v = CorpseRows.Build(new[] { dungeon }, Array.Empty<OtherCorpses>(), me with { FacingDeg = 90 }, T0.AddMinutes(25));
            Check(MathF.Abs(v.Mine[0].Relative + 90f) < 0.01f, "the arrow points left");
            // Another dungeon: no distance, no arrow.
            v = CorpseRows.Build(new[] { dungeon }, Array.Empty<OtherCorpses>(), new PlayerSpot(0x01E90100u, 1, 1, 0, 0), T0.AddMinutes(25));
            Check(v.Mine[0].Detail == "died 25m ago · rots in ~2h 35m" && float.IsNaN(v.Mine[0].Relative), "elsewhere: no distance");
            // Another character's: its name first.
            var other = new OtherCorpses("Aelrynth", "Aryze", new[] { dungeon });
            v = CorpseRows.Build(Array.Empty<CorpseRecord>(), new[] { other }, me, T0.AddMinutes(25));
            Check(v.Mine.Count == 0 && v.Others.Count == 1 && v.Others[0].Detail.StartsWith("Aryze · died 25m ago", StringComparison.Ordinal)
                  && v.Others[0].Record == null, "another character's row");
        }
    }
}
