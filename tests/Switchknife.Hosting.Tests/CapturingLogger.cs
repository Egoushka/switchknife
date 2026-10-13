using Microsoft.Extensions.Logging;

namespace Switchknife.Hosting.Tests;

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

/// <summary>Keeps every line at or above <c>minimum</c>, with its structured state.</summary>
internal sealed class CapturingLogger(LogLevel minimum = LogLevel.Trace) : ILogger
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, [.. pairs]));
    }
}
