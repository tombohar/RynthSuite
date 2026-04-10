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
                return Marshal.ReadInt32(fel + OFF_FEL_CURR_NUM);
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
                return ReadPString(fel + OFF_FEL_NAME);
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
                return Marshal.ReadInt32(fel + OFF_FEL_LEADER);
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
                return Marshal.ReadInt32(fel + OFF_FEL_OPEN) == 1;
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
                return Marshal.ReadInt32(fel + OFF_FEL_LOCKED) == 1;
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
                return Marshal.ReadInt32(fel + OFF_FEL_SHARE_XP) == 1;
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
        int sysPtr = Marshal.ReadInt32(ADDR_FELLOWSHIP_SYSTEM);
        if (sysPtr == 0) return IntPtr.Zero;
        int felPtr = Marshal.ReadInt32(new IntPtr(sysPtr + OFF_SYS_FELLOWSHIP));
        if (felPtr == 0) return IntPtr.Zero;
        return new IntPtr(felPtr);
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

            int bucketsPtr = Marshal.ReadInt32(fel + OFF_FEL_BUCKETS);
            int tableSize  = Marshal.ReadInt32(fel + OFF_FEL_TABLE_SIZE);
            int currNum    = Marshal.ReadInt32(fel + OFF_FEL_CURR_NUM);

            if (bucketsPtr == 0 || tableSize == 0 || currNum == 0) return;

            int maxMembers = Math.Min(currNum, 9);
            int found = 0;

            for (int b = 0; b < tableSize && found < maxMembers; b++)
            {
                int entryPtr = Marshal.ReadInt32(new IntPtr(bucketsPtr + b * 4));
                while (entryPtr != 0 && found < maxMembers)
                {
                    int memberId = Marshal.ReadInt32(new IntPtr(entryPtr + OFF_ENTRY_KEY));
                    string name = ReadPString(new IntPtr(entryPtr + OFF_ENTRY_NAME_PTR));
                    if (memberId != 0 && !string.IsNullOrEmpty(name))
                    { _memberCache[memberId] = name; found++; }
                    entryPtr = Marshal.ReadInt32(new IntPtr(entryPtr + OFF_ENTRY_NEXT));
                }
            }
        }
        catch { }
    }

    private string ReadPString(IntPtr addr)
    {
        try
        {
            int bufPtr = Marshal.ReadInt32(addr);
            if (bufPtr == 0) return "";
            string? raw = Marshal.PtrToStringAnsi(new IntPtr(bufPtr + 0x14));
            if (raw == null) return "";
            if (raw.Length > 64) raw = raw[..64];
            return raw;
        }
        catch { return ""; }
    }
}
