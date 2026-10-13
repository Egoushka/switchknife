using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Switchknife.Hosting.Tests;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that also reports each timer the service starts, so a test waits for the service to
/// begin its wait before moving the clock, and reads how long that wait is. No real time passes.
/// </summary>
internal sealed class WaitingClock : TimeProvider
{
    private readonly FakeTimeProvider _fake = new();
    private readonly Channel<TimeSpan> _waits = Channel.CreateUnbounded<TimeSpan>();

    public override DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

    public override long GetTimestamp() => _fake.GetTimestamp();

    public override long TimestampFrequency => _fake.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _fake.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _fake.CreateTimer(callback, state, dueTime, period);
        _waits.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>The length of the next wait the service starts; a hang fails the test after 10 seconds instead of blocking the run.</summary>
    public async Task<TimeSpan> NextWaitAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _waits.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Ends the current wait.</summary>
    public void Elapse(TimeSpan wait) => _fake.Advance(wait);
}
