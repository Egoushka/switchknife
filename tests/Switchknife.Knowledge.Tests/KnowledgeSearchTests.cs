using Npgsql;
using Switchknife.Facts;
using Switchknife.Testing.Postgres;

namespace Switchknife.Knowledge.Tests;

/// <summary>Spec <c>docs/adr/0005-knowledge-kit.md</c>, K0: a sentence finds entities, not single facts, across namespaces.</summary>
public sealed class KnowledgeSearchTests : IAsyncLifetime
{
    private readonly PostgresDatabase _postgres = new("postgres:18-alpine");

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    private async Task<NpgsqlDataSource> NewAsync()
    {
        Assert.SkipWhen(_postgres.Unavailable is not null, _postgres.Unavailable ?? "");
        var data = NpgsqlDataSource.Create(_postgres.ConnectionStringFor(await _postgres.CreateDatabaseAsync()));
        await using var command = data.CreateCommand(FactsSchema.Sql);
        await command.ExecuteNonQueryAsync();
        return data;
    }

    private static async Task SeedAsync(NpgsqlDataSource data, string @namespace, string kind, string name, params string[] texts)
    {
        var store = new FactsStore(data, @namespace);
        await store.DefinePredicateAsync("note", Cardinality.Many);
        var entity = await store.EnsureEntityAsync(kind, name);
        await store.IngestAsync(
            new NewEpisode("test", $"{@namespace}/{name}", $"{@namespace}/{name}"),
            [.. texts.Select((t, i) => new NewFact(entity, "note", $"{name} {i}", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Text: t))]);
    }

    [Fact]
    public async Task A_sentence_question_finds_an_entity_where_the_AND_search_finds_nothing()
    {
        await using var data = await NewAsync();
        await SeedAsync(data, "prior-art", "tool", "Jitter", "Adds random delay to retries so many clients do not retry together.");
        await SeedAsync(data, "prior-art", "tool", "Bulkhead", "Isolates pools so one slow dependency cannot drain every thread.");
        const string Question = "How do we stop many clients from retrying a failing service at the same moment, causing a thundering herd?";

        var andSearch = await new FactsStore(data, "prior-art").SearchAsync(Question, default, "none");
        var hits = await new KnowledgeSearch(data, ["prior-art"]).SearchAsync(Question);

        Assert.Empty(andSearch);
        Assert.Equal("Jitter", hits[0].Name);
    }

    [Fact]
    public async Task Facts_of_one_entity_are_grouped_and_show_at_most_two()
    {
        await using var data = await NewAsync();
        await SeedAsync(data, "prior-art", "tool", "Outbox", "Outbox table written with the order, relayed to the broker.", "Outbox relay polls the table.", "Outbox rows are deleted after publish.");
        await SeedAsync(data, "prior-art", "tool", "Saga", "Saga coordinates steps.");

        var hits = await new KnowledgeSearch(data, ["prior-art"]).SearchAsync("outbox relay table publish");

        var outbox = Assert.Single(hits, h => h.Name == "Outbox");
        Assert.Equal(2, outbox.Facts.Count);
        Assert.Equal("Outbox", hits[0].Name);
    }

    [Fact]
    public async Task Namespaces_are_searched_apart_and_fused_by_rank()
    {
        await using var data = await NewAsync();
        await SeedAsync(data, "prior-art", "tool", "Cache", "Cache stampede protection by request coalescing.");
        await SeedAsync(data, "notes", "practice", "Coalescing", "Coalescing duplicate cache requests keeps the origin calm.");

        var hits = await new KnowledgeSearch(data, ["prior-art", "notes"]).SearchAsync("cache request coalescing");

        Assert.Equal(["notes", "prior-art"], hits.Select(h => h.Namespace).Order());
    }

    [Fact]
    public async Task A_question_with_no_searchable_word_and_no_vector_returns_nothing()
    {
        await using var data = await NewAsync();

        Assert.Empty(await new KnowledgeSearch(data, ["prior-art"]).SearchAsync("how is it"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task The_limit_is_one_to_one_hundred(int limit)
    {
        await using var data = await NewAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new KnowledgeSearch(data, ["prior-art"]).SearchAsync("outbox", limit: limit));
    }
}
