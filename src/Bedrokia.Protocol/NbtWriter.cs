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

using System.Buffers.Binary;
using System.Text;

namespace Bedrokia.Protocol;

/// <summary>
/// Minimal NBT writer for the two Bedrock variants: plain little-endian (disk, block hashes) and
/// "network" little-endian (varint ints, varuint string lengths). Only the tags Bedrokia emits are supported.
/// </summary>
public sealed class NbtWriter(bool network)
{
    public const byte TagEnd = 0, TagByte = 1, TagInt = 3, TagString = 8, TagList = 9, TagCompound = 10;

    private readonly BedrockWriter _out = new();

    public NbtWriter BeginCompound(string name = "")
    {
        _out.Byte(TagCompound);
        return Name(name);
    }

    public NbtWriter EndCompound()
    {
        _out.Byte(TagEnd);
        return this;
    }

    public NbtWriter Byte(string name, byte value)
    {
        _out.Byte(TagByte);
        Name(name);
        _out.Byte(value);
        return this;
    }

    public NbtWriter Int(string name, int value)
    {
        _out.Byte(TagInt);
        Name(name);
        if (network)
            _out.VarInt32(value);
        else
            _out.Int32(value);
        return this;
    }

    public NbtWriter String(string name, string value)
    {
        _out.Byte(TagString);
        Name(name);
        return Text(value);
    }

    public NbtWriter EmptyList(string name, byte elementTag)
    {
        _out.Byte(TagList);
        Name(name);
        _out.Byte(elementTag);
        if (network)
            _out.VarInt32(0);
        else
            _out.Int32(0);
        return this;
    }

    public byte[] ToArray() => _out.ToArray();

    private NbtWriter Name(string name) => Text(name);

    private static readonly byte[] CachedEmpty = new NbtWriter(network: true).BeginCompound().EndCompound().ToArray();

    private NbtWriter Text(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (network)
        {
            _out.VarUInt32((uint)byteCount);
        }
        else
        {
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)byteCount);
            _out.Bytes(length);
        }
        var span = _out.GrowSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        return this;
    }

    /// <summary>Network NBT of an empty root compound.</summary>
    public static byte[] EmptyNetworkCompound() => CachedEmpty;
}
