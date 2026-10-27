using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MeshAll.Shared;

namespace MeshAll.Agent;

public static class InventoryReader
{
    private static readonly object CpuSampleLock = new();
    private static ulong previousIdle;
    private static ulong previousTotal;

    public static async Task RunAsync(
        AgentSettings settings,
        Func<AgentMessage, Task> send,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        do
        {
            await send(new AgentMessage("inventory", Payload: Wire.Element(Read(settings))));
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    public static Inventory Read(AgentSettings settings)
    {
        long totalMemory = 0;
        long usedMemory = 0;
        double cpuPercent = -1;

        if (OperatingSystem.IsWindows())
        {
            MemoryStatus memory = new()
            {
                Length = (uint)Marshal.SizeOf<MemoryStatus>()
            };
            if (GlobalMemoryStatusEx(ref memory))
            {
                totalMemory = (long)memory.TotalPhysical;
                usedMemory = totalMemory - (long)memory.AvailablePhysical;
            }

            if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            {
                cpuPercent = CalculateCpuPercent(idle, kernel + user);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            Dictionary<string, long> memory = File.ReadAllLines("/proc/meminfo")
                .Select(line => line.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2)
                .ToDictionary(parts => parts[0], parts => long.Parse(parts[1]) * 1024);

            totalMemory = memory.GetValueOrDefault("MemTotal");
            usedMemory = totalMemory - memory.GetValueOrDefault("MemAvailable");

            ulong[] ticks = File.ReadLines("/proc/stat").First()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1).Take(8).Select(ulong.Parse).ToArray();

            // Guest time is already included in user/nice; do not count it twice.
            cpuPercent = CalculateCpuPercent(ticks[3] + ticks.ElementAtOrDefault(4),
                ticks.Aggregate(0UL, (sum, tick) => sum + tick));
        }

        NetworkInterface? primaryInterface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(network => network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && network.GetIPProperties().UnicastAddresses
                    .Any(address => address.Address.AddressFamily == AddressFamily.InterNetwork));

        string address = primaryInterface?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address.ToString() ?? "Unknown";

        List<string> capabilities = ["command", "terminal", "files", "tcp", "power", "processes"];
        if (OperatingSystem.IsWindows())
        {
            capabilities.Add("desktop");
        }

        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        string platform = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

        return new Inventory(
            Environment.MachineName, platform, RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(), Environment.UserName, address,
            primaryInterface?.GetPhysicalAddress().ToString() ?? "", Environment.ProcessorCount,
            totalMemory, usedMemory, cpuPercent, Environment.TickCount64 / 1000,
            Wire.Version, capabilities.ToArray(), ReadDisks(),
            ReportedAt: sampledAt,
            CpuReportedAt: cpuPercent >= 0 ? sampledAt : null,
            MemoryReportedAt: totalMemory > 0 ? sampledAt : null,
            DisksReportedAt: sampledAt);
    }

    private static DiskInfo[] ReadDisks()
    {
        List<DiskInfo> disks = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    disks.Add(new DiskInfo(drive.Name, drive.TotalSize, drive.AvailableFreeSpace));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removable or inaccessible mounts do not invalidate the other readings.
            }
        }

        return disks.Take(100).ToArray();
    }

    private static double CalculateCpuPercent(ulong idle, ulong total)
    {
        lock (CpuSampleLock)
        {
            double percent = -1;
            if (previousTotal > 0 && total > previousTotal && idle >= previousIdle)
            {
                ulong elapsed = total - previousTotal;
                ulong idleElapsed = idle - previousIdle;
                if (idleElapsed <= elapsed)
                {
                    percent = 100.0 * (elapsed - idleElapsed) / elapsed;
                }
            }

            previousIdle = idle;
            previousTotal = total;
            return percent;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage;
        public ulong TotalVirtual, AvailableVirtual, Extended;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
