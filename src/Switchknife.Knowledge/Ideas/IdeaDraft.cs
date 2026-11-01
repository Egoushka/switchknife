namespace Switchknife.Knowledge.Ideas;

/// <summary>Who gains what, by how much, and the evidence it rests on.</summary>
/// <param name="Who">Who gains.</param>
/// <param name="Gain">What they gain and by how much.</param>
/// <param name="Evidence">What the claim rests on.</param>
/// <param name="ExpectedUahPerMonth">The estimate in UAH per month; required for <see cref="ValueKind.Money"/>. An estimate, never income.</param>
public sealed record ValueHypothesis(string Who, string Gain, string Evidence, decimal? ExpectedUahPerMonth = null);

/// <summary>The smallest action that can keep the idea alive or end it.</summary>
/// <param name="Action">What to do.</param>
/// <param name="CapHours">The most hours it may cost.</param>
/// <param name="CapUah">The most UAH it may cost.</param>
/// <param name="Metric">What is measured.</param>
/// <param name="PassLine">The value of the metric that keeps the idea alive.</param>
public sealed record CheapestTest(string Action, decimal CapHours, decimal CapUah, string Metric, string PassLine);

/// <summary>What an idea would look like after a change, for <see cref="IdeaValidator"/> to check.</summary>
/// <param name="Status">The status asked for.</param>
/// <param name="ValueKind"><c>people</c> or <c>money</c>; required from <see cref="IdeaStatus.Testing"/>.</param>
/// <param name="Hypothesis">Required from <see cref="IdeaStatus.Testing"/>.</param>
/// <param name="Test">Required from <see cref="IdeaStatus.Testing"/>.</param>
/// <param name="KillReason">Required for <see cref="IdeaStatus.Killed"/>.</param>
public sealed record IdeaDraft(
    IdeaStatus Status,
    ValueKind? ValueKind = null,
    ValueHypothesis? Hypothesis = null,
    CheapestTest? Test = null,
    string? KillReason = null);
