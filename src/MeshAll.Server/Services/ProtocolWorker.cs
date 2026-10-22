using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

// Standard RDP/SSH codecs are provided by an optional Apache Guacamole/FreeRDP
// worker. Authentication, device routing, policy and agent transport are MeshAll.
public sealed class ProtocolWorker(IConfiguration configuration)
{
    public sealed class Session(TcpClient client, TcpListener listener, Task forwarding, CancellationTokenSource stop) : IAsyncDisposable
    {
        public NetworkStream Stream { get; } = client.GetStream();
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync(); client.Dispose(); listener.Stop();
            try { await forwarding; } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            stop.Dispose();
        }
    }
    public async Task<Session> OpenAsync(string protocol, JsonElement options, AgentGateway.RemoteStream remote, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Forward()
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token); peer.NoDelay = true;
            await using var stream = peer.GetStream();
            async Task Input() { var buffer = new byte[32768]; int count; while ((count = await stream.ReadAsync(buffer, stop.Token)) > 0) await remote.WriteAsync(buffer[..count], stop.Token); }
            async Task Output() { await foreach (var bytes in remote.Reader.ReadAllAsync(stop.Token)) await stream.WriteAsync(bytes, stop.Token); }
            var input = Input(); var output = Output(); await Task.WhenAny(input, output); await stop.CancelAsync();
            try { await Task.WhenAll(input, output); } catch (OperationCanceledException) { }
        }
        var forwarding = Forward(); var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(configuration["MeshAll:ProtocolWorkerHost"] ?? "127.0.0.1", configuration.GetValue("MeshAll:ProtocolWorkerPort", 4822), ct);
            var stream = client.GetStream();
            await WriteAsync(stream, ["select", protocol], ct);
            var args = await ReadAsync(stream, ct);
            if (args[0] != "args") throw new IOException("Protocol worker: " + string.Join(" ", args.Skip(1)));
            var width = Integer(options, "width", 1280, 320, 7680); var height = Integer(options, "height", 800, 240, 4320);
            await WriteAsync(stream, ["size", width.ToString(), height.ToString(), "96"], ct);
            await WriteAsync(stream, ["audio"], ct); await WriteAsync(stream, ["video"], ct);
            await WriteAsync(stream, ["image", "image/png", "image/jpeg", "image/webp"], ct);
            var values = new Dictionary<string, string>
            {
                ["hostname"] = "127.0.0.1", ["port"] = port.ToString(), ["username"] = Text(options, "username"), ["password"] = Text(options, "password"),
                ["domain"] = Text(options, "domain"), ["private-key"] = Text(options, "privateKey"), ["passphrase"] = Text(options, "password"),
                ["width"] = width.ToString(), ["height"] = height.ToString(), ["dpi"] = "96", ["color-depth"] = "24",
                ["ignore-cert"] = "true", ["security"] = "any", ["resize-method"] = "display-update", ["enable-wallpaper"] = "false",
                ["enable-drive"] = "false", ["disable-audio"] = "true", ["font-name"] = "monospace", ["font-size"] = "12", ["enable-sftp"] = "false"
            };
            await WriteAsync(stream, new[] { "connect" }.Concat(args.Skip(1).Select(name => name.StartsWith("VERSION_", StringComparison.Ordinal) ? name : values.GetValueOrDefault(name, ""))).ToArray(), ct);
            var ready = await ReadAsync(stream, ct);
            if (ready[0] != "ready") throw new IOException("Remote protocol setup failed: " + string.Join(" ", ready.Skip(1)));
            // The exact-length handshake reader leaves graphical bytes in the
            // socket for the subsequent transparent transport.
            return new(client, listener, forwarding, stop);
        }
        catch
        {
            await stop.CancelAsync(); listener.Stop(); client.Dispose();
            try { await forwarding; } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            stop.Dispose(); throw;
        }
    }
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int Integer(JsonElement root, string name, int fallback, int min, int max) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? Math.Clamp(n, min, max) : fallback;
    public static async Task WriteAsync(Stream stream, string[] instruction, CancellationToken ct)
    {
        var value = string.Join(',', instruction.Select(part => part.Length + "." + part)) + ";";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct); await stream.FlushAsync(ct);
    }
    private static async Task<string[]> ReadAsync(Stream reader, CancellationToken ct)
    {
        var result = new List<string>(); var one = new byte[1];
        while (result.Count < 512)
        {
            var length = 0;
            while (true) { if (await reader.ReadAsync(one, ct) != 1) throw new IOException("Protocol worker closed the connection."); if (one[0] == '.') break; if (one[0] is < 48 or > 57 || length > 100000) throw new IOException("Invalid protocol instruction."); length = checked(length * 10 + one[0] - '0'); }
            var text = new byte[length]; var offset = 0;
            while (offset < length) { var count = await reader.ReadAsync(text.AsMemory(offset), ct); if (count == 0) throw new IOException("Protocol worker closed the connection."); offset += count; }
            result.Add(Encoding.UTF8.GetString(text));
            if (await reader.ReadAsync(one, ct) != 1) throw new IOException("Protocol worker closed the connection.");
            if (one[0] == ';') return result.ToArray(); if (one[0] != ',') throw new IOException("Invalid protocol instruction separator.");
        }
        throw new IOException("Protocol instruction has too many arguments.");
    }
}
