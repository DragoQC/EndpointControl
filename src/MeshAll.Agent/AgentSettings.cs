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
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    public static async Task<AgentSettings> LoadAsync(string[] args)
    {
        string path = Option(args, "--state") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshAll", "agent.json");
        if (File.Exists(path))
        {
            return JsonSerializer.Deserialize<AgentSettings>(await File.ReadAllTextAsync(path), Wire.Json) ?? throw new InvalidDataException("Invalid agent state.");
        }

        string server = (Option(args, "--server") ?? throw new ArgumentException("First run requires --server and --token. Use --help.")).TrimEnd('/');
        Uri? uri = new Uri(server);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
        {
            throw new ArgumentException("Use HTTPS for a remote server.");
        }

        AgentSettings? temporary = new AgentSettings(server, "", "", Option(args, "--fingerprint"), Option(args, "--file-root"), args.Contains("--require-consent"), Option(args, "--name"));
        using HttpClientHandler? handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => temporary.Validate(cert, errors);
        using HttpClient? http = new HttpClient(handler);
        string token = Option(args, "--token") ?? throw new ArgumentException("An enrollment token is required.");
        using HttpResponseMessage? response = await http.PostAsync(server + "/api/agents/enroll", new StringContent(JsonSerializer.Serialize(new EnrollmentRequest(token, temporary.Name ?? Environment.MachineName, OperatingSystem.IsWindows() ? "Windows" : "Linux"), Wire.Json), System.Text.Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Enrollment failed: " + await response.Content.ReadAsStringAsync());
        }

        EnrollmentResponse? enrolled = JsonSerializer.Deserialize<EnrollmentResponse>(await response.Content.ReadAsStringAsync(), Wire.Json) ?? throw new InvalidDataException("Invalid enrollment response.");
        AgentSettings? settings = temporary with
        {
            DeviceId = enrolled.DeviceId,
            Secret = enrolled.Secret
        };
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        byte[] state = JsonSerializer.SerializeToUtf8Bytes(settings, Wire.Json);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            FileSecurity? security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (SecurityIdentifier? sid in new[] {
                WindowsIdentity.GetCurrent().User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            await using FileStream file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
            await file.WriteAsync(state);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using FileStream file = new(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            await file.WriteAsync(state);
        }

        return settings;
    }
    public bool Validate(System.Security.Cryptography.X509Certificates.X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (string.IsNullOrWhiteSpace(Fingerprint))
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(Fingerprint.Replace(":", "")));
        }
        catch (FormatException)
        {
            return false;
        }
    }
    public ClientWebSocket Socket()
    {
        ClientWebSocket? socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) => Validate(certificate, errors);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Secret);
        socket.Options.SetRequestHeader("X-Device-Id", DeviceId);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return socket;
    }
}
