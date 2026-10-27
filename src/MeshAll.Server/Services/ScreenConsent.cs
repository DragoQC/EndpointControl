using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public sealed class ScreenConsentException(string reason) : IOException("Screen access was not approved.")
{
    public string Reason { get; } = reason;
}

public sealed record ScreenConsentApproval(string Nonce, int TargetSession);

public static class ScreenConsent
{
    public static bool IsScreen(int protocol, SessionRequest request) =>
        request.Kind == "desktop" && protocol is 0 or 2 && request.Port == 0
        || request.Port > 0 && request.Kind is "rdp" or "vnc" or "amt";

    public static ScreenConsentApproval Validate(JsonElement response, SessionRequest request, string nonce, bool windows)
    {
        if (Text(response, "nonce") != nonce || Text(response, "sessionid") != request.SessionId || Text(response, "protocol") != request.Kind)
        {
            throw new ScreenConsentException("consent-unavailable");
        }

        if (!response.TryGetProperty("granted", out JsonElement granted) || granted.ValueKind != JsonValueKind.True)
        {
            throw new ScreenConsentException(Text(response, "reason") switch
            {
                "timeout" => "consent-timeout",
                "denied" => "consent-denied",
                _ => "consent-unavailable"
            });
        }
        if (!response.TryGetProperty("targetSession", out JsonElement session) || session.ValueKind != JsonValueKind.Number || !session.TryGetInt32(out int target) || target < 0 || windows && target == 0)
        {
            throw new ScreenConsentException("consent-unavailable");
        }

        return new(nonce, target);
    }

    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
}
