using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MeshAll.Server.Services;

public static partial class AccountAuthentication
{
    public const string ChallengeScheme = "MeshAll.TwoFactorChallenge";

    public static async Task SignIn(HttpContext context, UserInfo user, bool mfa = false, bool setupOnly = false)
    {
        Store store = context.RequestServices.GetRequiredService<Store>();
        store.RevokeWebSession(context.User);
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Name),
            new("version", user.Version.ToString()),
            new("sid", store.CreateWebSession(user)),
            new(Store.SessionSchemaClaim, "2"),
            new("amr", mfa ? "mfa" : "pwd")
        ];
        if (setupOnly)
        {
            claims.Add(new("setup_required", "true"));
        }
        else
        {
            claims.Add(new(ClaimTypes.Role, user.Role));
        }

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }
    public static void MapAccountAuthentication(this WebApplication app)
    {
        app.MapPost("/auth/login", LoginAsync).RequireRateLimiting("auth");
        app.MapPost("/auth/setup", SetupAsync).RequireRateLimiting("auth");
        app.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();
        app.MapPost("/auth/2fa", CompleteTwoFactorAsync).RequireRateLimiting("auth");
        app.MapPost("/auth/2fa/setup", BeginTwoFactorSetupAsync).RequireAuthorization().RequireRateLimiting("auth");
        app.MapPost("/auth/2fa/confirm", ConfirmTwoFactorAsync).RequireAuthorization().RequireRateLimiting("auth");
        app.MapPost("/auth/2fa/recovery", RegenerateRecoveryCodesAsync).RequireAuthorization().RequireRateLimiting("auth");
        app.MapPost("/auth/2fa/disable", DisableTwoFactorAsync).RequireAuthorization().RequireRateLimiting("auth");
    }
}
