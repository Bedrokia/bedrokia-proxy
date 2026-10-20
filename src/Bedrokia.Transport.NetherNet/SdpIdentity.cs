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
using Bedrokia.Protocol.Login;

namespace Bedrokia.Transport.NetherNet;

/// <summary>
/// The 'a=identity' SDP attribute NetherNet peers use to bind their game identity to the DTLS certificate:
/// base64 JSON {assertion: "{fingerprints: detached JWS over the SDP fingerprints, token: JWT}", idp: {domain, protocol}}.
/// Format from go-nethernet (MIT), identity.go.
/// </summary>
internal static class SdpIdentity
{
    /// <summary>The canonical payload signed by the fingerprint assertion.</summary>
    public static byte[] FingerprintPayload(string algorithm, string digest) =>
        Encoding.UTF8.GetBytes("{\"fingerprint\":[{\"algorithm\":" + JsonSerializer.Serialize(algorithm)
                               + ",\"digest\":" + JsonSerializer.Serialize(digest) + "}]}");

    /// <summary>Reads "a=&lt;name&gt;:&lt;value&gt;" (first occurrence) from an SDP.</summary>
    public static string? Attribute(string sdp, string name)
    {
        foreach (var line in sdp.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("a=" + name + ":", StringComparison.Ordinal))
                return trimmed[(name.Length + 3)..];
        }
        return null;
    }

    public static string WithoutIdentity(string sdp) =>
        string.Join('\n', sdp.Split('\n').Where(l => !l.StartsWith("a=identity:", StringComparison.Ordinal)));

    public static (string Algorithm, string Digest) Fingerprint(string sdp)
    {
        var value = Attribute(sdp, "fingerprint") ?? throw new InvalidDataException("SDP has no fingerprint.");
        var parts = value.Split(' ', 2);
        if (parts.Length != 2)
            throw new InvalidDataException("Malformed SDP fingerprint.");
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Verifies the client's identity in <paramref name="offer"/> and returns the key it proved to own,
    /// or null when the offer carries no identity.
    /// </summary>
    /// <param name="verifyToken">Checks the multiplayer token and returns its 'cpk' key (throws <see cref="LoginException"/>).</param>
    /// <exception cref="LoginException">The identity is malformed or does not verify.</exception>
    public static ECDsa? VerifyOffer(string offer, Func<string, ECDsa> verifyToken)
    {
        if (Attribute(offer, "identity") is not { } encoded)
            return null;
        string fingerprints, token;
        try
        {
            using var identity = JsonDocument.Parse(Convert.FromBase64String(encoded));
            if (identity.RootElement.GetProperty("idp").GetProperty("protocol").GetString() != "default")
                throw new LoginException("Unsupported identity protocol.");
            using var assertion = JsonDocument.Parse(identity.RootElement.GetProperty("assertion").GetString()!);
            fingerprints = assertion.RootElement.GetProperty("fingerprints").GetString()!;
            token = assertion.RootElement.GetProperty("token").GetString()!;
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentNullException)
        {
            throw new LoginException("Malformed NetherNet identity.");
        }

        var key = verifyToken(token);
        var (algorithm, digest) = Fingerprint(offer);
        if (!Jwt.VerifyDetachedEs384(fingerprints, FingerprintPayload(algorithm, digest), key))
        {
            key.Dispose();
            throw new LoginException("NetherNet identity does not match the connection.");
        }
        return key;
    }

    /// <summary>Adds the server's identity to <paramref name="answer"/> (as a session-level attribute).</summary>
    public static string SignAnswer(string answer, ECDsa serverKey, string domain = "self")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = Jwt.SignEs384(serverKey,
            new Dictionary<string, string> { ["alg"] = "ES384", ["x5u"] = Convert.ToBase64String(serverKey.ExportSubjectPublicKeyInfo()) },
            new Dictionary<string, object> { ["exp"] = now + 60, ["iat"] = now, ["cpk"] = LoginRequest.ToJwk(serverKey) });
        var (algorithm, digest) = Fingerprint(answer);
        var assertion = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["fingerprints"] = Jwt.SignDetachedEs384(serverKey, FingerprintPayload(algorithm, digest)),
            ["token"] = token,
        });
        var identity = JsonSerializer.Serialize(new
        {
            assertion,
            idp = new { domain, protocol = "default" },
        });
        var line = "a=identity:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(identity)) + "\r\n";

        // Session-level attributes go before the first media section.
        var media = answer.IndexOf("\r\nm=", StringComparison.Ordinal);
        if (media < 0)
            media = answer.IndexOf("\nm=", StringComparison.Ordinal);
        if (media < 0)
            throw new InvalidDataException("SDP answer has no media section.");
        var insertAt = answer.IndexOf('\n', media) + 1;
        return answer[..insertAt] + line + answer[insertAt..];
    }
}
