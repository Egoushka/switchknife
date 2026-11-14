namespace Switchknife.Facts;

/// <summary>
/// How a fact's validity window relates to a query window: Allen's thirteen relations, then <see cref="Intersects"/>.
/// Windows are half-open <c>[from, to)</c>; an unbounded end compares as infinite, so two open ends are equal.
/// "Fact" below is the stored fact, "window" the one asked about.
/// </summary>
public enum IntervalRelation
{
    /// <summary>The fact shares at least one instant with the window (any overlap, not only Allen's <see cref="Overlaps"/>).</summary>
    Intersects,

    /// <summary>The fact ends before the window begins, with a gap.</summary>
    Before,

    /// <summary>The fact ends exactly when the window begins.</summary>
    Meets,

    /// <summary>The fact begins first and ends inside the window.</summary>
    Overlaps,

    /// <summary>The fact begins with the window and ends inside it.</summary>
    Starts,

    /// <summary>The fact lies strictly inside the window.</summary>
    During,

    /// <summary>The fact begins inside the window and ends with it.</summary>
    Finishes,

    /// <summary>The fact and the window are the same range.</summary>
    Equals,

    /// <summary>The fact begins after the window ends, with a gap.</summary>
    After,

    /// <summary>The fact begins exactly when the window ends.</summary>
    MetBy,

    /// <summary>The fact begins inside the window and ends after it.</summary>
    OverlappedBy,

    /// <summary>The fact begins with the window and ends after it.</summary>
    StartedBy,

    /// <summary>The fact begins before the window and ends after it.</summary>
    Contains,

    /// <summary>The fact begins before the window and ends with it.</summary>
    FinishedBy,
}
