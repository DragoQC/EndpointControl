using System.Net.Security;
using System.Net.WebSockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Agent;

public sealed record AgentSettings(string Server, string DeviceId, string Secret, string? Fingerprint = null,
    string? FileRoot = null, bool RequireConsent = false, string? Name = null)
{
    public static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    public static async Task<AgentSettings> LoadAsync(string[] args)
    {
        var path = Option(args, "--state") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshAll", "agent.json");
        if (File.Exists(path)) return JsonSerializer.Deserialize<AgentSettings>(await File.ReadAllTextAsync(path), Wire.Json) ?? throw new InvalidDataException("Invalid agent state.");
        var server = (Option(args, "--server") ?? throw new ArgumentException("First run requires --server and --token. Use --help.")).TrimEnd('/');
        var uri = new Uri(server);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Use HTTPS for a remote server.");
        var temporary = new AgentSettings(server, "", "", Option(args, "--fingerprint"), Option(args, "--file-root"), args.Contains("--require-consent"), Option(args, "--name"));
        using var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => temporary.Validate(cert, errors);
        using var http = new HttpClient(handler);
        var token = Option(args, "--token") ?? throw new ArgumentException("An enrollment token is required.");
        using var response = await http.PostAsync(server + "/api/agents/enroll", new StringContent(JsonSerializer.Serialize(new EnrollmentRequest(token, temporary.Name ?? Environment.MachineName, OperatingSystem.IsWindows() ? "Windows" : "Linux"), Wire.Json), System.Text.Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Enrollment failed: " + await response.Content.ReadAsStringAsync());
        var enrolled = JsonSerializer.Deserialize<EnrollmentResponse>(await response.Content.ReadAsStringAsync(), Wire.Json) ?? throw new InvalidDataException("Invalid enrollment response.");
        var settings = temporary with { DeviceId = enrolled.DeviceId, Secret = enrolled.Secret };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings, Wire.Json));
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return settings;
    }
    public bool Validate(System.Security.Cryptography.X509Certificates.X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (string.IsNullOrWhiteSpace(Fingerprint)) return errors == SslPolicyErrors.None;
        if (certificate is null) return false;
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(Fingerprint.Replace(":", ""))); }
        catch (FormatException) { return false; }
    }
    public ClientWebSocket Socket()
    {
        var socket = new ClientWebSocket(); socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) => Validate(certificate, errors);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Secret); socket.Options.SetRequestHeader("X-Device-Id", DeviceId);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20); return socket;
    }
}
