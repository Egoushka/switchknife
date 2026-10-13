# Switchknife.Hosting

`PollingService`, a `BackgroundService` that does one thing on a timer and never stops the host. Since .NET 6 an exception that leaves `ExecuteAsync` stops the whole host by default, so a poller that lets one database timeout through takes every endpoint in the process down with it. This base class catches what a poll throws, logs it, backs off and tries again.

```csharp
public sealed class FeedPoller(FeedClient feeds, IOptions<FeedOptions> options, TimeProvider time, ILogger<FeedPoller> logger)
    : PollingService(time, logger)
{
    protected override string Name => "feeds";
    protected override TimeSpan Interval => options.Value.PollInterval;   // read before every wait

    protected override Task PollAsync(CancellationToken cancellationToken) => feeds.FetchNewAsync(cancellationToken);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<FeedPoller>();
```

What the loop does:

1. Polls once on start, then waits. There is no initial delay and no jitter.
2. After a poll that returns: calls `OnSucceededAsync`, then waits `Interval`.
3. After a poll that throws anything, an `OperationCanceledException` from a timeout included: logs a warning `{Name} poll failed ({Failures} in a row); next poll in {Delay}` with the exception, calls `OnFailedAsync(exception)`, then waits `Backoff(Interval, failures, MaxBackoff)`: the interval doubled per consecutive failure after the first, at most `MaxBackoff` (default 5 minutes). One success resets the count.
4. A hook that throws is logged as an error, `{Name} {Hook} failed`, and the loop goes on; a failed `OnSucceededAsync` does not count as a failed poll.
5. When the host stops, the poll or the wait in progress is cancelled through its token and `ExecuteAsync` returns without an error. An exception a poll throws once the host is stopping is not caught.

| Member | Required | Use |
|---|---|---|
| `Name` | yes | Names the loop in every log line. |
| `Interval` | yes | The wait after a success. A property, so it may come from options that change at run time. |
| `PollAsync(ct)` | yes | One poll. Throw to report a failure. |
| `MaxBackoff` | no, 5 minutes | Cap on the wait after failures. Return `Interval` to retry at the normal pace. With an `Interval` above the cap, the first retry comes sooner than the next regular poll would. |
| `OnSucceededAsync(ct)`, `OnFailedAsync(exception, ct)` | no | Record health, update a status page. |
| `Time`, `Logger` | | The constructor's clock and logger, for subclasses (a primary-constructor parameter passed to the base and also captured is warning CS9107). |

Testing: every wait goes through the `TimeProvider` passed in, so `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) drives the loop without real time passing.

Native AOT: works. `samples/Switchknife.AotSmoke` runs a poller in a real host under Native AOT: the first poll throws, the host keeps running, the second poll succeeds, the host stops cleanly.

Limits:

- One poll at a time; the next wait starts after the poll and its hook end, so the period is the poll's duration plus the wait, not a fixed rate.
- No scope per poll: resolve scoped services yourself with `IServiceScopeFactory` inside `PollAsync`.
- `Interval` and `MaxBackoff` are not validated, and an exception from their getters is not caught: both are configuration errors.
