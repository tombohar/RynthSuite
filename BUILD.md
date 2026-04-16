# Build & Deploy

RynthSuite plugins produce **NativeAOT x86** DLLs (`PublishAot=true`). You must use `dotnet publish`, not `dotnet build` — a regular build produces a managed-only DLL that the engine loader rejects.

## Prerequisites

- .NET 9 SDK (x86)
- Visual Studio 2022 Build Tools with the .NET desktop and C++ desktop workloads (required by the NativeAOT ILC linker)
- RynthCore cloned at `C:\Projects\RynthCore\` (sibling to RynthSuite — project references point there)
- RynthSuite cloned at `C:\Projects\RynthSuite\`

If the build fails with `'vswhere.exe' is not recognized`, add the VS Installer directory to PATH:

```bash
set PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer
```

## Publish

Always clean first — incremental NativeAOT builds can silently skip recompilation and produce a stale DLL.

```bash
cd C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi
rmdir /s /q obj\Release bin\Release 2>nul
dotnet publish -c Release
```

Output: `bin\Release\net9.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll` (~7 MB)

## Deploy

**AC can stay open** — plugins are shadow-copied at load time, so the file is not locked.

The recommended deploy location is `C:\Games\RynthSuite\RynthAi\` — alongside the plugin's data files (nav profiles, loot profiles, metas). Add this DLL path in the RynthCore launcher's **Plugins** tab. Paths are persisted to `%AppData%\RynthCore\engine.json`.

```bash
copy bin\Release\net9.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll C:\Games\RynthSuite\RynthAi\
```

Alternatively, deploy to the engine's built-in plugin directory (no launcher configuration needed):

```bash
copy bin\Release\net9.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll C:\Games\RynthCore\Runtime\Plugins\
```

After deploying a plugin with AC running, click **RL** on the RynthCore overlay bar to hot-reload.

## Verify

Check file sizes after deploy to confirm the correct DLLs landed:

| File | Expected Size | If Wrong |
|------|--------------|----------|
| `RynthCore.Plugin.RynthAi.dll` | ~7 MB | If ~1 MB, you built the stale stub at `C:\Projects\RynthCore\Plugins\` instead of `C:\Projects\RynthSuite\Plugins\` |

To confirm NativeAOT actually ran, check that `.lib` and `.exp` files exist alongside the DLL in the `native\` directory.

## Gotchas

- **`dotnet build` is not enough.** NativeAOT only runs during `dotnet publish`. A `dotnet build` produces a valid managed DLL that compiles fine but has no unmanaged exports and will be silently ignored by the engine.
- **Clean before publish.** Incremental NativeAOT builds can silently reuse stale output. Delete `obj\Release` and `bin\Release` before every publish.
- **Two RynthAi projects exist.** The real plugin is at `C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi\`. There is a stale stub at `C:\Projects\RynthCore\Plugins\RynthCore.Plugin.RynthAi\` — do not build or deploy from there.
- **Engine publish copies a stale plugin.** The engine's MSBuild target copies the plugin DLL into the engine's publish output. If you sync that to `Runtime\`, it overwrites the good plugin with the stale stub. Always deploy the plugin from the RynthSuite path last.
