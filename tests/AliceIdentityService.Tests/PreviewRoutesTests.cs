using System.Net;
using System.Net.Http.Json;
using AliceIdentityService.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AliceIdentityService.Tests;

public sealed class PreviewFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
}

public sealed class PreviewRoutesTests(PreviewFactory factory) : IClassFixture<PreviewFactory>
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task DirectLoginIsServerRenderedWithoutTheReactBundle()
    {
        var html = await client.GetStringAsync("/Account/Login");
        Assert.Contains("One account.", html);
        Assert.Contains("Welcome back", html);
        Assert.Contains("/js/account.js", html);
        Assert.DoesNotContain("/admin/assets/", html);
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task OidcPreviewUsesTheCompactLayoutAndRetainsPreviewContext()
    {
        var html = await client.GetStringAsync("/Account/Login?preview=oidc&app=campus-portal");
        Assert.Contains("account-shell oidc", html);
        Assert.Contains("to continue to <strong>Campus Portal</strong>", html);
        Assert.DoesNotContain("One account.", html);
        Assert.Contains("/Account/Register?preview=oidc&amp;app=campus-portal", html);
        Assert.Contains("/Account/ForgotPassword?preview=oidc&amp;app=campus-portal", html);
    }

    [Theory]
    [InlineData("/Account/Register?preview=oidc&app=course-manager")]
    [InlineData("/Account/ForgotPassword?preview=oidc&app=course-manager")]
    public async Task OtherAccountPagesRetainCompactContext(string path)
    {
        var html = await client.GetStringAsync(path);
        Assert.Contains("account-shell oidc", html);
        Assert.Contains("Course Manager", html);
        Assert.Contains("/Account/Login?preview=oidc&amp;app=course-manager", html);
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
    public async Task InformationEndpointReturnsSharedApplicationMetadata()
    {
        var actual = await client.GetFromJsonAsync<ApplicationInfo>("/api/system/info");
        Assert.Equal(ApplicationInfo.Current, actual);
        Assert.Equal("Healthy", await client.GetStringAsync("/health"));
    }

    [Theory]
    [InlineData("/api/not-found")]
    [InlineData("/Account/not-found")]
    [InlineData("/connect/authorize")]
    public async Task UnknownServerRoutesDoNotReturnSpaHtml(string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
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
    public async Task BuiltAdminSupportsDeepLinks(string path)
    {
        var html = await client.GetStringAsync(path);
        Assert.Contains("/admin/assets/", html);
        Assert.Contains("id=\"root\"", html);
    }

    [Fact]
    public void ProductionCannotStartTheUnauthenticatedPreview()
    {
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        var error = Assert.Throws<InvalidOperationException>(() => production.CreateClient());
        Assert.Contains("development-only UI preview", error.Message);
    }
}
