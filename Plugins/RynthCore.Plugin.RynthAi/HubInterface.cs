using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's side of the "RynthAi.Hub" interface (Shared/RynthAiHubApi.cs): what the RynthILT
/// plugin (the Infinite Leaftide features, moved out of RynthAi 2026-10-13) borrows from RynthAi:
/// the Mini Remote that hosts its tabs, the /myquests tracker, spell names, VTank loot-profile
/// matching, the .usd importer drawn in its Gear tab, and the Mini Remote's session stats.
/// Unlike RynthAi.Status the table is never freed: RynthILT's render thread may still hold it
/// while plugins shut down (a plugin DLL is never unloaded, so the functions stay callable), so
/// Release only sets Version to 0 and every function answers "nothing" from then on.
/// </summary>
internal static unsafe class HubInterface
{
    private static RynthAiHubApiV1* _table;
    private static volatile bool _released;

    private static RynthAiPlugin? Plugin => _released ? null : PluginExports.Runtime.Plugin;

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiHubApiV1.InterfaceName || version == 0 || version > RynthAiHubApiV1.InterfaceVersion || _released)
            return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiHubApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiHubApiV1));
            t->StructSize = (uint)sizeof(RynthAiHubApiV1);
            t->MiniRemoteTab = &MiniRemoteTab;
            t->MiniRemoteVisible = &MiniRemoteVisible;
            t->QuestFlag = &QuestFlag;
            t->QuestStatus = &QuestStatus;
            t->QuestRefresh = &QuestRefresh;
            t->SpellName = &SpellName;
            t->LootKeeps = &LootKeeps;
            t->RenderSection = &RenderSection;
            t->SessionStats = &SessionStats;
            t->SessionReset = &SessionReset;
            t->SessionStatsMore = &SessionStatsMore;
            t->VaultedDungeonsEnabled = &VaultedDungeonsEnabled;
            t->BackAwayFrom = &BackAwayFrom;
            t->SetPlayerCounter = &SetPlayerCounter;
            t->Version = RynthAiHubApiV1.InterfaceVersion;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Plugin shutdown: Version 0 (callers re-query); the table itself stays allocated.</summary>
    public static void Release()
    {
        _released = true;
        if (_table != null) _table->Version = 0;
    }

    private static string? Utf8(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    /// <summary>Writes <paramref name="s"/> as NUL-terminated UTF-8 (truncated to fit); returns the bytes written.</summary>
    private static int WriteUtf8(string s, byte* dest, int capacity)
    {
        if (dest == null || capacity <= 0) return 0;
        byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
        int n = Math.Min(bytes.Length, capacity - 1);
        for (int i = 0; i < n; i++) dest[i] = bytes[i];
        dest[n] = 0;
        return n;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MiniRemoteTab(byte* tabId, byte* mode, byte* reply, int replyCapacity)
    {
        try
        {
            var plugin = Plugin;
            string? text = plugin?.MiniRemoteTabForIlt(Utf8(tabId) ?? string.Empty, Utf8(mode) ?? "show");
            if (text == null) { WriteUtf8("The Mini Remote is not available yet (log in first).", reply, replyCapacity); return 0; }
            WriteUtf8(text, reply, replyCapacity);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MiniRemoteVisible()
    {
        try { return Plugin?.MiniRemoteVisibleForIlt == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int QuestFlag(byte* key, QuestFlagV1* flag)
    {
        try
        {
            var tracker = Plugin?.QuestTrackerForIlt;
            string? k = Utf8(key);
            if (tracker == null || string.IsNullOrWhiteSpace(k) || flag == null || !tracker.TryGetFlag(k, out var rec)) return 0;
            flag->Solves = rec.Solves;
            flag->MaxSolves = rec.MaxSolves;
            flag->CompletedOnUtcTicks = rec.CompletedOn.Ticks;
            flag->RepeatSeconds = (long)rec.RepeatTime.TotalSeconds;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int QuestStatus(QuestStatusV1* status)
    {
        try
        {
            var tracker = Plugin?.QuestTrackerForIlt;
            if (tracker == null || status == null) return 0;
            status->Disabled = tracker.Disabled ? 1 : 0;
            status->Count = tracker.Count;
            status->Generation = tracker.RefreshGeneration;
            status->Refreshing = tracker.IsRefreshing ? 1 : 0;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int QuestRefresh(byte* reply, int replyCapacity)
    {
        try
        {
            var remote = Plugin?.MiniRemoteForIlt;
            if (remote == null) { WriteUtf8("Quest tracker not ready.", reply, replyCapacity); return 0; }
            string text = remote.RefreshQuests();
            WriteUtf8(text, reply, replyCapacity);
            return text.StartsWith("Refreshing", StringComparison.Ordinal) ? 1 : 0;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SpellName(uint spellId, byte* buffer, int capacity)
    {
        try
        {
            if (_released || spellId == 0) return 0;
            string name = SpellDatabase.GetSpellName(unchecked((int)spellId));
            return string.IsNullOrEmpty(name) ? 0 : WriteUtf8(name, buffer, capacity);
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int LootKeeps(byte* utlPath, uint* ids, int count, byte* keep, byte* error, int errorCapacity)
    {
        try
        {
            var plugin = Plugin;
            string? path = Utf8(utlPath);
            if (plugin == null || string.IsNullOrWhiteSpace(path) || count < 0 || (count > 0 && (ids == null || keep == null)))
            {
                WriteUtf8("RynthAi isn't ready (log in first).", error, errorCapacity);
                return 0;
            }
            VTankLootProfile profile;
            try { profile = VTankLootParser.Load(path); }
            catch (Exception ex) { WriteUtf8("could not read profile: " + ex.Message, error, errorCapacity); return 0; }
            var cache = plugin.ObjectCacheForIlt;
            var host = plugin.HostForIlt;
            var ctx = new VTankLootContext(host, host.GetPlayerId()) { Cache = cache };
            for (int i = 0; i < count; i++)
            {
                keep[i] = 0;
                WorldObject? wo = cache?[unchecked((int)ids[i])];
                if (wo == null) continue;
                foreach (var rule in profile.Rules)
                {
                    if (!VTankLootEvaluator.Match(rule, wo, ctx)) continue;
                    if (rule.Action == VTankLootAction.Keep) keep[i] = 1;
                    break;
                }
            }
            return 1;
        }
        catch (Exception ex)
        {
            WriteUtf8("loot match failed: " + ex.Message, error, errorCapacity);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RenderSection(byte* id)
    {
        try
        {
            var remote = Plugin?.MiniRemoteForIlt;
            if (remote == null || Utf8(id) != RynthAiHub.SectionVTankImport) return 0;
            remote.Usd.Render();
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RynthAi.Hub RenderSection");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SessionStats(SessionStatsV1* stats)
    {
        try
        {
            var tracker = Plugin?.SessionStatsForIlt;
            if (tracker == null || stats == null) return 0;
            var v = tracker.View;
            stats->StartUtcTicks = v.SessionStartUtc.Ticks;
            stats->SessionSeconds = v.SessionSeconds;
            stats->Xp = v.Xp;
            stats->XpPerHour = v.XpPerHour;
            stats->Lum = v.Lum;
            stats->LumPerHour = v.LumPerHour;
            stats->LumLast5Min = v.LumLast5Min;
            stats->PassUpTracked = v.PassUpTracked ? 1 : 0;
            stats->LumPassUp = v.LumPassUp;
            stats->PassUpPerHour = v.PassUpPerHour;
            stats->Kills = v.Kills;
            stats->KillsPerHour = v.KillsPerHour;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>The rest of the same session (RynthTracker's panel shows it whole). 0 before login.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SessionStatsMore(SessionStatsMoreV1* more)
    {
        try
        {
            var tracker = Plugin?.SessionStatsForIlt;
            if (tracker == null || more == null) return 0;
            var v = tracker.View;
            more->XpPerKill = v.XpPerKill;
            more->Deaths = v.Deaths;
            more->MaxLevel = v.MaxLevel ? 1 : 0;
            more->Rad = v.Rad;
            more->RadPerHour = v.RadPerHour;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>Obsolete slot: the profile's old Vaulted Dungeons value (RynthILT's own setting since 2026-10-20; an older RynthILT reads this).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int VaultedDungeonsEnabled()
    {
        try { return Plugin?.VaultedDungeonsEnabledForIlt == true ? 1 : 0; }
        catch { return 0; }
    }

    /// <summary>A server plugin's back-away (RynthILT: the Empyrean Handler's ring); BackAway* results, 0 before login.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BackAwayFrom(uint objectId, float radiusYards, int holdMs)
    {
        try { return Plugin?.BackAwayForServer(objectId, radiusYards, holdMs) ?? RynthAiHub.BackAwayNotNow; }
        catch { return RynthAiHub.BackAwayNotNow; }
    }

    /// <summary>A wield counter from the server plugin (T11 "Can Wield"); 0 for an id that is no counter, or after shutdown.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetPlayerCounter(uint propertyId, long value)
    {
        try { return !_released && T11ItemSupport.SetCounter(propertyId, value) ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SessionReset()
    {
        try { Plugin?.SessionStatsForIlt?.RequestReset(); }
        catch { }
    }
}
