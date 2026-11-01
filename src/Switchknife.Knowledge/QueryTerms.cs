using System.Globalization;

namespace Switchknife.Knowledge;

/// <summary>The words of a question that can be searched: lower case letters and digits, no filler.</summary>
internal static class QueryTerms
{
    private const int MaxTerms = 24;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "what", "which", "when", "where", "how", "why", "who", "does", "did", "are", "was",
        "were", "been", "being", "have", "has", "had", "can", "could", "should", "would", "will", "not", "but", "its", "their", "there", "than", "then",
        "them", "they", "such", "each", "other", "any", "all", "one", "only", "also", "more", "most", "some", "use", "used", "using", "via", "per", "out",
    };

    /// <summary>Distinct terms of at least three characters, with a trailing plural <c>s</c> folded, in order of first use.</summary>
    public static IReadOnlyList<string> Of(string question)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var word = new System.Text.StringBuilder();
        foreach (var c in question.ToLower(CultureInfo.InvariantCulture) + " ")
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }

            if (word.Length > 0)
            {
                var term = word.ToString();
                word.Clear();
                if (term.Length >= 5 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal))
                {
                    term = term[..^1];
                }

                if (term.Length >= 3 && !Filler.Contains(term) && seen.Add(term) && terms.Count < MaxTerms)
                {
                    terms.Add(term);
                }
            }
        }

        return terms;
    }
}
