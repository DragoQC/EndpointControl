using System.Text.Json;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshAll.Tests;

public sealed class EnrollmentKeyTests
{
    private const string Password = "Enrollment-fixture-password-4962";

    private static void Execute(Store store, string sql, params object[] values)
    {
        using SqliteConnection connection = new("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db"));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        for (int index = 0; index < values.Length; index++)
        {
            command.Parameters.AddWithValue("$" + index, values[index]);
        }
        command.ExecuteNonQuery();
    }

    private static Inventory Inventory(string name) => new(name, "Windows", "Windows", "x64", "", "192.0.2.9", "", 1, 100, 20, 0, 0, "test", [], []);

    [Fact]
    public void AKeyCanBeReopenedWithoutPuttingItsPlaintextInSummariesOrLogs()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("enrollment-admin", Password, "Admin");
        (string Id, string Token) key = store.CreateToken("400 computer rollout", "default", 400, 24);
        EnrollmentKeyDetails details = store.ReadEnrollmentKey(admin, key.Id);
        Assert.Equal(key.Token, details.Key);
        Assert.Equal(Store.NativeMeshKey(key.Token), details.NativeKey);
        Assert.Equal(400, details.Information.Remaining);
        Assert.False(details.NativeOnly);
        Assert.DoesNotContain(key.Token, JsonSerializer.Serialize(store.Tokens()));
        Assert.DoesNotContain(key.Token, JsonSerializer.Serialize(details));
        Assert.DoesNotContain(details.NativeKey!, JsonSerializer.Serialize(details));
        Assert.DoesNotContain(key.Token, details.ToString());
        using SqliteConnection connection = new("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db"));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT secret FROM enrollment_secrets WHERE tokenId=$id";
        command.Parameters.AddWithValue("$id", key.Id);
        string protectedValue = (string)command.ExecuteScalar()!;
        Assert.NotEqual(key.Token, protectedValue);
        Assert.DoesNotContain(key.Token, protectedValue);
        Assert.Equal(key.Token, store.ReadEnrollmentKey(admin, key.Id).Key);
    }

    [Fact]
    public void KeyRetrievalRequiresCurrentEnrollmentPermissionAndBindsCiphertextToItsId()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("secret-owner", Password, "Admin");
        UserInfo viewer = store.AddUser("secret-viewer", Password, "Viewer");
        (string Id, string Token) first = store.CreateToken("First", "default", 400, 24);
        (string Id, string Token) second = store.CreateToken("Second", "default", 400, 24);
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadEnrollmentKey(viewer, first.Id));
        Execute(store, "UPDATE enrollment_secrets SET secret=(SELECT secret FROM enrollment_secrets WHERE tokenId=$0) WHERE tokenId=$1", first.Id, second.Id);
        Assert.Throws<InvalidOperationException>(() => store.ReadEnrollmentKey(admin, second.Id));
        store.ChangePassword(admin.Id, "Updated-enrollment-password-4962");
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadEnrollmentKey(admin, first.Id));
    }

    [Fact]
    public void EveryPlatformUsesTheSelectedKeyAndGroupWithoutConsumingUses()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("download-admin", Password, "Admin");
        store.SaveGroup(null, "Deployment group", "");
        GroupInfo group = store.Groups().Single(item => item.Name == "Deployment group");
        (string Id, string Token) key = store.CreateToken("Shared installer budget", group.Id, 400, 24);
        NativeAgentHub agents = server.Services.GetRequiredService<NativeAgentHub>();
        EnrollmentKeyDetails details = store.ReadEnrollmentKey(admin, key.Id);
        foreach (string platform in agents.AvailablePlatforms)
        {
            string policy = NativeAgentExecutable.EmbeddedPolicy(agents.EnrollmentExecutable(details, platform));
            Assert.Contains("MeshName=Deployment group", policy);
            Assert.Contains("MeshID=0x" + Store.NativeMeshKey(key.Token), policy);
        }
        Assert.Equal(400, store.FindEnrollmentToken(key.Id)!.Remaining);
    }

    [Fact]
    public void FourHundredMixedNewRegistrationsShareOneBudgetAndReconnectsUseNone()
    {
        using TestServer server = new();
        Store store = server.Store;
        (string Id, string Token) key = store.CreateToken("400 uses", "default", 400, 24);
        for (int index = 0; index < 200; index++)
        {
            store.Enroll(new(key.Token, "Managed " + index, "Linux"));
            store.RegisterNative("certificate-" + index, Store.NativeMeshKey(key.Token), "Native " + index, 2, Inventory("native-" + index));
        }
        Assert.Equal(0, store.FindEnrollmentToken(key.Id)!.Remaining);
        Assert.Throws<InvalidOperationException>(() => store.Enroll(new(key.Token, "Extra managed", "Linux")));
        Assert.Throws<UnauthorizedAccessException>(() => store.RegisterNative("extra-certificate", Store.NativeMeshKey(key.Token), "Extra native", 2, Inventory("extra")));
        store.RegisterNative("certificate-0", Store.NativeMeshKey(key.Token), "Native 0", 2, Inventory("native-0"));
        Assert.Equal(0, store.FindEnrollmentToken(key.Id)!.Remaining);
        Assert.Equal(400, store.DeviceCount);
    }

    [Fact]
    public async Task ConcurrentNativeAndManagedRegistrationsCannotSpendTheLastUseTwice()
    {
        using TestServer server = new();
        Store store = server.Store;
        (string Id, string Token) key = store.CreateToken("One remaining", "default", 1, 24);
        int successes = 0;
        Task managed = Task.Run(() =>
        {
            try
            {
                store.Enroll(new(key.Token, "Managed", "Linux"));
                Interlocked.Increment(ref successes);
            }
            catch (InvalidOperationException) { }
        });
        Task native = Task.Run(() =>
        {
            try
            {
                store.RegisterNative("concurrent-certificate", Store.NativeMeshKey(key.Token), "Native", 2, Inventory("native"));
                Interlocked.Increment(ref successes);
            }
            catch (UnauthorizedAccessException) { }
        });
        await Task.WhenAll(managed, native);
        Assert.Equal(1, successes);
        Assert.Equal(0, store.FindEnrollmentToken(key.Id)!.Remaining);
        Assert.Equal(1, store.DeviceCount);
    }

    [Fact]
    public void LegacyNativeCredentialRecoveryKeepsTheIdAndUseCountAndRejectsAnotherKey()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("legacy-owner", Password, "Admin");
        (string Id, string Token) key = store.CreateToken("Existing rollout", "default", 400, 24);
        (string Id, string Token) other = store.CreateToken("Another rollout", "default", 400, 24);
        Execute(store, "DELETE FROM enrollment_secrets WHERE tokenId=$0", key.Id);
        Assert.Null(store.ReadEnrollmentKey(admin, key.Id).Key);
        Assert.Throws<InvalidOperationException>(() => store.RecoverNativeEnrollmentKey(admin, key.Id, Store.NativeMeshKey(other.Token)));
        store.RecoverNativeEnrollmentKey(admin, key.Id, Store.NativeMeshKey(key.Token));
        EnrollmentKeyDetails recovered = store.ReadEnrollmentKey(admin, key.Id);
        Assert.True(recovered.NativeOnly);
        Assert.Equal(Store.NativeMeshKey(key.Token), recovered.NativeKey);
        Assert.Equal(key.Id, recovered.Information.Id);
        Assert.Equal(400, recovered.Information.Remaining);
    }

    [Fact]
    public void ExpiredOrExhaustedKeysRemainViewOnlyAndRevocationRemovesProtectedMaterial()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("status-owner", Password, "Admin");
        (string Id, string Token) key = store.CreateToken("Exhausted", "default", 1, 24);
        store.Enroll(new(key.Token, "Only device", "Linux"));
        EnrollmentKeyDetails details = store.ReadEnrollmentKey(admin, key.Id);
        Assert.Equal(key.Token, details.Key);
        Assert.False(details.CanDownload);
        Assert.Throws<InvalidOperationException>(() => server.Services.GetRequiredService<NativeAgentHub>().EnrollmentExecutable(details, "win-x64"));
        store.RevokeToken(key.Id);
        Assert.Throws<InvalidOperationException>(() => store.ReadEnrollmentKey(admin, key.Id));
        using SqliteConnection connection = new("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db"));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM enrollment_secrets WHERE tokenId=$id";
        command.Parameters.AddWithValue("$id", key.Id);
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void ExpirationDoesNotEraseTheKeyOrItsUnusedBudget()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("expired-owner", Password, "Admin");
        (string Id, string Token) key = store.CreateToken("Expired rollout", "default", 400, 24);
        Execute(store, "UPDATE tokens SET expires=$1 WHERE id=$0", key.Id, DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        EnrollmentKeyDetails details = store.ReadEnrollmentKey(admin, key.Id);
        Assert.Equal(key.Token, details.Key);
        Assert.Equal(400, details.Information.Remaining);
        Assert.False(details.CanDownload);
    }
}
