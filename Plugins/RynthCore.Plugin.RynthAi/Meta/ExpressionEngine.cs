using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed class ExpressionEngine
{
    private readonly RynthCoreHost _host;
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    private uint _playerId;

    public IReadOnlyDictionary<string, string> Variables => _variables;

    public ExpressionEngine(RynthCoreHost host)
    {
        _host = host;
    }

    public void SetPlayerId(uint id) => _playerId = id;

    // ── Action expressions (side effects) ────────────────────────────────────

    /// <summary>
    /// Executes an action expression (e.g. setvar[key, value]).
    /// Returns true if it was recognized as an action, false if not.
    /// </summary>
    public bool TryExecuteAction(string expression)
    {
        string expr = expression.Trim();

        // setvar[key, value] — value can itself be a nested expression
        if (expr.StartsWith("setvar[", StringComparison.OrdinalIgnoreCase) && expr.EndsWith("]"))
        {
            string inner = expr.Substring(7, expr.Length - 8);
            int comma = inner.IndexOf(',');
            if (comma < 0)
            {
                _host.WriteToChat($"[RynthAi Expr] setvar requires key,value — got: {inner}", 1);
                return true;
            }
            string key = inner.Substring(0, comma).Trim();
            string valueExpr = inner.Substring(comma + 1).Trim();
            _variables[key] = Evaluate(valueExpr);
            return true;
        }

        return false;
    }

    // ── Value expressions (return a string) ──────────────────────────────────

    /// <summary>
    /// Evaluates an expression and returns its string result.
    /// Recognized functions are resolved; plain text is returned as-is.
    /// </summary>
    public string Evaluate(string expression)
    {
        string expr = expression.Trim();

        // getcharintprop[propertyId]
        if (expr.StartsWith("getcharintprop[", StringComparison.OrdinalIgnoreCase) && expr.EndsWith("]"))
        {
            string inner = expr.Substring(15, expr.Length - 16);
            if (!uint.TryParse(inner.Trim(), out uint stype))
                return "0";
            if (_playerId == 0 || !_host.HasGetObjectIntProperty)
                return "0";
            return _host.TryGetObjectIntProperty(_playerId, stype, out int val) ? val.ToString() : "0";
        }

        // getchardoubleprop[propertyId]
        if (expr.StartsWith("getchardoubleprop[", StringComparison.OrdinalIgnoreCase) && expr.EndsWith("]"))
        {
            string inner = expr.Substring(18, expr.Length - 19);
            if (!uint.TryParse(inner.Trim(), out uint stype))
                return "0";
            if (_playerId == 0 || !_host.HasGetObjectDoubleProperty)
                return "0";
            return _host.TryGetObjectDoubleProperty(_playerId, stype, out double val) ? val.ToString() : "0";
        }

        // getcharboolprop[propertyId]
        if (expr.StartsWith("getcharboolprop[", StringComparison.OrdinalIgnoreCase) && expr.EndsWith("]"))
        {
            string inner = expr.Substring(16, expr.Length - 17);
            if (!uint.TryParse(inner.Trim(), out uint stype))
                return "0";
            if (_playerId == 0 || !_host.HasGetObjectBoolProperty)
                return "0";
            return _host.TryGetObjectBoolProperty(_playerId, stype, out bool val) ? (val ? "1" : "0") : "0";
        }

        // getcharstringprop[propertyId]
        if (expr.StartsWith("getcharstringprop[", StringComparison.OrdinalIgnoreCase) && expr.EndsWith("]"))
        {
            string inner = expr.Substring(18, expr.Length - 19);
            if (!uint.TryParse(inner.Trim(), out uint stype))
                return "";
            if (_playerId == 0 || !_host.HasGetObjectStringProperty)
                return "";
            return _host.TryGetObjectStringProperty(_playerId, stype, out string val) ? val : "";
        }

        // getvar[key]
        if (expr.StartsWith("getvar[", StringComparison.OrdinalIgnoreCase) && expr.EndsWith("]"))
        {
            string key = expr.Substring(7, expr.Length - 8).Trim();
            return _variables.TryGetValue(key, out string? v) ? v : "";
        }

        // Plain literal
        return expr;
    }
}
