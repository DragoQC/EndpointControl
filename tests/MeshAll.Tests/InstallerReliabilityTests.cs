using System.Security.Cryptography;
using System.Text.Json;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshAll.Tests;

public sealed class InstallerReliabilityTests
{
    private sealed class FixtureBuilder : INativeMsiBuilder
    {
        public bool Available => true;
        public string FileName => "EndpointControlAgent-fixture.msi";
        public byte[] Package { get; set; } = [1, 2, 3, 4];
        public Action? DuringBuild
        {
            get; set;
        }
        public Task<byte[]> BuildAsync(byte[] executable, string notices, CancellationToken cancellationToken = default)
        {
            DuringBuild?.Invoke();
            return Task.FromResult(Package.ToArray());
        }
    }

    private static AgentInstallerDownloads Downloads(TestServer server, FixtureBuilder builder) => new(
        server.Store, server.Services.GetRequiredService<NativeAgentHub>(), builder, NullLogger<AgentInstallerDownloads>.Instance);

    [Fact]
    public async Task FailedPublicationCannotDestroyThePreviousDownload()
    {
        using TestServer server = new();
        FixtureBuilder builder = new();
        AgentInstallerDownloads downloads = Downloads(server, builder);
        (string Id, string Token) key = server.Store.CreateToken("Publication test", "default", 10, 1);
        byte[] original = builder.Package.ToArray();
        await downloads.PublishAsync(key.Token, CancellationToken.None);
        AgentInstallerInfo information = Assert.IsType<AgentInstallerInfo>(downloads.Current);
        string metadata = Path.Combine(server.Store.DataDirectory, "installers/windows-x64.json");
        byte[] manifest = await File.ReadAllBytesAsync(metadata);
        File.Delete(metadata);
        Directory.CreateDirectory(metadata); // An operational failure at the manifest commit point.
        builder.Package = [9, 8, 7, 6];
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => downloads.PublishAsync(key.Token, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(metadata);
            await File.WriteAllBytesAsync(metadata, manifest);
        }
        Assert.Equal(original, await downloads.ReadAsync(information, CancellationToken.None));
    }

    [Fact]
    public async Task RevocationAfterSelectingADownloadStillRejectsTheRead()
    {
        using TestServer server = new();
        AgentInstallerDownloads downloads = Downloads(server, new FixtureBuilder());
        (string Id, string Token) key = server.Store.CreateToken("Revoked download", "default", 10, 1);
        await downloads.PublishAsync(key.Token, CancellationToken.None);
        AgentInstallerInfo selected = Assert.IsType<AgentInstallerInfo>(downloads.Current);
        server.Store.RevokeToken(key.Id);
        await Assert.ThrowsAnyAsync<IOException>(() => downloads.ReadAsync(selected, CancellationToken.None));
    }

    [Fact]
    public async Task ThePreviouslyPublishedFlatFileRemainsDownloadableAfterAnUpgrade()
    {
        using TestServer server = new();
        AgentInstallerDownloads downloads = Downloads(server, new FixtureBuilder());
        (string Id, string Token) key = server.Store.CreateToken("Legacy download", "default", 10, 1);
        TokenInfo token = server.Store.NativeEnrollmentToken(key.Token);
        byte[] package = [10, 20, 30, 40];
        string directory = Path.Combine(server.Store.DataDirectory, "installers");
        Directory.CreateDirectory(directory);
        AgentInstallerInfo information = new("EndpointControlAgent-existing.msi", "0.10.0", "Unassigned",
            token.Id, token.Expires, package.Length, Convert.ToHexStringLower(SHA256.HashData(package)));
        await File.WriteAllBytesAsync(Path.Combine(directory, "windows-x64.msi"), package);
        await File.WriteAllBytesAsync(Path.Combine(directory, "windows-x64.json"), JsonSerializer.SerializeToUtf8Bytes(information, Wire.Json));
        Assert.Equal(information, downloads.Current);
        Assert.Equal(package, await downloads.ReadAsync(information, CancellationToken.None));
    }

    [Fact]
    public async Task AKeyRevokedDuringCompilationCannotReplaceTheCurrentInstaller()
    {
        using TestServer server = new();
        FixtureBuilder builder = new();
        AgentInstallerDownloads downloads = Downloads(server, builder);
        (string Id, string Token) first = server.Store.CreateToken("First installer", "default", 10, 1);
        await downloads.PublishAsync(first.Token, CancellationToken.None);
        (string Id, string Token) second = server.Store.CreateToken("Revoked while building", "default", 10, 1);
        builder.Package = [9, 8, 7];
        builder.DuringBuild = () => server.Store.RevokeToken(second.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloads.PublishAsync(second.Token, CancellationToken.None));
        Assert.Equal(first.Id, downloads.Current!.TokenId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (await downloads.ReadCurrentAsync(CancellationToken.None))!.Bytes);
    }

    [Fact]
    public async Task DownloadSnapshotsRemainCoherentAndStorageIsBoundedAcrossReplacement()
    {
        using TestServer server = new();
        FixtureBuilder builder = new();
        AgentInstallerDownloads downloads = Downloads(server, builder);
        (string Id, string Token) key = server.Store.CreateToken("Concurrent installer", "default", 10, 1);
        await downloads.PublishAsync(key.Token, CancellationToken.None);
        async Task Read()
        {
            for (int iteration = 0; iteration < 20; iteration++)
            {
                AgentInstallerDownload download = (await downloads.ReadCurrentAsync(CancellationToken.None))!;
                Assert.Equal(download.Information.Sha256, Convert.ToHexStringLower(SHA256.HashData(download.Bytes)));
                Assert.Equal(download.Information.Size, download.Bytes.LongLength);
            }
        }
        Task readers = Task.WhenAll(Read(), Read());
        for (int index = 0; index < 5; index++)
        {
            builder.Package = [(byte)index, 2, 3, 4];
            await downloads.PublishAsync(key.Token, CancellationToken.None);
        }
        await readers;
        Assert.Single(Directory.GetFiles(Path.Combine(server.Store.DataDirectory, "installers"), "*.msi"));
        Assert.Equal(10, server.Store.NativeEnrollmentToken(key.Token).Remaining);
    }
}
