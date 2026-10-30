using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.TrackerSessionTests;

// KillCounter (RynthTracker 0.3.0): one cast, several kills. The events are what the client
// sees, in the order it sees them: our damage lines (one per monster a spell hurt), the
// server's kill notices, and corpses ("Corpse of X") a few seconds later.
internal static partial class Program
{
    private static readonly DateTime T0 = new(2026, 10, 3, 2, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private static void Hit(KillCounter k, string line, double t)
    {
        Check(CombatText.TryParseOutgoingHit(line, out string victim, out _), $"parses: {line}");
        k.OnHit(victim, At(t));
    }

    private static void TestKillCounter()
    {
        Console.WriteLine("-- kill counter");

        // ACE ring: every kill gets its notice; the corpses that follow add nothing.
        {
            var k = new KillCounter();
            Hit(k, "You blast Drudge Skulker for 80 points with Flame Ring VI.", 0.0);
            Hit(k, "You blast Drudge Slinker for 75 points with Flame Ring VI.", 0.1);
            Hit(k, "You blast Mosswart Worshipper for 70 points with Flame Ring VI.", 0.2);
            k.OnKillNotice("Drudge Skulker's seared corpse smolders before you!", At(2.0));
            k.OnKillNotice("You bring Drudge Slinker to a fiery end!", At(2.1));
            k.OnKillNotice("Mosswart Worshipper is incinerated by your assault!", At(2.2));
            Eq(k.OnCorpse("Drudge Skulker", At(5.0)), KillCounter.CorpseResult.Notified, "ring: corpse 1 matches its notice");
            Eq(k.OnCorpse("Drudge Slinker", At(5.1)), KillCounter.CorpseResult.Notified, "ring: corpse 2 matches its notice");
            Eq(k.OnCorpse("Mosswart Worshipper", At(5.2)), KillCounter.CorpseResult.Notified, "ring: corpse 3 matches its notice");
            Eq(k.Kills, 3, "ring: three kills, none counted twice");
        }

        // Conquest chain: the primary's kill has a notice, the extra targets' don't.
        {
            var k = new KillCounter();
            Hit(k, "You blast Drudge Skulker for 120 points with Lightning Chain VI.", 0.0);
            Hit(k, "You blast Drudge Slinker for 60 points with Lightning Chain VI.", 0.2);
            Hit(k, "You blast Mosswart Worshipper for 30 points with Lightning Chain VI.", 0.4);
            k.OnKillNotice("You smite Drudge Skulker mightily!", At(2.0));        // the primary
            Hit(k, "You blast Mosswart Worshipper for 30 points with Lightning Chain VI.", 2.2);
            // the Slinker died on that cast with no line and no notice
            Eq(k.OnCorpse("Drudge Skulker", At(5.0)), KillCounter.CorpseResult.Notified, "chain: the primary's corpse is its notice's");
            Eq(k.OnCorpse("Drudge Slinker", At(5.1)), KillCounter.CorpseResult.Counted, "chain: the silent extra kill counts from its corpse");
            Hit(k, "You blast Mosswart Worshipper for 30 points with Lightning Chain VI.", 6.0);
            Eq(k.OnCorpse("Mosswart Worshipper", At(9.0)), KillCounter.CorpseResult.Counted, "chain: the last extra target too");
            Eq(k.Kills, 3, "chain: three kills from the chain fight");
            Eq(k.CorpseKills, 2, "chain: two of them found by their corpse");
        }

        // A swarm: same-named monsters, two die from one cast, one notice.
        {
            var k = new KillCounter();
            Hit(k, "You blast Olthoi Swarm Soldier for 100 points with Lightning Chain VI.", 0.0);
            Hit(k, "You blast Olthoi Swarm Soldier for 100 points with Lightning Chain VI.", 0.1);
            k.OnKillNotice("Olthoi Swarm Soldier's death is preceded by a sharp, stabbing pain!", At(1.5));
            Eq(k.OnCorpse("Olthoi Swarm Soldier", At(4.0)), KillCounter.CorpseResult.Notified, "swarm: first corpse takes the notice");
            Eq(k.OnCorpse("Olthoi Swarm Soldier", At(4.1)), KillCounter.CorpseResult.Counted, "swarm: second corpse counts");
            Eq(k.OnCorpse("Olthoi Swarm Soldier", At(4.2)), KillCounter.CorpseResult.HitNotCounted, "swarm: never more corpses than the volley hit");
            Eq(k.Kills, 2, "swarm: two kills");
        }

        // The primary's kill notice and an extra target's line in one volley, same name: the
        // notice stands for the primary, so the silent twin still counts from its corpse.
        {
            var k = new KillCounter();
            Hit(k, "You blast Olthoi Swarm Soldier for 100 points with Lightning Chain VI.", 0.0);   // earlier cast, primary
            k.OnKillNotice("You slay Olthoi Swarm Soldier viciously enough to impart death several times over!", At(2.0));
            Hit(k, "You blast Olthoi Swarm Soldier for 60 points with Lightning Chain VI.", 2.1);    // the twin, same cast
            Eq(k.OnCorpse("Olthoi Swarm Soldier", At(5.0)), KillCounter.CorpseResult.Notified, "notice + twin: primary's corpse");
            Hit(k, "You blast Olthoi Swarm Soldier for 60 points with Lightning Chain VI.", 5.5);    // a lone hit later on the twin
            Eq(k.OnCorpse("Olthoi Swarm Soldier", At(8.0)), KillCounter.CorpseResult.Counted, "notice + twin: the twin counts");
            Eq(k.Kills, 2, "notice + twin: two kills");
        }

        // Several casts at the same two monsters are still two monsters (max, not sum).
        {
            var k = new KillCounter();
            for (int cast = 0; cast < 4; cast++)
            {
                Hit(k, "You blast Drudge Skulker for 10 points with Flame Ring VI.", cast * 2.0);
                Hit(k, "You blast Drudge Skulker for 10 points with Flame Ring VI.", cast * 2.0 + 0.1);
            }
            Eq(k.OnCorpse("Drudge Skulker", At(9.0)), KillCounter.CorpseResult.Counted, "repeat casts: first corpse");
            Eq(k.OnCorpse("Drudge Skulker", At(9.1)), KillCounter.CorpseResult.Counted, "repeat casts: second corpse");
            Eq(k.OnCorpse("Drudge Skulker", At(9.2)), KillCounter.CorpseResult.HitNotCounted, "repeat casts: not a third");
        }

        // Single-target fighting: a corpse with no notice is someone else's kill, as before.
        {
            var k = new KillCounter();
            Hit(k, "You slash Banderling Scout for 12 points of slashing damage!", 0.0);
            Hit(k, "You slash Banderling Scout for 14 points of slashing damage!", 1.5);
            Eq(k.OnCorpse("Banderling Scout", At(4.0)), KillCounter.CorpseResult.HitNotCounted, "single-target: not counted without a notice");
            Eq(k.OnCorpse("Tusker Guard", At(4.0)), KillCounter.CorpseResult.NotOurs, "a corpse we never hit");
            Eq(k.Kills, 0, "single-target: no kills");
        }

        // Lines a second apart are separate casts (a melee swing each), not one volley.
        {
            var k = new KillCounter();
            Hit(k, "You slash Banderling Scout for 12 points of slashing damage!", 0.0);
            Hit(k, "You slash Banderling Guard for 14 points of slashing damage!", 1.0);
            Eq(k.OnCorpse("Banderling Guard", At(3.0)), KillCounter.CorpseResult.HitNotCounted, "a second apart is not multi-target");
        }

        // Expiry: a multi-target hit stops vouching for a corpse after TokenLifeMs.
        {
            var k = new KillCounter();
            Hit(k, "You blast Drudge Skulker for 10 points with Flame Ring VI.", 0.0);
            Hit(k, "You blast Drudge Slinker for 10 points with Flame Ring VI.", 0.1);
            Eq(k.OnCorpse("Drudge Slinker", At(0.1 + KillCounter.TokenLifeMs / 1000.0 + 1)), KillCounter.CorpseResult.NotOurs, "an old hit doesn't count");
        }

        // A notice waits NoticeLifeMs for its corpse, then is forgotten (the kill stays counted).
        {
            var k = new KillCounter();
            k.OnKillNotice("You smite Drudge Skulker mightily!", At(0));
            Eq(k.OnCorpse("Drudge Skulker", At(KillCounter.NoticeLifeMs / 1000.0 + 1)), KillCounter.CorpseResult.NotOurs, "an old notice is gone");
            Eq(k.Kills, 1, "the notice's kill stays counted");
            k.Reset();
            Eq(k.Kills, 0, "reset clears the count");
        }
    }
}
