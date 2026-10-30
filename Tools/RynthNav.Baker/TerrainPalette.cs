using System.Text;
using RynthCore.TerrainData;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthNav.Baker;

/// <summary>
/// The terrain types of Dereth as the game's Region file (portal.dat 0x13000000) lists them: a name and
/// a colour per type index 0..31 (the index in bits 2-6 of a landblock's terrain words). The table
/// sits behind SkyDesc / SoundDesc / SceneDesc blocks, so rather than walk those it is found by
/// shape: a count, then that many entries of (PString name, uint colour, uint sceneCount, scene ids),
/// every name printable, ending inside the file. Null if no table of that shape turns up.
/// </summary>
internal sealed class TerrainPalette
{
    public string[] Names { get; }
    public uint[] Argb { get; }
    private TerrainPalette(string[] names, uint[] argb) { Names = names; Argb = argb; }

    public static TerrainPalette? Load(string acFolder)
    {
        string? path = new[] { "client_portal.dat", "portal.dat" }.Select(n => Path.Combine(acFolder, n)).FirstOrDefault(File.Exists);
        if (path == null) return null;
        using var dat = new DatDatabase();
        if (!dat.Open(path)) return null;
        byte[]? data = dat.GetFileData(0x13000000);
        return data == null ? null : Find(data);
    }

    private static TerrainPalette? Find(byte[] d)
    {
        for (int start = 12; start + 8 < d.Length; start++)
        {
            uint n = BitConverter.ToUInt32(d, start);
            if (n < 16 || n > 64) continue;
            var t = TryParse(d, start + 4, (int)n);
            if (t != null) return t;
        }
        return null;
    }

    private static TerrainPalette? TryParse(byte[] d, int pos, int n)
    {
        var names = new string[n];
        var cols = new uint[n];
        for (int i = 0; i < n; i++)
        {
            if (pos + 2 > d.Length) return null;
            int len = BitConverter.ToUInt16(d, pos);
            if (len < 3 || len > 40 || pos + 2 + len > d.Length) return null;
            for (int k = 0; k < len; k++) { byte c = d[pos + 2 + k]; if (c < 0x20 || c > 0x7E) return null; }
            names[i] = Encoding.ASCII.GetString(d, pos + 2, len);
            pos += 2 + len;
            pos = (pos + 3) & ~3;                               // PString pads to 4 (the file starts 4-aligned)
            if (pos + 8 > d.Length) return null;
            cols[i] = BitConverter.ToUInt32(d, pos);
            uint scenes = BitConverter.ToUInt32(d, pos + 4);
            if (scenes > 200) return null;
            pos += 8 + (int)scenes * 4;
            if (pos > d.Length) return null;
        }
        return new TerrainPalette(names, cols);
    }
}
