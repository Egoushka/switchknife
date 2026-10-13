using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Switchknife.McpGuard;

/// <summary>
/// Guards for an endpoint that only non-browser clients call, such as an MCP server on the streamable HTTP transport.
/// Apply one to what <c>MapMcp</c> returns, or to any endpoint or route group.
/// </summary>
public static class McpGuardExtensions
{
    /// <summary>
    /// Refuses a request that carries an <c>Origin</c> header with 403, then one whose <c>Authorization</c> header is not
    /// exactly <c>Bearer {token}</c> with 401, compared in fixed time. Fails closed: a null or empty
    /// <paramref name="token"/> refuses every request.
    /// </summary>
    /// <param name="builder">The endpoint or route group to guard.</param>
    /// <param name="token">The bearer token callers must send; null or empty when none is configured.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder RequireMcpGuard<TBuilder>(this TBuilder builder, string? token)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configured = !string.IsNullOrEmpty(token);
        var expected = Encoding.UTF8.GetBytes($"Bearer {token}");
        return Wrap(builder, (context, next) =>
        {
            if (HasOrigin(context))
            {
                return Refuse(context, StatusCodes.Status403Forbidden);
            }

            var given = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (!configured || !CryptographicOperations.FixedTimeEquals(given, expected))
            {
                return Refuse(context, StatusCodes.Status401Unauthorized);
            }

            return next(context);
        });
    }

    /// <summary>
    /// Refuses a request that carries an <c>Origin</c> header with 403. A browser sends one on every cross-origin
    /// request and on every POST, so this is the DNS-rebinding guard for an endpoint authenticated by other means.
    /// </summary>
    /// <param name="builder">The endpoint or route group to guard.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder RejectOrigin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Wrap(builder, (context, next) => HasOrigin(context) ? Refuse(context, StatusCodes.Status403Forbidden) : next(context));
    }

    private static TBuilder Wrap<TBuilder>(TBuilder builder, Func<HttpContext, RequestDelegate, Task> guard)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint =>
        {
            var next = endpoint.RequestDelegate
                ?? throw new InvalidOperationException($"Endpoint '{endpoint.DisplayName}' has no request delegate to guard.");
            endpoint.RequestDelegate = context => guard(context, next);
        });
        return builder;
    }

    private static bool HasOrigin(HttpContext context) => context.Request.Headers.ContainsKey("Origin");

    private static Task Refuse(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}
