using Switchknife.Facts;

namespace Switchknife.Knowledge.Ideas;

/// <summary>The predicates and entity kinds of the <c>ideas</c> namespace.</summary>
public static class IdeaPredicates
{
    /// <summary>The namespace holding ideas, experiments and results.</summary>
    public const string Namespace = "ideas";

    /// <summary>Entity kind of an idea.</summary>
    public const string IdeaKind = "idea";

    /// <summary>Entity kind of an experiment (a test run).</summary>
    public const string ExperimentKind = "experiment";

    /// <summary>Entity kind of a result.</summary>
    public const string ResultKind = "result";

    /// <summary>The idea's one-line text, from capture.</summary>
    public const string Title = "title";

    /// <summary>The <see cref="IdeaStatus"/> in lower case.</summary>
    public const string Status = "status";

    /// <summary>The <see cref="ValueKind"/> in lower case.</summary>
    public const string ValueKind = "value_kind";

    /// <summary>A <see cref="ValueHypothesis"/> as JSON.</summary>
    public const string ValueHypothesis = "value_hypothesis";

    /// <summary>A <see cref="CheapestTest"/> as JSON.</summary>
    public const string CheapestTest = "cheapest_test";

    /// <summary>Why the idea was killed.</summary>
    public const string KillReason = "kill_reason";

    /// <summary>Any fact or idea → an idea.</summary>
    public const string Inspired = "inspired";

    /// <summary>Idea → experiment.</summary>
    public const string TestedBy = "tested_by";

    /// <summary>Experiment → result.</summary>
    public const string Produced = "produced";

    /// <summary>Result → money (UAH amount, date, source). Written by the owner only.</summary>
    public const string Earned = "earned";

    /// <summary>Every predicate with its cardinality, for <see cref="FactsStore.DefinePredicateAsync"/>.</summary>
    public static IReadOnlyList<(string Name, Cardinality Cardinality)> All { get; } =
    [
        (Title, Cardinality.One),
        (Status, Cardinality.One),
        (ValueKind, Cardinality.One),
        (ValueHypothesis, Cardinality.One),
        (CheapestTest, Cardinality.One),
        (KillReason, Cardinality.One),
        (Inspired, Cardinality.Many),
        (TestedBy, Cardinality.Many),
        (Produced, Cardinality.Event),
        (Earned, Cardinality.Event),
    ];
}
