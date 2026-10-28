using System.Text.Json.Serialization;

namespace Switchknife.Facts.Bench;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LabelledEpisode))]
[JsonSerializable(typeof(ExtractionScore))]
[JsonSerializable(typeof(LabelSetReport))]
[JsonSerializable(typeof(RunReport))]
internal sealed partial class BenchJson : JsonSerializerContext;
