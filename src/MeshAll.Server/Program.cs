using System.Security.Claims;
using System.Threading.RateLimiting;
using MeshAll.Server.Components;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("MESHALL_CONNECTION_FILE") is { Length: > 0 } connectionFile)
    builder.Configuration.AddJsonFile(connectionFile, optional: false, reloadOnChange: true);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<NetworkGateway>();
builder.Services.AddSingleton<ProtocolWorker>();
builder.Services.AddSingleton<NativeCoreBuilder>();
builder.Services.AddSingleton<NativeTunnelHub>();
builder.Services.AddSingleton<NativeAgentHub>();
builder.Services.AddSingleton<AgentGateway>();
builder.Services.AddScoped<Workspace>();
builder.Services.AddSingleton<IAuthorizationHandler,PermissionHandler>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddCascadingAuthenticationState();
var dataPath = Path.GetFullPath(builder.Configuration["MeshAll:DataDirectory"] ?? "Data", builder.Environment.ContentRootPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys"))).SetApplicationName("MeshAll");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/login";
    options.Cookie.Name = "MeshAll.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401; else context.Response.Redirect(context.RedirectUri); return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        var user = context.Principal is null?null:context.HttpContext.RequestServices.GetRequiredService<Store>().SessionUser(context.Principal,allowSetup:true);
        if (user is null)
        { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(); }
    };
}).AddCookie(AccountAuthentication.ChallengeScheme,options=>
{
    options.Cookie.Name="MeshAll.SecondFactor";options.Cookie.HttpOnly=true;options.Cookie.SameSite=SameSiteMode.Strict;
    options.Cookie.SecurePolicy=CookieSecurePolicy.SameAsRequest;options.ExpireTimeSpan=TimeSpan.FromMinutes(5);options.SlidingExpiration=false;
});
builder.Services.AddAuthorization(options =>
{
    foreach(var capability in Permissions.All)options.AddPolicy(capability.Id,policy=>policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(capability.Id)));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = Wire.MaxMessageBytes);
var app = builder.Build();
var store = app.Services.GetRequiredService<Store>();
if(Array.IndexOf(args,"--bootstrap-admin")>=0)
{
    if(store.HasUsers){Console.WriteLine("{\"created\":false}");return;}
    var password=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+','-').Replace('/','_');
    var admin=store.Setup(File.ReadAllText(store.BootstrapPath),"admin",password);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{created=true,username=admin.Name,password},Wire.Json));return;
}
if(Array.IndexOf(args,"--create-enrollment") is var enrollmentIndex && enrollmentIndex>=0)
{
    var name=enrollmentIndex+1<args.Length?args[enrollmentIndex+1]:"Installation";
    var key=store.CreateToken(name,"default",1,24);Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{id=key.Id,token=key.Token},Wire.Json));return;
}
if(Array.IndexOf(args,"--native-policy") is var policyIndex && policyIndex>=0)
{
    if(policyIndex+1>=args.Length)throw new ArgumentException("Missing enrollment key.");
    Console.Write(app.Services.GetRequiredService<NativeAgentHub>().Policy(args[policyIndex+1]));return;
}
if(Array.IndexOf(args,"--reset-two-factor") is var resetIndex && resetIndex>=0)
{
    var name=resetIndex+1<args.Length?args[resetIndex+1]:throw new ArgumentException("Supply the account username.");
    var user=store.Users().SingleOrDefault(u=>string.Equals(u.Name,name,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("Account not found.");
    store.ResetTwoFactor(user.Id);store.Audit("Local administrator","Two-factor authentication reset",user.Name);
    Console.WriteLine("Two-factor authentication reset; existing account sessions revoked.");return;
}
if (!store.HasUsers) app.Logger.LogInformation("MeshAll first run: open /setup. The setup key is in {Path}", store.BootstrapPath);
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    var toolPage = context.Request.Path.StartsWithSegments("/remote");
    context.Response.Headers["X-Frame-Options"] = toolPage ? "SAMEORIGIN" : "DENY";
    context.Response.Headers["Content-Security-Policy"] = $"frame-ancestors {(toolPage ? "'self'" : "'none'")}; object-src 'none'; base-uri 'self'";
    if (!context.Request.Path.StartsWithSegments("/_framework") && !context.Request.Path.StartsWithSegments("/app.css"))
        context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch(AntiforgeryValidationException) when(!context.Response.HasStarted)
    {
        context.Response.Clear();context.Response.StatusCode=400;
        await context.Response.WriteAsync("The form expired. Reload the page and try again.");
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async(context,next)=>
{
    if(context.User.HasClaim("setup_required","true")
        && !context.Request.Path.StartsWithSegments("/account/security")&&!context.Request.Path.StartsWithSegments("/auth/2fa")
        && context.Request.Path!="/auth/logout"&&!context.Request.Path.StartsWithSegments("/_framework")
        && !context.Request.Path.StartsWithSegments("/_blazor")&&!context.Request.Path.StartsWithSegments("/vendor")
        && context.Request.Path!="/app.js"&&context.Request.Path!="/app.css")
    {
        if(context.Request.Path.StartsWithSegments("/api"))context.Response.StatusCode=403;
        else context.Response.Redirect("/account/security");return;
    }
    await next();
});
app.UseAuthorization();
app.UseAntiforgery();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, Store db) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString(); var name = form["username"].ToString();
    var user = password.Length <= 256 && name.Length <= 50 ? db.Authenticate(name, password) : null;
    if (user is null) { db.Audit("Anonymous", "Sign-in failed", "MeshAll"); return Results.LocalRedirect("/login?failed=true"); }
    await context.SignOutAsync();await context.SignOutAsync(AccountAuthentication.ChallengeScheme);
    if(user.TwoFactorEnabled)
    {
        var challenge=db.CreateLoginChallenge(user);
        await context.SignInAsync(AccountAuthentication.ChallengeScheme,new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim("challenge",challenge)},AccountAuthentication.ChallengeScheme)));
        return Results.LocalRedirect("/login/2fa");
    }
    var setup=db.RequireTwoFactor;
    await AccountAuthentication.SignIn(context,user,setupOnly:setup); db.Audit(user.Name,setup?"Signed in for required two-factor enrollment":"Signed in","MeshAll");
    return Results.LocalRedirect(setup?"/account/security":db.LandingPage(user));
}).RequireRateLimiting("auth");

app.MapPost("/auth/setup", async (HttpContext context, IAntiforgery antiforgery, Store db) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    try
    {
        var user = db.Setup(form["key"].ToString(), form["username"].ToString(), form["password"].ToString());
        await AccountAuthentication.SignIn(context, user,setupOnly:db.RequireTwoFactor); return Results.LocalRedirect(db.RequireTwoFactor?"/account/security":"/");
    }
    catch (InvalidOperationException) { return Results.LocalRedirect("/setup?failed=true"); }
}).RequireRateLimiting("auth");

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync();await context.SignOutAsync(AccountAuthentication.ChallengeScheme);return Results.LocalRedirect("/login");
}).RequireAuthorization();

app.MapAccountAuthentication();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = Wire.Version }));
app.MapGet("/downloads/MeshAll-source.tar.gz", (IWebHostEnvironment environment) =>
{
    var archive = Path.Combine(environment.WebRootPath, "downloads", "MeshAll-source.tar.gz");
    return File.Exists(archive) ? Results.File(archive, "application/gzip", "MeshAll-source.tar.gz", enableRangeProcessing: true) : Results.NotFound();
});
app.MapGet("/install.sh", async (IWebHostEnvironment environment, IConfiguration configuration, Store db) =>
{
    var archive = Path.Combine(environment.WebRootPath, "downloads", "MeshAll-source.tar.gz");
    var template = Path.Combine(AppContext.BaseDirectory, "Install", "install.sh");
    if (!File.Exists(template)) return Results.NotFound("The server installer is unavailable in this build.");
    var url = db.Setting("publicUrl", configuration["MeshAll:PublicUrl"] ?? "http://localhost:5188").TrimEnd('/');
    var hash = "";
    if(File.Exists(archive)){await using var stream = File.OpenRead(archive);hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream));}
    var script = (await File.ReadAllTextAsync(template)).Replace("__MESHALL_SOURCE_URL__", url + "/downloads/MeshAll-source.tar.gz")
        .Replace("__MESHALL_SOURCE_SHA256__", hash).Replace("__MESHALL_CURL_PIN__", configuration["MeshAll:DownloadPublicKeyPin"] ?? "");
    return Results.Text(script, "text/x-shellscript");
});
app.MapPost("/api/agents/enroll", (EnrollmentRequest request, Store db) =>
{
    try { return Results.Ok(db.Enroll(request)); }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireRateLimiting("auth");
app.Map("/api/agents/connect", (HttpContext context, AgentGateway gateway) => gateway.AcceptAsync(context));
app.Map("/agent.ashx",(HttpContext context,NativeAgentHub hub)=>hub.AcceptAsync(context));
app.Map("/native-relay.ashx",(HttpContext context,NativeTunnelHub hub)=>hub.AcceptAsync(context));
app.MapGet("/api/agents/native-policy",(string token,NativeAgentHub hub)=>{try{return Results.File(System.Text.Encoding.UTF8.GetBytes(hub.Policy(token)),"application/octet-stream","MeshAllAgent.msh");}catch(InvalidOperationException){return Results.BadRequest("Enrollment key is invalid or expired.");}}).RequireAuthorization(Permissions.Enroll);
app.MapGet("/api/agents/native-executable",(string token,string platform,NativeAgentHub hub)=>{try{return Results.File(hub.Executable(token,platform),"application/octet-stream",platform.StartsWith("win",StringComparison.Ordinal)?"MeshAllAgent.exe":"MeshAllAgent");}catch(InvalidOperationException ex){return Results.BadRequest(ex.Message);}}).RequireRateLimiting("auth");
app.MapGet("/licenses/agent",(NativeAgentHub hub)=>Results.Text(hub.LicenseNotices(),"text/plain"));
app.Map("/api/remote/{id}/desktop", (HttpContext context, string id, AgentGateway gateway) => gateway.ProxyDesktopAsync(context, id)).RequireAuthorization(Permissions.Desktop);
app.Map("/api/network/{id}/{tool}", (HttpContext context, string id, string tool, NetworkGateway gateway) => gateway.ProxyAsync(context, id, tool)).RequireAuthorization();

app.MapGet("/api/devices/{id}/file", async (string id, string path, HttpContext context, Store db, AgentGateway gateway) =>
{
    if (db.FindDevice(id) is not { Disabled: false } device) return Results.NotFound();
    try
    {
        var file = await gateway.ExecuteAsync<FileContent>(id, "files.read", new FileInput(path), context.RequestAborted);
        db.Audit(context.User.Identity!.Name!, "File downloaded", device.Name, file.Name);
        return Results.File(Convert.FromBase64String(file.Base64), "application/octet-stream", file.Name);
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
    { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization(Permissions.FilesRead);

app.MapGet("/api/audit/export", (Store db) =>
    Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(db.AuditLog(10000), Wire.Json), "application/json", "meshall-audit.json"))
    .RequireAuthorization(Permissions.ExportAudit);
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program { }
