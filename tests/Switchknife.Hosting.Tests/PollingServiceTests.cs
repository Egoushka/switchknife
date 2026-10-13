using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Switchknife.Hosting.Tests;

public sealed class PollingServiceTests
{
    private static readonly TimeSpan Seconds30 = TimeSpan.FromSeconds(30);

    private readonly WaitingClock _clock = new();
    private readonly CapturingLogger _log = new();

    [Fact]
    public async Task Polls_on_start_then_every_interval_read_before_each_wait()
    {
        using var service = new ScriptedService(_clock, _log);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Seconds30, await _clock.NextWaitAsync());
        Assert.Equal(1, service.Polls);
        _clock.Elapse(Seconds30);
        Assert.Equal(Seconds30, await _clock.NextWaitAsync());
        Assert.Equal(2, service.Polls);

        service.Every = TimeSpan.FromMinutes(1);
        _clock.Elapse(Seconds30);
        Assert.Equal(TimeSpan.FromMinutes(1), await _clock.NextWaitAsync());

        await StopAsync(service);
        Assert.Equal((3, 3, 0), (service.Polls, service.Succeeded, service.Failed.Count));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task A_failed_poll_is_logged_and_retried_with_doubling_waits_until_one_succeeds()
    {
        var errors = new[] { new InvalidOperationException("one"), new InvalidOperationException("two"), new InvalidOperationException("three") };
        using var service = new ScriptedService(_clock, _log, [.. errors.Select(ScriptedService.Throws)]);
        await service.StartAsync(TestContext.Current.CancellationToken);

        foreach (var wait in new[] { 30, 60, 120 })
        {
            Assert.Equal(TimeSpan.FromSeconds(wait), await _clock.NextWaitAsync());
            _clock.Elapse(TimeSpan.FromSeconds(wait));
        }

        Assert.Equal(Seconds30, await _clock.NextWaitAsync());
        await StopAsync(service);

        Assert.Equal(errors, service.Failed);
        Assert.Equal(1, service.Succeeded);
        Assert.Collection(_log.Entries,
            e => AssertFailure(e, errors[0], 1, Seconds30),
            e => AssertFailure(e, errors[1], 2, TimeSpan.FromMinutes(1)),
            e => AssertFailure(e, errors[2], 3, TimeSpan.FromMinutes(2)));
        Assert.Equal("scripted poll failed (1 in a row); next poll in 00:00:30", _log.Entries[0].Message);
    }

    [Fact]
    public async Task The_wait_after_failures_stops_growing_at_MaxBackoff()
    {
        var fail = ScriptedService.Throws(new InvalidOperationException("down"));
        using var service = new ScriptedService(_clock, _log, fail, fail, fail, fail) { Every = TimeSpan.FromMinutes(2) };
        await service.StartAsync(TestContext.Current.CancellationToken);

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 4; i++)
        {
            waits.Add(await _clock.NextWaitAsync());
            _clock.Elapse(waits[^1]);
        }

        await StopAsync(service);
        Assert.Equal([TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)], waits);
    }

    [Fact]
    public async Task MaxBackoff_equal_to_the_interval_retries_at_the_normal_pace()
    {
        var fail = ScriptedService.Throws(new InvalidOperationException("down"));
        using var service = new ScriptedService(_clock, _log, fail, fail, fail) { Cap = Seconds30 };
        await service.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Seconds30, await _clock.NextWaitAsync());
            _clock.Elapse(Seconds30);
        }

        await StopAsync(service);
        Assert.Equal(3, service.Failed.Count);
    }

    [Fact]
    public async Task A_cancellation_the_host_did_not_ask_for_is_a_failed_poll()
    {
        // What HttpClient throws on its own timeout.
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        using var service = new ScriptedService(_clock, _log, ScriptedService.Throws(timeout));
        await service.StartAsync(TestContext.Current.CancellationToken);

        _clock.Elapse(await _clock.NextWaitAsync());
        Assert.Equal(Seconds30, await _clock.NextWaitAsync());
        await StopAsync(service);

        Assert.Equal((2, 1), (service.Polls, service.Succeeded));
        Assert.Same(timeout, Assert.Single(service.Failed));
        AssertFailure(Assert.Single(_log.Entries), timeout, 1, Seconds30);
    }

    [Fact]
    public async Task A_hook_that_throws_is_logged_and_neither_stops_the_loop_nor_counts_as_a_failed_poll()
    {
        var succeededError = new InvalidOperationException("health store down");
        var failedError = new InvalidOperationException("health store still down");
        var pollError = new InvalidOperationException("poll");
        using var service = new ScriptedService(_clock, _log, _ => Task.CompletedTask, ScriptedService.Throws(pollError))
        {
            SucceededHook = () => throw succeededError,
            FailedHook = async () =>
            {
                await Task.Yield();
                throw failedError;
            },
        };
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Seconds30, await _clock.NextWaitAsync());
        _clock.Elapse(Seconds30);
        Assert.Equal(Seconds30, await _clock.NextWaitAsync());
        _clock.Elapse(Seconds30);
        Assert.Equal(Seconds30, await _clock.NextWaitAsync());
        await StopAsync(service);

        Assert.Collection(_log.Entries,
            e => AssertHookFailure(e, succeededError, "OnSucceededAsync"),
            e => AssertFailure(e, pollError, 1, Seconds30),
            e => AssertHookFailure(e, failedError, "OnFailedAsync"),
            e => AssertHookFailure(e, succeededError, "OnSucceededAsync"));
    }

    [Fact]
    public async Task Stopping_during_a_wait_ends_the_loop_without_an_error()
    {
        using var service = new ScriptedService(_clock, _log);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await _clock.NextWaitAsync();

        await StopAsync(service);

        Assert.Equal(1, service.Polls);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Stopping_during_a_poll_cancels_it_and_is_not_a_failure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ScriptedService(_clock, _log, async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await service.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await StopAsync(service);

        Assert.Equal((0, 0), (service.Succeeded, service.Failed.Count));
        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]
    [InlineData(64, 300)]
    [InlineData(int.MaxValue, 300)]
    public void Backoff_doubles_the_interval_per_failure_up_to_the_cap(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PollingService.Backoff(Seconds30, failures, PollingService.DefaultMaxBackoff));

    [Fact]
    public void Backoff_needs_at_least_one_failure() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PollingService.Backoff(Seconds30, 0, PollingService.DefaultMaxBackoff));

    [Fact]
    public void Missing_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ScriptedService(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ScriptedService(TimeProvider.System, null!));
    }

    // StopAsync returns once the loop has ended; the loop must end by returning, not by faulting or cancelling.
    private static async Task StopAsync(ScriptedService service)
    {
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask?.Status);
    }

    private static void AssertFailure(LogEntry entry, Exception error, int failures, TimeSpan delay)
    {
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(error, entry.Exception);
        Assert.Contains(new KeyValuePair<string, object?>("Name", "scripted"), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("Failures", failures), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("Delay", delay), entry.State);
    }

    private static void AssertHookFailure(LogEntry entry, Exception error, string hook)
    {
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(error, entry.Exception);
        Assert.Equal($"scripted {hook} failed", entry.Message);
    }
}
