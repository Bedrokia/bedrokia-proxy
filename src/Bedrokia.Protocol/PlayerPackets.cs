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

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bedrokia.Protocol;

/// <summary>What other clients need to display a player.</summary>
public sealed record PlayerInfo(Guid Uuid, long EntityId, string Name, string Xuid, int BuildPlatform, byte[] Skin);

/// <summary>
/// Player list, AddPlayer and skins. Field order from gophertunnel (MIT) at protocol 2193
/// (packet/add_player.go, player.go PlayerListEntry, skin.go).
/// </summary>
public static class PlayerPackets
{
    private const int MaxImageBytes = 512 * 512 * 4;

    /// <summary>UUID as Bedrock writes it: each 8-byte half little-endian, most significant half first.</summary>
    public static BedrockWriter Uuid(this BedrockWriter writer, Guid uuid)
    {
        var b = uuid.ToByteArray(bigEndian: true);
        Span<byte> wire = stackalloc byte[16];
        for (var i = 0; i < 8; i++)
        {
            wire[i] = b[7 - i];
            wire[8 + i] = b[15 - i];
        }
        return writer.Bytes(wire);
    }

    /// <summary>
    /// Entity metadata of a living player. Without the "breathing" flag and air values the client shows the
    /// oxygen bubbles permanently, as if underwater. Keys, flags and types from gophertunnel (MIT) entity_metadata.go.
    /// </summary>
    public static BedrockWriter PlayerMetadata(this BedrockWriter w, string name, bool showName = true)
    {
        const int typeInt16 = 1, typeFloat = 3, typeString = 4, typeInt64 = 7;
        const long nameFlags = (1L << 14) | (1L << 15); // show name, always show name
        var flags = (showName ? nameFlags : 0) | (1L << 35) | (1L << 48) | (1L << 49); // breathing, collision, gravity
        void Key(uint key, uint type) => w.VarUInt32(key).VarUInt32(type).Byte((byte)type); // type + legacy type byte

        w.VarUInt32(7);
        Key(0, typeInt64); w.VarInt64(flags);
        Key(4, typeString); w.String(name);
        Key(7, typeInt16); w.Int16(300); // air supply
        Key(38, typeFloat); w.Float32(1); // scale
        Key(42, typeInt16); w.Int16(300); // max air supply
        Key(53, typeFloat); w.Float32(0.6f); // width
        Key(54, typeFloat); w.Float32(1.8f); // height
        return w;
    }

    /// <summary>
    /// UpdateAttributes for a player: walk speed, health, hunger and experience. Without it the client has no
    /// movement speed and walks too slowly. Names and defaults from Dragonfly (MIT) session/player.go.
    /// </summary>
    public static byte[] PlayerAttributes(long entityId, float health, float food, float saturation, int level = 0, float progress = 0, float speed = 0.1f, float maxHealth = 20f)
    {
        var attributes = new (string Name, float Max, float Value, float Default)[]
        {
            ("minecraft:movement", float.MaxValue, speed, 0.1f),
            ("minecraft:health", maxHealth, health, maxHealth),
            ("minecraft:absorption", float.MaxValue, 0, 0),
            ("minecraft:player.hunger", 20, food, 20),
            ("minecraft:player.saturation", 20, saturation, 20),
            ("minecraft:player.exhaustion", 5, 0, 0),
            ("minecraft:player.level", 24791, level, 0),
            ("minecraft:player.experience", 1, progress, 0),
        };
        var w = new BedrockWriter().VarUInt32(PacketId.UpdateAttributes).VarUInt64((ulong)entityId).VarUInt32((uint)attributes.Length);
        foreach (var (name, max, value, def) in attributes)
            w.Float32(0).Float32(max).Float32(value).Float32(0).Float32(max).Float32(def).String(name).VarUInt32(0); // no modifiers
        return w.VarUInt64(0).ToArray(); // tick
    }

    /// <summary>SetActorData for an entity's metadata (here: a player, including the receiving client itself).</summary>
    public static byte[] SetPlayerData(long entityId, string name, bool showName = true) => new BedrockWriter().VarUInt32(PacketId.SetActorData)
        .VarUInt64((ulong)entityId)
        .PlayerMetadata(name, showName)
        .VarUInt32(0).VarUInt32(0) // entity properties
        .VarUInt64(0) // tick
        .ToArray();

    public static byte[] PlayerListAdd(IReadOnlyCollection<PlayerInfo> players)
    {
        var w = new BedrockWriter().VarUInt32(PacketId.PlayerList).VarUInt32((uint)players.Count);
        foreach (var p in players)
        {
            w.VarUInt32(1).Byte(0); // action variant: add (+ legacy byte)
            w.Uuid(p.Uuid)
                .VarInt64(p.EntityId)
                .String(p.Name)
                .String(p.Xuid)
                .String("") // PlayFab id (1.26.60)
                .String("") // platform chat id
                .Int32(p.BuildPlatform)
                .Bytes(p.Skin)
                .Bool(false).Bool(false).Bool(false) // teacher, host, sub-client
                .Int32BE(unchecked((int)0xFF000000)); // player colour (BE ARGB packing)
        }
        return w.ToArray();
    }

    public static byte[] PlayerListRemove(Guid uuid) => new BedrockWriter().VarUInt32(PacketId.PlayerList).VarUInt32(1)
        .VarUInt32(0).Byte(1) // action variant: remove (+ legacy byte)
        .Uuid(uuid)
        .ToArray();

    public static byte[] AddPlayer(PlayerInfo p, float x, float y, float z, float pitch, float yaw, int gameMode, bool showName = true, string? nameTag = null) =>
        new BedrockWriter().VarUInt32(PacketId.AddPlayer)
            .Uuid(p.Uuid)
            .String(p.Name)
            .VarUInt64((ulong)p.EntityId)
            .String("") // platform chat id
            .Vec3(x, y, z)
            .Vec3(0, 0, 0) // velocity
            .Float32(pitch).Float32(yaw).Float32(yaw)
            .Int16(0).UInt16(0).VarUInt32(0).Bool(false).VarUInt32(0).VarUInt32(0) // held item: air
            .VarInt32(gameMode)
            .PlayerMetadata(nameTag ?? p.Name, showName)
            .VarUInt32(0).VarUInt32(0) // entity properties (int, float)
            .Int64(p.EntityId).Byte(1).Byte(0).VarUInt32(0) // abilities: member, normal commands, no layers
            .VarUInt32(0) // entity links
            .Bool(false) // passenger of block (1.26.60)
            .String("") // device id
            .Int32(p.BuildPlatform)
            .ToArray();

    /// <summary>
    /// Encodes the skin carried in the client data of the Login packet.
    /// ponytail: persona pieces and their tints are not forwarded (classic rendering of the skin texture).
    /// </summary>
    /// <exception cref="InvalidDataException">Missing or inconsistent skin fields.</exception>
    public static byte[] Skin(JsonElement clientData)
    {
        try
        {
            string Str(string name) => clientData.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            uint UInt(string name) => clientData.TryGetProperty(name, out var v) && v.TryGetUInt32(out var u) ? u : 0;
            bool Bool(string name) => clientData.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            byte[] B64(string name) => Convert.FromBase64String(Str(name));

            var w = new BedrockWriter(32 * 1024);
            var width = UInt("SkinImageWidth");
            var height = UInt("SkinImageHeight");
            var skin = B64("SkinData");
            Check(width, height, skin);

            w.String(Str("SkinId")).ByteArray(B64("SkinResourcePatch"))
                .UInt32(width).UInt32(height).ByteArray(skin);

            var animations = clientData.TryGetProperty("AnimatedImageData", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().ToList()
                : [];
            w.VarUInt32((uint)animations.Count);
            foreach (var animation in animations)
            {
                var aw = animation.GetProperty("ImageWidth").GetUInt32();
                var ah = animation.GetProperty("ImageHeight").GetUInt32();
                var image = Convert.FromBase64String(animation.GetProperty("Image").GetString()!);
                Check(aw, ah, image);
                w.UInt32(aw).UInt32(ah).ByteArray(image)
                    .VarUInt32(animation.GetProperty("Type").GetUInt32())
                    .Float32(animation.GetProperty("Frames").GetSingle())
                    .VarUInt32(animation.TryGetProperty("AnimationExpression", out var e) ? e.GetUInt32() : 0);
            }

            var capeWidth = UInt("CapeImageWidth");
            var capeHeight = UInt("CapeImageHeight");
            var cape = B64("CapeData");
            Check(capeWidth, capeHeight, cape);
            w.UInt32(capeWidth).UInt32(capeHeight).ByteArray(cape)
                .ByteArray(B64("SkinGeometryData"))
                .ByteArray(B64("SkinGeometryDataEngineVersion"))
                .ByteArray(B64("SkinAnimationData"))
                .String(Str("CapeId"))
                .String(Str("SkinId")) // full id
                .Byte(Str("ArmSize") == "slim" ? (byte)0 : (byte)1)
                .Int32BE(SkinColour(Str("SkinColor")))
                .VarUInt32(0) // persona pieces
                .VarUInt32(0) // piece tint colours
                .Bool(Bool("PremiumSkin"))
                .Bool(false) // persona skin: pieces are not forwarded
                .Bool(Bool("CapeOnClassicSkin"))
                .Bool(false) // primary user
                .Bool(Bool("OverrideSkin"))
                .String(Bool("TrustedSkin") ? "true" : "false")
                .String(Str("ProfileHash"));
            return w.ToArray();
        }
        catch (Exception e) when (e is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Malformed skin data.", e);
        }
    }

    /// <summary>
    /// A skin made by the server (an NPC): an RGBA image and, optionally, a custom geometry (a .geo.json file whose
    /// identifier is <paramref name="geometryName"/>). Same layout as <see cref="Skin(JsonElement)"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The image does not match its size.</exception>
    public static byte[] CustomSkin(string skinId, int width, int height, byte[] rgba, string geometryName = "geometry.humanoid.custom", string geometryJson = "")
    {
        Check((uint)width, (uint)height, rgba);
        var patch = Encoding.UTF8.GetBytes("{\"geometry\":{\"default\":\"" + geometryName + "\"}}");
        return new BedrockWriter(rgba.Length + geometryJson.Length + 256)
            .String(skinId).String("").ByteArray(patch)
            .UInt32((uint)width).UInt32((uint)height).ByteArray(rgba)
            .VarUInt32(0) // animations
            .UInt32(0).UInt32(0).ByteArray([]) // no cape
            .ByteArray(Encoding.UTF8.GetBytes(geometryJson))
            .ByteArray(geometryJson == "" ? [] : Encoding.UTF8.GetBytes(ProtocolInfo.GameVersion)) // a custom model needs it (the client crashes without)
            .ByteArray([]) // animation data
            .String("") // cape id
            .String(skinId) // full id
            .Byte(1) // wide arms
            .Int32BE(0)
            .VarUInt32(0).VarUInt32(0) // persona pieces, tints
            .Bool(false).Bool(false).Bool(false).Bool(false) // premium, persona, cape on classic, primary user
            .Bool(true) // override the client's own geometry
            .String("true") // trusted: the client renders it without asking
            .String("")
            .ToArray();
    }

    private static void Check(uint width, uint height, byte[] image)
    {
        if ((ulong)width * height * 4 != (ulong)image.Length || image.Length > MaxImageBytes)
            throw new InvalidDataException("Skin image size does not match its dimensions.");
    }

    // "#RRGGBB" → A | R<<8 | G<<16 | B<<24, written big-endian (BEARGB in gophertunnel).
    private static int SkinColour(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var rgb))
            return 0;
        uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return unchecked((int)(0xFF | (r << 8) | (g << 16) | (b << 24)));
    }
}
