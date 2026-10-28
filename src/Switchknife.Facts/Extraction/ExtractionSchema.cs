namespace Switchknife.Facts.Extraction;

/// <summary>
/// <see cref="ExtractedEpisode"/> as a JSON Schema in the strict form structured-output modes of model providers accept: no extra
/// properties, every property required, absent values written as null. Kept equal to the type by a test.
/// </summary>
public static class ExtractionSchema
{
    /// <summary>The schema (draft 2020-12 vocabulary, <c>$defs</c> for the two nested objects).</summary>
    public const string Json =
        """
        {"type":"object","additionalProperties":false,"required":["facts"],"properties":{"facts":{"type":"array","items":{"$ref":"#/$defs/fact"}}},"$defs":{"mention":{"type":"object","additionalProperties":false,"required":["ref","name","kind"],"properties":{"ref":{"type":["string","null"]},"name":{"type":["string","null"]},"kind":{"type":["string","null"]}}},"fact":{"type":"object","additionalProperties":false,"required":["subject","predicate","value","object","text","validFrom","validTo","replaces","evidence","quote","salience"],"properties":{"subject":{"$ref":"#/$defs/mention"},"predicate":{"type":"string"},"value":{"type":["string","null"]},"object":{"anyOf":[{"$ref":"#/$defs/mention"},{"type":"null"}]},"text":{"type":"string"},"validFrom":{"type":["string","null"]},"validTo":{"type":["string","null"]},"replaces":{"type":["string","null"]},"evidence":{"type":"array","items":{"type":"string"}},"quote":{"type":["string","null"]},"salience":{"type":["number","null"]}}}}}
        """;
}
