using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

// MeshAll's independent agent protocol and relay implementation.
public sealed partial class AgentGateway(Store store, ILogger<AgentGateway> logger)
{
    private sealed class Connection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;

        public SemaphoreSlim SendLock { get; } = new(1);

        public ConcurrentDictionary<string, TaskCompletionSource<AgentMessage>> Pending { get; } = new();

        public ConcurrentDictionary<string, Channel<byte[]>> Streams { get; } = new();

        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }
    private readonly ConcurrentDictionary<string, Connection> connections = new();
    private readonly ConcurrentDictionary<string, NativeAgentPeer> native = new();

    public void RegisterNative(NativeAgentPeer peer)
    {
        native.AddOrUpdate(peer.Id, peer, (_, old) =>
{
    old.Disconnect();
    return peer;
});
        store.Audit("Agent", "Native EndpointControl Agent connected", store.FindDevice(peer.Id)?.Name ?? peer.Id);
    }
    public void UnregisterNative(NativeAgentPeer peer)
    {
        if (native.TryRemove(new KeyValuePair<string, NativeAgentPeer>(peer.Id, peer)))
        {
            store.Audit("Agent", "Native EndpointControl Agent disconnected", store.FindDevice(peer.Id)?.Name ?? peer.Id);
        }
    }
    public bool Configured => true;

    public bool Connected => true;

    public string Status => "EndpointControl agent service ready";

    public bool Online(string id) => store.FindDevice(id) is
    {
        Disabled: false
    } && (native.TryGetValue(id, out NativeAgentPeer? peer) && peer.Online || connections.TryGetValue(id, out Connection? c) && c.Socket.State == WebSocketState.Open && DateTimeOffset.UtcNow - c.LastSeen < TimeSpan.FromSeconds(50));

    public int OnlineCount => connections.Keys.Concat(native.Keys).Distinct().Count(Online);

    /// <summary>Capture transport state once; list queries apply access and visibility filters in SQL.</summary>
    public HashSet<string> ConnectedDeviceIds()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HashSet<string> deviceIds = native
            .Where(connection => connection.Value.Online)
            .Select(connection => connection.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (KeyValuePair<string, Connection> connection in connections)
        {
            if (connection.Value.Socket.State == WebSocketState.Open
                && now - connection.Value.LastSeen < TimeSpan.FromSeconds(50))
            {
                deviceIds.Add(connection.Key);
            }
        }

        return deviceIds;
    }

    public bool RemoteGroup(string id) => store.Groups().Any(g => g.Id == id);

    public Task SaveGroupAsync(string? id, string name, string description, int consent = 0)
    {
        store.SaveGroup(id, name, description, consent);
        return Task.CompletedTask;
    }
    public Task DeleteGroupAsync(string id)
    {
        store.DeleteGroup(id);
        return Task.CompletedTask;
    }
    public Task SaveDeviceAsync(string id, string name, string group, string tags, string notes)
    {
        store.SaveDevice(id, name, group, tags, notes);
        return Task.CompletedTask;
    }
    public void Disconnect(string id)
    {
        if (connections.TryGetValue(id, out Connection? c))
        {
            c.Socket.Abort();
        }

        if (native.TryGetValue(id, out NativeAgentPeer? peer))
        {
            peer.Disconnect();
        }
    }

    public async Task AcceptAsync(HttpContext context)
    {
        string id = context.Request.Headers["X-Device-Id"].ToString();
        string authorization = context.Request.Headers.Authorization.ToString();
        if (!context.WebSockets.IsWebSocketRequest || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !store.ValidAgent(id, authorization[7..]))
        {
            context.Response.StatusCode = 401;
            return;
        }
        using WebSocket? socket = await context.WebSockets.AcceptWebSocketAsync();
        Connection connection = new Connection(socket);
        store.RestoreConnectedDevice(id);
        connections.AddOrUpdate(id, connection, (_, old) =>
{
    old.Socket.Abort();
    return connection;
});
        DeviceInfo? device = store.FindDevice(id);
        if (device is not
            {
                Disabled: false
            })
        {
            socket.Abort();
            return;
        }
        store.Audit("Agent", "Device connected", device.Name);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Task? monitor = MonitorAsync(connection, lifetime.Token);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                AgentMessage? message = await Wire.ReceiveAsync(socket, lifetime.Token);
                if (message is null)
                {
                    break;
                }

                if (message.Kind == "inventory")
                {
                    Inventory? inventory = Wire.Read<Inventory>(message.Payload);
                    if (inventory.Hostname is null || inventory.Hostname.Length > 255 || inventory.Disks is null || inventory.Disks.Length > 100
                        || inventory.Capabilities is null || inventory.Capabilities.Length > 30 || inventory.Interfaces is { Length: > 256 }
                        || inventory.Os is null || inventory.Os.Length > 4096 || inventory.Platform is null || inventory.Platform.Length > 64)
                    {
                        throw new InvalidDataException("Invalid agent inventory.");
                    }

                    inventory = NativeInventory.StampManagedResources(inventory, DateTimeOffset.UtcNow);
                    store.UpdateInventory(id, inventory with
                    {
                        ReportedAt = DateTimeOffset.UtcNow,
                        ConnectionAddress = context.Connection.RemoteIpAddress?.ToString()
                    });
                    connection.LastSeen = DateTimeOffset.UtcNow;
                }
                else if (message.Kind == "result" && message.Id is not null && connection.Pending.TryRemove(message.Id, out TaskCompletionSource<AgentMessage>? pending))
                {
                    pending.TrySetResult(message);
                }
                else if (message.Kind == "stream")
                {
                    SessionInput? input = Wire.Read<SessionInput>(message.Payload);
                    if (input.Base64 is not null && connection.Streams.TryGetValue(input.SessionId, out Channel<byte[]>? channel))
                    {
                        if (input.Base64.Length > 87384)
                        {
                            throw new InvalidDataException("Agent stream chunk exceeds the size limit.");
                        }
                        await channel.Writer.WriteAsync(Convert.FromBase64String(input.Base64), lifetime.Token);
                    }
                }
                else if (message.Kind == "stream.end" && message.Id is not null && connection.Streams.TryGetValue(message.Id, out Channel<byte[]>? channel))
                {
                    channel.Writer.TryComplete();
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or FormatException)
        {
            logger.LogInformation("Agent {Device} disconnected: {Reason}", id, ex.Message);
        }
        finally
        {
            await lifetime.CancelAsync();
            await monitor;
            if (connections.TryRemove(new KeyValuePair<string, Connection>(id, connection)))
            {
                store.Audit("Agent", "Device disconnected", device.Name);
            }

            foreach (TaskCompletionSource<AgentMessage>? pending in connection.Pending.Values)
            {
                pending.TrySetException(new IOException("Device disconnected."));
            }

            foreach (Channel<byte[]>? stream in connection.Streams.Values)
            {
                stream.Writer.TryComplete(new IOException("Device disconnected."));
            }
        }
    }
    private static async Task MonitorAsync(Connection connection, CancellationToken ct)
    {
        try
        {
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (DateTimeOffset.UtcNow - connection.LastSeen > TimeSpan.FromSeconds(50))
                {
                    connection.Socket.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
    public async Task<T> ExecuteAsync<T>(string id, string operation, object payload, CancellationToken ct = default)
    {
        if (native.TryGetValue(id, out NativeAgentPeer? peer))
        {
            if (!Online(id))
            {
                throw new IOException("Device is offline or blocked.");
            }

            return Wire.Read<T>(Wire.Element(await peer.ExecuteAsync(operation, payload, ct)));
        }
        if (!Online(id) || !connections.TryGetValue(id, out Connection? connection))
        {
            throw new IOException("Device is offline.");
        }

        if (connection.Pending.Count >= 16)
        {
            throw new IOException("Device is busy. Try again shortly.");
        }

        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<AgentMessage> completion = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Pending[requestId] = completion;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            await Wire.SendAsync(connection.Socket, new("request", requestId, operation, Wire.Element(payload)), connection.SendLock, timeout.Token);
            AgentMessage? reply = await completion.Task.WaitAsync(timeout.Token);
            if (!reply.Success)
            {
                throw new IOException(reply.Error ?? "Agent operation failed.");
            }

            return Wire.Read<T>(reply.Payload);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await Wire.SendAsync(connection.Socket, new("cancel", requestId), connection.SendLock, CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
            throw;
        }
        finally
        {
            connection.Pending.TryRemove(requestId, out _);
        }
    }
    public sealed class RemoteStream : IAsyncDisposable
    {
        private readonly AgentGateway? gateway;
        private readonly string device = "";
        private readonly Connection? connection;
        private readonly Func<byte[], CancellationToken, Task>? externalWrite;
        private readonly Func<ValueTask>? externalClose;
        private readonly Func<int, int, CancellationToken, Task>? externalResize;
        private bool disposed;

        public string Id
        {
            get;
        }
        public ChannelReader<byte[]> Reader
        {
            get;
        }
        private RemoteStream(AgentGateway gateway, string device, string id, Connection connection, ChannelReader<byte[]> reader)
        {
            this.gateway = gateway;
            this.device = device;
            Id = id;
            this.connection = connection;
            Reader = reader;
        }
        private RemoteStream(string id, ChannelReader<byte[]> reader, Func<byte[], CancellationToken, Task> write, Func<ValueTask> close, Func<int, int, CancellationToken, Task> resize)
        {
            Id = id;
            Reader = reader;
            externalWrite = write;
            externalClose = close;
            externalResize = resize;
        }
        public static RemoteStream External(string id, ChannelReader<byte[]> reader, Func<byte[], CancellationToken, Task> write, Func<ValueTask> close, Func<int, int, CancellationToken, Task> resize) => new(id, reader, write, close, resize);

        public Task WriteAsync(byte[] bytes, CancellationToken ct) => externalWrite is not null ? externalWrite(bytes, ct) : Wire.SendAsync(connection!.Socket, new("stream", Payload: Wire.Element(new SessionInput(Id, Base64: Convert.ToBase64String(bytes)))), connection.SendLock, ct);

        public Task ResizeAsync(int columns, int rows, CancellationToken ct) => externalResize is not null ? externalResize(columns, rows, ct) : gateway!.ExecuteAsync<string>(device, "stream.resize", new SessionInput(Id, Columns: columns, Rows: rows), ct);

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (externalClose is not null)
            {
                await externalClose();
                return;
            }
            if (connection!.Streams.TryRemove(Id, out Channel<byte[]>? stream))
            {
                stream.Writer.TryComplete();
            }

            try
            {
                await gateway!.ExecuteAsync<string>(device, "stream.close", new SessionInput(Id));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException)
            {
            }
        }
        internal static async Task<RemoteStream> OpenAsync(AgentGateway gateway, string device, SessionRequest request, string operation, CancellationToken ct)
        {
            if (!gateway.connections.TryGetValue(device, out Connection? connection))
            {
                throw new IOException("Device is offline.");
            }

            Channel<byte[]> channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            if (!connection.Streams.TryAdd(request.SessionId, channel))
            {
                throw new IOException("Duplicate stream.");
            }

            try
            {
                await gateway.ExecuteAsync<OpenedSession>(device, operation, request, ct);
                return new(gateway, device, request.SessionId, connection, channel.Reader);
            }
            catch
            {
                connection.Streams.TryRemove(request.SessionId, out _);
                throw;
            }
        }
    }
    public Task<RemoteStream> OpenStreamAsync(string id, SessionRequest request, CancellationToken ct) => native.TryGetValue(id, out NativeAgentPeer? peer) ? peer.OpenStreamAsync(request, ct) : RemoteStream.OpenAsync(this, id, request, request.Kind is "terminal" or "powershell" ? "terminal.open" : "tcp.open", ct);

    public static async Task<RemoteStream> OpenDirectStreamAsync(string host, int port, CancellationToken ct)
    {
        TcpClient client = new TcpClient
        {
            NoDelay = true
        };
        try
        {
            await client.ConnectAsync(host, port, ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        NetworkStream? stream = client.GetStream();
        CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Channel<byte[]> channel = Channel.CreateBounded<byte[]>(64);

        async Task Read()
        {
            try
            {
                byte[] buffer = new byte[32768];
                int count;
                while ((count = await stream.ReadAsync(buffer, stop.Token)) > 0)
                {
                    await channel.Writer.WriteAsync(buffer[..count], stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }
        Task? reading = Read();
        return RemoteStream.External(Guid.NewGuid().ToString("N"), channel.Reader, (bytes, token) => stream.WriteAsync(bytes, token).AsTask(), async () =>
{
    await stop.CancelAsync();
    client.Dispose();
    await reading;
    stop.Dispose();
}, (_, _, _) => Task.CompletedTask);
    }

    public static bool SameOrigin(HttpContext context) => context.Request.Headers.Origin.Count == 1
        && Uri.TryCreate(context.Request.Headers.Origin, UriKind.Absolute, out Uri? origin)
        && origin.Scheme == context.Request.Scheme && origin.UserInfo.Length == 0
        && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
        && string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);

    public async Task ValidateAsync(ClaimsPrincipal principal, string id, CancellationToken ct, string permission = Permissions.Desktop)
    {
        while (true)
        {
            await Task.Delay(5000, ct);
            UserInfo? user = store.SessionUser(principal);
            if (!store.HasPermission(user, permission) || !store.CanAccessDevice(user, id) || store.FindDevice(id) is not
                {
                    Disabled: false
                })
            {
                return;
            }
        }
    }
    public static async Task<byte[]?> ReceiveBrowserAsync(WebSocket socket, CancellationToken ct, int maximumBytes = 1024 * 1024)
    {
        using MemoryStream buffer = new MemoryStream();
        byte[] bytes = new byte[32768];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(bytes.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (buffer.Length + received.Count > maximumBytes)
            {
                throw new IOException("Browser message is too large.");
            }

            buffer.Write(bytes, 0, received.Count);
        } while (!received.EndOfMessage);
        return buffer.ToArray();
    }
}
