using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Switchknife.Facts.Extraction;

/// <summary>Why a fact was rejected. A code, never text from the source, so a report is safe to log.</summary>
public enum RejectReason
{
    /// <summary>The text is blank or longer than 300 characters.</summary>
    BadText,

    /// <summary>The predicate is blank.</summary>
    BadPredicate,

    /// <summary>The predicate is not listed and the options say to reject.</summary>
    UnknownPredicate,

    /// <summary>Neither or both of value and object, or an object on a <c>statement</c>.</summary>
    BadObject,

    /// <summary>A <c>ref</c> that is not a handle of the context.</summary>
    UnknownEntity,

    /// <summary>A missing subject, or a mention with no <c>ref</c> and no name or kind.</summary>
    BadEntity,

    /// <summary>No evidence id.</summary>
    NoEvidence,

    /// <summary>An evidence id that is not a line of the episode.</summary>
    EvidenceOutsideEpisode,

    /// <summary>A date that does not parse.</summary>
    BadDate,

    /// <summary>A date before 1900 or beyond the horizon.</summary>
    DateOutOfRange,

    /// <summary>The end is not after the start.</summary>
    EndBeforeStart,

    /// <summary>The same assertion appeared earlier in the output.</summary>
    Duplicate,

    /// <summary>More facts than the cap allows; this one was among the least salient.</summary>
    OverCap,
}

/// <summary>A rejected fact: its position in the output and why. Nothing from its content.</summary>
/// <param name="Index">Position in <see cref="ExtractedEpisode.Facts"/>.</param>
/// <param name="Reason">Why.</param>
public sealed record Rejection(int Index, RejectReason Reason);

/// <summary>A fact that passed validation, ready for the store once its entities are resolved.</summary>
/// <param name="Index">Position in the extractor's output.</param>
/// <param name="Subject">The entity it is about.</param>
/// <param name="Predicate">The predicate (<c>statement</c> for an open fact).</param>
/// <param name="Cardinality">The predicate's cardinality (<c>Many</c> for a statement).</param>
/// <param name="Value">The object as text; for a <c>statement</c> the same as <paramref name="Text"/>; null when the object is an entity.</param>
/// <param name="ObjectEntity">The object as an entity.</param>
/// <param name="Text">The sentence.</param>
/// <param name="ValidFrom">When it began.</param>
/// <param name="StartDefaulted"><paramref name="ValidFrom"/> is the episode time because the source gave none.</param>
/// <param name="ValidTo">When it ended; null when unknown.</param>
/// <param name="Replaces">The live fact it replaces, already checked against the context.</param>
/// <param name="Evidence">The cited line ids, in the order cited.</param>
/// <param name="LocatorJson">The locator for <c>fact_sources</c>: <c>{"evidence":[…]}</c>.</param>
/// <param name="Quote">A quote found in a cited line, or null.</param>
/// <param name="Salience">0 to 1, or null.</param>
/// <param name="Downgraded">The predicate was not listed, so the fact was rewritten to a <c>statement</c>.</param>
/// <param name="ReplacesDropped">The extractor gave a <c>replaces</c> that failed the checks and was dropped.</param>
/// <param name="QuoteDropped">The extractor gave a quote that is not in the cited lines (or is too long) and it was dropped.</param>
public sealed record ValidFact(
    int Index,
    EntityMention Subject,
    string Predicate,
    Cardinality Cardinality,
    string? Value,
    EntityMention? ObjectEntity,
    string Text,
    DateTimeOffset ValidFrom,
    bool StartDefaulted,
    DateTimeOffset? ValidTo,
    Guid? Replaces,
    IReadOnlyList<string> Evidence,
    string LocatorJson,
    string? Quote,
    double? Salience,
    bool Downgraded = false,
    bool ReplacesDropped = false,
    bool QuoteDropped = false);

/// <summary>The outcome of validating one output.</summary>
/// <param name="Facts">Accepted facts, in the order the extractor gave them.</param>
/// <param name="Rejections">Rejected facts with reason codes.</param>
/// <param name="Downgraded">Accepted facts rewritten to <c>statement</c> because their predicate was not listed.</param>
/// <param name="ReplacesDropped">Accepted facts whose <c>replaces</c> was dropped.</param>
/// <param name="QuotesDropped">Accepted facts whose quote was dropped.</param>
public sealed record ValidatedExtraction(
    IReadOnlyList<ValidFact> Facts, IReadOnlyList<Rejection> Rejections, int Downgraded, int ReplacesDropped, int QuotesDropped);

/// <summary>Checks an extractor's output against the episode it came from. Pure: no database, no clock.</summary>
public static class ExtractionValidator
{
    /// <summary>The predicate of an open fact (a sentence with no registered predicate); always allowed, cardinality <c>Many</c>.</summary>
    public const string StatementPredicate = "statement";

    private const int MaxTextLength = 300;

    private static readonly DateTimeOffset s_earliest = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] s_dateTimeFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK"];

    /// <summary>Applies every rule of the extraction contract; see the F5 spec for the list and the order.</summary>
    public static ValidatedExtraction Validate(ExtractedEpisode output, ExtractionContext context, ExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(context);
        options ??= new ExtractionOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFacts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.HorizonDays);
        var horizon = context.OccurredAt.AddDays(options.HorizonDays);

        var accepted = new List<ValidFact>();
        var rejected = new List<Rejection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var facts = output.Facts ?? [];
        for (var i = 0; i < facts.Count; i++)
        {
            var result = ValidateOne(i, facts[i], context, options, horizon);
            if (result.Reason is { } reason)
            {
                rejected.Add(new Rejection(i, reason));
            }
            else if (seen.Add(DuplicateKey(result.Fact!)))
            {
                accepted.Add(result.Fact!);
            }
            else
            {
                rejected.Add(new Rejection(i, RejectReason.Duplicate));
            }
        }

        if (accepted.Count > options.MaxFacts)
        {
            // Least salient go first; a missing salience counts lowest, and of equals the later one goes.
            var cut = accepted
                .OrderByDescending(f => f.Salience ?? -1)
                .ThenBy(f => f.Index)
                .Skip(options.MaxFacts)
                .Select(f => f.Index)
                .ToHashSet();
            rejected.AddRange(accepted.Where(f => cut.Contains(f.Index)).Select(f => new Rejection(f.Index, RejectReason.OverCap)));
            accepted.RemoveAll(f => cut.Contains(f.Index));
        }

        rejected.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new ValidatedExtraction(accepted, rejected, accepted.Count(f => f.Downgraded), accepted.Count(f => f.ReplacesDropped), accepted.Count(f => f.QuoteDropped));
    }

    private static Outcome ValidateOne(
        int index, ExtractedFact? raw, ExtractionContext context, ExtractionOptions options, DateTimeOffset horizon)
    {
        var downgraded = false;
        if (raw is null)
        {
            return Reject(RejectReason.BadText);
        }

        var text = raw.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
        {
            return Reject(RejectReason.BadText);
        }

        var predicate = raw.Predicate?.Trim();
        if (string.IsNullOrEmpty(predicate))
        {
            return Reject(RejectReason.BadPredicate);
        }

        var cardinality = Cardinality.Many;
        var open = predicate == StatementPredicate;
        if (!open)
        {
            if (context.TryGetPredicate(predicate, out var spec))
            {
                cardinality = spec.Cardinality;
            }
            else if (options.UnknownPredicates == UnknownPredicateMode.Reject)
            {
                return Reject(RejectReason.UnknownPredicate);
            }
            else
            {
                predicate = StatementPredicate;
                open = true;
                downgraded = true;
            }
        }

        string? value = null;
        EntityMention? objectMention = null;
        ExtractedMention? objectRaw = raw.ObjectEntity;
        if (open)
        {
            // A statement's value is its sentence. A model that wrote a statement and also an object is confused; a model whose
            // typed predicate we rewrote is not, so only the explicit statement is refused.
            if (!downgraded && objectRaw is not null)
            {
                return Reject(RejectReason.BadObject);
            }

            value = text;
        }
        else
        {
            var hasValue = !string.IsNullOrWhiteSpace(raw.Value);
            if (hasValue == (objectRaw is not null))
            {
                return Reject(RejectReason.BadObject);
            }

            value = hasValue ? raw.Value!.Trim() : null;
        }

        var subject = ResolveMention(raw.Subject, context, out var subjectReason);
        if (subject is null)
        {
            return Reject(subjectReason);
        }

        if (!open && objectRaw is not null)
        {
            objectMention = ResolveMention(objectRaw, context, out var objectReason);
            if (objectMention is null)
            {
                return Reject(objectReason);
            }
        }

        var evidence = (raw.Evidence ?? []).Select(e => e?.Trim() ?? string.Empty).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (evidence.Count == 0)
        {
            return Reject(RejectReason.NoEvidence);
        }

        if (!evidence.All(context.HasEvidence))
        {
            return Reject(RejectReason.EvidenceOutsideEpisode);
        }

        if (!TryDate(raw.ValidFrom, out var from, out var fromGiven) || !TryDate(raw.ValidTo, out var to, out var toGiven))
        {
            return Reject(RejectReason.BadDate);
        }

        if (fromGiven && (from < s_earliest || from > horizon) || toGiven && (to < s_earliest || to > horizon))
        {
            return Reject(RejectReason.DateOutOfRange);
        }

        var start = fromGiven ? from : context.OccurredAt;
        if (toGiven && to <= start)
        {
            return Reject(RejectReason.EndBeforeStart);
        }

        var quote = KeepQuote(raw.Quote, evidence, context, options.MaxQuoteLength);
        var replaces = KeepReplaces(raw.Replaces, cardinality, subject, context);
        var salience = raw.Salience is { } s && !double.IsNaN(s) ? Math.Clamp(s, 0, 1) : (double?)null;
        var fact = new ValidFact(
            index, subject, predicate, cardinality, value, objectMention, text, start, !fromGiven, toGiven ? to : null, replaces, evidence, Locator(evidence), quote, salience,
            downgraded, !string.IsNullOrWhiteSpace(raw.Replaces) && replaces is null, !string.IsNullOrWhiteSpace(raw.Quote) && quote is null);
        return new Outcome(fact, null);
    }

    private static Outcome Reject(RejectReason reason) => new(null, reason);

    private static EntityMention? ResolveMention(ExtractedMention? mention, ExtractionContext context, out RejectReason reason)
    {
        reason = RejectReason.BadEntity;
        if (mention is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(mention.Ref))
        {
            if (context.TryResolveEntity(mention.Ref, out var known))
            {
                return new EntityMention(known.Kind, known.Name, known.Id);
            }

            reason = RejectReason.UnknownEntity;
            return null;
        }

        if (string.IsNullOrWhiteSpace(mention.Name) || string.IsNullOrWhiteSpace(mention.Kind))
        {
            return null;
        }

        var kind = mention.Kind.Trim();
        var name = mention.Name.Trim();
        var match = context.FindEntity(kind, name);
        return new EntityMention(kind, name, match?.Id);
    }

    private static string? KeepQuote(string? quote, List<string> evidence, ExtractionContext context, int maxLength)
    {
        var trimmed = quote?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            return null;
        }

        var wanted = Squash(trimmed);
        return context.Evidence.Where(l => evidence.Contains(l.Id)).Any(l => Squash(l.Text).Contains(wanted, StringComparison.Ordinal)) ? trimmed : null;
    }

    private static Guid? KeepReplaces(string? handle, Cardinality cardinality, EntityMention subject, ExtractionContext context)
    {
        if (string.IsNullOrWhiteSpace(handle) || cardinality != Cardinality.Many || subject.KnownId is not { } subjectId)
        {
            return null;
        }

        return context.TryResolveFact(handle, out var known) && known.Subject == subjectId ? known.Id : null;
    }

    private static string Squash(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>A null or blank date is "not given"; anything else must be <c>YYYY-MM-DD</c> (midnight UTC) or ISO 8601 with an offset.</summary>
    private static bool TryDate(string? text, out DateTimeOffset value, out bool given)
    {
        value = default;
        var trimmed = text?.Trim();
        given = !string.IsNullOrEmpty(trimmed);
        if (!given)
        {
            return true;
        }

        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            value = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return true;
        }

        var hasOffset = trimmed!.EndsWith('Z') || trimmed.Length > 6 && trimmed[^6] is '+' or '-' && trimmed[^3] == ':';
        return hasOffset && DateTimeOffset.TryParseExact(trimmed, s_dateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static string DuplicateKey(ValidFact fact)
    {
        var subject = fact.Subject.KnownId is { } id ? id.ToString("N") : $"{fact.Subject.Kind}|{fact.Subject.Name}".ToLowerInvariant();
        var obj = fact.ObjectEntity is { } o ? (o.KnownId is { } oid ? oid.ToString("N") : $"{o.Kind}|{o.Name}".ToLowerInvariant()) : Squash(fact.Value ?? string.Empty);
        return string.Create(CultureInfo.InvariantCulture, $"{subject}\n{fact.Predicate.ToLowerInvariant()}\n{obj}\n{fact.ValidFrom.UtcTicks}");
    }

    private static string Locator(List<string> evidence)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("evidence");
            foreach (var id in evidence)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed record Outcome(ValidFact? Fact, RejectReason? Reason);
}
