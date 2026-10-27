using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;

namespace MeshAll.Server.Services;

public sealed partial class Store
{
    public bool RequireTwoFactor => Setting("requireTwoFactor", "false") == "true";

    public UserInfo? SessionUser(ClaimsPrincipal principal, bool allowSetup = false)
    {
        UserInfo? user = PrincipalUser(principal, allowSetup);
        return user is not null && ValidWebSession(principal, user) ? user : null;
    }

    private UserInfo? PrincipalUser(ClaimsPrincipal principal, bool allowSetup)
    {
        UserInfo? user = FindUser(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        return principal.Identity?.IsAuthenticated == true && user is
        {
            Disabled: false
        }
            && user.Version.ToString() == principal.FindFirstValue("version")
            && (!user.TwoFactorEnabled || principal.HasClaim("amr", "mfa"))
            && (allowSetup || !principal.HasClaim("setup_required", "true")) ? user : null;
    }
    public int RecoveryCodesRemaining(string id) => checked((int)Scalar<long>("SELECT COUNT(*) FROM recovery_codes WHERE userId=$0", id));

    private void VerifyCurrentPassword(string id, string password)
    {
        UserInfo? user = FindUser(id);
        if (user is null || user.Disabled || password.Length > 256 || Authenticate(user.Name, password) is null)
        {
            throw new InvalidOperationException("Your current password is incorrect.");
        }
    }
    public void BeginTwoFactorSetup(string id, string password)
    {
        lock (gate)
        {
            VerifyCurrentPassword(id, password);
            if (FindUser(id)!.TwoFactorEnabled)
            {
                throw new InvalidOperationException("Two-factor authentication is already enabled.");
            }

            Execute("INSERT INTO two_factor_setup VALUES($0,$1,$2) ON CONFLICT(userId) DO UPDATE SET secret=$1,expires=$2", id,
                securityProtector.Protect(Totp.NewSecret()), DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"));
        }
    }
    public string? PendingTwoFactorSecret(string id)
    {
        (string Secret, DateTimeOffset Expires) row = Query("SELECT secret,expires FROM two_factor_setup WHERE userId=$0", r => (Secret: r.GetString(0), Expires: Date(r.GetString(1))), id).SingleOrDefault();
        return row.Secret is not null && row.Expires > DateTimeOffset.UtcNow ? securityProtector.Unprotect(row.Secret) : null;
    }
    public string EnableTwoFactor(string id, string code)
    {
        lock (gate)
        {
            if (FindUser(id) is not
                {
                    Disabled: false, TwoFactorEnabled: false
                })
            {
                throw new InvalidOperationException("Account is unavailable or two-factor authentication is already enabled.");
            }

            string secret = PendingTwoFactorSecret(id) ?? throw new InvalidOperationException("Setup expired. Start again.");
            long step = Totp.Match(secret, code, DateTimeOffset.UtcNow) ?? throw new InvalidOperationException("Invalid authenticator code. Check the code and the time on your phone.");
            Execute("INSERT INTO two_factor VALUES($0,$1,$2); DELETE FROM two_factor_setup WHERE userId=$0; UPDATE users SET version=version+1 WHERE id=$0", id, securityProtector.Protect(secret), step);
            string receipt = ReplaceRecoveryCodes(id);
            Audit(FindUser(id)!.Name, "Two-factor authentication enabled", "Account");
            return receipt;
        }
    }
    private bool VerifyFactor(string id, string input)
    {
        if (input.Length > 100)
        {
            return false;
        }

        (string Secret, long Step) row = Query("SELECT secret,lastStep FROM two_factor WHERE userId=$0", r => (Secret: r.GetString(0), Step: r.GetInt64(1)), id).SingleOrDefault();
        if (row.Secret is null)
        {
            return false;
        }

        long? step = Totp.Match(securityProtector.Unprotect(row.Secret), input, DateTimeOffset.UtcNow, row.Step);
        if (step is not null)
        {
            using Microsoft.Data.Sqlite.SqliteConnection? connection = Open();
            using Microsoft.Data.Sqlite.SqliteCommand? update = Command(connection, "UPDATE two_factor SET lastStep=$1 WHERE userId=$0 AND lastStep<$1", [id, step.Value]);
            return update.ExecuteNonQuery() == 1;
        }
        string normalized = input.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 20 || !normalized.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        using Microsoft.Data.Sqlite.SqliteConnection? c = Open();
        using Microsoft.Data.Sqlite.SqliteCommand? cmd = Command(c, "DELETE FROM recovery_codes WHERE userId=$0 AND hash=$1", [id, Hash(normalized)]);
        return cmd.ExecuteNonQuery() == 1;
    }
    public string CreateLoginChallenge(UserInfo user)
    {
        lock (gate)
        {
            Execute("DELETE FROM login_challenges WHERE expires<$0", Now());
            string id = Secret();
            Execute("INSERT INTO login_challenges(id,userId,version,expires) VALUES($0,$1,$2,$3)", id, user.Id, user.Version, DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
            return id;
        }
    }
    public UserInfo? CompleteLoginChallenge(string challenge, string code)
    {
        lock (gate)
        {
            (string Id, int Version, DateTimeOffset Expires, int Attempts) row = Query("SELECT userId,version,expires,attempts FROM login_challenges WHERE id=$0", r => (Id: r.GetString(0), Version: r.GetInt32(1), Expires: Date(r.GetString(2)), Attempts: r.GetInt32(3)), challenge).SingleOrDefault();
            if (row.Id is null || row.Expires <= DateTimeOffset.UtcNow || row.Attempts >= 5)
            {
                return null;
            }

            UserInfo? user = FindUser(row.Id);
            if (user is null || user.Disabled || !user.TwoFactorEnabled || user.Version != row.Version)
            {
                return null;
            }

            Execute("UPDATE login_challenges SET attempts=attempts+1 WHERE id=$0", challenge);
            if (!VerifyFactor(user.Id, code))
            {
                return null;
            }

            Execute("DELETE FROM login_challenges WHERE id=$0", challenge);
            return user;
        }
    }
    private string ReplaceRecoveryCodes(string id)
    {
        string[] codes = Enumerable.Range(0, 10).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(10))).ToArray();
        using (Microsoft.Data.Sqlite.SqliteConnection? c = Open())
        using (Microsoft.Data.Sqlite.SqliteTransaction? tx = c.BeginTransaction())
        {
            using Microsoft.Data.Sqlite.SqliteCommand? clear = Command(c, "DELETE FROM recovery_codes WHERE userId=$0; DELETE FROM security_receipts WHERE userId=$0 OR expires<$1", [id, Now()]);
            clear.Transaction = tx;
            clear.ExecuteNonQuery();
            foreach (string? code in codes)
            {
                using Microsoft.Data.Sqlite.SqliteCommand? insert = Command(c, "INSERT INTO recovery_codes VALUES($0,$1)", [id, Hash(code)]);
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        string receipt = Secret();
        Execute("INSERT INTO security_receipts VALUES($0,$1,$2,$3)", Hash(receipt), id,
            securityProtector.Protect(JsonSerializer.Serialize(codes)), DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        return receipt;
    }
    public string[]? TakeRecoveryCodes(string id, string receipt)
    {
        lock (gate)
        {
            (string Payload, DateTimeOffset Expires) row = Query("SELECT payload,expires FROM security_receipts WHERE id=$0 AND userId=$1", r => (Payload: r.GetString(0), Expires: Date(r.GetString(1))), Hash(receipt), id).SingleOrDefault();
            Execute("DELETE FROM security_receipts WHERE id=$0 AND userId=$1", Hash(receipt), id);
            return row.Payload is not null && row.Expires > DateTimeOffset.UtcNow ? JsonSerializer.Deserialize<string[]>(securityProtector.Unprotect(row.Payload)) : null;
        }
    }
    public string RegenerateRecoveryCodes(string id, string password, string code)
    {
        lock (gate)
        {
            VerifyCurrentPassword(id, password);
            if (!VerifyFactor(id, code))
            {
                throw new InvalidOperationException("Invalid or already used authenticator/recovery code.");
            }

            string receipt = ReplaceRecoveryCodes(id);
            Audit(FindUser(id)!.Name, "Recovery codes regenerated", "Account");
            return receipt;
        }
    }
    public void DisableTwoFactor(string id, string password, string code)
    {
        lock (gate)
        {
            if (RequireTwoFactor)
            {
                throw new InvalidOperationException("This instance requires two-factor authentication.");
            }

            VerifyCurrentPassword(id, password);
            if (!VerifyFactor(id, code))
            {
                throw new InvalidOperationException("Invalid or already used authenticator/recovery code.");
            }

            ResetTwoFactor(id);
            Audit(FindUser(id)!.Name, "Two-factor authentication disabled", "Account");
        }
    }
    public void ResetTwoFactor(string id)
    {
        lock (gate)
        {
            Execute("DELETE FROM two_factor WHERE userId=$0; DELETE FROM two_factor_setup WHERE userId=$0; DELETE FROM recovery_codes WHERE userId=$0; DELETE FROM security_receipts WHERE userId=$0; DELETE FROM login_challenges WHERE userId=$0; UPDATE users SET version=version+1 WHERE id=$0", id);
        }
    }
}
