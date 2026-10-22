using System.Buffers.Binary;
using System.IO.Compression;
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
    public string ServerId { get; }
    public NativeAgentHub(Store store,AgentGateway gateway,NativeCoreBuilder cores,NativeTunnelHub tunnels,IConfiguration configuration,ILogger<NativeAgentHub> logger)
    {
        this.store=store;this.gateway=gateway;this.cores=cores;this.tunnels=tunnels;this.configuration=configuration;this.logger=logger;
        var path=Path.Combine(store.DataDirectory,"agent-identity.pfx");
        if(File.Exists(path))identity=X509CertificateLoader.LoadPkcs12FromFile(path,null,X509KeyStorageFlags.Exportable);
        else
        {
            using var key=RSA.Create(3072);var request=new CertificateRequest("CN=MeshAll Agent Server",key,HashAlgorithmName.SHA384,RSASignaturePadding.Pkcs1);
            using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(path,certificate.Export(X509ContentType.Pfx));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            identity=X509CertificateLoader.LoadPkcs12FromFile(path,null,X509KeyStorageFlags.Exportable);
        }
        using var rsa=identity.GetRSAPublicKey()!;ServerId=Convert.ToHexStringLower(SHA384.HashData(rsa.ExportRSAPublicKey()));
    }
    public string Policy(string token)
    {
        var group=store.NativeEnrollmentGroup(token);var url=store.Setting("publicUrl",configuration["MeshAll:PublicUrl"]??"https://localhost:5188").TrimEnd('/');
        return "MeshName="+group.Name.Replace('\r',' ').Replace('\n',' ')+"\r\nMeshType=2\r\nMeshID=0x"+Store.NativeMeshKey(token)+"\r\nServerID="+ServerId+"\r\nMeshServer="+url.Replace("https://","wss://",StringComparison.Ordinal).Replace("http://","ws://",StringComparison.Ordinal)+"/agent.ashx\r\ndisplayName=MeshAll Agent\r\ndescription=MeshAll remote-management agent\r\ncompanyName=MeshAll\r\nmeshServiceName=MeshAllAgent\r\nfileName=MeshAllAgent\r\n";
    }
    public byte[] Package(string token,string platform)
    {
        if(platform is not("win-x64" or "win-arm64" or "linux-x64" or "linux-arm64"))throw new InvalidOperationException("Unsupported native agent platform.");
        var policy=Policy(token);var name=platform.StartsWith("win",StringComparison.Ordinal)?"MeshAllAgent.exe":"MeshAllAgent";
        var path=Path.Combine(AppContext.BaseDirectory,"NativeBinaries",platform,name);
        if(!File.Exists(path))throw new InvalidOperationException("The native agent package is not available in this build.");
        using var buffer=new MemoryStream();
        using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true))
        {
            using(var output=zip.CreateEntry(name,CompressionLevel.Fastest).Open())using(var input=File.OpenRead(path))input.CopyTo(output);
            using(var writer=new StreamWriter(zip.CreateEntry("MeshAllAgent.msh").Open(),new UTF8Encoding(false)))writer.Write(policy);
            using(var writer=new StreamWriter(zip.CreateEntry("README.txt").Open()))writer.Write("MeshAll Agent\n\nKeep the executable and MeshAllAgent.msh together.\nWindows: run MeshAllAgent.exe and choose Install / Update.\nLinux: chmod +x MeshAllAgent; sudo ./MeshAllAgent -fullinstall\n\nThe C/C++ agent technology retains its upstream Apache-2.0 license.\nMeshAll orchestration and UI are MIT licensed.\n");
        }
        return buffer.ToArray();
    }
    public async Task AcceptAsync(HttpContext context)
    {
        if(!context.WebSockets.IsWebSocketRequest){context.Response.StatusCode=400;return;}
        using var socket=await context.WebSockets.AcceptWebSocketAsync();using var sendLock=new SemaphoreSlim(1);
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var nonce=RandomNumberGenerator.GetBytes(48);byte[]? agentNonce=null,seenHash=null,signature=null,info=null;X509Certificate2? certificate=null;NativeAgentPeer? peer=null;
        var received=new HashSet<ushort>();var cached=false;var authenticated=false;
        async Task Send(byte[] bytes)=>await socket.SendAsync(bytes.AsMemory(),WebSocketMessageType.Binary,true,lifetime.Token);
        try
        {
            using var opening=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);opening.CancelAfter(TimeSpan.FromSeconds(30));
            while(socket.State==WebSocketState.Open)
            {
                var frame=await ReadAsync(socket,authenticated?lifetime.Token:opening.Token);if(frame is null)break;
                if(authenticated){await peer!.ReceiveAsync(frame.Bytes,lifetime.Token);continue;}
                if(frame.Bytes.Length<2||frame.Bytes[0]=='{')continue;
                var command=BinaryPrimitives.ReadUInt16BigEndian(frame.Bytes);
                if(command is 1 or 2 or 3 or 4&&!received.Add(command))throw new IOException("Duplicate native authentication command.");
                switch(command)
                {
                    case 1:
                        if(frame.Bytes.Length!=98)throw new IOException("Invalid agent challenge.");
                        seenHash=frame.Bytes[2..50];agentNonce=frame.Bytes[50..98];
                        var tlsPath=configuration["Kestrel:Certificates:Default:Path"]??configuration["MeshAll:CertificatePath"];
                        if(string.IsNullOrEmpty(tlsPath))throw new IOException("Native agent connections require the configured HTTPS certificate.");
                        using(var web=X509CertificateLoader.LoadPkcs12FromFile(tlsPath,configuration["Kestrel:Certificates:Default:Password"]??configuration["MeshAll:CertificatePassword"]))
                        {
                            using var webKey=web.GetRSAPublicKey();
                            if(!CryptographicOperations.FixedTimeEquals(seenHash,SHA384.HashData(web.RawData))&&(webKey is null||!CryptographicOperations.FixedTimeEquals(seenHash,SHA384.HashData(webKey.ExportRSAPublicKey()))))throw new IOException("Agent TLS channel binding did not match this server.");
                        }
                        await Send(Packet(1,seenHash,nonce));
                        if(!cached){using var signing=identity.GetRSAPrivateKey()!;var proof=signing.SignData([..seenHash,..agentNonce,..nonce],HashAlgorithmName.SHA384,RSASignaturePadding.Pkcs1);var der=identity.RawData;var length=new byte[2];BinaryPrimitives.WriteUInt16BigEndian(length,(ushort)der.Length);await Send(Packet(2,length,der,proof));}
                        break;
                    case 2:
                        if(frame.Bytes.Length<4)throw new IOException("Missing agent certificate.");
                        var size=BinaryPrimitives.ReadUInt16BigEndian(frame.Bytes.AsSpan(2));if(size<64||frame.Bytes.Length<4+size)throw new IOException("Invalid agent certificate.");
                        certificate=X509CertificateLoader.LoadCertificate(frame.Bytes.AsSpan(4,size));signature=frame.Bytes[(4+size)..];break;
                    case 3:if(frame.Bytes.Length<70)throw new IOException("Invalid native agent information.");info=frame.Bytes;break;
                    case 4:cached=true;break;
                }
                if(agentNonce is null||seenHash is null||certificate is null||signature is null||info is null)continue;
                using var publicKey=certificate.GetRSAPublicKey()??throw new IOException("Agent identity is not RSA.");
                var raw=signature;
                if(raw.Length!=publicKey.KeySize/8){var cms=new SignedCms();cms.Decode(raw);if(cms.SignerInfos.Count!=1)throw new IOException("Invalid agent signature envelope.");raw=cms.SignerInfos[0].GetSignature();}
                byte[] challenge=[..seenHash,..nonce,..agentNonce];
                if(!publicKey.VerifyData(challenge,raw,HashAlgorithmName.SHA384,RSASignaturePadding.Pkcs1))throw new IOException("Agent identity proof was invalid.");
                var architecture=checked((int)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(6)));var version=BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(10));var platform=BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(14));
                var name=info.Length>=72?Encoding.UTF8.GetString(info,72,Math.Min(BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(70)),info.Length-72)):"New device";
                if(string.IsNullOrWhiteSpace(name))name="New device";
                var windows=platform==1;var inventory=new Inventory(name,windows?"Windows":platform==3?"macOS":"Linux",windows?"Windows":"Unix","Native "+architecture,"Agent service",context.Connection.RemoteIpAddress?.ToString()??"","",0,0,0,0,0,version.ToString(),["desktop","desktop-native","terminal","files","command","tcp","power"],[]);
                var fingerprint=Convert.ToHexStringLower(SHA384.HashData(publicKey.ExportRSAPublicKey()));
                var device=store.RegisterNative(fingerprint,Convert.ToHexStringLower(info[18..66]),name,architecture,inventory);
                peer=new NativeAgentPeer(device.Id,socket,sendLock,store,cores,tunnels,configuration,windows,architecture,inventory);
                gateway.RegisterNative(peer);authenticated=true;
                await Send(Packet(4));await Send(Packet(11,[0,0]));
            }
        }
        catch(Exception ex)when(ex is IOException or WebSocketException or OperationCanceledException or CryptographicException or ArgumentException or UnauthorizedAccessException){logger.LogInformation("Native agent connection ended: {Reason}",ex.Message);}
        finally{certificate?.Dispose();if(peer is not null){gateway.UnregisterNative(peer);await peer.DisposeAsync();}}
    }
    public sealed record Frame(byte[] Bytes,bool Text);
    public static async Task<Frame?> ReadAsync(WebSocket socket,CancellationToken ct)
    {
        using var stream=new MemoryStream();var buffer=new byte[65536];ValueWebSocketReceiveResult result;
        do{result=await socket.ReceiveAsync(buffer.AsMemory(),ct);if(result.MessageType==WebSocketMessageType.Close)return null;if(stream.Length+result.Count>Wire.MaxMessageBytes)throw new IOException("Native frame exceeds the size limit.");stream.Write(buffer,0,result.Count);}while(!result.EndOfMessage);
        return new(stream.ToArray(),result.MessageType==WebSocketMessageType.Text);
    }
    public static byte[] Packet(ushort command,params byte[][] parts){var result=new byte[2+parts.Sum(p=>p.Length)];BinaryPrimitives.WriteUInt16BigEndian(result,command);var offset=2;foreach(var part in parts){part.CopyTo(result,offset);offset+=part.Length;}return result;}
}
