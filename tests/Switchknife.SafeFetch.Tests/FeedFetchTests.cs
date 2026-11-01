using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using Switchknife.Testing.Http;

namespace Switchknife.SafeFetch.Tests;

public sealed class FeedFetchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private const string Xml = "<rss version=\"2.0\"><channel><title>t</title></channel></rss>";

    private static readonly SafeFetchOptions Options = new() { UserAgent = "test-agent/1.0", BlockedHosts = ["youtube.com"] };

    private static HttpResponseMessage Ok(string body = Xml, string? etag = null, DateTimeOffset? modified = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/rss+xml") };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        response.Content.Headers.LastModified = modified;
        return response;
    }

    private static (GuardedFetcher Fetcher, StubHttpHandler Stub, FakeTimeProvider Time) Build(
        Func<HttpRequestMessage, HttpResponseMessage> answer, SafeFetchOptions? options = null, IFetchQuota? quota = null)
    {
        var time = new FakeTimeProvider(Now);
        var stub = new StubHttpHandler((request, _) => answer(request));
        return (new GuardedFetcher(stub.Client(), options ?? Options, quota, time), stub, time);
    }

    [Fact]
    public async Task The_validators_are_sent_and_a_304_answers_not_modified()
    {
        HttpRequestMessage? seen = null;
        var (fetcher, _, _) = Build(r =>
        {
            seen = r;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        var result = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions("\"v1\"", "Tue, 06 Oct 2026 10:00:00 GMT"), default);

        Assert.Equal(FetchOutcome.NotModified, result.Outcome);
        Assert.Null(result.Body);
        Assert.Equal("\"v1\"", Assert.Single(seen!.Headers.GetValues("If-None-Match")));
        Assert.Equal("Tue, 06 Oct 2026 10:00:00 GMT", Assert.Single(seen.Headers.GetValues("If-Modified-Since")));
    }

    [Fact]
    public async Task A_changed_feed_returns_the_body_and_the_new_validators()
    {
        var modified = new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero);
        var (fetcher, _, _) = Build(_ => Ok(etag: "\"v2\"", modified: modified));

        var result = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions("\"v1\""), default);

        Assert.True(result.Ok);
        Assert.Equal(Xml, result.Body);
        Assert.Equal("\"v2\"", result.ETag);
        Assert.Equal("Tue, 06 Oct 2026 11:00:00 GMT", result.LastModified);
    }

    [Fact]
    public async Task A_first_fetch_sends_no_validators()
    {
        HttpRequestMessage? seen = null;
        var (fetcher, _, _) = Build(r =>
        {
            seen = r;
            return Ok();
        });

        await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);

        Assert.False(seen!.Headers.Contains("If-None-Match"));
        Assert.False(seen.Headers.Contains("If-Modified-Since"));
        Assert.Contains("rss+xml", seen.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_followed_and_reported()
    {
        var (fetcher, stub, _) = Build(r => r.RequestUri!.Host == "old.example"
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://new.example/feed.xml") } }
            : Ok());

        var result = await fetcher.FetchFeedAsync(new Uri("https://old.example/feed"), new FeedConditions(), default);

        Assert.True(result.Ok);
        Assert.Equal("https://new.example/feed.xml", result.FinalUri!.AbsoluteUri);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task A_404_fails_the_feed_without_parking_the_host_for_pages()
    {
        var (fetcher, stub, time) = Build(r => r.RequestUri!.AbsolutePath == "/feed" ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<p>page</p>", System.Text.Encoding.UTF8, "text/html"),
        });

        var feed = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);
        time.Advance(TimeSpan.FromSeconds(2));
        var again = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);
        time.Advance(TimeSpan.FromSeconds(2));
        var page = await fetcher.FetchAsync(new Uri("https://example.com/post"), default);

        Assert.Equal(FetchOutcome.Failed, feed.Outcome);
        Assert.Equal("404", feed.Detail);
        Assert.Equal(FetchOutcome.Failed, again.Outcome);
        Assert.True(page.Ok);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task A_page_failure_still_parks_the_host_for_pages()
    {
        var (fetcher, _, time) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await fetcher.FetchAsync(new Uri("https://example.com/post"), default);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(FetchOutcome.Cached, (await fetcher.FetchAsync(new Uri("https://example.com/other"), default)).Outcome);
    }

    [Fact]
    public async Task A_body_over_5_MB_is_refused_and_one_of_exactly_5_MB_is_read()
    {
        const int Cap = 5 * 1024 * 1024;
        var (fetcher, _, time) = Build(r => Ok(new string('a', r.RequestUri!.AbsolutePath == "/huge" ? Cap + 1 : Cap)));

        var huge = await fetcher.FetchFeedAsync(new Uri("https://example.com/huge"), new FeedConditions(), default);
        time.Advance(TimeSpan.FromSeconds(2));
        var edge = await fetcher.FetchFeedAsync(new Uri("https://example.com/edge"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.TooLarge, huge.Outcome);
        Assert.Null(huge.Body);
        Assert.True(edge.Ok);
        Assert.Equal(Cap, edge.Body!.Length);
    }

    [Theory]
    [InlineData("http://10.0.0.5/feed.xml")]
    [InlineData("http://127.0.0.1/feed.xml")]
    [InlineData("http://100.64.1.7/feed.xml")]
    [InlineData("http://[::1]/feed.xml")]
    [InlineData("https://example.com:8443/feed.xml")]
    public async Task A_private_host_or_odd_port_is_refused_before_any_request(string url)
    {
        var (fetcher, stub, _) = Build(_ => Ok());

        var result = await fetcher.FetchFeedAsync(new Uri(url), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Refused, result.Outcome);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("http://10.0.0.5/feed.xml")]
    [InlineData("http://10.0.0.5:1200/feed.xml")]
    [InlineData("http://feeds.internal:1200/feed.xml")]
    public async Task A_trusted_feed_host_may_be_private_and_use_any_port(string url)
    {
        var (fetcher, stub, _) = Build(_ => Ok(), Options with { TrustedFeedHosts = ["10.0.0.5", "Feeds.internal"] });

        var result = await fetcher.FetchFeedAsync(new Uri(url), new FeedConditions(), default);

        Assert.True(result.Ok);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task TrustedFeedHosts_names_only_the_listed_host()
    {
        var (fetcher, stub, _) = Build(_ => Ok(), Options with { TrustedFeedHosts = ["10.0.0.5"] });

        var result = await fetcher.FetchFeedAsync(new Uri("http://10.0.0.6/feed.xml"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Refused, result.Outcome);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("http://10.0.0.5/page")]
    [InlineData("http://10.0.0.5:5432/")]
    [InlineData("http://feeds.internal:5432/")]
    public async Task TrustedFeedHosts_never_lifts_a_rule_for_an_article_or_linked_page_fetch(string url)
    {
        var (fetcher, stub, _) = Build(_ => Ok(), Options with { TrustedFeedHosts = ["10.0.0.5", "feeds.internal"] });

        var result = await fetcher.FetchAsync(new Uri(url), default);

        Assert.Equal(FetchOutcome.Refused, result.Outcome);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_redirect_from_an_article_to_a_trusted_private_host_is_refused()
    {
        var redirect = new Func<HttpRequestMessage, HttpResponseMessage>(r => r.RequestUri!.Host == "public.example"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://10.0.0.5/secret") } }
            : Ok("internal"));
        var (fetcher, stub, _) = Build(redirect, Options with { TrustedFeedHosts = ["10.0.0.5"] });

        var result = await fetcher.FetchAsync(new Uri("https://public.example/post"), default);

        Assert.Equal(FetchOutcome.Refused, result.Outcome);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Feed_fetches_use_the_feed_client_and_every_other_fetch_the_plain_one()
    {
        var feedStub = new StubHttpHandler((_, _) => Ok());
        var pageStub = new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p>page</p>", System.Text.Encoding.UTF8, "text/html") });
        var fetcher = new GuardedFetcher(pageStub.Client(), Options, time: new FakeTimeProvider(Now), feedHttp: feedStub.Client());

        // Two hosts: the fake clock never passes the one-a-second wait between requests to one host.
        Assert.True((await fetcher.FetchFeedAsync(new Uri("https://feeds.example/feed"), new FeedConditions(), default)).Ok);
        Assert.True((await fetcher.FetchAsync(new Uri("https://example.com/post"), default)).Ok);

        Assert.Equal(["https://feeds.example/feed"], feedStub.Requests.Select(c => c.Uri.AbsoluteUri));
        Assert.Equal(["https://example.com/post"], pageStub.Requests.Select(c => c.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task A_redirect_from_a_feed_to_a_private_host_is_refused_unless_it_is_allowed()
    {
        var redirect = new Func<HttpRequestMessage, HttpResponseMessage>(r => r.RequestUri!.Host == "public.example"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://10.0.0.5/secret") } }
            : Ok("internal"));
        var (refusing, refusedCalls, _) = Build(redirect);
        var (allowing, allowedCalls, _) = Build(redirect, Options with { TrustedFeedHosts = ["10.0.0.5"] });

        var refused = await refusing.FetchFeedAsync(new Uri("https://public.example/feed"), new FeedConditions(), default);
        var allowed = await allowing.FetchFeedAsync(new Uri("https://public.example/feed"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Refused, refused.Outcome);
        Assert.Single(refusedCalls.Requests);
        Assert.True(allowed.Ok);
        Assert.Equal(2, allowedCalls.Requests.Count);
    }

    [Fact]
    public async Task Feeds_are_not_held_to_the_blocked_list_or_the_daily_page_cap()
    {
        var (fetcher, stub, _) = Build(_ => Ok(), quota: new NoQuota());

        var feed = await fetcher.FetchFeedAsync(new Uri("https://www.youtube.com/feeds/videos.xml?channel_id=x"), new FeedConditions(), default);
        var page = await fetcher.FetchAsync(new Uri("https://example.com/post"), default);

        Assert.True(feed.Ok);
        Assert.Equal(FetchOutcome.CapReached, page.Outcome);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task A_corrupt_compressed_body_is_a_failed_fetch_not_an_exception()
    {
        var (fetcher, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new System.IO.Compression.GZipStream(new MemoryStream("not gzip"u8.ToArray()), System.IO.Compression.CompressionMode.Decompress)),
        });

        var result = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Equal(nameof(InvalidDataException), result.Detail);
    }

    [Fact]
    public async Task A_feed_of_any_content_type_is_returned()
    {
        var (fetcher, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml, System.Text.Encoding.UTF8, "application/octet-stream") });

        Assert.True((await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default)).Ok);
    }

    [Fact]
    public async Task Only_the_feed_client_can_connect_to_a_trusted_private_host()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    _ = await stream.ReadAsync(new byte[4096], stop.Token);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi"u8.ToArray(), stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
            {
                // stopped
            }
        });
        using var feedClient = new HttpClient(GuardedFetcher.CreateHandler(["127.0.0.1"]));
        using var pageClient = new HttpClient(GuardedFetcher.CreateHandler());
        var url = $"http://127.0.0.1:{port}/";

        using var ok = await feedClient.GetAsync(url);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => pageClient.GetAsync(url));

        Assert.Equal("hi", await ok.Content.ReadAsStringAsync());
        Assert.IsType<UnsafeAddressException>(refused.GetBaseException());
        await stop.CancelAsync();
        await serving;
    }

    private sealed class NoQuota : IFetchQuota
    {
        public ValueTask<bool> TryTakeAsync(CancellationToken ct) => ValueTask.FromResult(false);
    }
}
