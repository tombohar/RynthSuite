using System;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// The difficulty tier where the player stands and the monster health a tier adds, as the active
/// server's plugin reports them, and which server this is for per-server data (2026-10-03 as one
/// server's AwakenedTier; server-neutral since 2026-10-21).
///
/// Some servers hold the same monsters (same names, same wcids) at several difficulty tiers, with
/// health, skills and damage scaled per tier. A server plugin that knows its server's tiers says so
/// in its "RynthServer.Profile" values (Plugins/Shared/RynthServerProfileApi.cs):
///   "difficultyTier"           the tier where the player stands (0 = the unscaled world);
///   "difficultyHealthPercent"  the health one tier adds, in percent of tier 0's (absent: 0).
/// They are read from the plugin that owns RynthCore's active server (ServerStateClient, plugin API
/// v86), about once a second. RynthAi knows nothing else about the server.
///
/// The rule: while no server plugin reports a tier this is inert - <see cref="Current"/> is 0,
/// <see cref="Active"/> false and every health scale 1 - so every store keyed by tier behaves
/// exactly as before. A reported tier 0 is Active (the Damage panel shows the picker) and still
/// changes nothing about the numbers.
///
/// <see cref="ServerKey"/>: the folder of the per-server creature data (CreatureProfileStore), see
/// <see cref="ResolveServerKey"/>.
/// </summary>
internal sealed class DifficultyTier
{
    /// <summary>The largest tier taken as reported (anything above is read as this).</summary>
    public const int MaxTier = 10_000;

    /// <summary>The largest health percent per tier taken (anything else, or below 0, reads as 0).</summary>
    public const double MaxHealthPercent = 1000;

    /// <summary>One reading of the server plugin's values: replaced whole (render and pump threads read it).</summary>
    internal sealed record Reading(bool Active, int Tier, double HealthPercent, string Plugin)
    {
        public static readonly Reading None = new(false, 0, 0, string.Empty);
    }

    private readonly ServerStateClient _server = new();
    private volatile Reading _reading = Reading.None;
    private volatile string _serverKey = string.Empty;
    private long _nextRefreshMs;
    private int _appliedRevision = -1;
    private string _appliedWorld = string.Empty;

    /// <summary>The active server's plugin reports a difficulty tier. False everywhere else.</summary>
    public bool Active => _reading.Active;

    /// <summary>The tier where the player stands; 0 whenever <see cref="Active"/> is false.</summary>
    public int Current => _reading.Active ? _reading.Tier : 0;

    /// <summary>The monster health one tier adds, in percent of tier 0's; 0 whenever <see cref="Active"/> is false.</summary>
    public double HealthPercentPerTier => _reading.Active ? _reading.HealthPercent : 0;

    /// <summary>The plugin the tier comes from ("" while none reports one).</summary>
    public string Source => _reading.Plugin;

    /// <summary>
    /// The server's key for per-server data (creatures.json): see <see cref="ResolveServerKey"/>;
    /// "" while unknown.
    /// </summary>
    public string ServerKey => _serverKey;

    /// <summary>The multiplier this server's tier <paramref name="tier"/> puts on a monster's health (1 without tiers).</summary>
    public double HealthScale(int tier) => HealthScale(tier, HealthPercentPerTier);

    /// <summary>The multiplier a tier puts on a monster's health: 1 + <paramref name="percentPerTier"/>% per tier.</summary>
    public static double HealthScale(int tier, double percentPerTier)
        => tier <= 0 || percentPerTier <= 0 ? 1.0 : 1.0 + percentPerTier / 100.0 * tier;

    /// <summary>Logout: nothing carries to the next login (it may be another server).</summary>
    public void Reset()
    {
        _server.Clear();
        _reading = Reading.None;
        _serverKey = string.Empty;
        _nextRefreshMs = 0;
        _appliedRevision = -1;
        _appliedWorld = string.Empty;
    }

    /// <summary>Re-reads the server and its tier, at most once a second (<paramref name="force"/>: now). Pump thread.</summary>
    public void Refresh(RynthCoreHost host, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now < _nextRefreshMs) return;
        _nextRefreshMs = now + 1000;
        try
        {
            _server.Poll(host, now, force: true);   // throttled here, not by the client's own 2 s
            string world = host.HasGetWorldName && host.TryGetWorldName(out string w) ? (w ?? string.Empty).Trim() : string.Empty;
            if (_server.Revision == _appliedRevision && world == _appliedWorld) return;
            _appliedRevision = _server.Revision;
            _appliedWorld = world;
            Apply(_server.State, _server.ProfileJson, _server.ProfilePlugin, world, CreatureProfileStore.ExistingServerFolder);
        }
        catch { }
    }

    /// <summary>
    /// What RynthCore's server list, the active server owner's profile JSON and the world name mean:
    /// the tier reading and the creature data key. <paramref name="existingFolder"/> answers whether a
    /// per-server creature data folder exists (its name as on disk, or null). Pure apart from it.
    /// </summary>
    internal void Apply(EngineServerState? state, string? profileJson, string? plugin, string? world,
                        Func<string, string?> existingFolder)
    {
        _reading = Read(profileJson, plugin);
        _serverKey = ResolveServerKey(state, world, existingFolder);
    }

    /// <summary>The tier values of a profile JSON; <see cref="Reading.None"/> without a "difficultyTier". Pure.</summary>
    internal static Reading Read(string? profileJson, string? plugin)
    {
        double tier = ServerProfileView.Value(profileJson, RynthServerProfile.ValueDifficultyTier, double.NaN);
        if (double.IsNaN(tier)) return Reading.None;
        double percent = ServerProfileView.Value(profileJson, RynthServerProfile.ValueDifficultyHealthPercent, 0);
        if (!(percent >= 0 && percent <= MaxHealthPercent)) percent = 0;
        return new Reading(true, (int)Math.Clamp(Math.Round(tier), 0, MaxTier), percent, plugin ?? string.Empty);
    }

    /// <summary>
    /// The folder name of this server's creature data. Pure apart from <paramref name="existingFolder"/>.
    ///   * RynthCore names the server (the active one, else one its detection found and this character
    ///     turned off): a folder that already holds data is kept, looked for under the server's id, then
    ///     its name, then the world name (RynthAi before 2026-10-21 used the world name, and "Aelrynth"
    ///     for that server's worlds, which its name finds); with none, the server's id.
    ///   * Otherwise (no server, an engine before v86): the world name, as before.
    /// Every key is made safe for a folder name (the SettingsProfiles rule); "" while nothing is known.
    /// </summary>
    internal static string ResolveServerKey(EngineServerState? state, string? world, Func<string, string?> existingFolder)
    {
        string legacy = SafeKey(world);
        EngineServer? server = ServerOf(state);
        if (server == null) return legacy;
        string id = SafeKey(server.Id);
        foreach (string candidate in new[] { id, SafeKey(server.Name), legacy })
        {
            if (candidate.Length == 0) continue;
            string? found = existingFolder(candidate);
            if (!string.IsNullOrEmpty(found)) return found;
        }
        return id.Length > 0 ? id : legacy;
    }

    /// <summary>RynthCore's active server, else the first one its detection found (turned off by an override); null for none.</summary>
    private static EngineServer? ServerOf(EngineServerState? state)
    {
        if (state == null) return null;
        if (state.HasActive)
            return state.Find(state.Active) ?? new EngineServer(state.Active, state.ActiveName, true, true, "auto", false, state.ActivePlugin, "");
        foreach (EngineServer s in state.Servers)
            if (s.Detected) return s;
        return null;
    }

    /// <summary>A world or server name as a folder name (the SettingsProfiles rule); "" for none.</summary>
    public static string SafeKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' || c == '-' ? c : '_');
        return sb.ToString();
    }
}
