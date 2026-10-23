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
public sealed class AgentGateway(Store store, ILogger<AgentGateway> logger)
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
    private readonly ConcurrentDictionary<string,NativeAgentPeer> native = new();
    public void RegisterNative(NativeAgentPeer peer) { native.AddOrUpdate(peer.Id,peer,(_,old)=>{old.Disconnect();return peer;});store.Audit("Agent","Native MeshAll Agent connected",store.FindDevice(peer.Id)?.Name??peer.Id); }
    public void UnregisterNative(NativeAgentPeer peer) { if(native.TryRemove(new KeyValuePair<string,NativeAgentPeer>(peer.Id,peer)))store.Audit("Agent","Native MeshAll Agent disconnected",store.FindDevice(peer.Id)?.Name??peer.Id); }
    public bool Configured => true;
    public bool Connected => true;
    public string Status => "Standalone MeshAll agent service ready";
    public bool Online(string id) => store.FindDevice(id) is { Disabled: false } && (native.TryGetValue(id,out var peer)&&peer.Online||connections.TryGetValue(id, out var c) && c.Socket.State == WebSocketState.Open && DateTimeOffset.UtcNow - c.LastSeen < TimeSpan.FromSeconds(50));
    public int OnlineCount => connections.Keys.Concat(native.Keys).Distinct().Count(Online);
    public bool RemoteGroup(string id) => store.Groups().Any(g => g.Id == id);
    public Task SaveGroupAsync(string? id, string name, string description, int consent = 0) { store.SaveGroup(id, name, description, consent); return Task.CompletedTask; }
    public Task DeleteGroupAsync(string id) { store.DeleteGroup(id); return Task.CompletedTask; }
    public Task SaveDeviceAsync(string id, string name, string group, string tags, string notes) { store.SaveDevice(id, name, group, tags, notes); return Task.CompletedTask; }
    public void Disconnect(string id) { if (connections.TryGetValue(id, out var c)) c.Socket.Abort();if(native.TryGetValue(id,out var peer))peer.Disconnect(); }

    public async Task AcceptAsync(HttpContext context)
    {
        var id = context.Request.Headers["X-Device-Id"].ToString(); var authorization = context.Request.Headers.Authorization.ToString();
        if (!context.WebSockets.IsWebSocketRequest || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !store.ValidAgent(id, authorization[7..])) { context.Response.StatusCode = 401; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(); var connection = new Connection(socket);
        connections.AddOrUpdate(id, connection, (_, old) => { old.Socket.Abort(); return connection; });
        var device = store.FindDevice(id)!; store.Audit("Agent", "Device connected", device.Name);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var monitor = MonitorAsync(connection, lifetime.Token);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await Wire.ReceiveAsync(socket, lifetime.Token); if (message is null) break;
                if (message.Kind == "inventory")
                {
                    var inventory = Wire.Read<Inventory>(message.Payload);
                    if (inventory.Hostname.Length > 255 || inventory.Disks.Length > 100 || inventory.Capabilities.Length > 30) throw new InvalidDataException("Invalid agent inventory.");
                    store.UpdateInventory(id, inventory); connection.LastSeen = DateTimeOffset.UtcNow;
                }
                else if (message.Kind == "result" && message.Id is not null && connection.Pending.TryRemove(message.Id, out var pending)) pending.TrySetResult(message);
                else if (message.Kind == "stream")
                {
                    var input = Wire.Read<SessionInput>(message.Payload);
                    if (input.Base64 is not null && connection.Streams.TryGetValue(input.SessionId, out var channel)) await channel.Writer.WriteAsync(Convert.FromBase64String(input.Base64), lifetime.Token);
                }
                else if (message.Kind == "stream.end" && message.Id is not null && connection.Streams.TryGetValue(message.Id, out var channel)) channel.Writer.TryComplete();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or FormatException) { logger.LogInformation("Agent {Device} disconnected: {Reason}", id, ex.Message); }
        finally
        {
            await lifetime.CancelAsync(); await monitor;
            if (connections.TryRemove(new KeyValuePair<string, Connection>(id, connection))) store.Audit("Agent", "Device disconnected", device.Name);
            foreach (var pending in connection.Pending.Values) pending.TrySetException(new IOException("Device disconnected."));
            foreach (var stream in connection.Streams.Values) stream.Writer.TryComplete(new IOException("Device disconnected."));
        }
    }
    private static async Task MonitorAsync(Connection connection, CancellationToken ct)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10)); while (await timer.WaitForNextTickAsync(ct)) if (DateTimeOffset.UtcNow - connection.LastSeen > TimeSpan.FromSeconds(50)) { connection.Socket.Abort(); return; } }
        catch (OperationCanceledException) { }
    }
    public async Task<T> ExecuteAsync<T>(string id, string operation, object payload, CancellationToken ct = default)
    {
        if(native.TryGetValue(id,out var peer)){if(!Online(id))throw new IOException("Device is offline or blocked.");return Wire.Read<T>(Wire.Element(await peer.ExecuteAsync(operation,payload,ct)));}
        if (!Online(id) || !connections.TryGetValue(id, out var connection)) throw new IOException("Device is offline.");
        if (connection.Pending.Count >= 16) throw new IOException("Device is busy. Try again shortly.");
        var requestId = Guid.NewGuid().ToString("N"); var completion = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Pending[requestId] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            await Wire.SendAsync(connection.Socket, new("request", requestId, operation, Wire.Element(payload)), connection.SendLock, timeout.Token);
            var reply = await completion.Task.WaitAsync(timeout.Token);
            if (!reply.Success) throw new IOException(reply.Error ?? "Agent operation failed.");
            return Wire.Read<T>(reply.Payload);
        }
        catch (OperationCanceledException)
        {
            try { await Wire.SendAsync(connection.Socket, new("cancel", requestId), connection.SendLock, CancellationToken.None); } catch (WebSocketException) { }
            throw;
        }
        finally { connection.Pending.TryRemove(requestId, out _); }
    }
    public sealed class RemoteStream : IAsyncDisposable
    {
        private readonly AgentGateway? gateway; private readonly string device=""; private readonly Connection? connection;
        private readonly Func<byte[],CancellationToken,Task>? externalWrite;
        private readonly Func<ValueTask>? externalClose;
        private readonly Func<int,int,CancellationToken,Task>? externalResize;
        private bool disposed;
        public string Id { get; }
        public ChannelReader<byte[]> Reader { get; }
        private RemoteStream(AgentGateway gateway, string device, string id, Connection connection, ChannelReader<byte[]> reader) { this.gateway = gateway; this.device = device; Id = id; this.connection = connection; Reader = reader; }
        private RemoteStream(string id,ChannelReader<byte[]> reader,Func<byte[],CancellationToken,Task> write,Func<ValueTask> close,Func<int,int,CancellationToken,Task> resize){Id=id;Reader=reader;externalWrite=write;externalClose=close;externalResize=resize;}
        public static RemoteStream External(string id,ChannelReader<byte[]> reader,Func<byte[],CancellationToken,Task> write,Func<ValueTask> close,Func<int,int,CancellationToken,Task> resize)=>new(id,reader,write,close,resize);
        public Task WriteAsync(byte[] bytes, CancellationToken ct) => externalWrite is not null?externalWrite(bytes,ct):Wire.SendAsync(connection!.Socket, new("stream", Payload: Wire.Element(new SessionInput(Id, Base64: Convert.ToBase64String(bytes)))), connection.SendLock, ct);
        public Task ResizeAsync(int columns, int rows, CancellationToken ct) => externalResize is not null?externalResize(columns,rows,ct):gateway!.ExecuteAsync<string>(device, "stream.resize", new SessionInput(Id, Columns: columns, Rows: rows), ct);
        public async ValueTask DisposeAsync()
        {
            if(disposed)return;disposed=true;if(externalClose is not null){await externalClose();return;}
            if (connection!.Streams.TryRemove(Id, out var stream)) stream.Writer.TryComplete();
            try { await gateway!.ExecuteAsync<string>(device, "stream.close", new SessionInput(Id)); } catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException) { }
        }
        internal static async Task<RemoteStream> OpenAsync(AgentGateway gateway, string device, SessionRequest request, string operation, CancellationToken ct)
        {
            if (!gateway.connections.TryGetValue(device, out var connection)) throw new IOException("Device is offline.");
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            if (!connection.Streams.TryAdd(request.SessionId, channel)) throw new IOException("Duplicate stream.");
            try { await gateway.ExecuteAsync<OpenedSession>(device, operation, request, ct); return new(gateway, device, request.SessionId, connection, channel.Reader); }
            catch { connection.Streams.TryRemove(request.SessionId, out _); throw; }
        }
    }
    public Task<RemoteStream> OpenStreamAsync(string id, SessionRequest request, CancellationToken ct) => native.TryGetValue(id,out var peer)?peer.OpenStreamAsync(request,ct):RemoteStream.OpenAsync(this, id, request, request.Kind is "terminal" or "powershell" ? "terminal.open" : "tcp.open", ct);
    public static async Task<RemoteStream> OpenDirectStreamAsync(string host,int port,CancellationToken ct)
    {
        var client=new TcpClient{NoDelay=true};
        try{await client.ConnectAsync(host,port,ct);}catch{client.Dispose();throw;}
        var stream=client.GetStream();var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);var channel=Channel.CreateBounded<byte[]>(64);
        async Task Read(){try{var buffer=new byte[32768];int count;while((count=await stream.ReadAsync(buffer,stop.Token))>0)await channel.Writer.WriteAsync(buffer[..count],stop.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException){}finally{channel.Writer.TryComplete();}}
        var reading=Read();
        return RemoteStream.External(Guid.NewGuid().ToString("N"),channel.Reader,(bytes,token)=>stream.WriteAsync(bytes,token).AsTask(),async()=>{await stop.CancelAsync();client.Dispose();await reading;stop.Dispose();},(_,_,_)=>Task.CompletedTask);
    }

    public static bool SameOrigin(HttpContext context) => Uri.TryCreate(context.Request.Headers.Origin, UriKind.Absolute, out var origin) && origin.Scheme == context.Request.Scheme && string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    public async Task ProxyDesktopAsync(HttpContext context, string id)
    {
        if(native.TryGetValue(id,out var peer)){await ProxyNativeDesktop(context,id,peer);return;}
        if (!context.WebSockets.IsWebSocketRequest || !SameOrigin(context)) { context.Response.StatusCode = 403; return; }
        var device = store.FindDevice(id); if (device is not { Disabled: false } || !Online(id)) { context.Response.StatusCode = 409; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(); using var sendLock = new SemaphoreSlim(1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); stop.CancelAfter(TimeSpan.FromHours(2));
        var session = store.StartSession(device, context.User.Identity!.Name!, "Desktop"); var opened = false;
        async Task Send(object value) { await sendLock.WaitAsync(stop.Token); try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json).AsMemory(), WebSocketMessageType.Text, true, stop.Token); } finally { sendLock.Release(); } }
        try
        {
            var consent = store.RequiresDesktopConsent(id);
            await Send(new { type = "status", message = consent ? "Waiting for the person at the PC to approve…" : "Connecting…" });
            await ExecuteAsync<OpenedSession>(id, "desktop.open", new SessionRequest(session, context.User.Identity!.Name!, consent), stop.Token); opened = true;
            store.Audit(context.User.Identity!.Name!, "Desktop session opened", device.Name, consent ? "User consent granted" : "Unattended policy");
            await Send(new { type = "status", message = "Connected" });
            async Task Frames() { while (!stop.IsCancellationRequested) { var frame = await ExecuteAsync<ScreenFrame>(id, "desktop.frame", new SessionInput(session), stop.Token); await Send(new { type = "frame", frame.Base64, frame.Width, frame.Height }); await Task.Delay(180, stop.Token); } }
            async Task Inputs() { while (socket.State == WebSocketState.Open) { var bytes = await ReceiveBrowserAsync(socket, stop.Token); if (bytes is null) return; var input = JsonSerializer.Deserialize<InputEvent>(bytes, Wire.Json); if (input is not null) await ExecuteAsync<string>(id, "desktop.input", new SessionInput(session, input), stop.Token); } }
            var frames = Frames(); var inputs = Inputs(); var validation = ValidateAsync(context.User, id, stop.Token);
            await Task.WhenAny(frames, inputs, validation); await stop.CancelAsync();
            try { await Task.WhenAll(frames, inputs, validation); } catch (OperationCanceledException) { }
            store.EndSession(session, "Completed");
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or JsonException)
        {
            store.EndSession(session, "Failed");
            if (socket.State == WebSocketState.Open && !stop.IsCancellationRequested) try { await Send(new { type = "error", message = ex.Message }); } catch (WebSocketException) { }
        }
        finally { if (opened) try { await ExecuteAsync<string>(id, "desktop.close", new SessionInput(session)); } catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException) { } }
    }
    private async Task ProxyNativeDesktop(HttpContext context,string id,NativeAgentPeer peer)
    {
        if(!context.WebSockets.IsWebSocketRequest||!SameOrigin(context)||!Online(id)){context.Response.StatusCode=403;return;}
        var device=store.FindDevice(id)!;using var browser=await context.WebSockets.AcceptWebSocketAsync();using var stop=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);stop.CancelAfter(TimeSpan.FromHours(2));
        var session=store.StartSession(device,context.User.Identity!.Name!,"Desktop");
        try
        {
            await using var tunnel=await peer.OpenTunnelAsync(0,new(session,context.User.Identity!.Name!,store.RequiresDesktopConsent(id)),stop.Token);
            await browser.SendAsync(Encoding.UTF8.GetBytes("c").AsMemory(),WebSocketMessageType.Text,true,stop.Token);
            async Task Output(){await foreach(var frame in tunnel.Reader.ReadAllAsync(stop.Token))await browser.SendAsync(frame.Bytes.AsMemory(),frame.Text?WebSocketMessageType.Text:WebSocketMessageType.Binary,true,stop.Token);}
            async Task Input(){while(browser.State==WebSocketState.Open){var frame=await NativeAgentHub.ReadAsync(browser,stop.Token);if(frame is null)return;await tunnel.SendAsync(frame.Bytes,frame.Text,stop.Token);}}
            var output=Output();var input=Input();var validation=ValidateAsync(context.User,id,stop.Token);await Task.WhenAny(output,input,validation);await stop.CancelAsync();try{await Task.WhenAll(output,input,validation);}catch(OperationCanceledException){}
            store.EndSession(session,"Completed");
        }
        catch(Exception ex)when(ex is IOException or WebSocketException or OperationCanceledException){store.EndSession(session,"Failed");logger.LogInformation("Native desktop ended: {Reason}",ex.Message);}
    }
    public async Task ValidateAsync(ClaimsPrincipal principal, string id, CancellationToken ct)
    {
        while (true) { await Task.Delay(5000, ct); var user = store.FindUser(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? ""); if (user is null || user.Disabled || user.Role == "Viewer" || user.Version.ToString() != principal.FindFirstValue("version") || store.FindDevice(id) is not { Disabled: false }) return; }
    }
    public static async Task<byte[]?> ReceiveBrowserAsync(WebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); var bytes = new byte[32768]; ValueWebSocketReceiveResult received;
        do { received = await socket.ReceiveAsync(bytes.AsMemory(), ct); if (received.MessageType == WebSocketMessageType.Close) return null; if (buffer.Length + received.Count > 1024 * 1024) throw new IOException("Browser message is too large."); buffer.Write(bytes, 0, received.Count); } while (!received.EndOfMessage);
        return buffer.ToArray();
    }
}
