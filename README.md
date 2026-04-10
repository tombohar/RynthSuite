# RynthSuite

RynthCore-based plugins for Asheron's Call. Requires [RynthCore](https://github.com/tombohar/RynthCore) — a .NET 9 NativeAOT injection framework for the AC client.

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

**Chat commands (in-game):**
| Command | Description |
|---|---|
| `/na cache` | Show creature and inventory cache summary |
| `/na cache2` | Show raw inventory GUIDs for diagnostics |
| `/na cast <spellId>` | Cast a spell at current target |
| `/na raycast` | Show raycast system status |
| `/na lostest` | Run line-of-sight test against current target |

---

## Requirements

- [RynthCore](https://github.com/tombohar/RynthCore) built and deployed to `C:\Games\RynthCore\`
- .NET 9 SDK (x86)
- Asheron's Call client installed at `C:\Turbine\Asheron's Call\` (for raycasting .dat access)

## Building

```bash
cd Plugins/RynthCore.Plugin.RynthAi
dotnet publish -c Release
```

The publish target copies the output DLL automatically to the RynthCore engine's `Plugins\` folder.

RynthCore must be at `C:\Projects\RynthCore\` (sibling to this repo) for the project references to resolve.

## Project Structure

```
RynthSuite/
└── Plugins/
    └── RynthCore.Plugin.RynthAi/
        ├── Combat/          World object cache, buff/combat/spell managers
        ├── LegacyUi/        ImGui dashboard panels (ported from legacy UI)
        ├── Raycasting/      .dat geometry loader, LOS engine
        ├── RynthAiPlugin.cs   Plugin entry point
        └── PluginExports.cs NativeAOT unmanaged exports
```

## License

MIT — see [LICENSE](LICENSE).
