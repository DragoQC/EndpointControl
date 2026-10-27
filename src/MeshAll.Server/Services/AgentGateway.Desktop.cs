using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed partial class AgentGateway
{
    public async Task ProxyDesktopAsync(HttpContext context, string id)
    {
        UserInfo? user = store.SessionUser(context.User);
        if (!store.HasPermission(user, Permissions.Desktop) || !store.CanAccessDevice(user, id))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (native.TryGetValue(id, out NativeAgentPeer? peer))
        {
            await ProxyNativeDesktop(context, id, peer);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest || !SameOrigin(context))
        {
            context.Response.StatusCode = 403;
            return;
        }
        DeviceInfo? device = store.FindDevice(id);
        if (device is not { Disabled: false } || !Online(id))
        {
            context.Response.StatusCode = 409;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        using SemaphoreSlim sendLock = new(1);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        stop.CancelAfter(TimeSpan.FromHours(2));
        string session = store.StartSession(device, context.User.Identity!.Name!, "Desktop");
        bool opened = false;

        async Task Send(object value)
        {
            await sendLock.WaitAsync(stop.Token);
            try
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json).AsMemory(), WebSocketMessageType.Text, true, stop.Token);
            }
            finally
            {
                sendLock.Release();
            }
        }

        try
        {
            bool consent = store.RequiresDesktopConsent(id);
            await Send(new
            {
                type = "status",
                message = consent ? "Waiting for the person at the PC to approve…" : "Connecting…"
            });
            await ExecuteAsync<OpenedSession>(id, "desktop.open", new SessionRequest(session, context.User.Identity!.Name!, consent), stop.Token);
            opened = true;
            store.Audit(context.User.Identity!.Name!, "Desktop session opened", device.Name, consent ? "User consent granted" : "Unattended policy");
            await Send(new
            {
                type = "status",
                message = "Connected"
            });

            async Task Frames()
            {
                while (!stop.IsCancellationRequested)
                {
                    ScreenFrame frame = await ExecuteAsync<ScreenFrame>(id, "desktop.frame", new SessionInput(session), stop.Token);
                    await Send(new
                    {
                        type = "frame",
                        frame.Base64,
                        frame.Width,
                        frame.Height
                    });
                    await Task.Delay(180, stop.Token);
                }
            }

            async Task Inputs()
            {
                while (socket.State == WebSocketState.Open)
                {
                    byte[]? bytes = await ReceiveBrowserAsync(socket, stop.Token);
                    if (bytes is null)
                    {
                        return;
                    }
                    if (!store.HasPermission(store.SessionUser(context.User), Permissions.Control))
                    {
                        throw new IOException("Desktop control is not permitted by your role.");
                    }

                    InputEvent? input = JsonSerializer.Deserialize<InputEvent>(bytes, Wire.Json);
                    if (input is not null)
                    {
                        await ExecuteAsync<string>(id, "desktop.input", new SessionInput(session, input), stop.Token);
                    }
                }
            }

            Task frames = Frames();
            Task inputs = Inputs();
            Task validation = ValidateAsync(context.User, id, stop.Token);
            await Task.WhenAny(frames, inputs, validation);
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(frames, inputs, validation);
            }
            catch (OperationCanceledException)
            {
            }
            store.EndSession(session, "Completed");
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or JsonException)
        {
            store.EndSession(session, "Failed");
            if (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                try
                {
                    await Send(new
                    {
                        type = "error",
                        message = ex.Message
                    });
                }
                catch (WebSocketException)
                {
                }
            }
        }
        finally
        {
            if (opened)
            {
                try
                {
                    await ExecuteAsync<string>(id, "desktop.close", new SessionInput(session));
                }
                catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task ProxyNativeDesktop(HttpContext context, string id, NativeAgentPeer peer)
    {
        if (!context.WebSockets.IsWebSocketRequest || !SameOrigin(context) || !Online(id))
        {
            context.Response.StatusCode = 403;
            return;
        }

        DeviceInfo device = store.FindDevice(id)!;
        using WebSocket browser = await context.WebSockets.AcceptWebSocketAsync();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        stop.CancelAfter(TimeSpan.FromHours(2));
        string session = store.StartSession(device, context.User.Identity!.Name!, "Desktop");
        Channel<NativeAgentHub.Frame> browserFrames = Channel.CreateBounded<NativeAgentHub.Frame>(16);

        async Task Receive()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    NativeAgentHub.Frame? frame = await NativeAgentHub.ReadAsync(browser, stop.Token, 65536);
                    if (frame is null)
                    {
                        break;
                    }
                    await browserFrames.Writer.WriteAsync(frame, stop.Token);
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException)
            {
            }
            finally
            {
                browserFrames.Writer.TryComplete();
                await stop.CancelAsync();
            }
        }

        Task receiving = Receive();
        async Task Authorize()
        {
            await ValidateAsync(context.User, id, stop.Token);
            await stop.CancelAsync();
        }
        Task validation = Authorize();
        try
        {
            if (store.RequiresDesktopConsent(id))
            {
                await browser.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ctrlChannel = "102938",
                    type = "console",
                    msg = "Waiting for the person at the PC to approve screen access…",
                    msgid = 1
                }, Wire.Json), WebSocketMessageType.Text, true, stop.Token);
            }
            await using NativeTunnelHub.Tunnel tunnel = await peer.OpenTunnelAsync(0,
                new(session, context.User.Identity!.Name!, store.RequiresDesktopConsent(id),
                    ViewOnly: !store.HasPermission(store.SessionUser(context.User), Permissions.Control)), stop.Token);
            await browser.SendAsync(Encoding.UTF8.GetBytes("c").AsMemory(), WebSocketMessageType.Text, true, stop.Token);

            async Task Output()
            {
                await foreach (NativeAgentHub.Frame frame in tunnel.Reader.ReadAllAsync(stop.Token))
                {
                    await browser.SendAsync(frame.Bytes.AsMemory(), frame.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, stop.Token);
                }
            }

            async Task Input()
            {
                bool negotiated = false;
                int controls = 0;
                await foreach (NativeAgentHub.Frame frame in browserFrames.Reader.ReadAllAsync(stop.Token))
                {
                    if (!negotiated)
                    {
                        if (Encoding.ASCII.GetString(frame.Bytes) == "2")
                        {
                            negotiated = true;
                        }
                        else if (++controls > 16 || !NativeDesktopProtocol.IsHandshakeControl(frame.Bytes))
                        {
                            throw new IOException("Only the desktop protocol is allowed on this relay.");
                        }
                    }
                    await tunnel.SendAsync(frame.Bytes, frame.Text, stop.Token);
                }
            }

            Task output = Output();
            Task input = Input();
            await Task.WhenAny(output, input, validation);
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(output, input, validation);
            }
            catch (OperationCanceledException)
            {
            }
            store.EndSession(session, "Completed");
        }
        catch (ScreenConsentException error)
        {
            logger.LogInformation("Screen approval ended: {Reason}; browser state {State}", error.Reason, browser.State);
            store.EndSession(session, "Denied");
            store.Audit(context.User.Identity!.Name!, error.Reason switch
            {
                "consent-denied" => "Screen approval declined",
                "consent-timeout" => "Screen approval timed out",
                _ => "Screen approval unavailable"
            }, device.Name);
            if (browser.State == WebSocketState.Open)
            {
                try
                {
                    await browser.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type = "meshall-desktop-ended",
                        reason = error.Reason
                    }, Wire.Json),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                    await browser.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, error.Reason, CancellationToken.None);
                    // Keep the receive pump alive until the terminal decision is delivered.
                    await Task.WhenAny(receiving, Task.Delay(1500));
                }
                catch (WebSocketException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException)
        {
            store.EndSession(session, "Failed");
            logger.LogInformation("Native desktop ended: {Reason}", ex.Message);
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(validation, receiving);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
