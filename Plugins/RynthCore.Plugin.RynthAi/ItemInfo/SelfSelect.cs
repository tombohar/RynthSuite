using System;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

/// <summary>
/// Objects RynthAi selects by itself (the looter picking an item up, a meta's selectitem, combat's
/// native attack selecting its target), so the selection-change handler can tell them from the
/// player's own clicks: Item Info on select prints only for the player's, and "Fight the monster I
/// select" (CombatManager.SelectedTarget.cs) follows only the player's. <see cref="Mark"/> goes
/// right before Host.SelectItem (or use <see cref="SelectAsBot"/>).
/// The last few marks are kept (not just one): combat re-selects its target on every native
/// attack, and a looter pick a moment later must not make that one look like the player's.
/// </summary>
internal static class SelfSelect
{
    /// <summary>A selection change this soon after <see cref="Mark"/>, for the same id, is ours.</summary>
    internal const long WindowMs = 2000;

    internal static Func<long> Clock = () => Environment.TickCount64;

    private const int Slots = 8;
    private static readonly uint[] _ids = new uint[Slots];
    private static readonly long[] _ats = new long[Slots];
    private static int _next;
    private static readonly object _gate = new();

    internal static void Mark(uint id)
    {
        if (id == 0) return;
        lock (_gate)
        {
            long now = Clock();
            // The same id again refreshes its slot (native attacks mark the target every swing).
            for (int i = 0; i < Slots; i++)
                if (_ids[i] == id) { _ats[i] = now; return; }
            _ids[_next] = id;
            _ats[_next] = now;
            _next = (_next + 1) % Slots;
        }
    }

    internal static bool IsOurs(uint id)
    {
        if (id == 0) return false;
        lock (_gate)
        {
            long now = Clock();
            for (int i = 0; i < Slots; i++)
                if (_ids[i] == id && now - _ats[i] <= WindowMs) return true;
            return false;
        }
    }

    /// <summary>Forgets every mark (tests).</summary>
    internal static void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ids);
            Array.Clear(_ats);
            _next = 0;
        }
    }

    /// <summary>Marks <paramref name="id"/> as RynthAi's own pick, then selects it.</summary>
    internal static bool SelectAsBot(this RynthCore.PluginSdk.RynthCoreHost host, uint id)
    {
        Mark(id);
        return host.SelectItem(id);
    }
}
