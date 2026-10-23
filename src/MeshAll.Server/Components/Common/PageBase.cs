using MeshAll.Server.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;

namespace MeshAll.Server.Components.Common;

public abstract class PageBase : ComponentBase, IAsyncDisposable
{
    [Inject] protected Store Store { get; set; } = default!;
    [Inject] protected AgentGateway Gateway { get; set; } = default!;
    [Inject] protected Workspace Workspace { get; set; } = default!;
    [Inject] protected ILogger<PageBase> Logger { get; set; } = default!;
    protected UserInfo? CurrentUser { get; set; }
    protected bool CanManage => Store.HasAnyRemotePermission(CurrentUser);
    protected bool Can(string permission) => Store.HasPermission(CurrentUser,permission);
    protected string? Error { get; set; }
    protected string? Success { get; set; }
    protected bool Busy { get; set; }
    private readonly CancellationTokenSource stop = new();
    private Task? refresh;
    protected virtual string? ViewPermission => Permissions.Inventory;
    protected virtual string? ChangePermission => null;
    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected override async Task OnInitializedAsync()
    {
        try { CurrentUser = await Workspace.RequireAsync(ViewPermission); await LoadAsync(); }
        catch (Exception ex) { Error = Message(ex); Logger.LogWarning(ex, "MeshAll page failed to load"); }
    }
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) refresh = RefreshAsync();
        return Task.CompletedTask;
    }
    private async Task RefreshAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stop.Token))
                await InvokeAsync(async () =>
                {
                    try { CurrentUser = await Workspace.RequireAsync(ViewPermission); if (!Busy) await LoadAsync(); }
                    catch (Exception ex) { Error = Message(ex); CurrentUser = null; }
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException) { }
    }
    protected async Task Act(Func<Task> action, string? success = null, string? permission = null)
    {
        if (Busy) return;
        Busy = true; Error = Success = null;
        try { CurrentUser = await Workspace.RequireAsync(permission??ChangePermission); await action(); Success = success; await LoadAsync(); }
        catch (Exception ex) { Error = Message(ex); Logger.LogWarning(ex, "MeshAll workspace action failed"); }
        finally { Busy = false; }
    }
    protected Task Act(Action action, string? success = null, string? permission = null) => Act(() => { action(); return Task.CompletedTask; }, success, permission);
    private static string Message(Exception ex) => ex is SqliteException ? "This change could not be saved. Check for a duplicate name and try again." : ex.Message;
    protected static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var n = (double)bytes; var u = 0; while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
        return $"{n:0.#} {units[u]}";
    }
    protected static string Ago(DateTimeOffset date)
    {
        if (date <= DateTimeOffset.UnixEpoch) return "Not reported";
        var span = DateTimeOffset.UtcNow - date;
        return span.TotalMinutes < 1 ? "Just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago" : $"{(int)span.TotalDays}d ago";
    }
    public virtual async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); if (refresh is not null) await refresh; stop.Dispose();
    }
}
