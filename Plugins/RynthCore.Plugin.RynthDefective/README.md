# RynthDefective

An item bar for RynthCore with one-touch use, and a fast weapon picker. Version 0.1.0.

Credit: RynthDefective is an independent implementation of the ideas of **Defective**, the Decal
item-bar plugin written by Hazridi and modified by Aun (<https://github.com/aunrela/Defective>). It
contains no code from it: it was written from the plugin's public feature description
against RynthCore's own APIs. (Defective's repository states no licence; ask Aun if you want a
literal port or shared code.)

## The Shortcuts bar, Game Panels and Selection

On a RynthCore engine with plugin API v90, **RynthDefective is what draws the Shortcuts
bar** (its RynthCore bar button is the lightning bolt; `/rdef` shows or hides it), **the Game Panels
buttons** (pack, spellbook, map ... in the bottom-right corner; `/rdef panels`) and **the Selection
window** (what is selected and its health, at the top; `/rdef selection`). Turn RynthDefective off and
all three go.

* **Settings**: the gear on the bar's title, *Settings...* in its right-click menu, or `/rdef settings`
  open RynthDefective's settings window: which of the three windows show, *Independent of the game's
  toolbar*, the slot picture size, *Allow weapon swaps while RynthAi's macro runs* and the weapon picker's
  icon size.
* **Right-click the bar's title** (or the end of any slot's menu) for *Settings...* and to show or hide
  *Game panel buttons* and *Selection* (they have no RynthCore bar button of their own).
* **Slots 1-18 are the game's own** shortcut slots (the retail toolbar's items, saved on the server, and
  AC's own keys); slots 19 and on are RynthDefective's. Click a slot to use it. A **healing kit** in one of
  the game's slots is used **on you, one touch**, by RynthDefective when you click it on the bar; other
  items are used as the game's toolbar would. The slot's game key (UseQuickSlot_N in Configure Keyboard)
  is the game's own and goes straight to the game, as before.
* **Independent of the game's toolbar** (per character, off by default): every slot on the bar is
  RynthDefective's own. The game's 18 slots are not on it; clicks are one-touch (a kit on yourself, a
  potion used, a weapon wielded) and keys are RynthCore hotkeys (`/rdef use N` for bar slot N). Turning it
  on offers to copy the items and spells in the game's 18 slots into the bar's slots 1-18 (nothing is copied unless you
  say so); your own slots move after them, to 19 and on, with their keys, so the bar looks as it did. The
  game's toolbar and its keys are not changed. Turning it off shows the game's 18 first again, RynthDefective's
  slot 1 at 19.

* **More bars** (up to 4: *Shortcuts 2* to *Shortcuts 5*): *Add a shortcut bar* in the bar's right-click menu
  (or Defective Settings, or `/rdef bars add`) opens another bar in a window of its own, to put anywhere: drag
  its title to move it, its edge to fit more slots in a row. Each has its own slots (10 to start, 1-24 with the
  edit button's - / + or the settings' slider), filled as the bar's own slots are: drag an item from the pack
  onto a slot, or right-click it for *Put the selected item here* or *Put a spell here...*. A click uses the
  item one-touch, as on the bar. Their slots have no keys (keys go on the bar's own slots). The engine remembers
  each bar's place, size and whether it shows; the slots are kept per character with the others. They have no
  RynthCore bar button: the Shortcuts bar's right-click menu shows or hides each, and removes one (*Remove a
  bar*, or *Remove this bar* in that bar's own menu; a bar that holds something asks once more, and its slots
  are forgotten). Why 4: the engine hosts at most 8 windows for a plugin, and RynthDefective draws 4 others.

On an older engine the bar below is a script window, with its settings under its *Settings* button.

## What it does

**Slots.** The bar is a row (or grid) of slots, each holding one item: a healing kit, a stamina kit,
a potion, a scroll, a weapon, a wand, a shield... Click a slot, or press its key, and the item is used
**without selecting anything in the game**:

| Item | What a press does |
| --- | --- |
| Healing / stamina kit | used on yourself |
| Potion, scroll, food, gem, anything else | used (a double-click) |
| Weapon, wand, shield, armor, jewelry | wielded / worn (a double-click); an item already in hand is left alone |

A slot can be set to another way: use it, use it on me, use it on the selected target, or wield it.

**Spell slots.** One of RynthDefective's own slots can hold a spell instead: right-click it on the Shortcuts
bar and choose *Put a spell here...*, then find the spell by name among the ones the character knows (the
engine can't tell RynthDefective which spell is selected in the game's spellbook, so it is picked there), or
type `/rdef spell 3 Heal Self VI`. Its picture is the spell's. Pressing it casts the spell, in Magic mode
(asked for first when the character is in another stance; the cast waits for the stance to settle): a spell
cast on yourself only (Self buffs, recalls) goes on you; any other on the selection, or on you when nothing is
selected and the spell is a helpful one (Heal Other, a buff); a war, void or debuff spell with nothing (or
yourself) selected is refused and says so, as is a spell the character doesn't know. The same one-at-a-time
and busy rules as items apply. The game's own 18 slots keep the game's own spell handling; copying them into an
independent bar brings their spells along as spell slots.

**Put an item in a slot** (Edit slots in the bar): *Set from selected* takes the item you have selected
in the game, or *Pick from pack* chooses from a searchable list of what you carry. `/rdef set 3` does the
first, `/rdef set 3 Health Potion` the second.

**A slot keeps the item's identity, not just the object.** It remembers the item's name, wcid and
material as well as its object id. When the exact object is gone (a used-up stack, a replaced weapon)
the slot uses another carried item of the same identity: not in hand before in hand, the smaller stack
first. A slot with nothing to use shows the last picture seen, dimmed, and "none"; it fills itself again
when you carry another.

**Counts.** Under each slot: the uses left on all the kits of that kind (28), the stack count (x120), "in
hand" for the weapon you hold, "none" when none is carried, and the slot's key.

**Keys.** Keys are the engine's hotkeys running `/rdef use <slot>`. Type a key in the slot editor (F9,
Ctrl+1, Alt+Shift+Num3) and press *Bind to this slot* (follows the slot) or *Bind to this item* (runs
`/rdef use <the item's name>`, works for every character on this PC). The same from chat: `/rdef bind 3 F9`.
Any item can also be bound by name straight from Settings > Hotkeys or `/rc hotkey add F9 /rdef use Stamina Kit`.
`/rdef use <name>` takes a full name, the start of a name or words in it ("Greater Stamina Kit",
"stamina kit"); of several matches the one not in hand, with the smaller stack, then the lower id, is used.

**The weapon picker** (the *Weapons* button, `/rdef weapons [search]`): every weapon, wand, shield and ammunition
stack you carry, as big rows with the item's picture, grouped by kind (melee, missile, war magic, void
magic, shields, ammunition), A-Z. Type words in the search box ("fire wand", "axe", "void"); the kind
buttons (All, Melee, Missile, War, Void) narrow it, and the one you leave pressed is remembered. The weapon in
hand is highlighted. **One click wields.** `+` in front of a row adds a favourite; favourites and the last
weapons you wielded are listed first (they are also in their kind's group).

## The quantity box

Under a selected stack, or an item in an open vendor's list, the Selection window shows how many: type the
number (the game gets no keys while the box has the keyboard) or pick 1, 10, 100, half or max.

* **A stack you carry** (or one in the chest you have open): **Split N** puts N into a new stack in the main
  pack, a side pack or the open container (*into ...* picks; the number is its free slots). One always stays in
  the stack, so more than the stack splits all but one. **Give N** hands N (up to the whole stack) to the last
  player or NPC you selected: select them, then the stack.
* **An item in a vendor's list**: **Buy N**, with its price and your pyreals (or the vendor's own currency).
  *max* is the most you can buy: what the vendor has, what you can pay for, what your packs and burden take.
  A buy that can't go says why before anything is sent ("Not enough pyreals: 100 Mana Scarab cost 1,200, you
  have 1,000 (you can buy 83)", "The pack is full ..."); the engine checks again before it sends.
* Every action answers once, in chat and on the box's last line: done, the server's refusal, or no answer.
  One action at a time.
* The same from chat, hotkeys and metas, with the window hidden: `/rdef qty`, `/rdef split`, `/rdef give`,
  `/rdef buy` (below). It doesn't need the Inventory panel or RynthInventory.
* Retail AC kept this number in its toolbar; RynthCore's *Hide retail toolbar* hides it with the toolbar.

## The Day/Night timer

A one-row window (its RynthCore bar button is the sun; `/rdef daynight [show|hide]`; Settings > UI > Toolbar >
*Day/Night timer*): a sun by day, a moon at night, and the **real** time until the next change, "Night in 23m",
"Day in 4m 10s" (seconds from 10 minutes down), with the Dereth hour's name when there is room. A Dereth day is
16 hours of 476.25 s, 2 h 7 min of real time: day (Dawnsong to Warmtide-and-Half) and night (Evensong to
Foredawn-and-Half) are 63.5 real minutes each; the clock and calendar are RynthAi's meta expressions'
(`getisday`, `getminutesuntilnight`). It shows "—" until you're in the world: the game clock isn't read before.
`/rdef daynight` also says the time in chat. Drag it anywhere; Ctrl+wheel resizes it; right-click hides it.

## Busy, combat and RynthAi

* One item action at a time: a request waits while the client is busy, a gesture animates, RynthAi waits on
  the server, or the last action is unanswered (the server's UseDone; a wield is answered by the item
  reading in hand). Pressing again while one waits does not queue a second; pressing another slot replaces the
  waiting one. A request not sent within 6 s gives up and says why.
* A busy reading that just sits there is a leftover, not a reason to wait: the client's busy count can stay
  up with nothing in flight after a death, a login or an interrupted action (and RynthAi's "waiting on the
  server" with it). A count that hasn't moved for 4 s with nothing of RynthDefective's in flight, a gesture
  that has "animated" for 4 s, or RynthAi's bit on for 5.5 s holds nothing (logged once); only a count above
  that leftover holds again. RynthDefective doesn't reset the count itself (the engine owns it) unless you turn
  on the option below. If the server really is busy it answers "You're too busy" and the use is tried once more
  a second later.
* **Shortcuts clear a stuck busy state** (Defective Settings, `/rdef shortcuts clearbusy on|off`; per character,
  off by default): a press held only by the client's busy count, with nothing of RynthDefective's in flight or
  unanswered, no animation, RynthAi with no action of its own out, and no "You're too busy" from the server in
  the last 5 s, resets the count as RynthAi's `/ra clearbusy` does (the engine's busy-count reset; not its
  *stop moving*), then the item is used: once per press, logged. Off by default because a reset also wipes
  actions the server hasn't answered that RynthDefective can't see (a click in the game's own windows, its own
  toolbar keys, another plugin's), and their late answers then take the count below zero (the engine repairs
  that after 2 s); RynthAi stopped resetting the count for that reason. With it off a leftover holds a press
  for at most 4 s.
* A key pressed in portal space (a death's too) is not kept for later: it says so.
* After a wield from a fighting stance the stance follows the weapon (wand: magic, melee weapon: melee,
  launcher with ammunition in hand: missile) once the character is still. At peace you stay at peace. A bow
  without arrows in hand returns you to peace rather than leaving a stance it can't use. Shields, armor and
  rings leave the stance alone.
* While RynthAi's macro runs, weapons/wands/shields are **not** wielded (it picks the weapon itself and would
  swap back) unless *Allow weapon swaps while RynthAi's macro runs* is on. Kits, potions and the rest always work.

## Commands

```
/rdef                         show / hide the bar
/rdef settings                RynthDefective's settings window
/rdef panels | selection      show / hide the Game Panels buttons, the Selection window
/rdef use <slot | item name>  use the slot's item, or the carried item with that name
/rdef set <slot> [item name]  put the selected item (or the named one) in a slot
/rdef spell <slot> <name>     put one of your spells in a slot (it casts it)
/rdef clear <slot>            empty a slot
/rdef bind <slot> <key>       bind a key (F9, Ctrl+1 ...) to the slot; /rdef unbind <slot>
/rdef weapons [search]        show the weapon picker
/rdef fav                     make the selected weapon a favourite (or take it out)
/rdef slots <n>               how many slots the script-window bar shows (1-24)
/rdef bars                    the extra Shortcuts bars; /rdef bars add | remove <2-5> | slots <2-5> <1-24> |
                              show <2-5> (show or hide it)
/rdef shortcuts clearbusy [on|off]  a press held only by a stuck busy count clears it first (off by default)
/rdef qty [n | max | half]    the quantity box's number (alone: what it is and what it's of)
/rdef split [n] [into <pack>] split n (or the box's number) off the selected stack; pack: main, a side
                              pack's name, or open (the chest you have open)
/rdef give [n]                give n of the selected stack to the last player or NPC you selected
/rdef buy [n] [item name]     buy n of the selected vendor item (or the one named) from the open vendor
/rdef daynight [show | hide] show / hide the Day/Night timer (alone: toggle); says the time now
/rdef list | status | help
```

`/rdef use`, `set`, `spell`, `clear`, `bind` take RynthDefective's own slot numbers (1-42): on the Shortcuts bar own
slot N is bar slot 18+N, or bar slot N when the bar is independent of the game's toolbar.

## Settings

Shortcuts bar: the windows shown, the quantity box under the selection (on by default), independent of the
game's toolbar, slot picture size, shortcuts clear a stuck busy state (off by default), the extra bars (shown,
slots, remove, add), allow weapon swaps under RynthAi, picker row icon size. Script-window bar: slots (1-24), slots per row, button size, picker row
icon size, item icons on/off, counts and keys on/off, allow weapon swaps under RynthAi, shortcuts clear a stuck
busy state. Kept per character
with the slots, favourites and recent weapons in
`<RynthSuite>\RynthDefective\characters\<server>_<character>.json`.

## Not in 0.1

Pages, arrow auto-equip, gear highlight, summon cooldown counters and the war-magic element changer
(Defective has them).

## Needs

RynthCore engine API 71 with ImGui for the windows (op level 2 for pictures; text buttons without it);
API 90 for the Shortcuts bar, Game Panels and Selection. `/rdef use` works on any engine with UseObject.
The quantity commands: splitting on any engine, giving with a count API 62, buying API 67.
