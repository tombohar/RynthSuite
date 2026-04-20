using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// All /ra chat commands — partial class split from RynthAiPlugin.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private void ChatLine(string text)
    {
        Host.WriteToChat(text, 1);
    }

    private void HandleHelpCommand()
    {
        ChatLine("[RynthAi] === Commands ===");
        ChatLine("[RynthAi] /ra fellow       â€” fellowship diagnostics and queries");
        ChatLine("[RynthAi] /ra help          — show this list");
        ChatLine("[RynthAi] /ra power <0-100|auto> — set attack power (auto = recklessness-aware)");
        ChatLine("[RynthAi] /ra cast <spellId> — cast spell on current target");
        ChatLine("[RynthAi] /ra buffs         — show active buff timers");
        ChatLine("[RynthAi] /ra scan          — show nearby monsters");
        ChatLine("[RynthAi] /ra cache         — show object cache summary");
        ChatLine("[RynthAi] /ra cache2        — show raw object cache IDs");
        ChatLine("[RynthAi] /ra attackable    — check if target is attackable");
        ChatLine("[RynthAi] /ra wielded       — show wielded items");
        ChatLine("[RynthAi] /ra dumpprops     — dump player properties");
        ChatLine("[RynthAi] /ra mexec <expr>  — evaluate meta expression");
        ChatLine("[RynthAi] /ra listvars      — show session variables");
        ChatLine("[RynthAi] /ra listpvars     — show persistent variables");
        ChatLine("[RynthAi] /ra listgvars     — show global variables");
        ChatLine("[RynthAi] /ra give [count] <item> to <player>         — give exact-named item(s) to player");
        ChatLine("[RynthAi] /ra givep [count] <item> to <player>        — partial item name match");
        ChatLine("[RynthAi] /ra givexp [count] <item> to <player>       — partial player name match");
        ChatLine("[RynthAi] /ra givepp [count] <item> to <player>       — partial item and player");
        ChatLine("[RynthAi] /ra giver [count] <regex> to <player>       — regex item name match");
        ChatLine("[RynthAi] /ra ig <profile> to <player>                — give items matching loot profile");
        ChatLine("[RynthAi] /ra igp <profile> to <player>               — ig with partial player name");
        ChatLine("[RynthAi] /ra use[i|l][p|pi|lp] <name> [on <name2>]  — use item (i=inv, l=land, p=partial)");
        ChatLine("[RynthAi] /ra raycast       — raycast system status");
        ChatLine("[RynthAi] /ra lostest       — line-of-sight test to target");
        ChatLine("[RynthAi] /ra buildinfo     — nearby geometry info");
        ChatLine("[RynthAi] /ra navdebug      — show nav coordinate/debug info");
        ChatLine("[RynthAi] /ra corpseinfo    — show corpse range/open diagnostics");
        ChatLine("[RynthAi] /ra corpsecheck   — explain whether a corpse would be looted");
        ChatLine("[RynthAi] /ra corpseopen    — force the nearest corpse open flow");
        ChatLine("[RynthAi] /ra fellowinfo    — show fellowship tracker state");
        ChatLine("[RynthAi] /ra lootparse     — inspect the selected loot profile");
        ChatLine("[RynthAi] /ra lootcheckinv  — test the loot profile against inventory");
        ChatLine("[RynthAi] /ra lootcheck     — classify selected item (on|off = auto on click)");
        ChatLine("[RynthAi] /ra dumpinv       — dump all inventory items (cache + direct)");
        ChatLine("[RynthAi] /ra clearbusy     — force-clear busy state (hourglass cursor)");
        ChatLine("[RynthAi] /ra settings savechar <name> — save current settings to named profile (create if new)");
        ChatLine("[RynthAi] /ra settings loadchar <name> — load named settings profile (create from current if new)");
    }

    private void HandlePowerCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ChatLine("[RynthAi] Settings not ready."); return; }

        if (parts.Length < 3)
        {
            string current = settings.MeleeAttackPower < 0 ? "auto" : $"{settings.MeleeAttackPower}%";
            string currentMissile = settings.MissileAttackPower < 0 ? "auto" : $"{settings.MissileAttackPower}%";
            ChatLine($"[RynthAi] Melee power: {current}, Missile power: {currentMissile}");
            ChatLine("[RynthAi] Usage: /ra power <0-100|auto>");
            return;
        }

        string val = parts[2].ToLower();
        if (val == "auto")
        {
            settings.MeleeAttackPower = -1;
            settings.MissileAttackPower = -1;
            ChatLine("[RynthAi] Attack power set to auto.");
        }
        else if (int.TryParse(val, out int pct) && pct >= 0 && pct <= 100)
        {
            settings.MeleeAttackPower = pct;
            settings.MissileAttackPower = pct;
            ChatLine($"[RynthAi] Attack power set to {pct}%.");
        }
        else
        {
            ChatLine("[RynthAi] Usage: /ra power <0-100|auto>");
        }
    }

    private void HandleAttackableCommand()
    {
        uint targetId = _currentTargetId;
        if (targetId == 0)
        {
            ChatLine("[RynthAi] No target selected — click something first.");
            return;
        }

        Host.TryGetObjectName(targetId, out string name);
        ChatLine($"[RynthAi] Target: {name} (0x{targetId:X8})");
        ChatLine($"[RynthAi] HasObjectIsAttackable: {Host.HasObjectIsAttackable}");

        if (!Host.HasObjectIsAttackable)
        {
            ChatLine("[RynthAi] API not available — engine too old.");
            return;
        }

        bool attackable = Host.ObjectIsAttackable(targetId);
        ChatLine($"[RynthAi] ObjectIsAttackable => {attackable}");
    }

    private void HandleCastCommand(string[] parts)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2], out int spellId))
        {
            ChatLine("[RynthAi] Usage: /ra cast <spellId>");
            return;
        }

        if (_currentTargetId == 0)
        {
            ChatLine("[RynthAi] No target selected.");
            return;
        }

        if (!Host.HasCastSpell)
        {
            ChatLine("[RynthAi] CastSpell not available — hook not found.");
            return;
        }

        bool ok = Host.CastSpell(_currentTargetId, spellId);
        ChatLine($"[RynthAi] CastSpell(target=0x{_currentTargetId:X8}, spell={spellId}) => {ok}");
    }

    private void HandleCacheCommand()
    {
        if (_objectCache == null)
        {
            ChatLine("[RynthAi] Object cache not initialized (not logged in yet).");
            return;
        }

        int creatures = 0, directInventory = 0, wands = 0;
        foreach (var wo in _objectCache.GetLandscape())
        {
            creatures++;
            if (creatures <= 5)
                ChatLine($"[RynthAi] Creature: {wo.Name} (0x{(uint)wo.Id:X8})");
        }
        foreach (var wo in _objectCache.GetDirectInventory(forceRefresh: true))
        {
            directInventory++;
            if (wo.ObjectClass == AcObjectClass.WandStaffOrb) wands++;
            if (directInventory <= 8)
                ChatLine($"[RynthAi] Inv [{wo.ObjectClass}]: {wo.Name} (0x{(uint)wo.Id:X8})");
        }
        var stats = _objectCache.GetStats();
        ChatLine($"[RynthAi] Cache: {creatures} creatures | cache inv={stats.Inventory} | direct inv={directInventory} ({wands} wands) — type /ra cache2 for details");
    }

    private void HandleCache2Command()
    {
        if (_objectCache == null) { ChatLine("[RynthAi] Cache not ready."); return; }
        var (total, creatures, inventory, landscape, pending) = _objectCache.GetStats();
        ChatLine($"[RynthAi] Cache stats: {total} total | {creatures} creatures | {inventory} inv | {landscape} landscape | {pending} pending");
        ChatLine("[RynthAi] Direct inventory snapshot:");
        int shown = 0;
        foreach (var wo in _objectCache.GetDirectInventory(forceRefresh: true))
        {
            if (shown++ >= 12) break;
            string wieldTag = wo.WieldedLocation > 0 ? $" wield=0x{wo.WieldedLocation:X}" : "";
            ChatLine($"[RynthAi] inv 0x{(uint)wo.Id:X8} [{wo.ObjectClass}]{wieldTag} {wo.Name}");
        }
        if (shown == 0) ChatLine("[RynthAi] Direct inventory snapshot is empty.");
    }

    private void HandleRaycastCommand(string[] parts)
    {
        if (_raycast == null)
        {
            ChatLine("[RynthAi] Raycast system not initialized.");
            return;
        }

        ChatLine($"[RynthAi] Raycast: {(_raycast.IsInitialized ? "READY" : "NOT LOADED")}");
        ChatLine($"[RynthAi]   Status: {_raycast.StatusMessage}");
    }

    private void HandleLosTestCommand()
    {
        if (_raycast == null || !_raycast.IsInitialized)
        {
            ChatLine("[RynthAi LOS] Raycast system not initialized.");
            return;
        }

        if (_currentTargetId == 0)
        {
            ChatLine("[RynthAi LOS] No target selected — select a monster first.");
            return;
        }

        Host.TryGetObjectName(_currentTargetId, out string targetName);
        ChatLine($"[RynthAi LOS] Target: {targetName} (0x{_currentTargetId:X8})");

        // Player position
        if (!Host.TryGetPlayerPose(out uint pCell, out float px, out float py, out float pz,
                out _, out _, out _, out _))
        {
            ChatLine("[RynthAi LOS] Player position unavailable.");
            return;
        }

        uint pBlockX = (pCell >> 24) & 0xFF;
        uint pBlockY = (pCell >> 16) & 0xFF;
        float playerGX = pBlockX * 192.0f + px;
        float playerGY = pBlockY * 192.0f + py;
        ChatLine($"[RynthAi LOS] Player Landcell=0x{pCell:X8} Block=({pBlockX},{pBlockY})");
        ChatLine($"[RynthAi LOS] Player Local=({px:F1},{py:F1},{pz:F1})");
        ChatLine($"[RynthAi LOS] Player Global=({playerGX:F1},{playerGY:F1},{pz:F1})");

        // Target position
        if (!Host.TryGetObjectPosition(_currentTargetId, out uint tCell, out float tx, out float ty, out float tz))
        {
            ChatLine("[RynthAi LOS] Target position unavailable.");
            return;
        }

        uint tBlockX = (tCell >> 24) & 0xFF;
        uint tBlockY = (tCell >> 16) & 0xFF;
        float targetGX = tBlockX * 192.0f + tx;
        float targetGY = tBlockY * 192.0f + ty;
        ChatLine($"[RynthAi LOS] Target Landcell=0x{tCell:X8}");
        ChatLine($"[RynthAi LOS] Target Global=({targetGX:F1},{targetGY:F1},{tz:F1})");

        float dx = targetGX - playerGX;
        float dy = targetGY - playerGY;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        ChatLine($"[RynthAi LOS] Distance: {dist:F1}m, Delta=({dx:F1},{dy:F1})");

        // Load geometry
        var geometry = _raycast.GeometryLoader.GetLandblockGeometry(pCell);
        ChatLine($"[RynthAi LOS] Geometry: {geometry?.Count ?? 0} volumes loaded");

        if (geometry != null && geometry.Count > 0)
        {
            var origin = new Raycasting.Vector3(playerGX, playerGY, pz + 1.0f);
            var targetPos = new Raycasting.Vector3(targetGX, targetGY, tz + 1.0f);

            var rayDir = targetPos - origin;
            float rayLen = rayDir.Length();
            if (rayLen > 0.001f) rayDir = rayDir / rayLen;

            int hitCount = 0;
            foreach (var vol in geometry)
            {
                if (vol.RayIntersect(origin, rayDir, rayLen, out float volDist))
                {
                    if (volDist > 0.5f && volDist < rayLen - 1.0f)
                    {
                        hitCount++;
                        if (hitCount <= 5)
                            ChatLine($"[RynthAi LOS]   HIT at {volDist:F2}m: center=({vol.Center.X:F1},{vol.Center.Y:F1},{vol.Center.Z:F1}) type={vol.Type}");
                    }
                }
            }

            bool nearby = RaycastEngine.HasNearbyGeometry(origin, targetPos, geometry);
            bool blocked = RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);
            ChatLine($"[RynthAi LOS] HasNearbyGeometry: {nearby}");
            ChatLine($"[RynthAi LOS] IsLinearPathBlocked: {blocked}");
            ChatLine($"[RynthAi LOS] Summary: {geometry.Count} total volumes, {hitCount} ray hits");
        }
    }

    private void HandleMexecCommand(string[] parts)
    {
        if (_metaManager == null)
        {
            ChatLine("[RynthAi] Meta not ready (not logged in yet).");
            return;
        }

        if (parts.Length < 3)
        {
            ChatLine("[RynthAi] Usage: /ra mexec <expression>");
            ChatLine("[RynthAi]   e.g. /ra mexec setvar[Nav, NTTest5]");
            ChatLine("[RynthAi]   e.g. /ra mexec getcharintprop[25]");
            return;
        }

        string expr = string.Join(" ", parts, 2, parts.Length - 2);
        ChatLine($"[RynthAi] Evaluating expression: \"{expr}\"");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result = _metaManager.Expressions.Evaluate(expr);
        sw.Stop();
        string typeTag;
        string display = result;
        if (result.Length > 2 && result[0] == 'D' && result[1] == ':' &&
            _metaManager.Expressions.TryGetDict(result, out var dictContents) && dictContents != null)
        {
            typeTag = "[Dictionary]";
            display = "[" + string.Join(",", dictContents.Select(kv => $"{kv.Key}=>{kv.Value}")) + "]";
        }
        else if (result.Length > 3 && result[0] == 'S' && result[1] == 'W' && result[2] == ':')
            typeTag = "[Stopwatch]";
        else if (result.StartsWith("ERR:", StringComparison.Ordinal))
            typeTag = "[error]";
        else if (result.Length >= 2 && result[0] == '[' && result[result.Length - 1] == ']')
            typeTag = "[List]";
        else if (double.TryParse(result, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            typeTag = "[number]";
        else
            typeTag = "[string]";
        ChatLine($"[RynthAi] Result: {typeTag} {display} ({sw.Elapsed.TotalMilliseconds:F3}ms)");
    }

    private void HandleListVarsCommand()
    {
        if (_metaManager == null)
        {
            ChatLine("[RynthAi] Meta not ready (not logged in yet).");
            return;
        }

        var vars = _metaManager.Expressions.Variables;
        if (vars.Count == 0)
        {
            ChatLine("[RynthAi] No variables set.");
            return;
        }

        ChatLine($"[RynthAi] Defined variables:");
        foreach (var kv in vars)
        {
            string typeLabel;
            string val = kv.Value;
            if (val.Length >= 2 && val[0] == '[' && val[val.Length - 1] == ']')
                typeLabel = "List";
            else if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
                typeLabel = "number";
            else
                typeLabel = "string";
            ChatLine($"[RynthAi] {kv.Key} ({typeLabel}) = {val}");
        }
    }

    private void HandleListPvarsCommand()
    {
        if (_metaManager == null) { ChatLine("[RynthAi] Meta not ready."); return; }
        var pvars = _metaManager.Expressions.Pvars;
        if (pvars.Count == 0) { ChatLine("[RynthAi] No persistent variables set."); return; }
        ChatLine($"[RynthAi] Persistent variables ({pvars.Count}):");
        foreach (var kv in pvars)
            ChatLine($"[RynthAi]   {kv.Key} = {kv.Value}");
    }

    private void HandleListGvarsCommand()
    {
        if (_metaManager == null) { ChatLine("[RynthAi] Meta not ready."); return; }
        var gvars = _metaManager.Expressions.Gvars;
        if (gvars.Count == 0) { ChatLine("[RynthAi] No global variables set."); return; }
        ChatLine($"[RynthAi] Global variables ({gvars.Count}):");
        foreach (var kv in gvars)
            ChatLine($"[RynthAi]   {kv.Key} = {kv.Value}");
    }

    private void HandleDumpPropsCommand()
    {
        uint playerId = Host.GetPlayerId();
        if (playerId == 0)
        {
            ChatLine("[RynthAi] Not logged in.");
            return;
        }

        // Int properties 0-400
        int intCount = 0;
        if (Host.HasGetObjectIntProperty)
        {
            ChatLine("[RynthAi] === Int Properties (0-400) ===");
            for (uint i = 0; i <= 400; i++)
            {
                if (Host.TryGetObjectIntProperty(playerId, i, out int v))
                {
                    string name = PropertyNames.GetIntName(i);
                    ChatLine(name != null
                        ? $"[RynthAi]   IntProp[{i}] = {v} ({name})"
                        : $"[RynthAi]   IntProp[{i}] = {v}");
                    intCount++;
                }
            }
            ChatLine($"[RynthAi] Found {intCount} int properties.");
        }
        else ChatLine("[RynthAi] Int property API not available.");

        // Bool properties 0-300
        int boolCount = 0;
        if (Host.HasGetObjectBoolProperty)
        {
            ChatLine("[RynthAi] === Bool Properties (0-300) ===");
            for (uint i = 0; i <= 300; i++)
            {
                if (Host.TryGetObjectBoolProperty(playerId, i, out bool v))
                {
                    string name = PropertyNames.GetBoolName(i);
                    ChatLine(name != null
                        ? $"[RynthAi]   BoolProp[{i}] = {(v ? "1" : "0")} ({name})"
                        : $"[RynthAi]   BoolProp[{i}] = {(v ? "1" : "0")}");
                    boolCount++;
                }
            }
            ChatLine($"[RynthAi] Found {boolCount} bool properties.");
        }
        else ChatLine("[RynthAi] Bool property API not available.");

        // String properties 0-60
        int strCount = 0;
        if (Host.HasGetObjectStringProperty)
        {
            ChatLine("[RynthAi] === String Properties (0-60) ===");
            for (uint i = 0; i <= 60; i++)
            {
                if (Host.TryGetObjectStringProperty(playerId, i, out string v))
                {
                    string name = PropertyNames.GetStringName(i);
                    ChatLine(name != null
                        ? $"[RynthAi]   StringProp[{i}] = {v} ({name})"
                        : $"[RynthAi]   StringProp[{i}] = {v}");
                    strCount++;
                }
            }
            ChatLine($"[RynthAi] Found {strCount} string properties.");
        }
        else ChatLine("[RynthAi] String property API not available.");

        ChatLine($"[RynthAi] Total: {intCount} int, {boolCount} bool, {strCount} string.");
    }

    private void HandleWieldedCommand()
    {
        if (_objectCache == null) { ChatLine("[RynthAi] Cache not ready."); return; }

        // Use CurrentWieldedLocation (stype=10, InqInt fallback) — more robust than
        // TryGetObjectWielderInfo which requires the phys-obj offset probe to succeed.
        ChatLine("[RynthAi] === Wielded Items (inventory cache scan) ===");
        int count = 0;
        int total = 0;
        foreach (var wo in _objectCache.GetInventory())
        {
            total++;
            int wieldLoc = wo.Values(LongValueKey.CurrentWieldedLocation, 0);
            if (wieldLoc <= 0)
                continue;
            count++;
            ChatLine($"[RynthAi]   0x{(uint)wo.Id:X8} [{wo.ObjectClass}] loc=0x{wieldLoc:X8} \"{wo.Name}\"");
        }
        ChatLine($"[RynthAi] {count} wielded item(s) out of {total} inventory item(s) in cache.");
        ChatLine($"[RynthAi] CombatMode={_combatManager?.CurrentCombatMode ?? 0} (1=peace 2=melee 4=missile 8=magic)");
    }

    private void HandleBuildInfoCommand()
    {
        if (_raycast == null || !_raycast.IsInitialized)
        {
            ChatLine("[RynthAi] Raycast not initialized.");
            return;
        }
        if (_playerId == 0) { ChatLine("[RynthAi] Not logged in."); return; }

        if (!Host.TryGetPlayerPose(out uint pCell, out float px, out float py, out float pz,
                out _, out _, out _, out _))
        {
            ChatLine("[RynthAi] Player position unavailable.");
            return;
        }

        uint pBlockX = (pCell >> 24) & 0xFF;
        uint pBlockY = (pCell >> 16) & 0xFF;
        ChatLine($"[RynthAi] === Build Info for landcell 0x{pCell:X8} ===");

        var geometry = _raycast.GeometryLoader.GetLandblockGeometry(pCell);
        if (geometry == null || geometry.Count == 0)
        {
            ChatLine("[RynthAi] No geometry loaded for this landblock.");
            return;
        }

        // Tally volume types
        int spheres = 0, aabbs = 0, cylinders = 0, meshes = 0, polygons = 0, other = 0;
        int totalMeshTris = 0;
        float largestDim = 0;
        Raycasting.BoundingVolume largestVol = null;

        foreach (var vol in geometry)
        {
            float maxDim = Math.Max(vol.Dimensions.X, Math.Max(vol.Dimensions.Y, vol.Dimensions.Z));
            if (maxDim > largestDim) { largestDim = maxDim; largestVol = vol; }

            switch (vol.Type)
            {
                case Raycasting.BoundingVolume.VolumeType.Sphere: spheres++; break;
                case Raycasting.BoundingVolume.VolumeType.AxisAlignedBox: aabbs++; break;
                case Raycasting.BoundingVolume.VolumeType.Cylinder: cylinders++; break;
                case Raycasting.BoundingVolume.VolumeType.Polygon: polygons++; break;
                case Raycasting.BoundingVolume.VolumeType.TriangleMesh:
                    meshes++;
                    totalMeshTris += (vol.MeshTriangles?.Length ?? 0) / 3;
                    break;
                default: other++; break;
            }
        }

        ChatLine($"[RynthAi] {geometry.Count} total volumes:");
        ChatLine($"[RynthAi]   Spheres={spheres}  AABBs={aabbs}  Cylinders={cylinders}  Meshes={meshes}  Polygons={polygons}  Other={other}");
        if (meshes > 0)
            ChatLine($"[RynthAi]   Mesh triangles total: {totalMeshTris}");
        else
            ChatLine($"[RynthAi]   NO triangle meshes — buildings fell back to AABB/sphere");

        if (largestVol != null)
            ChatLine($"[RynthAi]   Largest: {largestVol.Type} size=({largestVol.Dimensions.X:F1}x{largestVol.Dimensions.Y:F1}x{largestVol.Dimensions.Z:F1}) center=({largestVol.Center.X:F1},{largestVol.Center.Y:F1},{largestVol.Center.Z:F1})");

        // Show nearby volumes (within 50m of player)
        float playerGX = pBlockX * 192.0f + px;
        float playerGY = pBlockY * 192.0f + py;
        ChatLine($"[RynthAi] Volumes within 50m of player:");
        int nearby = 0;
        foreach (var vol in geometry)
        {
            float dx = vol.Center.X - playerGX;
            float dy = vol.Center.Y - playerGY;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist > 50f) continue;

            float maxDim = Math.Max(vol.Dimensions.X, Math.Max(vol.Dimensions.Y, vol.Dimensions.Z));
            // Only show interesting volumes (> 2m or mesh type)
            if (maxDim < 2f && vol.Type != Raycasting.BoundingVolume.VolumeType.TriangleMesh) continue;

            string extra = "";
            if (vol.Type == Raycasting.BoundingVolume.VolumeType.TriangleMesh)
                extra = $" tris={(vol.MeshTriangles?.Length ?? 0) / 3}";

            ChatLine($"[RynthAi]   {dist,5:F1}m {vol.Type} size=({vol.Dimensions.X:F1}x{vol.Dimensions.Y:F1}x{vol.Dimensions.Z:F1}){extra}");
            nearby++;
            if (nearby >= 15) { ChatLine("[RynthAi]   ... (truncated)"); break; }
        }
        if (nearby == 0)
            ChatLine("[RynthAi]   (none > 2m)");

        // Show BLDG and MESH log entries from GeometryLoader
        var diagLog = _raycast.GeometryLoader.DiagLog;
        if (diagLog.Count > 0)
        {
            ChatLine($"[RynthAi] Building/Mesh log entries ({diagLog.Count} total):");
            int shown = 0;
            for (int i = 0; i < diagLog.Count && shown < 25; i++)
            {
                if (diagLog[i].Contains("[BLDG]") || diagLog[i].Contains("[MESH]") || diagLog[i].Contains("Building"))
                {
                    ChatLine($"[RynthAi]   {diagLog[i]}");
                    shown++;
                }
            }
            if (shown == 0)
                ChatLine("[RynthAi]   (no [BLDG] or [MESH] entries — buildings may not exist in this landblock)");
        }

        ChatLine($"[RynthAi] Caches: {_raycast.GeometryLoader.LandblocksCached} landblocks, {_raycast.GeometryLoader.SetupsCached} setups");
    }

    private void HandleNavDebugCommand()
    {
        var settings = _dashboard?.Settings;
        if (settings == null)
        {
            ChatLine("[RynthAi] Nav debug unavailable: settings not ready.");
            return;
        }

        string routeName = string.IsNullOrEmpty(settings.CurrentNavPath)
            ? "None"
            : System.IO.Path.GetFileName(settings.CurrentNavPath);
        ChatLine($"[RynthAi] Nav route: {routeName}");
        ChatLine($"[RynthAi]   Type={settings.CurrentRoute.RouteType} Points={settings.CurrentRoute.Points.Count} Active={settings.ActiveNavIndex} HasNav3D={Host.HasNav3D}");

        bool hostCoordsOk = Host.TryGetCurCoords(out double hostNS, out double hostEW);
        ChatLine(hostCoordsOk
            ? $"[RynthAi]   GetCurCoords: NS={hostNS:F4} EW={hostEW:F4}"
            : "[RynthAi]   GetCurCoords: unavailable");

        bool poseOk = Host.TryGetPlayerPose(out uint cellId, out float x, out float y, out float z, out _, out _, out _, out _);
        if (!poseOk)
        {
            ChatLine("[RynthAi]   Player pose unavailable.");
            return;
        }

        ChatLine($"[RynthAi]   Pose: cell=0x{cellId:X8} local=({x:F2},{y:F2},{z:F2})");

        bool poseCoordsOk = LegacyUi.NavCoordinateHelper.TryConvertPoseToCoords(cellId, x, y, out double poseNS, out double poseEW);
        ChatLine(poseCoordsOk
            ? $"[RynthAi]   Pose->Coords: NS={poseNS:F4} EW={poseEW:F4}"
            : "[RynthAi]   Pose->Coords: conversion failed");

        if (hostCoordsOk && poseCoordsOk)
            ChatLine($"[RynthAi]   Delta(host-pose): dNS={(hostNS - poseNS):F4} dEW={(hostEW - poseEW):F4}");

        if (settings.CurrentRoute.Points.Count == 0)
            return;

        int nearest = 0;
        double bestD = double.MaxValue;
        double basisNS = hostCoordsOk ? hostNS : poseNS;
        double basisEW = hostCoordsOk ? hostEW : poseEW;
        for (int i = 0; i < settings.CurrentRoute.Points.Count; i++)
        {
            var point = settings.CurrentRoute.Points[i];
            if (point.Type != LegacyUi.NavPointType.Point)
                continue;

            double d = Math.Sqrt(Math.Pow(point.NS - basisNS, 2) + Math.Pow(point.EW - basisEW, 2));
            if (d < bestD)
            {
                bestD = d;
                nearest = i;
            }
        }

        ChatLine($"[RynthAi]   Nearest point now: [{nearest}] dist={(bestD * 240.0):F1} yd");

        if (settings.ActiveNavIndex >= 0 && settings.ActiveNavIndex < settings.CurrentRoute.Points.Count)
        {
            var active = settings.CurrentRoute.Points[settings.ActiveNavIndex];
            double activeDistYards = Math.Sqrt(Math.Pow(active.NS - basisNS, 2) + Math.Pow(active.EW - basisEW, 2)) * 240.0;
            ChatLine($"[RynthAi]   Active point: [{settings.ActiveNavIndex}] {active.Type} NS={active.NS:F4} EW={active.EW:F4} Z={active.Z:F2} dist={activeDistYards:F1} yd");
        }
    }

    private void HandleScanCommand()
    {
        if (_objectCache == null) { ChatLine("[RynthAi] Cache not ready."); return; }
        if (_playerId == 0) { ChatLine("[RynthAi] Not logged in."); return; }

        double maxDist = _dashboard?.Settings?.MonsterRange ?? 30.0;
        int playerId = (int)_playerId;
        bool hasRaycast = _raycast != null && _raycast.IsInitialized && _dashboard?.Settings?.EnableRaycasting == true;

        ChatLine($"[RynthAi] === Monster Scan (range {maxDist:F0}m) ===");
        int shown = 0;
        foreach (var wo in _objectCache.GetLandscape())
        {
            if (wo.Id == playerId) continue;
            if ((int)wo.ObjectClass != (int)AcObjectClass.Monster) continue;

            double dist = _objectCache.Distance(playerId, wo.Id);
            if (dist > maxDist || dist == double.MaxValue) continue;

            bool attackable = !Host.HasObjectIsAttackable || Host.ObjectIsAttackable((uint)wo.Id);

            string losTag;
            if (!attackable)
            {
                losTag = " [NOT ATTACKABLE]";
            }
            else if (hasRaycast)
            {
                bool blocked = _raycast!.IsTargetBlocked(Host, (uint)wo.Id, Raycasting.TargetingFSM.AttackType.Linear);
                losTag = blocked ? " [LOS BLOCKED]" : " [OK]";
            }
            else
            {
                losTag = " [OK]";
            }

            ChatLine($"[RynthAi]   {dist,5:F1}m {losTag} \"{wo.Name}\" (0x{(uint)wo.Id:X8})");
            shown++;
        }
        ChatLine($"[RynthAi] {shown} creature(s) in range. Scanner has {_combatManager?.ScannedTargets.Count ?? 0} valid target(s).");
    }

    private void HandleLootParseCommand(string fullCommand)
    {
        string pathArg = ExtractCommandArgument(fullCommand, "lootparse");
        if (!TryLoadLootProfile(pathArg, out VTankLootProfile profile, out string loadedPath))
            return;

        ChatLine($"[RynthAi] Loot profile: {System.IO.Path.GetFileName(loadedPath)}");
        ChatLine($"[RynthAi]   Path: {loadedPath}");
        ChatLine($"[RynthAi]   Rules: {profile.Rules.Count}");

        int unconditional = 0;
        foreach (var rule in profile.Rules)
        {
            string[] parts = rule.RawInfoLine.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                unconditional++;
        }

        ChatLine($"[RynthAi]   Unconditional rules: {unconditional}");

        int previewCount = Math.Min(profile.Rules.Count, 8);
        for (int i = 0; i < previewCount; i++)
        {
            VTankLootRule rule = profile.Rules[i];
            string name = string.IsNullOrWhiteSpace(rule.Name) ? "<unnamed>" : rule.Name;
            ChatLine($"[RynthAi]   {i + 1}. [{rule.Action}] keep={rule.KeepCount} {name}");
        }

        if (profile.Rules.Count > previewCount)
            ChatLine($"[RynthAi]   ... {profile.Rules.Count - previewCount} more rule(s)");
    }

    private void HandleLootCheckInventoryCommand(string fullCommand)
    {
        if (_objectCache == null)
        {
            ChatLine("[RynthAi] Cache not ready.");
            return;
        }

        string pathArg = ExtractCommandArgument(fullCommand, "lootcheckinv");
        if (!TryLoadLootProfile(pathArg, out VTankLootProfile profile, out _))
            return;

        int total = 0;
        int matched = 0;
        var actionCounts = new System.Collections.Generic.Dictionary<VTankLootAction, int>();
        int shown = 0;

        VTankLootContext lootCtx = new(Host, _playerId) { Cache = _objectCache };
        foreach (var item in _objectCache.GetInventory())
        {
            total++;
            VTankLootRule? firstMatch = null;
            foreach (var rule in profile.Rules)
            {
                if (rule.IsMatch(item, lootCtx))
                {
                    firstMatch = rule;
                    break;
                }
            }

            if (firstMatch == null)
                continue;

            matched++;
            actionCounts.TryGetValue(firstMatch.Action, out int count);
            actionCounts[firstMatch.Action] = count + 1;

            if (shown < 12)
            {
                string ruleName = string.IsNullOrWhiteSpace(firstMatch.Name) ? "<unnamed>" : firstMatch.Name;
                ChatLine($"[RynthAi]   [{firstMatch.Action}] {item.Name} (rule: {ruleName})");
                shown++;
            }
        }

        ChatLine($"[RynthAi] Loot check inventory: matched {matched} of {total} cached item(s).");
        if (matched == 0)
        {
            ChatLine("[RynthAi] No inventory items matched the current loot profile.");
            return;
        }

        foreach (var kvp in actionCounts)
            ChatLine($"[RynthAi]   {kvp.Key}: {kvp.Value}");

        if (matched > shown)
            ChatLine($"[RynthAi]   ... {matched - shown} more matched item(s)");
    }

    // /ra lootcheck               — one-shot classify currently selected item
    // /ra lootcheck on | off      — toggle auto-inspect on selection change
    private void HandleLootCheckSelectedCommand(string[] parts)
    {
        if (parts.Length >= 3)
        {
            string mode = parts[2].Trim().ToLowerInvariant();
            if (mode is "on" or "1" or "true")
            {
                _lootInspectMode = true;
                ChatLine("[RynthAi] Loot inspect: ON — classifying each item you select.");
                return;
            }
            if (mode is "off" or "0" or "false")
            {
                _lootInspectMode = false;
                ChatLine("[RynthAi] Loot inspect: OFF.");
                return;
            }
        }

        if (!Host.HasGetSelectedItemId)
        {
            ChatLine("[RynthAi] Host does not expose selected item id.");
            return;
        }
        uint selected = Host.GetSelectedItemId();
        if (selected == 0)
        {
            ChatLine("[RynthAi] No item selected. Click an item in the world or inventory first.");
            return;
        }
        InspectLootRuleForItem(unchecked((int)selected));
    }

    internal void InspectLootRuleForItem(int itemId, bool quiet = false)
    {
        if (_objectCache == null) return;
        WorldObject? item = _objectCache[itemId];
        if (item == null)
        {
            if (!quiet)
                ChatLine($"[RynthAi] Loot inspect: item 0x{(uint)itemId:X8} not in cache yet.");
            return;
        }
        if (!TryLoadLootProfile(string.Empty, out VTankLootProfile profile, out _))
            return;

        VTankLootContext ctx = new(Host, _playerId) { Cache = _objectCache };
        for (int i = 0; i < profile.Rules.Count; i++)
        {
            VTankLootRule rule = profile.Rules[i];
            if (!rule.IsMatch(item, ctx)) continue;
            string ruleName = string.IsNullOrWhiteSpace(rule.Name) ? $"#{i}" : rule.Name.Trim();
            ChatLine($"[RynthAi] {item.Name}: [{rule.Action}] {ruleName}");
            return;
        }
        ChatLine($"[RynthAi] {item.Name}: no loot rule matched.");
    }

    private bool TryLoadLootProfile(string explicitPath, out VTankLootProfile profile, out string loadedPath)
    {
        string candidatePath = explicitPath;
        if (string.IsNullOrWhiteSpace(candidatePath))
            candidatePath = _dashboard?.Settings?.CurrentLootPath ?? string.Empty;

        candidatePath = candidatePath.Trim().Trim('"');
        loadedPath = candidatePath;
        profile = _loadedLootProfile ?? new VTankLootProfile();

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            ChatLine("[RynthAi] No loot profile selected. Pick one in the dashboard or pass a full path.");
            return false;
        }

        if (!System.IO.File.Exists(candidatePath))
        {
            ChatLine($"[RynthAi] Loot profile not found: {candidatePath}");
            return false;
        }

        if (_loadedLootProfile != null && string.Equals(_loadedLootProfilePath, candidatePath, StringComparison.OrdinalIgnoreCase)
            && System.IO.File.GetLastWriteTime(candidatePath) == _loadedLootProfileTime)
        {
            profile = _loadedLootProfile;
            return true;
        }

        try
        {
            profile = VTankLootParser.Load(candidatePath);
            _loadedLootProfile = profile;
            _loadedLootProfilePath = candidatePath;
            _loadedLootProfileTime = System.IO.File.GetLastWriteTime(candidatePath);
            if (_dashboard?.Settings != null)
                _dashboard.Settings.CurrentLootPath = candidatePath;

            ChatLine($"[RynthAi] Loaded loot profile '{System.IO.Path.GetFileName(candidatePath)}' with {profile.Rules.Count} rule(s).");
            return true;
        }
        catch (Exception ex)
        {
            ChatLine($"[RynthAi] Failed to load loot profile: {ex.Message}");
            return false;
        }
    }

    private bool TryLoadNativeLootProfile(out RynthCore.Loot.LootProfile profile, out string loadedPath)
    {
        string candidatePath = (_dashboard?.Settings?.CurrentLootPath ?? string.Empty).Trim().Trim('"');
        loadedPath = candidatePath;
        profile = _nativeLootProfile ?? new RynthCore.Loot.LootProfile();

        if (string.IsNullOrWhiteSpace(candidatePath)
            || !candidatePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!System.IO.File.Exists(candidatePath))
        {
            ChatLine($"[RynthAi] Native loot profile not found: {candidatePath}");
            return false;
        }

        if (_nativeLootProfile != null
            && string.Equals(_nativeLootProfilePath, candidatePath, StringComparison.OrdinalIgnoreCase)
            && System.IO.File.GetLastWriteTime(candidatePath) == _nativeLootProfileTime)
        {
            profile = _nativeLootProfile;
            return true;
        }

        try
        {
            profile = RynthCore.Loot.LootProfile.Load(candidatePath);
            _nativeLootProfile = profile;
            _nativeLootProfilePath = candidatePath;
            _nativeLootProfileTime = System.IO.File.GetLastWriteTime(candidatePath);
            ChatLine($"[RynthAi] Loaded native loot profile '{System.IO.Path.GetFileName(candidatePath)}' with {profile.Rules.Count} rule(s).");
            return true;
        }
        catch (Exception ex)
        {
            ChatLine($"[RynthAi] Failed to load native loot profile: {ex.Message}");
            return false;
        }
    }

    private void HandleDumpInventoryCommand()
    {
        Host.Log("[RynthAi dumpinv] ENTRY");
        if (_objectCache == null) { ChatLine("[RynthAi] Cache not ready."); return; }
        uint playerId = Host.GetPlayerId();
        Host.Log($"[RynthAi dumpinv] playerId=0x{playerId:X8}");
        if (playerId == 0) { ChatLine("[RynthAi] Not logged in."); return; }

        // ── Part 1: Cached inventory — NO native ownership calls, just IDs and names ──
        Host.Log("[RynthAi dumpinv] Starting Part 1 - cache enum");
        ChatLine("[RynthAi] === Cached Inventory ===");
        int cacheCount = 0;
        try
        {
            foreach (var wo in _objectCache.GetInventory())
            {
                uint uid = unchecked((uint)wo.Id);
                string range = uid >= 0xC0000000u ? "Pack" : uid >= 0x80000000u ? "Dyn" : uid >= 0x50000000u ? "Plr" : "Stat";
                ChatLine($"[RynthAi]   0x{uid:X8} [{range}] [{wo.ObjectClass}] \"{wo.Name}\"");
                cacheCount++;
            }
        }
        catch (Exception ex)
        {
            ChatLine($"[RynthAi] Cache enumeration error: {ex.Message}");
        }
        ChatLine($"[RynthAi] Cache total: {cacheCount} item(s)");
        Host.Log($"[RynthAi dumpinv] Part 1 done, {cacheCount} items");

        // ── Part 2: Direct container scan via GetContainerContents ──
        Host.Log($"[RynthAi dumpinv] HasGetContainerContents={Host.HasGetContainerContents}");
        ChatLine($"[RynthAi] HasGetContainerContents={Host.HasGetContainerContents}");
        if (!Host.HasGetContainerContents)
        {
            ChatLine("[RynthAi] GetContainerContents API not available.");
            return;
        }

        Host.Log("[RynthAi dumpinv] Calling GetContainerContents for player...");
        try
        {
            uint[] topBuf = new uint[256];
            Host.Log("[RynthAi dumpinv] about to call GetContainerContents...");
            int topCount = Host.GetContainerContents(playerId, topBuf);
            Host.Log($"[RynthAi dumpinv] GetContainerContents returned {topCount}");
            Host.Log($"[RynthAi dumpinv] first few IDs: {(topCount > 0 ? $"0x{topBuf[0]:X8}" : "none")} {(topCount > 1 ? $"0x{topBuf[1]:X8}" : "")}");
            ChatLine($"[RynthAi] === Player container: {topCount} item(s) ===");

            var packIds = new System.Collections.Generic.List<uint>();
            for (int i = 0; i < topCount; i++)
            {
                uint itemId = topBuf[i];
                Host.TryGetObjectName(itemId, out string itemName);
                bool isPack = false;
                if (Host.TryGetItemType(itemId, out uint typeFlags))
                    isPack = (typeFlags & 0x200) != 0; // ItemType.Container
                string tag = isPack ? " [PACK]" : "";
                ChatLine($"[RynthAi]   0x{itemId:X8} \"{itemName}\"{tag}");
                if (isPack) packIds.Add(itemId);
            }

            // Scan inside each pack
            foreach (uint packId in packIds)
            {
                Host.TryGetObjectName(packId, out string packName);
                uint[] packBuf = new uint[256];
                int packCount = Host.GetContainerContents(packId, packBuf);
                ChatLine($"[RynthAi] === Pack 0x{packId:X8} \"{packName}\": {packCount} item(s) ===");
                for (int i = 0; i < packCount; i++)
                {
                    uint itemId = packBuf[i];
                    Host.TryGetObjectName(itemId, out string itemName);
                    ChatLine($"[RynthAi]     0x{itemId:X8} \"{itemName}\"");
                }
            }

            ChatLine("[RynthAi] Direct scan done.");
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi dumpinv] EXCEPTION: {ex}");
            ChatLine($"[RynthAi] Direct scan error: {ex.Message}");
        }
    }

    private static string ExtractCommandArgument(string fullCommand, string commandName)
    {
        string prefix = $"/ra {commandName}";
        if (!fullCommand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return fullCommand.Length > prefix.Length
            ? fullCommand[prefix.Length..].Trim()
            : string.Empty;
    }

    private void HandleSettingsCommand(string[] parts)
    {
        if (parts.Length < 4)
        {
            ChatLine("[RynthAi] Usage: /ra settings loadchar <name> | /ra settings savechar <name>");
            return;
        }

        string sub = parts[2].ToLower();
        if (sub != "loadchar" && sub != "savechar")
        {
            ChatLine("[RynthAi] Usage: /ra settings loadchar <name> | /ra settings savechar <name>");
            return;
        }

        string name = string.Join(" ", parts, 3, parts.Length - 3).Trim();
        if (string.IsNullOrEmpty(name))
        {
            ChatLine($"[RynthAi] Usage: /ra settings {sub} <name>");
            return;
        }

        if (_dashboard == null) { ChatLine("[RynthAi] Dashboard not ready."); return; }

        string result = sub == "loadchar"
            ? _dashboard.LoadProfile(name)
            : _dashboard.SaveAsProfile(name);
        ChatLine($"[RynthAi] {result}");
    }

    private void HandleClearBusyCommand()
    {
        int before = Host.HasGetBusyState ? Host.GetBusyState() : -1;
        if (Host.HasForceResetBusyCount)
            Host.ForceResetBusyCount();
        if (Host.HasStopCompletely)
            Host.StopCompletely();
        // Reset all tracked counts — engine, plugin, combat, buff
        _busyCount = 0;
        _busyCountLastIncrementAt = 0;
        _busyCountBecamePositiveAt = 0;
        if (_combatManager != null) _combatManager.BusyCount = 0;
        if (_buffManager != null)   _buffManager.BusyCount = 0;
        int after = Host.HasGetBusyState ? Host.GetBusyState() : -1;
        ChatLine($"[RynthAi] Busy state cleared (was {before} → {after})");
    }

    private void HandleBusyInfoCommand()
    {
        int engineBusy = Host.HasGetBusyState ? Host.GetBusyState() : -1;
        long now = CorpseNowMs;
        long lastIncrMs  = _busyCountLastIncrementAt  == 0 ? -1 : now - _busyCountLastIncrementAt;
        long elevatedMs  = _busyCountBecamePositiveAt == 0 ? -1 : now - _busyCountBecamePositiveAt;

        ChatLine($"[RynthAi] === Busy State ===");
        ChatLine($"  Engine busy count : {engineBusy}");
        ChatLine($"  Plugin busy count : {_busyCount}");
        ChatLine($"  Combat busy count : {(_combatManager?.BusyCount.ToString() ?? "n/a")}");
        ChatLine($"  Buff busy count   : {(_buffManager?.BusyCount.ToString() ?? "n/a")}");
        ChatLine($"  Last increment    : {(lastIncrMs  < 0 ? "never" : $"{lastIncrMs}ms ago")}");
        ChatLine($"  Elevated since    : {(elevatedMs  < 0 ? "not elevated" : $"{elevatedMs}ms ago")} (timeout at {BUSY_TIMEOUT_MS}ms)");

        ChatLine($"[RynthAi] === Corpse / Loot State ===");
        ChatLine($"  Target corpse     : 0x{(uint)_targetCorpseId:X8}");
        ChatLine($"  Opened container  : 0x{(uint)_openedContainerId:X8}");
        ChatLine($"  Current loot item : 0x{(uint)_currentLootItemId:X8}");
        ChatLine($"  Completed corpses : {_completedCorpses.Count}");

        ChatLine($"[RynthAi] === Give Queue ===");
        ChatLine($"  Pending gives     : {_pendingGives.Count}");
        ChatLine($"  Give interval     : {(_dashboard?.Settings.GiveQueueIntervalMs.ToString() ?? "n/a")}ms");

        string botAction = _dashboard?.Settings.BotAction ?? "n/a";
        ChatLine($"[RynthAi] === Bot ===");
        ChatLine($"  BotAction         : {botAction}");
        ChatLine($"  Macro running     : {(_dashboard?.Settings.IsMacroRunning.ToString() ?? "n/a")}");
    }

    private void HandleMapDumpCommand()
    {
        if (_objectCache == null) { ChatLine("[RynthAi] Cache not ready."); return; }
        if (!Host.HasGetObjectPosition) { ChatLine("[RynthAi] GetObjectPosition not available."); return; }
        if (!Host.TryGetPlayerPose(out uint playerCell, out _, out _, out _, out _, out _, out _, out _)) { ChatLine("[RynthAi] Can't get player cell."); return; }

        uint landblock = playerCell >> 16;
        ChatLine($"[RynthAi] === Map Dump (landblock 0x{landblock:X4}) ===");

        int total = 0, shown = 0;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            total++;
            if (!Host.TryGetObjectPosition((uint)wo.Id, out uint cellId, out _, out _, out _)) continue;
            if ((cellId >> 16) != landblock) continue;

            uint uid = (uint)wo.Id;
            string range = uid >= 0x80000000u ? "dyn" : "sta";
            Host.TryGetItemType(uid, out uint flags);
            string name = wo.Name.Length > 0 ? wo.Name : "(no name)";
            Host.Log($"[mapdump] 0x{uid:X8} [{range}] flags=0x{flags:X5} cls={wo.ObjectClass} name={name}");
            if (shown < 30)
            {
                ChatLine($"  0x{uid:X8} [{range}] fl=0x{flags:X5} {wo.ObjectClass} \"{name}\"");
                shown++;
            }
        }
        ChatLine($"[RynthAi] {total} landscape objects in cache ({shown} in landblock, rest in log)");
    }

    // ── use / select helpers ──────────────────────────────────────────────────

    private WorldObject? FindObject(
        string name, bool inv, bool land, bool partial)
    {
        if (_objectCache == null) return null;

        if (inv)
        {
            foreach (var wo in _objectCache.GetDirectInventory())
            {
                if (partial
                    ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                    return wo;
            }
        }

        if (land)
        {
            foreach (var wo in _objectCache.GetLandscapeObjects())
            {
                if (partial
                    ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                    return wo;
            }
        }

        return null;
    }

    private void HandleUseCommand(string[] parts, bool inv, bool land, bool partial)
    {
        if (parts.Length < 3)
        {
            ChatLine("[RynthAi] Usage: /ra use[i|l][p] <name> [on <name2>]");
            return;
        }

        string argStr = string.Join(" ", parts, 2, parts.Length - 2);

        // Check for "X on Y" syntax
        int onIdx = argStr.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onIdx >= 0)
        {
            string srcName = argStr.Substring(0, onIdx).Trim();
            string tgtName = argStr.Substring(onIdx + 4).Trim();
            var src = FindObject(srcName, inv, land, partial);
            var tgt = FindObject(tgtName, inv, land, partial);
            if (src == null) { ChatLine($"[RynthAi] Not found: '{srcName}'"); return; }
            if (tgt == null) { ChatLine($"[RynthAi] Not found: '{tgtName}'"); return; }
            Host.UseObjectOn((uint)src.Id, (uint)tgt.Id);
            ChatLine($"[RynthAi] UseObjectOn: {src.Name} (0x{src.Id:X}) → {tgt.Name} (0x{tgt.Id:X})");
            return;
        }

        var obj = FindObject(argStr.Trim(), inv, land, partial);
        if (obj == null) { ChatLine($"[RynthAi] Not found: '{argStr.Trim()}'"); return; }
        Host.UseObject((uint)obj.Id);
        ChatLine($"[RynthAi] UseObject: {obj.Name} (0x{obj.Id:X})");
    }

    private void HandleSelectCommand(string[] parts, bool inv, bool land, bool partial)
    {
        if (parts.Length < 3)
        {
            ChatLine("[RynthAi] Usage: /ra select[i|l][p] <name>");
            return;
        }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        var obj = FindObject(name, inv, land, partial);
        if (obj == null) { ChatLine($"[RynthAi] Not found: '{name}'"); return; }
        Host.SelectItem((uint)obj.Id);
        ChatLine($"[RynthAi] Selected: {obj.Name} (0x{obj.Id:X})");
    }

    // ── Give commands ─────────────────────────────────────────────────────────
    //
    //  /ra give [count] <itemName> to <playerName>         exact item (first match),  exact player
    //  /ra givea <itemName> to <playerName>               exact item (ALL matches),  exact player
    //  /ra givep [count] <itemName> to <playerName>       partial item (first),      exact player
    //  /ra giveap <itemName> to <playerName>              partial item (ALL),        exact player
    //  /ra givexp [count] <itemName> to <playerName>      exact item (first),        partial player
    //  /ra giveaxp <itemName> to <playerName>             exact item (ALL),          partial player
    //  /ra givepp [count] <itemName> to <playerName>      partial item (first),      partial player
    //  /ra giveapp <itemName> to <playerName>             partial item (ALL),        partial player
    //  /ra giver [count] <regex> to <playerName>          regex item (first),        exact player
    //  /ra givear <regex> to <playerName>                 regex item (ALL),          exact player
    //  /ra ig <profile> to <playerName>                   loot profile, exact player
    //  /ra igp <profile> to <playerName>                  loot profile, partial player
    //
    //  count is optional; omit to give all matching stacks.

    private enum GiveItemMatch { Exact, Partial, Regex }

    private void HandleGiveCommand(string[] parts, GiveItemMatch itemMatch, bool partialPlayer, bool allItems = false)
    {
        if (_objectCache == null) { ChatLine("[RynthAi] Object cache not ready."); return; }
        if (!Host.HasMoveItemExternal)  { ChatLine("[RynthAi] MoveItemExternal not available."); return; }

        string argStr = string.Join(" ", parts, 2, parts.Length - 2).Trim();

        // Split on last " to " so player names that contain "to" still work
        int toIdx = argStr.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIdx < 0)
        {
            ChatLine("[RynthAi] Usage: /ra give[a][p|P|pp|r] [count] <item> to <player>");
            return;
        }

        string itemPart   = argStr.Substring(0, toIdx).Trim();
        string playerPart = argStr.Substring(toIdx + 4).Trim();

        if (string.IsNullOrEmpty(itemPart) || string.IsNullOrEmpty(playerPart))
        {
            ChatLine("[RynthAi] Item name and player name must not be empty.");
            return;
        }

        // allItems: give every matching stack; otherwise honour optional leading count (default 1)
        int maxCount = allItems ? int.MaxValue : 1;
        string[] itemTokens = itemPart.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!allItems && itemTokens.Length >= 2 && int.TryParse(itemTokens[0], out int parsedCount) && parsedCount > 0)
        {
            maxCount = parsedCount;
            itemPart = itemTokens[1].Trim();
        }

        // Find target in landscape by name (player, NPC, container, etc.)
        WorldObject? target = null;
        foreach (var wo in _objectCache.GetLandscape())
        {
            bool match = partialPlayer
                ? wo.Name.IndexOf(playerPart, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(wo.Name, playerPart, StringComparison.OrdinalIgnoreCase);
            if (match) { target = wo; break; }
        }
        if (target == null) { ChatLine($"[RynthAi] Target not found: '{playerPart}'"); return; }

        // Compile regex if needed
        Regex? rx = null;
        if (itemMatch == GiveItemMatch.Regex)
        {
            try   { rx = new Regex(itemPart, RegexOptions.IgnoreCase); }
            catch { ChatLine($"[RynthAi] Invalid regex: {itemPart}"); return; }
        }

        // Collect matching inventory stacks
        var matches = new List<WorldObject>();
        foreach (var wo in _objectCache.GetDirectInventory(forceRefresh: true))
        {
            bool hit = itemMatch switch
            {
                GiveItemMatch.Exact   => string.Equals(wo.Name, itemPart, StringComparison.OrdinalIgnoreCase),
                GiveItemMatch.Partial => wo.Name.IndexOf(itemPart, StringComparison.OrdinalIgnoreCase) >= 0,
                GiveItemMatch.Regex   => rx!.IsMatch(wo.Name),
                _                     => false,
            };
            if (hit) matches.Add(wo);
            if (matches.Count >= maxCount) break;
        }

        if (matches.Count == 0) { ChatLine($"[RynthAi] No items found matching '{itemPart}'"); return; }

        if (allItems)
        {
            foreach (var item in matches)
            {
                int stackSize = Math.Max(1, item.Values(LongValueKey.StackCount, 1));
                EnqueueGive((uint)item.Id, (uint)target.Id, stackSize);
            }
            ChatLine($"[RynthAi] Queued {matches.Count} stack(s) matching '{itemPart}' → {target.Name}");
        }
        else
        {
            var item = matches[0];
            int stackSize = Math.Max(1, item.Values(LongValueKey.StackCount, 1));
            Host.MoveItemExternal((uint)item.Id, (uint)target.Id, stackSize);
            ChatLine($"[RynthAi] Giving '{item.Name}' to {target.Name}");
        }
    }

    private void HandleGiveProfileCommand(string[] parts, bool partialPlayer)
    {
        if (_objectCache == null) { ChatLine("[RynthAi] Object cache not ready."); return; }
        if (!Host.HasMoveItemExternal)  { ChatLine("[RynthAi] MoveItemExternal not available."); return; }

        string argStr = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        int toIdx = argStr.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIdx < 0) { ChatLine("[RynthAi] Usage: /ra ig[p] <lootProfile> to <player>"); return; }

        string profileArg = argStr.Substring(0, toIdx).Trim();
        string playerPart = argStr.Substring(toIdx + 4).Trim();

        // Resolve profile path — bare name resolved from ItemGiver dir, .utl extension added if needed
        const string itemGiverDir = @"C:\Games\RynthSuite\RynthAi\ItemGiver";
        string profilePath = System.IO.Path.IsPathRooted(profileArg)
            ? profileArg
            : System.IO.Path.Combine(itemGiverDir, profileArg);

        bool isJson = profilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (!isJson && !profilePath.EndsWith(".utl", StringComparison.OrdinalIgnoreCase))
            profilePath += ".utl";

        if (!System.IO.File.Exists(profilePath)) { ChatLine($"[RynthAi] Profile not found: {profilePath}"); return; }

        // Find target in landscape by name (player, NPC, container, etc.)
        WorldObject? target = null;
        foreach (var wo in _objectCache.GetLandscape())
        {
            bool match = partialPlayer
                ? wo.Name.IndexOf(playerPart, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(wo.Name, playerPart, StringComparison.OrdinalIgnoreCase);
            if (match) { target = wo; break; }
        }
        if (target == null) { ChatLine($"[RynthAi] Target not found: '{playerPart}'"); return; }

        // Load profile and give all matching items
        int given = 0;
        if (isJson)
        {
            var nativeProfile = RynthCore.Loot.LootProfile.Load(profilePath);
            foreach (var item in _objectCache.GetDirectInventory(forceRefresh: true))
            {
                var (action, _) = Loot.LootEvaluator.Classify(nativeProfile, item, null);
                if (action != RynthCore.Loot.LootAction.Keep) continue;
                Host.MoveItemExternal((uint)item.Id, (uint)target.Id, Math.Max(1, item.Values(LongValueKey.StackCount, 1)));
                given++;
            }
        }
        else
        {
            var vtProfile = VTankLootParser.Load(profilePath);
            var lootCtx   = new VTankLootContext(Host, Host.GetPlayerId()) { Cache = _objectCache };
            foreach (var item in _objectCache.GetDirectInventory(forceRefresh: true))
            {
                VTankLootRule? matched = null;
                foreach (var rule in vtProfile.Rules)
                    if (rule.IsMatch(item, lootCtx)) { matched = rule; break; }
                if (matched == null || (matched.Action != VTankLootAction.Keep && matched.Action != VTankLootAction.KeepUpTo)) continue;
                Host.MoveItemExternal((uint)item.Id, (uint)target.Id, Math.Max(1, item.Values(LongValueKey.StackCount, 1)));
                given++;
            }
        }

        ChatLine(given > 0
            ? $"[RynthAi] Giving {given} stack(s) from profile '{System.IO.Path.GetFileName(profilePath)}' to {target.Name}"
            : $"[RynthAi] No items in inventory matched profile '{System.IO.Path.GetFileName(profilePath)}'");
    }
}
