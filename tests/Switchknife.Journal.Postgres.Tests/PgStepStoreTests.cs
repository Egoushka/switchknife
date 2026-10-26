using System.Text.Json.Serialization;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Switchknife.Journal.Postgres;
using Switchknife.Testing.Postgres;

namespace Switchknife.Journal.Postgres.Tests;

/// <summary>A PostgreSQL server migrated with <see cref="PgStepStoreSchema"/> plus a side-effects table transaction tests write to.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgresFixture() => Postgres = new PostgresDatabase("postgres:18-alpine", Migrate);

    public PostgresDatabase Postgres { get; }

    public async ValueTask InitializeAsync() => await Postgres.StartAsync();

    public ValueTask DisposeAsync() => Postgres.DisposeAsync();

    /// <summary>The running server; skips the test when Docker is missing outside CI.</summary>
    public PostgresDatabase Running()
    {
        Assert.SkipWhen(Postgres.Unavailable is not null, Postgres.Unavailable ?? "");
        return Postgres;
    }

    private static void Migrate(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            PgStepStoreSchema.Sql + "create table if not exists side_effects (flow text not null, key text not null);", connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// The journal's behaviour (completed keys never rerun, unfinished steps resume, reset, events, forget), the same
/// guarantees <c>SqliteStepStore</c> gives, now backed by <see cref="PgStepStore"/>. Each test uses its own flow name
/// so tests may run against the same server without a reset between them.
/// </summary>
public sealed class PgStepStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_completed_step_returns_its_recorded_result_without_running_again()
    {
        var postgres = fixture.Running();
        await using var journal = StepJournal.Open(new PgStepStore(postgres.DataSource));
        var runs = 0;
        Task<Made> Step(StepContext _, CancellationToken __) => Task.FromResult(new Made($"container-{++runs}", 7));

        var first = await journal.RunAsync("pg-batch", "prep/task-1", Step, PgTestJson.Default.Made);
        var second = await journal.RunAsync("pg-batch", "prep/task-1", Step, PgTestJson.Default.Made);

        Assert.Equal(new Made("container-1", 7), first);
        Assert.Equal(first, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_failed_step_records_the_error_and_the_next_run_is_a_fresh_attempt()
    {
        var postgres = fixture.Running();
        await using var journal = StepJournal.Open(new PgStepStore(postgres.DataSource));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.RunAsync("pg-failed", "handover/task-2", (_, _) => throw new InvalidOperationException("push rejected")));

        var failed = await journal.FindAsync("pg-failed", "handover/task-2");
        StepContext? seen = null;
        await journal.RunAsync("pg-failed", "handover/task-2", (context, _) =>
        {
            seen = context;
            return Task.CompletedTask;
        });

        Assert.Equal(StepState.Failed, failed!.State);
        Assert.Equal("InvalidOperationException: push rejected", failed.Error);
        Assert.Equal(new StepContext("pg-failed", "handover/task-2", 2, Resumed: false), seen);
    }

    [Fact]
    public async Task Resetting_a_completed_step_makes_the_next_run_a_first_attempt()
    {
        var postgres = fixture.Running();
        await using var journal = StepJournal.Open(new PgStepStore(postgres.DataSource));
        await journal.RunAsync("pg-reset", "prep/task-1", (_, _) => Task.CompletedTask);

        var reset = await journal.ResetAsync("pg-reset", "prep/task-1");
        var removed = await journal.FindAsync("pg-reset", "prep/task-1");
        StepContext? seen = null;
        await journal.RunAsync("pg-reset", "prep/task-1", (context, _) =>
        {
            seen = context;
            return Task.CompletedTask;
        });

        Assert.True(reset);
        Assert.Null(removed);
        Assert.Equal(new StepContext("pg-reset", "prep/task-1", 1, Resumed: false), seen);
        Assert.False(await journal.ResetAsync("pg-reset", "unknown-key"));
    }

    [Fact]
    public async Task Events_read_back_in_order_per_flow_from_any_point()
    {
        var postgres = fixture.Running();
        await using var journal = StepJournal.Open(new PgStepStore(postgres.DataSource));
        var a1 = await journal.AppendEventAsync("pg-events", "task_started", """{"task":"t1"}""");
        await journal.AppendEventAsync("pg-events-other", "task_started", """{"task":"x"}""");
        var a2 = await journal.AppendEventAsync("pg-events", "task_finished", """{"task":"t1"}""");

        var all = await journal.ReadEventsAsync("pg-events");
        var after = await journal.ReadEventsAsync("pg-events", after: a1);

        Assert.Equal([a1, a2], all.Select(e => e.Sequence));
        Assert.Equal(["task_started", "task_finished"], all.Select(e => e.Kind));
        Assert.Equal("""{"task":"t1"}""", Assert.Single(after).Payload);
    }

    [Fact]
    public async Task Forgetting_a_flow_removes_its_steps_and_events_and_nothing_else()
    {
        var postgres = fixture.Running();
        await using var journal = StepJournal.Open(new PgStepStore(postgres.DataSource));
        await journal.RunAsync("pg-forget", "s", (_, _) => Task.CompletedTask);
        await journal.AppendEventAsync("pg-forget", "k", "{}");
        await journal.RunAsync("pg-forget-other", "s", (_, _) => Task.CompletedTask);

        var removed = await journal.ForgetAsync("pg-forget");

        Assert.Equal(2, removed);
        Assert.Null(await journal.FindAsync("pg-forget", "s"));
        Assert.Empty(await journal.ReadEventsAsync("pg-forget"));
        Assert.NotNull(await journal.FindAsync("pg-forget-other", "s"));
    }

    [Fact]
    public async Task Unfinished_lists_only_steps_left_started()
    {
        var postgres = fixture.Running();
        var store = new PgStepStore(postgres.DataSource);
        await using var journal = StepJournal.Open(store);
        await journal.RunAsync("pg-unfinished", "done", (_, _) => Task.CompletedTask);
        await LeaveStarted(store, "pg-unfinished", "left");

        var all = await journal.UnfinishedAsync("pg-unfinished");

        Assert.Equal("left", Assert.Single(all).Key);
    }

    [Fact]
    public async Task A_lease_left_by_a_crashed_attempt_expires_and_the_next_claim_is_resumed()
    {
        var postgres = fixture.Running();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new StepOptions { Lease = TimeSpan.FromSeconds(30), Retries = 0 };
        await using var journal = StepJournal.Open(new PgStepStore(postgres.DataSource, clock, options));

        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.RunAsync("pg-crash", "xfer/run-9", async (_, ct) =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, cancel.Token));

        clock.Advance(TimeSpan.FromSeconds(31));

        StepContext? seen = null;
        await journal.RunAsync("pg-crash", "xfer/run-9", (context, _) =>
        {
            seen = context;
            return Task.CompletedTask;
        });

        Assert.Equal(new StepContext("pg-crash", "xfer/run-9", 2, Resumed: true), seen);
        Assert.Equal(StepState.Completed, (await journal.FindAsync("pg-crash", "xfer/run-9"))!.State);
    }

    [Fact]
    public async Task Two_concurrent_runs_of_the_same_key_across_stores_run_the_step_once()
    {
        var postgres = fixture.Running();
        var options = new StepOptions { Backoff = TimeSpan.FromMilliseconds(30) };
        await using var processA = StepJournal.Open(new PgStepStore(postgres.DataSource, options: options));
        await using var processB = StepJournal.Open(new PgStepStore(postgres.DataSource, options: options));

        var runs = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Made> Step(StepContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new Made("done", context.Attempt);
        }

        // The step holds its lease until released, so a claim by the other store meanwhile has to wait it out.
        var first = processA.RunAsync("pg-concurrent", "shared-key", Step, PgTestJson.Default.Made);
        var second = processB.RunAsync("pg-concurrent", "shared-key", Step, PgTestJson.Default.Made);
        await entered.Task;
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, runs);
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public async Task A_step_written_in_the_callers_transaction_commits_once_and_does_not_rerun()
    {
        var postgres = fixture.Running();
        var store = new PgStepStore(postgres.DataSource);
        const string flow = "pg-txn-commit";

        var connection = await postgres.DataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            var runs = 0;
            Task<Made> Step(StepContext context, NpgsqlTransaction tx, CancellationToken ct)
            {
                runs++;
                return Task.FromResult(new Made("committed", context.Attempt));
            }

            var transaction = await connection.BeginTransactionAsync();
            await using (transaction.ConfigureAwait(false))
            {
                var made = await store.RunInTransactionAsync(transaction, flow, "step", Step, PgTestJson.Default.Made);
                await transaction.CommitAsync();
                Assert.Equal(new Made("committed", 1), made);
            }

            var second = await connection.BeginTransactionAsync();
            await using (second.ConfigureAwait(false))
            {
                var again = await store.RunInTransactionAsync(second, flow, "step", Step, PgTestJson.Default.Made);
                await second.CommitAsync();
                Assert.Equal(1, runs);
                Assert.Equal(new Made("committed", 1), again);
            }
        }
    }

    [Fact]
    public async Task A_step_written_in_the_callers_transaction_rolls_back_with_it_and_leaves_no_record()
    {
        var postgres = fixture.Running();
        var store = new PgStepStore(postgres.DataSource);
        const string flow = "pg-txn-rollback";

        var connection = await postgres.DataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync();
            await using (transaction.ConfigureAwait(false))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.RunInTransactionAsync<Made>(transaction, flow, "step",
                    async (_, tx, ct) =>
                    {
                        var insert = new NpgsqlCommand("insert into side_effects (flow, key) values (@flow, @key)", connection, tx);
                        await using (insert.ConfigureAwait(false))
                        {
                            insert.Parameters.AddWithValue("flow", flow);
                            insert.Parameters.AddWithValue("key", "step");
                            await insert.ExecuteNonQueryAsync(ct);
                        }
                        throw new InvalidOperationException("boom");
                    }, PgTestJson.Default.Made));
                await transaction.RollbackAsync();
            }
        }

        Assert.Null(await store.FindAsync(flow, "step", CancellationToken.None));
        var checkConnection = await postgres.DataSource.OpenConnectionAsync();
        await using (checkConnection.ConfigureAwait(false))
        {
            var check = new NpgsqlCommand("select count(*) from side_effects where flow = @flow", checkConnection);
            await using (check.ConfigureAwait(false))
            {
                check.Parameters.AddWithValue("flow", flow);
                Assert.Equal(0L, await check.ExecuteScalarAsync());
            }
        }
    }

    private static async Task LeaveStarted(PgStepStore store, string flow, string key)
    {
        await using var journal = StepJournal.Open(store);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.RunAsync(flow, key, async (_, ct) =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, cancel.Token));
    }
}

public sealed record Made(string Name, int Attempt);

[JsonSerializable(typeof(Made))]
internal sealed partial class PgTestJson : JsonSerializerContext;
