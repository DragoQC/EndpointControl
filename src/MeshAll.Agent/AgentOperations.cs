using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Agent;

public interface IAgentDesktop : IAsyncDisposable
{
    Task<ScreenFrame> FrameAsync(CancellationToken ct);

    Task<string> InputAsync(InputEvent input, CancellationToken ct);
}

public sealed partial class AgentOperations(AgentSettings settings, Func<AgentMessage, Task> send) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, StreamSession> streams = new();
    private readonly ConcurrentDictionary<string, IAgentDesktop> desktops = new();

    public async Task<object> ExecuteAsync(string operation, JsonElement payload, CancellationToken ct)
    {
        switch (operation)
        {
            case "command":
                return await CommandAsync(Wire.Read<CommandInput>(payload), ct);
            case "processes":
                return Process.GetProcesses().Select(p =>
{
    try
    {
        using (p)
        {
            return new ProcessInfo(p.Id, p.ProcessName, p.WorkingSet64);
        }
    }
    catch
    {
        return null;
    }
}).OfType<ProcessInfo>().ToArray();
            case "files.list":
                return List(Wire.Read<FileInput>(payload).Path);
            case "files.read":
                {
                    string path = FilePath(Wire.Read<FileInput>(payload).Path);
                    if (new FileInfo(path).Length > Wire.MaxFileBytes)
                    {
                        throw new IOException("This file exceeds the 5 MB transfer limit.");
                    }

                    return new FileContent(Path.GetFileName(path), Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct)));
                }
            case "files.write":
                {
                    FileInput? input = Wire.Read<FileInput>(payload);
                    byte[] bytes = Convert.FromBase64String(input.Content ?? "");
                    if (bytes.Length > Wire.MaxFileBytes)
                    {
                        throw new IOException("This file exceeds the 5 MB transfer limit.");
                    }

                    await File.WriteAllBytesAsync(FilePath(input.Path), bytes, ct);
                    return "Uploaded";
                }
            case "files.mkdir":
                Directory.CreateDirectory(FilePath(Wire.Read<FileInput>(payload).Path));
                return "Created";
            case "files.delete":
                {
                    string path = FilePath(Wire.Read<FileInput>(payload).Path);
                    if (Path.GetPathRoot(path) == path || path == Path.GetFullPath(settings.FileRoot ?? Environment.CurrentDirectory))
                    {
                        throw new IOException("A filesystem root cannot be deleted.");
                    }

                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: false);
                    }
                    else
                    {
                        File.Delete(path);
                    }

                    return "Deleted";
                }
            case "desktop.open":
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException("Desktop capture currently requires Windows.");
                    }

                    SessionRequest? request = Wire.Read<SessionRequest>(payload);
                    WindowsDesktop.Connection? connection = await WindowsDesktop.OpenAsync(request with
                    {
                        RequireConsent = request.RequireConsent || settings.RequireConsent
                    }, ct);
                    if (!desktops.TryAdd(request.SessionId, connection))
                    {
                        await connection.DisposeAsync();
                        throw new IOException("Duplicate desktop session.");
                    }
                    return new OpenedSession(request.SessionId);
                }
            case "desktop.frame":
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException();
                    }

                    return await Desktop(payload).FrameAsync(ct);
                }
            case "desktop.input":
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException();
                    }

                    await Desktop(payload).InputAsync(Wire.Read<SessionInput>(payload).Input ?? new("release"), ct);
                    return "Sent";
                }
            case "desktop.close":
                {
                    string id = Wire.Read<SessionInput>(payload).SessionId;
                    if (desktops.TryRemove(id, out IAgentDesktop? desktop))
                    {
                        await desktop.DisposeAsync();
                    }

                    return "Closed";
                }
            case "terminal.open":
                {
                    SessionRequest? request = Wire.Read<SessionRequest>(payload);
                    StreamSession? session = await StreamSession.TerminalAsync(request.SessionId, request.Kind == "powershell", send, ct);
                    if (!streams.TryAdd(request.SessionId, session))
                    {
                        await session.DisposeAsync();
                        throw new IOException("Duplicate terminal session.");
                    }
                    return new OpenedSession(request.SessionId);
                }
            case "tcp.open":
                {
                    SessionRequest? request = Wire.Read<SessionRequest>(payload);
                    if (request.Port is < 1 or > 65535)
                    {
                        throw new IOException("Invalid TCP port.");
                    }

                    if ((request.RequireConsent || settings.RequireConsent) && request.Kind is "rdp" or "vnc" or "amt")
                    {
                        if (!OperatingSystem.IsWindows() || !await WindowsDesktop.AskAsync(request.Actor, request.Kind, ct))
                        {
                            throw new IOException("Screen access was not approved by the person at the PC.");
                        }
                    }
                    StreamSession? session = await StreamSession.TcpAsync(request.SessionId, request.Host ?? "127.0.0.1", request.Port, send, ct);
                    if (!streams.TryAdd(request.SessionId, session))
                    {
                        await session.DisposeAsync();
                        throw new IOException("Duplicate TCP session.");
                    }
                    return new OpenedSession(request.SessionId);
                }
            case "stream.close":
                {
                    string id = Wire.Read<SessionInput>(payload).SessionId;
                    if (streams.TryRemove(id, out StreamSession? session))
                    {
                        await session.DisposeAsync();
                    }

                    return "Closed";
                }
            case "stream.resize":
                {
                    SessionInput? input = Wire.Read<SessionInput>(payload);
                    if (streams.TryGetValue(input.SessionId, out StreamSession? session))
                    {
                        session.Resize(input.Columns, input.Rows);
                    }

                    return "Resized";
                }
            case "wake":
                {
                    byte[] mac = Convert.FromHexString(Wire.Read<WakeInput>(payload).Mac.Replace(":", "").Replace("-", ""));
                    if (mac.Length != 6)
                    {
                        throw new IOException("A six-byte MAC address is required.");
                    }

                    byte[] packet = Enumerable.Repeat((byte)255, 6).Concat(Enumerable.Range(0, 16).SelectMany(_ => mac)).ToArray();
                    using UdpClient? udp = new UdpClient
                    {
                        EnableBroadcast = true
                    };
                    for (int i = 0; i < 3; i++)
                    {
                        await udp.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, 9), ct);
                    }

                    return "Wake packet sent";
                }
            case "power":
                {
                    string action = Wire.Read<PowerInput>(payload).Action;
                    if (action is not ("restart" or "shutdown"))
                    {
                        throw new IOException("Unsupported power action.");
                    }

                    ProcessStartInfo? info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "shutdown.exe" : "/sbin/shutdown")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    if (OperatingSystem.IsWindows())
                    {
                        info.ArgumentList.Add(action == "restart" ? "/r" : "/s");
                        info.ArgumentList.Add("/t");
                        info.ArgumentList.Add("5");
                    }
                    else
                    {
                        info.ArgumentList.Add(action == "restart" ? "-r" : "-h");
                        info.ArgumentList.Add("now");
                    }
                    Process.Start(info)?.Dispose();
                    return "Power action requested";
                }
            default:
                throw new IOException("Unsupported MeshAll operation: " + operation);
        }
    }
    private IAgentDesktop Desktop(JsonElement payload) => desktops.TryGetValue(Wire.Read<SessionInput>(payload).SessionId, out IAgentDesktop? desktop) ? desktop : throw new IOException("Desktop session is closed.");

    public async Task WriteStreamAsync(SessionInput input, CancellationToken ct)
    {
        if (streams.TryGetValue(input.SessionId, out StreamSession? stream) && input.Base64 is not null)
        {
            await stream.WriteAsync(Convert.FromBase64String(input.Base64), ct);
        }
    }
    private string FilePath(string input)
    {
        string? root = settings.FileRoot;
        string path = Path.GetFullPath(string.IsNullOrEmpty(input) ? root ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("SystemDrive") + "\\" : "/") : input);
        if (!string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!(path + Path.DirectorySeparatorChar).StartsWith(root, comparison))
            {
                throw new UnauthorizedAccessException("The path is outside this agent’s configured file root.");
            }

            for (DirectoryInfo? current = new DirectoryInfo(Path.GetDirectoryName(path) ?? path); current is not null && current.FullName.StartsWith(root, comparison); current = current.Parent)
            {
                if (current.LinkTarget is not null)
                {
                    throw new UnauthorizedAccessException("Symbolic links are not allowed inside a restricted file root.");
                }
            }

            if (File.Exists(path) && new FileInfo(path).LinkTarget is not null
                || Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null)
            {
                throw new UnauthorizedAccessException("Symbolic links are not allowed inside a restricted file root.");
            }
        }
        return path;
    }
    private FileListing List(string input)
    {
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(input) && string.IsNullOrEmpty(settings.FileRoot))
        {
            return new("", null, DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new RemoteFile(d.Name, d.Name, true, 0, DateTimeOffset.UnixEpoch)).ToArray());
        }

        string path = FilePath(input);
        DirectoryInfo? directory = new DirectoryInfo(path);
        List<RemoteFile>? entries = new List<RemoteFile>();
        foreach (FileSystemInfo? file in directory.EnumerateFileSystemInfos().Take(10000))
        {
            try
            {
                bool isDirectory = (file.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new(file.Name, file.FullName, isDirectory, isDirectory ? 0 : ((FileInfo)file).Length, file.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return new(path, directory.Parent?.FullName, entries.OrderByDescending(e => e.Directory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }
    public async ValueTask DisposeAsync()
    {
        foreach (StreamSession? stream in streams.Values)
        {
            await stream.DisposeAsync();
        }

        streams.Clear();
        foreach (IAgentDesktop? desktop in desktops.Values)
        {
            await desktop.DisposeAsync();
        }

        desktops.Clear();
    }
}
