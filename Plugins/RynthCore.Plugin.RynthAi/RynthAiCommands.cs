using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// All /ra chat commands — partial class split from RynthAiPlugin.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private CommandReply? _reply;

    /// <summary>What chat commands say back: typed ones always answer, a meta's are quiet when they work (CommandReply.cs).</summary>
    internal CommandReply Reply => _reply ??= new CommandReply(
        (text, color) => Host.WriteToChat(text, color),
        line => Host.Log(line),
        () => _metaCommandDepth > 0 || _metaManager?.IsExecutingAction == true);

    /// <summary>A chat line (lists, status, help, and lines that already answered a command): shown either way.</summary>
    private void ChatLine(string text) => Reply.Info(text);

    /// <summary>A command worked: what changed and the value now (quiet, logged, when a meta sent it).</summary>
    private void ReplyOk(string text) => Reply.Ok(text);

    /// <summary>A command didn't work: why, and the usage (a meta's repeats once a minute).</summary>
    private void ReplyFail(string text) => Reply.Fail(text);

    /// <summary>An action that reports whether it went out: <paramref name="okText"/> or <paramref name="failText"/>.</summary>
    private void ReplyIf(bool ok, string okText, string failText)
    {
        if (ok) ReplyOk(okText);
        else ReplyFail(failText);
    }

    /// <summary>
    /// A line from code that returns one string for either outcome (the dashboard's nav, meta and
    /// settings-profile loads and saves, the Items list fill and clean): a failure by its wording,
    /// else it worked.
    /// </summary>
    private void ReplyResult(string text)
    {
        if (ResultFailed(text)) ReplyFail(text);
        else ReplyOk(text);
    }

    private static readonly Regex ResultFailedRx = new(
        @"^(\[RynthAi\] )?(Can't|Couldn't|No |Not logged in|Nav NOT saved|Nav save failed|Your inventory|Settings not ready|Save failed|Dashboard not ready)"
        + @"|' can't be used| not loaded: | already exists\.",
        RegexOptions.CultureInvariant);

    /// <summary>True when a one-string result (see <see cref="ReplyResult"/>) says it didn't work.</summary>
    internal static bool ResultFailed(string text) => ResultFailedRx.IsMatch(text);

    /// <summary>/ra items clean and the engine Items panel's Remove Missing button (RynthPluginCleanItems).</summary>
    internal void CleanMissingItems()
        => ReplyResult(_dashboard?.CleanMissingItems(_objectCache, _playerId) ?? "[RynthAi] Settings not ready.");

    private void HandleHelpCommand()
    {
        ChatLine("[RynthAi] === Commands ===");
        ChatLine("[RynthAi] /ra fellow       — fellowship diagnostics and queries");
        ChatLine("[RynthAi] /ra help          — show this list");
        ChatLine("[RynthAi] /ra version       — RynthAi version and build date, and the engine it runs on");
        ChatLine("[RynthAi] /ra server        - which server RynthCore says this is, its overrides, and the server plugin's settings");
        ChatLine("[RynthAi] /ra power <0-100|auto> — set attack power (auto = recklessness-aware)");
        ChatLine("[RynthAi] /ra cast <spellId> — cast spell on current target");
        ChatLine("[RynthAi] /ra buffs         — show active buff timers");
        ChatLine("[RynthAi] /ra scan          — show nearby monsters");
        ChatLine("[RynthAi] /ra cache         — show object cache summary");
        ChatLine("[RynthAi] /ra cache2        — show raw object cache IDs");
        ChatLine("[RynthAi] /ra attackable    — check if target is attackable");
        ChatLine("[RynthAi] /ra wielded       — show wielded items");
        ChatLine("[RynthAi] /ra dumpprops [0xId] — every property of the selection (else yourself)");
        ChatLine("[RynthAi] /ra iteminfo [name] — the selected (or named) item's details, Mag-Tools style");
        ChatLine("[RynthAi] /ra mexec <expr>  — evaluate meta expression");
        ChatLine("[RynthAi] /ra listvars      — show session variables");
        ChatLine("[RynthAi] /ra listpvars     — show persistent variables");
        ChatLine("[RynthAi] /ra listgvars     — show global variables");
        ChatLine("[RynthAi] /ra give [count] <item> to <player>         — give exact-named item(s) to player");
        ChatLine("[RynthAi] /ra givep [count] <item> to <player>        — partial item name match");
        ChatLine("[RynthAi] /ra givexp [count] <item> to <player>       — partial player name match");
        ChatLine("[RynthAi] /ra givepp [count] <item> to <player>       — partial item and player");
        ChatLine("[RynthAi] /ra giver [count] <regex> to <player>       — regex item name match");
        ChatLine("[RynthAi] /ra givea[p|xp|pp|r] <item> to <player>     — give ALL matching stacks ('… stop' cancels the queue)");
        ChatLine("[RynthAi] /ra gap <item> to <player>                  — alias for giveapp (give all, partial item + partial player)");
        ChatLine("[RynthAi] /ra ig <profile> to <player>                — give items matching loot profile");
        ChatLine("[RynthAi] /ra igp <profile> to <player>               — ig with partial player name");
        ChatLine("[RynthAi] /ra use[i|l][p|pi|lp] <name> [on <name2>]  — use item (i=inv, l=land, p=partial)");
        ChatLine("[RynthAi] /ra raycast       — raycast system status");
        ChatLine("[RynthAi] /ra lostest [mode] — LoS test (mode: linear|bow|crossbow|atlatl|magic)");
        ChatLine("[RynthAi] /ra landtest      — terrain heightmap info for player's landblock");
        ChatLine("[RynthAi] /ra rayland       — landscape raycast test (down + 4 cardinals)");
        ChatLine("[RynthAi] /ra buildinfo     — nearby geometry info");
        ChatLine("[RynthAi] /ra navdebug      — show nav coordinate/debug info");
        ChatLine("[RynthAi] /ra addnavpt      — append a waypoint at current location to loaded nav");
        ChatLine("[RynthAi] /ra nav save [name] — save the route; a name saves it as <name>.nav");
        ChatLine("[RynthAi] /ra nav load <name> — load a nav from NavProfiles (none by that name: a new empty one)");
        ChatLine("[RynthAi] /ra nav list        — list the navs in NavProfiles");
        ChatLine("[RynthAi] /ra nav restart     — run the loaded route again from its first point (a stop/start keeps its place; a finished Once route stays done)");
        ChatLine("[RynthAi] /ra meta load <name> — load a meta from MetaFiles (none by that name: a new blank one)");
        ChatLine("[RynthAi] /ra meta new <name>  — create a blank meta and load it");
        ChatLine("[RynthAi] /ra meta list        — list the metas in MetaFiles");
        ChatLine("[RynthAi] /ra lua run <name>  — run a RynthLua script (same as /lua run; also stop, list, exec <code>)");
        ChatLine("[RynthAi] /ra items fill      — add weapons, healing kits, lockpicks and pet essences from your pack");
        ChatLine("[RynthAi] /ra offhand [auto|shield|weapon|none] [rule] — the off hand (shield / dual wield), global or for a Monsters rule; dual on|off");
        ChatLine("[RynthAi] /ra dunnav <NS> <EW>  — navigate to NS/EW coords through dungeon (no nav file needed)");
        ChatLine("[RynthAi] /ra dunnav-patrol      — circular hunt patrol through the whole dungeon (no nav file needed)");
        ChatLine("[RynthAi] /ra hazard add|del|list|near — mark current cell as lava/acid so patrol avoids it");
        ChatLine("[RynthAi] /ra jump[swzxc] [heading] [holdtime] — jump with optional face/direction (s=run w=fwd x=back z=strafeL c=strafeR)");
        ChatLine("[RynthAi] /ra corpseinfo    — show corpse range/open diagnostics");
        ChatLine("[RynthAi] /ra pace          — what the server is still answering (one action at a time)");
        ChatLine("[RynthAi] /ra owncorpse [go|stop] — where your corpse is; travel back to it, or stop");
        ChatLine("[RynthAi] /ra corpsecheck   — explain whether a corpse would be looted");
        ChatLine("[RynthAi] /ra corpseopen    — force the nearest corpse open flow");
        ChatLine("[RynthAi] /ra fellowinfo    — show fellowship tracker state");
        ChatLine("[RynthAi] /ra lootparse     — inspect the selected loot profile");
        ChatLine("[RynthAi] /ra lootcheckinv  — test the loot profile against inventory");
        ChatLine("[RynthAi] /ra lootcheck     — classify selected item (on|off = auto on click)");
        ChatLine("[RynthAi] /ra loot why      — which loot rule takes the selected item, and why the rules above it didn't");
        ChatLine("[RynthAi] /ra loot mode [profile|rares] — loot by the loot profile, or rares and needed mana stones only");
        ChatLine("[RynthAi] /ra loot add [keep|keep N|salvage|sell|read] [name|like] [preview] — add a loot rule for the selected item");
        ChatLine("[RynthAi] /ra dumpinv       — dump all inventory items (cache + direct)");
        ChatLine("[RynthAi] /ra combat        — dump combat state machine snapshot");
        ChatLine("[RynthAi] /ra mstats [monster] — its evades, resists, hits, spells and kills this session (list | reset | show | hide)");
        ChatLine("[RynthAi] /ra why           — one-glance diagnosis of why the bot is idle/attacking");
        ChatLine("[RynthAi] /ra start | stop   — start/stop the macro from chat");
        ChatLine("[RynthAi] /ra pause [sec]    — stop the macro, auto-resume after sec (default 60)");
        ChatLine("[RynthAi] /ra vtankyield [on|off] — with Decal: stop when VTank starts (one bot per client)");
        ChatLine("[RynthAi] /ra metamgr on|off|status — Meta Manager: load a meta when a timer or a /myquests quest timer runs out (Meta panel, Schedule)");
        ChatLine("[RynthAi] /ra metamgr poll [min] — check /myquests now, or set how often it checks (default 5 min)");
        ChatLine("[RynthAi] /ra metamgr start <#> [min] | stop <#> | reset <#|all> — run a countdown rule, stop it, re-arm a rule");
        ChatLine("[RynthAi] /ra navstate      — dump navigation state machine snapshot");
        ChatLine("[RynthAi] /ra salvstate     — dump salvage state machine snapshot");
        ChatLine("[RynthAi] /ra clearbusy     — force-clear busy state (hourglass cursor)");
        ChatLine("[RynthAi] /ra panic         — full AC state reset (cancel attack + peace mode + stop motion + clear busy)");
        ChatLine("[RynthAi] /ra forcebuff          — force-recast all buffs immediately");
        ChatLine("[RynthAi] /ra cancelforcebuff    — cancel an in-progress force rebuff");
        ChatLine("[RynthAi] /ra vtkeys             — VTank-style commands (targetlock, recharge, lootthis, meleelow/mid/high, navprio...)");
        ChatLine("[RynthAi] /ra buff force|cancel|skipped - force buff, cancel it, or list buffs left out as unusable");
        ChatLine("[RynthAi] /ra bufftest           — one-shot: snapshot enchant registries pre/post next item-spell cast (logs to RynthCore.log)");
        ChatLine("[RynthAi] /ra settings savechar <name> — save current settings to named profile (create if new)");
        ChatLine("[RynthAi] /ra settings loadchar <name> — load named settings profile (create from current if new)");
        ChatLine("[RynthAi] /ub autovendor [profile.utl|cancel] — run AutoVendor at the open vendor (TestMode on by default)");
        ChatLine("[RynthAi] /ub vendor open[p] <name|id|selected> | opencancel | addbuy[p]/addsell[p] [n] <item> | buyall | sellall | clearbuy | clearsell");
    }

    /// <summary>
    /// /ra offhand: the off hand (the Shield slot). Without arguments it shows the settings;
    /// "/ra offhand auto|shield|weapon|none" sets the global one; with a Monsters rule's name
    /// after it, that rule's ("default" hands the rule back to the global one);
    /// "/ra offhand dual on|off" makes Auto prefer dual wield over a shield.
    /// </summary>
    private void HandleOffhandCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Settings not ready."); return; }

        if (parts.Length < 3)
        {
            var global = OffhandRules.Parse(settings.OffhandDefault, OffhandMode.Auto);
            ChatLine($"[RynthAi] Offhand: {OffhandRules.Label(global)}; Auto prefers dual wield: {(settings.PreferDualWield ? "on" : "off")}");
            foreach (var r in settings.MonsterRules)
            {
                if (r.OffhandId == OffhandRules.RuleDefault) continue;
                string what = OffhandRules.RuleMode(r.OffhandId) is OffhandMode m
                    ? OffhandRules.Label(m)
                    : $"item 0x{(uint)r.OffhandId:X8} '{_objectCache?[r.OffhandId]?.Name ?? "?"}'";
                ChatLine($"[RynthAi]   rule '{r.Name}': {what}");
            }
            int shields = settings.ItemRules.Count(WeaponList.IsShieldRule);
            ChatLine($"[RynthAi] {shields} shield(s) in the Items list (Add Selected Weapon adds a shield too).");
            ReplyFail("[RynthAi] Usage: /ra offhand auto|shield|weapon|none [rule name] — /ra offhand default <rule name> — /ra offhand dual on|off");
            return;
        }

        string arg = parts[2];
        if (arg.Equals("dual", StringComparison.OrdinalIgnoreCase))
        {
            bool on = parts.Length < 4 || parts[3].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[3] == "1";
            settings.PreferDualWield = on;
            _dashboard!.SaveSettings();
            ReplyOk($"[RynthAi] Auto now prefers {(on ? "dual wield (with the Dual Wield skill and a second listed one-handed weapon)" : "a shield")}.");
            return;
        }

        bool toDefault = arg.Equals("default", StringComparison.OrdinalIgnoreCase);
        if (!toDefault && !OffhandRules.TryParse(arg, out _))
        {
            ReplyFail("[RynthAi] Usage: /ra offhand auto|shield|weapon|none [rule name] — /ra offhand default <rule name> — /ra offhand dual on|off");
            return;
        }
        var mode = toDefault ? OffhandMode.Auto : OffhandRules.Parse(arg, OffhandMode.Auto);

        if (parts.Length == 3)
        {
            if (toDefault) { ReplyFail("[RynthAi] Usage: /ra offhand default <rule name>"); return; }
            settings.OffhandDefault = OffhandRules.SettingValue(mode);
            _dashboard!.SaveSettings();
            ReplyOk($"[RynthAi] Offhand set to {OffhandRules.Label(mode)} (for every Monsters rule that doesn't choose its own).");
            return;
        }

        string ruleName = string.Join(" ", parts.Skip(3));
        var rule = settings.MonsterRules.FirstOrDefault(r => r.Name.Equals(ruleName, StringComparison.OrdinalIgnoreCase));
        if (rule == null) { ReplyFail($"[RynthAi] No Monsters rule named '{ruleName}'."); return; }
        rule.OffhandId = toDefault ? OffhandRules.RuleDefault : OffhandRules.RuleValue(mode);
        _dashboard!.SaveMonsterRules();
        ReplyOk(toDefault
            ? $"[RynthAi] Rule '{rule.Name}' uses the global Offhand setting again."
            : $"[RynthAi] Rule '{rule.Name}': Offhand {OffhandRules.Label(mode)}.");
    }

    private void HandlePowerCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Settings not ready."); return; }

        if (parts.Length < 3)
        {
            string current = settings.MeleeAttackPower < 0 ? "auto" : $"{settings.MeleeAttackPower}%";
            string currentMissile = settings.MissileAttackPower < 0 ? "auto" : $"{settings.MissileAttackPower}%";
            ChatLine($"[RynthAi] Melee power: {current}, Missile power: {currentMissile}");
            ReplyFail("[RynthAi] Usage: /ra power <0-100|auto>");
            return;
        }

        string val = parts[2].ToLower();
        if (val == "auto")
        {
            settings.MeleeAttackPower = -1;
            settings.MissileAttackPower = -1;
            ReplyOk("[RynthAi] Attack power set to auto.");
        }
        else if (int.TryParse(val, out int pct) && pct >= 0 && pct <= 100)
        {
            settings.MeleeAttackPower = pct;
            settings.MissileAttackPower = pct;
            ReplyOk($"[RynthAi] Attack power set to {pct}%.");
        }
        else
        {
            ReplyFail("[RynthAi] Usage: /ra power <0-100|auto>");
        }
    }

    private void HandleAttackableCommand()
    {
        uint targetId = _currentTargetId;
        if (targetId == 0)
        {
            ReplyFail("[RynthAi] No target selected — click something first.");
            return;
        }

        Host.TryGetObjectName(targetId, out string name);
        ChatLine($"[RynthAi] Target: {name} (0x{targetId:X8})");
        ChatLine($"[RynthAi] HasObjectIsAttackable: {Host.HasObjectIsAttackable}");

        if (!Host.HasObjectIsAttackable)
        {
            ReplyFail("[RynthAi] API not available — engine too old.");
            return;
        }

        bool attackable = Host.ObjectIsAttackable(targetId);
        ChatLine($"[RynthAi] ObjectIsAttackable => {attackable}");
    }

    private void HandleCastCommand(string[] parts)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2], out int spellId))
        {
            ReplyFail("[RynthAi] Usage: /ra cast <spellId>");
            return;
        }

        if (_currentTargetId == 0)
        {
            ReplyFail("[RynthAi] No target selected.");
            return;
        }

        if (!Host.HasCastSpell)
        {
            ReplyFail("[RynthAi] CastSpell not available — hook not found.");
            return;
        }

        bool ok = Host.CastSpellFor(_currentTargetId, spellId, "Command", $"spell {spellId} (/ra cast)", asked: true);
        string line = $"[RynthAi] CastSpell(target=0x{_currentTargetId:X8}, spell={spellId}) => {ok}";
        if (ok) ReplyOk(line); else ReplyFail(line);
    }

    private void HandleCacheCommand()
    {
        if (_objectCache == null)
        {
            ReplyFail("[RynthAi] Object cache not initialized (not logged in yet).");
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
        if (_objectCache == null) { ReplyFail("[RynthAi] Cache not ready."); return; }
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
            ReplyFail("[RynthAi] Raycast system not initialized.");
            return;
        }

        ChatLine($"[RynthAi] Raycast: {(_raycast.IsInitialized ? "READY" : "NOT LOADED")}");
        ChatLine($"[RynthAi]   Status: {_raycast.StatusMessage}");
        // What combat itself uses: READY above said nothing about it, and for a day combat had
        // no raycast at all while this line (and /ra lostest) worked (see WireRaycastIntoCombat).
        if (_combatManager == null)
            ChatLine("[RynthAi]   Combat: no combat manager");
        else
            ChatLine($"[RynthAi]   Combat LOS: {(_combatManager.RaycastInitialized ? "wired" : "NOT WIRED")}, " +
                     $"{(_dashboard?.Settings.EnableRaycasting == true ? "on" : "off")}, " +
                     $"checks {_combatManager.RaycastCheckCount}, blocked {_combatManager.RaycastBlockCount}");
    }

    private void HandleLosTestCommand(string[] parts)
    {
        if (_raycast == null || !_raycast.IsInitialized)
        {
            ReplyFail("[RynthAi LOS] Raycast system not initialized.");
            return;
        }

        if (_currentTargetId == 0)
        {
            ReplyFail("[RynthAi LOS] No target selected — select a monster first.");
            return;
        }

        // Mode parsing: /ra lostest [linear|bow|crossbow|atlatl|magic].
        // parts[0]="ra", parts[1]="lostest", parts[2]=optional mode. No arg = linear.
        string modeArg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "linear";
        var settings = _dashboard?.Settings;
        bool isArc = false;
        float arcVelocity = 0f;
        string modeLabel = "Linear";
        switch (modeArg)
        {
            case "linear":   isArc = false; modeLabel = "Linear"; break;
            case "bow":      isArc = true;  arcVelocity = settings?.BowArcVelocity      ?? 25.0f; modeLabel = $"Bow arc (v={arcVelocity:F1})"; break;
            case "crossbow": isArc = true;  arcVelocity = settings?.CrossbowArcVelocity ?? 40.0f; modeLabel = $"Crossbow arc (v={arcVelocity:F1})"; break;
            case "atlatl":   isArc = true;  arcVelocity = settings?.AtlatlArcVelocity   ?? 22.0f; modeLabel = $"Atlatl arc (v={arcVelocity:F1})"; break;
            case "magic":    isArc = true;  arcVelocity = settings?.MagicArcVelocity    ?? 40.0f; modeLabel = $"Magic arc (horizontal v={arcVelocity:F1}, from head height)"; break;
            default:
                ReplyFail($"[RynthAi LOS] Unknown mode '{modeArg}'. Use: linear | bow | crossbow | atlatl | magic");
                return;
        }

        Host.TryGetObjectName(_currentTargetId, out string targetName);
        ChatLine($"[RynthAi LOS] Target: {targetName} (0x{_currentTargetId:X8})  Mode: {modeLabel}");

        // Player position
        if (!Host.TryGetPlayerPose(out uint pCell, out float px, out float py, out float pz,
                out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi LOS] Player position unavailable.");
            return;
        }

        uint pBlockX = (pCell >> 24) & 0xFF;
        uint pBlockY = (pCell >> 16) & 0xFF;
        float playerGX = pBlockX * 192.0f + px;
        float playerGY = pBlockY * 192.0f + py;
        bool isDungeon = (pCell & 0xFFFF) >= 0x0100;
        ChatLine($"[RynthAi LOS] Player Landcell=0x{pCell:X8} Block=({pBlockX},{pBlockY})  {(isDungeon ? "[DUNGEON]" : "[OUTDOOR]")}");
        ChatLine($"[RynthAi LOS] Player Local=({px:F1},{py:F1},{pz:F1})");
        ChatLine($"[RynthAi LOS] Player Global=({playerGX:F1},{playerGY:F1},{pz:F1})");

        // Target position
        if (!Host.TryGetObjectPosition(_currentTargetId, out uint tCell, out float tx, out float ty, out float tz))
        {
            ReplyFail("[RynthAi LOS] Target position unavailable.");
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

        // Eye-to-eye endpoints.
        var origin = new Raycasting.Vector3(playerGX, playerGY, pz + 1.0f);
        var targetPos = new Raycasting.Vector3(targetGX, targetGY, tz + 1.0f);
        var rayDir = targetPos - origin;
        float rayLen = rayDir.Length();
        if (rayLen > 0.001f) rayDir = rayDir / rayLen;

        var geo = _raycast.GeometryLoader;
        var geometry = geo.GetLandblockGeometry(pCell);
        ChatLine($"[RynthAi LOS] Geometry: {geometry?.Count ?? 0} volumes loaded");

        bool terrainBlocked = false;
        float terrainHitDist = 0f;
        bool staticBlocked = false;

        if (isArc)
        {
            // ── Arc mode: sample the parabola; test terrain per sample + static as a whole. ──
            if (isDungeon)
            {
                // Outdoor heightmap is meaningless once you're inside — the dungeon
                // cell is self-contained geometry. The arc test below will surface
                // ceiling/floor hits via static.
                ChatLine("[RynthAi LOS] Terrain (arc): n/a (dungeon — no landscape)");
            }
            else
            {
                terrainBlocked = TerrainBlockedAlongArc(origin, targetPos, arcVelocity, geo, out terrainHitDist, out var arcHit);
                if (terrainBlocked)
                    ChatLine($"[RynthAi LOS] Terrain (arc): BLOCKED at {terrainHitDist:F1}m  arc=({arcHit.X:F0},{arcHit.Y:F0},{arcHit.Z:F1})");
                else
                    ChatLine("[RynthAi LOS] Terrain (arc): clear");
            }

            if (geometry != null && geometry.Count > 0)
            {
                // Same test combat runs for missile targets: straight line (walls), then the
                // arc the missile flies plus the clearance headroom (ceilings indoors).
                float clearance = settings?.MissileArcClearance ?? 0.5f;
                bool lineBlocked = RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);
                // Arc spells: ACE's fixed horizontal speed from the caster's head (what combat's
                // Arc-when-clear tests before each arc); missiles: the weapon's launch speed.
                bool arcBlocked;
                RaycastEngine.ArcLosResult arc;
                if (modeArg == "magic")
                {
                    var magicPath = TargetingFSM.MagicArcPath(origin, targetPos, arcVelocity, out var launch);
                    arcBlocked = RaycastEngine.IsBallisticArcBlocked(launch, targetPos, in magicPath, clearance, geometry, out arc);
                }
                else
                    arcBlocked = RaycastEngine.IsBallisticArcBlocked(origin, targetPos, arcVelocity, clearance, geometry, out arc);
                staticBlocked = lineBlocked || arcBlocked;
                ChatLine($"[RynthAi LOS] Straight line ({(isDungeon ? "multi-ray" : "single-ray")}): {(lineBlocked ? "BLOCKED" : "clear")}");
                ChatLine($"[RynthAi LOS] Arc: rises {arc.Sag:F2}m above the line, apex {arc.Apex:F2}m above launch, clearance {clearance:F1}m");
                if (arc.OutOfReach)
                    ChatLine($"[RynthAi LOS] Arc: OUT OF REACH at v={arcVelocity:F1} (raise the velocity or get closer)");
                else if (arcBlocked)
                    ChatLine($"[RynthAi LOS] Arc: BLOCKED {arc.HitAlong:F1}m out, {arc.HitZ:+0.0;-0.0;0.0}m vs launch{(isDungeon && arc.HitZ > 0.5f ? "  (ceiling)" : "")}");
                else
                    ChatLine("[RynthAi LOS] Arc: clear");
            }
        }
        else
        {
            // ── Linear mode: straight-line terrain + straight-line static. ──
            if (isDungeon)
            {
                ChatLine("[RynthAi LOS] Terrain: n/a (dungeon — no landscape)");
            }
            else if (geo.RaycastLandscape(origin, rayDir, rayLen,
                    out float tDist, out var tHit, out var tNormal))
            {
                if (tDist > 0.5f && tDist < rayLen - 1.0f)
                {
                    terrainBlocked = true;
                    terrainHitDist = tDist;
                    ChatLine($"[RynthAi LOS] Terrain: BLOCKED at {tDist:F1}m  ground=({tHit.X:F0},{tHit.Y:F0},{tHit.Z:F1})  normalZ={tNormal.Z:F3}");
                }
                else
                {
                    ChatLine($"[RynthAi LOS] Terrain: clear (intersection at {tDist:F1}m is outside segment)");
                }
            }
            else
            {
                ChatLine("[RynthAi LOS] Terrain: clear (no intersection)");
            }

            if (geometry != null && geometry.Count > 0)
            {
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
                // Dungeon LoS uses multi-ray checks to catch thin corner geometry —
                // mirrors TargetingFSM.IsTargetBlocked's behavior.
                staticBlocked = RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);
                ChatLine($"[RynthAi LOS] HasNearbyGeometry: {nearby}");
                ChatLine($"[RynthAi LOS] IsLinearPathBlocked ({(isDungeon ? "multi-ray" : "single-ray")}): {staticBlocked}");
                ChatLine($"[RynthAi LOS] Summary: {geometry.Count} total volumes, {hitCount} ray hits");
            }
        }

        string verdict;
        if (terrainBlocked && staticBlocked)
            verdict = $"BLOCKED (terrain @ {terrainHitDist:F1}m + static)";
        else if (terrainBlocked)
            verdict = $"BLOCKED by terrain @ {terrainHitDist:F1}m";
        else if (staticBlocked)
            verdict = "BLOCKED by static geometry";
        else
            verdict = "CLEAR";
        ChatLine($"[RynthAi LOS] Combined LoS ({modeLabel}): {verdict}");
    }

    // Samples the missile's arc from origin→target at the given launch speed and returns
    // true if any sample point dips below terrain Z. Same flight path as combat's missile
    // LOS (MissileBallistics: low ballistic solution, AC gravity), without the clearance.
    private static bool TerrainBlockedAlongArc(Raycasting.Vector3 origin, Raycasting.Vector3 target,
        float velocity, Raycasting.GeometryLoader geo,
        out float hitDist, out Raycasting.Vector3 hitPoint)
    {
        hitDist = 0f;
        hitPoint = origin;
        const int SAMPLES = 20;

        var arc = Raycasting.MissileBallistics.Solve(origin.X, origin.Y, origin.Z, target.X, target.Y, target.Z, velocity);
        if (!arc.Valid || arc.Vertical) return false; // out of reach is the shooter's problem, not terrain's

        for (int i = 1; i <= SAMPLES; i++)
        {
            float t = (float)i / SAMPLES;
            arc.PointAt(t, 0f, out float wx, out float wy, out float z);
            float groundZ = geo.GetTerrainZWorld(wx, wy);
            if (float.IsNaN(groundZ)) continue;

            if (z < groundZ)
            {
                hitDist  = t * arc.HorizDist;
                hitPoint = new Raycasting.Vector3(wx, wy, z);
                return true;
            }
        }
        return false;
    }

    private void HandleLandTestCommand()
    {
        if (_raycast == null || !_raycast.IsInitialized)
        {
            ReplyFail("[RynthAi LAND] Raycast system not initialized.");
            return;
        }

        if (!Host.TryGetPlayerPose(out uint pCell, out float px, out float py, out float pz,
                out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi LAND] Player position unavailable.");
            return;
        }

        // Landcell 0xXXYYCCCC → landblock key 0xXXYY (the high word).
        uint landblockKey = pCell >> 16;
        uint pBlockX = (pCell >> 24) & 0xFF;
        uint pBlockY = (pCell >> 16) & 0xFF;
        float playerGX = pBlockX * 192.0f + px;
        float playerGY = pBlockY * 192.0f + py;

        ChatLine($"[RynthAi LAND] Landcell=0x{pCell:X8}  LandblockKey=0x{landblockKey:X4}");
        ChatLine($"[RynthAi LAND] Player local=({px:F1},{py:F1},{pz:F1})  global=({playerGX:F1},{playerGY:F1})");

        var lb = _raycast.GeometryLoader.GetLandblockData(landblockKey);
        if (lb == null)
        {
            ReplyFail("[RynthAi LAND] Landblock data unavailable (dungeon, or height table not loaded).");
            return;
        }

        lb.GetHeightRange(out float minZ, out float maxZ);
        ChatLine($"[RynthAi LAND] Origin=({lb.WorldOriginX:F1},{lb.WorldOriginY:F1})  HasObjects={lb.HasObjects}");
        ChatLine($"[RynthAi LAND] Height range: min={minZ:F2}  max={maxZ:F2}  span={(maxZ - minZ):F2}m");

        // Four corners of the 9×9 vertex grid.
        float zSW = lb.GetVertexZ(0, 0);
        float zSE = lb.GetVertexZ(8, 0);
        float zNW = lb.GetVertexZ(0, 8);
        float zNE = lb.GetVertexZ(8, 8);
        ChatLine($"[RynthAi LAND] Corners Z: SW={zSW:F2} SE={zSE:F2} NW={zNW:F2} NE={zNE:F2}");

        // Nearest vertex under the player's world XY.
        float nearZ = lb.GetNearestVertexHeightWorld(playerGX, playerGY);
        if (float.IsNaN(nearZ))
        {
            ChatLine("[RynthAi LAND] Nearest-vertex Z: out of landblock bounds.");
        }
        else
        {
            float delta = pz - nearZ;
            ChatLine($"[RynthAi LAND] Nearest vertex Z={nearZ:F2}  Player Z={pz:F2}  Delta={delta:+0.00;-0.00;0}m");
        }
    }

    private void HandleRaylandCommand()
    {
        if (_raycast == null || !_raycast.IsInitialized)
        {
            ReplyFail("[RynthAi RAYLAND] Raycast system not initialized.");
            return;
        }
        if (!Host.TryGetPlayerPose(out uint pCell, out float px, out float py, out float pz,
                out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi RAYLAND] Player position unavailable.");
            return;
        }

        uint pBlockX = (pCell >> 24) & 0xFF;
        uint pBlockY = (pCell >> 16) & 0xFF;
        float worldX = pBlockX * 192.0f + px;
        float worldY = pBlockY * 192.0f + py;
        var geo = _raycast.GeometryLoader;

        ChatLine($"[RynthAi RAYLAND] Player world=({worldX:F1},{worldY:F1},{pz:F2})");

        // Triangle-exact Z at the player's (X, Y), direct lookup.
        float exactZ = geo.GetTerrainZWorld(worldX, worldY);
        if (float.IsNaN(exactZ))
        {
            ChatLine("[RynthAi RAYLAND] GetTerrainZWorld: unavailable (dungeon or off-map).");
        }
        else
        {
            float delta = pz - exactZ;
            ChatLine($"[RynthAi RAYLAND] TerrainZ exact={exactZ:F2}  PlayerZ={pz:F2}  Delta={delta:+0.00;-0.00;0}m");
        }

        // Down-ray from 20 m above player.
        const float dropHeight = 20.0f;
        var dropOrigin = new Raycasting.Vector3(worldX, worldY, pz + dropHeight);
        var dropDir    = new Raycasting.Vector3(0, 0, -1);
        if (geo.RaycastLandscape(dropOrigin, dropDir, dropHeight + 200.0f,
                out float dropDist, out var dropHit, out var dropNormal))
        {
            ChatLine($"[RynthAi RAYLAND] Down-ray: hit at {dropDist:F2}m  groundZ={dropHit.Z:F2}  normalZ={dropNormal.Z:F3}");
        }
        else
        {
            ChatLine("[RynthAi RAYLAND] Down-ray: no hit (unexpected).");
        }

        // Four 300 m cardinal rays from eye height.
        var eye = new Raycasting.Vector3(worldX, worldY, pz + 1.5f);
        RaylandCardinal(geo, eye, new Raycasting.Vector3( 1,  0, 0), "East ");
        RaylandCardinal(geo, eye, new Raycasting.Vector3(-1,  0, 0), "West ");
        RaylandCardinal(geo, eye, new Raycasting.Vector3( 0,  1, 0), "North");
        RaylandCardinal(geo, eye, new Raycasting.Vector3( 0, -1, 0), "South");
    }

    private void RaylandCardinal(GeometryLoader geo, Raycasting.Vector3 origin,
        Raycasting.Vector3 dir, string label)
    {
        const float maxDist = 300.0f;
        if (geo.RaycastLandscape(origin, dir, maxDist,
                out float dist, out var hit, out var normal))
        {
            ChatLine($"[RynthAi RAYLAND] {label} 300m: hit at {dist:F1}m  ({hit.X:F0},{hit.Y:F0},{hit.Z:F1})  normalZ={normal.Z:F3}");
        }
        else
        {
            ChatLine($"[RynthAi RAYLAND] {label} 300m: no hit");
        }
    }

    private void HandleMexecCommand(string[] parts)
    {
        if (_metaManager == null)
        {
            ReplyFail("[RynthAi] Meta not ready (not logged in yet).");
            return;
        }

        if (parts.Length < 3)
        {
            ReplyFail("[RynthAi] Usage: /ra mexec <expression>");
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
            ReplyFail("[RynthAi] Meta not ready (not logged in yet).");
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
        if (_metaManager == null) { ReplyFail("[RynthAi] Meta not ready."); return; }
        var pvars = _metaManager.Expressions.Pvars;
        if (pvars.Count == 0) { ChatLine("[RynthAi] No persistent variables set."); return; }
        ChatLine($"[RynthAi] Persistent variables ({pvars.Count}):");
        foreach (var kv in pvars)
            ChatLine($"[RynthAi]   {kv.Key} = {kv.Value}");
    }

    private void HandleListGvarsCommand()
    {
        if (_metaManager == null) { ReplyFail("[RynthAi] Meta not ready."); return; }
        var gvars = _metaManager.Expressions.Gvars;
        if (gvars.Count == 0) { ChatLine("[RynthAi] No global variables set."); return; }
        ChatLine($"[RynthAi] Global variables ({gvars.Count}):");
        foreach (var kv in gvars)
            ChatLine($"[RynthAi]   {kv.Key} = {kv.Value}");
    }

    private void HandleWieldedCommand()
    {
        if (_objectCache == null) { ReplyFail("[RynthAi] Cache not ready."); return; }

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
            ReplyFail("[RynthAi] Raycast not initialized.");
            return;
        }
        if (_playerId == 0) { ReplyFail("[RynthAi] Not logged in."); return; }

        if (!Host.TryGetPlayerPose(out uint pCell, out float px, out float py, out float pz,
                out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi] Player position unavailable.");
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
            ReplyFail("[RynthAi] Nav debug unavailable: settings not ready.");
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
            ReplyFail("[RynthAi]   Player pose unavailable.");
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
            if (!LegacyUi.NavRouteParser.IsPlainWaypoint(point.Type))
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

    /// <summary>
    /// /ra jump[swzxc] [heading] [holdtime] — UtilityBelt-compatible jump with optional
    /// face-heading and per-direction keys baked into the command name.
    /// </summary>
    private void HandleJumpCommand(string cmd, string[] parts)
    {
        if (_jumper == null)
        {
            ReplyFail("[RynthAi] Jumper not ready (not logged in yet).");
            return;
        }

        // "jumpsw" / "jumpx" / "jumpsx" / bare "jump"
        string letters = cmd.Length > 4 ? cmd.Substring(4) : string.Empty;
        foreach (char c in letters)
        {
            if (c != 's' && c != 'w' && c != 'x' && c != 'z' && c != 'c')
            {
                ReplyFail("[RynthAi] Usage: /ra jump[swzxc] [heading] [holdtime]");
                return;
            }
        }

        float? heading = null;
        int holdMs = 0;

        // args shape: parts[0]=/ra  parts[1]=jump...  [parts[2]=heading]  [parts[3]=holdtime]
        //          or parts[2]=holdtime (no heading).
        if (parts.Length >= 3)
        {
            // Heading must be 0-359 with a dot optionally; holdtime is an integer.
            // Try heading-first form (heading + optional holdtime).
            if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float maybeHeading)
                && maybeHeading >= 0 && maybeHeading < 360
                && parts.Length >= 4)
            {
                heading = maybeHeading;
                if (!int.TryParse(parts[3], out holdMs))
                {
                    ReplyFail("[RynthAi] Usage: /ra jump[swzxc] [heading] [holdtime]");
                    return;
                }
            }
            else if (parts.Length == 3 && int.TryParse(parts[2], out int onlyHold))
            {
                // Single numeric arg is holdtime.
                holdMs = onlyHold;
            }
            else if (parts.Length == 3
                && float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out float onlyHeading)
                && onlyHeading >= 0 && onlyHeading < 360)
            {
                heading = onlyHeading;
            }
            else
            {
                ReplyFail("[RynthAi] Usage: /ra jump[swzxc] [heading] [holdtime]");
                return;
            }
        }

        if (_jumper.Start(letters, heading, holdMs))   // a refusal (busy, bad values) says why itself
            ReplyOk(JumpLine(letters, heading, holdMs));
    }

    /// <summary>What a jump command answers: "Jumping forward, run (500 ms charge), facing 90°."</summary>
    internal static string JumpLine(string letters, float? heading, int holdMs)
    {
        var ways = new List<string>();
        if (letters.Contains('w')) ways.Add("forward");
        if (letters.Contains('x')) ways.Add("backward");
        if (letters.Contains('z')) ways.Add("left");
        if (letters.Contains('c')) ways.Add("right");
        if (letters.Contains('s')) ways.Add("run");
        string how = ways.Count > 0 ? " " + string.Join(", ", ways) : " in place";
        string charge = holdMs > 0 ? $" ({holdMs} ms charge)" : "";
        string face = heading.HasValue ? $", facing {heading.Value:0.#}°" : "";
        return $"[RynthAi] Jumping{how}{charge}{face}.";
    }

    // /ra follow [on|off|status] — track the fellowship leader's live position
    // instead of a route. Opt-in; off by default each session.
    private void HandleFollowCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Settings not ready."); return; }

        string arg = parts.Length >= 3
            ? parts[2].ToLowerInvariant()
            : (settings.FollowMode ? "off" : "on");

        switch (arg)
        {
            case "on":
            case "leader":
                settings.FollowNamedTargetId = 0;
                settings.FollowMode = true;
                settings.EnableNavigation = true;   // follow is a nav activity
                ReplyOk(settings.IsMacroRunning
                    ? "[RynthAi] Follow ON — tracking the fellowship leader."
                    : "[RynthAi] Follow ON — start the bot to begin moving.");
                break;
            case "off":
                settings.FollowMode = false;
                settings.FollowTargetId = 0;
                settings.FollowNamedTargetId = 0;
                ReplyOk("[RynthAi] Follow OFF.");
                break;
            case "status":
                ChatLine($"[RynthAi] Follow: {(settings.FollowMode ? "ON" : "off")}  " +
                         $"targetId=0x{settings.FollowTargetId:X8}  " +
                         $"inFellow={(_fellowshipTracker?.IsInFellowship ?? false)}  " +
                         $"isLeader={(_fellowshipTracker?.IsLeader ?? false)}");
                break;
            default:
            {
                // /ra follow <name>: follow that character (exact name, else partial).
                string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
                WorldObject? who = null;
                if (_objectCache != null)
                {
                    foreach (var wo in _objectCache.GetLandscapeObjects())
                        if (wo.Id != unchecked((int)_playerId) && string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase)) { who = wo; break; }
                    if (who == null)
                        foreach (var wo in _objectCache.GetLandscapeObjects())
                            if (wo.Id != unchecked((int)_playerId) && wo.ObjectClass != RynthCore.Loot.AcObjectClass.Corpse
                                && wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { who = wo; break; }
                }
                if (who == null) { ReplyFail($"[RynthAi] Follow: no one named '{name}' nearby. Usage: /ra follow [on|off|status|<name>]"); break; }
                settings.FollowNamedTargetId = unchecked((uint)who.Id);
                settings.FollowMode = true;
                settings.EnableNavigation = true;
                ReplyOk($"[RynthAi] Follow ON — following {who.Name}.");
                break;
            }
        }
    }

    /// <summary>
    /// /ra navheading &lt;degrees&gt; &lt;yards&gt;: run to the point that far from here on that compass heading
    /// (0 north, 90 east). Loads a one-point run-once route and turns navigation on. Lets a meta follow a
    /// direction (a treasure map's "the site is to the northwest") with the nav engine, no keystrokes.
    /// </summary>
    private void HandleNavHeadingCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        if (parts.Length < 4
            || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double deg)
            || !double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double yards)
            || yards <= 0 || yards > 2000)
        {
            ReplyFail("[RynthAi] Usage: /ra navheading <degrees: 0 north, 90 east> <yards, up to 2000>");
            return;
        }
        if (!Host.HasGetPlayerPose || !Host.TryGetPlayerPose(out _, out _, out _, out float z, out _, out _, out _, out _)
            || !NavCoordinateHelper.TryGetNavCoords(Host, out double ns, out double ew))
        {
            ReplyFail("[RynthAi] navheading: couldn't read where you are.");
            return;
        }

        double rad = deg * Math.PI / 180.0;
        double unit = yards / 240.0;   // nav coordinates are 240-yard units
        var route = new NavRouteParser { RouteType = NavRouteType.Once };
        route.Points.Add(new NavPoint
        {
            Type = NavPointType.Point,
            NS = ns + Math.Cos(rad) * unit,
            EW = ew + Math.Sin(rad) * unit,
            Z = z / 240.0,
        });
        settings.CurrentNavPath = "<navheading>";
        settings.CurrentRoute = route;
        settings.ActiveNavIndex = 0;
        settings.EnableNavigation = true;
        Host.Log($"[RynthAi] navheading: {yards:0} yd on {deg:0}° -> NS {route.Points[0].NS:0.0000} EW {route.Points[0].EW:0.0000}");
        ReplyOk($"[RynthAi] Navigating {yards:0} yd on heading {deg:0}° (to NS {route.Points[0].NS:0.000} EW {route.Points[0].EW:0.000}).");
    }

    private void HandleAddNavPointCommand()
    {
        var settings = _dashboard?.Settings;
        if (settings == null)
        {
            ReplyFail("[RynthAi] Settings not ready.");
            return;
        }

        // Stop any active turn motion so the character doesn't keep spinning
        Host.SetMotionBy("Command", 0x6500000D, false); // TurnRight
        Host.SetMotionBy("Command", 0x6500000E, false); // TurnLeft

        if (!Host.HasGetPlayerPose || !Host.TryGetPlayerPose(out _, out _, out _, out float z, out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi] Player pose unavailable.");
            return;
        }

        if (!NavCoordinateHelper.TryGetNavCoords(Host, out double ns, out double ew))
        {
            ReplyFail("[RynthAi] Could not resolve current coords.");
            return;
        }

        var newPt = new NavPoint { NS = ns, EW = ew, Z = z / 240.0 };   // nav units, like NS/EW
        settings.CurrentRoute.Points.Add(newPt);

        string navName = string.IsNullOrEmpty(settings.CurrentNavPath)
            ? "(unsaved)"
            : System.IO.Path.GetFileName(settings.CurrentNavPath);

        _dashboard?.AutoSaveRoute();

        int idx = settings.CurrentRoute.Points.Count - 1;
        ReplyOk($"[RynthAi] Added waypoint [{idx}] NS={ns:F3} EW={ew:F3} Z={z / 240.0:F4} to {navName}.");
    }

    private void HandleNavCommand(string[] parts)
    {
        if (_dashboard == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string sub = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        string rest = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3) : "";
        switch (sub)
        {
            case "save":
                ReplyResult(_dashboard.SaveRoute(rest.Length > 0 ? rest : null));
                break;
            case "load":
                if (rest.Length == 0) { ReplyFail("[RynthAi] Usage: /ra nav load <name>"); break; }
                ReplyResult(_dashboard.LoadNavByName(rest));
                break;
            case "list":
                var names = _dashboard.NavFileNames();
                ChatLine(names.Count == 0 ? "[RynthAi] No navs in NavProfiles." : $"[RynthAi] Navs ({names.Count}): {string.Join(", ", names)}");
                break;
            case "restart":
            {
                // Stopping and starting the macro keeps the route where it was (a finished Once
                // route stays finished); this is the way to run it again from the top on purpose.
                var route = _dashboard.Settings.CurrentRoute;
                if (_navigationEngine == null) { ReplyFail("[RynthAi] Navigation engine not ready."); break; }
                if (!_navigationEngine.RestartRoute())
                {
                    ChatLine(route != null && route.RouteType == NavRouteType.Follow && route.FollowTargetName.Length > 0
                        ? "[RynthAi] A Follow route has no waypoints to restart."
                        : "[RynthAi] No nav route loaded to restart.");
                    break;
                }
                ReplyOk($"[RynthAi] Nav route restarted from its first point ({route!.RouteType}, {route.Points.Count} waypoints).");
                break;
            }
            default:
                // Bare "/ra nav" asks what it takes; anything else is a wrong form.
                const string navUsage = "[RynthAi] /ra nav save [name] | /ra nav load <name> | /ra nav list | /ra nav restart";
                if (sub.Length == 0) ChatLine(navUsage);
                else ReplyFail($"[RynthAi] Unknown /ra nav verb '{sub}'. Usage: /ra nav save [name] | load <name> | list | restart");
                break;
        }
    }

    private void HandleMetaCommand(string[] parts)
    {
        if (_dashboard == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string sub = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        string rest = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3) : "";
        switch (sub)
        {
            case "load":
            case "new":
                if (rest.Length == 0) { ReplyFail($"[RynthAi] Usage: /ra meta {sub} <name>"); break; }
                ReplyResult(_dashboard.LoadMetaByName(rest, createOnly: sub == "new"));
                break;
            case "list":
                var names = _dashboard.MetaFileNames();
                ChatLine(names.Count == 0 ? "[RynthAi] No metas in MetaFiles." : $"[RynthAi] Metas ({names.Count}): {string.Join(", ", names)}");
                break;
            default:
                if (sub.Length == 0) ChatLine("[RynthAi] /ra meta load <name> | /ra meta new <name> | /ra meta list");
                else ReplyFail($"[RynthAi] Unknown /ra meta verb '{sub}'. Usage: /ra meta load <name> | new <name> | list");
                break;
        }
    }

    private void HandleScanCommand()
    {
        if (_objectCache == null) { ReplyFail("[RynthAi] Cache not ready."); return; }
        if (_playerId == 0) { ReplyFail("[RynthAi] Not logged in."); return; }

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
            if (rule.Conditions.Count == 0)
                unconditional++;

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
            ReplyFail("[RynthAi] Cache not ready.");
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
                if (VTankLootEvaluator.Match(rule, item, lootCtx))
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
                ReplyOk("[RynthAi] Loot inspect: ON — classifying each item you select.");
                return;
            }
            if (mode is "off" or "0" or "false")
            {
                _lootInspectMode = false;
                ReplyOk("[RynthAi] Loot inspect: OFF.");
                return;
            }
        }

        if (!Host.HasGetSelectedItemId)
        {
            ReplyFail("[RynthAi] Host does not expose selected item id.");
            return;
        }
        uint selected = Host.GetSelectedItemId();
        if (selected == 0)
        {
            ReplyFail("[RynthAi] No item selected. Click an item in the world or inventory first.");
            return;
        }
        InspectLootRuleForItem(unchecked((int)selected));
    }

    internal void InspectLootRuleForItem(int itemId, bool quiet = false)
    {
        if (_objectCache == null) { if (!quiet) ReplyFail("[RynthAi] Loot inspect: the object cache isn't ready (log in first)."); return; }
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
            if (!VTankLootEvaluator.Match(rule, item, ctx)) continue;
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
            ReplyFail("[RynthAi] No loot profile selected. Pick one in the dashboard or pass a full path.");
            return false;
        }

        if (!System.IO.File.Exists(candidatePath))
        {
            ReplyFail($"[RynthAi] Loot profile not found: {candidatePath}");
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
            ReportLootProfileProblems(profile, System.IO.Path.GetFileName(candidatePath));
            return true;
        }
        catch (Exception ex)
        {
            ReplyFail($"[RynthAi] Failed to load loot profile: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reports, in chat and the log, a header rule count that disagrees with the rules
    /// found, and names the rules that use condition types RynthAi cannot evaluate. The
    /// evaluator treats such a rule as never matching, so the items it was written for
    /// stay on corpses with nothing in chat or the log to say why.
    /// </summary>
    private void ReportLootProfileProblems(VTankLootProfile profile, string fileName)
    {
        if (profile.DeclaredRuleCount >= 0 && profile.DeclaredRuleCount != profile.Rules.Count)
        {
            string countMessage = $"[RynthAi] Loot profile '{fileName}': header declares {profile.DeclaredRuleCount} rule(s) " +
                                  $"but the file holds {profile.Rules.Count}; all {profile.Rules.Count} are in use. " +
                                  "Re-save the profile in the loot editor to correct the header.";
            ChatLine(countMessage);
            RynthLog.Write(LogCat.Looting, countMessage);
        }

        // A rule with no conditions is unconditional (VTank's too): it takes every item that reaches
        // it. R-20261101-133624-7eeb (Conquest, 2026-11-01): every item on every corpse was kept by
        // 'Trade Note', which in LootSnob is a Name match; one that has lost its condition does
        // exactly that, silently. Said at load, with where it stands, so it can be fixed.
        var unconditional = profile.Rules
            .Select((r, i) => (Rule: r, Index: i))
            .Where(x => x.Rule.Conditions.Count == 0)   // a disabled rule carries a DisabledRule node, so it isn't one
            .ToList();
        if (unconditional.Count > 0)
        {
            var first = unconditional[0];
            string names = string.Join(", ", unconditional.Take(3).Select(x => $"'{(string.IsNullOrWhiteSpace(x.Rule.Name) ? "#" + (x.Index + 1) : x.Rule.Name.Trim())}' (rule {x.Index + 1}, {x.Rule.Action})"));
            string uncMessage = $"[RynthAi] Loot profile '{fileName}': {unconditional.Count} rule(s) have no conditions, so they take every item that reaches them: {names}"
                             + (unconditional.Count > 3 ? $" and {unconditional.Count - 3} more" : "")
                             + $". Every item that no rule above rule {first.Index + 1} takes will be {first.Rule.Action}. Add a condition in the Loot Editor, or disable the rule.";
            ChatLine(uncMessage);
            RynthLog.Write(LogCat.Looting, uncMessage);
        }

        var bad = new List<string>();
        foreach (VTankLootRule rule in profile.Rules)
        {
            var types = rule.Conditions
                .Select(c => c.NodeType)
                .Where(t => VTankNodeTypes.GetDataLineCount(t) < 0)
                .Distinct()
                .ToList();
            if (types.Count > 0)
                bad.Add($"'{rule.Name.Trim()}' (type {string.Join("/", types)})");
        }
        if (bad.Count == 0) return;

        string message = $"[RynthAi] Loot profile '{fileName}': {bad.Count} rule(s) use condition types " +
                         $"RynthAi does not support and will never match: {string.Join(", ", bad)}.";
        ChatLine(message);
        RynthLog.Write(LogCat.Looting, message);
    }

    // ── Loot Editor (engine ImGui panel) ────────────────────────────────────

    private LootEditorBridge? _lootEditor;

    /// <summary>The in-game Loot Editor's bridge (PluginExports). Created on first use.</summary>
    internal LootEditorBridge LootEditor => _lootEditor ??= new LootEditorBridge(
        () => _dashboard?.Settings?.CurrentLootPath ?? string.Empty,
        OnLootProfileEdited,
        Vendor.AutoVendorManager.MainProfileDir)
    {
        Items = new LootItemHooks
        {
            SelectedItemId = () => Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0,
            Facts = BuildLootItemFacts,
            FirstMatchUtl = FirstLootRuleFor,
            FirstMatchNative = FirstLootRuleFor,
            Added = OnLootRuleAdded,
            BuiltInKeep = LootBuiltInKeepFor,
        },
    };

    /// <summary>
    /// Why RynthAi's looting takes the item without a loot profile rule, for the "Add to
    /// loot profile" popup: the looter's own ClassifyItemAgainstProfile as a preview (nothing
    /// recorded), so the answer is exactly what [LootEval] would tag. An item already in the
    /// pack that Mana Tap looted earlier says so even when the tap quota is full now.
    /// </summary>
    private Loot.LootBuiltInKeep? LootBuiltInKeepFor(uint itemId)
    {
        WorldObject? wo = _objectCache?[unchecked((int)itemId)];
        LegacyUiSettings? settings = _dashboard?.Settings;
        if (wo == null || settings == null) return null;
        try
        {
            if (ClassifyLootItem(wo, settings, preview: true, out string action, out _, out _, out string builtIn)
                && builtIn.Length > 0)
                return new Loot.LootBuiltInKeep(builtIn, BeforeProfile: action != ManaTapActionLabel);
            if (_manaStoneManager?.WasLootedForTap(wo.Id) == true)
                return new Loot.LootBuiltInKeep(
                    "it was looted for Mana Tap, to drain into an empty mana stone - change in the Items panel, Mana Stone Tapping",
                    BeforeProfile: false);
        }
        catch (Exception ex)
        {
            Host.Log($"[LootAdd] built-in keep check failed for 0x{itemId:X8}: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// The Loot Editor saved <paramref name="path"/>: drop the cached copies and,
    /// when it is the loot profile in use, load it again now (its chat line says so).
    /// Plugin pump thread (the editor's export), the same thread as the tick.
    /// </summary>
    private void OnLootProfileEdited(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        if (!ForgetLootProfile(path))
        {
            ChatLine($"[RynthAi] Loot Editor saved {name}.");
            return;
        }
        ChatLine($"[RynthAi] Loot Editor saved {name}; reloading it.");
        ReloadLootProfileInUse(path);
    }

    /// <summary>Drops the cached copies of <paramref name="path"/>. True when it is the loot profile in use.</summary>
    private bool ForgetLootProfile(string path)
    {
        bool Same(string a) => RynthCore.Loot.Editing.LootEditSession.SamePath(a, path);
        if (Same(_loadedLootProfilePath))
        {
            _loadedLootProfile = null;
            _loadedLootProfileTime = DateTime.MinValue;
        }
        if (Same(_nativeLootProfilePath))
        {
            _nativeLootProfile = null;
            _nativeLootProfileTime = DateTime.MinValue;
        }
        return Same(_dashboard?.Settings?.CurrentLootPath ?? string.Empty);
    }

    private void ReloadLootProfileInUse(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) TryLoadNativeLootProfile(out _, out _);
        else TryLoadLootProfile(string.Empty, out _, out _);
    }

    // ── Add to loot profile (a clicked item; /ra loot add, the engine's popup) ──

    /// <summary>A rule from "Add to loot profile" was saved: say so, and reload the profile when it is the one in use.</summary>
    private void OnLootRuleAdded(RynthCore.Loot.Editing.LootEditItemDraft d)
    {
        bool inUse = ForgetLootProfile(d.TargetPath);
        ChatLine($"[RynthAi] {d.Message}{(inUse ? "" : " (not the profile RynthAi loots with)")}.");
        if (inUse) ReloadLootProfileInUse(d.TargetPath);
    }

    /// <summary>
    /// What a loot rule can be built from: the item's name, class, stack, key properties and
    /// T11 reading, from the object cache. <paramref name="like"/> adds its spells;
    /// <paramref name="t11"/> asks for an ID when a T11 item's text hasn't arrived.
    /// </summary>
    private RynthCore.Loot.Editing.LootItemFacts? BuildLootItemFacts(uint itemId, bool like, bool t11)
    {
        WorldObject? wo = _objectCache?[unchecked((int)itemId)];
        if (wo == null) return null;
        var f = new RynthCore.Loot.Editing.LootItemFacts
        {
            Id = wo.Id,
            Name = wo.Name,
            ObjectClass = (int)wo.ObjectClass,
            StackSize = Math.Max(1, wo.Values(LongValueKey.StackCount, 1)),
            MaxStackSize = Math.Max(1, wo.Values(LongValueKey.MaxStackSize, 1)),
        };
        foreach (int key in RynthCore.Loot.Editing.LootItemRules.IntKeys)
        {
            int v = wo.Values(key, 0);
            if (v != 0) f.Ints[key] = v;
        }
        foreach (int key in RynthCore.Loot.Editing.LootItemRules.DoubleKeys)
        {
            double v = wo.Values((DoubleValueKey)key, 0.0);
            if (v != 0) f.Doubles[key] = v;
        }
        if (like)
        {
            var ctx = new VTankLootContext(Host, _playerId) { Cache = _objectCache };
            foreach (string n in ctx.GetItemSpellNames(itemId))
                if (!string.IsNullOrEmpty(n)) f.Spells.Add(n);
            // Material, workmanship and spells need an ID: ask for one, so the next preview has them.
            if (Host.HasRequestId && Host.HasHasAppraisalData && !Host.HasAppraisalData(itemId)) Host.RequestId(itemId);
        }
        // Always read (a cached parse), so the popup knows whether the T11 option applies.
        f.T11 = Loot.T11ItemSupport.Get(wo);
        // T11 tier, grade and modifiers are appraisal text: ask for it, so the next preview has them.
        if (t11 && f.T11.IsT11 && !f.T11.HasText && Host.HasRequestId) Host.RequestId(itemId);
        return f;
    }

    /// <summary>The first rule of <paramref name="profile"/> that takes the item now (the corpse looter's order), or -1.</summary>
    private int FirstLootRuleFor(uint itemId, VTankLootProfile profile)
    {
        WorldObject? wo = _objectCache?[unchecked((int)itemId)];
        if (wo == null) return -1;
        VTankLootEvaluator.FirstMatch(profile, wo, new VTankLootContext(Host, _playerId) { Cache = _objectCache }, out int index);
        return index;
    }

    private int FirstLootRuleFor(uint itemId, RynthCore.Loot.LootProfile profile)
    {
        WorldObject? wo = _objectCache?[unchecked((int)itemId)];
        if (wo == null) return -1;
        for (int i = 0; i < profile.Rules.Count; i++)
            if (Loot.LootEvaluator.Matches(profile.Rules[i], wo, _charSkills)) return i;
        return -1;
    }

    /// <summary>
    /// /ra loot add [keep|keep N|salvage|sell|read] [name|like] [t11] [preview]: a rule for
    /// the selected item, straight into the loot profile in use (the defaults: exact
    /// name and class; Read for scrolls, Keep # one full stack for stacks, else Keep).
    /// t11 also requires the item's T11 attributes.
    /// </summary>
    private void HandleLootCommand(string[] parts)
    {
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : string.Empty;
        if (sub == "why")
        {
            string how = parts.Length >= 4 ? parts[3].ToLowerInvariant() : string.Empty;
            if (how.Length > 0 && how is not ("window" or "show"))
            {
                ReplyFail($"[RynthAi] loot why: '{parts[3]}'? Usage: /ra loot why [window] (window: the answer in the Loot why window).");
                return;
            }
            HandleLootWhyCommand(window: how.Length > 0);
            return;
        }
        if (sub == "mode")
        {
            HandleLootModeCommand(parts);
            return;
        }
        if (sub != "add")
        {
            ChatLine("[RynthAi] /ra loot mode [profile|rares] — what is looted: the loot profile, or rares and needed mana stones only");
            ChatLine("[RynthAi] /ra loot why [window] — which loot rule takes the selected item, and why the rules above it didn't (window: in the Loot why window)");
            ChatLine("[RynthAi] /ra loot add [keep|keep N|salvage|sell|read] [name|like] [t11] [preview] — add a loot rule for the selected item to the loot profile in use");
            ChatLine("[RynthAi]   default: exact name + class; Read for scrolls, Keep # (one full stack) for stacks, else Keep. name = name only, like = items like this. t11 = also require its T11 tier, grade and modifiers (at least this good). preview = show it, add nothing.");
            ChatLine("[RynthAi]   Also: right-click an item in the Inventory panel, or 'Add selected item' in the Loot Editor; 'Why this loot rule?' there and on the Selection window's right-click.");
            return;
        }

        var req = new RynthCore.Loot.Editing.LootEditItemRequest();
        bool preview = false;
        for (int i = 3; i < parts.Length; i++)
        {
            string w = parts[i].ToLowerInvariant();
            switch (w)
            {
                case "keep": req.Action = (int)VTankLootAction.Keep; break;
                case "keep#": req.Action = (int)VTankLootAction.KeepUpTo; break;
                case "salvage": req.Action = (int)VTankLootAction.Salvage; break;
                case "sell": req.Action = (int)VTankLootAction.Sell; break;
                case "read": req.Action = (int)VTankLootAction.Read; break;
                case "name": req.Match = (int)RynthCore.Loot.Editing.LootItemMatch.Name; break;
                case "like": req.Match = (int)RynthCore.Loot.Editing.LootItemMatch.Like; break;
                case "exact": req.Match = (int)RynthCore.Loot.Editing.LootItemMatch.NameAndClass; break;
                case "t11": req.IncludeT11 = true; break;
                case "preview":
                case "?": preview = true; break;
                default:
                    if (int.TryParse(w.TrimStart('#'), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 0)
                    {
                        req.Action = (int)VTankLootAction.KeepUpTo;
                        req.KeepCount = n;
                        break;
                    }
                    ReplyFail($"[RynthAi] loot add: '{parts[i]}'? Use keep, keep N, salvage, sell, read, name, like, t11 or preview.");
                    return;
            }
        }

        RynthCore.Loot.Editing.LootEditItemDraft d = LootEditor.AddItem(req, add: !preview);
        if (d.Preview.Count > 0 && (preview || !d.Ok))
        {
            ChatLine($"[RynthAi] {d.ItemName} -> {d.TargetFile}:");
            foreach (string line in d.Preview) ChatLine("[RynthAi]   " + line);
            if (d.OrderNote.Length > 0) ChatLine("[RynthAi]   " + d.OrderNote);
        }
        foreach (string note in d.Notes) ChatLine("[RynthAi]   Note: " + note);
        if (!d.Ok) ChatLine("[RynthAi] Loot rule not added: " + d.Error);
        else if (preview) ChatLine("[RynthAi] (preview only; /ra loot add without 'preview' adds it)");
        else if (d.Added && d.OrderNote.Length > 0) ChatLine("[RynthAi]   " + d.OrderNote);
        // An add's own chat line comes from OnLootRuleAdded.
    }

    /// <summary>
    /// /ra loot mode [profile|rares]: the Loot Mode (Loot.LootModes), saved with the settings
    /// profile. No word: says the mode now.
    /// </summary>
    private void HandleLootModeCommand(string[] parts)
    {
        const string usage = "/ra loot mode profile|rares (profile: the loot profile; rares: rares and needed mana stones only)";
        var dash = _dashboard;
        LegacyUiSettings? s = dash?.Settings;
        if (s == null) { ReplyFail("[RynthAi] loot mode: settings aren't loaded yet (log in first). Usage: " + usage); return; }
        if (parts.Length < 4)
        {
            ChatLine($"[RynthAi] Loot Mode: {LootModeLine(s)}. Usage: {usage}");
            return;
        }
        string arg = string.Join(" ", parts, 3, parts.Length - 3);
        if (!Loot.LootModes.TryParse(arg, out int mode))
        {
            ReplyFail($"[RynthAi] loot mode: '{arg}'? Loot Mode is still {LootModeLine(s)}. Usage: {usage}");
            return;
        }
        s.LootMode = mode;
        dash!.SaveSettings();
        ReplyOk($"[RynthAi] Loot Mode: {LootModeLine(s)}.");
    }

    /// <summary>The dashboard's LOOT picker set the Loot Mode. Plugin tick.</summary>
    internal void SetLootModeFromPanel(int mode)
    {
        var dash = _dashboard;
        LegacyUiSettings? s = dash?.Settings;
        if (s == null) return;
        mode = Loot.LootModes.Clamp(mode);
        if (s.LootMode == mode) return;
        s.LootMode = mode;
        dash!.SaveSettings();
        ChatLine($"[RynthAi] Loot Mode: {LootModeLine(s)}.");
    }

    /// <summary>The mode for a chat line, with what it means for the loot profile.</summary>
    private static string LootModeLine(LegacyUiSettings s)
    {
        int mode = Loot.LootModes.Clamp(s.LootMode);
        if (!Loot.LootModes.IsRares(mode)) return Loot.LootModes.Describe(mode);
        string profile = string.IsNullOrEmpty(s.CurrentLootPath) ? "" : $"; the loot profile {System.IO.Path.GetFileName(s.CurrentLootPath)} is not used";
        return $"{Loot.LootModes.Describe(mode)} (mana stones up to the keep count, {s.ManaStoneKeepCount}{profile})";
    }

    private const int LootWhyChatLines = 10;

    /// <summary>
    /// /ra loot why [window]: the selected item's loot decision (<see cref="BuildLootWhyReport"/>) in
    /// chat, or (<paramref name="window"/>: window, the "Why this loot rule?" buttons) in the Loot why
    /// window, which then says it all instead.
    /// </summary>
    private void HandleLootWhyCommand(bool window = false)
    {
        Loot.LootWhy.Report report = BuildLootWhyReport(window);
        if (window)
        {
            // The window is the answer ("Why this loot rule?" buttons, /ra loot why window).
            ShowLootWhyWindow(report);
            return;
        }
        List<string> lines = Loot.LootWhy.ChatLines(report, LootWhyChatLines);
        if (!report.Ok)
        {
            ReplyFail(lines[0]);
            return;
        }
        foreach (string line in lines) ChatLine(line);
    }

    /// <summary>
    /// The selected item's loot decision, from the looter itself (built-in keeps and Keep # caps
    /// included), then, for a VTank profile, the rule that takes it and the rules above it that came
    /// close (all of them in the log, [LootWhy] lines). An item without its ID: asks for it.
    /// <paramref name="window"/>: for the window, so a failure says what to do there. Pump thread.
    /// </summary>
    private Loot.LootWhy.Report BuildLootWhyReport(bool window)
    {
        if (!Host.HasGetSelectedItemId) return Loot.LootWhy.Report.Fail("this engine can't tell RynthAi the selected item.");
        uint selected = Host.GetSelectedItemId();
        if (selected == 0)
            return Loot.LootWhy.Report.Fail(window
                ? "select an item first (on a corpse, the ground or in a pack), then Check again."
                : "select an item first (on a corpse, the ground or in a pack), then /ra loot why.");
        WorldObject? item = _objectCache?[unchecked((int)selected)];
        if (item == null) return Loot.LootWhy.Report.Fail($"item 0x{selected:X8} isn't known yet; select it again in a moment.");
        LegacyUiSettings? settings = _dashboard?.Settings;
        if (settings == null) return Loot.LootWhy.Report.Fail("settings aren't loaded yet (log in first).");
        string path = (settings.CurrentLootPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0) return Loot.LootWhy.Report.Fail("no loot profile is selected. Pick one on the dashboard (Loot).");
        if (!System.IO.File.Exists(path)) return Loot.LootWhy.Report.Fail("the loot profile in use isn't there: " + path);

        bool hasId = !Host.HasHasAppraisalData || Host.HasAppraisalData(selected);
        bool taken = ClassifyLootItem(item, settings, preview: true, out string action, out _, out string ruleLabel, out string builtIn);
        string looter = taken ? $"[{action}] {ruleLabel}" : "left";
        if (builtIn.Length > 0) looter += " (" + builtIn + ")";

        // Rares mode: its verdict is the whole answer (the loot profile isn't used).
        if (Loot.LootModes.IsRares(settings.LootMode))
        {
            Host.Log($"[LootWhy] '{item.Name}' 0x{selected:X8} {Loot.LootWhy.IdNote(hasId)} -> {looter}; Loot Mode rares");
            return Loot.LootWhy.ModeOnly(item.Name, hasId, looter, $"Loot Mode: {LootModeLine(settings)} (the loot profile isn't used)");
        }

        if (TryLoadNativeLootProfile(out _, out _))
            return Loot.LootWhy.Describe(item.Name, hasId, looter, string.Empty, 0, null, false);
        if (!TryLoadLootProfile(string.Empty, out VTankLootProfile profile, out path))
            return Loot.LootWhy.Report.Fail("the loot profile in use couldn't be read (chat says why).");

        VTankLootContext ctx = new(Host, _playerId) { Cache = _objectCache };
        Loot.LootWhy.Result why = Loot.LootWhy.Explain(profile, item, ctx, hasId);
        bool asked = why.Verdict == VTankLootVerdict.NeedsId && Host.HasRequestId && Host.RequestId(selected);
        Loot.LootWhy.Report report = Loot.LootWhy.Describe(item.Name, hasId, looter, System.IO.Path.GetFileName(path),
            profile.Rules.Count, why, asked);
        Host.Log($"[LootWhy] '{item.Name}' 0x{selected:X8} {Loot.LootWhy.IdNote(hasId)} -> {looter}; {report.Decision}");
        if (report.SwitchedOff.Length > 0)
            Host.Log("[LootWhy]   " + Loot.LootWhy.SwitchedOffLine(report.SwitchedOff));
        foreach (Loot.LootWhy.NearMiss m in report.NearMisses)
            Host.Log("[LootWhy]   " + Loot.LootWhy.NearMissLine(m));
        return report;
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

    /// <summary>/ra why — one-glance "why is the bot doing (or not doing) what it's doing": the
    /// winning activity, the legacy BotAction, busy count, target lock + cast state, and the D2
    /// three-tier scan counts, plus a one-line interpretation of the likely stall cause. Turns an
    /// "it's just standing there" investigation into a single read. (D3)</summary>
    private void HandleWhyCommand()
    {
        if (_combatManager == null) { ReplyFail("[RynthAi] Combat manager not ready."); return; }
        var s = _combatManager.GetStateSnapshot();

        int total  = _combatManager.LastScanTotalMonsters;
        int ring   = _combatManager.LastScanInRing;
        int poss   = _combatManager.LastScanPossible;
        int losBlk = _combatManager.LastScanLosBlocked;

        string arbiter = _arbiter != null
            ? $"{ActivityArbiter.ToBotAction(_arbiter.Current)} ({_arbiter.Current})"
            : "n/a (not started)";

        ChatLine("[RynthAi] === Why ===");
        ChatLine($"[RynthAi] macro={s.IsMacroRunning}  botAction='{s.BotAction}'  arbiter={arbiter}");
        ChatLine($"[RynthAi] enableCombat={s.EnableCombat}  busy={s.BusyCount}  facing={s.FacingTarget}");
        ChatLine($"[RynthAi] targets: total={total} ring={ring} possible={poss} losBlk={losBlk}  scanned={s.ScannedCount}");
        ChatLine($"[RynthAi] active=0x{(uint)s.ActiveTargetId:X8}  locked=0x{(uint)s.LockedTargetId:X8}");
        int forced = _combatManager.ForcedTargetId;
        bool bySelection = _combatManager.ForcedBySelection;
        if (forced != 0)
        {
            string forcedName = _objectCache?[forced]?.Name ?? $"0x{(uint)forced:X8}";
            ChatLine(bySelection
                ? $"[RynthAi] fighting the monster you selected: {forcedName} (Fight the monster I select{(forced == s.ActiveTargetId ? "" : "; not in reach yet")})"
                : $"[RynthAi] target lock (/ra targetlock): {forcedName}{(forced == s.ActiveTargetId ? "" : " (not in reach yet)")}");
        }
        // D4 record-only skip reasons: why the last combat/buff cycle did (or didn't) cast.
        ChatLine($"[RynthAi] combatSkip='{s.LastCombatSkipReason}'"
                 + (_buffManager != null ? $"  buffSkip='{_buffManager.GetStateSnapshot().LastBuffSkipReason}'" : ""));
        if (s.ScannedCount > 0)
            ChatLine($"[RynthAi] closest: '{s.ClosestScannedName}' @ {s.ClosestScannedDist:0.0}yd");
        // A higher-priority monster the target was picked over, and why: often just Monster Range
        // (a setting), sometimes a wall. Only monsters inside Monster Range are candidates.
        string? passedOver = _combatManager.DescribeHigherPriorityPassedOver();
        if (passedOver != null)
            ChatLine($"[RynthAi] higher priority not targeted: {passedOver}"
                     + (passedOver.Contains("beyond Monster Range", StringComparison.Ordinal)
                        ? " - raise Monster Range (Advanced settings, Ranges) to fight it from farther" : ""));
        ChatLine($"[RynthAi] melee/missile: lastSwing={_combatManager.LastSwingEvidenceDescription}  attackRebinds={_combatManager.AttackRebinds}  "
                 + $"stallWatch episodes={_combatManager.StallEpisodes} recovered={_combatManager.StallRecoveries}");

        // Nav and salvage each own a piece of the "standing there" class, so name
        // what they're holding rather than leaving it to /ra navstate + /ra salvstate.
        if (_navigationEngine != null)
        {
            var n = _navigationEngine.GetStateSnapshot();
            string navHold = !n.EnableNavigation ? "nav disabled"
                           : n.Recovery.StartsWith("area hold", StringComparison.Ordinal) ? n.Recovery
                           : n.Recovery.StartsWith("detour", StringComparison.Ordinal) ? $"recovering: {n.Recovery}"
                           : n.InRecovery        ? $"STUCK-recovery {n.RecoveryKind} ({n.RecoveryRemainMs}ms, x{n.StuckCount})"
                           : n.InPause           ? $"route pause ({n.PauseRemainMs}ms)"
                           : n.PortalState != "None" ? $"portal/recall {n.PortalState}"
                           : n.PointCount == 0   ? "no route loaded"
                           : n.Stopped           ? "stopped (gated by BotAction)"
                           : "running";
            ChatLine($"[RynthAi] nav: {navHold}  pt={n.Index + 1}/{n.PointCount} ({n.PointType})"
                     + (double.IsNaN(n.DistYd) ? "" : $" {n.DistYd:0.0}yd err={n.HeadingErrDeg:+0.0;-0.0}°"));
        }
        if (_salvageManager != null)
        {
            var v = _salvageManager.GetStateSnapshot();
            if (v.QueueCount > 0 || v.Phase != "Idle" || v.LastError.Length > 0)
                ChatLine($"[RynthAi] salvage: phase={v.Phase} queue={v.QueueCount} combine={v.CombinePhase}"
                         + $" grp={v.CombineGroupIdx + 1}/{v.CombineGroupCount}"
                         + (v.LastError.Length > 0 ? $"  lastErr='{v.LastError}' ({v.MsSinceLastError / 1000}s ago)" : ""));
        }

        // Interpretation — the point of D2/D3: name the most likely reason it isn't attacking.
        string why =
            !s.IsMacroRunning        ? "macro is STOPPED (start it from the panel / command)." :
            // Owner's corrected rule (2026-11-02, R-20261103-000159-a65e): Boost Nav Priority only
            // while a nav route runs; with no route it changes nothing, so it is no reason then.
            _dashboard?.Settings is { } boostSettings && BoostNavRule.Active(boostSettings)
                                     ? "Boost Nav Priority is on and a nav route runs: RynthAi only follows it (no hunting, no looting). Turn it off to hunt along the route." :
            !s.EnableCombat          ? "combat is disabled in settings." :
            s.BusyCount > 0          ? "AC busy>0 — waiting on a server action (cast/use/move); try /ra clearbusy if stuck." :
            forced != 0 && forced == s.ActiveTargetId && bySelection
                                     ? "fighting the monster you selected (Fight the monster I select: Settings > Ranges)." :
            forced != 0 && forced == s.ActiveTargetId
                                     ? "fighting the target-locked monster (/ra targetlock; /ra targetlock off releases it)." :
            total < 0              ? "no combat scan has run yet (cold start / just logged in)." :
            total == 0               ? "no monsters in the object cache (respawn-blind, or wrong area)." :
            ring  == 0               ? "monsters exist but ALL out of engage range (nav not closing distance?)." :
            poss == 0 && losBlk > 0  ? "monsters in range but LOS-blocked (behind walls)." :
            poss == 0                ? "monsters in range but all filtered (blacklist / not-attackable)." :
            s.ActiveTargetId == 0    ? "candidates exist but none acquired yet (should engage next tick)." :
                                       "engaged — should be attacking; if not, check busy / cast cadence.";
        ChatLine($"[RynthAi] likely: {why}");
    }

    /// <summary>
    /// /ra ghosts            list the ghosts combat found (objects the client draws but the server
    ///                       doesn't have for this character: no swing at them ever starts)
    /// /ra ghosts clear      remove them from the client (engine API v83), like a server delete
    /// /ra ghosts forget     make them targetable again
    /// /ra ghost             mark the selected monster as a ghost and remove it from the client
    /// </summary>
    private void HandleGhostsCommand(string cmd, string[] parts)
    {
        if (_combatManager == null) { ReplyFail("[RynthAi] Combat manager not ready."); return; }
        var ghosts = _combatManager.Ghosts;
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        bool canRemove = Host.HasRemoveClientObject;

        if (cmd.Equals("ghost", StringComparison.OrdinalIgnoreCase) && sub.Length == 0)
        {
            uint sel = Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0;
            var wo = sel != 0 ? _objectCache?[(int)sel] : null;
            if (wo == null || (int)wo.ObjectClass != (int)AcObjectClass.Monster)
            {
                ReplyFail("[RynthAi] /ra ghost: select the monster that never takes a swing first.");
                return;
            }
            double silent = _combatManager.ServerSilentMs((int)sel, RynthClock.Now);
            _combatManager.MarkGhost((int)sel, wo.Name, silent, "marked by /ra ghost");
            ReplyOk($"[RynthAi] 0x{sel:X8} '{wo.Name}' marked as a ghost" + (canRemove ? " and sent for removal from the client (see the log)." : " (this engine can't remove it from the client; it is skipped)."));
            return;
        }

        switch (sub)
        {
            case "clear":
            {
                if (!canRemove) { ReplyFail("[RynthAi] This engine can't remove objects from the client (needs plugin API v83)."); return; }
                int sent = 0, refused = 0;
                foreach (var g in ghosts.All.ToList())
                {
                    int r = Host.RemoveClientObject((uint)g.Id);
                    if (r == 1 || r == 2) sent++; else refused++;
                }
                ReplyOk($"[RynthAi] Ghosts: {sent} removed from the client" + (refused > 0 ? $", {refused} refused (the server talked about them recently, or not a creature - they stay skipped)." : "."));
                return;
            }
            case "forget":
            {
                int n = ghosts.Count;
                ghosts.Clear();
                ReplyOk($"[RynthAi] Ghosts: {n} forgotten; they can be targeted again.");
                return;
            }
        }

        if (ghosts.Count == 0)
        {
            ChatLine($"[RynthAi] No ghosts found this session ({ghosts.MarkedTotal} marked in all). /ra ghost marks the selected monster.");
            return;
        }
        ChatLine($"[RynthAi] {ghosts.Count} ghost(s) skipped by combat (the client draws them, the server doesn't have them):");
        DateTime now = RynthClock.Now;
        foreach (var g in ghosts.All)
        {
            double silent = _combatManager.ServerSilentMs(g.Id, now);
            string s = silent < 0 ? "no server update seen" : $"server silent {silent / 1000:0}s";
            ChatLine($"  0x{(uint)g.Id:X8} '{g.Name}' - marked {(now - g.MarkedAt).TotalSeconds:0}s ago, {s}");
        }
        ChatLine(canRemove ? "[RynthAi] /ra ghosts clear removes them from the client; /ra ghosts forget makes them targetable again."
                           : "[RynthAi] /ra ghosts forget makes them targetable again (removing them needs engine API v83).");
    }

    private void HandleCombatStateCommand()
    {
        if (_combatManager == null) { ReplyFail("[RynthAi] Combat manager not ready."); return; }

        var s = _combatManager.GetStateSnapshot();
        var now = RynthClock.Now;
        double sinceAttackS = s.LastAttackCmd      == DateTime.MinValue ? -1 : (now - s.LastAttackCmd).TotalSeconds;
        double sinceLostS   = s.TargetLostScanTime == DateTime.MinValue ? -1 : (now - s.TargetLostScanTime).TotalSeconds;
        double sinceStanceS = s.LastStanceAttempt  == DateTime.MinValue ? -1 : (now - s.LastStanceAttempt).TotalSeconds;
        double sinceEquipS  = s.LastEquipTime      == DateTime.MinValue ? -1 : (now - s.LastEquipTime).TotalSeconds;

        static string ModeName(int m) => m switch
        {
            -1 => "?",
            1 => "NonCombat",
            2 => "Melee",
            4 => "Missile",
            8 => "Magic",
            _ => $"mode={m}",
        };

        string cachedMode = ModeName(s.CurrentCombatMode);
        string liveMode   = ModeName(s.LiveCombatMode);
        string desiredMode = ModeName(s.PickedWeaponMode);

        ChatLine("[RynthAi] === Combat State ===");
        ChatLine($"[RynthAi] macro={s.IsMacroRunning}  enableCombat={s.EnableCombat}  botAction={s.BotAction}");
        ChatLine($"[RynthAi] mode cached={cachedMode}  live={liveMode}  desired={desiredMode}");
        ChatLine($"[RynthAi] activeTargetId=0x{(uint)s.ActiveTargetId:X8}  lockedTargetId=0x{(uint)s.LockedTargetId:X8}  facing={s.FacingTarget}");
        ChatLine($"[RynthAi] scanned={s.ScannedCount}  closest=0x{(uint)s.ClosestScannedId:X8} '{s.ClosestScannedName}' @ {s.ClosestScannedDist:F1}yd");
        ChatLine($"[RynthAi] weapon=0x{(uint)s.PickedWeaponId:X8} '{s.PickedWeaponName}' ({s.PickedWeaponWhy}) wieldLoc={s.PickedWeaponWieldLoc} ammoWielded={s.HasWieldedAmmoFlag}");
        ChatLine($"[RynthAi] busy={s.BusyCount}  sinceAtk={sinceAttackS:F1}s sinceStance={sinceStanceS:F1}s sinceEquip={sinceEquipS:F1}s sinceLost={sinceLostS:F1}s");
        ChatLine($"[RynthAi] script {s.ScriptHook}");

        // Dump every cache-known object that's wielded — using Host.TryGetObjectWielderInfo
        // as the authoritative check, not the cached WieldedLocation/Wielder fields
        // (those stay 0 for items that arrived via OnCreateObject and never went
        // through the GetDirectInventory walk).
        if (_objectCache != null)
        {
            uint pid = Host.GetPlayerId();
            int wieldedShown = 0;
            int totalKnown   = 0;
            ChatLine("[RynthAi] wielded items:");
            foreach (var item in _objectCache.AllKnownObjects())
            {
                totalKnown++;

                int  locInq   = item.Values(LongValueKey.CurrentWieldedLocation, 0);
                int  locCache = item.WieldedLocation;
                int  locApi   = 0;
                bool apiHit   = false;
                if (Host.HasGetObjectWielderInfo
                    && Host.TryGetObjectWielderInfo(unchecked((uint)item.Id), out uint wlder, out uint locFromApi)
                    && wlder == pid && locFromApi > 0)
                {
                    locApi = unchecked((int)locFromApi);
                    apiHit = true;
                }

                if (!apiHit && locInq <= 0 && locCache <= 0) continue;

                uint flags = 0;
                if (Host.HasGetItemType) Host.TryGetItemType(unchecked((uint)item.Id), out flags);
                ChatLine($"[RynthAi]   0x{(uint)item.Id:X8} '{item.Name}' apiLoc={locApi} inqLoc={locInq} cacheLoc={locCache} typeFlags=0x{flags:X}");
                if (++wieldedShown >= 25) { ChatLine("[RynthAi]   …truncated"); break; }
            }
            ChatLine($"[RynthAi]   ({wieldedShown} wielded shown of {totalKnown} cache-known objects)");
        }

        if (_buffManager != null)
        {
            var b = _buffManager.GetStateSnapshot();
            double sinceCastS = b.LastCastAttempt == DateTime.MinValue ? -1 : (now - b.LastCastAttempt).TotalSeconds;
            ChatLine($"[RynthAi] buff: enable={b.EnableBuffing} needsBuff={b.NeedsAnyBuffNow} pendingSpell={b.PendingSpellId} sinceCast={sinceCastS:F1}s");
            ChatLine($"[RynthAi] buff: forceRebuff={b.IsForceRebuffing} healSelf={b.IsHealingSelf} restam={b.IsRechargingStamina} remana={b.IsRechargingMana}");
            ChatLine($"[RynthAi] buff: ramTimers={b.RamBuffTimerCount} itemTimers={b.ItemSpellTimerCount} hp={b.HealthPct}% mana={b.ManaPct}% stam={b.StaminaPct}%");
        }

        Host.Log($"[RynthAi combat] macro={s.IsMacroRunning} ec={s.EnableCombat} ba={s.BotAction} " +
                 $"mode_cached={cachedMode} mode_live={liveMode} desired={desiredMode} " +
                 $"active=0x{(uint)s.ActiveTargetId:X8} locked=0x{(uint)s.LockedTargetId:X8} facing={s.FacingTarget} " +
                 $"scanned={s.ScannedCount} closest=0x{(uint)s.ClosestScannedId:X8} '{s.ClosestScannedName}' @ {s.ClosestScannedDist:F1}yd " +
                 $"weapon=0x{(uint)s.PickedWeaponId:X8} '{s.PickedWeaponName}' wieldLoc={s.PickedWeaponWieldLoc} " +
                 $"busy={s.BusyCount} sinceAtk={sinceAttackS:F1}s sinceStance={sinceStanceS:F1}s sinceEquip={sinceEquipS:F1}s sinceLost={sinceLostS:F1}s");
    }

    /// <summary>
    /// /ra start | /ra stop | /ra pause [sec] | /ra resume — macro control from chat.
    /// Until now the only way to start or stop a box was the ImGui button, which is
    /// unreachable on a background box (and on the phone-remote path, which already
    /// had this via TogglePanelMacro). Routed through the same TogglePanelMacro the
    /// button and the remote use, so there's still one write path for the flag.
    /// </summary>
    private void HandleMacroRunCommand(string verb, string[] parts)
    {
        var dash = _dashboard;
        if (dash == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        bool running = dash.Settings.IsMacroRunning;

        switch (verb)
        {
            case "start":
            case "resume":
                _macroResumeAt = 0;
                if (running) { ReplyOk("[RynthAi] Macro already RUNNING."); return; }
                if (RefuseMacroStartForVTank()) return;   // one bot per client (Decal bridge)
                dash.TogglePanelMacro();
                ReplyOk("[RynthAi] Macro STARTED.");
                return;

            case "stop":
                _macroResumeAt = 0;
                if (!running) { ReplyOk("[RynthAi] Macro already STOPPED."); return; }
                dash.TogglePanelMacro();
                // Kill autorun now rather than waiting out the nav engine's stop
                // debounce — a /ra stop is usually issued because the box is
                // running somewhere you don't want it to.
                _navigationEngine?.Stop();
                ReplyOk("[RynthAi] Macro STOPPED.");
                return;

            case "pause":
            {
                int sec = 60;
                if (parts.Length >= 3 && int.TryParse(parts[2], out int parsed))
                    sec = Math.Clamp(parsed, 1, 3600);
                if (running)
                {
                    dash.TogglePanelMacro();
                    _navigationEngine?.Stop();
                }
                _macroResumeAt = CorpseNowMs + sec * 1000L;
                ReplyOk($"[RynthAi] Macro PAUSED — auto-resume in {sec}s ('/ra start' to resume now, '/ra stop' to cancel).");
                return;
            }
        }
    }

    /// <summary>/ra navstate — dump the nav engine's snapshot. Nav is the other half of
    /// the "bot just stands there" class and had no state surface of its own.</summary>
    private void HandleNavStateCommand()
    {
        if (_navigationEngine == null) { ReplyFail("[RynthAi] Navigation engine not ready."); return; }
        var n = _navigationEngine.GetStateSnapshot();

        ChatLine("[RynthAi] === Nav State ===");
        ChatLine($"[RynthAi] macro={n.IsMacroRunning}  enableNav={n.EnableNavigation}  botAction='{n.BotAction}'");
        ChatLine($"[RynthAi] route={n.RouteType} pts={n.PointCount}  idx={n.Index} ({n.PointType})  linearDir={n.LinearDir}");
        ChatLine($"[RynthAi] dist={(double.IsNaN(n.DistYd) ? "n/a" : n.DistYd.ToString("0.0") + "yd")}"
                 + $"  headingErr={(double.IsNaN(n.HeadingErrDeg) ? "n/a" : n.HeadingErrDeg.ToString("+0.0;-0.0") + "°")}"
                 + $"  sinceSteer={n.MsSinceSteer}ms");
        ChatLine($"[RynthAi] moving={n.MovingForward} turning={n.Turning} stopped={n.Stopped}");
        ChatLine($"[RynthAi] pause={n.InPause} ({n.PauseRemainMs}ms)  portal={n.PortalState}");
        ChatLine($"[RynthAi] recovery={n.InRecovery} kind={n.RecoveryKind} remain={n.RecoveryRemainMs}ms  stuckCount={n.StuckCount}");
        ChatLine($"[RynthAi] way back: {n.Recovery}  (tries at this waypoint: {n.DetourAttempts})");
        if (n.FollowMode)
            ChatLine($"[RynthAi] follow: target=0x{n.FollowTargetId:X8}");
        ChatLine($"[RynthAi] status: {n.StatusLine}");

        Host.Log($"[RynthAi navstate] macro={n.IsMacroRunning} enableNav={n.EnableNavigation} ba='{n.BotAction}' "
                 + $"route={n.RouteType} pts={n.PointCount} idx={n.Index}({n.PointType}) dir={n.LinearDir} "
                 + $"dist={n.DistYd:F1} err={n.HeadingErrDeg:F1} sinceSteer={n.MsSinceSteer}ms "
                 + $"moving={n.MovingForward} turning={n.Turning} stopped={n.Stopped} "
                 + $"pause={n.InPause}/{n.PauseRemainMs}ms portal={n.PortalState} "
                 + $"recovery={n.InRecovery}/{n.RecoveryKind}/{n.RecoveryRemainMs}ms stuck={n.StuckCount} "
                 + $"wayBack='{n.Recovery}' tries={n.DetourAttempts}");
    }

    /// <summary>/ra salvstate — dump the salvage FSM snapshot: which phase, what's queued,
    /// where the combine sweep is, and the last thing that failed.</summary>
    private void HandleSalvageStateCommand()
    {
        if (_salvageManager == null) { ReplyFail("[RynthAi] Salvage manager not ready."); return; }
        var v = _salvageManager.GetStateSnapshot();

        ChatLine("[RynthAi] === Salvage State ===");
        ChatLine($"[RynthAi] panelApi={v.HasPanelApi}  combineEnabled={v.EnableCombine}");
        ChatLine($"[RynthAi] withoutPanel={(v.DirectOn ? "on" : "off")}{(v.DirectOffReason.Length > 0 ? $" ({v.DirectOffReason})" : "")}  directApi={v.HasDirectApi}  requests={v.DirectRequests}  panelFallbacks={v.DirectFallbacks}");
        ChatLine($"[RynthAi] phase={v.Phase} readyIn={v.PhaseReadyInMs}ms  queue={v.QueueCount}  panelEverOpened={v.PanelEverOpened}");
        ChatLine($"[RynthAi] batch={v.BatchSize} first=0x{v.CurrentItemId:X8}  ust=0x{v.CurrentUstId:X8}  tracked={v.TrackedItems}");
        ChatLine($"[RynthAi] items: {v.ItemsSalvaged} salvaged in {v.Batches} batch(es), {v.ItemsSkipped} skipped (reasons in the log)");
        ChatLine($"[RynthAi] combine: phase={v.CombinePhase} grp={v.CombineGroupIdx + 1}/{v.CombineGroupCount} addIdx={v.CombineAddIdx} openAttempts={v.CombineOpenAttempts}");
        ChatLine($"[RynthAi] combine: pendingScan={v.PendingCombineScan} sinceSweep={(v.MsSinceCombineSweep < 0 ? "never" : (v.MsSinceCombineSweep / 1000) + "s")}");
        ChatLine($"[RynthAi] retries: tracked={v.RetryTrackedItems} backoffIn={v.RetryBackoffInMs}ms");
        ChatLine($"[RynthAi] session: groupsOk={v.GroupsSucceeded} groupsFail={v.GroupsFailed} bagsMerged={v.BagsMerged}");
        ChatLine(v.LastError.Length > 0
            ? $"[RynthAi] lastError: '{v.LastError}' ({v.MsSinceLastError / 1000}s ago)"
            : "[RynthAi] lastError: none");

        Host.Log($"[RynthAi salvstate] panelApi={v.HasPanelApi} directApi={v.HasDirectApi} direct={v.DirectOn}/'{v.DirectOffReason}' req={v.DirectRequests} fb={v.DirectFallbacks} combine={v.EnableCombine} phase={v.Phase}/{v.PhaseReadyInMs}ms "
                 + $"queue={v.QueueCount} batch={v.BatchSize} item=0x{v.CurrentItemId:X8} ust=0x{v.CurrentUstId:X8} tracked={v.TrackedItems} salvaged={v.ItemsSalvaged} skipped={v.ItemsSkipped} "
                 + $"cphase={v.CombinePhase} grp={v.CombineGroupIdx}/{v.CombineGroupCount} add={v.CombineAddIdx} "
                 + $"openAttempts={v.CombineOpenAttempts} pendingScan={v.PendingCombineScan} sinceSweep={v.MsSinceCombineSweep}ms "
                 + $"retries={v.RetryTrackedItems} backoff={v.RetryBackoffInMs}ms ok={v.GroupsSucceeded} fail={v.GroupsFailed} "
                 + $"merged={v.BagsMerged} lastErr='{v.LastError}'@{v.MsSinceLastError}ms");
    }

    private void HandleDumpInventoryCommand()
    {
        Host.Log("[RynthAi dumpinv] ENTRY");
        if (_objectCache == null) { ReplyFail("[RynthAi] Cache not ready."); return; }
        uint playerId = Host.GetPlayerId();
        Host.Log($"[RynthAi dumpinv] playerId=0x{playerId:X8}");
        if (playerId == 0) { ReplyFail("[RynthAi] Not logged in."); return; }

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
            ReplyFail("[RynthAi] GetContainerContents API not available.");
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
            ReplyFail("[RynthAi] Usage: /ra settings loadchar <name> | /ra settings savechar <name>");
            return;
        }

        string sub = parts[2].ToLower();
        if (sub != "loadchar" && sub != "savechar")
        {
            ReplyFail("[RynthAi] Usage: /ra settings loadchar <name> | /ra settings savechar <name>");
            return;
        }

        string name = string.Join(" ", parts, 3, parts.Length - 3).Trim();
        if (string.IsNullOrEmpty(name))
        {
            ReplyFail($"[RynthAi] Usage: /ra settings {sub} <name>");
            return;
        }

        if (_dashboard == null) { ReplyFail("[RynthAi] Dashboard not ready."); return; }

        string result = sub == "loadchar"
            ? _dashboard.LoadProfile(name)
            : _dashboard.SaveAsProfile(name);
        ReplyResult($"[RynthAi] {result}");
    }

    private void HandleClearBusyCommand()
    {
        int before = Host.HasGetBusyState ? Host.GetBusyState() : -1;
        // You asked for it: forget what the pacing waits on too (ActionPacer).
        RynthCore.Plugin.Shared.ActionPacer.Forget("/ra clearbusy");
        if (Host.HasForceResetBusyCount)
            Host.ForceResetBusyCount();
        if (Host.HasStopCompletely)
            Host.StopCompletelyBy("Command");
        // Reset all tracked counts — engine, plugin, combat, buff
        _busyCount = 0;
        _busyCountLastIncrementAt = 0;
        _busyCountBecamePositiveAt = 0;
        if (_combatManager != null) _combatManager.BusyCount = 0;
        if (_buffManager != null)   _buffManager.BusyCount = 0;
        int after = Host.HasGetBusyState ? Host.GetBusyState() : -1;
        ReplyOk($"[RynthAi] Busy state cleared (was {before} → {after})");
    }

    // Broader reset for the "AC wedged with item-action notice" failure mode
    // that /ra clearbusy can't fix. The notice comes from two client globals
    // outside the CommandInterpreter / m_cBusy state that clearbusy targets:
    // AC's pending-item-request slot ("one item at a time") and its attacking
    // flag ("while attacking"); /rc unlockactions (engine) clears both.
    private void HandlePanicCommand()
    {
        int before = Host.HasGetBusyState ? Host.GetBusyState() : -1;
        ReplyOk("[RynthAi] PANIC — full AC state reset");
        RynthCore.Plugin.Shared.ActionPacer.Forget("/ra panic");

        if (Host.HasCancelAttack)      Host.CancelAttack();
        if (Host.HasChangeCombatMode)  Host.ChangeCombatModeFor(CombatMode.NonCombat, "Command", "/ra panic", force: true);
        if (Host.HasStopCompletely)    Host.StopCompletelyBy("Command");
        if (Host.HasForceResetBusyCount) Host.ForceResetBusyCount();
        // The item-action lock itself is not the busy count: AC refuses every use,
        // equip and move while its pending-item-request slot or attacking flag is
        // set, and nothing above touches those. The engine opens them (its
        // /rc unlockactions; an older engine just logs the unknown command).
        if (Host.HasInvokeChatParser)  Host.InvokeChatParser("/rc unlockactions");

        _busyCount = 0;
        _busyCountLastIncrementAt = 0;
        _busyCountBecamePositiveAt = 0;
        if (_combatManager != null) _combatManager.BusyCount = 0;
        if (_buffManager != null)   _buffManager.BusyCount = 0;

        int after = Host.HasGetBusyState ? Host.GetBusyState() : -1;
        ReplyOk($"[RynthAi] Panic complete (busy {before} → {after}). If items still won't use or equip, /rc actionstate shows why.");
    }

    private void HandleForceBuff()
    {
        if (_buffManager == null) { ReplyFail("[RynthAi] Buff manager not ready."); return; }
        _buffManager.RequestForceFullRebuff();   // chat commands run on AC's thread; applied on the pump
        Reply.Answered();   // the pump says "Starting Force Rebuff..." on its next heartbeat
    }

    // /ra buff force | cancel | skipped: the same actions as /ra forcebuff, /ra cancelforcebuff
    // and the Force Buff button, plus the buffs the "Skip buffs this character can't use"
    // setting is leaving out right now.
    private void HandleBuffCommand(string[] parts)
    {
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        switch (sub)
        {
            case "force":  HandleForceBuff(); break;
            case "cancel": HandleCancelForceBuff(); break;
            case "skipped":
                if (_buffManager == null) { ReplyFail("[RynthAi] Buff manager not ready."); break; }
                _buffManager.RequestSkipReport();   // chat commands run on AC's thread; the pump builds the list and answers
                Reply.Answered();
                break;
            default:
                ChatLine("[RynthAi] /ra buff force    - recast every buff now, whatever time is left");
                ChatLine("[RynthAi] /ra buff cancel   - cancel an in-progress force buff");
                ChatLine("[RynthAi] /ra buff skipped  - buffs left out because this character can't use them");
                break;
        }
    }

    private void HandleCancelForceBuff()
    {
        if (_buffManager == null) { ReplyFail("[RynthAi] Buff manager not ready."); return; }
        _buffManager.RequestCancelBuffing();
        Reply.Answered();   // the pump says "Sequence cancelled." on its next heartbeat
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
        if (_objectCache == null) { ReplyFail("[RynthAi] Cache not ready."); return; }
        if (!Host.HasGetObjectPosition) { ReplyFail("[RynthAi] GetObjectPosition not available."); return; }
        if (!Host.TryGetPlayerPose(out uint playerCell, out _, out _, out _, out _, out _, out _, out _)) { ReplyFail("[RynthAi] Can't get player cell."); return; }

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

            // The live walk above can miss carried items: off AC's main thread it only
            // knows names from the engine's name snapshot, and an item missing from it
            // drops out of the walk. Fall back to every object the cache knows and ask
            // the client who holds it — "/ub usei X" said "Not found" for an item that
            // "/ub use X" found, because the cache had it filed under the landscape.
            foreach (var wo in _objectCache.AllKnownObjects())
            {
                if ((partial
                        ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                        : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                    && IsCarriedByPlayer(wo.Id))
                    return wo;
            }
        }

        if (land)
        {
            // Multiple landscape objects can share an exact name (e.g. 4 identically
            // named corpses in a hive). Prefer the NEAREST match — the one the player
            // is on / closest to — over an arbitrary first hit that could be a far
            // corpse out of use range. BUT a corpse's live position can be momentarily
            // unreadable (TryGetObjectPosition fails → Distance returns double.MaxValue),
            // so fall back to the first match rather than reporting "not found".
            WorldObject? best = null, anyMatch = null;
            double bestDist = double.MaxValue;
            foreach (var wo in _objectCache.GetLandscapeObjects())
            {
                bool match = partial
                    ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                anyMatch ??= wo;
                double dist = _playerId != 0 ? _objectCache.Distance((int)_playerId, wo.Id) : double.MaxValue;
                if (dist < bestDist) { bestDist = dist; best = wo; }
            }
            if (best != null || anyMatch != null) return best ?? anyMatch;
        }

        return null;
    }

    /// <summary>True when the client says the player holds this object: worn or
    /// wielded, in the main pack, or in a side pack (item → pack → player).</summary>
    private bool IsCarriedByPlayer(int objectId)
    {
        if (_playerId == 0 || !Host.HasGetObjectOwnershipInfo) return false;
        uint id = unchecked((uint)objectId);
        for (int depth = 0; depth < 3; depth++)
        {
            if (!Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _)) return false;
            if (wielder == _playerId || container == _playerId) return true;
            if (container == 0) return false;
            id = container;
        }
        return false;
    }

    /// <summary>After an exact-name miss, name up to three items whose names contain the
    /// typed text and point at the partial-match form of the command ("usei" → "useip").
    /// Exact is the default, like UtilityBelt, and "/ub usei round" read as a bug.</summary>
    private void SuggestPartialMatches(string name, bool inv, bool land, string verb)
    {
        if (_objectCache == null || name.Length == 0) return;

        var names = new List<string>();
        bool Wants(WorldObject wo) =>
            names.Count < 3
            && wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
            && !names.Contains(wo.Name, StringComparer.OrdinalIgnoreCase);

        if (inv)
        {
            foreach (var wo in _objectCache.GetDirectInventory())
                if (Wants(wo)) names.Add(wo.Name);
            foreach (var wo in _objectCache.AllKnownObjects())
                if (Wants(wo) && IsCarriedByPlayer(wo.Id)) names.Add(wo.Name);
        }
        if (land)
        {
            foreach (var wo in _objectCache.GetLandscapeObjects())
                if (Wants(wo)) names.Add(wo.Name);
        }

        if (names.Count > 0)
            ChatLine($"[RynthAi]   Did you mean {string.Join(", ", names.Select(n => $"'{n}'"))}? {verb}p matches part of a name.");
    }

    private void HandleUseCommand(string[] parts, bool inv, bool land, bool partial)
    {
        if (parts.Length < 3)
        {
            ReplyFail("[RynthAi] Usage: /ra use[i|l][p] <name> [on <name2>]");
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
            if (src == null) { ReplyFail($"[RynthAi] Not found: '{srcName}'"); return; }
            if (tgt == null) { ReplyFail($"[RynthAi] Not found: '{tgtName}'"); return; }
            Host.UseOnFor((uint)src.Id, (uint)tgt.Id, "Command", "/ra use X on Y", UseKind.Asked);
            ReplyOk($"[RynthAi] UseObjectOn: {src.Name} (0x{src.Id:X}) → {tgt.Name} (0x{tgt.Id:X})");
            return;
        }

        var obj = FindObject(argStr.Trim(), inv, land, partial);
        if (obj == null)
        {
            ReplyFail($"[RynthAi] Not found: '{argStr.Trim()}'");
            if (!partial) SuggestPartialMatches(argStr.Trim(), inv, land, parts[1].ToLowerInvariant());
            return;
        }
        Host.UseFor((uint)obj.Id, "Command", "/ra use", UseKind.Asked);
        ReplyOk($"[RynthAi] UseObject: {obj.Name} (0x{obj.Id:X})");
    }

    private void HandleSelectCommand(string[] parts, bool inv, bool land, bool partial)
    {
        if (parts.Length < 3)
        {
            ReplyFail("[RynthAi] Usage: /ra select[i|l][p] <name>");
            return;
        }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        var obj = FindObject(name, inv, land, partial);
        if (obj == null) { ReplyFail($"[RynthAi] Not found: '{name}'"); return; }
        Host.SelectItem((uint)obj.Id);
        ReplyOk($"[RynthAi] Selected: {obj.Name} (0x{obj.Id:X})");
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

    /// <summary>
    /// The most stacks one give-all command (/ra givea*, /ub give) queues. A line that matches more
    /// than this gives the first MaxGiveAllPerCommand and says so; run it again for the rest.
    /// </summary>
    internal const int MaxGiveAllPerCommand = 500;

    /// <summary>
    /// Hands <paramref name="amount"/> of an item to a player or NPC with AC's give action
    /// (Event_GiveObjectRequest) when the engine has it; MoveItemExternal is move-to-container
    /// and silently does nothing for an NPC. amount 0 or the whole stack = the whole object.
    /// </summary>
    internal bool GiveItem(uint itemId, uint targetId, int amount, int stackSize)
    {
        if (Host.HasGiveObjectTo)
            return Host.GiveObjectTo(itemId, targetId, amount <= 0 || amount >= stackSize ? 0 : amount);
        return Host.MoveItemExternal(itemId, targetId, Math.Max(1, amount));
    }

    /// <summary>
    /// Carried items a give may hand over, in pack order: never worn or wielded gear. Walks the
    /// live inventory, then every known object the client says the player carries (the live walk
    /// can miss items off AC's main thread, as FindObject notes).
    /// </summary>
    private List<WorldObject> GiveCandidates(Func<string, bool> nameMatches)
    {
        var found = new List<WorldObject>();
        var seen = new HashSet<int>();
        if (_objectCache == null) return found;
        foreach (var wo in _objectCache.GetDirectInventory(forceRefresh: true))
            if (nameMatches(wo.Name) && !WorldObjectCache.IsWieldedByPlayer(Host, wo) && seen.Add(wo.Id))
                found.Add(wo);
        foreach (var wo in _objectCache.AllKnownObjects())
            if (!seen.Contains(wo.Id) && nameMatches(wo.Name) && IsCarriedByPlayer(wo.Id)
                && !WorldObjectCache.IsWieldedByPlayer(Host, wo) && seen.Add(wo.Id))
                found.Add(wo);
        return found;
    }

    private void HandleGiveCommand(string[] parts, GiveItemMatch itemMatch, bool partialPlayer, bool allItems = false)
    {
        if (_objectCache == null) { ReplyFail("[RynthAi] Object cache not ready."); return; }
        if (!Host.HasMoveItemExternal)  { ReplyFail("[RynthAi] MoveItemExternal not available."); return; }

        string argStr = string.Join(" ", parts, 2, parts.Length - 2).Trim();

        // Split on last " to " so player names that contain "to" still work
        int toIdx = argStr.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIdx < 0)
        {
            ReplyFail("[RynthAi] Usage: /ra give[a][p|P|pp|r] [count] <item> to <player>");
            return;
        }

        string itemPart   = argStr.Substring(0, toIdx).Trim();
        string playerPart = argStr.Substring(toIdx + 4).Trim();

        if (string.IsNullOrEmpty(itemPart) || string.IsNullOrEmpty(playerPart))
        {
            ReplyFail("[RynthAi] Item name and player name must not be empty.");
            return;
        }

        // allItems: give every matching stack; otherwise honour optional leading count (default 1)
        int maxCount = allItems ? int.MaxValue : 1;
        bool countGiven = false;
        string[] itemTokens = itemPart.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!allItems && itemTokens.Length >= 2 && int.TryParse(itemTokens[0], out int parsedCount) && parsedCount > 0)
        {
            maxCount = parsedCount;
            countGiven = true;
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
        if (target == null) { ReplyFail($"[RynthAi] Target not found: '{playerPart}'"); return; }

        // Compile regex if needed
        Regex? rx = null;
        if (itemMatch == GiveItemMatch.Regex)
        {
            try   { rx = new Regex(itemPart, RegexOptions.IgnoreCase); }
            catch { ReplyFail($"[RynthAi] Invalid regex: {itemPart}"); return; }
        }

        // Collect matching carried stacks (never worn or wielded gear)
        var matches = GiveCandidates(name => itemMatch switch
        {
            GiveItemMatch.Exact   => string.Equals(name, itemPart, StringComparison.OrdinalIgnoreCase),
            GiveItemMatch.Partial => name.IndexOf(itemPart, StringComparison.OrdinalIgnoreCase) >= 0,
            GiveItemMatch.Regex   => rx!.IsMatch(name),
            _                     => false,
        });
        if (!allItems && matches.Count > maxCount) matches.RemoveRange(maxCount, matches.Count - maxCount);

        if (matches.Count == 0) { ReplyFail($"[RynthAi] No items found matching '{itemPart}'"); return; }

        if (allItems)
        {
            // Every match goes, except what must never be handed over: equipped gear, the items
            // RynthAi uses (Weapons and Consumables lists, monster-rule weapons), attuned and retained
            // items, and, for a line a running meta sends, whatever the loot profile keeps. One log
            // line per stack queued or skipped, and a cap on how many one command queues.
            bool fromMeta = MetaCommandActive;
            var toGive = new List<WorldObject>();
            int protectedCount = 0;
            foreach (var item in matches)
            {
                string? why = ItemProtectionReason(item, includeLootKeep: fromMeta);
                if (why != null)
                {
                    protectedCount++;
                    Log($"[Give] skipped 0x{(uint)item.Id:X8} '{item.Name}': {why}");
                    continue;
                }
                toGive.Add(item);
            }
            bool capped = toGive.Count > MaxGiveAllPerCommand;
            if (capped)
            {
                Log($"[Give] '{itemPart}' matched {toGive.Count} stacks; the cap is {MaxGiveAllPerCommand}, so only the first {MaxGiveAllPerCommand} are queued.");
                toGive.RemoveRange(MaxGiveAllPerCommand, toGive.Count - MaxGiveAllPerCommand);
            }
            foreach (var item in toGive)
            {
                EnqueueGive((uint)item.Id, (uint)target.Id, 0);   // 0 = the whole stack, whatever it reads at send time
                Log($"[Give] queued 0x{(uint)item.Id:X8} '{item.Name}' x{Math.Max(1, item.Values(LongValueKey.StackCount, 1))} -> {target.Name}{(fromMeta ? " (from a running meta)" : "")}");
            }
            string skippedNote = protectedCount > 0 ? $", {protectedCount} protected stack(s) skipped" : "";
            string capNote = capped ? $", capped at {MaxGiveAllPerCommand}: run it again for the rest" : "";
            if (toGive.Count == 0)
                ReplyFail($"[RynthAi] Nothing to give: every item matching '{itemPart}' is protected ({protectedCount} skipped).");
            else
                ReplyOk($"[RynthAi] Queued {toGive.Count} stack(s) matching '{itemPart}' → {target.Name}{skippedNote}{capNote}");
        }
        else if (countGiven)
        {
            // The count is how many items to give. It used to cap only how many stacks
            // were collected, and then the whole first stack went: "/ra give 5 Pyreal
            // to X" handed over the entire pile. Give min(count, stack) from each stack
            // until the count is met (the give carries the amount; the server splits).
            int remaining = maxCount;
            var gives = new List<(WorldObject Item, int Amount)>();
            foreach (var item in matches)
            {
                if (remaining <= 0) break;
                int stackSize = Math.Max(1, item.Values(LongValueKey.StackCount, 1));
                int amount = Math.Min(stackSize, remaining);
                gives.Add((item, amount));
                remaining -= amount;
            }
            if (gives.Count == 1)
            {
                // Through the queue: an NPC that takes one item per give needs the repeat-until-met loop.
                EnqueueGive((uint)gives[0].Item.Id, (uint)target.Id, gives[0].Amount);
                ReplyOk($"[RynthAi] Giving {gives[0].Amount} x '{gives[0].Item.Name}' to {target.Name}");
            }
            else
            {
                foreach (var (item, amount) in gives)
                    EnqueueGive((uint)item.Id, (uint)target.Id, amount);
                ReplyOk($"[RynthAi] Queued {maxCount - remaining} item(s) from {gives.Count} stack(s) matching '{itemPart}' → {target.Name}");
            }
        }
        else
        {
            var item = matches[0];
            int stackSize = Math.Max(1, item.Values(LongValueKey.StackCount, 1));
            GiveItem((uint)item.Id, (uint)target.Id, stackSize, stackSize);
            ReplyOk($"[RynthAi] Giving '{item.Name}' to {target.Name}");
        }
    }

    /// <summary>
    /// Items a profile give must skip: GetDirectInventory includes worn and wielded
    /// gear, and a broad Keep rule (by object class) would hand over the character's
    /// armor, its weapon in hand, or a Weapons-tab weapon waiting in the pack.
    /// </summary>
    private bool IsGiveProfileExempt(WorldObject item)
    {
        if (WorldObjectCache.IsWieldedByPlayer(Host, item)) return true;
        var rules = _dashboard?.Settings?.ItemRules;
        if (rules != null)
            foreach (var rule in rules)
                if (rule.Id == item.Id) return true;
        return false;
    }

    private void HandleGiveProfileCommand(string[] parts, bool partialPlayer)
    {
        if (_objectCache == null) { ReplyFail("[RynthAi] Object cache not ready."); return; }
        if (!Host.HasMoveItemExternal)  { ReplyFail("[RynthAi] MoveItemExternal not available."); return; }

        string argStr = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        int toIdx = argStr.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIdx < 0) { ReplyFail("[RynthAi] Usage: /ra ig[p] <lootProfile> to <player>"); return; }

        string profileArg = argStr.Substring(0, toIdx).Trim();
        string playerPart = argStr.Substring(toIdx + 4).Trim();

        // Resolve profile path — bare name resolved from ItemGiver dir, .utl extension added if needed
        string itemGiverDir = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "ItemGiver");
        string profilePath = System.IO.Path.IsPathRooted(profileArg)
            ? profileArg
            : System.IO.Path.Combine(itemGiverDir, profileArg);

        bool isJson = profilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (!isJson && !profilePath.EndsWith(".utl", StringComparison.OrdinalIgnoreCase))
            profilePath += ".utl";

        if (!System.IO.File.Exists(profilePath)) { ReplyFail($"[RynthAi] Profile not found: {profilePath}"); return; }

        // Find target in landscape by name (player, NPC, container, etc.)
        WorldObject? target = null;
        foreach (var wo in _objectCache.GetLandscape())
        {
            bool match = partialPlayer
                ? wo.Name.IndexOf(playerPart, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(wo.Name, playerPart, StringComparison.OrdinalIgnoreCase);
            if (match) { target = wo; break; }
        }
        if (target == null) { ReplyFail($"[RynthAi] Target not found: '{playerPart}'"); return; }

        // Load profile and enqueue all matching items through the throttled give queue,
        // mirroring the givea* commands so the server isn't flooded with MoveItemExternal calls.
        int queued = 0;
        if (isJson)
        {
            var nativeProfile = RynthCore.Loot.LootProfile.Load(profilePath);
            foreach (var item in _objectCache.GetDirectInventory(forceRefresh: true))
            {
                if (IsGiveProfileExempt(item)) continue;
                var (action, _) = Loot.LootEvaluator.Classify(nativeProfile, item, null);
                if (action != RynthCore.Loot.LootAction.Keep) continue;
                EnqueueGive((uint)item.Id, (uint)target.Id, 0);   // 0 = the whole stack
                queued++;
            }
        }
        else
        {
            var vtProfile = VTankLootParser.Load(profilePath);
            var lootCtx   = new VTankLootContext(Host, Host.GetPlayerId()) { Cache = _objectCache };
            foreach (var item in _objectCache.GetDirectInventory(forceRefresh: true))
            {
                if (IsGiveProfileExempt(item)) continue;
                VTankLootRule? matched = null;
                foreach (var rule in vtProfile.Rules)
                    if (VTankLootEvaluator.Match(rule, item, lootCtx)) { matched = rule; break; }
                if (matched == null || (matched.Action != VTankLootAction.Keep && matched.Action != VTankLootAction.KeepUpTo)) continue;
                EnqueueGive((uint)item.Id, (uint)target.Id, 0);   // 0 = the whole stack
                queued++;
            }
        }

        ChatLine(queued > 0
            ? $"[RynthAi] Queued {queued} stack(s) from profile '{System.IO.Path.GetFileName(profilePath)}' → {target.Name}"
            : $"[RynthAi] No items in inventory matched profile '{System.IO.Path.GetFileName(profilePath)}'");
    }

    /// <summary>
    /// /ra dunnav &lt;NS&gt; &lt;EW&gt;
    /// Builds a dungeon cell graph for the player's current landblock, runs A* to
    /// the nearest cell to the given destination, and wires the resulting route into
    /// NavigationEngine (Once type). Works in any dungeon without a pre-recorded nav file.
    /// </summary>
    private void HandleDungeonNavCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Settings not ready."); return; }

        if (parts.Length < 4 ||
            !double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double destNS) ||
            !double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double destEW))
        {
            ReplyFail("[RynthAi] Usage: /ra dunnav <NS> <EW>  (e.g. /ra dunnav 12.5 -34.2)");
            return;
        }

        if (!Host.TryGetPlayerPose(out uint playerCell, out _, out _, out float playerWZ, out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi] Cannot get player position."); return;
        }

        uint landblockKey = playerCell >> 16;
        bool isDungeon    = (playerCell & 0xFFFF) >= 0x0100;
        if (!isDungeon)
        {
            ReplyFail("[RynthAi] dunnav requires you to be inside a dungeon (cell >= 0x0100).");
            return;
        }

        var cellDat = _raycast?.GeometryLoader.CellDat;
        if (cellDat == null || !cellDat.IsLoaded)
        {
            ReplyFail("[RynthAi] Cell dat not loaded — raycast system must be initialized first."); return;
        }

        if (!NavCoordinateHelper.TryGetNavCoords(Host, out double playerNS, out double playerEW))
        {
            ReplyFail("[RynthAi] Cannot get player nav coordinates."); return;
        }

        ReplyOk($"[RynthAi] DunNav: building graph for landblock 0x{landblockKey:X4}...");

        var route = DungeonPathfinder.NavigateTo(
            landblockKey, cellDat,
            playerNS, playerEW, playerWZ,
            destNS,   destEW,
            out int nodeCount, out int pathLength,
            los: _raycast?.GeometryLoader?.DungeonLOS);

        if (route == null)
        {
            ReplyFail($"[RynthAi] DunNav: no walkable path found — destination may only be reachable via a drop (graph={nodeCount} nodes, dest={destNS:F2}N/{destEW:F2}E).");
            return;
        }

        if (!settings.IsMacroRunning && RefuseMacroStartForVTank()) return;   // one bot per client (Decal bridge)
        settings.IsMacroRunning   = true;
        settings.CurrentRoute     = route;
        settings.ActiveNavIndex   = 0;
        settings.EnableNavigation = true;

        ReplyOk($"[RynthAi] DunNav: {nodeCount} cells, path={pathLength} steps → navigating to {destNS:F2}N/{destEW:F2}E");
    }

    private void HandleDungeonNavPatrolCommand(string[] parts) => HandleDungeonNavPatrol();

    /// <summary>Takes the auto-generated patrol off the character: no patrol route, navigation off.</summary>
    private void StopPatrolForServer()
    {
        _dunPatrolActive = false;
        _dunPatrolRoute = null;
        var s = _dashboard?.Settings;
        if (s == null) return;
        s.EnableNavigation = false;
        s.CurrentRoute = new NavRouteParser();
        _navigationEngine?.Stop();
    }

    /// <summary>DreamWeave's Thread lines in chat ("The Thread Gem opens a way into ...", "[REALM] ... eph=1"):
    /// where the character is, for ServerPolicy. Logged when it changes.</summary>
    private void NoteServerPolicyChat(string text)
    {
        bool was = ServerPolicy.Run != null;
        uint cell = Host.HasGetPlayerPose && Host.TryGetPlayerPose(out uint c, out _, out _, out _, out _, out _, out _, out _) ? c : 0;
        if (!ServerPolicy.NoteChat(text, cell)) return;
        bool now = ServerPolicy.Run != null;
        if (now != was) Host.Log(now ? "[RynthAi] ServerPolicy: in a Thread (the server says so)." : "[RynthAi] ServerPolicy: out of the Thread.");
    }

    /// <summary>
    /// Builds a Circular hunt patrol covering every cell in the current dungeon landblock.
    /// Called from both /ra dunnav-patrol and the NavPanel "Dungeon Patrol" button.
    /// </summary>
    public void HandleDungeonNavPatrol() => BuildDunPatrol(isRebuild: false);

    /// <summary>The Dungeon Patrol settings (Settings > Navigation) as patrol options, in range.</summary>
    internal static DungeonPatrolOptions PatrolOptionsFrom(LegacyUiSettings s) => new DungeonPatrolOptions
    {
        Coverage          = (PatrolCoverage)Math.Clamp(s.DunPatrolCoverage, 0, (int)PatrolCoverage.Sweep),
        MaxDeadEndCells      = s.DunPatrolSkipDeadEnds,
        AvoidHazardFloors = s.DunPatrolAvoidFloors,
        WallClearance     = s.DunPatrolWallDistance,
        LookInRoomMeters  = s.DunPatrolLookInSize,
        SightMeters       = s.DunPatrolSightYd,
    }.Clamped();

    /// <summary>
    /// Builds (or rebuilds) the circular dungeon patrol route from the player's current
    /// cell, excluding all currently-known hazard cells, and installs it as the active
    /// route. When <paramref name="isRebuild"/> is true this was triggered by OnTick
    /// after a new hazard was sighted mid-patrol, or a Dungeon Patrol setting changed
    /// (<paramref name="settingsChanged"/>) — it reroutes silently (no fresh "patrol
    /// started" chatter, just a one-line note) and never flips IsMacroRunning.
    /// </summary>
    // One log line per patrol waypoint and a summary (diagnostics: which rule put each point
    // where it is, e.g. the patrol walking into small rooms it should only look into).
    private void LogPatrolWaypoints(NavRouteParser route, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geometry,
                                    IReadOnlySet<uint>? hazards, Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo> pointLog,
                                    uint playerCell, uint startCell)
    {
        try
        {
            Host.Log($"[RynthAi] DunNav-Patrol wp build: player cell {playerCell:X8}, start cell {startCell:X8}, {route.Points.Count} points, geometry {(geometry != null ? "on" : "off")}"
                     + (hazards is { Count: > 0 } ? $", hazards {string.Join(",", hazards.Where(c => c >> 16 == startCell >> 16).OrderBy(c => c).Select(c => (c & 0xFFFF).ToString("X4")))}" : ""));
            foreach (string line in DungeonPathfinder.DescribePatrol(route, graph, geometry, hazards, pointLog))
                Host.Log("[RynthAi] DunNav-Patrol " + line);
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] DunNav-Patrol wp log failed: {ex.Message}");
        }
    }

    private void BuildDunPatrol(bool isRebuild, bool settingsChanged = false, bool portalAppeared = false, bool followedIn = false)
    {
        // The one way into the dungeon patrol (the command, the dashboard button, a meta's "Patrol", the hazard
        // reroute): where a server's owners asked for it to be off (a Thread on DreamWeave) nothing is built (ServerPolicy).
        if (ServerPolicy.PatrolOff(Host))
        {
            if (_dunPatrolActive) StopPatrolForServer();
            if (!isRebuild) ChatLine(ServerPolicy.PatrolOffMessage);
            return;
        }
        var settings = _dashboard?.Settings;
        if (settings == null) { if (!isRebuild) ReplyFail("[RynthAi] Settings not ready."); return; }
        var patrolOptions = PatrolOptionsFrom(settings);

        if (!Host.TryGetPlayerPose(out uint playerCell, out float playerLocalX, out float playerLocalY,
                                   out float patrolWZ, out _, out _, out _, out _))
        {
            if (!isRebuild) ReplyFail("[RynthAi] Cannot get player position."); return;
        }

        uint landblockKey = playerCell >> 16;
        bool isDungeon    = (playerCell & 0xFFFF) >= 0x0100;
        if (!isDungeon)
        {
            // Left the dungeon (e.g. portalled out) — stop tracking; nothing to rebuild.
            _dunPatrolActive = false;
            if (!isRebuild) ReplyFail("[RynthAi] dunnav-patrol requires you to be inside a dungeon (cell >= 0x0100).");
            return;
        }

        var cellDat = _raycast?.GeometryLoader.CellDat;
        if (cellDat == null || !cellDat.IsLoaded)
        {
            if (!isRebuild) ReplyFail("[RynthAi] Cell dat not loaded — raycast system must be initialized first."); return;
        }

        if (!NavCoordinateHelper.TryGetNavCoords(Host, out double playerNS, out double playerEW))
        {
            if (!isRebuild) ReplyFail("[RynthAi] Cannot get player nav coordinates."); return;
        }

        var graph = DungeonPathfinder.GetGraph(landblockKey, cellDat);
        if (graph.Count == 0) { if (!isRebuild) ReplyFail("[RynthAi] DunNav-Patrol: dungeon graph is empty."); return; }

        // Prefer the actual cell the player is standing in. NearestCell-by-distance can
        // pick a same-floor cell across an interior wall when the player is hugging a
        // wall corner; the engine's own playerCell is authoritative.
        uint startCell;
        if (graph.ContainsKey(playerCell))
        {
            startCell = playerCell;
        }
        else
        {
            startCell = DungeonPathfinder.NearestCell(graph, playerNS, playerEW, patrolWZ);
            if (startCell == 0) { if (!isRebuild) ReplyFail("[RynthAi] DunNav-Patrol: cannot find starting cell."); return; }
        }

        // Pull in hazards discovered on previous visits to this dungeon so the route avoids
        // them from the first waypoint — hazards are static, so a dungeon we've already
        // explored is built clean without having to re-sight and reroute.
        _objectCache?.SeedHazardsFromStore(landblockKey);

        // Detector C: offline EnvCell hazard cells (lava/acid textures on a cell's walkable floor).
        // Catches the invisible-HotSpot majority the live weenie scan can't see — pure dat reads,
        // cached per landblock. No-op unless the user has flagged hazard textures (/ra hazard learnhere|texture).
        var portalDat = _raycast?.GeometryLoader?.PortalDat;
        HashSet<uint>? surfaceHazards = null;
        if (portalDat != null)
        {
            surfaceHazards = DungeonHazardSurfaces.ComputeHazardCells(landblockKey, _raycast?.GeometryLoader?.DungeonLOS, portalDat);
            if (surfaceHazards.Count > 0) _objectCache?.SeedSurfaceHazards(surfaceHazards);
        }

        // Settings "Avoid Lava/Acid Floors" off: the floor-texture cells stay walkable for the
        // patrol (the live set keeps them for everything else). Pools sighted or marked by hand
        // (DungeonHazardStore) are avoided either way.
        var hazards = DungeonPathfinder.PatrolHazards(_objectCache?.GetHazardCells(), surfaceHazards,
            patrolOptions.AvoidHazardFloors ? null : DungeonHazardStore.Load(landblockKey), patrolOptions);

        // Start-in-hazard evacuation: if the player is standing on an excluded (acid/lava) cell, build
        // the patrol from the nearest SAFE cell and prepend a lead-in waypoint so the first move walks
        // out of the hazard onto safe ground — instead of producing an empty, hazard-locked route.
        bool   evacuate = false;
        double evacNS = 0, evacEW = 0, evacZ = 0;
        if (hazards != null && hazards.Contains(startCell))
        {
            uint safeCell = DungeonPathfinder.NearestSafeCell(graph, startCell, hazards, patrolWZ);
            if (safeCell != 0 && graph.TryGetValue(safeCell, out var safeNode))
            {
                evacuate = true;
                evacNS = safeNode.NS; evacEW = safeNode.EW; evacZ = safeNode.Z / 240.0;
                startCell = safeCell;
            }
        }

        // The dungeon map geometry (doorways, floors, walls): doorways taken straight on and
        // big rooms crossed directly. Null (old cell walk) until DungeonLOS is up.
        var geometry = DungeonPathfinder.GetGeometry(landblockKey, cellDat, _raycast?.GeometryLoader?.DungeonLOS);
        // Portals (DungeonPathfinder.Portals.cs, 2026-10-23): the patrol keeps out of every portal the
        // client knows in this dungeon (Tiny Hive: it walked onto the Surface Portal). A character
        // standing in a portal's circle walks out first, and the patrol is built from where it gets
        // to (that cell, that side of the portal).
        var portals = PatrolPortalsIn(landblockKey);
        double playerWX = ((playerCell >> 24) & 0xFF) * 192.0 + playerLocalX, playerWY = ((playerCell >> 16) & 0xFF) * 192.0 + playerLocalY;
        (double X, double Y, double Z) startAt = (playerWX, playerWY, patrolWZ);
        NavPoint? walkOut = null;
        uint losCell = playerCell;
        float losX = playerLocalX, losY = playerLocalY, losZ = patrolWZ;
        List<PortalAvoidance>? portalReport = null;

        // Diagnostics: record which rule laid each waypoint (logged below, one line each).
        var pointLog = new Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo>(ReferenceEqualityComparer.Instance);
        DungeonPathfinder.PointLog = pointLog;
        NavRouteParser route;
        try
        {
            if (portals.Count > 0)
                walkOut = DungeonPathfinder.PortalWalkOut(geometry, portals, playerWX, playerWY, patrolWZ, patrolOptions.WallClearance);
            if (walkOut != null)
            {
                startAt = (DungeonGeometry.NavToWorld(walkOut.EW), DungeonGeometry.NavToWorld(walkOut.NS), walkOut.Z * 240.0);
                uint outCell = DungeonPathfinder.CellAt(graph, geometry, walkOut);
                if (graph.ContainsKey(outCell))
                {
                    if (!evacuate && (hazards == null || !hazards.Contains(outCell))) startCell = outCell;
                    losCell = outCell;
                    losX = (float)(startAt.X - ((outCell >> 24) & 0xFF) * 192.0);
                    losY = (float)(startAt.Y - ((outCell >> 16) & 0xFF) * 192.0);
                    losZ = (float)startAt.Z;
                }
            }
            route = DungeonPathfinder.BuildPatrolRoute(graph, startCell, hazards, geometry, patrolOptions, portals, startAt);
            portalReport = DungeonPathfinder.LastPortalAvoidance;
        }
        finally { DungeonPathfinder.PointLog = null; }

        // Lead the bot out of the hazard first.
        if (evacuate)
        {
            var lead = new NavPoint { Type = NavPointType.Point, NS = evacNS, EW = evacEW, Z = evacZ };
            route.Points.Insert(0, lead);
            pointLog[lead] = new DungeonPathfinder.PatrolPointInfo { Kind = "evacuate", Cell = startCell, Why = "start cell is a hazard" };
        }

        // Pick the first waypoint with a clear line of sight from the player. The patrol
        // builder emits its first waypoints at the doorway between startCell and its DFS-
        // chosen neighbor — that doorway can sit on the far wall of startCell, so a player
        // who logged in near the opposite wall faces a pillar or corner between them and
        // waypoint 0. Walking the list forward to the first LOS-clear point sidesteps the
        // "patrol immediately runs into a wall on login" failure mode. (From where the character
        // walks out of a portal's circle to, when it stands in one.)
        int startIdx = FindFirstLineOfSightWaypoint(route, losCell, losX, losY, losZ);
        // The way to that waypoint: out of a portal's circle first, round any portal in between.
        if (portals.Count > 0 && route.Points.Count > 0)
        {
            DungeonPathfinder.PointLog = pointLog;
            try { startIdx = LeadPatrolPastPortals(route, graph, geometry, portals, portalReport, walkOut, playerWX, playerWY, patrolWZ, startIdx, patrolOptions.WallClearance); }
            finally { DungeonPathfinder.PointLog = null; }
        }
        LogPatrolWaypoints(route, graph, geometry, hazards, pointLog, playerCell, startCell);
        LogPatrolPortals(portalReport, portals, route);

        // Empty-route guard: a single-cell / hazard-locked start yields no waypoints. Do NOT claim the
        // patrol started or enable nav — the bot would just stand in place (in the hazard) taking damage.
        if (route.Points.Count == 0)
        {
            _dunPatrolActive = false;
            if (!isRebuild)
                ReplyFail("[RynthAi] DunNav-Patrol: no walkable route from here — you're standing in or surrounded by a hazard. Move to safe ground and retry.");
            return;
        }

        if (!isRebuild)
        {
            if (!settings.IsMacroRunning && RefuseMacroStartForVTank())   // one bot per client (Decal bridge)
            {
                _dunPatrolActive = false;
                return;
            }
            settings.IsMacroRunning = true;
            // The patrol picked its own start (the first waypoint in sight). Starting the macro
            // here must not also trigger the macro-start "nearest waypoint" pick on the next
            // tick: on a loop that is often a waypoint near the END (it comes back to the start),
            // so the bot walked there, back to waypoint 0, and only then on (2026-10-08).
            _macroWasRunning = true;
        }
        settings.CurrentNavPath   = string.Empty;
        settings.CurrentRoute     = route;
        settings.ActiveNavIndex   = startIdx;
        settings.EnableNavigation = true;

        // Arm mid-patrol hazard rerouting: remember which dungeon this route belongs to
        // and the hazard generation it already accounts for. OnTick rebuilds when a newer
        // hazard is sighted in this same landblock.
        _dunPatrolActive        = true;
        _dunPatrolRoute         = route;
        _dunPatrolLandblock     = landblockKey;
        _dunPatrolFollowTriedLb = uint.MaxValue;
        _dunPatrolHazardVersion = _objectCache?.HazardVersion ?? 0;
        _dunPatrolSettings.MarkBuilt(patrolOptions);
        MarkPatrolPortals(portals);

        int hazardCount = hazards?.Count ?? 0;
        string hazardNote = hazardCount > 0 ? $", {hazardCount} hazard cell(s) avoided" : "";
        string portalNote = portals.Count > 0 ? $", kept out of {portals.Count} portal(s)" : "";
        if (isRebuild && followedIn)
        {
            ChatLine($"[RynthAi] DunNav-Patrol: followed you into dungeon 0x{landblockKey:X4} → {route.Points.Count} waypoints, patrolling here{hazardNote}{portalNote}. Stop the macro or load a route to end it.");
        }
        else if (isRebuild && settingsChanged)
        {
            ChatLine($"[RynthAi] DunNav-Patrol: patrol settings changed → rebuilt from here, {route.Points.Count} waypoints ({patrolOptions.Describe()}).");
        }
        else if (isRebuild && portalAppeared)
        {
            ChatLine($"[RynthAi] DunNav-Patrol: a portal came into view → rerouted to keep out of it ({portals.Count} portal(s) kept out of).");
        }
        else if (isRebuild)
        {
            ChatLine($"[RynthAi] DunNav-Patrol: new hazard sighted → rerouted around it ({hazardCount} hazard cell(s) avoided).");
        }
        else
        {
            string skipNote = startIdx > 0 ? $", skipped {startIdx} blocked waypoint(s)" : "";
            int doorwayPts = 0;
            foreach (var p in route.Points) if (p.Doorway) doorwayPts++;
            string layout = geometry != null && doorwayPts > 0
                ? $", {doorwayPts} of them lined up on doorways"
                : geometry == null ? " (dungeon map not loaded yet: cell-centre route)" : "";
            ReplyOk($"[RynthAi] DunNav-Patrol: {graph.Count} cells, {route.Points.Count} waypoints{layout} → circular patrol started{skipNote}{hazardNote}{portalNote}");
        }
    }

    /// <summary>
    /// Called once per OnTick while logged in. If a dunnav-patrol is running and a new
    /// hazard (lava/acid hotspot) has been sighted since the route was built, rebuilds the
    /// route so the hotspot is treated as a wall the bot turns around at. Likewise when a
    /// Dungeon Patrol setting changed (once it has settled, PatrolSettingsWatch): the patrol
    /// is rebuilt from where the character is, the macro keeps running. No-op otherwise.
    /// </summary>
    private void TickDunPatrolHazardReroute()
    {
        if (!_dunPatrolActive) return;
        // A patrol running when the character enters a Thread on DreamWeave (or one that began before the world name
        // was known): stopped the moment it is (ServerPolicy).
        if (ServerPolicy.PatrolOff(Host)) { StopPatrolForServer(); ChatLine(ServerPolicy.PatrolOffMessage); return; }

        var settings = _dashboard?.Settings;
        // A different route was loaded in the patrol's place (a nav file, a meta's route, Save As
        // made it a recorded route): stand down, and say so when it was replaced. We only own the
        // auto-generated, file-less circular patrol.
        bool replaced = settings != null && _dunPatrolRoute != null && !ReferenceEquals(settings.CurrentRoute, _dunPatrolRoute);
        if (settings == null || replaced
            || !string.IsNullOrEmpty(settings.CurrentNavPath)
            || settings.CurrentRoute?.RouteType != NavRouteType.Circular)
        {
            if (settings != null && (replaced || !string.IsNullOrEmpty(settings.CurrentNavPath)))
            {
                string what = PatrolReplacementName(settings);
                Host.Log($"[RynthAi] DunNav-Patrol: ended - {what} is the route now.");
                if (settings.CurrentRoute?.Points.Count > 0)
                    ChatLine($"[RynthAi] Dungeon Patrol ended: {what} is the route now. Press Patrol to patrol again.");
            }
            _dunPatrolActive = false;
            _dunPatrolRoute = null;
            return;
        }
        // Macro stopped or navigation switched off: the patrol is paused, not ended. It stays the
        // route, and starting again carries on with it (2026-11-01: a stop used to stand it down,
        // so after a restart the patrol ran without its hazard reroutes or keep-the-waypoint rule).
        if (!settings.IsMacroRunning || !settings.EnableNavigation)
        {
            _dunPatrolPrevRunningLb = uint.MaxValue;
            _dunPatrolArrivedRunning = false;
            return;
        }

        // The character went into another dungeon while patrolling: the patrol follows (not after a death).
        if (TickPatrolFollow(settings)) return;
        if (_objectCache == null) return;

        var patrolOptions = PatrolOptionsFrom(settings);
        bool settingsChanged = _dunPatrolSettings.ShouldRebuild(patrolOptions, Environment.TickCount64);
        bool hazardsChanged = _objectCache.HazardVersion != _dunPatrolHazardVersion;
        // A portal the patrol wasn't built round came into view (RynthAiPlugin.PatrolPortals.cs).
        bool portalAppeared = PatrolPortalAppeared();
        if (!hazardsChanged && !settingsChanged && !portalAppeared) return; // nothing new sighted, settings as built

        // Only reroute if still in the dungeon this patrol was built for. A portal to a new
        // landblock means BuildDunPatrol would (correctly) re-home, but that should happen via
        // an explicit /ra dunnav-patrol, not a hazard tick — so just resync and skip.
        if (!Host.TryGetPlayerPose(out uint playerCell, out _, out _, out _, out _, out _, out _, out _)
            || (playerCell >> 16) != _dunPatrolLandblock)
        {
            _dunPatrolHazardVersion = _objectCache.HazardVersion;
            _dunPatrolSettings.MarkBuilt(patrolOptions);
            return;
        }

        BuildDunPatrol(isRebuild: true, settingsChanged: settingsChanged, portalAppeared: portalAppeared && !hazardsChanged);
        // A rebuild that gave up (graph not loaded, ...) keeps the old route: don't retry every tick.
        if (settingsChanged) _dunPatrolSettings.MarkBuilt(patrolOptions);
        if (portalAppeared && _dunPatrolActive) MarkPatrolPortals(PatrolPortalsIn(_dunPatrolLandblock));
    }

    /// <summary>What the patrol was replaced with, for the chat line: "nav route 'X'", "the meta's route 'X'", "another route".</summary>
    private static string PatrolReplacementName(LegacyUiSettings s)
    {
        string path = s.CurrentNavPath ?? string.Empty;
        if (path.StartsWith("<embedded:", StringComparison.Ordinal)) return $"the meta's route '{path.Substring(10).TrimEnd('>')}'";
        if (path.Length > 0) return $"nav route '{System.IO.Path.GetFileNameWithoutExtension(path)}'";
        return "another route";
    }

    /// <summary>What the Dungeon Patrol does when the character is in another dungeon than the one it was built for.</summary>
    internal enum PatrolFollow
    {
        /// <summary>Nothing to do (same dungeon, not a dungeon, in portal space, already tried here).</summary>
        None,
        /// <summary>Build the patrol again here.</summary>
        Follow,
        /// <summary>Sent here by a death: say so, and leave it to the Patrol button.</summary>
        AfterDeath,
        /// <summary>Came here with the macro (or nav) off: say so, and leave it to the Patrol button.</summary>
        CameWhileStopped,
    }

    internal const long PatrolFollowDeathMs = 120_000;

    /// <summary>
    /// The decision half of <see cref="TickPatrolFollow"/>. The patrol follows the character through a
    /// portal (or a recall) taken while it runs into another dungeon: patrolling is the mode, and the
    /// patrol it had is for a dungeon it is no longer in (nav would only hold on it). Never into a
    /// building on the landscape (not a dungeon landblock), never mid-portal, once per dungeon. Not
    /// when a death sent the character there (a lifestone dungeon is no place to start hunting on its
    /// own), nor when it got there with the macro off (an HP-failsafe recall, travelling by hand):
    /// those say how to patrol here instead.
    /// </summary>
    internal static PatrolFollow DecidePatrolFollow(uint patrolLb, uint playerCell, bool isDungeonLandblock,
                                                    bool portaling, uint triedLb, long msSinceDeath, bool arrivedWhileRunning)
    {
        uint lb = playerCell >> 16;
        if (playerCell == 0 || lb == patrolLb || (playerCell & 0xFFFF) < 0x0100) return PatrolFollow.None;
        if (portaling || !isDungeonLandblock || lb == triedLb) return PatrolFollow.None;
        if (msSinceDeath >= 0 && msSinceDeath < PatrolFollowDeathMs) return PatrolFollow.AfterDeath;
        return arrivedWhileRunning ? PatrolFollow.Follow : PatrolFollow.CameWhileStopped;
    }

    /// <summary>
    /// Running patrol, character in another dungeon: builds the patrol there (or, after a death, says
    /// how to). True when it acted (the caller's hazard/settings checks are for the old dungeon).
    /// </summary>
    private bool TickPatrolFollow(LegacyUiSettings settings)
    {
        if (!Host.HasGetPlayerPose || !Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)) return false;
        uint lb = cell >> 16;
        if (_dunPatrolPrevRunningLb != uint.MaxValue && _dunPatrolPrevRunningLb != lb) _dunPatrolArrivedRunning = true;
        _dunPatrolPrevRunningLb = lb;
        if (lb == _dunPatrolLandblock) { _dunPatrolArrivedRunning = false; return false; }
        if ((cell & 0xFFFF) < 0x0100) return false;
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null || !cellDat.IsLoaded || _navPlanner == null) return false;   // asked again next tick
        bool portaling = (Host.HasIsPortaling && Host.IsPortaling()) || _deathHolding;
        long sinceDeath = _lastDeathAt == 0 ? -1 : Environment.TickCount64 - _lastDeathAt;
        var what = DecidePatrolFollow(_dunPatrolLandblock, cell, _navPlanner.IsDungeonLandblock(lb, cellDat), portaling,
                                      _dunPatrolFollowTriedLb, sinceDeath, _dunPatrolArrivedRunning);
        if (what == PatrolFollow.None) return false;

        uint was = _dunPatrolLandblock;
        _dunPatrolFollowTriedLb = lb;
        _dunPatrolArrivedRunning = false;
        if (what != PatrolFollow.Follow)
        {
            string how = what == PatrolFollow.AfterDeath ? "after a death" : "with the macro off on the way";
            Host.Log($"[RynthAi] DunNav-Patrol: in dungeon 0x{lb:X4} {how}; the patrol was for 0x{was:X4} - not following.");
            ChatLine($"[RynthAi] Dungeon Patrol: you are in dungeon 0x{lb:X4} ({how}); the patrol was for dungeon 0x{was:X4}. Press Patrol to patrol here.");
            return true;
        }

        Host.Log($"[RynthAi] DunNav-Patrol: the character went from dungeon 0x{was:X4} into 0x{lb:X4} - building the patrol here.");
        BuildDunPatrol(isRebuild: true, followedIn: true);
        if (!_dunPatrolActive || _dunPatrolLandblock != lb)
            ChatLine($"[RynthAi] Dungeon Patrol: couldn't lay a patrol in dungeon 0x{lb:X4} from here. Press Patrol to try again.");
        return true;
    }

    // ── Patrol-management info / actions (engine-side right-click flyout) ──────

    /// <summary>
    /// JSON for the engine Avalonia patrol flyout: the player's current dungeon + the set of
    /// dungeons that have persisted hazard cells. Built by hand (NativeAOT-friendly, no
    /// reflection). All values are plain ints / hex strings, so no escaping is needed.
    /// </summary>
    public string BuildPatrolInfoJson()
    {
        bool inDungeon = false;
        uint landblock = 0;
        if (Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _))
        {
            landblock = cell >> 16;
            inDungeon = (cell & 0xFFFF) >= 0x0100;
        }

        var sb = new System.Text.StringBuilder(256);
        sb.Append('{');
        sb.Append("\"inDungeon\":").Append(inDungeon ? "true" : "false").Append(',');
        sb.Append("\"currentLandblock\":\"").Append(landblock.ToString("X4")).Append("\",");
        sb.Append("\"currentHazards\":").Append(inDungeon ? DungeonHazardStore.Count(landblock) : 0).Append(',');
        sb.Append("\"liveHazards\":").Append(_objectCache?.HazardCellCount ?? 0).Append(',');
        sb.Append("\"dungeons\":[");
        var lbs = DungeonHazardStore.ListLandblocks();
        for (int i = 0; i < lbs.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"landblock\":\"").Append(lbs[i].ToString("X4"))
              .Append("\",\"cells\":").Append(DungeonHazardStore.Count(lbs[i])).Append('}');
        }
        sb.Append("],");
        sb.Append("\"routes\":[");
        try
        {
            string navFolder = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "NavProfiles");
            if (System.IO.Directory.Exists(navFolder))
            {
                var files = System.IO.Directory.GetFiles(navFolder, "*.nav");
                Array.Sort(files);
                for (int i = 0; i < files.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    string nm = System.IO.Path.GetFileNameWithoutExtension(files[i]);
                    sb.Append('"').Append(JsonEscape(nm)).Append('"');
                }
            }
        }
        catch { }
        sb.Append("]}");
        return sb.ToString();
    }

    // Minimal JSON string escaper for hand-built export JSON (route names, panel text).
    internal static string JsonEscape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Clears persisted + live hazards for one landblock (hex string, e.g. "AB12").</summary>
    public void ClearDungeonHazards(string landblockHex)
    {
        if (string.IsNullOrWhiteSpace(landblockHex)) return;
        string h = landblockHex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
        if (!uint.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                           System.Globalization.CultureInfo.InvariantCulture, out uint lb))
            return;
        DungeonHazardStore.Clear(lb);
        _objectCache?.ClearLiveHazards(lb);
        ChatLine($"[RynthAi] Cleared recorded hazards for dungeon 0x{lb:X4}.");
    }

    /// <summary>Clears every persisted + live hazard cell across all dungeons.</summary>
    public void ClearAllDungeonHazards()
    {
        DungeonHazardStore.ClearAll();
        _objectCache?.ClearAllLiveHazards();
        ChatLine("[RynthAi] Cleared all recorded dungeon hazards.");
    }

    // ── Manual hazard marking (/ra hazard …) ─────────────────────────────────
    //
    // AC dungeon lava/acid is usually baked into the EnvCell environment, NOT a named
    // world object, so the name-pattern auto-detector can't see it. Manual marking lets
    // the user stand on (or next to) a hazard and flag that cell directly; it then feeds
    // the exact same persistence + route-exclusion path as an auto-detected hazard.

    public void HandleHazardCommand(string[] parts)
    {
        // parts = ["ra", "hazard", <sub>, <args...>] — the dispatcher splits the whole command,
        // so the hazard subcommand is parts[2] (matches HandleSettingsCommand's parts[2] convention).
        string sub = parts.Length > 2 ? parts[2].ToLowerInvariant() : "help";
        switch (sub)
        {
            case "add":
            case "mark":   MarkCurrentCellHazard();   break;
            case "del":
            case "rm":
            case "remove":
            case "unmark": UnmarkCurrentCellHazard(); break;
            case "list":   ListDungeonHazardCells();  break;
            case "near":
            case "scan":   ScanNearbyLandscape();     break;
            case "surf":
            case "surfaces": DumpCurrentCellSurfaces();  break;
            case "learnhere":
            case "learn":    LearnHazardTextureHere();   break;
            case "texture":
            case "tex":      HandleHazardTextureCommand(parts); break;
            default:
                ChatLine("[RynthAi] /ra hazard add|del|list|near|surfaces|learnhere|texture");
                ChatLine("[RynthAi]   add       — mark the cell you're standing in as a hazard (patrol turns around at it)");
                ChatLine("[RynthAi]   del       — unmark the current cell");
                ChatLine("[RynthAi]   list      — recorded hazard cells in this dungeon");
                ChatLine("[RynthAi]   near      — list nearby landscape object names + cells (diagnostic)");
                ChatLine("[RynthAi]   surfaces  — dump the current cell's surface texture ids (Detector C diagnostic)");
                ChatLine("[RynthAi]   learnhere — stand on lava/acid: auto-flag its floor texture (precision-guarded)");
                ChatLine("[RynthAi]   texture add|del|list 0x<id> — manage Detector C lava/acid floor textures");
                break;
        }
    }

    // ── Detector C: EnvCell-surface hazard textures (lava/acid floors) ─────────

    /// <summary>Dumps the current EnvCell's surface palette → OrigTextureId, flagging known-hazard textures.</summary>
    private void DumpCurrentCellSurfaces()
    {
        if (!TryGetCurrentDungeonCell(out uint cell)) return;
        var cellDat   = _raycast?.GeometryLoader?.CellDat;
        var portalDat = _raycast?.GeometryLoader?.PortalDat;
        if (cellDat == null || portalDat == null || !cellDat.IsLoaded)
        {
            ReplyFail("[RynthAi] Geometry not loaded — raycast must be initialized first."); return;
        }

        var surfaces = DungeonHazardSurfaces.GetCellSurfaces(cellDat, portalDat, cell);
        if (surfaces.Count == 0) { ChatLine($"[RynthAi] EnvCell 0x{cell:X8}: no readable surfaces."); return; }

        // Only a texture on the walkable floor makes a cell a hazard; walls and ceilings don't.
        var floorTex = DungeonHazardSurfaces.GetCellFloorTextures(cell, _raycast?.GeometryLoader?.DungeonLOS, portalDat);
        ChatLine($"[RynthAi] EnvCell 0x{cell:X8} surfaces ({surfaces.Count}) — texId is the Detector C hazard key (floor textures only):");
        foreach (var (surfIdx, texId) in surfaces)
        {
            string texStr = texId == 0 ? "(solid color)" : $"0x{texId:X8}";
            string where  = texId != 0 && floorTex.Contains(texId) ? "  floor" : "";
            string tag    = texId != 0 && DungeonHazardSurfaces.IsHazardTexture(texId)
                ? (floorTex.Contains(texId) ? "  ← HAZARD" : "  (flagged, but not on this floor)") : "";
            ChatLine($"[RynthAi]   surf 0x{surfIdx:X4}  tex {texStr}{where}{tag}");
        }
        ChatLine("[RynthAi] On a lava/acid floor, run /ra hazard learnhere (auto) or /ra hazard texture add 0x<texId> (a floor texture).");
    }

    /// <summary>Stand on a hazard floor: flags the texture(s) rare across this landblock (the lava texture).</summary>
    private void LearnHazardTextureHere()
    {
        if (!TryGetCurrentDungeonCell(out uint cell)) return;
        var cellDat   = _raycast?.GeometryLoader?.CellDat;
        var portalDat = _raycast?.GeometryLoader?.PortalDat;
        if (cellDat == null || portalDat == null || !cellDat.IsLoaded)
        {
            ReplyFail("[RynthAi] Geometry not loaded — raycast must be initialized first."); return;
        }

        var learned = DungeonHazardSurfaces.LearnFromCell(cell, _raycast?.GeometryLoader?.DungeonLOS, portalDat);
        DungeonPathfinder.InvalidateCache();
        if (learned.Count == 0)
        {
            ReplyFail("[RynthAi] No rare floor texture found here to flag. If this IS lava, use /ra hazard surfaces then texture add 0x<id>.");
            return;
        }
        foreach (uint t in learned) ReplyOk($"[RynthAi] Flagged hazard texture 0x{t:X8}.");
        ReplyOk("[RynthAi] Re-run /ra dunnav-patrol to rebuild the route avoiding every cell with that floor.");
    }

    private void HandleHazardTextureCommand(string[] parts)
    {
        // parts = ["ra", "hazard", "texture", <sub>, <id>] — sub at parts[3], id at parts[4].
        string sub = parts.Length > 3 ? parts[3].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "add":
            {
                if (parts.Length < 5 || !TryParseHexUint(parts[4], out uint tex))
                { ReplyFail("[RynthAi] Usage: /ra hazard texture add 0x<OrigTextureId>"); return; }
                bool added = DungeonHazardSurfaces.AddHazardTexture(tex);
                DungeonPathfinder.InvalidateCache();
                ReplyOk(added
                    ? $"[RynthAi] Added hazard texture 0x{tex:X8}. Re-run /ra dunnav-patrol to rebuild around it."
                    : $"[RynthAi] Texture 0x{tex:X8} was already flagged.");
                break;
            }
            case "del":
            case "rm":
            case "remove":
            {
                if (parts.Length < 5 || !TryParseHexUint(parts[4], out uint tex))
                { ReplyFail("[RynthAi] Usage: /ra hazard texture del 0x<OrigTextureId>"); return; }
                bool removed = DungeonHazardSurfaces.RemoveHazardTexture(tex);
                DungeonPathfinder.InvalidateCache();
                ReplyOk(removed ? $"[RynthAi] Removed hazard texture 0x{tex:X8}." : $"[RynthAi] Texture 0x{tex:X8} was not flagged.");
                break;
            }
            default:
            {
                var list = DungeonHazardSurfaces.ListHazardTextures();
                if (list.Count == 0)
                {
                    ChatLine("[RynthAi] No hazard textures flagged. Stand on lava and use /ra hazard learnhere (or surfaces + texture add).");
                    return;
                }
                ChatLine($"[RynthAi] Detector C hazard textures ({list.Count}):");
                foreach (uint t in list) ChatLine($"[RynthAi]   0x{t:X8}");
                break;
            }
        }
    }

    private static bool TryParseHexUint(string s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private bool TryGetCurrentDungeonCell(out uint cell)
    {
        cell = 0;
        if (!Host.TryGetPlayerPose(out uint c, out _, out _, out _, out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi] Cannot read player position."); return false;
        }
        if ((c & 0xFFFF) < 0x0100)
        {
            ReplyFail("[RynthAi] You must be inside a dungeon (cell >= 0x0100) to mark hazards."); return false;
        }
        cell = c;
        return true;
    }

    /// <summary>Public entry for the engine patrol flyout "Mark this cell" button.</summary>
    public void MarkCurrentCellHazardFromUi() => MarkCurrentCellHazard();
    /// <summary>Public entry for the engine patrol flyout "Unmark this cell" button.</summary>
    public void UnmarkCurrentCellHazardFromUi() => UnmarkCurrentCellHazard();

    private void MarkCurrentCellHazard()
    {
        if (!TryGetCurrentDungeonCell(out uint cell)) return;
        bool added = _objectCache?.AddHazardCell(cell) ?? false;
        if (added)
            ReplyOk($"[RynthAi] Marked cell 0x{cell:X8} as a hazard — patrol will avoid it (saved). Re-run /ra dunnav-patrol to rebuild now.");
        else
            ReplyOk($"[RynthAi] Cell 0x{cell:X8} was already marked as a hazard.");
    }

    private void UnmarkCurrentCellHazard()
    {
        if (!TryGetCurrentDungeonCell(out uint cell)) return;
        bool removed = _objectCache?.RemoveHazardCell(cell) ?? false;
        ChatLine(removed
            ? $"[RynthAi] Unmarked hazard cell 0x{cell:X8}."
            : $"[RynthAi] Cell 0x{cell:X8} was not marked as a hazard.");
    }

    private void ListDungeonHazardCells()
    {
        if (!Host.TryGetPlayerPose(out uint c, out _, out _, out _, out _, out _, out _, out _))
        {
            ReplyFail("[RynthAi] Cannot read player position."); return;
        }
        uint lb = c >> 16;
        var cells = _objectCache?.GetHazardCellsForLandblock(lb) ?? new System.Collections.Generic.List<uint>();
        if (cells.Count == 0)
        {
            ChatLine($"[RynthAi] No hazard cells recorded for dungeon 0x{lb:X4}.");
            return;
        }
        ChatLine($"[RynthAi] Hazard cells in dungeon 0x{lb:X4} ({cells.Count}):");
        foreach (uint cell in cells)
            ChatLine($"[RynthAi]   0x{cell:X8}{(cell == c ? "  ← you are here" : "")}");
    }

    private void ScanNearbyLandscape()
    {
        if (_objectCache == null) { ReplyFail("[RynthAi] Object cache not ready."); return; }
        int shown = 0;
        ChatLine("[RynthAi] Nearby landscape objects (name → cell):");
        foreach (var wo in _objectCache.GetLandscape())
        {
            uint uid = (uint)wo.Id;
            string name = string.IsNullOrEmpty(wo.Name) ? "(no name)" : wo.Name;
            string cellStr = Host.TryGetObjectPosition(uid, out uint cell, out _, out _, out _)
                ? $"0x{cell:X8}" : "(no pos)";
            ChatLine($"[RynthAi]   {name} → {cellStr}  (0x{uid:X8})");
            if (++shown >= 20) { ChatLine("[RynthAi]   … (truncated at 20)"); break; }
        }
        if (shown == 0) ChatLine("[RynthAi]   (none in cache)");
    }

    /// <summary>
    /// Scans <paramref name="route"/> and returns the index of the first NavPoint whose
    /// world position has clear line of sight from the player. Returns 0 if no LOS check
    /// is possible (no raycast subsystem / no geometry) or if no waypoint clears — at
    /// worst we behave like the old "always start at 0" code, so this is a strict win.
    /// </summary>
    private int FindFirstLineOfSightWaypoint(NavRouteParser route, uint playerCell,
                                             float playerLocalX, float playerLocalY, float playerZ)
    {
        if (route?.Points == null || route.Points.Count == 0) return 0;
        var geo = _raycast?.GeometryLoader;
        if (geo == null) return 0;

        var geometry = geo.GetLandblockGeometry(playerCell);
        if (geometry == null || geometry.Count == 0) return 0;

        uint pBlockX = (playerCell >> 24) & 0xFF;
        uint pBlockY = (playerCell >> 16) & 0xFF;
        // Origin ≈ chest height so the ray doesn't graze floor polygons at the source.
        var origin = new Vector3(pBlockX * 192f + playerLocalX,
                                 pBlockY * 192f + playerLocalY,
                                 playerZ + 1.0f);

        for (int i = 0; i < route.Points.Count; i++)
        {
            var p = route.Points[i];
            if (!NavRouteParser.IsPlainWaypoint(p.Type)) continue; // skip pause/chat/portal-action nodes

            // NavPoint NS/EW use the same basis as NavCoordinateHelper; invert to world:
            //   globalX = (EW * 10 + 1019.5) * 24
            //   globalY = (NS * 10 + 1019.5) * 24
            //   worldZ  = navZ * 240   (NavPoint.Z is raw / 240)
            float gx = (float)((p.EW * 10.0 + 1019.5) * 24.0);
            float gy = (float)((p.NS * 10.0 + 1019.5) * 24.0);
            float gz = (float)(p.Z * 240.0) + 1.0f;
            var target = new Vector3(gx, gy, gz);

            // multiRay=true matches dungeon LOS used elsewhere (TargetingFSM) so thin
            // corner walls don't slip between the center ray.
            if (!RaycastEngine.IsLinearPathBlocked(origin, target, geometry, multiRay: true))
                return i;
        }

        return 0;
    }
}
