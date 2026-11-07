using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AwesomeAssertions;
using DiscordChatExporter.Web.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class AccessGateTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public AccessGateTests(WebAppFactory factory) => _factory = factory;

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Api_without_cookie_is_unauthorized()
    {
        var client = NoRedirectClient();
        var response = await client.GetAsync("/api/session");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Key_query_sets_cookie_and_redirects()
    {
        var client = NoRedirectClient();
        var response = await client.GetAsync($"/?key={WebAppFactory.TestKey}");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response
            .Headers.GetValues("Set-Cookie")
            .Should()
            .Contain(c => c.Contains(AccessGate.CookieName));
    }

    [Fact]
    public async Task Wrong_key_is_forbidden()
    {
        var client = NoRedirectClient();
        var response = await client.GetAsync("/?key=wrong");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Authenticated_request_with_cookie_succeeds()
    {
        var client = NoRedirectClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Cookie", $"{AccessGate.CookieName}={WebAppFactory.TestKey}");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task State_changing_request_without_header_is_forbidden()
    {
        var client = NoRedirectClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session");
        request.Headers.Add("Cookie", $"{AccessGate.CookieName}={WebAppFactory.TestKey}");
        request.Content = new StringContent(
            """{"token":"x"}""",
            System.Text.Encoding.UTF8,
            "application/json"
        );
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cross_origin_state_change_is_forbidden()
    {
        var client = NoRedirectClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session");
        request.Headers.Add("Cookie", $"{AccessGate.CookieName}={WebAppFactory.TestKey}");
        request.Headers.Add(AccessGate.RequestHeader, "1");
        request.Headers.Add("Origin", "https://evil.example");
        request.Content = new StringContent(
            """{"token":"x"}""",
            System.Text.Encoding.UTF8,
            "application/json"
        );
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Files_traversal_is_rejected()
    {
        var client = NoRedirectClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/files/nonexistent/../../etc/passwd");
        request.Headers.Add("Cookie", $"{AccessGate.CookieName}={WebAppFactory.TestKey}");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Index_is_served()
    {
        var client = NoRedirectClient();
        var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("id=\"app\"");
    }
}

public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    public const string TestKey = "test-access-key-0123456789abcdef";

    public WebAppFactory()
    {
        Environment.SetEnvironmentVariable("DCE_ACCESS_KEY", TestKey);
        Environment.SetEnvironmentVariable(
            "DCE_DATA_DIR",
            Path.Combine(Path.GetTempPath(), "dce-test-" + Guid.NewGuid().ToString("N"))
        );
    }
}
