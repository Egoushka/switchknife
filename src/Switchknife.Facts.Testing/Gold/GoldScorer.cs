using System.Globalization;
using System.Text;

namespace Switchknife.Facts.Testing.Gold;

/// <summary>
/// Scores extracted facts against a gold set. The match rule is the one feed-eater froze for its fact gold set: an expected fact
/// matches an emitted fact of the same item when every anchor group has an alternative that is a substring of the normalised
/// subject, text, value and object; one emitted fact satisfies at most one expected fact; an item counts as recalled when at
/// least half of its expected facts match.
/// </summary>
public static class GoldScorer
{
    /// <summary>Scores <paramref name="results"/> (by item id) against <paramref name="gold"/>; an item without a result counts as nothing emitted.</summary>
    /// <param name="gold">The gold items.</param>
    /// <param name="results">What the extractor gave, by item id.</param>
    /// <param name="minSalience">Emitted facts with a lower salience are ignored; a fact without salience counts as 0.</param>
    public static GoldScore Score(IEnumerable<GoldItem> gold, IReadOnlyDictionary<string, ItemResult> results, double minSalience = 0)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(results);

        var rows = new List<ItemScore>();
        int itemsRecalled = 0, itemsTotal = 0, factsMatched = 0, factsExpected = 0, emittedAll = 0, emittedMatched = 0;
        int negativesClean = 0, negativesTotal = 0, kindOk = 0, kindTotal = 0, deadlineOk = 0, deadlineTotal = 0;

        foreach (var item in gold)
        {
            results.TryGetValue(item.Id, out var result);
            var facts = (result?.Facts ?? []).Where(f => (f.Salience ?? 0) >= minSalience).ToList();
            emittedAll += facts.Count;

            var used = new HashSet<int>();
            var missed = new List<string>();
            foreach (var expected in item.Facts)
            {
                var index = -1;
                for (var i = 0; i < facts.Count && index < 0; i++)
                {
                    index = !used.Contains(i) && Hits(expected, Blob(facts[i])) ? i : -1;
                }

                if (index < 0)
                {
                    missed.Add(expected.Claim);
                    continue;
                }

                used.Add(index);
                kindTotal++;
                kindOk += expected.Kind == facts[index].Predicate || (expected.KindAlternatives?.Contains(facts[index].Predicate ?? "") ?? false) ? 1 : 0;
                if (expected.Deadline is not null)
                {
                    deadlineTotal++;
                    deadlineOk += facts[index].ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == expected.Deadline ? 1 : 0;
                }
            }

            var matched = item.Facts.Count - missed.Count;
            emittedMatched += used.Count;
            var need = (item.Facts.Count + 1) / 2;
            bool? recalled = null;
            if (item.Negative)
            {
                negativesTotal++;
                negativesClean += facts.Count == 0 ? 1 : 0;
            }
            else
            {
                itemsTotal++;
                factsExpected += item.Facts.Count;
                factsMatched += matched;
                recalled = matched >= need;
                itemsRecalled += matched >= need ? 1 : 0;
            }

            rows.Add(new ItemScore(item.Id, item.Negative, item.Facts.Count, matched, need, recalled, facts.Count, missed, result?.Error));
        }

        return new GoldScore(
            itemsRecalled, itemsTotal, factsMatched, factsExpected, emittedAll, emittedMatched,
            emittedAll == 0 ? null : (double)emittedMatched / emittedAll,
            negativesClean, negativesTotal, kindOk, kindTotal, deadlineOk, deadlineTotal, rows);
    }

    private static bool Hits(GoldFact expected, string blob) =>
        expected.Match.All(group => group.Any(alternative => blob.Contains(Normalise(alternative), StringComparison.Ordinal)));

    private static string Blob(EmittedFact fact) =>
        Normalise(string.Join(' ', new[] { fact.Subject, fact.Text, fact.Value, fact.ObjectName }.Where(s => !string.IsNullOrEmpty(s))));

    private static string Normalise(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var raw in value.ToLowerInvariant())
        {
            var c = raw switch { '‑' or '–' => '-', '’' => '\'', _ => raw };
            if (c is '$' or ',')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }
}
