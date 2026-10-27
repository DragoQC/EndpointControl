using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshAll.Tests;

public sealed class NativeTunnelReliabilityTests
{
    private sealed class FixtureSocket(Action<byte[]>? sent = null, bool failWrites = false) : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        private readonly TaskCompletionSource<WebSocketReceiveResult> reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Aborted
        {
            get; private set;
        }
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort()
        {
            Aborted = true;
            state = WebSocketState.Aborted;
            reading.TrySetResult(new(0, WebSocketMessageType.Close, true));
        }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => reading.Task.WaitAsync(cancellationToken);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (failWrites)
            {
                return Task.FromException(new WebSocketException("Fixture negotiation failure"));
            }
            sent?.Invoke(buffer.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class DelayedAcceptance : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WebSocket> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            Requested.TrySetResult();
            return Accepted.Task;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanceledOrFailedTunnelSetupCannotLeaveAnAcceptedAgentSocketAlive(bool cancelBeforeAccept)
    {
        using TestServer server = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource cancellation = new();
        string certificatePath = Path.Combine(server.Store.DataDirectory, "tunnel-fixture.pfx");
        using RSA key = RSA.Create(2048);
        CertificateRequest certificateRequest = new("CN=Tunnel fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx), deadline.Token);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MeshAll:CertificatePath"] = certificatePath
        }).Build();
        TaskCompletionSource<string> relay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FixtureSocket control = new(bytes => relay.TrySetResult(JsonSerializer.Deserialize<JsonElement>(bytes).GetProperty("value").GetString()!));
        NativeTunnelHub hub = server.Services.GetRequiredService<NativeTunnelHub>();
        Inventory inventory = new("Fixture", "Windows", "Windows", "x64", "user", "192.0.2.9", "", 1, 100, 20, 0, 0, "test", [], []);
        using SemaphoreSlim send = new(1);
        await using NativeAgentPeer peer = new("fixture", control, send, server.Store,
            server.Services.GetRequiredService<NativeCoreBuilder>(), hub, configuration, true, 2, inventory);
        Task<NativeTunnelHub.Tunnel> opening = hub.OpenAsync(peer, 0, new("session", "Actor", false), cancellation.Token);
        string uri = await relay.Task.WaitAsync(deadline.Token);
        DefaultHttpContext context = new();
        context.Request.QueryString = new(uri[uri.IndexOf('?')..]);
        DelayedAcceptance acceptance = new();
        context.Features.Set<IHttpWebSocketFeature>(acceptance);
        Task accepting = hub.AcceptAsync(context);
        await acceptance.Requested.Task.WaitAsync(deadline.Token);
        using FixtureSocket socket = new(failWrites: !cancelBeforeAccept);
        if (cancelBeforeAccept)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(deadline.Token));
            acceptance.Accepted.TrySetResult(socket);
        }
        else
        {
            acceptance.Accepted.TrySetResult(socket);
            await Assert.ThrowsAsync<WebSocketException>(() => opening.WaitAsync(deadline.Token));
        }
        await accepting.WaitAsync(deadline.Token);
        Assert.True(socket.Aborted);
    }
}
