using MeshAll.Shared;
using MeshAll.Server.Localization;

namespace MeshAll.Server.Services;

public enum ResourceHealth
{
    Healthy,
    Warning,
    Critical,
    Unknown
}

public static class ResourceUsage
{
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(2);
    public const double HighCpuPercent = 85;
    public const double HighMemoryPercent = 90;
    public const double NearlyFullDiskPercent = 90;
    public const double CriticalDiskPercent = 97;

    public static bool IsValidCpu(double cpu) => double.IsFinite(cpu) && cpu is >= 0 and <= 100;

    public static bool IsValidMemory(long total, long used) => total > 0 && used >= 0 && used <= total;

    public static bool IsValidDisk(DiskInfo disk) => !string.IsNullOrWhiteSpace(disk.Name)
        && disk.Total > 0 && disk.Free >= 0 && disk.Free <= disk.Total;

    public static bool IsFresh(DateTimeOffset? sampledAt, DateTimeOffset now) => sampledAt is not null
        && sampledAt <= now.AddSeconds(5) && now - sampledAt <= FreshnessWindow;

    public static double DiskPercent(DiskInfo disk) => 100.0 * (disk.Total - disk.Free) / disk.Total;

    public static string Tone(ResourceHealth health) => health switch
    {
        ResourceHealth.Healthy => "healthy",
        ResourceHealth.Warning => "warning",
        ResourceHealth.Critical => "critical",
        _ => "unknown"
    };

    public static string Label(ResourceHealth health) => health switch
    {
        ResourceHealth.Healthy => UiText.Get("Healthy"),
        ResourceHealth.Warning => UiText.Get("Needs attention"),
        ResourceHealth.Critical => UiText.Get("Critical storage"),
        _ => UiText.Get("Partial / stale data")
    };
}

/// <summary>A small resource projection; dashboards do not load full device inventories.</summary>
public sealed record DeviceResourceInfo(
    string Id,
    string Name,
    string Platform,
    string Address,
    string GroupId,
    string GroupName,
    int Cores,
    double Cpu,
    long MemoryTotal,
    long MemoryUsed,
    DiskInfo[] Disks,
    DateTimeOffset? CpuReportedAt,
    DateTimeOffset? MemoryReportedAt,
    DateTimeOffset? DisksReportedAt)
{
    public double? CpuPercent(DateTimeOffset now) => ResourceUsage.IsFresh(CpuReportedAt, now)
        && ResourceUsage.IsValidCpu(Cpu) ? Cpu : null;

    public double? MemoryPercent(DateTimeOffset now) => ResourceUsage.IsFresh(MemoryReportedAt, now)
        && ResourceUsage.IsValidMemory(MemoryTotal, MemoryUsed) ? 100.0 * MemoryUsed / MemoryTotal : null;

    public bool HasFreshDisks(DateTimeOffset now) => ResourceUsage.IsFresh(DisksReportedAt, now);

    public DiskInfo? BusiestDisk => Disks.Where(ResourceUsage.IsValidDisk)
        .OrderByDescending(ResourceUsage.DiskPercent)
        .FirstOrDefault();

    public double? DiskPercent(DateTimeOffset now) => HasFreshDisks(now) && BusiestDisk is DiskInfo disk
        ? ResourceUsage.DiskPercent(disk) : null;

    /// <summary>Total local-volume usage for this device; pressure alerts still inspect its fullest disk.</summary>
    public double? StoragePercent(DateTimeOffset now)
    {
        if (!HasFreshDisks(now))
        {
            return null;
        }

        DiskInfo[] volumes = Disks.Where(ResourceUsage.IsValidDisk).ToArray();
        decimal capacity = volumes.Sum(disk => (decimal)disk.Total);
        decimal used = volumes.Sum(disk => (decimal)disk.Total - disk.Free);
        return capacity > 0 ? (double)(100 * used / capacity) : null;
    }

    public DateTimeOffset? LastSampleAt => new[] {
        CpuReportedAt,
        MemoryReportedAt,
        DisksReportedAt
    }.Max();

    public ResourceHealth Health(DateTimeOffset now)
    {
        double? disk = DiskPercent(now);
        if (disk >= ResourceUsage.CriticalDiskPercent)
        {
            return ResourceHealth.Critical;
        }

        if (disk >= ResourceUsage.NearlyFullDiskPercent
            || CpuPercent(now) >= ResourceUsage.HighCpuPercent
            || MemoryPercent(now) >= ResourceUsage.HighMemoryPercent)
        {
            return ResourceHealth.Warning;
        }

        return CpuPercent(now) is not null && MemoryPercent(now) is not null && HasFreshDisks(now)
            ? ResourceHealth.Healthy
            : ResourceHealth.Unknown;
    }

    public static DeviceResourceInfo FromInventory(string id, string name, Inventory inventory) => new(
        id, name, inventory.Platform, inventory.Ip, "", "", inventory.Cores, inventory.Cpu,
        inventory.MemoryTotal, inventory.MemoryUsed, inventory.Disks,
        inventory.CpuReportedAt, inventory.MemoryReportedAt, inventory.DisksReportedAt);
}

public sealed record DeviceSummarySnapshot(
    IReadOnlyList<DeviceResourceInfo> Devices,
    long TotalDevices,
    long BlockedDevices,
    long ActiveSessions,
    double? CpuPercent,
    long ReportingCpuCores,
    int CpuReportingDevices,
    decimal MemoryUsed,
    decimal MemoryTotal,
    double? MemoryPercent,
    int MemoryReportingDevices,
    decimal StorageUsed,
    decimal StorageTotal,
    double? StoragePercent,
    int StorageReportingDevices,
    int NearlyFullDevices,
    int NearlyFullVolumes,
    int HighCpuDevices,
    int HighMemoryDevices,
    int AttentionDevices,
    int IncompleteDevices,
    DateTimeOffset SampledAt)
{
    public int ConnectedDevices => Devices.Count;
    public long OfflineDevices => Math.Max(0, TotalDevices - BlockedDevices - ConnectedDevices);
    public decimal StorageFree => StorageTotal - StorageUsed;

    public static DeviceSummarySnapshot Create(
        IReadOnlyList<DeviceResourceInfo> connectedDevices,
        long totalDevices,
        long blockedDevices,
        long activeSessions,
        DateTimeOffset now)
    {
        DeviceResourceInfo[] cpuDevices = connectedDevices
            .Where(device => device.CpuPercent(now) is not null).ToArray();
        long cpuCores = cpuDevices.Sum(device => (long)device.Cores);
        double? cpuPercent = AverageUsage(cpuDevices.Select(device => device.CpuPercent(now)));

        DeviceResourceInfo[] memoryDevices = connectedDevices
            .Where(device => device.MemoryPercent(now) is not null).ToArray();
        DeviceResourceInfo[] storageDevices = connectedDevices
            .Where(device => device.StoragePercent(now) is not null).ToArray();
        DiskInfo[] disks = storageDevices.SelectMany(device => device.Disks)
            .Where(ResourceUsage.IsValidDisk).ToArray();

        return new DeviceSummarySnapshot(
            connectedDevices, totalDevices, blockedDevices, activeSessions,
            cpuPercent, cpuCores, cpuDevices.Length,
            memoryDevices.Sum(device => (decimal)device.MemoryUsed),
            memoryDevices.Sum(device => (decimal)device.MemoryTotal),
            AverageUsage(memoryDevices.Select(device => device.MemoryPercent(now))), memoryDevices.Length,
            disks.Sum(disk => (decimal)disk.Total - disk.Free),
            disks.Sum(disk => (decimal)disk.Total),
            AverageUsage(storageDevices.Select(device => device.StoragePercent(now))), storageDevices.Length,
            storageDevices.Count(device => device.DiskPercent(now) >= ResourceUsage.NearlyFullDiskPercent),
            disks.Count(disk => ResourceUsage.DiskPercent(disk) >= ResourceUsage.NearlyFullDiskPercent),
            connectedDevices.Count(device => device.CpuPercent(now) >= ResourceUsage.HighCpuPercent),
            connectedDevices.Count(device => device.MemoryPercent(now) >= ResourceUsage.HighMemoryPercent),
            connectedDevices.Count(device => device.Health(now) is ResourceHealth.Warning or ResourceHealth.Critical),
            connectedDevices.Count(device => device.Health(now) == ResourceHealth.Unknown), now);
    }

    public static DeviceSummarySnapshot Empty => Create([], 0, 0, 0, DateTimeOffset.UtcNow);

    private static double? AverageUsage(IEnumerable<double?> samples)
    {
        double[] reported = samples.OfType<double>().ToArray();
        return reported.Length > 0 ? reported.Average() : null;
    }
}
