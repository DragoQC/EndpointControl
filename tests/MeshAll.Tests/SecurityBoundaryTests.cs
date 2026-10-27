using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.WebSockets;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Xunit;

namespace MeshAll.Tests;

public sealed class SecurityBoundaryTests
{
    private const string Password = "Test-account-password-8671";

    [Fact]
    public void AnEnrolledControllerIsNotAPathAroundApplicationPermissions()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("controller-admin", Password, "Admin");
        UserInfo user = store.AddUser("device-operator", Password, "Operator");
        (string Id, string Token) token = store.CreateToken("Controller test", "default", 1, 1);
        EnrollmentResponse device = store.Enroll(new(token.Token, "Controller", "Linux"));
        store.UpdateInventory(device.DeviceId, new Inventory(Environment.MachineName, "Linux", "Linux", "x64", "root", "127.0.0.1", "", 4, 100, 30, 0, 0, "test", [], [], ConnectionAddress: "127.0.0.1"));
        Assert.False(store.CanAccessDevice(user, device.DeviceId));
        Assert.True(store.CanAccessDevice(admin, device.DeviceId));
        Assert.Throws<UnauthorizedAccessException>(() => store.RequireDeviceAccess(user, device.DeviceId));
        store.UpdateInventory(device.DeviceId, new Inventory("changed-hostname", "Linux", "Linux", "x64", "root", "127.0.0.1", "", 4, 100, 30, 0, 0, "test", [], []));
        Assert.False(store.CanAccessDevice(user, device.DeviceId));
        RoleInfo role = store.FindRole("Operator")!;
        store.ManageRole(admin, role.Id, role.Name, role.Permissions.Append(Permissions.Controller));
        Assert.True(store.CanAccessDevice(store.FindUser(user.Id), device.DeviceId));
    }

    private static async Task<HttpResponseMessage> Form(HttpClient client, string page, string endpoint, Dictionary<string, string> fields)
    {
        string html = await client.GetStringAsync(page);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        return await client.PostAsync(endpoint, new FormUrlEncodedContent(fields));
    }

    private static async Task<string> Login(HttpClient client, string username)
    {
        using HttpResponseMessage response = await Form(client, "/login", "/auth/login", new()
        {
            ["username"] = username,
            ["password"] = Password
        });
        return response.Headers.GetValues("Set-Cookie").Last(cookie => cookie.StartsWith("MeshAll.Session=", StringComparison.Ordinal)).Split(';')[0];
    }

    [Fact]
    public async Task ARealCookieCopiedBeforeLogoutCannotBeReplayedAfterLogout()
    {
        using TestServer server = new();
        server.Store.AddUser("cookie-user", Password, "Admin");
        using HttpClient client = server.Browser();
        string cookie = await Login(client, "cookie-user");
        using HttpClient replay = server.Browser();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync("/api/audit/export")).StatusCode);
        using HttpResponseMessage logout = await Form(client, "/account/security", "/auth/logout", []);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/audit/export")).StatusCode);
    }

    [Fact]
    public async Task RevocationCancelsRemoteSetupBeforeCredentialsOrATargetAreOpened()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("owner", Password, "Admin");
        UserInfo user = store.AddUser("operator", Password, "Operator");
        (string Id, string Token) key = store.CreateToken("Pending connection", "default", 1, 1);
        EnrollmentResponse device = store.Enroll(new(key.Token, "Remote target", "Windows"));
        store.UpdateInventory(device.DeviceId, new("Remote target", "Windows", "Windows", "x64", "", "192.0.2.9", "", 2, 100, 30, 0, 0, "test", [], []));
        using HttpClient client = server.Browser();
        string cookie = await Login(client, user.Name);
        Microsoft.AspNetCore.TestHost.WebSocketClient sockets = server.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request => { request.Headers["Origin"] = "https://localhost"; request.Headers["Cookie"] = cookie; };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using WebSocket socket = await sockets.ConnectAsync(new Uri($"wss://localhost/api/network/{device.DeviceId}/rdp?direct=1"), timeout.Token);
        store.SetUserDisabled(user.Id, true, admin.Id);
        byte[] buffer = new byte[256];
        WebSocketReceiveResult closed = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
    }

    [Fact]
    public async Task CrossOriginInteractiveSocketsAreRejectedBeforeACircuitCanBeAttached()
    {
        using TestServer server = new();
        server.Store.AddUser("socket-user", Password, "Admin");
        using HttpClient client = server.Browser();
        string cookie = await Login(client, "socket-user");
        Microsoft.AspNetCore.TestHost.WebSocketClient sockets = server.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request => { request.Headers["Origin"] = "https://untrusted.example"; request.Headers["Cookie"] = cookie; };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(new Uri("wss://localhost/_blazor"), CancellationToken.None));
        Assert.Contains("403", error.Message);
    }

    [Theory]
    [InlineData(Permissions.Sessions, "sessions", "audit")]
    [InlineData(Permissions.Audit, "audit", "sessions")]
    public async Task CombinedLogsRespectEachSeparateCapability(string permission, string allowed, string denied)
    {
        using TestServer server = new();
        Store store = server.Store;
        store.SaveRole(null, "Restricted logs", [permission]);
        RoleInfo role = store.Roles().Single(item => item.Name == "Restricted logs");
        store.AddUser("log-reader", Password, role.Id);
        using HttpClient client = server.Browser();
        await Login(client, "log-reader");
        string html = await client.GetStringAsync("/logs?tab=" + denied);
        Assert.Contains(allowed == "sessions" ? "Search sessions" : "Search audit log", html);
        Assert.DoesNotContain(denied == "sessions" ? "Search sessions" : "Search audit log", html);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/audit/export")).StatusCode);
    }

    [Fact]
    public void HostVerificationRequiresValidatedPublicIdentityMaterial()
    {
        string fingerprint = string.Concat(Enumerable.Repeat("ab", 32));
        Assert.Equal(fingerprint, RemoteIdentityResolver.NormalizeRdp("SHA256 Fingerprint=" + string.Join(':', Enumerable.Repeat("AB", 32))));
        Assert.Throws<IOException>(() => RemoteIdentityResolver.NormalizeRdp("unknown certificate"));
        Assert.Throws<IOException>(() => RemoteIdentityResolver.NormalizeSsh("no host key"));
        string hostKey = "ssh-ed25519 " + Convert.ToBase64String(new byte[48]);
        Assert.Equal(hostKey, RemoteIdentityResolver.NormalizeSsh(hostKey + " host comment"));
    }

    [Fact]
    public void MultipleSshHostKeysFollowTheActualCodecPreferenceOrder()
    {
        string keys = string.Join('\n', SshHostKeyPreferences.Algorithms.Reverse().Select(algorithm => algorithm + " " + Convert.ToBase64String(new byte[48])));
        string selected = RemoteIdentityResolver.NormalizeSsh(keys);
        Assert.StartsWith(SshHostKeyPreferences.Algorithms[0] + " ", selected);
    }

    private static ClaimsPrincipal Principal(Store store, UserInfo user, string? sid = null, bool legacy = false)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, user.Id), new("version", user.Version.ToString()),
            new("amr", "pwd"), new("sid", sid ?? store.CreateWebSession(user))];
        if (!legacy)
        {
            claims.Add(new(Store.SessionSchemaClaim, "2"));
        }

        return new(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void LogoutRevokesOnlyTheMatchingSessionAndCannotBeReplayed()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo user = store.AddUser("session-user", Password, "Admin");
        ClaimsPrincipal first = Principal(store, user);
        ClaimsPrincipal second = Principal(store, user);
        Assert.NotNull(store.SessionUser(first));
        store.RevokeWebSession(first);
        Assert.Null(store.SessionUser(first));
        Assert.Null(store.ValidateCookieSession(first, DateTimeOffset.UtcNow));
        Assert.NotNull(store.SessionUser(second));
        Assert.Null(store.SessionUser(Principal(store, user, Store.Secret())));
    }

    [Fact]
    public void OnlyPreUpgradeSignedSessionsMayMigrateAndRevocationSurvivesMigration()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo user = store.AddUser("legacy-user", Password, "Admin");
        ClaimsPrincipal legacy = Principal(store, user, Store.Secret(), legacy: true);
        Assert.Null(store.SessionUser(legacy));
        Assert.NotNull(store.ValidateCookieSession(legacy, DateTimeOffset.UtcNow.AddHours(-1)));
        store.RevokeWebSession(legacy);
        Assert.Null(store.ValidateCookieSession(legacy, DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Null(store.ValidateCookieSession(Principal(store, user, Store.Secret(), legacy: true), DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public void DelegatedManagersCannotEscalateOrTakeOverMorePrivilegedAccounts()
    {
        using TestServer server = new();
        Store store = server.Store;
        UserInfo admin = store.AddUser("admin", Password, "Admin");
        store.SaveRole(null, "Account support", [Permissions.Inventory, Permissions.Users, Permissions.Roles]);
        RoleInfo role = store.Roles().Single(item => item.Name == "Account support");
        UserInfo support = store.AddUser("support", Password, role.Id);
        Assert.Throws<UnauthorizedAccessException>(() => store.ManageAddUser(support, "escalation", Password, "Admin"));
        Assert.Throws<UnauthorizedAccessException>(() => store.ManagePassword(support, admin.Id, Password));
        Assert.Throws<UnauthorizedAccessException>(() => store.ManageUserRole(support, support.Id, "Admin"));
        Assert.Throws<UnauthorizedAccessException>(() => store.ManageUserDisabled(support, admin.Id, true));
        Assert.Throws<UnauthorizedAccessException>(() => store.ManageRole(support, role.Id, role.Name, [Permissions.Users, Permissions.Commands]));
        store.ManageRole(support, null, "Limited reader", [Permissions.Inventory]);
        RoleInfo reader = store.Roles().Single(item => item.Name == "Limited reader");
        UserInfo managed = store.ManageAddUser(support, "managed", Password, reader.Id);
        store.ManagePassword(support, managed.Id, "Changed-test-password-8671");
        Assert.NotNull(store.Authenticate("managed", "Changed-test-password-8671"));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("192.168.120.109", true)]
    [InlineData("fd00::10", true)]
    public void DirectConnectionsRejectLocalAndMetadataTargets(string address, bool allowed) =>
        Assert.Equal(allowed, RequestSecurity.AllowedDirectAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"password\":12}")]
    [InlineData("{\"width\":\"huge\"}")]
    public void MalformedRemoteSetupIsRejectedBeforeOpeningATarget(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws<IOException>(() => NetworkGateway.ValidateOptions(document.RootElement));
    }

    [Fact]
    public async Task SecurityHeadersAndSensitiveRoutesFailClosedForAnonymousRequests()
    {
        using TestServer server = new();
        server.Store.AddUser("admin", Password, "Admin");
        using HttpClient client = server.Browser();
        using HttpResponseMessage login = await client.GetAsync("/login");
        string csp = string.Join("; ", login.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("form-action 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("no-referrer", Assert.Single(login.Headers.GetValues("Referrer-Policy")));
        foreach (string path in new[] { "/api/audit/export", "/api/devices/unknown/file?path=/etc/passwd", "/api/remote/unknown/desktop/access", "/api/agents/native-msi?token=invalid&platform=win-x64", "/api/agents/windows-msi" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        }

        foreach (string path in new[] { "/logs", "/settings", "/users", "/account/security" })
        {
            Assert.Equal("/login", new Uri(new Uri("https://localhost"), (await client.GetAsync(path)).Headers.Location!).AbsolutePath);
        }
    }

    [Fact]
    public async Task MsiDownloadsRequireEnrollmentPermissionAndAValidUnrevokedKey()
    {
        using TestServer server = new();
        server.Store.AddUser("msi-viewer", Password, "Viewer");
        server.Store.AddUser("msi-admin", Password, "Admin");
        (string Id, string Token) token = server.Store.CreateToken("Revoked MSI", "default", 1, 24);
        server.Store.RevokeToken(token.Id);
        using HttpClient viewer = server.Browser();
        await Login(viewer, "msi-viewer");
        string path = $"/api/agents/native-msi?token={token.Token}&platform=win-x64";
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(path)).StatusCode);
        using HttpClient admin = server.Browser();
        await Login(admin, "msi-admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path)).StatusCode);
    }
}
