using System.Net;
using System.Net.Http.Json;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AliceIdentityService.Tests;

public class PreviewFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=ais3_unavailable;Username=test;Timeout=1;Command Timeout=1",
            ["OpenIddict:Issuer"] = "https://localhost/",
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:1:Name"] = "File",
            ["Serilog:WriteTo:1:Args:path"] = Path.Combine(Path.GetTempPath(), "ais3-http-test-logs", "ais-.log")
        }));
    }
}

public sealed class PreviewRoutesTests(PreviewFactory factory) : IClassFixture<PreviewFactory>
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    [Fact]
    public async Task DirectLoginIsServerRenderedWithoutTheReactBundle()
    {
        var html = await client.GetStringAsync("/Account/Login");
        Assert.Contains("One account.", html);
        Assert.Contains("Welcome back", html);
        Assert.Contains("/js/account.js", html);
        Assert.DoesNotContain("/admin/assets/", html);
        Assert.Contains("<form", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("autocomplete=\"current-password\"", html);
    }

    [Fact]
    public async Task OldPreviewParametersCannotEstablishOidcContext()
    {
        var html = await client.GetStringAsync("/Account/Login?preview=oidc&app=campus-portal");
        Assert.Contains("account-shell direct", html);
        Assert.DoesNotContain("Campus Portal", html);
    }

    [Theory]
    [InlineData("/Account/Register?preview=oidc&app=course-manager")]
    [InlineData("/Account/ForgotPassword?preview=oidc&app=course-manager")]
    public async Task UnvalidatedNamesDoNotEstablishContextOnPreviewPages(string path)
    {
        var html = await client.GetStringAsync(path);
        Assert.Contains("account-shell direct", html);
        Assert.DoesNotContain("Course Manager", html);
    }

    [Theory]
    [InlineData("/Account/Login?app=campus-portal")]
    [InlineData("/Account/Login?preview=oidc&app=unknown-client")]
    [InlineData("/Account/Login?preview=oidc&app=%3Cscript%3E")]
    public async Task UnrecognizedPreviewContextUsesTheDirectLayout(string path)
    {
        var html = await client.GetStringAsync(path);
        Assert.Contains("account-shell direct", html);
        Assert.DoesNotContain("account-shell oidc", html);
    }

    [Fact]
    public async Task InformationEndpointRequiresAnAdministrator()
    {
        var response = await client.GetAsync("/api/system/info");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("Healthy", await client.GetStringAsync("/health"));
    }

    [Theory]
    [InlineData("/api/not-found")]
    [InlineData("/Account/not-found")]
    [InlineData("/connect/not-found")]
    public async Task UnknownServerRoutesDoNotReturnSpaHtml(string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/Account/ForgotPassword")]
    public async Task PreviewDoesNotAcceptAccountSubmissions(string path)
    {
        var response = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "sample@example.com" }));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/")]
    [InlineData("/admin/users/alex")]
    [InlineData("/admin/index.html")]
    [InlineData("/admin/assets/example.js")]
    public async Task AdminRequiresSignInIncludingStaticFiles(string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/Account/Login?ReturnUrl=", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ReadinessFailsWithoutTheDatabaseWhileLivenessAndPreviewStillWork()
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Login")).StatusCode);
    }

    [Fact]
    public void ProductionCannotStartTheUnauthenticatedPreview()
    {
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        var error = Assert.Throws<InvalidOperationException>(() => production.CreateClient());
        Assert.Contains("development-only", error.Message);
    }

    [Fact]
    public async Task PlainHttpRedirectsToHttpsEvenWithAnHttpOnlyBackendListener()
    {
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await http.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https://localhost:7443/Account/Login", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task LoginRejectsMissingAntiforgeryTokenBeforeCheckingCredentials()
    {
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "sample@example.test",
            ["Password"] = "not-used"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
