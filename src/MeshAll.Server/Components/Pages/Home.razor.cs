using MeshAll.Server.Localization;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.AspNetCore.Components;

namespace MeshAll.Server.Components.Pages;

public partial class Home
{
    private DeviceSummarySnapshot snapshot = DeviceSummarySnapshot.Empty;
    private List<AuditInfo> recentActivity = [];
    private string attention = "all";
    private string sort = "attention";

    private static string MetricValue(double? percent) => percent is null ? "—" : DisplayFormat.Percent(percent);

    protected override PageResult<DeviceResourceInfo> ReadPage(PageRequest request)
    {
        HashSet<string> connectedIds = Gateway.ConnectedDeviceIds();
        List<DeviceResourceInfo> resources = Store.ConnectedResources(connectedIds);
        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        long sessions = Can(Permissions.Sessions) ? Store.ActiveSessionCount : 0;
        snapshot = DeviceSummarySnapshot.Create(resources, Store.DeviceCount, Store.BlockedDeviceCount, sessions, sampledAt);
        recentActivity = Can(Permissions.Audit) ? Store.AuditLog(5) : [];
        IEnumerable<DeviceResourceInfo> matches = resources.Where(MatchesFilters);
        IOrderedEnumerable<DeviceResourceInfo> ordered = sort switch
        {
            "cpu" => matches.OrderByDescending(device => device.CpuPercent(sampledAt)),
            "memory" => matches.OrderByDescending(device => device.MemoryPercent(sampledAt)),
            "disk" => matches.OrderByDescending(device => device.DiskPercent(sampledAt)),
            "name" => matches.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase),
            _ => matches.OrderByDescending(device => HealthPriority(device.Health(sampledAt)))
        };
        DeviceResourceInfo[] filtered = ordered
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToArray();
        return PageResult<DeviceResourceInfo>.FromSnapshot(filtered, request);
    }

    private bool MatchesFilters(DeviceResourceInfo device)
    {
        bool matchesSearch = $"{device.Name} {device.Address} {device.Platform} {device.GroupName}".Contains(Search, StringComparison.OrdinalIgnoreCase);
        bool matchesAttention = attention switch
        {
            "attention" => device.Health(snapshot.SampledAt) is ResourceHealth.Warning or ResourceHealth.Critical,
            "cpu" => device.CpuPercent(snapshot.SampledAt) >= ResourceUsage.HighCpuPercent,
            "memory" => device.MemoryPercent(snapshot.SampledAt) >= ResourceUsage.HighMemoryPercent,
            "disk" => device.DiskPercent(snapshot.SampledAt) >= ResourceUsage.NearlyFullDiskPercent,
            "unknown" => device.Health(snapshot.SampledAt) == ResourceHealth.Unknown,
            _ => true
        };
        return matchesSearch && matchesAttention;
    }

    private static int HealthPriority(ResourceHealth health) => health switch
    {
        ResourceHealth.Critical => 3,
        ResourceHealth.Warning => 2,
        ResourceHealth.Unknown => 1,
        _ => 0
    };

    private Task AttentionChangedAsync(ChangeEventArgs arguments) => SetAttentionAsync(arguments.Value?.ToString() ?? "all");

    private Task SetAttentionAsync(string value)
    {
        attention = value;
        return ResetPageAsync();
    }

    private Task SortChangedAsync(ChangeEventArgs arguments)
    {
        sort = arguments.Value?.ToString() ?? "attention";
        return ResetPageAsync();
    }

    private static string MemoryDescription(DeviceResourceInfo device) => device.MemoryTotal > 0
        ? $"{DisplayFormat.Bytes(device.MemoryUsed)} / {DisplayFormat.Bytes(device.MemoryTotal)}" : UiText.Get("No memory sample");

    private static string DiskDescription(DeviceResourceInfo device) => device.BusiestDisk is DiskInfo disk
        ? UiText.Get("{0} free", DisplayFormat.Bytes(disk.Free)) : UiText.Get("No volume reported");
}
