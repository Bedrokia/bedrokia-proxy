using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Bedrokia.Protocol;

namespace Bedrokia.Proxy;

/// <summary>A resource pack the proxy sends to players: its manifest identity and the zip bytes.</summary>
public sealed record ResourcePack(Guid Uuid, string Version, string Name, byte[] Data)
{
    public byte[] Sha256 { get; } = SHA256.HashData(Data);

    /// <summary>"uuid_version", how the client names a pack in its download requests.</summary>
    public string Id => $"{Uuid}_{Version}";
}

/// <summary>
/// The packs of the proxy (resource_packs/, as WaterdogPE): players download them once, when they join the proxy, and
/// they are used on every server behind it (the servers' own packs are not sent again). Same code as Bedrokia's
/// resource_packs/: .mcpack or .zip files or pack folders, chunks of 128 KiB (gophertunnel layouts, MIT).
/// </summary>
public sealed class ResourcePacks
{
    public const uint DataInfoId = 82, ChunkDataId = 83, ChunkRequestId = 84;
    private const int ChunkSize = 128 * 1024;

    public IReadOnlyList<ResourcePack> All { get; }

    private ResourcePacks(List<ResourcePack> packs) => All = packs;

    public static ResourcePacks Load(string directory)
    {
        var packs = new List<ResourcePack>();
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return new ResourcePacks(packs);
        }
        var sources = Directory.GetFiles(directory, "*.mcpack").Concat(Directory.GetFiles(directory, "*.zip"))
            .Concat(Directory.GetDirectories(directory)).Order();
        foreach (var source in sources)
        {
            try
            {
                var data = Directory.Exists(source) ? ZipFolder(source) : File.ReadAllBytes(source);
                if (Read(data, Path.GetFileName(source)) is not { } pack)
                {
                    ProxyServer.Log($"Resource pack {Path.GetFileName(source)}: no manifest.json with a \"resources\" module, skipped.");
                    continue;
                }
                if (packs.Any(p => p.Uuid == pack.Uuid))
                {
                    ProxyServer.Log($"Resource pack {Path.GetFileName(source)}: same UUID as another pack, skipped.");
                    continue;
                }
                packs.Add(pack);
                ProxyServer.Log($"Resource pack '{pack.Name}' {pack.Version} loaded ({pack.Data.Length / 1024} KiB).");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                ProxyServer.Log($"Resource pack {Path.GetFileName(source)} could not be read: {e.Message}");
            }
        }
        return new ResourcePacks(packs);
    }

    public ResourcePack? ById(string id) => All.FirstOrDefault(p => p.Id == id || p.Uuid.ToString() == id.Split('_')[0]);

    private static byte[] ZipFolder(string folder)
    {
        using var buffer = new MemoryStream();
        ZipFile.CreateFromDirectory(folder, buffer, CompressionLevel.Optimal, includeBaseDirectory: false);
        return buffer.ToArray();
    }

    // manifest.json at the root of the zip or in its single top folder.
    private static ResourcePack? Read(byte[] data, string fallbackName)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entry = zip.Entries.Where(e => e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault();
        if (entry is null)
            return null;
        using var stream = entry.Open();
        using var manifest = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = manifest.RootElement;
        if (!root.TryGetProperty("modules", out var modules) || !modules.EnumerateArray()
                .Any(m => m.TryGetProperty("type", out var type) && type.GetString() == "resources"))
            return null;
        var header = root.GetProperty("header");
        var uuid = Guid.Parse(header.GetProperty("uuid").GetString()!);
        var version = header.GetProperty("version") is { ValueKind: JsonValueKind.Array } v
            ? string.Join('.', v.EnumerateArray().Select(n => n.GetInt32()))
            : header.GetProperty("version").GetString() ?? "1.0.0";
        var name = header.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s && !s.StartsWith("pack.") ? s : fallbackName;
        return new ResourcePack(uuid, version, name, data);
    }

    // --- Packets (gophertunnel layouts at protocol 2193, as Bedrokia sends them) ---

    public byte[] InfoPacket(bool required)
    {
        var w = new BedrockWriter().VarUInt32(PacketId.ResourcePacksInfo)
            .Bool(required).Bool(false).Bool(false).Bool(false) // required, addons, scripts, force-disable vibrant visuals
            .EmptyUuid().String("") // world template
            .VarUInt32((uint)All.Count);
        foreach (var pack in All)
            w.Uuid(pack.Uuid).String(pack.Version).UInt64((ulong)pack.Data.Length)
                .String("").String("").String("") // content key, sub-pack, content identity
                .Bool(false).Bool(false).Bool(false) // scripts, addon, RTX
                .String(""); // download URL
        return w.ToArray();
    }

    public byte[] StackPacket(bool required)
    {
        var w = new BedrockWriter().VarUInt32(PacketId.ResourcePackStack).Bool(required).VarUInt32((uint)All.Count);
        foreach (var pack in All)
            w.String(pack.Uuid.ToString()).String(pack.Version).String("");
        return w.String(ProtocolInfo.GameVersion).UInt32(0).Bool(false).Bool(false).ToArray();
    }

    public static byte[] DataInfoPacket(ResourcePack pack) =>
        new BedrockWriter().VarUInt32(DataInfoId).String(pack.Id).UInt32(ChunkSize)
            .UInt32((uint)((pack.Data.Length + ChunkSize - 1) / ChunkSize)).UInt64((ulong)pack.Data.Length)
            .ByteArray(pack.Sha256).Bool(false).Byte(6) // not premium, type: resources
            .ToArray();

    public static byte[]? ChunkPacket(ResourcePack pack, int index)
    {
        var offset = (long)index * ChunkSize;
        if (index < 0 || offset >= pack.Data.Length)
            return null;
        var length = (int)Math.Min(ChunkSize, pack.Data.Length - offset);
        return new BedrockWriter().VarUInt32(ChunkDataId).String(pack.Id).UInt32((uint)index).UInt64((ulong)offset)
            .ByteArray(pack.Data.AsSpan((int)offset, length).ToArray()).ToArray();
    }
}
