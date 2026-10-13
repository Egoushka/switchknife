using System.Text.Json.Serialization;

namespace Switchknife.Journal.Tests;

public sealed class StepJournalTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("switchknife-journal").FullName;
    private StepJournal _journal = null!;

    private string Path => System.IO.Path.Combine(_dir, "journal.db");

    public async ValueTask InitializeAsync() => _journal = await StepJournal.OpenAsync(Path);

    public async ValueTask DisposeAsync()
    {
        await _journal.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_completed_step_returns_its_recorded_result_without_running_again_even_after_a_reopen()
    {
        var runs = 0;
        Task<Made> Step(StepContext _, CancellationToken __) => Task.FromResult(new Made($"container-{++runs}", 7));

        var first = await _journal.RunAsync("batch-1", "prep/task-1", Step, TestJson.Default.Made);
        await Reopen();
        var second = await _journal.RunAsync("batch-1", "prep/task-1", Step, TestJson.Default.Made);

        Assert.Equal(new Made("container-1", 7), first);
        Assert.Equal(first, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task An_attempt_that_never_recorded_an_outcome_runs_again_resumed()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _journal.RunAsync("batch-1", "xfer/run-9", async (_, ct) =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, cancel.Token));
        await Reopen();

        StepContext? seen = null;
        await _journal.RunAsync("batch-1", "xfer/run-9", (context, _) =>
        {
            seen = context;
            return Task.CompletedTask;
        });

        Assert.Equal(new StepContext("batch-1", "xfer/run-9", 2, Resumed: true), seen);
        Assert.Equal(StepState.Completed, (await _journal.FindAsync("batch-1", "xfer/run-9"))!.State);
    }

    [Fact]
    public async Task A_failed_step_records_the_error_and_the_next_run_is_a_fresh_attempt()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _journal.RunAsync("batch-1", "handover/task-2", (_, _) => throw new InvalidOperationException("push rejected")));

        var failed = await _journal.FindAsync("batch-1", "handover/task-2");
        StepContext? seen = null;
        await _journal.RunAsync("batch-1", "handover/task-2", (context, _) =>
        {
            seen = context;
            return Task.CompletedTask;
        });

        Assert.Equal(StepState.Failed, failed!.State);
        Assert.Equal("InvalidOperationException: push rejected", failed.Error);
        Assert.Equal(new StepContext("batch-1", "handover/task-2", 2, Resumed: false), seen);
    }

    [Fact]
    public async Task Unfinished_lists_only_steps_left_started_and_can_narrow_to_one_flow()
    {
        await _journal.RunAsync("batch-1", "done", (_, _) => Task.CompletedTask);
        await LeaveStarted("batch-1", "left");
        await LeaveStarted("batch-2", "other");

        var all = await _journal.UnfinishedAsync();
        var one = await _journal.UnfinishedAsync("batch-2");

        Assert.Equal(["batch-1/left", "batch-2/other"], all.Select(s => $"{s.Flow}/{s.Key}").Order());
        Assert.Equal("other", Assert.Single(one).Key);
    }

    [Fact]
    public async Task Events_read_back_in_order_per_flow_from_any_point_and_survive_a_reopen()
    {
        var a1 = await _journal.AppendEventAsync("batch-1", "task_started", """{"task":"t1"}""");
        await _journal.AppendEventAsync("batch-2", "task_started", """{"task":"x"}""");
        var a2 = await _journal.AppendEventAsync("batch-1", "task_finished", """{"task":"t1"}""");
        await Reopen();

        var all = await _journal.ReadEventsAsync("batch-1");
        var after = await _journal.ReadEventsAsync("batch-1", after: a1);

        Assert.Equal([a1, a2], all.Select(e => e.Sequence));
        Assert.Equal(["task_started", "task_finished"], all.Select(e => e.Kind));
        Assert.Equal("""{"task":"t1"}""", Assert.Single(after).Payload);
    }

    [Fact]
    public async Task Forgetting_a_flow_removes_its_steps_and_events_and_nothing_else()
    {
        await _journal.RunAsync("batch-1", "s", (_, _) => Task.CompletedTask);
        await _journal.AppendEventAsync("batch-1", "k", "{}");
        await _journal.RunAsync("batch-2", "s", (_, _) => Task.CompletedTask);

        var removed = await _journal.ForgetAsync("batch-1");

        Assert.Equal(2, removed);
        Assert.Null(await _journal.FindAsync("batch-1", "s"));
        Assert.Empty(await _journal.ReadEventsAsync("batch-1"));
        Assert.NotNull(await _journal.FindAsync("batch-2", "s"));
    }

    [Fact]
    public async Task Steps_of_different_keys_may_run_at_once()
    {
        var keys = Enumerable.Range(0, 20).Select(i => $"task-{i}").ToList();

        var results = await Task.WhenAll(keys.Select(k =>
            _journal.RunAsync("batch-1", k, async (c, ct) => { await Task.Delay(5, ct); return new Made(c.Key, c.Attempt); }, TestJson.Default.Made)));

        Assert.Equal(keys, results.Select(r => r.Name));
        Assert.Empty(await _journal.UnfinishedAsync());
    }

    [Fact]
    public async Task A_journal_from_a_newer_version_is_refused()
    {
        await _journal.DisposeAsync();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => StepJournal.OpenAsync(Path));
        _journal = await StepJournal.OpenAsync(System.IO.Path.Combine(_dir, "other.db"));

        Assert.Contains("schema version 99", e.Message, StringComparison.Ordinal);
    }

    private async Task LeaveStarted(string flow, string key)
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _journal.RunAsync(flow, key, async (_, ct) =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, cancel.Token));
    }

    private async Task Reopen()
    {
        await _journal.DisposeAsync();
        _journal = await StepJournal.OpenAsync(Path);
    }
}

public sealed record Made(string Name, int Size);

[JsonSerializable(typeof(Made))]
internal sealed partial class TestJson : JsonSerializerContext;
