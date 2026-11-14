using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notes.Api.Endpoints;

namespace Notes.Api.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class DemoEndpointsTests
{
    private static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
    {
        ["Alice"] = new('a', 64),
        ["Bob"] = new('b', 64),
        ["Carol"] = new('c', 64),
        ["Dana"] = new('d', 64)
    };

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Browser_keys_are_off_by_default_and_do_not_require_key_configuration(bool? enabled)
    {
        await using var app = await StartAsync(enabled, includeKeys: false);
        using var client = app.GetTestClient();
        using var request = BrowserRequest();
        using var response = await client.SendAsync(request);
        await AssertHiddenAsync(response);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST:8080")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("[::1]:8080")]
    public async Task Enabled_local_browser_receives_only_the_four_configured_keys(string host)
    {
        await using var app = await StartAsync(true);
        using var client = app.GetTestClient();
        using var request = BrowserRequest(host: host);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("keys", Assert.Single(json.RootElement.EnumerateObject()).Name);
        var actual = json.RootElement.GetProperty("keys");
        Assert.Equal(4, actual.EnumerateObject().Count());
        foreach (var (name, key) in Keys)
        {
            Assert.Equal(key, actual.GetProperty(name).GetString());
        }

        Assert.DoesNotContain("database-configuration-must-stay-private", body);
        Assert.DoesNotContain("extra-key-must-not-be-returned", body);
    }

    [Theory]
    [InlineData(null, "same-origin")]
    [InlineData("false", "same-origin")]
    [InlineData("TRUE", "same-origin")]
    [InlineData("true, true", "same-origin")]
    [InlineData("true", null)]
    [InlineData("true", "none")]
    [InlineData("true", "same-site")]
    [InlineData("true", "cross-site")]
    [InlineData("true", "same-origin, same-origin")]
    public async Task Browser_signals_are_required_and_must_be_exact(string? marker, string? site)
    {
        await using var app = await StartAsync(true);
        using var client = app.GetTestClient();
        using var request = BrowserRequest(marker: marker, site: site);
        using var response = await client.SendAsync(request);
        await AssertHiddenAsync(response);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("localhost.example.com")]
    [InlineData("127.0.0.1.example.com")]
    [InlineData("192.168.0.10")]
    [InlineData("0.0.0.0")]
    [InlineData("localhost.")]
    public async Task Noncanonical_or_nonloopback_hosts_cannot_read_keys(string host)
    {
        await using var app = await StartAsync(true);
        using var client = app.GetTestClient();
        using var request = BrowserRequest(host: host);
        using var response = await client.SendAsync(request);
        await AssertHiddenAsync(response);
    }

    [Theory]
    [InlineData("http://localhost", true)]
    [InlineData("http://LOCALHOST", true)]
    [InlineData("https://localhost", false)]
    [InlineData("http://localhost:8080", false)]
    [InlineData("http://example.com", false)]
    [InlineData("null", false)]
    [InlineData("http://localhost/path", false)]
    public async Task An_explicit_origin_must_match_the_request_origin(string origin, bool allowed)
    {
        await using var app = await StartAsync(true);
        using var client = app.GetTestClient();
        using var request = BrowserRequest();
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            await AssertHiddenAsync(response);
        }
    }

    [Fact]
    public async Task An_explicit_empty_origin_is_rejected()
    {
        await using var app = await StartAsync(true);
        // HttpClient omits empty headers, so set this one directly on the test request.
        var context = await app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/demo/keys";
            context.Request.Headers["X-Notes-Demo"] = "true";
            context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
            context.Request.Headers.Origin = "";
            Assert.True(context.Request.Headers.ContainsKey("Origin"));
        });
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("X-Notes-Demo", "true")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    [InlineData("Origin", "http://localhost")]
    public async Task Duplicate_browser_headers_cannot_read_keys(string header, string value)
    {
        await using var app = await StartAsync(true);
        using var client = app.GetTestClient();
        using var request = BrowserRequest();
        request.Headers.Remove(header);
        request.Headers.TryAddWithoutValidation(header, new[] { value, value });
        using var response = await client.SendAsync(request);
        await AssertHiddenAsync(response);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Other_methods_never_return_keys(string method)
    {
        await using var app = await StartAsync(true);
        using var client = app.GetTestClient();
        using var request = BrowserRequest();
        request.Method = new HttpMethod(method);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await AssertNoKeysAsync(response);
    }

    [Fact]
    public async Task Enabling_without_valid_keys_fails_before_serving_requests()
    {
        await using var app = CreateApp(true, includeKeys: false);
        var error = Assert.Throws<InvalidOperationException>(() => app.MapDemoApi());
        Assert.Contains("Demo:ApiKeys:Alice", error.Message);
    }

    [Fact]
    public async Task Enabling_with_duplicate_keys_fails_before_serving_requests()
    {
        await using var app = CreateApp(true);
        app.Configuration["Demo:ApiKeys:Bob"] = Keys["Alice"];
        var error = Assert.Throws<InvalidOperationException>(() => app.MapDemoApi());
        Assert.Contains("unique", error.Message);
    }

    private static HttpRequestMessage BrowserRequest(
        string host = "localhost",
        string? marker = "true",
        string? site = "same-origin")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/demo/keys");
        request.Headers.Host = host;
        if (marker is not null) request.Headers.TryAddWithoutValidation("X-Notes-Demo", marker);
        if (site is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
        return request;
    }

    private static WebApplication CreateApp(bool? enabled, bool includeKeys = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development"
        });
        builder.Configuration.Sources.Clear();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notes"] = "database-configuration-must-stay-private",
            ["Demo:ApiKeys:Extra"] = "extra-key-must-not-be-returned"
        };
        if (enabled.HasValue) settings["Demo:EnableBrowserKeys"] = enabled.Value.ToString();
        if (includeKeys)
        {
            foreach (var (name, key) in Keys) settings[$"Demo:ApiKeys:{name}"] = key;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.Logging.ClearProviders();
        return builder.Build();
    }

    private static async Task<WebApplication> StartAsync(bool? enabled, bool includeKeys = true)
    {
        var app = CreateApp(enabled, includeKeys);
        app.MapDemoApi();
        await app.StartAsync();
        return app;
    }

    private static async Task AssertHiddenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertNoKeysAsync(response);
    }

    private static async Task AssertNoKeysAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        foreach (var key in Keys.Values) Assert.DoesNotContain(key, body);
    }
}
