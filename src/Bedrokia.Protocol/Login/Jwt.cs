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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bedrokia.Protocol.Login;

/// <summary>Compact JWS (header.payload.signature) with the two algorithms Bedrock uses.</summary>
public sealed class Jwt
{
    private Jwt(JsonElement header, JsonElement payload, byte[] signedPart, byte[] signature)
    {
        Header = header;
        Payload = payload;
        SignedPart = signedPart;
        Signature = signature;
    }

    public JsonElement Header { get; }
    public JsonElement Payload { get; }
    private byte[] SignedPart { get; }
    private byte[] Signature { get; }

    public string? HeaderString(string name) =>
        Header.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public string? PayloadString(string name) =>
        Payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <exception cref="LoginException">The token is not a well-formed JWS.</exception>
    public static Jwt Parse(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new LoginException("Malformed token.");
        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            if (header.RootElement.ValueKind != JsonValueKind.Object || payload.RootElement.ValueKind != JsonValueKind.Object)
                throw new LoginException("Malformed token.");
            return new Jwt(header.RootElement.Clone(), payload.RootElement.Clone(),
                Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlDecode(parts[2]));
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw new LoginException("Malformed token.");
        }
    }

    /// <summary>ES384 with a raw r‖s signature, as Bedrock clients produce.</summary>
    public bool VerifyEs384(ECDsa key) =>
        HeaderString("alg") == "ES384" && key.VerifyData(SignedPart, Signature, HashAlgorithmName.SHA384);

    public bool VerifyRs256(RSA key) =>
        HeaderString("alg") == "RS256" && key.VerifyData(SignedPart, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    public static string SignEs384(ECDsa key, object header, object payload)
    {
        var signed = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return signed + "." + Base64UrlEncode(key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA384));
    }

    /// <summary>Detached ES384 JWS ("header..signature") over <paramref name="payload"/>.</summary>
    public static string SignDetachedEs384(ECDsa key, byte[] payload)
    {
        var header = Base64UrlEncode("{\"alg\":\"ES384\"}"u8.ToArray());
        var signature = key.SignData(Encoding.ASCII.GetBytes(header + "." + Base64UrlEncode(payload)), HashAlgorithmName.SHA384);
        return header + ".." + Base64UrlEncode(signature);
    }

    /// <summary>Verifies a detached ES384 JWS ("header..signature") over <paramref name="payload"/>.</summary>
    public static bool VerifyDetachedEs384(string jws, byte[] payload, ECDsa key)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3 || parts[1].Length != 0)
            return false;
        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "ES384")
                return false;
            return key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + Base64UrlEncode(payload)), Base64UrlDecode(parts[2]), HashAlgorithmName.SHA384);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return false;
        }
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A login was rejected; the message is safe to show to the player.</summary>
public sealed class LoginException(string message) : Exception(message);
