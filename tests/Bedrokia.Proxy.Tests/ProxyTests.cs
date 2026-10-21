using Bedrokia.Protocol;
using Bedrokia.Proxy;

namespace Bedrokia.Proxy.Tests;

public class ProxyTests
{
    [Fact]
    public void SwapsThePlayerIdBothWays()
    {
        // MovePlayer of the player on the new server (id 300) reaches the client with its first id (5), and back.
        var fromServer = Packets.MovePlayer(300, 1, 2, 3, 0, 0, 0, teleport: true, onGround: false);
        var toClient = EntityIds.Swap(fromServer, 300, 5);
        Assert.Equal(Packets.MovePlayer(5, 1, 2, 3, 0, 0, 0, teleport: true, onGround: false), toClient);
        Assert.Equal(fromServer, EntityIds.Swap(toClient, 5, 300));

        // An entity of the new server that has the client's id gets the server's player id instead: no collision.
        Assert.Equal(Packets.RemoveActor(300), EntityIds.Swap(Packets.RemoveActor(5), 300, 5));
    }

    [Fact]
    public void LeavesOtherPacketsAndIdsAlone()
    {
        var other = Packets.RemoveActor(42);
        Assert.Same(other, EntityIds.Swap(other, 300, 5));
        var text = Packets.Text(TextType.Raw, "hi");
        Assert.Same(text, EntityIds.Swap(text, 300, 5));
        // Respawn: Vec3 and a state byte before the id.
        var respawn = new BedrockWriter().VarUInt32(45).Vec3(1, 2, 3).Byte(1).VarUInt64(300).ToArray();
        Assert.Equal(new BedrockWriter().VarUInt32(45).Vec3(1, 2, 3).Byte(1).VarUInt64(5).ToArray(), EntityIds.Swap(respawn, 300, 5));
    }

    [Fact]
    public void FindsTransferTargets()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "proxy.properties");
            // The old "servers" key is moved to servers.properties.
            File.WriteAllText(path, "servers=lobby=127.0.0.1:19133, survie=127.0.0.1:19134\ndefault-server=lobby\n");
            var config = ProxyConfig.Load(path);
            Assert.Equal("survie", config.FindTransferTarget("SURVIE", 0)?.Name);
            Assert.Equal("survie", config.FindTransferTarget("play.example.com", 19134)?.Name); // public address, same port
            Assert.Null(config.FindTransferTarget("other.example.com", 19132));
            Assert.Equal("lobby", config.DefaultServer.Name);
            Assert.Contains("webrtc-ports=auto", File.ReadAllText(path)); // missing keys are written with their default

            // Edited while the proxy runs: used at once; a broken file keeps the previous list.
            var servers = Path.Combine(dir, "servers.properties");
            File.WriteAllText(servers, "default=faction\nlobby=10.0.0.1:19135\nfaction=10.0.0.1:19136\n");
            File.SetLastWriteTimeUtc(servers, DateTime.UtcNow.AddSeconds(5));
            Assert.Equal("faction", config.DefaultServer.Name);
            Assert.Equal(19136, config.Find("faction")?.Port);
            File.WriteAllText(servers, "lobby=no-port\n");
            File.SetLastWriteTimeUtc(servers, DateTime.UtcNow.AddSeconds(10));
            Assert.Equal(2, config.Servers.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_proxy_serves_its_resource_packs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "proxy-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "pack"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "pack", "manifest.json"),
                """{"format_version":2,"header":{"name":"Test","uuid":"0db3592a-cdd6-c43f-6133-dde3fad30f1e","version":[1,2,1]},"modules":[{"type":"resources","uuid":"af715979-f448-b3a4-7157-dc2251878c62","version":[1,2,1]}]}""");
            var packs = Bedrokia.Proxy.ResourcePacks.Load(dir);
            var pack = Assert.Single(packs.All);
            Assert.Equal("1.2.1", pack.Version);
            Assert.Same(pack, packs.ById(pack.Id));
            Assert.NotNull(Bedrokia.Proxy.ResourcePacks.ChunkPacket(pack, 0));
            Assert.Null(Bedrokia.Proxy.ResourcePacks.ChunkPacket(pack, 1)); // a single chunk
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
