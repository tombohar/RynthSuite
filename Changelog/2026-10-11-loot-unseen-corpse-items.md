# RynthAi: corpse items the object cache missed are looted

## The bug

Silentkelpie, 2026-10-11: a Necrolytic Extract on his own kill was left on the corpse. The
loot profile had Keep rules for it (by name), and there was no `[LootEval]` line for it at all.

Corpse looting only judges what `WorldObjectCache.GetContainedItems` returns, and that only
holds objects the cache has classified. The cache never processed a create for the extract,
so it was in neither the inventory nor the landscape set. The looter never saw it, judged the
other 13 items, and finished the corpse. When the corpse went away the cache logged the extract
as `DELETE-BEFORE-CLASSIFY`. The loot rules themselves were fine: the same profile keeps a
"Necrolytic Extract" (class Misc) with rule 57.

## The fix

While a corpse is open, every 500 ms the looter asks the client what the corpse holds
(`GetContainerContents`). It looks up any item the cache is missing (the cache's lazy lookup
files it) and judges it like the rest. A rescue is logged at Trace:

    [RynthAi] Corpse loot: found 1 item(s) on 0xF0703900 the object cache had missed: 0xF0703199 'Necrolytic Extract' Misc.

An engine without `GetContainerContents` behaves as before.

## Tests

`LootCorpseTests`:
- an item whose create never reached the cache is found and looted;
- with no `GetContainerContents`, nothing changes and nothing breaks.

`FakeHost.ContentsCalls` is a new opt-in fake for `GetContainerContents`.
