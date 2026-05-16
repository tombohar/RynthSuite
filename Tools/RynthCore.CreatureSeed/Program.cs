using System.Text;
using System.Text.Json;
using MySqlConnector;
using RynthCore.Plugin.RynthAi.CreatureData;

// RynthCore.CreatureSeed
// Out-of-process seeder: reads creature MaxHealth from the local ACE world DB and
// fills gaps in RynthAi's creatures.json so the dashboard can show an absolute
// health number for monsters the player has never appraised.
//
// Never touches acclient / the plugin / the engine. Re-runnable; additive only.
//
//   dotnet run --project Tools\RynthCore.CreatureSeed [-- options]
//     --config <path>   ACE Config.js (default: C:\Projects\ACE\Source\ACE.Server\Config.js)
//     --conn   <str>    explicit MySQL connection string (overrides --config)
//     --out    <path>   creatures.json (default: C:\Games\RynthSuite\RynthAi\CreatureData\creatures.json)
//     --dry-run         compute + report, write nothing

const string DefaultConfig = @"C:\Projects\ACE\Source\ACE.Server\Config.js";
const string DefaultOut = @"C:\Games\RynthSuite\RynthAi\CreatureData\creatures.json";

const int WeenieTypeCreature = 10;       // ACE.Entity.Enum.WeenieType.Creature
const int PropStringName = 1;            // ACE PropertyString.Name
const int PropAttr2ndMaxHealth = 1;      // ACE PropertyAttribute2nd.MaxHealth

string configPath = DefaultConfig;
string? connOverride = null;
string outPath = DefaultOut;
bool dryRun = false;
string? probe = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config": configPath = args[++i]; break;
        case "--conn": connOverride = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--dry-run": dryRun = true; break;
        case "--probe": probe = args[++i]; break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 2;
    }
}

string connString;
try
{
    connString = connOverride ?? BuildConnFromAceConfig(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to resolve MySQL connection: {ex.Message}");
    return 1;
}

// Diagnostic: show every weenie whose name matches a substring, with its
// WeenieType and (LEFT JOINed) MaxHealth attribute row so missing/odd data is visible.
if (probe != null)
{
    try
    {
        using var c = new MySqlConnection(connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
            SELECT w.class_Id, w.type, s.value,
                   a.type AS aType, a.init_Level
            FROM weenie_properties_string s
            JOIN weenie w ON w.class_Id = s.object_Id
            LEFT JOIN weenie_properties_attribute_2nd a
                  ON a.object_Id = w.class_Id AND a.type = {PropAttr2ndMaxHealth}
            WHERE s.type = {PropStringName} AND s.value LIKE @p
            ORDER BY s.value, w.class_Id
            LIMIT 60";
        cmd.Parameters.AddWithValue("@p", "%" + probe + "%");
        using var r = cmd.ExecuteReader();
        Console.WriteLine($"probe '{probe}'  (wcid | weenieType | name | maxHealthRow)");
        int n = 0;
        while (r.Read())
        {
            n++;
            string mh = r.IsDBNull(4) ? "(no MaxHealth attr)" : r.GetInt64(4).ToString();
            Console.WriteLine($"  {r.GetInt64(0),-7} | type={r.GetInt32(1),-3} | {r.GetString(2),-32} | {mh}");
        }
        if (n == 0) Console.WriteLine("  (no name matches at all)");
    }
    catch (Exception ex) { Console.Error.WriteLine($"probe failed: {ex.Message}"); return 1; }
    return 0;
}

// 1. Pull every Creature weenie's display name + innate MaxHealth.
var rows = new List<(uint Wcid, string Name, uint MaxHealth)>();
try
{
    using var conn = new MySqlConnection(connString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
        SELECT w.class_Id   AS wcid,
               s.value       AS name,
               a.init_Level  AS maxHealth
        FROM weenie w
        JOIN weenie_properties_string s
              ON s.object_Id = w.class_Id AND s.type = {PropStringName}
        JOIN weenie_properties_attribute_2nd a
              ON a.object_Id = w.class_Id AND a.type = {PropAttr2ndMaxHealth}
        WHERE w.type = {WeenieTypeCreature}
          AND a.init_Level > 0
          AND s.value IS NOT NULL AND s.value <> ''";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        uint wcid = (uint)r.GetInt64(0);
        string name = r.GetString(1).Trim();
        uint hp = (uint)r.GetInt64(2);
        if (name.Length == 0 || hp == 0) continue;
        rows.Add((wcid, name, hp));
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"world DB query failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"ace_world: {rows.Count} creature rows.");

// 2. Collapse same-name variants -> keep HIGHEST MaxHealth (user's chosen policy:
//    stable with CreatureProfileStore.Upsert's raise-only merge).
var byName = new Dictionary<string, (uint Wcid, string Name, uint MaxHealth)>(StringComparer.OrdinalIgnoreCase);
foreach (var row in rows)
{
    if (!byName.TryGetValue(row.Name, out var cur) || row.MaxHealth > cur.MaxHealth)
        byName[row.Name] = row;
}
Console.WriteLine($"collapsed to {byName.Count} distinct names (highest-HP per name).");

// 3. Load existing creatures.json (preserve every observed record).
var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    IncludeFields = true,
    PropertyNameCaseInsensitive = true,
};

var store = new Dictionary<string, CreatureProfile>(StringComparer.OrdinalIgnoreCase);
int existingCount = 0;
if (File.Exists(outPath))
{
    string existingJson = File.ReadAllText(outPath); // strips UTF-8 BOM on read
    if (!string.IsNullOrWhiteSpace(existingJson))
    {
        var loaded = JsonSerializer.Deserialize<Dictionary<string, CreatureProfile>>(existingJson, jsonOpts);
        if (loaded != null)
            foreach (var kv in loaded) store[kv.Key] = kv.Value;
    }
    existingCount = store.Count;
}
Console.WriteLine($"existing creatures.json: {existingCount} records.");

// Names already known from real observation — never seed over these.
var observedNames = new HashSet<string>(
    store.Values.Select(v => v.Name.Trim()), StringComparer.OrdinalIgnoreCase);

// 4. Additive merge: add a seed record only for names not already present.
int added = 0, skipped = 0;
foreach (var e in byName.Values)
{
    if (observedNames.Contains(e.Name)) { skipped++; continue; }

    string key = e.Wcid == 0 ? e.Name : $"{e.Name}|{e.Wcid}";
    if (store.ContainsKey(key)) { skipped++; continue; }

    store[key] = new CreatureProfile
    {
        Name = e.Name,
        Wcid = e.Wcid,
        MaxHealth = e.MaxHealth,
        Samples = 0,                       // 0 == seed; any real observation outranks it
        Notes = "seeded:ace_world",
        // resists left at the 1.0 "no data" sentinel via CreatureProfile defaults
    };
    added++;
}

Console.WriteLine($"seed: +{added} new, {skipped} skipped (already known), total now {store.Count}.");

// Spot-check a few well-known mobs so the numbers can be eyeballed.
string[] probes = { "Drudge Skulker", "Banderling Guard", "Banderling Crusher",
                     "Banderling Guard Champion", "Olthoi Soldier", "Drudge Bloodletter",
                     "Drudge Biter", "Drudge Cabalist", "Banderling Chief" };
Console.WriteLine("spot-check (name -> wcid / MaxHealth):");
foreach (var p in probes)
{
    var hit = store.Values.FirstOrDefault(v => string.Equals(v.Name, p, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(hit != null
        ? $"  {p,-22} -> {hit.Wcid} / {hit.MaxHealth} hp"
        : $"  {p,-22} -> (not in world DB)");
}

if (dryRun)
{
    Console.WriteLine("--dry-run: nothing written.");
    return 0;
}

if (added == 0)
{
    Console.WriteLine("no new records to add; creatures.json left unchanged.");
    return 0;
}

// 5. Back up, then write UTF-8 WITHOUT BOM (matches existing file; avoids the
//    JsonDocument-BOM parse pitfall the engine has hit before).
try
{
    if (File.Exists(outPath))
        File.Copy(outPath, outPath + ".bak", overwrite: true);

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    string outJson = JsonSerializer.Serialize(store, jsonOpts);
    File.WriteAllText(outPath, outJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"wrote {outPath} ({store.Count} records). backup: {outPath}.bak");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"write failed: {ex.Message}");
    return 1;
}

return 0;

// --- helpers ---

static string BuildConnFromAceConfig(string configPath)
{
    if (!File.Exists(configPath))
        throw new FileNotFoundException($"ACE Config.js not found at {configPath} (pass --config or --conn)");

    string text = File.ReadAllText(configPath);
    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    });

    var world = doc.RootElement.GetProperty("MySql").GetProperty("World");
    string host = world.GetProperty("Host").GetString() ?? "127.0.0.1";
    int port = world.TryGetProperty("Port", out var pv) ? pv.GetInt32() : 3306;
    string db = world.GetProperty("Database").GetString() ?? "ace_world";
    string user = world.GetProperty("Username").GetString() ?? "root";
    string pass = world.GetProperty("Password").GetString() ?? "";

    return new MySqlConnectionStringBuilder
    {
        Server = host,
        Port = (uint)port,
        Database = db,
        UserID = user,
        Password = pass,
        ConnectionTimeout = 15,
    }.ConnectionString;
}
