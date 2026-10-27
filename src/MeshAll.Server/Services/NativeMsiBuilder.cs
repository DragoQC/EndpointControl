using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public interface INativeMsiBuilder
{
    bool Available
    {
        get;
    }
    string FileName
    {
        get;
    }
    Task<byte[]> BuildAsync(byte[] executable, string notices, CancellationToken cancellationToken = default);
}

/// <summary>Builds a self-contained MSI locally, without sending enrollment data to another service.</summary>
public sealed class NativeMsiBuilder(IConfiguration configuration, ILogger<NativeMsiBuilder> logger) : INativeMsiBuilder
{
    private readonly SemaphoreSlim gate = new(1);
    private string Compiler => configuration["MeshAll:MsiCompilerPath"] ?? "/usr/bin/wixl";
    private string DatabaseTool => configuration["MeshAll:MsiDatabaseToolPath"] ?? Path.Combine(Path.GetDirectoryName(Compiler) ?? "", "msibuild");
    public bool Available => OperatingSystem.IsLinux()
        && Path.IsPathFullyQualified(Compiler) && File.Exists(Compiler)
        && Path.IsPathFullyQualified(DatabaseTool) && File.Exists(DatabaseTool);
    public string FileName => $"{ProductBrand.AgentDownloadName}-{Wire.Version}-win-x64.msi";

    public async Task<byte[]> BuildAsync(byte[] executable, string notices, CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            throw new IOException("MSI packaging requires wixl and msitools on the MeshAll server.");
        }
        string definition = NativeMsiPackage.Definition(executable, Wire.Version).ToString();
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
        {
            throw new IOException("MSI packaging is busy. Try again shortly.");
        }

        DirectoryInfo? directory = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("meshall-msi-");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, NativeMsiPackage.AgentFileName), executable, timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, NativeMsiPackage.NoticesFileName), notices, Encoding.UTF8, timeout.Token);
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, "EndpointControl.ico"),
                await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "NativeBinaries", "EndpointControl.ico"), timeout.Token), timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "agent.wxs"), definition, Encoding.UTF8, timeout.Token);
            await RunToolAsync(Compiler, ["--arch", "x64", "--output", "agent.msi", "agent.wxs"], directory.FullName, timeout.Token);
            // Debian's wixl 0.106 ignores MajorUpgrade.Schedule and ServiceInstall.Vital.
            // Normalize the actual MSI tables, then test those tables rather than only the authoring XML.
            await RunToolAsync(DatabaseTool, ["agent.msi", "-q",
                "UPDATE `InstallExecuteSequence` SET `Sequence` = 1501 WHERE `Action` = 'RemoveExistingProducts'",
                "-q", "UPDATE `ServiceInstall` SET `ErrorControl` = 32769 WHERE `ServiceInstall` = 'InstallAgentService'"],
                directory.FullName, timeout.Token);
            string package = Path.Combine(directory.FullName, "agent.msi");
            if (!File.Exists(package) || new FileInfo(package).Length is < 512 or > 64 * 1024 * 1024)
            {
                throw new IOException("MSI packaging did not produce a valid package.");
            }
            byte[] bytes = await File.ReadAllBytesAsync(package, timeout.Token);
            if (bytes.Length is < 512 or > 64 * 1024 * 1024
                || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
            {
                throw new IOException("MSI packaging did not produce a valid package.");
            }
            return bytes;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("MSI packaging timed out. Try again shortly.");
        }
        finally
        {
            try
            {
                directory?.Delete(recursive: true);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task RunToolAsync(string tool, string[] arguments, string directory, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(tool)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("MSI compiler could not be started.");
        }
        catch (Win32Exception)
        {
            throw new IOException("MSI compiler could not be started.");
        }
        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, errors);
                throw;
            }
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0)
            {
                string detail = await errors;
                logger.LogError("MSI compiler failed with exit code {Code}: {Detail}", process.ExitCode, detail[..Math.Min(detail.Length, 2000)]);
                throw new IOException("MSI compilation failed. Check the server's wixl installation.");
            }
        }
    }
}
