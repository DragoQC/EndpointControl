using MeshAll.Server.Localization;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshAll.Server.Components.Pages;

public partial class Device
{
    [Parameter]
    public string Id { get; set; } = "";

    private static readonly string[] Tabs = ["Overview", "Commands", "Terminal", "Files", "Desktop", "Remote tools", "Processes"];
    private DeviceInfo? device;
    private List<GroupInfo> groups = [];
    private bool online;
    private bool editing;
    private string tab = "Overview";
    private string name = "";
    private string group = "";
    private string tags = "";
    private string notes = "";
    private int interfacePage = 1;
    private int interfacePageSize = PageRequest.MaximumSize;
    private PageResult<NetworkInterfaceInfo> interfaceRows = PageResult<NetworkInterfaceInfo>.Empty();

    private bool Allowed(string view) => view switch
    {
        "Overview" => Can(Permissions.Inventory),
        _ when !Store.CanAccessDevice(CurrentUser, Id) => false,
        "Commands" => Can(Permissions.Commands),
        "Terminal" => Can(Permissions.Terminal),
        "Files" => Can(Permissions.FilesRead),
        "Desktop" => Can(Permissions.Desktop),
        "Processes" => Can(Permissions.Processes),
        "Remote tools" => new[]
        {
            Permissions.Rdp, Permissions.Vnc, Permissions.Ssh, Permissions.Amt, Permissions.Terminal
        }.Any(Can),
        _ => false
    };

    private static string Reported(string? value) => string.IsNullOrWhiteSpace(value) ? UiText.Get("Not reported") : value;
    private static object InterfaceKey(NetworkInterfaceInfo network) => $"{network.Name}/{network.Address}";
    private void SelectTab(string view) => tab = view;

    protected override Task LoadAsync()
    {
        device = Store.FindDevice(Id);
        online = device is not null && !device.Disabled && Gateway.ConnectedDeviceIds().Contains(Id);
        groups = Store.Groups();
        UpdateInterfacePage();
        return Task.CompletedTask;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (CurrentUser is not null)
        {
            await LoadAsync();
        }
    }

    private void EditDevice()
    {
        name = device!.Name;
        group = device.GroupId;
        tags = device.Tags;
        notes = device.Notes;
        editing = true;
    }

    private Task SaveDevice() => Act(async () =>
    {
        Store.RequireDeviceAccess(CurrentUser!, Id);
        await Gateway.SaveDeviceAsync(Id, name, group, tags, notes);
        Store.Audit(CurrentUser!.Name, "Device updated", name);
        editing = false;
    }, "Device saved.", Permissions.EditDevices);

    private Task RefreshInventory() => Act(
        async () => await Workspace.RunAsync<string>(Id, "inventory.refresh", new { }),
        "Inventory refresh requested.", Permissions.Inventory);

    private void ChangeInterfacePage(int page)
    {
        interfacePage = page;
        UpdateInterfacePage();
    }

    private void ChangeInterfacePageSize(int size)
    {
        interfacePage = 1;
        interfacePageSize = size;
        UpdateInterfacePage();
    }

    private void UpdateInterfacePage()
    {
        NetworkInterfaceInfo[] interfaces = device?.Inventory?.Interfaces ?? [];
        interfaceRows = PageResult<NetworkInterfaceInfo>.FromSnapshot(interfaces, new PageRequest(interfacePage, interfacePageSize));
        interfacePage = interfaceRows.PageNumber;
    }

    private async Task Power(string action)
    {
        if (await JS.InvokeAsync<bool>("confirm", Ui[action == "restart" ? "Restart {0} now?" : "Shut down {0} now?", device!.Name]))
        {
            await Act(async () => await Workspace.RunAsync<string>(Id, "power", new PowerInput(action)), "Power action sent.", Permissions.Power);
        }
    }

    private async Task Revoke()
    {
        if (!await JS.InvokeAsync<bool>("confirm", Ui[device!.Disabled ? "Restore EndpointControl access for {0}?" : "Block EndpointControl access for {0}?", device.Name]))
        {
            return;
        }

        await Act(() =>
        {
            Store.RequireDeviceAccess(CurrentUser!, Id);
            bool blocked = !device.Disabled;
            Store.SetDeviceBlocked(Id, blocked);
            if (blocked)
            {
                Gateway.Disconnect(Id);
            }
            Store.Audit(CurrentUser!.Name, blocked ? "Device access blocked" : "Device access restored", device.Name);
        }, "EndpointControl access updated.", Permissions.BlockDevices);
    }

    private async Task Remove()
    {
        if (await JS.InvokeAsync<bool>("confirm", Ui["Remove {0}? The device can return when its agent reconnects.", device!.Name]))
        {
            await Act(() =>
            {
                Store.RequireDeviceAccess(CurrentUser!, Id);
                Store.RemoveDevice(Id);
                Store.Audit(CurrentUser!.Name, "Device removed from website", device.Name);
                Navigation.NavigateTo("/devices");
            }, permission: Permissions.RemoveDevices);
        }
    }
}
