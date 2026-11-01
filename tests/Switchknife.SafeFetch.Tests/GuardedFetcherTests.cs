using System.Net;
using Microsoft.Extensions.Time.Testing;
using Switchknife.Testing.Http;

namespace Switchknife.SafeFetch.Tests;

public sealed class GuardedFetcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly SafeFetchOptions Options = new() { UserAgent = "test-agent/1.0", BlockedHosts = ["facebook.com", "youtube.com"] };

    private static HttpResponseMessage Html(string body, string type = "text/html; charset=utf-8", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, type.Split(';')[0]) };

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static (GuardedFetcher Fetcher, StubHttpHandler Stub, FakeTimeProvider Time) Build(
        Func<HttpRequestMessage, HttpResponseMessage> answer, SafeFetchOptions? options = null, IFetchQuota? quota = null)
    {
        var time = new FakeTimeProvider(Now);
        var stub = new StubHttpHandler((request, _) => answer(request));
        return (new GuardedFetcher(stub.Client(), options ?? Options, quota, time), stub, time);
    }

    [Fact]
    public async Task Fetches_a_page_with_no_cookies_no_credentials_and_a_clear_user_agent()
    {
        var (fetcher, stub, _) = Build(_ => Html("<p>hello</p>"));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/post"), default);

        Assert.True(result.Ok);
        Assert.Equal("<p>hello</p>", result.Body);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("https://example.com/post", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Requests_carry_only_the_user_agent_and_accept_headers()
    {
        HttpRequestMessage? seen = null;
        var (fetcher, _, _) = Build(r =>
        {
            seen = r;
            return Html("x");
        });

        await fetcher.FetchAsync(new Uri("https://example.com/"), default);

        Assert.Contains("test-agent", seen!.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Null(seen.Headers.Authorization);
        Assert.False(seen.Headers.Contains("Cookie"));
        Assert.False(seen.Headers.Contains("Referer"));
        Assert.False(seen.Headers.Contains("X-Api-Key"));
    }

    [Theory]
    [InlineData("ftp://example.com/file", FetchOutcome.Refused)]
    [InlineData("file:///etc/passwd", FetchOutcome.Refused)]
    [InlineData("gopher://example.com/", FetchOutcome.Refused)]
    [InlineData("https://example.com:8443/", FetchOutcome.Refused)]
    [InlineData("http://example.com:22/", FetchOutcome.Refused)]
    [InlineData("https://user:pass@example.com/", FetchOutcome.Refused)]
    [InlineData("http://127.0.0.1/", FetchOutcome.Refused)]
    [InlineData("http://10.0.0.1/admin", FetchOutcome.Refused)]
    [InlineData("http://100.64.1.7:80/", FetchOutcome.Refused)]
    [InlineData("http://169.254.169.254/latest/meta-data/", FetchOutcome.Refused)]
    [InlineData("http://[::1]/", FetchOutcome.Refused)]
    [InlineData("http://[fd00::1]/", FetchOutcome.Refused)]
    [InlineData("https://facebook.com/x", FetchOutcome.Blocked)]
    [InlineData("https://m.facebook.com/x", FetchOutcome.Blocked)]
    [InlineData("https://www.youtube.com/watch?v=1", FetchOutcome.Blocked)]
    public async Task Refuses_before_any_request(string url, FetchOutcome expected)
    {
        var (fetcher, stub, _) = Build(_ => Html("secret"));

        var result = await fetcher.FetchAsync(new Uri(url), default);

        Assert.Equal(expected, result.Outcome);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Does_not_block_a_host_that_only_ends_with_a_blocked_name()
    {
        var (fetcher, _, _) = Build(_ => Html("ok"));

        Assert.True((await fetcher.FetchAsync(new Uri("https://notfacebook.com/"), default)).Ok);
    }

    [Fact]
    public async Task Nothing_is_blocked_by_default()
    {
        var (fetcher, _, _) = Build(_ => Html("ok"), new SafeFetchOptions());

        Assert.True((await fetcher.FetchAsync(new Uri("https://facebook.com/"), default)).Ok);
    }

    [Fact]
    public async Task Follows_up_to_three_redirects_and_then_gives_up()
    {
        var (three, _, time) = Build(r => r.RequestUri!.AbsolutePath switch
        {
            "/a" => Redirect("/b"),
            "/b" => Redirect("https://other.example/c"),
            "/c" => Redirect("/d"),
            _ => Html("done"),
        });
        var task = three.FetchAsync(new Uri("https://example.com/a"), default);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        var ok = await task;
        Assert.True(ok.Ok);
        Assert.Equal("https://other.example/d", ok.FinalUri!.ToString());

        var (loop, stub, time2) = Build(_ => Redirect("/again"));
        var looping = loop.FetchAsync(new Uri("https://example.com/start"), default);
        while (!looping.IsCompleted)
        {
            time2.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        Assert.Equal(FetchOutcome.TooManyRedirects, (await looping).Outcome);
        Assert.Equal(4, stub.Requests.Count);   // the start and three redirects
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://example.com:8443/x")]
    [InlineData("https://facebook.com/login")]
    [InlineData("http://example.com/downgraded")]
    public async Task A_redirect_to_a_refused_target_is_refused_and_not_followed(string target)
    {
        var (fetcher, stub, _) = Build(r => r.RequestUri!.AbsolutePath == "/start" ? Redirect(target) : Html("internal secret"));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/start"), default);

        Assert.True(result.Outcome is FetchOutcome.Refused or FetchOutcome.Blocked);
        Assert.Null(result.Body);
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    [InlineData("application/json")]
    public async Task Only_html_and_plain_text_are_read(string type)
    {
        var (fetcher, _, _) = Build(_ => Html("bytes", type));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/file"), default);

        Assert.Equal(FetchOutcome.BadType, result.Outcome);
        Assert.Null(result.Body);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xhtml+xml")]
    public async Task Plain_text_and_xhtml_are_read(string type) =>
        Assert.True((await Build(_ => Html("fine", type)).Fetcher.FetchAsync(new Uri("https://example.com/"), default)).Ok);

    [Fact]
    public async Task The_body_is_cut_at_one_megabyte()
    {
        var (fetcher, _, _) = Build(_ => Html(new string('a', 3 * 1024 * 1024)));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/big"), default);

        Assert.True(result.Ok);
        Assert.Equal(1024 * 1024, result.Body!.Length);
    }

    [Fact]
    public async Task A_failing_host_is_remembered_for_a_day_and_not_retried()
    {
        var (fetcher, stub, time) = Build(_ => Html("oops", status: HttpStatusCode.InternalServerError));

        Assert.Equal(FetchOutcome.Failed, (await fetcher.FetchAsync(new Uri("https://flaky.example/a"), default)).Outcome);
        Assert.Equal(FetchOutcome.Cached, (await fetcher.FetchAsync(new Uri("https://flaky.example/b"), default)).Outcome);
        Assert.Single(stub.Requests);

        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(FetchOutcome.Failed, (await fetcher.FetchAsync(new Uri("https://flaky.example/c"), default)).Outcome);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task A_transport_error_and_a_timeout_are_failures_not_exceptions()
    {
        var (broken, _, _) = Build(_ => throw new HttpRequestException("connection reset"));
        Assert.Equal(FetchOutcome.Failed, (await broken.FetchAsync(new Uri("https://reset.example/"), default)).Outcome);

        using var client = new HttpClient(new HangingHandler());
        var hanging = new GuardedFetcher(client, new SafeFetchOptions { Timeout = TimeSpan.FromMilliseconds(150) }, time: new FakeTimeProvider(Now));
        Assert.Equal(FetchOutcome.Failed, (await hanging.FetchAsync(new Uri("https://slow.example/"), default)).Outcome);
    }

    [Fact]
    public async Task The_callers_own_cancellation_throws_instead_of_failing_the_host()
    {
        using var client = new HttpClient(new HangingHandler());
        var fetcher = new GuardedFetcher(client, time: new FakeTimeProvider(Now));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(new Uri("https://slow.example/"), cts.Token));
        Assert.Equal(1, fetcher.TrackedHosts);   // the politeness slot only, no failure entry
    }

    [Fact]
    public async Task A_refused_address_from_the_connector_surfaces_as_refused()
    {
        var (fetcher, _, _) = Build(_ => throw new HttpRequestException("blocked", new UnsafeAddressException("evil.example")));

        Assert.Equal(FetchOutcome.Refused, (await fetcher.FetchAsync(new Uri("https://evil.example/"), default)).Outcome);
    }

    [Fact]
    public async Task One_request_a_second_per_host()
    {
        var (fetcher, stub, time) = Build(_ => Html("x"));

        await fetcher.FetchAsync(new Uri("https://same.example/1"), default);
        var second = fetcher.FetchAsync(new Uri("https://same.example/2"), default);
        var other = await fetcher.FetchAsync(new Uri("https://different.example/1"), default);   // another host does not wait

        // The second fetch waits on a fake-clock timer, so it cannot have been sent until the clock moves.
        Assert.True(other.Ok);
        Assert.False(second.IsCompleted);
        Assert.Equal(2, stub.Requests.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await second).Ok);
    }

    [Fact]
    public async Task Expired_entries_are_pruned_once_the_tables_pass_the_threshold()
    {
        var (fetcher, _, time) = Build(_ => Html("oops", status: HttpStatusCode.InternalServerError), Options with { PruneAbove = 2 });
        foreach (var host in new[] { "a.example", "b.example", "c.example" })
        {
            await fetcher.FetchAsync(new Uri($"https://{host}/"), default);
        }

        Assert.Equal(6, fetcher.TrackedHosts);   // three failures, three politeness slots

        time.Advance(TimeSpan.FromHours(25));
        await fetcher.FetchAsync(new Uri("https://d.example/"), default);

        Assert.Equal(2, fetcher.TrackedHosts);   // only d: its failure and its slot
    }

    [Fact]
    public async Task A_used_up_quota_stops_page_fetches_before_any_request()
    {
        var quota = new CountingQuota(2);
        var (fetcher, stub, _) = Build(_ => Html("x"), quota: quota);

        Assert.True((await fetcher.FetchAsync(new Uri("https://a.example/"), default)).Ok);
        Assert.True((await fetcher.FetchAsync(new Uri("https://b.example/"), default)).Ok);
        Assert.Equal(FetchOutcome.CapReached, (await fetcher.FetchAsync(new Uri("https://c.example/"), default)).Outcome);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Refused_blocked_and_cached_fetches_take_no_quota()
    {
        var quota = new CountingQuota(10);
        var (fetcher, _, _) = Build(_ => Html("oops", status: HttpStatusCode.InternalServerError), quota: quota);

        await fetcher.FetchAsync(new Uri("http://10.0.0.1/"), default);
        await fetcher.FetchAsync(new Uri("https://facebook.com/"), default);
        await fetcher.FetchAsync(new Uri("https://flaky.example/a"), default);
        await fetcher.FetchAsync(new Uri("https://flaky.example/b"), default);

        Assert.Equal(1, quota.Taken);
    }

    private sealed class CountingQuota(int limit) : IFetchQuota
    {
        public int Taken { get; private set; }

        public ValueTask<bool> TryTakeAsync(CancellationToken ct)
        {
            if (Taken >= limit)
            {
                return ValueTask.FromResult(false);
            }

            Taken++;
            return ValueTask.FromResult(true);
        }
    }

    internal sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
