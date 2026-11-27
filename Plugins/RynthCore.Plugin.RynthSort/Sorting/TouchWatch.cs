using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>How the player last touched an item (what the window says).</summary>
internal enum TouchKind : byte
{
    /// <summary>It changed packs, and RynthSort didn't move it.</summary>
    Moved,
    /// <summary>It is (or was) selected in the game.</summary>
    Selected,
    /// <summary>It was dragged in AC's windows.</summary>
    Dragged,
    /// <summary>It was worn or wielded and is in a pack now.</summary>
    TakenOff,
    /// <summary>It turned up in a pack just after a drop (picked up, or a stack split off).</summary>
    PickedUp,
    /// <summary>The player asked RynthSort to leave it alone (the window's "Leave it alone").</summary>
    Chosen,
    /// <summary>The player sent it to a pack from RynthSort's window, without a rule ("Just move it").</summary>
    Sent,
}

/// <summary>
/// "Leave my items alone": which items the player used lately, so sorting leaves them where they
/// are for the hold time (#rynth-issues "The bag sort feature is lacking in UX": "it moves the
/// items you're working with into one of 9 bags"). An item counts as touched when
/// <list type="bullet">
/// <item>it is selected in the game (the plugin polls the selection every tick, so it stays
/// touched while it is selected and for the hold time after);</item>
/// <item>the player drags it in AC's windows (engine API v77 drag and drop hooks);</item>
/// <item>between two reads of the packs it changed packs and RynthSort didn't send it there
/// (the player, or another plugin, moved it);</item>
/// <item>it was worn or wielded at the last read and is in a pack now (taken off);</item>
/// <item>it is new in the packs within <see cref="DropWindowMs"/> of a drop (picked up from the
/// ground, or a stack split off one being dragged: the new stack has a new id).</item>
/// </list>
/// Moves inside one pack (a slot change) aren't seen by the reads; a drag catches those. Pure (no
/// host calls): the controller feeds it the reads and its own moves, the plugin the selection and
/// the drags; the tests drive it by hand.
/// </summary>
internal sealed class TouchWatch
{
    /// <summary>A pack change this soon after RynthSort's own move of the item to that pack is RynthSort's.</summary>
    public const long OwnMoveMs = 30_000;
    /// <summary>New items this soon after a drop are the player's.</summary>
    public const long DropWindowMs = 3000;
    /// <summary>An item missing from the reads is remembered this long (a pack not listed for a moment).</summary>
    public const long ForgetMs = 60_000;
    /// <summary>The "container" of a worn or wielded item.</summary>
    public const uint WornMarker = 0xFFFFFFFE;
    /// <summary>The longest hold the settings offer (old touches are dropped after it).</summary>
    public const long MaxHoldMs = 60 * 60_000;

    private readonly Dictionary<uint, (uint Container, long SeenMs)> _where = new();
    private readonly Dictionary<uint, (long AtMs, TouchKind Kind)> _touched = new();
    private readonly Dictionary<uint, (uint Dest, long AtMs)> _ours = new();
    private long _droppedAtMs = long.MinValue / 2;
    private bool _primed;

    /// <summary>Forget everything (logout, another character).</summary>
    public void Reset()
    {
        _where.Clear();
        _touched.Clear();
        _ours.Clear();
        _droppedAtMs = long.MinValue / 2;
        _primed = false;
    }

    /// <summary>"Sort them too": every item is fair game again (new touches still count).</summary>
    public void ReleaseAll() => _touched.Clear();

    /// <summary>"Let RynthSort move it": <paramref name="itemId"/> is fair game again (a new touch still counts). False when it wasn't held.</summary>
    public bool Release(uint itemId) => _touched.Remove(itemId);

    /// <summary>The player asked for <paramref name="itemId"/> to be left alone from now (the window's "Leave it alone", "Just move it").</summary>
    public void Hold(uint itemId, long nowMs, TouchKind kind)
    {
        if (itemId != 0) Touch(itemId, nowMs, kind);
    }

    /// <summary>RynthSort sent <paramref name="itemId"/> to <paramref name="destId"/>: its arrival there isn't the player's.</summary>
    public void NoteOwnMove(uint itemId, uint destId, long nowMs) => _ours[itemId] = (destId, nowMs);

    /// <summary>The game's selection now (0 = none). Only items in the packs count (not monsters, corpses or packs).</summary>
    public void NoteSelected(uint objectId, long nowMs)
    {
        if (objectId == 0 || !_where.TryGetValue(objectId, out var at) || at.Container == WornMarker) return;
        Touch(objectId, nowMs, TouchKind.Selected);
    }

    /// <summary>The player started dragging <paramref name="objectId"/> (OnDragStart).</summary>
    public void NoteDrag(uint objectId, long nowMs)
    {
        if (objectId != 0) Touch(objectId, nowMs, TouchKind.Dragged);
    }

    /// <summary>The player dropped <paramref name="objectId"/> somewhere (OnItemDropped): new items for a moment after are theirs too.</summary>
    public void NoteDrop(uint objectId, long nowMs)
    {
        if (objectId != 0) Touch(objectId, nowMs, TouchKind.Dragged);
        _droppedAtMs = nowMs;
    }

    /// <summary>
    /// A read of the packs: items that changed packs without RynthSort, were taken off, or are new
    /// just after a drop count as touched. The first read after a reset only learns where things are.
    /// </summary>
    public void Observe(SortInventory inv, long nowMs)
    {
        void See(uint id, uint container)
        {
            if (_primed)
            {
                if (_where.TryGetValue(id, out var was))
                {
                    if (was.Container != container)
                    {
                        if (_ours.TryGetValue(id, out var own) && own.Dest == container && nowMs - own.AtMs < OwnMoveMs)
                            _ours.Remove(id);
                        else
                            Touch(id, nowMs, was.Container == WornMarker ? TouchKind.TakenOff : TouchKind.Moved);
                    }
                }
                else if (nowMs - _droppedAtMs < DropWindowMs)
                {
                    Touch(id, nowMs, TouchKind.PickedUp);
                }
            }
            _where[id] = (container, nowMs);
        }

        foreach (SortItem it in inv.Main.Items) See(it.Id, inv.Main.Id);
        foreach (SortPack p in inv.Packs)
            foreach (SortItem it in p.Items) See(it.Id, p.Id);
        foreach (uint id in inv.Worn) See(id, WornMarker);
        _primed = true;
        Prune(nowMs);
    }

    /// <summary>Whether <paramref name="itemId"/> was touched within <paramref name="holdMs"/> (0 = holding is off).</summary>
    public bool IsHeld(uint itemId, long nowMs, long holdMs) => TryGetHeld(itemId, nowMs, holdMs, out _, out _);

    /// <summary>When and how <paramref name="itemId"/> was last touched, if that is within <paramref name="holdMs"/>.</summary>
    public bool TryGetHeld(uint itemId, long nowMs, long holdMs, out long atMs, out TouchKind kind)
    {
        atMs = 0;
        kind = TouchKind.Moved;
        if (holdMs <= 0 || !_touched.TryGetValue(itemId, out var t) || nowMs - t.AtMs >= holdMs) return false;
        atMs = t.AtMs;
        kind = t.Kind;
        return true;
    }

    /// <summary>How many touches are still within <paramref name="holdMs"/> (items anywhere, not only in the packs).</summary>
    public int HeldCount(long nowMs, long holdMs)
    {
        if (holdMs <= 0) return 0;
        int n = 0;
        foreach (var t in _touched.Values) if (nowMs - t.AtMs < holdMs) n++;
        return n;
    }

    private void Touch(uint id, long nowMs, TouchKind kind) => _touched[id] = (nowMs, kind);

    private List<uint>? _gone;

    private void Prune(long nowMs)
    {
        _gone ??= new List<uint>();
        _gone.Clear();
        foreach (var kv in _where) if (nowMs - kv.Value.SeenMs > ForgetMs) _gone.Add(kv.Key);
        foreach (uint id in _gone) _where.Remove(id);
        _gone.Clear();
        foreach (var kv in _touched) if (nowMs - kv.Value.AtMs > MaxHoldMs) _gone.Add(kv.Key);
        foreach (uint id in _gone) _touched.Remove(id);
        _gone.Clear();
        foreach (var kv in _ours) if (nowMs - kv.Value.AtMs > OwnMoveMs) _gone.Add(kv.Key);
        foreach (uint id in _gone) _ours.Remove(id);
    }

    /// <summary>"you moved it", "you have it selected" ...: how the window says it.</summary>
    public static string Words(TouchKind kind) => kind switch
    {
        TouchKind.Selected => "you selected it",
        TouchKind.Dragged => "you dragged it",
        TouchKind.TakenOff => "you took it off",
        TouchKind.PickedUp => "you picked it up",
        TouchKind.Chosen => "you asked to leave it",
        TouchKind.Sent => "you sent it there",
        _ => "you moved it",
    };
}
