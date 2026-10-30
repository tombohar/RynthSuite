// ============================================================================
//  RynthCore.TerrainData - DatDatabase.cs
//  Read-only access to Asheron's Call .dat archives (client_portal.dat,
//  client_cell_1.dat, ...). Written for RynthCore (MIT) on 2026-10-05.
//
//  Written from the dat container format as it is publicly documented, with
//  Chorizite's DatReaderWriter (MIT, notice below) used as the format reference:
//    - file header at byte 0x140: magic, block size, file size, data set,
//      subset, free head / tail / count, then the byte offset of the root
//      B-tree node (all little-endian uint32);
//    - storage is fixed-size blocks; a block's first uint32 is the byte offset
//      of the next block of the same chain (0 ends the chain), the remaining
//      BlockSize - 4 bytes are payload;
//    - the directory is a B-tree whose nodes are stored as block chains: 62
//      uint32 child offsets, a uint32 entry count (at most 61), then the
//      entries, 24 bytes each: flags/version, id, byte offset of the file's
//      first block, size, timestamp, iteration. Entries are sorted by id,
//      child i holds the ids between entry i-1 and entry i, and a node whose
//      first child offset is 0 is a leaf.
//
//  Nothing is indexed up front: lookups descend the tree from the root. Every
//  interior node is kept once read (about 1 node in 62, a few hundred KB for
//  client_cell_1.dat) and leaves go through a small LRU, so a lookup usually
//  costs one leaf read and neighbouring ids (adjacent landblocks) none. The
//  public API (null when a file or the database is missing) is the one
//  RynthAi, RynthVision, RynthJuice and the RynthNav tools already use.
//
//  Thread-safety: every stream read and cache access holds one lock, so one
//  database can be shared between threads. NativeAOT/trimming safe (no
//  reflection).
//
//  Chorizite DatReaderWriter notice (format reference):
//    Copyright 2024 ACClientLib
//    Permission is hereby granted, free of charge, to any person obtaining a
//    copy of this software and associated documentation files (the
//    "Software"), to deal in the Software without restriction, including
//    without limitation the rights to use, copy, modify, merge, publish,
//    distribute, sublicense, and/or sell copies of the Software, and to permit
//    persons to whom the Software is furnished to do so, subject to the
//    following conditions: The above copyright notice and this permission
//    notice shall be included in all copies or substantial portions of the
//    Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
//    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN
//    NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
//    DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
//    OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
//    USE OR OTHER DEALINGS IN THE SOFTWARE.
// ============================================================================

// The API returns null for "not there" without annotations; its callers
// (RynthAi, RynthJuice, RynthVision) are written against that.
#nullable disable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// A read-only .dat archive. <see cref="Open"/> reads the header and checks
    /// the root directory node; files are then found by id
    /// (<see cref="FindFile"/>, <see cref="GetFileData"/>) by searching the
    /// on-disk B-tree. Methods return null / empty when the database isn't open
    /// or the file isn't there; nothing throws for a missing or damaged file.
    /// </summary>
    public class DatDatabase : IDisposable
    {
        // ---- container layout ----
        private const long HeaderOffset = 0x140;
        private const int HeaderBytes = 9 * 4;            // magic .. root offset
        private const int ChildSlots = 62;
        private const int MaxEntriesPerNode = 61;
        private const int EntryBytes = 24;
        private const int CountOffset = ChildSlots * 4;                         // 248
        private const int EntriesOffset = CountOffset + 4;                      // 252
        private const int NodeBytes = EntriesOffset + MaxEntriesPerNode * EntryBytes; // 1716

        // A real tree is 4 levels deep at most; this only stops a damaged child
        // pointer from sending a search round in circles.
        private const int DepthLimit = 16;

        // Cache budget: all interior nodes (capped), a 128-leaf LRU.
        private const int LeafLruSize = 128;
        private const int InteriorCap = 4096;

        private readonly object _gate = new object();
        private FileStream _file;
        private long _length;
        private byte[] _nodeBuf;   // one node's bytes, reused (under _gate)
        private readonly Dictionary<uint, Node> _interior = new Dictionary<uint, Node>();
        private readonly LruCache<uint, Node> _leaves = new LruCache<uint, Node>(LeafLruSize);
        private int _count = -1;

        /// <summary>Header word at 0x140 (the dat magic).</summary>
        public uint FileType { get; private set; }
        /// <summary>Block size in bytes (1024 in the retail dats).</summary>
        public uint BlockSize { get; private set; }
        /// <summary>File size recorded in the header.</summary>
        public uint FileSize { get; private set; }
        /// <summary>The header's data set (database type) word.</summary>
        public uint DataSet { get; private set; }
        /// <summary>Byte offset of the root directory node.</summary>
        public uint BTreeRoot { get; private set; }

        /// <summary>True between a successful <see cref="Open"/> and <see cref="Close"/>.</summary>
        public bool IsLoaded { get; private set; }
        /// <summary>The path last passed to <see cref="Open"/>.</summary>
        public string FilePath { get; private set; }

        /// <summary>Diagnostics from Open and failed reads (first 100 lines).</summary>
        public List<string> DiagLog { get; } = new List<string>();

        /// <summary>
        /// Number of files in the dat. Counted on first use from the directory
        /// nodes' entry counts (only the first bytes of each node are read) and
        /// remembered until <see cref="Close"/>; 0 when not open.
        /// </summary>
        public int RecordCount
        {
            get
            {
                lock (_gate)
                {
                    if (!IsLoaded) return 0;
                    if (_count < 0) _count = CountUnder(BTreeRoot, 0);
                    return _count;
                }
            }
        }

        private sealed class Node
        {
            public uint[] Children;          // entry count + 1 offsets; null for a leaf
            public DatBTreeEntry[] Entries;  // ascending ObjectId
        }

        // ------------------------------------------------------------------ open / close

        /// <summary>Opens <paramref name="path"/> read-only. False (and a DiagLog line) on any failure.</summary>
        public bool Open(string path)
        {
            lock (_gate)
            {
                CloseLocked();
                FilePath = path;
                try
                {
                    if (!File.Exists(path)) { Log($"Not found: {path}"); return false; }

                    // acclient.exe keeps its own handles on the dats and may open
                    // them after us (RynthAi opens at plugin init), so share
                    // everything, including delete: the client's open asks for
                    // delete-rename access and fails ("cannot access the data
                    // files") if an earlier handle refuses it.
                    _file = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                    _length = _file.Length;

                    Span<byte> h = stackalloc byte[HeaderBytes];
                    if (!ReadAt(HeaderOffset, h)) { Log("Header is short"); CloseLocked(); return false; }
                    FileType = BinaryPrimitives.ReadUInt32LittleEndian(h);
                    BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4));
                    FileSize = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(8));
                    DataSet = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(12));
                    BTreeRoot = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(32));
                    Log($"Header: type=0x{FileType:X8} blockSize={BlockSize} fileSize={FileSize} dataSet=0x{DataSet:X8} root=0x{BTreeRoot:X8} (file {_length} bytes)");

                    if (BlockSize <= 4 || BlockSize > 0x10000) { Log($"Bad block size {BlockSize}"); CloseLocked(); return false; }
                    if (BTreeRoot == 0 || BTreeRoot >= _length) { Log($"Bad root offset 0x{BTreeRoot:X8}"); CloseLocked(); return false; }

                    _nodeBuf = new byte[NodeBytes];
                    IsLoaded = true;
                    if (GetNode(BTreeRoot) == null)
                    {
                        Log($"Root node at 0x{BTreeRoot:X8} doesn't parse");
                        CloseLocked();
                        return false;
                    }
                    Log($"Opened {Path.GetFileName(path)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Open failed: {ex.Message}");
                    CloseLocked();
                    return false;
                }
            }
        }

        /// <summary>Closes the file and drops the cached directory nodes.</summary>
        public void Close()
        {
            lock (_gate) CloseLocked();
        }

        public void Dispose() { Close(); }

        private void CloseLocked()
        {
            IsLoaded = false;
            _file?.Dispose();
            _file = null;
            _length = 0;
            _nodeBuf = null;
            _interior.Clear();
            _leaves.Clear();
            _count = -1;
        }

        // ------------------------------------------------------------------ lookups

        /// <summary>The directory entry for <paramref name="fileId"/>, or null.</summary>
        public DatBTreeEntry FindFile(uint fileId)
        {
            lock (_gate)
            {
                if (!IsLoaded) return null;
                uint at = BTreeRoot;
                for (int depth = 0; depth < DepthLimit && at != 0; depth++)
                {
                    Node n = GetNode(at);
                    if (n == null) return null;
                    int i = LowerBound(n.Entries, fileId);
                    if (i < n.Entries.Length && n.Entries[i].ObjectId == fileId) return n.Entries[i];
                    if (n.Children == null) return null;
                    at = n.Children[i];
                }
                return null;
            }
        }

        /// <summary>The bytes of the file <paramref name="entry"/> describes, or null.</summary>
        public byte[] ReadFileData(DatBTreeEntry entry)
        {
            if (entry == null) return null;
            lock (_gate)
            {
                if (!IsLoaded) return null;
                if (entry.FileSize > _length) { Log($"0x{entry.ObjectId:X8}: size {entry.FileSize} exceeds the file"); return null; }
                var data = new byte[entry.FileSize];
                if (ReadChain(entry.FileOffset, data) != data.Length)
                {
                    Log($"0x{entry.ObjectId:X8}: block chain at 0x{entry.FileOffset:X8} is broken");
                    return null;
                }
                return data;
            }
        }

        /// <summary><see cref="FindFile"/> then <see cref="ReadFileData"/>.</summary>
        public byte[] GetFileData(uint fileId) => ReadFileData(FindFile(fileId));

        /// <summary>
        /// The interior (EnvCell) ids 0xXXYY0100-0xXXYYFFFD of landblock
        /// <paramref name="landblockKey"/> (0xXXYY), ascending. Only the nodes
        /// whose id range overlaps the landblock are read.
        /// </summary>
        public List<uint> GetLandblockCellIds(uint landblockKey)
        {
            var found = new List<uint>();
            lock (_gate)
            {
                if (!IsLoaded) return found;
                uint lo = (landblockKey << 16) | 0x0100;
                uint hi = (landblockKey << 16) | 0xFFFD;
                CollectRange(BTreeRoot, lo, hi, found, 0);
            }
            return found;
        }

        // In-order walk of the part of the subtree at `at` holding ids in [lo, hi].
        private void CollectRange(uint at, uint lo, uint hi, List<uint> into, int depth)
        {
            if (at == 0 || depth >= DepthLimit) return;
            Node n = GetNode(at);
            if (n == null) return;
            var e = n.Entries;
            for (int i = 0; i <= e.Length; i++)
            {
                // Child i spans (e[i-1], e[i]); visit it if that overlaps [lo, hi].
                if (n.Children != null)
                {
                    bool aboveLo = i == e.Length || e[i].ObjectId > lo;
                    bool belowHi = i == 0 || e[i - 1].ObjectId < hi;
                    if (aboveLo && belowHi) CollectRange(n.Children[i], lo, hi, into, depth + 1);
                }
                if (i == e.Length) break;
                uint id = e[i].ObjectId;
                if (id > hi) break;
                if (id >= lo) into.Add(id);
            }
        }

        /// <summary>
        /// Every entry, ascending by id, read from the tree as it goes (nothing
        /// is kept afterwards). Reads every node of the dat: for diagnostics and
        /// offline tools, not for lookups.
        /// </summary>
        public IEnumerable<DatBTreeEntry> EnumerateEntries()
        {
            Node root;
            lock (_gate)
            {
                if (!IsLoaded) yield break;
                root = GetNodeUncached(BTreeRoot);
            }
            if (root == null) yield break;

            // Explicit stack (no recursion through iterators); the lock is taken
            // per node read, never held across a yield.
            var stack = new Stack<(Node node, int next)>();
            stack.Push((root, 0));
            DescendLeft(stack);
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next >= node.Entries.Length) continue;
                yield return node.Entries[next];
                stack.Push((node, next + 1));
                if (node.Children != null)
                {
                    Node child = ReadForWalk(node.Children[next + 1], stack.Count);
                    if (child != null) { stack.Push((child, 0)); DescendLeft(stack); }
                }
            }
        }

        // Pushes the leftmost path below the node on top of the stack.
        private void DescendLeft(Stack<(Node node, int next)> stack)
        {
            while (true)
            {
                var top = stack.Peek().node;
                if (top.Children == null) return;
                Node child = ReadForWalk(top.Children[0], stack.Count);
                if (child == null) return;
                stack.Push((child, 0));
            }
        }

        private Node ReadForWalk(uint at, int depth)
        {
            if (at == 0 || depth >= DepthLimit) return null;
            lock (_gate)
            {
                if (!IsLoaded) return null;
                return GetNodeUncached(at);
            }
        }

        /// <summary>The lowest file ids in the dat (up to <paramref name="maxCount"/>), for diagnostics.</summary>
        public List<uint> GetSampleIds(int maxCount = 20)
        {
            var ids = new List<uint>();
            if (maxCount <= 0) return ids;
            foreach (var e in EnumerateEntries())
            {
                ids.Add(e.ObjectId);
                if (ids.Count >= maxCount) break;
            }
            return ids;
        }

        // ------------------------------------------------------------------ nodes

        // First entry index whose id is >= id (Length when none).
        private static int LowerBound(DatBTreeEntry[] e, uint id)
        {
            int a = 0, b = e.Length;
            while (a < b)
            {
                int m = (a + b) >> 1;
                if (e[m].ObjectId < id) a = m + 1; else b = m;
            }
            return a;
        }

        // Cached node. Caller holds _gate.
        private Node GetNode(uint at)
        {
            if (_interior.TryGetValue(at, out Node n)) return n;
            if (_leaves.TryGet(at, out n)) return n;
            n = ParseNode(at);
            if (n == null) return null;
            if (n.Children != null)
            {
                if (_interior.Count < InteriorCap) _interior[at] = n;
            }
            else _leaves.Set(at, n);
            return n;
        }

        // For full walks: use a cached node if there is one, but don't fill the
        // caches with the whole tree. Caller holds _gate.
        private Node GetNodeUncached(uint at)
        {
            if (_interior.TryGetValue(at, out Node n)) return n;
            if (_leaves.TryGet(at, out n)) return n;
            return ParseNode(at);
        }

        // Reads and decodes the node at byte offset `at`. Caller holds _gate.
        private Node ParseNode(uint at)
        {
            byte[] buf = _nodeBuf;
            int got = ReadChain(at, buf);
            if (got < EntriesOffset) return null;
            ReadOnlySpan<byte> s = buf;
            int count = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(CountOffset));
            if (count < 0 || count > MaxEntriesPerNode || got < EntriesOffset + count * EntryBytes) return null;

            var entries = new DatBTreeEntry[count];
            for (int i = 0; i < count; i++)
            {
                var r = s.Slice(EntriesOffset + i * EntryBytes, EntryBytes);
                entries[i] = new DatBTreeEntry
                {
                    BitFlags = BinaryPrimitives.ReadUInt32LittleEndian(r),
                    ObjectId = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(4)),
                    FileOffset = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(8)),
                    FileSize = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(12)),
                };
            }

            uint[] children = null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(s) != 0)
            {
                children = new uint[count + 1];
                for (int i = 0; i <= count; i++)
                    children[i] = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i * 4));
            }
            return new Node { Children = children, Entries = entries };
        }

        // Node entry counts under `at`, reading only each node's first bytes
        // (child table + count). Caller holds _gate.
        private int CountUnder(uint at, int depth)
        {
            if (at == 0 || depth >= DepthLimit) return 0;
            Span<byte> head = stackalloc byte[EntriesOffset];
            if (ReadChain(at, head) != EntriesOffset) return 0;
            int count = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(CountOffset));
            if (count < 0 || count > MaxEntriesPerNode) return 0;
            int total = count;
            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0)
            {
                // Copy the child offsets out: the recursion reuses the stack.
                Span<uint> kids = stackalloc uint[count + 1];
                for (int i = 0; i <= count; i++) kids[i] = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(i * 4));
                for (int i = 0; i <= count; i++) total += CountUnder(kids[i], depth + 1);
            }
            return total;
        }

        // ------------------------------------------------------------------ blocks

        // Copies the block chain starting at byte offset `first` into `dest`
        // and returns how many bytes it filled: dest.Length, or less when the
        // chain ends (a 0 link) first; -1 when a link points outside the file.
        // Caller holds _gate.
        private int ReadChain(uint first, Span<byte> dest)
        {
            int payload = (int)BlockSize - 4;
            long block = first;
            int done = 0;
            Span<byte> link = stackalloc byte[4];
            while (done < dest.Length)
            {
                if (block == 0) return done;
                if (block + 4 > _length) return -1;
                int take = Math.Min(payload, dest.Length - done);
                if (!ReadAt(block, link) || !ReadAt(block + 4, dest.Slice(done, take))) return -1;
                done += take;
                block = BinaryPrimitives.ReadUInt32LittleEndian(link);
            }
            return done;
        }

        // Caller holds _gate (or is Open, before anything else can see the file).
        private bool ReadAt(long offset, Span<byte> into)
        {
            if (offset < 0 || offset + into.Length > _length) return false;
            _file.Position = offset;
            int got = 0;
            while (got < into.Length)
            {
                int n = _file.Read(into.Slice(got));
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private void Log(string msg)
        {
            string line = "[DatDB] " + msg;
            System.Diagnostics.Debug.WriteLine(line);
            if (DiagLog.Count < 100) DiagLog.Add(line);
        }
    }

    /// <summary>One directory entry: a file's id, flags, first block and size.</summary>
    public class DatBTreeEntry
    {
        public uint BitFlags;
        public uint ObjectId;
        public uint FileOffset;  // byte offset of the file's first block
        public uint FileSize;
    }
}
