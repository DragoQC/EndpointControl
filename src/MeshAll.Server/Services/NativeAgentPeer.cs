using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed partial class NativeAgentPeer : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly SemaphoreSlim sendLock;
    private readonly Store store;
    private readonly NativeCoreBuilder cores;
    private readonly NativeTunnelHub tunnels;
    private readonly bool windows, intel;
    private Inventory inventory;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> requests = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource coreReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task heartbeat;

    public string Id
    {
        get;
    }
    public string WebCertificateHash
    {
        get;
    }
    public bool Online => socket.State == WebSocketState.Open && !stop.IsCancellationRequested;

    public int Consent => store.Groups().FirstOrDefault(g => g.Id == store.FindDevice(Id)?.GroupId)?.Consent ?? 0;

    public NativeAgentPeer(string id, WebSocket socket, SemaphoreSlim sendLock, Store store, NativeCoreBuilder cores, NativeTunnelHub tunnels, IConfiguration config, bool windows, int architecture, Inventory inventory)
    {
        Id = id;
        this.socket = socket;
        this.sendLock = sendLock;
        this.store = store;
        this.cores = cores;
        this.tunnels = tunnels;
        this.windows = windows;
        intel = architecture is 1 or 2 or 3 or 4 or 5 or 6 or 16 or 18 or 19 or 20;
        this.inventory = inventory;
        string path = config["Kestrel:Certificates:Default:Path"] ?? config["MeshAll:CertificatePath"]!;
        using System.Security.Cryptography.X509Certificates.X509Certificate2? cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(path, config["Kestrel:Certificates:Default:Password"] ?? config["MeshAll:CertificatePassword"]);
        WebCertificateHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA384.HashData(cert.RawData));
        heartbeat = Heartbeat();
    }
    private async Task Heartbeat()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(15000, stop.Token);
                await SendJsonAsync(new
                {
                    action = "ping"
                }, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }
    public async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }
    public Task SendJsonAsync(object value, CancellationToken ct) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json), ct);

    public void Disconnect() => socket.Abort();

    public async Task ReceiveAsync(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        if (bytes[0] == '{')
        {
            JsonElement data = JsonSerializer.Deserialize<JsonElement>(bytes);
            string action = Text(data, "action");
            if (action == "msg" && Text(data, "type") == "console" && Text(data, "value").StartsWith("uncaughtException", StringComparison.Ordinal))
            {
                string issue = Text(data, "value");
                store.Audit("Agent", "Agent core diagnostic", inventory.Hostname, issue.Length > 2000 ? issue[..2000] : issue);
            }
            store.TouchDevice(Id);
            if (action == "getUserImage" && data.TryGetProperty("promise", out JsonElement imagePromise)
                && imagePromise.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                await SendJsonAsync(new
                {
                    action = "getUserImage",
                    promise = imagePromise,
                    image = "default2"
                }, ct);
                return;
            }
            if (action == "ping")
            {
                await SendJsonAsync(new
                {
                    action = "pong"
                }, ct);
                return;
            }
            if (action == "meshall-inventory" && data.TryGetProperty("inventory", out JsonElement report) && report.ValueKind == JsonValueKind.Object)
            {
                inventory = NativeInventory.Apply(inventory, report, DateTimeOffset.UtcNow);
                store.UpdateInventory(Id, inventory);
                return;
            }
            if (action == "coreinfo")
            {
                string users = data.TryGetProperty("users", out JsonElement userList) && userList.ValueKind == JsonValueKind.Array ? string.Join(", ", userList.EnumerateArray().Select(u => u.ToString())) : inventory.Username;
                inventory = inventory with
                {
                    Os = Text(data, "osdesc", inventory.Os),
                    Username = users
                };
                store.UpdateInventory(Id, inventory);
                coreReady.TrySetResult();
            }
            string response = Text(data, "responseid");
            if (response.Length > 0 && requests.TryGetValue(response, out TaskCompletionSource<JsonElement>? pending))
            {
                if (action == "msg" && Text(data, "type") is "runcommands" or "meshall-consent")
                {
                    pending.TrySetResult(data);
                }
                else if (action is "getsysinfo" or "netinfo" or "sysinfo")
                {
                    pending.TrySetResult(data);
                }
            }
            return;
        }
        if (bytes.Length < 2)
        {
            return;
        }

        ushort command = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if (command == 11)
        {
            (byte[] Bytes, byte[] Hash) core = cores.Build(windows, intel);
            if (bytes.Length == 52 && bytes.AsSpan(4, 48).SequenceEqual(core.Hash))
            {
                await SendAsync(NativeAgentHub.Packet(16, [0, 0]), ct);
                coreReady.TrySetResult();
            }
            else
            {
                await SendAsync(NativeAgentHub.Packet(10, [0, 0], core.Hash, core.Bytes), ct);
            }
        }
        else if (command == 12)
        {
            await SendAsync(NativeAgentHub.Packet(11, [0, 0]), ct);
        }
    }
    private async Task<JsonElement> RequestAsync(object value, CancellationToken ct, string? id = null)
    {
        await coreReady.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        id ??= Guid.NewGuid().ToString("N");
        System.Text.Json.Nodes.JsonObject? command = JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
        command["responseid"] = id;
        TaskCompletionSource<JsonElement> completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id] = completion;
        try
        {
            await SendJsonAsync(command, ct);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(70), ct);
        }
        finally
        {
            requests.TryRemove(id, out _);
        }
    }
    public async Task<object> ExecuteAsync(string operation, object payload, CancellationToken ct)
    {
        if (operation == "command")
        {
            return await CommandAsync((CommandInput)payload, ct);
        }

        if (operation == "inventory.refresh")
        {
            await coreReady.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await SendJsonAsync(new
            {
                action = "meshall-inventory"
            }, ct);
            return "Inventory refresh requested";
        }
        if (operation == "processes")
        {
            CommandOutput? result = await CommandAsync(new(windows ? "Get-Process | Select-Object Id,ProcessName,WorkingSet64 | ConvertTo-Json -Compress" : "ps -eo pid=,rss=,comm="), ct);
            if (windows)
            {
                JsonElement json = JsonSerializer.Deserialize<JsonElement>(result.Output);
                return (json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToArray() : [json]).Select(p => new ProcessInfo((int)Number(p, "Id"), Text(p, "ProcessName"), Number(p, "WorkingSet64"))).ToArray();
            }
            return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 3 && int.TryParse(p[0], out _) && long.TryParse(p[1], out _)).Select(p => new ProcessInfo(int.Parse(p[0]), p[2], long.Parse(p[1]) * 1024)).ToArray();
        }
        if (operation.StartsWith("files.", StringComparison.Ordinal))
        {
            return await FilesAsync(operation, (FileInput)payload, ct);
        }

        if (operation == "power")
        {
            PowerInput input = (PowerInput)payload;
            await SendJsonAsync(new
            {
                action = "poweraction",
                actiontype = input.Action == "restart" ? 3 : 2
            }, ct);
            return "Power action sent";
        }
        if (operation == "wake")
        {
            await SendJsonAsync(new
            {
                action = "wakeonlan",
                macs = new[] {
                    ((WakeInput)payload).Mac
                }
            }, ct);
            return "Wake packet requested";
        }
        throw new IOException("Unsupported native operation: " + operation);
    }
    private async Task<CommandOutput> CommandAsync(CommandInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Command) || input.Command.Length > 16000)
        {
            throw new IOException("Enter a command up to 16000 characters.");
        }

        string marker = "__MESHALL_" + Guid.NewGuid().ToString("N") + "__";
        string directory = input.Directory ?? "";
        string script = NativeCommandBuilder.Build(input, windows, marker);
        JsonElement reply = await RequestAsync(new
        {
            action = "runcommands",
            type = windows ? 2 : 3,
            cmds = script,
            reply = true,
            runAsUser = 0
        }, ct);
        string output = Text(reply, "result");
        int at = output.LastIndexOf(marker, StringComparison.Ordinal);
        int code = -1;
        if (at >= 0)
        {
            string[] lines = output[(at + marker.Length)..].Trim().Split('\n', StringSplitOptions.TrimEntries);
            if (int.TryParse(lines[0], out int parsed))
            {
                code = parsed;
            }

            if (lines.Length > 1)
            {
                directory = lines[1];
            }

            output = output[..at].TrimEnd();
        }
        bool truncated = output.Length > 150000;
        if (truncated)
        {
            output = output[..150000] + "\n[Output truncated]";
        }

        return new(output, code, directory, truncated);
    }
    public async Task<NativeTunnelHub.Tunnel> OpenTunnelAsync(int protocol, SessionRequest request, CancellationToken ct)
    {
        await coreReady.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        ScreenConsentApproval? approved = null;
        if (ScreenConsent.IsScreen(protocol, request) && (request.RequireConsent || (Consent & 8) != 0))
        {
            string nonce = Guid.NewGuid().ToString("N");
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                JsonElement approval = await RequestAsync(new
                {
                    action = "msg",
                    type = "meshall-consent",
                    nonce,
                    protocol = request.Kind,
                    sessionid = request.SessionId,
                    rights = uint.MaxValue,
                    username = request.Actor,
                    realname = request.Actor,
                    viewOnly = request.ViewOnly
                }, deadline.Token);
                approved = ScreenConsent.Validate(approval, request, nonce, windows);
                store.Audit(request.Actor, "Screen approval granted", inventory.Hostname, request.Kind);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ScreenConsentException("consent-timeout");
            }
            catch (IOException error) when (error is not ScreenConsentException)
            {
                throw new ScreenConsentException("consent-unavailable");
            }
            finally
            {
                if (approved is null)
                {
                    try
                    {
                        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
                        await SendJsonAsync(new
                        {
                            action = "msg",
                            type = "meshall-consent-cancel",
                            nonce,
                            sessionid = request.SessionId
                        }, cleanup.Token);
                    }
                    catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException) { }
                }
            }
        }
        return await tunnels.OpenAsync(this, protocol, request, ct, approved);
    }
    public async Task<AgentGateway.RemoteStream> OpenStreamAsync(SessionRequest request, CancellationToken ct)
    {
        int protocol = request.Port > 0 ? 0 : request.Kind == "powershell" ? 6 : 1;
        NativeTunnelHub.Tunnel? tunnel = await OpenTunnelAsync(protocol, request, ct);
        Channel<byte[]> channel = Channel.CreateBounded<byte[]>(64);
        CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Read()
        {
            try
            {
                await foreach (NativeAgentHub.Frame? frame in tunnel.Reader.ReadAllAsync(lifetime.Token))
                {
                    if (frame.Bytes.Length > 0 && frame.Bytes[0] == '{')
                    {
                        try
                        {
                            JsonElement json = JsonSerializer.Deserialize<JsonElement>(frame.Bytes);
                            if (json.TryGetProperty("ctrlChannel", out _))
                            {
                                if (Text(json, "type") == "ping")
                                {
                                    await tunnel.TextAsync("{\"ctrlChannel\":\"102938\",\"type\":\"pong\"}", lifetime.Token);
                                }

                                continue;
                            }
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    await channel.Writer.WriteAsync(frame.Bytes, lifetime.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }
        Task? reading = Read();
        return AgentGateway.RemoteStream.External(request.SessionId, channel.Reader, (bytes, token) => tunnel.SendAsync(bytes, false, token), async () =>
{
    await lifetime.CancelAsync();
    await tunnel.DisposeAsync();
    await reading;
    lifetime.Dispose();
}, (cols, rows, token) => tunnel.TextAsync(JsonSerializer.Serialize(new
{
    ctrlChannel = "102938",
    type = "termsize",
    cols,
    rows
}), token));
    }
    private static string Text(JsonElement root, string key, string fallback = "") => root.TryGetProperty(key, out JsonElement value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString() : fallback;

    private static long Number(JsonElement root, string key) => root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) ? n : 0;

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await heartbeat;
        foreach (TaskCompletionSource<JsonElement>? request in requests.Values)
        {
            request.TrySetException(new IOException("Native agent disconnected."));
        }

        stop.Dispose();
    }
}
