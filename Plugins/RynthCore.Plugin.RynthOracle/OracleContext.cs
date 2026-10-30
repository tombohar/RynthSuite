using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle;

/// <summary>Everything the views read and the actions they can take. Pump thread only.</summary>
internal sealed class OracleContext
{
    public RynthCoreHost Host;
    public readonly OracleSettings Settings = new();
    public readonly QuestFlagStore Flags = new();
    public readonly QuestCatalog Catalog = new();
    public readonly QuestTimerWatcher Timers = new();
    public readonly QuestCatalogUpdater Updater = new();
    /// <summary>This character's tracked quests and recorded rewards (loaded once the name is known).</summary>
    public readonly QuestBook Book = new();
    public readonly CharacterSnapshot Character = new();
    public readonly VoidTracker Void = new();
    public readonly FellowshipTracker Fellowship = new();
    public readonly ServerIdentity Server = new();
    // A server's own screens are its server plugin's (2026-10-20, 2026-10-21): nothing here is one server's.
    /// <summary>RynthAi's session (the one session every tracker shows); read while the Character tab shows.</summary>
    public readonly RynthCore.Plugin.Shared.RynthAiSessionClient AiSession = new();
    /// <summary>The last session RynthAi reported; SessionKnown false without RynthAi.</summary>
    public RynthCore.Plugin.Shared.SessionStatsV1 Session;
    public bool SessionKnown;
    /// <summary>RynthCore's server list (API v86) and the active server owner's settings; State null on older engines.</summary>
    public readonly RynthCore.Plugin.Shared.ServerStateClient ServerState = new();
    /// <summary>
    /// The active server plugin's quest pack ("RynthServer.QuestPack"): whatever the plugin RynthCore names
    /// as the active server's owner offers; none without one (retail quests only).
    /// </summary>
    public readonly RynthCore.Plugin.Shared.ServerQuestPackClient QuestPacks = new();
    /// <summary>
    /// The active server's Void DoT duration rule: its plugin's profile value "voidDotDurationScale"
    /// (RynthConquest publishes Conquest's), 1 on every other server or without the plugin.
    /// </summary>
    public double VoidDotScale = 1.0;

    /// <summary>Titles the character holds (engine API v75); TitlesKnown false until the engine has the list.</summary>
    public readonly System.Collections.Generic.HashSet<int> EarnedTitles = new();
    public bool TitlesKnown;
    public uint CurrentTitle;

    public bool InWorld;
    public long NowMs;
    public DateTime UtcNow;
    /// <summary>The last thing the quest list check said, shown on the About tab.</summary>
    public string UpdaterMessage = "";

    /// <summary>Set by the plugin: sends /myquests (reply hidden when the setting says so). False when nothing was sent (not in world, or one went out moments ago).</summary>
    public Func<bool> RequestQuests = () => false;

    /// <summary>Set by the plugin (the Refresh buttons): re-reads the character's stats and buffs, and RynthAi's session, at once.</summary>
    public Action RereadCharacter = () => { };

    /// <summary>Set by the plugin: re-reads the titles the engine holds for the character.</summary>
    public Action RereadTitles = () => { };

    /// <summary>Set by the plugin: reloads the quest list and server pack from their files and re-asks RynthCore which server this is.</summary>
    public Action ReloadQuestList = () => { };

    /// <summary>Where <see cref="Print"/> goes instead of the chat window (the offline tests).</summary>
    public Action<string>? PrintSink = null;

    /// <summary>Set by the plugin: sends a chat command to the server as if typed (the views' buttons).</summary>
    public Func<string, bool> Send = _ => false;

    /// <summary>Set by the plugin: runs a chat command through RynthCore's plugin dispatch (another plugin's window).</summary>
    public Func<string, bool> RunCommand = _ => false;

    /// <summary>Set by the plugin: "&lt;verb&gt; &lt;targets&gt;" for RynthNav's /rnav (go, route, arrow); false when RynthNav isn't there.</summary>
    public Func<string, bool> SendNav = _ => false;

    /// <summary>Set by the plugin: shows or hides the Quest Timers window.</summary>
    public Action<bool> SetQuestTimersWindow = _ => { };
    /// <summary>Set by the plugin: whether the Quest Timers window is showing.</summary>
    public Func<bool> QuestTimersWindowVisible = () => false;

    public Func<uint, int> Props => id => Character.Int(Host, id);

    /// <summary>
    /// The plugin's chat lines, queued and written by the tick (Plugins/Shared/CommandReply.cs: the host
    /// drops a line within 100 ms of another plugin's, so a quest's several lines used to go missing).
    /// Null before Initialize (and in the offline tests, which use <see cref="PrintSink"/>).
    /// </summary>
    public CommandReply? Reply;

    /// <summary>A line in the player's chat window only (nothing is sent to the server).</summary>
    public void Print(string text) => Print(text, ok: true);

    /// <summary>A line saying a command didn't work (the failure colour).</summary>
    public void PrintFail(string text) => Print(text, ok: false);

    private void Print(string text, bool ok)
    {
        if (PrintSink != null) { PrintSink(text); return; }
        if (Reply != null) { Reply.Say(ok, text); return; }
        if (!Host.WriteToChat("[RynthOracle] " + text, ok ? CommandReply.OkChatType : CommandReply.FailChatType))
            Host.Log("[RynthOracle] " + text);
    }

    /// <summary>A click on a quest: its info and hint text, link and flag history in chat (upstream "thinks" them).</summary>
    public void PrintQuest(Quest q)
    {
        foreach (string line in QuestText.Lines(q, Flags, Book, UtcNow)) Print(line);
    }
}
