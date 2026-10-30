using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// game.World's UtilityBelt extras: the chat events by kind (tell, local, channel, fellow,
/// emote), OnChatInput, the open container and its events, and the Vendor object.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    // ── Chat lines by kind ──────────────────────────────────────────────────

    internal enum ChatKind { Other, Tell, Local, Channel, Fellow, Emote }

    /// <summary>"[Room] ..." prefixes → UB's ChatChannel numbers.</summary>
    private static readonly (string Tag, int Room)[] ChatRooms =
    {
        ("Allegiance", 1), ("General", 2), ("Trade", 3), ("LFG", 4), ("Roleplay", 5), ("Olthoi", 6),
        ("Society", 7), ("Celestial Hand", 8), ("Eldrytch Web", 9), ("Radiant Blood", 10),
    };

    private const int ChatTypeSpeech = 2, ChatTypeTell = 3, ChatTypeEmote = 12;

    /// <summary>
    /// What kind of line this is, from AC's text: "[General] Bob says, \"...\"" is a channel
    /// line (Room from the tag), "[Fellowship] ..." a fellowship line, "Bob tells you, ..."
    /// (type Tell) a tell, "Bob says, ..." (type Speech) a local message, and a type Emote line
    /// an emote. SenderName / SenderId are "" / 0 when the line names nobody.
    /// </summary>
    internal static ChatKind ClassifyChat(string line, int chatType, out int room, out string senderName, out uint senderId, out string body)
    {
        room = 0;
        string rest = line;
        string? tag = null;
        if (line.StartsWith('['))
        {
            int close = line.IndexOf(']');
            if (close > 1)
            {
                tag = line[1..close];
                rest = line[(close + 1)..].TrimStart();
            }
        }
        ParseChatSender(rest, out senderName, out senderId, out body, out bool isTell);
        if (tag != null)
        {
            if (tag.Equals("Fellowship", StringComparison.OrdinalIgnoreCase)) return ChatKind.Fellow;
            foreach (var (t, r) in ChatRooms)
                if (t.Equals(tag, StringComparison.OrdinalIgnoreCase)) { room = r; return ChatKind.Channel; }
            return ChatKind.Other;
        }
        if (isTell && chatType == ChatTypeTell) return ChatKind.Tell;   // anything else only looks like one
        if (!isTell && senderName.Length > 0 && chatType == ChatTypeSpeech) return ChatKind.Local;
        if (chatType == ChatTypeEmote) return ChatKind.Emote;
        return ChatKind.Other;
    }

    // ── World.OnChatInput ───────────────────────────────────────────────────
    // AC hands typed lines to plugins on its own thread, where Lua can't run, so handlers see
    // a line on the next tick and can't stop that line any more. What they can do, the UB way,
    // is set e.Eat = true for a line starting with "/word": from then on RynthLua eats every
    // line starting with that word as it is typed (until the script stops), and handlers get
    // those lines with Eat already true. game.RegisterCommand eats from the first line.

    private readonly ConcurrentQueue<(string Text, bool Eaten)> _worldChatInputs = new();
    /// <summary>"/word" → the script whose handler ate it.</summary>
    private readonly ConcurrentDictionary<string, string> _worldEatenCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Table Args, ScriptContext Ctx, string Word, long Until)> _worldChatInputWatch = new();

    private static string CommandWord(string line)
    {
        if (!line.StartsWith('/')) return string.Empty;
        int sp = line.IndexOf(' ');
        return sp < 0 ? line : line[..sp];
    }

    /// <summary>AC's thread: queue the line for OnChatInput; true when a learned command eats it.</summary>
    private bool WorldQueueChatInput(string line, bool alreadyEaten)
    {
        bool eat = !alreadyEaten && !_worldEatenCommands.IsEmpty && CommandWord(line) is { Length: > 0 } w && _worldEatenCommands.ContainsKey(w);
        _worldChatInputs.Enqueue((line, alreadyEaten || eat));   // drained every tick
        return eat;
    }

    /// <summary>Pump thread, every tick: deliver queued input lines, learn eaten commands.</summary>
    private void WorldTickChatInput()
    {
        long now = Environment.TickCount64;
        for (int i = _worldChatInputWatch.Count - 1; i >= 0; i--)
        {
            var (args, ctx, word, until) = _worldChatInputWatch[i];
            if (args.Get("Eat").CastToBool() && word.Length > 0 && ctx.Host.Loaded)
            {
                _worldEatenCommands[word] = ctx.Name;
                _worldChatInputWatch.RemoveAt(i);
            }
            else if (now > until || !ctx.Host.Loaded) _worldChatInputWatch.RemoveAt(i);
        }

        while (_worldChatInputs.TryDequeue(out var input))
        {
            string word = CommandWord(input.Text);
            foreach (var ctx in new List<ScriptContext>(_scripts.Values))
            {
                if (!ctx.Host.Loaded || !ctx.HasHandlers("World.OnChatInput")) continue;
                ctx.Fire("World.OnChatInput", s =>
                {
                    var t = new Table(s);
                    t["Text"] = input.Text;
                    t["Eat"] = input.Eaten;
                    if (!input.Eaten && word.Length > 0) _worldChatInputWatch.Add((t, ctx, word, now + 3000));
                    return DynValue.NewTable(t);
                });
            }
        }
    }

    // ── The open container ──────────────────────────────────────────────────

    private uint _worldOpenContainer;

    /// <summary>The engine's view/stop-viewing events, and every tick's check of the ground container (whichever is first).</summary>
    private void WorldSetOpenContainer(uint id)
    {
        if (id == _worldOpenContainer) return;
        uint old = _worldOpenContainer;
        _worldOpenContainer = id;
        if (old != 0)
            FireWorld("World.OnContainerClosed", (s, ctx) =>
            {
                var t = new Table(s);
                t["Container"] = WrapObject(s, ctx, old);
                return DynValue.NewTable(t);
            });
        if (id != 0)
        {
            _worldKnown.Add(id);
            FireWorld("World.OnContainerOpened", (s, ctx) =>
            {
                var t = new Table(s);
                t["Container"] = WrapObject(s, ctx, id);
                return DynValue.NewTable(t);
            });
        }
    }

    public override void OnViewObjectContents(uint objectId)
    {
        // Only containers on the ground count (the engine also reports the player's own packs).
        if (objectId != 0 && objectId == (Host.HasGetGroundContainerId ? Host.GetGroundContainerId() : objectId))
            WorldSetOpenContainer(objectId);
    }

    public override void OnStopViewingObjectContents(uint objectId)
    {
        if (objectId == _worldOpenContainer) WorldSetOpenContainer(0);
    }

    private void WorldPollOpenContainer()
    {
        if (!Host.HasGetGroundContainerId || GameState != "InGame") return;
        WorldSetOpenContainer(Host.GetGroundContainerId());
    }

    // ── game.World.Vendor ───────────────────────────────────────────────────

    partial void WorldOnVendor(uint vendorId, bool open)
    {
        if (!open) { Cart.ClearBuy(); Cart.ClearSell(); }
        FireAll(open ? "World.Vendor.OnOpened" : "World.Vendor.OnClosed", s => DynValue.NewTable(new Table(s)));
    }

    /// <summary>
    /// UB's Vendor: IsOpen, VendorId, Name, MinBuyValue, MaxBuyValue, Magic, BuyPriceModifier,
    /// SellPriceModifier, CurrencyType, CurrencyName, PlayerCurrencyAmount, Category (ObjectType),
    /// Items ({ObjectId, Name, Amount (-1 = unlimited), UnitPrice, StackSize, MaxStackSize,
    /// ObjectType}), OnOpened, OnClosed. Read live from the engine's vendor snapshot (API v67).
    /// </summary>
    private DynValue CreateVendorTable(Script s, ScriptContext ctx)
    {
        var v = new Table(s);
        v["OnOpened"] = ctx.Event("World.Vendor.OnOpened").CreateTable(s);
        v["OnClosed"] = ctx.Event("World.Vendor.OnClosed").CreateTable(s);
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            bool open = Host.TryGetVendorInfo(out var info);
            switch (key)
            {
                case "IsOpen": return DynValue.NewBoolean(open);
                case "VendorId": return DynValue.NewNumber(open ? info.VendorId : 0);
                case "Name": return DynValue.NewString(open ? info.Name : string.Empty);
                case "MinBuyValue": return DynValue.NewNumber(open ? info.MinValue : 0);
                case "MaxBuyValue": return DynValue.NewNumber(open ? info.MaxValue : 0);
                case "Magic": return DynValue.NewNumber(open && info.DealsMagic ? 1 : 0);
                case "BuyPriceModifier": return DynValue.NewNumber(open ? info.BuyRate : 0);
                case "SellPriceModifier": return DynValue.NewNumber(open ? info.SellRate : 0);
                case "CurrencyType": return DynValue.NewNumber(open ? info.AltCurrencyWcid : 0);
                case "CurrencyName":
                    return DynValue.NewString(!open ? string.Empty : info.UsesAltCurrency ? info.AltCurrencyName : "Pyreal");
                case "PlayerCurrencyAmount":
                    return DynValue.NewNumber(!open ? 0 : info.UsesAltCurrency ? Math.Max(0, info.AltCurrencyHave) : Math.Max(0, info.PlayerCoins));
                case "Category": return LuaEnumValues.Get(s, "ObjectType", open ? info.ItemTypes : 0);
                case "Items":
                {
                    var list = new Table(s);
                    int i = 0;
                    if (open)
                        foreach (var item in Host.GetVendorItems())
                        {
                            var t = new Table(s);
                            t["ObjectId"] = (double)item.ObjectId;
                            t["Name"] = item.Name;
                            t["Amount"] = (double)item.Amount;
                            t["UnitPrice"] = (double)item.UnitPrice;
                            t["StackSize"] = (double)item.StackSize;
                            t["MaxStackSize"] = (double)item.MaxStackSize;
                            t["ObjectType"] = LuaEnumValues.Get(s, "ObjectType", item.ItemType);
                            list.Set(++i, DynValue.NewTable(t));
                        }
                    return LuaLists.Wrap(s, list);
                }
                default: return DynValue.Nil;
            }
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
            DynValue.NewString(Host.TryGetVendorInfo(out var info) ? $"Vendor {info.Name} (0x{info.VendorId:X8})" : "Vendor (closed)"));
        v.MetaTable = mt;
        return DynValue.NewTable(v);
    }
}
