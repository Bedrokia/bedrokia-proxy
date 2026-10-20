// ┌───────────────────────────────────────────────────────────────────┐
// │  Bedrokia Proxy - a NetherNet proxy for Bedrokia servers           │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

namespace Bedrokia.Proxy;

/// <summary>A Bedrokia server behind the proxy.</summary>
public sealed record BackendServer(string Name, string Host, int Port);

/// <summary>proxy.properties: key=value lines, '#' comments. Missing keys are added with their default.</summary>
public sealed class ProxyConfig
{
    public int Port { get; private set; } = 19132;
    public string PublicIp { get; private set; } = "";
    public bool OnlineMode { get; private set; } = true;
    public string WebRtcPorts { get; private set; } = "auto";
    public List<BackendServer> Servers { get; } = [];
    public BackendServer DefaultServer { get; private set; } = null!;

    private static readonly (string Key, string Default, string Comment)[] Keys =
    [
        ("server-port", "19132", "Port players join on (TCP signaling; UDP on the same number with webrtc-ports=auto)"),
        ("public-ip", "", "Address announced to players (empty: found with STUN in Docker/Pterodactyl)"),
        ("xbox-auth", "on", "on: players are verified with Xbox Live (keep it on: the servers behind trust the proxy)"),
        ("webrtc-ports", "auto", "auto = every player on the UDP port of server-port, or first-last = one port per player"),
        ("servers", "lobby=127.0.0.1:19133", "Servers behind the proxy: name=host:port, separated by commas"),
        ("default-server", "lobby", "Server players join first"),
    ];

    /// <exception cref="InvalidDataException">A value is invalid.</exception>
    public static ProxyConfig Load(string path)
    {
        var values = new Dictionary<string, string>();
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && !line.TrimStart().StartsWith('#'))
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        var missing = Keys.Where(k => !values.ContainsKey(k.Key)).ToList();
        if (missing.Count > 0)
        {
            foreach (var (key, def, comment) in missing)
            {
                lines.Add($"# {comment}");
                lines.Add($"{key}={def}");
                values[key] = def;
            }
            File.WriteAllLines(path, lines);
        }

        var config = new ProxyConfig
        {
            PublicIp = values["public-ip"],
            OnlineMode = values["xbox-auth"] is not ("off" or "false"),
            WebRtcPorts = values["webrtc-ports"],
        };
        if (!int.TryParse(values["server-port"], out var port) || port is < 1 or > 65535)
            throw new InvalidDataException($"{path}: server-port must be a port number.");
        config.Port = port;
        foreach (var entry in values["servers"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2);
            var colon = parts.Length == 2 ? parts[1].LastIndexOf(':') : -1;
            if (colon <= 0 || !int.TryParse(parts[1][(colon + 1)..], out var p) || p is < 1 or > 65535 || parts[0].Trim().Length == 0)
                throw new InvalidDataException($"{path}: servers: '{entry}' must look like name=host:port.");
            config.Servers.Add(new BackendServer(parts[0].Trim(), parts[1][..colon].Trim(), p));
        }
        if (config.Servers.Count == 0)
            throw new InvalidDataException($"{path}: servers is empty.");
        config.DefaultServer = config.Find(values["default-server"])
            ?? throw new InvalidDataException($"{path}: default-server '{values["default-server"]}' is not in servers.");
        if (config.WebRtcRange() is null && !config.SharesWebRtcPort)
            throw new InvalidDataException($"{path}: webrtc-ports must be auto or look like 19140-19160.");
        return config;
    }

    public BackendServer? Find(string name) =>
        Servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The server a Transfer packet points to: by name, by host and port, or by port alone when only one server
    /// uses it (plugins usually know the public address, the proxy the internal one). Null: a real transfer.
    /// </summary>
    public BackendServer? FindTransferTarget(string address, int port) =>
        Find(address)
        ?? Servers.FirstOrDefault(s => s.Port == port && s.Host.Equals(address, StringComparison.OrdinalIgnoreCase))
        ?? (Servers.Count(s => s.Port == port) == 1 ? Servers.First(s => s.Port == port) : null);

    public bool SharesWebRtcPort => WebRtcPorts.Equals("auto", StringComparison.OrdinalIgnoreCase);

    public (int First, int Last)? WebRtcRange()
    {
        var parts = WebRtcPorts.Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], out var first) && int.TryParse(parts[1], out var last)
               && first is >= 1024 and <= 65535 && last >= first && last <= 65535
            ? (first, last)
            : null;
    }
}
