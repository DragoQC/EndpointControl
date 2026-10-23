using System.Buffers.Binary;
using System.Text;

namespace MeshAll.Server.Services;

public static class NativeAgentExecutable
{
    // Native agent's established embedded-policy trailer (agentcore.c).
    public static ReadOnlySpan<byte> PolicyMarker => [0xB9,0x96,0x01,0x58,0x80,0x54,0x4A,0x19,0xB7,0xF7,0xE9,0xBE,0x44,0x91,0x4C,0x19];
    public static byte[] Build(byte[] executable,string policy,string notices)
    {
        var settings=Encoding.UTF8.GetBytes(policy);var license=Encoding.UTF8.GetBytes("\0MESHALL-EMBEDDED-LICENSES-BEGIN\n"+notices+"\nMESHALL-EMBEDDED-LICENSES-END\0");
        var result=new byte[checked(executable.Length+license.Length+settings.Length+20)];
        executable.CopyTo(result,0);license.CopyTo(result,executable.Length);settings.CopyTo(result,executable.Length+license.Length);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(result.Length-20,4),settings.Length);PolicyMarker.CopyTo(result.AsSpan(result.Length-16));return result;
    }
}
