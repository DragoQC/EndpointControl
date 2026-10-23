using System.Text.Json;

namespace MeshAll.Server.Services;

public static class NativeDesktopProtocol
{
    // The retained viewer sends its latency control message before protocol "2".
    // Permit only harmless control metadata before locking this relay to desktop.
    public static bool IsHandshakeControl(byte[] bytes)
    {
        if(bytes.Length is 0 or >4096 || bytes[0]!='{')return false;
        try
        {
            using var doc=JsonDocument.Parse(bytes);var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Any(p=>p.Name is not("ctrlChannel" or "type" or "time")))return false;
            return root.TryGetProperty("ctrlChannel",out var channel)&&channel.ToString()=="102938"
                && root.TryGetProperty("type",out var type)&&type.ValueKind==JsonValueKind.String&&type.GetString() is "rtt" or "ping" or "pong"
                && (!root.TryGetProperty("time",out var time)||time.ValueKind==JsonValueKind.Number);
        }
        catch(JsonException){return false;}
    }
}
