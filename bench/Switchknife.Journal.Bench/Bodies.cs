using System.Text;
using System.Text.Json;

namespace Switchknife.Journal.Bench;

/// <summary>
/// Tool-call sized JSON bodies <c>{"tool","input","output"}</c>. The output is log-like text (words, paths, numbers), which
/// compresses about as well as real tool output; <c>incompressible</c> swaps it for random hex, the worst case for TOAST.
/// </summary>
internal sealed class Bodies
{
    private static readonly string[] Words =
    [
        "error", "warning", "build", "passed", "failed", "src", "tests", "restore", "package", "version", "Switchknife.Journal",
        "Npgsql", "async", "Task", "return", "await", "public", "class", "namespace", "using", "checked", "assembly", "line",
        "projects", "elapsed", "00:00:03.41", "dotnet", "main", "origin", "commit", "diff", "hunk", "index", "flow", "step",
    ];

    private readonly string[] _bodies;

    public Bodies(int sizeBytes, int distinct, bool incompressible, int seed)
    {
        var random = new Random(seed ^ sizeBytes);
        _bodies = new string[distinct];
        for (var i = 0; i < distinct; i++)
        {
            _bodies[i] = Make(sizeBytes, incompressible, random);
        }
    }

    public string this[long i] => _bodies[i % _bodies.Length];

    private static string Make(int sizeBytes, bool incompressible, Random random)
    {
        const int Overhead = 96;
        var text = new StringBuilder(sizeBytes);
        while (text.Length < Math.Max(sizeBytes - Overhead, 1))
        {
            if (incompressible)
            {
                text.Append(Convert.ToHexString(random.GetItems<byte>(Enumerable.Range(0, 256).Select(b => (byte)b).ToArray(), 32))).Append('\n');
            }
            else
            {
                var line = random.Next(6, 14);
                for (var w = 0; w < line; w++)
                {
                    text.Append(Words[random.Next(Words.Length)]).Append(w % 5 == 4 ? '/' : ' ');
                }

                text.Append(random.Next(1, 5000)).Append('\n');
            }
        }

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("tool", "Bash");
            json.WriteStartObject("input");
            json.WriteString("command", "dotnet build -c Release -warnaserror");
            json.WriteEndObject();
            json.WriteString("output", text.ToString(0, Math.Min(text.Length, Math.Max(sizeBytes - Overhead, 1))));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
