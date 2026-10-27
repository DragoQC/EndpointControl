using System.Diagnostics;
using MeshAll.Shared;
using MeshAll.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshAll.Tests;

public class ScriptAndVncTests
{
    [Fact]
    public async Task BashScriptsUseBashAndKeepTheirBodyAndExitStatus()
    {
        using TestServer server = new();
        const string body = "\nvalues=(alpha beta)\n[[ ${values[1]} == beta ]] || exit 3\nprintf 'bash-only-%s\\n' \"${values[1]}\"\nexit 7\n";
        server.Store.SaveScript(null, "Unix check.bash", "Linux", body, "bash");
        ScriptInfo script = Assert.Single(server.Store.Scripts());
        Assert.Equal("Unix check.sh", script.Name);
        Assert.Equal("bash", script.Kind);
        Assert.Equal(body, script.Code);
        Assert.Equal("bash", ScriptTypes.Infer("test.bash", "auto"));
        Assert.True(ScriptTypes.Compatible("bash", false));
        Assert.False(ScriptTypes.Compatible("bash", true));
        Assert.Throws<InvalidOperationException>(() => server.Store.SaveScript(null, "wrong", "Windows", body, "bash"));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using Process process = new()
        {
            StartInfo = new("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(NativeCommandBuilder.Build(new(body, Shell: "bash"), false, "__BASH_EXIT__"));
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Contains("bash-only-beta", output);
        Assert.Contains("__BASH_EXIT__7", output);
    }

    [Fact]
    public void SavedExtensionsSelectTheInterpreterAndKeepTheScriptBody()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        store.SaveScript(null, "Power check", "Windows", "Write-Output $PSVersionTable.PSEdition", ".ps1");
        store.SaveScript(null, "Batch check.ps1", "Windows", "@echo off\r\necho %COMSPEC%\r\nexit /b 7", ".cmd");
        List<ScriptInfo>? scripts = store.Scripts();
        Assert.Equal("Power check.ps1", scripts.Single(s => s.Kind == ".ps1").Name);
        ScriptInfo? batch = scripts.Single(s => s.Kind == ".cmd");
        Assert.Equal("Batch check.cmd", batch.Name);
        Assert.Contains("exit /b 7", batch.Code);
        Assert.Equal(".cmd", ScriptTypes.Resolve(batch.Kind, true));
        Assert.Throws<InvalidOperationException>(() => ScriptTypes.Resolve(batch.Kind, false));
        Assert.Throws<InvalidOperationException>(() => store.SaveScript(null, "Wrong target", "Linux", "echo test", ".ps1"));
    }
    [Fact]
    public void LegacyScriptSchemaKeepsCodeAndInfersAnExistingCmdExtension()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        store.SaveScript(null, "Legacy.cmd", "Windows", "echo legacy");
        using (SqliteConnection? connection = new SqliteConnection("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db")))
        {
            connection.Open();
            using SqliteCommand? command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE scripts DROP COLUMN kind";
            command.ExecuteNonQuery();
        }
        IServiceProvider? s = server.Services;
        Store? migrated = new Store(s.GetRequiredService<IConfiguration>(), s.GetRequiredService<IWebHostEnvironment>(), s.GetRequiredService<IDataProtectionProvider>());
        ScriptInfo? script = Assert.Single(migrated.Scripts());
        Assert.Equal(".cmd", script.Kind);
        Assert.Equal("echo legacy", script.Code);
    }
    [Fact]
    public async Task ExplicitShellFilePreservesExitAndWorkingDirectoryOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        DirectoryInfo? folder = Directory.CreateTempSubdirectory("MeshAll-Shell-Test-");
        try
        {
            CommandInput? input = new CommandInput("printf 'script-file-output\\n'\nexit 7", folder.FullName, Shell: ".sh");
            string command = NativeCommandBuilder.Build(input, false, "__SCRIPT_EXIT__");
            using Process? process = new Process
            {
                StartInfo = new("/bin/sh")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(command);
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Contains("script-file-output", output);
            Assert.Contains("__SCRIPT_EXIT__7", output);
            Assert.Contains(folder.FullName, output);
        }
        finally
        {
            folder.Delete(true);
        }
    }
    [Fact]
    public void VncUsesTheDefaultOrRememberedDevicePort()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        (string Id, string Token) token = store.CreateToken("VNC", "default", 1, 1);
        EnrollmentResponse? agent = store.Enroll(new(token.Token, "VNC host", "Linux"));
        Assert.Equal(5900, store.VncPort(agent.DeviceId));
        store.SaveVncPort(agent.DeviceId, 5907);
        Assert.Equal(5907, store.VncPort(agent.DeviceId));
        Assert.Throws<InvalidOperationException>(() => store.SaveVncPort(agent.DeviceId, 0));
        store.RemoveDevice(agent.DeviceId);
        store.RestoreConnectedDevice(agent.DeviceId);
        Assert.Equal(5907, store.VncPort(agent.DeviceId));
    }
}
