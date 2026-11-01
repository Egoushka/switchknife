using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Switchknife.Facts;

namespace Switchknife.Knowledge.Ideas;

public sealed partial class IdeaStore
{
    // The earliest live fact of each link under one idea, and the UAH earned under its results.
    private const string ChainSql = """
        with tested as (
            select f.object_id as experiment, lower(f.valid) as at from facts.facts f
            where f.namespace = $1 and f.subject_id = $2 and f.predicate = 'tested_by' and f.retired_at is null),
        produced as (
            select f.object_id as result, lower(f.valid) as at from facts.facts f join tested t on f.subject_id = t.experiment
            where f.namespace = $1 and f.predicate = 'produced' and f.retired_at is null),
        earned as (
            select f.object_value::numeric as uah, lower(f.valid) as at from facts.facts f join produced p on f.subject_id = p.result
            where f.namespace = $1 and f.predicate = 'earned' and f.retired_at is null)
        select
            (select f.object_value from facts.facts f
             where f.namespace = $1 and f.subject_id = $2 and f.predicate = 'status' and f.retired_at is null and f.valid @> $3
             order by lower(f.valid) desc limit 1),
            (select min(lower(f.valid)) from facts.facts f
             where f.namespace = $1 and f.object_id = $2 and f.predicate = 'inspired' and f.retired_at is null),
            (select min(at) from tested),
            (select min(at) from produced),
            (select min(at) from earned),
            (select coalesce(sum(uah), 0) from earned)
        """;

    private static readonly string[] ChainLinks = [IdeaPredicates.Inspired, IdeaPredicates.TestedBy, IdeaPredicates.Produced, IdeaPredicates.Earned];

    /// <summary>Records that <paramref name="from"/> (an idea or other entity of this namespace) inspired <paramref name="idea"/>.</summary>
    public Task<IngestResult> RecordInspirationAsync(Guid from, Guid idea, NewEpisode source, CancellationToken cancellationToken = default) =>
        Link(from, IdeaPredicates.Inspired, idea, "inspired an idea", source, cancellationToken);

    /// <summary>Starts an experiment on <paramref name="idea"/>: a new experiment entity and the <c>tested_by</c> link to it.</summary>
    /// <returns>The experiment.</returns>
    public async Task<Guid> StartExperimentAsync(Guid idea, NewEpisode source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var experiment = await _facts.EnsureEntityAsync(IdeaPredicates.ExperimentKind, $"test of {idea:N}", cancellationToken: cancellationToken).ConfigureAwait(false);
        await Link(idea, IdeaPredicates.TestedBy, experiment, "tested by an experiment", source, cancellationToken).ConfigureAwait(false);
        return experiment;
    }

    /// <summary>Records what <paramref name="experiment"/> showed: a new result entity, the <c>produced</c> event, its outcome and, when given, the metric and a note.</summary>
    /// <remarks>The same <c>externalRef</c> (an approvals item id) returns the same result, so a retry adds no entity; null makes a new one.</remarks>
    /// <returns>The result.</returns>
    public async Task<Guid> RecordResultAsync(
        Guid experiment,
        ResultOutcome outcome,
        NewEpisode source,
        decimal? metricValue = null,
        string? note = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = await _facts.EnsureEntityAsync(IdeaPredicates.ResultKind, $"result of {experiment:N}", externalRef, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var facts = new List<NewFact>
        {
            new(experiment, IdeaPredicates.Produced, null, now, Text: "produced a result", ObjectEntity: result),
            new(result, IdeaPredicates.Outcome, OutcomeText(outcome), now),
        };
        if (metricValue is { } metric)
        {
            facts.Add(new NewFact(result, IdeaPredicates.MetricValue, metric.ToString(CultureInfo.InvariantCulture), now));
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            facts.Add(new NewFact(result, IdeaPredicates.Note, note, now));
        }

        await _facts.IngestAsync(source, facts, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Records UAH earned by <paramref name="result"/> on <paramref name="on"/>: an <c>earned</c> event, the only fact the board counts as income.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="uah"/> is not positive.</exception>
    public Task<IngestResult> RecordEarnedAsync(Guid result, decimal uah, DateTimeOffset on, NewEpisode source, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(uah);
        ArgumentNullException.ThrowIfNull(source);
        var value = uah.ToString(CultureInfo.InvariantCulture);
        return _facts.IngestAsync(source, [new NewFact(result, IdeaPredicates.Earned, value, on, Text: $"earned {value} UAH")], cancellationToken);
    }

    /// <summary>The chain of <paramref name="idea"/> in one query: the links reached, days in each, UAH earned.</summary>
    public async Task<IdeaChain> ChainAsync(Guid idea, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var command = _data.CreateCommand(ChainSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = IdeaPredicates.Namespace });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = idea });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var status = reader.IsDBNull(0) ? null : reader.GetString(0);
        var reached = new List<(string Link, DateTimeOffset At)>();
        for (var i = 0; i < ChainLinks.Length; i++)
        {
            if (!reader.IsDBNull(i + 1))
            {
                reached.Add((ChainLinks[i], reader.GetFieldValue<DateTimeOffset>(i + 1)));
            }
        }

        var stages = new List<ChainStage>(reached.Count);
        for (var i = 0; i < reached.Count; i++)
        {
            var end = i + 1 < reached.Count ? reached[i + 1].At : now;
            stages.Add(new ChainStage(reached[i].Link, reached[i].At, Math.Max(0, (end - reached[i].At).TotalDays)));
        }

        return new IdeaChain(idea, status, stages, reader.GetDecimal(5));
    }

    private Task<IngestResult> Link(Guid subject, string predicate, Guid target, string text, NewEpisode source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _facts.IngestAsync(source, [new NewFact(subject, predicate, null, _time.GetUtcNow(), Text: text, ObjectEntity: target)], cancellationToken);
    }

    internal static string OutcomeText(ResultOutcome outcome) => outcome switch
    {
        ResultOutcome.Passed => "passed",
        ResultOutcome.Failed => "failed",
        ResultOutcome.Inconclusive => "inconclusive",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
