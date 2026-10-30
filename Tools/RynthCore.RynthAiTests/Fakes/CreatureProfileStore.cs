using System;
using System.IO;

namespace RynthCore.Plugin.RynthAi.CreatureData;

// Stand-in for the plugin's CreatureProfileStore, which is not linked (it is a disk-backed
// JSON store). CreatureWeakness only reads CreatureProfileStore.Folder, to look for the
// user's creature_types.txt override in C:\Games\RynthSuite\RynthAi\CreatureData. Pointing
// it at a folder that never exists keeps the tests on the shipped data only, so results do
// not depend on what is installed on the machine running them.
internal sealed class CreatureProfileStore
{
    internal static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "no-user-creature-data");
}
