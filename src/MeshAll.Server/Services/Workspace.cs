using System.Security.Claims;
using MeshAll.Shared;
using Microsoft.AspNetCore.Components.Authorization;

namespace MeshAll.Server.Services;

public sealed class Workspace(Store store, AgentGateway gateway, AuthenticationStateProvider authentication)
{
    public async Task<UserInfo> RequireAsync(bool manage = false, bool admin = false)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        var user = store.FindUser(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        if (user is null || user.Disabled || user.Version.ToString() != principal.FindFirstValue("version"))
            throw new UnauthorizedAccessException("Your session expired. Sign in again.");
        if ((manage && user.Role == "Viewer") || (admin && user.Role != "Admin"))
            throw new UnauthorizedAccessException("Your account does not have permission for this action.");
        return user;
    }

    public async Task<T> RunAsync<T>(string id, string operation, object payload, bool record = true, CancellationToken ct = default)
    {
        var user = await RequireAsync(manage: true);
        var device = store.FindDevice(id) ?? throw new InvalidOperationException("Device not found.");
        if (device.Disabled) throw new InvalidOperationException("Device access has been revoked.");
        string? session = record ? store.StartSession(device, user.Name, operation) : null;
        try
        {
            var result = await gateway.ExecuteAsync<T>(id, operation, payload, ct);
            if (session is not null)
            {
                store.EndSession(session, "Completed");
                store.Audit(user.Name, operation, device.Name, "Completed");
            }
            return result;
        }
        catch
        {
            if (session is not null) { store.EndSession(session, "Failed"); store.Audit(user.Name, operation, device.Name, "Failed"); }
            throw;
        }
    }

    public async Task<string> BeginDesktopAsync(string id)
    {
        var user = await RequireAsync(manage: true);
        var device = store.FindDevice(id) ?? throw new InvalidOperationException("Device not found.");
        if (device.Disabled || !gateway.Online(id)) throw new InvalidOperationException("Device is not available.");
        store.Audit(user.Name, "Desktop opened", device.Name);
        return store.StartSession(device, user.Name, "Desktop");
    }
}
