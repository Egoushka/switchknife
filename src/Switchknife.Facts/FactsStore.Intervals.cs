using Npgsql;

namespace Switchknife.Facts;

public sealed partial class FactsStore
{
    private const string IntervalColumns =
    "select id, subject_id, predicate, object_value, text, lower(valid), upper(valid), recorded_at, object_id from facts.facts";

    private const string W0 = "coalesce($3, '-infinity'::timestamptz)";

    private const string W1 = "coalesce($4, 'infinity'::timestamptz)";

    private const string StartExpr = "coalesce(lower(valid), '-infinity'::timestamptz)";

    private const string EndExpr = "coalesce(upper(valid), 'infinity'::timestamptz)";

    private const string FactWindowSql = "select lower(valid), upper(valid), upper_inc(valid) from facts.facts where namespace = $1 and id = $2";

    /// <summary>
    /// The facts whose validity window stands in <paramref name="relation"/> to <c>[from, to)</c>, as believed at
    /// <paramref name="knownAt"/>. "What was true while X" without hand-written SQL.
    /// </summary>
    /// <param name="relation">How the fact's window relates to the query window.</param>
    /// <param name="from">The window's start; null for unbounded.</param>
    /// <param name="to">The window's end; null for unbounded. Must be after <paramref name="from"/>.</param>
    /// <param name="subject">Only this entity's facts, or null for all.</param>
    /// <param name="predicate">Only this predicate, or null for all.</param>
    /// <param name="knownAt">The instant of belief; defaults to now (the store's clock). A future instant is refused.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="relation"/> is unknown, the window is empty, or <paramref name="knownAt"/> is after the store's clock.</exception>
    public Task<IReadOnlyList<Fact>> QueryAsync(
        IntervalRelation relation, DateTimeOffset? from, DateTimeOffset? to, Guid? subject = null, string? predicate = null,
        DateTimeOffset? knownAt = null, CancellationToken cancellationToken = default)
    {
        return QueryWindowAsync(relation, Micro(from), Micro(to), subject, predicate, knownAt, excluded: null, cancellationToken);
    }

    /// <summary>
    /// The facts whose window stands in <paramref name="relation"/> to the window of <paramref name="factId"/>, as believed at
    /// <paramref name="knownAt"/>: what was true while that fact was. The fact itself is left out.
    /// </summary>
    /// <param name="factId">A row of this namespace, live or retired.</param>
    /// <param name="relation">How a result's window relates to the fact's; defaults to any shared instant.</param>
    /// <param name="knownAt">The instant of belief; defaults to now. A future instant is refused.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="InvalidOperationException">The fact is not in this namespace.</exception>
    public async Task<IReadOnlyList<Fact>> WhileAsync(
        Guid factId, IntervalRelation relation = IntervalRelation.Intersects, DateTimeOffset? knownAt = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? from, to;
        var command = _data.CreateCommand(FactWindowSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(Text(_namespace));
            command.Parameters.Add(Uuid(factId));
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Fact {factId} is not in namespace '{_namespace}'.");
            }

            from = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
            to = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            if (to is { } point && reader.GetBoolean(2))
            {
                // An event is the closed range [t, t]; as a window it is the smallest one that holds t.
                to = point.AddTicks(10);
            }
        }

        return await QueryWindowAsync(relation, from, to, subject: null, predicate: null, knownAt, excluded: factId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Fact>> QueryWindowAsync(
        IntervalRelation relation, DateTimeOffset? from, DateTimeOffset? to, Guid? subject, string? predicate, DateTimeOffset? knownAt, Guid? excluded, CancellationToken ct)
    {
        if (!Enum.IsDefined(relation))
        {
            throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unknown relation.");
        }

        if (from is { } f && to is { } t && t <= f)
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, "The window must end after it begins.");
        }

        var now = Micro(_time.GetUtcNow());
        var known = knownAt is { } k ? Micro(k) : now;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(known, now, nameof(knownAt));

        // The window comparison keeps $3 and $4 both referenced, which a prepared statement requires, whatever the relation uses.
        var sql = IntervalColumns + $" where namespace = $1 and recorded_at <= $2 and (retired_at is null or retired_at > $2) and {W0} <= {W1} and ({Predicate(relation)})";
        var next = 5;
        var command = _data.CreateCommand(string.Empty);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Time(known));
        command.Parameters.Add(Time(from));
        command.Parameters.Add(Time(to));
        if (subject is { } s)
        {
            sql += $" and subject_id = ${next++}";
            command.Parameters.Add(Uuid(s));
        }

        if (predicate is not null)
        {
            sql += $" and predicate = ${next++}";
            command.Parameters.Add(Text(predicate));
        }

        if (excluded is { } id)
        {
            sql += $" and id <> ${next}";
            command.Parameters.Add(Uuid(id));
        }

        command.CommandText = sql + " order by lower(valid), predicate, id";
        var facts = new List<Fact>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            facts.Add(ReadFact(reader));
        }

        return facts;
    }

    // $3 is the window start and $4 its end; null reads as infinite.
    private static string Predicate(IntervalRelation relation) => relation switch
    {
        IntervalRelation.Intersects => "valid && tstzrange($3, $4, '[)')",
        IntervalRelation.Before => $"{EndExpr} < {W0}",
        IntervalRelation.Meets => $"{EndExpr} = {W0}",
        IntervalRelation.Overlaps => $"{StartExpr} < {W0} and {W0} < {EndExpr} and {EndExpr} < {W1}",
        IntervalRelation.Starts => $"{StartExpr} = {W0} and {EndExpr} < {W1}",
        IntervalRelation.During => $"{StartExpr} > {W0} and {EndExpr} < {W1}",
        IntervalRelation.Finishes => $"{StartExpr} > {W0} and {EndExpr} = {W1}",
        IntervalRelation.Equals => $"{StartExpr} = {W0} and {EndExpr} = {W1}",
        IntervalRelation.After => $"{StartExpr} > {W1}",
        IntervalRelation.MetBy => $"{StartExpr} = {W1}",
        IntervalRelation.OverlappedBy => $"{W0} < {StartExpr} and {StartExpr} < {W1} and {EndExpr} > {W1}",
        IntervalRelation.StartedBy => $"{StartExpr} = {W0} and {EndExpr} > {W1}",
        IntervalRelation.Contains => $"{StartExpr} < {W0} and {EndExpr} > {W1}",
        IntervalRelation.FinishedBy => $"{StartExpr} < {W0} and {EndExpr} = {W1}",
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unknown relation."),
    };
}
