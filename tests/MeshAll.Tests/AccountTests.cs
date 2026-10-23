using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using MeshAll.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshAll.Tests;

public sealed class TestServer:WebApplicationFactory<Program>
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"meshall-tests-"+Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");builder.UseSetting("MeshAll:DataDirectory",directory);
        builder.ConfigureAppConfiguration((_,configuration)=>configuration.AddInMemoryCollection(new Dictionary<string,string?>{["MeshAll:DataDirectory"]=directory}));
    }
    public Store Store=>Services.GetRequiredService<Store>();
    public HttpClient Browser()=>CreateClient(new WebApplicationFactoryClientOptions{AllowAutoRedirect=false,BaseAddress=new Uri("https://localhost")});
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if(Directory.Exists(directory))Directory.Delete(directory,true);
    }
}

public class AccountTests
{
    private const string Password="Test-only-password-6238";
    private static async Task<HttpResponseMessage> Form(HttpClient client,string page,string endpoint,params(string Key,string Value)[] fields)
    {
        var html=await client.GetStringAsync(page);
        var token=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success,"No antiforgery token in form");
        return await client.PostAsync(endpoint,new FormUrlEncodedContent(fields.Append(("__RequestVerificationToken",WebUtility.HtmlDecode(token.Groups[1].Value))).Select(p=>new KeyValuePair<string,string>(p.Item1,p.Item2))));
    }
    private static ClaimsPrincipal Principal(UserInfo user,bool mfa=false,bool setup=false)
    {
        var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,user.Id),new("version",user.Version.ToString()),new("amr",mfa?"mfa":"pwd")};
        if(setup)claims.Add(new("setup_required","true"));return new(new ClaimsIdentity(claims,"test"));
    }
    private static (UserInfo User,string Secret,string[] Codes) Enable(Store store)
    {
        var user=store.AddUser("factor-user",Password,"Admin");store.BeginTwoFactorSetup(user.Id,Password);var secret=store.PendingTwoFactorSecret(user.Id)!;
        var receipt=store.EnableTwoFactor(user.Id,Totp.Code(secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30));
        return(store.FindUser(user.Id)!,secret,store.TakeRecoveryCodes(user.Id,receipt)!);
    }
    [Theory]
    [InlineData(59L,"94287082")][InlineData(1111111109L,"07081804")][InlineData(1111111111L,"14050471")]
    [InlineData(1234567890L,"89005924")][InlineData(2000000000L,"69279037")][InlineData(20000000000L,"65353130")]
    public void TotpMatchesRfc6238(long seconds,string expected)
    {
        var key=Totp.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));Assert.Equal(expected,Totp.Code(key,seconds/30,8));
        Assert.Equal(seconds/30,Totp.Match(key,expected[^6..],DateTimeOffset.FromUnixTimeSeconds(seconds)));
    }
    [Fact]
    public async Task PasswordDoesNotGrantASessionBeforeSecondFactor()
    {
        using var server=new TestServer();var account=Enable(server.Store);using var client=server.Browser();
        var login=await Form(client,"/login","/auth/login",("username",account.User.Name),("password",Password));
        Assert.Equal("/login/2fa",login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/audit/export")).StatusCode);
        var invalid=await Form(client,"/login/2fa","/auth/2fa",("code","not-a-code"));Assert.Contains("failed=true",invalid.Headers.Location?.OriginalString);
        var verified=await Form(client,"/login/2fa","/auth/2fa",("code",account.Codes[0]));Assert.Equal("/",verified.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/audit/export")).StatusCode);
        Assert.Equal(9,server.Store.RecoveryCodesRemaining(account.User.Id));
    }
    [Fact]
    public async Task EnforcementRestrictsTheNextLoginToEnrollment()
    {
        using var server=new TestServer();var user=server.Store.AddUser("policy-user",Password,"Admin");using var existing=server.Browser();
        await Form(existing,"/login","/auth/login",("username",user.Name),("password",Password));
        server.Store.Setting("requireTwoFactor","true",true);
        Assert.Equal(HttpStatusCode.OK,(await existing.GetAsync("/api/audit/export")).StatusCode);
        using var fresh=server.Browser();var login=await Form(fresh,"/login","/auth/login",("username",user.Name),("password",Password));
        Assert.Equal("/account/security",login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Forbidden,(await fresh.GetAsync("/api/audit/export")).StatusCode);
        Assert.Equal("/account/security",(await fresh.GetAsync("/devices")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK,(await fresh.GetAsync("/account/security")).StatusCode);
        await Form(fresh,"/account/security","/auth/2fa/setup",("password",Password));
        var key=server.Store.PendingTwoFactorSecret(user.Id)!;
        var confirmed=await Form(fresh,"/account/security","/auth/2fa/confirm",("code",Totp.Code(key,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30)));
        Assert.StartsWith("/account/security?codes=",confirmed.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK,(await fresh.GetAsync("/api/audit/export")).StatusCode);
        var codesPage=await fresh.GetStringAsync(confirmed.Headers.Location);Assert.Contains("Recovery codes",codesPage);
        var codesAgain=await fresh.GetStringAsync(confirmed.Headers.Location);Assert.DoesNotContain("aria-label=\"Recovery codes\"",codesAgain);
    }
    [Fact]
    public void TotpRecoveryAndChallengesAreSingleUseAndBoundToAccountVersion()
    {
        using var server=new TestServer();var store=server.Store;var account=Enable(store);
        var setupCode=Totp.Code(account.Secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30);
        Assert.Null(store.CompleteLoginChallenge(store.CreateLoginChallenge(account.User),setupCode));
        var challenge=store.CreateLoginChallenge(account.User);Assert.NotNull(store.CompleteLoginChallenge(challenge,account.Codes[0]));
        Assert.Null(store.CompleteLoginChallenge(challenge,account.Codes[1]));
        Assert.Null(store.CompleteLoginChallenge(store.CreateLoginChallenge(account.User),account.Codes[0]));
        var locked=store.CreateLoginChallenge(account.User);for(var i=0;i<5;i++)Assert.Null(store.CompleteLoginChallenge(locked,"invalid"));
        Assert.Null(store.CompleteLoginChallenge(locked,account.Codes[1]));
        var stale=store.CreateLoginChallenge(account.User);store.ChangePassword(account.User.Id,"Changed-test-password-6238");
        Assert.Null(store.CompleteLoginChallenge(stale,account.Codes[1]));
        Assert.Null(store.SessionUser(Principal(account.User,true)));
        var current=store.FindUser(account.User.Id)!;Assert.Null(store.SessionUser(Principal(current)));Assert.NotNull(store.SessionUser(Principal(current,true)));
    }
    [Fact]
    public void SetupNeedsTheCurrentPasswordAndTheSecretIsProtected()
    {
        using var server=new TestServer();var store=server.Store;var user=store.AddUser("setup-user",Password,"Viewer");
        Assert.Throws<InvalidOperationException>(()=>store.BeginTwoFactorSetup(user.Id,"wrong-password"));
        store.BeginTwoFactorSetup(user.Id,Password);var secret=store.PendingTwoFactorSecret(user.Id)!;
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(store.DataDirectory,"meshall.db"));connection.Open();
        using var query=connection.CreateCommand();query.CommandText="SELECT secret FROM two_factor_setup";
        Assert.DoesNotContain(secret,(string)query.ExecuteScalar()!);
        Assert.Throws<InvalidOperationException>(()=>store.EnableTwoFactor(user.Id,"invalid"));Assert.False(store.FindUser(user.Id)!.TwoFactorEnabled);
    }
    [Fact]
    public void RecoveryRegenerationRevokesPreviousCodesAndPolicyPreventsDisabling()
    {
        using var server=new TestServer();var store=server.Store;var account=Enable(store);
        var receipt=store.RegenerateRecoveryCodes(account.User.Id,Password,account.Codes[0]);var replacement=store.TakeRecoveryCodes(account.User.Id,receipt)!;
        Assert.Null(store.TakeRecoveryCodes(account.User.Id,receipt));Assert.Equal(10,replacement.Length);
        Assert.Null(store.CompleteLoginChallenge(store.CreateLoginChallenge(account.User),account.Codes[1]));
        store.Setting("requireTwoFactor","true",true);
        Assert.Throws<InvalidOperationException>(()=>store.DisableTwoFactor(account.User.Id,Password,replacement[0]));
        store.Setting("requireTwoFactor","false",true);store.DisableTwoFactor(account.User.Id,Password,replacement[0]);
        Assert.False(store.FindUser(account.User.Id)!.TwoFactorEnabled);
    }
    [Fact]
    public async Task MissingCsrfAndReadOnlyRolesCannotReachPrivilegedActions()
    {
        using var server=new TestServer();server.Store.AddUser("viewer-user",Password,"Viewer");using var client=server.Browser();
        var missing=await client.PostAsync("/auth/login",new FormUrlEncodedContent(new Dictionary<string,string>{{"username","viewer-user"},{"password",Password}}));
        Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);
        await Form(client,"/login","/auth/login",("username","viewer-user"),("password",Password));
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/audit/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/remote/unknown/desktop")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/network/unknown/ssh")).StatusCode);
    }
    [Fact]
    public void CustomRolesRevokeOldSessionsAndKeepAnAdministrator()
    {
        using var server=new TestServer();var store=server.Store;var admin=store.AddUser("admin-user",Password,"Admin");
        store.SaveRole(null,"Help desk",[Permissions.Desktop]);var role=store.Roles().Single(r=>r.Name=="Help desk");var user=store.AddUser("helper-user",Password,role.Id);
        Assert.True(store.HasPermission(user,Permissions.Inventory));Assert.True(store.HasPermission(user,Permissions.Desktop));
        Assert.False(store.HasPermission(user,Permissions.Control));Assert.False(store.HasPermission(user,Permissions.Commands));
        store.SaveRole(role.Id,role.Name,[Permissions.Inventory]);Assert.Null(store.SessionUser(Principal(user)));
        Assert.False(store.HasPermission(store.FindUser(user.Id),Permissions.Desktop));
        Assert.Throws<InvalidOperationException>(()=>store.ChangeUserRole(admin.Id,role.Id));
        Assert.Throws<InvalidOperationException>(()=>store.DeleteRole(role.Id));
    }
}
