// ┌───────────────────────────────────────────────────────────────────┐
// │  Bedrokia Proxy - a NetherNet proxy for Bedrokia servers           │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SIPSorcery.Net;

namespace Bedrokia.Transport.NetherNet;

/// <summary>
/// Client side of NetherNet, as a game client does it: opens both data channels, posts the SDP offer to
/// POST /v1/join/{networkId} of a server and applies its answer. Anonymous (no identity): the server must accept
/// offline peers.
/// </summary>
public static class NetherNetClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Connects to the NetherNet server at <paramref name="host"/>:<paramref name="port"/> (TCP signaling).</summary>
    /// <exception cref="IOException">Signaling failed, the offer was refused or WebRTC did not connect in time.</exception>
    public static async Task<NetherNetSession> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
        if (addresses.Length == 0)
            throw new IOException($"{host} has no IPv4 address.");
        var remote = new IPEndPoint(addresses[0], port);

        var peer = new RTCPeerConnection(new RTCConfiguration { certificates2 = [NetherNetListener.CreateDtlsCertificate()] });
        var session = new NetherNetSession(peer, remote, null);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Connected += _ => connected.TrySetResult();
        session.Closed += (_, reason) => connected.TrySetException(new IOException(reason));
        try
        {
            session.Attach(await peer.createDataChannel(NetherNetSession.ReliableLabel, new RTCDataChannelInit { ordered = true }));
            session.Attach(await peer.createDataChannel(NetherNetSession.UnreliableLabel,
                new RTCDataChannelInit { ordered = false, maxRetransmits = 0 }));

            var gathered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.onicegatheringstatechange += state =>
            {
                if (state == RTCIceGatheringState.complete)
                    gathered.TrySetResult();
            };
            var offer = peer.createOffer();
            await peer.setLocalDescription(offer);
            // Host candidates are enough between servers; do not wait long for the rest.
            await Task.WhenAny(gathered.Task, Task.Delay(2000, cancellationToken));
            var sdp = peer.localDescription?.sdp?.ToString() ?? offer.sdp;

            var networkId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            using var content = new StringContent(sdp, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sdp");
            using var response = await Http.PostAsync($"http://{remote.Address}:{port}/v1/join/{networkId}", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new IOException($"the server refused the connection ({(int)response.StatusCode}: {body})");

            var result = peer.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = SdpIdentity.WithoutIdentity(body),
            });
            if (result != SetDescriptionResultEnum.OK)
                throw new IOException($"invalid answer from the server ({result})");

            await connected.Task.WaitAsync(ConnectTimeout, cancellationToken);
            return session;
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or TaskCanceledException or InvalidOperationException)
        {
            session.Disconnect("connection failed");
            throw new IOException(e is TimeoutException ? "WebRTC connection timed out" : e.Message, e);
        }
        catch
        {
            session.Disconnect("connection failed");
            throw;
        }
    }
}
