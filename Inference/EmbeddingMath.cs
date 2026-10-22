using LLama.Native;

namespace LLMHostProject.Inference;

/// <summary>Vector helpers for embeddings and the pooling option.</summary>
public static class EmbeddingMath
{
    /// <summary>Optionally cuts the vector to <paramref name="dimensions"/> values (Matryoshka-style models), then scales it to length 1.</summary>
    public static float[] Finish(float[] v, int? dimensions, bool normalize)
    {
        if (dimensions is > 0 && dimensions < v.Length) v = v[..dimensions.Value];
        else v = (float[])v.Clone();
        if (normalize) L2(v);
        return v;
    }

    public static void L2(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        double norm = Math.Sqrt(sum);
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }

    /// <summary>The OpenAI <c>encoding_format: "base64"</c> form: little-endian float32 values.</summary>
    public static string ToBase64(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian) for (int i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
        return Convert.ToBase64String(bytes);
    }

    public static float[] FromBase64(string s)
    {
        var bytes = Convert.FromBase64String(s);
        if (!BitConverter.IsLittleEndian) for (int i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
        var v = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static readonly string[] PoolingNames = { "auto", "none", "mean", "cls", "last", "rank" };

    /// <summary>The pooling to give the context: "auto" leaves it to the model file, except for language models, which have none and get mean pooling.</summary>
    public static LLamaPoolingType PoolingFor(string? option, bool modelHasPooling, bool reranker = false) => (option ?? "auto").Trim().ToLowerInvariant() switch
    {
        "none" => LLamaPoolingType.None,
        "mean" => LLamaPoolingType.Mean,
        "cls" => LLamaPoolingType.CLS,
        "last" => LLamaPoolingType.Last,
        "rank" => LLamaPoolingType.Rank,
        _ => reranker ? LLamaPoolingType.Rank : modelHasPooling ? LLamaPoolingType.Unspecified : LLamaPoolingType.Mean,
    };
}
