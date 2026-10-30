# RynthAi: Mini Remote right-click menu with Character and server sections; Spells added

Pairs with the matching RynthCore commit on `sk`, which sends the server sections.

## What changed

- **Right-clicking the Mini Remote** (its Remote tab, in the floating window, RynthCore's Mini
  Remote window or the dashboard drawer) now opens a menu in two halves:
  - **Character** (top): windows every server has.
    - **Spells**: the spell catalog and buff profile editor, the same window as `/ra spells`.
    - **Skills**, **Inventory** and **Item Count**: RynthCore panels. They are listed when
      RynthCore can run `/rc` commands.
    - **Inventory HUDs setup**.
  - **Server section** (bottom): the windows of the server plugins installed for the world
    you're on.
    - RynthCore decides the list: which server you're on, which plugins are installed and what
      each one adds. It sends the list to RynthAi (the `"servermenu"` remote command, parsed by
      `Huds/ServerMenuModel.cs`).
    - On Infinite Leaftide it lists:
      - the Mini Remote's Character, Quests, Pets, Banking, Gear and Games tabs (each greyed out
        until the server reports that feature on);
      - Guardian (RynthAi);
      - Bounties (RynthILT, only when installed);
      - Progression.
    - A server's section shows only while you're on that server or it is forced, and only when
      one of its plugins is installed. A new server or server plugin only needs registering in
      RynthCore; this menu doesn't change.
    - Forcing: when no installed server plugin matches the world, the section offers
      "Treat this world as <server>".
      - For Infinite Leaftide this turns on RynthCore's session override (`/rc ilt on`) and this
        character's "Treat this world as ILT".
      - With that per-character override on, the Infinite Leaftide section shows even when
        RynthCore doesn't detect ILT. It is labelled "(forced)".
    - With an older RynthCore, or its ImGui layer off, no list arrives. The menu then falls back
      to RynthAi's own Infinite Leaftide entries.
  - The server-features status line, the "Treat this world as ILT" checkbox and "Refresh server
    features" moved from the Options menu to the Infinite Leaftide section.
- **The Options button** now holds only the Mini Remote's settings: sections, layout, slot size,
  lock and hide, and Inventory HUDs setup.
- **Fix:** the Options button didn't open its menu while the tab bar was showing. The button sits
  inside the Remote tab, whose ID ImGui adds to the popup's. The popup now opens at window level.

## Files

- `Huds/ServerMenuModel.cs`: new. It parses the `"servermenu"` payload and recognizes
  `tab:<id>` items.
- `Huds/MiniRemoteHud.cs`:
  - the right-click menu (`RenderMenu`, `RenderCharacterMenu`, `RenderServerMenu`,
    `RenderLocalIltMenu`, `RenderIltStatus`);
  - the Options popup is now opened from a flag (`_openOptions`).
- `Huds/HudController.cs`: `RunEngineCommand`, which runs a `/rc` command on the pump thread.
- `RynthAiPlugin.cs`: the `servermenu` remote command, handled before settings exist.
- `Tools/RynthCore.RynthAiHostTests/Tests/ServerMenuTests.cs`: new (payload parsing, malformed
  lines, tab features).

## Testing

- Host tests: 616 of 617 pass. The one failure is the known map-bake test, which needs the real
  dat files. The three new server menu tests pass.
- In game: right-click the Mini Remote on and off an ILT world. Check that:
  - the Character half shows Spells, Skills, Inventory and Item Count;
  - the Infinite Leaftide half appears only on ILT, or when forced;
  - the tab items switch tabs;
  - Guardian and Bounties open their panels.
