using System.Security.Cryptography;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed record AgentInstallerInfo(string FileName, string Version, string GroupName,
    string TokenId, DateTimeOffset Expires, long Size, string Sha256);

/// <summary>Publishes the latest enrollment-ready installer in private instance storage.</summary>
public sealed class AgentInstallerDownloads(Store store, NativeAgentHub agents, NativeMsiBuilder builder)
{
    private readonly SemaphoreSlim gate = new(1);
    private string DirectoryPath => Path.Combine(store.DataDirectory, "installers");
    private string PackagePath => Path.Combine(DirectoryPath, "windows-x64.msi");
    private string MetadataPath => Path.Combine(DirectoryPath, "windows-x64.json");

    public AgentInstallerInfo? Current
    {
        get
        {
            try
            {
                if (!File.Exists(MetadataPath) || !File.Exists(PackagePath))
                {
                    return null;
                }
                AgentInstallerInfo? information = JsonSerializer.Deserialize<AgentInstallerInfo>(File.ReadAllText(MetadataPath), Wire.Json);
                return information is not null && new FileInfo(PackagePath).Length == information.Size
                    && store.Tokens().Any(token => token.Id == information.TokenId && token.Expires > DateTimeOffset.UtcNow && token.Remaining > 0)
                    ? information : null;
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                return null;
            }
        }
    }

    public async Task<byte[]> PublishAsync(string token, CancellationToken cancellationToken)
    {
        TokenInfo enrollment = store.NativeEnrollmentToken(token);
        GroupInfo group = store.NativeEnrollmentGroup(token);
        byte[] package = await builder.BuildAsync(agents.Executable(token, NativeMsiPackage.Platform), agents.LicenseNotices(), cancellationToken);
        AgentInstallerInfo information = new(builder.FileName, Wire.Version, group.Name, enrollment.Id,
            enrollment.Expires, package.Length, Convert.ToHexStringLower(SHA256.HashData(package)));
        await gate.WaitAsync(cancellationToken);
        string staging = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N"));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(DirectoryPath);
            }
            else
            {
                Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await WritePrivateAsync(staging + ".msi", package, cancellationToken);
            await WritePrivateAsync(staging + ".json", JsonSerializer.SerializeToUtf8Bytes(information, Wire.Json), cancellationToken);
            File.Move(staging + ".msi", PackagePath, overwrite: true);
            File.Move(staging + ".json", MetadataPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(staging + ".msi");
                File.Delete(staging + ".json");
            }
            finally
            {
                gate.Release();
            }
        }
        return package;
    }

    public async Task<byte[]> ReadAsync(AgentInstallerInfo information, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(PackagePath, cancellationToken);
        if (bytes.LongLength != information.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != information.Sha256)
        {
            throw new IOException("The installer changed. Reload the download page and try again.");
        }
        return bytes;
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using FileStream file = new(path, options);
        await file.WriteAsync(bytes, cancellationToken);
    }
}
