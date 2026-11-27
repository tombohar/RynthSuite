using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-06: Silentkelpie skipped its own kills unopened (Necrolytic Extract left behind). The
// corpse LongDesc had gained "Corpse will decay in 56s" under the "Killed by" line and the killer
// read as "Silentkelpie.\nCorpse will decay in 56s", which never matches the character name.
internal static class CorpseKillerTests
{
    public static void Register(Runner r)
    {
        r.Add("corpse killer: plain 'Killed by X.'", Plain);
        r.Add("corpse killer: decay line after the killer is ignored", DecayLineIgnored);
        r.Add("corpse killer: CRLF and multi-word names", CrLfMultiWord);
        r.Add("corpse killer: no 'Killed by' line reads as unknown", Missing);
        r.Add("corpse killer: radar column reads the same name", RadarMatches);
        r.Add("fellowship kills: a known killer outside the read member list is not looted; unknown killer, a fellow or no list yet still are", FellowModeLoots);
    }

    private static void Plain()
    {
        Check.Eq(RynthAiPlugin.ExtractCorpseKillerName("Killed by Silentkelpie."), "Silentkelpie", "plain line");
        Check.Eq(RynthAiPlugin.ExtractCorpseKillerName("Killed by Silentkelpie"), "Silentkelpie", "no trailing period");
    }

    private static void DecayLineIgnored()
    {
        Check.Eq(RynthAiPlugin.ExtractCorpseKillerName("Killed by Silentkelpie.\nCorpse will decay in 56s"),
            "Silentkelpie", "LF decay line");
        Check.Eq(RynthAiPlugin.ExtractCorpseKillerName("Killed by Silentkelpie.\n\nCorpse will decay in 4m 12s."),
            "Silentkelpie", "blank line then decay line");
    }

    private static void CrLfMultiWord()
    {
        Check.Eq(RynthAiPlugin.ExtractCorpseKillerName("Killed by Moreck II.\r\nCorpse will decay in 3m 10s"),
            "Moreck II", "CRLF and a roman-numeral suffix");
    }

    private static void Missing()
    {
        Check.Null(RynthAiPlugin.ExtractCorpseKillerName(""), "empty LongDesc");
        Check.Null(RynthAiPlugin.ExtractCorpseKillerName(null), "null LongDesc");
        Check.Null(RynthAiPlugin.ExtractCorpseKillerName("Corpse will decay in 56s"), "decay line only");
        Check.Null(RynthAiPlugin.ExtractCorpseKillerName("Killed by .\nCorpse will decay in 56s"), "empty killer");
    }

    // #rynth-issues 2026-11-25: "Rynth treats all corpses as fellowship kills, both player corpses and monster corpses".
    private static void FellowModeLoots()
    {
        Check.False(RynthAiPlugin.FellowModeLoots("Someone Else", killerIsFellow: false, membersKnown: 4), "someone else's kill, the list read: left");
        Check.False(RynthAiPlugin.FellowModeLoots("Mite Defiler", killerIsFellow: false, membersKnown: 4), "a player's death corpse (a monster killed them): left");
        Check.True(RynthAiPlugin.FellowModeLoots("A Fellow", killerIsFellow: true, membersKnown: 4), "a fellow's kill: looted");
        Check.True(RynthAiPlugin.FellowModeLoots("", killerIsFellow: false, membersKnown: 4), "killer not known yet: looted pending the ID");
        Check.True(RynthAiPlugin.FellowModeLoots("Someone Else", killerIsFellow: false, membersKnown: 0), "member list not read yet: the server decides, as before");
    }

        private static void RadarMatches()
    {
        Check.Eq(RynthRadarUi.ExtractKiller("Killed by Silentkelpie.\nCorpse will decay in 56s"), "Silentkelpie", "radar decay line");
        Check.Eq(RynthRadarUi.ExtractKiller(null), "", "radar unknown is empty");
    }
}
