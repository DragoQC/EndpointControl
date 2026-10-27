using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MeshAll.Server.Services;

public sealed partial class Store
{
    private const int PasswordHashIterations = 210_000;
    private const string UserColumns = "SELECT id,name,role,disabled,version,EXISTS(SELECT 1 FROM two_factor f WHERE f.userId=users.id) FROM users";

    public bool HasUsers => Scalar<long>("SELECT COUNT(*) FROM users") > 0;

    public List<UserInfo> Users() => Query(UserColumns + " ORDER BY name", User);

    public UserInfo? FindUser(string id) => Query(UserColumns + " WHERE id=$0", User, id).SingleOrDefault();

    private static UserInfo User(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetBoolean(3), reader.GetInt32(4), reader.GetBoolean(5));

    public UserInfo? Authenticate(string name, string password)
    {
        (string Id, string Hash, bool Disabled) row = Query("SELECT id,password,disabled FROM users WHERE name=$0 COLLATE NOCASE",
            reader => (Id: reader.GetString(0), Hash: reader.GetString(1), Disabled: reader.GetBoolean(2)), name).SingleOrDefault();
        bool valid = VerifyPassword(password, row.Hash ?? HashPassword("constant-time-dummy-password"));
        return valid && !row.Disabled && row.Id is not null ? FindUser(row.Id) : null;
    }

    public UserInfo Setup(string bootstrap, string name, string password)
    {
        lock (gate)
        {
            if (HasUsers || !File.Exists(BootstrapPath) || !Equal(bootstrap.Trim(), File.ReadAllText(BootstrapPath).Trim()))
            {
                throw new InvalidOperationException("The setup key is invalid or setup is already complete.");
            }

            UserInfo user = AddUser(name, password, "Admin");
            File.Delete(BootstrapPath);
            Audit(user.Name, "Workspace created", "EndpointControl", "First administrator configured");
            return user;
        }
    }

    public UserInfo AddUser(string name, string password, string role)
    {
        name = Required(name, 50);
        ValidatePassword(password);
        if (!Roles().Any(item => item.Id == role))
        {
            throw new InvalidOperationException("Choose a valid role.");
        }

        string id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO users(id,name,password,role) VALUES($0,$1,$2,$3)", id, name, HashPassword(password), role);
        return FindUser(id)!;
    }

    public void SetUserDisabled(string id, bool disabled, string currentId)
    {
        lock (gate)
        {
            if (id == currentId)
            {
                throw new InvalidOperationException("You cannot disable your own account.");
            }
            if (disabled && FindUser(id) is { Role: "Admin", Disabled: false } && ActiveAdministratorCount <= 1)
            {
                throw new InvalidOperationException("Keep at least one active Administrator.");
            }

            Execute("UPDATE users SET disabled=$1,version=version+1 WHERE id=$0", id, disabled);
        }
    }

    public void ChangePassword(string id, string password)
    {
        ValidatePassword(password);
        Execute("UPDATE users SET password=$1,version=version+1 WHERE id=$0", id, HashPassword(password));
    }

    private static void ValidatePassword(string value)
    {
        if (value.Length is < 12 or > 256)
        {
            throw new InvalidOperationException("Use a password between 12 and 256 characters.");
        }
    }

    private static string HashPassword(string value)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(
            Rfc2898DeriveBytes.Pbkdf2(value, salt, PasswordHashIterations, HashAlgorithmName.SHA256, 32));
    }

    private static bool VerifyPassword(string value, string hash)
    {
        try
        {
            string[] pieces = hash.Split(':');
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(pieces[1]),
                Rfc2898DeriveBytes.Pbkdf2(value, Convert.FromBase64String(pieces[0]), PasswordHashIterations, HashAlgorithmName.SHA256, 32));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
