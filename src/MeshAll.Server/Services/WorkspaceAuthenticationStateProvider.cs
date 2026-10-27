using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace MeshAll.Server.Services;

public sealed class WorkspaceAuthenticationStateProvider(ILoggerFactory loggerFactory, Store store)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(5);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken) =>
        Task.FromResult(store.SessionUser(state.User, allowSetup: true) is not null);
}
