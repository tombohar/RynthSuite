// ============================================================================
//  RynthDefective - RynthDefectivePlugin.Selection.cs
//  The Selection window (2026-10-22): what is selected, how far and which way,
//  and a creature's health, as the retail toolbar showed it
//  (ShortcutBar/SelectionWindow.cs draws it).
//
//  The tick builds the view 10 times a second: the selected object
//  (GetSelectedItemId), its name, where it is from the player (positions in
//  landblock coordinates: 192 units a landblock; the facing from the player's
//  pose, as RynthAi's combat turns), and its health from AC's health updates
//  (OnUpdateHealth), asked for when a creature is selected and every 2 s after.
//  An item's mana (2026-11-01, Tom: "a bar that reports the mana status"): its
//  ItemCurMana / ItemMaxMana from the last identify (AC identifies what is
//  selected; asked once if it hasn't 1.5 s later), identified again every 15 s
//  while it stays selected, so a worn item's bar follows it down.
//  Its right-click menu's "Why this loot rule?" asks RynthAi (AskLootWhy).
// ============================================================================

using System;
using System.Globalization;
using RynthCore.Plugin.RynthDefective.ShortcutBar;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective;

public sealed partial class RynthDefectivePlugin
{
    internal const string SelectionSurface = "window:selection";

    private SelectionWindow? _selectionWindow;                     // render thread
    private volatile SelectionView _selectionView = SelectionView.NotReady;
    private long _selectionDrawnMs = long.MinValue / 2, _nextSelectionMs, _nextHealthQueryMs, _nextManaQueryMs;
    private bool _manaAsked;   // the selection had no identify, and one was asked for
    private uint _selectionId;
    private int _selectionFailures;

    // The last health AC sent for the selected object (game thread writes, tick reads).
    private volatile HealthSeen? _health;
    private sealed record HealthSeen(uint Id, float Ratio, uint Current, uint Max);

    private const uint CreatureType = 0x10;   // ItemType.Creature

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (targetId != 0 && targetId == _selectionId)
            _health = new HealthSeen(targetId, healthRatio, currentHealth, maxHealth);
    }

    /// <summary>RynthPluginRenderEmbed "window:selection". 1 drawn, 0 not ready, -2 failed (three in a row). Render thread.</summary>
    internal int RenderSelection(out float wantHeight)
    {
        wantHeight = 0;
        if (!UsesShortcutBar) return 0;
        System.Threading.Volatile.Write(ref _selectionDrawnMs, Environment.TickCount64);
        try
        {
            _selectionWindow ??= new SelectionWindow(Host, id => QueueBar(() => { if (!Host.UseObject(id)) Print("That can't be used now."); }))
            {
                Quantity = _qty,
                QuantityAction = (click, n) => QueueBar(() => RunQuantityClick(click, n)),
                LootWhy = Host.HasSendPluginCommand ? _ => QueueBar(AskLootWhy) : null,
            };
            wantHeight = _selectionWindow.Draw(_selectionView);
            _selectionFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthDefective] selection draw failed: {ex.GetType().Name}: {ex.Message}");
            return ++_selectionFailures >= 3 ? -2 : 0;
        }
    }

    /// <summary>Plugin tick: the selection's view while its window shows.</summary>
    private void TickSelection(long now)
    {
        if (!UsesShortcutBar || !_identityKnown) return;
        if (now - System.Threading.Volatile.Read(ref _selectionDrawnMs) > 2000 || now < _nextSelectionMs) return;
        _nextSelectionMs = now + 100;
        try { _selectionView = BuildSelection(now); }
        catch (Exception ex) { Host.Log($"[RynthDefective] selection read failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private SelectionView BuildSelection(long now)
    {
        uint sel = Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0;
        bool changed = sel != _selectionId;
        _selectionId = sel;
        if (changed) _health = null;
        if (sel == 0) return new SelectionView(true, 0, string.Empty, string.Empty, float.NaN, -1f, 0, 0);

        string name = Host.TryGetObjectName(sel, out string n) && n.Length > 0 ? n : $"0x{sel:X8}";
        bool creature = Host.TryGetItemType(sel, out uint type) && (type & CreatureType) != 0;
        if (creature && (changed || now >= _nextHealthQueryMs))
        {
            _nextHealthQueryMs = now + 2000;
            Host.QueryHealth(sel);
        }

        // An item's mana, from its identify.
        float mana = -1f;
        uint manaCur = 0, manaMax = 0;
        if (changed) { _manaAsked = false; _nextManaQueryMs = now + 1500; }
        if (!creature)
        {
            if (Host.TryGetObjectIntProperty(sel, ItemMaxManaProperty, out int most) && most > 0)
            {
                Host.TryGetObjectIntProperty(sel, ItemCurManaProperty, out int cur);
                manaMax = (uint)most;
                manaCur = (uint)Math.Clamp(cur, 0, most);
                mana = manaCur / (float)most;
                if (changed) _nextManaQueryMs = now + ManaRefreshMs;   // AC identifies a new selection itself
                else if (now >= _nextManaQueryMs)
                {
                    _nextManaQueryMs = now + ManaRefreshMs;
                    Host.RequestId(sel);
                }
            }
            else if (!_manaAsked && now >= _nextManaQueryMs && !(Host.HasHasAppraisalData && Host.HasAppraisalData(sel)))
            {
                _manaAsked = true;   // not identified by now: once, so an item with mana gets its bar
                Host.RequestId(sel);
            }
        }

        // The quantity box (a stack, or an item in the open vendor's list).
        Quantity.QuantityView? qty = QuantityViewNow();

        string where = string.Empty;
        float relative = float.NaN;
        Items.CarriedItem? carried = null;
        foreach (var it in _session.Items()) if (it.Id == sel) { carried = it; break; }
        if (qty?.Subject.Mode == Quantity.QtyMode.Vendor && qty.Subject.Quote is { } quote)
            where = "the vendor's" + (quote.UnitPrice > 0 ? $" · {quote.UnitPrice.ToString("N0", CultureInfo.InvariantCulture)} {quote.Currency} each" : "");
        else if (carried != null) where = CarriedText(carried);
        else if (Host.TryGetObjectPosition(sel, out uint oc, out float ox, out float oy, out float oz) && oc != 0
                 && Host.TryGetPlayerPose(out uint pc, out float px, out float py, out float pz, out float qw, out _, out _, out float qz) && pc != 0)
        {
            double dx = Global(oc, ox, true) - Global(pc, px, true), dy = Global(oc, oy, false) - Global(pc, py, false), dz = oz - pz;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double bearing = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (bearing < 0) bearing += 360.0;
            double facing = ((-2.0 * Math.Atan2(qz, qw) * 180.0 / Math.PI) % 360.0 + 720.0) % 360.0;
            double rel = bearing - facing;
            while (rel > 180) rel -= 360;
            while (rel < -180) rel += 360;
            if (dist >= 0.5) relative = (float)rel;
            where = $"{dist.ToString(dist < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} yd {Compass(bearing)}";
        }
        // Not carried (on the ground, in a corpse or chest): AC's own stack size.
        if (carried == null && qty?.Subject.Mode != Quantity.QtyMode.Vendor
            && Host.TryGetObjectIntProperty(sel, StackSizeProperty, out int stack) && stack > 1)
            where = "stack of " + stack.ToString("N0", CultureInfo.InvariantCulture) + (where.Length > 0 ? " · " + where : "");

        HealthSeen? h = _health;
        float health = h != null && h.Id == sel ? h.Ratio : -1f;
        return new SelectionView(true, sel, name, where, relative, health, h?.Current ?? 0, h?.Max ?? 0)
        {
            Qty = qty, Creature = creature, Mana = mana, ManaCur = manaCur, ManaMax = manaMax,
        };
    }

    /// <summary>
    /// The Selection menu's "Why this loot rule?": RynthAi answers for the item selected now, in its
    /// own window (its remote command "lootwhy", the one /ra loot why window runs). Plugin tick.
    /// </summary>
    private void AskLootWhy()
    {
        if (!Host.SendPluginCommand("RynthAi", "lootwhy", "show"))
            PrintFail("Why this loot rule? needs RynthAi, which isn't loaded.");
    }

    private const uint StackSizeProperty = 12;   // PropertyInt.StackSize
    private const uint ItemCurManaProperty = 107, ItemMaxManaProperty = 108;   // PropertyInt.ItemCurMana / ItemMaxMana
    private const long ManaRefreshMs = 15000;

    /// <summary>A carried item's line: how many in the stack or uses left, and where (in your pack, in hand, worn).</summary>
    private static string CarriedText(Items.CarriedItem it)
    {
        var parts = new System.Collections.Generic.List<string>(2);
        if (it.MaxStack > 1 || it.Stack > 1) parts.Add("stack of " + it.Stack.ToString("N0", CultureInfo.InvariantCulture));
        if (it.MaxUses > 0) parts.Add($"{it.Uses.ToString(CultureInfo.InvariantCulture)} of {it.MaxUses.ToString(CultureInfo.InvariantCulture)} uses");
        parts.Add(!it.Wielded ? "in your pack" : Items.ItemKinds.IsWeapon(Items.ItemKinds.Classify(it)) ? "in hand" : "worn");
        return string.Join(" · ", parts);
    }

    /// <summary>A landblock coordinate as a world one: 192 units a landblock (the cell's high bytes).</summary>
    private static double Global(uint cell, float local, bool x) =>
        ((x ? cell >> 24 : cell >> 16) & 0xFF) * 192.0 + local;

    private static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private static string Compass(double bearing) => Points[(int)Math.Round(bearing / 45.0) % 8];
}
