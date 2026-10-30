using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's side of the "RynthAi.Activity" interface (Shared/RynthAiActivityApi.cs): what the bot
/// is doing right now, as bits, for plugins that move items in the character's packs (RynthSort)
/// and must not land a move on top of a loot, salvage, buff, use or cast. Read only. Runs on the
/// plugin pump thread (the caller's OnTick), on the current plugin instance.
/// </summary>
internal static unsafe class ActivityInterface
{
    private static RynthAiActivityApiV1* _table;

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiActivityApiV1.InterfaceName || version == 0 || version > RynthAiActivityApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiActivityApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiActivityApiV1));
            t->Version = RynthAiActivityApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthAiActivityApiV1);
            t->GetActivityFlags = &GetActivityFlags;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Frees the table (plugin shutdown). Callers resolve it every tick.</summary>
    public static void Release()
    {
        var t = _table;
        _table = null;
        if (t != null) NativeMemory.Free(t);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetActivityFlags(uint* flags)
    {
        try
        {
            if (flags == null) return 0;
            *flags = 0;
            RynthAiActivity? a = PluginExports.Runtime.Plugin?.CurrentActivityFlags();
            if (a == null) return 0;
            *flags = (uint)a.Value;
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The bits from RynthAi's state (pure, so the host tests check the mapping).</summary>
    internal static RynthAiActivity Compose(BotActivity activity, bool macroRunning, bool corpseInHand, bool salvageBusy,
                                            bool serverWait, bool crafting, bool autoCram, bool pacedWait = false)
    {
        RynthAiActivity f = RynthAiActivity.None;
        if (macroRunning) f |= RynthAiActivity.MacroRunning;
        switch (activity)
        {
            case BotActivity.Buffing: f |= RynthAiActivity.Buffing; break;
            case BotActivity.Combat: f |= RynthAiActivity.Combat; break;
            case BotActivity.Looting: f |= RynthAiActivity.Looting; break;
            case BotActivity.Salvaging: f |= RynthAiActivity.Salvaging; break;
            case BotActivity.Navigating: f |= RynthAiActivity.Navigating; break;
        }
        if (corpseInHand) f |= RynthAiActivity.Looting;
        if (salvageBusy) f |= RynthAiActivity.Salvaging;
        if (serverWait) f |= RynthAiActivity.ServerAction;
        if (crafting) f |= RynthAiActivity.Crafting;
        if (autoCram) f |= RynthAiActivity.AutoCram;
        if (pacedWait) f |= RynthAiActivity.PacedAction;
        return f;
    }
}

public sealed partial class RynthAiPlugin
{
    /// <summary>What the bot is doing now (RynthAi.Activity); null before login.</summary>
    internal RynthAiActivity? CurrentActivityFlags()
    {
        var st = _dashboard?.Settings;
        if (st == null || !_loginComplete) return null;
        bool pacedWait = ActionPacer.Enabled && ActionPacer.Current(Host) != null;
        bool serverWait = _busyCount > 0 || pacedWait;
        return ActivityInterface.Compose(
            st.IsMacroRunning ? _activity : BotActivity.Idle,
            st.IsMacroRunning,
            corpseInHand: _openedContainerId != 0 || _targetCorpseId != 0,
            salvageBusy: _salvageManager?.IsBusy == true,
            serverWait: serverWait,
            crafting: _missileCraftingManager?.IsCrafting == true,
            autoCram: st.EnableAutocram,
            pacedWait: pacedWait);
    }
}
