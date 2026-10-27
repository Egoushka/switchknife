using System.Reflection;

namespace Switchknife.Facts;

/// <summary>The SQL that creates the <c>facts</c> schema, for a host that does not run it through Switchknife.Migrations.</summary>
public static class FactsSchema
{
    /// <summary>The text of <c>Migrations/0001_facts.sql</c>, embedded in this assembly. Needs the <c>vector</c> and <c>btree_gist</c> extensions to be creatable by the connecting role.</summary>
    public static string Sql { get; } = ReadEmbedded();

    /// <summary>The assembly the scripts are embedded in, to hand to <c>DatabaseMigrator</c> beside the host's own scripts.</summary>
    public static Assembly Scripts => typeof(FactsSchema).Assembly;

    private static string ReadEmbedded()
    {
        var name = Scripts.GetManifestResourceNames().Single(n => n.EndsWith("0001_facts.sql", StringComparison.Ordinal));
        using var stream = Scripts.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
