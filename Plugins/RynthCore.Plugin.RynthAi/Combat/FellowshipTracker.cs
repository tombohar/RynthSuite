using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Reads fellowship membership directly from AC client memory.
///
/// Memory layout (from UB's AcClient structs / acclient.exe analysis):
///
/// Static pointers:
///   0x0087150C = ClientFellowshipSystem** s_pFellowshipSystem
///   0x00844C08 = UInt32* player_iid (our character ID)
///
/// ClientFellowshipSystem:
///   +0x10 = CFellowship* m_pFellowship (null if not in fellowship)
///
/// CFellowship / Fellowship:
///   +0x0C = PackableHashData** _buckets (hash table bucket array)
///   +0x10 = UInt32 _table_size (number of buckets)
///   +0x14 = UInt32 _currNum (member count)
///   +0x18 = PStringBase _name (fellowship name, char*)
///   +0x1C = UInt32 _leader (leader character ID)
///   +0x20 = int _share_xp
///   +0x24 = int _even_xp_split
///   +0x28 = int _open_fellow
///   +0x2C = int _locked
///
/// PackableHashData (hash table entry for each member):
///   +0x00 = UInt32 _key (member character ID)
///   +0x04 = Fellow _data:
///       +0x04 = Fellow.PackObj vtable (4 bytes)
///       +0x08 = Fellow._name (PStringBase = char*, 4 bytes)
///       +0x0C = Fellow._level (UInt32)
///       +0x1C = Fellow._share_loot (int)
///       +0x20 = Fellow._max_health (UInt32)
///       +0x28 = Fellow._max_mana (UInt32)
///       +0x2C = Fellow._current_health (UInt32)
///   +0x34 = PackableHashData* _next (next in chain, null = end)
/// </summary>
public class FellowshipTracker : IDisposable
{
    // Static memory addresses in acclient.exe
    private static readonly IntPtr ADDR_FELLOWSHIP_SYSTEM = new IntPtr(0x0087150C);
    private static readonly IntPtr ADDR_PLAYER_IID = new IntPtr(0x00844C08);

    // ── Page-validity probe ─────────────────────────────────────────────
    // Every read below is a raw dereference into AC's single-threaded native
    // state (the fellowship object graph is mutated by AC's main thread with
    // no locking). In Decal-coexistence mode this class is ticked from
    // RynthAi's own 30 Hz pump thread, not AC's main thread, so a read here
    // can race a member join/leave/teardown and land on a freed or
    // mid-reassignment pointer. Under NativeAOT, try/catch does NOT reliably
    // catch the resulting access violation (see the engine's identical
    // IsReadablePointer in ClientObjectHooks.cs — same crash class, same
    // fix). VirtualQuery-probe every pointer before dereferencing it instead
    // of relying on the catch blocks to save us.
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint MEM_COMMIT = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern int VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, int dwLength);

    private static bool IsReadablePointer(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return false;
        if (VirtualQuery(ptr, out var mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        if ((mbi.Protect & PAGE_NOACCESS) != 0 || (mbi.Protect & PAGE_GUARD) != 0) return false;
        return true;
    }

    // Struct offsets
    private const int OFF_SYS_FELLOWSHIP = 0x10;
    private const int OFF_FEL_BUCKETS    = 0x0C;
    private const int OFF_FEL_TABLE_SIZE = 0x10;
    private const int OFF_FEL_CURR_NUM   = 0x14;
    private const int OFF_FEL_NAME       = 0x18;
    private const int OFF_FEL_LEADER     = 0x1C;
    private const int OFF_FEL_SHARE_XP   = 0x20;
    private const int OFF_FEL_EVEN_SPLIT = 0x24;
    private const int OFF_FEL_OPEN       = 0x28;
    private const int OFF_FEL_LOCKED     = 0x2C;

    // Hash entry offsets
    private const int OFF_ENTRY_KEY      = 0x00;
    private const int OFF_ENTRY_NAME_PTR = 0x08;
    private const int OFF_ENTRY_LEVEL    = 0x0C;
    private const int OFF_ENTRY_NEXT     = 0x34;

    private Dictionary<int, string> _memberCache = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private const double REFRESH_INTERVAL_MS = 2000;

    public FellowshipTracker() { }

    public void Dispose() { }

    public bool IsInFellowship
    {
        get { try { return GetFellowshipPtr() != IntPtr.Zero; } catch { return false; } }
    }

    public int MemberCount
    {
        get
        {
            try
            {
                IntPtr fel = GetFellowshipPtr();
                if (fel == IntPtr.Zero) return 0;
                IntPtr addr = fel + OFF_FEL_CURR_NUM;
                if (!IsReadablePointer(addr)) return 0;
                return Marshal.ReadInt32(addr);
            }
            catch { return 0; }
        }
    }

    public string FellowshipName
    {
        get
        {
            try
            {
                IntPtr fel = GetFellowshipPtr();
                if (fel == IntPtr.Zero) return "";
                IntPtr addr = fel + OFF_FEL_NAME;
                if (!IsReadablePointer(addr)) return "";
                return ReadPString(addr);
            }
            catch { return ""; }
        }
    }

    public int LeaderId
    {
        get
        {
            try
            {
                IntPtr fel = GetFellowshipPtr();
                if (fel == IntPtr.Zero) return 0;
                IntPtr addr = fel + OFF_FEL_LEADER;
                if (!IsReadablePointer(addr)) return 0;
                return Marshal.ReadInt32(addr);
            }
            catch { return 0; }
        }
    }

    public bool IsLeader
    {
        get
        {
            try
            {
                int leader = LeaderId;
                if (leader == 0) return false;
                if (!IsReadablePointer(ADDR_PLAYER_IID)) return false;
                int myId = Marshal.ReadInt32(ADDR_PLAYER_IID);
                return leader == myId;
            }
            catch { return false; }
        }
    }

    public bool IsOpen
    {
        get
        {
            try
            {
                IntPtr fel = GetFellowshipPtr();
                if (fel == IntPtr.Zero) return false;
                IntPtr addr = fel + OFF_FEL_OPEN;
                return IsReadablePointer(addr) && Marshal.ReadInt32(addr) == 1;
            }
            catch { return false; }
        }
    }

    public bool IsLocked
    {
        get
        {
            try
            {
                IntPtr fel = GetFellowshipPtr();
                if (fel == IntPtr.Zero) return false;
                IntPtr addr = fel + OFF_FEL_LOCKED;
                return IsReadablePointer(addr) && Marshal.ReadInt32(addr) == 1;
            }
            catch { return false; }
        }
    }

    public bool ShareXP
    {
        get
        {
            try
            {
                IntPtr fel = GetFellowshipPtr();
                if (fel == IntPtr.Zero) return false;
                IntPtr addr = fel + OFF_FEL_SHARE_XP;
                return IsReadablePointer(addr) && Marshal.ReadInt32(addr) == 1;
            }
            catch { return false; }
        }
    }

    public bool IsMember(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        RefreshIfNeeded();
        foreach (var kvp in _memberCache)
            if (kvp.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool IsMember(int characterId)
    {
        RefreshIfNeeded();
        return _memberCache.ContainsKey(characterId);
    }

    public IEnumerable<string> GetMemberNames()
    {
        RefreshIfNeeded();
        return _memberCache.Values;
    }

    public string GetMemberName(int index)
    {
        RefreshIfNeeded();
        int i = 0;
        foreach (var kvp in _memberCache) { if (i == index) return kvp.Value; i++; }
        return "";
    }

    public int GetMemberId(int index)
    {
        RefreshIfNeeded();
        int i = 0;
        foreach (var kvp in _memberCache) { if (i == index) return kvp.Key; i++; }
        return 0;
    }

    private IntPtr GetFellowshipPtr()
    {
        if (!IsReadablePointer(ADDR_FELLOWSHIP_SYSTEM)) return IntPtr.Zero;
        int sysPtr = Marshal.ReadInt32(ADDR_FELLOWSHIP_SYSTEM);
        if (sysPtr == 0) return IntPtr.Zero;
        IntPtr sysFieldAddr = new IntPtr(sysPtr + OFF_SYS_FELLOWSHIP);
        if (!IsReadablePointer(sysFieldAddr)) return IntPtr.Zero;
        int felPtr = Marshal.ReadInt32(sysFieldAddr);
        if (felPtr == 0) return IntPtr.Zero;
        IntPtr fel = new IntPtr(felPtr);
        return IsReadablePointer(fel) ? fel : IntPtr.Zero;
    }

    private void RefreshIfNeeded()
    {
        if ((DateTime.Now - _lastRefresh).TotalMilliseconds < REFRESH_INTERVAL_MS) return;
        _lastRefresh = DateTime.Now;
        _memberCache.Clear();
        try
        {
            IntPtr fel = GetFellowshipPtr();
            if (fel == IntPtr.Zero) return;

            IntPtr bucketsAddr = fel + OFF_FEL_BUCKETS;
            IntPtr tableSizeAddr = fel + OFF_FEL_TABLE_SIZE;
            IntPtr currNumAddr = fel + OFF_FEL_CURR_NUM;
            if (!IsReadablePointer(bucketsAddr) || !IsReadablePointer(tableSizeAddr) || !IsReadablePointer(currNumAddr))
                return;

            int bucketsPtr = Marshal.ReadInt32(bucketsAddr);
            int tableSize  = Marshal.ReadInt32(tableSizeAddr);
            int currNum    = Marshal.ReadInt32(currNumAddr);

            // Fellowship caps at 9 members; a corrupt/mid-mutation table_size
            // here (bogus large value) would otherwise turn the bucket loop
            // below into an unbounded scan over garbage addresses.
            if (bucketsPtr == 0 || tableSize == 0 || tableSize > 64 || currNum == 0 || currNum > 9)
                return;
            if (!IsReadablePointer(new IntPtr(bucketsPtr)))
                return;

            int maxMembers = Math.Min(currNum, 9);
            int found = 0;

            for (int b = 0; b < tableSize && found < maxMembers; b++)
            {
                IntPtr bucketSlotAddr = new IntPtr(bucketsPtr + b * 4);
                if (!IsReadablePointer(bucketSlotAddr)) continue;
                int entryPtr = Marshal.ReadInt32(bucketSlotAddr);

                // Chain guard: a corrupted/circular bucket chain (mid-mutation
                // read racing AC's main thread) must not spin this pump thread
                // forever — cap the walk independent of the found-count exit.
                int chainGuard = 0;
                while (entryPtr != 0 && found < maxMembers && chainGuard++ < 32)
                {
                    IntPtr entry = new IntPtr(entryPtr);
                    if (!IsReadablePointer(entry)) break;

                    IntPtr keyAddr = entry + OFF_ENTRY_KEY;
                    IntPtr nameFieldAddr = entry + OFF_ENTRY_NAME_PTR;
                    IntPtr nextAddr = entry + OFF_ENTRY_NEXT;
                    if (!IsReadablePointer(keyAddr) || !IsReadablePointer(nameFieldAddr) || !IsReadablePointer(nextAddr))
                        break;

                    int memberId = Marshal.ReadInt32(keyAddr);
                    string name = ReadPString(nameFieldAddr);
                    if (memberId != 0 && !string.IsNullOrEmpty(name))
                    { _memberCache[memberId] = name; found++; }
                    entryPtr = Marshal.ReadInt32(nextAddr);
                }
            }
        }
        catch { }
    }

    private string ReadPString(IntPtr addr)
    {
        try
        {
            if (!IsReadablePointer(addr)) return "";
            int bufPtr = Marshal.ReadInt32(addr);
            if (bufPtr == 0) return "";
            IntPtr strAddr = new IntPtr(bufPtr + 0x14);
            if (!IsReadablePointer(strAddr)) return "";
            string? raw = Marshal.PtrToStringAnsi(strAddr);
            if (raw == null) return "";
            if (raw.Length > 64) raw = raw[..64];
            return raw;
        }
        catch { return ""; }
    }
}
