namespace Switchknife.Testing.Http;

/// <summary>A request as <see cref="StubHttpHandler"/> saw it, copied before the caller could dispose it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The absolute request URI (the client's base address already applied).</param>
/// <param name="Body">The content read as a string; empty when the request had no content.</param>
/// <param name="Headers">
/// Request and content headers by name, case-insensitive; a header sent more than once has its values joined with ", ".
/// </param>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers);
