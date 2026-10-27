using System.Runtime.InteropServices;

namespace MeshAll.Server.Services;

/// <summary>Use the same host-key preference order as the local SSH codec.</summary>
public static class SshHostKeyPreferences
{
    private static readonly string[] Fallback = ["ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-ed25519", "ssh-rsa"];
    private static readonly Lazy<IReadOnlyList<string>> Preferred = new(Load);
    public static IReadOnlyList<string> Algorithms => Preferred.Value;

    private static IReadOnlyList<string> Load()
    {
        IntPtr session = IntPtr.Zero;
        IntPtr algorithms = IntPtr.Zero;
        try
        {
            if (Initialize(0) != 0)
            {
                return Fallback;
            }

            session = CreateSession(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (session == IntPtr.Zero)
            {
                return Fallback;
            }

            int count = SupportedAlgorithms(session, 1, out algorithms); // LIBSSH2_METHOD_HOSTKEY
            if (count is < 1 or > 64)
            {
                return Fallback;
            }

            List<string> names = [];
            for (int index = 0; index < count; index++)
            {
                string? name = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(algorithms, index * IntPtr.Size));
                if (name is null || name.Contains("-cert-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.StartsWith("rsa-sha2-", StringComparison.Ordinal))
                {
                    name = "ssh-rsa";
                }

                if (Fallback.Contains(name, StringComparer.Ordinal) && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
            return names.Count == 0 ? Fallback : names;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Fallback;
        }
        finally
        {
            if (algorithms != IntPtr.Zero)
            {
                Free(session, algorithms);
            }

            if (session != IntPtr.Zero)
            {
                FreeSession(session);
            }
        }
    }

    [DllImport("libssh2.so.1", EntryPoint = "libssh2_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Initialize(int flags);
    [DllImport("libssh2.so.1", EntryPoint = "libssh2_session_init_ex", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateSession(IntPtr allocate, IntPtr free, IntPtr reallocate, IntPtr context);
    [DllImport("libssh2.so.1", EntryPoint = "libssh2_session_supported_algs", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SupportedAlgorithms(IntPtr session, int method, out IntPtr algorithms);
    [DllImport("libssh2.so.1", EntryPoint = "libssh2_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Free(IntPtr session, IntPtr memory);
    [DllImport("libssh2.so.1", EntryPoint = "libssh2_session_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FreeSession(IntPtr session);
}
