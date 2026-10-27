using System.Net.WebSockets;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed class NetworkGateway(Store store, AgentGateway agents, ProtocolWorker worker, RemoteIdentityResolver identities, ILogger<NetworkGateway> logger)
{
    public async Task ProxyAsync(HttpContext context, string id, string tool)
    {
        if (!context.WebSockets.IsWebSocketRequest || !AgentGateway.SameOrigin(context) || tool is not ("terminal" or "powershell" or "vnc" or "rdp" or "ssh" or "amt"))
        {
            context.Response.StatusCode = 403;
            return;
        }
        UserInfo? user = store.SessionUser(context.User);
        if (!store.HasPermission(user, Permissions.ForTool(tool)) || !store.CanAccessDevice(user, id))
        {
            context.Response.StatusCode = 403;
            return;
        }
        bool direct = context.Request.Query["direct"] == "1" && tool is "rdp" or "vnc" or "ssh" or "amt";
        DeviceInfo? device = store.FindDevice(id);
        if (device is not
            {
                Disabled: false
            } || !direct && !agents.Online(id))
        {
            context.Response.StatusCode = 409;
            return;
        }
        if (direct && (!System.Net.IPAddress.TryParse(device.Inventory?.Ip, out System.Net.IPAddress? address)
            || !RequestSecurity.AllowedDirectAddress(address) || ControllerBoundary.IsLocal(address) && !store.HasPermission(user, Permissions.Controller)
            || tool is "rdp" or "vnc" or "amt" && store.RequiresDesktopConsent(id)))
        {
            context.Response.StatusCode = 403;
            return;
        }
        bool terminal = tool is "terminal" or "powershell";
        int port = terminal ? 0 : int.TryParse(context.Request.Query["port"], out int requested) ? requested : tool switch
        {
            "vnc" => store.VncPort(id),
            "rdp" => 3389,
            "ssh" => 22,
            _ => 16994
        };
        if (!terminal && port is < 1 or > 65535)
        {
            context.Response.StatusCode = 400;
            return;
        }
        using WebSocket? socket = await context.WebSockets.AcceptWebSocketAsync();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        stop.CancelAfter(TimeSpan.FromHours(2));
        string session = store.StartSession(device, context.User.Identity!.Name!, tool);
        async Task Validate()
        {
            await agents.ValidateAsync(context.User, id, stop.Token, Permissions.ForTool(tool));
            await stop.CancelAsync();
        }
        Task validation = Validate();
        try
        {
            JsonElement options = default;
            if (tool is "rdp" or "ssh")
            {
                using CancellationTokenSource setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                setupTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                byte[] setup = await AgentGateway.ReceiveBrowserAsync(socket, setupTimeout.Token, 32768) ?? throw new IOException("Missing connection parameters.");
                options = JsonSerializer.Deserialize<JsonElement>(setup);
                ValidateOptions(options);
            }
            bool screen = tool is "rdp" or "vnc" or "amt";
            RemoteHostIdentity? identity = tool is "rdp" or "ssh" ? await identities.ResolveAsync(device, tool, port, stop.Token) : null;
            await using AgentGateway.RemoteStream? remote = direct ? await AgentGateway.OpenDirectStreamAsync(device.Inventory!.Ip, port, stop.Token) : await agents.OpenStreamAsync(id, new(session, context.User.Identity!.Name!, screen && store.RequiresDesktopConsent(id), tool, "127.0.0.1", port), stop.Token);
            await using ProtocolWorker.Session? protocol = identity is not null ? await worker.OpenAsync(tool, options, remote, identity, stop.Token) : null;
            store.Audit(context.User.Identity!.Name!, "Remote session opened", device.Name, tool);

            async Task Output()
            {
                if (protocol is not null)
                {
                    byte[] buffer = new byte[32768];
                    int count;
                    while ((count = await protocol.Stream.ReadAsync(buffer, stop.Token)) > 0)
                    {
                        await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, stop.Token);
                    }
                }
                else
                {
                    await foreach (byte[] bytes in remote.Reader.ReadAllAsync(stop.Token))
                    {
                        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, stop.Token);
                    }
                }
            }
            async Task Input()
            {
                while (socket.State == WebSocketState.Open)
                {
                    byte[]? bytes = await AgentGateway.ReceiveBrowserAsync(socket, stop.Token);
                    if (bytes is null)
                    {
                        return;
                    }

                    if (tool is "terminal" or "powershell" && bytes.Length > 0 && bytes[0] == '{')
                    {
                        if (TryReadTerminalResize(bytes, out int columns, out int rows))
                        {
                            await remote.ResizeAsync(columns, rows, stop.Token);
                            continue;
                        }
                    }
                    if (protocol is not null)
                    {
                        await protocol.Stream.WriteAsync(bytes, stop.Token);
                    }
                    else
                    {
                        await remote.WriteAsync(bytes, stop.Token);
                    }
                }
            }
            Task? output = Output();
            Task? input = Input();
            await Task.WhenAny(output, input, validation);
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(output, input, validation);
            }
            catch (OperationCanceledException)
            {
            }
            store.EndSession(session, "Completed");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException or System.Net.Sockets.SocketException or JsonException)
        {
            store.EndSession(session, "Failed");
            logger.LogInformation("MeshAll {Tool} session ended: {Reason}", tool, ex.Message);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, ex is JsonException ? "Invalid remote connection options."
                        : ex is OperationCanceledException ? "Remote setup or session authorization expired."
                        : SafeReason(ex.Message), CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await validation;
            }
            catch (OperationCanceledException) { }
        }
    }

    public static void ValidateOptions(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Count() > 16)
        {
            throw new IOException("Invalid remote connection options.");
        }
        foreach ((string Name, int Limit) field in new[] { ("username", 256), ("domain", 256), ("password", 512), ("privateKey", 16000) })
        {
            if (options.TryGetProperty(field.Name, out JsonElement value)
                && (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > field.Limit || value.GetString()!.Contains('\0')))
            {
                throw new IOException("Invalid remote connection options.");
            }
        }
        foreach (string name in new[] { "width", "height", "dpi" })
        {
            if (options.TryGetProperty(name, out JsonElement value)
                && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
            {
                throw new IOException("Invalid remote connection options.");
            }
        }
    }

    internal static bool TryReadTerminalResize(byte[] bytes, out int columns, out int rows)
    {
        columns = rows = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String && type.GetString() == "resize")
            {
                if (!document.RootElement.TryGetProperty("columns", out JsonElement width) || width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out columns)
                    || !document.RootElement.TryGetProperty("rows", out JsonElement height) || height.ValueKind != JsonValueKind.Number || !height.TryGetInt32(out rows))
                {
                    throw new IOException("Invalid terminal resize request.");
                }
                columns = Math.Clamp(columns, 20, 300);
                rows = Math.Clamp(rows, 5, 150);
                return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private static string SafeReason(string message) => message switch
    {
        "Connect the agent once to verify the remote host identity, or configure a trusted host identity." => message,
        "The remote host identity could not be verified through the agent." => message,
        "Invalid remote connection options." => message,
        "Invalid terminal resize request." => message,
        _ => "Remote session failed."
    };
}
