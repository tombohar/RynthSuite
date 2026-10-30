using System;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthInventory;

/// <summary>What the window reads and the actions it can take. Plugin pump thread only.</summary>
internal sealed class InventoryContext
{
    public RynthCoreHost Host;
    public readonly InventorySettings Settings = new();
    public InventoryStore Store = new();

    /// <summary>The merged view of every file (and this client's live snapshot); null until first built.</summary>
    public InventoryIndex? Index;
    /// <summary>Bumped whenever <see cref="Index"/> is replaced.</summary>
    public int IndexRevision;
    public int BrokenFiles;
    public bool Loading;

    public bool InWorld;
    public uint Player;
    public string Server = string.Empty;
    public string Account = string.Empty;
    public string Character = string.Empty;
    /// <summary>This character's latest snapshot (never changed once published; a change makes a new one).</summary>
    public CharacterSnapshot? Live;
    public DateTime LastSavedUtc;
    public string LastSaveError = string.Empty;
    /// <summary>The storage container open right now, if it is being recorded (0 none).</summary>
    public uint OpenStorage;
    public string OpenStorageLabel = string.Empty;

    /// <summary>The engine draws icons in script windows (op level 2).</summary>
    public bool IconsAvailable;
    public DateTime UtcNow;

    public Action RequestRescan = () => { };
    public Action RequestReload = () => { };
    /// <summary>Forgets a character (moves its file aside). Returns a message for the player.</summary>
    public Func<CharacterInfo, string> Forget = _ => string.Empty;

    /// <summary>
    /// The plugin's chat lines, queued and written by the tick (Plugins/Shared/CommandReply.cs: the host
    /// drops a line within 100 ms of another plugin's). Null before Initialize.
    /// </summary>
    public CommandReply? Reply;

    public void Print(string text) => Print(text, ok: true);

    /// <summary>A line saying a command didn't work (the failure colour).</summary>
    public void PrintFail(string text) => Print(text, ok: false);

    private void Print(string text, bool ok)
    {
        if (Reply != null) { Reply.Say(ok, text); return; }
        if (!Host.WriteToChat("[RynthInventory] " + text, ok ? CommandReply.OkChatType : CommandReply.FailChatType))
            Host.Log("[RynthInventory] " + text);
    }
}
