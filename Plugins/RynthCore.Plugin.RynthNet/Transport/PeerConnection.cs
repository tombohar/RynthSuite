// ============================================================================
//  RynthNet - Transport/PeerConnection.cs
//  One pipe to one other client: a read loop and a write loop (two tasks),
//  a bounded send queue, and the peer's identity once its hello arrived.
//  Closing is idempotent and can come from either loop, the supervisor or
//  NetNode.Stop; it cancels both loops and disposes the pipe.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthNet.Transport;

internal sealed class PeerConnection
{
    /// <summary>Frames waiting to be written before the peer counts as stuck and is dropped.</summary>
    private const int MaxQueuedFrames = 512;

    private readonly NetNode _node;
    private readonly PipeStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<byte[]> _sendQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0);
    private int _queued;
    private int _closed;
    private long _lastReceiveMs;
    private long _lastPingMs;

    public readonly bool Outbound;
    /// <summary>Outbound: the node id the pipe name promised. Inbound: empty until hello.</summary>
    public readonly string ExpectedNodeId;
    /// <summary>The other process id from the kernel (0 when unknown).</summary>
    public readonly int KernelPid;
    public readonly long ConnectedAtMs;

    // Written under NetNode's lock.
    public string NodeId = "";
    public int Pid;
    public NodeIdentity Identity = new();
    public bool Joined;
    public string StatusJson = "";
    public long StatusAtMs;

    public volatile int RttMs = -1;
    public string CloseReason { get; private set; } = "";

    public PeerConnection(NetNode node, PipeStream pipe, bool outbound, string expectedNodeId, int kernelPid)
    {
        _node = node;
        _pipe = pipe;
        Outbound = outbound;
        ExpectedNodeId = expectedNodeId;
        KernelPid = kernelPid;
        ConnectedAtMs = NetNode.NowMs;
        _lastReceiveMs = ConnectedAtMs;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public int QueuedFrames => Volatile.Read(ref _queued);
    public long LastReceiveMs => Interlocked.Read(ref _lastReceiveMs);
    public long LastPingMs { get => Interlocked.Read(ref _lastPingMs); set => Interlocked.Exchange(ref _lastPingMs, value); }

    /// <summary>Starts both loops; the hello frame goes first.</summary>
    public void Start(byte[] hello)
    {
        Send(hello);
        _node.Track(Task.Run(WriteLoopAsync));
        _node.Track(Task.Run(ReadLoopAsync));
    }

    /// <summary>Queues a frame. Thread-safe. False when closed or when the peer stopped reading.</summary>
    public bool Send(byte[] frame)
    {
        if (IsClosed) return false;
        if (Interlocked.Increment(ref _queued) > MaxQueuedFrames)
        {
            Interlocked.Decrement(ref _queued);
            Close("stopped reading (send queue full)");
            return false;
        }
        _sendQueue.Enqueue(frame);
        try { _sendSignal.Release(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    private async Task WriteLoopAsync()
    {
        CancellationToken ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _sendSignal.WaitAsync(ct).ConfigureAwait(false);
                while (_sendQueue.TryDequeue(out byte[]? frame))
                {
                    await _pipe.WriteAsync(frame, ct).ConfigureAwait(false);
                    Interlocked.Decrement(ref _queued);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { Close("pipe closed"); }
        catch (IOException ex) { Close("write failed: " + ex.Message); }
        catch (Exception ex) { Close("write failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private async Task ReadLoopAsync()
    {
        CancellationToken ct = _cts.Token;
        byte[] header = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 0 || length > Wire.MaxFrame)
                {
                    Close($"bad frame length {length}");
                    return;
                }
                Interlocked.Exchange(ref _lastReceiveMs, NetNode.NowMs);
                if (length == 0) continue;   // keepalive
                byte[] payload = new byte[length];
                await _pipe.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastReceiveMs, NetNode.NowMs);
                if (!_node.OnFrame(this, payload)) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { Close("disconnected"); }
        catch (ObjectDisposedException) { Close("pipe closed"); }
        catch (IOException ex) { Close("read failed: " + ex.Message); }
        catch (Exception ex) { Close("read failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>Idempotent: cancels both loops, disposes the pipe, tells the node.</summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        try { _cts.Cancel(); } catch { }
        try { _pipe.Dispose(); } catch { }
        _node.OnPeerClosed(this, reason);
    }
}
