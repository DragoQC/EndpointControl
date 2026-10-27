using System.Security.Claims;
using MeshAll.Shared;
using Microsoft.AspNetCore.Components.Authorization;

namespace MeshAll.Server.Services;

public sealed class Workspace(Store store, AgentGateway gateway, AuthenticationStateProvider authentication)
{
    public async Task<UserInfo> RequireAsync(string? permission = null)
    {
        ClaimsPrincipal principal = (await authentication.GetAuthenticationStateAsync()).User;
        UserInfo? user = store.SessionUser(principal);
        if (user is null)
        {
            throw new UnauthorizedAccessException("Your session expired. Sign in again.");
        }

        if (permission is not null && !store.HasPermission(user, permission))
        {
            throw new UnauthorizedAccessException("Your account does not have permission for this action.");
        }

        return user;
    }

    public async Task<T> RunAsync<T>(string id, string operation, object payload, bool record = true, CancellationToken ct = default)
    {
        UserInfo user = await RequireAsync(Permissions.ForOperation(operation));
        DeviceInfo device = store.FindDevice(id) ?? throw new InvalidOperationException("Device not found.");
        store.RequireDeviceAccess(user, id);
        if (device.Disabled)
        {
            throw new InvalidOperationException("Device access has been revoked.");
        }

        string? session = record ? store.StartSession(device, user.Name, operation) : null;
        try
        {
            T result = await gateway.ExecuteAsync<T>(id, operation, payload, ct);
            if (session is not null)
            {
                store.EndSession(session, "Completed");
                store.Audit(user.Name, operation, device.Name, "Completed");
            }
            return result;
        }
        catch
        {
            if (session is not null)
            {
                store.EndSession(session, "Failed");
                store.Audit(user.Name, operation, device.Name, "Failed");
            }
            throw;
        }
    }

    public async Task<string> BeginDesktopAsync(string id)
    {
        UserInfo user = await RequireAsync(Permissions.Desktop);
        store.RequireDeviceAccess(user, id);
        DeviceInfo device = store.FindDevice(id) ?? throw new InvalidOperationException("Device not found.");
        if (device.Disabled || !gateway.Online(id))
        {
            throw new InvalidOperationException("Device is not available.");
        }

        store.Audit(user.Name, "Desktop opened", device.Name);
        return store.StartSession(device, user.Name, "Desktop");
    }
}
