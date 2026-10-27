using System.Text.Json;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Xunit;

namespace MeshAll.Tests;

public sealed class DeviceMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UsageCountsEachReportingComputerEqually()
    {
        DeviceResourceInfo small = Resource("small", 2, 80, 4, 1, [new("C:", 100, 10)]);
        DeviceResourceInfo large = Resource("large", 6, 20, 12, 9, [new("/", 900, 450)]);
        DeviceResourceInfo stale = Resource("stale", 64, 99, 1000, 999, [new("stale", 10000, 0)]) with
        {
            CpuReportedAt = Now.AddMinutes(-3),
            MemoryReportedAt = Now.AddMinutes(-3),
            DisksReportedAt = Now.AddMinutes(-3)
        };

        DeviceSummarySnapshot summary = DeviceSummarySnapshot.Create([small, large, stale], 5, 1, 450, Now);

        Assert.Equal(50, summary.CpuPercent);
        Assert.Equal(8, summary.ReportingCpuCores);
        Assert.Equal(2, summary.CpuReportingDevices);
        Assert.Equal(50, summary.MemoryPercent);
        Assert.Equal(70, summary.StoragePercent);
        Assert.Equal(460, summary.StorageFree);
        Assert.Equal(1, summary.NearlyFullDevices);
        Assert.Equal(1, summary.NearlyFullVolumes);
        Assert.Equal(1, summary.AttentionDevices);
        Assert.Equal(1, summary.IncompleteDevices);
        Assert.Equal(3, summary.ConnectedDevices);
        Assert.Equal(1, summary.OfflineDevices);
        Assert.Equal(450, summary.ActiveSessions);
    }

    [Fact]
    public void FullSystemVolumeStillGetsAttentionWhenOtherLocalVolumesAreEmpty()
    {
        DeviceResourceInfo device = Resource("mixed-volumes", 4, 20, 100, 30,
            [new("C:", 100, 1), new("D:", 900, 899)]);
        DeviceSummarySnapshot summary = DeviceSummarySnapshot.Create([device], 1, 0, 0, Now);

        Assert.Equal(10, summary.StoragePercent);
        Assert.Equal(99, device.DiskPercent(Now));
        Assert.Equal(1, summary.NearlyFullDevices);
        Assert.Equal(ResourceHealth.Critical, device.Health(Now));
    }

    [Fact]
    public void ValidCpuUsageDoesNotNeedACoreCountToJoinTheAverage()
    {
        DeviceResourceInfo device = Resource("no-core-count", 0, 40, 100, 30, [new("C:", 100, 50)]);
        DeviceSummarySnapshot summary = DeviceSummarySnapshot.Create([device], 1, 0, 0, Now);

        Assert.Equal(40, summary.CpuPercent);
        Assert.Equal(1, summary.CpuReportingDevices);
    }

    [Fact]
    public void IdleZeroIsARealSampleButMissingReadingsAreNotZero()
    {
        DeviceResourceInfo idle = Resource("idle", 4, 0, 8, 0, [new("C:", 100, 100)]);
        DeviceResourceInfo unknown = Resource("unknown", 8, -1, 0, 0, []) with
        {
            CpuReportedAt = null,
            MemoryReportedAt = null,
            DisksReportedAt = null
        };

        DeviceSummarySnapshot idleSummary = DeviceSummarySnapshot.Create([idle, unknown], 2, 0, 0, Now);
        Assert.Equal(0, idleSummary.CpuPercent);
        Assert.Equal(0, idleSummary.MemoryPercent);
        Assert.Equal(1, idleSummary.CpuReportingDevices);
        Assert.Equal(4, idleSummary.ReportingCpuCores);

        DeviceSummarySnapshot unknownSummary = DeviceSummarySnapshot.Create([unknown], 1, 0, 0, Now);
        Assert.Null(unknownSummary.CpuPercent);
        Assert.Null(unknownSummary.MemoryPercent);
        Assert.Null(unknownSummary.StoragePercent);
        Assert.Equal(ResourceHealth.Unknown, unknown.Health(Now));
    }

    [Fact]
    public void PartialOrInvalidReportsDoNotRefreshTheLastSuccessfulResourceSamples()
    {
        Inventory previous = Inventory() with
        {
            Cpu = 92,
            MemoryUsed = 98,
            Disks = [new("C:", 100, 1)],
            CpuReportedAt = Now.AddMinutes(-3),
            MemoryReportedAt = Now.AddMinutes(-3),
            DisksReportedAt = Now.AddMinutes(-3)
        };
        using JsonDocument partial = JsonDocument.Parse("""
            {"cpu":null,"memoryTotal":100,"memoryUsed":200,"disks":null,"uptime":300}
            """);
        Inventory updated = NativeInventory.Apply(previous, partial.RootElement, Now);

        Assert.Equal(Now, updated.ReportedAt);
        Assert.Equal(previous.Cpu, updated.Cpu);
        Assert.Equal(previous.CpuReportedAt, updated.CpuReportedAt);
        Assert.Equal(previous.MemoryUsed, updated.MemoryUsed);
        Assert.Equal(previous.MemoryReportedAt, updated.MemoryReportedAt);
        Assert.Equal(previous.DisksReportedAt, updated.DisksReportedAt);
        DeviceSummarySnapshot summary = DeviceSummarySnapshot.Create([DeviceResourceInfo.FromInventory("device", "Device", updated)], 1, 0, 0, Now);
        Assert.Null(summary.CpuPercent);
        Assert.Null(summary.MemoryPercent);
        Assert.Equal(0, summary.NearlyFullDevices);
    }

    [Fact]
    public void ValidCpuSurvivesMalformedInterfacesAndDiskEntries()
    {
        using JsonDocument report = JsonDocument.Parse("""
            {"cpu":25,"memoryTotal":100,"memoryUsed":30,
             "interfaces":[{"name":false},{"name":"LAN","address":"192.0.2.2","family":"IPv4","mac":"00:11","internal":false}],
             "disks":[{"name":"invalid","total":"bad","free":1},{"name":"C:","total":100,"free":2}]}
            """);
        Inventory updated = NativeInventory.Apply(Inventory(), report.RootElement, Now);

        Assert.Equal(25, updated.Cpu);
        Assert.Equal(Now, updated.CpuReportedAt);
        Assert.Equal(Now, updated.MemoryReportedAt);
        Assert.Single(updated.Disks);
        Assert.Single(updated.Interfaces!);
        Assert.Equal(ResourceHealth.Critical, DeviceResourceInfo.FromInventory("device", "Device", updated).Health(Now));
    }

    [Fact]
    public void ResourceProjectionExcludesBlockedAndRemovedDevices()
    {
        using TestServer server = new();
        Store store = server.Store;
        (string Id, string Token) token = store.CreateToken("Projection", "default", 3, 1);
        EnrollmentResponse visible = store.Enroll(new(token.Token, "Visible", "Windows"));
        EnrollmentResponse blocked = store.Enroll(new(token.Token, "Blocked", "Windows"));
        EnrollmentResponse removed = store.Enroll(new(token.Token, "Removed", "Windows"));
        foreach (string id in new[] {
            visible.DeviceId,
            blocked.DeviceId,
            removed.DeviceId
        })
        {
            store.UpdateInventory(id, Inventory() with
            {
                CpuReportedAt = Now,
                MemoryReportedAt = Now,
                DisksReportedAt = Now
            });
        }
        store.SetDeviceBlocked(blocked.DeviceId, true);
        store.RemoveDevice(removed.DeviceId);

        List<DeviceResourceInfo> resources = store.ConnectedResources([visible.DeviceId, blocked.DeviceId, removed.DeviceId]);
        Assert.Single(resources);
        Assert.Equal(visible.DeviceId, resources[0].Id);
        Assert.Equal(4, resources[0].Cores);
        Assert.Equal(Now, resources[0].CpuReportedAt);
    }

    private static DeviceResourceInfo Resource(string name, int cores, double cpu, long memoryTotal, long memoryUsed, DiskInfo[] disks) =>
        new(name, name, "Windows", "192.0.2.1", "default", "Unassigned", cores, cpu,
            memoryTotal, memoryUsed, disks, Now, Now, Now);

    private static Inventory Inventory() => new("device", "Windows", "Windows 11", "x64", "user",
        "192.0.2.1", "00:11", 4, 100, 30, 20, 100, "test", [], [new("C:", 100, 50)]);
}
