using System.Security.Claims;

namespace MeshAll.Server.Services;

public sealed partial class Store
{
    public const string SessionSchemaClaim = "session_schema";

    public string CreateWebSession(UserInfo user)
    {
        string id = Secret();
        RegisterWebSession(user, id);
        return id;
    }

    private void RegisterWebSession(UserInfo user, string id)
    {
        Execute("DELETE FROM web_sessions WHERE expires<$0; INSERT OR IGNORE INTO web_sessions(id,userId,version,expires) VALUES($1,$2,$3,$4)",
            Now(), Hash(id), user.Id, user.Version, DateTimeOffset.UtcNow.AddHours(8).ToString("O"));
    }

    private bool ValidWebSession(ClaimsPrincipal principal, UserInfo user)
    {
        string? id = principal.FindFirstValue("sid");
        return id is { Length: 64 } && id.All(char.IsAsciiHexDigit) && Scalar<long>(
            "SELECT EXISTS(SELECT 1 FROM web_sessions WHERE id=$0 AND userId=$1 AND version=$2 AND revoked=0 AND expires>$3)",
            Hash(id), user.Id, user.Version, Now()) != 0;
    }

    public UserInfo? ValidateCookieSession(ClaimsPrincipal principal, DateTimeOffset? issuedAt)
    {
        UserInfo? user = PrincipalUser(principal, allowSetup: true);
        string? id = principal.FindFirstValue("sid");
        if (user is null || id is not { Length: 64 } || !id.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        // Only already-signed, pre-upgrade cookies may be migrated. The cookie
        // handler verifies their signature and expiry before this method runs.
        DateTimeOffset cutover = Date(Setting("webSessionCutover", Now()));
        if (!principal.HasClaim(SessionSchemaClaim, "2") && issuedAt <= cutover
            && DateTimeOffset.UtcNow < cutover.AddHours(8)
            && Scalar<long>("SELECT COUNT(*) FROM web_sessions WHERE id=$0", Hash(id)) == 0)
        {
            RegisterWebSession(user, id);
        }

        if (!ValidWebSession(principal, user))
        {
            return null;
        }

        // Keep the server lifetime aligned with sliding cookie expiration,
        // without writing to SQLite on every request.
        Execute("UPDATE web_sessions SET expires=$1 WHERE id=$0 AND expires<$2 AND revoked=0",
            Hash(id), DateTimeOffset.UtcNow.AddHours(8).ToString("O"), DateTimeOffset.UtcNow.AddHours(7).ToString("O"));
        return user;
    }

    public void RevokeWebSession(ClaimsPrincipal principal)
    {
        string? id = principal.FindFirstValue("sid");
        if (id is { Length: 64 })
        {
            Execute("UPDATE web_sessions SET revoked=1 WHERE id=$0", Hash(id));
        }
    }
}
