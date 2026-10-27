using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed class NativeAgentHub
{
    private readonly Store store;
    private readonly AgentGateway gateway;
    private readonly NativeCoreBuilder cores;
    private readonly NativeTunnelHub tunnels;
    private readonly IConfiguration configuration;
    private readonly ILogger<NativeAgentHub> logger;
    private readonly X509Certificate2 identity;

    public string ServerId
    {
        get;
    }
    public NativeAgentHub(Store store, AgentGateway gateway, NativeCoreBuilder cores, NativeTunnelHub tunnels, IConfiguration configuration, ILogger<NativeAgentHub> logger)
    {
        this.store = store;
        this.gateway = gateway;
        this.cores = cores;
        this.tunnels = tunnels;
        this.configuration = configuration;
        this.logger = logger;
        string path = Path.Combine(store.DataDirectory, "agent-identity.pfx");
        if (File.Exists(path))
        {
            identity = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
        }
        else
        {
            using RSA key = RSA.Create(3072);
            CertificateRequest request = new CertificateRequest("CN=EndpointControl Agent Server", key, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
            using X509Certificate2? certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            identity = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
        }
        using RSA? rsa = identity.GetRSAPublicKey()!;
        ServerId = Convert.ToHexStringLower(SHA384.HashData(rsa.ExportRSAPublicKey()));
    }
    public string Policy(string token)
    {
        GroupInfo? group = store.NativeEnrollmentGroup(token);
        string url = store.Setting("publicUrl", configuration["MeshAll:PublicUrl"] ?? "https://localhost:5188").TrimEnd('/');
        return "MeshName=" + group.Name.Replace('\r', ' ').Replace('\n', ' ') + "\r\nMeshType=2\r\nMeshID=0x" + Store.NativeMeshKey(token) + "\r\nServerID=" + ServerId + "\r\nMeshServer=" + url.Replace("https://", "wss://", StringComparison.Ordinal).Replace("http://", "ws://", StringComparison.Ordinal) + "/agent.ashx\r\ndisplayName=EndpointControl Agent\r\ndescription=EndpointControl remote-management agent\r\ncompanyName=EndpointControl\r\nmeshServiceName=MeshAllAgent\r\nfileName=MeshAllAgent\r\n";
    }
    public static readonly string[] Platforms = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

    public string[] AvailablePlatforms => Platforms.Where(p => File.Exists(BinaryPath(p))).ToArray();

    private static string BinaryPath(string platform) => Path.Combine(AppContext.BaseDirectory, "NativeBinaries", platform, platform.StartsWith("win", StringComparison.Ordinal) ? "MeshAllAgent.exe" : "MeshAllAgent");

    public string LicenseNotices() => NativeAgentExecutable.LicenseNotices();

    public byte[] Executable(string token, string platform)
    {
        if (!Platforms.Contains(platform, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Unsupported native agent platform.");
        }

        string policy = Policy(token) + "InstallFlags=2\r\n";
        string path = BinaryPath(platform);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The native executable is unavailable in this build.");
        }

        return NativeAgentExecutable.Build(File.ReadAllBytes(path), policy, LicenseNotices());
    }
    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        using WebSocket? socket = await context.WebSockets.AcceptWebSocketAsync();
        using SemaphoreSlim sendLock = new SemaphoreSlim(1);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        byte[] nonce = RandomNumberGenerator.GetBytes(48);
        byte[]? agentNonce = null, seenHash = null, signature = null, info = null;
        X509Certificate2? certificate = null;
        NativeAgentPeer? peer = null;
        HashSet<ushort> received = new HashSet<ushort>();
        bool cached = false;
        bool authenticated = false;

        async Task Send(byte[] bytes) => await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, lifetime.Token);
        try
        {
            using CancellationTokenSource opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            opening.CancelAfter(TimeSpan.FromSeconds(30));
            while (socket.State == WebSocketState.Open)
            {
                Frame? frame = await ReadAsync(socket, authenticated ? lifetime.Token : opening.Token, authenticated ? Wire.MaxMessageBytes : 16384);
                if (frame is null)
                {
                    break;
                }

                if (authenticated)
                {
                    await peer!.ReceiveAsync(frame.Bytes, lifetime.Token);
                    continue;
                }
                if (frame.Bytes.Length < 2 || frame.Bytes[0] == '{')
                {
                    continue;
                }

                ushort command = BinaryPrimitives.ReadUInt16BigEndian(frame.Bytes);
                if (command is 1 or 2 or 3 or 4 && !received.Add(command))
                {
                    throw new IOException("Duplicate native authentication command.");
                }

                switch (command)
                {
                    case 1:
                        if (frame.Bytes.Length != 98)
                        {
                            throw new IOException("Invalid agent challenge.");
                        }

                        seenHash = frame.Bytes[2..50];
                        agentNonce = frame.Bytes[50..98];
                        string? tlsPath = configuration["Kestrel:Certificates:Default:Path"] ?? configuration["MeshAll:CertificatePath"];
                        if (string.IsNullOrEmpty(tlsPath))
                        {
                            throw new IOException("Native agent connections require the configured HTTPS certificate.");
                        }

                        using (X509Certificate2? web = X509CertificateLoader.LoadPkcs12FromFile(tlsPath, configuration["Kestrel:Certificates:Default:Password"] ?? configuration["MeshAll:CertificatePassword"]))
                        {
                            using RSA? webKey = web.GetRSAPublicKey();
                            if (!CryptographicOperations.FixedTimeEquals(seenHash, SHA384.HashData(web.RawData)) && (webKey is null || !CryptographicOperations.FixedTimeEquals(seenHash, SHA384.HashData(webKey.ExportRSAPublicKey()))))
                            {
                                throw new IOException("Agent TLS channel binding did not match this server.");
                            }
                        }
                        await Send(Packet(1, seenHash, nonce));
                        if (!cached)
                        {
                            using RSA? signing = identity.GetRSAPrivateKey()!;
                            byte[] proof = signing.SignData([.. seenHash, .. agentNonce, .. nonce], HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
                            byte[] der = identity.RawData;
                            byte[] length = new byte[2];
                            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)der.Length);
                            await Send(Packet(2, length, der, proof));
                        }
                        break;
                    case 2:
                        if (frame.Bytes.Length < 4)
                        {
                            throw new IOException("Missing agent certificate.");
                        }

                        ushort size = BinaryPrimitives.ReadUInt16BigEndian(frame.Bytes.AsSpan(2));
                        if (size < 64 || frame.Bytes.Length < 4 + size)
                        {
                            throw new IOException("Invalid agent certificate.");
                        }

                        certificate = X509CertificateLoader.LoadCertificate(frame.Bytes.AsSpan(4, size));
                        signature = frame.Bytes[(4 + size)..];
                        break;
                    case 3:
                        if (frame.Bytes.Length < 70)
                        {
                            throw new IOException("Invalid native agent information.");
                        }

                        info = frame.Bytes;
                        break;
                    case 4:
                        cached = true;
                        break;
                }
                if (agentNonce is null || seenHash is null || certificate is null || signature is null || info is null)
                {
                    continue;
                }

                using RSA? publicKey = certificate.GetRSAPublicKey() ?? throw new IOException("Agent identity is not RSA.");
                if (publicKey.KeySize is < 2048 or > 8192)
                {
                    throw new IOException("Unsupported agent identity key size.");
                }
                byte[] raw = signature;
                if (raw.Length != publicKey.KeySize / 8)
                {
                    SignedCms cms = new SignedCms();
                    cms.Decode(raw);
                    if (cms.SignerInfos.Count != 1)
                    {
                        throw new IOException("Invalid agent signature envelope.");
                    }

                    raw = cms.SignerInfos[0].GetSignature();
                }
                byte[] challenge = [.. seenHash, .. nonce, .. agentNonce];
                if (!publicKey.VerifyData(challenge, raw, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1))
                {
                    throw new IOException("Agent identity proof was invalid.");
                }

                int architecture = checked((int)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(6)));
                uint version = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(10));
                string name = info.Length >= 72 ? Encoding.UTF8.GetString(info, 72, Math.Min(BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(70)), info.Length - 72)) : "New device";
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "New device";
                }
                // Native platformType is a device/icon category, not the OS.
                // Architecture IDs identify the executable's actual platform.
                bool windows = architecture is 1 or 2 or 3 or 4 or 21 or 22 or 34 or 42 or 43;
                string address = context.Connection.RemoteIpAddress?.ToString() ?? "";
                Inventory inventory = new Inventory(name, windows ? "Windows" : architecture is 16 or 29 ? "macOS" : "Linux", windows ? "Windows" : "Unix", NativeInventory.Architecture(architecture), "Not reported", address, "", 0, 0, 0, -1, 0, version.ToString(), ["desktop", "desktop-native", "terminal", "files", "command", "tcp", "power"], [], ConnectionAddress: address, AgentArchitecture: architecture);
                string fingerprint = Convert.ToHexStringLower(SHA384.HashData(publicKey.ExportRSAPublicKey()));
                DeviceInfo? device = store.RegisterNative(fingerprint, Convert.ToHexStringLower(info[18..66]), name, architecture, inventory);
                peer = new NativeAgentPeer(device.Id, socket, sendLock, store, cores, tunnels, configuration, windows, architecture, device.Inventory ?? inventory);
                gateway.RegisterNative(peer);
                authenticated = true;
                await Send(Packet(4));
                await Send(Packet(11, [0, 0]));
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or CryptographicException or ArgumentException or UnauthorizedAccessException or JsonException)
        {
            logger.LogInformation("Native agent connection ended: {Reason}", ex.Message);
            if (!authenticated)
            {
                context.RequestServices.GetRequiredService<SecurityEvents>().Rejected(context, "Agent authentication");
            }
        }
        finally
        {
            certificate?.Dispose();
            if (peer is not null)
            {
                gateway.UnregisterNative(peer);
                await peer.DisposeAsync();
            }
        }
    }
    public sealed record Frame(byte[] Bytes, bool Text);

    public static async Task<Frame?> ReadAsync(WebSocket socket, CancellationToken ct, int maximumBytes = Wire.MaxMessageBytes)
    {
        using MemoryStream stream = new MemoryStream();
        byte[] buffer = new byte[65536];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (stream.Length + result.Count > maximumBytes)
            {
                throw new IOException("Native frame exceeds the size limit.");
            }

            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return new(stream.ToArray(), result.MessageType == WebSocketMessageType.Text);
    }
    public static byte[] Packet(ushort command, params byte[][] parts)
    {
        byte[] result = new byte[2 + parts.Sum(p => p.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(result, command);
        int offset = 2;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
