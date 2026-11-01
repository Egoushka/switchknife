using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Switchknife.SafeFetch;

/// <summary>How a fetch ended. Only <see cref="Ok"/> carries a body.</summary>
public enum FetchOutcome
{
    /// <summary>Fetched; <see cref="FetchResult.Body"/> holds the text.</summary>
    Ok,

    /// <summary>Scheme, port, credentials or address not allowed, before or after a redirect.</summary>
    Refused,

    /// <summary>The host is on <see cref="SafeFetchOptions.BlockedHosts"/>.</summary>
    Blocked,

    /// <summary>A page answered with a content type other than HTML or plain text.</summary>
    BadType,

    /// <summary>More than <see cref="SafeFetchOptions.MaxRedirects"/> redirects.</summary>
    TooManyRedirects,

    /// <summary>A non-success status, a network error or the timeout.</summary>
    Failed,

    /// <summary>The <see cref="IFetchQuota"/> is used up.</summary>
    CapReached,

    /// <summary>A page fetch to this host failed within <see cref="SafeFetchOptions.FailureMemory"/>; nothing was sent.</summary>
    Cached,

    /// <summary>A feed answered 304 to its validators.</summary>
    NotModified,

    /// <summary>A feed body over <see cref="SafeFetchOptions.MaxFeedBytes"/>.</summary>
    TooLarge,
}

/// <summary>The outcome of one fetch.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Body">The decoded text, for <see cref="FetchOutcome.Ok"/>.</param>
/// <param name="FinalUri">The URL after redirects.</param>
/// <param name="Detail">A short reason for a failure: a status code, an exception type, a content type.</param>
/// <param name="ETag">The feed's new ETag, to send back next time.</param>
/// <param name="LastModified">The feed's new Last-Modified, to send back next time.</param>
public sealed record FetchResult(FetchOutcome Outcome, string? Body = null, Uri? FinalUri = null, string? Detail = null, string? ETag = null, string? LastModified = null)
{
    /// <summary>True for <see cref="FetchOutcome.Ok"/>.</summary>
    public bool Ok => Outcome == FetchOutcome.Ok;
}

/// <summary>The validators of the last good fetch of a feed, sent back so an unchanged feed answers 304.</summary>
public sealed record FeedConditions(string? ETag = null, string? LastModified = null);

/// <summary>
/// Fetches a URL that untrusted input named. Build <paramref name="http"/> on <see cref="CreateHandler"/>, so the connection goes only to an address
/// <see cref="IpGuard"/> accepted. <paramref name="feedHttp"/> is the client for feed URLs the owner configured and is the only one whose handler may trust
/// <see cref="SafeFetchOptions.TrustedFeedHosts"/>; page and redirect URLs go through <paramref name="http"/>. Without it, <paramref name="http"/> does both.
/// </summary>
public sealed partial class GuardedFetcher(
    HttpClient http, SafeFetchOptions? options = null, IFetchQuota? quota = null, TimeProvider? time = null, ILogger? logger = null, HttpClient? feedHttp = null)
{
    private readonly SafeFetchOptions _options = options ?? new SafeFetchOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts held in the failure and politeness tables; bounded by <see cref="SafeFetchOptions.PruneAbove"/> plus the live ones.</summary>
    public int TrackedHosts => _failedUntil.Count + _nextAt.Count;

    /// <summary>The handler for the client: pinned connect, no redirects, no cookies, no proxy.</summary>
    public static SocketsHttpHandler CreateHandler(IReadOnlyCollection<string>? trustedHosts = null) => new()
    {
        ConnectCallback = (trustedHosts is { Count: > 0 } ? GuardedConnector.Default.Trusting(trustedHosts) : GuardedConnector.Default).ConnectAsync,
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>A page: HTML, XHTML or plain text, cut at <see cref="SafeFetchOptions.MaxPageBytes"/>, counted against the quota.</summary>
    public Task<FetchResult> FetchAsync(Uri uri, CancellationToken ct) => RunAsync(uri, null, ct);

    /// <summary>
    /// A feed: conditional GET, any content type, up to <see cref="SafeFetchOptions.MaxFeedBytes"/>, not counted against the quota and not on the
    /// blocked list. A failure does not park the host: the feed's own backoff decides when to ask again.
    /// </summary>
    public Task<FetchResult> FetchFeedAsync(Uri uri, FeedConditions conditions, CancellationToken ct) => RunAsync(uri, conditions, ct);

    private async Task<FetchResult> RunAsync(Uri uri, FeedConditions? feed, CancellationToken ct)
    {
        if (Check(uri, feed is not null) is { } refused)
        {
            return refused;
        }

        Prune();
        if (feed is null)
        {
            if (_failedUntil.TryGetValue(uri.Host, out var until) && until > _time.GetUtcNow())
            {
                return new FetchResult(FetchOutcome.Cached, Detail: "failed recently");
            }

            if (quota is not null && !await quota.TryTakeAsync(ct).ConfigureAwait(false))
            {
                return new FetchResult(FetchOutcome.CapReached);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await FollowAsync(uri, feed, timeout, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is UnsafeAddressException || ex.GetBaseException() is UnsafeAddressException)
        {
            return new FetchResult(FetchOutcome.Refused, Detail: ex.GetBaseException().Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or InvalidDataException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            LogFailed(_logger, ex, uri.Host);
            if (feed is null)
            {
                _failedUntil[uri.Host] = _time.GetUtcNow() + _options.FailureMemory;
            }

            return new FetchResult(FetchOutcome.Failed, Detail: ex.GetType().Name);
        }
    }

    /// <summary>The timeout runs per request and starts after the politeness wait, so queueing behind another fetch never fails a host.</summary>
    private async Task<FetchResult> FollowAsync(Uri start, FeedConditions? feed, CancellationTokenSource limit, CancellationToken caller)
    {
        var current = start;
        for (var hop = 0; ; hop++)
        {
            await GateAsync(current.Host, caller).ConfigureAwait(false);
            limit.CancelAfter(_options.Timeout);
            var ct = limit.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            if (feed is null)
            {
                request.Headers.Accept.ParseAdd("text/html,text/plain;q=0.9");
            }
            else
            {
                request.Headers.Accept.ParseAdd("application/rss+xml,application/atom+xml,application/feed+json,application/xml;q=0.9,text/xml;q=0.9,application/json;q=0.8,*/*;q=0.5");
                AddValidators(request, feed);
            }

            using var response = await (feed is null ? http : feedHttp ?? http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (hop >= _options.MaxRedirects)
                {
                    return new FetchResult(FetchOutcome.TooManyRedirects);
                }

                var next = new Uri(current, location);
                if (current.Scheme == "https" && next.Scheme == "http")
                {
                    return new FetchResult(FetchOutcome.Refused, Detail: "redirect from https to http");
                }

                current = next;
                if (Check(current, feed is not null) is { } refused)
                {
                    return refused;
                }

                continue;
            }

            if (feed is not null && response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchResult(FetchOutcome.NotModified, FinalUri: current);
            }

            if (!response.IsSuccessStatusCode)
            {
                if (feed is null)
                {
                    _failedUntil[current.Host] = _time.GetUtcNow() + _options.FailureMemory;
                }

                return new FetchResult(FetchOutcome.Failed, Detail: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            if (feed is not null)
            {
                var (text, over) = await ReadCappedAsync(response, _options.MaxFeedBytes, ct).ConfigureAwait(false);
                return over
                    ? new FetchResult(FetchOutcome.TooLarge)
                    : new FetchResult(FetchOutcome.Ok, text, current, ETag: Header(response, "ETag"), LastModified: Header(response, "Last-Modified"));
            }

            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("text/html" or "application/xhtml+xml" or "text/plain"))
            {
                return new FetchResult(FetchOutcome.BadType, Detail: type);
            }

            return new FetchResult(FetchOutcome.Ok, (await ReadCappedAsync(response, _options.MaxPageBytes, ct).ConfigureAwait(false)).Text, current);
        }
    }

    private static void AddValidators(HttpRequestMessage request, FeedConditions feed)
    {
        if (feed.ETag is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", feed.ETag);
        }

        if (feed.LastModified is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", feed.LastModified);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Private hosts are refused. A feed URL may name one in <see cref="SafeFetchOptions.TrustedFeedHosts"/>, which also lifts the port rule for it; page
    /// URLs never get that, because their links are not the owner's. Feeds skip the blocked list.
    /// </summary>
    private FetchResult? Check(Uri uri, bool feed)
    {
        var trusted = feed && _options.TrustedFeedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        if (uri.Scheme is not ("http" or "https") || (!trusted && uri.Port is not (80 or 443)) || uri.UserInfo.Length > 0)
        {
            return new FetchResult(FetchOutcome.Refused, Detail: "scheme, port or credentials");
        }

        if (!feed && _options.BlockedHosts.Any(b => uri.Host.Equals(b, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)))
        {
            return new FetchResult(FetchOutcome.Blocked);
        }

        return !trusted && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !IpGuard.IsPublic(literal)
            ? new FetchResult(FetchOutcome.Refused, Detail: "address")
            : null;
    }

    /// <summary>Reads at most <paramref name="max"/> bytes; Over says the body was longer.</summary>
    private static async Task<(string Text, bool Over)> ReadCappedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(max + 1);
            try
            {
                var read = 0;
                while (read <= max)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, max + 1 - read), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                Encoding encoding;
                try
                {
                    encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet ?? "utf-8");
                }
                catch (ArgumentException)
                {
                    encoding = Encoding.UTF8;
                }

                return (encoding.GetString(buffer, 0, Math.Min(read, max)), read > max);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Drops expired failure and politeness entries once the tables grow, so a long run over many hosts does not accumulate them.</summary>
    private void Prune()
    {
        var now = _time.GetUtcNow();
        if (_failedUntil.Count > _options.PruneAbove)
        {
            foreach (var (host, until) in _failedUntil)
            {
                if (until <= now)
                {
                    _failedUntil.TryRemove(host, out _);
                }
            }
        }

        lock (_nextAt)
        {
            if (_nextAt.Count > _options.PruneAbove)
            {
                foreach (var host in _nextAt.Where(e => e.Value <= now).Select(e => e.Key).ToList())
                {
                    _nextAt.Remove(host);
                }
            }
        }
    }

    /// <summary>One request per <see cref="SafeFetchOptions.HostGap"/> per host: the next slot is reserved up front, so concurrent callers queue.</summary>
    private async Task GateAsync(string host, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_nextAt)
        {
            var now = _time.GetUtcNow();
            var at = _nextAt.TryGetValue(host, out var next) && next > now ? next : now;
            _nextAt[host] = at + _options.HostGap;
            wait = at - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetching {Host} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string host);
}
