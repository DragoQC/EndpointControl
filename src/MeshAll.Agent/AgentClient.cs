using System.Collections.Concurrent;
using System.Net.WebSockets;
using MeshAll.Shared;

namespace MeshAll.Agent;

public sealed class AgentClient(AgentSettings settings)
{
    public async Task RunAsync(CancellationToken stop)
    {
        var retry = 1;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var socket = settings.Socket();
                var builder = new UriBuilder(settings.Server + "/api/agents/connect") { Scheme = new Uri(settings.Server).Scheme == "https" ? "wss" : "ws" };
                await socket.ConnectAsync(builder.Uri, stop); retry = 1;
                Console.WriteLine("MeshAll Agent connected.");
                using var sendLock = new SemaphoreSlim(1);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
                async Task Send(AgentMessage message) => await Wire.SendAsync(socket, message, sendLock, lifetime.Token);
                await using var operations = new AgentOperations(settings, Send);
                var requests = new ConcurrentDictionary<string, (CancellationTokenSource Stop, Task Work)>();
                var inventory = InventoryReader.RunAsync(settings, Send, lifetime.Token);
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var message = await Wire.ReceiveAsync(socket, lifetime.Token); if (message is null) break;
                        if (message.Kind == "cancel" && message.Id is not null && requests.TryGetValue(message.Id, out var request)) { request.Stop.Cancel(); continue; }
                        if (message.Kind == "stream") { await operations.WriteStreamAsync(Wire.Read<SessionInput>(message.Payload), lifetime.Token); continue; }
                        if (message.Kind != "request" || message.Id is null || requests.Count >= 16) continue;
                        var requestStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var id = message.Id;
                        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        requests[id] = (requestStop, finished.Task);
                        async Task Execute()
                        {
                            AgentMessage result;
                            try { var payload = await operations.ExecuteAsync(message.Operation ?? "", message.Payload, requestStop.Token); result = new("result", id, Payload: Wire.Element(payload)); }
                            catch (Exception ex) { result = new("result", id, Success: false, Error: ex is OperationCanceledException ? "Request canceled or timed out." : ex.Message); }
                            try { await Send(result); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
                            finally { requests.TryRemove(id, out _); requestStop.Dispose(); finished.TrySetResult(); }
                        }
                        _ = Task.Run(Execute, CancellationToken.None);
                    }
                }
                finally
                {
                    await lifetime.CancelAsync();
                    try { await inventory; } catch (OperationCanceledException) { }
                    await Task.WhenAll(requests.Values.Select(r => r.Work));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine("MeshAll connection ended: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retry++ * 2)), stop); } catch (OperationCanceledException) { break; }
        }
    }
}
