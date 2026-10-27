using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed class NativeTunnelHub
{
    private sealed class Pending(string secret)
    {
        public string Secret { get; } = secret;

        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddSeconds(75);

        public TaskCompletionSource<Tunnel> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Tunnel(WebSocket socket) : IAsyncDisposable
    {
        private readonly SemaphoreSlim send = new(1);

        internal Channel<NativeAgentHub.Frame> Frames { get; } = Channel.CreateBounded<NativeAgentHub.Frame>(64);

        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelReader<NativeAgentHub.Frame> Reader => Frames.Reader;

        public async Task SendAsync(byte[] bytes, bool text, CancellationToken ct)
        {
            await send.WaitAsync(ct);
            try
            {
                await socket.SendAsync(bytes.AsMemory(), text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);
            }
            finally
            {
                send.Release();
            }
        }
        public Task TextAsync(string text, CancellationToken ct) => SendAsync(Encoding.UTF8.GetBytes(text), true, ct);

        public ValueTask DisposeAsync()
        {
            socket.Abort();
            Frames.Writer.TryComplete();
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    public async Task<Tunnel> OpenAsync(NativeAgentPeer peer, int protocol, SessionRequest request, CancellationToken ct, ScreenConsentApproval? approval = null)
    {
        string id = Guid.NewGuid().ToString("N");
        string secret = Store.Secret();
        Pending slot = new Pending(secret);
        pending[id] = slot;
        bool returned = false;
        try
        {
            Dictionary<string, object?> message = new Dictionary<string, object?>
            {
                ["action"] = "msg",
                ["type"] = "tunnel",
                ["value"] = $"*/native-relay.ashx?id={id}&ticket={secret}",
                ["rights"] = protocol is 0 or 2 && request.Kind == "desktop" ? (uint)(8 | 512 | 1024 | 2048 | (request.ViewOnly ? 256 : 0)) : uint.MaxValue,
                ["consent"] = peer.Consent,
                ["userid"] = "user//" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Actor)))[..24],
                ["username"] = request.Actor,
                ["realname"] = request.Actor,
                ["sessionid"] = request.SessionId,
                ["servertlshash"] = peer.WebCertificateHash,
                ["soptions"] = new
                {
                    consentTitle = ProductBrand.AgentName,
                    notifyTitle = ProductBrand.AgentName,
                    consentTimeout = 30,
                    meshallStrictConsent = (peer.Consent & 8) != 0 || request.RequireConsent,
                    consentAutoAccept = false,
                    meshallApprovalNonce = approval?.Nonce,
                    meshallApprovalProtocol = request.Kind
                }
            };
            if (request.Port > 0)
            {
                message["tcpport"] = request.Port;
                message["tcpaddr"] = request.Host ?? "127.0.0.1";
            }
            await peer.SendJsonAsync(message, ct);
            Tunnel tunnel;
            try
            {
                tunnel = await slot.Connected.Task.WaitAsync(TimeSpan.FromSeconds(approval is null ? 75 : 15), ct);
            }
            catch (TimeoutException) when (approval is not null) { throw new ScreenConsentException("consent-unavailable"); }
            await tunnel.TextAsync("c", ct);
            if (protocol > 0)
            {
                if (protocol is 1 or 6)
                {
                    await tunnel.TextAsync("{\"ctrlChannel\":\"102938\",\"type\":\"options\",\"cols\":100,\"rows\":30}", ct);
                }

                await tunnel.TextAsync(protocol.ToString(), ct);
            }
            returned = true;
            return tunnel;
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (!returned)
            {
                if (slot.Connected.Task.IsCompletedSuccessfully)
                {
                    await slot.Connected.Task.Result.DisposeAsync();
                }
                else
                {
                    slot.Connected.TrySetCanceled();
                }
            }
        }
    }
    public async Task AcceptAsync(HttpContext context)
    {
        string id = context.Request.Query["id"].ToString();
        string ticket = context.Request.Query["ticket"].ToString();
        if (!context.WebSockets.IsWebSocketRequest || ticket.Length != 64 || !pending.TryGetValue(id, out Pending? slot)
            || slot.Expires < DateTimeOffset.UtcNow || slot.Connected.Task.IsCompleted
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ticket), Encoding.UTF8.GetBytes(slot.Secret)))
        {
            context.Response.StatusCode = 401;
            return;
        }
        using WebSocket? socket = await context.WebSockets.AcceptWebSocketAsync();
        await using Tunnel tunnel = new Tunnel(socket);
        if (!slot.Connected.TrySetResult(tunnel))
        {
            socket.Abort();
            return;
        }
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                NativeAgentHub.Frame? frame = await NativeAgentHub.ReadAsync(socket, context.RequestAborted);
                if (frame is null)
                {
                    break;
                }

                await tunnel.Frames.Writer.WriteAsync(frame, context.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
        }
        finally
        {
            tunnel.Frames.Writer.TryComplete();
        }
    }
}
