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

namespace Bedrokia.Protocol;

/// <summary>
/// Bedrock packet ids for the protocol version in <see cref="ProtocolInfo"/>.
/// Source: gophertunnel (MIT) minecraft/protocol/packet/id.go at protocol 2193.
/// </summary>
public static class PacketId
{
    public const uint Login = 1;
    public const uint PlayStatus = 2;
    public const uint ServerToClientHandshake = 3;
    public const uint ClientToServerHandshake = 4;
    public const uint Disconnect = 5;
    public const uint ResourcePacksInfo = 6;
    public const uint ResourcePackStack = 7;
    public const uint ResourcePackClientResponse = 8;
    public const uint Text = 9;
    public const uint SetTime = 10;
    public const uint StartGame = 11;
    public const uint AddPlayer = 12;
    public const uint RemoveActor = 14;
    public const uint MovePlayer = 19;
    public const uint UpdateBlock = 21;
    public const uint LevelEvent = 25;
    public const uint ActorEvent = 27;
    public const uint MobEffect = 28;
    public const uint UpdateAttributes = 29;
    public const uint InventoryTransaction = 30;
    public const uint MobEquipment = 31;
    public const uint Interact = 33;
    public const uint BlockPickRequest = 34;
    public const uint PlayerAction = 36;
    public const uint SetActorData = 39;
    public const uint Animate = 44;
    public const uint Respawn = 45;
    public const uint ContainerClose = 47;
    public const uint Transfer = 85;
    public const uint AdventureSettings = 55;
    public const uint BlockActorData = 56;
    public const uint LevelChunk = 58;
    public const uint SetPlayerGameType = 62;
    public const uint PlayerList = 63;
    public const uint RequestChunkRadius = 69;
    public const uint ChunkRadiusUpdated = 70;
    public const uint BossEvent = 74;
    public const uint CommandRequest = 77;
    public const uint PlayerSkin = 93;
    public const uint BookEdit = 97;
    public const uint NPCRequest = 98;
    public const uint ModalFormRequest = 100;
    public const uint ModalFormResponse = 101;
    public const uint ServerSettingsRequest = 102;
    public const uint ServerSettingsResponse = 103;
    public const uint SetLocalPlayerAsInitialised = 113;
    public const uint NetworkChunkPublisherUpdate = 121;
    public const uint BiomeDefinitionList = 122;
    public const uint LecternUpdate = 125;
    public const uint ClientCacheStatus = 129;
    public const uint ClientCacheBlobStatus = 135;
    public const uint Emote = 138;
    public const uint AnvilDamage = 141;
    public const uint NetworkSettings = 143;
    public const uint PlayerAuthInput = 144;
    public const uint CreativeContent = 145;
    public const uint ItemStackRequest = 147;
    public const uint EmoteList = 152;
    public const uint ItemRegistry = 162;
    public const uint FilterText = 163;
    public const uint SubChunkRequest = 175;
    public const uint RequestAbility = 184;
    public const uint RequestNetworkSettings = 193;
    public const uint SetPlayerInventoryOptions = 307;
    public const uint ServerBoundLoadingScreen = 312;
    public const uint JigsawStructureData = 313;
    public const uint ServerBoundDiagnostics = 315;
    // After 300 the ids are iota + 101 in gophertunnel id.go (IDCameraInstruction = 300): line number + 97.
    public const uint ClientboundDataStore = 330;
    public const uint ServerboundDataStore = 332;
    public const uint ClientboundDataDrivenUIShowScreen = 333;
    public const uint ClientboundDataDrivenUICloseScreen = 334;
    public const uint ClientboundDataDrivenUIReload = 335;
    public const uint ServerboundDataDrivenScreenClosed = 343;
    public const uint VoxelShapes = 337;
    public const uint SetPlayerFurnaceOptions = 351;
}

public static class ProtocolInfo
{
    /// <summary>Not validated against a real client yet (see docs/ROADMAP.md).</summary>
    public const int Version = 2193;
    public const string GameVersion = "1.26.50";
}

public static class PlayStatus
{
    public const int LoginSuccess = 0;
    public const int LoginFailedClient = 1;
    public const int LoginFailedServer = 2;
    public const int PlayerSpawn = 3;
    public const int LoginFailedServerFull = 7;
}

public static class DisconnectReason
{
    public const int Unknown = 0;
    public const int ServerFull = 25;
    public const int NotAuthenticated = 46;
    public const int Kicked = 55;
    public const int Shutdown = 68;
}

public enum TextType : byte
{
    Raw = 0,
    Chat = 1,
    Popup = 3,
    Tip = 5,
    System = 6,
}

public enum MobEffectAction : byte
{
    Add = 1,
    Modify = 2,
    Remove = 3,
}

/// <summary>Encoders for the server-to-client packets Bedrokia sends. Each result starts with the packet header.</summary>
public static class Packets
{
    private static BedrockWriter Begin(uint id) => new BedrockWriter().VarUInt32(id);

    public static byte[] MobEffect(ulong runtimeEntityId, MobEffectAction action, int effectId, int amplifier, bool showParticles, int durationTicks, bool ambient = false) =>
        Begin(PacketId.MobEffect)
            .VarUInt64(runtimeEntityId)
            .Byte((byte)action)
            .VarInt32(effectId)
            .VarInt32(amplifier)
            .Bool(showParticles)
            .VarInt32(durationTicks)
            .VarUInt64(0)
            .Bool(ambient)
            .ToArray();

    public static byte[] NetworkSettings(ushort compressionThreshold) => Begin(PacketId.NetworkSettings)
        .UInt16(compressionThreshold)
        .UInt16(0) // flate
        .Bool(false).Byte(0).Float32(0) // client throttling off
        .ToArray();

    public static byte[] PlayStatus(int status) => Begin(PacketId.PlayStatus).Int32BE(status).ToArray();

    public static byte[] ServerToClientHandshake(string jwt) =>
        Begin(PacketId.ServerToClientHandshake).ByteArray(System.Text.Encoding.UTF8.GetBytes(jwt)).ToArray();

    /// <summary>Sends the client to another server (address and port), like typing it in the server list.</summary>
    public static byte[] Transfer(string address, ushort port) => Begin(PacketId.Transfer)
        .String(address)
        .UInt16(port)
        .Bool(false) // reload the world: false keeps the loading screen short
        .Bool(false) // no gathering join info (optional)
        .ToArray();

    public static byte[] Disconnect(int reason, string message) => Begin(PacketId.Disconnect)
        .VarInt32(reason)
        .Bool(false) // show the disconnection screen
        .String(message)
        .String("") // filtered message
        .ToArray();

    public static byte[] ResourcePacksInfo() => Begin(PacketId.ResourcePacksInfo)
        .Bool(false).Bool(false).Bool(false).Bool(false) // pack required, addons, scripts, force-disable vibrant visuals
        .EmptyUuid().String("") // world template
        .VarUInt32(0) // packs
        .ToArray();

    public static byte[] ResourcePackStack() => Begin(PacketId.ResourcePackStack)
        .Bool(false)
        .VarUInt32(0) // packs
        .String(ProtocolInfo.GameVersion)
        .UInt32(0) // experiments
        .Bool(false).Bool(false)
        .ToArray();

    public static byte[] JigsawStructureData() => Begin(PacketId.JigsawStructureData)
        .Bytes(new NbtWriter(network: true).BeginCompound()
            .EmptyList("processors", NbtWriter.TagCompound)
            .EmptyList("template_pools", NbtWriter.TagCompound)
            .EmptyList("jigsaws", NbtWriter.TagCompound)
            .EmptyList("structure_sets", NbtWriter.TagCompound)
            .EndCompound().ToArray())
        .ToArray();

    public static byte[] VoxelShapes() => Begin(PacketId.VoxelShapes).VarUInt32(0).VarUInt32(0).UInt16(0).ToArray();

    public static byte[] ChunkRadiusUpdated(int radius) => Begin(PacketId.ChunkRadiusUpdated).VarInt32(radius).ToArray();

    public static byte[] NetworkChunkPublisherUpdate(int x, int y, int z, uint radiusBlocks) => Begin(PacketId.NetworkChunkPublisherUpdate)
        .BlockPos(x, y, z).VarUInt32(radiusBlocks).UInt32(0)
        .ToArray();

    public static byte[] LevelChunk(int chunkX, int chunkZ, uint subChunkCount, byte[] payload, int dimension = 0) => Begin(PacketId.LevelChunk)
        .VarInt32(chunkX).VarInt32(chunkZ)
        .VarInt32(dimension) // 0 overworld, 1 nether
        .VarUInt32(subChunkCount)
        .Bool(false) // no sub-chunk limit
        .Bool(false) // blob cache disabled
        .VarUInt32(0) // blob hashes
        .ByteArray(payload)
        .ToArray();

    public static byte[] SetPlayerGameType(int gameMode) => Begin(PacketId.SetPlayerGameType).VarInt32(gameMode).ToArray();

    public static byte[] SetTime(int time) => Begin(PacketId.SetTime).VarInt32(time).ToArray();

    /// <summary>LevelEvent (id 25): event type, position (block centre for block events), data.</summary>
    public static byte[] LevelEvent(int eventType, int x, int y, int z, int data) =>
        Begin(PacketId.LevelEvent).VarInt32(eventType).Vec3(x + 0.5f, y + 0.5f, z + 0.5f).VarInt32(data).ToArray();

    public sealed record BiomeDefinition(string Name, float Temperature, float Downfall, float Depth, float Scale, int WaterColour, string[] Tags);

    /// <summary>
    /// BiomeDefinitionList with every vanilla biome, sorted by id, BiomeID -1 (vanilla), as Dragonfly (MIT) sends it.
    /// The client must receive the complete list: a partial one makes it fail to join.
    /// Layout from gophertunnel (MIT) packet/biome_definition_list.go.
    /// </summary>
    public static byte[] BiomeDefinitionList(IReadOnlyList<BiomeDefinition> biomes)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, short>();
        short Intern(string s)
        {
            if (!index.TryGetValue(s, out var i))
            {
                i = (short)strings.Count;
                index[s] = i;
                strings.Add(s);
            }
            return i;
        }
        foreach (var b in biomes)
            Intern(b.Name); // names first, as Dragonfly does

        var w = Begin(PacketId.BiomeDefinitionList).VarUInt32((uint)biomes.Count);
        foreach (var b in biomes)
        {
            w.Int16(Intern(b.Name)).Int16(-1)
                .Float32(b.Temperature).Float32(b.Downfall).Float32(0) // foliage snow
                .Float32(b.Depth).Float32(b.Scale)
                .Int32(b.WaterColour)
                .Bool(b.Downfall > 0)
                .Bool(true).VarUInt32((uint)b.Tags.Length);
            foreach (var tag in b.Tags)
                w.UInt16((ushort)Intern(tag));
            w.Bool(false); // no client-side chunk generation data
        }
        w.VarUInt32((uint)strings.Count);
        foreach (var s in strings)
            w.String(s);
        return w.ToArray();
    }

    /// <summary>Flags: 1 = update neighbours, 2 = network.</summary>
    public static byte[] UpdateBlock(int x, int y, int z, uint networkBlockId) => Begin(PacketId.UpdateBlock)
        .BlockPos(x, y, z).VarUInt32(networkBlockId).VarUInt32(3).VarUInt32(0)
        .ToArray();

    public static byte[] RemoveActor(long entityId) => Begin(PacketId.RemoveActor).VarInt64(entityId).ToArray();

    /// <summary>Mode 0 = normal (smooth), 1 = reset (teleport).</summary>
    public static byte[] MovePlayer(long entityId, float x, float y, float z, float pitch, float yaw, float headYaw, bool teleport, bool onGround) =>
        Begin(PacketId.MovePlayer)
            .VarUInt64((ulong)entityId)
            .Vec3(x, y, z)
            .Float32(pitch).Float32(yaw).Float32(headYaw)
            .Byte(teleport ? (byte)1 : (byte)0)
            .Bool(onGround)
            .VarUInt64(0) // ridden entity
            .Bool(false) // no teleport data
            .VarUInt64(0) // tick
            .ToArray();

    public static byte[] Text(TextType type, string message, string source = "")
    {
        var writer = Begin(PacketId.Text)
            .Bool(false) // needs translation
            .Byte(type == TextType.Chat ? (byte)1 : (byte)0) // authored message / message only
            .Byte((byte)type);
        if (type == TextType.Chat)
            writer.String(source);
        // An empty text is a malformed packet for the client, which disconnects (an empty line in a plugin's help).
        return writer.String(message.Length == 0 ? "§r" : message)
            .String("") // xuid
            .String("") // platform chat id
            .Bool(false) // no filtered message
            .ToArray();
    }

    /// <summary>Values that change per player or world; everything else uses fixed defaults.</summary>
    public sealed record CustomBlockDefinition(string Name, OrderedDictionary<string, object> Nbt);

    /// <summary>Values that change per player or world; everything else uses fixed defaults.</summary>
    public sealed record StartGameData(
        long EntityId,
        int GameMode,
        float X, float Y, float Z,
        int SpawnX, int SpawnY, int SpawnZ,
        string WorldName,
        long Time,
        int Difficulty = 2,
        bool DaylightCycle = true,
        int Dimension = 0,
        IReadOnlyList<CustomBlockDefinition>? CustomBlocks = null);

    /// <summary>Field order from gophertunnel (MIT) packet/start_game.go at protocol 2193.</summary>
    public static byte[] StartGame(StartGameData d)
    {
        var w = Begin(PacketId.StartGame)
            .VarInt64(d.EntityId) // unique id
            .VarUInt64((ulong)d.EntityId) // runtime id
            .VarInt32(d.GameMode)
            .Vec3(d.X, d.Y, d.Z)
            .Float32(0).Float32(0) // pitch, yaw
            .Int64(0) // seed
            .Int16(0).String("") // spawn biome type, user defined biome
            .VarInt32(d.Dimension) // 0 overworld, 1 nether
            .VarInt32(1) // generator: infinite
            .VarInt32(d.GameMode) // world game mode
            .Bool(false) // hardcore
            .VarInt32(d.Difficulty) // 0 peaceful .. 3 hard
            .BlockPos(d.SpawnX, d.SpawnY, d.SpawnZ)
            .Bool(true) // achievements disabled
            .VarInt32(0).Bool(false).Bool(false) // editor world type, created in / exported from editor
            .VarInt32(0) // day cycle lock time
            .VarUInt32(0).Bool(false).String("") // education offer, features, product id
            .Float32(0).Float32(0) // rain, lightning
            .Bool(false) // confirmed platform locked content
            .Bool(true).Bool(true) // multiplayer, LAN broadcast
            .VarInt32(0).VarInt32(0) // XBL / platform broadcast mode
            .Bool(true) // commands enabled
            .Bool(false) // texture pack required
            // Game rules (name, editable, type 1 = bool, value). Without recipesunlock=false the client treats every
            // recipe as locked and refuses to craft.
            .VarUInt32(4)
            .String("dodaylightcycle").Bool(false).VarUInt32(1).Bool(d.DaylightCycle)
            .String("showcoordinates").Bool(false).VarUInt32(1).Bool(true)
            .String("recipesunlock").Bool(false).VarUInt32(1).Bool(false)
            .String("dolimitedcrafting").Bool(false).VarUInt32(1).Bool(false)
            .UInt32(0) // experiments
            .Bool(false).Bool(false).Bool(false) // experiments toggled, bonus chest, start with map
            .Byte(1) // player permissions: member
            .Int32(4) // server chunk tick radius
            .Bool(false).Bool(false).Bool(false) // locked behaviour pack, locked texture pack, from locked template
            .Bool(false).Bool(false).Bool(false) // MSA gamertags only, from world template, template settings locked
            .Bool(false).Bool(false).Bool(false).Bool(false) // only v1 villagers, persona, custom skins disabled, emote chat muted
            .String("*") // base game version
            .Int32(0).Int32(0) // limited world width, depth
            .Bool(false) // new nether
            .String("").String("") // education shared resource URI
            .Bool(false) // force experimental gameplay: absent
            .Byte(0) // chat restriction level
            .Bool(false) // disable player interactions
            .VarInt32(0).Bool(false) // editor connection policy, anonymous block drops
            .String("").String(d.WorldName).String("") // level id, world name, template content identity
            .Bool(false) // trial
            .VarInt32(0).Bool(false) // movement: rewind history size, server authoritative block breaking
            .Int64(d.Time)
            .VarInt32(0); // enchantment seed

        if (d.CustomBlocks is { Count: > 0 } customBlocks)
        {
            w.VarUInt32((uint)customBlocks.Count);
            foreach (var b in customBlocks)
                w.String(b.Name).Bytes(Nbt.Write(b.Nbt, network: true));
        }
        else
        {
            w.VarUInt32(0);
        }

        return w.String(Guid.NewGuid().ToString()) // multiplayer correlation id
        .Bool(true) // server authoritative inventory (ItemStackRequest)
        .String(ProtocolInfo.GameVersion)
        .Bytes(NbtWriter.EmptyNetworkCompound()) // property data
        .UInt64(0) // server block state checksum
        .EmptyUuid() // world template id
        .Bool(false) // client side generation
        .Bool(true) // block network ids are hashes
        .Bool(false) // server authoritative sound
        .Bool(false) // server join information: absent
        .String("").String("").String("").String("") // server, scenario, world, owner ids
        .ToArray();
    }

    public static byte[] Emote(ulong runtimeId, string emoteId, byte flags) => Begin(PacketId.Emote)
        .VarUInt64(runtimeId)
        .String(emoteId)
        .Byte(flags)
        .ToArray();
}
