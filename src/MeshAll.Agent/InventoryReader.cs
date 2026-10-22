using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MeshAll.Shared;

namespace MeshAll.Agent;

public static class InventoryReader
{
    private static ulong lastIdle, lastTotal;
    public static async Task RunAsync(AgentSettings settings, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await send(new("inventory", Payload: Wire.Element(Read(settings))));
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }
    public static Inventory Read(AgentSettings settings)
    {
        long total = 0, used = 0; double cpu = 0;
        if (OperatingSystem.IsWindows())
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory)) { total = (long)memory.TotalPhysical; used = total - (long)memory.AvailablePhysical; }
            if (GetSystemTimes(out var idle, out var kernel, out var user)) cpu = Cpu(idle, kernel + user);
        }
        else if (OperatingSystem.IsLinux())
        {
            var values = File.ReadAllLines("/proc/meminfo").Select(l => l.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2).ToDictionary(p => p[0], p => long.Parse(p[1]) * 1024);
            total = values.GetValueOrDefault("MemTotal"); used = total - values.GetValueOrDefault("MemAvailable");
            var ticks = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
            cpu = Cpu(ticks[3] + ticks.ElementAtOrDefault(4), ticks.Aggregate(0UL, (a, b) => a + b));
        }
        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork));
        var ip = network?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "Unknown";
        var disks = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives()) { try { if (drive.IsReady) disks.Add(new(drive.Name, drive.TotalSize, drive.AvailableFreeSpace)); } catch (IOException) { } }
        var capabilities = new List<string> { "command", "terminal", "files", "tcp", "power", "processes" };
        if (OperatingSystem.IsWindows()) capabilities.Add("desktop");
        return new(Environment.MachineName, OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux", RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(), Environment.UserName, ip, network?.GetPhysicalAddress().ToString() ?? "", Environment.ProcessorCount,
            total, used, cpu, Environment.TickCount64 / 1000, Wire.Version, capabilities.ToArray(), disks.ToArray());
    }
    private static double Cpu(ulong idle, ulong total)
    {
        var difference = total - lastTotal; var value = lastTotal == 0 || difference == 0 ? 0 : 100.0 * (difference - (idle - lastIdle)) / difference;
        lastIdle = idle; lastTotal = total; return Math.Clamp(value, 0, 100);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
