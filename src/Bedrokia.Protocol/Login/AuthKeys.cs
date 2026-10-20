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

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bedrokia.Protocol.Login;

/// <summary>
/// Signing keys of the Minecraft authorization service, used to verify the OpenID "multiplayer token"
/// that clients send since 1.26.10. Discovery → issuer → /.well-known/openid-configuration → JWKS.
/// Thread-safe: <see cref="Current"/> is an immutable snapshot replaced on refresh.
/// </summary>
public sealed class AuthKeys(HttpClient http)
{
    public const string Audience = "api://auth-minecraft-services/multiplayer";
    private const string DiscoveryUrl = "https://client.discovery.minecraft-services.net/api/v1.0/discovery/MinecraftPE/builds/";

    private volatile Snapshot? _current;

    public sealed record Snapshot(string Issuer, IReadOnlyDictionary<string, RSA> Keys);

    /// <summary>Null until the first successful <see cref="RefreshAsync"/>.</summary>
    public Snapshot? Current => _current;

    /// <exception cref="HttpRequestException">A service could not be reached.</exception>
    /// <exception cref="InvalidDataException">A service answered with unexpected content.</exception>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var discovery = await GetJsonAsync(DiscoveryUrl + ProtocolInfo.GameVersion, cancellationToken);
            var issuer = discovery.RootElement.GetProperty("result").GetProperty("serviceEnvironments")
                .GetProperty("auth").GetProperty("prod").GetProperty("issuer").GetString()
                ?? throw new InvalidDataException("Discovery has no auth issuer.");

            using var openId = await GetJsonAsync(issuer.TrimEnd('/') + "/.well-known/openid-configuration", cancellationToken);
            var jwksUri = openId.RootElement.GetProperty("jwks_uri").GetString()
                ?? throw new InvalidDataException("OpenID configuration has no jwks_uri.");
            var tokenIssuer = openId.RootElement.GetProperty("issuer").GetString() ?? issuer;

            using var jwks = await GetJsonAsync(jwksUri, cancellationToken);
            var keys = new Dictionary<string, RSA>();
            foreach (var key in jwks.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (key.GetProperty("kty").GetString() != "RSA" || key.GetProperty("kid").GetString() is not { } kid)
                    continue;
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Jwt.Base64UrlDecode(key.GetProperty("n").GetString()!),
                    Exponent = Jwt.Base64UrlDecode(key.GetProperty("e").GetString()!),
                });
                keys[kid] = rsa;
            }
            if (keys.Count == 0)
                throw new InvalidDataException("JWKS contains no RSA key.");
            _current = new Snapshot(tokenIssuer, keys);
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"Unexpected authorization service response: {e.Message}", e);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
            ?? throw new InvalidDataException($"{url} returned an empty body.");
    }
}
