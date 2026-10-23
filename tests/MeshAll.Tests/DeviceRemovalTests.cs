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
    private static Inventory Report=>new("returning-device","Windows","Windows 11","x64","test-user","192.0.2.10","00:11:22:33:44:55",4,8000000000,2000000000,5,100,"test",[],[]);
    [Fact]
    public void RemovedNativeDeviceReturnsWithItsIdentityAndPolicyAfterEnrollmentKeyExpires()
    {
        using var server=new TestServer();var store=server.Store;
        store.SaveGroup("office","Office","Test policy",73);var token=store.CreateToken("First enrollment","office",1,1);
        var device=store.RegisterNative("known-native-certificate",Store.NativeMeshKey(token.Token),"PC",4,Report);
        store.SaveDevice(device.Id,"Office PC","office","important","Keep policy when returning");
        store.RemoveDevice(device.Id);store.TouchDevice(device.Id);store.UpdateInventory(device.Id,Report with{Uptime=200});
        Assert.Empty(store.Devices());Assert.Null(store.FindDevice(device.Id));Assert.True(store.IsNative(device.Id));
        store.RevokeToken(token.Id);
        var returned=store.RegisterNative("known-native-certificate","expired-enrollment","PC",4,Report);
        Assert.Equal(device.Id,returned.Id);Assert.Equal("Office PC",returned.Name);Assert.Equal("office",returned.GroupId);
        Assert.Equal("important",returned.Tags);Assert.Equal("Keep policy when returning",returned.Notes);
        Assert.True(store.RequiresDesktopConsent(returned.Id));Assert.Single(store.Devices());
    }
    [Fact]
    public void RemovedManagedAgentStaysHiddenUntilItReconnects()
    {
        using var server=new TestServer();var store=server.Store;var token=store.CreateToken("Enrollment","default",1,1);
        var agent=store.Enroll(new(token.Token,"Managed agent","Windows"));
        store.RemoveDevice(agent.DeviceId);store.UpdateInventory(agent.DeviceId,Report);store.TouchDevice(agent.DeviceId);
        Assert.Null(store.FindDevice(agent.DeviceId));Assert.True(store.ValidAgent(agent.DeviceId,agent.Secret));
        store.RestoreConnectedDevice(agent.DeviceId);Assert.NotNull(store.FindDevice(agent.DeviceId));Assert.Single(store.Devices());
    }
    [Fact]
    public void RemovalDoesNotUndoAnExistingAccessBlock()
    {
        using var server=new TestServer();var store=server.Store;var token=store.CreateToken("Enrollment","default",1,1);
        var device=store.RegisterNative("blocked-certificate",Store.NativeMeshKey(token.Token),"Blocked",4,Report);
        store.SetDeviceBlocked(device.Id,true);store.RemoveDevice(device.Id);store.RestoreConnectedDevice(device.Id);
        Assert.Throws<UnauthorizedAccessException>(()=>store.RegisterNative("blocked-certificate","old-key","Blocked",4,Report));
        Assert.Null(store.FindDevice(device.Id));Assert.True(store.FindDevice(device.Id,includeRemoved:true)!.Disabled);
    }
    [Fact]
    public void UpgradingAddsNewAdministratorCapabilitiesWithoutChangingUserAccounts()
    {
        using var server=new TestServer();var store=server.Store;var user=store.AddUser("My-own-admin","Test-only-password-6238","Admin");
        using(var connection=new SqliteConnection("Data Source="+Path.Combine(store.DataDirectory,"meshall.db")))
        {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE roles SET permissions='[]' WHERE id='Admin'";command.ExecuteNonQuery();
        }
        var services=server.Services;var upgraded=new Store(services.GetRequiredService<IConfiguration>(),services.GetRequiredService<IWebHostEnvironment>(),services.GetRequiredService<IDataProtectionProvider>());
        Assert.True(upgraded.HasPermission(upgraded.FindUser(user.Id),Permissions.RemoveDevices));
        Assert.Equal(user,upgraded.FindUser(user.Id));Assert.NotNull(upgraded.Authenticate(user.Name,"Test-only-password-6238"));
        var viewer=upgraded.AddUser("Read only","Test-only-password-6238","Viewer");Assert.False(upgraded.HasPermission(viewer,Permissions.RemoveDevices));
    }
    [Fact]
    public void ExistingDeviceSchemaIsMigratedWithoutLosingInventoryOrAgentCredentials()
    {
        using var server=new TestServer();var store=server.Store;var token=store.CreateToken("Legacy enrollment","default",1,1);
        var agent=store.Enroll(new(token.Token,"Legacy device","Windows"));store.UpdateInventory(agent.DeviceId,Report);
        using(var connection=new SqliteConnection("Data Source="+Path.Combine(store.DataDirectory,"meshall.db")))
        {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="ALTER TABLE devices DROP COLUMN removed";command.ExecuteNonQuery();
        }
        var services=server.Services;var migrated=new Store(services.GetRequiredService<IConfiguration>(),services.GetRequiredService<IWebHostEnvironment>(),services.GetRequiredService<IDataProtectionProvider>());
        Assert.Single(migrated.Devices());Assert.Equal(System.Text.Json.JsonSerializer.Serialize(Report,Wire.Json),System.Text.Json.JsonSerializer.Serialize(migrated.FindDevice(agent.DeviceId)!.Inventory,Wire.Json));
        Assert.True(migrated.ValidAgent(agent.DeviceId,agent.Secret));migrated.RemoveDevice(agent.DeviceId);Assert.Empty(migrated.Devices());
    }
}
