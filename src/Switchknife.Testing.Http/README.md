# Switchknife.Testing.Http

A stub `HttpMessageHandler` for tests of code that takes an `HttpClient`. It answers each request from a function and records it, so no test reaches the network. No dependency beyond the BCL and none on a test framework: it works the same under xUnit 2, xUnit v3 or anything else.

```csharp
var stub = new StubHttpHandler((request, body) => request.RequestUri!.AbsolutePath switch
{
    "/repos/o/r/pulls" => StubHttpHandler.Json("""{"number":7}""", HttpStatusCode.Created),
    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
});
var client = new PullRequests(stub.Client("https://api.example.test/"));

await client.OpenAsync("o", "r", "Title");

var (method, uri, body, headers) = Assert.Single(stub.Requests);
Assert.Equal(HttpMethod.Post, method);
Assert.Equal("/repos/o/r/pulls", uri.PathAndQuery);
Assert.Equal("Bearer the-token", headers["Authorization"]);
```

- `new StubHttpHandler((request, body) => response)`: the body is read once, before the function runs, and passed to it as a string (empty when the request has none). The function may throw to simulate a dropped connection.
- `new StubHttpHandler(async (request, body, ct) => response)`: for responses that wait, such as a hanging upstream that only the caller's timeout or cancellation ends.
- `StubHttpHandler.Sequence(reply1, reply2, ...)`: the n-th request gets the n-th reply; when they run out, the last one keeps answering (a poller that sees the same state again). Assert on `Requests.Count` to catch extra calls.
- `Requests`: every request in arrival order as `RecordedRequest(Method, Uri, Body, Headers)`, copied when it arrived, so it is still readable after the caller disposed the request and its content. `Headers` holds request and content headers, case-insensitive, repeated values joined with `", "`. Recording is thread-safe.
- `StubHttpHandler.Json(json, status = 200)`: a response with `application/json; charset=utf-8` content.
- `Client(baseAddress?)`: an `HttpClient` over the stub; disposing the client leaves the stub and its records alone.

Limits:

- `Body` is the content decoded as text (its charset, else UTF-8). Reading it buffers the content, so a test of a binary upload can still read `request.Content` as bytes inside the function.
- No routing table: route inside the function with a `switch` on method and path, as above.
