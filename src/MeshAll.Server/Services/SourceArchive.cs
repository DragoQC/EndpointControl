using System.Security.Cryptography;

namespace MeshAll.Server.Services;

public sealed class SourceArchive
{
    private readonly SemaphoreSlim gate = new(1);
    private (string Path, long Length, DateTime Modified, string Hash)? cached;

    public async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            return "";
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is { } value && value.Path == path && value.Length == file.Length && value.Modified == file.LastWriteTimeUtc)
            {
                return value.Hash;
            }
            await using FileStream stream = file.OpenRead();
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            cached = (path, file.Length, file.LastWriteTimeUtc, hash);
            return hash;
        }
        finally { gate.Release(); }
    }
}
