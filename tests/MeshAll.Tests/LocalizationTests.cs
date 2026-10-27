using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeshAll.Server.Localization;
using MeshAll.Server.Services;
using Xunit;

namespace MeshAll.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void FrenchResourcesCoverEveryEnglishKeyAndPreserveFormatArguments()
    {
        ResourceManager resources = new("MeshAll.Server.Resources.Localization.UiText", typeof(UiText).Assembly);
        ResourceSet french = resources.GetResourceSet(CultureInfo.GetCultureInfo("fr"), true, false)!;
        Regex placeholders = new(@"\{\d+(?:[^}]*)\}");

        foreach (string key in UiText.ResourceKeys)
        {
            string? translated = french.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(translated), key);
            string[] sourceArguments = placeholders.Matches(key).Select(match => match.Value).Order().ToArray();
            string[] translatedArguments = placeholders.Matches(translated!).Select(match => match.Value).Order().ToArray();
            Assert.Equal(sourceArguments, translatedArguments);
        }
    }

    [Fact]
    public void FrenchFormattingAndKnownMessagesRespectTheCurrentRequestCulture()
    {
        using CultureScope culture = new("fr");
        UiText text = new();
        Assert.Equal("Se connecter", text["Sign in"]);
        Assert.Equal("12,5 %", DisplayFormat.Percent(12.5));
        Assert.Equal("1,5 Gio", DisplayFormat.Bytes(1610612736));
        Assert.Equal("0 j 01 h 01 min", DisplayFormat.Uptime(3660));
        Assert.Equal("Rôle inconnu personnalisé", text.RoleName("Rôle inconnu personnalisé", false));
        Assert.Equal("Administrateur", text.RoleName("Administrator", true));
        Assert.Equal("Mon groupe", text.GroupName("Mon groupe", "custom"));
        Assert.Equal("Non attribué", text.GroupName("Unassigned", "default"));
        Assert.Equal("Commande", text.Message("command"));
        Assert.Equal("Disque", text["Disk"]);
        Assert.Equal("Saisissez une valeur de 1 à 70 caractères.", text.Message("Enter a value between 1 and 70 characters."));
        Assert.Equal("Sortie personnalisée du système", text.Message("Sortie personnalisée du système"));
        Assert.Equal("12.5", DisplayFormat.CssNumber(12.5));
    }

    [Theory]
    [InlineData("fr", "/login?failed=true", HttpStatusCode.Redirect)]
    [InlineData("en", "/account/security", HttpStatusCode.Redirect)]
    [InlineData("de", "/login", HttpStatusCode.BadRequest)]
    [InlineData("fr", "https://example.invalid/", HttpStatusCode.BadRequest)]
    [InlineData("fr", "//example.invalid/", HttpStatusCode.BadRequest)]
    [InlineData("fr", "/\\example.invalid/", HttpStatusCode.BadRequest)]
    public async Task LanguageSelectionIsPersistentAndOnlyRedirectsToLocalPaths(string language, string returnUrl, HttpStatusCode expected)
    {
        using TestServer server = new();
        using HttpClient browser = server.Browser();
        string html = await browser.GetStringAsync("/login");
        Match token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);
        using HttpResponseMessage response = await browser.PostAsync("/culture", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["culture"] = language,
            ["returnUrl"] = returnUrl
        }));
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Redirect)
        {
            Assert.Equal(returnUrl, response.Headers.Location?.OriginalString);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(".AspNetCore.Culture="));
            string localized = await browser.GetStringAsync("/login");
            Assert.Contains($"<html lang=\"{language}\">", localized);
            Assert.Contains(language == "fr" ? "Se connecter" : "Sign in", localized);
            Assert.DoesNotContain("Your devices. One clear view.", localized);
            Assert.DoesNotContain("fleet", localized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LanguageSelectionRequiresAntiforgeryAndWorksDuringRequiredFactorSetup()
    {
        using TestServer server = new();
        using HttpClient browser = server.Browser();
        using HttpResponseMessage rejected = await browser.PostAsync("/culture", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["culture"] = "fr",
            ["returnUrl"] = "/login"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        server.Store.AddUser("locale-setup", "Test-only-password-6238", "Admin");
        server.Store.Setting("requireTwoFactor", "true", true);
        await PostForm(browser, "/login", "/auth/login", new()
        {
            ["username"] = "locale-setup",
            ["password"] = "Test-only-password-6238"
        });
        using HttpResponseMessage selected = await PostForm(browser, "/account/security", "/culture",
            new()
            {
                ["culture"] = "fr",
                ["returnUrl"] = "/account/security"
            });
        Assert.Equal(HttpStatusCode.Redirect, selected.StatusCode);
        string security = await browser.GetStringAsync("/account/security");
        Assert.Contains("lang=\"fr\"", security);
        Assert.Contains("Configurer", security);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/audit/export")).StatusCode);
    }

    private static async Task<HttpResponseMessage> PostForm(HttpClient browser, string page, string endpoint, Dictionary<string, string> values)
    {
        string html = await browser.GetStringAsync(page);
        Match token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value);
        return await browser.PostAsync(endpoint, new FormUrlEncodedContent(values));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string culture)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
