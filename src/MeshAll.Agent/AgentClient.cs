using System.Collections.Concurrent;
using System.Net.WebSockets;
using MeshAll.Shared;

namespace MeshAll.Agent;

public sealed class AgentClient(AgentSettings settings)
{
    public async Task RunAsync(CancellationToken stop)
    {
        int retry = 1;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using ClientWebSocket socket = settings.Socket();
                UriBuilder builder = new(settings.Server + "/api/agents/connect")
                {
                    Scheme = new Uri(settings.Server).Scheme == "https" ? "wss" : "ws"
                };
                await socket.ConnectAsync(builder.Uri, stop);
                retry = 1;
                Console.WriteLine("EndpointControl Agent connected.");
                using SemaphoreSlim sendLock = new(1);
                using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);

                Task Send(AgentMessage message) => Wire.SendAsync(socket, message, sendLock, lifetime.Token);
                await using AgentOperations operations = new(settings, Send);
                ConcurrentDictionary<string, (CancellationTokenSource Stop, Task Work)> requests = new();
                Task inventory = InventoryReader.RunAsync(settings, Send, lifetime.Token);
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        AgentMessage? message = await Wire.ReceiveAsync(socket, lifetime.Token);
                        if (message is null)
                        {
                            break;
                        }

                        if (message.Kind == "cancel" && message.Id is not null && requests.TryGetValue(message.Id, out (CancellationTokenSource Stop, Task Work) request))
                        {
                            try
                            {
                                request.Stop.Cancel();
                            }
                            catch (ObjectDisposedException)
                            {
                                // The request completed between lookup and cancellation.
                            }
                            continue;
                        }
                        if (message.Kind == "stream")
                        {
                            await operations.WriteStreamAsync(Wire.Read<SessionInput>(message.Payload), lifetime.Token);
                            continue;
                        }
                        if (message.Kind != "request" || message.Id is null || requests.Count >= 16)
                        {
                            continue;
                        }

                        CancellationTokenSource requestStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        string id = message.Id;
                        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!requests.TryAdd(id, (requestStop, finished.Task)))
                        {
                            requestStop.Dispose();
                            continue;
                        }

                        async Task Execute()
                        {
                            AgentMessage result;
                            try
                            {
                                object payload = await operations.ExecuteAsync(message.Operation ?? "", message.Payload, requestStop.Token);
                                result = new("result", id, Payload: Wire.Element(payload));
                            }
                            catch (Exception ex)
                            {
                                result = new("result", id, Success: false, Error: ex is OperationCanceledException ? "Request canceled or timed out." : ex.Message);
                            }
                            try
                            {
                                await Send(result);
                            }
                            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                            {
                            }
                            finally
                            {
                                requests.TryRemove(id, out _);
                                requestStop.Dispose();
                                finished.TrySetResult();
                            }
                        }
                        _ = Task.Run(Execute, CancellationToken.None);
                    }
                }
                finally
                {
                    await lifetime.CancelAsync();
                    try
                    {
                        await Task.WhenAll(inventory, Task.WhenAll(requests.Values.Select(request => request.Work)));
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("EndpointControl connection ended: " + ex.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retry++ * 2)), stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
