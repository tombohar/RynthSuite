# Mini Remote and Tracker: one set of session-stat rules

RynthTracker 0.4.0 (previous: 0.3.0). Pairs with the matching RynthCore commit on `sk`, which adds the Tracker panel's new rows.

## Why

Before this release the Mini Remote and the Tracker panel computed the same stats with different code:

| Stat | Tracker (RynthTracker 0.3.0) | Mini Remote (RynthAi before) |
|---|---|---|
| Luminance | Character lum (6), plus Aelrynth's bank totals (9102 / 9103). On ACECustom it missed all banked lum and counted bank withdrawals as income. | ACECustom bank lum (9005) only, so it missed lum on retail ACE and on Aelrynth. |
| Kills | Kill notices, plus corpse kills from multi-target spells | Kill notices only |
| XP | Rises, polled every 0.5 s | Net change since the start, polled every 5 s |
| Worlds | All | ILT only ("Session stats: ILT worlds only") |
| Reload | The session carried over | The session started over |
| Lum in the last 5 minutes, pass-up | Neither tracked | Neither tracked |

## What changed

- **One shared engine:** `Plugins/Shared/SessionStats.cs` holds the rules, and
  `Plugins/Shared/SessionStatsTracker.cs` feeds it from the game. Both plugins compile them in, so
  every number below is computed the same way in both.
  - **XP:** the rise of TotalExperience (1), read every 0.5 s.
  - **Luminance, per server:**
    - Retail ACE: the rise of AvailableLuminance (6).
    - Aelrynth: as before, the Bank mod's auto-banked total (9102) added and its drawn total (9103)
      taken out.
    - ACECustom: the rise of BankedLuminance (9005). Once that property is read:
      - rises of AvailableLuminance (6) are ignored, because there they are bank withdrawals;
      - lum moved in by "Deposited N luminance" or "Received N luminance from X" is taken out of
        the next bank rises (a line whose rise hasn't arrived within 60 s is dropped);
      - "You've banked N Luminance." counts on its own only until a bank property (9005 or 9102)
        is read.
  - **Luminance in the last 5 minutes (new):** a rolling 5-minute window, kept in 5-second
    buckets.
  - **Pass-up (new):** ACECustom doesn't send the allegiance lum counters (9013 / 9014) to the
    client. Pass-up is therefore bank income minus your own "You've banked" lines, because a
    vassal's pass-up reaches the bank without a chat line. Fellowship and Zone Share splits also
    arrive silently, so they are included. It is shown only where the bank is read (ACECustom).
  - **Kills:** kill notices, plus corpse kills of monsters you hit with a multi-target spell
    (`KillCounter`, moved to Shared).
  - **XP per kill, deaths and Radiance:** unchanged from the Tracker.
  - **Session lifecycle:** saved every 30 s and restored after a plugin reload (same client,
    same character, saved within the last 10 minutes); a logout ends the session.
    - RynthTracker saves to `%LOCALAPPDATA%\RynthCore\tracker`, the Mini Remote to
      `%LOCALAPPDATA%\RynthCore\miniremote-stats`.
    - Saves from RynthTracker 0.3.0 still load (the new keys are optional).
- **The Mini Remote's Stats section now works on every world:**
  - It shows Time, XP/h, Lum/h, Lum 5m, Pass-up/h (ACECustom) and Kills/h, plus Coins/h and Pyr/h
    on ILT.
  - The Reset XP and Report buttons also work everywhere. Report now includes lum in the last
    5 minutes and pass-up.
- **ILT Character tab "Session rates":**
  - XP, Luminance, its last 5 minutes (with the per-hour pace), pass-up and Kills now come from
    the shared stats.
  - Coins, pyreals and item conversions are unchanged.
  - The Reset button resets both the coin counts and the shared stats.
- **RynthTracker's snapshot JSON** gains these keys:
  - `l5`: lum in the last 5 minutes;
  - `pt`: pass-up total;
  - `ph`: pass-up per hour;
  - `pk`: 1 when pass-up is known.

  The existing keys are unchanged.
- **Removed:** `IltLumIncome` and the `IltParse.LumBanked` regex. RynthAi's kill path no longer
  calls `IltHubController.RecordKill`.

The Tracker panel and the Mini Remote each keep their own session and their own reset. They
match when both started at the same login; resetting one doesn't reset the other.

## Files

- `Plugins/Shared/SessionStats.cs` (new), `Plugins/Shared/SessionStatsTracker.cs` (new).
- `Plugins/Shared/KillCounter.cs`, `Plugins/Shared/TrackerSessionStore.cs`: moved from RynthTracker
  into the `RynthCore.Plugin.Shared` namespace. The store adds `DirectoryFor` and the new save keys.
- `Plugins/RynthCore.Plugin.RynthTracker/RynthTrackerPlugin.cs`: a thin wrapper over the shared tracker, version 0.4.0.
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiPlugin.cs`, `RynthAiPlugin.LocalFeatures.cs`: own a
  `SessionStatsTracker` and forward login, logout, shutdown, tick, chat, kill, object and health
  events to it.
- `Plugins/RynthCore.Plugin.RynthAi/Huds/MiniRemoteHud.cs`, `Huds/HudController.cs`: universal Stats.
- `Plugins/RynthCore.Plugin.RynthAi/IltHub/IltSessionRates.cs`, `IltHubController.cs`, `IltParse.cs`.
- `Tools/RynthCore.TrackerSessionTests`: new `SessionStatsTests.cs`, covering:
  - ILT bank lum, deposits, transfers, withdrawals and spending;
  - pass-up;
  - the chat fallback;
  - Aelrynth and retail lum;
  - the 5-minute window;
  - kills and XP per kill;
  - reload equivalence;
  - parsing older saves.
- `Tools/RynthCore.RynthAiHostTests`: `LumIncomeTests` removed (it tested `IltLumIncome`).

## Testing

- TrackerSessionTests: 275 assertions, all pass.
- RynthAi host tests: 614 of 615 pass. The one failure is the known map-bake test, which needs
  the dats.
- RynthAi and RynthTracker both publish as NativeAOT in Release.
- Not yet checked in game.
