namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Patrol On Login and Hunt On Login (2026-11-11): what starts once the character is in the world.
/// The two are one choice: ticking one clears the other (<see cref="Exclusive"/>). A profile that has
/// both on anyway (edited by hand, or from a sender that only knows one of them) gets the one that
/// fits where the character is: the Dungeon Patrol in a dungeon, the Outdoor Hunt outdoors
/// (<see cref="Choose"/>). Both turn the macro on themselves, so Start Macro On Login isn't needed.
/// Pure, for the tests.
/// </summary>
internal static class LoginAutoStart
{
    internal enum Start { None, Patrol, Hunt }

    internal const string HuntOnLoginTip =
        "Starts the Outdoor Hunt from where you are once you're in the world (and the macro with it).\n" +
        "In a dungeon, or without RynthNav: it doesn't start, and says why in chat.\n" +
        "One or the other: ticking it clears Patrol On Login.";

    /// <summary>What runs at login. <paramref name="inDungeon"/> only matters with both on.</summary>
    internal static Start Choose(bool patrolOnLogin, bool huntOnLogin, bool inDungeon) =>
        patrolOnLogin && huntOnLogin ? (inDungeon ? Start.Patrol : Start.Hunt)
        : patrolOnLogin ? Start.Patrol
        : huntOnLogin ? Start.Hunt
        : Start.None;

    /// <summary>
    /// The two boxes after a change: the one just turned on wins and the other goes off. Both turned
    /// on in one change (no way to tell which was meant): the patrol, as before Hunt On Login existed.
    /// Turning one off leaves the other as it was.
    /// </summary>
    internal static (bool Patrol, bool Hunt) Exclusive(bool wasPatrol, bool wasHunt, bool patrol, bool hunt)
    {
        bool patrolTurnedOn = patrol && !wasPatrol;
        bool huntTurnedOn = hunt && !wasHunt;
        if (patrolTurnedOn) return (true, false);
        if (huntTurnedOn) return (false, true);
        return (patrol, hunt);
    }
}
