using System.Text.RegularExpressions;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed record RemoteHostIdentity(string Protocol, string Value, bool Windows);

public sealed class RemoteIdentityResolver(Store store, AgentGateway agents, IConfiguration configuration)
{
    public async Task<RemoteHostIdentity> ResolveAsync(DeviceInfo device, string protocol, int port, CancellationToken cancellationToken)
    {
        string? configured = configuration[$"MeshAll:TrustedRemoteIdentities:{device.Id}:{protocol}:{port}"];
        string? value = configured;
        if (value is null && agents.Online(device.Id))
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            CommandOutput output = await agents.ExecuteAsync<CommandOutput>(device.Id, "command", Probe(protocol, device.Inventory?.Platform == "Windows"), timeout.Token);
            if (output.ExitCode != 0)
            {
                throw new IOException("The remote host identity could not be verified through the agent.");
            }
            value = protocol == "rdp" ? NormalizeRdp(output.Output) : NormalizeSsh(output.Output);
            store.SaveRemoteIdentity(device.Id, protocol, port, value);
        }
        value ??= store.RemoteIdentity(device.Id, protocol, port);
        if (value is null)
        {
            throw new IOException("Connect the agent once to verify the remote host identity, or configure a trusted host identity.");
        }
        value = protocol == "rdp" ? NormalizeRdp(value) : NormalizeSsh(value);
        return new(protocol, value, device.Inventory?.Platform == "Windows");
    }

    public static string NormalizeRdp(string value)
    {
        Match hash = Regex.Match(value, @"(?i)(?<![0-9a-f])(?:[0-9a-f]{2}:){31}[0-9a-f]{2}(?![0-9a-f])|(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!hash.Success)
        {
            throw new IOException("The remote host identity could not be verified through the agent.");
        }
        return hash.Value.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    public static string NormalizeSsh(string value)
    {
        foreach (string algorithm in SshHostKeyPreferences.Algorithms)
        {
            foreach (string line in value.Split('\n'))
            {
                string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int index = Array.IndexOf(fields, algorithm);
                if (index >= 0 && index + 1 < fields.Length && fields[index + 1].Length <= 8192)
                {
                    try
                    {
                        byte[] key = Convert.FromBase64String(fields[index + 1]);
                        if (key.Length >= 32)
                        {
                            return algorithm + " " + fields[index + 1];
                        }
                    }
                    catch (FormatException) { }
                }
            }
        }
        throw new IOException("The remote host identity could not be verified through the agent.");
    }

    private static CommandInput Probe(string protocol, bool windows) => protocol == "rdp"
        ? windows ? new("""
            $ErrorActionPreference='Stop'
            $setting=Get-CimInstance -Namespace root/cimv2/TerminalServices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'"
            $certificate=Get-ChildItem 'Cert:\LocalMachine\Remote Desktop','Cert:\LocalMachine\My' | Where-Object Thumbprint -eq $setting.SSLCertificateSHA1Hash | Select-Object -First 1
            if(!$certificate){throw 'RDP certificate unavailable'}
            $sha=[Security.Cryptography.SHA256]::Create()
            try {[BitConverter]::ToString($sha.ComputeHash($certificate.RawData)).Replace('-','')} finally {$sha.Dispose()}
            """, Shell: ".ps1")
            : new("openssl x509 -in /etc/xrdp/cert.pem -noout -fingerprint -sha256", Shell: ".sh")
        : windows ? new("Get-Content 'C:\\ProgramData\\ssh\\ssh_host_ed25519_key.pub' -ErrorAction Stop", Shell: ".ps1")
            : new("for key in /etc/ssh/ssh_host_*_key.pub; do if [ -r \"$key\" ]; then cat \"$key\"; fi; done", Shell: ".sh");
}
