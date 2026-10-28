using System.Globalization;

namespace Switchknife.Facts.Extraction;

/// <summary>A line of the source the extractor may cite. The text stays with the consumer; the library only echoes it into the prompt.</summary>
/// <param name="Id">The consumer's id for the line (Nytka: a segment id); unique in the episode, no whitespace or brackets.</param>
/// <param name="Text">What was said.</param>
/// <param name="Speaker">Who said it, when that matters.</param>
public sealed record EvidenceLine(string Id, string Text, string? Speaker = null);

/// <summary>An entity the extractor is told about.</summary>
/// <param name="Id">The entity's id in the store.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Its name.</param>
public sealed record KnownEntity(Guid Id, string Kind, string Name);

/// <summary>A live fact the extractor is shown, and may replace.</summary>
/// <param name="Id">The fact row's id.</param>
/// <param name="Subject">The entity it is about; must be one of the context's entities (a fact whose entity was cut is cut with it).</param>
/// <param name="Predicate">Its predicate.</param>
/// <param name="Text">Its sentence.</param>
public sealed record KnownFact(Guid Id, Guid Subject, string Predicate, string Text);

/// <summary>A predicate the extractor may use.</summary>
/// <param name="Name">The predicate, as defined in the store.</param>
/// <param name="Cardinality">How many values a subject may have at once; the validator drops a <c>replaces</c> on <c>One</c> and <c>Event</c>.</param>
/// <param name="Description">What it means, in a few words, for the prompt.</param>
public sealed record PredicateSpec(string Name, Cardinality Cardinality, string? Description = null);

/// <summary>How much of the store's state is shown. Lists are kept in the order given, so the consumer sorts newest first.</summary>
/// <param name="MaxEntities">Entities kept.</param>
/// <param name="MaxFacts">Live facts kept (Nytka: 200).</param>
public sealed record ContextBudget(int MaxEntities = 100, int MaxFacts = 200);

/// <summary>An entity of the context with the handle the extractor uses for it.</summary>
/// <param name="Handle">Short name (<c>e1</c>).</param>
/// <param name="Entity">The entity.</param>
public sealed record HandledEntity(string Handle, KnownEntity Entity);

/// <summary>A live fact of the context with its handle.</summary>
/// <param name="Handle">Short name (<c>f1</c>).</param>
/// <param name="Fact">The fact.</param>
public sealed record HandledFact(string Handle, KnownFact Fact);

/// <summary>
/// Everything an extractor is shown about one episode and everything its output is checked against. Entities and live facts
/// get short handles (<c>e1</c>, <c>f1</c>) so the model never has to copy a GUID and a made-up handle is easy to refuse.
/// </summary>
public sealed class ExtractionContext
{
    private readonly Dictionary<string, HandledEntity> _entityByHandle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandledFact> _factByHandle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PredicateSpec> _predicates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evidenceIds = new(StringComparer.Ordinal);

    /// <summary>Builds a context. Throws <see cref="ArgumentException"/> for duplicate or malformed evidence ids and duplicate predicates.</summary>
    /// <param name="occurredAt">When the source happened: the default start of a fact and the origin of the date horizon.</param>
    /// <param name="evidence">The citable lines.</param>
    /// <param name="predicates">The predicates the extractor may use besides <c>statement</c>.</param>
    /// <param name="entities">Known entities, newest first.</param>
    /// <param name="facts">Live facts of those entities, newest first.</param>
    /// <param name="budget">How much to keep; defaults to 100 entities and 200 facts.</param>
    public ExtractionContext(
        DateTimeOffset occurredAt,
        IEnumerable<EvidenceLine> evidence,
        IEnumerable<PredicateSpec> predicates,
        IEnumerable<KnownEntity>? entities = null,
        IEnumerable<KnownFact>? facts = null,
        ContextBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(predicates);
        budget ??= new ContextBudget();
        ArgumentOutOfRangeException.ThrowIfNegative(budget.MaxEntities);
        ArgumentOutOfRangeException.ThrowIfNegative(budget.MaxFacts);
        OccurredAt = occurredAt;

        var lines = new List<EvidenceLine>();
        foreach (var line in evidence)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (string.IsNullOrEmpty(line.Id) || line.Id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '[' or ']'))
            {
                throw new ArgumentException($"Evidence id '{line.Id}' must be non-empty with no whitespace, control characters or brackets.", nameof(evidence));
            }

            if (!_evidenceIds.Add(line.Id))
            {
                throw new ArgumentException($"Evidence id '{line.Id}' appears twice.", nameof(evidence));
            }

            lines.Add(line);
        }

        Evidence = lines;

        var specs = new List<PredicateSpec>();
        foreach (var spec in predicates)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);
            if (spec.Name == ExtractionValidator.StatementPredicate)
            {
                continue;
            }

            if (!_predicates.TryAdd(spec.Name, spec))
            {
                throw new ArgumentException($"Predicate '{spec.Name}' is listed twice.", nameof(predicates));
            }

            specs.Add(spec);
        }

        Predicates = specs;

        var keptEntities = new List<HandledEntity>();
        var entityList = (entities ?? []).ToList();
        foreach (var entity in entityList.Take(budget.MaxEntities))
        {
            var handled = new HandledEntity(string.Create(CultureInfo.InvariantCulture, $"e{keptEntities.Count + 1}"), entity);
            _entityByHandle.Add(handled.Handle, handled);
            keptEntities.Add(handled);
        }

        CutEntities = entityList.Count - keptEntities.Count;
        Entities = keptEntities;

        var keptIds = keptEntities.Select(e => e.Entity.Id).ToHashSet();
        var keptFacts = new List<HandledFact>();
        var cutFacts = 0;
        foreach (var fact in facts ?? [])
        {
            if (keptFacts.Count >= budget.MaxFacts || !keptIds.Contains(fact.Subject))
            {
                cutFacts++;
                continue;
            }

            var handled = new HandledFact(string.Create(CultureInfo.InvariantCulture, $"f{keptFacts.Count + 1}"), fact);
            _factByHandle.Add(handled.Handle, handled);
            keptFacts.Add(handled);
        }

        CutFacts = cutFacts;
        Facts = keptFacts;
    }

    /// <summary>When the source happened.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>The citable lines, in order.</summary>
    public IReadOnlyList<EvidenceLine> Evidence { get; }

    /// <summary>The listed predicates (without <c>statement</c>).</summary>
    public IReadOnlyList<PredicateSpec> Predicates { get; }

    /// <summary>The entities shown, with handles.</summary>
    public IReadOnlyList<HandledEntity> Entities { get; }

    /// <summary>The live facts shown, with handles.</summary>
    public IReadOnlyList<HandledFact> Facts { get; }

    /// <summary>Entities left out by the budget.</summary>
    public int CutEntities { get; }

    /// <summary>Live facts left out by the budget, or because their entity was.</summary>
    public int CutFacts { get; }

    /// <summary>The entity behind a handle, or false.</summary>
    public bool TryResolveEntity(string? handle, out KnownEntity entity)
    {
        if (handle is not null && _entityByHandle.TryGetValue(handle.Trim(), out var found))
        {
            entity = found.Entity;
            return true;
        }

        entity = null!;
        return false;
    }

    /// <summary>The live fact behind a handle, or false.</summary>
    public bool TryResolveFact(string? handle, out KnownFact fact)
    {
        if (handle is not null && _factByHandle.TryGetValue(handle.Trim(), out var found))
        {
            fact = found.Fact;
            return true;
        }

        fact = null!;
        return false;
    }

    /// <summary>The listed predicate, or false (<c>statement</c> is never listed).</summary>
    public bool TryGetPredicate(string name, out PredicateSpec spec) => _predicates.TryGetValue(name, out spec!);

    /// <summary>True when the episode has a line with this id.</summary>
    public bool HasEvidence(string id) => _evidenceIds.Contains(id);

    /// <summary>The single entity of this kind and name (trimmed, case-insensitive), or null when none or several match.</summary>
    public KnownEntity? FindEntity(string kind, string name)
    {
        KnownEntity? found = null;
        foreach (var handled in Entities)
        {
            if (string.Equals(handled.Entity.Kind.Trim(), kind.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(handled.Entity.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (found is not null)
                {
                    return null;
                }

                found = handled.Entity;
            }
        }

        return found;
    }
}
