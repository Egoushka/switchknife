using Switchknife.Facts;
using Switchknife.Knowledge.Ideas;

namespace Switchknife.Knowledge.Tests;

/// <summary>Spec <c>docs/specs/2026-11-01-knowledge-k3-ideas.md</c>, checks I12-I15.</summary>
public sealed class GitHubIssueTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Webhook =
        """
        {"action":"edited","issue":{"number":12,"title":"Grade ideas","body":"A tool\r\nthat grades","labels":[{"name":"idea"}],"comments":3,
         "repository_url":"https://api.github.com/repos/someone/other","updated_at":"2026-01-02T10:00:00Z"},
         "repository":{"full_name":"owner/repo"}}
        """;

    private const string Rest =
        """
        {"number":12,"title":"Grade ideas","body":"A tool\nthat grades","state":"open","labels":[],
         "repository_url":"https://api.github.com/repos/owner/repo","updated_at":"2026-01-03T10:00:00Z"}
        """;

    private static readonly IdeaDraft Complete = new(
        IdeaStatus.Testing,
        ValueKind.Money,
        new ValueHypothesis("freelancers", "save 2 h a week", "3 interviews", ExpectedUahPerMonth: 5000m),
        new CheapestTest("landing page", CapHours: 6m, CapUah: 0m, "signups", "10 in 14 days"));

    private static IssueSeed Seed(string title, string? body) => GitHubIssueAdapter.ToSeed(new GitHubIssue("owner/repo", 12, title, body));

    [Fact]
    public void I12_hash_follows_title_and_body_only()
    {
        var hash = GitHubIssueAdapter.Hash("Grade ideas", "A tool");

        Assert.Equal(hash, GitHubIssueAdapter.Hash("Grade ideas", "A tool"));
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(hash, GitHubIssueAdapter.Hash("Grade ideas!", "A tool"));
        Assert.NotEqual(hash, GitHubIssueAdapter.Hash("Grade ideas", "A tool."));
        Assert.NotEqual(GitHubIssueAdapter.Hash("ab", null), GitHubIssueAdapter.Hash("a", "b"));
        Assert.Equal(GitHubIssueAdapter.Hash("t", null), GitHubIssueAdapter.Hash("t", ""));
    }

    [Fact]
    public void I12_labels_comments_and_dates_do_not_change_the_hash()
    {
        var webhook = GitHubIssueAdapter.FromPayload(Webhook)!;
        var rest = GitHubIssueAdapter.FromPayload(Rest)!;

        Assert.Equal(webhook.Episode.ContentHash, rest.Episode.ContentHash);
        Assert.NotEqual(webhook.Episode.OccurredAt, rest.Episode.OccurredAt);
    }

    [Fact]
    public void I15_payload_becomes_an_episode_and_a_seed_title()
    {
        var seed = GitHubIssueAdapter.FromPayload(Webhook, EpisodeTrust.Agent)!;

        Assert.Equal(new NewEpisode("github-issue", "owner/repo#12", GitHubIssueAdapter.Hash("Grade ideas", "A tool\nthat grades"),
            new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), EpisodeTrust.Agent), seed.Episode);
        Assert.Equal("github-issue:owner/repo#12", seed.ExternalRef);
        Assert.Equal("Grade ideas", seed.Title);
        Assert.Equal("owner/repo", GitHubIssueAdapter.Parse(Rest)!.Repository);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"number":3,"title":"PR","repository_url":"https://api.github.com/repos/o/r","pull_request":{"url":"x"}}""")]
    [InlineData("""{"number":3,"title":" ","repository_url":"https://api.github.com/repos/o/r"}""")]
    [InlineData("""{"number":0,"title":"t","repository_url":"https://api.github.com/repos/o/r"}""")]
    [InlineData("""{"number":3,"title":"t"}""")]
    public void I15_anything_but_an_issue_is_null(string json) => Assert.Null(GitHubIssueAdapter.FromPayload(json));

    [Fact]
    public void I15_the_package_has_no_http_client() =>
        Assert.DoesNotContain(typeof(GitHubIssueAdapter).Assembly.GetReferencedAssemblies(), a => a.Name == "System.Net.Http");

    [Fact]
    public async Task I13_the_same_issue_twice_writes_once()
    {
        await using var db = await postgres.NewAsync();
        db.At(1);

        var first = await db.Ideas.CaptureAsync(Seed("Grade ideas", "A tool"));
        db.At(2);
        var second = await db.Ideas.CaptureAsync(Seed("Grade ideas", "A tool"));

        Assert.False(first.Ingest.Duplicate);
        Assert.Equal(2, first.Ingest.Written);
        Assert.True(second.Ingest.Duplicate);
        Assert.Equal(first.Idea, second.Idea);
        Assert.Single(await db.Facts.HistoryAsync(first.Idea, IdeaPredicates.Title));
    }

    [Fact]
    public async Task I14_an_edit_retires_the_old_title_and_keeps_what_the_owner_set()
    {
        await using var db = await postgres.NewAsync();
        db.At(1);
        var idea = (await db.Ideas.CaptureAsync(Seed("Grade ideas", "A tool"))).Idea;
        db.At(2);
        Assert.True((await db.Ideas.MoveAsync(idea, Complete, new NewEpisode("item", "knowledge:idea:1", "h2"))).Accepted);
        db.At(3);

        var edited = await db.Ideas.CaptureAsync(Seed("Grade ideas fast", "A tool"));

        Assert.Equal(idea, edited.Idea);
        Assert.False(edited.Ingest.Duplicate);
        var live = (await db.Facts.AsOfAsync(idea, null, Harness.Day(3))).ToDictionary(f => f.Predicate, f => f.Value);
        Assert.Equal("Grade ideas fast", live[IdeaPredicates.Title]);
        Assert.Equal("testing", live[IdeaPredicates.Status]);
        Assert.Equal("money", live[IdeaPredicates.ValueKind]);
        Assert.Contains(IdeaPredicates.ValueHypothesis, live.Keys);
        Assert.Contains(IdeaPredicates.CheapestTest, live.Keys);
        var titles = await db.Facts.HistoryAsync(idea, IdeaPredicates.Title);
        Assert.DoesNotContain(titles, v => v.Fact.Value == "Grade ideas" && v.RetiredAt is null);
        Assert.Contains(titles, v => v.Fact.Value == "Grade ideas" && v.RetiredReason == "source_changed");
    }

    [Fact]
    public async Task I14_an_edited_seed_stays_a_seed()
    {
        await using var db = await postgres.NewAsync();
        db.At(1);
        var idea = (await db.Ideas.CaptureAsync(Seed("Grade ideas", "A tool"))).Idea;
        db.At(2);

        await db.Ideas.CaptureAsync(Seed("Grade ideas", "A tool, now with a body"));

        var live = (await db.Facts.AsOfAsync(idea, null, Harness.Day(2))).ToDictionary(f => f.Predicate, f => f.Value);
        Assert.Equal("seed", live[IdeaPredicates.Status]);
        Assert.Equal("Grade ideas", live[IdeaPredicates.Title]);
    }
}
