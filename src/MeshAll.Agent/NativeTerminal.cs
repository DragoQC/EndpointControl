using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MeshAll.Agent;

public sealed class NativeTerminal(Stream input, Stream output, Action<int, int> resize, Func<ValueTask> closeOperation) : IAsyncDisposable
{
    public Stream Input { get; } = input;

    public Stream Output { get; } = output;

    public void Resize(int columns, int rows) => resize(columns, rows);

    public ValueTask DisposeAsync() => closeOperation();

    public static NativeTerminal Start(bool powershell)
    {
        if (OperatingSystem.IsWindows())
        {
            return Windows(powershell);
        }

        if (OperatingSystem.IsLinux())
        {
            return Linux();
        }

        throw new PlatformNotSupportedException("An interactive PTY is implemented for Windows and Linux.");
    }

    [SupportedOSPlatform("windows")]
    private static NativeTerminal Windows(bool powershell)
    {
        if (!CreatePipe(out nint inputRead, out nint inputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception();
        }

        if (!CreatePipe(out nint outputRead, out nint outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception();
        }

        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero;
        try
        {
            int result = CreatePseudoConsole(new(100, 30), inputRead, outputWrite, 0, out console);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size) || !UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception();
            }

            StartupEx startup = new StartupEx
            {
                Startup = new Startup
                {
                    Size = Marshal.SizeOf<StartupEx>()
                },
                Attributes = attributes
            };
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), powershell ? "WindowsPowerShell\\v1.0\\powershell.exe" : "cmd.exe");
            StringBuilder? command = new StringBuilder('"' + executable + '"' + (powershell ? " -NoLogo -NoProfile" : " /D"));
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x80000, IntPtr.Zero, null, ref startup, out ProcessInfo process))
            {
                throw new Win32Exception();
            }

            CloseHandle(process.Thread);
            CloseHandle(process.Process);
            Process? child = Process.GetProcessById(process.ProcessId);
            FileStream? input = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 4096, false);
            FileStream? output = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 4096, false);
            inputWrite = outputRead = IntPtr.Zero;
            nint handle = console;
            console = IntPtr.Zero;
            return new(input, output, (columns, rows) => ResizePseudoConsole(handle, new((short)columns, (short)rows)), () =>
{
    try
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
        }
    }
    catch (InvalidOperationException)
    {
    }
    input.Dispose();
    ClosePseudoConsole(handle);
    output.Dispose();
    child.Dispose();
    return ValueTask.CompletedTask;
});
        }
        finally
        {
            CloseHandle(inputRead);
            CloseHandle(outputWrite);
            if (inputWrite != IntPtr.Zero)
            {
                CloseHandle(inputWrite);
            }

            if (outputRead != IntPtr.Zero)
            {
                CloseHandle(outputRead);
            }

            if (console != IntPtr.Zero)
            {
                ClosePseudoConsole(console);
            }

            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static NativeTerminal Linux()
    {
        int master = posix_openpt(2 | 0x100 | 0x80000);
        if (master < 0 || grantpt(master) != 0 || unlockpt(master) != 0)
        {
            throw new IOException("Unable to allocate a pseudo-terminal.");
        }

        string name = Marshal.PtrToStringUTF8(ptsname(master)) ?? throw new IOException("Missing PTY name.");
        nint actions = Marshal.AllocHGlobal(256);
        List<nint>? allocations = new List<IntPtr>();
        IntPtr ArrayOf(IEnumerable<string> values)
        {
            nint[] strings = values.Select(v =>
{
    nint p = Marshal.StringToHGlobalAnsi(v);
    allocations.Add(p);
    return p;
}).Append(IntPtr.Zero).ToArray();
            nint array = Marshal.AllocHGlobal(strings.Length * IntPtr.Size);
            Marshal.Copy(strings, 0, array, strings.Length);
            allocations.Add(array);
            return array;
        }
        try
        {
            if (posix_spawn_file_actions_init(actions) != 0 || posix_spawn_file_actions_addopen(actions, 0, name, 2, 0) != 0 || posix_spawn_file_actions_adddup2(actions, 0, 1) != 0 || posix_spawn_file_actions_adddup2(actions, 0, 2) != 0)
            {
                throw new IOException("Unable to configure the PTY.");
            }

            nint arguments = ArrayOf(["setsid", "--ctty", "/bin/bash", "--noprofile", "--norc", "-i"]);
            IEnumerable<string>? environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Where(e => e.Key.ToString() != "TERM").Select(e => e.Key + "=" + e.Value).Append("TERM=xterm-256color");
            int result = posix_spawnp(out int pid, "setsid", actions, IntPtr.Zero, arguments, ArrayOf(environment));
            if (result != 0)
            {
                throw new IOException("Unable to start shell: errno " + result);
            }

            int inputFd = dup(master);
            if (inputFd < 0)
            {
                throw new IOException("Unable to duplicate PTY descriptor.");
            }

            FileStream? output = new FileStream(new SafeFileHandle((IntPtr)master, true), FileAccess.Read, 4096, false);
            master = -1;
            FileStream? input = new FileStream(new SafeFileHandle((IntPtr)inputFd, true), FileAccess.Write, 4096, false);
            void Resize(int columns, int rows)
            {
                WindowSize dimensions = new WindowSize
                {
                    Rows = (ushort)rows,
                    Columns = (ushort)columns
                };
                ioctl(inputFd, 0x5414, ref dimensions);
            }
            Resize(100, 30);
            return new(input, output, Resize, () =>
{
    kill(-pid, 9);
    input.Dispose();
    output.Dispose();
    waitpid(pid, out _, 0);
    return ValueTask.CompletedTask;
});
        }
        finally
        {
            posix_spawn_file_actions_destroy(actions);
            Marshal.FreeHGlobal(actions);
            foreach (nint pointer in allocations)
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (master >= 0)
            {
                close(master);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct Startup
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill;
        public uint Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupEx
    {
        public Startup Startup;
        public IntPtr Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows, Columns, Width, Height;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupEx startup, out ProcessInfo process);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);

    [DllImport("libc")] private static extern int grantpt(int fd);

    [DllImport("libc")] private static extern int unlockpt(int fd);

    [DllImport("libc")] private static extern IntPtr ptsname(int fd);

    [DllImport("libc")] private static extern int dup(int fd);

    [DllImport("libc")] private static extern int close(int fd);

    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport("libc", CharSet = CharSet.Ansi)] private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, string name, int flags, uint mode);

    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int target);

    [DllImport("libc", CharSet = CharSet.Ansi)] private static extern int posix_spawnp(out int pid, string file, IntPtr actions, IntPtr attributes, IntPtr argv, IntPtr environment);

    [DllImport("libc")] private static extern int ioctl(int fd, uint request, ref WindowSize size);

    [DllImport("libc")] private static extern int kill(int pid, int signal);

    [DllImport("libc")] private static extern int waitpid(int pid, out int status, int flags);
}
