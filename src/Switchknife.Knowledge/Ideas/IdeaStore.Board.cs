using Npgsql;
using NpgsqlTypes;

namespace Switchknife.Knowledge.Ideas;

public sealed partial class IdeaStore
{
    // Every status row except those that were never the owner's belief (retracted, outranked, merged away). A status change
    // supersedes the open row and rewrites it closed, so the same status repeats at the same start; the reader collapses repeats.
    private const string StatusHistorySql = """
        select subject_id, object_value, lower(valid) from facts.facts
        where namespace = $1 and predicate = 'status'
          and (retired_reason is null or retired_reason not in ('retracted', 'outranked', 'merged'))
        order by subject_id, lower(valid), recorded_at
        """;

    private const string LiveFieldsSql = """
        select subject_id, predicate, object_value from facts.facts
        where namespace = $1 and predicate in ('title', 'value_hypothesis', 'cheapest_test') and retired_at is null and valid @> $2
        """;

    private const string EarnedSql = """
        select coalesce(sum(object_value::numeric), 0), coalesce(sum(object_value::numeric) filter (where lower(valid) >= $2), 0)
        from facts.facts where namespace = $1 and predicate = 'earned' and retired_at is null
        """;

    private static readonly AbsentMeasure[] AbsentMeasures =
    [
        new("late_yield", "needs confirmed links and read counts, which arrive with the map namespace (K4)"),
        new("question_coverage", "needs the search query log, which arrives with knowledge_search"),
    ];

    /// <summary>
    /// The board in one round trip: live counts, the funnel, median days per status, stuck and unboxed tests, and money with
    /// estimated and earned UAH apart. Late yield and question coverage are null, listed in <see cref="IdeaBoard.Absent"/>.
    /// </summary>
    /// <param name="targetUahPerMonth">The income target to echo next to earned UAH; null for none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IdeaBoard> BoardAsync(decimal? targetUahPerMonth = null, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var batch = new NpgsqlBatch(connection)
        {
            BatchCommands =
            {
                Command(StatusHistorySql),
                Command(LiveFieldsSql, now),
                Command(EarnedSql, monthStart),
            },
        };
        await using var batchScope = batch.ConfigureAwait(false);
        var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);

        var history = new Dictionary<Guid, List<(IdeaStatus Status, DateTimeOffset At)>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ParseStatus(reader.GetString(1)) is not { } status)
            {
                continue;
            }

            var idea = reader.GetGuid(0);
            if (!history.TryGetValue(idea, out var stays))
            {
                history[idea] = stays = [];
            }

            if (stays.Count == 0 || stays[^1].Status != status)
            {
                stays.Add((status, reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var fields = new Dictionary<(Guid, string), string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fields[(reader.GetGuid(0), reader.GetString(1))] = reader.GetString(2);
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var earned = reader.GetDecimal(0);
        var earnedThisMonth = reader.GetDecimal(1);

        return Summarize(history, fields, now, new IdeaMoney(0, earned, earnedThisMonth, targetUahPerMonth));
    }

    private static IdeaBoard Summarize(
        Dictionary<Guid, List<(IdeaStatus Status, DateTimeOffset At)>> history,
        Dictionary<(Guid, string), string> fields,
        DateTimeOffset now,
        IdeaMoney money)
    {
        var live = new int[5];
        var ever = new int[5];
        var days = new List<double>[] { [], [], [], [], [] };
        var stuck = new List<StuckTest>();
        var unboxed = new List<Guid>();
        var estimated = 0m;
        foreach (var (idea, stays) in history)
        {
            foreach (var status in stays.Select(s => s.Status).Distinct())
            {
                ever[(int)status]++;
            }

            for (var i = 0; i + 1 < stays.Count; i++)
            {
                days[(int)stays[i].Status].Add((stays[i + 1].At - stays[i].At).TotalDays);
            }

            var (current, since) = stays[^1];
            live[(int)current]++;
            if (current != IdeaStatus.Testing)
            {
                continue;
            }

            estimated += Field(fields, idea, IdeaPredicates.ValueHypothesis) is { } h ? IdeaJson.ReadHypothesis(h)?.ExpectedUahPerMonth ?? 0 : 0;
            var inTesting = (now - since).TotalDays;
            if ((Field(fields, idea, IdeaPredicates.CheapestTest) is { } t ? IdeaJson.ReadTest(t)?.TimeBoxDays : null) is not { } box)
            {
                unboxed.Add(idea);
            }
            else if (inTesting > box)
            {
                stuck.Add(new StuckTest(idea, Field(fields, idea, IdeaPredicates.Title), since, box, inTesting));
            }
        }

        int Live(IdeaStatus s) => live[(int)s];
        int Ever(IdeaStatus s) => ever[(int)s];
        return new IdeaBoard(
            new StatusCounts(Live(IdeaStatus.Seed), Live(IdeaStatus.Testing), Live(IdeaStatus.Validated), Live(IdeaStatus.Killed), Live(IdeaStatus.Earning)),
            new IdeaFunnel(
                Ever(IdeaStatus.Seed),
                Ever(IdeaStatus.Testing),
                Ever(IdeaStatus.Validated),
                Ever(IdeaStatus.Earning),
                Ratio(Ever(IdeaStatus.Testing), Ever(IdeaStatus.Seed)),
                Ratio(Ever(IdeaStatus.Validated), Ever(IdeaStatus.Testing)),
                Ratio(Ever(IdeaStatus.Earning), Ever(IdeaStatus.Validated))),
            new StageDays(Median(days[(int)IdeaStatus.Seed]), Median(days[(int)IdeaStatus.Testing]), Median(days[(int)IdeaStatus.Validated])),
            [.. stuck.OrderBy(s => s.Since)],
            unboxed,
            money with { EstimatedUahPerMonth = estimated },
            LateYield: null,
            QuestionCoverage: null,
            AbsentMeasures);
    }

    private static string? Field(Dictionary<(Guid, string), string> fields, Guid idea, string predicate) =>
        fields.TryGetValue((idea, predicate), out var value) ? value : null;

    private static double? Ratio(int part, int whole) => whole == 0 ? null : (double)part / whole;

    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private static NpgsqlBatchCommand Command(string sql, DateTimeOffset? at = null)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = IdeaPredicates.Namespace });
        if (at is { } value)
        {
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value });
        }

        return command;
    }
}
