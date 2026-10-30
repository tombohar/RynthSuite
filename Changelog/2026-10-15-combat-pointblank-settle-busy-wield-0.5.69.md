# RynthAi 0.5.69 - point-blank facing, settle only after a switch, 'too busy' wields

Date: 2026-10-15
Plugin: RynthCore.Plugin.RynthAi 0.5.69-legacy-ui (previous release 0.5.68-legacy-ui)

Follow-ups from Silentkelpie's combat logs of 2026-10-14/15, after the 0.5.64 swap and stall
changes.

## Point-blank missile stalls: turn to the target once

7 of Kelpie's 13 missile stalls in one session were at about 1.7 m with the target about 177°
behind. In missile and melee modes the server rotates the character, so RynthAi doesn't turn on
the client (a client turn there caused spinning, 2026-09-27). At point blank with the target
behind, though, the server doesn't rotate, and the stall watchdog's first step (re-issue the
attack) didn't fix it.

Now, in Missile mode only, when the target is within 3 m, more than 90° off, and no shot has
gone out for 2.5 s, combat turns to the target's heading once (`TurnToHeadingBy`, the same call
the wall-facing hold uses). It then waits 300 ms for the turn to settle. The same target is not
turned to again within 3 s. The watchdog's re-issue step also turns first, whatever the last shot.
The log line is `[Combat] point blank: turning to ...` with a running count.

- `Combat/PointBlankFacing.cs` (new): the rule, pure and unit tested.
- `Combat/CombatManager.cs`: `HoldForPointBlankFacing`, called from the attack path and the
  watchdog's re-issue.

## Settle hold: only after a target switch

`WeaponSwapSettleMs` (750 ms) held every swap until the target had been locked that long. In
Kelpie's logs this put 0.75 s in front of 22 of 25 swaps that went ahead anyway, nearly all on
a fresh lock after a kill. Only 2-4% of those were followed by a quick switch.

The hold now applies only when the lock replaced another live target (a switch). A lock after a
kill or a drop swaps at once. The log reason is now "target switched N ms ago". The setting is
renamed in the Advanced settings to "Wait After Target Switch (ms)", with an updated tooltip.
The value and the settings key are unchanged.

## Wields answered 'too busy' are not failed tries

Kelpie's two "didn't wield after 3 tries" give-ups in the last four days both coincided with a
teleport: a house recall at 00:44:23 (one second after the wield went out) and a portal at
23:22:52. The flight recorder showed the server answering each try with `WeenieError 0x1D
(YoureTooBusy)` and then `InventoryServerSaveFailed` with no error. The engine keeps only the
last of those records, so RynthAi read "error 0", saw no refusal, and each try timed out as
"not seen in hand". After three, the better weapon was benched for 2 minutes, even after the
character had landed somewhere new.

Now:

- `0x1D` (WeenieError or UseDone), or an `InventoryServerSaveFailed` with no error, reads as
  "busy". The try is given back and the wield goes again after 1.5 s. After 6 busy answers for
  one weapon they count as tries, so a weapon the server always refuses is still rested.
- Leaving portal space (a portal, recall or teleport) clears every try and rest, for the main
  hand, ammo and off hand.
- A real refusal still counts as a try, as before.
- The log line is `[EquipDiag] wield of ... answered 'too busy': not counted, again in 1.5 s`.

Files:

- `Combat/WeaponSwapTracker.cs`: `Busy`, `NewPlace`, `BusyRetryMs`, `MaxBusyRefunds`.
- `Combat/CombatManager.cs`: `ServerAnswerSinceSend` (was `ServerRefusedSinceSend`),
  `FoldServerAnswer` for the weapon, ammo and off-hand trackers, and `NoteSwapPortal` on the
  heartbeat.
- `Combat/CombatManager.Ammo.cs`, `Combat/CombatManager.Offhand.cs`: use `FoldServerAnswer`.

## Tests

- `Tools\RynthCore.RynthAiTests`: 40 tests, all passed. New: "point blank facing: one snap only
  for a close target behind, after a full shot cycle".
- `Tools\RynthCore.RynthAiHostTests`: 1009 of 1010 passed. New:
  - "swap holds: a lock after a kill swaps at once; a switch from a live target waits"
  - "weapon swap: 'too busy' is no try, and a portal or recall ends the rest"
  - "weapon swap: combat reads ACE's busy answer (0x1D then SaveFailed 0) as busy, a real
    refusal as a try"

  The one failure is the map bake check, which needs the game dats.
