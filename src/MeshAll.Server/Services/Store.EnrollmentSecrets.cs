using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace MeshAll.Server.Services;

/// <summary>Only authenticated enrollment actions receive decrypted key material.</summary>
public sealed class EnrollmentKeyDetails(TokenInfo information, string? key, string? nativeKey, bool nativeOnly)
{
    public TokenInfo Information { get; } = information;
    [JsonIgnore] public string? Key { get; } = key;
    [JsonIgnore] public string? NativeKey { get; } = nativeKey;
    public bool NativeOnly { get; } = nativeOnly;
    public bool CanDownload => NativeKey is not null && Information.Remaining > 0 && Information.Expires > DateTimeOffset.UtcNow;
    public EnrollmentKeyDetails WithInformation(TokenInfo information) => new(information, Key, NativeKey, NativeOnly);
    public override string ToString() => nameof(EnrollmentKeyDetails);
}

public sealed partial class Store
{
    public EnrollmentKeyDetails ReadEnrollmentKey(UserInfo actor, string id)
    {
        RequireCurrentPermission(actor, Permissions.Enroll);
        TokenInfo information = FindEnrollmentToken(id) ?? throw new InvalidOperationException("Enrollment key not found.");
        (string? Secret, string? Native) saved = Query("SELECT secret,nativeSecret FROM enrollment_secrets WHERE tokenId=$0",
            reader => (Secret: reader.IsDBNull(0) ? null : reader.GetString(0), Native: reader.IsDBNull(1) ? null : reader.GetString(1)), id).SingleOrDefault();
        try
        {
            IDataProtector protector = enrollmentKeysProtector.CreateProtector(id);
            if (saved.Secret is not null)
            {
                string token = protector.Unprotect(saved.Secret);
                if (Scalar<long>("SELECT EXISTS(SELECT 1 FROM tokens WHERE id=$0 AND hash=$1)", id, Hash(token)) == 0)
                {
                    throw new CryptographicException();
                }
                return new(information, token, NativeMeshKey(token), false);
            }
            if (saved.Native is not null)
            {
                string native = protector.CreateProtector("native").Unprotect(saved.Native);
                if (!MatchesNativeEnrollmentKey(id, native))
                {
                    throw new CryptographicException();
                }
                return new(information, native, native, true);
            }
            return new(information, null, null, false);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The saved enrollment key could not be read.");
        }
    }

    public void RecoverNativeEnrollmentKey(UserInfo actor, string id, string nativeKey)
    {
        lock (gate)
        {
            RequireCurrentPermission(actor, Permissions.Enroll);
            if (FindEnrollmentToken(id) is null || !MatchesNativeEnrollmentKey(id, nativeKey))
            {
                throw new InvalidOperationException("The installer does not match this enrollment key.");
            }
            string secret = enrollmentKeysProtector.CreateProtector(id).CreateProtector("native").Protect(nativeKey);
            Execute("INSERT INTO enrollment_secrets(tokenId,nativeSecret) VALUES($0,$1) ON CONFLICT(tokenId) DO UPDATE SET nativeSecret=$1", id, secret);
        }
    }

    private bool MatchesNativeEnrollmentKey(string id, string nativeKey) => nativeKey.Length == 96 && nativeKey.All(char.IsAsciiHexDigit)
        && Scalar<long>("SELECT EXISTS(SELECT 1 FROM native_enrollments WHERE tokenId=$0 AND meshHash=$1)", id, Hash(nativeKey)) != 0;
}
