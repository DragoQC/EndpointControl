namespace MeshAll.Server.Services;

public sealed partial class Store
{
    private UserInfo RequireCurrentPermission(UserInfo actor, string permission)
    {
        UserInfo? current = FindUser(actor.Id);
        if (current is null || current.Version != actor.Version || !HasPermission(current, permission))
        {
            throw new UnauthorizedAccessException("Your account does not have permission for this action.");
        }
        return current;
    }

    public bool CanDelegateRole(UserInfo? actor, RoleInfo role) => actor is not null
        && (actor.Role == "Admin" || role.Id != "Admin" && role.Permissions.All(permission => HasPermission(actor, permission)));

    public bool CanManageUser(UserInfo? actor, UserInfo target) => FindRole(target.Role) is RoleInfo role && CanDelegateRole(actor, role);

    public List<RoleInfo> AssignableRoles(UserInfo actor) => Roles().Where(role => CanDelegateRole(actor, role)).ToList();

    private void RequireDelegation(UserInfo actor, string roleId)
    {
        if (FindRole(roleId) is not RoleInfo role || !CanDelegateRole(actor, role))
        {
            throw new UnauthorizedAccessException("You cannot manage or grant permissions beyond your own role.");
        }
    }

    private void RequireManageUser(UserInfo actor, string id)
    {
        UserInfo target = FindUser(id) ?? throw new InvalidOperationException("Account not found.");
        RequireDelegation(actor, target.Role);
    }

    public UserInfo ManageAddUser(UserInfo actor, string name, string password, string role)
    {
        lock (gate)
        {
            actor = RequireCurrentPermission(actor, Permissions.Users);
            RequireDelegation(actor, role);
            return AddUser(name, password, role);
        }
    }

    public void ManageUserDisabled(UserInfo actor, string id, bool disabled)
    {
        lock (gate)
        {
            actor = RequireCurrentPermission(actor, Permissions.Users);
            RequireManageUser(actor, id);
            SetUserDisabled(id, disabled, actor.Id);
        }
    }

    public void ManagePassword(UserInfo actor, string id, string password)
    {
        lock (gate)
        {
            actor = RequireCurrentPermission(actor, Permissions.Users);
            RequireManageUser(actor, id);
            ChangePassword(id, password);
        }
    }

    public void ManageUserRole(UserInfo actor, string id, string role)
    {
        lock (gate)
        {
            actor = RequireCurrentPermission(actor, Permissions.Users);
            RequireManageUser(actor, id);
            RequireDelegation(actor, role);
            ChangeUserRole(id, role);
        }
    }

    public void ManageRole(UserInfo actor, string? id, string name, IEnumerable<string> permissions)
    {
        lock (gate)
        {
            actor = RequireCurrentPermission(actor, Permissions.Roles);
            if (id is not null)
            {
                RequireDelegation(actor, id);
            }
            string[] grants = permissions.Distinct(StringComparer.Ordinal).ToArray();
            if (actor.Role != "Admin" && grants.Any(permission => !HasPermission(actor, permission)))
            {
                throw new UnauthorizedAccessException("You cannot manage or grant permissions beyond your own role.");
            }
            SaveRole(id, name, grants);
        }
    }

    public void ManageDeleteRole(UserInfo actor, string id)
    {
        lock (gate)
        {
            actor = RequireCurrentPermission(actor, Permissions.Roles);
            RequireDelegation(actor, id);
            DeleteRole(id);
        }
    }
}
