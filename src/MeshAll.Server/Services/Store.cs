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

public record ScriptInfo(string Id, string Name, string Platform, string Code, string Kind = "auto");

public sealed partial class Store
{
    private readonly string connectionString;
    private readonly object gate = new();
    private readonly IDataProtector securityProtector;
    private readonly IDataProtector connectionPreferencesProtector;

    public string DataDirectory
    {
        get;
    }
    public string BootstrapPath => Path.Combine(DataDirectory, "bootstrap.key");

    public Store(IConfiguration config, IWebHostEnvironment environment, IDataProtectionProvider protection)
    {
        securityProtector = protection.CreateProtector("MeshAll.AccountSecurity.v1");
        connectionPreferencesProtector = protection.CreateProtector("MeshAll.UserConnections.v1");
        DataDirectory = Path.GetFullPath(config["MeshAll:DataDirectory"] ?? "Data", environment.ContentRootPath);
        string publicDirectory = Path.GetFullPath(environment.WebRootPath ?? "wwwroot", environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar);
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (DataDirectory.Equals(publicDirectory, pathComparison) || DataDirectory.StartsWith(publicDirectory + Path.DirectorySeparatorChar, pathComparison))
        {
            throw new InvalidOperationException("The private data directory must be outside the public web directory.");
        }
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "meshall.db"),
            DefaultTimeout = 15
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
            CREATE TABLE IF NOT EXISTS scripts(id TEXT PRIMARY KEY,name TEXT NOT NULL,platform TEXT NOT NULL,code TEXT NOT NULL,kind TEXT NOT NULL DEFAULT 'auto');
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
            CREATE TABLE IF NOT EXISTS remote_ports(deviceId TEXT NOT NULL,tool TEXT NOT NULL,port INTEGER NOT NULL,PRIMARY KEY(deviceId,tool));
            CREATE TABLE IF NOT EXISTS user_rdp_defaults(userId TEXT PRIMARY KEY,domain TEXT NOT NULL,username TEXT NOT NULL,password TEXT);
            CREATE TABLE IF NOT EXISTS web_sessions(id TEXT PRIMARY KEY,userId TEXT NOT NULL,version INTEGER NOT NULL,expires TEXT NOT NULL,revoked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS protected_devices(id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS remote_identities(deviceId TEXT NOT NULL,tool TEXT NOT NULL,port INTEGER NOT NULL,value TEXT NOT NULL,PRIMARY KEY(deviceId,tool,port));
            CREATE INDEX IF NOT EXISTS audit_time ON audit(time);
            CREATE INDEX IF NOT EXISTS sessions_started ON sessions(started DESC,id DESC);
            CREATE INDEX IF NOT EXISTS sessions_active ON sessions(ended) WHERE ended IS NULL;
            CREATE INDEX IF NOT EXISTS users_role ON users(role);
            CREATE INDEX IF NOT EXISTS devices_group_name ON devices(groupId,name COLLATE NOCASE,id);
            """);
        if (!Query("PRAGMA table_info(devices)", r => r.GetString(1)).Contains("removed", StringComparer.Ordinal))
        {
            Execute("ALTER TABLE devices ADD COLUMN removed INTEGER NOT NULL DEFAULT 0");
        }

        if (!Query("PRAGMA table_info(scripts)", r => r.GetString(1)).Contains("kind", StringComparer.Ordinal))
        {
            Execute("ALTER TABLE scripts ADD COLUMN kind TEXT NOT NULL DEFAULT 'auto'");
        }

        InitializeRoles();
        Execute("INSERT OR IGNORE INTO settings(key,value) VALUES('webSessionCutover',$0)", Now());
        if (!HasUsers && !File.Exists(BootstrapPath))
        {
            File.WriteAllText(BootstrapPath, Secret());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(BootstrapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    public List<GroupInfo> Groups() => Query("SELECT g.id,g.name,g.description,COALESCE(p.consent,0) FROM groups g LEFT JOIN group_policies p ON p.id=g.id ORDER BY g.name",
        r => new GroupInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));

    public bool RequiresDesktopConsent(string deviceId) => Query("SELECT COALESCE(p.consent,0) FROM devices d LEFT JOIN group_policies p ON d.groupId=p.id WHERE d.id=$0", r => r.GetInt32(0), deviceId).Any(c => (c & 8) != 0);

    public void SaveGroup(string? id, string name, string description, int consent = 0)
    {
        name = Required(name, 70);
        description = description.Trim();
        if (description.Length > 500)
        {
            throw new InvalidOperationException("Description is too long.");
        }

        Execute("INSERT INTO groups VALUES($0,$1,$2) ON CONFLICT(id) DO UPDATE SET name=$1,description=$2; INSERT OR REPLACE INTO group_policies VALUES($0,$3)",
            id ?? Guid.NewGuid().ToString("N"), name, description, consent);
    }

    public void DeleteGroup(string id)
    {
        if (id == "default")
        {
            throw new InvalidOperationException("The unassigned group cannot be removed.");
        }

        lock (gate)
        {
            Execute("UPDATE devices SET groupId='default' WHERE groupId=$0; DELETE FROM tokens WHERE groupId=$0; DELETE FROM groups WHERE id=$0", id);
        }
    }

    public List<DeviceInfo> Devices() => Query("SELECT id,name,groupId,tags,notes,disabled,lastSeen,inventory FROM devices WHERE removed=0 ORDER BY name", Device);

    public DeviceInfo? FindDevice(string id, bool includeRemoved = false) => Query("SELECT id,name,groupId,tags,notes,disabled,lastSeen,inventory FROM devices WHERE id=$0 AND (removed=0 OR $1=1)", Device, id, includeRemoved).SingleOrDefault();

    private static DeviceInfo Device(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetString(4), r.GetBoolean(5), Date(r.GetString(6)),
        r.IsDBNull(7) ? null : JsonSerializer.Deserialize<Inventory>(r.GetString(7), Wire.Json));

    public void SaveDevice(string id, string name, string group, string tags, string notes)
    {
        lock (gate)
        {
            if (!Groups().Any(g => g.Id == group))
            {
                throw new InvalidOperationException("Group no longer exists.");
            }

            Execute("UPDATE devices SET name=$1,groupId=$2,tags=$3,notes=$4 WHERE id=$0", id, Required(name, 100), group,
                tags.Length <= 300 ? tags.Trim() : throw new InvalidOperationException("Tags are too long."),
                notes.Length <= 4000 ? notes.Trim() : throw new InvalidOperationException("Notes are too long."));
        }
    }

    public void UpdateInventory(string id, Inventory inventory)
    {
        Execute("UPDATE devices SET inventory=$1,lastSeen=$2 WHERE id=$0", id, JsonSerializer.Serialize(inventory, Wire.Json), Now());
        if (ControllerBoundary.IsController(inventory))
        {
            Execute("INSERT OR IGNORE INTO protected_devices(id) VALUES($0)", id);
        }
    }

    public void TouchDevice(string id) => Execute("UPDATE devices SET lastSeen=$1 WHERE id=$0", id, Now());

    public void RemoveDevice(string id)
    {
        lock (gate)
        {
            Execute("UPDATE devices SET removed=1 WHERE id=$0", id);
        }
    }
    public void RestoreConnectedDevice(string id)
    {
        lock (gate)
        {
            Execute("UPDATE devices SET removed=0 WHERE id=$0 AND disabled=0", id);
        }
    }
    public void DisableDevice(string id) => Execute("UPDATE devices SET disabled=1,secret='' WHERE id=$0", id);

    public void SetDeviceBlocked(string id, bool blocked) => Execute("UPDATE devices SET disabled=$1 WHERE id=$0", id, blocked);

    public bool ValidAgent(string id, string secret) => Scalar<long>(
        "SELECT COUNT(*) FROM devices WHERE id=$0 AND secret=$1 AND disabled=0", id, Hash(secret)) == 1;

    public int VncPort(string id)
    {
        int configuredPort = Query("SELECT port FROM remote_ports WHERE deviceId=$0 AND tool='vnc'",
            reader => reader.GetInt32(0), id).SingleOrDefault();
        return configuredPort > 0 ? configuredPort : 5900;
    }

    public void SaveVncPort(string id, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Choose a port between 1 and 65535.");
        }

        if (FindDevice(id) is null)
        {
            throw new InvalidOperationException("Device not found.");
        }

        Execute("INSERT INTO remote_ports VALUES($0,'vnc',$1) ON CONFLICT(deviceId,tool) DO UPDATE SET port=$1", id, port);
    }

    public List<AuditInfo> AuditLog(int limit = PageRequest.MaximumSize) => Query(
        "SELECT id,time,actor,action,target,detail FROM audit ORDER BY id DESC LIMIT $0",
        ReadAudit, Math.Clamp(limit, 1, PageRequest.MaximumSize));

    public void Audit(string actor, string action, string target, string detail = "") => Execute(
        "INSERT INTO audit(time,actor,action,target,detail) VALUES($0,$1,$2,$3,$4)", Now(), actor, action, target, detail);

    public string StartSession(DeviceInfo device, string actor, string kind)
    {
        string id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO sessions VALUES($0,$1,$2,$3,$4,$5,NULL,'Active')", id, device.Id, device.Name, actor, kind, Now());
        return id;
    }
    public void EndSession(string id, string status) => Execute("UPDATE sessions SET ended=$1,status=$2 WHERE id=$0 AND ended IS NULL", id, Now(), status);

    public void RecoverInterruptedSessions() => Execute(
        "UPDATE sessions SET ended=strftime('%Y-%m-%dT%H:%M:%fZ','now'),status='Interrupted' WHERE ended IS NULL");

    public List<SessionInfo> Sessions() => SessionPage(new PageRequest()).Items.ToList();


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
        foreach (string? id in Query("SELECT localId FROM upstream", r => r.GetString(0)).Where(id => !keep.Contains(id)))
        {
            Execute("DELETE FROM devices WHERE id=$0; DELETE FROM groups WHERE id=$0; DELETE FROM upstream WHERE localId=$0", id);
        }
    }

    private static string Required(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max
        ? value.Trim() : throw new InvalidOperationException($"Enter a value between 1 and {max} characters.");

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    public static string Secret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

}
