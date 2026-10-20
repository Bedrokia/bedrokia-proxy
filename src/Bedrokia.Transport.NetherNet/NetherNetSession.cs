// ┌───────────────────────────────────────────────────────────────────┐
// │  ____  _____ ____  ____   ___  _  __ ___    _                     │
// │ | __ )| ____|  _ \|  _ \ / _ \| |/ /|_ _|  / \                    │
// │ |  _ \|  _| | | | | |_) | | | | ' /  | |  / _ \                   │
// │ | |_) | |___| |_| |  _ <| |_| | . \  | | / ___ \                  │
// │ |____/|_____|____/|_| \_\\___/|_|\_\|___/_/   \_\                 │
// │                                                                   │
// │  Bedrokia - Minecraft: Bedrock Edition server software            │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using SIPSorcery.Net;

namespace Bedrokia.Transport.NetherNet;

/// <summary>
/// One NetherNet (WebRTC) connection. The client opens two data channels; every message starts with a byte
/// counting the segments that still follow (0 = last), so large packets are split and reassembled.
/// Thread-safe. Events are raised from SIPSorcery threads: hand the work to the game thread.
/// </summary>
public sealed class NetherNetSession
{
    public const string ReliableLabel = "ReliableDataChannel";
    public const string UnreliableLabel = "UnreliableDataChannel";
    private const int SegmentSize = 10_000; // below every max-message-size seen in practice
    // Anti-OOM cap on a message from the client. The login carries the skin: persona skins with their geometry and
    // animations go well past 512 KB, so the cap stays at 4 MB (the segment count is a byte: 255 at most anyway).
    private const int MaxMessageSize = 4 * 1024 * 1024;

    /// <summary>
    /// Silence this long means the client is gone. Long on purpose: while it asks the player whether to download the
    /// resource packs, the client sends nothing (30 s disconnected slow players). A lost or crashed peer is caught
    /// sooner by WebRTC itself (connection state disconnected or failed).
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly RTCPeerConnection _peer;
    private readonly Lock _sync = new();
    private readonly Timer _idleCheck;
    private long _lastReceived = Environment.TickCount64;
    private RTCDataChannel? _reliable, _unreliable;
    private byte[]? _reassemblyBuffer;
    private int _reassemblyOffset;
    private int _expectedRemaining = -1;
    private bool _connected, _closed;

    internal NetherNetSession(RTCPeerConnection peer, IPEndPoint remoteEndPoint, ECDsa? peerKey)
    {
        _peer = peer;
        RemoteEndPoint = remoteEndPoint;
        PeerPublicKey = peerKey;
        peer.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                Close($"WebRTC connection {state}");
        };
        _idleCheck = new Timer(_ =>
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastReceived) > IdleTimeout.TotalMilliseconds)
                Close("timed out");
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Address the signaling request came from.</summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>Key the client proved it owns through its SDP identity; null for anonymous (offline mode) peers.</summary>
    public ECDsa? PeerPublicKey { get; }

    public bool IsClosed
    {
        get
        {
            lock (_sync)
                return _closed;
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
                return _connected && !_closed;
        }
    }

    /// <summary>Both data channels are open: game packets can flow.</summary>
    public event Action<NetherNetSession>? Connected;

    /// <summary>A complete message (one batch of game packets, without the 0xFE header).</summary>
    public event Action<NetherNetSession, byte[]>? PacketReceived;

    public event Action<NetherNetSession, string>? Closed;

    /// <summary>Sends <paramref name="message"/> on the reliable channel (or unreliable channel if small & ephemeral), split into segments.</summary>
    public void Send(byte[] message, bool preferUnreliable = false)
    {
        lock (_sync)
        {
            if (_closed)
                return;

            // Route to UnreliableDataChannel only if non-fragmented and small (< 500 bytes)
            var channel = preferUnreliable && message.Length <= 500 && _unreliable is not null
                ? _unreliable
                : _reliable;

            if (channel is null)
                return;

            if (channel == _unreliable)
            {
                // Unreliable packets MUST NEVER be segmented: 1 single segment with remaining = 0
                var segment = new byte[message.Length + 1];
                segment[0] = 0;
                message.AsSpan().CopyTo(segment.AsSpan(1));
                channel.send(segment);
                return;
            }

            var segments = Math.Max(1, (message.Length + SegmentSize - 1) / SegmentSize);
            if (segments > 256)
            {
                Close("outgoing message too large");
                return;
            }
            for (var i = 0; i < segments; i++)
            {
                var start = i * SegmentSize;
                var length = Math.Min(SegmentSize, message.Length - start);
                var segment = new byte[length + 1];
                segment[0] = (byte)(segments - 1 - i);
                message.AsSpan(start, length).CopyTo(segment.AsSpan(1));
                channel.send(segment);
            }
        }
    }


    /// <summary>Closes after a short grace period, so a Disconnect packet sent just before can reach the client.</summary>
    public void Disconnect(string reason) =>
        _ = Task.Delay(IsConnected ? 500 : 0).ContinueWith(_ => Close(reason), TaskScheduler.Default);

    internal void Attach(RTCDataChannel channel)
    {
        var reliable = channel.label == ReliableLabel;
        if (!reliable && channel.label != UnreliableLabel)
        {
            Close($"unexpected data channel '{channel.label}'");
            return;
        }
        lock (_sync)
        {
            if ((reliable ? _reliable : _unreliable) is not null)
            {
                Close($"duplicate data channel '{channel.label}'");
                return;
            }
            if (reliable)
                _reliable = channel;
            else
                _unreliable = channel;
        }
        channel.onmessage += (_, _, data) => OnMessage(data, reliable);
        channel.onclose += () => Close($"data channel '{channel.label}' closed");
        channel.onopen += CheckConnected;
        if (channel.readyState == RTCDataChannelState.open)
            CheckConnected();
    }

    private void CheckConnected()
    {
        lock (_sync)
        {
            if (_connected || _closed || _reliable?.readyState != RTCDataChannelState.open || _unreliable?.readyState != RTCDataChannelState.open)
                return;
            _connected = true;
        }
        Connected?.Invoke(this);
    }

    private void OnMessage(byte[] data, bool reliable)
    {
        Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
        byte[]? complete = null;
        lock (_sync)
        {
            if (_closed)
                return;
            if (data.Length < 2)
            {
                CloseLocked("empty message");
                return;
            }
            var remaining = data[0];
            if (!reliable)
            {
                if (remaining != 0)
                {
                    CloseLocked("segmented message on the unreliable channel");
                    return;
                }
                complete = data[1..];
            }
            else
            {
                if (_expectedRemaining >= 0 && remaining != _expectedRemaining)
                {
                    ResetReassemblyBuffer();
                    _expectedRemaining = -1;
                    CloseLocked($"segment {remaining} received, {_expectedRemaining} expected");
                    return;
                }
                if (remaining == 0 && _reassemblyBuffer is null)
                {
                    complete = data[1..];
                }
                else
                {
                    var segLen = data.Length - 1;
                    if (_reassemblyBuffer is null)
                    {
                        var estimated = Math.Max(32 * 1024, (remaining + 1) * segLen);
                        _reassemblyBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(MaxMessageSize, estimated));
                        _reassemblyOffset = 0;
                    }
                    else if (_reassemblyOffset + segLen > _reassemblyBuffer.Length)
                    {
                        var newCapacity = Math.Min(MaxMessageSize, Math.Max(_reassemblyBuffer.Length * 2, _reassemblyOffset + segLen));
                        var newRented = ArrayPool<byte>.Shared.Rent(newCapacity);
                        _reassemblyBuffer.AsSpan(0, _reassemblyOffset).CopyTo(newRented);
                        ArrayPool<byte>.Shared.Return(_reassemblyBuffer);
                        _reassemblyBuffer = newRented;
                    }

                    if (_reassemblyOffset + segLen > MaxMessageSize)
                    {
                        ResetReassemblyBuffer();
                        _expectedRemaining = -1;
                        CloseLocked("message too large");
                        return;
                    }

                    data.AsSpan(1).CopyTo(_reassemblyBuffer.AsSpan(_reassemblyOffset));
                    _reassemblyOffset += segLen;
                    _expectedRemaining = remaining - 1;
                    if (remaining == 0)
                    {
                        complete = _reassemblyBuffer.AsSpan(0, _reassemblyOffset).ToArray();
                        ResetReassemblyBuffer();
                    }
                }
                if (remaining == 0)
                    _expectedRemaining = -1;
            }

        }
        if (complete is not null)
            PacketReceived?.Invoke(this, complete);
    }

    private void ResetReassemblyBuffer()
    {
        if (_reassemblyBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_reassemblyBuffer);
            _reassemblyBuffer = null;
        }
        _reassemblyOffset = 0;
    }

    private void Close(string reason)
    {
        lock (_sync)
            CloseLocked(reason);
    }

    private void CloseLocked(string reason)
    {
        if (_closed)
            return;
        _closed = true;
        ResetReassemblyBuffer();
        _idleCheck.Dispose();
        // Closing re-enters SIPSorcery callbacks; do it off this thread and lock.
        _ = Task.Run(() =>
        {
            try
            {
                _peer.close();
            }
            catch (Exception)
            {
                // Already torn down.
            }
            Closed?.Invoke(this, reason);
        });
    }
}
