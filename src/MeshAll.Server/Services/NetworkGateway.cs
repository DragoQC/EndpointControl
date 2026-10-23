using System.Net.WebSockets;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed class NetworkGateway(Store store, AgentGateway agents, ProtocolWorker worker, ILogger<NetworkGateway> logger)
{
    public async Task ProxyAsync(HttpContext context, string id, string tool)
    {
        if (!context.WebSockets.IsWebSocketRequest || !AgentGateway.SameOrigin(context) || tool is not ("terminal" or "powershell" or "vnc" or "rdp" or "ssh" or "amt")) { context.Response.StatusCode = 403; return; }
        var direct=context.Request.Query["direct"]=="1"&&tool is "rdp" or "vnc" or "ssh" or "amt";
        var device = store.FindDevice(id); if (device is not { Disabled: false } || !direct&&!agents.Online(id)) { context.Response.StatusCode = 409; return; }
        if(direct&&(!System.Net.IPAddress.TryParse(device.Inventory?.Ip,out _)||tool is "rdp" or "vnc" or "amt"&&store.RequiresDesktopConsent(id))){context.Response.StatusCode=403;return;}
        var terminal=tool is "terminal" or "powershell";
        var port = terminal?0:int.TryParse(context.Request.Query["port"], out var requested) ? requested : tool switch { "vnc" => 5900, "rdp" => 3389, "ssh" => 22, _ => 16994 };
        if (!terminal&&port is < 1 or > 65535) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); stop.CancelAfter(TimeSpan.FromHours(2));
        var session = store.StartSession(device, context.User.Identity!.Name!, tool);
        try
        {
            JsonElement options = default;
            if (tool is "rdp" or "ssh")
            {
                var setup = await AgentGateway.ReceiveBrowserAsync(socket, stop.Token) ?? throw new IOException("Missing connection parameters.");
                options = JsonSerializer.Deserialize<JsonElement>(setup);
            }
            var screen = tool is "rdp" or "vnc" or "amt";
            await using var remote = direct?await AgentGateway.OpenDirectStreamAsync(device.Inventory!.Ip,port,stop.Token):await agents.OpenStreamAsync(id, new(session, context.User.Identity!.Name!, screen && store.RequiresDesktopConsent(id), tool, "127.0.0.1", port), stop.Token);
            await using var protocol = tool is "rdp" or "ssh" ? await worker.OpenAsync(tool, options, remote, stop.Token) : null;
            store.Audit(context.User.Identity!.Name!, "Remote session opened", device.Name, tool);
            async Task Output()
            {
                if (protocol is not null) { var buffer = new byte[32768]; int count; while ((count = await protocol.Stream.ReadAsync(buffer, stop.Token)) > 0) await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, stop.Token); }
                else await foreach (var bytes in remote.Reader.ReadAllAsync(stop.Token)) await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, stop.Token);
            }
            async Task Input()
            {
                while (socket.State == WebSocketState.Open)
                {
                    var bytes = await AgentGateway.ReceiveBrowserAsync(socket, stop.Token); if (bytes is null) return;
                    if (tool is "terminal" or "powershell" && bytes.Length > 0 && bytes[0] == '{')
                    {
                        try { using var doc = JsonDocument.Parse(bytes); if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "resize") { await remote.ResizeAsync(doc.RootElement.GetProperty("columns").GetInt32(), doc.RootElement.GetProperty("rows").GetInt32(), stop.Token); continue; } } catch (JsonException) { }
                    }
                    if (protocol is not null) await protocol.Stream.WriteAsync(bytes, stop.Token); else await remote.WriteAsync(bytes, stop.Token);
                }
            }
            var output = Output(); var input = Input(); var validation = agents.ValidateAsync(context.User, id, stop.Token);
            await Task.WhenAny(output, input, validation); await stop.CancelAsync();
            try { await Task.WhenAll(output, input, validation); } catch (OperationCanceledException) { }
            store.EndSession(session, "Completed");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException or System.Net.Sockets.SocketException)
        {
            store.EndSession(session, "Failed"); logger.LogInformation("MeshAll {Tool} session ended: {Reason}", tool, ex.Message);
            if (socket.State == WebSocketState.Open) try { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, ex.Message.Length <= 100 ? ex.Message : "Remote session failed.", CancellationToken.None); } catch (WebSocketException) { }
        }
    }
}
