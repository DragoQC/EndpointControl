using System.Threading.RateLimiting;
using System.Security.Claims;
using System.Security.Authentication;
using MeshAll.Server.Localization;
using MeshAll.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;

namespace MeshAll.Server.Services;

public static partial class ServerHosting
{
    public static WebApplicationBuilder AddMeshAllServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<Store>();
        builder.Services.AddSingleton<SecurityEvents>();
        builder.Services.AddSingleton<SourceArchive>();
        builder.Services.AddSingleton<NetworkGateway>();
        builder.Services.AddSingleton<ProtocolWorker>();
        builder.Services.AddSingleton<RemoteIdentityResolver>();
        builder.Services.AddSingleton<NativeCoreBuilder>();
        builder.Services.AddSingleton<NativeTunnelHub>();
        builder.Services.AddSingleton<NativeAgentHub>();
        builder.Services.AddSingleton<NativeMsiBuilder>();
        builder.Services.AddSingleton<AgentInstallerDownloads>();
        builder.Services.AddSingleton<AgentGateway>();
        builder.Services.AddScoped<Workspace>();
        builder.Services.AddLocalization();
        builder.Services.AddSingleton<UiText>();
        builder.Services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture("en").AddSupportedCultures("en", "fr").AddSupportedUICultures("en", "fr");
            options.RequestCultureProviders = [new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
        });

        builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        builder.Services.AddSingleton<IAuthorizationHandler, LogsPermissionHandler>();

        builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment());

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, WorkspaceAuthenticationStateProvider>();

        string dataPath = Path.GetFullPath(builder.Configuration["MeshAll:DataDirectory"] ?? "Data", builder.Environment.ContentRootPath);

        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys"))).SetApplicationName("MeshAll");

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.Cookie.Name = "MeshAll.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = 401;
                }
                else
                {
                    context.Response.Redirect(context.RedirectUri);
                }

                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                Store accounts = context.HttpContext.RequestServices.GetRequiredService<Store>();
                UserInfo? user = context.Principal is null ? null : accounts.ValidateCookieSession(context.Principal, context.Properties.IssuedUtc);
                if (user is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync();
                }
                else if (!context.Principal!.HasClaim(Store.SessionSchemaClaim, "2"))
                {
                    ClaimsIdentity identity = new(context.Principal.Identity as ClaimsIdentity);
                    identity.AddClaim(new Claim(Store.SessionSchemaClaim, "2"));
                    context.ReplacePrincipal(new ClaimsPrincipal(identity));
                    context.ShouldRenew = true;
                }
            };
        }).AddCookie(AccountAuthentication.ChallengeScheme, options =>
        {
            options.Cookie.Name = "MeshAll.SecondFactor";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.SlidingExpiration = false;
        });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(Permissions.LogsPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new LogsPermissionRequirement()));
            foreach ((string Id, string Label) capability in Permissions.All)
            {
                options.AddPolicy(capability.Id, policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(capability.Id)));
            }
        });

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<SecurityEvents>().Rejected(context.HttpContext, "Request rate limit");
                return ValueTask.CompletedTask;
            };
            options.AddPolicy("agent-connect", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.AddPolicy("downloads", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = Wire.MaxMessageBytes;
            options.ConfigureHttpsDefaults(https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
        });

        return builder;
    }
}
