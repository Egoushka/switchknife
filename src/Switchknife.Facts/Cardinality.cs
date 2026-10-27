namespace Switchknife.Facts;

/// <summary>How many values a predicate allows a subject at one time.</summary>
public enum Cardinality
{
    /// <summary>One at a time (<c>lives_in</c>): a value that begins later ends the one before it.</summary>
    One,

    /// <summary>Several at once (<c>likes</c>): values coexist and are never superseded by rule.</summary>
    Many,

    /// <summary>A point in time (<c>met</c>): never superseded.</summary>
    Event,
}
