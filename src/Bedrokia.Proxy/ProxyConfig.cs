// ┌───────────────────────────────────────────────────────────────────┐
// │  Bedrokia Proxy - a NetherNet proxy for Bedrokia servers           │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

namespace Bedrokia.Proxy;

/// <summary>A Bedrokia server behind the proxy.</summary>
public sealed record BackendServer(string Name, string Host, int Port);

/// <summary>
/// proxy.properties (network settings) and servers.properties (the servers behind the proxy, reloaded by itself
/// when the file changes): key=value lines, '#' comments. Missing keys are added with their default.
/// </summary>
public sealed class ProxyConfig
{
    public int Port { get; private set; } = 19132;
    public string PublicIp { get; private set; } = "";
    public string WebRtcPorts { get; private set; } = "auto";

    /// <summary>Players must accept the proxy's resource packs to join.</summary>
    public bool ForceResourcePacks { get; private set; }

    private readonly Lock _serversLock = new();
    private string _serversPath = "";
    private DateTime _serversStamp;
    private IReadOnlyList<BackendServer> _servers = [];
    private BackendServer _defaultServer = null!;

    private static readonly (string Key, string Default, string Comment)[] Keys =
    [
        ("server-port", "19132", "Port players join on (TCP signaling; UDP on the same number with webrtc-ports=auto)"),
        ("public-ip", "", "Address announced to players (empty: found with STUN in Docker/Pterodactyl)"),
        ("webrtc-ports", "auto", "auto = every player on the UDP port of server-port, or first-last = one port per player"),
        ("force-resources", "off", "on/off: players must accept the resource packs of resource_packs/ (downloaded once, used on every server)"),
    ];

    public IReadOnlyList<BackendServer> Servers
    {
        get
        {
            lock (_serversLock)
            {
                ReloadServersIfChanged();
                return _servers;
            }
        }
    }

    public BackendServer DefaultServer
    {
        get
        {
            lock (_serversLock)
            {
                ReloadServersIfChanged();
                return _defaultServer;
            }
        }
    }

    private static Dictionary<string, string> ReadProperties(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && !line.TrimStart().StartsWith('#'))
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    /// <exception cref="InvalidDataException">A value is invalid.</exception>
    public static ProxyConfig Load(string path)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var values = ReadProperties(lines);
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
            WebRtcPorts = values["webrtc-ports"],
            ForceResourcePacks = values["force-resources"].ToLowerInvariant() is "on" or "true" or "yes",
        };
        if (!int.TryParse(values["server-port"], out var port) || port is < 1 or > 65535)
            throw new InvalidDataException($"{path}: server-port must be a port number.");
        config.Port = port;
        if (config.WebRtcRange() is null && !config.SharesWebRtcPort)
            throw new InvalidDataException($"{path}: webrtc-ports must be auto or look like 19140-19160.");

        config._serversPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "servers.properties");
        if (!File.Exists(config._serversPath))
        {
            // Older proxy.properties kept the list in "servers" (name=host:port, comma separated): moved over.
            var old = values.GetValueOrDefault("servers", "lobby=127.0.0.1:19133")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            File.WriteAllLines(config._serversPath,
            [
                "# Servers behind the proxy, one per line: name=ip:port (the ip:port of the server in the panel).",
                "# Players and plugins use the names (Transfer(\"survie\", 0), Lobby menu). Saved changes apply at once.",
                "# In Pterodactyl, 127.0.0.1 is the proxy's own container: use the node IP (or 172.18.0.1).",
                "",
                "# Server players join first",
                $"default={values.GetValueOrDefault("default-server", old.FirstOrDefault()?.Split('=')[0] ?? "lobby")}",
                "",
                .. old,
            ]);
        }
        config.ReloadServersIfChanged(throwOnError: true);
        return config;
    }

    // Called under _serversLock (or before the config is shared). A broken file keeps the previous list.
    private void ReloadServersIfChanged(bool throwOnError = false)
    {
        var stamp = File.Exists(_serversPath) ? File.GetLastWriteTimeUtc(_serversPath) : default;
        if (stamp == _serversStamp)
            return;
        _serversStamp = stamp;
        try
        {
            var values = ReadProperties(File.ReadAllLines(_serversPath));
            var servers = new List<BackendServer>();
            foreach (var (name, address) in values.Where(v => !v.Key.Equals("default", StringComparison.OrdinalIgnoreCase)))
            {
                var colon = address.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(address[(colon + 1)..], out var p) || p is < 1 or > 65535)
                    throw new InvalidDataException($"{_serversPath}: '{name}={address}' must look like name=ip:port.");
                servers.Add(new BackendServer(name, address[..colon].Trim(), p));
            }
            if (servers.Count == 0)
                throw new InvalidDataException($"{_serversPath}: no server (add lines like lobby=ip:port).");
            var defaultName = values.GetValueOrDefault("default", servers[0].Name);
            var defaultServer = servers.FirstOrDefault(s => s.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{_serversPath}: default '{defaultName}' is not one of the servers.");
            var first = _servers.Count == 0;
            _servers = servers;
            _defaultServer = defaultServer;
            ProxyServer.Log($"{(first ? "Servers" : "servers.properties reloaded")}: {string.Join(", ", servers.Select(s => $"{s.Name} ({s.Host}:{s.Port})"))}; default: {defaultServer.Name}.");
        }
        catch (Exception e) when (e is InvalidDataException or IOException && !throwOnError)
        {
            ProxyServer.Log($"{e.Message} The previous server list is kept.");
        }
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
