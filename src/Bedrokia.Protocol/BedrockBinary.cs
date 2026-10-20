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
using System.Runtime.CompilerServices;
using System.Text;

namespace Bedrokia.Protocol;

/// <summary>
/// Growable writer for Bedrock game packets. Fixed-width integers are little-endian unless named BE;
/// strings and byte arrays are prefixed with a varuint32 length.
/// </summary>
public sealed class BedrockWriter(int capacity = 256)
{
    private byte[] _buffer = new byte[capacity];

    public int Length { get; private set; }

    public Span<byte> GrowSpan(int count) => Grow(count);

    private Span<byte> Grow(int count)
    {
        if (Length + count > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        var span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Byte(byte value)
    {
        Grow(1)[0] = value;
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Bytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Grow(value.Length));
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Int16(short value) { BinaryPrimitives.WriteInt16LittleEndian(Grow(2), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter UInt16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Int32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter UInt32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Int32BE(int value) { BinaryPrimitives.WriteInt32BigEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Int64(long value) { BinaryPrimitives.WriteInt64LittleEndian(Grow(8), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter UInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Float32(float value) { BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter VarUInt64(ulong value)
    {
        if (value < 0x80)
            return Byte((byte)value);

        while (value >= 0x80)
        {
            Byte((byte)(value | 0x80));
            value >>= 7;
        }
        return Byte((byte)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter VarUInt32(uint value)
    {
        if (value < 0x80)
            return Byte((byte)value);

        while (value >= 0x80)
        {
            Byte((byte)(value | 0x80));
            value >>= 7;
        }
        return Byte((byte)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int VarUInt32Length(uint value) =>
        value < 0x80 ? 1 : value < 0x4000 ? 2 : value < 0x200000 ? 3 : value < 0x10000000 ? 4 : 5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ZigZagEncode32(int value) => (uint)((value << 1) ^ (value >> 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ZigZagDecode32(uint value) => (int)(value >> 1) ^ -(int)(value & 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ZigZagEncode64(long value) => (ulong)((value << 1) ^ (value >> 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ZigZagDecode64(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter VarInt32(int value) => VarUInt32(ZigZagEncode32(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter VarInt64(long value) => VarUInt64(ZigZagEncode64(value));

    public BedrockWriter String(ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        VarUInt32((uint)byteCount);
        var span = Grow(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        return this;
    }

    public BedrockWriter String(string value) => String(value.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter ByteArray(ReadOnlySpan<byte> value)
    {
        VarUInt32((uint)value.Length);
        return Bytes(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter Vec3(float x, float y, float z) => Float32(x).Float32(y).Float32(z);

    /// <summary>Block position: three zigzag varint32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter BlockPos(int x, int y, int z) => VarInt32(x).VarInt32(y).VarInt32(z);

    /// <summary>The nil UUID (16 zero bytes; byte order does not matter for it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BedrockWriter EmptyUuid() => Bytes(stackalloc byte[16]);

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, Length);

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();
}

/// <summary>
/// Rented array-backed zero-allocation writer for high throughput packet and batch construction.
/// Returned to <see cref="ArrayPool{T}.Shared"/> on <see cref="Dispose"/>.
/// </summary>
public sealed class PooledBedrockWriter : IDisposable
{
    private byte[] _rented;
    private int _length;
    private bool _disposed;

    public PooledBedrockWriter(int minimumCapacity = 512)
    {
        _rented = ArrayPool<byte>.Shared.Rent(minimumCapacity);
        _length = 0;
    }

    public int Length => _length;
    public ReadOnlySpan<byte> WrittenSpan => _rented.AsSpan(0, _length);

    public Span<byte> Grow(int count)
    {
        if (_length + count > _rented.Length)
        {
            var newRented = ArrayPool<byte>.Shared.Rent(Math.Max(_rented.Length * 2, _length + count));
            _rented.AsSpan(0, _length).CopyTo(newRented);
            ArrayPool<byte>.Shared.Return(_rented);
            _rented = newRented;
        }
        var span = _rented.AsSpan(_length, count);
        _length += count;
        return span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Byte(byte value)
    {
        Grow(1)[0] = value;
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Bytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Grow(value.Length));
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Int16(short value) { BinaryPrimitives.WriteInt16LittleEndian(Grow(2), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter UInt16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Int32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter UInt32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Int32BE(int value) { BinaryPrimitives.WriteInt32BigEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Int64(long value) { BinaryPrimitives.WriteInt64LittleEndian(Grow(8), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter UInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter Float32(float value) { BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value); return this; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter VarUInt64(ulong value)
    {
        if (value < 0x80)
            return Byte((byte)value);

        while (value >= 0x80)
        {
            Byte((byte)(value | 0x80));
            value >>= 7;
        }
        return Byte((byte)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter VarUInt32(uint value)
    {
        if (value < 0x80)
            return Byte((byte)value);

        while (value >= 0x80)
        {
            Byte((byte)(value | 0x80));
            value >>= 7;
        }
        return Byte((byte)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter VarInt32(int value) => VarUInt32(BedrockWriter.ZigZagEncode32(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter VarInt64(long value) => VarUInt64(BedrockWriter.ZigZagEncode64(value));

    public PooledBedrockWriter String(ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        VarUInt32((uint)byteCount);
        var span = Grow(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        return this;
    }

    public PooledBedrockWriter String(string value) => String(value.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBedrockWriter ByteArray(ReadOnlySpan<byte> value)
    {
        VarUInt32((uint)value.Length);
        return Bytes(value);
    }

    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_rented);
            _rented = [];
            _length = 0;
        }
    }
}

/// <summary>Bounds-checked reader for Bedrock game packets; overruns throw <see cref="InvalidDataException"/>.</summary>
public ref struct BedrockReader(ReadOnlySpan<byte> data)
{
    private const int MaxStringLength = 1 << 20;

    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException("Unexpected end of packet.");
        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Byte() => Bytes(1)[0];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Bool() => Byte() != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Int32BE() => BinaryPrimitives.ReadInt32BigEndian(Bytes(4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Float32() => BinaryPrimitives.ReadSingleLittleEndian(Bytes(4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong VarUInt64()
    {
        var b = Byte();
        if ((b & 0x80) == 0)
            return b;

        ulong value = (ulong)(b & 0x7F);
        for (var shift = 7; shift < 70; shift += 7)
        {
            b = Byte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Varint is too long.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint VarUInt32()
    {
        var b = Byte();
        if ((b & 0x80) == 0)
            return b;

        ulong value = (ulong)(b & 0x7F);
        for (var shift = 7; shift < 70; shift += 7)
        {
            b = Byte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (value > uint.MaxValue)
                    throw new InvalidDataException("Varint32 overflow.");
                return (uint)value;
            }
        }
        throw new InvalidDataException("Varint is too long.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int VarInt32() => BedrockWriter.ZigZagDecode32(VarUInt32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long VarInt64() => BedrockWriter.ZigZagDecode64(VarUInt64());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ByteArray()
    {
        var length = VarUInt32();
        if (length > MaxStringLength * 16)
            throw new InvalidDataException("Byte array is too long.");
        return Bytes((int)length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string String()
    {
        var length = VarUInt32();
        if (length > MaxStringLength)
            throw new InvalidDataException("String is too long.");
        return Encoding.UTF8.GetString(Bytes((int)length));
    }
}
