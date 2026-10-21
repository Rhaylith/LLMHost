using LLama.Native;
using LLama.Sampling;
using LLMHostProject.Core;

namespace LLMHostProject.Inference;

/// <summary>
/// Builds llama.cpp's native sampler chain from <see cref="GenerationOptions"/>:
/// <c>grammar → logit bias → [penalties, dry, top_n_sigma, top_k, typ_p, top_p, min_p, xtc, temperature] → pick</c>.
/// The bracketed stages run in <see cref="GenerationOptions.SamplerOrder"/> (default: the order shown). Mirostat replaces the
/// truncation and temperature stages; temperature &lt;= 0 picks the most likely token.
/// </summary>
public sealed class ConfigurableSamplingPipeline : BaseSamplingPipeline
{
    /// <summary>Names accepted in <see cref="GenerationOptions.SamplerOrder"/> (the llama-server names).</summary>
    public static readonly string[] StageNames =
        { "penalties", "dry", "top_n_sigma", "top_k", "typ_p", "top_p", "min_p", "xtc", "temperature" };

    private readonly GenerationOptions _g;
    private readonly string? _gbnf;
    private readonly uint _seed;

    public ConfigurableSamplingPipeline(GenerationOptions g, string? gbnf, uint seed)
    {
        _g = g; _gbnf = gbnf; _seed = seed;
    }

    /// <summary>The stages that will run, in order (unknown names dropped, duplicates removed; null/empty = default order).</summary>
    public static IReadOnlyList<string> EffectiveOrder(IEnumerable<string>? requested)
    {
        var order = new List<string>();
        foreach (var raw in requested ?? Array.Empty<string>())
        {
            string n = NormalizeStage(raw);
            if (StageNames.Contains(n) && !order.Contains(n)) order.Add(n);
        }
        return order.Count == 0 ? StageNames : order;
    }

    public static string NormalizeStage(string s) => s.Trim().ToLowerInvariant().Replace('-', '_') switch
    {
        "typical_p" or "typical" or "typ" => "typ_p",
        "temp" => "temperature",
        "repetition_penalty" or "penalty" => "penalties",
        var x => x,
    };

    /// <summary>Problems with the requested stage names (for validation messages).</summary>
    public static IEnumerable<string> UnknownStages(IEnumerable<string>? requested) =>
        (requested ?? Array.Empty<string>()).Where(s => !StageNames.Contains(NormalizeStage(s)));

    protected override SafeLLamaSamplerChainHandle CreateChain(SafeLLamaContextHandle context)
    {
        var chain = SafeLLamaSamplerChainHandle.Create(LLamaSamplerChainParams.Default());
        var model = context.ModelHandle;

        if (!string.IsNullOrWhiteSpace(_gbnf)) chain.AddGrammar(model, _gbnf, "root");

        if (_g.LogitBias is { Count: > 0 })
        {
            var bias = _g.LogitBias.Select(kv => new LLamaLogitBias { Token = kv.Key, Bias = kv.Value }).ToArray();
            chain.AddLogitBias(model.Vocab.Count, bias);
        }

        bool mirostat = _g.Mirostat is 1 or 2;
        foreach (var stage in EffectiveOrder(_g.SamplerOrder))
        {
            switch (stage)
            {
                case "penalties":
                    if (_g.RepeatPenalty != 1f || _g.FrequencyPenalty != 0f || _g.PresencePenalty != 0f)
                        chain.AddPenalties(_g.RepeatLastN, _g.RepeatPenalty, _g.FrequencyPenalty, _g.PresencePenalty);
                    break;
                case "dry":
                    if (_g.DryMultiplier > 0f)
                    {
                        int last = _g.DryPenaltyLastN < 0 ? (int)context.ContextSize : _g.DryPenaltyLastN;
                        var breakers = (_g.DrySequenceBreakers ?? new List<string> { "\n", ":", "\"", "*" }).ToArray();
                        chain.AddDry(model, breakers, _g.DryMultiplier, _g.DryBase, _g.DryAllowedLength, last);
                    }
                    break;
                case "top_n_sigma": if (!mirostat && _g.TopNSigma > 0f) chain.AddTopNSigma(_g.TopNSigma); break;
                case "top_k": if (!mirostat && _g.TopK > 0) chain.AddTopK(_g.TopK); break;
                case "typ_p": if (!mirostat && _g.TypicalP is > 0f and < 1f) chain.AddTypical(_g.TypicalP, 1); break;
                case "top_p": if (!mirostat && _g.TopP is > 0f and < 1f) chain.AddTopP(_g.TopP, 1); break;
                case "min_p": if (!mirostat && _g.MinP > 0f) chain.AddMinP(_g.MinP, 1); break;
                case "xtc":
                    if (!mirostat && _g.XtcProbability > 0f) chain.AddXTC(_g.XtcProbability, _g.XtcThreshold, 1, _seed);
                    break;
                case "temperature":
                    if (_g.Temperature > 0f) chain.AddTemperature(_g.Temperature);
                    break;
            }
        }

        if (_g.Temperature <= 0f) chain.AddGreedySampler();
        else if (_g.Mirostat == 1) chain.AddMirostat1Sampler(model.Vocab.Count, _seed, _g.MirostatTau, _g.MirostatEta, 100);
        else if (_g.Mirostat == 2) chain.AddMirostat2Sampler(_seed, _g.MirostatTau, _g.MirostatEta);
        else chain.AddDistributionSampler(_seed);
        return chain;
    }
}
