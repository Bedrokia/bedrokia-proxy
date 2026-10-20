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
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "servers=lobby=127.0.0.1:19133, survie=127.0.0.1:19134\ndefault-server=lobby\n");
            var config = ProxyConfig.Load(path);
            Assert.Equal("survie", config.FindTransferTarget("SURVIE", 0)?.Name);
            Assert.Equal("survie", config.FindTransferTarget("play.example.com", 19134)?.Name); // public address, same port
            Assert.Null(config.FindTransferTarget("other.example.com", 19132));
            Assert.Contains("xbox-auth=on", File.ReadAllText(path)); // missing keys are written with their default
        }
        finally
        {
            File.Delete(path);
        }
    }
}
