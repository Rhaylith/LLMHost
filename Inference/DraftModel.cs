using LLama;
using LLama.Common;
using LLama.Native;
using LLMHostProject.Core;
using LLMHostProject.Models;

namespace LLMHostProject.Inference;

/// <summary>
/// A small model that guesses the next few tokens for speculative decoding. It keeps its own KV cache for one sequence and
/// follows the target model's accepted text: whatever the target has confirmed is re-used, the rest is rolled back.
/// </summary>
public sealed class DraftModel : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _ctx;
    private readonly List<LLamaToken> _cached = new();
    private readonly LLamaBatch _batch = new();
    private readonly int _batchSize;

    public LocalModel Model { get; }
    public long SizeBytes => Model.SizeBytes;

    private DraftModel(LocalModel model, LLamaWeights weights, LLamaContext ctx, int batchSize)
    {
        Model = model; _weights = weights; _ctx = ctx; _batchSize = batchSize;
    }

    public static async Task<DraftModel> LoadAsync(LocalModel model, int gpuLayers, uint contextSize, int threads, CancellationToken ct)
    {
        var p = new ModelParams(model.Path)
        {
            ContextSize = contextSize,
            GpuLayerCount = gpuLayers,
            UseMemorymap = true,
            Threads = threads,
            BatchThreads = threads,
            BatchSize = 512,
            UBatchSize = 512,
            OpOffload = gpuLayers > 0,
        };
        var weights = await LLamaWeights.LoadFromFileAsync(p, ct);
        try { return new DraftModel(model, weights, weights.CreateContext(p), 512); }
        catch { weights.Dispose(); throw; }
    }

    /// <summary>
    /// Speculation only works when both models split text into the same tokens. Compares the vocabulary size and the text of a
    /// sample of tokens; returns a human-readable reason when they differ, null when they match.
    /// </summary>
    public static string? CheckVocabulary(LLamaWeights target, LLamaWeights draft)
    {
        int nt = target.Vocab.Count, nd = draft.Vocab.Count;
        // models of one family often pad the vocabulary differently; a small difference is fine
        if (Math.Abs(nt - nd) > 128)
            return $"the draft model's vocabulary ({nd} tokens) does not match the main model's ({nt} tokens)";
        int n = Math.Min(nt, nd);
        for (int i = 0; i < 512 && n > 0; i++)
        {
            int id = (int)((long)i * (n - 1) / 511);
            string a = target.Vocab.LLamaTokenToString((LLamaToken)id, true) ?? "";
            string b = draft.Vocab.LLamaTokenToString((LLamaToken)id, true) ?? "";
            if (a != b) return $"token {id} is '{a}' in the main model but '{b}' in the draft model (different tokenizers)";
        }
        return null;
    }

    public LLamaWeights Weights => _weights;

    /// <summary>
    /// Makes the draft's cache follow <paramref name="prefix"/> (everything the main model has accepted plus the token it is about to
    /// evaluate) and proposes up to <paramref name="count"/> tokens greedily. Stops early when the draft is unsure
    /// (top probability below <paramref name="minP"/>) or predicts an end-of-text token.
    /// </summary>
    public List<LLamaToken> Propose(IReadOnlyList<LLamaToken> prefix, int count, float minP)
    {
        var drafts = new List<LLamaToken>();
        if (prefix.Count == 0 || count <= 0) return drafts;

        // reuse what the draft already holds (keep at least one token to evaluate, so its logits exist)
        int common = 0, limit = Math.Min(_cached.Count, prefix.Count - 1);
        while (common < limit && (int)_cached[common] == (int)prefix[common]) common++;
        if (common < _cached.Count)
        {
            bool ok = false;
            if (common > 0)
            {
                _ctx.NativeHandle.MemorySequenceRemove((LLamaSeqId)0, common, -1);
                ok = (int)_ctx.NativeHandle.MemorySequenceMaxPosition((LLamaSeqId)0) == common - 1;
            }
            if (!ok) { _ctx.NativeHandle.MemoryClear(true); _cached.Clear(); common = 0; }
            else _cached.RemoveRange(common, _cached.Count - common);
        }

        for (int i = common; i < prefix.Count; i += _batchSize)
        {
            _batch.Clear();
            int stop = Math.Min(prefix.Count, i + _batchSize);
            for (int j = i; j < stop; j++) _batch.Add(prefix[j], j, (LLamaSeqId)0, j == prefix.Count - 1);
            if (_ctx.Decode(_batch) != DecodeResult.Ok) { Reset(); return drafts; }
        }
        _cached.Clear();
        _cached.AddRange(prefix);

        for (int k = 0; k < count; k++)
        {
            var (token, prob) = Argmax(_ctx.NativeHandle.GetLogitsIth(-1));
            if (prob < minP || token.IsEndOfGeneration(_weights.Vocab)) break;
            drafts.Add(token);
            if (k == count - 1) break;                       // the last guess is never needed as context
            _batch.Clear();
            _batch.Add(token, _cached.Count, (LLamaSeqId)0, true);
            if (_ctx.Decode(_batch) != DecodeResult.Ok) { Reset(); return drafts; }
            _cached.Add(token);
        }
        return drafts;
    }

    private void Reset()
    {
        try { _ctx.NativeHandle.MemoryClear(true); } catch { }
        _cached.Clear();
    }

    /// <summary>Most likely token and its probability (softmax of the logits).</summary>
    internal static (LLamaToken Token, float Prob) Argmax(ReadOnlySpan<float> logits)
    {
        int best = 0; float max = float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++) if (logits[i] > max) { max = logits[i]; best = i; }
        double sum = 0;
        for (int i = 0; i < logits.Length; i++) sum += Math.Exp(logits[i] - max);
        return ((LLamaToken)best, (float)(1.0 / sum));
    }

    public void Dispose()
    {
        try { _ctx.Dispose(); } catch { }
        try { _weights.Dispose(); } catch { }
    }
}
