using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Agent;

// MeshAll's own desktop broker: a dedicated interactive-session process owns
// the visible consent prompt, capture and input. No desktop is captured before
// that process has received an explicit approval when consent is required.
[SupportedOSPlatform("windows")]
public static class WindowsDesktop
{
    private record Request(string Operation, JsonElement Payload);

    private record Reply(bool Success, JsonElement Payload, string? Error = null);

    public sealed class Connection : IAgentDesktop
    {
        private readonly NamedPipeServerStream pipe;
        private readonly Process process;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim gate = new(1);
        private bool disposed;

        internal Connection(NamedPipeServerStream pipe, Process process)
        {
            this.pipe = pipe;
            this.process = process;
            reader = new(pipe, Encoding.UTF8, false, 65536, true);
            writer = new(pipe, new UTF8Encoding(false), 65536, true)
            {
                AutoFlush = true
            };
        }
        internal async Task<T> CallAsync<T>(string operation, object payload, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Request(operation, Wire.Element(payload)), Wire.Json).AsMemory(), ct);
                string line = await reader.ReadLineAsync(ct) ?? throw new IOException("The desktop broker closed.");
                Reply? reply = JsonSerializer.Deserialize<Reply>(line, Wire.Json) ?? throw new IOException("Invalid desktop broker response.");
                if (!reply.Success)
                {
                    throw new IOException(reply.Error ?? "Desktop request failed.");
                }

                return Wire.Read<T>(reply.Payload);
            }
            finally
            {
                gate.Release();
            }
        }
        public Task<ScreenFrame> FrameAsync(CancellationToken ct) => CallAsync<ScreenFrame>("frame", new
        {
        }, ct);

        public Task<string> InputAsync(InputEvent input, CancellationToken ct) => CallAsync<string>("input", input, ct);

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            await pipe.DisposeAsync();
            process.Dispose();
        }
    }

    public static async Task<Connection> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        string name = "MeshAll-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream? pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        Connection? connection = null;
        try
        {
            string executable = Environment.ProcessPath ?? throw new IOException("Agent executable path is unavailable.");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Desktop access requires the published MeshAllAgent executable.");
            }

            using (Process? self = Process.GetCurrentProcess())
            {
                process = self.SessionId == 0 ? StartInteractive(executable, name) : Process.Start(new ProcessStartInfo(executable)
                {
                    ArgumentList =
{
                        "--broker",
                        "--pipe",
                        name
},
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }

            if (process is null)
            {
                throw new IOException("The interactive desktop broker could not be started.");
            }

            using CancellationTokenSource? opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opening.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(opening.Token);
            connection = new(pipe, process);
            bool approved = await connection.CallAsync<bool>("open", request, ct);
            if (!approved)
            {
                throw new IOException("The person at the PC denied screen access, or the request timed out.");
            }

            return connection;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            else
            {
                pipe.Dispose();
                try
                {
                    if (process is
                        {
                            HasExited: false
                        })
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                process?.Dispose();
            }
            throw;
        }
    }
    public static async Task<bool> AskAsync(string actor, string kind, CancellationToken ct)
    {
        await using Connection? broker = await OpenAsync(new(Guid.NewGuid().ToString("N"), actor, true, kind), ct);
        return true;
    }

    public static void RunBroker(string pipeName)
    {
        if (!pipeName.StartsWith("MeshAll-", StringComparison.Ordinal) || pipeName.Length > 80)
        {
            throw new ArgumentException("Invalid broker pipe.");
        }

        SetProcessDpiAwarenessContext((IntPtr)(-4));
        using NamedPipeClientStream? pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(10000);
        using StreamReader? reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, true);
        using StreamWriter? writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true)
        {
            AutoFlush = true
        };
        bool opened = false;
        HashSet<ushort>? pressed = new HashSet<ushort>();
        bool first = true;
        try
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                object result;
                try
                {
                    Request? request = JsonSerializer.Deserialize<Request>(line, Wire.Json) ?? throw new IOException("Invalid broker command.");
                    if (first)
                    {
                        first = false;
                        if (request.Operation != "open")
                        {
                            throw new IOException("A desktop must be opened first.");
                        }

                        SessionRequest? session = Wire.Read<SessionRequest>(request.Payload);
                        string actor = new string(session.Actor.Where(c => !char.IsControl(c)).Take(100).ToArray());
                        bool allowed = !session.RequireConsent || MessageBoxTimeout(IntPtr.Zero,
                            actor + " is requesting " + (session.Kind == "desktop" ? "permission to view and control your screen" : session.Kind.ToUpperInvariant() + " screen access") + ".\n\nAllow this session? You will be asked again for the next session.",
                            "EndpointControl Agent - screen access request", 0x4 | 0x20 | 0x100 | 0x10000 | 0x40000, 0, 30000) == 6;
                        writer.WriteLine(JsonSerializer.Serialize(new Reply(true, Wire.Element(allowed)), Wire.Json));
                        if (!allowed || session.Kind != "desktop")
                        {
                            return;
                        }

                        opened = true;
                        continue;
                    }
                    if (!opened)
                    {
                        throw new IOException("Screen access has not been approved.");
                    }

                    using InputDesktop? desktop = new InputDesktop();
                    result = request.Operation switch
                    {
                        "frame" => Capture(),
                        "input" => ApplyInput(Wire.Read<InputEvent>(request.Payload), pressed),
                        _ => throw new IOException("Unsupported desktop operation.")
                    };
                    writer.WriteLine(JsonSerializer.Serialize(new Reply(true, Wire.Element(result)), Wire.Json));
                }
                catch (Exception ex)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new Reply(false, Wire.Element(""), ex.Message), Wire.Json));
                }
            }
        }
        finally
        {
            try
            {
                ApplyInput(new("release"), pressed);
            }
            catch (Win32Exception)
            {
            }
        }
    }

    private static ScreenFrame Capture()
    {
        int x = GetSystemMetrics(76);
        int y = GetSystemMetrics(77);
        int width = GetSystemMetrics(78);
        int height = GetSystemMetrics(79);
        if (width < 1 || height < 1 || width > 16384 || height > 16384)
        {
            throw new IOException("The interactive display is unavailable.");
        }

        using Bitmap? bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics? graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        using MemoryStream? buffer = new MemoryStream();
        using EncoderParameters? options = new EncoderParameters(1);
        options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
        bitmap.Save(buffer, ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg"), options);
        return new(Convert.ToBase64String(buffer.ToArray()), width, height);
    }
    private static string ApplyInput(InputEvent input, HashSet<ushort> pressed)
    {
        List<NativeInput>? events = new List<NativeInput>();
        if (input.Type is "move" or "down" or "up")
        {
            SetCursorPos(GetSystemMetrics(76) + (int)(Math.Clamp(input.X, 0, 1) * Math.Max(0, GetSystemMetrics(78) - 1)), GetSystemMetrics(77) + (int)(Math.Clamp(input.Y, 0, 1) * Math.Max(0, GetSystemMetrics(79) - 1)));
        }

        switch (input.Type)
        {
            case "down":
            case "up":
                uint flag = input.Button switch
                {
                    0 => input.Type == "down" ? 2u : 4u,
                    1 => input.Type == "down" ? 0x20u : 0x40u,
                    _ => input.Type == "down" ? 8u : 16u
                };
                events.Add(new()
                {
                    Type = 0,
                    Value = new()
                    {
                        Mouse = new()
                        {
                            Flags = flag
                        }
                    }
                });
                break;
            case "wheel":
                events.Add(new()
                {
                    Type = 0,
                    Value = new()
                    {
                        Mouse = new()
                        {
                            Flags = 0x800,
                            Data = unchecked((uint)input.Delta)
                        }
                    }
                });
                break;
            case "keydown":
            case "keyup":
                ushort key = checked((ushort)Math.Clamp(input.Key, 0, 255));
                if (input.Type == "keydown")
                {
                    pressed.Add(key);
                }
                else
                {
                    pressed.Remove(key);
                }

                events.Add(Key(key, input.Type == "keyup"));
                break;
            case "release":
                events.AddRange(pressed.Select(k => Key(k, true)));
                pressed.Clear();
                events.Add(new()
                {
                    Type = 0,
                    Value = new()
                    {
                        Mouse = new()
                        {
                            Flags = 4 | 16 | 0x40
                        }
                    }
                });
                break;
        }
        if (events.Count > 0 && SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<NativeInput>()) != events.Count)
        {
            throw new Win32Exception();
        }

        return "Sent";
    }
    private static NativeInput Key(ushort key, bool up) => new()
    {
        Type = 1,
        Value = new()
        {
            Keyboard = new()
            {
                VirtualKey = key,
                Flags = (up ? 2u : 0u) | (key is >= 33 and <= 46 or 91 or 92 or 93 ? 1u : 0u)
            }
        }
    };

    private sealed class InputDesktop : IDisposable
    {
        private readonly IntPtr original = GetThreadDesktop(GetCurrentThreadId());
        private readonly IntPtr desktop;

        public InputDesktop()
        {
            desktop = OpenInputDesktop(0, false, 0x1FF);
            if (desktop != IntPtr.Zero)
            {
                SetThreadDesktop(desktop);
            }
        }
        public void Dispose()
        {
            if (desktop != IntPtr.Zero)
            {
                SetThreadDesktop(original);
                CloseDesktop(desktop);
            }
        }
    }
    private static Process StartInteractive(string executable, string pipe)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue)
        {
            throw new IOException("There is no interactive Windows session.");
        }

        if (!OpenProcessToken(GetCurrentProcess(), 0xF01FF, out nint token))
        {
            throw new Win32Exception();
        }

        IntPtr duplicate = IntPtr.Zero;
        try
        {
            foreach (string? name in new[] {
                "SeTcbPrivilege",
                "SeAssignPrimaryTokenPrivilege",
                "SeIncreaseQuotaPrivilege"
            })
            {
                if (LookupPrivilegeValue(null, name, out Luid luid))
                {
                    TokenPrivileges privileges = new TokenPrivileges
                    {
                        Count = 1,
                        Luid = luid,
                        Attributes = 2
                    };
                    AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            if (!DuplicateTokenEx(token, 0xF01FF, IntPtr.Zero, 2, 1, out duplicate) || !SetTokenInformation(duplicate, 12, ref session, sizeof(uint)))
            {
                throw new Win32Exception();
            }

            StartupInfo startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = "winsta0\\default"
            };
            StringBuilder? command = new StringBuilder('"' + executable + "\" --broker --pipe " + pipe);
            if (!CreateProcessAsUser(duplicate, executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out ProcessInfo info))
            {
                throw new Win32Exception();
            }

            CloseHandle(info.Process);
            CloseHandle(info.Thread);
            return Process.GetProcessById(info.ProcessId);
        }
        finally
        {
            if (duplicate != IntPtr.Zero)
            {
                CloseHandle(duplicate);
            }
            CloseHandle(token);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Value;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }
    [DllImport("user32.dll", EntryPoint = "MessageBoxTimeoutW", CharSet = CharSet.Unicode)] private static extern int MessageBoxTimeout(IntPtr owner, string text, string title, uint type, ushort language, uint milliseconds);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);

    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);

    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll")] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr duplicate);

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int info, ref uint value, int size);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
}
