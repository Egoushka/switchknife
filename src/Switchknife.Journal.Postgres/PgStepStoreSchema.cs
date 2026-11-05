using System.Reflection;

namespace Switchknife.Journal.Postgres;

/// <summary>The SQL that creates <see cref="PgStepStore"/>'s tables, for a host that does not use Switchknife.Migrations.</summary>
public static class PgStepStoreSchema
{
    /// <summary>
    /// Every script under <c>Migrations/</c>, embedded in this assembly, in name order (<c>0001_steps.sql</c>, then
    /// <c>0002_event_stream.sql</c>): the one script to run before a <see cref="PgStepStore"/> is used. Each is idempotent.
    /// </summary>
    public static string Sql { get; } = ReadEmbedded();

    private static string ReadEmbedded()
    {
        var assembly = typeof(PgStepStoreSchema).Assembly;
        var scripts = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            scripts.Add(reader.ReadToEnd());
        }
        return string.Join(Environment.NewLine, scripts);
    }
}
