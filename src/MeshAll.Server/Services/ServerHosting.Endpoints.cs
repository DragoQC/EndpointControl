using System.Text;
using MeshAll.Server.Localization;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public static partial class ServerHosting
{
    public static WebApplication MapMeshAllEndpoints(this WebApplication app)
    {
        app.MapAccountAuthentication();
        app.MapLanguageSelection();

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            version = Wire.Version
        }));

        app.MapGet("/downloads/EndpointControl-source.tar.gz", (IWebHostEnvironment environment) =>
        {
            string archive = Path.Combine(environment.WebRootPath, "downloads", "EndpointControl-source.tar.gz");
            return File.Exists(archive) ? Results.File(archive, "application/gzip", "EndpointControl-source.tar.gz", enableRangeProcessing: true) : Results.NotFound();
        }).RequireRateLimiting("downloads");

        app.MapGet("/downloads/MeshAll-source.tar.gz", () => Results.Redirect("/downloads/EndpointControl-source.tar.gz"));

        app.MapGet("/install.sh", async (IWebHostEnvironment environment, IConfiguration configuration, Store db, SourceArchive source, HttpContext context) =>
        {
            string archive = Path.Combine(environment.WebRootPath, "downloads", "EndpointControl-source.tar.gz");
            string template = Path.Combine(AppContext.BaseDirectory, "Install", "install.sh");
            if (!File.Exists(template))
            {
                return Results.NotFound("The server installer is unavailable in this build.");
            }

            string url = db.Setting("publicUrl", configuration["MeshAll:PublicUrl"] ?? "http://localhost:5188").TrimEnd('/');
            string hash = await source.HashAsync(archive, context.RequestAborted);
            string script = (await File.ReadAllTextAsync(template)).Replace("__MESHALL_SOURCE_URL__", url + "/downloads/EndpointControl-source.tar.gz")
                .Replace("__MESHALL_SOURCE_SHA256__", hash).Replace("__MESHALL_CURL_PIN__", configuration["MeshAll:DownloadPublicKeyPin"] ?? "");
            return Results.Text(script, "text/x-shellscript");
        }).RequireRateLimiting("downloads");

        app.MapPost("/api/agents/enroll", (EnrollmentRequest request, Store db) =>
        {
            try
            {
                return Results.Ok(db.Enroll(request));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message
                });
            }
        }).RequireRateLimiting("auth");

        app.Map("/api/agents/connect", (HttpContext context, AgentGateway gateway) => gateway.AcceptAsync(context)).RequireRateLimiting("agent-connect");
        app.Map("/agent.ashx", (HttpContext context, NativeAgentHub hub) => hub.AcceptAsync(context)).RequireRateLimiting("agent-connect");
        app.Map("/native-relay.ashx", (HttpContext context, NativeTunnelHub hub) => hub.AcceptAsync(context));

        app.MapGet("/api/agents/native-policy", (string token, NativeAgentHub hub) =>
        {
            try
            {
                return Results.File(Encoding.UTF8.GetBytes(hub.Policy(token)), "application/octet-stream", ProductBrand.AgentDownloadName + ".msh");
            }
            catch (InvalidOperationException)
            {
                return Results.BadRequest("Enrollment key is invalid or expired.");
            }
        }).RequireAuthorization(Permissions.Enroll);

        app.MapGet("/api/agents/native-executable", (string token, string platform, NativeAgentHub hub) =>
        {
            try
            {
                return Results.File(hub.Executable(token, platform), "application/octet-stream", ProductBrand.AgentDownloadName + (platform.StartsWith("win", StringComparison.Ordinal) ? ".exe" : ""));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).RequireRateLimiting("auth");

        app.MapGet("/api/agents/native-msi", async (string token, string platform, HttpContext context,
            AgentInstallerDownloads installers, NativeMsiBuilder packages, Store db) =>
        {
            if (platform != NativeMsiPackage.Platform)
            {
                return Results.BadRequest("MSI packaging currently supports Windows x64.");
            }
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                byte[] package = await installers.PublishAsync(token, context.RequestAborted);
                db.Audit(context.User.Identity!.Name!, "Agent MSI downloaded", platform);
                return Results.File(package, "application/x-msi", packages.FileName);
            }
            catch (InvalidOperationException error)
            {
                return Results.BadRequest(error.Message);
            }
            catch (IOException)
            {
                return Results.Problem("MSI packaging is unavailable. Check that wixl and msitools are installed on the server.", statusCode: 503);
            }
        }).RequireAuthorization(Permissions.Enroll).RequireRateLimiting("downloads");

        app.MapGet("/api/agents/windows-msi", async (HttpContext context, AgentInstallerDownloads installers, Store db) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                AgentInstallerDownload? download = await installers.ReadCurrentAsync(context.RequestAborted);
                if (download is null)
                {
                    return Results.NotFound("Create an enrollment key and generate a Windows MSI first.");
                }
                db.Audit(context.User.Identity!.Name!, "Agent MSI downloaded", download.Information.GroupName);
                return Results.File(download.Bytes, "application/x-msi", download.Information.FileName);
            }
            catch (IOException)
            {
                return Results.Problem("The installer changed. Reload the download page and try again.", statusCode: 503);
            }
        }).RequireAuthorization(Permissions.Enroll).RequireRateLimiting("downloads");

        app.MapGet("/licenses/agent", (NativeAgentHub hub) => Results.Text(hub.LicenseNotices(), "text/plain"));
        app.Map("/api/remote/{id}/desktop", (HttpContext context, string id, AgentGateway gateway) => gateway.ProxyDesktopAsync(context, id)).RequireAuthorization(Permissions.Desktop);

        app.MapGet("/api/remote/{id}/desktop/access", (string id, HttpContext context, Store db, AgentGateway gateway) =>
        {
            if (!db.CanAccessDevice(db.SessionUser(context.User), id))
            {
                return Results.Forbid();
            }
            if (db.FindDevice(id) is not { Disabled: false })
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                online = gateway.Online(id),
                requiresConsent = db.RequiresDesktopConsent(id),
                controlAllowed = db.HasPermission(db.SessionUser(context.User), Permissions.Control)
            });
        }).RequireAuthorization(Permissions.Desktop);

        app.Map("/api/network/{id}/{tool}", (HttpContext context, string id, string tool, NetworkGateway gateway) => gateway.ProxyAsync(context, id, tool)).RequireAuthorization();

        app.MapGet("/api/devices/{id}/file", async (string id, string path, HttpContext context, Store db, AgentGateway gateway) =>
        {
            if (!db.CanAccessDevice(db.SessionUser(context.User), id))
            {
                return Results.Forbid();
            }
            if (db.FindDevice(id) is not { Disabled: false } device)
            {
                return Results.NotFound();
            }

            try
            {
                FileContent file = await gateway.ExecuteAsync<FileContent>(id, "files.read", new FileInput(path), context.RequestAborted);
                db.Audit(context.User.Identity!.Name!, "File downloaded", device.Name, file.Name);
                return Results.File(Convert.FromBase64String(file.Base64), "application/octet-stream", file.Name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message
                });
            }
        }).RequireAuthorization(Permissions.FilesRead);

        app.MapGet("/api/audit/export", (HttpContext context, Store db) =>
        {
            context.Response.Headers.ContentDisposition = "attachment; filename=meshall-audit.json";
            return Results.Json(db.ExportAudit(context.RequestAborted), Wire.Json);
        }).RequireAuthorization(Permissions.ExportAudit);

        return app;
    }
}
