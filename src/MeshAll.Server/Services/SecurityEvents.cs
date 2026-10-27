using Microsoft.Extensions.Caching.Memory;

namespace MeshAll.Server.Services;

public sealed class SecurityEvents(Store store, ILogger<SecurityEvents> logger) : IDisposable
{
    private readonly MemoryCache recent = new(new MemoryCacheOptions { SizeLimit = 2048 });

    public void Rejected(HttpContext context, string category)
    {
        string source = context.Connection.RemoteIpAddress?.ToString() ?? "Local connection";
        string key = source + "/" + category;
        lock (recent)
        {
            if (recent.TryGetValue(key, out _))
            {
                return;
            }
            recent.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 1 });
        }
        string actor = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name ?? "Authenticated user" : "Anonymous";
        store.Audit(actor, "Security request rejected", category, source);
        logger.LogWarning("Security request rejected: {Category}; source {Address}", category, source);
    }

    public void Dispose() => recent.Dispose();
}
