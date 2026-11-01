using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchknife.Knowledge.Ideas;

/// <summary>Source-generated serialization of the two JSON-valued predicates; no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ValueHypothesis))]
[JsonSerializable(typeof(CheapestTest))]
public sealed partial class IdeaJsonContext : JsonSerializerContext;

/// <summary>Reads and writes the values of <c>value_hypothesis</c> and <c>cheapest_test</c>.</summary>
public static class IdeaJson
{
    /// <summary>The fact value for <c>value_hypothesis</c>.</summary>
    public static string Write(ValueHypothesis value) => JsonSerializer.Serialize(value, IdeaJsonContext.Default.ValueHypothesis);

    /// <summary>The fact value for <c>cheapest_test</c>.</summary>
    public static string Write(CheapestTest value) => JsonSerializer.Serialize(value, IdeaJsonContext.Default.CheapestTest);

    /// <summary>Parses a <c>value_hypothesis</c> value; null when it is not valid.</summary>
    public static ValueHypothesis? ReadHypothesis(string json) => Read(json, IdeaJsonContext.Default.ValueHypothesis);

    /// <summary>Parses a <c>cheapest_test</c> value; null when it is not valid.</summary>
    public static CheapestTest? ReadTest(string json) => Read(json, IdeaJsonContext.Default.CheapestTest);

    private static T? Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
