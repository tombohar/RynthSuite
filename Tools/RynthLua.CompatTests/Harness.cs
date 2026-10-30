using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using RynthCore.Plugin.RynthLua;

namespace RynthLua.CompatTests;

/// <summary>
/// What a test script sees besides RynthLua's own API: <c>check(cond, msg)</c>,
/// <c>eq(actual, expected, msg)</c>, <c>done()</c>, and the <c>harness</c> table that drives
/// the fake world (chat lines, new objects, vitals, enchantments, vendor, trade, ...) and
/// reads back what the client was asked to do. World changes reach the plugin the way the
/// engine delivers them: queued, between ticks.
/// </summary>
internal static class Harness
{
    public static int Checks;
    public static readonly List<string> Failures = new();
    public static bool Done;
    public static RynthLuaPlugin? Plugin;

    private static FakeWorld W => FakeWorld.W;

    /// <summary>Plugins\RynthCore.Plugin.RynthLua\Examples\<paramref name="file"/>, found from this source file's folder.</summary>
    private static string ExamplePath(string file, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        string tools = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(source) ?? string.Empty) ?? string.Empty;
        string root = System.IO.Path.GetDirectoryName(tools) ?? string.Empty;
        return System.IO.Path.Combine(root, "Plugins", "RynthCore.Plugin.RynthLua", "Examples", System.IO.Path.GetFileName(file));
    }

    public static void Reset()
    {
        Checks = 0;
        Failures.Clear();
        Done = false;
        FakeCombat.Reset();
    }

    public static void Register(Script s, string scriptName)
    {
        s.Globals["check"] = DynValue.NewCallback((c, a) =>
        {
            Checks++;
            bool ok = a.Count > 0 && a[0].CastToBool();
            if (!ok) Failures.Add(a.Count > 1 ? a[1].ToPrintString() : "(check failed)");
            return DynValue.NewBoolean(ok);
        });
        s.Globals["eq"] = DynValue.NewCallback((c, a) =>
        {
            Checks++;
            DynValue actual = a.Count > 0 ? a[0] : DynValue.Nil;
            DynValue expected = a.Count > 1 ? a[1] : DynValue.Nil;
            bool ok = actual.Equals(expected)
                      || (actual.Type == DataType.Number && expected.Type == DataType.Number && Math.Abs(actual.Number - expected.Number) < 1e-6);
            if (!ok) Failures.Add($"{(a.Count > 2 ? a[2].ToPrintString() : "eq")}: expected <{expected.ToPrintString()}>, got <{actual.ToPrintString()}>");
            return DynValue.NewBoolean(ok);
        });
        s.Globals["done"] = DynValue.NewCallback((c, a) => { Done = true; return DynValue.Nil; });

        var h = new Table(s);
        var ids = new Table(s);
        foreach (var f in typeof(FakeWorld).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(uint)))
            ids[f.Name] = (double)(uint)f.GetRawConstantValue()!;
        h["ids"] = ids;
        h["player"] = (double)FakeWorld.Player;

        void Fn(string name, Func<CallbackArguments, DynValue> body) => h[name] = DynValue.NewCallback((c, a) => body(a));
        static double Num(CallbackArguments a, int i, double d = 0) => i < a.Count && a[i].Type == DataType.Number ? a[i].Number : d;
        static string Str(CallbackArguments a, int i, string d = "") => i < a.Count && !a[i].IsNil() ? a[i].CastToString() ?? d : d;
        static uint Id(CallbackArguments a, int i)
        {
            if (i >= a.Count) return 0;
            if (a[i].Type == DataType.Number) return (uint)a[i].Number;
            if (a[i].Type == DataType.Table && a[i].Table.Get("Id").Type == DataType.Number) return (uint)a[i].Table.Get("Id").Number;
            return 0;
        }
        DynValue Strings(IEnumerable<string> list)
        {
            var t = new Table(s);
            foreach (string x in list) t.Append(DynValue.NewString(x));
            return DynValue.NewTable(t);
        }

        Fn("chat", a => { W.IncomingChat(Str(a, 0), (int)Num(a, 1, 0)); return DynValue.Nil; });
        Fn("chatInput", a =>
        {
            int eat = 0;
            Plugin!.OnChatBarEnter(Str(a, 0), ref eat);
            return DynValue.NewBoolean(eat != 0);
        });
        Fn("create", a =>
        {
            Table t = a[0].Table;
            var o = new FakeObject
            {
                Id = W.NewId(),
                Name = t.Get("Name").CastToString() ?? "Thing",
                Wcid = (uint)(t.Get("Wcid").CastToNumber() ?? 1),
                ItemType = (uint)(t.Get("ItemType").CastToNumber() ?? 0x80),
                Bitfield = (uint)(t.Get("Bitfield").CastToNumber() ?? 0),
                Container = (uint)(t.Get("Container").CastToNumber() ?? 0),
            };
            o.Strings[1] = o.Name;
            if (t.Get("Stack").Type == DataType.Number) o.Stack = (int)t.Get("Stack").Number;
            if (t.Get("Value").Type == DataType.Number) o.Ints[19] = (int)t.Get("Value").Number;
            if (o.Container == 0)
            {
                o.Cell = FakeWorld.HomeCell;
                o.X = FakeWorld.HomeX + (float)(t.Get("X").CastToNumber() ?? 1);
                o.Y = FakeWorld.HomeY + (float)(t.Get("Y").CastToNumber() ?? 0);
                o.Z = FakeWorld.HomeZ + (float)(t.Get("Z").CastToNumber() ?? 0);
            }
            W.Create(o);
            return DynValue.NewNumber(o.Id);
        });
        Fn("delete", a => { W.Delete(Id(a, 0)); return DynValue.Nil; });
        Fn("set", a =>
        {
            var o = W.Get(Id(a, 0)) ?? throw new ScriptRuntimeException("harness.set: no such object");
            string kind = Str(a, 1);
            uint key = (uint)Num(a, 2);
            DynValue v = a.Count > 3 ? a[3] : DynValue.Nil;
            switch (kind)
            {
                case "int": o.Ints[key] = (int)v.Number; break;
                case "int64": o.Int64s[key] = (long)v.Number; break;
                case "float": o.Floats[key] = v.Number; break;
                case "string": o.Strings[key] = v.String; break;
                case "bool": o.Bools[key] = v.CastToBool(); break;
                case "data": o.DataIds[key] = (uint)v.Number; break;
                default: throw new ScriptRuntimeException("harness.set: kind is int, int64, float, string, bool or data");
            }
            return DynValue.Nil;
        });
        Fn("move", a =>
        {
            var o = W.Get(Id(a, 0)) ?? throw new ScriptRuntimeException("harness.move: no such object");
            o.X += (float)Num(a, 1); o.Y += (float)Num(a, 2); o.Z += (float)Num(a, 3);
            return DynValue.Nil;
        });
        Fn("vitals", a =>
        {
            Table t = a[0].Table;
            if (t.Get("Health").Type == DataType.Number) W.Hp = (uint)t.Get("Health").Number;
            if (t.Get("Stamina").Type == DataType.Number) W.St = (uint)t.Get("Stamina").Number;
            if (t.Get("Mana").Type == DataType.Number) W.Mana = (uint)t.Get("Mana").Number;
            return DynValue.Nil;
        });
        Fn("level", a => { W.Me.Ints[25] = (int)Num(a, 0); return DynValue.Nil; });
        Fn("xp", a => { W.Me.Int64s[1] = (long)Num(a, 0); return DynValue.Nil; });
        Fn("luminance", a => { W.Me.Int64s[6] = (long)Num(a, 0); return DynValue.Nil; });
        Fn("vitae", a => { W.Vitae = (float)Num(a, 0, 1); return DynValue.Nil; });
        Fn("combatMode", a =>
        {
            int prev = W.CombatMode;
            int mode = (int)Num(a, 0, 1);
            W.CombatMode = mode;
            W.Events.Enqueue(p => p.OnCombatModeChange(mode, prev));
            return DynValue.Nil;
        });
        Fn("enchant", a =>
        {
            uint spell = (uint)Num(a, 0);
            double secs = Num(a, 1, -1);
            W.Enchantments.RemoveAll(e => e.Spell == spell);
            W.Enchantments.Add((spell, secs < 0 ? double.MaxValue : W.ServerTime + secs));
            W.Events.Enqueue(p => p.OnEnchantmentAdded(spell, secs));
            return DynValue.Nil;
        });
        Fn("dispel", a =>
        {
            uint spell = (uint)Num(a, 0);
            W.Enchantments.RemoveAll(e => e.Spell == spell);
            W.Events.Enqueue(p => p.OnEnchantmentRemoved(spell));
            return DynValue.Nil;
        });
        Fn("select", a => { W.SetSelected(Id(a, 0)); return DynValue.Nil; });
        Fn("portal", a => { W.Portaling = a.Count > 0 && a[0].CastToBool(); W.PortalStepsLeft = int.MaxValue; return DynValue.Nil; });
        Fn("vendor", a =>
        {
            uint id = Id(a, 0);
            if (id != 0) W.OpenVendor(id); else W.CloseVendor();
            return DynValue.Nil;
        });
        Fn("container", a =>
        {
            uint id = Id(a, 0);
            uint was = W.GroundContainer;
            W.GroundContainer = id;
            if (was != 0) W.Events.Enqueue(p => p.OnStopViewingObjectContents(was));
            if (id != 0) W.Events.Enqueue(p => p.OnViewObjectContents(id));
            return DynValue.Nil;
        });
        Fn("tradeStart", a => { W.StartTrade(Id(a, 0)); return DynValue.Nil; });
        Fn("partnerAdd", a =>
        {
            uint item = Id(a, 0);
            W.TradeEvent(0x0200, () => W.TradePartnerItems.Add(item));
            return DynValue.Nil;
        });
        Fn("partnerAccept", a =>
        {
            W.TradeEvent(0x0202, () => { W.TradePartnerAccepted = true; W.TradePartnerAccepts++; W.TradeLastAcceptedBy = W.TradePartner; });
            return DynValue.Nil;
        });
        Fn("tradeComplete", a =>
        {
            W.TradeEvent(0x01FF, () => { W.TradeCompleted++; W.TradeOpen = false; W.TradeLastClose = 1; });
            return DynValue.Nil;
        });
        Fn("chatLog", a => Strings(W.ChatOut));
        Fn("invoked", a => Strings(W.Invoked));
        Fn("calls", a => Strings(W.Calls));
        Fn("clearCalls", a => { W.Calls.Clear(); W.Invoked.Clear(); W.ChatOut.Clear(); return DynValue.Nil; });
        Fn("exists", a => DynValue.NewBoolean(W.Get(Id(a, 0)) != null));
        Fn("containerOf", a => DynValue.NewNumber(W.Get(Id(a, 0))?.Container ?? 0));
        Fn("wielderOf", a => DynValue.NewNumber(W.Get(Id(a, 0))?.Wielder ?? 0));
        Fn("stackOf", a => DynValue.NewNumber(W.Get(Id(a, 0))?.Stack ?? 0));
        Fn("autorun", a => DynValue.NewBoolean(W.AutoRun));
        Fn("salvageItems", a =>
        {
            var t = new Table(s);
            foreach (uint id in W.SalvageItems) t.Append(DynValue.NewNumber(id));
            return DynValue.NewTable(t);
        });
        // RynthAi's combat hook (FakeCombat): turn the table on/off, open a request as RynthAi
        // would, and read back what RynthLua claimed, decided and released.
        Fn("combatTable", a => { FakeCombat.Enabled = a.Count > 0 && a[0].CastToBool(); return DynValue.Nil; });
        Fn("combatOpen", a => DynValue.NewNumber(FakeCombat.Open((uint)Num(a, 0, 1), Id(a, 1), (long)Num(a, 2, 2000))));
        Fn("combatResolve", a => { FakeCombat.ResolveInFlight(RynthCore.Plugin.Shared.RynthAiCombat.KindAttack, (int)Num(a, 0, -1)); return DynValue.Nil; });
        Fn("combatOlderRynthAi", a => { FakeCombat.OlderRynthAi = a.Count > 0 && a[0].CastToBool(); return DynValue.Nil; });
        Fn("combatScanned", a =>
        {
            FakeCombat.Scanned.Clear();
            if (a.Count > 0 && a[0].Type == DataType.Table)
                foreach (DynValue row in a[0].Table.Values)
                    FakeCombat.Scanned.Add(((uint)row.Table.Get(1).Number, (float)row.Table.Get(2).Number));
            return DynValue.Nil;
        });
        Fn("combatDecisions", a =>
        {
            var t = new Table(s);
            foreach (var d in FakeCombat.Decisions)
            {
                var row = new Table(s);
                row["seq"] = (double)d.Seq; row["kind"] = (double)d.Kind; row["spell"] = (double)d.Spell;
                row["target"] = (double)d.Target; row["flags"] = (double)d.Flags; row["taken"] = d.Taken;
                t.Append(DynValue.NewTable(row));
            }
            return DynValue.NewTable(t);
        });
        Fn("combatState", a =>
        {
            var t = new Table(s);
            t["owner"] = FakeCombat.Owner; t["kinds"] = (double)FakeCombat.Kinds;
            t["claims"] = (double)FakeCombat.Claims; t["refusals"] = (double)FakeCombat.Refusals;
            t["releases"] = (double)FakeCombat.Releases; t["releasedKinds"] = (double)FakeCombat.ReleasedKinds;
            return DynValue.NewTable(t);
        });
        // A second script in the same plugin (another script competing for a handler).
        Fn("runScript", a => { Plugin!.RunCode(Str(a, 0), Str(a, 1)); return DynValue.Nil; });
        // A script file in the (temp) Scripts folder, for "/lua start <name>" (replies.lua).
        Fn("writeScript", a =>
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(RynthLuaPaths.Scripts, Str(a, 0) + ".lua"), Str(a, 1));
            return DynValue.Nil;
        });
        Fn("stopScript", a => DynValue.NewBoolean(Plugin!.StopScript(Str(a, 0))));
        Fn("scriptLoaded", a => DynValue.NewBoolean(Plugin!.ScriptIsLoaded(Str(a, 0))));
        Fn("scriptConsole", a => DynValue.NewString(Plugin!.ScriptConsoleText(Str(a, 0))));
        Fn("combatReload", a => { FakeCombat.Reload(); return DynValue.Nil; });
        // The text of a script in the plugin's Examples folder (so a test can run it).
        Fn("exampleCode", a => DynValue.NewString(System.IO.File.ReadAllText(ExamplePath(Str(a, 0)))));
        Fn("scriptHooks", a =>
        {
            var (keepAlive, onStop) = Plugin!.ScriptHookCounts(Str(a, 0));
            var t = new Table(s);
            t["keepAlive"] = (double)keepAlive; t["onStop"] = (double)onStop;
            return DynValue.NewTable(t);
        });
        s.Globals["harness"] = DynValue.NewTable(h);
    }
}
