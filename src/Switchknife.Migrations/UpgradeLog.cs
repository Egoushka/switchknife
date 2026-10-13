using System.Globalization;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace Switchknife.Migrations;

/// <summary>
/// Writes DbUp's log to an <see cref="ILogger"/>. DbUp passes composite formats (<c>"script '{0}'"</c>); each line is
/// formatted here with the invariant culture and logged under one constant template, so a sink sees the same text
/// DbUp meant and no positional properties named <c>0</c>, <c>1</c>.
/// </summary>
internal sealed partial class UpgradeLog(ILogger logger, LogLevel informationLevel) : IUpgradeLog
{
    public void LogTrace(string format, params object[] args) => Write(LogLevel.Trace, null, format, args);

    public void LogDebug(string format, params object[] args) => Write(LogLevel.Debug, null, format, args);

    public void LogInformation(string format, params object[] args) => Write(informationLevel, null, format, args);

    public void LogWarning(string format, params object[] args) => Write(LogLevel.Warning, null, format, args);

    public void LogError(string format, params object[] args) => Write(LogLevel.Error, null, format, args);

    public void LogError(Exception ex, string format, params object[] args) => Write(LogLevel.Error, ex, format, args);

    private void Write(LogLevel level, Exception? exception, string format, object[] args)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        // Without arguments the text is not a format: a brace in it is literal.
        var message = args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format, args) : format;
        Line(logger, level, exception, message);
    }

    [LoggerMessage(Message = "{Message}")]
    private static partial void Line(ILogger logger, LogLevel level, Exception? exception, string message);
}
