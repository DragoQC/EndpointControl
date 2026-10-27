using System.Text.Json;
using MeshAll.Server.Services;
using MeshAll.Shared;
using Xunit;

namespace MeshAll.Tests;

public sealed class ScreenConsentTests
{
    private static readonly SessionRequest Request = new("session", "admin", true);

    [Theory]
    [InlineData(0, "desktop", 0, true)]
    [InlineData(2, "desktop", 0, true)]
    [InlineData(5, "desktop", 0, false)]
    [InlineData(1, "terminal", 0, false)]
    [InlineData(6, "powershell", 0, false)]
    [InlineData(0, "rdp", 3389, true)]
    [InlineData(0, "vnc", 5900, true)]
    public void ScreenApprovalDoesNotAccidentallyApplyToFileOrTerminalTools(int protocol, string kind, int port, bool expected) =>
        Assert.Equal(expected, ScreenConsent.IsScreen(protocol, Request with
        {
            Kind = kind,
            Port = port
        }));

    [Fact]
    public void ExplicitApprovalRequiresMatchingNonceSessionProtocolAndInteractiveSession()
    {
        JsonElement response = Wire.Element(new
        {
            nonce = "nonce",
            sessionid = "session",
            protocol = "desktop",
            granted = true,
            targetSession = 2
        });
        Assert.Equal(new ScreenConsentApproval("nonce", 2), ScreenConsent.Validate(response, Request, "nonce", true));
        Assert.Throws<ScreenConsentException>(() => ScreenConsent.Validate(response, Request, "other", true));
        Assert.Throws<ScreenConsentException>(() => ScreenConsent.Validate(response, Request with { SessionId = "other" }, "nonce", true));
        Assert.Throws<ScreenConsentException>(() => ScreenConsent.Validate(response, Request with { Kind = "rdp" }, "nonce", true));
        Assert.Throws<ScreenConsentException>(() => ScreenConsent.Validate(Wire.Element(new { nonce = "nonce", sessionid = "session", protocol = "desktop", granted = true, targetSession = 0 }), Request, "nonce", true));
    }

    [Theory]
    [InlineData("denied", "consent-denied")]
    [InlineData("timeout", "consent-timeout")]
    [InlineData("unavailable", "consent-unavailable")]
    public void FailedApprovalNeverOpensATunnelAndHasATerminalReason(string reason, string expected)
    {
        ScreenConsentException error = Assert.Throws<ScreenConsentException>(() => ScreenConsent.Validate(
            Wire.Element(new
            {
                nonce = "nonce",
                sessionid = "session",
                protocol = "desktop",
                granted = false,
                reason
            }), Request, "nonce", true));
        Assert.Equal(expected, error.Reason);
    }
}
