using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MeshAll.Server.Services;

public sealed partial class Store
{
    public long DeviceCount => Scalar<long>("SELECT COUNT(*) FROM devices WHERE removed=0");
    public long BlockedDeviceCount => Scalar<long>("SELECT COUNT(*) FROM devices WHERE removed=0 AND disabled=1");
    public long ActiveSessionCount => Scalar<long>("SELECT COUNT(*) FROM sessions WHERE ended IS NULL");

    public PageResult<SessionInfo> SessionPage(PageRequest request, string search = "")
    {
        const string filter = "WHERE instr(lower(deviceName || ' ' || actor || ' ' || kind || ' ' || status),lower($0))>0";

        return ReadPage(
            $"SELECT id,deviceId,deviceName,actor,kind,started,ended,status FROM sessions {filter} ORDER BY started DESC,id DESC",
            $"SELECT COUNT(*) FROM sessions {filter}",
            ReadSession,
            request,
            search.Trim());
    }

    public PageResult<AuditInfo> AuditPage(PageRequest request, string search = "")
    {
        const string filter = "WHERE instr(lower(actor || ' ' || action || ' ' || target || ' ' || detail),lower($0))>0";

        return ReadPage(
            $"SELECT id,time,actor,action,target,detail FROM audit {filter} ORDER BY id DESC",
            $"SELECT COUNT(*) FROM audit {filter}",
            ReadAudit,
            request,
            search.Trim());
    }

    public PageResult<DeviceInfo> DevicePage(
        PageRequest request,
        IReadOnlyCollection<string> connectedIds,
        string search = "",
        string status = "all",
        string group = "")
    {
        const string filter = """
            WHERE removed=0
              AND ($0='' OR groupId=$0)
              AND instr(lower(name || ' ' || tags || ' ' || COALESCE(json_extract(inventory,'$.ip'),'')
                  || ' ' || COALESCE(json_extract(inventory,'$.os'),'')),lower($1))>0
              AND ($2='all'
                  OR ($2='revoked' AND disabled=1)
                  OR ($2='online' AND disabled=0 AND id IN (SELECT value FROM json_each($3)))
                  OR ($2='offline' AND disabled=0 AND id NOT IN (SELECT value FROM json_each($3))))
            """;

        return ReadPage(
            $"SELECT id,name,groupId,tags,notes,disabled,lastSeen,inventory FROM devices {filter} ORDER BY name COLLATE NOCASE,id",
            $"SELECT COUNT(*) FROM devices {filter}",
            Device,
            request,
            group,
            search.Trim(),
            status,
            JsonSerializer.Serialize(connectedIds));
    }

    public PageResult<UserInfo> UserPage(PageRequest request) => ReadPage(
        UserColumns + " ORDER BY name COLLATE NOCASE,id",
        "SELECT COUNT(*) FROM users",
        User,
        request);

    public PageResult<GroupInfo> GroupPage(PageRequest request) => ReadPage(
        """
        SELECT g.id,g.name,g.description,COALESCE(p.consent,0)
        FROM groups g LEFT JOIN group_policies p ON p.id=g.id
        ORDER BY g.name COLLATE NOCASE,g.id
        """,
        "SELECT COUNT(*) FROM groups",
        reader => new GroupInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)),
        request);

    public PageResult<RoleInfo> RolePage(PageRequest request) => ReadPage(
        "SELECT id,name,permissions,builtIn FROM roles ORDER BY name COLLATE NOCASE,id",
        "SELECT COUNT(*) FROM roles",
        reader => new RoleInfo(reader.GetString(0), reader.GetString(1),
            JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [], reader.GetBoolean(3)),
        request);

    public PageResult<TokenInfo> TokenPage(PageRequest request) => ReadPage(
        "SELECT id,name,groupId,expires,remaining FROM tokens ORDER BY expires DESC,id DESC",
        "SELECT COUNT(*) FROM tokens",
        reader => new TokenInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Date(reader.GetString(3)), reader.GetInt32(4)),
        request);

    public Dictionary<string, long> DeviceCountsByGroup() => Query(
        "SELECT groupId,COUNT(*) FROM devices WHERE removed=0 GROUP BY groupId",
        reader => (GroupId: reader.GetString(0), Count: reader.GetInt64(1)))
        .ToDictionary(row => row.GroupId, row => row.Count, StringComparer.Ordinal);

    public Dictionary<string, long> UserCountsByRole() => Query(
        "SELECT role,COUNT(*) FROM users GROUP BY role",
        reader => (RoleId: reader.GetString(0), Count: reader.GetInt64(1)))
        .ToDictionary(row => row.RoleId, row => row.Count, StringComparer.Ordinal);

    /// <summary>Explicit exports stream rows instead of buffering the entire log.</summary>
    public async IAsyncEnumerable<AuditInfo> ExportAudit(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = Open();
        await using SqliteCommand command = Command(connection,
            "SELECT id,time,actor,action,target,detail FROM audit ORDER BY id DESC", []);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return ReadAudit(reader);
        }
    }

    private PageResult<T> ReadPage<T>(
        string selectSql,
        string countSql,
        Func<SqliteDataReader, T> readRow,
        PageRequest request,
        params object?[] filters)
    {
        // Keep the count and rows in one read snapshot when live activity arrives.
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand countCommand = Command(connection, countSql, filters);
        countCommand.Transaction = transaction;

        long totalCount = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        PageRequest page = request.ClampTo(totalCount);
        object?[] parameters = [.. filters, page.Size, page.Offset];
        string pagingSql = $"{selectSql} LIMIT ${filters.Length} OFFSET ${filters.Length + 1}";
        using SqliteCommand pageCommand = Command(connection, pagingSql, parameters);
        pageCommand.Transaction = transaction;
        using SqliteDataReader reader = pageCommand.ExecuteReader();
        List<T> items = new(page.Size);

        while (reader.Read())
        {
            items.Add(readRow(reader));
        }

        return new PageResult<T>(items, totalCount, page.Number, page.Size);
    }

    private static SessionInfo ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), Date(reader.GetString(5)),
        reader.IsDBNull(6) ? null : Date(reader.GetString(6)), reader.GetString(7));

    private static AuditInfo ReadAudit(SqliteDataReader reader) => new(
        reader.GetInt64(0), Date(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5));

}
