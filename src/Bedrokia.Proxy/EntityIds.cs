// ┌───────────────────────────────────────────────────────────────────┐
// │  Bedrokia Proxy - a NetherNet proxy for Bedrokia servers           │
// │  Licensed under the Apache License 2.0 - see LICENSE              │
// └───────────────────────────────────────────────────────────────────┘

using System.Buffers.Binary;
using Bedrokia.Protocol;

namespace Bedrokia.Proxy;

/// <summary>
/// The client keeps the entity id of its first StartGame for the whole connection; each server gives the player
/// another one. Packets carrying the player's id are rewritten: server id ↔ client id (a swap, so an entity of the
/// new server that has the client's id keeps a distinct id too).
/// </summary>
/// <remarks>
/// ponytail: only ids at the start of the packets Bedrokia sends or receives are rewritten (layouts below, from
/// gophertunnel at protocol 2193). An id deeper in a packet (entity links, AddPlayer abilities, attack targets in
/// InventoryTransaction) keeps the server value: add its layout here if a feature shows the wrong entity.
/// </remarks>
public static class EntityIds
{
    // U = VarUInt64 runtime id, S = VarInt64 unique id, L = Int64 LE unique id (all rewritten);
    // b = 1 byte, v = varint, V = Vec3, u = UUID, s = string (skipped).
    private static readonly Dictionary<uint, string> Layouts = new()
    {
        [12] = "usU", // AddPlayer
        [13] = "SU", // AddActor
        [14] = "S", // RemoveActor
        [15] = "SU", // AddItemActor
        [17] = "UU", // TakeItemActor: item, taker
        [18] = "U", // MoveActorAbsolute
        [19] = "U", // MovePlayer
        [22] = "SU", // AddPainting
        [27] = "U", // ActorEvent
        [28] = "U", // MobEffect
        [29] = "U", // UpdateAttributes
        [31] = "U", // MobEquipment
        [32] = "U", // MobArmorEquipment
        [33] = "bU", // Interact
        [36] = "U", // PlayerAction
        [39] = "U", // SetActorData
        [40] = "U", // SetActorMotion
        [44] = "bU", // Animate
        [45] = "VbU", // Respawn
        [111] = "U", // MoveActorDelta
        [113] = "U", // SetLocalPlayerAsInitialised
        [138] = "U", // Emote
        [187] = "L", // UpdateAbilities
    };

    /// <summary>Returns <paramref name="packet"/> itself when nothing changes, else a rewritten copy.</summary>
    public static byte[] Swap(byte[] packet, long a, long b)
    {
        if (a == b || a < 0 || b < 0)
            return packet;
        try
        {
            var o = 0;
            var id = (uint)ReadVarUInt(packet, ref o) & 0x3FF;
            if (!Layouts.TryGetValue(id, out var layout))
                return packet;
            List<(int Start, int End, char Kind, long Value)>? edits = null;
            foreach (var field in layout)
            {
                var start = o;
                long value;
                switch (field)
                {
                    case 'U':
                        value = (long)ReadVarUInt(packet, ref o);
                        break;
                    case 'S':
                        value = BedrockWriter.ZigZagDecode64(ReadVarUInt(packet, ref o));
                        break;
                    case 'L':
                        value = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(o, 8));
                        o += 8;
                        break;
                    case 'b':
                        o += 1;
                        continue;
                    case 'v':
                        ReadVarUInt(packet, ref o);
                        continue;
                    case 'V':
                        o += 12;
                        continue;
                    case 'u':
                        o += 16;
                        continue;
                    default: // 's'
                        o += (int)ReadVarUInt(packet, ref o);
                        continue;
                }
                if (value == a || value == b)
                    (edits ??= []).Add((start, o, field, value == a ? b : a));
            }
            if (edits is null || o > packet.Length)
                return packet;

            var w = new BedrockWriter(packet.Length + 8);
            var copied = 0;
            foreach (var (start, end, kind, value) in edits)
            {
                w.Bytes(packet.AsSpan(copied, start - copied));
                _ = kind switch
                {
                    'U' => w.VarUInt64((ulong)value),
                    'S' => w.VarInt64(value),
                    _ => w.Int64(value),
                };
                copied = end;
            }
            w.Bytes(packet.AsSpan(copied));
            return w.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return packet; // malformed: let the receiver decide
        }
    }

    private static ulong ReadVarUInt(byte[] data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset >= data.Length)
                throw new InvalidDataException("Truncated varint.");
            var b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Varint too long.");
    }
}
