using System.Security.Claims;
using MeshAll.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace MeshAll.Server.Services;

public static partial class AccountAuthentication
{
    private static async Task<IResult> LoginAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        IFormCollection form = await context.Request.ReadFormAsync();
        string password = form["password"].ToString();
        string name = form["username"].ToString();
        UserInfo? user = password.Length <= 256 && name.Length <= 50 ? store.Authenticate(name, password) : null;
        if (user is null)
        {
            store.Audit("Anonymous", "Sign-in failed", ProductBrand.Name, context.Connection.RemoteIpAddress?.ToString() ?? "Local connection");
            return Results.LocalRedirect("/login?failed=true");
        }

        await context.SignOutAsync();
        await context.SignOutAsync(ChallengeScheme);
        if (user.TwoFactorEnabled)
        {
            string challenge = store.CreateLoginChallenge(user);
            ClaimsIdentity identity = new([new Claim("challenge", challenge)], ChallengeScheme);
            await context.SignInAsync(ChallengeScheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect("/login/2fa");
        }

        bool setupRequired = store.RequireTwoFactor;
        await SignIn(context, user, setupOnly: setupRequired);
        store.Audit(user.Name, setupRequired ? "Signed in for required two-factor enrollment" : "Signed in", ProductBrand.Name, context.Connection.RemoteIpAddress?.ToString() ?? "Local connection");
        return Results.LocalRedirect(setupRequired ? "/account/security" : store.LandingPage(user));
    }

    private static async Task<IResult> SetupAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        IFormCollection form = await context.Request.ReadFormAsync();
        try
        {
            UserInfo user = store.Setup(form["key"].ToString(), form["username"].ToString(), form["password"].ToString());
            await SignIn(context, user, setupOnly: store.RequireTwoFactor);
            return Results.LocalRedirect(store.RequireTwoFactor ? "/account/security" : "/");
        }
        catch (InvalidOperationException)
        {
            return Results.LocalRedirect("/setup?failed=true");
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        store.RevokeWebSession(context.User);
        store.Audit(context.User.Identity?.Name ?? "Anonymous", "Signed out", ProductBrand.Name);
        await context.SignOutAsync();
        await context.SignOutAsync(ChallengeScheme);
        return Results.LocalRedirect("/login");
    }
}
