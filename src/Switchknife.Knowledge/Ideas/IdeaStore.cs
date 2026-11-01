using Npgsql;
using Switchknife.Facts;

namespace Switchknife.Knowledge.Ideas;

/// <summary>The outcome of a status change: either the rejection codes (nothing written) or the ingest that wrote the fact set.</summary>
public sealed record IdeaMoveResult(IReadOnlyList<IdeaRejection> Rejections, IngestResult? Ingest)
{
    /// <summary>True when the fact set was written.</summary>
    public bool Accepted => Ingest is not null;
}

/// <summary>A capture: the idea and what the ingest did (<see cref="IngestResult.Duplicate"/> when the same source text was seen before).</summary>
public sealed record IdeaCapture(Guid Idea, IngestResult Ingest);

/// <summary>
/// The ideas chain over a <see cref="FactsStore"/> in namespace <c>ideas</c>. Capture has no gate; every later status
/// change passes <see cref="IdeaValidator"/> and is written as one episode, or not at all.
/// </summary>
public sealed partial class IdeaStore
{
    private readonly NpgsqlDataSource _data;
    private readonly FactsStore _facts;
    private readonly TimeProvider _time;

    /// <summary>Creates a store over a database the facts schema was applied to; call <see cref="InitializeAsync"/> once before use.</summary>
    /// <param name="data">The data source.</param>
    /// <param name="time">The clock for <c>valid_from</c> and the store's ingest time. Defaults to the system clock.</param>
    public IdeaStore(NpgsqlDataSource data, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _time = time ?? TimeProvider.System;
        _facts = new FactsStore(data, IdeaPredicates.Namespace, _time);
    }

    /// <summary>Defines the predicates of <see cref="IdeaPredicates.All"/>; safe to call on every start.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (name, cardinality) in IdeaPredicates.All)
        {
            await _facts.DefinePredicateAsync(name, cardinality, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a seed: the title and <c>status = seed</c>, no gate. The same <paramref name="externalRef"/> returns the same idea; capturing it
    /// again writes the title and repeats <c>status = seed</c> only while the idea is still a seed, so a later status set by the owner stays.
    /// </summary>
    /// <param name="title">The idea's text.</param>
    /// <param name="source">The episode the text came from.</param>
    /// <param name="externalRef">The consumer's id for the idea (an issue number); null makes a new idea each time.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<Guid> CaptureAsync(string title, NewEpisode source, string? externalRef = null, CancellationToken cancellationToken = default) =>
        (await CaptureCoreAsync(title, source, externalRef, cancellationToken).ConfigureAwait(false)).Idea;

    /// <summary>
    /// Writes the seed of a GitHub issue (<see cref="GitHubIssueAdapter"/>). The same text again is a <see cref="IngestResult.Duplicate"/>; an edited
    /// title or body retires the facts only the old text supported and writes the new title. Status, hypothesis and test written from other sources stay live.
    /// </summary>
    public Task<IdeaCapture> CaptureAsync(IssueSeed seed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return CaptureCoreAsync(seed.Title, seed.Episode, seed.ExternalRef, cancellationToken);
    }

    private async Task<IdeaCapture> CaptureCoreAsync(string title, NewEpisode source, string? externalRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(source);
        var idea = await _facts.EnsureEntityAsync(IdeaPredicates.IdeaKind, title, externalRef, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var facts = new List<NewFact> { new(idea, IdeaPredicates.Title, title, now) };
        var status = await _facts.AsOfAsync(idea, IdeaPredicates.Status, now, cancellationToken: cancellationToken).ConfigureAwait(false) is [var live, ..] ? live.Value : null;
        if (status is null or "seed")
        {
            facts.Add(Status(idea, IdeaStatus.Seed, now));
        }

        var ingest = await _facts.IngestAsync(source, facts, cancellationToken).ConfigureAwait(false);
        return new IdeaCapture(idea, ingest);
    }

    /// <summary>
    /// Moves <paramref name="idea"/> to <paramref name="draft"/>'s status. A draft that fails <see cref="IdeaValidator.Validate(IdeaStatus, IdeaDraft)"/>
    /// writes nothing and returns the codes. A valid one writes status and its fields in one episode; leaving
    /// <c>killed</c> retracts the <c>kill_reason</c> in a second step, so a failure between the two leaves a stale reason on a revived idea, not a lost one.
    /// </summary>
    public async Task<IdeaMoveResult> MoveAsync(Guid idea, IdeaDraft draft, NewEpisode source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(source);
        var now = _time.GetUtcNow();
        var current = await _facts.AsOfAsync(idea, null, now, cancellationToken: cancellationToken).ConfigureAwait(false);
        var from = current.FirstOrDefault(f => f.Predicate == IdeaPredicates.Status)?.Value is { } text ? ParseStatus(text) : (IdeaStatus?)null;
        if (from is null)
        {
            return new IdeaMoveResult([new IdeaRejection(IdeaRejectReason.Missing, "idea")], null);
        }

        var rejections = IdeaValidator.Validate(from.Value, draft);
        if (rejections.Count > 0)
        {
            return new IdeaMoveResult(rejections, null);
        }

        var facts = new List<NewFact> { Status(idea, draft.Status, now) };
        if (draft.Status == IdeaStatus.Killed)
        {
            facts.Add(new NewFact(idea, IdeaPredicates.KillReason, draft.KillReason, now));
        }
        else if (draft.Status != IdeaStatus.Seed)
        {
            facts.Add(new NewFact(idea, IdeaPredicates.ValueKind, draft.ValueKind!.Value == ValueKind.Money ? "money" : "people", now));
            facts.Add(new NewFact(idea, IdeaPredicates.ValueHypothesis, IdeaJson.Write(draft.Hypothesis!), now));
            facts.Add(new NewFact(idea, IdeaPredicates.CheapestTest, IdeaJson.Write(draft.Test!), now));
        }

        var ingest = await _facts.IngestAsync(source, facts, cancellationToken).ConfigureAwait(false);
        if (from == IdeaStatus.Killed && draft.Status != IdeaStatus.Killed
            && current.FirstOrDefault(f => f.Predicate == IdeaPredicates.KillReason) is { } reason)
        {
            await _facts.RetractAsync(reason.Id, cancellationToken).ConfigureAwait(false);
        }

        return new IdeaMoveResult([], ingest);
    }

    private static NewFact Status(Guid idea, IdeaStatus status, DateTimeOffset now) =>
        new(idea, IdeaPredicates.Status, StatusText(status), now);

    internal static string StatusText(IdeaStatus status) => status switch
    {
        IdeaStatus.Seed => "seed",
        IdeaStatus.Testing => "testing",
        IdeaStatus.Validated => "validated",
        IdeaStatus.Killed => "killed",
        IdeaStatus.Earning => "earning",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    internal static IdeaStatus? ParseStatus(string text) => text switch
    {
        "seed" => IdeaStatus.Seed,
        "testing" => IdeaStatus.Testing,
        "validated" => IdeaStatus.Validated,
        "killed" => IdeaStatus.Killed,
        "earning" => IdeaStatus.Earning,
        _ => null,
    };
}
