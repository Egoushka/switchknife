namespace Switchknife.Facts.Bench;

/// <summary>A clock the run moves to each episode's recorded time, so the library stamps <c>recorded_at</c> the way the history says it arrived.</summary>
internal sealed class ReplayClock : TimeProvider
{
    private DateTimeOffset _now = new(Workload.Origin);

    public void Set(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public override DateTimeOffset GetUtcNow() => _now;
}
