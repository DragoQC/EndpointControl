using MeshAll.Server.Services;

namespace MeshAll.Server.Components.Pages;

public partial class Enroll
{
    protected override string? ViewPermission => Permissions.Enroll;
    protected override string? ChangePermission => Permissions.Enroll;
    private readonly CancellationTokenSource lifetime = new();
    private List<GroupInfo> groups = [];
    private string name = "";
    private string group = "default";
    private int uses = 1;
    private int hours = 24;
    private EnrollmentKeyDetails? selected;

    protected override PageResult<TokenInfo> ReadPage(PageRequest request)
    {
        groups = Store.Groups();
        if (selected is not null)
        {
            TokenInfo? latest = Store.FindEnrollmentToken(selected.Information.Id);
            selected = latest is null ? null : selected.WithInformation(latest);
        }
        return Store.TokenPage(request);
    }

    private string GroupName(TokenInfo key) => Ui.GroupName(groups.FirstOrDefault(item => item.Id == key.GroupId)?.Name ?? "Unassigned", key.GroupId);

    private Task Generate() => Act(async () =>
    {
        (string Id, string Token) created = Store.CreateToken(name, group, uses, hours);
        Store.Audit(CurrentUser!.Name, "Enrollment key created", name);
        selected = await Downloads.DetailsAsync(CurrentUser, created.Id, lifetime.Token);
    }, "Enrollment key created.");

    private Task OpenKey(string id) => Act(async () =>
    {
        UserInfo actor = await Workspace.RequireAsync(Permissions.Enroll);
        selected = await Downloads.DetailsAsync(actor, id, lifetime.Token);
    }, permission: Permissions.Enroll);

    private void CloseKey() => selected = null;

    private Task Revoke(TokenInfo key) => Act(() =>
    {
        Store.RevokeToken(key.Id);
        if (selected?.Information.Id == key.Id)
        {
            selected = null;
        }
        Store.Audit(CurrentUser!.Name, "Enrollment key revoked", key.Name);
    }, "Enrollment key revoked.");

    public override async ValueTask DisposeAsync()
    {
        selected = null;
        await lifetime.CancelAsync();
        lifetime.Dispose();
        await base.DisposeAsync();
    }
}
