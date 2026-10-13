using Microsoft.Extensions.Logging;

namespace Switchknife.Hosting.Tests;

/// <summary>Runs the polls it is given in order, then succeeds; records every poll and hook call.</summary>
internal sealed class ScriptedService(TimeProvider time, ILogger logger, params Func<CancellationToken, Task>[] polls)
    : PollingService(time, logger)
{
    private readonly Queue<Func<CancellationToken, Task>> _polls = new(polls);

    public TimeSpan Every { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan? Cap { get; set; }

    public int Polls { get; private set; }

    public int Succeeded { get; private set; }

    public List<Exception> Failed { get; } = [];

    public Func<Task>? SucceededHook { get; set; }

    public Func<Task>? FailedHook { get; set; }

    protected override string Name => "scripted";

    protected override TimeSpan Interval => Every;

    protected override TimeSpan MaxBackoff => Cap ?? base.MaxBackoff;

    protected override Task PollAsync(CancellationToken cancellationToken)
    {
        Polls++;
        return _polls.TryDequeue(out var poll) ? poll(cancellationToken) : Task.CompletedTask;
    }

    protected override Task OnSucceededAsync(CancellationToken cancellationToken)
    {
        Succeeded++;
        return SucceededHook?.Invoke() ?? Task.CompletedTask;
    }

    protected override Task OnFailedAsync(Exception exception, CancellationToken cancellationToken)
    {
        Failed.Add(exception);
        return FailedHook?.Invoke() ?? Task.CompletedTask;
    }

    public static Func<CancellationToken, Task> Throws(Exception error) => _ => Task.FromException(error);
}
