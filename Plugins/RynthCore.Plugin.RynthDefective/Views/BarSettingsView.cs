using System;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;

namespace RynthCore.Plugin.RynthDefective.Views;

/// <summary>
/// RynthDefective's settings in a window of their own (2026-10-27), for the Shortcuts bar: the old
/// script-window bar that held them (SettingsPanel) stays shut while the new bar is used. Opened from
/// the bar (its gear, its right-click menu) and /rdef settings. Which of RynthDefective's windows show
/// (the bar, the Game Panels buttons, Selection), the independent bar and its copy offer, the slot
/// picture size, and the weapon switches. Recorded on the pump thread; the host parts are delegates,
/// so the tests draw it offline.
/// </summary>
internal sealed class BarSettingsView
{
    public const string BarTitle = ShortcutBar.ShortcutBarWindow.Title;
    public const string PanelsTitle = ShortcutBar.PanelButtonsWindow.Title;
    public const string SelectionTitle = ShortcutBar.SelectionWindow.Title;

    private readonly DefSession _s;

    /// <summary>Whether one of RynthDefective's windows (by its title) shows: true, false, null = this engine has no such window.</summary>
    public Func<string, bool?> WindowShown = _ => null;
    /// <summary>Shows or hides one of them.</summary>
    public Action<string, bool> ShowWindow = (_, _) => { };
    /// <summary>A message for the player (the chat, and the status line).</summary>
    public Action<string> Tell = _ => { };
    /// <summary>Opens the weapon picker (/rdef weapons).</summary>
    public Action ShowWeapons = () => { };

    public BarSettingsView(DefSession session) => _s = session;

    public void Draw(UiWindow w)
    {
        if (!_s.LoggedIn)
        {
            w.TextDisabled("Log in to change RynthDefective's settings.");
            return;
        }
        DefSettings st = _s.Settings;
        void Changed()
        {
            st.Clamp();
            _s.Save();
        }

        w.SeparatorText("Windows");
        w.TextWrapped("RynthDefective draws the Shortcuts bar, the Game Panels buttons and the Selection window. "
                      + "Right-click the bar's title for this window and to show or hide the other two.");
        WindowBox(w, BarTitle, "Shortcuts bar", "set.win.bar", "The item bar (/rdef). Its RynthCore bar button is the lightning bolt.");
        WindowBox(w, PanelsTitle, "Game panel buttons", "set.win.panels", "Buttons for the game's own panels: pack, spellbook, map ... (/rdef panels).");
        WindowBox(w, SelectionTitle, "Selection", "set.win.sel", "What is selected, how far and which way, and a creature's health (/rdef selection).");
        if (WindowShown(SelectionTitle) != null)
        {
            w.Checkbox("Quantity box under a stack or a vendor's item", "set.qty", st.QuantityBox, v => { st.QuantityBox = v; Changed(); });
            w.Tooltip("In the Selection window: how many, with 1 / 10 / 100 / half / max, then Split that many into a pack, "
                      + "Give that many to the last player or NPC you selected, or Buy that many from the open vendor.\n"
                      + "/rdef qty, /rdef split, /rdef give and /rdef buy do the same with it off. Kept for this character.");
        }

        w.SeparatorText("Shortcuts bar");
        w.Checkbox("Independent of the game's toolbar", "set.indep", st.IndependentBar, v => Tell(_s.SetIndependent(v)));
        w.Tooltip("On: every slot on the bar is RynthDefective's own. The game's 18 shortcut slots are not on it; a click uses the item "
                  + "one-touch (a kit on yourself, a weapon wielded), and its keys are RynthCore hotkeys (/rdef use N).\n"
                  + "Off (the default): slots 1-18 are the game's own toolbar slots and keys; RynthDefective's slots follow from 19.\n"
                  + "Kept for this character.");
        if (st.IndependentBar && _s.CopyOffer > 0)
        {
            int n = _s.CopyOffer;
            w.TextColored(SlotText.Waiting, $"The game's 18 slots hold {n} shortcut{(n == 1 ? "" : "s")} (items and spells). Copy {(n == 1 ? "it" : "them")} into this bar's slots 1-18?");
            w.TextWrapped("Your own slots move after them, to 19 and on, with their keys, so the bar looks as before. "
                          + "The game's toolbar and its keys stay as they are.");
            w.SmallButton("Copy them", "set.copy", () => Tell(_s.CopyRetailSlots()));
            w.SameLine();
            w.SmallButton("No thanks", "set.nocopy", () => _s.DeclineCopy());
        }
        else if (!st.IndependentBar)
        {
            w.TextDisabled("A healing kit in one of the game's slots is used on you when you click it on the bar (its game key is the game's own).");
        }
        w.SliderInt("Slot picture size", "set.button", st.ButtonSize, DefSettings.ButtonMin, DefSettings.ButtonMax, v => { st.ButtonSize = v; Changed(); });
        w.Tooltip("Ctrl+wheel over the bar's title makes the whole bar bigger or smaller.");

        w.SeparatorText("Weapons");
        w.SmallButton("Weapon picker...", "set.weapons", () => ShowWeapons());
        w.Tooltip("Every weapon and wand you carry, wield one with a click (/rdef weapons, or Weapons... in the bar's right-click menu).");
        w.Checkbox("Weapons button on the bar", "set.weaponsbtn", st.WeaponsButtonOnBar, v => { st.WeaponsButtonOnBar = v; Changed(); });
        w.Tooltip("Shows the sword button in the Shortcuts bar's title, next to edit, settings and hide. Off (the default): the picker opens from here.");
        w.Checkbox("Allow weapon swaps while RynthAi's macro runs", "set.swap", st.SwapWhileBotRuns, v => { st.SwapWhileBotRuns = v; Changed(); });
        w.Tooltip("Off (the default): a weapon, wand or shield is not wielded while the RynthAi macro runs - it picks the weapon itself and would swap back. Kits, potions and other items always work.");
        w.SliderInt("Weapon picker row icon", "set.picker", st.PickerIconSize, DefSettings.PickerIconMin, DefSettings.PickerIconMax, v => { st.PickerIconSize = v; Changed(); });

        w.SeparatorText("About");
        w.TextWrapped(SettingsPanel.Credit);
        w.TextDisabled("Commands: /rdef (show or hide the bar), /rdef settings, /rdef panels, /rdef selection, /rdef weapons, /rdef use <slot or name>, "
                       + "/rdef qty, /rdef split, /rdef give, /rdef buy, /rdef help.");
    }

    private void WindowBox(UiWindow w, string title, string label, string id, string tip)
    {
        bool? shown = WindowShown(title);
        if (shown == null) return;   // not on this engine (the Game Panels buttons need plugin API v90's AcPanel)
        w.Checkbox(label, id, shown.Value, v => ShowWindow(title, v));
        w.Tooltip(tip);
    }
}
