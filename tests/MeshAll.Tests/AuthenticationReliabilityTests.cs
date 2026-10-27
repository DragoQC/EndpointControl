using MeshAll.Server.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshAll.Tests;

public sealed class AuthenticationReliabilityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("missing-delimiter")]
    [InlineData(":")]
    [InlineData("YQ==:Yg==")]
    [InlineData("YQ==:Yg==:extra")]
    public void DamagedStoredPasswordHashesFailAuthenticationWithoutThrowing(string hash)
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo user = store.AddUser("damaged-hash", "Test-password-672984", "Admin");
        using SqliteConnection connection = new("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db"));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET password=$hash WHERE id=$id";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$id", user.Id);
        command.ExecuteNonQuery();
        Assert.Null(store.Authenticate(user.Name, "Test-password-672984"));
    }
}
