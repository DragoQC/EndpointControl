using System.Text;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public static class NativeCommandBuilder
{
    public static string Build(CommandInput input, bool windows, string marker)
    {
        string kind = ScriptTypes.Resolve(input.Shell, windows);
        string directory = input.Directory ?? "";
        if (!windows)
        {
            if (input.Shell == "auto")
            {
                return (directory.Length > 0 ? $"cd '{directory.Replace("'", "'\\''")}' || exit 1\n" : "") + input.Command + $"\nmeshall=$?; printf '\\n{marker}%s\\n' \"$meshall\"; pwd\n";
            }

            string delimiter = "MESHALL_SCRIPT_" + Guid.NewGuid().ToString("N");
            return (directory.Length > 0 ? $"cd '{directory.Replace("'", "'\\''")}' || exit 1\n" : "") +
                "umask 077\nmeshallRoot=$(mktemp -d) || exit 1\ntrap 'rm -rf \"$meshallRoot\"' 0\ncat > \"$meshallRoot/script.sh\" <<'" + delimiter + "'\n" +
                input.Command.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n" + delimiter + "\n" + (kind == "bash" ? "/bin/bash" : "/bin/sh") + " \"$meshallRoot/script.sh\"\nmeshall=$?\nprintf '\\n" + marker + "%s\\n' \"$meshall\"\npwd\n";
        }
        if (input.Shell == "auto")
        {
            return (directory.Length > 0 ? $"Set-Location -LiteralPath '{directory.Replace("'", "''")}' -ErrorAction Stop\n" : "") + input.Command + $"\n$meshall=$?; Write-Output ('{marker}' + [int](!$meshall)); (Get-Location).Path\n";
        }

        string content = kind == ".cmd" ? "@echo off\r\nchcp 65001 >nul\r\n" + input.Command : input.Command;
        UTF8Encoding encoding = new UTF8Encoding(kind == ".ps1");
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
        string encoded = Convert.ToBase64String(bytes);
        string root = "MeshAllScript_" + Guid.NewGuid().ToString("N");
        string program = kind == ".cmd" ? "$env:ComSpec" : "(Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe')";
        string invocation = ScriptTypes.PowerShellFileInvocation("(Join-Path ([IO.Path]::GetTempPath()) '" + root + "\\script.ps1')");
        string arguments = kind == ".cmd" ? "('/d /s /c \"\"'+$meshallFile+'\"\"')" : "'-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation)) + "'";
        return $$"""
            $meshallExit=1; $meshallRoot=$null; $meshallProcess=$null
            [Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
            try {
                {{(directory.Length > 0 ? $"Set-Location -LiteralPath '{directory.Replace("'", "''")}' -ErrorAction Stop" : "")}}
                $meshallRoot=Join-Path ([IO.Path]::GetTempPath()) '{{root}}'
                New-Item -ItemType Directory -Path $meshallRoot -ErrorAction Stop | Out-Null
                $meshallFile=Join-Path $meshallRoot 'script{{kind}}'
                [IO.File]::WriteAllBytes($meshallFile,[Convert]::FromBase64String('{{encoded}}'))
                $meshallInfo=New-Object Diagnostics.ProcessStartInfo
                $meshallInfo.FileName={{program}}
                $meshallInfo.Arguments={{arguments}}
                $meshallInfo.WorkingDirectory=(Get-Location).Path
                $meshallInfo.UseShellExecute=$false; $meshallInfo.CreateNoWindow=$true
                $meshallInfo.RedirectStandardOutput=$true; $meshallInfo.RedirectStandardError=$true
                $meshallInfo.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
                $meshallInfo.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
                $meshallProcess=New-Object Diagnostics.Process
                $meshallProcess.StartInfo=$meshallInfo
                [void]$meshallProcess.Start()
                $meshallOut=$meshallProcess.StandardOutput.ReadToEndAsync(); $meshallErr=$meshallProcess.StandardError.ReadToEndAsync()
                if(!$meshallProcess.WaitForExit({{Math.Clamp(input.TimeoutSeconds, 1, 60) * 1000}})) {
                    & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $meshallProcess.Id /T /F | Out-Null
                    throw 'Script timed out.'
                }
                $meshallProcess.WaitForExit()
                [Console]::Write($meshallOut.Result); [Console]::Error.Write($meshallErr.Result)
                $meshallExit=$meshallProcess.ExitCode
            } catch { Write-Error $_ -ErrorAction Continue }
            finally {
                if($meshallProcess){$meshallProcess.Dispose()}
                if($meshallRoot){Remove-Item -LiteralPath $meshallRoot -Recurse -Force -ErrorAction SilentlyContinue}
                Write-Output ''; Write-Output ('{{marker}}'+$meshallExit); (Get-Location).Path
            }
            """ + "\n\n";
    }
}
