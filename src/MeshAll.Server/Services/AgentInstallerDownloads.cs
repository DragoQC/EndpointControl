using System.Security.Cryptography;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed record AgentInstallerInfo(string FileName, string Version, string GroupName,
    string TokenId, DateTimeOffset Expires, long Size, string Sha256);

public sealed record AgentInstallerDownload(AgentInstallerInfo Information, byte[] Bytes);

/// <summary>Publishes the latest enrollment-ready installer in private instance storage.</summary>
public sealed class AgentInstallerDownloads(Store store, NativeAgentHub agents, INativeMsiBuilder builder, ILogger<AgentInstallerDownloads> logger)
{
    private const int MaximumPackageBytes = 64 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1);
    private string DirectoryPath => Path.Combine(store.DataDirectory, "installers");
    private string LegacyPackagePath => Path.Combine(DirectoryPath, "windows-x64.msi");
    private string MetadataPath => Path.Combine(DirectoryPath, "windows-x64.json");

    public AgentInstallerInfo? Current
    {
        get
        {
            try
            {
                if (!File.Exists(MetadataPath) || new FileInfo(MetadataPath).Length > 16384)
                {
                    return null;
                }
                AgentInstallerInfo? information = JsonSerializer.Deserialize<AgentInstallerInfo>(File.ReadAllText(MetadataPath), Wire.Json);
                return information is not null && ValidMetadata(information)
                    && new FileInfo(ResolvePackagePath(information)).Length == information.Size
                    && store.EnrollmentTokenUsable(information.TokenId)
                    ? information : null;
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public async Task<byte[]> PublishAsync(string token, CancellationToken cancellationToken)
    {
        byte[] package = await builder.BuildAsync(agents.Executable(token, NativeMsiPackage.Platform), agents.LicenseNotices(), cancellationToken);
        if (package.Length is < 1 or > MaximumPackageBytes)
        {
            throw new IOException("MSI packaging did not produce a valid package.");
        }
        await gate.WaitAsync(cancellationToken);
        string staging = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N"));
        string? artifact = null;
        bool createdArtifact = false;
        bool committed = false;
        try
        {
            // Compilation may take time: a revoked/exhausted key cannot publish afterward.
            TokenInfo enrollment = store.NativeEnrollmentToken(token);
            GroupInfo group = store.NativeEnrollmentGroup(token);
            AgentInstallerInfo information = new(builder.FileName, Wire.Version, group.Name, enrollment.Id,
                enrollment.Expires, package.Length, Convert.ToHexStringLower(SHA256.HashData(package)));
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(DirectoryPath);
            }
            else
            {
                Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            artifact = ArtifactPath(information.Sha256);
            createdArtifact = !File.Exists(artifact);
            await WritePrivateAsync(staging + ".msi", package, cancellationToken);
            await WritePrivateAsync(staging + ".json", JsonSerializer.SerializeToUtf8Bytes(information, Wire.Json), cancellationToken);
            File.Move(staging + ".msi", artifact, overwrite: true);
            cancellationToken.ThrowIfCancellationRequested();
            // The manifest is the sole commit point. A failed write leaves the old artifact intact.
            File.Move(staging + ".json", MetadataPath, overwrite: true);
            committed = true;
            foreach (string obsolete in Directory.EnumerateFiles(DirectoryPath, "windows-x64-*.msi"))
            {
                if (obsolete != artifact)
                {
                    TryRemove(obsolete);
                }
            }
            TryRemove(LegacyPackagePath);
        }
        finally
        {
            try
            {
                TryRemove(staging + ".msi");
                TryRemove(staging + ".json");
                if (!committed && createdArtifact && artifact is not null)
                {
                    TryRemove(artifact);
                }
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
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCheckedAsync(information, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AgentInstallerDownload?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            AgentInstallerInfo? information = Current;
            if (information is null)
            {
                return null;
            }
            return new(information, await ReadCheckedAsync(information, cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<byte[]> ReadCheckedAsync(AgentInstallerInfo information, CancellationToken cancellationToken)
    {
        if (!ValidMetadata(information) || !store.EnrollmentTokenUsable(information.TokenId))
        {
            throw new IOException("The installer is no longer available.");
        }
        string path = ResolvePackagePath(information);
        if (new FileInfo(path).Length != information.Size)
        {
            throw new IOException("The installer changed. Reload the download page and try again.");
        }
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != information.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != information.Sha256
            || !store.EnrollmentTokenUsable(information.TokenId))
        {
            throw new IOException("The installer changed. Reload the download page and try again.");
        }
        return bytes;
    }

    private string ArtifactPath(string hash) => Path.Combine(DirectoryPath, "windows-x64-" + hash + ".msi");

    private string ResolvePackagePath(AgentInstallerInfo information)
    {
        string artifact = ArtifactPath(information.Sha256);
        return File.Exists(artifact) ? artifact : LegacyPackagePath;
    }

    private static bool ValidMetadata(AgentInstallerInfo information) => information.Size is > 0 and <= MaximumPackageBytes
        && information.Sha256 is { Length: 64 } && information.Sha256.All(char.IsAsciiHexDigit)
        && information.TokenId is { Length: > 0 and <= 64 }
        && information.FileName is { Length: > 0 and <= 255 }
        && !information.FileName.Any(char.IsControl) && !information.FileName.Contains('\\')
        && information.FileName == Path.GetFileName(information.FileName);

    private void TryRemove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "An obsolete private installer file could not be removed");
        }
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
