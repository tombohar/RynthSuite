using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.Combat;

/// <summary>
/// Evaluates vTank-compatible monster match expressions against a target.
/// Syntax: range>5 && species==drudge
/// Variables: true, false, name, species, typeid, maxhp, range, hasshield, metastate
/// Operators (by precedence low→high): || && == != &lt; > &lt;= >= # + - * / %
/// Grouping with ( ).
/// </summary>
internal sealed class MonsterMatchEvaluator
{
    private readonly WorldObjectCache _cache;
    private readonly uint _playerId;

    // Context — set before each eval
    private WorldObject? _target;
    private string _metaState = "Default";

    public MonsterMatchEvaluator(WorldObjectCache cache, uint playerId)
    {
        _cache = cache;
        _playerId = playerId;
    }

    public bool Evaluate(string expression, WorldObject target, string metaState)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        _target = target;
        _metaState = metaState ?? "Default";
        try
        {
            int pos = 0;
            string result = ParseOr(expression.Trim(), ref pos);
            return IsTruthy(result);
        }
        catch
        {
            return false;
        }
    }

    // ── Parser (recursive descent, left-to-right) ────────────────────────────

    private string ParseOr(string s, ref int pos)
    {
        string left = ParseAnd(s, ref pos);
        while (TryConsumeOp(s, ref pos, "||"))
        {
            string right = ParseAnd(s, ref pos);
            left = (IsTruthy(left) || IsTruthy(right)) ? "1" : "0";
        }
        return left;
    }

    private string ParseAnd(string s, ref int pos)
    {
        string left = ParseComparison(s, ref pos);
        while (TryConsumeOp(s, ref pos, "&&"))
        {
            string right = ParseComparison(s, ref pos);
            left = (IsTruthy(left) && IsTruthy(right)) ? "1" : "0";
        }
        return left;
    }

    private string ParseComparison(string s, ref int pos)
    {
        string left = ParseAddSub(s, ref pos);
        SkipWs(s, ref pos);

        string? op = null;
        if (TryConsumeOp(s, ref pos, "=="))      op = "==";
        else if (TryConsumeOp(s, ref pos, "!=")) op = "!=";
        else if (TryConsumeOp(s, ref pos, ">=")) op = ">=";
        else if (TryConsumeOp(s, ref pos, "<=")) op = "<=";
        else if (TryConsumeOp(s, ref pos, "#"))  op = "#";
        else if (TryConsumeChar(s, ref pos, '>')) op = ">";
        else if (TryConsumeChar(s, ref pos, '<')) op = "<";

        if (op == null) return left;

        string right = ParseAddSub(s, ref pos);

        if (op == "#")
        {
            try { return Regex.IsMatch(left, right, RegexOptions.IgnoreCase) ? "1" : "0"; }
            catch { return "0"; }
        }

        if (double.TryParse(left, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double ln) &&
            double.TryParse(right, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double rn))
        {
            return op switch
            {
                "==" => ln == rn ? "1" : "0",
                "!=" => ln != rn ? "1" : "0",
                ">"  => ln > rn  ? "1" : "0",
                "<"  => ln < rn  ? "1" : "0",
                ">=" => ln >= rn ? "1" : "0",
                "<=" => ln <= rn ? "1" : "0",
                _    => "0",
            };
        }

        // String comparison
        int cmp = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "==" => cmp == 0 ? "1" : "0",
            "!=" => cmp != 0 ? "1" : "0",
            ">"  => cmp > 0  ? "1" : "0",
            "<"  => cmp < 0  ? "1" : "0",
            ">=" => cmp >= 0 ? "1" : "0",
            "<=" => cmp <= 0 ? "1" : "0",
            _    => "0",
        };
    }

    private string ParseAddSub(string s, ref int pos)
    {
        string left = ParseMulDiv(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            if (TryConsumeChar(s, ref pos, '+'))
            {
                string right = ParseMulDiv(s, ref pos);
                if (TryNum(left, out double l) && TryNum(right, out double r))
                    left = Fmt(l + r);
                else
                    left = left + right;
            }
            else if (TryConsumeChar(s, ref pos, '-'))
            {
                string right = ParseMulDiv(s, ref pos);
                if (TryNum(left, out double l) && TryNum(right, out double r))
                    left = Fmt(l - r);
            }
            else break;
        }
        return left;
    }

    private string ParseMulDiv(string s, ref int pos)
    {
        string left = ParseUnary(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            char c = pos < s.Length ? s[pos] : '\0';
            if (c == '*') { pos++; string r = ParseUnary(s, ref pos); left = TryNum(left, out double l) && TryNum(r, out double rv) ? Fmt(l * rv) : "0"; }
            else if (c == '/') { pos++; string r = ParseUnary(s, ref pos); left = TryNum(left, out double l) && TryNum(r, out double rv) && rv != 0 ? Fmt(l / rv) : "0"; }
            else if (c == '%') { pos++; string r = ParseUnary(s, ref pos); left = TryNum(left, out double l) && TryNum(r, out double rv) && rv != 0 ? Fmt((int)l % (int)rv) : "0"; }
            else break;
        }
        return left;
    }

    private string ParseUnary(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        if (pos < s.Length && s[pos] == '(')
        {
            pos++; // consume '('
            string inner = ParseOr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ')') pos++;
            return inner;
        }
        return ParseAtom(s, ref pos);
    }

    private string ParseAtom(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        if (pos >= s.Length) return "";

        // Quoted string
        if (s[pos] == '"' || s[pos] == '\'')
        {
            char q = s[pos++];
            int start = pos;
            while (pos < s.Length && s[pos] != q) pos++;
            string val = s.Substring(start, pos - start);
            if (pos < s.Length) pos++;
            return val;
        }

        // Read token: letters, digits, _, ., -  (no spaces)
        int begin = pos;
        while (pos < s.Length && !IsTokenEnd(s[pos])) pos++;
        string token = s.Substring(begin, pos - begin).Trim();

        return ResolveVariable(token);
    }

    private static bool IsTokenEnd(char c) =>
        c == '>' || c == '<' || c == '=' || c == '!' || c == '&' || c == '|' ||
        c == ')' || c == '(' || c == '#' || c == '*' || c == '/' || c == '%' ||
        c == '+' || c == ' ' || c == '\t';

    // ── Variable resolution ───────────────────────────────────────────────────

    private string ResolveVariable(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        switch (token.ToLowerInvariant())
        {
            case "true":       return "1";
            case "false":      return "0";
            case "name":       return _target?.Name ?? "";
            case "metastate":  return _metaState;
            case "range":
            {
                if (_target == null) return "9999";
                double d = _cache.Distance(_target.Id, (int)_playerId);
                return d < 0 ? "9999" : Fmt(d);
            }
            case "maxhp":
            {
                if (_target == null) return "0";
                int hp = _target.Values(LongValueKey.MaximumHealth, 0);
                return hp.ToString();
            }
            case "typeid":
            {
                if (_target == null) return "0";
                int tid = _target.Values(LongValueKey.TemplateType, 0);
                return tid.ToString();
            }
            case "species":
            {
                if (_target == null) return "";
                // CreatureType (STypeInt 62) maps to AC creature-type IDs; return as string int.
                // Use 'typeid' for exact type matching; 'species' returns the creature type int.
                int ct = _target.Values(LongValueKey.CreatureType, 0);
                return ct.ToString();
            }
            case "hasshield":
            {
                // Shield detection requires enumerating target equip slots — not yet available.
                return "0";
            }
            default:
                // Treat as literal string/number
                return token;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsTruthy(string s) =>
        !string.IsNullOrEmpty(s) && s != "0" && s != "false";

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out v);

    private static string Fmt(double v) =>
        v == Math.Truncate(v)
            ? ((long)v).ToString()
            : v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
    }

    private static bool TryConsumeOp(string s, ref int pos, string op)
    {
        SkipWs(s, ref pos);
        if (pos + op.Length <= s.Length && s.AsSpan(pos, op.Length).Equals(op, StringComparison.Ordinal))
        {
            pos += op.Length;
            return true;
        }
        return false;
    }

    private static bool TryConsumeChar(string s, ref int pos, char c)
    {
        if (pos < s.Length && s[pos] == c) { pos++; return true; }
        return false;
    }
}
