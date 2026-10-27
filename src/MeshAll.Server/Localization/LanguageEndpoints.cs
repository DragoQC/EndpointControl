using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Localization;

namespace MeshAll.Server.Localization;

public static class LanguageEndpoints
{
    public static IEndpointRouteBuilder MapLanguageSelection(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/culture", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            IFormCollection form = await context.Request.ReadFormAsync();
            string culture = form["culture"].ToString();
            string returnUrl = form["returnUrl"].ToString();
            if (returnUrl.Length == 0)
            {
                returnUrl = "/";
            }

            if (culture is not ("en" or "fr") || !IsLocalPath(returnUrl))
            {
                return Results.BadRequest(UiText.Get("Invalid language selection."));
            }

            context.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)), new CookieOptions
                {
                    Path = "/",
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    IsEssential = true,
                    Expires = DateTimeOffset.UtcNow.AddYears(1)
                });

            return Results.LocalRedirect(returnUrl);
        }).AllowAnonymous();

        return endpoints;
    }

    private static bool IsLocalPath(string path) => path.Length <= 2048 && path.StartsWith('/')
        && (path.Length == 1 || path[1] is not ('/' or '\\')) && !path.Any(char.IsControl);
}
