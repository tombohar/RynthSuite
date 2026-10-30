# RynthAi: corpses with several keeps are looted to the end

## What Silentkelpie saw (2026-10-11, flight recorder 23:13)

Corpses with three or four keeps (Necrolytic Extracts, Coalesced Aetheria) were closed with
items still on them. On corpse 0xF021B0AB:

- 22:59:47.07 first pickup sent; the server merges it into the pack's stack (StackableMerge),
  InventoryRemoveObject at 48.88.
- 22:59:53.01 second pickup sent, 4.1 s later, with nothing happening in between. InventoryRemoveObject at 54.83.
- 22:59:58.60 corpse closed: the 12 s LootCorpseTimeoutMs ran out. Everything else on it was left.

## Causes

1. **A merged item was never "in the inventory".** The pickup counts as done once the item shows
   up in a pack. A stack merge removes the picked object, and the client keeps no container for it,
   so it never showed up there. Each stackable keep waited the full PickupArriveMaxMs (4 s)
   before the next pickup went out.
2. **The corpse timeout counted from the claim, not from the last progress.** With about 6 s per
   stackable item, three of them used up the 12 s, and the corpse was abandoned mid-loot.

## Fix (CorpseOpenController)

- An item that left the corpse and has no container is taken as merged into a stack. It is
  confirmed once its bend-down has played, about 1 s, with the reason "merged into a stack in the pack".
- Every confirmed pickup restarts the corpse timeout. The timeout now means 12 s without taking anything.

## Tests (LootCorpseTests)

- `loot pacing: an item merged into a stack in the pack is confirmed without the 4 s arrival wait`
  (before: 4064 ms).
- `loot pacing: each item taken restarts the corpse timeout, so a corpse with many keeps is looted to the end`
  (before: 1 of 3 taken, then abandoned).

## Not fixed here

On corpse 0xF0266213 (23:12:47 to 23:13:00) the client sent its own Use on the open corpse about
2 s after each merge. RynthAi did not send it (it has no engine marker in the recorder). That Use
closed the corpse, and RynthAi reopened it each time, at about 5 s per item. Something
other than RynthAi's loot path sent it: another plugin, or a click or key.
