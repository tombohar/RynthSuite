using System;
using System.Globalization;
using System.IO;

namespace RynthCore.Plugin.RynthJuice;

/// <summary>
/// RynthJuice tunables, persisted as a tiny key=value file at
/// %APPDATA%\RynthCore\rynthjuice.cfg. Plain text (no JSON serializer) to stay
/// trivially NativeAOT-safe and hand-editable.
/// </summary>
internal sealed class JuiceSettings
{
    public bool Enabled = true;
    public bool ShowHeals = true;          // green "+N" when a target's health rises
    public bool ShowPlayerDamage = true;   // red numbers over your own character when hit
    public float Scale = 1.0f;             // global size multiplier for numbers
    public float HeightOffset = 1.8f;      // metres above the target origin to anchor numbers
    public float MaxDistance = 55f;        // metres; skip numbers for targets farther than this
    public int MinDamage = 1;              // ignore deltas below this (filters regen ticks)

    private string _path = "";

    public void Load()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");
            _path = Path.Combine(dir, "rynthjuice.cfg");
            if (!File.Exists(_path))
                return;

            foreach (string raw in File.ReadAllLines(_path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                Apply(key, val);
            }
        }
        catch { /* defaults stand */ }
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(_path))
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "rynthjuice.cfg");
            }
            var ic = CultureInfo.InvariantCulture;
            File.WriteAllText(_path,
                "# RynthCore RynthJuice settings\n" +
                $"enabled={(Enabled ? 1 : 0)}\n" +
                $"heals={(ShowHeals ? 1 : 0)}\n" +
                $"playerdamage={(ShowPlayerDamage ? 1 : 0)}\n" +
                $"scale={Scale.ToString("0.##", ic)}\n" +
                $"height={HeightOffset.ToString("0.##", ic)}\n" +
                $"maxdist={MaxDistance.ToString("0.##", ic)}\n" +
                $"mindamage={MinDamage}\n");
        }
        catch { /* non-fatal */ }
    }

    private void Apply(string key, string val)
    {
        var ic = CultureInfo.InvariantCulture;
        switch (key)
        {
            case "enabled":      Enabled = val is "1" or "true"; break;
            case "heals":        ShowHeals = val is "1" or "true"; break;
            case "playerdamage": ShowPlayerDamage = val is "1" or "true"; break;
            case "scale":        if (float.TryParse(val, NumberStyles.Float, ic, out var s)) Scale = Math.Clamp(s, 0.3f, 4f); break;
            case "height":       if (float.TryParse(val, NumberStyles.Float, ic, out var h)) HeightOffset = Math.Clamp(h, 0f, 8f); break;
            case "maxdist":      if (float.TryParse(val, NumberStyles.Float, ic, out var m)) MaxDistance = Math.Clamp(m, 5f, 200f); break;
            case "mindamage":    if (int.TryParse(val, NumberStyles.Integer, ic, out var d)) MinDamage = Math.Max(0, d); break;
        }
    }
}
