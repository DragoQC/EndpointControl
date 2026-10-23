using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed class NativeAgentPeer : IAsyncDisposable
{
    private readonly WebSocket socket;private readonly SemaphoreSlim sendLock;private readonly Store store;private readonly NativeCoreBuilder cores;private readonly NativeTunnelHub tunnels;
    private readonly bool windows,intel;private Inventory inventory;
    private readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> requests=new();
    private readonly CancellationTokenSource stop=new();private readonly TaskCompletionSource coreReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task heartbeat;
    public string Id { get; }
    public string WebCertificateHash { get; }
    public bool Online=>socket.State==WebSocketState.Open&&!stop.IsCancellationRequested;
    public int Consent=>store.Groups().FirstOrDefault(g=>g.Id==store.FindDevice(Id)?.GroupId)?.Consent??73;
    public NativeAgentPeer(string id,WebSocket socket,SemaphoreSlim sendLock,Store store,NativeCoreBuilder cores,NativeTunnelHub tunnels,IConfiguration config,bool windows,int architecture,Inventory inventory)
    {
        Id=id;this.socket=socket;this.sendLock=sendLock;this.store=store;this.cores=cores;this.tunnels=tunnels;this.windows=windows;intel=architecture is 1 or 2 or 3 or 4 or 5 or 6 or 16 or 18 or 19 or 20;this.inventory=inventory;
        var path=config["Kestrel:Certificates:Default:Path"]??config["MeshAll:CertificatePath"]!;
        using var cert=System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(path,config["Kestrel:Certificates:Default:Password"]??config["MeshAll:CertificatePassword"]);
        WebCertificateHash=Convert.ToHexStringLower(System.Security.Cryptography.SHA384.HashData(cert.RawData));heartbeat=Heartbeat();
    }
    private async Task Heartbeat(){try{while(!stop.IsCancellationRequested){await Task.Delay(15000,stop.Token);await SendJsonAsync(new{action="ping"},stop.Token);}}catch(Exception ex)when(ex is OperationCanceledException or WebSocketException or ObjectDisposedException){}}
    public async Task SendAsync(byte[] bytes,CancellationToken ct){await sendLock.WaitAsync(ct);try{await socket.SendAsync(bytes.AsMemory(),WebSocketMessageType.Binary,true,ct);}finally{sendLock.Release();}}
    public Task SendJsonAsync(object value,CancellationToken ct)=>SendAsync(JsonSerializer.SerializeToUtf8Bytes(value,Wire.Json),ct);
    public void Disconnect()=>socket.Abort();
    public async Task ReceiveAsync(byte[] bytes,CancellationToken ct)
    {
        if(bytes.Length==0)return;
        if(bytes[0]=='{')
        {
            var data=JsonSerializer.Deserialize<JsonElement>(bytes);var action=Text(data,"action");
            if(action=="msg"&&Text(data,"type")=="console"&&Text(data,"value").StartsWith("uncaughtException",StringComparison.Ordinal))
            {
                var issue=Text(data,"value");store.Audit("Agent","Agent core diagnostic",inventory.Hostname,issue.Length>2000?issue[..2000]:issue);
            }
            if(action=="ping"){await SendJsonAsync(new{action="pong"},ct);return;}
            if(action=="coreinfo")
            {
                var users=data.TryGetProperty("users",out var userList)&&userList.ValueKind==JsonValueKind.Array?string.Join(", ",userList.EnumerateArray().Select(u=>u.ToString())):inventory.Username;
                inventory=inventory with{Os=Text(data,"osdesc",inventory.Os),Username=users};coreReady.TrySetResult();
            }
            store.UpdateInventory(Id,inventory);
            var response=Text(data,"responseid");
            if(response.Length>0&&requests.TryGetValue(response,out var pending))
            {
                if(action=="msg"&&Text(data,"type") is "runcommands" or "meshall-consent")pending.TrySetResult(data);
                else if(action is "getsysinfo" or "netinfo" or "sysinfo")pending.TrySetResult(data);
            }
            return;
        }
        if(bytes.Length<2)return;
        var command=BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if(command==11)
        {
            var core=cores.Build(windows,intel);
            if(bytes.Length==52&&bytes.AsSpan(4,48).SequenceEqual(core.Hash)){await SendAsync(NativeAgentHub.Packet(16,[0,0]),ct);coreReady.TrySetResult();}
            else await SendAsync(NativeAgentHub.Packet(10,[0,0],core.Hash,core.Bytes),ct);
        }
        else if(command==12)await SendAsync(NativeAgentHub.Packet(11,[0,0]),ct);
    }
    private async Task<JsonElement> RequestAsync(object value,CancellationToken ct,string? id=null)
    {
        await coreReady.Task.WaitAsync(TimeSpan.FromSeconds(30),ct);id??=Guid.NewGuid().ToString("N");
        var command=JsonSerializer.SerializeToNode(value,Wire.Json)!.AsObject();command["responseid"]=id;
        var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);requests[id]=completion;
        try{await SendJsonAsync(command,ct);return await completion.Task.WaitAsync(TimeSpan.FromSeconds(70),ct);}finally{requests.TryRemove(id,out _);}
    }
    public async Task<object> ExecuteAsync(string operation,object payload,CancellationToken ct)
    {
        if(operation=="command")return await CommandAsync((CommandInput)payload,ct);
        if(operation=="processes")
        {
            var result=await CommandAsync(new(windows?"Get-Process | Select-Object Id,ProcessName,WorkingSet64 | ConvertTo-Json -Compress":"ps -eo pid=,rss=,comm="),ct);
            if(windows){var json=JsonSerializer.Deserialize<JsonElement>(result.Output);return(json.ValueKind==JsonValueKind.Array?json.EnumerateArray().ToArray():[json]).Select(p=>new ProcessInfo((int)Number(p,"Id"),Text(p,"ProcessName"),Number(p,"WorkingSet64"))).ToArray();}
            return result.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>l.Split((char[]?)null,3,StringSplitOptions.RemoveEmptyEntries)).Where(p=>p.Length==3&&int.TryParse(p[0],out _)&&long.TryParse(p[1],out _)).Select(p=>new ProcessInfo(int.Parse(p[0]),p[2],long.Parse(p[1])*1024)).ToArray();
        }
        if(operation.StartsWith("files.",StringComparison.Ordinal))return await FilesAsync(operation,(FileInput)payload,ct);
        if(operation=="power"){var input=(PowerInput)payload;await SendJsonAsync(new{action="poweraction",actiontype=input.Action=="restart"?3:2},ct);return "Power action sent";}
        if(operation=="wake"){await SendJsonAsync(new{action="wakeonlan",macs=new[]{((WakeInput)payload).Mac}},ct);return "Wake packet requested";}
        throw new IOException("Unsupported native operation: "+operation);
    }
    private async Task<CommandOutput> CommandAsync(CommandInput input,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(input.Command)||input.Command.Length>16000)throw new IOException("Enter a command up to 16000 characters.");
        var marker="__MESHALL_"+Guid.NewGuid().ToString("N")+"__";var directory=input.Directory??"";
        var script=windows?(directory.Length>0?$"Set-Location -LiteralPath '{directory.Replace("'","''")}' -ErrorAction Stop\n":"")+input.Command+$"\n$meshall=$?; Write-Output ('{marker}' + [int](!$meshall)); (Get-Location).Path\n":(directory.Length>0?$"cd '{directory.Replace("'","'\\''")}' || exit 1\n":"")+input.Command+$"\nmeshall=$?; printf '\\n{marker}%s\\n' \"$meshall\"; pwd\n";
        var reply=await RequestAsync(new{action="runcommands",type=windows?2:3,cmds=script,reply=true,runAsUser=0},ct);
        var output=Text(reply,"result");var at=output.LastIndexOf(marker,StringComparison.Ordinal);var code=-1;
        if(at>=0){var lines=output[(at+marker.Length)..].Trim().Split('\n',StringSplitOptions.TrimEntries);if(int.TryParse(lines[0],out var parsed))code=parsed;if(lines.Length>1)directory=lines[1];output=output[..at].TrimEnd();}
        var truncated=output.Length>150000;if(truncated)output=output[..150000]+"\n[Output truncated]";return new(output,code,directory,truncated);
    }
    public async Task<NativeTunnelHub.Tunnel> OpenTunnelAsync(int protocol,SessionRequest request,CancellationToken ct)
    {
        await coreReady.Task.WaitAsync(TimeSpan.FromSeconds(30),ct);
        if(request.Port>0&&request.RequireConsent&&request.Kind is "rdp" or "vnc" or "amt")
        {
            var nonce=Guid.NewGuid().ToString("N");var approval=await RequestAsync(new{action="msg",type="meshall-consent",nonce,protocol=request.Kind,meshallRequestor=request.Actor,sessionid=request.SessionId,rights=uint.MaxValue,username=request.Actor,realname=request.Actor},ct);
            if(Text(approval,"nonce")!=nonce||!approval.TryGetProperty("granted",out var granted)||granted.ValueKind!=JsonValueKind.True)throw new IOException("The person at the PC did not approve this screen session.");
        }
        return await tunnels.OpenAsync(this,protocol,request,ct);
    }
    public async Task<AgentGateway.RemoteStream> OpenStreamAsync(SessionRequest request,CancellationToken ct)
    {
        var protocol=request.Port>0?0:request.Kind=="powershell"?6:1;
        var tunnel=await OpenTunnelAsync(protocol,request,ct);var channel=Channel.CreateBounded<byte[]>(64);var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Read(){try{await foreach(var frame in tunnel.Reader.ReadAllAsync(lifetime.Token)){if(frame.Bytes.Length>0&&frame.Bytes[0]=='{'){try{var json=JsonSerializer.Deserialize<JsonElement>(frame.Bytes);if(json.TryGetProperty("ctrlChannel",out _)){if(Text(json,"type")=="ping")await tunnel.TextAsync("{\"ctrlChannel\":\"102938\",\"type\":\"pong\"}",lifetime.Token);continue;}}catch(JsonException){}}await channel.Writer.WriteAsync(frame.Bytes,lifetime.Token);}}catch(Exception ex)when(ex is OperationCanceledException or WebSocketException or IOException){}finally{channel.Writer.TryComplete();}}
        var reading=Read();
        return AgentGateway.RemoteStream.External(request.SessionId,channel.Reader,(bytes,token)=>tunnel.SendAsync(bytes,false,token),async()=>{await lifetime.CancelAsync();await tunnel.DisposeAsync();await reading;lifetime.Dispose();},(cols,rows,token)=>tunnel.TextAsync(JsonSerializer.Serialize(new{ctrlChannel="102938",type="termsize",cols,rows}),token));
    }
    private async Task<object> FilesAsync(string operation,FileInput input,CancellationToken ct)
    {
        await using var tunnel=await OpenTunnelAsync(5,new(Guid.NewGuid().ToString("N"),"MeshAll operator",false),ct);var path=input.Path.Replace('\\','/');
        Task Send(object value)=>tunnel.TextAsync(JsonSerializer.Serialize(value,Wire.Json),ct);
        async Task<JsonElement> Json(Func<JsonElement,bool> predicate){await foreach(var frame in tunnel.Reader.ReadAllAsync(ct)){if(frame.Bytes.Length==0||frame.Bytes[0]!='{')continue;var value=JsonSerializer.Deserialize<JsonElement>(frame.Bytes);if(predicate(value))return value;}throw new IOException("File session ended.");}
        if(operation=="files.list")
        {
            await Send(new{action="ls",reqid=1,path});var listing=await Json(r=>r.TryGetProperty("dir",out _));var actual=Text(listing,"path",path);
            return new FileListing(actual,actual.Length==0||actual=="/"?null:Parent(actual),listing.GetProperty("dir").EnumerateArray().Select(f=>{var name=Text(f,"n");return new RemoteFile(name,actual.Length==0?name:actual.TrimEnd('/')+"/"+name,Number(f,"t") is 1 or 2,Number(f,"s"),DateTimeOffset.TryParse(Text(f,"d"),out var time)?time:DateTimeOffset.UnixEpoch);}).OrderByDescending(f=>f.Directory).ThenBy(f=>f.Name).ToArray());
        }
        if(operation is "files.mkdir" or "files.delete")
        {
            if(string.IsNullOrWhiteSpace(path)||path=="/"||path.TrimEnd('/').EndsWith(':'))throw new IOException("A filesystem root cannot be changed.");
            if(operation=="files.mkdir")await Send(new{action="mkdir",reqid=1,path});else await Send(new{action="rm",reqid=1,path=Parent(path),delfiles=new[]{Name(path)},rec=false});
            await Send(new{action="ls",reqid=2,path=Parent(path)});var listing=await Json(r=>r.TryGetProperty("dir",out _));var exists=listing.GetProperty("dir").EnumerateArray().Any(f=>Text(f,"n")==Name(path));
            if(operation=="files.mkdir"?!exists:exists)throw new IOException("The remote filesystem rejected this change.");return "Completed";
        }
        if(operation=="files.read")
        {
            await Send(new{action="download",sub="start",id="file",path});var start=await Json(r=>Text(r,"action")=="download");if(Text(start,"sub")!="start")throw new IOException("File could not be opened.");await Send(new{action="download",sub="startack",id="file"});using var output=new MemoryStream();
            await foreach(var frame in tunnel.Reader.ReadAllAsync(ct)){if(frame.Bytes.Length<4||frame.Bytes[0]=='{')continue;if(output.Length+frame.Bytes.Length-4>Wire.MaxFileBytes)throw new IOException("File exceeds the 5 MB transfer limit.");output.Write(frame.Bytes,4,frame.Bytes.Length-4);if((frame.Bytes[3]&1)!=0)return new FileContent(Name(path),Convert.ToBase64String(output.ToArray()));await Send(new{action="download",sub="ack",id="file"});}throw new IOException("File download interrupted.");
        }
        if(operation=="files.write")
        {
            var bytes=Convert.FromBase64String(input.Content??"");if(bytes.Length>Wire.MaxFileBytes)throw new IOException("File exceeds the 5 MB transfer limit.");await Send(new{action="upload",reqid="up",path=Parent(path),name=Name(path),size=bytes.Length,append=false});
            if(Text(await Json(r=>Text(r,"action")is "uploadstart" or "uploaderror"),"action")=="uploaderror")throw new IOException("File could not be created.");
            for(var offset=0;offset<bytes.Length;offset+=65536){var block=bytes.AsMemory(offset,Math.Min(65536,bytes.Length-offset)).ToArray();if(block[0]is 0 or 123)block=[0,..block];await tunnel.SendAsync(block,false,ct);if(Text(await Json(r=>Text(r,"action")is "uploadack" or "uploaderror"),"action")=="uploaderror")throw new IOException("File upload failed.");}
            await Send(new{action="uploaddone",reqid="up"});await Json(r=>Text(r,"action")=="uploaddone");return "Uploaded";
        }
        throw new IOException("Unsupported file operation.");
    }
    private static string Name(string path)=>path.TrimEnd('/').Split('/')[^1];
    private static string Parent(string path){var p=path.TrimEnd('/');var index=p.LastIndexOf('/');return index<0?"":index==0?"/":p[..index];}
    private static string Text(JsonElement root,string key,string fallback="")=>root.TryGetProperty(key,out var value)?value.ValueKind==JsonValueKind.String?value.GetString()??fallback:value.ToString():fallback;
    private static long Number(JsonElement root,string key)=>root.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var n)?n:0;
    public async ValueTask DisposeAsync(){await stop.CancelAsync();await heartbeat;foreach(var request in requests.Values)request.TrySetException(new IOException("Native agent disconnected."));stop.Dispose();}
}
