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
    public void SavedExtensionsSelectTheInterpreterAndKeepTheScriptBody()
    {
        using var server=new TestServer();var store=server.Store;
        store.SaveScript(null,"Power check","Windows","Write-Output $PSVersionTable.PSEdition",".ps1");
        store.SaveScript(null,"Batch check.ps1","Windows","@echo off\r\necho %COMSPEC%\r\nexit /b 7",".cmd");
        var scripts=store.Scripts();Assert.Equal("Power check.ps1",scripts.Single(s=>s.Kind==".ps1").Name);
        var batch=scripts.Single(s=>s.Kind==".cmd");Assert.Equal("Batch check.cmd",batch.Name);Assert.Contains("exit /b 7",batch.Code);
        Assert.Equal(".cmd",ScriptTypes.Resolve(batch.Kind,true));
        Assert.Throws<InvalidOperationException>(()=>ScriptTypes.Resolve(batch.Kind,false));
        Assert.Throws<InvalidOperationException>(()=>store.SaveScript(null,"Wrong target","Linux","echo test",".ps1"));
    }
    [Fact]
    public void LegacyScriptSchemaKeepsCodeAndInfersAnExistingCmdExtension()
    {
        using var server=new TestServer();var store=server.Store;store.SaveScript(null,"Legacy.cmd","Windows","echo legacy");
        using(var connection=new SqliteConnection("Data Source="+Path.Combine(store.DataDirectory,"meshall.db")))
        {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="ALTER TABLE scripts DROP COLUMN kind";command.ExecuteNonQuery();
        }
        var s=server.Services;var migrated=new Store(s.GetRequiredService<IConfiguration>(),s.GetRequiredService<IWebHostEnvironment>(),s.GetRequiredService<IDataProtectionProvider>());
        var script=Assert.Single(migrated.Scripts());Assert.Equal(".cmd",script.Kind);Assert.Equal("echo legacy",script.Code);
    }
    [Fact]
    public async Task ExplicitShellFilePreservesExitAndWorkingDirectoryOnUnix()
    {
        if(OperatingSystem.IsWindows())return;
        var folder=Directory.CreateTempSubdirectory("MeshAll-Shell-Test-");
        try
        {
            var input=new CommandInput("printf 'script-file-output\\n'\nexit 7",folder.FullName,Shell:".sh");
            var command=NativeCommandBuilder.Build(input,false,"__SCRIPT_EXIT__");
            using var process=new Process{StartInfo=new("/bin/sh"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}};
            process.StartInfo.ArgumentList.Add("-c");process.StartInfo.ArgumentList.Add(command);process.Start();
            var output=await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();
            Assert.Contains("script-file-output",output);Assert.Contains("__SCRIPT_EXIT__7",output);Assert.Contains(folder.FullName,output);
        }
        finally{folder.Delete(true);}
    }
    [Fact]
    public void VncUsesTheDefaultOrRememberedDevicePort()
    {
        using var server=new TestServer();var store=server.Store;var token=store.CreateToken("VNC","default",1,1);
        var agent=store.Enroll(new(token.Token,"VNC host","Linux"));Assert.Equal(5900,store.VncPort(agent.DeviceId));
        store.SaveVncPort(agent.DeviceId,5907);Assert.Equal(5907,store.VncPort(agent.DeviceId));
        Assert.Throws<InvalidOperationException>(()=>store.SaveVncPort(agent.DeviceId,0));
        store.RemoveDevice(agent.DeviceId);store.RestoreConnectedDevice(agent.DeviceId);Assert.Equal(5907,store.VncPort(agent.DeviceId));
    }
}
