using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshAll.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;

namespace MeshAll.Server.Services;

public record UserInfo(string Id, string Name, string Role, bool Disabled, int Version, bool TwoFactorEnabled = false);
public record DeviceInfo(string Id, string Name, string GroupId, string Tags, string Notes,
    bool Disabled, DateTimeOffset LastSeen, Inventory? Inventory);
public record GroupInfo(string Id, string Name, string Description, int Consent = 0);
public record AuditInfo(long Id, DateTimeOffset Time, string Actor, string Action, string Target, string Detail);
public record SessionInfo(string Id, string DeviceId, string DeviceName, string Actor, string Kind,
    DateTimeOffset Started, DateTimeOffset? Ended, string Status);
public record TokenInfo(string Id, string Name, string GroupId, DateTimeOffset Expires, int Remaining);
public record ScriptInfo(string Id, string Name, string Platform, string Code);

public sealed partial class Store
{
    private readonly string connectionString;
    private readonly object gate = new();
    private readonly IDataProtector securityProtector;
    public string DataDirectory { get; }
    public string BootstrapPath => Path.Combine(DataDirectory, "bootstrap.key");

    public Store(IConfiguration config, IWebHostEnvironment environment, IDataProtectionProvider protection)
    {
        securityProtector = protection.CreateProtector("MeshAll.AccountSecurity.v1");
        DataDirectory = Path.GetFullPath(config["MeshAll:DataDirectory"] ?? "Data", environment.ContentRootPath);
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "meshall.db"), DefaultTimeout = 15
        }.ToString();
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE,
              password TEXT NOT NULL, role TEXT NOT NULL, disabled INTEGER NOT NULL DEFAULT 0, version INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS groups(id TEXT PRIMARY KEY,name TEXT NOT NULL,description TEXT NOT NULL);
            INSERT OR IGNORE INTO groups VALUES('default','Unassigned','Devices awaiting a home');
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,name TEXT NOT NULL,groupId TEXT NOT NULL,
              tags TEXT NOT NULL DEFAULT '',notes TEXT NOT NULL DEFAULT '',secret TEXT NOT NULL,
              disabled INTEGER NOT NULL DEFAULT 0,lastSeen TEXT NOT NULL,inventory TEXT,removed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS tokens(id TEXT PRIMARY KEY,name TEXT NOT NULL,hash TEXT NOT NULL UNIQUE,
              groupId TEXT NOT NULL,expires TEXT NOT NULL,remaining INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT,time TEXT NOT NULL,
              actor TEXT NOT NULL,action TEXT NOT NULL,target TEXT NOT NULL,detail TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,deviceId TEXT NOT NULL,deviceName TEXT NOT NULL,
              actor TEXT NOT NULL,kind TEXT NOT NULL,started TEXT NOT NULL,ended TEXT,status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS scripts(id TEXT PRIMARY KEY,name TEXT NOT NULL,platform TEXT NOT NULL,code TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS upstream(localId TEXT PRIMARY KEY,remoteId TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS group_policies(id TEXT PRIMARY KEY,consent INTEGER NOT NULL DEFAULT 0);
            INSERT OR IGNORE INTO group_policies VALUES('default',0);
            CREATE TABLE IF NOT EXISTS native_enrollments(meshHash TEXT PRIMARY KEY,tokenId TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS native_identities(certificate TEXT PRIMARY KEY,deviceId TEXT NOT NULL UNIQUE,architecture INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS two_factor(userId TEXT PRIMARY KEY,secret TEXT NOT NULL,lastStep INTEGER NOT NULL DEFAULT -1);
            CREATE TABLE IF NOT EXISTS two_factor_setup(userId TEXT PRIMARY KEY,secret TEXT NOT NULL,expires TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recovery_codes(userId TEXT NOT NULL,hash TEXT NOT NULL,PRIMARY KEY(userId,hash));
            CREATE TABLE IF NOT EXISTS login_challenges(id TEXT PRIMARY KEY,userId TEXT NOT NULL,version INTEGER NOT NULL,expires TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS security_receipts(id TEXT PRIMARY KEY,userId TEXT NOT NULL,payload TEXT NOT NULL,expires TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS roles(id TEXT PRIMARY KEY,name TEXT NOT NULL COLLATE NOCASE UNIQUE,permissions TEXT NOT NULL,builtIn INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS audit_time ON audit(time);
            UPDATE sessions SET ended=strftime('%Y-%m-%dT%H:%M:%fZ','now'),status='Interrupted' WHERE ended IS NULL;
            """);
        if(!Query("PRAGMA table_info(devices)",r=>r.GetString(1)).Contains("removed",StringComparer.Ordinal))
            Execute("ALTER TABLE devices ADD COLUMN removed INTEGER NOT NULL DEFAULT 0");
        InitializeRoles();
        if (!HasUsers && !File.Exists(BootstrapPath))
        {
            File.WriteAllText(BootstrapPath, Secret());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(BootstrapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public bool HasUsers => Scalar<long>("SELECT COUNT(*) FROM users") > 0;
    private const string UserColumns = "SELECT id,name,role,disabled,version,EXISTS(SELECT 1 FROM two_factor f WHERE f.userId=users.id) FROM users";
    public List<UserInfo> Users() => Query(UserColumns+" ORDER BY name", User);
    public UserInfo? FindUser(string id) => Query(UserColumns+" WHERE id=$0", User, id).SingleOrDefault();
    private static UserInfo User(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetInt32(4),r.GetBoolean(5));

    public UserInfo? Authenticate(string name, string password)
    {
        var row = Query("SELECT id,password,disabled FROM users WHERE name=$0 COLLATE NOCASE",
            r => (Id: r.GetString(0), Hash: r.GetString(1), Disabled: r.GetBoolean(2)), name).SingleOrDefault();
        var valid = VerifyPassword(password, row.Hash ?? HashPassword("constant-time-dummy-password"));
        return valid && !row.Disabled && row.Id is not null ? FindUser(row.Id) : null;
    }

    public UserInfo Setup(string bootstrap, string name, string password)
    {
        lock (gate)
        {
            if (HasUsers || !File.Exists(BootstrapPath) || !Equal(bootstrap.Trim(), File.ReadAllText(BootstrapPath).Trim()))
                throw new InvalidOperationException("The setup key is invalid or setup is already complete.");
            var user = AddUser(name, password, "Admin");
            File.Delete(BootstrapPath);
            Audit(user.Name, "Workspace created", "MeshAll", "First administrator configured");
            return user;
        }
    }

    public UserInfo AddUser(string name, string password, string role)
    {
        name = Required(name, 50);
        ValidatePassword(password);
        if (!Roles().Any(r=>r.Id==role)) throw new InvalidOperationException("Choose a valid role.");
        var id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO users(id,name,password,role) VALUES($0,$1,$2,$3)", id, name, HashPassword(password), role);
        return FindUser(id)!;
    }

    public void SetUserDisabled(string id, bool disabled, string currentId)
    {
        lock(gate)
        {
            if (id == currentId) throw new InvalidOperationException("You cannot disable your own account.");
            if(disabled && FindUser(id) is {Role:"Admin",Disabled:false} && Users().Count(u=>u.Role=="Admin"&&!u.Disabled)<=1)throw new InvalidOperationException("Keep at least one active Administrator.");
            Execute("UPDATE users SET disabled=$1,version=version+1 WHERE id=$0", id, disabled);
        }
    }

    public void ChangePassword(string id, string password)
    {
        ValidatePassword(password);
        Execute("UPDATE users SET password=$1,version=version+1 WHERE id=$0", id, HashPassword(password));
    }

    public List<GroupInfo> Groups() => Query("SELECT g.id,g.name,g.description,COALESCE(p.consent,0) FROM groups g LEFT JOIN group_policies p ON p.id=g.id ORDER BY g.name",
        r => new GroupInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
    public bool RequiresDesktopConsent(string deviceId) => Query("SELECT COALESCE(p.consent,0) FROM devices d LEFT JOIN group_policies p ON d.groupId=p.id WHERE d.id=$0", r => r.GetInt32(0), deviceId).Any(c => (c & 8) != 0);

    public void SaveGroup(string? id, string name, string description, int consent = 0)
    {
        name = Required(name, 70); description = description.Trim();
        if (description.Length > 500) throw new InvalidOperationException("Description is too long.");
        Execute("INSERT INTO groups VALUES($0,$1,$2) ON CONFLICT(id) DO UPDATE SET name=$1,description=$2; INSERT OR REPLACE INTO group_policies VALUES($0,$3)",
            id ?? Guid.NewGuid().ToString("N"), name, description, consent);
    }

    public void DeleteGroup(string id)
    {
        if (id == "default") throw new InvalidOperationException("The unassigned group cannot be removed.");
        lock (gate)
        {
            Execute("UPDATE devices SET groupId='default' WHERE groupId=$0; DELETE FROM tokens WHERE groupId=$0; DELETE FROM groups WHERE id=$0", id);
        }
    }

    public List<DeviceInfo> Devices() => Query("SELECT id,name,groupId,tags,notes,disabled,lastSeen,inventory FROM devices WHERE removed=0 ORDER BY name", Device);
    public DeviceInfo? FindDevice(string id,bool includeRemoved=false) => Query("SELECT id,name,groupId,tags,notes,disabled,lastSeen,inventory FROM devices WHERE id=$0 AND (removed=0 OR $1=1)", Device, id,includeRemoved).SingleOrDefault();
    private static DeviceInfo Device(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetString(4), r.GetBoolean(5), Date(r.GetString(6)),
        r.IsDBNull(7) ? null : JsonSerializer.Deserialize<Inventory>(r.GetString(7), Wire.Json));

    public void SaveDevice(string id, string name, string group, string tags, string notes)
    {
        lock (gate)
        {
            if (!Groups().Any(g => g.Id == group)) throw new InvalidOperationException("Group no longer exists.");
            Execute("UPDATE devices SET name=$1,groupId=$2,tags=$3,notes=$4 WHERE id=$0", id, Required(name, 100), group,
                tags.Length <= 300 ? tags.Trim() : throw new InvalidOperationException("Tags are too long."),
                notes.Length <= 4000 ? notes.Trim() : throw new InvalidOperationException("Notes are too long."));
        }
    }

    public void UpdateInventory(string id, Inventory inventory) => Execute(
        "UPDATE devices SET inventory=$1,lastSeen=$2 WHERE id=$0", id, JsonSerializer.Serialize(inventory, Wire.Json), Now());
    public void TouchDevice(string id)=>Execute("UPDATE devices SET lastSeen=$1 WHERE id=$0",id,Now());
    public void RemoveDevice(string id){lock(gate)Execute("UPDATE devices SET removed=1 WHERE id=$0",id);}
    public void RestoreConnectedDevice(string id){lock(gate)Execute("UPDATE devices SET removed=0 WHERE id=$0 AND disabled=0",id);}
    public void DisableDevice(string id) => Execute("UPDATE devices SET disabled=1,secret='' WHERE id=$0", id);
    public void SetDeviceBlocked(string id, bool blocked) => Execute("UPDATE devices SET disabled=$1 WHERE id=$0", id, blocked);
    public bool ValidAgent(string id, string secret) => Scalar<long>(
        "SELECT COUNT(*) FROM devices WHERE id=$0 AND secret=$1 AND disabled=0", id, Hash(secret)) == 1;

    public (string Id, string Token) CreateToken(string name, string groupId, int uses, int hours)
    {
        lock (gate)
        {
            if (!Groups().Any(g => g.Id == groupId)) throw new InvalidOperationException("Choose a valid group.");
            if (uses is < 1 or > 100 || hours is < 1 or > 168) throw new InvalidOperationException("Use 1–100 devices and 1–168 hours.");
            var id = Guid.NewGuid().ToString("N"); var token = Secret();
            Execute("INSERT INTO tokens VALUES($0,$1,$2,$3,$4,$5)", id, Required(name, 70), Hash(token), groupId,
                DateTimeOffset.UtcNow.AddHours(hours).ToString("O"), uses);
            Execute("INSERT INTO native_enrollments VALUES($0,$1)", Hash(NativeMeshKey(token)), id);
            return (id, token);
        }
    }

    public List<TokenInfo> Tokens() => Query("SELECT id,name,groupId,expires,remaining FROM tokens ORDER BY expires DESC",
        r => new TokenInfo(r.GetString(0), r.GetString(1), r.GetString(2), Date(r.GetString(3)), r.GetInt32(4)));
    public void RevokeToken(string id) => Execute("DELETE FROM tokens WHERE id=$0", id);

    public EnrollmentResponse Enroll(EnrollmentRequest input)
    {
        var name = Required(input.Name, 100);
        lock (gate)
        {
            using var c = Open(); using var transaction = c.BeginTransaction();
            using var query = c.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT id,groupId,expires,remaining FROM tokens WHERE hash=$0";
            query.Parameters.AddWithValue("$0", Hash(input.Token));
            string tokenId, group;
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read() || Date(reader.GetString(2)) <= DateTimeOffset.UtcNow || reader.GetInt32(3) < 1)
                    throw new InvalidOperationException("Enrollment token is invalid, expired, or exhausted.");
                tokenId = reader.GetString(0); group = reader.GetString(1);
            }
            var id = Guid.NewGuid().ToString("N"); var secret = Secret();
            using var insert = c.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO devices(id,name,groupId,secret,lastSeen) VALUES($0,$1,$2,$3,$4); UPDATE tokens SET remaining=remaining-1 WHERE id=$5";
            object[] values = [id, name, group, Hash(secret), Now(), tokenId];
            for (var i = 0; i < values.Length; i++) insert.Parameters.AddWithValue($"${i}", values[i]);
            insert.ExecuteNonQuery(); transaction.Commit();
            Audit("Agent", "Device enrolled", name, input.Platform.Length <= 60 ? input.Platform : "Unknown");
            return new(id, secret);
        }
    }

    public static string NativeMeshKey(string token) => Convert.ToHexStringLower(SHA384.HashData(Encoding.UTF8.GetBytes(token)));
    public GroupInfo NativeEnrollmentGroup(string token)
    {
        var group = Query("SELECT groupId,expires,remaining FROM tokens WHERE hash=$0", r => (Id:r.GetString(0),Expires:Date(r.GetString(1)),Remaining:r.GetInt32(2)), Hash(token)).SingleOrDefault();
        if (group.Id is null || group.Expires <= DateTimeOffset.UtcNow || group.Remaining < 1) throw new InvalidOperationException("Enrollment key is invalid, expired, or exhausted.");
        return Groups().Single(g => g.Id == group.Id);
    }
    public DeviceInfo RegisterNative(string certificate, string meshKey, string name, int architecture, Inventory inventory)
    {
        lock (gate)
        {
            var existing = Query("SELECT deviceId FROM native_identities WHERE certificate=$0", r => r.GetString(0), certificate).SingleOrDefault();
            if (existing is not null)
            {
                var device = FindDevice(existing,includeRemoved:true) ?? throw new InvalidOperationException("Native identity has no device record.");
                if (device.Disabled) throw new UnauthorizedAccessException("Device access is blocked.");
                RestoreConnectedDevice(existing);
                var saved=device.Inventory is null?inventory:device.Inventory with{ConnectionAddress=inventory.ConnectionAddress,AgentArchitecture=architecture,Version=inventory.Version,Capabilities=inventory.Capabilities};
                UpdateInventory(existing, saved); return FindDevice(existing)!;
            }
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using var lookup = connection.CreateCommand(); lookup.Transaction = transaction;
            lookup.CommandText = "SELECT t.id,t.groupId,t.expires,t.remaining FROM native_enrollments n JOIN tokens t ON n.tokenId=t.id WHERE n.meshHash=$0";
            lookup.Parameters.AddWithValue("$0", Hash(meshKey)); string token, group;
            using (var reader = lookup.ExecuteReader())
            {
                if (!reader.Read() || Date(reader.GetString(2)) <= DateTimeOffset.UtcNow || reader.GetInt32(3) < 1) throw new UnauthorizedAccessException("Native enrollment key is invalid, expired, or exhausted.");
                token = reader.GetString(0); group = reader.GetString(1);
            }
            var id = Guid.NewGuid().ToString("N");
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO devices(id,name,groupId,secret,lastSeen,inventory) VALUES($0,$1,$2,'',$3,$4); INSERT INTO native_identities VALUES($5,$0,$6); UPDATE tokens SET remaining=remaining-1 WHERE id=$7";
            object[] values = [id, Required(name,100), group, Now(), JsonSerializer.Serialize(inventory,Wire.Json), certificate, architecture, token];
            for (var i=0;i<values.Length;i++) insert.Parameters.AddWithValue($"${i}",values[i]);
            insert.ExecuteNonQuery(); transaction.Commit(); Audit("Agent","Native MeshAll Agent enrolled",name); return FindDevice(id)!;
        }
    }
    public bool IsNative(string id) => Scalar<long>("SELECT COUNT(*) FROM native_identities WHERE deviceId=$0",id)>0;

    public List<AuditInfo> AuditLog(int limit = 250) => Query(
        "SELECT id,time,actor,action,target,detail FROM audit ORDER BY id DESC LIMIT $0",
        r => new AuditInfo(r.GetInt64(0), Date(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)), limit);
    public void Audit(string actor, string action, string target, string detail = "") => Execute(
        "INSERT INTO audit(time,actor,action,target,detail) VALUES($0,$1,$2,$3,$4)", Now(), actor, action, target, detail);

    public string StartSession(DeviceInfo device, string actor, string kind)
    {
        var id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO sessions VALUES($0,$1,$2,$3,$4,$5,NULL,'Active')", id, device.Id, device.Name, actor, kind, Now());
        return id;
    }
    public void EndSession(string id, string status) => Execute("UPDATE sessions SET ended=$1,status=$2 WHERE id=$0 AND ended IS NULL", id, Now(), status);
    public List<SessionInfo> Sessions() => Query("SELECT id,deviceId,deviceName,actor,kind,started,ended,status FROM sessions ORDER BY started DESC LIMIT 250",
        r => new SessionInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Date(r.GetString(5)),
            r.IsDBNull(6) ? null : Date(r.GetString(6)), r.GetString(7)));

    public List<ScriptInfo> Scripts() => Query("SELECT id,name,platform,code FROM scripts ORDER BY name",
        r => new ScriptInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
    public void SaveScript(string? id, string name, string platform, string code)
    {
        if (platform is not ("Windows" or "Linux" or "Any")) throw new InvalidOperationException("Invalid platform.");
        Execute("INSERT INTO scripts VALUES($0,$1,$2,$3) ON CONFLICT(id) DO UPDATE SET name=$1,platform=$2,code=$3",
            id ?? Guid.NewGuid().ToString("N"), Required(name, 80), platform, Required(code, 16000));
    }
    public void DeleteScript(string id) => Execute("DELETE FROM scripts WHERE id=$0", id);
    public string Setting(string key, string fallback) => Query("SELECT value FROM settings WHERE key=$0", r => r.GetString(0), key).SingleOrDefault() ?? fallback;
    public void Setting(string key, string value, bool save) => Execute("INSERT INTO settings VALUES($0,$1) ON CONFLICT(key) DO UPDATE SET value=$1", key, value);

    public static string RemoteKey(string remote) => Hash(remote)[..32];
    public string? RemoteId(string local) => Query("SELECT remoteId FROM upstream WHERE localId=$0", r => r.GetString(0), local).SingleOrDefault();
    public void ImportGroup(string id, string remote, string name, string description, int consent)
    {
        Execute("INSERT INTO groups VALUES($0,$1,$2) ON CONFLICT(id) DO UPDATE SET name=$1,description=$2; INSERT OR REPLACE INTO upstream VALUES($0,$3); INSERT OR REPLACE INTO group_policies VALUES($0,$4)", id, name, description, remote, consent);
    }
    public void ImportDevice(string id, string remote, string name, string group, string tags, string notes, Inventory inventory, bool online)
    {
        Execute("""
            INSERT INTO devices(id,name,groupId,tags,notes,secret,lastSeen,inventory) VALUES($0,$1,$2,$3,$4,'',$5,$6)
            ON CONFLICT(id) DO UPDATE SET name=$1,groupId=$2,tags=$3,notes=$4,inventory=$6,
              lastSeen=CASE WHEN $7=1 THEN $5 ELSE devices.lastSeen END;
            INSERT OR REPLACE INTO upstream VALUES($0,$8)
            """, id, name, group, tags, notes, online ? Now() : DateTimeOffset.UnixEpoch.ToString("O"),
            JsonSerializer.Serialize(inventory, Wire.Json), online ? 1 : 0, remote);
    }
    public void PruneRemote(HashSet<string> keep)
    {
        foreach (var id in Query("SELECT localId FROM upstream", r => r.GetString(0)).Where(id => !keep.Contains(id)))
            Execute("DELETE FROM devices WHERE id=$0; DELETE FROM groups WHERE id=$0; DELETE FROM upstream WHERE localId=$0", id);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private void Execute(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); cmd.ExecuteNonQuery();
    }
    private T Scalar<T>(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); using var reader = cmd.ExecuteReader();
        var rows = new List<T>(); while (reader.Read()) rows.Add(map(reader)); return rows;
    }
    private static SqliteCommand Command(SqliteConnection c, string sql, object?[] values)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue($"${i}", values[i] ?? DBNull.Value);
        return cmd;
    }
    private static string Required(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max
        ? value.Trim() : throw new InvalidOperationException($"Enter a value between 1 and {max} characters.");
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    public static string Secret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    private static void ValidatePassword(string value)
    {
        if (value.Length is < 12 or > 256) throw new InvalidOperationException("Use a password between 12 and 256 characters.");
    }
    private static string HashPassword(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(value, salt, 210000, HashAlgorithmName.SHA256, 32));
    }
    private static bool VerifyPassword(string value, string hash)
    {
        try
        {
            var pieces = hash.Split(':');
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(pieces[1]),
                Rfc2898DeriveBytes.Pbkdf2(value, Convert.FromBase64String(pieces[0]), 210000, HashAlgorithmName.SHA256, 32));
        }
        catch (FormatException) { return false; }
    }
}
