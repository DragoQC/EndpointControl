using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshAll.Tests;

public class DeviceRemovalTests
{
    private static Inventory Report => new("returning-device", "Windows", "Windows 11", "x64", "test-user", "192.0.2.10", "00:11:22:33:44:55", 4, 8000000000, 2000000000, 5, 100, "test", [], []);

    [Fact]
    public void RemovedNativeDeviceReturnsWithItsIdentityAndPolicyAfterEnrollmentKeyExpires()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        store.SaveGroup("office", "Office", "Test policy", 73);
        (string Id, string Token) token = store.CreateToken("First enrollment", "office", 1, 1);
        DeviceInfo? device = store.RegisterNative("known-native-certificate", Store.NativeMeshKey(token.Token), "PC", 4, Report);
        store.SaveDevice(device.Id, "Office PC", "office", "important", "Keep policy when returning");
        store.RemoveDevice(device.Id);
        store.TouchDevice(device.Id);
        store.UpdateInventory(device.Id, Report with
        {
            Uptime = 200
        });
        Assert.Empty(store.Devices());
        Assert.Null(store.FindDevice(device.Id));
        Assert.True(store.IsNative(device.Id));
        store.RevokeToken(token.Id);
        DeviceInfo? returned = store.RegisterNative("known-native-certificate", "expired-enrollment", "PC", 4, Report);
        Assert.Equal(device.Id, returned.Id);
        Assert.Equal("Office PC", returned.Name);
        Assert.Equal("office", returned.GroupId);
        Assert.Equal("important", returned.Tags);
        Assert.Equal("Keep policy when returning", returned.Notes);
        Assert.True(store.RequiresDesktopConsent(returned.Id));
        Assert.Single(store.Devices());
    }
    [Fact]
    public void RemovedManagedAgentStaysHiddenUntilItReconnects()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        (string Id, string Token) token = store.CreateToken("Enrollment", "default", 1, 1);
        EnrollmentResponse? agent = store.Enroll(new(token.Token, "Managed agent", "Windows"));
        store.RemoveDevice(agent.DeviceId);
        store.UpdateInventory(agent.DeviceId, Report);
        store.TouchDevice(agent.DeviceId);
        Assert.Null(store.FindDevice(agent.DeviceId));
        Assert.True(store.ValidAgent(agent.DeviceId, agent.Secret));
        store.RestoreConnectedDevice(agent.DeviceId);
        Assert.NotNull(store.FindDevice(agent.DeviceId));
        Assert.Single(store.Devices());
    }
    [Fact]
    public void RemovalDoesNotUndoAnExistingAccessBlock()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        (string Id, string Token) token = store.CreateToken("Enrollment", "default", 1, 1);
        DeviceInfo? device = store.RegisterNative("blocked-certificate", Store.NativeMeshKey(token.Token), "Blocked", 4, Report);
        store.SetDeviceBlocked(device.Id, true);
        store.RemoveDevice(device.Id);
        store.RestoreConnectedDevice(device.Id);
        Assert.Throws<UnauthorizedAccessException>(() => store.RegisterNative("blocked-certificate", "old-key", "Blocked", 4, Report));
        Assert.Null(store.FindDevice(device.Id));
        Assert.True(store.FindDevice(device.Id, includeRemoved: true)!.Disabled);
    }
    [Fact]
    public void UpgradingAddsNewAdministratorCapabilitiesWithoutChangingUserAccounts()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        UserInfo? user = store.AddUser("My-own-admin", "Test-only-password-6238", "Admin");
        using (SqliteConnection? connection = new SqliteConnection("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db")))
        {
            connection.Open();
            using SqliteCommand? command = connection.CreateCommand();
            command.CommandText = "UPDATE roles SET permissions='[]' WHERE id='Admin'";
            command.ExecuteNonQuery();
        }
        IServiceProvider? services = server.Services;
        Store? upgraded = new Store(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IWebHostEnvironment>(), services.GetRequiredService<IDataProtectionProvider>());
        Assert.True(upgraded.HasPermission(upgraded.FindUser(user.Id), Permissions.RemoveDevices));
        Assert.Equal(user, upgraded.FindUser(user.Id));
        Assert.NotNull(upgraded.Authenticate(user.Name, "Test-only-password-6238"));
        UserInfo? viewer = upgraded.AddUser("Read only", "Test-only-password-6238", "Viewer");
        Assert.False(upgraded.HasPermission(viewer, Permissions.RemoveDevices));
    }
    [Fact]
    public void ExistingDeviceSchemaIsMigratedWithoutLosingInventoryOrAgentCredentials()
    {
        using TestServer? server = new TestServer();
        Store? store = server.Store;
        (string Id, string Token) token = store.CreateToken("Legacy enrollment", "default", 1, 1);
        EnrollmentResponse? agent = store.Enroll(new(token.Token, "Legacy device", "Windows"));
        store.UpdateInventory(agent.DeviceId, Report);
        using (SqliteConnection? connection = new SqliteConnection("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db")))
        {
            connection.Open();
            using SqliteCommand? command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE devices DROP COLUMN removed";
            command.ExecuteNonQuery();
        }
        IServiceProvider? services = server.Services;
        Store? migrated = new Store(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IWebHostEnvironment>(), services.GetRequiredService<IDataProtectionProvider>());
        Assert.Single(migrated.Devices());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(Report, Wire.Json), System.Text.Json.JsonSerializer.Serialize(migrated.FindDevice(agent.DeviceId)!.Inventory, Wire.Json));
        Assert.True(migrated.ValidAgent(agent.DeviceId, agent.Secret));
        migrated.RemoveDevice(agent.DeviceId);
        Assert.Empty(migrated.Devices());
    }
}
