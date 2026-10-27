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
            await stop.CancelAsync();
            client.Dispose();
            listener.Stop();
            try
            {
                await forwarding;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
            stop.Dispose();
        }
    }
    public async Task<Session> OpenAsync(string protocol, JsonElement options, AgentGateway.RemoteStream remote, RemoteHostIdentity identity, CancellationToken ct)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Forward()
        {
            using TcpClient? peer = await listener.AcceptTcpClientAsync(stop.Token);
            peer.NoDelay = true;
            await using NetworkStream? stream = peer.GetStream();

            async Task Input()
            {
                byte[] buffer = new byte[32768];
                int count;
                while ((count = await stream.ReadAsync(buffer, stop.Token)) > 0)
                {
                    await remote.WriteAsync(buffer[..count], stop.Token);
                }
            }
            async Task Output()
            {
                await foreach (byte[] bytes in remote.Reader.ReadAllAsync(stop.Token))
                {
                    await stream.WriteAsync(bytes, stop.Token);
                }
            }
            Task? input = Input();
            Task? output = Output();
            await Task.WhenAny(input, output);
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(input, output);
            }
            catch (OperationCanceledException)
            {
            }
        }
        Task? forwarding = Forward();
        TcpClient client = new TcpClient
        {
            NoDelay = true
        };
        try
        {
            using CancellationTokenSource negotiation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            negotiation.CancelAfter(TimeSpan.FromSeconds(20));
            CancellationToken setup = negotiation.Token;
            await client.ConnectAsync(configuration["MeshAll:ProtocolWorkerHost"] ?? "127.0.0.1", configuration.GetValue("MeshAll:ProtocolWorkerPort", 4822), setup);
            NetworkStream? stream = client.GetStream();
            await WriteAsync(stream, ["select", protocol], setup);
            string[] args = await ReadAsync(stream, setup);
            if (args[0] != "args")
            {
                throw new IOException("Protocol worker: " + string.Join(" ", args.Skip(1)));
            }

            int width = Integer(options, "width", 1280, 320, 7680);
            int height = Integer(options, "height", 800, 240, 4320);
            await WriteAsync(stream, ["size", width.ToString(), height.ToString(), "96"], setup);
            await WriteAsync(stream, ["audio"], setup);
            await WriteAsync(stream, ["video"], setup);
            await WriteAsync(stream, ["image", "image/png", "image/jpeg", "image/webp"], setup);
            Dictionary<string, string> values = new Dictionary<string, string>
            {
                ["hostname"] = "127.0.0.1",
                ["port"] = port.ToString(),
                ["username"] = Text(options, "username"),
                ["password"] = Text(options, "password"),
                ["domain"] = Text(options, "domain"),
                ["private-key"] = Text(options, "privateKey"),
                ["passphrase"] = Text(options, "password"),
                ["width"] = width.ToString(),
                ["height"] = height.ToString(),
                ["dpi"] = "96",
                ["color-depth"] = "24",
                ["ignore-cert"] = "false",
                ["cert-tofu"] = "false",
                ["security"] = identity.Windows ? "nla" : "tls",
                ["resize-method"] = "display-update",
                ["enable-wallpaper"] = "false",
                ["enable-drive"] = "false",
                ["disable-audio"] = "true",
                ["font-name"] = "monospace",
                ["font-size"] = "12",
                ["enable-sftp"] = "false"
            };
            if (protocol == "rdp")
            {
                values["cert-fingerprints"] = "sha256:" + string.Join(':', Enumerable.Range(0, identity.Value.Length / 2).Select(index => identity.Value.Substring(index * 2, 2)));
            }
            else
            {
                values["host-key"] = $"[127.0.0.1]:{port} {identity.Value}";
            }
            string verificationArgument = protocol == "rdp" ? "cert-fingerprints" : "host-key";
            if (!args.Contains(verificationArgument, StringComparer.Ordinal))
            {
                throw new IOException("The protocol worker does not support required host identity verification.");
            }
            await WriteAsync(stream, new[] {
                "connect"
            }.Concat(args.Skip(1).Select(name => name.StartsWith("VERSION_", StringComparison.Ordinal) ? name : values.GetValueOrDefault(name, ""))).ToArray(), setup);
            string[] ready = await ReadAsync(stream, setup);
            if (ready[0] != "ready")
            {
                throw new IOException("Remote protocol setup failed: " + string.Join(" ", ready.Skip(1)));
            }
            // The exact-length handshake reader leaves graphical bytes in the
            // socket for the subsequent transparent transport.
            return new(client, listener, forwarding, stop);
        }
        catch
        {
            await stop.CancelAsync();
            listener.Stop();
            client.Dispose();
            try
            {
                await forwarding;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
            stop.Dispose();
            throw;
        }
    }
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Integer(JsonElement root, string name, int fallback, int min, int max) => root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? Math.Clamp(n, min, max) : fallback;

    public static async Task WriteAsync(Stream stream, string[] instruction, CancellationToken ct)
    {
        string value = string.Join(',', instruction.Select(part => part.EnumerateRunes().Count() + "." + part)) + ";";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct);
        await stream.FlushAsync(ct);
    }
    private static async Task<string[]> ReadAsync(Stream reader, CancellationToken ct)
    {
        List<string> result = new List<string>();
        byte[] one = new byte[1];
        int instructionBytes = 0;
        while (result.Count < 512)
        {
            int length = 0;
            int digits = 0;
            while (true)
            {
                if (await reader.ReadAsync(one, ct) != 1)
                {
                    throw new IOException("Protocol worker closed the connection.");
                }

                if (one[0] == '.')
                {
                    break;
                }

                if (one[0] is < 48 or > 57 || length > 1638 || ++digits > 5)
                {
                    throw new IOException("Invalid protocol instruction.");
                }

                length = checked(length * 10 + one[0] - '0');
            }
            instructionBytes += length;
            if (length > 16384 || instructionBytes > 65536)
            {
                throw new IOException("Protocol instruction exceeds the size limit.");
            }
            byte[] text = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int count = await reader.ReadAsync(text.AsMemory(offset), ct);
                if (count == 0)
                {
                    throw new IOException("Protocol worker closed the connection.");
                }

                offset += count;
            }
            result.Add(Encoding.UTF8.GetString(text));
            if (await reader.ReadAsync(one, ct) != 1)
            {
                throw new IOException("Protocol worker closed the connection.");
            }

            if (one[0] == ';')
            {
                return result.ToArray();
            }

            if (one[0] != ',')
            {
                throw new IOException("Invalid protocol instruction separator.");
            }
        }
        throw new IOException("Protocol instruction has too many arguments.");
    }
}
