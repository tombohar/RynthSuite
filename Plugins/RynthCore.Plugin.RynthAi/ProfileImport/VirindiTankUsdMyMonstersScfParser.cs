// Line-oriented uTank2 SCF .usd MyMonsters table — logic mirrors Tools/RynthCore.MonsterEditor/VirindiTankUsdTextScfParser.cs
// for LegacyUi.MonsterRule. If VT changes the SCF layout, update both files together.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.ProfileImport;

/// <summary>Parses VirindiTank text-format <c>.usd</c> files that contain a <c>MyMonsters</c> SCF block.</summary>
internal static class VirindiTankUsdMyMonstersScfParser
{
    /// <summary>Returns false if the file is not SCF MyMonsters (e.g. SQLite binary profile).</summary>
    public static bool TryParseMyMonsters(string allText, List<MonsterRule> outList, out string? note)
    {
        outList.Clear();
        note = null;
        if (string.IsNullOrWhiteSpace(allText) || allText.IndexOf("MyMonsters", StringComparison.Ordinal) < 0)
            return false;

        string[] L = allText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(s => s.Trim())
            .ToArray();
        if (L.Length < 8)
        {
            note = "File too short.";
            return false;
        }

        int idx = 0;
        for (; idx < L.Length; idx++)
            if (L[idx] == "MyMonsters")
                break;
        if (idx >= L.Length)
        {
            note = "MyMonsters not found.";
            return false;
        }

        idx++;
        if (!int.TryParse(L[idx], out int colCount) || colCount < 1)
        {
            note = "MyMonsters: bad column count.";
            return false;
        }

        idx += 1 + colCount * 2;
        if (idx >= L.Length)
        {
            note = "MyMonsters: truncated after header.";
            return false;
        }

        if (!int.TryParse(L[idx], out int rowCount) || rowCount < 0)
        {
            note = "MyMonsters: bad row count.";
            return false;
        }

        idx++;
        if (rowCount == 0)
        {
            note = "MyMonsters has no rows.";
            return false;
        }

        if (colCount < 4 + 3)
        {
            note = "MyMonsters: column count too small.";
            return false;
        }

        int nBool = colCount - 4 - 3;

        for (int r = 0; r < rowCount; r++)
        {
            if (idx + 1 >= L.Length)
            {
                note = $"No data for row {r + 1} (early EOF).";
                return outList.Count > 0;
            }

            if (!T(L, "s", ref idx))
            {
                note = RowErr(r, "s");
                return outList.Count > 0;
            }

            if (!ValStr(L, ref idx, out string raw))
            {
                note = RowErr(r, "name");
                return outList.Count > 0;
            }

            if (!T(L, "i", ref idx))
            {
                note = RowErr(r, "i ap");
                return outList.Count > 0;
            }

            if (!ValInt(L, ref idx, out int ap, out int err) || err != 0)
            {
                note = RowErr(r, "ap int");
                return outList.Count > 0;
            }

            if (!T(L, "i", ref idx))
            {
                note = RowErr(r, "i dt");
                return outList.Count > 0;
            }

            if (!ValInt(L, ref idx, out int vtDmg, out err) || err != 0)
            {
                note = RowErr(r, "dt int");
                return outList.Count > 0;
            }

            if (!T(L, "i", ref idx))
            {
                note = RowErr(r, "i wep");
                return outList.Count > 0;
            }

            if (!ValInt(L, ref idx, out int wep, out err) || err != 0)
            {
                note = RowErr(r, "wep int");
                return outList.Count > 0;
            }

            if (nBool < 14)
            {
                note = "Expected at least 14 bool columns in mob row.";
                return outList.Count > 0;
            }

            if (nBool < 1)
            {
                note = "nBool<1.";
                return false;
            }

            var b = new bool[nBool];
            for (int k = 0; k < nBool; k++)
            {
                if (!T(L, "b", ref idx) || !ValBool(L, ref idx, out b[k]))
                {
                    note = RowErr(r, $"b{k}");
                    return outList.Count > 0;
                }
            }

            if (!T(L, "i", ref idx) || !ValInt(L, ref idx, out int sev, out err) || err != 0)
            {
                note = RowErr(r, "sec vuln int");
                return outList.Count > 0;
            }

            if (!T(L, "i", ref idx) || !ValInt(L, ref idx, out int ofh, out err) || err != 0)
            {
                note = RowErr(r, "offh int");
                return outList.Count > 0;
            }

            if (!T(L, "i", ref idx) || !ValInt(L, ref idx, out int pet, out err) || err != 0)
            {
                note = RowErr(r, "pet int");
                return outList.Count > 0;
            }

            outList.Add(MakeRule(raw, ap, vtDmg, wep, b, sev, ofh, pet));
        }

        if (outList.Count == 0)
        {
            note = "No rows were parsed from MyMonsters.";
            return false;
        }

        return true;
    }

    private static string RowErr(int r, string what) => $"MyMonsters row {r + 1} ({what}).";

    private static bool T(string[] L, string t, ref int i)
    {
        if (i >= L.Length || L[i] != t)
            return false;
        i++;
        return true;
    }

    private static bool ValStr(string[] L, ref int i, out string s)
    {
        s = "";
        if (i >= L.Length)
            return false;
        s = L[i++];
        return true;
    }

    private static bool ValInt(string[] L, ref int i, out int v, out int err)
    {
        err = 0;
        v = 0;
        if (i >= L.Length)
        {
            err = 1;
            return false;
        }

        if (!int.TryParse(L[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) &&
            !int.TryParse(L[i], out v))
        {
            err = 1;
            return false;
        }

        i++;
        return true;
    }

    private static bool ValBool(string[] L, ref int i, out bool b)
    {
        b = false;
        if (i >= L.Length)
            return false;
        string t = L[i++];
        if (string.Equals(t, "True", StringComparison.OrdinalIgnoreCase))
        {
            b = true;
            return true;
        }

        if (string.Equals(t, "False", StringComparison.OrdinalIgnoreCase))
        {
            b = false;
            return true;
        }

        return false;
    }

    private static MonsterRule MakeRule(string raw, int ap, int vtDmg, int wep, bool[] b, int sev, int ofh, int pet)
    {
        string n = Nm(raw);
        if (b.Length < 14)
            b = Pad(b, 14);
        bool i = b[0], v = b[1], y = b[2], gW = b[3];
        bool useBolt = b[4], useRing = b[5], broad = b[6], fest = b[7];
        bool strk = b[13];

        return new MonsterRule
        {
            Name = n,
            Priority = ap,
            DamageType = MapDmg(vtDmg),
            WeaponId = WepId(wep),
            Imperil = i,
            Vuln = v,
            Yield = y,
            GravityWell = gW,
            UseBolt = useBolt,
            UseRing = useRing,
            Broadside = broad,
            Fester = fest,
            UseStreak = strk,
            // b[8..12]: VT's Void columns WC, FC, Cp, DC, Cs.
            WeakeningCurse = b[8],
            FesteringCurse = b[9],
            Corruption = b[10],
            DestructiveCurse = b[11],
            Corrosion = b[12],
            ExVuln = "None",
            OffhandId = WepId(ofh),
            PetDamage = MapPet(pet, sev),
        };
    }

    private static bool[] Pad(bool[] b, int min)
    {
        if (b.Length >= min)
            return b;
        var r = new bool[min];
        for (int i = 0; i < b.Length; i++)
            r[i] = b[i];
        return r;
    }

    private static string Nm(string s)
    {
        s = s.Trim();
        if (s.Length > 1 && s[0] == '<' && s[^1] == '>')
        {
            string t = s.Substring(1, s.Length - 2);
            if (t.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                return "Default";
            return t;
        }

        return s;
    }

    private static int WepId(int w) => w;

    private static string MapDmg(int c) => c switch
    {
        8 => "Auto",
        1 => "Slash",
        2 => "Pierce",
        3 => "Bludgeon",
        4 => "Acid",
        5 => "Fire",
        6 => "Cold",
        7 => "Lightning",
        0 => "Nether",
        _ => "Auto",
    };

    private static string MapPet(int pdt, int _)
    {
        if (pdt is 0 or 101)
            return "PAuto";
        if (pdt is >= 1 and <= 8)
        {
            return pdt switch
            {
                1 => "Slash",
                2 => "Pierce",
                3 => "Bludgeon",
                4 => "Fire",
                5 => "Cold",
                6 => "Lightning",
                7 => "Acid",
                8 => "Nether",
                _ => "PAuto",
            };
        }

        return "PAuto";
    }
}
