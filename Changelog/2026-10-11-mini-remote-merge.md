# RynthAi: one Mini Remote, resizable slots

## What changed

There were two Mini Remotes: RynthCore's own "Mini Remote" window (an empty grid of quick-launch
slots) and RynthAi's floating "MINI REMOTE" (stats, item grid, toggles, bank, rebuff, translate).
They are now one window.

- **The engine's Mini Remote window hosts RynthAi's remote.** It is the same window you get from
  the bar button, `/rc miniremote`, or the dashboard's Hub launcher. It shows every RynthAi
  section, Toggles included, and resizes itself to fit them.
  - New embed surface `RynthPluginRenderEmbed("miniremote-window")`, next to the existing
    `"miniremote"` (the dashboard drawer, which still leaves Toggles out).
  - While that window shows the remote, RynthAi's floating copy stays hidden
    (`HudController.MiniRemoteInEngineWindow`), so there is only ever one on screen.
    `/ra remote` says where the remote is instead of opening a second one.
  - In the window, the Options menu offers the Toggles section and leaves out "Floating window too".
- **Grow and shrink the slot boxes.**
  - Ctrl+mouse wheel over the slot grid makes the boxes bigger or smaller, 2 px per notch.
  - Options > "Slot size" slider (18-64 px) with a Default (28 px) button.
  - Saved per character in `huds.json` as `MiniRemoteSlotSize`. Older files, which don't have
    it, get the default.
  - The columns, and the window or drawer around them, refit to the new size.

## Files

- `Huds/HudState.cs`: `MiniRemoteSlotSize` plus its range constants; clamped in `HudStore.Normalize`.
- `Huds/MiniRemoteHud.cs`: the slot size is read from state rather than a constant, plus
  Ctrl+wheel, the Options slider and the `window` embed mode.
- `Huds/HudController.cs`: `RenderEmbedded(bool window)`, `MiniRemoteInEngineWindow`, the
  floating window suppressed while the engine hosts the remote, and the `/ra remote` reply.
- `PluginExports.cs` and `RynthAiPlugin.LocalFeatures.cs`: the `"miniremote-window"` surface.

Needs the matching RynthCore engine (Mini Remote window host, same date). An older engine never
asks for `"miniremote-window"`, so with one this plugin behaves as before, apart from the
resizable slots.

## Tests

- RynthAi host tests: 619 tests, 618 passed. The one failure is the map bake (0x6346), which
  already failed before this change.
