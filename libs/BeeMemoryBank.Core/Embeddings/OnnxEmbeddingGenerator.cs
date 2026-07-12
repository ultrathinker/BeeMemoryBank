using BeeMemoryBank.Core.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Semantic embedding generator using all-MiniLM-L6-v2 (ONNX, 384-dim).
/// Requires model.onnx in the same directory as the executing assembly,
/// or a path specified via BMB_ONNX_MODEL_PATH environment variable.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator, IDisposable
{
    public const string Version = "minilm-l6-v2";
    private const int MaxSequenceLength = 256;

    public int Dimension => 384;

    // Lazy: the ~22 MB model + ONNX runtime are loaded on the FIRST Generate() call, not at
    // construction. This keeps injecting IEmbeddingGenerator free — e.g. the WorkManager background-sync
    // worker pulls in EventApplier/ConceptTagService (which inject this) but only computes an embedding
    // for a concept-tag rename, so it almost never pays the load cost. Lazy<> is thread-safe.
    private readonly Lazy<InferenceSession> _session;
    private readonly Lazy<BertWordPieceTokenizer> _tokenizer = new(LoadTokenizer);

    public OnnxEmbeddingGenerator(string? modelPath = null)
    {
        modelPath ??=
            Environment.GetEnvironmentVariable("BMB_ONNX_MODEL_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "model.onnx");

        var path = modelPath;
        _session = new Lazy<InferenceSession>(() =>
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"ONNX model not found at '{path}'. " +
                    $"Download all-MiniLM-L6-v2 ONNX and place it at that path, " +
                    $"or set BMB_ONNX_MODEL_PATH environment variable.",
                    path);
            return new InferenceSession(path);
        });
    }

    public OnnxEmbeddingGenerator(byte[] modelBytes)
    {
        _session = new Lazy<InferenceSession>(() => new InferenceSession(modelBytes));
    }

    private static BertWordPieceTokenizer LoadTokenizer()
    {
        var vocabStream = typeof(OnnxEmbeddingGenerator).Assembly
            .GetManifestResourceStream("BeeMemoryBank.Core.Embeddings.Models.vocab.txt")
            ?? throw new InvalidOperationException("Embedded vocab.txt not found in BeeMemoryBank.Core assembly.");

        using (vocabStream)
        {
            var vocab = BertWordPieceTokenizer.LoadVocab(vocabStream);
            return new BertWordPieceTokenizer(vocab);
        }
    }

    public float[] Generate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[Dimension];

        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Value.Encode(text, MaxSequenceLength);
        int seqLen = inputIds.Length;

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, seqLen]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, seqLen]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, seqLen]);

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
        };

        InferenceSession session;
        try
        {
            session = _session.Value;
        }
        catch (FileNotFoundException ex)
        {
            throw new ModelUnavailableException(ex.Message, ex);
        }

        using var results = session.Run(inputs);
        // Cast to DenseTensor to get a flat Span — avoids int[] allocation per element access
        var denseTensor = results[0].AsTensor<float>() as DenseTensor<float>
            ?? throw new InvalidOperationException("ONNX output is not a DenseTensor<float>.");
        var buffer = denseTensor.Buffer.Span;

        // Mean pooling: average token embeddings weighted by attention mask
        var embedding = new float[Dimension];
        float maskSum = 0f;

        for (int t = 0; t < seqLen; t++)
        {
            float mask = attentionMask[t];
            maskSum += mask;
            var row = buffer.Slice(t * Dimension, Dimension);
            for (int d = 0; d < Dimension; d++)
                embedding[d] += row[d] * mask;
        }

        if (maskSum > 0f)
            for (int d = 0; d < Dimension; d++)
                embedding[d] /= maskSum;

        // L2 normalization
        float norm = 0f;
        for (int d = 0; d < Dimension; d++) norm += embedding[d] * embedding[d];
        norm = MathF.Sqrt(norm);
        if (norm > 0f)
            for (int d = 0; d < Dimension; d++) embedding[d] /= norm;

        return embedding;
    }

    public void Dispose()
    {
        if (_session.IsValueCreated) _session.Value.Dispose();
    }
}
