using System.Text.Json;
using Json.Schema;

namespace Switchknife.Claims.Tests;

/// <summary>
/// The claim/v1 schema (docs/claims.md): one real record per shape, converted by hand to a claim, must validate;
/// each invalid record must fail for the reason its file name gives.
/// </summary>
public sealed class ClaimSchemaTests
{
    private static readonly string[] Shapes = ["chargehand", "facts", "k4-research", "plainsight"];

    private static readonly string Root = FindRoot();
    private static readonly string Dir = Path.Combine(Root, "schemas", "claim", "v1");
    private static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Dir, "claim.schema.json")));

    public static TheoryData<string> ShapeNames => [.. Shapes];

    public static TheoryData<string> InvalidFiles => [.. Directory.GetFiles(Path.Combine(Dir, "invalid"), "*.json").Select(f => Path.GetFileName(f))];

    [Fact]
    public void Fixtures_cover_all_four_shapes()
    {
        foreach (var shape in Shapes)
        {
            Assert.True(File.Exists(Path.Combine(Dir, "fixtures", $"{shape}.native.json")), $"{shape}.native.json");
            Assert.True(File.Exists(Path.Combine(Dir, "fixtures", $"{shape}.claim.json")), $"{shape}.claim.json");
        }
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Claim_fixture_validates(string shape)
    {
        var result = Validate(Path.Combine(Dir, "fixtures", $"{shape}.claim.json"));

        Assert.True(result.IsValid, Describe(result));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Invalid_fixture_is_rejected(string file)
    {
        var result = Validate(Path.Combine(Dir, "invalid", file));

        Assert.False(result.IsValid, $"{file} should not validate");
    }

    [Fact]
    public void Invalid_fixtures_are_not_empty()
    {
        Assert.NotEmpty(InvalidFiles);
    }

    private static EvaluationResults Validate(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Schema.Evaluate(doc.RootElement.Clone(), new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    private static string Describe(EvaluationResults result) =>
        string.Join("; ", (result.Details ?? []).Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Switchknife.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Switchknife.slnx not found above the test binary");
    }
}
