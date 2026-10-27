using System.Net.Sockets;
using System.Net.WebSockets;
using MeshAll.Shared;

namespace MeshAll.Agent;

public sealed class StreamSession : IAsyncDisposable
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly Func<ValueTask> close;
    private readonly Action<int, int>? resize;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim write = new(1);
    private readonly Task reading;

    private StreamSession(string id, Stream input, Stream output, Func<ValueTask> close, Action<int, int>? resize, Func<AgentMessage, Task> send)
    {
        this.input = input;
        this.output = output;
        this.close = close;
        this.resize = resize;
        reading = ReadAsync(id, send);
    }

    public static async Task<StreamSession> TcpAsync(string id, string host, int port, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        TcpClient client = new()
        {
            NoDelay = true
        };
        try
        {
            await client.ConnectAsync(host, port, ct);
            NetworkStream stream = client.GetStream();
            return new(id, stream, stream, () =>
            {
                client.Dispose();
                return ValueTask.CompletedTask;
            }, null, send);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static Task<StreamSession> TerminalAsync(string id, bool powershell, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        NativeTerminal terminal = NativeTerminal.Start(powershell);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new StreamSession(id, terminal.Input, terminal.Output, terminal.DisposeAsync, terminal.Resize, send));
    }

    private async Task ReadAsync(string id, Func<AgentMessage, Task> send)
    {
        try
        {
            byte[] buffer = new byte[32768];
            int count;
            while ((count = await output.ReadAsync(buffer, stop.Token)) > 0)
            {
                await send(new("stream", Payload: Wire.Element(new SessionInput(id, Base64: Convert.ToBase64String(buffer, 0, count)))));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                await send(new("stream.end", Id: id));
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await write.WaitAsync(ct);
        try
        {
            await input.WriteAsync(bytes, ct);
            await input.FlushAsync(ct);
        }
        finally
        {
            write.Release();
        }
    }

    public void Resize(int columns, int rows) => resize?.Invoke(Math.Clamp(columns, 20, 300), Math.Clamp(rows, 5, 150));

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await close();
        input.Dispose();
        if (!ReferenceEquals(input, output))
        {
            output.Dispose();
        }

        try
        {
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
        stop.Dispose();
    }
}
