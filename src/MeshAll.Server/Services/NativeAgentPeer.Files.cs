using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed partial class NativeAgentPeer
{
    private async Task<object> FilesAsync(string operation, FileInput input, CancellationToken ct)
    {
        await using NativeTunnelHub.Tunnel tunnel = await OpenTunnelAsync(5,
            new(Guid.NewGuid().ToString("N"), "EndpointControl operator", false), ct);
        string path = input.Path.Replace('\\', '/');
        Task Send(object value) => tunnel.TextAsync(JsonSerializer.Serialize(value, Wire.Json), ct);

        async Task<JsonElement> Json(Func<JsonElement, bool> predicate)
        {
            await foreach (NativeAgentHub.Frame frame in tunnel.Reader.ReadAllAsync(ct))
            {
                if (frame.Bytes.Length == 0 || frame.Bytes[0] != '{')
                {
                    continue;
                }

                JsonElement value = JsonSerializer.Deserialize<JsonElement>(frame.Bytes);
                if (predicate(value))
                {
                    return value;
                }
            }
            throw new IOException("File session ended.");
        }

        if (operation == "files.list")
        {
            await Send(new
            {
                action = "ls",
                reqid = 1,
                path
            });
            JsonElement listing = await Json(reply => reply.TryGetProperty("dir", out _));
            string actual = Text(listing, "path", path);
            RemoteFile[] entries = listing.GetProperty("dir").EnumerateArray().Select(entry =>
            {
                string name = Text(entry, "n");
                string entryPath = actual.Length == 0 ? name : actual.TrimEnd('/') + "/" + name;
                DateTimeOffset modified = DateTimeOffset.TryParse(Text(entry, "d"), out DateTimeOffset time) ? time : DateTimeOffset.UnixEpoch;
                return new RemoteFile(name, entryPath, Number(entry, "t") is 1 or 2, Number(entry, "s"), modified);
            }).OrderByDescending(entry => entry.Directory).ThenBy(entry => entry.Name).ToArray();
            return new FileListing(actual, actual.Length == 0 || actual == "/" ? null : Parent(actual), entries);
        }

        if (operation is "files.mkdir" or "files.delete")
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/" || path.TrimEnd('/').EndsWith(':'))
            {
                throw new IOException("A filesystem root cannot be changed.");
            }

            if (operation == "files.mkdir")
            {
                await Send(new
                {
                    action = "mkdir",
                    reqid = 1,
                    path
                });
            }
            else
            {
                await Send(new
                {
                    action = "rm",
                    reqid = 1,
                    path = Parent(path),
                    delfiles = new[] { Name(path) },
                    rec = false
                });
            }

            await Send(new
            {
                action = "ls",
                reqid = 2,
                path = Parent(path)
            });
            JsonElement listing = await Json(reply => reply.TryGetProperty("dir", out _));
            bool exists = listing.GetProperty("dir").EnumerateArray().Any(entry => Text(entry, "n") == Name(path));
            if (operation == "files.mkdir" ? !exists : exists)
            {
                throw new IOException("The remote filesystem rejected this change.");
            }
            return "Completed";
        }

        if (operation == "files.read")
        {
            await Send(new
            {
                action = "download",
                sub = "start",
                id = "file",
                path
            });
            JsonElement start = await Json(reply => Text(reply, "action") == "download");
            if (Text(start, "sub") != "start")
            {
                throw new IOException("File could not be opened.");
            }

            await Send(new
            {
                action = "download",
                sub = "startack",
                id = "file"
            });
            using MemoryStream output = new();
            await foreach (NativeAgentHub.Frame frame in tunnel.Reader.ReadAllAsync(ct))
            {
                if (frame.Bytes.Length < 4 || frame.Bytes[0] == '{')
                {
                    continue;
                }
                if (output.Length + frame.Bytes.Length - 4 > Wire.MaxFileBytes)
                {
                    throw new IOException("File exceeds the 5 MB transfer limit.");
                }

                output.Write(frame.Bytes, 4, frame.Bytes.Length - 4);
                if ((frame.Bytes[3] & 1) != 0)
                {
                    return new FileContent(Name(path), Convert.ToBase64String(output.ToArray()));
                }
                await Send(new
                {
                    action = "download",
                    sub = "ack",
                    id = "file"
                });
            }
            throw new IOException("File download interrupted.");
        }

        if (operation == "files.write")
        {
            byte[] bytes = Convert.FromBase64String(input.Content ?? "");
            if (bytes.Length > Wire.MaxFileBytes)
            {
                throw new IOException("File exceeds the 5 MB transfer limit.");
            }

            await Send(new
            {
                action = "upload",
                reqid = "up",
                path = Parent(path),
                name = Name(path),
                size = bytes.Length,
                append = false
            });
            if (Text(await Json(reply => Text(reply, "action") is "uploadstart" or "uploaderror"), "action") == "uploaderror")
            {
                throw new IOException("File could not be created.");
            }

            for (int offset = 0; offset < bytes.Length; offset += 65536)
            {
                byte[] block = bytes.AsMemory(offset, Math.Min(65536, bytes.Length - offset)).ToArray();
                if (block[0] is 0 or 123)
                {
                    block = [0, .. block];
                }

                await tunnel.SendAsync(block, false, ct);
                if (Text(await Json(reply => Text(reply, "action") is "uploadack" or "uploaderror"), "action") == "uploaderror")
                {
                    throw new IOException("File upload failed.");
                }
            }
            await Send(new
            {
                action = "uploaddone",
                reqid = "up"
            });
            await Json(reply => Text(reply, "action") == "uploaddone");
            return "Uploaded";
        }

        throw new IOException("Unsupported file operation.");
    }

    private static string Name(string path) => path.TrimEnd('/').Split('/')[^1];

    private static string Parent(string path)
    {
        string trimmed = path.TrimEnd('/');
        int index = trimmed.LastIndexOf('/');
        return index < 0 ? "" : index == 0 ? "/" : trimmed[..index];
    }
}
