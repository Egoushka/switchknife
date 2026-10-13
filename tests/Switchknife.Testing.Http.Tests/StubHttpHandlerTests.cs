using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Switchknife.Testing.Http.Tests;

public sealed class StubHttpHandlerTests
{
    [Fact]
    public async Task Answers_from_the_function_with_the_body_already_read()
    {
        string? seenBody = null;
        var stub = new StubHttpHandler((request, body) =>
        {
            seenBody = body;
            return StubHttpHandler.Json($$"""{"path":"{{request.RequestUri!.AbsolutePath}}"}""", HttpStatusCode.Created);
        });
        using var http = stub.Client("https://api.example.test/");

        using var response = await http.PostAsync("items", new StringContent("""{"name":"a"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("""{"path":"/items"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("""{"name":"a"}""", seenBody);
    }

    [Fact]
    public async Task Records_method_uri_body_and_headers_after_the_caller_disposed_the_request()
    {
        var stub = new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = stub.Client("https://api.example.test/v1/");

        using (var request = new HttpRequestMessage(HttpMethod.Put, "things/7?force=true"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "t0k");
            request.Headers.Add("Prefer", "respond-async");
            request.Headers.TryAddWithoutValidation("X-Multi", ["a", "b"]);
            request.Content = new StringContent("payload", Encoding.UTF8, "text/plain");
            using var response = await http.SendAsync(request);
        }

        var (method, uri, body, headers) = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("https://api.example.test/v1/things/7?force=true", uri.ToString());
        Assert.Equal("/v1/things/7?force=true", uri.PathAndQuery);
        Assert.Equal("payload", body);
        Assert.Equal("Bearer t0k", headers["authorization"]);
        Assert.Equal("respond-async", headers["Prefer"]);
        Assert.Equal("a, b", headers["X-Multi"]);
        Assert.Equal("text/plain; charset=utf-8", headers["Content-Type"]);
    }

    [Fact]
    public async Task A_request_without_content_records_an_empty_body()
    {
        string? seenBody = null;
        var stub = new StubHttpHandler((_, body) =>
        {
            seenBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = stub.Client("https://api.example.test/");

        using var response = await http.GetAsync("items");

        Assert.Equal("", seenBody);
        var seen = Assert.Single(stub.Requests);
        Assert.Equal("", seen.Body);
        Assert.Null(seen.Headers.GetValueOrDefault("Content-Type"));
    }

    [Fact]
    public async Task A_throwing_function_fails_the_call_and_the_request_is_still_recorded()
    {
        var stub = new StubHttpHandler((_, _) => throw new HttpRequestException("Connection reset"));
        using var http = stub.Client("https://api.example.test/");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("items"));

        Assert.Equal("Connection reset", error.Message);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task The_async_function_gets_the_cancellation_token_so_a_hanging_upstream_times_out()
    {
        var stub = new StubHttpHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = stub.Client("https://api.example.test/");
        http.Timeout = TimeSpan.FromMilliseconds(50);

        await Assert.ThrowsAsync<TaskCanceledException>(() => http.GetAsync("slow"));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task The_function_can_still_read_the_content_as_bytes()
    {
        byte[]? bytes = null;
        var stub = new StubHttpHandler(async (request, _, ct) =>
        {
            bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = stub.Client("https://api.example.test/");

        using var response = await http.PostAsync("upload", new StreamContent(new MemoryStream([1, 2, 255])));

        Assert.Equal([1, 2, 255], bytes);
    }

    [Fact]
    public async Task A_sequence_answers_in_order_then_repeats_the_last_reply()
    {
        var stub = StubHttpHandler.Sequence(
            _ => StubHttpHandler.Json("""{"n":1}"""),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            request => StubHttpHandler.Json($$"""{"last":"{{request.RequestUri!.AbsolutePath}}"}"""));
        using var http = stub.Client("https://api.example.test/");

        var statuses = new List<string>();
        foreach (var path in new[] { "a", "b", "c", "d" })
        {
            using var response = await http.GetAsync(path);
            statuses.Add($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Equal(["200 {\"n\":1}", "503 ", "200 {\"last\":\"/c\"}", "200 {\"last\":\"/d\"}"], statuses);
        Assert.Equal(["/a", "/b", "/c", "/d"], stub.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public void A_sequence_needs_a_reply() =>
        Assert.Throws<ArgumentException>(() => StubHttpHandler.Sequence());

    [Fact]
    public async Task Concurrent_requests_are_all_recorded()
    {
        var stub = new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = stub.Client("https://api.example.test/");

        await Task.WhenAll(Enumerable.Range(0, 200).Select(async i =>
        {
            using var response = await http.PostAsync("n", new StringContent(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }));

        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(), stub.Requests.Select(r => r.Body).Order());
    }

    [Fact]
    public async Task Disposing_a_client_leaves_the_handler_usable()
    {
        var stub = new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using (var first = stub.Client("https://api.example.test/"))
        {
            using var response = await first.GetAsync("one");
        }

        using var second = stub.Client();
        using var again = await second.GetAsync(new Uri("https://other.example.test/two"));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(["https://api.example.test/one", "https://other.example.test/two"], stub.Requests.Select(r => r.Uri.ToString()));
    }
}
