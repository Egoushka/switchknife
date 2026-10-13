using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Switchknife.Hosting;

/// <summary>
/// A <see cref="BackgroundService"/> that calls <see cref="PollAsync"/> on start and then every <see cref="Interval"/>
/// until the host stops. A poll that throws is logged and retried after an exponential backoff capped at
/// <see cref="MaxBackoff"/>; nothing a poll or a hook throws ever leaves <see cref="ExecuteAsync"/>, because an
/// unhandled exception there stops the whole host.
/// </summary>
/// <param name="time">The clock every wait uses; <see cref="TimeProvider.System"/> in production.</param>
/// <param name="logger">Receives the failure lines, under <see cref="Name"/>.</param>
public abstract partial class PollingService(TimeProvider time, ILogger logger) : BackgroundService
{
    /// <summary>The longest wait after failures unless <see cref="MaxBackoff"/> says otherwise: 5 minutes.</summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Names this loop in every log line.</summary>
    protected abstract string Name { get; }

    /// <summary>The wait after a successful poll. Read before each wait, so it may come from options that change.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>
    /// The cap on the wait after consecutive failures. Return <see cref="Interval"/> to retry at the normal pace
    /// instead of backing off.
    /// </summary>
    protected virtual TimeSpan MaxBackoff => DefaultMaxBackoff;

    // Subclasses use these instead of their own copies: capturing a parameter that is also passed to the base is CS9107.
    /// <summary>The clock passed to the constructor.</summary>
    protected TimeProvider Time => _time;

    /// <summary>The logger passed to the constructor.</summary>
    protected ILogger Logger => _logger;

    /// <summary>One poll. Throw to report a failure; the loop logs it and tries again after the backoff.</summary>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    protected abstract Task PollAsync(CancellationToken cancellationToken);

    /// <summary>Called after each poll that returned. An exception here is logged and does not count as a failed poll.</summary>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    protected virtual Task OnSucceededAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called after each poll that threw, after the failure is logged. An exception here is logged and swallowed.</summary>
    /// <param name="exception">What the poll threw.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    protected virtual Task OnFailedAsync(Exception exception, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The wait after <paramref name="failures"/> consecutive failures: the interval doubled per failure after the first, at most <paramref name="max"/>.</summary>
    /// <param name="interval">The wait after a success.</param>
    /// <param name="failures">Consecutive failures, 1 or more.</param>
    /// <param name="max">The cap.</param>
    public static TimeSpan Backoff(TimeSpan interval, int failures, TimeSpan max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        return TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * Math.Pow(2, failures - 1), max.TotalSeconds));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Exception? failure = null;
                try
                {
                    await PollAsync(stoppingToken).ConfigureAwait(false);
                    failures = 0;
                }
                // Any exception, a timeout's OperationCanceledException included, unless the host is stopping.
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    failures++;
                    failure = error;
                }

                var delay = failures == 0 ? Interval : Backoff(Interval, failures, MaxBackoff);
                if (failure is null)
                {
                    await HookAsync(nameof(OnSucceededAsync), OnSucceededAsync, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    PollFailed(_logger, failure, Name, failures, delay);
                    await HookAsync(nameof(OnFailedAsync), ct => OnFailedAsync(failure, ct), stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping: end without an error.
        }
    }

    // Called inside the try, so a hook that throws before returning its task is caught too.
    private async Task HookAsync(string hook, Func<CancellationToken, Task> call, CancellationToken stoppingToken)
    {
        try
        {
            await call(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            HookFailed(_logger, error, Name, hook);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name} poll failed ({Failures} in a row); next poll in {Delay}")]
    private static partial void PollFailed(ILogger logger, Exception error, string name, int failures, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Name} {Hook} failed")]
    private static partial void HookFailed(ILogger logger, Exception error, string name, string hook);
}
