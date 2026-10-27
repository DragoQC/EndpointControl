using System.Net.WebSockets;
using System.Text.Json;

namespace MeshAll.Shared;

/// <summary>Serialization, framing and size limits shared by the server and managed agent.</summary>
public static class Wire
{
    public const string Version = "0.11.0";
    public const int MaxMessageBytes = 12 * 1024 * 1024;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Json)
        ?? throw new InvalidDataException("Missing message payload.");

    public static async Task SendAsync(WebSocket socket, AgentMessage message,
        SemaphoreSlim sendLock, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("Message is too large.");
        }

        await sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public static async Task<AgentMessage?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using MemoryStream stream = new();
        byte[] buffer = new byte[64 * 1024];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Expected JSON text.");
            }
            if (stream.Length + result.Count > MaxMessageBytes)
            {
                throw new InvalidDataException("Message is too large.");
            }

            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<AgentMessage>(stream.GetBuffer().AsSpan(0, (int)stream.Length), Json)
            ?? throw new InvalidDataException("Invalid message.");
    }
}
