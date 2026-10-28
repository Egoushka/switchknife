using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Testing;

/// <summary>An <see cref="IFactExtractor"/> that replays a script instead of calling a model: queued replies or errors, in order, with every request recorded.</summary>
public sealed class ScriptedExtractor : IFactExtractor
{
    private readonly Queue<Func<CancellationToken, Task<ExtractorResponse>>> _script = new();
    private readonly List<ExtractionRequest> _requests = [];

    /// <summary>
    /// Queues one step per argument: a <see cref="string"/> is a model reply (reported as extractor model <c>scripted</c>),
    /// an <see cref="ExtractorResponse"/> is returned as given, an <see cref="Exception"/> is thrown. The n-th call runs the n-th step;
    /// when the script runs out, the last step keeps answering.
    /// </summary>
    public ScriptedExtractor(params object[] script)
    {
        foreach (var step in script)
        {
            _script.Enqueue(step switch
            {
                string text => _ => Task.FromResult(new ExtractorResponse(text, new ExtractorInfo(null, "scripted"))),
                ExtractorResponse response => _ => Task.FromResult(response),
                Exception error => _ => throw error,
                _ => throw new ArgumentException("A script step is a reply, a response or an exception.", nameof(script)),
            });
        }
    }

    /// <summary>Answers every call with <paramref name="always"/>, which may wait on the token to simulate a slow or hanging model.</summary>
    public ScriptedExtractor(Func<CancellationToken, Task<ExtractorResponse>> always) => _script.Enqueue(always);

    /// <summary>How many times the extractor was called.</summary>
    public int Calls => Requests.Count;

    /// <summary>Every request in arrival order, to assert on the prompt the pipeline built.</summary>
    public IReadOnlyList<ExtractionRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    public Task<ExtractorResponse> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<ExtractorResponse>> step;
        lock (_requests)
        {
            _requests.Add(request);
            step = _script.Count > 1 ? _script.Dequeue() : _script.Peek();
        }

        return step(cancellationToken);
    }
}
