using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Screen;

// What the RynthSort window shows (2026-11-26, the approved design for #rynth-issues "The bag sort
// feature is lacking in UX"): built on the plugin tick by ScreenBuilder from the controller, the
// last read of the packs and the rules, published whole, and drawn by Window/SortWindow on AC's
// render thread. Never changed after it is published: the renderer only reads it. Every string the
// renderer draws is made here (or once when a popup opens), not per frame.

/// <summary>The coloured pill beside the status line.</summary>
internal enum PillTone : byte
{
    /// <summary>Grey: auto-sort is off.</summary>
    Off,
    /// <summary>Blue: items are moving.</summary>
    Moving,
    /// <summary>Amber: something waits (settling, paused, stuck, no rules).</summary>
    Waiting,
    /// <summary>Teal: on and tidy.</summary>
    Tidy,
}

internal sealed class StatusModel
{
    public string Line = string.Empty;
    public string Pill = "Off";
    public PillTone Tone;
    /// <summary>The status line's tooltip (the whole line, and what the colours mean).</summary>
    public string Tip = string.Empty;
}

/// <summary>Where a tile's item belongs by the rules.</summary>
internal enum TileRule : byte
{
    /// <summary>No rule names it: it stays in the main pack.</summary>
    None,
    /// <summary>A rule sends it to a side pack the character carries (<see cref="TileModel.RulePack"/>).</summary>
    Pack,
    /// <summary>Its rule names a pack the character doesn't carry: it stays where it is.</summary>
    Missing,
    /// <summary>A side pack's job picks it (<see cref="TileModel.RulePack"/>, the job in <see cref="TileModel.RuleText"/>).</summary>
    Job,
}

/// <summary>One item in a pack: its picture, count and what is happening to it.</summary>
internal sealed class TileModel
{
    public uint Id;
    public string Name = string.Empty;
    /// <summary>The count in the corner ("", "75", "2.4k").</summary>
    public string Qty = string.Empty;
    public int Stack = 1;
    public int Cls;
    /// <summary>The kind's colour (ARGB): the tile's background while its picture is being made.</summary>
    public uint KindColor;
    /// <summary>"Gems": the kind as a chip says it.</summary>
    public string KindLabel = string.Empty;
    /// <summary>"AO": the letters shown while its picture is being made.</summary>
    public string Initials = string.Empty;
    /// <summary>The pack it is in: 0 = main pack, n = side pack n.</summary>
    public int Where;
    public TileRule Rule;
    /// <summary>The side pack its rule sends it to (1-based) when <see cref="Rule"/> is Pack.</summary>
    public int RulePack;
    /// <summary>The rule entry it follows, as a chip says it ("Gems", <c>named "Opal"</c>), or "".</summary>
    public string RuleText = string.Empty;
    /// <summary>It follows a name rule (a name beats a kind).</summary>
    public bool ByName;
    /// <summary>Left alone: the player used it lately (or asked).</summary>
    public bool Held;
    /// <summary>Why it is left alone ("you selected it just now").</summary>
    public string HeldWhy = string.Empty;
    /// <summary>A sort would move it to another pack now (it is in the Next moves list).</summary>
    public bool Outgoing;
    /// <summary>It is being moved, or the sort running now is about to move it: the tile lifts and pulses.</summary>
    public bool Moving;
    /// <summary>New in the main pack and still inside the settle time: the spinner ring.</summary>
    public bool Settling;
}

/// <summary>A slot an item a sort would bring will take (the dashed "incoming" ghost).</summary>
internal sealed class GhostModel
{
    public uint ItemId;
    /// <summary>"Black Opal is coming here".</summary>
    public string Tip = string.Empty;
}

/// <summary>One entry of a pack's rules: a kind or a name, with its ×.</summary>
internal sealed class ChipModel
{
    /// <summary>The side pack it sends to (1-based); 0 for a rule whose pack isn't carried.</summary>
    public int Pack;
    /// <summary>The rule it belongs to (SortRule.Uid).</summary>
    public int RuleUid;
    public bool IsName;
    public int Cls;
    /// <summary>The name part (name chips).</summary>
    public string Name = string.Empty;
    /// <summary>"Gems", <c>named "Opal"</c>.</summary>
    public string Label = string.Empty;
    /// <summary>The kind's colour (ARGB) for the dot (kind chips).</summary>
    public uint Dot;
    /// <summary>The rule is turned off (shown faded; a click turns it on).</summary>
    public bool Off;
    /// <summary>An overflow pack's kind: another pack's rule has it first, and this pack takes them when that one is full.</summary>
    public bool Overflow;
    public string Tip = string.Empty;
}

internal sealed class PackModel
{
    /// <summary>1-based, top pack first when the pack column's order is known.</summary>
    public int Number;
    public uint Id;
    public string Name = string.Empty;
    /// <summary>What a sentence calls it: <see cref="Name"/>, with its number when another pack has the same name ("Satchel (2)").</summary>
    public string Label = string.Empty;
    /// <summary>The bag badge's number ("1").</summary>
    public string Badge = string.Empty;
    /// <summary>"Top of your pack column", "Pack 2 from the top".</summary>
    public string Where = string.Empty;
    /// <summary>"12/24".</summary>
    public string Count = string.Empty;
    public int Used;
    public int Capacity;
    public float Fill;
    /// <summary>Over 85 % full: the meter turns amber.</summary>
    public bool NearlyFull;
    /// <summary>Every slot taken (its real capacity): the meter turns red.</summary>
    public bool Full;
    /// <summary>"+3 coming", or "".</summary>
    public string Incoming = string.Empty;
    public TileModel[] Tiles = Array.Empty<TileModel>();
    public GhostModel[] Ghosts = Array.Empty<GhostModel>();
    public ChipModel[] Chips = Array.Empty<ChipModel>();
    /// <summary>Amber lines under the grid (full, a rule that uses this pack only because its own isn't carried).</summary>
    public string[] Warnings = Array.Empty<string>();
    /// <summary>The header's tooltip: which pack in the game this is.</summary>
    public string Tip = string.Empty;
    /// <summary>Its job (Salvage, Sell, Mule), None when it has none.</summary>
    public Sorting.JobKind Job;
    /// <summary>"Salvage", "Mule: Name": the pill on the card, "" with no job.</summary>
    public string JobLabel = string.Empty;
    public string JobMule = string.Empty;
    /// <summary>What the job takes, or why it can't work now (RynthAi not loaded, too old, no profile).</summary>
    public string JobLine = string.Empty;
    /// <summary><see cref="JobLine"/> says why it can't work (amber).</summary>
    public bool JobProblem;
    /// <summary>Folded down to its header and rules (0.6.1): no slot grid.</summary>
    public bool Collapsed;
}

internal sealed class MainModel
{
    /// <summary>"12 / 102".</summary>
    public string Count = string.Empty;
    public int Used;
    public int Capacity;
    public float Fill;
    public bool NearlyFull;
    /// <summary>Every slot taken: the meter turns red.</summary>
    public bool Full;
    public TileModel[] Tiles = Array.Empty<TileModel>();
    /// <summary>Empty wells after the tiles (at least three rows in all, a row past the last item).</summary>
    public int Wells;
    /// <summary>"Handy at the front: healing kits, mana stones and food." while that option works, else "".</summary>
    public string Handy = string.Empty;
    /// <summary>Folded down to its header (0.6.1): no slot grid.</summary>
    public bool Collapsed;
}

/// <summary>A Next moves row, a Left alone row or a feed row: picture, name, a line under it, and what is on the right.</summary>
internal sealed class RowModel
{
    /// <summary>The item (its picture); 0 = none (a feed line about a sort).</summary>
    public uint ItemId;
    public uint KindColor;
    public string Name = string.Empty;
    public string Sub = string.Empty;
    /// <summary>"Satchel" (Next moves: where it goes), the time (the feed), or "".</summary>
    public string Right = string.Empty;
    public string Tip = string.Empty;
}

/// <summary>A kind the "+ Add a kind..." picker offers.</summary>
internal sealed class KindChoice
{
    public int Cls;
    public string Label = string.Empty;
    public uint Dot;
    /// <summary>The side pack whose rules (that are on) have it now (1-based), 0 = none, -1 = a pack not carried.</summary>
    public int InPack;
}

/// <summary>A starter layout as the Layouts popup shows it.</summary>
internal sealed class LayoutCard
{
    public string Key = string.Empty;
    public string Name = string.Empty;
    public string Blurb = string.Empty;
    /// <summary>Per side pack (as carried), the kind colours (ARGB) of the mini bar; an empty array = the pack stays free.</summary>
    public uint[][] Bars = Array.Empty<uint[]>();
    /// <summary>"5 would move", "nothing to move".</summary>
    public string Meta = string.Empty;
    public LayoutRow[] Rows = Array.Empty<LayoutRow>();
    /// <summary>"Stay in the main pack: armor, trade notes." or "Every kind you carry has a pack."</summary>
    public string Left = string.Empty;
    /// <summary>" This replaces your rules; Undo brings them back." when the character has rules, else "".</summary>
    public string Replaces = string.Empty;
}

internal sealed class LayoutRow
{
    public int Pack;
    public string Badge = string.Empty;
    public string PackName = string.Empty;
    public ChipModel[] Chips = Array.Empty<ChipModel>();
}

/// <summary>The settings popup's values (the renderer keeps its own copy while a slider is dragged).</summary>
internal sealed class SettingsModel
{
    public int SettleSec;
    public int HoldMin;
    public int GapMs;
    public bool SortInCombat;
    public bool OrderInPacks;
    public bool KindFirst;
    /// <summary>The engine can read the order inside packs (API v80).</summary>
    public bool CanOrder;
    /// <summary>"Keep handy items at the front of the main pack" is on.</summary>
    public bool HandyOn;
    /// <summary>The kinds and names that count as handy, as chips (IsName for a name part), with their ×.</summary>
    public ChipModel[] HandyChips = Array.Empty<ChipModel>();
    /// <summary>The kinds the handy list's picker offers (the ones not in it yet).</summary>
    public KindChoice[] HandyChoices = Array.Empty<KindChoice>();
}

/// <summary>The line at the bottom after a change: what happened, and Undo when it can be undone.</summary>
internal sealed class ToastModel
{
    public string Text = string.Empty;
    public bool CanUndo;
    /// <summary>Changes with every new toast (the renderer restarts its fade).</summary>
    public int Seq;
    /// <summary>Shown until (the tick's clock, ms).</summary>
    public long UntilMs;
}

internal sealed class ScreenModel
{
    /// <summary>Logged in and the packs read: everything below is filled.</summary>
    public bool Ready;
    /// <summary>What to say instead ("Log in to sort your packs.").</summary>
    public string NotReady = string.Empty;
    /// <summary>"Character · Server".</summary>
    public string Who = string.Empty;
    public StatusModel Status = new();
    public bool Auto;
    /// <summary>A sort, Put back or Empty is running (Sort now is greyed).</summary>
    public bool Busy;
    /// <summary>How many items Put back would move back (0: no button).</summary>
    public int PutBack;
    public string PutBackTip = string.Empty;
    /// <summary>Amber lines under the header (the Lua script set to start too, AutoCram, numbering, main pack full).</summary>
    public string[] Warnings = Array.Empty<string>();
    /// <summary>The red banner: per full pack, the items that didn't fit (they stay where they are).</summary>
    public string[] NoFit = Array.Empty<string>();
    public MainModel Main = new();
    public PackModel[] Packs = Array.Empty<PackModel>();
    /// <summary>"4 carried".</summary>
    public string PacksNote = string.Empty;
    public RowModel[] Moves = Array.Empty<RowModel>();
    public int MovesCount;
    /// <summary>"Nothing moves until you press Sort now or turn on Auto-sort.", or "".</summary>
    public string MovesHint = string.Empty;
    /// <summary>"Then stacks are joined and 3 items put in order inside packs.", or "".</summary>
    public string OrderHint = string.Empty;
    public RowModel[] Held = Array.Empty<RowModel>();
    /// <summary>"3 min" (the Left alone eyebrow), "off".</summary>
    public string HoldNote = string.Empty;
    public RowModel[] Feed = Array.Empty<RowModel>();
    /// <summary>Rules whose pack isn't carried (their entries as chips, × removes).</summary>
    public ChipModel[] Orphans = Array.Empty<ChipModel>();
    public KindChoice[] KindChoices = Array.Empty<KindChoice>();
    /// <summary>The starter layouts, filled only while the Layouts popup is open.</summary>
    public LayoutCard[] Layouts = Array.Empty<LayoutCard>();
    public SettingsModel Settings = new();
    public ToastModel? Toast;
    /// <summary>The game's selection can be read (the picker's "The item selected in the game").</summary>
    public bool CanAddSelected;
    /// <summary>Items can be selected in the game (the bag badge selects its pack).</summary>
    public bool CanSelect;
    /// <summary>The character has no rules yet (the window suggests a layout).</summary>
    public bool NoRules;
    /// <summary>Every tile by item id (the detail and drop popups look items up here).</summary>
    public Dictionary<uint, TileModel> TilesById = new();
    /// <summary>Changes with every build.</summary>
    public int Version;

    public static readonly ScreenModel Empty = new() { NotReady = "Log in to sort your packs." };

    /// <summary>The side pack numbered <paramref name="number"/> (1-based), or null.</summary>
    public PackModel? Pack(int number) => number >= 1 && number <= Packs.Length ? Packs[number - 1] : null;

    /// <summary>"main pack" for 0, else the side pack's name ("Satchel", "Satchel (2)" when two share it), or "pack 5" past the packs carried.</summary>
    public string PackName(int index)
    {
        if (index == 0) return "main pack";
        PackModel? p = Pack(index);
        return p != null ? (p.Label.Length > 0 ? p.Label : p.Name) : "pack " + index;
    }
}
