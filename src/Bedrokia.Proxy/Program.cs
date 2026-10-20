// ┌───────────────────────────────────────────────────────────────────┐
// │  Bedrokia Proxy - a NetherNet proxy for Bedrokia servers           │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

using Bedrokia.Proxy;

ProxyConfig config;
try
{
    config = ProxyConfig.Load("proxy.properties");
}
catch (InvalidDataException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

ProxyServer.Log($"Bedrokia Proxy {typeof(ProxyServer).Assembly.GetName().Version?.ToString(3)} (Bedrock {Bedrokia.Protocol.ProtocolInfo.GameVersion}).");
var proxy = new ProxyServer(config);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

_ = Task.Run(() =>
{
    while (Console.ReadLine() is { } line)
    {
        var args = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (args.FirstOrDefault()?.ToLowerInvariant() ?? "")
        {
            case "send" when args.Length == 3:
                var player = proxy.Players.FirstOrDefault(p => string.Equals(p.Name, args[1], StringComparison.OrdinalIgnoreCase));
                if (player is null)
                    ProxyServer.Log($"{args[1]} is not online.");
                else if (config.Find(args[2]) is not { } server)
                    ProxyServer.Log($"Unknown server {args[2]}: {string.Join(", ", config.Servers.Select(s => s.Name))}.");
                else
                    player.SendTo(server);
                break;
            case "stop" or "end":
                stop.Cancel();
                return;
            case "list":
                var players = proxy.Players.Where(p => p.Name is not null).ToList();
                ProxyServer.Log($"{players.Count} player(s): {string.Join(", ", players.Select(p => $"{p.Name} ({p.ServerName})"))}");
                break;
            case "":
                break;
            default:
                ProxyServer.Log("Commands: list, send <player> <server>, stop");
                break;
        }
    }
});

await proxy.RunAsync(stop.Token);
ProxyServer.Log("Proxy stopped.");
return 0;
