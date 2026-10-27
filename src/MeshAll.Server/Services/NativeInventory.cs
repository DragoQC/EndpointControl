using System.Net;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public static class NativeInventory
{
    private const long MaximumReportedBytes = 1L << 60;
    private static readonly string[] VirtualInterfaceNames = ["wireguard", "vpn", "tun", "tap", "wg", "loopback"];

    public static string Architecture(int architectureId) => architectureId switch
    {
        1 or 3 or 5 or 15 or 19 => "x86",
        2 or 4 or 6 or 16 or 18 or 20 or 30 or 33 or 36 or 37 => "x64",
        29 or 32 or 42 or 43 or 26 or 41 => "arm64",
        9 or 13 or 24 or 25 or 27 or 35 or 44 => "arm",
        45 => "riscv64",
        _ => "Not reported"
    };

    public static Inventory Apply(Inventory previous, JsonElement report, DateTimeOffset receivedAt)
    {
        NetworkInterfaceInfo[] interfaces = ReadInterfaces(report, previous.Interfaces ?? []);
        NetworkInterfaceInfo? primaryInterface = interfaces
            .Where(IsUsableInterface)
            .OrderByDescending(network => network.Address == previous.ConnectionAddress)
            .ThenBy(network => VirtualInterfaceNames.Any(name => network.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(network => network.Family.Contains('6'))
            .FirstOrDefault();

        bool cpuSampled = TryNumber(report, "cpu", out double cpu) && ResourceUsage.IsValidCpu(cpu);
        double memoryUsed = 0;
        bool memorySampled = TryNumber(report, "memoryTotal", out double memoryTotal)
            && memoryTotal is > 0 and <= MaximumReportedBytes
            && TryNumber(report, "memoryUsed", out memoryUsed)
            && memoryUsed <= memoryTotal;
        bool disksSampled = report.TryGetProperty("disks", out JsonElement disks)
            && disks.ValueKind == JsonValueKind.Array;

        long totalMemory = memorySampled ? (long)memoryTotal : previous.MemoryTotal;
        long usedMemory = memorySampled ? (long)memoryUsed : previous.MemoryUsed;
        string architecture = NormalizeArchitecture(Text(report, "architecture", previous.Architecture));

        return previous with
        {
            Hostname = Text(report, "hostname", previous.Hostname),
            Os = Text(report, "os", previous.Os),
            Architecture = architecture,
            Username = ReadUsers(report, previous.Username),
            Ip = primaryInterface?.Address ?? previous.Ip,
            Mac = primaryInterface?.Mac ?? previous.Mac,
            Cores = (int)Math.Min(Number(report, "cores", previous.Cores), 65536),
            MemoryTotal = totalMemory,
            MemoryUsed = usedMemory,
            Cpu = cpuSampled ? cpu : previous.Cpu,
            Uptime = (long)Math.Min(Number(report, "uptime", previous.Uptime), MaximumReportedBytes),
            CpuModel = Text(report, "cpuModel", previous.CpuModel ?? ""),
            Version = Text(report, "version", previous.Version == "0" ? "Not reported" : previous.Version),
            Manufacturer = Text(report, "manufacturer", previous.Manufacturer ?? ""),
            Model = Text(report, "model", previous.Model ?? ""),
            Interfaces = interfaces,
            Disks = disksSampled ? ReadDisks(disks) : previous.Disks,
            ReportedAt = receivedAt,
            CpuReportedAt = cpuSampled ? receivedAt : previous.CpuReportedAt,
            MemoryReportedAt = memorySampled ? receivedAt : previous.MemoryReportedAt,
            DisksReportedAt = disksSampled ? receivedAt : previous.DisksReportedAt,
            Diagnostic = Text(report, "diagnostic", "")
        };
    }

    /// <summary>Managed-agent timestamps use server receipt time, just like native reports.</summary>
    public static Inventory StampManagedResources(Inventory report, DateTimeOffset receivedAt)
    {
        bool validCpu = ResourceUsage.IsValidCpu(report.Cpu);
        bool validMemory = ResourceUsage.IsValidMemory(report.MemoryTotal, report.MemoryUsed)
            && report.MemoryTotal <= MaximumReportedBytes;

        return report with
        {
            Cpu = validCpu ? report.Cpu : -1,
            MemoryTotal = validMemory ? report.MemoryTotal : 0,
            MemoryUsed = validMemory ? report.MemoryUsed : 0,
            Disks = report.Disks.Where(ResourceUsage.IsValidDisk).Take(100).ToArray(),
            ReportedAt = receivedAt,
            CpuReportedAt = validCpu ? receivedAt : null,
            MemoryReportedAt = validMemory ? receivedAt : null,
            DisksReportedAt = receivedAt
        };
    }

    private static string NormalizeArchitecture(string architecture) => architecture switch
    {
        "x86_64" or "amd64" => "x64",
        "aarch64" => "arm64",
        "ia32" or "i686" => "x86",
        _ => architecture
    };

    private static string Text(JsonElement report, string name, string fallback)
    {
        if (!report.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? fallback : text[..Math.Min(text.Length, 500)];
    }

    private static bool TryNumber(JsonElement report, string name, out double number)
    {
        number = 0;
        return report.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out number) && double.IsFinite(number) && number >= 0;
    }

    private static double Number(JsonElement report, string name, double fallback) =>
        TryNumber(report, name, out double number) ? number : fallback;

    private static NetworkInterfaceInfo[] ReadInterfaces(JsonElement report, NetworkInterfaceInfo[] fallback)
    {
        if (!report.TryGetProperty("interfaces", out JsonElement interfaces) || interfaces.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        List<NetworkInterfaceInfo> validInterfaces = [];
        foreach (JsonElement item in interfaces.EnumerateArray().Take(256))
        {
            try
            {
                NetworkInterfaceInfo? network = item.Deserialize<NetworkInterfaceInfo>(Wire.Json);
                if (network is
                    {
                        Name: not null, Address: not null, Mac: not null, Family: not null
                    })
                {
                    validInterfaces.Add(network);
                }
            }
            catch (JsonException)
            {
                // A malformed interface must not discard the valid resource samples.
            }
        }

        return validInterfaces.ToArray();
    }

    private static bool IsUsableInterface(NetworkInterfaceInfo network) => !network.Internal
        && IPAddress.TryParse(network.Address, out IPAddress? address)
        && !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal
        && !network.Address.StartsWith("169.254.", StringComparison.Ordinal);

    private static DiskInfo[] ReadDisks(JsonElement disks)
    {
        List<DiskInfo> validDisks = [];
        foreach (JsonElement item in disks.EnumerateArray().Take(100))
        {
            try
            {
                DiskInfo? disk = item.Deserialize<DiskInfo>(Wire.Json);
                if (disk is not null && ResourceUsage.IsValidDisk(disk) && disk.Total <= MaximumReportedBytes)
                {
                    validDisks.Add(disk);
                }
            }
            catch (JsonException)
            {
                // Keep other valid volumes when a single entry is malformed.
            }
        }

        return validDisks.ToArray();
    }

    private static string ReadUsers(JsonElement report, string fallback)
    {
        if (!report.TryGetProperty("users", out JsonElement users) || users.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        return string.Join(", ", users.EnumerateArray()
            .Where(user => user.ValueKind == JsonValueKind.String)
            .Take(50)
            .Select(user => user.GetString()));
    }
}
