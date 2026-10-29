using System.Globalization;

namespace Switchknife.Pace;

/// <summary>How one consumer is paced.</summary>
/// <param name="Share">Its share of the week, 0 to 1.</param>
/// <param name="Action">What to answer when it is over its pace line. Null means <see cref="Decision.Hold"/>. The owner has none.</param>
/// <param name="OnStale">What to answer when the meter is stale. Null means <see cref="Decision.Hold"/>. The owner has none.</param>
public sealed record ConsumerPolicy(double Share, Decision? Action = null, Decision? OnStale = null);

/// <summary>The rules the ledger runs by. Build it from a file; <see cref="Validate"/> before use.</summary>
public sealed record Policy
{
    /// <summary>Consumers by name. Shares sum to at most 1; the owner's share is the part automation may not touch.</summary>
    public required IReadOnlyDictionary<string, ConsumerPolicy> Consumers { get; init; }

    /// <summary>The consumer that is measured and never gated. It may be absent from <see cref="Consumers"/>.</summary>
    public string Owner { get; init; } = "owner";

    /// <summary>Whether <see cref="Ledger.Admit"/> may refuse.</summary>
    public PaceMode Mode { get; init; } = PaceMode.Observe;

    /// <summary>A meter reading older than this makes the state stale.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How long an admitted job's reservation lasts without a report.</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromHours(2);

    /// <summary>A multiplier on a model's reported cost, by model name. Unlisted models weigh 1.</summary>
    public IReadOnlyDictionary<string, double> ModelWeights { get; init; } = new Dictionary<string, double>();

    /// <summary>What is wrong with this policy; empty when it can run.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Owner))
        {
            problems.Add("owner must be named");
        }

        double total = 0;
        foreach (var (name, consumer) in Consumers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add("a consumer has an empty name");
            }

            if (double.IsNaN(consumer.Share) || consumer.Share < 0 || consumer.Share > 1)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"consumer '{name}': share {consumer.Share} is outside 0 to 1"));
            }
            else
            {
                total += consumer.Share;
            }

            if (name == Owner && (consumer.Action is not null || consumer.OnStale is not null))
            {
                problems.Add($"consumer '{name}' is the owner, who is never gated: remove its action and on_stale");
            }

            foreach (var (label, value) in new[] { ("action", consumer.Action), ("on_stale", consumer.OnStale) })
            {
                if (value is { } d && d is not (Decision.Hold or Decision.Api or Decision.Cheap))
                {
                    problems.Add($"consumer '{name}': {label} must be hold, api or cheap");
                }
            }
        }

        if (total > 1 + 1e-9)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"shares sum to {total:0.####}, past 1"));
        }

        if (StaleAfter <= TimeSpan.Zero)
        {
            problems.Add("stale_after must be positive");
        }

        if (LeaseTtl <= TimeSpan.Zero)
        {
            problems.Add("lease_ttl must be positive");
        }

        foreach (var (model, weight) in ModelWeights)
        {
            if (double.IsNaN(weight) || weight < 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"model '{model}': weight {weight} is negative"));
            }
        }

        return problems;
    }

    internal double TotalShare => Consumers.Values.Sum(c => c.Share);

    internal double Weight(string model) => ModelWeights.TryGetValue(model, out var w) ? w : 1.0;
}
