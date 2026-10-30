using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's side of the "RynthAi.Yield" interface (Shared/RynthAiYieldApi.cs, 2026-10-29): a plugin
/// acting for the player (RynthDefective's Shortcuts bar) takes the player's turn, and RynthAi starts
/// nothing new until it has gone (ActionPacer.HoldForPlayer). Runs on the plugin pump thread (the
/// caller's OnTick), on the current plugin instance.
/// </summary>
internal static unsafe class YieldInterface
{
    private static RynthAiYieldApiV1* _table;

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiYieldApiV1.InterfaceName || version == 0 || version > RynthAiYieldApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiYieldApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiYieldApiV1));
            t->Version = RynthAiYieldApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthAiYieldApiV1);
            t->HoldForPlayer = &HoldForPlayer;
            t->ReleaseHold = &ReleaseHold;
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
    private static int HoldForPlayer(uint ms)
    {
        try { return PluginExports.Runtime.Plugin?.HoldForPlayer(ms) == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReleaseHold()
    {
        try
        {
            PluginExports.Runtime.Plugin?.ReleasePlayerHold();
            return 1;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed partial class RynthAiPlugin
{
    /// <summary>The player's turn for another plugin (RynthAi.Yield); false before login or with pacing off.</summary>
    internal bool HoldForPlayer(uint ms)
    {
        if (!_loginComplete) return false;
        return ActionPacer.HoldForPlayer(Host, Math.Min(ms, RynthAiYieldApiV1.MaxHoldMs));
    }

    internal void ReleasePlayerHold() => ActionPacer.ReleasePlayerHold(Host);
}
