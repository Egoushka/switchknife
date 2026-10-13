using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Switchknife.McpGuard.Tests;

public sealed class McpGuardExtensionsTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // The shape MapMcp returns: a route group whose endpoints take a RequestDelegate.
        RequestDelegate reached = context => context.Response.WriteAsync("reached");
        var mcp = _app.MapGroup("/mcp");
        mcp.MapPost("", reached);
        mcp.MapGet("", reached);
        mcp.RequireMcpGuard(Token);

        _app.MapPost("/unset", reached).RequireMcpGuard(null);
        _app.MapPost("/empty", reached).RequireMcpGuard("");
        _app.MapPost("/origin-only", reached).RejectOrigin();
        // Route handlers get their delegate after conventions run; the guard must still wrap it.
        var handlers = _app.MapGroup("/handlers").RequireMcpGuard(Token);
        handlers.MapGet("/{id}", (string id) => $"reached {id}");

        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? authorization, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await _http.SendAsync(request);
    }

    [Fact]
    public async Task The_right_token_without_an_origin_reaches_every_endpoint_of_the_group()
    {
        var post = await SendAsync(HttpMethod.Post, "/mcp", $"Bearer {Token}");
        var get = await SendAsync(HttpMethod.Get, "/mcp", $"Bearer {Token}");

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("reached", await post.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer nope")]
    [InlineData("Bearer test-token-and-more")]
    [InlineData("bearer test-token")]
    [InlineData("test-token")]
    [InlineData("Basic dGVzdC10b2tlbg==")]
    public async Task A_missing_or_wrong_token_is_401(string? authorization)
    {
        var response = await SendAsync(HttpMethod.Post, "/mcp", authorization);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost")]
    [InlineData("null")]
    public async Task Any_origin_header_is_403_even_with_the_right_token(string origin)
    {
        var response = await SendAsync(HttpMethod.Post, "/mcp", $"Bearer {Token}", origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_origin_is_checked_before_the_token()
    {
        var response = await SendAsync(HttpMethod.Post, "/mcp", null, "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/unset")]
    [InlineData("/empty")]
    public async Task Without_a_configured_token_every_request_is_refused(string path)
    {
        var none = await SendAsync(HttpMethod.Post, path, null);
        var bare = await SendAsync(HttpMethod.Post, path, "Bearer ");
        var any = await SendAsync(HttpMethod.Post, path, $"Bearer {Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, any.StatusCode);
    }

    [Fact]
    public async Task Reject_origin_alone_needs_no_token_and_refuses_any_origin()
    {
        var plain = await SendAsync(HttpMethod.Post, "/origin-only", null);
        var browser = await SendAsync(HttpMethod.Post, "/origin-only", null, "https://evil.example");

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);
    }

    [Fact]
    public async Task A_route_handler_group_is_guarded_and_still_binds_its_parameters()
    {
        var refused = await SendAsync(HttpMethod.Get, "/handlers/7", null);
        var reached = await SendAsync(HttpMethod.Get, "/handlers/7", $"Bearer {Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("reached 7", await reached.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_null_builder_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => McpGuardExtensions.RequireMcpGuard<IEndpointConventionBuilder>(null!, Token));
        Assert.Throws<ArgumentNullException>(() => McpGuardExtensions.RejectOrigin<IEndpointConventionBuilder>(null!));
    }
}
