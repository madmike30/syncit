using System.Text.Json;

namespace SyncIt.Core.Ipc;

/// <summary>
/// Serializes and deserializes IpcMessages and their typed payloads.
/// Messages are newline-delimited JSON — one JSON object per line.
/// </summary>
public static class IpcSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = false,
    };

    public static string Serialize(IpcMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static IpcMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<IpcMessage>(json, Options);

    public static string SerializePayload<T>(T payload) =>
        JsonSerializer.Serialize(payload, Options);

    public static T? DeserializePayload<T>(string? json) =>
        json is null ? default : JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Build an IpcMessage with a typed payload.
    /// </summary>
    public static IpcMessage Build<T>(IpcMessageType type, T payload) =>
        new() { Type = type, Payload = SerializePayload(payload) };

    /// <summary>
    /// Build an IpcMessage with no payload.
    /// </summary>
    public static IpcMessage Build(IpcMessageType type) =>
        new() { Type = type };
}
