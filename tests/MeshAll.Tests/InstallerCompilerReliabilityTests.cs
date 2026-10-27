using MeshAll.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshAll.Tests;

public sealed class InstallerCompilerReliabilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(512)]
    public async Task AToolThatReportsSuccessButWritesAnInvalidInstallerCannotProduceADownload(int size)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using TestServer server = new();
        string compiler = Path.Combine(server.Store.DataDirectory, "fixture-compiler");
        await File.WriteAllTextAsync(compiler, $"#!/bin/sh\ntruncate -s {size} agent.msi\n");
        File.SetUnixFileMode(compiler, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MeshAll:MsiCompilerPath"] = compiler,
            ["MeshAll:MsiDatabaseToolPath"] = "/bin/true"
        }).Build();
        NativeMsiBuilder builder = new(configuration, NullLogger<NativeMsiBuilder>.Instance);
        (string Id, string Token) token = server.Store.CreateToken("Compiler fixture", "default", 1, 1);
        byte[] executable = server.Services.GetRequiredService<NativeAgentHub>().Executable(token.Token, NativeMsiPackage.Platform);
        await Assert.ThrowsAsync<IOException>(() => builder.BuildAsync(executable, "Fixture notices"));
    }
}
