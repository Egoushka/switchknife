using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Switchknife.Facts;

namespace Switchknife.Knowledge.Ideas;

/// <summary>An issue as the adapter reads it. Title and body are the idea's text; labels and comments are not read.</summary>
/// <param name="Repository">The repository as <c>owner/name</c>.</param>
/// <param name="Number">The issue number.</param>
/// <param name="Title">The issue title.</param>
/// <param name="Body">The issue body; null when empty.</param>
/// <param name="UpdatedAt">When GitHub last changed the issue, for the episode's record.</param>
public sealed record GitHubIssue(string Repository, int Number, string Title, string? Body = null, DateTimeOffset? UpdatedAt = null);

/// <summary>What an issue becomes: the episode it is read as, and the title written as the seed's <c>title</c> fact under <see cref="ExternalRef"/>.</summary>
/// <param name="Episode">Source kind <c>github-issue</c>, ref <c>owner/name#n</c>, the hash of title and body.</param>
/// <param name="ExternalRef">The idea's external reference, so the same issue is always the same idea.</param>
/// <param name="Title">The value of the <c>title</c> fact.</param>
public sealed record IssueSeed(NewEpisode Episode, string ExternalRef, string Title);

/// <summary>
/// Turns a GitHub issue payload into an <see cref="IssueSeed"/> for <see cref="IdeaStore.CaptureAsync(IssueSeed, CancellationToken)"/>. Reads JSON only;
/// fetching the issue is the host's job. An edited title or body is a new version of the same source, so the store retires what only the old text supported.
/// </summary>
public static class GitHubIssueAdapter
{
    /// <summary>The episode source kind.</summary>
    public const string SourceKind = "github-issue";

    /// <summary>Reads a webhook <c>issues</c> event or a REST issue object; null when the JSON is neither, or is a pull request.</summary>
    public static GitHubIssue? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        IssuePayloadJson? root;
        try
        {
            root = JsonSerializer.Deserialize(json, GitHubJsonContext.Default.IssuePayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null)
        {
            return null;
        }

        var issue = root.Issue ?? root;
        var repository = root.Repository?.FullName ?? RepositoryFromUrl(issue.RepositoryUrl);
        if (issue.PullRequest is not null || issue.Number is not > 0 || string.IsNullOrWhiteSpace(issue.Title) || string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        return new GitHubIssue(repository, issue.Number.Value, issue.Title, string.IsNullOrEmpty(issue.Body) ? null : issue.Body, issue.UpdatedAt);
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the title and the body, line endings normalized. The same text gives the same hash; a change to
    /// either gives another. Labels, comments, state and dates are not part of it, so changing them is not a new version.
    /// </summary>
    public static string Hash(string title, string? body)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = Normalize(title) + "\0" + Normalize(body ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The seed for an issue.</summary>
    /// <param name="issue">The issue.</param>
    /// <param name="trust">How far the issue's author is believed; the owner's own repositories by default.</param>
    public static IssueSeed ToSeed(GitHubIssue issue, EpisodeTrust trust = EpisodeTrust.Owner)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(issue.Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(issue.Title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issue.Number);
        var source = $"{issue.Repository}#{issue.Number}";
        return new IssueSeed(
            new NewEpisode(SourceKind, source, Hash(issue.Title, issue.Body), issue.UpdatedAt, trust), $"{SourceKind}:{source}", issue.Title.Trim());
    }

    /// <summary><see cref="Parse"/> then <see cref="ToSeed"/>; null when the payload is not an issue.</summary>
    public static IssueSeed? FromPayload(string json, EpisodeTrust trust = EpisodeTrust.Owner) =>
        Parse(json) is { } issue ? ToSeed(issue, trust) : null;

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // REST issues carry https://api.github.com/repos/{owner}/{name}; webhook events carry repository.full_name instead.
    private static string? RepositoryFromUrl(string? url)
    {
        const string marker = "/repos/";
        var at = url?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return null;
        }

        return url![(at + marker.Length)..].TrimEnd('/').Split('/') is [{ Length: > 0 } owner, { Length: > 0 } name] ? $"{owner}/{name}" : null;
    }
}

internal sealed record IssuePayloadJson(
    int? Number = null,
    string? Title = null,
    string? Body = null,
    string? RepositoryUrl = null,
    DateTimeOffset? UpdatedAt = null,
    JsonElement? PullRequest = null,
    IssuePayloadJson? Issue = null,
    RepositoryJson? Repository = null);

internal sealed record RepositoryJson(string? FullName = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(IssuePayloadJson))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
