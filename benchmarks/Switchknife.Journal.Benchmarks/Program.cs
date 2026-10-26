using BenchmarkDotNet.Running;
using Switchknife.Journal.Benchmarks;

BenchmarkRunner.Run<RunAsyncBenchmarks>();

namespace Switchknife.Journal.Benchmarks
{
    using System.Text.Json.Serialization;
    using BenchmarkDotNet.Attributes;

    /// <summary>
    /// RunAsync on a key already completed (cache hit: no step runs, the recorded result comes back) against a key
    /// never seen before (first attempt: intent recorded, the step runs, result recorded). Against SqliteStepStore;
    /// a PgStepStore run needs a reachable PostgreSQL server, which this benchmark does not provide.
    /// </summary>
    [MemoryDiagnoser]
    public class RunAsyncBenchmarks
    {
        private string _path = null!;
        private StepJournal _journal = null!;
        private int _newKeyCounter;

        [GlobalSetup]
        public async Task SetupAsync()
        {
            _path = Path.Combine(Path.GetTempPath(), $"switchknife-bench-{Guid.NewGuid():N}.db");
            _journal = await StepJournal.OpenAsync(_path).ConfigureAwait(false);
            await _journal.RunAsync("bench", "cache-hit", (_, _) => Task.FromResult(new Made("done")), BenchJson.Default.Made).ConfigureAwait(false);
        }

        [GlobalCleanup]
        public async Task CleanupAsync()
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
            File.Delete(_path);
        }

        [Benchmark(Baseline = true)]
        public Task<Made> CacheHit() =>
            _journal.RunAsync("bench", "cache-hit", (_, _) => Task.FromResult(new Made("done")), BenchJson.Default.Made);

        [Benchmark]
        public Task<Made> NewKey() =>
            _journal.RunAsync("bench", $"new-{Interlocked.Increment(ref _newKeyCounter)}", (_, _) => Task.FromResult(new Made("done")), BenchJson.Default.Made);
    }

    public sealed record Made(string Name);

    [JsonSerializable(typeof(Made))]
    public sealed partial class BenchJson : JsonSerializerContext;
}
