using System.Net;
using System.Net.Sockets;
using MeshAll.Agent;
using MeshAll.Server.Services;
using System.Threading.Channels;
using Xunit;

namespace MeshAll.Tests;

public sealed class ManagedStreamReliabilityTests
{
    [Fact]
    public async Task ConcurrentServerStreamDisposalWaitsForTheSameCleanup()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int closes = 0;
        AgentGateway.RemoteStream stream = AgentGateway.RemoteStream.External("fixture", Channel.CreateUnbounded<byte[]>().Reader,
            (_, _) => Task.CompletedTask, async () =>
            {
                Interlocked.Increment(ref closes);
                started.TrySetResult();
                await release.Task;
            }, (_, _, _) => Task.CompletedTask);
        Task first = stream.DisposeAsync().AsTask();
        await started.Task;
        Task second = stream.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task RepeatedTcpStreamCleanupIsSafeAndClosesThePeer()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        StreamSession session = await StreamSession.TcpAsync("fixture", "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port, _ => Task.CompletedTask, timeout.Token);
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        await session.DisposeAsync();
        await session.DisposeAsync();
        byte[] bytes = new byte[1];
        Assert.Equal(0, await peer.GetStream().ReadAsync(bytes, timeout.Token));
    }

    [Fact]
    public async Task APreCanceledTerminalRequestDoesNotStartAPlatformShell()
    {
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StreamSession.TerminalAsync("canceled", false, _ => Task.CompletedTask, canceled.Token));
    }
}
