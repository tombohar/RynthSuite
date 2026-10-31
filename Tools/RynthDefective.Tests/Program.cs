using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthDefective;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;
using RynthCore.Plugin.RynthDefective.Views;

// The engine parser's only outside dependency: the registry's limits (the engine's values).
namespace RynthCore.Engine.UI.ScriptWindows
{
    internal static class ScriptWindowRegistry
    {
        public const int MaxOpsPerWindow = 1500;
        public const int MaxBytesPerWindow = 64 * 1024;
        public const int MaxWindowsPerOwner = 32;
        public const int MaxBytesPerSubmit = 256 * 1024;
    }
}

namespace RynthDefective.Tests
{
    internal static partial class Program
    {
        private static int _pass, _fail;
        private static string _temp = "";

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Write")]
        private static extern int Write(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_submit")]
        private static extern ref byte[] SubmitBuffer(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Apply")]
        private static extern void Apply(UiHost h, ReadOnlySpan<byte> data);

        private static int Main()
        {
            _temp = Path.Combine(Path.GetTempPath(), "RynthDefective.Tests." + Environment.ProcessId);
            Directory.CreateDirectory(_temp);
            try
            {
                Run("kinds: kits, wands by element, shields, ammunition, armor", KindsClassify);
                Run("kinds: actions and stance by kind", KindsActions);
                Run("match: the exact object, else the same name / wcid / material", MatchRebind);
                Run("match: counts, uses left, the smaller stack first", MatchCounts);
                Run("match: slots follow their items; nothing is cleared", MatchMaintain);
                Run("match: items by what the player typed", MatchByName);
                Run("picker: groups, order, search words, kind filter", PickerGroups);
                Run("picker: favourites and recent weapons first", PickerShortcuts);
                Run("pack list: one line per kind of item", PackLists);
                Run("commands: /rdef parsing", Commands);
                Run("replies: the shared queue (tag, colours, retry, drop, quiet)", ReplyQueue);
                Run("replies: every /rdef verb answers, typed right or wrong", ReplyEveryVerb);
                Run("store: save and load", StoreRoundTrip);
                Run("store: odd files", StoreOddFiles);
                Run("hotkeys: the engine's file read for the key under a slot", HotkeyFile);
                Run("session: set from selected, clear, move", SessionSlots);
                Run("session: bind and unbind through /rc hotkey", SessionKeys);
                Run("session: favourites and the status line", SessionMisc);
                Run("use: a kit on yourself, answered by UseDone", UseKit);
                Run("use: one at a time, the newest wish wins, a held key queues once", UseQueue);
                Run("use: busy, animating and RynthAi waiting hold a request, then it gives up", UseBusy);
                Run("busy rule: a leftover count, our own use, animation, a count that just changed", BusyRuleDecisions);
                Run("use: a busy count left by a death or a login doesn't hold the bar", UseStaleBusy);
                Run("use: 'You're too busy' from the server: one more try", UseTooBusyRetry);
                Run("busy rule with RynthAi holding: its own action and our last use hold, the rest of the count doesn't", BusyRulePaced);
                Run("use: the player's turn - after a death the press goes next, RynthAi holds and is let go", UsePlayersTurn);
                Run("use: just after a portal the client takes nothing for a few seconds - the press waits and goes when it can", UseAfterPortal);
                Run("spells: the table's names and flags, the name search", SpellTableAndSearch);
                Run("spells: whom a cast goes to", SpellCastTarget);
                Run("spells: a slot casts, Magic mode first, refusals", SpellUse);
                Run("spells: putting a spell in a slot (by id, by name), the command", SpellSession);
                Run("store: a spell slot saved and read; files from before read unchanged", SpellStore);
                Run("window: a spell slot on the script-window bar and in its editor", SpellWindow);
                Run("use: no UseDone, the quiet release and the time limit", UseUnanswered);
                Run("use: a slot whose object is gone uses the next of its name", UseRebind);
                Run("use: by name, missing items, target needed", UseByNameAndTargets);
                Run("use: wield, confirmed by the item in hand", UseWield);
                Run("use: wield refusals and time-outs", UseWieldFails);
                Run("use: RynthAi's macro and weapon swaps", UseBot);
                Run("use: the stance after a swap", UseStance);
                Run("use: logout and portal space drop everything", UseReset);
                Run("window: the bar's display list parses in the engine", WindowBar);
                Run("window: editor, pack list and settings parse", WindowEditor);
                Run("window: the picker parses and a click wields", WindowPicker);
                Run("window: a bar button of its own, one-shot size request", WindowHeader);
                Run("window: slot clicks and editor events reach their code", WindowEvents);
                Run("window: lists stay inside the engine's limits", WindowLimits);
                Run("bar: the game's slots and own slots, usual and independent", BarMappingUsualAndIndependent);
                Run("bar: a healing kit in one of the game's slots is used on you here", BarRetailKit);
                Run("bar: making it independent offers to copy the game's 18 slots", BarIndependentCopy);
                Run("store: the independent bar setting; files from before read it off", BarStoreIndependent);
                Run("window: the settings window parses and its switches reach their code", BarSettingsWindow);
                Run("corpses: the server's lines", CorpseLinesParse);
                Run("corpses: a death, the server's line before or after it, nothing dropped", CorpseDeaths);
                Run("corpses: a corpse in view is the death beside it, else one found", CorpseSeen);
                Run("corpses: gone, rotted, at most 20, forget", CorpseGoneAndExpiry);
                Run("corpses: the files, this character's and the others'", CorpseStoreFiles);
                Run("corpses: rows: places, times, distance and the arrow", CorpseRowText);
                Run("quantity: typed numbers, max and half", QtyParse);
                Run("quantity: a split leaves one, a give takes up to the stack, the box keeps what was typed", QtyCounts);
                Run("quantity: a vendor's price (stacks, notes, rounding, a player's listing)", QtyVendorPrice);
                Run("quantity: the most you can buy (money, slots, burden, stock)", QtyVendorMax);
                Run("quantity: why a buy can't go, and the engine's refusals in words", QtyVendorRefusals);
                Run("quantity: /rdef qty, split, give and buy parsing", QtyCommands);
                Run("quantity: split - refusals, the pack, the open chest, the server's answer", QtySplit);
                Run("quantity: give - the last player or NPC selected, all of it, refusals", QtyGive);
                Run("quantity: buy - the selected item or by name, checks first, the vendor's answer", QtyBuy);
                Run("quantity: /rdef qty and the Selection window's view", QtyBoxAndView);
                Run("quantity: a server's bank - its values, pack + bank, one that can't be read, max", QtyBankRules);
                Run("quantity: buy and max with no bank, a bank that covers it, one that doesn't, one that can't be read", QtyBankBuy);
                Run("store: the quantity box setting; files from before read it on", QtyStore);
            }
            finally
            {
                try { Directory.Delete(_temp, true); } catch { }
            }
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            if (_pass == 0) { Console.WriteLine("ABORT: zero assertions ran."); return 1; }
            return _fail == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            int before = _fail;
            try { test(); }
            catch (Exception ex) { _fail++; Console.WriteLine($"FAIL: {name} threw {ex}"); }
            Console.WriteLine($"  {(before == _fail ? "ok  " : "FAIL")} {name}");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Console.WriteLine("FAIL: " + what); }
        }

        private static void Eq<T>(T actual, T expected, string what) =>
            Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

        // ── Sample items ────────────────────────────────────────────────────

        private const uint Me = 0x50000001;

        private static CarriedItem Item(uint id, string name, int stack = 1, int maxStack = 1, uint wcid = 0, int material = 0)
        {
            return new CarriedItem { Id = id, Name = name, Stack = stack, MaxStack = maxStack, Wcid = wcid, Material = material };
        }

        private static CarriedItem Kit(uint id, string name = "Peerless Healing Kit", int uses = 28, uint wcid = 628)
        {
            var it = Item(id, name, wcid: wcid);
            it.Bitfield = ItemKinds.BitfieldHealer;
            it.MaxUses = 30;
            it.Uses = uses;
            it.Icon = 0x06001234;
            return it;
        }

        private static CarriedItem Potion(uint id, string name = "Health Potion", int stack = 5)
        {
            var it = Item(id, name, stack, 100, wcid: 1000);
            it.ItemType = 0x80;   // Misc
            it.Icon = 0x06001111;
            return it;
        }

        private static CarriedItem Melee(uint id, string name, int material = 0, bool wielded = false)
        {
            var it = Item(id, name, wcid: 300, material: material);
            it.ItemType = ItemKinds.TypeMelee;
            it.ValidLocations = 0x00100000;
            it.CombatUse = 1;
            if (wielded) it.WieldedLocation = 0x00100000;
            return it;
        }

        private static CarriedItem Bow(uint id, string name = "Yumi", bool wielded = false)
        {
            var it = Item(id, name, wcid: 400);
            it.ItemType = ItemKinds.TypeMissile;
            it.ValidLocations = 0x00400000;
            it.CombatUse = 2;
            if (wielded) it.WieldedLocation = 0x00400000;
            return it;
        }

        private static CarriedItem Arrows(uint id, bool wielded = false)
        {
            var it = Item(id, "Arrow", 200, 250, wcid: 500);
            it.ItemType = ItemKinds.TypeMissile;
            it.ValidLocations = ItemKinds.AmmoSlot;
            it.CombatUse = ItemKinds.CombatUseAmmo;
            if (wielded) it.WieldedLocation = ItemKinds.AmmoSlot;
            return it;
        }

        private static CarriedItem Wand(uint id, string name, int damageType = 0x10, int material = 0, bool wielded = false)
        {
            var it = Item(id, name, wcid: 600, material: material);
            it.ItemType = ItemKinds.TypeCaster;
            it.ValidLocations = 0x01000000;
            it.DamageType = damageType;
            if (wielded) it.WieldedLocation = 0x01000000;
            return it;
        }

        private static CarriedItem Shield(uint id, string name = "Kite Shield", bool wielded = false)
        {
            var it = Item(id, name, wcid: 700);
            it.ItemType = ItemKinds.TypeArmor;
            it.ValidLocations = ItemKinds.ShieldSlot;
            it.CombatUse = ItemKinds.CombatUseShield;
            if (wielded) it.WieldedLocation = ItemKinds.ShieldSlot;
            return it;
        }

        // ── A scripted game ─────────────────────────────────────────────────

        internal sealed class FakeWorld : IDefWorld
        {
            public long NowMs { get; set; } = 1_000_000;
            public bool InWorld { get; set; } = true;
            public uint PlayerId => Me;
            public uint SelectedId { get; set; }
            public int BusyCount { get; set; }
            /// <summary>The busy count above zero (1) or idle; set BusyCount for a count of its own.</summary>
            public bool ClientBusy { get => BusyCount > 0; set => BusyCount = value ? Math.Max(1, BusyCount) : 0; }
            public bool Animating { get; set; }
            public bool? BotWaitingOnServer { get; set; }
            public bool? BotMacroRunning { get; set; }
            /// <summary>RynthAi has RynthAi.Yield with its pacing on (off: a RynthAi from before, as the older tests assume).</summary>
            public bool YieldOn;
            /// <summary>RynthAi's own action is unanswered (read only while YieldOn).</summary>
            public bool BotOwnAction;
            public bool? BotPaced => YieldOn ? BotOwnAction : null;
            /// <summary>The asks for the player's turn, the releases, and whether RynthAi holds now.</summary>
            public int HoldAsks, Releases;
            public bool BotHeldNow;
            public long HeldUntil;
            public bool HoldBot(uint ms)
            {
                if (!YieldOn) return false;
                HoldAsks++;
                BotHeldNow = true;
                HeldUntil = NowMs + ms;
                return true;
            }
            public void ReleaseBot()
            {
                Releases++;
                BotHeldNow = false;
            }
            public int UseDoneSeq { get; set; } = 1;
            public int CombatMode { get; set; } = Stance.NonCombat;
            public int RefusalSeq { get; set; }
            public uint RefusedObject;
            /// <summary>The error of the newest UseDone (Answer sets it).</summary>
            public uint LastUseDoneError;
            public List<CarriedItem> Inventory = new();
            public bool RefuseSends;
            public readonly List<string> Sent = new();

            public bool RefusalNamed(uint objectId) => RefusedObject == objectId;
            public uint UseDoneError(int seqAtSend) => UseDoneSeq == seqAtSend + 1 ? LastUseDoneError : 0;
            public IReadOnlyList<CarriedItem> Items() => Inventory;
            public CarriedItem? Live(uint id) => Inventory.FirstOrDefault(i => i.Id == id);

            public bool UseObject(uint id)
            {
                if (RefuseSends) return false;
                Sent.Add($"use {id:X}");
                return true;
            }

            public bool UseObjectOn(uint id, uint target)
            {
                if (RefuseSends) return false;
                Sent.Add($"useon {id:X} {(target == Me ? "me" : target.ToString("X"))}");
                return true;
            }

            public bool SetCombatMode(int mode)
            {
                Sent.Add("mode " + mode);
                if (!ModeRefused) CombatMode = mode;
                return true;
            }

            /// <summary>The client asked for a mode but stays where it is (a weapon that can't cast in hand).</summary>
            public bool ModeRefused;
            public bool CanCast { get; set; } = true;
            /// <summary>The spells the character knows; null = the spellbook can't be read.</summary>
            public HashSet<uint>? Known;
            public bool? KnowsSpell(uint spellId) => Known == null ? null : Known.Contains(spellId);

            public bool CastSpell(uint target, uint spellId)
            {
                if (RefuseSends) return false;
                Sent.Add($"cast {spellId} {(target == Me ? "me" : target.ToString("X"))}");
                return true;
            }

            public void Advance(long ms) => NowMs += ms;
            public void Answer(uint error = 0)
            {
                UseDoneSeq++;
                LastUseDoneError = error;
            }
        }

        private sealed class Harness
        {
            public readonly FakeWorld World = new();
            public readonly UseController Use = new();
            public readonly List<string> Said = new(), Warned = new(), Logged = new();
            public readonly List<CarriedItem> Wielded = new();

            public Harness()
            {
                Use.Say = Said.Add;
                Use.Warn = Warned.Add;
                Use.Log = Logged.Add;
                Use.Wielded = Wielded.Add;
            }

            public void Tick(long advance = 0)
            {
                World.Advance(advance);
                Use.Tick(World);
            }

            public void Ticks(int count, long step)
            {
                for (int i = 0; i < count; i++) Tick(step);
            }

            public bool Slot(ItemRef r, UseMode mode = UseMode.Auto) =>
                Use.Request(new UseRequest(r, null, mode, "slot", World.NowMs));

            public bool Named(string name) =>
                Use.Request(new UseRequest(null, name, UseMode.Auto, "name", World.NowMs));
        }
    }
}
