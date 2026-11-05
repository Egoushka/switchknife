using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Switchknife.Knowledge.Bench;

/// <summary>
/// A BERT-style cross-encoder in ONNX (for example cross-encoder/ms-marco-MiniLM-L6-v2): scores each (question, passage) pair in one
/// batch on the CPU. The model directory holds <c>model.onnx</c> and <c>vocab.txt</c>; it is downloaded outside this repository.
/// </summary>
internal sealed class Reranker : IDisposable
{
    private const int MaxTokens = 512;
    private const int MaxQuestionTokens = 64;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _hasTokenTypes;

    public Reranker(string directory)
    {
        _session = new InferenceSession(Path.Combine(directory, "model.onnx"));
        _tokenizer = BertTokenizer.Create(Path.Combine(directory, "vocab.txt"), new BertOptions { LowerCaseBeforeTokenization = true });
        _hasTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public string Name { get; init; } = "cross-encoder";

    /// <summary>One relevance score per passage, in the passages' order; higher is better.</summary>
    public float[] Score(string question, IReadOnlyList<string> passages)
    {
        if (passages.Count == 0)
        {
            return [];
        }

        var q = _tokenizer.EncodeToIds(question, MaxQuestionTokens, addSpecialTokens: false, out _, out _);
        var pairs = passages.Select(p =>
        {
            var room = MaxTokens - q.Count - 3;
            var passage = _tokenizer.EncodeToIds(p, room, addSpecialTokens: false, out _, out _);
            return (Ids: _tokenizer.BuildInputsWithSpecialTokens(q, passage), Types: _tokenizer.CreateTokenTypeIdsFromSequences(q, passage));
        }).ToList();

        var width = pairs.Max(p => p.Ids.Count);
        var ids = new DenseTensor<long>([pairs.Count, width]);
        var mask = new DenseTensor<long>([pairs.Count, width]);
        var types = new DenseTensor<long>([pairs.Count, width]);
        for (var row = 0; row < pairs.Count; row++)
        {
            for (var col = 0; col < pairs[row].Ids.Count; col++)
            {
                ids[row, col] = pairs[row].Ids[col];
                mask[row, col] = 1;
                types[row, col] = pairs[row].Types[col];
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_hasTokenTypes)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
        }

        using var results = _session.Run(inputs);
        var logits = results[0].AsTensor<float>();
        var scores = new float[pairs.Count];
        for (var row = 0; row < pairs.Count; row++)
        {
            scores[row] = logits[row, 0];
        }

        return scores;
    }

    public void Dispose() => _session.Dispose();
}
