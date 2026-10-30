namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Names of the creature types (PropertyInt 2 CreatureType on a monster, PropertyInt 166
/// SlayerCreatureType on a weapon), in ACE's ACE.Entity.Enum.CreatureType order.
/// </summary>
internal static class CreatureTypeNames
{
    private static readonly string[] Names =
    {
        "Invalid", "Olthoi", "Banderling", "Drudge", "Mosswart", "Lugian", "Tumerok", "Mite", "Tusker",
        "Phyntos Wasp", "Rat", "Auroch", "Cow", "Golem", "Undead", "Gromnie", "Reedshark", "Armoredillo",
        "Fae", "Virindi", "Wisp", "Knathtead", "Shadow", "Mattekar", "Mumiyah", "Rabbit", "Sclavus",
        "Shallows Shark", "Monouga", "Zefir", "Skeleton", "Human", "Shreth", "Chittick", "Moarsman",
        "Olthoi Larvae", "Slithis", "Deru", "Fire Elemental", "Snowman", "Unknown", "Bunny",
        "Lightning Elemental", "Rockslide", "Grievver", "Niffis", "Ursuin", "Crystal", "Hollow Minion",
        "Scarecrow", "Idol", "Empyrean", "Hopeslayer", "Doll", "Marionette", "Carenzi", "Siraluun",
        "Aun Tumerok", "Hea Tumerok", "Simulacrum", "Acid Elemental", "Frost Elemental", "Elemental",
        "Statue", "Wall", "Altered Human", "Device", "Harbinger", "Dark Sarcophagus", "Chicken",
        "Gotrok Lugian", "Margul", "Bleached Rabbit", "Nasty Rabbit", "Grimacing Rabbit", "Burun",
        "Target", "Ghost", "Fiun", "Eater", "Penguin", "Ruschk", "Thrungus", "Viamontian Knight",
        "Remoran", "Swarm", "Moar", "Enchanted Arms", "Sleech", "Mukkir", "Merwart", "Food",
        "Paradox Olthoi", "Harvest", "Energy", "Apparition", "Aerbax", "Touched", "Blighted Moarsman",
        "Gear Knight", "Gurog", "Anekshay",
    };

    /// <summary>The type's name ("type N" for one this table doesn't know).</summary>
    public static string Name(int type) =>
        type > 0 && type < Names.Length ? Names[type] : "type " + type;
}
