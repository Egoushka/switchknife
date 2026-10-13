using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Switchknife.Migrations.Tests;

public sealed class UpgradeLogTests
{
    [Fact]
    public void Each_DbUp_level_maps_to_the_same_ILogger_level()
    {
        var logger = new CapturingLogger();
        var log = new UpgradeLog(logger, LogLevel.Information);
        var failure = new InvalidOperationException("boom");

        log.LogTrace("trace {0}", 1);
        log.LogDebug("debug {0}", 2);
        log.LogInformation("information {0}", 3);
        log.LogWarning("warning {0}", 4);
        log.LogError("error {0}", 5);
        log.LogError(failure, "error with exception {0}", 6);

        Assert.Equal(
            [
                (LogLevel.Trace, "trace 1", null),
                (LogLevel.Debug, "debug 2", null),
                (LogLevel.Information, "information 3", null),
                (LogLevel.Warning, "warning 4", null),
                (LogLevel.Error, "error 5", null),
                (LogLevel.Error, "error with exception 6", (Exception?)failure),
            ],
            logger.Entries.Select(e => (e.Level, e.Message, e.Exception)));
    }

    [Fact]
    public void Information_lines_take_the_configured_level_and_the_others_keep_theirs()
    {
        var logger = new CapturingLogger();
        var log = new UpgradeLog(logger, LogLevel.Debug);

        log.LogInformation("Executing Database Server script '{0}'", "App.Migrations.0001.sql");
        log.LogWarning("warning");
        log.LogError("error");

        Assert.Equal([LogLevel.Debug, LogLevel.Warning, LogLevel.Error], logger.Entries.Select(e => e.Level));
        Assert.Equal("Executing Database Server script 'App.Migrations.0001.sql'", logger.Entries[0].Message);
    }

    [Fact]
    public void A_line_is_formatted_with_the_invariant_culture_under_one_constant_template()
    {
        var logger = new CapturingLogger();
        var log = new UpgradeLog(logger, LogLevel.Information);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
        try
        {
            log.LogInformation("took {0} s", 1.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("took 1.5 s", entry.Message);
        Assert.Contains(new KeyValuePair<string, object?>("{OriginalFormat}", "{Message}"), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("Message", "took 1.5 s"), entry.State);
    }

    [Fact]
    public void A_line_without_arguments_is_kept_verbatim_braces_included()
    {
        var logger = new CapturingLogger();
        var log = new UpgradeLog(logger, LogLevel.Information);

        log.LogInformation("select '{0}' {not a format}");

        Assert.Equal("select '{0}' {not a format}", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void A_line_below_the_enabled_level_is_not_formatted()
    {
        var logger = new CapturingLogger(LogLevel.Warning);
        var log = new UpgradeLog(logger, LogLevel.Information);

        // Formatting this would throw (no argument 1); a disabled level must not get that far.
        log.LogInformation("{1}", "only one argument");
        log.LogDebug("{1}", "only one argument");

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void LogLevel_None_drops_information_lines()
    {
        var logger = new CapturingLogger(LogLevel.Trace);
        var log = new UpgradeLog(logger, LogLevel.None);

        log.LogInformation("dropped");
        log.LogWarning("kept");

        Assert.Equal("kept", Assert.Single(logger.Entries).Message);
    }
}
