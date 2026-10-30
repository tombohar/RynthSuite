using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RynthCore.Install;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// Players reported metas and navs "not saving" and the plugin folders being "read only"
// (2026-10-13). Every save now reports a failure (Plugins/Shared/DataWriteGuard.cs); this is the
// startup self-check that finds a folder the client can't write before anything is lost, and
// the tick that posts those lines once the character is in game.
public sealed partial class RynthAiPlugin
{
    /// <summary>The folders RynthAi saves metas, navs, loot and settings profiles in.</summary>
    internal static IEnumerable<string> DataFolders()
    {
        string root = RynthInstallPaths.RynthAiDir;
        yield return root;
        yield return Path.Combine(root, "MetaFiles");
        yield return Path.Combine(root, "NavProfiles");
        yield return Path.Combine(root, "LootProfiles");
        yield return Path.Combine(root, "SettingsProfiles");
    }

    private void StartDataFolderCheck()
    {
        DataWriteGuard.Reset();
        DataWriteGuard.LogSink = msg => Log(msg);
        // Off the init thread: a slow disk, antivirus or a network share mustn't hold up the client.
        Task.Run(() =>
        {
            try { DataWriteGuard.CheckFolders(DataFolders()); }
            catch { }
        });
    }

    private void PostDataWriteProblems()
    {
        if (DataWriteGuard.PendingCount == 0) return;
        DataWriteGuard.DrainChat(line => Host.WriteToChat(line, 2));
    }
}
