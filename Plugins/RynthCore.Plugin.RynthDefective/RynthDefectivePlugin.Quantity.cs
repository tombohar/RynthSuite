// ============================================================================
//  RynthDefective - RynthDefectivePlugin.Quantity.cs
//  The quantity box (2026-10-30, Quantity/QuantityController.cs): its commands
//  (/rdef qty, split, give, buy), the Selection window's buttons, and the tick
//  that notes the give target and watches the action in flight.
//
//  It lives here, with the Selection window, because the number is about what
//  is selected (as retail's toolbar had it): it needs no inventory panel, and
//  every command works with the window hidden or on an engine without it.
// ============================================================================

using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Quantity;
using RynthCore.Plugin.RynthDefective.ShortcutBar;

namespace RynthCore.Plugin.RynthDefective;

public sealed partial class RynthDefectivePlugin
{
    private readonly QuantityController _qty = new();
    private HostQtyWorld? _qtyWorld;
    private long _nextQtyTickMs;

    private HostQtyWorld QtyWorld => _qtyWorld ??= new HostQtyWorld(Host);

    /// <summary>Every answer of the box goes to chat (and to the window's line).</summary>
    private void InitQuantity()
    {
        _qty.Report = (ok, text) => Reply.Say(ok, text);
    }

    private void RunQuantityCommand(DefCommand c)
    {
        switch (c.Verb)
        {
            case DefVerb.Quantity: _qty.SetBox(QtyWorld, c.Amount); break;
            case DefVerb.Split: _qty.Split(QtyWorld, c.Amount, c.Text); break;
            case DefVerb.Give: _qty.Give(QtyWorld, c.Amount); break;
            case DefVerb.Buy: _qty.Buy(QtyWorld, c.Amount, c.Text); break;
        }
    }

    /// <summary>A button in the Selection window's box, with the count it showed (queued from the render thread).</summary>
    private void RunQuantityClick(QtyClick click, int n)
    {
        if (!_identityKnown) return;
        QtyAmount amount = QtyAmount.Of(n);
        switch (click)
        {
            case QtyClick.Split: _qty.Split(QtyWorld, amount, string.Empty); break;
            case QtyClick.Give: _qty.Give(QtyWorld, amount); break;
            case QtyClick.Buy: _qty.Buy(QtyWorld, amount, string.Empty); break;
        }
    }

    /// <summary>Plugin tick, ten times a second: the give target and the answer to the action in flight.</summary>
    private void TickQuantity(long now)
    {
        if (!_inWorld || !_identityKnown || now < _nextQtyTickMs) return;
        _nextQtyTickMs = now + 100;
        try { _qty.Tick(QtyWorld); }
        catch (System.Exception ex) { Host.Log($"[RynthDefective] quantity tick failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The Selection window's box for what is selected (null: none, or the setting is off).</summary>
    private QuantityView? QuantityViewNow()
    {
        if (!_session.Settings.QuantityBox) return null;
        try { return _qty.View(QtyWorld); }
        catch (System.Exception ex)
        {
            Host.Log($"[RynthDefective] quantity view failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
