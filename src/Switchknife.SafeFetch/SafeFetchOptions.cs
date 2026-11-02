namespace Switchknife.SafeFetch;

/// <summary>Limits and lists for <see cref="GuardedFetcher"/>. The defaults are the ones feed-eater ran with.</summary>
public sealed record SafeFetchOptions
{
    /// <summary>Sent on every request, so a site owner can tell who is fetching.</summary>
    public string UserAgent { get; init; } = "Switchknife.SafeFetch";

    /// <summary>Page fetches to these hosts and their subdomains answer <see cref="FetchOutcome.Blocked"/> without a request. Feeds skip the list.</summary>
    public IReadOnlyCollection<string> BlockedHosts { get; init; } = [];

    /// <summary>
    /// Hosts the owner configured for feeds, which may be private and use any port. Only <see cref="GuardedFetcher.FetchFeedAsync"/>
    /// honours them; a page or redirect target named by fetched content never does.
    /// </summary>
    public IReadOnlyCollection<string> TrustedFeedHosts { get; init; } = [];

    /// <summary>Per request, started after the politeness wait.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Redirects followed before <see cref="FetchOutcome.TooManyRedirects"/>.</summary>
    public int MaxRedirects { get; init; } = 3;

    /// <summary>A page body is cut here.</summary>
    public int MaxPageBytes { get; init; } = 1024 * 1024;

    /// <summary>A feed body over this answers <see cref="FetchOutcome.TooLarge"/>.</summary>
    public int MaxFeedBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>The gap between two requests to one host.</summary>
    public TimeSpan HostGap { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a host whose page fetch failed answers <see cref="FetchOutcome.Cached"/>.</summary>
    public TimeSpan FailureMemory { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Expired failure and politeness entries are dropped once either table holds more than this many hosts.</summary>
    public int PruneAbove { get; init; } = 256;
}

/// <summary>A cap on page fetches, kept by the host (feed-eater keeps a count per day in its database, so a restart does not reset it).</summary>
public interface IFetchQuota
{
    /// <summary>Takes one fetch from the quota; false when it is used up. Called once per page fetch, never for feeds.</summary>
    ValueTask<bool> TryTakeAsync(CancellationToken ct);
}
