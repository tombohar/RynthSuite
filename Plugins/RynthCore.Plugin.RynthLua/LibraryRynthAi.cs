using System;
using System.Globalization;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// <c>require("rynth.ai")</c>: RynthAi through its "RynthAi.Script" interface (RynthAiBridge).
///
///   ai.Available (also IsLoaded)      true while RynthAi's interface is there
///   ai.Evaluate(expr)                 a meta expression; numbers come back as numbers
///   ai.GetSetting(name)               number / boolean / text, nil for an unknown setting
///   ai.SetSetting(name, value)        true, or false for an unknown setting (saves the profile)
///   ai.SettingNames()                 every setting name (sorted)
///   ai.MacroRunning                   read, or assign true/false to start/stop the macro
///   ai.GoTo(ns, ew)                   walk there (numbers, or "33.5N" / "44.8W")
///   ai.Stop()                         stop navigation (and the macro)
///   ai.FindNearest(name [, portalsOnly])  WorldObject, distance (nil when nothing matches)
///   ai.DistanceTo(objectOrId)         metres, -1 when unknown
///   ai.IsPathClear(objectOrIdOrName)  line of sight
///   ai.Command(text) (SubmitCommand)  a chat line as if typed (/ra, /vt, ...; AC's parser without RynthAi)
///   ai.SetAttackHandler(fn)           this script chooses RynthAi's attack spells (CombatHookPump.cs);
///   ai.SetHealHandler(fn)             ... and its heal spells, asked before kits. true, or false and why.
///                                     fn(ctx) returns spellId, targetId [, "area"], or nil to let RynthAi choose
///   ai.ClearCombatHandlers()          drops this script's handlers (stopping the script does too)
///   ai.CombatHook                     { Supported, AttackOwner, HealOwner, Claimed }
///
/// Everything except Available and Command raises "RynthAi isn't loaded ..." when RynthAi
/// isn't there, so a script can check <c>ai.Available</c> first or pcall.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private DynValue CreateRynthAiModule(Script s, ScriptContext ctx)
    {
        var m = new Table(s);

        void Need(string what)
        {
            if (!_ai.Available) throw new ScriptRuntimeException($"rynth.ai.{what}: {RynthAiBridge.NotLoaded}");
        }

        string Text(DynValue[] args, int i, string what)
        {
            DynValue v = LuaArgs.At(args, i);
            string t = v.Type is DataType.String or DataType.Number ? v.CastToString() ?? string.Empty : string.Empty;
            if (t.Trim().Length == 0) throw new ScriptRuntimeException($"rynth.ai.{what} needs text");
            return t;
        }

        uint Target(DynValue v, string what)
        {
            uint id = LuaArgs.Id(v);
            if (id == 0 && v.Type == DataType.String && _ai.FindNearest(v.String, portalOrNpcOnly: false, out uint found, out _))
                id = found;
            if (id == 0) throw new ScriptRuntimeException($"rynth.ai.{what}: no object by that id, object or name");
            return id;
        }

        m.Set("Evaluate", DynValue.NewCallback((c, a) =>
        {
            string expression = Text(LuaArgs.Of(a, m), 0, "Evaluate(expression)");
            Need("Evaluate");
            return EvalExpression(expression);
        }));

        m.Set("GetSetting", DynValue.NewCallback((c, a) =>
        {
            string name = Text(LuaArgs.Of(a, m), 0, "GetSetting(name)");
            Need("GetSetting");
            string? v = _ai.GetSetting(name);
            if (v == null) return DynValue.Nil;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return DynValue.NewNumber(d);
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return DynValue.True;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return DynValue.False;
            return DynValue.NewString(v);
        }));

        m.Set("SetSetting", DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, m);
            string name = Text(args, 0, "SetSetting(name, value)");
            DynValue v = LuaArgs.At(args, 1);
            string text = v.Type switch
            {
                DataType.Boolean => v.Boolean ? "1" : "0",
                DataType.Number  => v.Number.ToString("R", CultureInfo.InvariantCulture),
                DataType.String  => v.String,
                _ => throw new ScriptRuntimeException($"rynth.ai.SetSetting(\"{name}\", value): the value must be a number, text or boolean"),
            };
            Need("SetSetting");
            return DynValue.NewBoolean(_ai.SetSetting(name, text));
        }));

        m.Set("SettingNames", DynValue.NewCallback((c, a) =>
        {
            Need("SettingNames");
            var list = new Table(s);
            foreach (string n in _ai.SettingNames()) list.Append(DynValue.NewString(n));
            return DynValue.NewTable(list);
        }));

        m.Set("GoTo", DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, m);
            double ns = ParseCoord(LuaArgs.At(args, 0), 'N', 'S');
            double ew = ParseCoord(LuaArgs.At(args, 1), 'E', 'W');
            Need("GoTo");
            return DynValue.NewBoolean(_ai.GoTo(ns, ew));
        }));

        m.Set("Stop", DynValue.NewCallback((c, a) =>
        {
            Need("Stop");
            _ai.Stop();
            return DynValue.Nil;
        }));

        m.Set("FindNearest", DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, m);
            string name = Text(args, 0, "FindNearest(name [, portalsOnly])");
            bool portalsOnly = LuaArgs.At(args, 1).CastToBool();
            Need("FindNearest");
            if (!_ai.FindNearest(name, portalsOnly, out uint id, out double distance) || id == 0) return DynValue.Nil;
            return DynValue.NewTuple(WrapObject(s, ctx, id), DynValue.NewNumber(distance));
        }));

        m.Set("DistanceTo", DynValue.NewCallback((c, a) =>
        {
            DynValue target = LuaArgs.At(LuaArgs.Of(a, m), 0);
            Need("DistanceTo");
            return DynValue.NewNumber(_ai.DistanceTo(Target(target, "DistanceTo(objectOrId)")));
        }));

        m.Set("IsPathClear", DynValue.NewCallback((c, a) =>
        {
            DynValue target = LuaArgs.At(LuaArgs.Of(a, m), 0);
            Need("IsPathClear");
            return DynValue.NewBoolean(_ai.IsPathClear(Target(target, "IsPathClear(objectOrIdOrName)")));
        }));

        DynValue command = DynValue.NewCallback((c, a) =>
        {
            string text = LuaArgs.Str(LuaArgs.At(LuaArgs.Of(a, m), 0)).Trim();
            if (text.Length == 0) throw new ScriptRuntimeException("rynth.ai.Command(text) needs a command");
            _ai.SubmitCommand(text);
            return DynValue.Nil;
        });
        m.Set("Command", command);
        m.Set("SubmitCommand", command);

        // The combat hook (RynthAi.Combat): handlers run as script threads started by the pump.
        DynValue SetHandler(CallbackArguments a, uint kind, string what)
        {
            DynValue fn = LuaArgs.At(LuaArgs.Of(a, m), 0);
            if (!fn.IsNil() && fn.Type != DataType.Function)
                throw new ScriptRuntimeException($"rynth.ai.{what}(fn) needs a function (or nil to clear it)");
            string? refused = SetCombatHandler(ctx, kind, fn);
            return refused == null ? DynValue.True : DynValue.NewTuple(DynValue.False, DynValue.NewString(refused));
        }
        m.Set("SetAttackHandler", DynValue.NewCallback((c, a) => SetHandler(a, RynthCore.Plugin.Shared.RynthAiCombat.KindAttack, "SetAttackHandler")));
        m.Set("SetHealHandler", DynValue.NewCallback((c, a) => SetHandler(a, RynthCore.Plugin.Shared.RynthAiCombat.KindHeal, "SetHealHandler")));
        m.Set("ClearCombatHandlers", DynValue.NewCallback((c, a) =>
        {
            ClearCombatHandlers(ctx);
            return DynValue.Nil;
        }));

        // Live fields: Available / IsLoaded, MacroRunning (read and write).
        var mt = new Table(s);
        mt.Set("__index", DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 && a[1].Type == DataType.String ? a[1].String : string.Empty;
            switch (key)
            {
                case "Available":
                case "IsLoaded":
                    return DynValue.NewBoolean(_ai.Available);
                case "MacroRunning":
                    return DynValue.NewBoolean(_ai.MacroRunning);
                case "CombatHook":
                {
                    var h = new Table(s);
                    h["Supported"] = _ai.CombatAvailable;
                    h["AttackOwner"] = CombatOwner(RynthCore.Plugin.Shared.RynthAiCombat.KindAttack) is string ao ? DynValue.NewString(ao) : DynValue.Nil;
                    h["HealOwner"] = CombatOwner(RynthCore.Plugin.Shared.RynthAiCombat.KindHeal) is string ho ? DynValue.NewString(ho) : DynValue.Nil;
                    h["Claimed"] = CombatClaimed;
                    return DynValue.NewTable(h);
                }
                default:
                    return DynValue.Nil;
            }
        }));
        mt.Set("__newindex", DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 && a[1].Type == DataType.String ? a[1].String : string.Empty;
            DynValue v = a.Count > 2 ? a[2] : DynValue.Nil;
            switch (key)
            {
                case "MacroRunning":
                    Need("MacroRunning");
                    _ai.MacroRunning = v.CastToBool();
                    return DynValue.Nil;
                case "Available":
                case "IsLoaded":
                case "CombatHook":
                    throw new ScriptRuntimeException($"rynth.ai.{key} is read-only");
                default:
                    // Script's own fields on the module are fine.
                    m.Set(a[1], v);
                    return DynValue.Nil;
            }
        }));
        mt.Set("__tostring", DynValue.NewCallback((c, a) =>
            DynValue.NewString(_ai.Available ? "module rynth.ai (RynthAi loaded)" : "module rynth.ai (RynthAi not loaded)")));
        m.MetaTable = mt;
        return DynValue.NewTable(m);
    }
}
