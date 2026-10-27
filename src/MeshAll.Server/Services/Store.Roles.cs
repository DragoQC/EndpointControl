using System.Text.Json;

namespace MeshAll.Server.Services;

public record RoleInfo(string Id, string Name, string[] Permissions, bool BuiltIn);

public sealed partial class Store
{
    private void InitializeRoles()
    {
        string[] all = Permissions.All.Select(p => p.Id).ToArray();
        string[] adminOnly = new[] {
            Permissions.Controller,
            Permissions.Enroll,
            Permissions.BlockDevices,
            Permissions.RemoveDevices,
            Permissions.Groups,
            Permissions.ExportAudit,
            Permissions.Users,
            Permissions.Roles,
            Permissions.Settings
        };
        foreach (RoleInfo? role in new[] {
            new RoleInfo("Admin", "Administrator", all, true),
            new RoleInfo("Operator", "Operator", all.Except(adminOnly).ToArray(), true),
            new RoleInfo("Viewer", "Viewer", [Permissions.Inventory, Permissions.Sessions, Permissions.Audit], true)
        })
        {
            Execute("INSERT OR IGNORE INTO roles VALUES($0,$1,$2,1)", role.Id, role.Name, JsonSerializer.Serialize(role.Permissions));
        }
        // Administrator is immutable and must include capabilities added by upgrades.
        Execute("UPDATE roles SET permissions=$0 WHERE id='Admin'", JsonSerializer.Serialize(all));
    }
    public List<RoleInfo> Roles() => Query(
        "SELECT id,name,permissions,builtIn FROM roles ORDER BY name", ReadRole);

    public RoleInfo? FindRole(string id) => Query(
        "SELECT id,name,permissions,builtIn FROM roles WHERE id=$0", ReadRole, id).SingleOrDefault();

    private long ActiveAdministratorCount => Scalar<long>(
        "SELECT COUNT(*) FROM users WHERE role='Admin' AND disabled=0");

    public bool HasPermission(UserInfo? user, string permission)
    {
        if (user is null || user.Disabled)
        {
            return false;
        }

        return Scalar<long>("""
            SELECT EXISTS(
                SELECT 1 FROM roles r JOIN json_each(r.permissions) capability
                WHERE r.id=$0 AND capability.value=$1)
            """, user.Role, permission) != 0;
    }

    private static RoleInfo ReadRole(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [], reader.GetBoolean(3));

    public bool HasAnyRemotePermission(UserInfo? user) => new[] {
        Permissions.Desktop,
        Permissions.Terminal,
        Permissions.Commands,
        Permissions.Processes,
        Permissions.FilesRead,
        Permissions.Rdp,
        Permissions.Vnc,
        Permissions.Ssh,
        Permissions.Amt
    }.Any(p => HasPermission(user, p));

    public string LandingPage(UserInfo user) => new[] {
        (Permissions.Inventory, "/"),
        (Permissions.Users, "/users"),
        (Permissions.Roles, "/roles"),
        (Permissions.Settings, "/settings"),
        (Permissions.Scripts, "/scripts"),
        (Permissions.Sessions, "/logs?tab=sessions"),
        (Permissions.Audit, "/logs?tab=audit"),
        (Permissions.Enroll, "/enroll")
    }.FirstOrDefault(p => HasPermission(user, p.Item1)).Item2 ?? "/account/security";

    public void SaveRole(string? id, string name, IEnumerable<string> permissions)
    {
        lock (gate)
        {
            if (id == "Admin")
            {
                throw new InvalidOperationException("The built-in Administrator role always retains full access.");
            }

            List<string> list = permissions.Distinct(StringComparer.Ordinal).ToList();
            if (list.Any(p => !Permissions.All.Any(a => a.Id == p)))
            {
                throw new InvalidOperationException("Unknown capability.");
            }

            if (list.Contains(Permissions.Control) && !list.Contains(Permissions.Desktop))
            {
                list.Add(Permissions.Desktop);
            }

            if (list.Contains(Permissions.FilesWrite) && !list.Contains(Permissions.FilesRead))
            {
                list.Add(Permissions.FilesRead);
            }

            if (list.Contains(Permissions.ExportAudit) && !list.Contains(Permissions.Audit))
            {
                list.Add(Permissions.Audit);
            }

            if (list.Any(p => p.StartsWith("desktop.", StringComparison.Ordinal) || p.StartsWith("files.", StringComparison.Ordinal) || p.StartsWith("network.", StringComparison.Ordinal) || p is Permissions.Commands or Permissions.Terminal or Permissions.Processes or Permissions.Power or Permissions.Groups or Permissions.EditDevices or Permissions.BlockDevices or Permissions.RemoveDevices) && !list.Contains(Permissions.Inventory))
            {
                list.Add(Permissions.Inventory);
            }

            string key = id ?? Guid.NewGuid().ToString("N");
            Execute("INSERT INTO roles VALUES($0,$1,$2,0) ON CONFLICT(id) DO UPDATE SET name=$1,permissions=$2; UPDATE users SET version=version+1 WHERE role=$0", key, Required(name, 70), JsonSerializer.Serialize(list.Order(StringComparer.Ordinal)));
        }
    }
    public void DeleteRole(string id)
    {
        lock (gate)
        {
            if (FindRole(id) is not
                {
                    BuiltIn: false
                })
            {
                throw new InvalidOperationException("Built-in roles cannot be deleted.");
            }

            if (Scalar<long>("SELECT COUNT(*) FROM users WHERE role=$0", id) > 0)
            {
                throw new InvalidOperationException("Assign users to another role before deleting this role.");
            }

            Execute("DELETE FROM roles WHERE id=$0", id);
        }
    }
    public void ChangeUserRole(string id, string role)
    {
        lock (gate)
        {
            if (FindRole(role) is null)
            {
                throw new InvalidOperationException("Role no longer exists.");
            }

            if (FindUser(id) is
                {
                    Role: "Admin", Disabled: false
                } && role != "Admin" && ActiveAdministratorCount <= 1)
            {
                throw new InvalidOperationException("Keep at least one active Administrator.");
            }

            Execute("UPDATE users SET role=$1,version=version+1 WHERE id=$0", id, role);
        }
    }
}
