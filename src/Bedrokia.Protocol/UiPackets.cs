using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Bedrokia.Protocol;

/// <summary>
/// Packet serialization and parsing for Bedrock Forms UI and Data-Driven UI (DDUI / DataStore).
/// </summary>
public static class UiPackets
{
    /// <summary>
    /// Creates a ModalFormRequestPacket (0x64 / 100) payload.
    /// </summary>
    public static byte[] ModalFormRequest(uint formId, ReadOnlySpan<byte> jsonData)
    {
        var writer = new BedrockWriter();
        writer.VarUInt32(PacketId.ModalFormRequest);
        writer.VarUInt32(formId);
        writer.VarUInt32((uint)jsonData.Length);
        writer.Bytes(jsonData);
        return writer.ToArray();
    }

    /// <summary>
    /// Creates a ModalFormRequestPacket payload from a JSON string.
    /// </summary>
    public static byte[] ModalFormRequest(uint formId, string jsonData) =>
        ModalFormRequest(formId, Encoding.UTF8.GetBytes(jsonData));

    /// <summary>
    /// Decodes a ModalFormResponsePacket (0x65 / 101) sent by the client.
    /// </summary>
    public static (uint FormId, bool HasResponseData, string? ResponseJson, bool HasCancelReason, byte? CancelReason) ReadModalFormResponse(ref BedrockReader reader)
    {
        uint formId = reader.VarUInt32();
        bool hasResponse = reader.Bool();
        string? responseJson = hasResponse ? reader.String() : null;
        bool hasCancel = reader.Bool();
        byte? cancelReason = hasCancel ? reader.Byte() : null;
        return (formId, hasResponse, responseJson, hasCancel, cancelReason);
    }

    /// <summary>
    /// Creates a ServerSettingsResponsePacket (0x67 / 103) payload.
    /// </summary>
    public static byte[] ServerSettingsResponse(uint formId, ReadOnlySpan<byte> jsonData)
    {
        var writer = new BedrockWriter();
        writer.VarUInt32(PacketId.ServerSettingsResponse);
        writer.VarUInt32(formId);
        writer.VarUInt32((uint)jsonData.Length);
        writer.Bytes(jsonData);
        return writer.ToArray();
    }

    // Data-Driven UI, layouts from gophertunnel (MIT) packet/clientbound_data_driven_ui_*.go and protocol/data_store.go.

    /// <summary>ClientBoundDataDrivenUIShowScreen (333): screen id, form instance id (uint32), optional data instance id.</summary>
    public static byte[] DataDrivenUIShowScreen(string screenId, uint formId, uint? dataInstanceId = null)
    {
        var writer = new BedrockWriter().VarUInt32(PacketId.ClientboundDataDrivenUIShowScreen).String(screenId).UInt32(formId)
            .Bool(dataInstanceId is not null);
        if (dataInstanceId is { } id)
            writer.UInt32(id);
        return writer.ToArray();
    }

    /// <summary>ClientBoundDataDrivenUICloseScreen (334) without a form id: closes every screen.</summary>
    public static byte[] DataDrivenUICloseAllScreens() =>
        new BedrockWriter().VarUInt32(PacketId.ClientboundDataDrivenUICloseScreen).Bool(false).ToArray();

    /// <summary>ClientBoundDataDrivenUIReload (335): no payload.</summary>
    public static byte[] DataDrivenUIReload() => new BedrockWriter().VarUInt32(PacketId.ClientboundDataDrivenUIReload).ToArray();

    private const uint ChangeTypeUpdate = 0, ControlDouble = 0, ControlBoolean = 1, ControlString = 2;
    private static uint _updateCount;

    /// <summary>
    /// ClientBoundDataStore (330): one "update" entry per property, sent as a string value. The update counters only
    /// grow, so the client never treats a new value as an old one.
    /// </summary>
    public static byte[] ClientboundDataStore(string storeName, IReadOnlyDictionary<string, string> updates)
    {
        var writer = new BedrockWriter().VarUInt32(PacketId.ClientboundDataStore).VarUInt32((uint)updates.Count);
        foreach (var (property, value) in updates)
        {
            var count = Interlocked.Increment(ref _updateCount);
            writer.VarUInt32(ChangeTypeUpdate).String(storeName).String(property).String("") // path
                .VarUInt32(ControlString).String(value).UInt32(count).UInt32(count);
        }
        return writer.ToArray();
    }

    /// <summary>
    /// Decodes ServerBoundDataStore (332): store, property, path, then a double, a bool or a string (returned as text,
    /// "true"/"false" or an invariant number), then two update counters.
    /// </summary>
    /// <exception cref="InvalidDataException">Unknown value type.</exception>
    public static (string StoreName, string Property, string Value) ReadServerboundDataStore(ref BedrockReader reader)
    {
        var storeName = reader.String();
        var property = reader.String();
        reader.String(); // path
        var value = reader.VarUInt32() switch
        {
            ControlDouble => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(reader.Bytes(8)))
                .ToString(CultureInfo.InvariantCulture),
            ControlBoolean => reader.Bool() ? "true" : "false",
            ControlString => reader.String(),
            var other => throw new InvalidDataException($"Unknown data store value type {other}."),
        };
        reader.Bytes(8); // property and path update counters
        return (storeName, property, value);
    }
}
