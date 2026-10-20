// ┌───────────────────────────────────────────────────────────────────┐
// │  Bedrokia Proxy - a NetherNet proxy for Bedrokia servers           │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Bedrokia.Protocol.Login;
using Bedrokia.Transport.NetherNet;

namespace Bedrokia.Proxy;

/// <summary>
/// Players join the proxy (NetherNet, Xbox Live checked here) and play on a Bedrokia server behind it. A Transfer
/// packet from that server towards another server of the list moves the player there on the same connection.
/// </summary>
public sealed class ProxyServer(ProxyConfig config)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AuthKeys _authKeys = new(Http);
    private readonly ConcurrentDictionary<PlayerSession, byte> _players = new();

    public ProxyConfig Config { get; } = config;

    /// <summary>Null until loaded: nobody can join before (Xbox Live is mandatory, against cracked accounts and stolen names).</summary>
    public AuthKeys.Snapshot? AuthKeys => _authKeys.Current;

    public IEnumerable<PlayerSession> Players => _players.Keys;

    public static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    public async Task RunAsync(CancellationToken stop)
    {
        var range = Config.WebRtcRange();
        using var listener = new NetherNetListener(new IPEndPoint(IPAddress.Any, Config.Port),
            LoadOrCreateKey(Path.Combine("data", "nethernet-identity.pem")),
            VerifyToken, allowAnonymous: false,
            range is { } r ? new SIPSorcery.Sys.PortRange(r.First, Math.Max(r.First + 1, r.Last)) : null,
            sharedUdpPort: Config.SharesWebRtcPort ? Config.Port : null)
        {
            // Pterodactyl gives 0.0.0.0 for an allocation on every address: not one players can reach.
            PublicAddress = IPAddress.TryParse(Config.PublicIp, out var ip) && !ip.Equals(IPAddress.Any) ? ip : DetectPublicIp(),
        };
        listener.SessionOpened += session =>
        {
            var player = new PlayerSession(this, session);
            _players[player] = 0;
            session.Closed += (_, reason) =>
            {
                _players.TryRemove(player, out byte _);
                player.OnClientClosed(reason);
            };
        };
        var signaling = listener.RunAsync(stop);
        _ = RefreshAuthKeysAsync(stop);
        Log($"Listening on port {Config.Port}: TCP (join) + UDP. Type 'help' for commands.");
        await signaling;
        foreach (var player in Players)
            player.Kick("Le proxy redémarre.");
        await Task.Delay(600, CancellationToken.None); // lets the Disconnect packets go out
    }

    private ECDsa VerifyToken(string token)
    {
        var keys = AuthKeys ?? throw new LoginException("The server cannot reach Xbox Live right now. Try again in a minute.");
        return LoginRequest.VerifyMultiplayerToken(token, keys, DateTimeOffset.UtcNow).ClientKey;
    }

    private static IPAddress? DetectPublicIp()
    {
        if (!File.Exists("/.dockerenv") && Environment.GetEnvironmentVariable("P_SERVER_UUID") is null)
            return null;
        try
        {
            if (SIPSorcery.Net.STUNClient.GetPublicIPAddress("stun.l.google.com", 19302) is { } ip)
            {
                Log($"public-ip is empty: players will connect to {ip} (found with STUN).");
                return ip;
            }
        }
        catch (Exception e)
        {
            Log($"STUN failed: {e.Message}");
        }
        Log("public-ip is empty and the public address could not be found: set public-ip in proxy.properties.");
        return null;
    }

    private async Task RefreshAuthKeysAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(30);
            try
            {
                var first = _authKeys.Current is null;
                await _authKeys.RefreshAsync(stop);
                if (first)
                    Log("Xbox Live authentication keys loaded.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or TaskCanceledException)
            {
                Log($"Cannot load Xbox Live authentication keys ({e.Message}); retrying in 30 s.");
                delay = TimeSpan.FromSeconds(30);
            }
            try
            {
                await Task.Delay(delay, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Clients trust the key on first use: it must survive restarts.
    private static ECDsa LoadOrCreateKey(string path)
    {
        var key = ECDsa.Create();
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        key.GenerateKey(ECCurve.NamedCurves.nistP384);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, key.ExportECPrivateKeyPem());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}
