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

/// <summary>An NBT list: element tag type plus values.</summary>
public sealed record NbtList(byte ElementType, List<object> Values);

/// <summary>
/// Generic NBT reader/writer for Bedrock's two encodings: little-endian (disk, item user data) and
/// "network" little-endian (varint ints/longs, varuint string lengths). Compounds are
/// <see cref="OrderedDictionary{TKey,TValue}"/>; values are byte, short, int, long, float, double, byte[],
/// string, <see cref="NbtList"/>, compound, int[] and long[].
/// </summary>
public static class Nbt
{
    public const byte End = 0, Byte = 1, Short = 2, Int = 3, Long = 4, Float = 5, Double = 6, ByteArray = 7,
        String = 8, List = 9, Compound = 10, IntArray = 11, LongArray = 12;

    private const int MaxDepth = 512;

    /// <summary>Reads consecutive root compounds until the data ends.</summary>
    /// <exception cref="InvalidDataException">Malformed NBT.</exception>
    public static List<OrderedDictionary<string, object>> ReadAll(ReadOnlySpan<byte> data, bool network)
    {
        var reader = new BedrockReader(data);
        var result = new List<OrderedDictionary<string, object>>();
        while (reader.Remaining > 0)
            result.Add(ReadRoot(ref reader, network));
        return result;
    }

    public static OrderedDictionary<string, object> ReadRoot(ref BedrockReader reader, bool network)
    {
        if (reader.Byte() != Compound)
            throw new InvalidDataException("NBT root is not a compound.");
        ReadString(ref reader, network);
        return (OrderedDictionary<string, object>)ReadPayload(ref reader, Compound, network, 0);
    }

    private static object ReadPayload(ref BedrockReader r, byte type, bool network, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("NBT is nested too deeply.");
        switch (type)
        {
            case Byte: return r.Byte();
            case Short: return BinaryPrimitives.ReadInt16LittleEndian(r.Bytes(2));
            case Int: return network ? r.VarInt32() : r.Int32();
            case Long: return network ? r.VarInt64() : BinaryPrimitives.ReadInt64LittleEndian(r.Bytes(8));
            case Float: return r.Float32();
            case Double: return BinaryPrimitives.ReadDoubleLittleEndian(r.Bytes(8));
            case ByteArray: return r.Bytes(Length(ref r, network)).ToArray();
            case String: return ReadString(ref r, network);
            case List:
            {
                var elementType = r.Byte();
                var count = Length(ref r, network);
                var values = new List<object>(Math.Min(count, 4096));
                for (var i = 0; i < count; i++)
                    values.Add(ReadPayload(ref r, elementType, network, depth + 1));
                return new NbtList(elementType, values);
            }
            case Compound:
            {
                var compound = new OrderedDictionary<string, object>();
                while (true)
                {
                    var tag = r.Byte();
                    if (tag == End)
                        return compound;
                    var name = ReadString(ref r, network);
                    compound[name] = ReadPayload(ref r, tag, network, depth + 1);
                }
            }
            case IntArray:
            {
                var values = new int[Length(ref r, network)];
                for (var i = 0; i < values.Length; i++)
                    values[i] = network ? r.VarInt32() : r.Int32();
                return values;
            }
            case LongArray:
            {
                var values = new long[Length(ref r, network)];
                for (var i = 0; i < values.Length; i++)
                    values[i] = network ? r.VarInt64() : BinaryPrimitives.ReadInt64LittleEndian(r.Bytes(8));
                return values;
            }
            default:
                throw new InvalidDataException($"Unknown NBT tag {type}.");
        }
    }

    private static int Length(ref BedrockReader r, bool network)
    {
        var length = network ? r.VarInt32() : r.Int32();
        if (length < 0 || length > r.Remaining * 8 + 1)
            throw new InvalidDataException("Invalid NBT length.");
        return length;
    }

    private static string ReadString(ref BedrockReader r, bool network)
    {
        var length = network ? (int)r.VarUInt32() : BinaryPrimitives.ReadUInt16LittleEndian(r.Bytes(2));
        return Encoding.UTF8.GetString(r.Bytes(length));
    }

    /// <summary>Writes a root compound with an empty name.</summary>
    public static byte[] Write(OrderedDictionary<string, object> root, bool network)
    {
        var w = new BedrockWriter();
        w.Byte(Compound);
        WriteString(w, "", network);
        WritePayload(w, root, network);
        return w.ToArray();
    }

    public static byte TypeOf(object value) => value switch
    {
        byte or bool => Byte,
        short => Short,
        int => Int,
        long => Long,
        float => Float,
        double => Double,
        byte[] => ByteArray,
        string => String,
        NbtList => List,
        OrderedDictionary<string, object> => Compound,
        int[] => IntArray,
        long[] => LongArray,
        _ => throw new ArgumentException($"Unsupported NBT value {value.GetType()}"),
    };

    private static void WritePayload(BedrockWriter w, object value, bool network)
    {
        switch (value)
        {
            case byte b: w.Byte(b); break;
            case bool b: w.Bool(b); break;
            case short s: w.Int16(s); break;
            case int i: if (network) w.VarInt32(i); else w.Int32(i); break;
            case long l: if (network) w.VarInt64(l); else w.Int64(l); break;
            case float f: w.Float32(f); break;
            case double d: w.Int64(BitConverter.DoubleToInt64Bits(d)); break;
            case byte[] bytes: WriteLength(w, bytes.Length, network); w.Bytes(bytes); break;
            case string s: WriteString(w, s, network); break;
            case NbtList list:
                w.Byte(list.Values.Count == 0 ? list.ElementType : TypeOf(list.Values[0]));
                WriteLength(w, list.Values.Count, network);
                foreach (var item in list.Values)
                    WritePayload(w, item, network);
                break;
            case OrderedDictionary<string, object> compound:
                foreach (var (name, item) in compound)
                {
                    w.Byte(TypeOf(item));
                    WriteString(w, name, network);
                    WritePayload(w, item, network);
                }
                w.Byte(End);
                break;
            case int[] ints:
                WriteLength(w, ints.Length, network);
                foreach (var i in ints)
                    if (network) w.VarInt32(i); else w.Int32(i);
                break;
            case long[] longs:
                WriteLength(w, longs.Length, network);
                foreach (var l in longs)
                    if (network) w.VarInt64(l); else w.Int64(l);
                break;
            default:
                throw new ArgumentException($"Unsupported NBT value {value.GetType()}");
        }
    }

    private static void WriteLength(BedrockWriter w, int length, bool network)
    {
        if (network)
            w.VarInt32(length);
        else
            w.Int32(length);
    }

    private static void WriteString(BedrockWriter w, string value, bool network)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (network)
            w.VarUInt32((uint)byteCount);
        else
            w.UInt16((ushort)byteCount);
        var span = w.GrowSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
    }
}
