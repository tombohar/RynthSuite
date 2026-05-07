# RynthSuite

RynthCore-based plugins and tools for Asheron's Call. Requires [RynthCore](https://github.com/tombohar/RynthCore) — a .NET 10 NativeAOT injection framework for the AC client.

---

## Plugins

### RynthAi

A combat and navigation assistant for Asheron's Call, ported from the legacy NexTank/NexSuite codebase. Runs as an ImGui overlay inside the AC client via RynthCore. (Project history: originally named NexSuite.)

**Features (in progress):**
- ImGui dashboard with live health/mana/stamina display
- Navigation engine with waypoint routing (VTank `.nav` format)
- Nav marker overlay rendered in-world
- World object cache (creature tracking, inventory classification)
- Line-of-sight raycasting using AC `.dat` geometry
- Buff manager, combat manager, spell database (embedded vtank spelldump)
- Missile crafting manager
- Meta file parser/writer (.met) with expression engine and quest tracker
- Loot evaluation, salvage manager, inventory management
- Dungeon pathfinder, jumper, mana stone manager

**Chat commands (in-game):**
| Command | Description |
|---|---|
| `/na cache` | Show creature and inventory cache summary |
| `/na cache2` | Show raw inventory GUIDs for diagnostics |
| `/na cast <spellId>` | Cast a spell at current target |
| `/na raycast` | Show raycast system status |
| `/na lostest` | Run line-of-sight test against current target |

---

## Tools

Cross-platform Avalonia editors built on the shared LootSdk. These target `net10.0` (no `-windows` suffix) and run on Windows, Linux, and macOS.

- **`Tools/RynthCore.LootEditor`** — Avalonia loot rule editor for VTank-format `.utl` profiles
- **`Tools/RynthCore.MonsterEditor`** — Avalonia monster profile editor
- **`Tools/RynthCore.LootSdkTests`** — LootSdk test harness

## Shared

- **`Shared/RynthCore.LootSdk`** — clean-room VTank loot profile parser/writer + `MaterialTypes` + `SalvageCombineSettings` + AC game enums. `net10.0`, fully cross-platform.

---

## Requirements

- [RynthCore](https://github.com/tombohar/RynthCore) built and deployed to `C:\Games\RynthCore\`
- **.NET 10 SDK (x86)** for the plugin (NativeAOT)
- .NET 10 SDK for the tools (any platform)
- Asheron's Call client installed at `C:\Turbine\Asheron's Call\` (for raycasting `.dat` access)
- RynthCore cloned at `C:\Projects\RynthCore\` (sibling to this repo) — project references resolve there

## Building

### Plugin

```powershell
cd Plugins\RynthCore.Plugin.RynthAi
dotnet publish -c Release
```

The publish target copies the output DLL automatically to RynthCore's engine `Plugins\` folder.

### Tools

```powershell
cd Tools\RynthCore.LootEditor
dotnet build -c Release
```

(Same for `MonsterEditor` and `LootSdkTests`.)

For the full plugin build/deploy story see [`BUILD.md`](BUILD.md).

## Project Structure

```
RynthSuite/
├── Plugins/
│   └── RynthCore.Plugin.RynthAi/         The plugin (NativeAOT x86)
│       ├── Combat/                       World object cache, buff/combat/spell
│       │                                  managers, monster matcher, missile
│       │                                  crafting, fellowship tracker
│       ├── CreatureData/                 Creature profile store
│       ├── LegacyUi/                     ImGui dashboards ported from NexTank
│       │                                  (radar, monsters, nav, weapons,
│       │                                   chat, advanced settings, dungeon
│       │                                   map)
│       ├── Loot/                         Inventory + loot + salvage managers
│       ├── Maps/                         Dungeon map texture loader
│       ├── Meta/                         .met file parser/writer, expression
│       │                                  engine, quest tracker
│       ├── Raycasting/                   .dat geometry loader, LOS engine
│       ├── PluginExports.cs              NativeAOT unmanaged exports
│       └── RynthAiPlugin.cs              Plugin entry point
├── Shared/
│   └── RynthCore.LootSdk/                Cross-platform loot profile SDK
├── Tools/
│   ├── RynthCore.LootEditor/             Avalonia loot rule editor
│   ├── RynthCore.LootSdkTests/           LootSdk test harness
│   └── RynthCore.MonsterEditor/          Avalonia monster profile editor
└── Docs/                                 Code reviews + Chorizite gap analysis
```

## License

MIT — see [LICENSE](LICENSE).
