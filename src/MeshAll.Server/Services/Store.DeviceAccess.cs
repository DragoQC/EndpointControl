namespace MeshAll.Server.Services;

public sealed partial class Store
{
    public bool CanAccessDevice(UserInfo? user, string id) => user is not null
        && (Scalar<long>("SELECT COUNT(*) FROM protected_devices WHERE id=$0", id) == 0 || HasPermission(user, Permissions.Controller));

    public void RequireDeviceAccess(UserInfo user, string id)
    {
        if (!CanAccessDevice(user, id))
        {
            throw new UnauthorizedAccessException("This device hosts the management server and requires the controller-management capability.");
        }
    }
}
