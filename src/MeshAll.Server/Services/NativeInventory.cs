using System.Net;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public static class NativeInventory
{
    public static string Architecture(int id)=>id switch{1 or 3 or 5 or 15 or 19=>"x86",2 or 4 or 6 or 16 or 18 or 20 or 30 or 33 or 36 or 37=>"x64",29 or 32 or 42 or 43 or 26 or 41=>"arm64",9 or 13 or 24 or 25 or 27 or 35 or 44=>"arm",45=>"riscv64",_=>"Not reported"};
    public static Inventory Apply(Inventory previous,JsonElement data,DateTimeOffset received)
    {
        string Text(string key,string fallback)=>data.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(v.GetString())?v.GetString()![..Math.Min(v.GetString()!.Length,500)]:fallback;
        double Number(string key,double fallback)=>data.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)&&double.IsFinite(n)&&n>=0?n:fallback;
        var interfaces=data.TryGetProperty("interfaces",out var net)&&net.ValueKind==JsonValueKind.Array?net.EnumerateArray().Take(256).Select(n=>n.Deserialize<NetworkInterfaceInfo>(Wire.Json)).OfType<NetworkInterfaceInfo>().Where(n=>n.Name is not null&&n.Address is not null&&n.Mac is not null&&n.Family is not null).ToArray():previous.Interfaces??[];
        bool Usable(NetworkInterfaceInfo n)=>!n.Internal&&IPAddress.TryParse(n.Address,out var address)&&!IPAddress.IsLoopback(address)&&!address.IsIPv6LinkLocal&&!n.Address.StartsWith("169.254.",StringComparison.Ordinal);
        var primary=interfaces.Where(Usable).OrderByDescending(n=>n.Address==previous.ConnectionAddress).ThenBy(n=>new[]{"wireguard","vpn","tun","tap","wg","loopback"}.Any(s=>n.Name.Contains(s,StringComparison.OrdinalIgnoreCase))).ThenBy(n=>n.Family.Contains('6')).FirstOrDefault();
        var disks=data.TryGetProperty("disks",out var volumes)&&volumes.ValueKind==JsonValueKind.Array?volumes.EnumerateArray().Take(100).Select(d=>d.Deserialize<DiskInfo>(Wire.Json)).OfType<DiskInfo>().Where(d=>d.Name is not null&&d.Total>0&&d.Free>=0&&d.Free<=d.Total).ToArray():previous.Disks;
        var users=data.TryGetProperty("users",out var list)&&list.ValueKind==JsonValueKind.Array?string.Join(", ",list.EnumerateArray().Where(u=>u.ValueKind==JsonValueKind.String).Take(50).Select(u=>u.GetString())):previous.Username;
        var arch=Text("architecture",previous.Architecture) switch{"x86_64" or "amd64"=>"x64","aarch64"=>"arm64","ia32" or "i686"=>"x86",var other=>other};
        var memory=(long)Math.Min(Number("memoryTotal",previous.MemoryTotal),1L<<60);
        return previous with{Hostname=Text("hostname",previous.Hostname),Os=Text("os",previous.Os),Architecture=arch,
            Username=users,Ip=primary?.Address??previous.Ip,Mac=primary?.Mac??previous.Mac,Cores=(int)Math.Min(Number("cores",previous.Cores),65536),
            MemoryTotal=memory,MemoryUsed=(long)Math.Min(Number("memoryUsed",previous.MemoryUsed),memory),Cpu=Math.Min(Number("cpu",previous.Cpu),100),
            Uptime=(long)Math.Min(Number("uptime",previous.Uptime),1L<<60),CpuModel=Text("cpuModel",previous.CpuModel??""),Version=Text("version",previous.Version=="0"?"Not reported":previous.Version),
            Manufacturer=Text("manufacturer",previous.Manufacturer??""),Model=Text("model",previous.Model??""),Interfaces=interfaces,Disks=disks,ReportedAt=received,Diagnostic=Text("diagnostic","")};
    }
}
