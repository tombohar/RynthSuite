using System;
using System.Globalization;
using MoonSharp.Interpreter;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The globals every script sees (besides Lua's libraries and LuaScriptHost's scheduler
/// globals). Kept exactly as they were when Lua lived in RynthAi, so existing scripts run:
///
///   print(...)                 → Lua panel console
///   chat(text)                 → your chat window only
///   command(text)              → as if typed in chat: /ra, /ub, /mt, /say, /f, ...
///   expr(expression)           → a meta expression, e.g. expr("getcharvital_current[1]")
///   meta.&lt;function&gt;(args...)   → any meta expression function by name
///   getvar(name) / setvar(name, value)  → meta session variables (shared with metas)
///   me.name, me.id, me.health, me.maxhealth, me.stamina, me.maxstamina,
///   me.mana, me.maxmana, me.landcell, me.coords
///   RynthAi:GoTo / Stop / UsePortal / GetDistance / GetPlayerNS / GetPlayerEW / GetPlayerZ / IsPathClear
///   Settings.&lt;name&gt;            read or set any RynthAi setting metas can use
///   function OnBotTick() end   called every 250 ms while the RynthAi macro runs
///
/// Meta expressions, settings, the macro, navigation, object search and line of sight
/// are RynthAi's: they come through RynthAiBridge and raise a Lua error when RynthAi
/// isn't loaded. The rest only needs RynthCore.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private void RegisterV1Api(Script s, LuaScriptHost host)
    {
        s.Globals["chat"] = DynValue.NewCallback((ctx, a) =>
        {
            if (Host.HasWriteToChat) Host.WriteToChat(JoinArgs(a), 1);
            return DynValue.Nil;
        });

        s.Globals["command"] = DynValue.NewCallback((ctx, a) =>
        {
            string text = JoinArgs(a).Trim();
            if (text.Length > 0) _ai.SubmitCommand(text);
            return DynValue.Nil;
        });

        s.Globals["expr"] = DynValue.NewCallback((ctx, a) =>
            EvalExpression(a.Count > 0 ? a[0].CastToString() ?? "" : ""));

        s.Globals["getvar"] = DynValue.NewCallback((ctx, a) => CallMeta("getvar", a));
        s.Globals["setvar"] = DynValue.NewCallback((ctx, a) => CallMeta("setvar", a));

        // meta.<name>(...): any expression function, looked up on first use.
        var meta = new Table(s);
        var metaMt = new Table(s);
        metaMt["__index"] = DynValue.NewCallback((ctx, a) =>
        {
            string fn = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            if (fn.Length == 0) return DynValue.Nil;
            return DynValue.NewCallback((_, args) => CallMeta(fn, args));
        });
        meta.MetaTable = metaMt;
        s.Globals["meta"] = DynValue.NewTable(meta);

        // me.<field>: read live each time.
        var me = new Table(s);
        var meMt = new Table(s);
        meMt["__index"] = DynValue.NewCallback((ctx, a) =>
        {
            string key = (a.Count > 1 ? a[1].CastToString() ?? "" : "").ToLowerInvariant();
            return key switch
            {
                "name"       => EvalExpression("wobjectgetname[wobjectgetplayer[]]"),
                "id"         => DynValue.NewNumber(Host.GetPlayerId()),
                "health"     => EvalExpression("getcharvital_current[1]"),
                "stamina"    => EvalExpression("getcharvital_current[2]"),
                "mana"       => EvalExpression("getcharvital_current[3]"),
                "maxhealth"  => EvalExpression("getcharvital_buffedmax[1]"),
                "maxstamina" => EvalExpression("getcharvital_buffedmax[2]"),
                "maxmana"    => EvalExpression("getcharvital_buffedmax[3]"),
                "landcell"   => EvalExpression("getplayerlandcell[]"),
                "coords"     => EvalExpression("coordinatetostring[getplayercoordinates[]]"),
                _            => DynValue.Nil,
            };
        });
        me.MetaTable = meMt;
        s.Globals["me"] = DynValue.NewTable(me);

        RegisterCompatApi(s, host);
    }

    private static string JoinArgs(CallbackArguments a)
    {
        var parts = new string[a.Count];
        for (int i = 0; i < a.Count; i++) parts[i] = a[i].ToPrintString();
        return string.Join(" ", parts);
    }

    /// <summary>Calls meta function <paramref name="fn"/> with Lua args quoted as literals.</summary>
    private DynValue CallMeta(string fn, CallbackArguments args)
    {
        var quoted = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            DynValue v = args[i];
            quoted[i] = v.Type switch
            {
                DataType.Number  => v.Number.ToString("R", CultureInfo.InvariantCulture),
                DataType.Boolean => v.Boolean ? "1" : "0",
                DataType.Nil     => "",
                // Backticks make the meta parser take the text as-is (commas and
                // brackets included); a backtick inside would end it early.
                _                => "`" + (v.CastToString() ?? "").Replace('`', '\'') + "`",
            };
        }
        return EvalExpression($"{fn}[{string.Join(",", quoted)}]");
    }

    /// <summary>A meta expression through RynthAi; numbers come back as Lua numbers.</summary>
    private DynValue EvalExpression(string expression)
    {
        if (!_ai.Evaluate(expression, out string r))
            throw new ScriptRuntimeException(r == RynthAiBridge.NotLoaded ? r : $"{expression}: {r}");
        if (double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return DynValue.NewNumber(d);
        return DynValue.NewString(r);
    }

    // ── The Decal RynthAi's script surface ──────────────────────────────────

    private void RegisterCompatApi(Script s, LuaScriptHost host)
    {
        var ra = new Table(s);

        // Methods are called with a colon, so argument 0 is the table itself.
        ra["GoTo"] = DynValue.NewCallback((ctx, a) =>
        {
            double ns = ParseCoord(a.Count > 1 ? a[1] : DynValue.Nil, 'N', 'S');
            double ew = ParseCoord(a.Count > 2 ? a[2] : DynValue.Nil, 'E', 'W');
            if (!_ai.GoTo(ns, ew)) throw new ScriptRuntimeException("GoTo: " + RynthAiBridge.NotLoaded);
            ChatLine($"Going to {Math.Abs(ns):F2}{(ns < 0 ? "S" : "N")}, {Math.Abs(ew):F2}{(ew < 0 ? "W" : "E")}");
            return DynValue.Nil;
        });
        ra["Stop"] = DynValue.NewCallback((_, _) => { _ai.Stop(); return DynValue.Nil; });
        ra["UsePortal"] = DynValue.NewCallback((ctx, a) =>
        {
            string name = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            if (!_ai.FindNearest(name, portalOrNpcOnly: true, out uint id, out _))
            {
                host.Print($"UsePortal: no portal or NPC named like '{name}' nearby.");
                return DynValue.False;
            }
            return DynValue.NewBoolean(Host.UseFor(id, "Script", $"ra.UsePortal('{name}')", UseKind.Asked));
        });
        ra["GetDistance"] = DynValue.NewCallback((ctx, a) =>
        {
            string name = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            return DynValue.NewNumber(_ai.FindNearest(name, portalOrNpcOnly: false, out _, out double d) ? d : -1);
        });
        ra["GetPlayerNS"] = DynValue.NewCallback((_, _) =>
            DynValue.NewNumber(NavCoordinateHelper.TryGetNavCoords(Host, out double ns, out _) ? ns : 0));
        ra["GetPlayerEW"] = DynValue.NewCallback((_, _) =>
            DynValue.NewNumber(NavCoordinateHelper.TryGetNavCoords(Host, out _, out double ew) ? ew : 0));
        ra["GetPlayerZ"] = DynValue.NewCallback((_, _) =>
            DynValue.NewNumber(Host.HasGetPlayerPose && Host.TryGetPlayerPose(out _, out _, out _, out float z, out _, out _, out _, out _) ? z : 0));
        ra["IsPathClear"] = DynValue.NewCallback((ctx, a) =>
        {
            DynValue t = a.Count > 1 ? a[1] : DynValue.Nil;
            uint id = 0;
            if (t.Type == DataType.Number) id = (uint)t.Number;
            else _ai.FindNearest(t.CastToString() ?? "", portalOrNpcOnly: false, out id, out _);
            if (id == 0) throw new ScriptRuntimeException("IsPathClear(target): no target by that name or id");
            return DynValue.NewBoolean(_ai.IsPathClear(id));
        });
        s.Globals["RynthAi"] = DynValue.NewTable(ra);

        // Settings.<name>: the same settings map metas use for getsetting/setsetting.
        var settings = new Table(s);
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((ctx, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            string? v = _ai.GetSetting(key);
            if (v == null) return DynValue.Nil;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? DynValue.NewNumber(d) : DynValue.NewString(v);
        });
        mt["__newindex"] = DynValue.NewCallback((ctx, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            DynValue v = a.Count > 2 ? a[2] : DynValue.Nil;
            string text = v.Type switch
            {
                DataType.Boolean => v.Boolean ? "1" : "0",
                DataType.Number  => v.Number.ToString("R", CultureInfo.InvariantCulture),
                _                => v.CastToString() ?? "",
            };
            if (!_ai.SetSetting(key, text))
                throw new ScriptRuntimeException(_ai.Available ? $"Settings.{key}: no such setting" : RynthAiBridge.NotLoaded);
            return DynValue.Nil;
        });
        settings.MetaTable = mt;
        s.Globals["Settings"] = DynValue.NewTable(settings);
    }

    /// <summary>"33.5N" / "44.8W" / a number → signed nav coordinate (S and W negative).</summary>
    private static double ParseCoord(DynValue v, char pos, char neg)
    {
        if (v.Type == DataType.Number) return v.Number;
        string t = (v.CastToString() ?? "").Trim().ToUpperInvariant();
        double sign = 1;
        if (t.EndsWith(neg)) { sign = -1; t = t[..^1]; }
        else if (t.EndsWith(pos)) t = t[..^1];
        if (!double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            throw new ScriptRuntimeException($"GoTo: can't read coordinate '{v.CastToString()}' (use e.g. \"33.5N\", \"44.8W\")");
        return sign * d;
    }
}
