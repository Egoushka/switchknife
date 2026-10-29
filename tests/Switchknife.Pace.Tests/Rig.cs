using Microsoft.Extensions.Time.Testing;

namespace Switchknife.Pace.Tests;

/// <summary>A ledger on a fake clock, with a week that resets seven days after the start.</summary>
internal sealed class Rig
{
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public Rig(PaceMode mode = PaceMode.Observe)
    {
        var consumers = new Dictionary<string, ConsumerPolicy>
        {
            ["a"] = new(0.25),
            ["b"] = new(0.10, Decision.Api),
            ["owner"] = new(0.55),
        };
        Policy = new Policy { Consumers = consumers, Mode = mode };
        Clock = new FakeTimeProvider(Start);
        Ledger = new Ledger(Policy, Clock);
    }

    public Policy Policy { get; }

    public FakeTimeProvider Clock { get; }

    public Ledger Ledger { get; }

    public static DateTimeOffset Reset => Start.AddDays(7);

    public void Meter(double utilization, MeterWindow window = MeterWindow.SevenDay, bool rejected = false, DateTimeOffset? resetsAt = null) =>
        Ledger.Observe(new MeterReading(window, utilization, resetsAt ?? (window == MeterWindow.FiveHour ? Clock.GetUtcNow().AddHours(5) : Reset), rejected));

    public UsageResult Use(string consumer, double usd, string? lease = null) =>
        Ledger.Report(new UsageReport(consumer, new Dictionary<string, double> { ["m"] = usd }, lease));

    public void AdvanceDays(double days) => Clock.Advance(TimeSpan.FromDays(days));

    public ConsumerStatus Consumer(string name) => Ledger.Status().Consumers[name];

    /// <summary>Reports $10 of use by <paramref name="consumer"/> and a meter rise to <paramref name="utilization"/>, so one dollar is worth rise ÷ 10.</summary>
    public void UseAndRise(string consumer, double usd, double utilization)
    {
        Use(consumer, usd);
        Meter(utilization);
    }
}
