using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthCore.Plugin.RynthDefective.Views;

/// <summary>
/// The weapon picker window, for picking among dozens of weapons while playing by hand: a search
/// box, a row of kind buttons (All, Melee, Missile, War, Void - the kind the player prefers is kept),
/// then the carried weapons and wands as big rows with their picture. Favourites and the recently
/// wielded come first; below them every kind, A-Z. One click on a row wields it; the weapon in hand
/// is highlighted. The "+" / "-" in front of a row adds or takes out a favourite.
/// </summary>
internal sealed class PickerView
{
    private static readonly uint InHandColor = UiFormat.Rgb(110, 210, 110);
    private static readonly uint ElementColor = UiFormat.Rgb(150, 185, 235);

    private readonly DefSession _s;

    public string Search = string.Empty;

    public PickerView(DefSession session) => _s = session;

    public void Draw(UiWindow w)
    {
        if (!_s.LoggedIn)
        {
            w.TextDisabled("Log in to pick a weapon.");
            return;
        }
        DefSettings st = _s.Settings;
        IReadOnlyList<CarriedItem> items = _s.Items();
        PickerModel m = WeaponPicker.Build(items, _s.Data.Favourites, _s.Data.Recent, Search, st.PreferredKind);

        w.InputText("Search", "pk.search", Search, 48, v => Search = v);
        w.Tooltip("Words in the name, the element or the kind: \"fire wand\", \"axe\", \"void\".");
        foreach (KindFilter f in Enum.GetValues<KindFilter>())
        {
            KindFilter kind = f;
            w.ToggleButton(WeaponPicker.KindLabel(f), "pk.kind" + (int)f, st.PreferredKind == f, () =>
            {
                st.PreferredKind = kind;
                _s.Save();
            }, small: true);
            w.SameLine();
        }
        w.SmallButton("Clear", "pk.clear", () => Search = string.Empty);
        w.Tooltip("Clears the search. The kind button you leave pressed is remembered.");

        DrawHand(w, items);
        if (st.PreferredKind != KindFilter.All || Search.Length > 0)
            w.TextDisabled($"{m.Shown} of {m.Total} shown");

        w.BeginChild("pk.list", 0f, 0f, true);
        float icon = st.PickerIconSize;
        bool icons = st.ShowIcons && _s.IconsAvailable;
        if (m.Favourites.Count > 0)
        {
            w.SeparatorText("Favourites");
            foreach (PickerRow r in m.Favourites) DrawRow(w, r, "f", icon, icons, showElement: true);
        }
        if (m.Recent.Count > 0)
        {
            w.SeparatorText("Recent");
            foreach (PickerRow r in m.Recent) DrawRow(w, r, "r", icon, icons, showElement: true);
        }
        foreach (PickerGroup g in m.Groups)
        {
            w.SeparatorText($"{ItemKinds.Label(g.Group)} ({g.Rows.Count})");
            foreach (PickerRow r in g.Rows) DrawRow(w, r, "g", icon, icons, showElement: g.Group is WeaponGroup.WarMagic or WeaponGroup.VoidMagic);
        }
        if (m.Groups.Count == 0)
            w.TextDisabled(m.Total == 0 ? "No weapons or wands in your pack." : "Nothing matches. Clear the search or pick All.");
        w.EndChild();
    }

    /// <summary>What is in the hands now, and the item's name for the title line.</summary>
    private void DrawHand(UiWindow w, IReadOnlyList<CarriedItem> items)
    {
        string hand = string.Empty;
        foreach (CarriedItem it in items)
        {
            if (!it.Wielded) continue;
            ItemKind k = ItemKinds.Classify(it);
            if (!ItemKinds.IsWeapon(k) && k != ItemKind.Shield) continue;
            hand += (hand.Length > 0 ? ", " : string.Empty) + it.FullName;
        }
        UseRequest? pending = _s.Use.Pending;
        if (pending != null) w.TextColored(SlotText.Waiting, $"Waiting to wield {pending.Label}...");
        else w.TextColored(hand.Length > 0 ? InHandColor : SlotText.Plain, "In hand: " + (hand.Length > 0 ? hand : "nothing"));
    }

    private void DrawRow(UiWindow w, PickerRow r, string section, float icon, bool icons, bool showElement)
    {
        CarriedItem it = r.Item;
        string idKey = section + it.Id;
        ItemRef reference = ItemRef.From(it);

        w.SmallButton(r.Favourite ? "-" : "+", "fav" + idKey, () => _s.SetStatus(_s.ToggleFavourite(reference)));
        w.Tooltip(r.Favourite ? "Take out of the favourites" : "Add to the favourites (listed first)");
        w.SameLine();
        if (icons)
        {
            w.Image(UiFormat.IconKindObject, it.Id, icon, icon);
            w.SameLine();
        }
        string label = it.FullName;
        if (showElement && r.Element.Length > 0) label += $"  ({r.Element})";
        if (r.InHand) label += "   [in hand]";
        if (r.InHand) w.PushStyleColor(UiFormat.ColText, InHandColor);
        w.Selectable(label, "pk" + idKey, r.InHand, () => _s.Wield(reference), 0f, icons ? icon : 0f);
        if (r.InHand) w.PopStyleColor();
    }
}
