using System;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;

namespace RynthCore.Plugin.RynthDefective.Views;

/// <summary>The settings under the bar: how many slots and how big, what is shown, and the credit.</summary>
internal sealed class SettingsPanel
{
    /// <summary>The credit line, in the plugin's about text and the README.</summary>
    public const string Credit =
        "RynthDefective is an independent implementation of the ideas of Defective, the Decal item-bar plugin written by "
        + "Hazridi and modified by Aun (github.com/aunrela/Defective). It contains no code from it.";

    private readonly DefSession _s;
    private readonly Action _fit;

    public SettingsPanel(DefSession session, Action fit)
    {
        _s = session;
        _fit = fit;
    }

    public void Draw(UiWindow w)
    {
        DefSettings st = _s.Settings;
        void Changed(bool fit)
        {
            st.Clamp();
            _s.Save();
            if (fit) _fit();
        }

        w.SeparatorText("Settings");
        w.SliderInt("Slots", "set.slots", st.SlotCount, DefSettings.SlotsMin, DefSettings.SlotCountMax, v => { st.SlotCount = v; Changed(true); });
        w.SliderInt("Slots per row", "set.perrow", st.SlotsPerRow, DefSettings.PerRowMin, DefSettings.PerRowMax, v => { st.SlotsPerRow = v; Changed(true); });
        w.SliderInt("Button size", "set.button", st.ButtonSize, DefSettings.ButtonMin, DefSettings.ButtonMax, v => { st.ButtonSize = v; Changed(true); });
        w.SliderInt("Picker row icon", "set.picker", st.PickerIconSize, DefSettings.PickerIconMin, DefSettings.PickerIconMax, v => { st.PickerIconSize = v; Changed(false); });
        w.Checkbox("Show item icons", "set.icons", st.ShowIcons, v => { st.ShowIcons = v; Changed(false); });
        w.Tooltip(_s.IconsAvailable ? "Off: the slots are text buttons." : "This engine can't draw item pictures (it needs script window op level 2): the slots are text buttons.");
        w.Checkbox("Show counts and keys under the slots", "set.counts", st.ShowCounts, v => { st.ShowCounts = v; Changed(true); });
        w.Checkbox("Allow weapon swaps while RynthAi's macro runs", "set.swap", st.SwapWhileBotRuns, v => { st.SwapWhileBotRuns = v; Changed(false); });
        w.Tooltip("Off (the default): a weapon, wand or shield is not wielded while the RynthAi macro runs - it picks the weapon itself and would swap back. Kits, potions and other items always work.");

        w.SeparatorText("About");
        w.TextWrapped(Credit);
        w.TextDisabled("Commands: /rdef (show or hide), /rdef use <slot or name>, /rdef weapons, /rdef help.");
    }
}
