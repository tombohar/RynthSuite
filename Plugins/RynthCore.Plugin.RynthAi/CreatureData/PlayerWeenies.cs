namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Weenie class ids that only player characters use, so they are never monster data: the
/// Damage tab, monster_damage.txt and creatures.json skip them, and the stores drop rows
/// already saved under them when they load.
///
/// On ACE every player character is weenie 1 ("human") whatever the heritage (heritage is a
/// property), and admin / sentinel characters ("+Name") are weenie 4. creatures.json had
/// ~130 player characters under these two (appraisals from before e52ccc8), and no monster in
/// the shipped creature_weakness.json uses either.
/// </summary>
internal static class PlayerWeenies
{
    public const uint Human = 1;
    public const uint Admin = 4;

    public static bool IsPlayerWcid(uint wcid) => wcid == Human || wcid == Admin;

    /// <summary>
    /// Player object ids (ACE's player guid range, 0x50000001-0x6FFFFFFF). Monsters are
    /// dynamic (0x8xxxxxxx) or landblock statics (0x7xxxxxxx), never in this range.
    /// </summary>
    public static bool IsPlayerGuid(uint objectId) => objectId >= 0x50000000u && objectId < 0x70000000u;
}
