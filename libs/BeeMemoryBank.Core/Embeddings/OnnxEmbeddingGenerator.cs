using BeeMemoryBank.Core.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Semantic embedding generator using multilingual-e5-small (ONNX, 384-dim, quantized int8).
/// Requires model.onnx in the same directory as the executing assembly,
/// or a path specified via BMB_ONNX_MODEL_PATH environment variable.
///
/// <para>
/// E5 models are trained asymmetrically: text meant to be indexed (article/chunk content) needs a
/// <c>"passage: "</c> prefix, text meant to be searched-for (queries, and by extension symmetric
/// similarity comparisons like concept-tag matching) needs a <c>"query: "</c> prefix. Dropping the
/// prefix still produces a usable embedding, just at lower retrieval quality than the model is
/// capable of -- see intfloat/multilingual-e5-small's model card. <see cref="Generate"/> applies
/// the passage prefix; <see cref="GenerateQuery"/> applies the query prefix.
/// </para>
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator, IDisposable
{
    /// <summary>
    /// Bumped whenever the embedding model or tokenizer changes -- <see cref="EmbeddingProjectionService"/>
    /// and <see cref="Services.ConceptTagService"/> compare this against each row's stored
    /// <c>embedding_model_version</c> to flag stale embeddings (from a previous model) for
    /// re-generation. Both the old MiniLM model and this one happen to produce 384-dim vectors, so
    /// the dimension-based staleness check elsewhere would NOT have caught a swap between them --
    /// this string comparison is the only thing that does.
    /// </summary>
    public const string Version = "multilingual-e5-small-v1";

    private const string PassagePrefix = "passage: ";
    private const string QueryPrefix = "query: ";

    // WP-15: internal (not private) so ArticleChunker can size chunks to fit within one embedding
    // call without silent re-truncation by Encode below.
    internal const int MaxSequenceLength = 256;

    public int Dimension => 384;

    // Lazy: the ~113 MB model + ONNX runtime are loaded on the FIRST Generate() call, not at
    // construction. This keeps injecting IEmbeddingGenerator free — e.g. the WorkManager background-sync
    // worker pulls in EventApplier/ConceptTagService (which inject this) but only computes an embedding
    // for a concept-tag rename, so it almost never pays the load cost. Lazy<> is thread-safe.
    private readonly Lazy<InferenceSession> _session;
    private readonly Lazy<XlmRobertaTokenizer> _tokenizer = new(XlmRobertaTokenizer.LoadDefault);

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
                    $"Download multilingual-e5-small ONNX and place it at that path, " +
                    $"or set BMB_ONNX_MODEL_PATH environment variable.",
                    path);
            return new InferenceSession(path);
        });
    }

    public OnnxEmbeddingGenerator(byte[] modelBytes)
    {
        _session = new Lazy<InferenceSession>(() => new InferenceSession(modelBytes));
    }

    /// <summary>Embeds text meant to be indexed (article/chunk content). Applies the "passage: " prefix E5 requires.</summary>
    public float[] Generate(string text) => GenerateInternal(text, PassagePrefix);

    /// <summary>Embeds text meant to be searched-for (queries, or symmetric similarity comparisons). Applies the "query: " prefix E5 requires.</summary>
    public float[] GenerateQuery(string text) => GenerateInternal(text, QueryPrefix);

    private float[] GenerateInternal(string text, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[Dimension];

        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Value.Encode(prefix + text, MaxSequenceLength);
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
