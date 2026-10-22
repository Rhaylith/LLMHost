using System.Text.RegularExpressions;
using LLMHostProject.Models;

namespace LLMHostProject.Core;

/// <summary>One model the first-run wizard suggests.</summary>
public record ModelSuggestion(string Slot, CatalogEntry Entry, long EstimatedBytes, string Reason);

/// <summary>What the first-run wizard recommends for this machine.</summary>
public static class Onboarding
{
    /// <summary>Billions of parameters from a catalog label such as "8B", "0.5B", "137M", "30B"; 0 when unknown.</summary>
    public static double ParamsBillions(string label)
    {
        var m = Regex.Match(label ?? "", @"^\s*([\d.]+)\s*([BMK])", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return 0;
        return char.ToUpperInvariant(m.Groups[2].Value[0]) switch { 'B' => n, 'M' => n / 1000, _ => n / 1_000_000 };
    }

    /// <summary>Memory a model needs once loaded at its recommended quant: about 0.6 bytes per parameter (Q4_K_M) or 1.1 (Q8_0), plus room for the context.</summary>
    public static long EstimateBytes(CatalogEntry e)
    {
        double b = ParamsBillions(e.Params);
        double perParam = e.RecommendedQuant.StartsWith("Q8", StringComparison.OrdinalIgnoreCase) ? 1.1 : 0.6;
        return (long)(b * 1e9 * perParam + 700L * 1024 * 1024);
    }

    /// <summary>
    /// The memory the wizard plans with: the biggest usable GPU memory, otherwise 60 % of the RAM that is free (the rest is for the system and the app).
    /// </summary>
    public static long Budget(HardwareInfo hw, long ramAvailable)
    {
        long gpu = hw.Gpus.Where(g => !g.Integrated).Select(g => g.UsableBytes).DefaultIfEmpty(0).Max();
        return Math.Max(gpu, (long)(ramAvailable * 0.6));
    }

    /// <summary>One small, one medium and one vision model that fit in <paramref name="budget"/> (a slot is left out when nothing fits).</summary>
    public static IReadOnlyList<ModelSuggestion> Recommend(long budget, IEnumerable<CatalogEntry> catalog)
    {
        var chat = catalog.Where(e => !e.IsVision && !e.Tags.Contains("embeddings") && !e.Tags.Contains("rerank") && ParamsBillions(e.Params) > 0).ToList();
        var result = new List<ModelSuggestion>();

        // small: a quick one for any machine, at most a quarter of the budget
        var small = chat.Where(e => EstimateBytes(e) <= budget && (EstimateBytes(e) <= budget / 4 || EstimateBytes(e) <= 2L << 30)).OrderByDescending(e => ParamsBillions(e.Params)).FirstOrDefault(e => ParamsBillions(e.Params) <= 4)
                    ?? chat.OrderBy(e => ParamsBillions(e.Params)).FirstOrDefault(e => EstimateBytes(e) <= budget);
        if (small != null) result.Add(new("small", small, EstimateBytes(small), "Fast and light: a good first model."));

        // medium: the strongest that still leaves room for a long conversation (about 70 % of the budget)
        var medium = chat.Where(e => EstimateBytes(e) <= budget * 0.7 && !e.Tags.Contains("tiny") && (small == null || e.Key != small.Key)).OrderByDescending(e => ParamsBillions(e.Params)).ThenBy(e => e.Key).FirstOrDefault();
        if (medium != null) result.Add(new("medium", medium, EstimateBytes(medium), "The strongest model that fits comfortably."));

        // vision: the largest picture model within half of the budget
        var vision = catalog.Where(e => e.IsVision && EstimateBytes(e) <= budget * 0.5).OrderByDescending(e => ParamsBillions(e.Params)).FirstOrDefault()
                     ?? catalog.Where(e => e.IsVision && EstimateBytes(e) <= budget).OrderBy(e => ParamsBillions(e.Params)).FirstOrDefault();
        if (vision != null) result.Add(new("vision", vision, EstimateBytes(vision), "Understands pictures."));
        return result;
    }
}
