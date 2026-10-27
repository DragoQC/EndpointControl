using System.Diagnostics;
using System.Text;
using MeshAll.Shared;

namespace MeshAll.Agent;

public sealed partial class AgentOperations
{
    private static async Task<CommandOutput> CommandAsync(CommandInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Command) || input.Command.Length > 16000)
        {
            throw new IOException("Enter a command up to 16,000 characters.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(input.TimeoutSeconds, 1, 60)));
        ProcessStartInfo info = new(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrWhiteSpace(input.Directory) ? Environment.CurrentDirectory : Path.GetFullPath(input.Directory)
        };
        string kind = ScriptTypes.Resolve(input.Shell, OperatingSystem.IsWindows());
        string? temporary = null;
        try
        {
            if (input.Shell != "auto")
            {
                temporary = Directory.CreateTempSubdirectory("MeshAllScript_").FullName;
                string file = Path.Combine(temporary, "script" + ScriptTypes.Extension(kind));
                UTF8Encoding encoding = new(kind == ".ps1");
                string text = kind == ".cmd" ? "@echo off\r\nchcp 65001 >nul\r\n" + input.Command : input.Command;
                await File.WriteAllBytesAsync(file, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(), ct);
                if (kind == ".cmd")
                {
                    info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                    info.Arguments = "/d /s /c \"\"" + file + "\"\"";
                }
                else if (kind == ".ps1")
                {
                    foreach (string argument in new[]
                    {
                        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(ScriptTypes.PowerShellFileInvocation("'" + file.Replace("'", "''") + "'")))
                    })
                    {
                        info.ArgumentList.Add(argument);
                    }
                }
                else
                {
                    info.FileName = kind == "bash" ? "/bin/bash" : "/bin/sh";
                    info.ArgumentList.Add(file);
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                info.ArgumentList.Add("-NoLogo");
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-EncodedCommand");
                info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); " + input.Command)));
            }
            else
            {
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add(input.Command);
            }

            using Process process = Process.Start(info) ?? throw new IOException("The command could not be started.");
            StringBuilder output = new();
            bool truncated = false;
            object outputLock = new();

            async Task Read(StreamReader stream)
            {
                char[] buffer = new char[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer)) > 0)
                {
                    lock (outputLock)
                    {
                        int take = Math.Min(count, Math.Max(0, 150000 - output.Length));
                        output.Append(buffer, 0, take);
                        truncated |= take < count;
                    }
                }
            }

            Task reads = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
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
                await reads;
                throw new IOException("Command canceled or timed out.");
            }
            await reads;
            return new(output.ToString(), process.ExitCode, info.WorkingDirectory, truncated);
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    Directory.Delete(temporary, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
