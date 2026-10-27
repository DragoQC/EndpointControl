using System.Security.Claims;
using MeshAll.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace MeshAll.Server.Services;

public static partial class AccountAuthentication
{
    private static async Task<IResult> CompleteTwoFactorAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        AuthenticateResult pending = await context.AuthenticateAsync(ChallengeScheme);
        string? challenge = pending.Principal?.FindFirstValue("challenge");
        IFormCollection form = await context.Request.ReadFormAsync();
        UserInfo? user = pending.Succeeded && challenge is not null ? store.CompleteLoginChallenge(challenge, form["code"].ToString()) : null;
        if (user is null)
        {
            store.Audit("Anonymous", "Two-factor sign-in failed", ProductBrand.Name, context.Connection.RemoteIpAddress?.ToString() ?? "Local connection");
            return Results.LocalRedirect("/login/2fa?failed=true");
        }

        await context.SignOutAsync(ChallengeScheme);
        await SignIn(context, user, mfa: true);
        store.Audit(user.Name, "Signed in with two-factor authentication", ProductBrand.Name, context.Connection.RemoteIpAddress?.ToString() ?? "Local connection");
        return Results.LocalRedirect(store.LandingPage(user));
    }

    private static async Task<IResult> BeginTwoFactorSetupAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        IFormCollection form = await context.Request.ReadFormAsync();
        UserInfo? user = store.SessionUser(context.User, allowSetup: true);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            store.BeginTwoFactorSetup(user.Id, form["password"].ToString());
            return Results.LocalRedirect("/account/security");
        }
        catch (InvalidOperationException)
        {
            return Results.LocalRedirect("/account/security?failed=setup");
        }
    }

    private static async Task<IResult> ConfirmTwoFactorAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        IFormCollection form = await context.Request.ReadFormAsync();
        UserInfo? user = store.SessionUser(context.User, allowSetup: true);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            string receipt = store.EnableTwoFactor(user.Id, form["code"].ToString());
            await SignIn(context, store.FindUser(user.Id)!, mfa: true);
            return Results.LocalRedirect("/account/security?codes=" + receipt);
        }
        catch (InvalidOperationException)
        {
            return Results.LocalRedirect("/account/security?failed=confirm");
        }
    }

    private static async Task<IResult> RegenerateRecoveryCodesAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        IFormCollection form = await context.Request.ReadFormAsync();
        UserInfo? user = store.SessionUser(context.User);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            string receipt = store.RegenerateRecoveryCodes(user.Id, form["password"].ToString(), form["code"].ToString());
            return Results.LocalRedirect("/account/security?codes=" + receipt);
        }
        catch (InvalidOperationException)
        {
            return Results.LocalRedirect("/account/security?failed=recovery");
        }
    }

    private static async Task<IResult> DisableTwoFactorAsync(HttpContext context, IAntiforgery antiforgery, Store store)
    {
        await antiforgery.ValidateRequestAsync(context);
        IFormCollection form = await context.Request.ReadFormAsync();
        UserInfo? user = store.SessionUser(context.User);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            store.DisableTwoFactor(user.Id, form["password"].ToString(), form["code"].ToString());
            await SignIn(context, store.FindUser(user.Id)!);
            return Results.LocalRedirect("/account/security");
        }
        catch (InvalidOperationException)
        {
            return Results.LocalRedirect("/account/security?failed=disable");
        }
    }
}
