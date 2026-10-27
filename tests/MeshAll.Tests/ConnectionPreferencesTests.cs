using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshAll.Server.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshAll.Tests;

public sealed class ConnectionPreferencesTests
{
    private const string AccountPassword = "Test-account-password-7294";
    private const string RemotePassword = "Test-remote-password-9372";

    [Fact]
    public void CredentialsAreProtectedPerUserAndCanBeUpdatedOrCleared()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo first = store.AddUser("first", AccountPassword, "Operator");
        UserInfo second = store.AddUser("second", AccountPassword, "Operator");
        Assert.Null(store.ReadRdpCredentials(first));

        store.SaveRdpCredentials(first, " LAB ", " remote-user ", RemotePassword);
        Assert.Equal(new RdpDefaultsSummary("LAB", "remote-user", true), store.RdpDefaults(first));
        Assert.Equal(RemotePassword, store.ReadRdpCredentials(first)!.Password);
        Assert.Null(store.RdpDefaults(second));
        Assert.Null(store.ReadRdpCredentials(second));

        using SqliteConnection connection = new("Data Source=" + Path.Combine(store.DataDirectory, "meshall.db"));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT password FROM user_rdp_defaults WHERE userId=$id";
        command.Parameters.AddWithValue("$id", first.Id);
        string ciphertext = (string)command.ExecuteScalar()!;
        Assert.NotEmpty(ciphertext);
        Assert.DoesNotContain(RemotePassword, ciphertext);
        Assert.DoesNotContain(RemotePassword, store.ReadRdpCredentials(first)!.ToString());
        Assert.DoesNotContain(RemotePassword, JsonSerializer.Serialize(store.ReadRdpCredentials(first)));
        Assert.DoesNotContain(RemotePassword, JsonSerializer.Serialize(store.AuditPage(new PageRequest(1, 50))));

        // Copying ciphertext to another user's row must not make it decryptable.
        command.CommandText = "INSERT INTO user_rdp_defaults SELECT $second,domain,username,password FROM user_rdp_defaults WHERE userId=$id";
        command.Parameters.AddWithValue("$second", second.Id);
        command.ExecuteNonQuery();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => store.ReadRdpCredentials(second));
        Assert.DoesNotContain(RemotePassword, error.ToString());

        store.SaveRdpCredentials(second, "", "other-user", "updated-remote-password");
        store.ClearRdpCredentials(first);
        Assert.Null(store.ReadRdpCredentials(first));
        Assert.Equal("updated-remote-password", store.ReadRdpCredentials(second)!.Password);
        store.SaveRdpCredentials(second, "", "passwordless-user", "");
        Assert.False(store.RdpDefaults(second)!.HasPassword);
        Assert.Equal("", store.ReadRdpCredentials(second)!.Password);
    }

    [Fact]
    public void RevokedOrStaleAccountsCannotReadOrChangeSavedCredentials()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("admin", AccountPassword, "Admin");
        UserInfo user = store.AddUser("operator", AccountPassword, "Operator");
        UserInfo viewer = store.AddUser("viewer", AccountPassword, "Viewer");
        store.SaveRdpCredentials(user, "", "remote-user", RemotePassword);
        Assert.Throws<UnauthorizedAccessException>(() => store.SaveRdpCredentials(viewer, "", "user", RemotePassword));
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadRdpCredentials(viewer));
        store.ChangePassword(user.Id, "Changed-account-password-7294");
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadRdpCredentials(user));
        Assert.Throws<UnauthorizedAccessException>(() => store.ClearRdpCredentials(user));
        UserInfo current = store.FindUser(user.Id)!;
        Assert.Equal(RemotePassword, store.ReadRdpCredentials(current)!.Password);
        store.SetUserDisabled(user.Id, true, admin.Id);
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadRdpCredentials(current));

        UserInfo restricted = store.AddUser("restricted", AccountPassword, "Operator");
        store.SaveRdpCredentials(restricted, "", "remote-user", RemotePassword);
        store.ChangeUserRole(restricted.Id, "Viewer");
        UserInfo revoked = store.FindUser(restricted.Id)!;
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadRdpCredentials(revoked));
        Assert.Throws<UnauthorizedAccessException>(() => store.SaveRdpCredentials(revoked, "", "user", RemotePassword));
        Assert.Throws<UnauthorizedAccessException>(() => store.ClearRdpCredentials(revoked));
    }

    [Fact]
    public async Task PrerenderAndAuditExportsDoNotContainSavedPasswords()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("admin", AccountPassword, "Admin");
        store.SaveRdpCredentials(admin, "LAB", "remote-user", RemotePassword);
        using HttpClient browser = server.Browser();
        string login = await browser.GetStringAsync("/login");
        string token = WebUtility.HtmlDecode(Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        await browser.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = admin.Name,
            ["password"] = AccountPassword,
            ["__RequestVerificationToken"] = token
        }));

        foreach (string path in new[] { "/settings", "/account/security", "/api/audit/export" })
        {
            string response = await browser.GetStringAsync(path);
            Assert.DoesNotContain(RemotePassword, response);
            Assert.DoesNotContain("My workspace", response);
            Assert.DoesNotContain("Workspace name", response);
        }

        store.Setting("requireTwoFactor", "true", true);
        using HttpClient enrollment = server.Browser();
        login = await enrollment.GetStringAsync("/login");
        token = WebUtility.HtmlDecode(Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        await enrollment.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = admin.Name,
            ["password"] = AccountPassword,
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal("/account/security", (await enrollment.GetAsync("/account/connections")).Headers.Location?.OriginalString);
    }
}
