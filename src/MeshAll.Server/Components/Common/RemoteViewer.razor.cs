using MeshAll.Server.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshAll.Server.Components.Common;

public partial class RemoteViewer : IAsyncDisposable
{
    [Inject] private Workspace Workspace { get; set; } = default!;
    [Inject] private Store Store { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private AgentGateway Agents { get; set; } = default!;

    [Parameter]
    public string Id { get; set; } = "";

    [Parameter]
    public string Tool { get; set; } = "desktop";

    [Parameter]
    public bool AutoConnect
    {
        get; set;
    }

    [Parameter]
    public int? RequestedPort
    {
        get; set;
    }

    [Parameter]
    public bool? RequestedDirect
    {
        get; set;
    }

    [Parameter]
    public bool Inline
    {
        get; set;
    }

    private DeviceInfo? device;
    private ElementReference workspace;
    private string username = "";
    private string password = "";
    private string domain = "";
    private string privateKey = "";
    private string status = "Disconnected";
    private string? error;
    private int port;
    private bool busy;
    private bool running;
    private bool control;
    private bool ready;
    private bool hasRendered;
    private bool disposed;
    private bool direct;
    private bool canControl;
    private bool inputAvailable;
    private bool vncNeedsCredentials;
    private bool vncNeedsUsername;
    private bool vncNeedsPassword;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private DotNetObjectReference<RemoteViewer>? reference;
    private string Label => RemoteTools.Label(Tool);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            UserInfo user = await Workspace.RequireAsync(Permissions.ForTool(Tool));
            Store.RequireDeviceAccess(user, Id);
            canControl = Store.HasPermission(user, Permissions.Control);
            control = canControl;
            device = Store.FindDevice(Id);
            port = Tool switch
            {
                "rdp" => 3389,
                "vnc" => RequestedPort ?? Store.VncPort(Id),
                "ssh" => 22,
                _ => 0
            };
            direct = Tool is "rdp" or "vnc" or "ssh" && (RequestedDirect ?? !Agents.Online(Id));
            if (!RemoteTools.IsKnown(Tool) || device is null)
            {
                error = "Remote tool or device not found.";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        hasRendered = true;
        try
        {
            await JS.InvokeVoidAsync("meshAll.remote.mount", owner, workspace);
        }
        catch (JSException)
        {
            error = "The remote viewer could not be loaded.";
        }

        if (Tool == "rdp" && error is null)
        {
            try
            {
                UserInfo user = await Workspace.RequireAsync(Permissions.Rdp);
                if (disposed)
                {
                    return;
                }

                RdpCredentials? credentials = Store.ReadRdpCredentials(user);
                domain = credentials?.Domain ?? "";
                username = credentials?.Username ?? "";
                password = credentials?.Password ?? "";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        if (disposed)
        {
            return;
        }

        ready = true;
        StateHasChanged();
        if ((AutoConnect || Tool == "vnc") && error is null)
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        if (disposed || busy || running || device is null)
        {
            return;
        }

        busy = true;
        error = null;
        try
        {
            UserInfo user = await Workspace.RequireAsync(Permissions.ForTool(Tool));
            if (disposed)
            {
                return;
            }

            await JS.InvokeVoidAsync("meshAll.remote.mount", owner, workspace);
            Store.RequireDeviceAccess(user, Id);
            canControl = Store.HasPermission(user, Permissions.Control);
            control &= canControl;
            if (Tool == "vnc" && Store.HasPermission(user, Permissions.EditDevices))
            {
                Store.SaveVncPort(Id, port);
            }

            if (Store.FindDevice(Id) is not { Disabled: false })
            {
                throw new InvalidOperationException("Device access is blocked in EndpointControl.");
            }

            reference ??= DotNetObjectReference.Create(this);
            inputAvailable = false;
            running = true;
            bool needsConsent = Tool is "rdp" or "vnc" or "amt" && Store.RequiresDesktopConsent(Id);
            status = needsConsent ? "Waiting for the person at the PC to approve screen access…" : "Connecting…";
            await InvokeAsync(StateHasChanged);
            await Task.Yield();
            if (disposed)
            {
                return;
            }

            RemoteConnectionOptions options = new(Id, Tool, port, username, password, domain, privateKey,
                direct, Store.IsNative(Id), owner, control, canControl);
            await JS.InvokeVoidAsync("meshAll.remote.connect", options, reference);
            privateKey = "";
            if (Tool != "rdp")
            {
                password = "";
            }
        }
        catch (JSException)
        {
            error = "The remote viewer could not be loaded.";
            running = inputAvailable = false;
            status = "Disconnected";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            running = false;
            status = "Disconnected";
        }
        finally
        {
            busy = false;
        }
    }

    [JSInvokable]
    public async Task StateAsync(string next, bool connected)
    {
        if (disposed)
        {
            return;
        }

        status = next;
        if (!connected && next != "Connecting…")
        {
            running = false;
            vncNeedsCredentials = false;
            inputAvailable = false;
        }

        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task DesktopInputStateAsync(bool allowed, bool available, bool enabled)
    {
        if (disposed)
        {
            return;
        }

        canControl = allowed;
        inputAvailable = available;
        if (!allowed)
        {
            control = false;
        }
        else if (available)
        {
            control = enabled;
        }

        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task VncCredentialsAsync(bool needsUsername, bool needsPassword)
    {
        if (disposed)
        {
            return;
        }

        vncNeedsCredentials = true;
        vncNeedsUsername = needsUsername;
        vncNeedsPassword = needsPassword;
        await InvokeAsync(StateHasChanged);
    }

    private async Task SubmitVncCredentialsAsync()
    {
        await Workspace.RequireAsync(Permissions.Vnc);
        if (disposed)
        {
            return;
        }

        await JS.InvokeVoidAsync("meshAll.remote.credentials", owner, username, password);
        password = "";
        vncNeedsCredentials = false;
    }

    private async Task DisconnectAsync()
    {
        await JS.InvokeVoidAsync("meshAll.remote.disconnect", owner);
        running = inputAvailable = false;
        status = "Disconnected";
    }

    private async Task SetControlAsync()
    {
        UserInfo user = await Workspace.RequireAsync(Permissions.Control);
        canControl = Store.HasPermission(user, Permissions.Control);
        if (!disposed)
        {
            await JS.InvokeVoidAsync("meshAll.remote.control", owner, control && canControl);
        }
    }

    private async Task SendCtrlAltDeleteAsync()
    {
        if (!disposed && control && canControl && inputAvailable)
        {
            await Workspace.RequireAsync(Permissions.Control);
            if (!disposed)
            {
                await JS.InvokeVoidAsync("meshAll.remote.cad", owner);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        password = privateKey = "";
        if (hasRendered)
        {
            try
            {
                await JS.InvokeVoidAsync("meshAll.remote.dispose", owner);
            }
            catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException or ObjectDisposedException)
            {
            }
        }

        reference?.Dispose();
    }

    private sealed record RemoteConnectionOptions(string Id, string Tool, int Port, string Username,
        string Password, string Domain, string PrivateKey, bool Direct, bool Native, string Owner,
        bool ControlRequested, bool ControlAllowed)
    {
        public override string ToString() => nameof(RemoteConnectionOptions);
    }
}
