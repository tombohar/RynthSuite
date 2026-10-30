// ============================================================================
//  RynthNet - Transport/NetNode.cs
//  One client's end of the local mesh. No server process, no hub:
//
//  - Every client listens on its own pipe (RynthNet.1.<ns>.<pid>.<nonce>).
//  - A supervisor thread lists the namespace once a second and dials each
//    client whose (pid, node id) sorts above its own; the other side of each
//    pair dials it. So every pair has exactly one connection, nobody is
//    elected, and a crashed or frozen client only takes its own links down.
//  - Each connection is two tasks (read, write). Nothing here touches the
//    game or the host API: what arrives is queued (Events, Logs) for the
//    consumer's own thread.
//  - Stop() says bye, cancels everything, disposes every pipe and joins the
//    supervisor and every task with a timeout. It returns false if anything
//    was still running, so the caller can log it.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthNet.Transport;

public sealed class NetNodeOptions
{
    /// <summary>Pipe namespace; empty = this Windows user and session (the normal case). Tests pass their own.</summary>
    public string Namespace { get; init; } = "";
    public string App { get; init; } = "RynthNet";
    public int MaxPeers { get; init; } = 32;
    public int ScanIntervalMs { get; init; } = 1000;
    public int PingIntervalMs { get; init; } = 2000;
    public int TimeoutMs { get; init; } = 7000;
    public int HelloTimeoutMs { get; init; } = 5000;
}

public sealed class NetNode : IDisposable
{
    public static long NowMs => Environment.TickCount64;

    private const int MaxEvents = 4096;
    private const int MaxLogs = 512;

    private readonly NetNodeOptions _options;
    private readonly object _lock = new();
    private readonly List<PeerConnection> _connections = new();           // every live pipe
    private readonly Dictionary<string, PeerConnection> _peers = new();   // joined, by node id
    private readonly Dictionary<string, long> _badPipes = new();          // pipe name -> retry after (ms)
    private readonly List<Task> _tasks = new();
    private readonly ConcurrentQueue<NetEvent> _events = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private int _eventCount, _logCount;
    private int _droppedEvents;

    private CancellationTokenSource? _cts;
    private Thread? _supervisor;
    private NamedPipeServerStream? _firstServer;
    private volatile NodeIdentity _identity = new();
    private volatile string _statusJson = "";
    private int _started, _stopped;
    private volatile bool _listening;
    private string _ns = "";

    public NetNode(NetNodeOptions? options = null)
    {
        _options = options ?? new NetNodeOptions();
    }

    public int Pid { get; } = Environment.ProcessId;
    public string NodeId { get; private set; } = "";
    public string PipeName { get; private set; } = "";
    public bool IsListening => _listening;
    public bool IsRunning => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _stopped) == 0;
    public int DroppedEvents => Volatile.Read(ref _droppedEvents);
    public NodeIdentity Identity => _identity;

    // ── Consumer side (any thread) ──────────────────────────────────────────

    public bool TryDequeueEvent(out NetEvent evt)
    {
        if (_events.TryDequeue(out evt!)) { Interlocked.Decrement(ref _eventCount); return true; }
        return false;
    }

    public bool TryDequeueLog(out string line)
    {
        if (_logs.TryDequeue(out line!)) { Interlocked.Decrement(ref _logCount); return true; }
        return false;
    }

    internal void Log(string line)
    {
        if (Interlocked.Increment(ref _logCount) > MaxLogs) { Interlocked.Decrement(ref _logCount); return; }
        _logs.Enqueue(line);
    }

    private void Raise(NetEvent evt)
    {
        if (Interlocked.Increment(ref _eventCount) > MaxEvents)
        {
            Interlocked.Decrement(ref _eventCount);
            Interlocked.Increment(ref _droppedEvents);
            return;
        }
        _events.Enqueue(evt);
    }

    /// <summary>Copies of the joined clients.</summary>
    public PeerInfo[] GetPeers()
    {
        lock (_lock)
        {
            var list = new PeerInfo[_peers.Count];
            int i = 0;
            foreach (PeerConnection p in _peers.Values)
                list[i++] = Snapshot(p);
            return list;
        }
    }

    public PeerInfo? GetPeer(string nodeId)
    {
        lock (_lock) return _peers.TryGetValue(nodeId, out PeerConnection? p) ? Snapshot(p) : null;
    }

    private static PeerInfo Snapshot(PeerConnection p) => new()
    {
        NodeId = p.NodeId,
        Pid = p.Pid,
        Outbound = p.Outbound,
        Identity = p.Identity,
        StatusJson = p.StatusJson,
        StatusAtMs = p.StatusAtMs,
        ConnectedAtMs = p.ConnectedAtMs,
        RttMs = p.RttMs,
    };

    /// <summary>Replaces this client's identity and tells every connection (even ones still in hello).</summary>
    public void SetIdentity(NodeIdentity identity)
    {
        _identity = identity;
        if (!IsRunning) return;
        byte[] frame = Wire.Ident(identity);
        foreach (PeerConnection p in LiveConnections()) p.Send(frame);
    }

    /// <summary>Replaces this client's status (one JSON object) and sends it to every client.</summary>
    public void SetStatus(string statusObjectJson)
    {
        byte[] frame = Wire.Status(statusObjectJson);   // throws on invalid JSON: the caller's bug, not the peers'
        _statusJson = statusObjectJson;
        if (!IsRunning) return;
        foreach (PeerConnection p in LiveConnections()) p.Send(frame);
    }

    /// <summary>Sends a prepared frame to one joined client. False when it isn't connected.</summary>
    public bool SendTo(string nodeId, byte[] frame)
    {
        PeerConnection? p;
        lock (_lock) _peers.TryGetValue(nodeId, out p);
        return p != null && p.Send(frame);
    }

    /// <summary>Sends a prepared frame to every joined client the filter accepts. Returns how many.</summary>
    public int Broadcast(byte[] frame, Func<PeerInfo, bool>? filter = null)
    {
        var targets = new List<PeerConnection>();
        lock (_lock)
        {
            foreach (PeerConnection p in _peers.Values)
                if (filter == null || filter(Snapshot(p))) targets.Add(p);
        }
        int n = 0;
        foreach (PeerConnection p in targets)
            if (p.Send(frame)) n++;
        return n;
    }

    private List<PeerConnection> LiveConnections()
    {
        lock (_lock) return new List<PeerConnection>(_connections);
    }

    internal void Track(Task task)
    {
        lock (_lock) _tasks.Add(task);
    }

    // ── Start / stop ────────────────────────────────────────────────────────

    /// <summary>Claims a pipe name and starts listening and discovering. False (with a reason) on failure.</summary>
    public bool Start(out string error)
    {
        error = "";
        if (Interlocked.Exchange(ref _started, 1) != 0) { error = "already started"; return false; }
        try
        {
            _ns = string.IsNullOrEmpty(_options.Namespace) ? PipeSecurityHelper.DefaultNamespace() : _options.Namespace;
            for (int attempt = 0; attempt < 3 && _firstServer == null; attempt++)
            {
                string nonce = PipeSecurityHelper.NewNonce();
                string name = PipeSecurityHelper.PipeName(_ns, Pid, nonce);
                try
                {
                    _firstServer = PipeSecurityHelper.CreateServer(name, first: true);
                    PipeName = name;
                    NodeId = PipeSecurityHelper.NodeId(Pid, nonce);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = ex.Message;   // the name exists already (someone else's): try another nonce
                }
            }
            if (_firstServer == null)
            {
                Interlocked.Exchange(ref _stopped, 1);
                error = "could not create the pipe: " + error;
                return false;
            }

            _cts = new CancellationTokenSource();
            _listening = true;
            Track(Task.Run(AcceptLoopAsync));
            _supervisor = new Thread(SupervisorLoop) { IsBackground = true, Name = "RynthNet supervisor" };
            _supervisor.Start();
            Log($"[RynthNet] listening on {PipeName}");
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _stopped, 1);
            try { _firstServer?.Dispose(); } catch { }
            _firstServer = null;
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Says bye, cancels, disposes every pipe, then waits up to <paramref name="timeoutMs"/> for
    /// the supervisor thread and every task. True when everything ended.
    /// </summary>
    public bool Stop(int timeoutMs = 2000)
    {
        if (Volatile.Read(ref _started) == 0) return true;
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return true;
        long deadline = NowMs + timeoutMs;

        // Goodbye first, so the others drop us at once instead of timing out.
        byte[] bye = Wire.Bye("shutdown");
        List<PeerConnection> live = LiveConnections();
        foreach (PeerConnection p in live) p.Send(bye);
        long byeDeadline = Math.Min(deadline, NowMs + 250);
        while (NowMs < byeDeadline)
        {
            bool flushed = true;
            foreach (PeerConnection p in live)
                if (!p.IsClosed && p.QueuedFrames > 0) { flushed = false; break; }
            if (flushed) break;
            Thread.Sleep(5);
        }

        _listening = false;
        try { _cts?.Cancel(); } catch { }
        foreach (PeerConnection p in LiveConnections()) p.Close("shutdown");
        try { _firstServer?.Dispose(); } catch { }

        bool clean = true;
        if (_supervisor != null)
        {
            int left = (int)Math.Max(0, deadline - NowMs);
            if (!_supervisor.Join(left)) clean = false;
        }
        // A task can start another while we wait (the accept loop admitting a last pipe), so
        // wait until a fresh look finds nothing running.
        while (true)
        {
            Task[] running;
            lock (_lock) running = _tasks.FindAll(t => !t.IsCompleted).ToArray();
            if (running.Length == 0) break;
            long left = deadline - NowMs;
            if (left <= 0) { clean = false; break; }
            try { Task.WaitAll(running, (int)left); }
            catch (AggregateException) { }
        }
        if (clean)
        {
            lock (_lock) _tasks.Clear();
            try { _cts?.Dispose(); } catch { }
        }
        return clean;
    }

    public void Dispose() => Stop();

    /// <summary>Threads and tasks of this node still running (0 after a clean Stop).</summary>
    public int RunningWorkers
    {
        get
        {
            int n = _supervisor != null && _supervisor.IsAlive ? 1 : 0;
            lock (_lock)
                foreach (Task t in _tasks)
                    if (!t.IsCompleted) n++;
            return n;
        }
    }

    // ── Listening ───────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        CancellationToken ct = _cts!.Token;
        NamedPipeServerStream? server = _firstServer;
        _firstServer = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                server ??= PipeSecurityHelper.CreateServer(PipeName, first: false);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                NamedPipeServerStream accepted = server;
                server = null;
                Admit(accepted);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try { server?.Dispose(); } catch { }
                server = null;
                if (ct.IsCancellationRequested) break;
                Log($"[RynthNet] listen error: {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        try { server?.Dispose(); } catch { }
    }

    private void Admit(NamedPipeServerStream pipe)
    {
        int kernelPid = PipeSecurityHelper.ClientPid(pipe);
        PeerConnection peer;
        lock (_lock)
        {
            if (_connections.Count >= _options.MaxPeers * 2 || !IsRunning)
            {
                try { pipe.Dispose(); } catch { }
                return;
            }
            peer = new PeerConnection(this, pipe, outbound: false, expectedNodeId: "", kernelPid: kernelPid);
            _connections.Add(peer);
        }
        peer.Start(Wire.Hello(NodeId, Pid, _identity));
    }

    // ── Discovery and dialing (supervisor thread) ───────────────────────────

    private void SupervisorLoop()
    {
        CancellationToken ct = _cts!.Token;
        long nextScan = 0;
        bool scanErrorLogged = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                long now = NowMs;
                if (now >= nextScan)
                {
                    nextScan = now + _options.ScanIntervalMs;
                    try
                    {
                        Scan(now, ct);
                        scanErrorLogged = false;
                    }
                    catch (Exception ex) when (!scanErrorLogged)
                    {
                        scanErrorLogged = true;
                        Log($"[RynthNet] discovery failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Housekeeping(NowMs);
            }
            catch (Exception ex)
            {
                Log($"[RynthNet] supervisor: {ex.GetType().Name}: {ex.Message}");
            }
            ct.WaitHandle.WaitOne(200);
        }
    }

    private void Scan(long now, CancellationToken ct)
    {
        List<string> names = PipeSecurityHelper.Discover(_ns);
        foreach (string name in names)
        {
            if (ct.IsCancellationRequested) return;
            if (name == PipeName) continue;
            if (!PipeSecurityHelper.TryParse(name, _ns, out int pid, out string nodeId)) continue;
            lock (_lock)
            {
                if (_badPipes.TryGetValue(name, out long retryAt) && now < retryAt) continue;
                if (_peers.ContainsKey(nodeId)) continue;
                bool pending = false;
                foreach (PeerConnection c in _connections)
                    if (c.ExpectedNodeId == nodeId || c.NodeId == nodeId) { pending = true; break; }
                if (pending) continue;
                if (_connections.Count >= _options.MaxPeers) return;
            }
            // Exactly one side of each pair dials: the one whose (pid, node id) sorts first.
            if (!DialsFirst(Pid, NodeId, pid, nodeId)) continue;
            Dial(name, pid, nodeId, now);
        }
    }

    internal static bool DialsFirst(int myPid, string myNode, int theirPid, string theirNode)
        => myPid != theirPid ? myPid < theirPid : string.CompareOrdinal(myNode, theirNode) < 0;

    private void Dial(string name, int pid, string nodeId, long now)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            client.Connect(300);
        }
        catch (TimeoutException)
        {
            client.Dispose();   // every instance busy right now: next scan
            return;
        }
        catch (Exception ex)
        {
            client.Dispose();
            lock (_lock) _badPipes[name] = now + 5000;
            Log($"[RynthNet] can't open {name}: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (!PipeSecurityHelper.VerifyServer(client, pid, out string why))
        {
            client.Dispose();
            lock (_lock) _badPipes[name] = now + 60_000;
            Log($"[RynthNet] refused {name}: {why}");
            return;
        }
        PeerConnection peer;
        lock (_lock)
        {
            if (!IsRunning) { client.Dispose(); return; }
            peer = new PeerConnection(this, client, outbound: true, expectedNodeId: nodeId, kernelPid: pid);
            _connections.Add(peer);
        }
        peer.Start(Wire.Hello(NodeId, Pid, _identity));
    }

    private void Housekeeping(long now)
    {
        foreach (PeerConnection p in LiveConnections())
        {
            bool joined;
            lock (_lock) joined = p.Joined;
            if (!joined)
            {
                if (now - p.ConnectedAtMs > _options.HelloTimeoutMs) p.Close("no hello");
            }
            else if (now - p.LastReceiveMs > _options.TimeoutMs)
            {
                p.Close("timed out");
            }
            else if (now - p.LastPingMs >= _options.PingIntervalMs)
            {
                p.LastPingMs = now;
                p.Send(Wire.Ping(now));
            }
        }
        lock (_lock)
        {
            if (_badPipes.Count > 0)
            {
                var expired = new List<string>();
                foreach (var kv in _badPipes)
                    if (now >= kv.Value) expired.Add(kv.Key);
                foreach (string k in expired) _badPipes.Remove(k);
            }
            _tasks.RemoveAll(t => t.IsCompleted);
        }
    }

    // ── Frames (I/O threads) ────────────────────────────────────────────────

    /// <summary>Handles one frame from a peer. False when the peer was closed because of it.</summary>
    internal bool OnFrame(PeerConnection peer, byte[] payload)
    {
        WireMessage? m = Wire.Parse(payload);
        if (m == null)
        {
            peer.Close("malformed frame");
            return false;
        }

        bool joined;
        lock (_lock) joined = peer.Joined;
        if (!joined)
        {
            if (m.Type != "hello")
            {
                peer.Close("expected hello, got " + m.Type);
                return false;
            }
            return OnHello(peer, m);
        }

        long now = NowMs;
        switch (m.Type)
        {
            case "ident":
                if (m.Identity == null) break;
                NodeIdentity updated;
                lock (_lock)
                {
                    updated = new NodeIdentity
                    {
                        Character = m.Identity.Character,
                        World = m.Identity.World,
                        Account = m.Identity.Account,
                        LoggedIn = m.Identity.LoggedIn,
                        Tags = m.Identity.Tags,
                        App = peer.Identity.App,
                    };
                    peer.Identity = updated;
                }
                Raise(new NetEvent { Kind = NetEventKind.PeerUpdated, NodeId = peer.NodeId, From = updated.Character, AtMs = now });
                break;
            case "status":
                lock (_lock)
                {
                    peer.StatusJson = m.StatusJson;
                    peer.StatusAtMs = now;
                }
                break;
            case "ping":
                peer.Send(Wire.Pong(m.Ts));
                break;
            case "pong":
                long rtt = now - m.Ts;
                if (rtt >= 0 && rtt < 60_000) peer.RttMs = (int)rtt;
                break;
            case "cmd":
                Raise(new NetEvent
                {
                    Kind = NetEventKind.Command, NodeId = peer.NodeId, From = FromName(peer), Id = m.Id,
                    Target = m.Target, Text = m.Text, DelayMs = m.DelayMs, AtMs = now,
                });
                break;
            case "ack":
                Raise(new NetEvent { Kind = NetEventKind.Ack, NodeId = peer.NodeId, From = FromName(peer), Id = m.Id, Ok = m.Ok, Text = m.Text, AtMs = now });
                break;
            case "data":
                Raise(new NetEvent
                {
                    Kind = NetEventKind.Data, NodeId = peer.NodeId, From = FromName(peer), Channel = m.Channel,
                    Text = m.Text, Target = m.Target, AtMs = now,
                });
                break;
            case "bye":
                peer.Close(m.Text.Length > 0 ? "left (" + m.Text + ")" : "left");
                return false;
            // "hello" again, or a type from a newer version: ignored.
        }
        return true;
    }

    private string FromName(PeerConnection peer)
    {
        lock (_lock)
        {
            string c = peer.Identity.Character;
            return c.Length > 0 ? c : "pid " + peer.Pid;
        }
    }

    private bool OnHello(PeerConnection peer, WireMessage m)
    {
        if (m.Proto != Wire.Proto)
        {
            peer.Send(Wire.Bye("protocol " + Wire.Proto + " only"));
            peer.Close($"protocol {m.Proto} (this client speaks {Wire.Proto})");
            return false;
        }
        string expectedPrefix = m.Pid + ".";
        if (m.Pid <= 0 || !m.NodeId.StartsWith(expectedPrefix, StringComparison.Ordinal) || m.NodeId.Length != expectedPrefix.Length + 16)
        {
            peer.Close("bad hello (node id)");
            return false;
        }
        if (peer.Outbound && m.NodeId != peer.ExpectedNodeId)
        {
            peer.Close("hello from the wrong node");
            return false;
        }
        if (!peer.Outbound && peer.KernelPid != 0 && peer.KernelPid != m.Pid)
        {
            peer.Close($"hello claims pid {m.Pid}, the pipe says {peer.KernelPid}");
            return false;
        }
        if (m.NodeId == NodeId)
        {
            peer.Close("connected to itself");
            return false;
        }

        NodeIdentity identity = m.Identity ?? new NodeIdentity();
        string? refused = null;
        lock (_lock)
        {
            if (_peers.ContainsKey(m.NodeId))
                refused = "duplicate connection";   // only in a restart race; the older link stays
            else if (_peers.Count >= _options.MaxPeers)
                refused = "too many clients";
            else
            {
                peer.NodeId = m.NodeId;
                peer.Pid = m.Pid;
                peer.Identity = identity;
                peer.Joined = true;
                _peers[m.NodeId] = peer;
            }
        }
        if (refused != null)
        {
            peer.Send(Wire.Bye(refused));
            peer.Close(refused);
            return false;
        }
        Raise(new NetEvent { Kind = NetEventKind.PeerJoined, NodeId = m.NodeId, From = identity.Character, AtMs = NowMs });
        Log($"[RynthNet] {Describe(identity, m.Pid)} connected ({(peer.Outbound ? "dialed" : "accepted")})");
        string status = _statusJson;
        if (status.Length > 0) peer.Send(Wire.Status(status));
        return true;
    }

    internal void OnPeerClosed(PeerConnection peer, string reason)
    {
        bool wasJoined = false;
        NodeIdentity identity;
        lock (_lock)
        {
            _connections.Remove(peer);
            if (peer.Joined && _peers.TryGetValue(peer.NodeId, out PeerConnection? current) && ReferenceEquals(current, peer))
            {
                _peers.Remove(peer.NodeId);
                wasJoined = true;
            }
            identity = peer.Identity;
        }
        if (wasJoined)
        {
            Raise(new NetEvent { Kind = NetEventKind.PeerLeft, NodeId = peer.NodeId, From = identity.Character, Text = reason, AtMs = NowMs });
            Log($"[RynthNet] {Describe(identity, peer.Pid)} disconnected: {reason}");
        }
    }

    private static string Describe(NodeIdentity id, int pid)
        => id.Character.Length > 0 ? $"{id.Character} (pid {pid})" : $"pid {pid} (not logged in)";
}
