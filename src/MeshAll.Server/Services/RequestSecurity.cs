using System.Net;

namespace MeshAll.Server.Services;

public static class RequestSecurity
{
    public static void SetHeaders(HttpContext context)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        bool viewer = context.Request.Path.StartsWithSegments("/remote");
        context.Response.Headers["X-Frame-Options"] = viewer ? "SAMEORIGIN" : "DENY";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; script-src-attr 'none'; style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data: blob:; font-src 'self'; connect-src 'self' "
            + (context.Request.IsHttps ? "wss://" : "ws://") + context.Request.Host.ToUriComponent()
            + "; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors "
            + (viewer ? "'self'" : "'none'");
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), fullscreen=(self)";
        if (context.Request.IsHttps)
        {
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
        }
        if (!context.Request.Path.StartsWithSegments("/_framework"))
        {
            context.Response.Headers.CacheControl = "no-store";
        }
    }

    public static bool IsAgentSocket(PathString path) => path == "/agent.ashx" || path == "/native-relay.ashx" || path == "/api/agents/connect";

    public static bool AllowedDirectAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
        {
            return false;
        }
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length != 4 || bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }
}
