using System.Net;
using MeshAll.Server.Localization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace MeshAll.Server.Services;

public static partial class ServerHosting
{
    public static WebApplication UseMeshAllMiddleware(this WebApplication app)
    {
        app.UseRequestLocalization();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
        }

        app.Use(async (context, next) =>
        {
            RequestSecurity.SetHeaders(context);
            if (context.Request.Path.StartsWithSegments("/auth") || context.Request.Path == "/culture" || context.Request.Path == "/api/agents/enroll")
            {
                IHttpMaxRequestBodySizeFeature? limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false })
                {
                    limit.MaxRequestBodySize = 32768;
                }
            }
            if (app.Environment.IsProduction() && !context.Request.IsHttps
                && context.Connection.RemoteIpAddress is IPAddress address && !IPAddress.IsLoopback(address))
            {
                context.Response.StatusCode = 400;
                return;
            }

            try
            {
                await next();
            }
            catch (AntiforgeryValidationException) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                RequestSecurity.SetHeaders(context);
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(UiText.Get("The form expired. Reload the page and try again."));
            }
            finally
            {
                if (context.Response.StatusCode is 401 or 403 && context.Request.Path.StartsWithSegments("/api"))
                {
                    context.RequestServices.GetRequiredService<SecurityEvents>().Rejected(context, "Management access");
                }
            }
        });

        app.UseRateLimiter();
        app.UseAuthentication();

        app.Use(async (context, next) =>
        {
            if (context.User.HasClaim("setup_required", "true")
                && !context.Request.Path.StartsWithSegments("/account/security") && !context.Request.Path.StartsWithSegments("/auth/2fa")
                && context.Request.Path != "/auth/logout" && !context.Request.Path.StartsWithSegments("/_framework")
                && context.Request.Path != "/culture"
                && !context.Request.Path.StartsWithSegments("/_blazor") && !context.Request.Path.StartsWithSegments("/vendor")
                && context.Request.Path != "/app.js" && context.Request.Path != "/app.css")
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = 403;
                }
                else
                {
                    context.Response.Redirect("/account/security");
                }

                return;
            }
            await next();
        });

        app.UseAuthorization();
        app.UseAntiforgery();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest && !RequestSecurity.IsAgentSocket(context.Request.Path) && !AgentGateway.SameOrigin(context))
            {
                context.RequestServices.GetRequiredService<SecurityEvents>().Rejected(context, "WebSocket origin");
                context.Response.StatusCode = 403;
                return;
            }
            await next();
        });

        return app;
    }
}
