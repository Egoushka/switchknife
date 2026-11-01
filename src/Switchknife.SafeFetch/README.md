# Switchknife.SafeFetch

Fetches a URL that untrusted input named (a link in a feed item, a recipe URL a user pasted, a redirect target) without letting it reach the host's own network. Taken from feed-eater, where it has fetched articles and feeds in production.

The guard has two layers. `GuardedFetcher` checks the URL before any request and again at every redirect: `http` or `https` only, port 80 or 443, no credentials in the URL, no private address literal, at most three redirects, never `https` to `http`. `GuardedConnector`, the handler's connect callback, then resolves the name once, refuses the whole connection if any answer is not a global unicast address (`IpGuard`), and connects to the address it checked, so a second DNS answer cannot rebind the fetch to an internal address.

```csharp
var http = new HttpClient(GuardedFetcher.CreateHandler());
var fetcher = new GuardedFetcher(http, new SafeFetchOptions { UserAgent = "my-app/1.0 (+https://example.com/bot)" });

var page = await fetcher.FetchAsync(new Uri(link), ct);
if (page.Ok)
{
    var text = ReadableText.Extract(page.Body!, maxChars: 20_000);
}
```

Every outcome is a `FetchResult`, never an exception for a bad URL, a refused address, an error status or a timeout; only the caller's own cancellation throws. `Outcome` says which: `Ok`, `Refused`, `Blocked`, `BadType`, `TooManyRedirects`, `Failed`, `CapReached`, `Cached`, `NotModified`, `TooLarge`.

Pages and feeds are held to different rules:

| | `FetchAsync` (page) | `FetchFeedAsync` (feed) |
|---|---|---|
| Content type | HTML, XHTML or plain text, else `BadType` | any |
| Body cap | cut at `MaxPageBytes` (1 MB) | over `MaxFeedBytes` (5 MB) is `TooLarge` |
| Conditional GET | no | sends `FeedConditions` (ETag, Last-Modified); 304 is `NotModified`, 200 returns the new validators |
| `BlockedHosts` (and subdomains) | `Blocked` | ignored |
| `IFetchQuota` | one taken per fetch; none left is `CapReached` | never taken |
| A failure | parks the host: `Cached` for `FailureMemory` (24 h) | parks nothing; the caller's backoff decides |
| `TrustedFeedHosts` | never honoured | may be private and use any port |

Both wait for one request per `HostGap` (1 s) per host; the next slot is reserved up front, so concurrent callers queue instead of bursting. The `Timeout` (10 s) starts after that wait.

A host that polls feeds from its own network (a feed bridge on a private address) gives `FetchFeedAsync` a second client whose handler trusts those hosts, and lists them in `TrustedFeedHosts`:

```csharp
var feeds = new HttpClient(GuardedFetcher.CreateHandler(trustedHosts: ["feeds.internal"]));
var fetcher = new GuardedFetcher(http, options with { TrustedFeedHosts = ["feeds.internal"] }, quota, feedHttp: feeds);
```

Give the trusting client only hosts the owner configured. Page fetches and redirects always go through the first client, so a link in fetched content can never reach a trusted host.

`IFetchQuota` is a cap kept by the host, for example a count per day in its database so a restart does not reset it. Without one there is no cap.

`HtmlText.ToPlain` flattens HTML to lines; `ReadableText.Extract` keeps the title and prefers `article`, then `main`, then `body`, dropping navigation, headers, footers, forms and scripts. Both cut input at 256 KB and return empty text for markup that would time a regular expression out.

Limits:

- In-memory politeness and failure tables, per instance: register one `GuardedFetcher` per process. Expired entries are pruned once a table passes `PruneAbove` hosts.
- No robots.txt and no JavaScript rendering.
- The connector trusts the system resolver's answers; it judges the addresses, not the resolver.
