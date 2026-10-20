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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SIPSorcery.Net;

namespace Bedrokia.Transport.NetherNet;

/// <summary>
/// One UDP port for every player's WebRTC connection (the idea of pion's UDPMux): a packet from an address not seen
/// yet must be an ICE check, whose USERNAME starts with the server-side ufrag of one connection; after that, the
/// address alone says which connection it belongs to.
/// </summary>
internal sealed class UdpMux : IDisposable
{
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<IPEndPoint, MuxIceChannel> _byEndPoint = new();
    private readonly ConcurrentDictionary<string, MuxIceChannel> _byUfrag = new();
    private volatile bool _closed;

    /// <exception cref="SocketException">The port cannot be bound.</exception>
    public UdpMux(IPEndPoint bind)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
            _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); // SIO_UDP_CONNRESET off: an ICMP error must not stop receiving
        _socket.Bind(bind);
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        new Thread(Receive) { IsBackground = true, Name = "NetherNet UDP " + Port }.Start();
    }

    public int Port { get; }

    /// <summary>Packets to the connection whose local ICE ufrag is <paramref name="ufrag"/> are given to <paramref name="channel"/>.</summary>
    public void Register(string ufrag, MuxIceChannel channel) => _byUfrag[ufrag] = channel;

    public void Unregister(MuxIceChannel channel)
    {
        foreach (var (key, value) in _byUfrag)
            if (value == channel)
                _byUfrag.TryRemove(key, out _);
        foreach (var (key, value) in _byEndPoint)
            if (value == channel)
                _byEndPoint.TryRemove(key, out _);
    }

    public SocketError Send(IPEndPoint destination, byte[] buffer)
    {
        try
        {
            _socket.SendTo(buffer, destination);
            return SocketError.Success;
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode;
        }
        catch (ObjectDisposedException)
        {
            return SocketError.Shutdown;
        }
    }

    private void Receive()
    {
        var buffer = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_closed)
        {
            int length;
            try
            {
                length = _socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException)
            {
                continue; // a reset from one peer; the socket still works
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            var remote = (IPEndPoint)from;
            if (!_byEndPoint.TryGetValue(remote, out var channel))
            {
                if (StunUfrag(buffer.AsSpan(0, length)) is not { } ufrag || !_byUfrag.TryGetValue(ufrag, out channel))
                    continue; // nobody expects it
                remote = new IPEndPoint(remote.Address, remote.Port); // the receive endpoint object is reused
                _byEndPoint[remote] = channel;
            }
            try
            {
                channel.Deliver(remote, buffer.AsSpan(0, length).ToArray());
            }
            catch (Exception)
            {
                // One broken connection must not stop the others.
            }
        }
    }

    /// <summary>The recipient's ufrag of a STUN binding request ("local:remote" USERNAME), or null.</summary>
    internal static string? StunUfrag(ReadOnlySpan<byte> packet)
    {
        // RFC 5389: two zero bits, length, magic cookie 0x2112A442, 12-byte transaction id, then type-length-value attributes.
        if (packet.Length < 20 || (packet[0] & 0xC0) != 0 || packet[4] != 0x21 || packet[5] != 0x12 || packet[6] != 0xA4 || packet[7] != 0x42)
            return null;
        var end = Math.Min(packet.Length, 20 + (packet[2] << 8 | packet[3]));
        for (var pos = 20; pos + 4 <= end;)
        {
            var type = packet[pos] << 8 | packet[pos + 1];
            var length = packet[pos + 2] << 8 | packet[pos + 3];
            if (pos + 4 + length > end)
                return null;
            if (type == 0x0006) // USERNAME
            {
                var username = Encoding.UTF8.GetString(packet.Slice(pos + 4, length));
                var colon = username.IndexOf(':');
                return colon < 0 ? username : username[..colon];
            }
            pos += 4 + (length + 3 & ~3);
        }
        return null;
    }

    public void Dispose()
    {
        _closed = true;
        _socket.Dispose();
    }
}

/// <summary>An ICE channel that sends and receives through the <see cref="UdpMux"/>; its own socket stays unused.</summary>
internal sealed class MuxIceChannel : RtpIceChannel
{
    private readonly UdpMux _mux;

    public MuxIceChannel(UdpMux mux, RTCConfiguration config)
        : base(config.X_BindAddress, RTCIceComponent.rtp, config.iceServers, config.iceTransportPolicy, config.X_ICEIncludeAllInterfaceAddresses, 0, null)
    {
        _mux = mux;
        OnClosed += _ => mux.Unregister(this);
    }

    public override SocketError Send(RTPChannelSocketsEnum sendOn, IPEndPoint dstEndPoint, byte[] buffer) =>
        IsClosed ? SocketError.Disconnecting : _mux.Send(dstEndPoint, buffer);

    public void Deliver(IPEndPoint from, byte[] packet) => OnRTPPacketReceived(null!, _mux.Port, from, packet);
}

/// <summary>A peer connection whose ICE channel is a <see cref="MuxIceChannel"/> (RTCPeerConnection.CreateRtpChannel otherwise).</summary>
internal sealed class MuxPeerConnection : RTCPeerConnection
{
    // Field initialisers run before the base constructor, which already asks for the channel.
    private readonly UdpMux _mux = CurrentMux!;
    private readonly RTCConfiguration _config = CurrentConfig!;

    [ThreadStatic] private static UdpMux? CurrentMux;
    [ThreadStatic] private static RTCConfiguration? CurrentConfig;

    private static readonly MethodInfo OnRtpData = typeof(RTCPeerConnection).GetMethod("OnRTPDataReceived", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("RTCPeerConnection.OnRTPDataReceived (SIPSorcery changed)");

    private MuxPeerConnection(RTCConfiguration config) : base(config, 0, null, false)
    {
    }

    public MuxIceChannel Channel => (MuxIceChannel)GetRtpChannel();

    public static MuxPeerConnection Create(RTCConfiguration config, UdpMux mux)
    {
        (CurrentMux, CurrentConfig) = (mux, config);
        try
        {
            return new MuxPeerConnection(config);
        }
        finally
        {
            (CurrentMux, CurrentConfig) = (null, null);
        }
    }

    protected override RTPChannel CreateRtpChannel()
    {
        if (MultiplexRtpChannel is not null)
            return MultiplexRtpChannel; // bundle: every media shares the one channel
        var channel = new MuxIceChannel(_mux, _config);
        MultiplexRtpChannel = channel;
        channel.OnRTPDataReceived += (Action<int, IPEndPoint, byte[]>)Delegate.CreateDelegate(typeof(Action<int, IPEndPoint, byte[]>), this, OnRtpData);
        channel.Start();
        return channel;
    }
}
