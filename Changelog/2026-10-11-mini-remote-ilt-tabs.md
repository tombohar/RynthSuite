# RynthAi: the ILT Hub becomes the Mini Remote's tabs

Pairs with the matching RynthCore commit on `sk`, which adds the Guardian panel under the bar's ILT button.

## What changed

The ILT Hub and the Mini Remote are now one window, called **Mini Remote**.

- **Tabs.** The Mini Remote has a tab bar: **Remote** (the existing sections), then on ILT
  worlds **Character, Quests, Pets, Banking, Gear and Games**.
  - Each tab shows once the server reports that ILT feature on.
  - Off ILT worlds there is no tab bar, so the Mini Remote looks as it did before.
  - The selected tab is saved per character in `huds.json` (`MiniRemoteTab`).
  - Each ILT tab draws at a fixed size, and the window follows it. Both the floating window and
    RynthCore's Mini Remote window refit when the tab changes.
  - Right-click Options belongs to the Remote tab. The ILT tabs keep their own right-click menus.
  - The dashboard's Mini Remote drawer stays Remote-only. It is too narrow for the tabs, and its
    Options menu points to the Mini Remote window instead.
- **The separate ILT Hub windows are gone.** Their saved open/closed flags
  (`CharacterWindowOpen`, `PetsWindowOpen`, `QuestTrackerPoppedOut` and the rest) are ignored when
  `ilt-hub.json` loads.
  - The Games hand HUD and the quest favorites HUD still float on their own.
  - The favorites HUD's "Open quest tracker" now switches the Mini Remote to its Quests tab.
- **Commands.**
  - `/ra hub open <section> [show|hide|toggle]` switches the Mini Remote to that tab. It shows the
    floating Mini Remote unless RynthCore's window already hosts it. `hide` goes back to Remote.
  - `/ra hub tab <section>` only switches tabs. RynthCore's dashboard uses it after opening its
    own Mini Remote window.
- **Guardian moved to RynthCore's ILT button.**
  - The Temple guardian tools (riddle answers, Give, the phrasebook) draw in RynthCore's new
    Guardian panel, through the new embed surface `"guardian"` (`RynthPluginRenderEmbed`).
  - `/ra guardian window [show|hide|toggle]` and the Options menu's "Guardian panel" open that
    panel through `/rc guardian`.
  - The Mini Remote's "Guardian answer" section is unchanged.
- **RynthAi's own Bounties feature is removed.** RynthILT's Bounties panel (the ILT button,
  `/rc bounties`) is the one bounty tracker.
  - `/ra bounty ...` and `/ra hub open bounties` open that panel.
  - The old `Bounty` block in `ilt-hub.json` is ignored on load.
  - The `IltBounties` trace category is gone.
- **Wording.** Chat prefixes changed from `[ILT Hub]` to `[Mini Remote]`. The Settings tooltips
  and the Server Features status line now say "Mini Remote" as well.

## Files

- `Huds/MiniRemoteHud.cs`: the tab bar (`RenderBody`) and the pending tab switch (`RequestTab`).
  `BeginTab` calls `igBeginTabItem` with a null `p_open`, because ImGui.NET only wraps the flags
  overload together with a close button. The Options menu's ILT group now links to the Guardian
  and Bounties panels.
- `Huds/HudController.cs`: `HandleTabCommand(tab, mode)`.
- `Huds/HudState.cs`: `MiniRemoteTab`.
- `IltHub/IltSections.cs`: the tab list, tab ids and `IsTab`. Bounties was removed.
- `IltHub/IltHubUi.cs`: `TabSize`, `RenderTab` and `RenderGuardianPanel`. The per-section windows
  were removed.
- `IltHub/IltHubController.cs`:
  - `MiniRemoteTabCommand`, `OffersTabs`, `RenderTab`, `RenderGuardianPanel` and
    `OpenEnginePanel`.
  - The `tab` verb.
  - Bounty and guardian-window verbs go to the engine panels.
- `IltHub/IltQuests.cs`: `RenderTab`. The popped-out tracker window was removed.
- `IltHub/IltHubContext.cs`: `ShowTab`.
- `IltHub/IltHubState.cs` and `IltHub/IltHubStore.cs`: the window flags and the bounty state were
  removed.
- `IltHub/IltBounties.cs`, `IltHub/IltBountyParse.cs` and
  `Tools/RynthCore.RynthAiHostTests/Tests/BountyTests.cs`: deleted.
- `PluginExports.cs`: the `"guardian"` embed surface.
- `RynthAiPlugin.LocalFeatures.cs`: `RenderEmbeddedGuardian` and the tab command wiring.
- `RynthAiPlugin.cs` and `Diagnostics/RynthLog.cs`: wording, and the `IltBounties` category was
  removed.
- `LegacyUi/LegacyAdvancedSettingsUi.cs`, `IltHub/ServerFeatureGate.cs` and the other `IltHub/*`
  files: wording.

## Testing

- Host tests: 613 of 614 pass. The one failure is the known map-bake test, which needs the real
  dat files.
- In game, on an ILT world, check that:
  - the tabs appear as features report on;
  - the window refits per tab;
  - the Char launcher opens the Character tab;
  - the bar's ILT button lists Bounties and Guardian, and the Guardian panel draws the guardian
    tools;
  - `/ra hub open quests` switches tabs.
