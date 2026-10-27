using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace MeshAll.Server.Services;

public sealed record RdpDefaultsSummary(string Domain, string Username, bool HasPassword);

/// <summary>Decrypted credentials stay in the authenticated connection/editor state.</summary>
public sealed class RdpCredentials(string domain, string username, string password)
{
    public string Domain { get; } = domain;
    public string Username { get; } = username;
    [JsonIgnore]
    public string Password { get; } = password;

    public override string ToString() => nameof(RdpCredentials);
}

public sealed partial class Store
{
    public RdpDefaultsSummary? RdpDefaults(UserInfo user)
    {
        RequireRdpPermission(user);
        return Query(
            "SELECT domain,username,password IS NOT NULL FROM user_rdp_defaults WHERE userId=$0",
            reader => new RdpDefaultsSummary(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)),
            user.Id).SingleOrDefault();
    }

    public RdpCredentials? ReadRdpCredentials(UserInfo user)
    {
        RequireRdpPermission(user);
        StoredRdpDefaults? saved = Query(
            "SELECT domain,username,password FROM user_rdp_defaults WHERE userId=$0",
            reader => new StoredRdpDefaults(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)),
            user.Id).SingleOrDefault();

        if (saved is null)
        {
            return null;
        }

        try
        {
            string password = saved.ProtectedPassword is null ? ""
                : connectionPreferencesProtector.CreateProtector(user.Id).Unprotect(saved.ProtectedPassword);
            return new RdpCredentials(saved.Domain, saved.Username, password);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The saved RDP password could not be read. Save your RDP credentials again.");
        }
    }

    public void SaveRdpCredentials(UserInfo user, string domain, string username, string password)
    {
        RequireRdpPermission(user);
        domain = domain.Trim();
        username = username.Trim();
        if (domain.Length > 256 || string.IsNullOrWhiteSpace(username) || username.Length > 256 || password.Length > 512)
        {
            throw new InvalidOperationException("Enter an RDP username and keep each credential field within its size limit.");
        }

        string? protectedPassword = password.Length == 0 ? null
            : connectionPreferencesProtector.CreateProtector(user.Id).Protect(password);

        Execute("""
            INSERT INTO user_rdp_defaults(userId,domain,username,password) VALUES($0,$1,$2,$3)
            ON CONFLICT(userId) DO UPDATE SET domain=$1,username=$2,password=$3
            """, user.Id, domain, username, protectedPassword);
        Audit(user.Name, "RDP defaults saved", user.Name);
    }

    public void ClearRdpCredentials(UserInfo user)
    {
        RequireRdpPermission(user);
        Execute("DELETE FROM user_rdp_defaults WHERE userId=$0", user.Id);
        Audit(user.Name, "RDP defaults cleared", user.Name);
    }

    private void RequireRdpPermission(UserInfo user)
    {
        UserInfo? current = FindUser(user.Id);
        if (current is null || current.Disabled || current.Version != user.Version || !HasPermission(current, Permissions.Rdp))
        {
            throw new UnauthorizedAccessException("Your account does not have permission for this action.");
        }
    }

    private sealed record StoredRdpDefaults(string Domain, string Username, string? ProtectedPassword)
    {
        public override string ToString() => nameof(StoredRdpDefaults);
    }
}
