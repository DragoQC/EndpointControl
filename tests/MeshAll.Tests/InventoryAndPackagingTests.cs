using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Xunit;

namespace MeshAll.Tests;

public class InventoryAndPackagingTests
{
    [Theory]
    [InlineData("{\"ctrlChannel\":102938,\"type\":\"rtt\",\"time\":123456}", true)]
    [InlineData("{\"ctrlChannel\":\"102938\",\"type\":\"pong\"}", true)]
    [InlineData("1", false)]
    [InlineData("5", false)]
    [InlineData("{\"ctrlChannel\":102938,\"type\":\"options\",\"protocol\":5}", false)]
    [InlineData("{\"ctrlChannel\":102938,\"type\":\"rtt\",\"protocol\":1}", false)]
    public void DesktopHandshakeAllowsLatencyButNotOtherProtocols(string message, bool allowed) => Assert.Equal(allowed, NativeDesktopProtocol.IsHandshakeControl(Encoding.UTF8.GetBytes(message)));

    [Fact]
    public void ExecutableUsesTheNativeEmbeddedPolicyTrailerAndContainsNotices()
    {
        byte[] original = [0x4d, 0x5a, 1, 2, 3, 4];
        string policy = "MeshServer=wss://test.invalid/agent.ashx\r\nMeshName=Tést\r\n";
        byte[] result = NativeAgentExecutable.Build(original, policy, "Apache License 2.0");
        Assert.True(result.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.True(result.AsSpan(result.Length - 16).SequenceEqual(NativeAgentExecutable.PolicyMarker));
        int size = BinaryPrimitives.ReadInt32BigEndian(result.AsSpan(result.Length - 20, 4));
        Assert.Equal(policy, Encoding.UTF8.GetString(result.AsSpan(result.Length - 20 - size, size)));
        Assert.Contains("Apache License 2.0", Encoding.UTF8.GetString(result));
    }
    [Fact]
    public void InventoryKeepsRealHardwareAndSeparatesConnectionAndInterfaceAddresses()
    {
        Inventory? previous = new Inventory("node", "Windows", "Windows", "x64", "Not reported", "192.0.2.10", "", 0, 0, 0, -1, 0, "123", [], [], ConnectionAddress: "192.0.2.10");
        using JsonDocument? report = JsonDocument.Parse("""
        {"hostname":"actual-node","os":"Windows 11","architecture":"amd64","cores":8,"memoryTotal":17179869184,"memoryUsed":4294967296,"cpu":12.5,"uptime":3600,
         "cpuModel":"Test CPU","interfaces":[{"name":"WireGuard","address":"10.0.0.2","family":"IPv4","mac":"","internal":false},{"name":"Ethernet","address":"192.0.2.10","family":"IPv4","mac":"00:11:22:33:44:55","internal":false}],
         "disks":[{"name":"C:","total":100000000000,"free":50000000000}],"users":["PC\\user"]}
        """);
        Inventory? updated = NativeInventory.Apply(previous, report.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal("actual-node", updated.Hostname);
        Assert.Equal("x64", updated.Architecture);
        Assert.Equal(8, updated.Cores);
        Assert.Equal(17179869184, updated.MemoryTotal);
        Assert.Equal(12.5, updated.Cpu);
        Assert.Equal("192.0.2.10", updated.Ip);
        Assert.Equal("00:11:22:33:44:55", updated.Mac);
        Assert.Equal("PC\\user", updated.Username);
        Assert.Single(updated.Disks);
        Assert.NotNull(updated.ReportedAt);
        using JsonDocument? partial = JsonDocument.Parse("{\"uptime\":3630,\"cpu\":null}");
        Inventory? next = NativeInventory.Apply(updated, partial.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal(updated.MemoryTotal, next.MemoryTotal);
        Assert.Equal(updated.Cpu, next.Cpu);
        Assert.Equal(3630, next.Uptime);
    }
}
