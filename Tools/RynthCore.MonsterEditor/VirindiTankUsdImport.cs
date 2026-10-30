using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RynthCore.MonsterEditor;

/// <summary>
/// Best-effort import of VirindiTank "user" macro profiles (typically <c>.usd</c>) into RynthAi
/// <see cref="MonsterRule"/> rows. Tries, in order: (1) line-oriented SCF <c>MyMonsters</c> (classic
/// uTank2 <c>*.usd</c> in <c>VirindiPlugins\VirindiTank</c>); (2) SQLite; (3) binary scan for
/// vTank-style match expressions.
/// </summary>
public static class VirindiTankUsdImport
{
    public sealed class Result
    {
        public bool Ok { get; set; }
        public List<MonsterRule> Rules { get; } = new();
        /// <summary>Human-readable summary: counts, method used, warnings.</summary>
        public string Message { get; set; } = string.Empty;
    }

    public static Result TryImport(string filePath)
    {
        var r = new Result();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            r.Message = "File not found.";
            return r;
        }

        string? allText;
        try
        {
            // VT text profiles are UTF-8; invalid sequences become replacement chars, still OK for SCF.
            allText = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            r.Message = $"Read failed: {ex.Message}";
            return r;
        }

        // Primary path: SCF <c>MyMonsters</c> block (see VirindiPlugins\VirindiTank\*.usd, format 8+).
        if (VirindiTankUsdTextScfParser.TryParseMyMonsters(allText, r.Rules, out string? scfNote) && r.Rules.Count > 0)
        {
            r.Ok = true;
            r.Message = BuildMessage("MyMonsters (VT SCF / line format)", r.Rules.Count, scfNote);
            return r;
        }
        r.Rules.Clear();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex2)
        {
            r.Message = $"Read as bytes failed: {ex2.Message}";
            return r;
        }

        if (IsSqliteDatabase(bytes))
        {
            try
            {
                if (TryImportFromSqlite(filePath, r.Rules, out int count, out string? sqlNote))
                {
                    r.Ok = true;
                    r.Message = BuildMessage("SQLite (Virindi uTank settings database)", count, sqlNote);
                    return r;
                }
            }
            catch (Exception ex)
            {
                const string pfx = "SQLite read error: ";
                string errMsg = pfx + ex.Message;
                if (TryImportFromStringScan(bytes, r.Rules, out int exprCount, out string? scanNote))
                {
                    r.Ok = exprCount > 0;
                    r.Message = BuildMessage("Text scan (after SQLite exception)", exprCount, errMsg + " " + scanNote);
                }
                else
                    r.Message = errMsg;
                return r;
            }
        }

        string? fallNote;
        if (TryImportFromStringScan(bytes, r.Rules, out int n, out fallNote))
        {
            r.Ok = n > 0;
            r.Message = n > 0
                ? BuildMessage("Expression scan (non-SQLite or unreadable table)", n, fallNote)
                : "Could not import: no vTank-style match expressions in file. " + (fallNote ?? string.Empty) +
                  " If this is a Virindi Tank user profile, ensure the file is a .usd from your VirindiPlugins\\VirindiTank folder (SQLite based).";
        }
        else
        {
            r.Message = fallNote ?? "Import failed.";
        }

        return r;
    }

    private static string BuildMessage(string method, int count, string? extra)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Imported {count} rule(s) using {method}.");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            sb.Append(' ');
            sb.Append(extra);
        }
        return sb.ToString();
    }

    private static bool IsSqliteDatabase(byte[] header)
    {
        const string sig = "SQLite format 3\0";
        if (header.Length < sig.Length) return false;
        for (int i = 0; i < sig.Length; i++)
        {
            if (header[i] != sig[i]) return false;
        }
        return true;
    }

    private static bool TryImportFromSqlite(string filePath, List<MonsterRule> outList, out int count, out string? note)
    {
        count = 0;
        note = null;
        outList.Clear();

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource  = filePath,
            Mode        = SqliteOpenMode.ReadOnly,
        };

        using var con = new SqliteConnection(csb.ToString());
        con.Open();

        // Discover tables and their columns; pick the best "monster list" table.
        var tableScores = new List<(string name, int score)>();
        using (var listCmd = con.CreateCommand())
        {
            listCmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var r = listCmd.ExecuteReader();
            while (r.Read())
            {
                string t = r.GetString(0);
                if (!IsSafeTableName(t)) continue;
                int s = ScoreTableIntent(con, t);
                if (s > 0) tableScores.Add((t, s));
            }
        }

        if (tableScores.Count == 0)
        {
            // Unknown schema — try every user table; ReadMonsterTable skips unsuitable ones.
            using (var listCmd2 = con.CreateCommand())
            {
                listCmd2.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                using var r2 = listCmd2.ExecuteReader();
                while (r2.Read())
                {
                    string t = r2.GetString(0);
                    if (IsSafeTableName(t)) tableScores.Add((t, 1));
                }
            }
        }

        if (tableScores.Count == 0)
        {
            note = "No user tables in SQLite file.";
            return false;
        }

        tableScores.Sort((a, b) => b.score.CompareTo(a.score));
        var best = tableScores[0].name;
        if (!ReadMonsterTable(con, best, outList, out int readCount, out string? readErr))
        {
            note = readErr;
            return false;
        }

        count = readCount;
        note = $"Table \"{best}\" (score {tableScores[0].score}). {readErr ?? string.Empty}".Trim();
        return count > 0;
    }

    private static bool IsSafeTableName(string t)
    {
        foreach (char c in t)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') continue;
            return false;
        }
        return t.Length is > 0 and < 200;
    }

    private static int ScoreTableIntent(SqliteConnection con, string table)
    {
        int s = 0;
        if (table.IndexOf("monster", StringComparison.OrdinalIgnoreCase) >= 0) s += 15;
        if (table.IndexOf("mob", StringComparison.OrdinalIgnoreCase) >= 0) s += 8;
        if (table.IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0) s += 4;

        using var c = con.CreateCommand();
        c.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            string col = r.GetString(1);
            s += ColumnScore(col);
        }
        return s;
    }

    private static int ColumnScore(string col)
    {
        string c = col.ToLowerInvariant();
        if (c.Contains("fester", StringComparison.Ordinal) || c is "f") return 4;
        if (c.Contains("broad", StringComparison.Ordinal) || c is "b" or "broadside") return 4;
        if (c.Contains("gravity", StringComparison.Ordinal) || c is "g") return 2;
        if (c.Contains("imperil", StringComparison.Ordinal) || c is "i") return 2;
        if (c.Contains("yield", StringComparison.Ordinal) || c is "y") return 2;
        if (c.Contains("vuln", StringComparison.Ordinal) && c.Contains("ex", StringComparison.Ordinal) == false) return 2;
        if (c is "v") return 2;
        if (c.Contains("expression", StringComparison.Ordinal) || c is "matchexpr" or "matchexpression" or "match" or "expr" or "e")
            return 8;
        if (c is "name" or "mname" or "monstername" or "n")
            return 6;
        if (c.Contains("damag", StringComparison.Ordinal) || c.Contains("element", StringComparison.Ordinal) || c is "dmg" or "dt")
            return 4;
        if (c.Contains("priority", StringComparison.Ordinal) || c is "p" or "pri" or "sort" or "idx" or "ordinal")
            return 2;
        if (c.Contains("weapon", StringComparison.Ordinal) || c.Contains("wand", StringComparison.Ordinal)) return 2;
        if (c is "a" or "r" or "s" or "w" or "u") return 1; // A/R/S are ambiguous; low weight
        return 0;
    }

    private static bool ReadMonsterTable(SqliteConnection con, string table, List<MonsterRule> outList, out int count, out string? err)
    {
        count = 0;
        err   = null;
        // Prefer stable ordering when a dedicated column exists (ROWID is the SQLite insert order for many VT exports).
        string orderBy = "ROWID";
        if (TableHasColumn(con, table, "Order")) orderBy = "CAST([Order] AS INTEGER)";
        else if (TableHasColumn(con, table, "SortIndex")) orderBy = "SortIndex";
        else if (TableHasColumn(con, table, "Index")) orderBy = "[Index]";

        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {orderBy} ASC";
        using var r = cmd.ExecuteReader();

        int ordName = FindOrdinalByHints(r, "name", "monstername", "mname", "entry", "n", "text0");
        int ordExpr = FindOrdinalByHints(r, "matchexpression", "match", "expression", "expr", "e", "ed");
        if (ordExpr < 0) ordExpr = FindExpressionColumn(r);
        int ordDmg  = FindOrdinalByHints(r, "damagetype", "element", "dmg", "de", "delement");
        int ordPri  = FindOrdinalByHints(r, "priority", "p", "pri", "importance", "ipriority");
        int ordEx   = FindOrdinalByHints(r, "exvuln", "exvul", "extravuln", "vulnextra");
        int ordWep  = FindOrdinalByHints(r, "weaponid", "weapon", "windex", "weaponindex");
        int ordOff  = FindOrdinalByHints(r, "offhand", "offhandid", "shield");
        int ordF = FindOrdinalByHints(r, "fester", "f", "b_fester", "bisfester", "festerb");
        int ordB = FindOrdinalByHints(r, "broadside", "b", "broad", "broadb");
        int ordG = FindOrdinalByHints(r, "gravity", "g", "grav");
        int ordI = FindOrdinalByHints(r, "imperil", "i", "imprl");
        int ordY = FindOrdinalByHints(r, "yield", "y", "yld");
        int ordV = FindOrdinalByHints(r, "vulnerability", "v", "vuln", "b_vuln", "bisfestervuln", "bisfesterv"); // be careful: skip ex vuln
        // VT often: UseArc, UseRing, Streak, Bolt
        int ordA = FindOrdinalByHints(r, "usearc", "arc", "a");
        int ordR = FindOrdinalByHints(r, "usering", "ring", "r", "b_ring");
        int ordS = FindOrdinalByHints(r, "streak", "s", "usestreak", "b_streak", "bisfeststreak", "bisfstreak", "bisfstreak2");
        int ordBl= FindOrdinalByHints(r, "usebolt", "bolt", "bl", "b_bolt", "bisfstreakbolt", "bisfstreakbol", "bisfb");

        if (ordName < 0 && ordExpr < 0)
        {
            err = "Table has no recognizable name/expression column.";
            return false;
        }

        while (r.Read())
        {
            var m = new MonsterRule();
            string? name  = GetStr(r, ordName);
            string? expr  = GetStr(r, ordExpr);
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                m.Name = "Default";
                m.MatchExpression = string.Empty;
            }
            else
            {
                m.Name = name?.Trim() ?? string.Empty;
                m.MatchExpression = expr?.Trim() ?? string.Empty;
            }

            m.Priority = 1;
            if (ordPri >= 0) m.Priority = Math.Clamp(ToInt32(r, ordPri, 1), -100, 10000);
            m.DamageType  = string.IsNullOrEmpty(GetStr(r, ordDmg))  ? "Auto" : MapDamageType(GetStr(r, ordDmg) ?? "Auto");
            m.ExVuln     = string.IsNullOrEmpty(GetStr(r, ordEx))  ? "None" : (GetStr(r, ordEx) ?? "None");
            m.WeaponId   = ordWep  >= 0 ? Math.Max(0, ToInt32(r, ordWep, 0))  : 0;
            m.OffhandId  = ordOff  >= 0 ? Math.Max(0, ToInt32(r, ordOff, 0))  : 0;
            m.Fester     = GetBoolish(r, ordF);
            m.Broadside  = GetBoolish(r, ordB);
            m.GravityWell= GetBoolish(r, ordG);
            m.Imperil     = GetBoolish(r, ordI);
            m.Yield      = GetBoolish(r, ordY);
            m.Vuln        = GetBoolish(r, ordV);
            m.UseArc     = GetBoolish(r, ordA);
            m.UseRing     = GetBoolish(r, ordR);
            m.UseStreak   = GetBoolish(r, ordS);
            m.UseBolt     = ordBl >= 0 ? GetBoolish(r, ordBl) : m.UseBolt;

            // If row is empty, skip
            if (string.IsNullOrEmpty(m.Name) && string.IsNullOrEmpty(m.MatchExpression))
            {
                bool anyToggle = m.Fester || m.Broadside || m.GravityWell || m.Imperil || m.Yield || m.Vuln
                    || m.UseArc || m.UseRing || m.UseStreak;
                if (!anyToggle) continue;
            }

            if (m.Name.Length == 0 && m.MatchExpression.Length == 0)
                continue;

            outList.Add(m);
        }

        count = outList.Count;
        if (count == 0) err = "No mappable rows.";
        return true;
    }

    private static bool TableHasColumn(SqliteConnection con, string table, string col)
    {
        using var c = con.CreateCommand();
        c.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            if (r.GetString(1).Equals(col, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Column whose name is exactly <paramref name="hints"/> (lowercase) or a short exact code (P, V, etc.).</summary>
    private static int FindOrdinalByHints(SqliteDataReader r, params string[] hints)
    {
        for (int i = 0; i < r.FieldCount; i++)
        {
            string n  = r.GetName(i);
            string nl = n.ToLowerInvariant();
            foreach (string h in hints)
            {
                if (h.Equals("p", StringComparison.Ordinal) && nl is "p" or "ipriority")
                    return i;
                if (h.Equals("v", StringComparison.Ordinal) && (nl is "v" or "b_v" or "b_vuln" or "vuln")
                    && !nl.Contains("exvul", StringComparison.Ordinal) && !nl.Contains("ex_vul", StringComparison.Ordinal))
                    return i;
                if (nl == h.ToLowerInvariant()) return i;
            }
        }
        return -1;
    }

    private static int FindExpressionColumn(SqliteDataReader r)
    {
        for (int i = 0; i < r.FieldCount; i++)
        {
            string nl = r.GetName(i).ToLowerInvariant();
            if (nl.Contains("matchexpr", StringComparison.Ordinal)
                || (nl.Contains("match", StringComparison.Ordinal) && nl.Contains("exp", StringComparison.Ordinal)))
                return i;
        }
        return -1;
    }

    private static string? GetStr(SqliteDataReader r, int ord) => ord < 0 || r.IsDBNull(ord) ? null : r.GetValue(ord)?.ToString();

    private static int ToInt32(SqliteDataReader r, int ord, int dft)
    {
        try
        {
            if (r.IsDBNull(ord)) return dft;
            return Convert.ToInt32(r.GetValue(ord), CultureInfo.InvariantCulture);
        }
        catch
        {
            if (int.TryParse(r.GetValue(ord)?.ToString(), out int p)) return p;
            return dft;
        }
    }

    private static bool GetBoolish(SqliteDataReader r, int ord)
    {
        if (ord < 0 || r.IsDBNull(ord)) return false;
        var v = r.GetValue(ord);
        if (v is long l) return l != 0;
        if (v is int i) return i != 0;
        if (v is bool b) return b;
        string? s = v?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string MapDamageType(string raw)
    {
        string t = raw.Trim();
        if (t.Length == 0) return "Auto";
        if (t.Contains('<', StringComparison.Ordinal) && t.Contains('>', StringComparison.Ordinal)) // <auto>
        {
            if (t.Contains("auto", StringComparison.OrdinalIgnoreCase)) return "Auto";
        }
        // VT uses words like "Slash" — normalize first letter
        if (t.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return "Auto";
        foreach (string s in new[] { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" })
        {
            if (s.Equals(t, StringComparison.OrdinalIgnoreCase)) return s;
        }
        // Unknown — keep for display, Rynth will treat oddly if not in list; still better than Data loss
        return t;
    }

    // ── Binary / UTF-16 / UTF-8 string scan: expression fallbacks ────────────

    private static bool TryImportFromStringScan(byte[] data, List<MonsterRule> outList, out int count, out string? note)
    {
        count = 0;
        note  = null;
        outList.Clear();
        var strs = new HashSet<string>(StringComparer.Ordinal);

        foreach (string s in ExtractAllStrings(data, minLen: 8, maxLen: 480))
        {
            if (LooksLikeVtExpression(s))
                strs.Add(s.Trim());
        }

        if (strs.Count == 0)
        {
            count = 0;
            return false;
        }

        // Prefer longer expressions (often more specific)
        foreach (string s in strs.OrderByDescending(x => x.Length))
        {
            outList.Add(new MonsterRule
            {
                Name = string.Empty,
                MatchExpression = s,
                DamageType = "Auto",
                UseBolt  = true,
            });
        }

        count = outList.Count;
        note  = "Binary mode only recovers vTank **match expression** rows (name-only entries must be added by hand, or re-export from a SQLite .usd).";
        return true;
    }

    /// <summary>Pull printable ASCII/UTF-8 (single-byte) and UTF-16-LE "wide string" runs from a blob.</summary>
    private static IEnumerable<string> ExtractAllStrings(byte[] data, int minLen, int maxLen)
    {
        // 7-bit runs (covers most human-readable .usd contents outside SQLite BLOBs)
        int start = -1;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if ((b >= 0x20 && b <= 0x7E) || b == 0x09) // printables + tab
            {
                if (start < 0) start = i;
            }
            else
            {
                if (start >= 0)
                {
                    int len = i - start;
                    if (len >= minLen)
                        yield return Encoding.UTF8.GetString(data, start, len > maxLen ? maxLen : len);
                }
                start = -1;
            }
        }
        if (start >= 0)
        {
            int len2 = data.Length - start;
            if (len2 >= minLen)
                yield return Encoding.UTF8.GetString(data, start, len2 > maxLen ? maxLen : len2);
        }

        // UTF-16-LE ASCII: [char][0][char][0] (common in Windows/.NET-originated data)
        for (int i = 0; i < data.Length - 1;)
        {
            if (data[i + 1] != 0) { i++; continue; }
            if (data[i] < 0x20) { i++; continue; }
            {
                char ch = (char)data[i];
                if (!char.IsLetter(ch) && !char.IsDigit(ch) && ch is not ( '<' or '=' or '(' or '!' or 'f' or 't')) { i++; continue; }
            }
            int p = i;
            var sb = new StringBuilder();
            while (p + 1 < data.Length)
            {
                if (data[p + 1] != 0) break;
                int c = data[p];
                if (c < 0x20) break;
                if (c > 0x7E) break;
                sb.Append((char)c);
                p += 2;
                if (sb.Length >= maxLen) break;
            }
            if (sb.Length >= minLen) yield return sb.ToString();
            if (p > i) { i = p; }
            else { i++; }
        }
    }

    private static bool LooksLikeVtExpression(string s)
    {
        s = s.Trim();
        if (s.Length < 6) return false;
        if (s.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Contains("SQLite format", StringComparison.Ordinal)) return false;
        if (s.Contains("&&", StringComparison.Ordinal) || s.Contains("||", StringComparison.Ordinal)) return true;
        if (s.Contains("==", StringComparison.Ordinal) && (
                s.Contains("range", StringComparison.Ordinal)
                || s.Contains("species", StringComparison.Ordinal)
                || s.Contains("typeid", StringComparison.Ordinal)
                || s.Contains("name", StringComparison.Ordinal)
                || s.Contains("maxhp", StringComparison.Ordinal)
                || s.Contains("hasshield", StringComparison.Ordinal)
                || s.Contains("metastate", StringComparison.Ordinal)))
            return true;
        // e.g. range>5 && !foo
        if (Regex.IsMatch(s, @"(?i)range\s*[<>=!]+")) return true;
        return false;
    }
}
