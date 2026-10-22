using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Agent;

public interface IAgentDesktop : IAsyncDisposable
{
    Task<ScreenFrame> FrameAsync(CancellationToken ct);
    Task<string> InputAsync(InputEvent input, CancellationToken ct);
}

public sealed class AgentOperations(AgentSettings settings, Func<AgentMessage, Task> send) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, StreamSession> streams = new();
    private readonly ConcurrentDictionary<string, IAgentDesktop> desktops = new();
    public async Task<object> ExecuteAsync(string operation, JsonElement payload, CancellationToken ct)
    {
        switch (operation)
        {
            case "command": return await CommandAsync(Wire.Read<CommandInput>(payload), ct);
            case "processes": return Process.GetProcesses().Select(p => { try { using (p) return new ProcessInfo(p.Id, p.ProcessName, p.WorkingSet64); } catch { return null; } }).OfType<ProcessInfo>().ToArray();
            case "files.list": return List(Wire.Read<FileInput>(payload).Path);
            case "files.read": {
                var path = FilePath(Wire.Read<FileInput>(payload).Path);
                if (new FileInfo(path).Length > Wire.MaxFileBytes) throw new IOException("This file exceeds the 5 MB transfer limit.");
                return new FileContent(Path.GetFileName(path), Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct)));
            }
            case "files.write": {
                var input = Wire.Read<FileInput>(payload); var bytes = Convert.FromBase64String(input.Content ?? "");
                if (bytes.Length > Wire.MaxFileBytes) throw new IOException("This file exceeds the 5 MB transfer limit.");
                await File.WriteAllBytesAsync(FilePath(input.Path), bytes, ct); return "Uploaded";
            }
            case "files.mkdir": Directory.CreateDirectory(FilePath(Wire.Read<FileInput>(payload).Path)); return "Created";
            case "files.delete": {
                var path = FilePath(Wire.Read<FileInput>(payload).Path);
                if (Path.GetPathRoot(path) == path || path == Path.GetFullPath(settings.FileRoot ?? Environment.CurrentDirectory)) throw new IOException("A filesystem root cannot be deleted.");
                if (Directory.Exists(path)) Directory.Delete(path, recursive: false); else File.Delete(path); return "Deleted";
            }
            case "desktop.open": {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop capture currently requires Windows.");
                var request = Wire.Read<SessionRequest>(payload);
                var connection = await WindowsDesktop.OpenAsync(request with { RequireConsent = request.RequireConsent || settings.RequireConsent }, ct);
                if (!desktops.TryAdd(request.SessionId, connection)) { await connection.DisposeAsync(); throw new IOException("Duplicate desktop session."); }
                return new OpenedSession(request.SessionId);
            }
            case "desktop.frame": {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                return await Desktop(payload).FrameAsync(ct);
            }
            case "desktop.input": {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                await Desktop(payload).InputAsync(Wire.Read<SessionInput>(payload).Input ?? new("release"), ct); return "Sent";
            }
            case "desktop.close": {
                var id = Wire.Read<SessionInput>(payload).SessionId;
                if (desktops.TryRemove(id, out var desktop)) await desktop.DisposeAsync(); return "Closed";
            }
            case "terminal.open": {
                var request = Wire.Read<SessionRequest>(payload);
                var session = await StreamSession.TerminalAsync(request.SessionId, request.Kind == "powershell", send, ct);
                if (!streams.TryAdd(request.SessionId, session)) { await session.DisposeAsync(); throw new IOException("Duplicate terminal session."); }
                return new OpenedSession(request.SessionId);
            }
            case "tcp.open": {
                var request = Wire.Read<SessionRequest>(payload);
                if (request.Port is < 1 or > 65535) throw new IOException("Invalid TCP port.");
                if ((request.RequireConsent || settings.RequireConsent) && request.Kind is "rdp" or "vnc" or "amt")
                {
                    if (!OperatingSystem.IsWindows() || !await WindowsDesktop.AskAsync(request.Actor, request.Kind, ct)) throw new IOException("Screen access was not approved by the person at the PC.");
                }
                var session = await StreamSession.TcpAsync(request.SessionId, request.Host ?? "127.0.0.1", request.Port, send, ct);
                if (!streams.TryAdd(request.SessionId, session)) { await session.DisposeAsync(); throw new IOException("Duplicate TCP session."); }
                return new OpenedSession(request.SessionId);
            }
            case "stream.close": {
                var id = Wire.Read<SessionInput>(payload).SessionId;
                if (streams.TryRemove(id, out var session)) await session.DisposeAsync(); return "Closed";
            }
            case "stream.resize": {
                var input = Wire.Read<SessionInput>(payload); if (streams.TryGetValue(input.SessionId, out var session)) session.Resize(input.Columns, input.Rows); return "Resized";
            }
            case "wake": {
                var mac = Convert.FromHexString(Wire.Read<WakeInput>(payload).Mac.Replace(":", "").Replace("-", ""));
                if (mac.Length != 6) throw new IOException("A six-byte MAC address is required.");
                var packet = Enumerable.Repeat((byte)255, 6).Concat(Enumerable.Range(0, 16).SelectMany(_ => mac)).ToArray();
                using var udp = new UdpClient { EnableBroadcast = true }; for (var i = 0; i < 3; i++) await udp.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, 9), ct); return "Wake packet sent";
            }
            case "power": {
                var action = Wire.Read<PowerInput>(payload).Action;
                if (action is not ("restart" or "shutdown")) throw new IOException("Unsupported power action.");
                var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "shutdown.exe" : "/sbin/shutdown") { UseShellExecute = false, CreateNoWindow = true };
                if (OperatingSystem.IsWindows()) { info.ArgumentList.Add(action == "restart" ? "/r" : "/s"); info.ArgumentList.Add("/t"); info.ArgumentList.Add("5"); }
                else { info.ArgumentList.Add(action == "restart" ? "-r" : "-h"); info.ArgumentList.Add("now"); }
                Process.Start(info)?.Dispose(); return "Power action requested";
            }
            default: throw new IOException("Unsupported MeshAll operation: " + operation);
        }
    }
    private IAgentDesktop Desktop(JsonElement payload) => desktops.TryGetValue(Wire.Read<SessionInput>(payload).SessionId, out var desktop) ? desktop : throw new IOException("Desktop session is closed.");
    public async Task WriteStreamAsync(SessionInput input, CancellationToken ct)
    {
        if (streams.TryGetValue(input.SessionId, out var stream) && input.Base64 is not null)
            await stream.WriteAsync(Convert.FromBase64String(input.Base64), ct);
    }
    private string FilePath(string input)
    {
        var root = settings.FileRoot;
        var path = Path.GetFullPath(string.IsNullOrEmpty(input) ? root ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("SystemDrive") + "\\" : "/") : input);
        if (!string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!(path + Path.DirectorySeparatorChar).StartsWith(root, comparison)) throw new UnauthorizedAccessException("The path is outside this agent’s configured file root.");
            for (var current = new DirectoryInfo(Path.GetDirectoryName(path) ?? path); current is not null && current.FullName.StartsWith(root, comparison); current = current.Parent)
                if (current.LinkTarget is not null) throw new UnauthorizedAccessException("Symbolic links are not allowed inside a restricted file root.");
            if (File.Exists(path) && new FileInfo(path).LinkTarget is not null) throw new UnauthorizedAccessException("Symbolic links are not allowed inside a restricted file root.");
        }
        return path;
    }
    private FileListing List(string input)
    {
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(input) && string.IsNullOrEmpty(settings.FileRoot))
            return new("", null, DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new RemoteFile(d.Name, d.Name, true, 0, DateTimeOffset.UnixEpoch)).ToArray());
        var path = FilePath(input); var directory = new DirectoryInfo(path); var entries = new List<RemoteFile>();
        foreach (var file in directory.EnumerateFileSystemInfos().Take(10000))
        {
            try { var isDirectory = (file.Attributes & FileAttributes.Directory) != 0; entries.Add(new(file.Name, file.FullName, isDirectory, isDirectory ? 0 : ((FileInfo)file).Length, file.LastWriteTimeUtc)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new(path, directory.Parent?.FullName, entries.OrderByDescending(e => e.Directory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private static async Task<CommandOutput> CommandAsync(CommandInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Command) || input.Command.Length > 16000) throw new IOException("Enter a command up to 16,000 characters.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(input.TimeoutSeconds, 1, 60)));
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrWhiteSpace(input.Directory) ? Environment.CurrentDirectory : Path.GetFullPath(input.Directory) };
        if (OperatingSystem.IsWindows()) { info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); " + input.Command))); }
        else { info.ArgumentList.Add("-c"); info.ArgumentList.Add(input.Command); }
        using var process = Process.Start(info) ?? throw new IOException("The command could not be started.");
        var output = new StringBuilder(); var truncated = false; var gate = new object();
        async Task Read(StreamReader stream) { var buffer = new char[4096]; int count; while ((count = await stream.ReadAsync(buffer)) > 0) lock (gate) { var take = Math.Min(count, Math.Max(0, 150000 - output.Length)); output.Append(buffer, 0, take); truncated |= take < count; } }
        var reads = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } await reads; throw new IOException("Command canceled or timed out."); }
        await reads; return new(output.ToString(), process.ExitCode, info.WorkingDirectory, truncated);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var stream in streams.Values) await stream.DisposeAsync(); streams.Clear();
        foreach (var desktop in desktops.Values) await desktop.DisposeAsync(); desktops.Clear();
    }
}

public sealed class StreamSession : IAsyncDisposable
{
    private readonly Stream input, output;
    private readonly Func<ValueTask> close;
    private readonly Action<int, int>? resize;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim write = new(1);
    private readonly Task reading;
    private StreamSession(string id, Stream input, Stream output, Func<ValueTask> close, Action<int, int>? resize, Func<AgentMessage, Task> send)
    {
        this.input = input; this.output = output; this.close = close; this.resize = resize;
        reading = ReadAsync(id, send);
    }
    public static async Task<StreamSession> TcpAsync(string id, string host, int port, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try { await client.ConnectAsync(host, port, ct); var stream = client.GetStream(); return new(id, stream, stream, () => { client.Dispose(); return ValueTask.CompletedTask; }, null, send); }
        catch { client.Dispose(); throw; }
    }
    public static Task<StreamSession> TerminalAsync(string id, bool powershell, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        var terminal = NativeTerminal.Start(powershell); ct.ThrowIfCancellationRequested();
        return Task.FromResult(new StreamSession(id, terminal.Input, terminal.Output, terminal.DisposeAsync, terminal.Resize, send));
    }
    private async Task ReadAsync(string id, Func<AgentMessage, Task> send)
    {
        try { var buffer = new byte[32768]; int count; while ((count = await output.ReadAsync(buffer, stop.Token)) > 0) await send(new("stream", Payload: Wire.Element(new SessionInput(id, Base64: Convert.ToBase64String(buffer, 0, count))))); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { try { await send(new("stream.end", Id: id)); } catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException) { } }
    }
    public async Task WriteAsync(byte[] bytes, CancellationToken ct) { await write.WaitAsync(ct); try { await input.WriteAsync(bytes, ct); await input.FlushAsync(ct); } finally { write.Release(); } }
    public void Resize(int columns, int rows) => resize?.Invoke(Math.Clamp(columns, 20, 300), Math.Clamp(rows, 5, 150));
    public async ValueTask DisposeAsync() { await stop.CancelAsync(); await close(); input.Dispose(); if (!ReferenceEquals(input, output)) output.Dispose(); try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { } stop.Dispose(); }
}
