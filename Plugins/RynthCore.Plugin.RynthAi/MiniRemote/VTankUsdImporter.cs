// VTankUsdImporter.cs — VirindiTank .usd settings → RynthAi monster / weapon / consumable rules.
// Originally SilentKelpie's IltUsdImporter (ILT Hub, Gear tab). It edits RynthAi's own rules, so it
// stayed in RynthAi when the ILT Hub went to RynthILT (2026-10-13); RynthILT's Gear tab still shows it
// (RynthAi.Hub section "vtank-import").
//
// .usd is VTank's line-based table dump. Each table is:
//     <table name>
//     <column count 1..32>
//     <column name> x count
//     <column type token> x count
//     <row count>
//     per cell: <tag line s|i|b|d|...> then <value line>
// Tables we read: MyMonsters (monster rules), weapons/wands/shields/combatitems (weapon
// rules), pets (pet essences), items/itemlist/buffeditems/AssistItems (consumables).
// Object ids are resolved against the current inventory (id first, then name). The
// import is previewed first and only applied after a confirm; existing rules with the
// same name / id are replaced, everything else is kept.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

internal sealed class VTankUsdImporter
{
    /// <summary>Default VTank profile folder when the Decal registry value is unavailable.</summary>
    public const string DefaultVtProfileDir = @"C:\Games\VirindiPlugins\VirindiTank\";

    private static readonly string[] Elements = { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    private static readonly HashSet<string> WeaponTables = new(StringComparer.OrdinalIgnoreCase) { "weapons", "wands", "shields", "combatitems" };
    private static readonly HashSet<string> ConsumableTables = new(StringComparer.OrdinalIgnoreCase) { "items", "itemlist", "buffeditems", "assistitems" };

    /// <summary>One parsed table: column names and rows of raw cell strings.</summary>
    private sealed class UsdTable
    {
        public string Name = string.Empty;
        public string[] Columns = Array.Empty<string>();
        public List<string[]> Rows = new();

        public string Get(string[] row, params string[] names)
        {
            foreach (string n in names)
            {
                int i = Array.FindIndex(Columns, c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i < row.Length) return row[i] ?? string.Empty;
            }
            return string.Empty;
        }
    }

    /// <summary>Result of a preview: the rules that would be written.</summary>
    private sealed class ImportPlan
    {
        public string Path = string.Empty;
        public List<MonsterRule> Monsters = new();
        public List<ItemRule> Weapons = new();
        public List<ConsumableRule> Consumables = new();
        public List<string> Notes = new();
    }

    private readonly MiniRemoteContext _ctx;
    private volatile ImportPlan? _plan;
    private volatile string _status = string.Empty;
    private string _pathInput = string.Empty;

    public VTankUsdImporter(MiniRemoteContext ctx)
    {
        _ctx = ctx;
        _pathInput = ctx.State.LastUsdPath;
    }

    /// <summary>VTank's profile folder from Decal's registry entry, else the default install path.</summary>
    public static string VtProfileDir()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Decal\Plugins\{642F1F48-16BE-48BF-B1D4-286652C4533E}");
            if (key?.GetValue("ProfilePath") is string p && p.Length > 0 && Directory.Exists(p)) return p;
        }
        catch { /* registry unavailable: fall back */ }
        return DefaultVtProfileDir;
    }

    // ── Parsing ─────────────────────────────────────────────────────────────

    private static List<UsdTable> Parse(string[] lines)
    {
        var tables = new List<UsdTable>();
        int i = 0;
        while (i < lines.Length - 1)
        {
            string name = lines[i].Trim();
            if (name.Length == 0 || !int.TryParse(lines[i + 1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cols)
                || cols < 1 || cols > 32 || name.Any(char.IsWhiteSpace))
            {
                i++;
                continue;
            }

            int p = i + 2;
            if (p + cols * 2 >= lines.Length) { i++; continue; }
            var t = new UsdTable { Name = name, Columns = new string[cols] };
            for (int c = 0; c < cols; c++) t.Columns[c] = lines[p++].Trim();
            p += cols; // type tokens
            if (p >= lines.Length || !int.TryParse(lines[p].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows) || rows < 0 || rows > 100_000)
            {
                i++;
                continue;
            }
            p++;
            bool ok = true;
            for (int r = 0; r < rows && ok; r++)
            {
                var row = new string[cols];
                for (int c = 0; c < cols; c++)
                {
                    if (p + 1 >= lines.Length) { ok = false; break; }
                    // Every cell is a tag line plus a value line; unknown tags are still two lines.
                    row[c] = lines[p + 1].Trim();
                    p += 2;
                }
                if (ok) t.Rows.Add(row);
            }
            tables.Add(t);
            i = p;
        }
        return tables;
    }

    private static int ParseId(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)) return hex;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return unchecked((int)l);
        return 0;
    }

    private static bool ValidId(int id) => id != 0 && id != -1 && id != -5;

    private static bool ParseBool(string s)
        => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>VTank damage value (name or AC damage-type bitmask) → RynthAi element, or null.</summary>
    private static string? MapElement(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return null;
        foreach (string e in Elements) if (v.Equals(e, StringComparison.OrdinalIgnoreCase)) return e;
        if (v.StartsWith("Bludg", StringComparison.OrdinalIgnoreCase)) return "Bludgeon";
        if (v.StartsWith("Pierc", StringComparison.OrdinalIgnoreCase)) return "Pierce";
        if (v.StartsWith("Electric", StringComparison.OrdinalIgnoreCase)) return "Lightning";
        if (v.StartsWith("Void", StringComparison.OrdinalIgnoreCase)) return "Nether";
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dt) || dt <= 0) return null;
        return BitsToElement(dt);
    }

    private static string? BitsToElement(int dt)
    {
        if ((dt & 1024) != 0) return "Nether";
        if ((dt & 16) != 0) return "Fire";
        if ((dt & 8) != 0) return "Cold";
        if ((dt & 64) != 0) return "Lightning";
        if ((dt & 32) != 0) return "Acid";
        if ((dt & 4) != 0) return "Bludgeon";
        if ((dt & 2) != 0) return "Pierce";
        if ((dt & 1) != 0) return "Slash";
        return null;
    }

    /// <summary>Resolves a USD object (id, name) to a carried item.</summary>
    private WorldObject? Resolve(int id, string name)
    {
        var cache = _ctx.Inventory.Cache;
        if (cache == null) return null;
        if (ValidId(id))
        {
            var byId = cache[id];
            if (byId != null) return byId;
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _ctx.Inventory.Items().FirstOrDefault(w => w.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string ConsumableType(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("essence")) return "Pet";
        if (n.Contains("kit")) return "HealthKit";
        if (n.Contains("mana stone") || n.Contains("mana charge")) return "ManaStone";
        if (n.Contains("stamina")) return "Stamina";
        if (n.Contains("lockpick")) return "Lockpick";
        return "General";
    }

    // ── Preview / apply (pump thread) ───────────────────────────────────────

    public void Preview(string path)
    {
        path = (path ?? string.Empty).Trim().Trim('"');
        if (path.Length > 0 && !Path.IsPathRooted(path)) path = Path.Combine(VtProfileDir(), path);
        if (!File.Exists(path)) { _status = "File not found: " + path; _plan = null; return; }

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) { _status = "Read failed: " + ex.Message; _plan = null; return; }

        var plan = new ImportPlan { Path = path };
        foreach (var t in Parse(lines))
        {
            if (t.Name.Equals("MyMonsters", StringComparison.OrdinalIgnoreCase)) PlanMonsters(t, plan);
            else if (WeaponTables.Contains(t.Name)) PlanWeapons(t, plan);
            else if (t.Name.Equals("pets", StringComparison.OrdinalIgnoreCase)) PlanConsumables(t, plan, forceType: "Pet");
            else if (ConsumableTables.Contains(t.Name)) PlanConsumables(t, plan, forceType: null);
        }
        _ctx.State.LastUsdPath = path;
        _plan = plan;
        _status = $"Preview: {plan.Monsters.Count} monsters, {plan.Weapons.Count} weapons, {plan.Consumables.Count} consumables.";
    }

    private void PlanMonsters(UsdTable t, ImportPlan plan)
    {
        foreach (var row in t.Rows)
        {
            string name = t.Get(row, "MonsterName", "Name").Trim();
            if (name.Length == 0) continue;
            var rule = new MonsterRule
            {
                Name = name,
                Priority = int.TryParse(t.Get(row, "AttackPriority", "Priority"), out int pri) ? Math.Max(0, pri) : 1,
                DamageType = MapElement(t.Get(row, "DamageType")) ?? "Auto",
                ExVuln = MapElement(t.Get(row, "SecondaryVuln", "ExtraVuln")) ?? "None",
                PetDamage = MapElement(t.Get(row, "PetDamage")) ?? "PAuto",
                UseRing = ParseBool(t.Get(row, "Ring")),
                UseStreak = ParseBool(t.Get(row, "Streak")),
                UseArc = ParseBool(t.Get(row, "Arc")),
                Fester = ParseBool(t.Get(row, "Fester")),
                Broadside = ParseBool(t.Get(row, "Broadside")),
                GravityWell = ParseBool(t.Get(row, "GravityW", "GravityWell")),
                Imperil = ParseBool(t.Get(row, "Imperil")),
                Yield = ParseBool(t.Get(row, "Yield")),
                Vuln = ParseBool(t.Get(row, "Vuln")),
            };
            var weapon = Resolve(ParseId(t.Get(row, "WeaponToUse", "Weapon")), t.Get(row, "WeaponName"));
            if (weapon != null) rule.WeaponId = weapon.Id;
            else if (t.Get(row, "WeaponName").Length > 0) plan.Notes.Add($"{name}: weapon '{t.Get(row, "WeaponName")}' not carried");
            var off = Resolve(ParseId(t.Get(row, "SecondaryEquip", "Offhand")), t.Get(row, "OffhandName"));
            if (off != null) rule.OffhandId = off.Id;
            plan.Monsters.Add(rule);
        }
    }

    private void PlanWeapons(UsdTable t, ImportPlan plan)
    {
        // The VT "shields" table (or any row whose item wields into the shield slot)
        // becomes an off-hand Shield entry, never a main-hand weapon.
        bool shieldTable = t.Name.Equals("shields", StringComparison.OrdinalIgnoreCase);
        foreach (var row in t.Rows)
        {
            int id = ParseId(t.Get(row, "Object", "ObjectId", "Id"));
            string name = t.Get(row, "Name").Trim();
            var wo = Resolve(id, name);
            if (wo == null) { if (name.Length > 0) plan.Notes.Add($"weapon '{name}' not carried"); continue; }
            if (plan.Weapons.Any(w => w.Id == wo.Id)) continue;
            bool isShield = shieldTable || RynthCore.Plugin.RynthAi.CombatManager.IsShieldItem(wo);
            string element = BitsToElement(wo.Values(LongValueKey.DamageType, 0)) ?? "Slash";
            plan.Weapons.Add(new ItemRule
            {
                Id      = wo.Id,
                Name    = wo.Name,
                Action  = isShield ? ItemRule.ShieldAction : ItemRule.WeaponAction,
                Element = element,
            });
        }
    }

    private void PlanConsumables(UsdTable t, ImportPlan plan, string? forceType)
    {
        foreach (var row in t.Rows)
        {
            int id = ParseId(t.Get(row, "Object", "ObjectId", "Id"));
            string name = t.Get(row, "Name").Trim();
            var wo = Resolve(id, name);
            if (wo == null) { if (name.Length > 0) plan.Notes.Add($"item '{name}' not carried"); continue; }
            if (wo.ObjectClass is AcObjectClass.MeleeWeapon or AcObjectClass.MissileWeapon or AcObjectClass.WandStaffOrb) continue;
            if (plan.Consumables.Any(c => c.Id == wo.Id)) continue;
            plan.Consumables.Add(new ConsumableRule { Id = wo.Id, Name = wo.Name, Type = forceType ?? ConsumableType(wo.Name) });
        }
    }

    /// <summary>Writes the previewed rules into the live settings and saves them.</summary>
    public void Apply()
    {
        var plan = _plan;
        var s = _ctx.Settings;
        if (plan == null || s == null) { _status = "Nothing to apply."; return; }

        foreach (var m in plan.Monsters)
        {
            s.MonsterRules.RemoveAll(r => r.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            s.MonsterRules.Add(m);
        }
        foreach (var w in plan.Weapons)
        {
            s.ItemRules.RemoveAll(r => r.Id == w.Id);
            s.ItemRules.Add(w);
        }
        foreach (var c in plan.Consumables)
        {
            s.ConsumableRules.RemoveAll(r => r.Id == c.Id);
            s.ConsumableRules.Add(c);
        }
        _ctx.SaveCombatSettings?.Invoke();
        _status = $"Imported {plan.Monsters.Count} monsters, {plan.Weapons.Count} weapons, {plan.Consumables.Count} consumables.";
        _ctx.Chat("[Mini Remote] " + _status);
        _plan = null;
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Import VirindiTank settings (.usd)");
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##usdpath", @"full path or file name in C:\Games\VirindiPlugins\VirindiTank", ref _pathInput, 260u);
        ImGui.SameLine();
        string p = _pathInput;
        if (ImGui.SmallButton("Preview")) _ctx.Post(() => Preview(p));

        var plan = _plan;
        if (plan != null)
        {
            ImGui.TextUnformatted($"{plan.Monsters.Count} monster rules, {plan.Weapons.Count} weapons, {plan.Consumables.Count} consumables");
            if (ImGui.TreeNode("Preview details"))
            {
                foreach (var m in plan.Monsters.Take(50)) SafeImGui.BulletText($"Monster {m.Name} [{m.DamageType}] pri {m.Priority}");
                foreach (var w in plan.Weapons) SafeImGui.BulletText($"Weapon {w.Name} [{w.Element}]");
                foreach (var c in plan.Consumables) SafeImGui.BulletText($"{c.Type}: {c.Name}");
                foreach (var n in plan.Notes.Take(30)) SafeImGui.TextColored(LegacyDashboardRenderer.ColAmber, n);
                ImGui.TreePop();
            }
            if (ImGui.Button("Apply import..."))
                _ctx.Confirm("Import VTank settings",
                    $"Write these rules into the current RynthAi profile?\nRules with the same monster name / item id are replaced.",
                    Apply, "Import");
        }
        if (_status.Length > 0) SafeImGui.TextWrapped(_status);
    }
}
