using System.Text.Json;
using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Tests.Extraction;

public sealed class ParserTests
{
    private const string Bare = """{"facts":[{"subject":{"ref":"e1","name":null,"kind":null},"predicate":"likes","value":"jazz","object":null,"text":"Anna likes jazz.","validFrom":null,"validTo":null,"replaces":null,"evidence":["l2"],"quote":null,"salience":0.5}]}""";

    private static string Json(ExtractedEpisode episode) => JsonSerializer.Serialize(episode, ExtractionJsonContext.Default.ExtractedEpisode);

    [Fact]
    public void Output_parses_from_plain_fenced_and_prefixed_json()
    {
        ExtractedEpisode? Parse(string text)
        {
            Assert.True(ExtractionParser.TryParse(text, out var episode, out var failure), text);
            Assert.Equal(ParseFailure.None, failure);
            return episode;
        }

        var plain = Parse(Bare)!;
        var fenced = Parse("```json\n" + Bare + "\n```")!;
        var prefixed = Parse("Here are the facts I found:\n" + Bare + "\nLet me know if you need more.")!;
        var extra = Parse(Bare.Replace("\"salience\":0.5", "\"salience\":0.5,\"mood\":\"calm\"", StringComparison.Ordinal))!;

        var fact = Assert.Single(plain.Facts!)!;
        Assert.Equal(("e1", "likes", "jazz", "l2"), (fact.Subject!.Ref, fact.Predicate, fact.Value, Assert.Single(fact.Evidence!)));
        Assert.Equal(Json(plain), Json(fenced));
        Assert.Equal(Json(plain), Json(prefixed));
        Assert.Equal(Json(plain), Json(extra));
    }

    [Theory]
    [InlineData("", ParseFailure.BadJson)]
    [InlineData("   ", ParseFailure.BadJson)]
    [InlineData("[]", ParseFailure.BadJson)]
    [InlineData("I could not find any facts.", ParseFailure.BadJson)]
    [InlineData("{\"facts\":[{\"predicate\":\"likes\"", ParseFailure.BadJson)]
    [InlineData("{}", ParseFailure.BadShape)]
    [InlineData("{\"facts\":null}", ParseFailure.BadShape)]
    [InlineData("{\"facts\":[null]}", ParseFailure.BadShape)]
    [InlineData("{\"facts\":{\"a\":1}}", ParseFailure.BadShape)]
    [InlineData("{\"facts\":[{\"evidence\":\"l2\"}]}", ParseFailure.BadShape)]
    [InlineData("{\"facts\":[{\"salience\":\"high\"}]}", ParseFailure.BadShape)]
    public void Malformed_or_misshapen_output_is_a_failure_with_a_code(string text, ParseFailure expected)
    {
        Assert.False(ExtractionParser.TryParse(text, out var episode, out var failure));
        Assert.Null(episode);
        Assert.Equal(expected, failure);
    }

    [Fact]
    public void A_null_text_is_a_bad_json_not_an_exception()
    {
        Assert.False(ExtractionParser.TryParse(null, out _, out var failure));
        Assert.Equal(ParseFailure.BadJson, failure);
    }

    [Fact]
    public void An_empty_facts_list_is_a_valid_output()
    {
        Assert.True(ExtractionParser.TryParse("{\"facts\":[]}", out var episode, out _));
        Assert.Empty(episode!.Facts!);
    }

    [Fact]
    public void The_json_schema_matches_the_output_type()
    {
        using var schema = JsonDocument.Parse(ExtractionSchema.Json);
        var root = schema.RootElement;
        var defs = root.GetProperty("$defs");

        // The wire names come from the serializer's own metadata, so a renamed or added property fails here.
        static string[] Wire(System.Text.Json.Serialization.Metadata.JsonTypeInfo info) => [.. info.Properties.Select(p => p.Name).Order(StringComparer.Ordinal)];
        static string[] Names(JsonElement properties) => [.. properties.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        static string[] Required(JsonElement element) => [.. element.GetProperty("required").EnumerateArray().Select(p => p.GetString()!).Order(StringComparer.Ordinal)];

        var episode = ExtractionJsonContext.Default.ExtractedEpisode;
        var fact = ExtractionJsonContext.Default.ExtractedFact;
        var mention = ExtractionJsonContext.Default.ExtractedMention;
        Assert.Equal(Wire(episode), Names(root.GetProperty("properties")));
        Assert.Equal(Wire(episode), Required(root));
        Assert.Equal(Wire(fact), Names(defs.GetProperty("fact").GetProperty("properties")));
        Assert.Equal(Wire(fact), Required(defs.GetProperty("fact")));
        Assert.Equal(Wire(mention), Names(defs.GetProperty("mention").GetProperty("properties")));
        Assert.Equal(Wire(mention), Required(defs.GetProperty("mention")));

        foreach (var node in new[] { root, defs.GetProperty("fact"), defs.GetProperty("mention") })
        {
            Assert.False(node.GetProperty("additionalProperties").GetBoolean());
        }

        // The schema accepts what the type serializes to: every property it names exists in a serialized sample.
        var sample = JsonSerializer.Serialize(new ExtractedEpisode([Fx.Likes]), ExtractionJsonContext.Default.ExtractedEpisode);
        using var written = JsonDocument.Parse(sample);
        var writtenFact = written.RootElement.GetProperty("facts")[0];
        Assert.Equal(Wire(fact), Names(writtenFact));
        Assert.True(ExtractionParser.TryParse(sample, out var again, out _));
        Assert.Equal(Json(new ExtractedEpisode([Fx.Likes])), Json(again!));
    }
}
