# Build & Deploy

RynthSuite ships two build flavors:

- **Plugins** (`Plugins/RynthCore.Plugin.RynthAi/`) — **NativeAOT x86** DLLs targeting `net10.0-windows`. You must use `dotnet publish`, not `dotnet build` — a regular build produces a managed-only DLL that the engine loader rejects.
- **Tools and SDK** (`Tools/`, `Shared/RynthCore.LootSdk/`) — managed `net10.0` projects. **Cross-platform.** Build with normal `dotnet build` on Windows, Linux, or macOS.

## Prerequisites

- **.NET 10 SDK** (x86 components required for the plugin's NativeAOT publish; the Tools build with the regular SDK on any OS)
- Visual Studio 2022 Build Tools with the **.NET desktop** and **C++ desktop** workloads — required by the NativeAOT ILC linker (Windows only, plugin only)
- RynthCore cloned at `C:\Projects\RynthCore\` (sibling to RynthSuite — project references point there)
- RynthSuite cloned at `C:\Projects\RynthSuite\`

If the plugin build fails with `'vswhere.exe' is not recognized`, add the VS Installer directory to PATH:

```powershell
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
```

## Publish — Plugin (NativeAOT)

Always clean first — incremental NativeAOT builds can silently skip recompilation and produce a stale DLL.

```powershell
cd C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi
Remove-Item -Recurse -Force obj\Release, bin\Release -ErrorAction SilentlyContinue
dotnet publish -c Release
```

Output: `bin\Release\net10.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll` (~7 MB)

The plugin's MSBuild target also copies the published DLL into RynthCore's engine `bin\Release\net10.0-windows\win-x86\Plugins\` so a freshly built engine picks it up automatically. (RynthCore's engine publish has previously had a bug where it could overwrite the real plugin with a stale stub from `RynthCore/Plugins/`. That stale stub has been removed from RynthCore — if you see this regress, check that `C:\Projects\RynthCore\Plugins\` doesn't exist.)

## Build — Tools & SDK (cross-platform)

Plain `dotnet build` works for these:

```powershell
cd C:\Projects\RynthSuite\Tools\RynthCore.LootEditor
dotnet build -c Release
```

```powershell
cd C:\Projects\RynthSuite\Tools\RynthCore.MonsterEditor
dotnet build -c Release
```

```powershell
cd C:\Projects\RynthSuite\Tools\RynthCore.LootSdkTests
dotnet build -c Release
dotnet run -c Release
```

These projects target `net10.0` (no `-windows` suffix) and use Avalonia 11.2.3, so they build and run identically on Windows, Linux, and macOS.

## Deploy — Plugin

**AC can stay open** — plugins are shadow-copied at load time, so the file is not locked.

The recommended deploy location is `C:\Games\RynthSuite\RynthAi\` — alongside the plugin's data files (nav profiles, loot profiles, metas). Add this DLL path in the RynthCore launcher's **Plugins** tab. Paths are persisted to `%AppData%\RynthCore\engine.json`.

```powershell
copy bin\Release\net10.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll C:\Games\RynthSuite\RynthAi\
```

Alternatively, deploy to the engine's built-in plugin directory (no launcher configuration needed):

```powershell
copy bin\Release\net10.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll C:\Games\RynthCore\Runtime\Plugins\
```

After deploying a plugin with AC running, click **RL** on the RynthCore overlay bar to hot-reload.

## Verify

| File | Expected Size | If Wrong |
|------|--------------|----------|
| `RynthCore.Plugin.RynthAi.dll` | ~7 MB | If ~1 MB, you ran `dotnet build` instead of `dotnet publish` (no NativeAOT exports) |

To confirm NativeAOT actually ran, check that `.lib` and `.exp` files exist alongside the DLL in the publish output's `native\` directory.

## Gotchas

- **`dotnet build` is not enough for the plugin.** NativeAOT only runs during `dotnet publish`. A `dotnet build` produces a valid managed DLL that compiles fine but has no unmanaged exports and will be silently ignored by the engine.
- **Clean before publish.** Incremental NativeAOT builds can silently reuse stale output. Delete `obj\Release` and `bin\Release` before every plugin publish.
- **Tools and SDK do not need `dotnet publish`.** They're plain managed assemblies — `dotnet build` is fine and faster.
