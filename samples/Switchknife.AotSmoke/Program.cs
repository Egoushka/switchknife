using System.Text.Json.Serialization;
using Switchknife.Journal;

// Exercises each package the way a host would, under Native AOT. Exit 0 and one "ok" line per package.
var dir = Directory.CreateTempSubdirectory("switchknife-aot").FullName;
try
{
    var path = Path.Combine(dir, "journal.db");
    await using (var journal = await StepJournal.OpenAsync(path))
    {
        var made = await journal.RunAsync("flow", "step", (c, _) => Task.FromResult(new Made($"made-{c.Attempt}")), SmokeJson.Default.Made);
        await journal.AppendEventAsync("flow", "made", """{"name":"made-1"}""");
        if (made.Name != "made-1")
            return Fail($"first run returned {made.Name}");
    }
    await using (var journal = await StepJournal.OpenAsync(path))
    {
        var again = await journal.RunAsync("flow", "step", (_, _) => Task.FromResult(new Made("ran-twice")), SmokeJson.Default.Made);
        var events = await journal.ReadEventsAsync("flow");
        if (again.Name != "made-1" || events.Count != 1)
            return Fail($"after reopen: {again.Name}, {events.Count} events");
    }
    await using (var journal = await StepJournal.OpenAsync(path))
    {
        var reset = await journal.ResetAsync("flow", "step");
        var rerun = await journal.RunAsync("flow", "step", (c, _) => Task.FromResult(new Made($"made-{c.Attempt}-{c.Resumed}")), SmokeJson.Default.Made);
        if (!reset || rerun.Name != "made-1-False" || await journal.ResetAsync("flow", "missing"))
            return Fail($"after reset: {reset}, {rerun.Name}");
    }
    Console.WriteLine("ok Switchknife.Journal");
    return 0;
}
finally
{
    Directory.Delete(dir, recursive: true);
}

static int Fail(string why)
{
    Console.Error.WriteLine($"fail Switchknife.Journal: {why}");
    return 1;
}

internal sealed record Made(string Name);

[JsonSerializable(typeof(Made))]
internal sealed partial class SmokeJson : JsonSerializerContext;
