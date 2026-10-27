using System.Text.Json;

namespace MeshAll.Shared;

public record EnrollmentRequest(string Token, string Name, string Platform);

public record EnrollmentResponse(string DeviceId, string Secret);

public record AgentMessage(string Kind, string? Id = null, string? Operation = null,
    JsonElement Payload = default, bool Success = true, string? Error = null);

public record DiskInfo(string Name, long Total, long Free);

public record Inventory(string Hostname, string Platform, string Os, string Architecture,
    string Username, string Ip, string Mac, int Cores, long MemoryTotal, long MemoryUsed,
    double Cpu, long Uptime, string Version, string[] Capabilities, DiskInfo[] Disks,
    string? CpuModel = null, string? Manufacturer = null, string? Model = null,
    NetworkInterfaceInfo[]? Interfaces = null, DateTimeOffset? ReportedAt = null,
    string? ConnectionAddress = null, int AgentArchitecture = 0, string? Diagnostic = null,
    DateTimeOffset? CpuReportedAt = null, DateTimeOffset? MemoryReportedAt = null,
    DateTimeOffset? DisksReportedAt = null);

public record NetworkInterfaceInfo(string Name, string Address, string Family, string Mac, bool Internal);

public record CommandInput(string Command, string? Directory = null, int TimeoutSeconds = 30, string Shell = "auto");

public record CommandOutput(string Output, int ExitCode, string Directory, bool Truncated = false);

public record FileInput(string Path, string? Content = null);

public record RemoteFile(string Name, string Path, bool Directory, long Size, DateTimeOffset Modified);

public record FileListing(string Path, string? Parent, RemoteFile[] Entries);

public record FileContent(string Name, string Base64);

public record ScreenFrame(string Base64, int Width, int Height);

public record InputEvent(string Type, double X = 0, double Y = 0, int Button = 0,
    int Key = 0, int Delta = 0, string? Text = null);

public record ProcessInfo(int Id, string Name, long Memory);

public record PowerInput(string Action);

public record WakeInput(string Mac);

public record SessionRequest(string SessionId, string Actor, bool RequireConsent = true,
    string Kind = "desktop", string? Host = null, int Port = 0, bool Notify = true, bool Toolbar = true, bool ViewOnly = false);

public record SessionInput(string SessionId, InputEvent? Input = null, string? Base64 = null, int Columns = 100, int Rows = 30);

public record OpenedSession(string SessionId);
