# Mini Remote: a Charms tab, and Banking brought up to date with ACECustom's /bank

Pairs with the matching RynthCore commit on `sk`.

## Charms tab (ILT)

- **New Mini Remote tab, Charms.** It sits after Gear and replaces both the engine's Settings >
  Charms Tracking page and the charm toggles that were on the Gear tab.
- **Summary line:** how many charms are acquired and how many are active, with a Refresh button.
- **The table has one row per ACECustom registry charm**, with these columns:
  - Charm (hover for its effect);
  - Acquired: Carried, Stored (carried before, remembered per character) or No;
  - Status: ON, OFF, or ? while its appraisal is pending;
  - Tier;
  - Server: Enabled, Disabled, or - when not reported;
  - a Toggle button that uses the carried copy. When several copies are carried, it uses the one
    that's ON.
- **Guardian Hand** rows follow, each with its own Toggle, plus a "Guardian Hand matching" note.
- The tab rescans every 3 s while the Mini Remote window is open. Toggles are refused while an
  item action is already waiting.
- **Gear tab:** the charm strip is gone. Dispel / Surging Strength now opens by default, and a note
  says where the charms went.
- **Where to open it:**
  - the Mini Remote tab bar;
  - the local ILT menu;
  - `/ra hub open charms` (`charm` also works);
  - the engine's ILT server menu (the matching engine).
- `GetCharmsJson` stays exported for older engines, which still have the Settings page.

## Banking (ILT), per ACECustom upstream `/bank` (2026-10-11)

- **Prestige Coins** balance row, read from `/bank`. The Mini Remote's bank strip shows "PC:" when
  you have some, and the copy line includes them.
- **Deposit:** pick what to deposit and press Deposit. The choices are Everything, Pyreals,
  Pyreal peas, Luminance, Luminance tokens (`/b lum`), Legendary keys, Mythical keys,
  Enlightened coins, Weakly enlightened coins, Prestige coins and Trade notes.
- **Withdraw:** pick a kind and type an amount. The kinds are Pyreals, Luminance, Legendary keys,
  Mythical keys, Enlightened coins, Weakly enlightened coins, Prestige coins and Trade notes.
  - Trade notes take a denomination (100 to 250,000) and a count of up to 50,000. The panel shows
    the total in pyreals.
  - Bad input is refused before anything is sent.
- The server's reply lines (Deposited, Withdrew, refusals) show under the buttons. The balances
  refresh 1.5 s after each action.
- **Throttles:**
  - Deposits, withdrawals and transfers share the server's 5 s bank-action spacing.
  - Plain balance refreshes are spaced on their own, so they no longer hold up an action.
- **Session rates:** money moved between the bank and your pack is no longer counted as income.
  While a deposit, withdrawal or transfer is under way (about 12 s, extended by each Deposited or
  Withdrew line), bank and pack changes are not added to the income figures.

## Files

- `IltHub/IltCharms.cs` (new): the Charms tab.
- `IltHub/IltCharmTracker.cs`: `CharmState.ToggleId`, the copy to use.
- `IltHub/IltGear.cs`: charm strip removed.
- `IltHub/IltSections.cs`, `IltHubController.cs`, `IltHubUi.cs`, `HudState.cs`: the tab is wired in.
- `IltHub/IltBankCommands.cs` (new): deposit and withdraw command building, and reply recognition.
  It has no dependencies, so it can be tested on its own.
- `IltHub/IltBanking.cs`, `IltHubState.cs`: Prestige Coins, the deposit and withdraw UI, and the
  throttles.
- `IltHub/IltSessionRates.cs`: ignores money moves.
- `Huds/MiniRemoteHud.cs`: the "PC:" cell.

## Tests

- `Tests/BankCommandTests.cs`, 4 new tests:
  - deposit commands;
  - withdraw commands, including trade notes;
  - withdraw input refusals;
  - reply-line recognition.
- `Tests/InventoryAttributeFilterTests.cs`, 5 new tests of the engine's inventory filter (the
  engine file is linked into the test project):
  - each rule kind;
  - items not yet identified;
  - match all vs match any;
  - value conversions and names;
  - the saved settings line.

Result: 636 of 637 pass. The one failure is the known map-bake test, which needs local dats.

Not yet tested in game.
