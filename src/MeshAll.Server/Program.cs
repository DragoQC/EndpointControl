using System.Security.Claims;
using System.Threading.RateLimiting;
using MeshAll.Server.Components;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

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
        var user = context.HttpContext.RequestServices.GetRequiredService<Store>()
            .FindUser(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        if (user is null || user.Disabled || user.Version.ToString() != context.Principal?.FindFirstValue("version"))
        { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(); }
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Manage", policy => policy.RequireRole("Admin", "Operator"));
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
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
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

static async Task SignIn(HttpContext context, UserInfo user)
{
    var identity = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Name),
        new Claim(ClaimTypes.Role, user.Role), new Claim("version", user.Version.ToString())
    }, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
}

app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, Store db) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString(); var name = form["username"].ToString();
    var user = password.Length <= 256 && name.Length <= 50 ? db.Authenticate(name, password) : null;
    if (user is null) { db.Audit("Anonymous", "Sign-in failed", "MeshAll"); return Results.LocalRedirect("/login?failed=true"); }
    await SignIn(context, user); db.Audit(user.Name, "Signed in", "MeshAll");
    return Results.LocalRedirect("/");
}).RequireRateLimiting("auth");

app.MapPost("/auth/setup", async (HttpContext context, IAntiforgery antiforgery, Store db) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    try
    {
        var user = db.Setup(form["key"].ToString(), form["username"].ToString(), form["password"].ToString());
        await SignIn(context, user); return Results.LocalRedirect("/");
    }
    catch (InvalidOperationException) { return Results.LocalRedirect("/setup?failed=true"); }
}).RequireRateLimiting("auth");

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(); return Results.LocalRedirect("/login");
}).RequireAuthorization();

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
app.MapGet("/api/agents/native-policy",(string token,NativeAgentHub hub)=>{try{return Results.File(System.Text.Encoding.UTF8.GetBytes(hub.Policy(token)),"application/octet-stream","MeshAllAgent.msh");}catch(InvalidOperationException){return Results.BadRequest("Enrollment key is invalid or expired.");}}).RequireAuthorization("Admin");
app.MapGet("/api/agents/native-package",(string token,string platform,NativeAgentHub hub)=>{try{return Results.File(hub.Package(token,platform),"application/zip","MeshAll-Agent-"+platform+".zip");}catch(InvalidOperationException ex){return Results.BadRequest(ex.Message);}}).RequireRateLimiting("auth");
app.Map("/api/remote/{id}/desktop", (HttpContext context, string id, AgentGateway gateway) => gateway.ProxyDesktopAsync(context, id)).RequireAuthorization("Manage");
app.Map("/api/network/{id}/{tool}", (HttpContext context, string id, string tool, NetworkGateway gateway) => gateway.ProxyAsync(context, id, tool)).RequireAuthorization("Manage");

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
}).RequireAuthorization("Manage");

app.MapGet("/api/audit/export", (Store db) =>
    Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(db.AuditLog(10000), Wire.Json), "application/json", "meshall-audit.json"))
    .RequireAuthorization("Admin");
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
