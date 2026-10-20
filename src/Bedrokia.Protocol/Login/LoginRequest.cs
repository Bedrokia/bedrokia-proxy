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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bedrokia.Protocol.Login;

/// <summary>Who the player is, after the login was checked.</summary>
/// <param name="Authenticated">True when the identity was signed by the Minecraft authorization service.</param>
/// <param name="ClientData">Client data claims (skin, device...); signed by the client, so not trusted for identity.</param>
public sealed record PlayerIdentity(string Name, string Xuid, Guid Uuid, ECDsa ClientKey, bool Authenticated, JsonElement ClientData);

/// <summary>
/// Parses and verifies the connection request of a Login packet:
/// int32 LE length ‖ JSON {Certificate, AuthenticationType, Token} ‖ int32 LE length ‖ client data JWT.
/// </summary>
public static class LoginRequest
{
    private const int MaxSectionLength = 1 << 20;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <param name="keys">Authorization service keys; null accepts unverified identities (offline mode, tests).</param>
    /// <exception cref="LoginException">Malformed request or failed verification.</exception>
    public static PlayerIdentity Verify(ReadOnlySpan<byte> request, AuthKeys.Snapshot? keys, DateTimeOffset now)
    {
        var chainJson = Section(ref request);
        var clientData = Encoding.UTF8.GetString(Section(ref request));
        if (!request.IsEmpty)
            throw new LoginException("Unexpected data after the login request.");

        string token;
        try
        {
            using var chain = JsonDocument.Parse(chainJson.ToArray());
            token = chain.RootElement.TryGetProperty("Token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        }
        catch (JsonException)
        {
            throw new LoginException("Malformed login request.");
        }
        if (token.Length == 0)
            throw new LoginException("This client is too old: no multiplayer token in the login request.");

        var (jwt, clientKey) = VerifyMultiplayerToken(token, keys, now);
        var authenticated = keys is not null;
        var xuid = jwt.PayloadString("xid") ?? "";
        var name = jwt.PayloadString("xname") ?? "";
        if (name.Length is 0 or > 32 || name.Any(char.IsControl))
        {
            clientKey.Dispose();
            throw new LoginException("Invalid player name.");
        }
        // The client data is signed with the key the token vouches for: proves the client owns that key.
        var clientDataToken = Jwt.Parse(clientData);
        if (!clientDataToken.VerifyEs384(clientKey))
        {
            clientKey.Dispose();
            throw new LoginException("Invalid client data signature.");
        }

        var uuid = xuid.Length > 0
            ? UuidFromXuid(xuid)
            : Guid.TryParse(jwt.PayloadString("leguuid"), out var legacy) ? legacy : Guid.NewGuid();
        return new PlayerIdentity(name, xuid, uuid, clientKey, authenticated, clientDataToken.Payload);
    }

    /// <summary>
    /// Checks a multiplayer token (from the Login packet or a NetherNet identity) and returns it with the client
    /// public key from its 'cpk' claim. With <paramref name="keys"/> null the signature is not checked (offline mode).
    /// </summary>
    /// <exception cref="LoginException">Invalid signature, claims or public key.</exception>
    public static (Jwt Token, ECDsa ClientKey) VerifyMultiplayerToken(string token, AuthKeys.Snapshot? keys, DateTimeOffset now)
    {
        var jwt = Jwt.Parse(token);
        if (keys is not null)
        {
            if (jwt.HeaderString("kid") is not { } kid || !keys.Keys.TryGetValue(kid, out var key) || !jwt.VerifyRs256(key))
                throw new LoginException("Your Xbox Live login could not be verified.");
            CheckClaims(jwt.Payload, keys.Issuer, now);
            if (string.IsNullOrEmpty(jwt.PayloadString("xid")))
                throw new LoginException("You must be signed in with Xbox Live to join.");
        }
        if (!jwt.Payload.TryGetProperty("cpk", out var cpk))
            throw new LoginException("Invalid client public key.");
        return (jwt, ParsePublicKey(cpk));
    }

    /// <summary>A P-384 public key given either as base64 SubjectPublicKeyInfo or, since 26.40, as a JWK object.</summary>
    /// <exception cref="LoginException">The key is malformed.</exception>
    public static ECDsa ParsePublicKey(JsonElement value)
    {
        var key = ECDsa.Create();
        try
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value.GetString()!), out _);
            }
            else if (value.ValueKind == JsonValueKind.Object
                     && value.GetProperty("kty").GetString() == "EC" && value.GetProperty("crv").GetString() == "P-384")
            {
                key.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP384,
                    Q = new ECPoint
                    {
                        X = Jwt.Base64UrlDecode(value.GetProperty("x").GetString()!),
                        Y = Jwt.Base64UrlDecode(value.GetProperty("y").GetString()!),
                    },
                });
            }
            else
            {
                throw new FormatException("Unsupported key format.");
            }
            if (key.KeySize != 384)
                throw new FormatException("Not a P-384 key.");
            return key;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            key.Dispose();
            throw new LoginException("Invalid client public key.");
        }
    }

    /// <summary>JWK form of a P-384 public key, as Bedrock writes the 'cpk' claim.</summary>
    public static Dictionary<string, string> ToJwk(ECDsa key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        return new Dictionary<string, string>
        {
            ["kty"] = "EC",
            ["crv"] = "P-384",
            ["x"] = Jwt.Base64UrlEncode(q.X!),
            ["y"] = Jwt.Base64UrlEncode(q.Y!),
        };
    }

    private static ReadOnlySpan<byte> Section(ref ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            throw new LoginException("Truncated login request.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (length <= 0 || length > MaxSectionLength || length > data.Length - 4)
            throw new LoginException("Invalid login request length.");
        var section = data.Slice(4, length);
        data = data[(4 + length)..];
        return section;
    }

    private static void CheckClaims(JsonElement payload, string issuer, DateTimeOffset now)
    {
        var iss = payload.TryGetProperty("iss", out var i) ? i.GetString() : null;
        if (iss?.TrimEnd('/') != issuer.TrimEnd('/'))
            throw new LoginException("Your Xbox Live login was issued by an unknown service.");

        var audienceOk = payload.TryGetProperty("aud", out var aud) && aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == AuthKeys.Audience,
            JsonValueKind.Array => aud.EnumerateArray().Any(a => a.GetString() == AuthKeys.Audience),
            _ => false,
        };
        if (!audienceOk)
            throw new LoginException("Your Xbox Live login is not meant for multiplayer servers.");

        if (!payload.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expires)
            || DateTimeOffset.FromUnixTimeSeconds(expires) < now - ClockSkew)
            throw new LoginException("Your Xbox Live login has expired. Restart the game and try again.");
        if (payload.TryGetProperty("nbf", out var nbf) && nbf.TryGetInt64(out var notBefore)
            && DateTimeOffset.FromUnixTimeSeconds(notBefore) > now + ClockSkew)
            throw new LoginException("Your Xbox Live login is not valid yet. Check your clock.");
    }

    /// <summary>Version 3 UUID of "pocket-auth-1-xuid:" + xuid, as other Bedrock servers derive it.</summary>
    public static Guid UuidFromXuid(string xuid)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("pocket-auth-1-xuid:" + xuid));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }

    /// <summary>
    /// Server half of the encryption handshake: returns the ServerToClientHandshake JWT and the session key
    /// SHA-256(salt ‖ ECDH(server, client)).
    /// </summary>
    public static (string Jwt, byte[] Key) CreateEncryptionHandshake(ECDsa clientKey)
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        using var client = ECDiffieHellman.Create(clientKey.ExportParameters(includePrivateParameters: false));
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = SHA256.HashData([.. salt, .. server.DeriveRawSecretAgreement(client.PublicKey)]);

        using var signer = ECDsa.Create(server.ExportParameters(includePrivateParameters: true));
        var jwt = Jwt.SignEs384(signer,
            new Dictionary<string, string> { ["alg"] = "ES384", ["x5u"] = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) },
            new Dictionary<string, string> { ["salt"] = Convert.ToBase64String(salt).TrimEnd('=') });
        return (jwt, key);
    }
}
