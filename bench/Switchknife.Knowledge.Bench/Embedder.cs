using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Switchknife.Knowledge.Bench;

/// <summary>Embeds texts through an OpenAI-compatible endpoint; the key stays in the process, never in a file or an argument.</summary>
internal sealed class Embedder(Uri baseUrl, string key, string model, int dimensions) : IDisposable
{
    private const int Batch = 64;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public string Model { get; } = model;

    public int Dimensions { get; } = dimensions;

    /// <summary>The endpoint from <c>KNOWLEDGE_EMBED_URL</c> (with its version path) and the key from <c>KNOWLEDGE_EMBED_KEY</c>, or null when either is unset.</summary>
    public static Embedder? FromEnvironment(string model, int dimensions)
    {
        var url = Environment.GetEnvironmentVariable("KNOWLEDGE_EMBED_URL");
        var key = Environment.GetEnvironmentVariable("KNOWLEDGE_EMBED_KEY");
        return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) ? null : new Embedder(new Uri(url.EndsWith('/') ? url : url + "/"), key, model, dimensions);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts)
    {
        var all = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += Batch)
        {
            var input = texts.Skip(i).Take(Batch).ToList();
            var payload = new Dictionary<string, object> { ["model"] = Model, ["input"] = input };
            if (Dimensions > 0)
            {
                payload["dimensions"] = Dimensions;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "embeddings"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"embeddings request failed: HTTP {(int)response.StatusCode}.");
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
            var rows = doc.RootElement.GetProperty("data").EnumerateArray().OrderBy(r => r.GetProperty("index").GetInt32())
                .Select(r => r.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToList();
            if (rows.Count != input.Count)
            {
                throw new InvalidOperationException($"asked for {input.Count} embeddings, got {rows.Count}.");
            }

            all.AddRange(rows);
        }

        return all;
    }

    public void Dispose() => _http.Dispose();
}
