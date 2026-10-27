using System.Globalization;
using MeshAll.Server.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshAll.Tests;

public sealed class PagingTests
{
    [Fact]
    public void SessionPagesAreBoundedOrderedAndSearchTheEntireHistory()
    {
        using TestServer server = new();
        Store store = server.Store;
        SeedSessions(store, 257);

        PageResult<SessionInfo> first = store.SessionPage(new PageRequest(1, 5000));
        PageResult<SessionInfo> second = store.SessionPage(new PageRequest(2));
        PageResult<SessionInfo> last = store.SessionPage(new PageRequest(999));

        Assert.Equal(257, first.TotalCount);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal("Session-0256", first.Items[0].DeviceName);
        Assert.Equal("Session-0206", second.Items[0].DeviceName);
        Assert.Empty(first.Items.Select(session => session.Id).Intersect(second.Items.Select(session => session.Id)));
        Assert.Equal(6, last.PageNumber);
        Assert.Equal(7, last.Items.Count);
        Assert.Equal(251, last.FirstItem);
        Assert.Equal(257, last.LastItem);
        Assert.Equal(257, store.ActiveSessionCount);
        Assert.Equal(50, store.Sessions().Count);

        PageResult<SessionInfo> oldMatch = store.SessionPage(new PageRequest(1), "Session-0000");
        Assert.Single(oldMatch.Items);
        Assert.Equal(1, oldMatch.TotalCount);
        Assert.Equal("Session-0000", oldMatch.Items[0].DeviceName);
        Assert.Empty(store.SessionPage(new PageRequest(), "%' OR 1=1 --").Items);
    }

    [Fact]
    public void UnrequestedSessionRowsAreNeverMaterialized()
    {
        using TestServer server = new();
        Store store = server.Store;
        SeedSessions(store, 80);
        Execute(store, """
            INSERT INTO sessions VALUES('old-corrupt-row','device','Old row','test','Command','0000-invalid',NULL,'Active')
            """);

        // Deserializing all rows before Take(50) would parse this invalid date
        // and fail. The count sees it, while the requested page never reads it.
        PageResult<SessionInfo> page = store.SessionPage(new PageRequest());
        Assert.Equal(81, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
    }

    [Fact]
    public void AuditPaginationCountsAllEventsWithoutReadingOlderRows()
    {
        using TestServer server = new();
        Store store = server.Store;
        Execute(store, "INSERT INTO audit(time,actor,action,target,detail) VALUES('invalid-date','test','Old fixture','old','')");
        for (int index = 0; index < 115; index++)
        {
            store.Audit("test", "Paged event", $"Target-{index:D4}");
        }

        PageResult<AuditInfo> page = store.AuditPage(new PageRequest(2));
        Assert.Equal(116, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal("Target-0064", page.Items[0].Target);
        Assert.Equal(50, store.AuditLog(10000).Count);
        Assert.Single(store.AuditPage(new PageRequest(), "Target-0000").Items);
    }

    [Fact]
    public void DeviceFiltersAndPaginationExcludeHiddenDevicesAndRespectConnectionState()
    {
        using TestServer server = new();
        Store store = server.Store;
        using SqliteConnection connection = Open(store);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices(id,name,groupId,secret,lastSeen,disabled,removed)
            VALUES($id,$name,'default','fixture',$seen,$blocked,$removed)
            """;
        for (int index = 0; index < 75; index++)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", index.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$name", $"Device-{index:D3}");
            command.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$blocked", index == 74 ? 1 : 0);
            command.Parameters.AddWithValue("$removed", index == 73 ? 1 : 0);
            command.ExecuteNonQuery();
        }
        transaction.Commit();

        string[] connected = ["0", "1", "73", "74"];
        PageResult<DeviceInfo> all = store.DevicePage(new PageRequest(), connected);
        PageResult<DeviceInfo> online = store.DevicePage(new PageRequest(), connected, status: "online");
        PageResult<DeviceInfo> offline = store.DevicePage(new PageRequest(), connected, status: "offline");
        Assert.Equal(74, all.TotalCount);
        Assert.Equal(50, all.Items.Count);
        Assert.Equal(2, online.TotalCount);
        Assert.Equal(71, offline.TotalCount);
        Assert.Equal(1, store.DevicePage(new PageRequest(), connected, status: "revoked").TotalCount);
        Assert.Equal(1, store.DevicePage(new PageRequest(), connected, search: "Device-072").TotalCount);
        Assert.Equal(74, store.DeviceCountsByGroup()["default"]);
    }

    [Fact]
    public void AllManagementListsUseTheSameMaximumPageSize()
    {
        using TestServer server = new();
        Store store = server.Store;
        using SqliteConnection connection = Open(store);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO users VALUES($id,$name,'fixture-hash','Viewer',0,1);
            INSERT INTO groups VALUES($id,$name,'fixture');
            INSERT INTO roles VALUES($id,$name,'[]',0);
            INSERT INTO tokens VALUES($id,$name,$id,'default',$expires,1);
            INSERT INTO scripts VALUES($id,$name || '.ps1','Windows','Write-Output fixture','.ps1');
            """;
        for (int index = 0; index < 61; index++)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", $"fixture-{index:D3}");
            command.Parameters.AddWithValue("$name", $"Fixture {index:D3}");
            command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();

        PageRequest request = new(1, int.MaxValue);
        Assert.Equal(50, store.UserPage(request).Items.Count);
        Assert.Equal(50, store.GroupPage(request).Items.Count);
        Assert.Equal(50, store.RolePage(request).Items.Count);
        Assert.Equal(50, store.TokenPage(request).Items.Count);
        Assert.Equal(50, store.ScriptPage(request).Items.Count);
        Assert.Equal(61, store.UserPage(request).TotalCount);
        Assert.Equal(11, store.UserPage(new PageRequest(2)).Items.Count);
        Assert.Equal(61, store.UserCountsByRole()["Viewer"]);
    }

    [Fact]
    public void EmptyAndDefaultRequestsHaveUsefulBoundedMetadata()
    {
        PageResult<int> empty = PageResult<int>.FromSnapshot([], default);
        Assert.Equal(50, empty.PageSize);
        Assert.Equal(1, empty.PageNumber);
        Assert.Equal(0, empty.FirstItem);
        Assert.Equal(0, empty.LastItem);
        Assert.Equal(1, empty.TotalPages);
        Assert.Equal(50, new PageRequest().Size);
        Assert.Equal(1, new PageRequest(-1, -1).Size);
    }

    private static void SeedSessions(Store store, int count)
    {
        using SqliteConnection connection = Open(store);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sessions VALUES($id,'device',$name,'test','Command',$started,NULL,'Active')";
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < count; index++)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", $"session-{index:D4}");
            command.Parameters.AddWithValue("$name", $"Session-{index:D4}");
            command.Parameters.AddWithValue("$started", start.AddSeconds(index).ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static SqliteConnection Open(Store store)
    {
        SqliteConnection connection = new("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db"));
        connection.Open();
        return connection;
    }

    private static void Execute(Store store, string sql)
    {
        using SqliteConnection connection = Open(store);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
