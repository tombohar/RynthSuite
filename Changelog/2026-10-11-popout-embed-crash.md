# RynthAi: fix the client crash when the Mini Remote (or Guardian) is popped out

Pairs with the matching RynthCore commit on `sk`, which carries the engine half of the fix.

## The crash

All four client crashes on 2026-10-11 between 21:32 and 21:38 (dumps

- The Application event log (.NET Runtime 1026) gives the stack:
  - `ImGuiPopOuts.RenderAll`
  - `ImGuiPanelHost.DrawPopped`
  - `MiniRemoteFace.Draw`
  - `PluginManager.RenderEmbed`
  - an access violation inside coreclr
- The Mini Remote had been popped out into its own window, and RynthCore asked RynthAi to draw it
  there.
- A popped-out panel has its own ImGui context. RynthAi's embedded surfaces always switched to
  `Host.ImGuiContext`, the engine's main context, which isn't mid-frame at that point. ImGui
  dereferenced a null current window, and an access violation can't be caught, so the client
  died.
- It happened on the first frame the popped-out Mini Remote showed: the Char (hub) button's
  left-click, or right after login when the window was left popped out.
- The 4 to 9 seconds of "MAIN THREAD HANG DETECTED" before each exit was the runtime writing the
  crash dump, not a separate hang.

## Fix

- The embedded surfaces (`miniremote`, `miniremote-window`, `guardian`) draw in the context that
  is current when RynthCore calls (`EmbedContext` in `RynthAiPlugin.LocalFeatures.cs`). That is
  the window RynthCore has begun, whether it's docked or popped out.
- `RynthPluginRenderEmbed` accepts the names with a `:popout` suffix. the matching RynthCore
  sends those from a pop-out so older RynthAi builds decline instead of crashing.
- With this build the popped-out Mini Remote and Guardian show RynthAi's own content.

## Files

- `PluginExports.cs`: strips the `:popout` suffix.
- `RynthAiPlugin.LocalFeatures.cs`: `EmbedContext`, used by `RenderEmbeddedMiniRemote` and
  `RenderEmbeddedGuardian`.

## Testing

- Host tests: 616 of 617 pass. The one failure is the known map-bake test, which needs the real
  dat files.
- Not yet checked in game: pop out the Mini Remote and the Guardian panel, use the Char button,
  and log in with the Mini Remote left popped out.
