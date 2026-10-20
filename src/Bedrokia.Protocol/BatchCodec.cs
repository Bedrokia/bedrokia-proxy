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

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Bedrokia.Protocol;

/// <summary>
/// Encodes and decodes Bedrock packet batches: 0xFE, [encrypted]( [compression id] compressed( (varuint length, packet)* ) [checksum] ).
/// One instance per connection; not thread-safe (callers serialize access).
/// </summary>
/// <param name="withHeader">False for NetherNet, which carries batches without the 0xFE header.</param>
public sealed class BatchCodec(bool withHeader = true)
{
    public const byte Header = 0xFE;
    public const byte CompressionFlate = 0x00;
    public const byte CompressionNone = 0xFF;
    public const int MaxDecompressedSize = 16 * 1024 * 1024;
    public const int MaxPacketsPerBatch = 812;

    private int _compressionThreshold = -1;
    private Cipher? _send, _receive;

    public bool CompressionEnabled => _compressionThreshold >= 0;
    public bool EncryptionEnabled => _send is not null;

    /// <summary>Batches from now on carry a compression id; those of <paramref name="threshold"/> bytes or more are deflated.</summary>
    public void EnableCompression(int threshold) => _compressionThreshold = threshold;

    /// <summary>Enables AES-256-CTR with checksums, both directions, from the next batch on.</summary>
    public void EnableEncryption(byte[] key)
    {
        if (!withHeader)
            throw new InvalidOperationException("NetherNet connections are encrypted by DTLS, not by the game layer.");
        _send = new Cipher(key);
        _receive = new Cipher(key);
    }

    public byte[] Encode(byte[] packet)
    {
        var packetLen = BedrockWriter.VarUInt32Length((uint)packet.Length) + packet.Length;
        if (CompressionEnabled && packetLen < _compressionThreshold)
        {
            // Below compression threshold: 0xFF (CompressionNone) with zero extra intermediate buffer allocations
            var output = new BedrockWriter(packetLen + (withHeader ? 2 : 1));
            if (withHeader)
                output.Byte(Header);
            output.Byte(CompressionNone);
            output.ByteArray(packet);
            var data = output.ToArray();
            return _send is null ? data : _send.Encrypt(data);
        }

        var batch = new BedrockWriter(packetLen);
        batch.ByteArray(packet);
        return EncodePayload(batch.ToArray());
    }

    public byte[] Encode(IEnumerable<byte[]> packets)
    {
        if (packets is IReadOnlyList<byte[]> list)
            return EncodeBatch(list);

        var batch = new BedrockWriter();
        foreach (var packet in packets)
            batch.ByteArray(packet);
        return EncodePayload(batch.WrittenSpan);
    }

    public byte[] EncodeBatch(IReadOnlyList<byte[]> packets)
    {
        if (packets.Count == 0)
            return [];
        if (packets.Count == 1)
            return Encode(packets[0]);

        var totalLen = 0;
        for (var i = 0; i < packets.Count; i++)
            totalLen += BedrockWriter.VarUInt32Length((uint)packets[i].Length) + packets[i].Length;

        using var batch = new PooledBedrockWriter(totalLen);
        for (var i = 0; i < packets.Count; i++)
            batch.ByteArray(packets[i]);

        return EncodePayload(batch.WrittenSpan);
    }

    private byte[] EncodePayload(ReadOnlySpan<byte> payload)
    {
        using var output = new PooledBedrockWriter(payload.Length + 16);
        if (withHeader)
            output.Byte(Header);
        if (CompressionEnabled)
        {
            if (payload.Length >= _compressionThreshold)
            {
                output.Byte(CompressionFlate);
                var compressed = Deflate(payload);
                output.Bytes(compressed);
                var data = output.ToArray();
                return _send is null ? data : _send.Encrypt(data);
            }
            else
            {
                output.Byte(CompressionNone);
            }
        }
        output.Bytes(payload);
        var res = output.ToArray();
        return _send is null ? res : _send.Encrypt(res);
    }

    /// <summary>Splits one batch into packets (each starting with its varuint header).</summary>
    /// <exception cref="InvalidDataException">Bad header, checksum, compression or framing.</exception>
    public List<byte[]> Decode(ReadOnlySpan<byte> data)
    {
        if (withHeader && (data.IsEmpty || data[0] != Header))
            throw new InvalidDataException("Not a game packet batch.");
        var body = (withHeader ? data[1..] : data).ToArray();
        if (_receive is not null)
            body = _receive.Decrypt(body);

        if (CompressionEnabled)
        {
            if (body.Length == 0)
                throw new InvalidDataException("Missing compression id.");
            body = body[0] switch
            {
                CompressionNone => body[1..],
                CompressionFlate => Inflate(body.AsSpan(1)),
                _ => throw new InvalidDataException($"Unsupported compression {body[0]}."),
            };
        }

        var packets = new List<byte[]>();
        var reader = new BedrockReader(body);
        while (reader.Remaining > 0)
        {
            if (packets.Count >= MaxPacketsPerBatch)
                throw new InvalidDataException("Too many packets in one batch.");
            packets.Add(reader.ByteArray().ToArray());
        }
        return packets;
    }

    /// <summary>Decodes packets into pooled buffers, calling <paramref name="onPooledPacket"/> with rented buffer and length.</summary>
    public void DecodePackets(ReadOnlySpan<byte> data, Action<byte[], int> onPooledPacket)
    {
        if (withHeader && (data.IsEmpty || data[0] != Header))
            throw new InvalidDataException("Not a game packet batch.");
        var body = (withHeader ? data[1..] : data).ToArray();
        if (_receive is not null)
            body = _receive.Decrypt(body);

        if (CompressionEnabled)
        {
            if (body.Length == 0)
                throw new InvalidDataException("Missing compression id.");
            body = body[0] switch
            {
                CompressionNone => body[1..],
                CompressionFlate => Inflate(body.AsSpan(1)),
                _ => throw new InvalidDataException($"Unsupported compression {body[0]}."),
            };
        }

        var reader = new BedrockReader(body);
        var packetCount = 0;
        while (reader.Remaining > 0)
        {
            if (++packetCount > MaxPacketsPerBatch)
                throw new InvalidDataException("Too many packets in one batch.");
            var len = (int)reader.VarUInt32();
            var span = reader.Bytes(len);
            var rented = ArrayPool<byte>.Shared.Rent(len);
            span.CopyTo(rented);
            onPooledPacket(rented, len);
        }
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(Math.Max(512, data.Length));
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data);
        return ms.ToArray();
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        var inputRented = ArrayPool<byte>.Shared.Rent(data.Length);
        var readBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var outputRented = ArrayPool<byte>.Shared.Rent(Math.Max(64 * 1024, data.Length * 4));
        var totalRead = 0;
        try
        {
            data.CopyTo(inputRented);
            using var input = new MemoryStream(inputRented, 0, data.Length, writable: false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            int read;
            while ((read = inflate.Read(readBuffer, 0, readBuffer.Length)) > 0)
            {
                if (totalRead + read > MaxDecompressedSize)
                    throw new InvalidDataException("Decompressed batch is too large.");

                if (totalRead + read > outputRented.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(Math.Max(outputRented.Length * 2, totalRead + read));
                    outputRented.AsSpan(0, totalRead).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(outputRented);
                    outputRented = larger;
                }

                readBuffer.AsSpan(0, read).CopyTo(outputRented.AsSpan(totalRead));
                totalRead += read;
            }
            return outputRented.AsSpan(0, totalRead).ToArray();
        }
        catch (IOException e)
        {
            throw new InvalidDataException("Corrupt compressed batch.", e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputRented);
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(outputRented);
        }
    }

    /// <summary>
    /// AES-256-CTR keystream (IV = key[0..12] ‖ 00000002, 128-bit big-endian counter) plus the Bedrock checksum:
    /// SHA-256(counter LE64 ‖ plaintext ‖ key)[0..8], appended before encryption.
    /// </summary>
    private sealed class Cipher
    {
        private readonly byte[] _key;
        private readonly Aes _aes = Aes.Create();
        private readonly byte[] _counterBlock = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _keystreamUsed = 16;
        private ulong _packetCounter;

        public Cipher(byte[] key)
        {
            _key = key;
            _aes.Key = key;
            key.AsSpan(0, 12).CopyTo(_counterBlock);
            _counterBlock[15] = 2;
        }

        // data = 0xFE ‖ plaintext; returns 0xFE ‖ encrypt(plaintext ‖ checksum).
        public byte[] Encrypt(byte[] data)
        {
            var result = new byte[data.Length + 8];
            data.CopyTo(result, 0);
            Checksum(data.AsSpan(1)).CopyTo(result.AsSpan(data.Length));
            Xor(result.AsSpan(1));
            return result;
        }

        public byte[] Decrypt(byte[] body)
        {
            Xor(body);
            if (body.Length < 8)
                throw new InvalidDataException("Encrypted batch is too short.");
            var plain = body[..^8];
            if (!CryptographicOperations.FixedTimeEquals(Checksum(plain), body.AsSpan(body.Length - 8)))
                throw new InvalidDataException("Invalid batch checksum.");
            return plain;
        }

        private byte[] Checksum(ReadOnlySpan<byte> plain)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(counter, _packetCounter++);
            sha.AppendData(counter);
            sha.AppendData(plain);
            sha.AppendData(_key);
            return sha.GetHashAndReset()[..8];
        }

        private void Xor(Span<byte> data)
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (_keystreamUsed == 16)
                {
                    _aes.EncryptEcb(_counterBlock, _keystream, PaddingMode.None);
                    for (var j = 15; j >= 0 && ++_counterBlock[j] == 0; j--)
                    {
                    }
                    _keystreamUsed = 0;
                }
                data[i] ^= _keystream[_keystreamUsed++];
            }
        }
    }
}
