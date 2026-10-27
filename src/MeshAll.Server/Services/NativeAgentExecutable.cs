using System.Buffers.Binary;
using System.Text;

namespace MeshAll.Server.Services;

public static class NativeAgentExecutable
{
    public static string EmbeddedPolicy(byte[] executable)
    {
        if (executable.Length < 20 || !executable.AsSpan(executable.Length - 16).SequenceEqual(PolicyMarker))
        {
            throw new InvalidOperationException("The executable does not contain an enrollment policy.");
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(executable.AsSpan(executable.Length - 20, 4));
        if (length is < 1 or > 65536 || length > executable.Length - 20)
        {
            throw new InvalidOperationException("The executable does not contain an enrollment policy.");
        }
        return Encoding.UTF8.GetString(executable, executable.Length - 20 - length, length);
    }

    public static string LicenseNotices() => "EndpointControl native agent\nCopyright Intel Corporation and contributors. EndpointControl modifications: DragoQC and contributors.\nSource: https://github.com/DragoQC/EndpointControl/tree/main/components/agent-native\n\n" +
        string.Join("\n\n", new[] { "LICENSE", "MeshAll-MIT-LICENSE", "NOTICES.md" }
            .Select(name => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "NativeBinaries", name))));

    // Native agent's established embedded-policy trailer (agentcore.c).
    public static ReadOnlySpan<byte> PolicyMarker => [0xB9, 0x96, 0x01, 0x58, 0x80, 0x54, 0x4A, 0x19, 0xB7, 0xF7, 0xE9, 0xBE, 0x44, 0x91, 0x4C, 0x19];

    public static byte[] Build(byte[] executable, string policy, string notices)
    {
        byte[] settings = Encoding.UTF8.GetBytes(policy);
        byte[] license = Encoding.UTF8.GetBytes("\0MESHALL-EMBEDDED-LICENSES-BEGIN\n" + notices + "\nMESHALL-EMBEDDED-LICENSES-END\0");
        byte[] result = new byte[checked(executable.Length + license.Length + settings.Length + 20)];
        executable.CopyTo(result, 0);
        license.CopyTo(result, executable.Length);
        settings.CopyTo(result, executable.Length + license.Length);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(result.Length - 20, 4), settings.Length);
        PolicyMarker.CopyTo(result.AsSpan(result.Length - 16));
        return result;
    }
}
