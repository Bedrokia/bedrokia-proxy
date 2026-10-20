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

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Bedrokia.Protocol.Login;
using SIPSorcery.Net;
using SIPSorcery.Sys;

namespace Bedrokia.Transport.NetherNet;

/// <summary>
/// NetherNet transport for dedicated servers: an HTTP signaling endpoint on TCP
/// (GET /v1/join = status, POST /v1/join/{networkId} = SDP offer → answer) and WebRTC data channels on UDP.
/// Behaviour from go-nethernet (MIT), endpoint/handler.go and listener.go.
/// </summary>
/// <remarks>
/// ponytail: plain HTTP only. Clients try HTTPS first; a TLS hello is closed at once so they fall back to HTTP
/// (trust on first use), as with Bedrock Dedicated Server without a certificate.
/// </remarks>
public sealed class NetherNetListener : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1 << 20;
    private const int MaxConcurrentRequests = 64;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpListener _tcp;
    private readonly ECDsa _serverKey;
    private readonly Func<string, ECDsa> _verifyToken;
    private readonly bool _allowAnonymous;
    private readonly PortRange? _udpPorts;
    private readonly UdpMux? _mux;
    private readonly SemaphoreSlim _requestSlots = new(MaxConcurrentRequests);

    /// <param name="bind">TCP address for signaling (normally the game port, 19132).</param>
    /// <param name="serverKey">Long-lived P-384 key presented to clients (trust on first use).</param>
    /// <param name="verifyToken">Checks a client multiplayer token and returns its key; throws <see cref="LoginException"/>.</param>
    /// <param name="allowAnonymous">Accept offers without an identity (offline mode only).</param>
    /// <param name="udpPorts">UDP ports for WebRTC, one per player, or null for any free port.</param>
    /// <param name="sharedUdpPort">One UDP port for every player instead (0: any free port); then <paramref name="udpPorts"/> is unused.</param>
    /// <exception cref="SocketException">The TCP or UDP port cannot be bound.</exception>
    public NetherNetListener(IPEndPoint bind, ECDsa serverKey, Func<string, ECDsa> verifyToken,
        bool allowAnonymous, PortRange? udpPorts, int? sharedUdpPort = null)
    {
        if (sharedUdpPort is { } shared)
            _mux = new UdpMux(new IPEndPoint(IPAddress.Any, shared));
        _tcp = new TcpListener(bind);
        _tcp.Start();
        _serverKey = serverKey;
        _verifyToken = verifyToken;
        _allowAnonymous = allowAnonymous;
        _udpPorts = udpPorts;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_tcp.LocalEndpoint;

    /// <summary>The UDP port every player shares, or null when each player gets a port of the range.</summary>
    public int? SharedUdpPort => _mux?.Port;

    /// <summary>
    /// IPv4 address announced in the WebRTC candidates instead of the machine's own ones, for a server behind Docker
    /// or NAT (Pterodactyl): players cannot reach a container's 172.x address. Null: the local addresses.
    /// </summary>
    public IPAddress? PublicAddress { get; init; }

    /// <summary>
    /// Puts <paramref name="address"/> (if any) and <paramref name="port"/> (if any) in every IPv4 host candidate of an SDP,
    /// dropping the duplicates this makes.
    /// </summary>
    internal static string WithPublicAddress(string sdp, IPAddress? address, int? port = null)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>();
        foreach (var line in sdp.Split("\r\n"))
        {
            // a=candidate:<foundation> <component> <transport> <priority> <address> <port> typ <type> ...
            var fields = line.Split(' ');
            if (line.StartsWith("a=candidate:", StringComparison.Ordinal) && fields.Length > 7 && fields[7] == "host"
                && IPAddress.TryParse(fields[4], out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (address is not null)
                    fields[4] = address.ToString();
                if (port is not null)
                    fields[5] = port.Value.ToString();
                if (!seen.Add(fields[4] + ":" + fields[5]))
                    continue;
                lines.Add(string.Join(' ', fields));
            }
            else if (port is not null && line.StartsWith("a=candidate:", StringComparison.Ordinal))
                continue; // shared port: IPv4 only, other candidates would lead to a port of their own
            else
                lines.Add(line);
        }
        return string.Join("\r\n", lines);
    }

    /// <summary>A WebRTC connection was negotiated; it raises <see cref="NetherNetSession.Connected"/> once usable.</summary>
    public event Action<NetherNetSession>? SessionOpened;

    /// <summary>Signaling errors and refused offers, for diagnostics.</summary>
    public event Action<string>? Trace;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync(cancellationToken);
                if (!_requestSlots.Wait(0))
                {
                    client.Dispose(); // overloaded: refuse rather than queue
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleAsync(client, cancellationToken);
                    }
                    finally
                    {
                        _requestSlots.Release();
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken serverStopping)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
        timeout.CancelAfter(RequestTimeout);
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        try
        {
            var stream = client.GetStream();
            var request = await HttpRequest.ReadAsync(stream, MaxHeaderBytes, MaxBodyBytes, timeout.Token);
            if (request is null)
            {
                // Same reply as BDS without a certificate: TLS fatal alert "handshake_failure".
                // The client then retries over plain HTTP.
                Trace?.Invoke($"signaling: {remote} tried TLS, refused (plain HTTP only)");
                await stream.WriteAsync(new byte[] { 0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x28 }, timeout.Token);
                return;
            }

            var (status, contentType, body) = await RouteAsync(request, remote);
            Trace?.Invoke($"signaling: {remote} {request.Method} {request.Path} -> {status}");
            var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException)
        {
            Trace?.Invoke($"signaling request from {remote} failed: {e.Message}");
        }
    }

    private async Task<(string Status, string ContentType, byte[] Body)> RouteAsync(HttpRequest request, IPEndPoint remote)
    {
        // Bedrock Dedicated Server 1.26.52 answers with an empty body, and a 26.52 client only goes on to
        // POST its offer after that; a JSON status (Mojang guide) made it stop here. Match BDS.
        if (request.Method == "GET" && request.Path == "/v1/join")
            return ("200 OK", "application/json", []);

        const string prefix = "/v1/join/";
        if (request.Method == "POST" && request.Path.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (!ulong.TryParse(request.Path[prefix.Length..], out _))
                return ("400 Bad Request", "text/plain", "Network ID must be uint64"u8.ToArray());
            if (request.Body.Length == 0)
                return ("400 Bad Request", "text/plain", "Missing SDP offer"u8.ToArray());
            try
            {
                var answer = await NegotiateAsync(Encoding.UTF8.GetString(request.Body), remote);
                return ("200 OK", "application/sdp", Encoding.UTF8.GetBytes(answer));
            }
            catch (LoginException e)
            {
                Trace?.Invoke($"refused NetherNet offer from {remote}: {e.Message}");
                return ("403 Forbidden", "text/plain", Encoding.UTF8.GetBytes(e.Message));
            }
            catch (Exception e) when (e is InvalidDataException or ApplicationException or InvalidOperationException or ArgumentException)
            {
                Trace?.Invoke($"bad NetherNet offer from {remote}: {e.Message}");
                return ("400 Bad Request", "text/plain", "Invalid SDP offer"u8.ToArray());
            }
        }
        return ("404 Not Found", "text/plain", "Not found"u8.ToArray());
    }

    internal static readonly (Org.BouncyCastle.X509.X509Certificate Cert, Org.BouncyCastle.Crypto.AsymmetricKeyParameter Key) DtlsKey = CreateDtlsKey();

    private static (Org.BouncyCastle.X509.X509Certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter) CreateDtlsKey()
    {
        var (cert, key) = SIPSorcery.Net.SharpSRTP.DTLS.DtlsCertificateUtils.GenerateCertificate("bedrokia", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddYears(1), useRSA: false);
        var x509 = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(cert.GetCertificateAt(0).GetEncoded());
        return (x509, key);
    }

    internal static RTCCertificate2 CreateDtlsCertificate() => new() { Certificate = DtlsKey.Cert, PrivateKey = DtlsKey.Key };

    private async Task<string> NegotiateAsync(string offer, IPEndPoint remote)
    {
        var peerKey = SdpIdentity.VerifyOffer(offer, _verifyToken);
        if (peerKey is null && !_allowAnonymous)
            throw new LoginException("An Xbox Live identity is required to join this server.");

        var config = new RTCConfiguration { certificates2 = [CreateDtlsCertificate()] };
        var peer = _mux is null ? new RTCPeerConnection(config, 0, _udpPorts) : MuxPeerConnection.Create(config, _mux);
        var session = new NetherNetSession(peer, remote, peerKey);
        peer.ondatachannel += session.Attach;
        peer.oniceconnectionstatechange += state => Trace?.Invoke($"nethernet: {remote} ICE {state}");
        peer.onconnectionstatechange += state => Trace?.Invoke($"nethernet: {remote} connection {state}");

        string answer;
        try
        {
            // The guide asks to strip the identity before handing the offer to WebRTC.
            answer = await WhipWhepServer.AnswerAsync(peer, SdpIdentity.WithoutIdentity(offer), true);
            if (_mux is not null && peer is MuxPeerConnection muxed)
            {
                // Its checks reach the shared port: route them to this connection by its ufrag.
                var ufrag = answer.Split("\r\n").FirstOrDefault(l => l.StartsWith("a=ice-ufrag:", StringComparison.Ordinal))?["a=ice-ufrag:".Length..]
                    ?? throw new InvalidOperationException("The answer has no ICE ufrag.");
                _mux.Register(ufrag, muxed.Channel);
            }
            if (PublicAddress is not null || _mux is not null)
                answer = WithPublicAddress(answer, PublicAddress, _mux?.Port);
        }
        catch
        {
            peer.close();
            throw;
        }
        answer = SdpIdentity.SignAnswer(answer, _serverKey);
        Trace?.Invoke($"nethernet: offer from {remote}:\n{SdpIdentity.WithoutIdentity(offer)}\nanswer:\n{SdpIdentity.WithoutIdentity(answer)}");

        SessionOpened?.Invoke(session);
        _ = Task.Delay(ConnectTimeout).ContinueWith(_ =>
        {
            if (!session.IsConnected)
                session.Disconnect("WebRTC connection timed out");
        }, TaskScheduler.Default);
        return answer;
    }

    public void Dispose()
    {
        _tcp.Stop();
        _mux?.Dispose();
        _requestSlots.Dispose();
    }
}

/// <summary>The few parts of an HTTP/1.1 request the signaling endpoint needs.</summary>
internal sealed record HttpRequest(string Method, string Path, byte[] Body)
{
    /// <summary>Reads one request; returns null when the peer speaks TLS or sends something that is not HTTP.</summary>
    /// <exception cref="InvalidDataException">Headers or body exceed the limits.</exception>
    public static async Task<HttpRequest?> ReadAsync(Stream stream, int maxHeaderBytes, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxHeaderBytes];
        var length = 0;
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(buffer, length)) < 0)
        {
            if (length == buffer.Length)
                throw new InvalidDataException("Request headers are too large.");
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                return null;
            length += read;
            if (buffer[0] == 0x16)
                return null; // TLS ClientHello
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var requestLine = head[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;

        var contentLength = 0;
        foreach (var header in head.Skip(1))
        {
            var colon = header.IndexOf(':');
            if (colon > 0 && header[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(header[(colon + 1)..].Trim(), out contentLength) || contentLength < 0 || contentLength > maxBodyBytes))
                throw new InvalidDataException("Invalid Content-Length.");
        }

        var body = new byte[contentLength];
        var already = Math.Min(length - (headerEnd + 4), contentLength);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        if (already < contentLength)
            await stream.ReadExactlyAsync(body.AsMemory(already), cancellationToken);
        return new HttpRequest(requestLine[0], requestLine[1].Split('?')[0], body);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length) =>
        buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
}
