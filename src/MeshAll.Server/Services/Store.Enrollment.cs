using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshAll.Shared;
using Microsoft.Data.Sqlite;

namespace MeshAll.Server.Services;

public sealed partial class Store
{
    public (string Id, string Token) CreateToken(string name, string groupId, int uses, int hours)
    {
        lock (gate)
        {
            if (!Groups().Any(group => group.Id == groupId))
            {
                throw new InvalidOperationException("Choose a valid group.");
            }
            if (uses is < 1 or > EnrollmentPolicy.MaximumDevices || hours is < 1 or > EnrollmentPolicy.MaximumHours)
            {
                throw new InvalidOperationException("Use 1–10,000 devices and 1–2,160 hours.");
            }

            string id = Guid.NewGuid().ToString("N");
            string token = Secret();
            Execute("INSERT INTO tokens VALUES($0,$1,$2,$3,$4,$5)", id, Required(name, 70), Hash(token), groupId,
                DateTimeOffset.UtcNow.AddHours(hours).ToString("O"), uses);
            Execute("INSERT INTO native_enrollments VALUES($0,$1)", Hash(NativeMeshKey(token)), id);
            return (id, token);
        }
    }

    public List<TokenInfo> Tokens() => Query("SELECT id,name,groupId,expires,remaining FROM tokens ORDER BY expires DESC",
        reader => new TokenInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), Date(reader.GetString(3)), reader.GetInt32(4)));

    public void RevokeToken(string id) => Execute("DELETE FROM tokens WHERE id=$0", id);

    public bool EnrollmentTokenUsable(string id) => Scalar<long>(
        "SELECT EXISTS(SELECT 1 FROM tokens WHERE id=$0 AND expires>$1 AND remaining>0)", id, Now()) != 0;

    public TokenInfo NativeEnrollmentToken(string token)
    {
        TokenInfo? enrollment = Query("SELECT id,name,groupId,expires,remaining FROM tokens WHERE hash=$0",
            reader => new TokenInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), Date(reader.GetString(3)), reader.GetInt32(4)), Hash(token)).SingleOrDefault();
        return enrollment is not null && enrollment.Expires > DateTimeOffset.UtcNow && enrollment.Remaining > 0
            ? enrollment : throw new InvalidOperationException("Enrollment key is invalid, expired, or exhausted.");
    }

    public EnrollmentResponse Enroll(EnrollmentRequest input)
    {
        if (input.Token is not { Length: 64 } || !input.Token.All(char.IsAsciiHexDigit)
            || input.Platform is null || input.Platform.Length > 60)
        {
            throw new InvalidOperationException("Invalid enrollment request.");
        }
        string name = Required(input.Name, 100);
        lock (gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand query = Command(connection,
                "SELECT id,groupId,expires,remaining FROM tokens WHERE hash=$0", [Hash(input.Token)]);
            query.Transaction = transaction;
            string tokenId;
            string groupId;
            using (SqliteDataReader reader = query.ExecuteReader())
            {
                if (!reader.Read() || Date(reader.GetString(2)) <= DateTimeOffset.UtcNow || reader.GetInt32(3) < 1)
                {
                    throw new InvalidOperationException("Enrollment token is invalid, expired, or exhausted.");
                }

                tokenId = reader.GetString(0);
                groupId = reader.GetString(1);
            }

            string id = Guid.NewGuid().ToString("N");
            string secret = Secret();
            using SqliteCommand insert = Command(connection,
                "INSERT INTO devices(id,name,groupId,secret,lastSeen) VALUES($0,$1,$2,$3,$4); UPDATE tokens SET remaining=remaining-1 WHERE id=$5",
                [id, name, groupId, Hash(secret), Now(), tokenId]);
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
            transaction.Commit();
            Audit("Agent", "Device enrolled", name, input.Platform);
            return new(id, secret);
        }
    }

    public static string NativeMeshKey(string token) => Convert.ToHexStringLower(SHA384.HashData(Encoding.UTF8.GetBytes(token)));

    public GroupInfo NativeEnrollmentGroup(string token)
    {
        (string Id, DateTimeOffset Expires, int Remaining) group = Query(
            "SELECT groupId,expires,remaining FROM tokens WHERE hash=$0",
            reader => (Id: reader.GetString(0), Expires: Date(reader.GetString(1)), Remaining: reader.GetInt32(2)), Hash(token)).SingleOrDefault();
        if (group.Id is null || group.Expires <= DateTimeOffset.UtcNow || group.Remaining < 1)
        {
            throw new InvalidOperationException("Enrollment key is invalid, expired, or exhausted.");
        }

        return Groups().Single(item => item.Id == group.Id);
    }

    public DeviceInfo RegisterNative(string certificate, string meshKey, string name, int architecture, Inventory inventory)
    {
        lock (gate)
        {
            string? existing = Query("SELECT deviceId FROM native_identities WHERE certificate=$0", reader => reader.GetString(0), certificate).SingleOrDefault();
            if (existing is not null)
            {
                DeviceInfo device = FindDevice(existing, includeRemoved: true) ?? throw new InvalidOperationException("Native identity has no device record.");
                if (device.Disabled)
                {
                    throw new UnauthorizedAccessException("Device access is blocked.");
                }

                RestoreConnectedDevice(existing);
                Inventory saved = device.Inventory is null ? inventory : device.Inventory with
                {
                    ConnectionAddress = inventory.ConnectionAddress,
                    AgentArchitecture = architecture,
                    Version = inventory.Version,
                    Capabilities = inventory.Capabilities
                };
                UpdateInventory(existing, saved);
                return FindDevice(existing)!;
            }

            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand lookup = Command(connection,
                "SELECT t.id,t.groupId,t.expires,t.remaining FROM native_enrollments n JOIN tokens t ON n.tokenId=t.id WHERE n.meshHash=$0",
                [Hash(meshKey)]);
            lookup.Transaction = transaction;
            string tokenId;
            string groupId;
            using (SqliteDataReader reader = lookup.ExecuteReader())
            {
                if (!reader.Read() || Date(reader.GetString(2)) <= DateTimeOffset.UtcNow || reader.GetInt32(3) < 1)
                {
                    throw new UnauthorizedAccessException("Native enrollment key is invalid, expired, or exhausted.");
                }

                tokenId = reader.GetString(0);
                groupId = reader.GetString(1);
            }

            string id = Guid.NewGuid().ToString("N");
            using SqliteCommand insert = Command(connection,
                "INSERT INTO devices(id,name,groupId,secret,lastSeen,inventory) VALUES($0,$1,$2,'',$3,$4); INSERT INTO native_identities VALUES($5,$0,$6); UPDATE tokens SET remaining=remaining-1 WHERE id=$7",
                [id, Required(name, 100), groupId, Now(), JsonSerializer.Serialize(inventory, Wire.Json), certificate, architecture, tokenId]);
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
            transaction.Commit();
            Audit("Agent", "Native EndpointControl Agent enrolled", name);
            UpdateInventory(id, inventory);
            return FindDevice(id)!;
        }
    }

    public bool IsNative(string id) => Scalar<long>("SELECT COUNT(*) FROM native_identities WHERE deviceId=$0", id) > 0;
}
