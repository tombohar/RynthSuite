# RynthAi: Quests and Pets on every server; "Server: <name>" in the right-click menu

Pairs with the matching RynthCore commit on `sk`.

## What changed

- **Quests and Pets are universal.**
  - They were Infinite Leaftide tabs. Now they show on every server, in every Mini Remote: the
    floating window, RynthCore's Mini Remote window, and that window popped out.
  - The tab bar runs: Remote, Quests, Pets. On ILT worlds the ILT tabs follow: Character,
    Banking, Gear, Games.
  - The quest tracker (`/myquests`) and the pet roster, priorities and healing pet tick and read
    chat on every world (`IltHubController.IsUniversal`).
  - The ILT-only parts of those tabs show only on ILT worlds:
    - Quests: the quest bonus list (`/qb`).
    - Pets: the Summon Essence Refill and Universal Summoning Mastery charms, and the `/pets` and
      `/shinies` logs.
- **Guardian stays ILT only.** It's still RynthCore's ILT panel, listed under the server section.
- **Right-click menu:**
  - Top: **Character**, with Spells, Quests, Pets, Skills, Inventory, Item Count and Inventory
    HUDs setup.
  - Bottom: **Server: <server name>** (e.g. "Server: Infinite Leaftide"), with the installed
    server plugins' windows. For ILT that's the Character, Banking, Gear and Games tabs,
    Guardian, Bounties and Progression.
  - With an older RynthCore that still lists Quests and Pets under the server, they are left out
    there so they don't show twice.

## Files

- `IltHub/IltSections.cs`: the tab order (universal first) and `IsUniversal`.
- `IltHub/IltHubController.cs`:
  - `SectionAvailable`: Quests and Pets are always available;
  - new `TabShown`;
  - universal features tick and read chat on every world.
- `IltHub/IltPets.cs`, `IltHub/IltQuests.cs`: the ILT-only parts are gated on an ILT world.
- `Huds/MiniRemoteHud.cs`:
  - the tab bar (`TabShown`);
  - Quests and Pets under Character;
  - the "Server: " header;
  - universal tabs skipped in the server section.
- `Tools/RynthCore.RynthAiHostTests/Tests/ServerMenuTests.cs`: a universal-tabs test.

## Testing

- Host tests: 617 of 618 pass. The one failure is the known map-bake test, which needs the real
  dat files. The new universal-tabs test passes.
- Not yet checked in game:
  - Quests and Pets tabs on a non-ILT world;
  - the "Server: Infinite Leaftide" section on ILT;
  - no `/qb`, `/pets` or `/shinies` being sent off ILT.
