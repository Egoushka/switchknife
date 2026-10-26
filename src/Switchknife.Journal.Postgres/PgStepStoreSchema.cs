using System.Reflection;

namespace Switchknife.Journal.Postgres;

/// <summary>The SQL that creates <see cref="PgStepStore"/>'s tables, for a host that does not use Switchknife.Migrations.</summary>
public static class PgStepStoreSchema
{
    /// <summary>The text of <c>Migrations/0001_steps.sql</c>, embedded in this assembly: the one script to run before a <see cref="PgStepStore"/> is used.</summary>
    public static string Sql { get; } = ReadEmbedded();

    private static string ReadEmbedded()
    {
        var assembly = typeof(PgStepStoreSchema).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0001_steps.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
