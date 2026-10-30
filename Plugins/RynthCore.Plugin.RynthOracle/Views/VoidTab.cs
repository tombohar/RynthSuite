// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>Void: your damage-over-time spells on every target, and the switch for the Void window.</summary>
internal sealed partial class OracleView
{
    private void DrawVoid(UiWindow w)
    {
        CharacterSnapshot c = _c.Character;
        bool visible = VoidWindowVisible?.Invoke() ?? false;
        w.Button(visible ? "Hide the Void window" : "Show the Void window", "v.toggle", () => SetVoidWindow?.Invoke(!visible));
        w.Tooltip("A small window with your Corrosion, Corruption and Destructive Curse timers on the selected target.");
        if (!c.VoidMagic.Known)
            w.TextDisabled("This character hasn't trained Void Magic.");
        Field(w, "Surge of Destruction", OneText(c, SpellIds.SurgeOfDestruction), 0f, 160f);

        w.SeparatorText("Your spells on targets");
        if (_c.Void.Dots.Count == 0)
        {
            w.TextDisabled("None running. Cast Corrosion, Corruption or Destructive Curse on something.");
            return;
        }
        w.TextDisabled("Target");
        w.SameLine(230f);
        w.TextDisabled("Spell");
        w.SameLine(430f);
        w.TextDisabled("Left");
        long now = _c.NowMs;
        foreach (VoidDot d in _c.Void.Dots)
        {
            int s = d.SecondsRemaining(now);
            if (s <= 0) continue;
            w.Text(Cut(d.TargetName, 30));
            w.SameLine(230f);
            if (d.Destruction) w.TextColored(UiColors.Destruction, d.SpellName);
            else w.Text(d.SpellName);
            w.SameLine(430f);
            w.Text(s + "s");
        }
    }
}

/// <summary>
/// The Void window (upstream's target view): your Corrosion, Corruption and Destructive Curse
/// seconds on the selected target, highlighted when cast with Surge of Destruction up, and the
/// surge's own time. Shown by itself for characters with Void Magic (setting).
/// </summary>
internal sealed class VoidView
{
    private readonly OracleContext _c;

    public VoidView(OracleContext c) => _c = c;

    public void Draw(UiWindow w)
    {
        if (!_c.InWorld)
        {
            w.TextDisabled("Not in the world.");
            return;
        }
        uint target = _c.Host.GetSelectedItemId();
        string name = target != 0 && _c.Host.TryGetObjectName(target, out string n) ? n : "";
        w.Text(name.Length > 0 ? name : "No target selected");

        long now = _c.NowMs;
        Row(w, "Corrosion", target, name, VoidFamily.Corrosion, now);
        Row(w, "Corruption", target, name, VoidFamily.Corruption, now);
        Row(w, "Curse", target, name, VoidFamily.Curse, now);

        double surge = double.MaxValue;
        foreach (ActiveEnchantment e in _c.Character.Enchantments)
            if (e.SpellId == SpellIds.SurgeOfDestruction) surge = Math.Min(surge, e.Remaining);
        w.TextDisabled("Destruction");
        w.SameLine(100f);
        if (surge == double.MaxValue) w.Text("-");
        else w.TextColored(UiColors.Destruction, double.IsPositiveInfinity(surge) ? "on" : $"{(int)Math.Ceiling(surge)}s");
    }

    private void Row(UiWindow w, string label, uint target, string name, VoidFamily family, long now)
    {
        w.TextDisabled(label);
        w.SameLine(100f);
        bool destruction = false;
        int s = target != 0 ? _c.Void.Remaining(target, name, family, now, out destruction) : -1;
        if (s <= 0) w.Text("-");
        else if (destruction) w.TextColored(UiColors.Destruction, s + "s");
        else w.Text(s + "s");
    }
}
