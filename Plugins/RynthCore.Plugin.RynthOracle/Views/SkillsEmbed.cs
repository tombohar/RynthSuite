using System;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The Skills, Attributes and Progress tabs have no page of their own: RynthCore draws its Skills
/// panel body (raise and train, attributes and vitals, the ILT planners) into the Oracle window,
/// below the tab strip. This class is the plugin's half of that hand-off. It tells the engine
/// which body to draw with "/rc oracle-skills skills|attrs|progress" and to stop with
/// "/rc oracle-skills off".
///
/// It is a small state machine over what the engine was last told, so a command goes out only
/// when the wanted mode changes: never once per frame or per recorded pass. The first command,
/// once a character is in the world, is sent whatever the mode (usually "off"): the engine takes
/// any "/rc oracle-skills" as Oracle announcing that it has these tabs, and only then points the
/// bar's Skills icon and "/rc skills" at them instead of its standalone Skills panel.
/// </summary>
internal sealed class SkillsEmbed
{
    /// <summary>The engine's word for "no embedded Skills body".</summary>
    public const string Off = "off";

    /// <summary>A failed send is tried again no sooner than this.</summary>
    public const long RetryMs = 1000;

    private readonly Func<string, bool> _send;
    private string? _sent;   // null until the first command has gone out (the engine knows nothing of us yet)
    private long _retryAtMs;

    /// <param name="send">Runs a chat command through the engine; false when it didn't go out.</param>
    public SkillsEmbed(Func<string, bool> send) => _send = send;

    /// <summary>What the engine was last told (and accepted): "skills", "attrs", "progress" or "off"; "off" before any command.</summary>
    public string Sent => _sent ?? Off;

    /// <summary>True once the engine has been sent a command (it then knows Oracle has these tabs).</summary>
    public bool Announced => _sent != null;

    /// <summary>The engine mode for an Oracle tab, or null when the tab isn't one of the three.</summary>
    public static string? ModeFor(string tab) => tab switch
    {
        OracleView.TabSkills => "skills",
        OracleView.TabAttributes => "attrs",
        OracleView.TabProgress => "progress",
        _ => null,
    };

    /// <summary>True for the tabs whose page is drawn by the engine.</summary>
    public static bool IsEmbedTab(string tab) => ModeFor(tab) != null;

    /// <summary>
    /// The mode the engine should be in: the selected tab's while the Oracle window is showing and
    /// a character is in the world, otherwise off.
    /// </summary>
    public static string Wanted(string tab, bool windowVisible, bool inWorld) =>
        windowVisible && inWorld ? ModeFor(tab) ?? Off : Off;

    /// <summary>
    /// Sends the command for the wanted mode if it differs from the last one sent (the first
    /// command waits for the world). Returns true when a command went out. A refused send leaves
    /// the state as it was and is retried after <see cref="RetryMs"/>.
    /// </summary>
    public bool Update(string tab, bool windowVisible, bool inWorld, long nowMs)
    {
        if (_sent == null && !inWorld) return false;
        string want = Wanted(tab, windowVisible, inWorld);
        if (want == _sent || nowMs < _retryAtMs) return false;
        if (!_send("/rc oracle-skills " + want))
        {
            _retryAtMs = nowMs + RetryMs;
            return false;
        }
        _sent = want;
        return true;
    }

    /// <summary>
    /// Unload: leave the engine with no embedded body, whatever was shown. One attempt; nothing is
    /// kept to retry. Returns true when a command went out.
    /// </summary>
    public bool Shutdown()
    {
        if (_sent == null || _sent == Off) return false;
        bool ok;
        try { ok = _send("/rc oracle-skills " + Off); }
        catch { ok = false; }
        if (ok) _sent = Off;
        return ok;
    }
}
