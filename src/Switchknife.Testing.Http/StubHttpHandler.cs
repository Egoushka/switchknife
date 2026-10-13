using System.Net;
using System.Text;

namespace Switchknife.Testing.Http;

/// <summary>
/// An <see cref="HttpMessageHandler"/> for tests: answers each request from a function and records it, so no test reaches
/// the network. The request body is read before the function runs and is passed to it.
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>Answers every request with <paramref name="respond"/>(request, body); it may throw to simulate a failure.</summary>
    public StubHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _respond = (request, body, _) => Task.FromResult(respond(request, body));
    }

    /// <summary>
    /// Answers every request with <paramref name="respond"/>(request, body, cancellationToken), for responses that wait:
    /// a hanging upstream, a timeout, a cancellation mid-flight.
    /// </summary>
    public StubHttpHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _respond = respond;
    }

    /// <summary>Every request so far, in the order they arrived. A snapshot: safe to read while requests run.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>
    /// Answers the n-th request with the n-th reply; once they run out, the last one answers every further request.
    /// Assert on <see cref="Requests"/> to catch extra calls.
    /// </summary>
    public static StubHttpHandler Sequence(params Func<HttpRequestMessage, HttpResponseMessage>[] replies)
    {
        ArgumentNullException.ThrowIfNull(replies);
        if (replies.Length == 0)
            throw new ArgumentException("At least one reply is required.", nameof(replies));

        var next = 0;
        return new StubHttpHandler((request, _) => replies[Math.Min(Interlocked.Increment(ref next) - 1, replies.Length - 1)](request));
    }

    /// <summary>A response carrying <paramref name="json"/> as <c>application/json; charset=utf-8</c>.</summary>
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A client sending through this handler; disposing it leaves the handler and its records intact.</summary>
    public HttpClient Client(string? baseAddress = null) =>
        new(this, disposeHandler: false) { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Content is null ? request.Headers : request.Headers.Concat(request.Content.Headers))
            headers[name] = string.Join(", ", values);

        lock (_requests)
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, headers));

        return await _respond(request, body, cancellationToken).ConfigureAwait(false);
    }
}
