using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MeshAll.Server.Services;

public static class AccountAuthentication
{
    public const string ChallengeScheme = "MeshAll.TwoFactorChallenge";
    public static async Task SignIn(HttpContext context, UserInfo user, bool mfa = false, bool setupOnly = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier,user.Id),new(ClaimTypes.Name,user.Name),new("version",user.Version.ToString()),
            new("sid",Store.Secret()),new("amr",mfa?"mfa":"pwd")
        };
        if (setupOnly) claims.Add(new("setup_required","true")); else claims.Add(new(ClaimTypes.Role,user.Role));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)));
    }
    public static void MapAccountAuthentication(this WebApplication app)
    {
        app.MapPost("/auth/2fa",async(HttpContext context,IAntiforgery antiforgery,Store db)=>
        {
            await antiforgery.ValidateRequestAsync(context);
            var pending=await context.AuthenticateAsync(ChallengeScheme);
            var challenge=pending.Principal?.FindFirstValue("challenge");
            var form=await context.Request.ReadFormAsync();
            var user=pending.Succeeded&&challenge is not null?db.CompleteLoginChallenge(challenge,form["code"].ToString()):null;
            if(user is null){db.Audit("Anonymous","Two-factor sign-in failed","MeshAll");return Results.LocalRedirect("/login/2fa?failed=true");}
            await context.SignOutAsync(ChallengeScheme);await SignIn(context,user,mfa:true);
            db.Audit(user.Name,"Signed in with two-factor authentication","MeshAll");return Results.LocalRedirect(db.LandingPage(user));
        }).RequireRateLimiting("auth");

        app.MapPost("/auth/2fa/setup",async(HttpContext context,IAntiforgery antiforgery,Store db)=>
        {
            await antiforgery.ValidateRequestAsync(context);var form=await context.Request.ReadFormAsync();
            var user=db.SessionUser(context.User,allowSetup:true);if(user is null)return Results.Unauthorized();
            try{db.BeginTwoFactorSetup(user.Id,form["password"].ToString());return Results.LocalRedirect("/account/security");}
            catch(InvalidOperationException){return Results.LocalRedirect("/account/security?failed=setup");}
        }).RequireAuthorization().RequireRateLimiting("auth");
        app.MapPost("/auth/2fa/confirm",async(HttpContext context,IAntiforgery antiforgery,Store db)=>
        {
            await antiforgery.ValidateRequestAsync(context);var form=await context.Request.ReadFormAsync();
            var user=db.SessionUser(context.User,allowSetup:true);if(user is null)return Results.Unauthorized();
            try
            {
                var receipt=db.EnableTwoFactor(user.Id,form["code"].ToString());await SignIn(context,db.FindUser(user.Id)!,mfa:true);
                return Results.LocalRedirect("/account/security?codes="+receipt);
            }
            catch(InvalidOperationException){return Results.LocalRedirect("/account/security?failed=confirm");}
        }).RequireAuthorization().RequireRateLimiting("auth");
        app.MapPost("/auth/2fa/recovery",async(HttpContext context,IAntiforgery antiforgery,Store db)=>
        {
            await antiforgery.ValidateRequestAsync(context);var form=await context.Request.ReadFormAsync();
            var user=db.SessionUser(context.User);if(user is null)return Results.Unauthorized();
            try{return Results.LocalRedirect("/account/security?codes="+db.RegenerateRecoveryCodes(user.Id,form["password"].ToString(),form["code"].ToString()));}
            catch(InvalidOperationException){return Results.LocalRedirect("/account/security?failed=recovery");}
        }).RequireAuthorization().RequireRateLimiting("auth");
        app.MapPost("/auth/2fa/disable",async(HttpContext context,IAntiforgery antiforgery,Store db)=>
        {
            await antiforgery.ValidateRequestAsync(context);var form=await context.Request.ReadFormAsync();
            var user=db.SessionUser(context.User);if(user is null)return Results.Unauthorized();
            try{db.DisableTwoFactor(user.Id,form["password"].ToString(),form["code"].ToString());await SignIn(context,db.FindUser(user.Id)!);return Results.LocalRedirect("/account/security");}
            catch(InvalidOperationException){return Results.LocalRedirect("/account/security?failed=disable");}
        }).RequireAuthorization().RequireRateLimiting("auth");
    }
}
