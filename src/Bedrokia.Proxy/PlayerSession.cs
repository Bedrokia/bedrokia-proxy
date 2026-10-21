// ┌───────────────────────────────────────────────────────────────────┐
// │  Bedrokia Proxy - a NetherNet proxy for Bedrokia servers           │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

using System.Buffers.Binary;
using System.Net.Sockets;
using Bedrokia.Protocol;
using Bedrokia.Protocol.Login;
using Bedrokia.Transport.NetherNet;

namespace Bedrokia.Proxy;

/// <summary>
/// One player: the NetherNet connection to the client and the one (two while switching) to a Bedrokia server.
/// The first server sees the client's own packets (resource packs included); on a switch the proxy logs in to the
/// next server itself, replays the client's Login, turns its StartGame into what a running client accepts
/// (dimension change, game mode, position) and clears what the old server had shown.
/// Thread-safe: every event takes <see cref="_sync"/>.
/// </summary>
public sealed class PlayerSession
{
    private const uint ChangeDimensionId = 61, SetDifficultyId = 60, StopSoundId = 87, SetTitleId = 88, AddPaintingId = 22,
        RemoveObjectiveId = 106, SetDisplayObjectiveId = 107, ResourcePackDataInfoId = 82, AvailableActorIdentifiersId = 119, AddActorId = 13, AddItemActorId = 15;
    private const int DimensionChangeDone = 14, CompressionThreshold = 256, MaxPacketsPerBatch = 64;
    private static readonly TimeSpan FakeDimensionWait = TimeSpan.FromMilliseconds(1500);

    private readonly ProxyServer _proxy;
    private readonly NetherNetSession _client;
    private readonly BatchCodec _codec = new(withHeader: false);
    private readonly Lock _sync = new();
    private bool _closed, _loggedIn, _packsPhase;
    private string _xuid = "";
    private BackendServer? _loginTarget;

    // What the client got as custom content (the first server's item list, its custom blocks with it): a server with
    // another list is reached by reconnecting to the proxy, the client cannot take a second list.
    private string? _clientContent;
    private byte[]? _login, _chunkRadius;
    private long _clientSelf = -1;
    private int _clientDimension;
    private Backend? _current, _pending;

    // What the client shows from the current server, to clear it on a switch (client ids).
    private readonly HashSet<long> _entities = [];
    private readonly HashSet<string> _playerList = [];
    private readonly HashSet<int> _effects = [];
    private readonly HashSet<long> _bossBars = [];
    private readonly HashSet<string> _objectives = [];

    public PlayerSession(ProxyServer proxy, NetherNetSession client)
    {
        _proxy = proxy;
        _client = client;
        client.PacketReceived += (_, batch) => OnClientBatch(batch);
    }

    public string? Name { get; private set; }

    public string? ServerName => _current?.Server.Name;

    private sealed class Backend(BackendServer server, bool first)
    {
        public BackendServer Server { get; } = server;

        /// <summary>The server of the login: it talks to the client directly (resource packs, StartGame).</summary>
        public bool First { get; } = first;

        public NetherNetSession? Session;
        public readonly BatchCodec Codec = new(withHeader: false);
        public bool NetworkReady, Initialised, Kicked, ShutDown;
        public long Self = -1;
        public StartInfo? Start;
        public byte[]? PendingStart; // a server switched to: its StartGame, kept until its item list is compared

        /// <summary>Packets for the client, held while the client changes dimension; null once switched.</summary>
        public List<byte[]>? Buffer;

        public void Send(params byte[][] packets) => Session?.Send(Codec.EncodeBatch(packets));
    }

    private sealed record StartInfo(int GameMode, float X, float Y, float Z, int Dimension, int Difficulty);

    /// <summary>Moves the player to <paramref name="server"/>, as a Transfer from its server would.</summary>
    public void SendTo(BackendServer server)
    {
        lock (_sync)
        {
            if (!_closed && _current is not null)
                RequestSwitch(server);
        }
    }

    public void Kick(string message)
    {
        lock (_sync)
        {
            if (_closed)
                return;
            if (_codec.CompressionEnabled)
                SendToClient(Packets.Disconnect(DisconnectReason.Kicked, message));
            _client.Disconnect(message);
            Close();
        }
    }

    internal void OnClientClosed(string reason)
    {
        lock (_sync)
        {
            if (Name is not null && !_closed)
                ProxyServer.Log($"{Name} left ({reason}).");
            Close();
        }
    }

    private void Close()
    {
        _closed = true;
        _current?.Session?.Disconnect("player left");
        _pending?.Session?.Disconnect("player left");
    }

    private void SendToClient(params byte[][] packets)
    {
        for (var i = 0; i < packets.Length; i += MaxPacketsPerBatch)
            _client.Send(_codec.EncodeBatch(packets[i..Math.Min(packets.Length, i + MaxPacketsPerBatch)]));
    }

    private void Message(string text) => SendToClient(Packets.Text(TextType.Raw, text));

    private static uint PacketIdOf(byte[] packet)
    {
        var reader = new BedrockReader(packet);
        return reader.VarUInt32() & 0x3FF;
    }

    // --- Client → server ---

    private void OnClientBatch(byte[] batch)
    {
        lock (_sync)
        {
            if (_closed)
                return;
            List<byte[]> packets;
            try
            {
                packets = _codec.Decode(batch);
            }
            catch (InvalidDataException e)
            {
                Kick($"Protocol error ({e.Message}).");
                return;
            }

            var forward = new List<byte[]>(packets.Count);
            var changed = false;
            var loadingScreenEnded = false;
            foreach (var packet in packets)
            {
                try
                {
                    if (!_loggedIn)
                    {
                        OnPreLoginPacket(packet);
                        changed = true;
                        if (_closed)
                            return;
                        continue;
                    }
                    if (_packsPhase)
                    {
                        OnPackPacket(packet);
                        changed = true;
                        if (_closed)
                            return;
                        continue;
                    }
                    var id = PacketIdOf(packet);
                    if (id == PacketId.RequestChunkRadius)
                        _chunkRadius = packet;
                    if (id == PacketId.ServerBoundLoadingScreen)
                    {
                        var reader = new BedrockReader(packet);
                        reader.VarUInt32();
                        loadingScreenEnded |= reader.VarInt32() == 2; // 1 = start, 2 = end
                    }
                }
                catch (InvalidDataException)
                {
                    // Malformed: the server decides what to do with it.
                }
                catch (LoginException e)
                {
                    ProxyServer.Log($"{_client.RemoteEndPoint}: login refused: {e.Message}");
                    Kick(e.Message);
                    return;
                }

                var target = _current;
                if (target?.Session is null || !target.NetworkReady || target.Buffer is not null)
                {
                    changed = true; // nothing reaches a server that is not ready, or while the client switches
                    continue;
                }
                var rewritten = EntityIds.Swap(packet, _clientSelf, target.Self);
                changed |= !ReferenceEquals(rewritten, packet);
                forward.Add(rewritten);
            }

            if (forward.Count > 0 && _current?.Session is { } session)
                session.Send(changed ? _current.Codec.EncodeBatch(forward) : batch);
            if (loadingScreenEnded && _current is { Buffer: not null } switching)
                FinishSwitch(switching);
        }
    }

    private void OnPreLoginPacket(byte[] packet)
    {
        var reader = new BedrockReader(packet);
        var id = reader.VarUInt32() & 0x3FF;
        if (id == PacketId.RequestNetworkSettings && !_codec.CompressionEnabled)
        {
            var protocol = reader.Int32BE();
            if (protocol != ProtocolInfo.Version)
            {
                SendToClient(Packets.PlayStatus(protocol < ProtocolInfo.Version ? PlayStatus.LoginFailedClient : PlayStatus.LoginFailedServer));
                _client.Disconnect("incompatible version");
                Close();
                return;
            }
            SendToClient(Packets.NetworkSettings(CompressionThreshold));
            _codec.EnableCompression(CompressionThreshold);
            return;
        }
        if (id != PacketId.Login || !_codec.CompressionEnabled)
        {
            Kick($"Unexpected packet {id} during login.");
            return;
        }

        reader.Int32BE(); // protocol, already checked
        // Xbox Live is mandatory: no cracked account, no stolen name, no ban bypass with another name.
        var keys = _proxy.AuthKeys ?? throw new LoginException("The server cannot reach Xbox Live right now. Try again in a minute.");
        var identity = LoginRequest.Verify(reader.ByteArray(), keys, DateTimeOffset.UtcNow);
        // NetherNet has no game-layer encryption: the login must match the key the WebRTC identity proved.
        if (_client.PeerPublicKey is { } peerKey
            && !peerKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(identity.ClientKey.ExportSubjectPublicKeyInfo()))
            throw new LoginException("Login does not match the connection identity.");
        if (_client.PeerPublicKey is null)
            throw new LoginException("An Xbox Live identity is required to join this server.");

        Name = identity.Name;
        _xuid = identity.Xuid;
        _login = packet;
        _loggedIn = true;
        // Back for the content of another server (see Reconnect): straight to it; otherwise the default server.
        _loginTarget = _proxy.TakeRoute(_xuid) ?? _proxy.Config.DefaultServer;
        ProxyServer.Log($"{Name} ({(identity.Authenticated ? "xuid " + identity.Xuid : "not authenticated")}) joined from {_client.RemoteEndPoint}, sent to {_loginTarget.Name}.");
        // The proxy's resource packs first (downloaded once, the same on every server), then the server.
        _packsPhase = true;
        SendToClient(Packets.PlayStatus(PlayStatus.LoginSuccess), _proxy.Packs.InfoPacket(_proxy.Config.ForceResourcePacks));
    }

    // ResourcePackClientResponse: 0 refused, 1 send packs, 2 all downloaded, 3 completed; then the chunk requests.
    private void OnPackPacket(byte[] packet)
    {
        var reader = new BedrockReader(packet);
        var id = reader.VarUInt32() & 0x3FF;
        var packs = _proxy.Packs;
        var required = _proxy.Config.ForceResourcePacks;
        if (id == ResourcePacks.ChunkRequestId)
        {
            var packId = reader.String();
            var index = BinaryPrimitives.ReadInt32LittleEndian(reader.Bytes(4));
            if (packs.ById(packId) is { } pack && ResourcePacks.ChunkPacket(pack, index) is { } chunk)
                SendToClient(chunk);
            return;
        }
        if (id != PacketId.ResourcePackClientResponse)
            return; // client cache status and the like: nothing the server needs before it starts
        var response = reader.VarUInt32();
        if (reader.Remaining > 0)
            reader.String();
        switch (response)
        {
            case 0 when required && packs.All.Count > 0:
                Kick("Ce serveur demande ses packs de ressources.");
                return;
            case 1:
            {
                var count = reader.Remaining > 0 ? reader.VarUInt32() : 0;
                var sent = 0;
                for (var i = 0; i < count; i++)
                    if (packs.ById(reader.String()) is { } pack)
                    {
                        SendToClient(ResourcePacks.DataInfoPacket(pack));
                        sent++;
                    }
                if (sent == 0)
                    SendToClient(packs.StackPacket(required));
                return;
            }
            case 3: // completed: the server now, which never sends its own packs to the client
                _packsPhase = false;
                Connect(_loginTarget ?? _proxy.Config.DefaultServer, first: true);
                return;
            default: // refused (not required) or all downloaded
                SendToClient(packs.StackPacket(required));
                return;
        }
    }

    // --- Server connections ---

    private void Connect(BackendServer server, bool first)
    {
        var backend = new Backend(server, first);
        if (first)
            _current = backend;
        else
            _pending = backend;
        _ = Task.Run(async () =>
        {
            NetherNetSession session;
            try
            {
                session = await NetherNetClient.ConnectAsync(server.Host, server.Port, CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                lock (_sync)
                    OnBackendGone(backend, e.Message);
                return;
            }
            lock (_sync)
            {
                if (_closed || (backend != _current && backend != _pending))
                {
                    session.Disconnect("player left");
                    return;
                }
                backend.Session = session;
                session.PacketReceived += (_, batch) => OnBackendBatch(backend, batch);
                session.Closed += (_, reason) =>
                {
                    lock (_sync)
                        OnBackendGone(backend, reason);
                };
                if (session.IsClosed)
                {
                    OnBackendGone(backend, "connection closed");
                    return;
                }
                backend.Send(new BedrockWriter().VarUInt32(PacketId.RequestNetworkSettings).Int32BE(ProtocolInfo.Version).ToArray());
            }
        });
    }

    // Called under _sync.
    private void OnBackendGone(Backend backend, string reason)
    {
        if (_closed)
            return;
        if (backend == _pending)
        {
            _pending = null;
            ProxyServer.Log($"{Name} could not join {backend.Server.Name}: {reason}");
            Message($"§cImpossible de rejoindre {backend.Server.Name} : {reason}");
            return;
        }
        if (backend != _current)
            return; // the server the player just left
        if (backend.Kicked)
        {
            _client.Disconnect(reason); // its Disconnect packet already reached the client
            Close();
            return;
        }
        var fallback = _proxy.Config.DefaultServer;
        if (backend.Server != fallback && backend.Self >= 0 && _clientSelf >= 0 && _pending is null)
        {
            ProxyServer.Log($"{Name} lost {backend.Server.Name} ({reason}): sent to {fallback.Name}.");
            Message(backend.ShutDown ? $"§e{backend.Server.Name} redémarre : retour sur {fallback.Name}." : $"§cConnexion à {backend.Server.Name} perdue : retour sur {fallback.Name}.");
            Connect(fallback, first: false);
            return;
        }
        Kick(backend.ShutDown ? "Le serveur redémarre." : $"Serveur {backend.Server.Name} indisponible ({reason}).");
    }

    private void OnBackendBatch(Backend backend, byte[] batch)
    {
        lock (_sync)
        {
            if (_closed || (backend != _current && backend != _pending))
                return;
            List<byte[]> packets;
            try
            {
                packets = backend.Codec.Decode(batch);
            }
            catch (InvalidDataException e)
            {
                ProxyServer.Log($"{Name}: protocol error from {backend.Server.Name}: {e.Message}");
                backend.Session?.Disconnect("protocol error");
                return;
            }

            var output = new List<byte[]>(packets.Count);
            var changed = false;
            foreach (var packet in packets)
            {
                byte[]? result;
                try
                {
                    result = FromServer(backend, packet);
                }
                catch (InvalidDataException)
                {
                    result = packet;
                }
                changed |= !ReferenceEquals(result, packet);
                if (result is not null)
                    output.Add(result);
                if (_closed || (backend != _current && backend != _pending))
                    return;
            }
            if (output.Count == 0)
                return;
            if (backend.Buffer is not null)
                backend.Buffer.AddRange(output);
            else if (backend == _current)
            {
                if (changed)
                    SendToClient([.. output]);
                else
                    _client.Send(batch);
            }
        }
    }

    // One packet from a server: what the client gets instead (the packet itself, a rewritten copy, or null).
    private byte[]? FromServer(Backend backend, byte[] packet)
    {
        var reader = new BedrockReader(packet);
        var id = reader.VarUInt32() & 0x3FF;

        if (!backend.NetworkReady)
        {
            if (id == PacketId.NetworkSettings)
            {
                backend.Codec.EnableCompression(BinaryPrimitives.ReadUInt16LittleEndian(reader.Bytes(2)));
                backend.NetworkReady = true;
                backend.Send(_login!);
            }
            else if (id == PacketId.PlayStatus)
                backend.Session?.Disconnect($"refused (status {reader.Int32BE()})");
            return null;
        }

        if (id == PacketId.Disconnect)
        {
            var reason = reader.VarInt32();
            reader.Bool();
            var message = reader.String();
            if (backend != _current || backend.Buffer is not null)
            {
                backend.Session?.Disconnect(message); // refused before the player got there
                return null;
            }
            if (reason == DisconnectReason.Shutdown && backend.Server != _proxy.Config.DefaultServer)
            {
                backend.ShutDown = true; // the fallback server takes the player when the connection closes
                return null;
            }
            backend.Kicked = true;
            return packet;
        }

        if (backend.First)
        {
            switch (id)
            {
                case PacketId.StartGame:
                {
                    var start = ReadStartGame(packet, out var runtimeId);
                    backend.Self = _clientSelf = runtimeId;
                    _clientDimension = start.Dimension;
                    break;
                }
                case PacketId.ItemRegistry:
                    _clientContent = ContentOf(packet);
                    break;
                // The client already went through the proxy's packs and login success: the server's are answered here.
                case PacketId.PlayStatus when BinaryPrimitives.ReadInt32BigEndian(reader.Bytes(4)) == PlayStatus.LoginSuccess:
                    return null;
                case PacketId.ResourcePacksInfo:
                    backend.Send(ResourcePackResponse(2));
                    return null;
                case PacketId.ResourcePackStack:
                    backend.Send(ResourcePackResponse(3));
                    return null;
                case ResourcePackDataInfoId:
                    return null;
            }
        }
        else
        {
            switch (id)
            {
                case PacketId.PlayStatus:
                {
                    var status = reader.Int32BE();
                    if (status == PlayStatus.PlayerSpawn && backend.Self >= 0 && !backend.Initialised)
                    {
                        backend.Initialised = true;
                        backend.Send(new BedrockWriter().VarUInt32(PacketId.SetLocalPlayerAsInitialised).VarUInt64((ulong)backend.Self).ToArray());
                    }
                    else if (status is not (PlayStatus.LoginSuccess or PlayStatus.PlayerSpawn))
                        backend.Session?.Disconnect($"refused (status {status})");
                    return null;
                }
                case PacketId.ResourcePacksInfo:
                    backend.Send(ResourcePackResponse(2)); // all downloaded: the client already has the packs of the first server
                    return null;
                case PacketId.ResourcePackStack:
                    backend.Send(ResourcePackResponse(3)); // completed
                    return null;
                case PacketId.StartGame:
                    if (backend == _pending)
                        backend.PendingStart = packet; // its item list comes right after: compared first
                    return null;
                case PacketId.ItemRegistry:
                    // Same items and blocks as the client has: an instant switch. Others: the client must reconnect
                    // (it takes one list per connection), straight to that server, packs already downloaded.
                    if (backend == _pending && backend.PendingStart is { } startGame)
                    {
                        if (_clientContent is null || _clientContent == ContentOf(packet) || _proxy.PublicHost is null)
                            BeginSwitch(backend, startGame);
                        else
                            Reconnect(backend);
                    }
                    return null;
                case ResourcePackDataInfoId or PacketId.NetworkSettings or PacketId.BiomeDefinitionList or AvailableActorIdentifiersId
                    or PacketId.JigsawStructureData or PacketId.VoxelShapes:
                    return null; // sent once per connection: the client got them from the first server
            }
            if (backend == _pending)
                return null;
        }

        if (id == PacketId.Transfer)
        {
            var address = reader.String();
            var port = reader.Bytes(2);
            if (_proxy.Config.FindTransferTarget(address, BinaryPrimitives.ReadUInt16LittleEndian(port)) is { } target)
            {
                RequestSwitch(target);
                return null;
            }
            return packet; // a server outside the proxy: the client really leaves
        }

        var rewritten = EntityIds.Swap(packet, backend.Self, _clientSelf);
        Track(rewritten, id);
        return rewritten;
    }

    private static string ContentOf(byte[] itemRegistry) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(itemRegistry));

    // The next server has other custom items or blocks: the client reconnects to the proxy, which sends it there.
    private void Reconnect(Backend backend)
    {
        _pending = null;
        backend.Session?.Disconnect("player reconnecting for this server's content");
        _proxy.RouteNextLogin(_xuid, backend.Server);
        ProxyServer.Log($"{Name}: {backend.Server.Name} has other items and blocks than {_current?.Server.Name}: reconnecting the client to it.");
        Message($"§7Chargement de §f{backend.Server.Name}§7...");
        SendToClient(Packets.Transfer(_proxy.PublicHost!, (ushort)_proxy.Config.Port));
    }

    private static byte[] ResourcePackResponse(byte response) =>
        new BedrockWriter().VarUInt32(PacketId.ResourcePackClientResponse).Byte(response).UInt16(0).ToArray();

    private static StartInfo ReadStartGame(byte[] packet, out long runtimeId)
    {
        var r = new BedrockReader(packet);
        r.VarUInt32();
        r.VarInt64(); // unique id
        runtimeId = (long)r.VarUInt64();
        var gameMode = r.VarInt32();
        float x = r.Float32(), y = r.Float32(), z = r.Float32();
        r.Bytes(8); // pitch, yaw
        r.Bytes(8); // seed
        r.Bytes(2); // spawn biome type
        r.String(); // user defined biome
        var dimension = r.VarInt32();
        r.VarInt32(); // generator
        r.VarInt32(); // world game mode
        r.Bool(); // hardcore
        var difficulty = r.VarInt32();
        return new StartInfo(gameMode, x, y, z, dimension, difficulty);
    }

    // --- Server switch ---

    private void RequestSwitch(BackendServer target)
    {
        if (_pending is not null || _current?.Buffer is not null)
        {
            Message("§cChangement de serveur déjà en cours.");
            return;
        }
        if (_current?.Server == target)
        {
            Message($"§cTu es déjà sur {target.Name}.");
            return;
        }
        ProxyServer.Log($"{Name}: {_current?.Server.Name} -> {target.Name}");
        Connect(target, first: false);
    }

    // The new server sent StartGame: the old one is dropped and the client is prepared for the new world.
    private void BeginSwitch(Backend backend, byte[] startGame)
    {
        var start = ReadStartGame(startGame, out var runtimeId);
        backend.Self = runtimeId;
        backend.Start = start;
        backend.Buffer = [];
        var old = _current;
        _current = backend;
        _pending = null;
        old?.Session?.Disconnect("switching server"); // the old server saves the player as on a normal quit

        backend.Send(_chunkRadius ?? new BedrockWriter().VarUInt32(PacketId.RequestChunkRadius).VarInt32(8).Bytes([8]).ToArray());

        var packets = ClearOldServer();
        if (start.Dimension != _clientDimension)
        {
            SendToClient([.. packets]);
            FinishSwitch(backend);
            return;
        }
        // Same dimension: the client would keep the old chunks. A trip through another dimension clears them.
        var fake = start.Dimension == 0 ? 1 : 0;
        packets.AddRange(DimensionChange(fake, start.X, start.Y, start.Z));
        int cx = (int)Math.Floor(start.X) >> 4, cz = (int)Math.Floor(start.Z) >> 4;
        packets.Add(Packets.NetworkChunkPublisherUpdate((int)start.X, (int)start.Y, (int)start.Z, 3 * 16));
        var emptyChunk = EmptyChunkPayload(fake);
        for (var x = -2; x <= 2; x++)
        for (var z = -2; z <= 2; z++)
            packets.Add(Packets.LevelChunk(cx + x, cz + z, 0, emptyChunk, fake));
        SendToClient([.. packets]);
        _clientDimension = fake;
        _ = Task.Delay(FakeDimensionWait).ContinueWith(_ =>
        {
            lock (_sync)
                FinishSwitch(backend);
        }, TaskScheduler.Default);
    }

    // The client is in the right dimension (or about to be): position, game mode, then what the server sent meanwhile.
    private void FinishSwitch(Backend backend)
    {
        if (_closed || backend != _current || backend.Buffer is null || backend.Start is not { } start)
            return;
        var packets = DimensionChange(start.Dimension, start.X, start.Y, start.Z);
        _clientDimension = start.Dimension;
        packets.Add(Packets.SetPlayerGameType(start.GameMode));
        packets.Add(new BedrockWriter().VarUInt32(SetDifficultyId).VarUInt32((uint)start.Difficulty).ToArray());
        packets.Add(Packets.MovePlayer(_clientSelf, start.X, start.Y, start.Z, 0, 0, 0, teleport: true, onGround: false));
        packets.AddRange(backend.Buffer);
        backend.Buffer = null;
        SendToClient([.. packets]);
        ProxyServer.Log($"{Name} is now on {backend.Server.Name}.");
    }

    private List<byte[]> DimensionChange(int dimension, float x, float y, float z) =>
    [
        new BedrockWriter().VarUInt32(ChangeDimensionId).VarInt32(dimension).Vec3(x, y, z).Bool(false).Bool(false).ToArray(),
        new BedrockWriter().VarUInt32(StopSoundId).String("").Bool(true).Bool(false).ToArray(),
        Packets.PlayStatus(PlayStatus.PlayerSpawn),
        // Since 1.19.50 the server sends the "dimension change done" acknowledgement itself.
        new BedrockWriter().VarUInt32(PacketId.PlayerAction).VarUInt64((ulong)_clientSelf).VarInt32(DimensionChangeDone)
            .BlockPos(0, 0, 0).BlockPos(0, 0, 0).VarInt32(0).ToArray(),
    ];

    // No sub-chunk: one biome per section (plains / nether wastes), no border blocks.
    private static byte[] EmptyChunkPayload(int dimension)
    {
        var w = new BedrockWriter().Byte(1).VarInt32(dimension == 1 ? 8 : 1);
        for (var s = 1; s < (dimension == 1 ? 8 : 24); s++)
            w.Byte(0xFF); // same biomes as the section below
        return w.Byte(0).ToArray();
    }

    // Removes the entities, player list, effects, boss bars, scoreboards and title of the old server.
    private List<byte[]> ClearOldServer()
    {
        var packets = new List<byte[]>();
        foreach (var entity in _entities)
            if (entity != _clientSelf)
                packets.Add(Packets.RemoveActor(entity));
        if (_playerList.Count > 0)
        {
            var w = new BedrockWriter().VarUInt32(PacketId.PlayerList).VarUInt32((uint)_playerList.Count);
            foreach (var uuid in _playerList)
                w.VarUInt32(0).Byte(1).Bytes(Convert.FromBase64String(uuid)); // remove
            packets.Add(w.ToArray());
        }
        foreach (var effect in _effects)
            packets.Add(Packets.MobEffect((ulong)_clientSelf, MobEffectAction.Remove, effect, 0, false, 0));
        foreach (var boss in _bossBars)
            packets.Add(new BedrockWriter().VarUInt32(PacketId.BossEvent).VarInt64(boss).VarUInt32(2).ToArray()); // hide
        foreach (var objective in _objectives)
            packets.Add(new BedrockWriter().VarUInt32(RemoveObjectiveId).String(objective).ToArray());
        packets.Add(new BedrockWriter().VarUInt32(SetTitleId).VarInt32(0).String("").VarInt32(0).VarInt32(0).VarInt32(0)
            .String("").String("").String("").ToArray()); // clear
        _entities.Clear();
        _playerList.Clear();
        _effects.Clear();
        _bossBars.Clear();
        _objectives.Clear();
        return packets;
    }

    // Remembers what a packet for the client shows (ids already in client form).
    private void Track(byte[] packet, uint id)
    {
        var r = new BedrockReader(packet);
        r.VarUInt32();
        switch (id)
        {
            case AddActorId or AddItemActorId or AddPaintingId:
                _entities.Add(r.VarInt64());
                break;
            case PacketId.AddPlayer:
                r.Bytes(16);
                r.String();
                _entities.Add((long)r.VarUInt64()); // Bedrokia: unique id = runtime id
                break;
            case PacketId.RemoveActor:
                _entities.Remove(r.VarInt64());
                break;
            case PacketId.PlayerList:
            {
                var count = r.VarUInt32();
                for (var i = 0; i < count; i++)
                {
                    var add = r.VarUInt32() == 1;
                    r.Byte();
                    var uuid = Convert.ToBase64String(r.Bytes(16));
                    if (!add)
                    {
                        _playerList.Remove(uuid);
                        continue;
                    }
                    _playerList.Add(uuid);
                    if (i + 1 < count)
                        SkipPlayerListEntry(ref r);
                }
                break;
            }
            case PacketId.MobEffect:
                if ((long)r.VarUInt64() == _clientSelf)
                {
                    var action = r.Byte();
                    var effect = r.VarInt32();
                    if (action == (byte)MobEffectAction.Remove)
                        _effects.Remove(effect);
                    else
                        _effects.Add(effect);
                }
                break;
            case PacketId.BossEvent:
            {
                var boss = r.VarInt64();
                var type = r.VarUInt32();
                if (type == 0)
                    _bossBars.Add(boss);
                else if (type == 2)
                    _bossBars.Remove(boss);
                break;
            }
            case SetDisplayObjectiveId:
                r.String();
                _objectives.Add(r.String());
                break;
            case RemoveObjectiveId:
                _objectives.Remove(r.String());
                break;
            case ChangeDimensionId:
                _clientDimension = r.VarInt32();
                break;
        }
    }

    // Rest of a PlayerList "add" entry after its UUID, as Bedrokia writes it (PlayerPackets.PlayerListAdd and Skin).
    private static void SkipPlayerListEntry(ref BedrockReader r)
    {
        r.VarInt64();
        r.String();
        r.String();
        r.String();
        r.Bytes(4); // build platform
        r.String(); // skin id
        r.String(); // play fab id
        r.ByteArray(); // resource patch
        r.Bytes(8);
        r.ByteArray(); // skin image
        var animations = r.VarUInt32();
        for (var i = 0; i < animations; i++)
        {
            r.Bytes(8);
            r.ByteArray();
            r.VarUInt32();
            r.Bytes(4);
            r.VarUInt32();
        }
        r.Bytes(8);
        r.ByteArray(); // cape image
        r.ByteArray(); // geometry
        r.ByteArray(); // geometry engine version
        r.ByteArray(); // animation data
        r.String(); // cape id
        r.String(); // full id
        r.Byte(); // arm size
        r.Bytes(4); // colour
        if (r.VarUInt32() != 0 || r.VarUInt32() != 0)
            throw new InvalidDataException("Persona pieces are not tracked."); // Bedrokia never sends them
        r.Bytes(5); // premium, persona, cape on classic, primary user, override
        r.String(); // trusted
        r.String(); // profile hash
        r.Bytes(3); // teacher, host, sub-client
        r.Bytes(4); // player colour
    }
}
