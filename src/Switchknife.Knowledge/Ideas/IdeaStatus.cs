namespace Switchknife.Knowledge.Ideas;

/// <summary>Where an idea stands. A seed may stay a seed forever.</summary>
public enum IdeaStatus
{
    /// <summary>Captured with nothing but its text and source. Never judged, ranked down or expired.</summary>
    Seed,

    /// <summary>Hours or money are being spent on its cheapest test.</summary>
    Testing,

    /// <summary>A recorded test passed.</summary>
    Validated,

    /// <summary>Dropped, with a reason. Stays searchable and linkable.</summary>
    Killed,

    /// <summary>Money from it has been recorded.</summary>
    Earning,
}

/// <summary>Who gains from an idea.</summary>
public enum ValueKind
{
    /// <summary>People gain (time, quality, learning).</summary>
    People,

    /// <summary>Money: the hypothesis carries an expected UAH per month.</summary>
    Money,
}
