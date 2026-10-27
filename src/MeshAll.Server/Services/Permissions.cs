using Microsoft.AspNetCore.Authorization;

namespace MeshAll.Server.Services;

public static class Permissions
{
    public const string LogsPolicy = "logs.access";
    public const string Controller = "devices.controller";
    public const string Inventory = "devices.view", EditDevices = "devices.edit", BlockDevices = "devices.block", RemoveDevices = "devices.remove", Enroll = "devices.enroll", Groups = "groups.manage";
    public const string Desktop = "desktop.view", Control = "desktop.control", Terminal = "terminal.use", Commands = "commands.run", Processes = "processes.view";
    public const string FilesRead = "files.read", FilesWrite = "files.write", Rdp = "network.rdp", Vnc = "network.vnc", Ssh = "network.ssh", Amt = "network.amt", Power = "power.manage";
    public const string Scripts = "scripts.manage", Sessions = "sessions.view", Audit = "audit.view", ExportAudit = "audit.export", Users = "users.manage", Roles = "roles.manage", Settings = "settings.manage";
    public static readonly (string Id, string Label)[] All = [
        (Controller,"Manage the server's own enrolled device"),
        (Inventory,"View device inventory"),(EditDevices,"Edit devices"),(BlockDevices,"Block / restore device access"),(RemoveDevices,"Remove devices from the website"),(Enroll,"Enroll agents"),(Groups,"Manage device groups"),
        (Desktop,"View native desktop"),(Control,"Control native desktop keyboard / mouse"),(Terminal,"Use interactive terminals"),(Commands,"Run commands"),(Processes,"View processes"),
        (FilesRead,"Browse / download files"),(FilesWrite,"Upload / change / delete files"),(Rdp,"Use RDP"),(Vnc,"Use VNC"),(Ssh,"Use SSH"),(Amt,"Use Intel AMT"),(Power,"Manage device power"),
        (Scripts,"Manage saved scripts"),(Sessions,"View sessions"),(Audit,"View audit log"),(ExportAudit,"Export audit log"),(Users,"Manage users"),(Roles,"Manage roles"),(Settings,"Manage instance settings")
    ];

    public static string ForTool(string tool) => tool switch
    {
        "desktop" => Desktop,
        "terminal" or "powershell" => Terminal,
        "rdp" => Rdp,
        "vnc" => Vnc,
        "ssh" => Ssh,
        "amt" => Amt,
        _ => "unknown"
    };

    public static string ForOperation(string operation) => operation switch
    {
        "command" => Commands,
        "processes" => Processes,
        "files.list" or "files.read" => FilesRead,
        "files.write" or "files.mkdir" or "files.delete" => FilesWrite,
        "power" or "wake" => Power,
        "inventory.refresh" => Inventory,
        _ => "unknown"
    };
}
public sealed class LogsPermissionHandler(Store store) : AuthorizationHandler<LogsPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LogsPermissionRequirement requirement)
    {
        UserInfo? user = store.SessionUser(context.User);
        if (store.HasPermission(user, Permissions.Sessions) || store.HasPermission(user, Permissions.Audit))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
public sealed record LogsPermissionRequirement : IAuthorizationRequirement;
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionHandler(Store store) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (store.SessionUser(context.User) is
            {
            } user && store.HasPermission(user, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
