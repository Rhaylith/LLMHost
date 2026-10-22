using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LLama.Native;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class EmbeddingUnitTests
{
    [Fact]
    public void FinishTruncatesThenNormalises()
    {
        var v = new float[] { 3, 4, 12, 0 };
        var full = EmbeddingMath.Finish(v, null, true);
        Assert.Equal(1.0, Math.Sqrt(full.Sum(x => (double)x * x)), 5);

        var two = EmbeddingMath.Finish(v, 2, true);                        // 3,4 -> 0.6, 0.8
        Assert.Equal(new[] { 0.6f, 0.8f }, two, new FloatComparer());

        var raw = EmbeddingMath.Finish(v, 2, false);
        Assert.Equal(new float[] { 3, 4 }, raw);
        Assert.Equal(new float[] { 3, 4, 12, 0 }, v);                      // the input is never modified
        Assert.Equal(4, EmbeddingMath.Finish(v, 99, false).Length);        // more dimensions than available: unchanged here, the API rejects it
    }

    private sealed class FloatComparer : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => Math.Abs(a - b) < 1e-5;
        public int GetHashCode(float f) => 0;
    }

    [Fact]
    public void ZeroVectorStaysZero() => Assert.All(EmbeddingMath.Finish(new float[3], null, true), x => Assert.Equal(0f, x));

    [Fact]
    public void Base64IsLittleEndianFloat32AndRoundTrips()
    {
        var v = new[] { 1.5f, -2f, 0f, 3.25e-3f };
        string b64 = EmbeddingMath.ToBase64(v);
        Assert.Equal(16, Convert.FromBase64String(b64).Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xC0, 0x3F }, Convert.FromBase64String(b64)[..4]);       // 1.5f
        Assert.Equal(v, EmbeddingMath.FromBase64(b64));
    }

    [Fact]
    public void CosineOfIdenticalAndOrthogonalVectors()
    {
        Assert.Equal(1.0, EmbeddingMath.Cosine(new float[] { 1, 2, 3 }, new float[] { 2, 4, 6 }), 6);
        Assert.Equal(0.0, EmbeddingMath.Cosine(new float[] { 1, 0 }, new float[] { 0, 1 }), 6);
        Assert.Equal(0.0, EmbeddingMath.Cosine(new float[] { 0, 0 }, new float[] { 1, 1 }), 6);
    }

    [Theory]
    [InlineData("auto", true, LLamaPoolingType.Unspecified)]
    [InlineData("auto", false, LLamaPoolingType.Mean)]
    [InlineData(null, false, LLamaPoolingType.Mean)]
    [InlineData("mean", true, LLamaPoolingType.Mean)]
    [InlineData("CLS", false, LLamaPoolingType.CLS)]
    [InlineData("last", false, LLamaPoolingType.Last)]
    [InlineData("rank", true, LLamaPoolingType.Rank)]
    [InlineData("none", true, LLamaPoolingType.None)]
    public void PoolingOptionMapsToTheContextSetting(string? option, bool modelHasPooling, LLamaPoolingType expected) =>
        Assert.Equal(expected, EmbeddingMath.PoolingFor(option, modelHasPooling));

    [Fact]
    public void InputsMayBeAStringOrAnArrayOfStrings()
    {
        Assert.Equal(new[] { "a" }, ApiServer.ReadEmbeddingInputs(JsonNode.Parse("""{"input":"a"}""")!));
        Assert.Equal(new[] { "a", "b" }, ApiServer.ReadEmbeddingInputs(JsonNode.Parse("""{"input":["a","b"]}""")!));
        Assert.Empty(ApiServer.ReadEmbeddingInputs(JsonNode.Parse("{}")!));
        Assert.Throws<ArgumentException>(() => ApiServer.ReadEmbeddingInputs(JsonNode.Parse("""{"input":[[1,2,3]]}""")!));
    }

    [Fact]
    public void RerankDocumentsAcceptStringsAndTextObjects()
    {
        Assert.Equal(new[] { "x", "y" }, ApiServer.ReadRerankDocuments(JsonNode.Parse("""{"documents":["x",{"text":"y"}]}""")!));
        Assert.Throws<ArgumentException>(() => ApiServer.ReadRerankDocuments(JsonNode.Parse("{}")!));
        Assert.Throws<ArgumentException>(() => ApiServer.ReadRerankDocuments(JsonNode.Parse("""{"documents":[5]}""")!));
    }

    [Fact]
    public void SettingsAreValidated()
    {
        var s = new AppSettings();
        s.DefaultLoad.Pooling = "sum"; s.DefaultLoad.EmbedBatchSize = -1; s.Rag.RerankCandidates = 1;
        var f = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("defaultLoad.pooling", f);
        Assert.Contains("defaultLoad.embedBatchSize", f);
        Assert.Contains("rag.rerankCandidates", f);
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
    }

    [Fact]
    public void EmbeddingDefaultsKeepTheOldBehaviour()
    {
        var o = new LoadOptions();
        Assert.Equal("auto", o.Pooling);
        Assert.True(o.Normalize);
        Assert.Equal(0, o.EmbedBatchSize);
    }
}

/// <summary>Runs only when LLMHOST_TEST_EMBED points at a small embedding GGUF (bge-small-en-v1.5).</summary>
public sealed class EmbedFactAttribute : FactAttribute
{
    public EmbedFactAttribute()
    {
        string? p = Environment.GetEnvironmentVariable("LLMHOST_TEST_EMBED");
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) Skip = "Set LLMHOST_TEST_EMBED to an embedding .gguf (e.g. bge-small-en-v1.5) to run this test.";
    }
}

/// <summary>Runs only when LLMHOST_TEST_RERANK points at a reranker GGUF (bge-reranker-v2-m3).</summary>
public sealed class RerankFactAttribute : FactAttribute
{
    public RerankFactAttribute()
    {
        string? p = Environment.GetEnvironmentVariable("LLMHOST_TEST_RERANK");
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) Skip = "Set LLMHOST_TEST_RERANK to a reranker .gguf (e.g. bge-reranker-v2-m3) to run this test.";
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class EmbeddingIntegrationTests
{
    private static LoadOptions Opts(uint ctx = 1024, bool embeddings = false) =>
        new() { Device = "cpu", ContextSize = ctx, MaxConcurrent = 1, CacheSlots = 1, Threads = 8, SkipFitCheck = true, Vision = false, Embeddings = embeddings };

    private static async Task<ModelRuntime> Load(string? path, LoadOptions o, string id)
    {
        var model = new ModelLibrary().Find(path!)!;
        return await ModelRuntime.LoadAsync(model, o, id, null, CancellationToken.None);
    }

    private static readonly string[] Texts =
    {
        "The quick brown fox jumps over the lazy dog.",
        "Paris is the capital of France.",
        "Embeddings turn text into vectors that can be compared.",
        "A short one.",
        string.Join(" ", Enumerable.Repeat("a long sentence about many things", 12)),
    };

    private static void AssertSame(float[] a, float[] b, double tolerance = 1e-4)
    {
        Assert.Equal(a.Length, b.Length);
        double cos = EmbeddingMath.Cosine(a, b);
        double maxDiff = a.Zip(b, (x, y) => (double)Math.Abs(x - y)).Max();
        // quantised kernels give slightly different rounding for different batch shapes (about 1e-3), so equality to 1e-4 is not possible; cross-talk would be far larger
        Assert.True(cos > 0.999, $"batched and single vectors differ: cosine {cos:F6}, largest difference {maxDiff:E2}");
    }

    private static void AssertSameStrict(float[] a, float[] b, double tolerance = 1e-4)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) <= tolerance, $"value {i} differs: {a[i]} vs {b[i]}");
    }

    private static void AssertBatchEqualsSingles(ModelRuntime rt)
    {
        var batch = rt.EmbedManyAsync(Texts).GetAwaiter().GetResult();
        Assert.Equal(Texts.Length, batch.Vectors.Length);
        for (int i = 0; i < Texts.Length; i++)
        {
            var single = rt.EmbedManyAsync(new[] { Texts[i] }).GetAwaiter().GetResult();
            AssertSame(single.Vectors[0], batch.Vectors[i]);
            Assert.Equal(single.Tokens[0], batch.Tokens[i]);
        }
    }

    [VlmFact]
    public async Task ABatchGivesTheSameVectorsAsOneByOneForAChatModel()
    {
        // a chat model asked for embeddings uses a separate context with mean pooling
        using var rt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM"), Opts(), "emb-chat");
        AssertBatchEqualsSingles(rt);
        var v = (await rt.EmbedManyAsync(Texts)).Vectors;
        Assert.All(v, x => Assert.Equal(1.0, Math.Sqrt(x.Sum(y => (double)y * y)), 3));              // normalised by default
        Assert.True(v[0].Length > 100);
    }

    [VlmFact]
    public async Task NormalisationDimensionsAndPoolingCanBeChosen()
    {
        var o = Opts(); o.Normalize = false;
        using var raw = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM"), o, "emb-raw");
        var unnormalised = (await raw.EmbedManyAsync(new[] { Texts[1] })).Vectors[0];
        Assert.True(Math.Abs(Math.Sqrt(unnormalised.Sum(y => (double)y * y)) - 1.0) > 0.01, "the vector should not be unit length when normalisation is off");

        var forced = (await raw.EmbedManyAsync(new[] { Texts[1] }, new ModelRuntime.EmbedOptions(true, null, true))).Vectors[0];
        Assert.Equal(1.0, Math.Sqrt(forced.Sum(y => (double)y * y)), 3);                              // a request can override

        var cut = (await raw.EmbedManyAsync(new[] { Texts[1] }, new ModelRuntime.EmbedOptions(true, 64, true))).Vectors[0];
        Assert.Equal(64, cut.Length);
        Assert.Equal(1.0, Math.Sqrt(cut.Sum(y => (double)y * y)), 3);

        var last = Opts(); last.Pooling = "last";
        using var lastRt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM"), last, "emb-last");
        var withLast = (await lastRt.EmbedManyAsync(new[] { Texts[1] }, new ModelRuntime.EmbedOptions(true, null, true))).Vectors[0];
        var withMean = (await raw.EmbedManyAsync(new[] { Texts[1] }, new ModelRuntime.EmbedOptions(true, null, true))).Vectors[0];
        Assert.True(EmbeddingMath.Cosine(withLast, withMean) < 0.9999, "last-token and mean pooling must give different vectors");
    }

    [VlmFact]
    public async Task ALongInputIsTruncatedOrRefused()
    {
        using var rt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM"), Opts(ctx: 256), "emb-long");
        string huge = string.Join(" ", Enumerable.Repeat("word", 2000));
        var ok = await rt.EmbedManyAsync(new[] { huge });
        Assert.Equal(256, ok.Tokens[0]);                                                              // cut to what one step can hold
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => rt.EmbedManyAsync(new[] { huge }, new ModelRuntime.EmbedOptions(false)));
        Assert.Contains("fit in one step", ex.Message);
    }

    [VlmFact]
    public async Task ARerankRequestFallsBackToSimilarityForAnEmbeddingOnlyModel()
    {
        using var rt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM"), Opts(), "emb-rerank-fallback");
        Assert.False(rt.IsCrossEncoder);
        var docs = new[] { "Bananas are yellow.", "Paris is the capital of France.", "Embeddings turn text into vectors." };
        var r = await rt.RerankAsync("Paris is the capital of France.", docs);
        Assert.Equal("embedding", r.Method);
        Assert.Equal(1, Array.IndexOf(r.Scores, r.Scores.Max()));                                      // the identical sentence wins
        Assert.Equal(1.0, r.Scores[1], 3);
    }

    [VlmFact]
    public async Task AnEmbeddingModelRefusesToChat()
    {
        var path = Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;
        using var mgr = new ModelManager(new ModelLibrary());
        await mgr.LoadAsync(path, Opts(embeddings: true));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.ResolveAsync(Path.GetFileNameWithoutExtension(path), embeddings: false));
        Assert.Contains("cannot generate text", ex.Message);
        Assert.Contains("/v1/embeddings", ex.Message);
        Assert.NotNull(await mgr.ResolveAsync(Path.GetFileNameWithoutExtension(path), embeddings: true));
    }

    // ---- a real embedding model (bge-small: BERT, CLS pooling taken from the model file)

    [EmbedFact]
    public async Task AnEmbeddingModelBatchesAndRanksByMeaning()
    {
        using var rt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_EMBED"), Opts(ctx: 512, embeddings: true), "bge-small");
        Assert.True(rt.Model.IsEmbedding);
        Assert.True(rt.Capabilities.Embedding);
        AssertBatchEqualsSingles(rt);

        var v = (await rt.EmbedManyAsync(new[]
        {
            "How do I bake bread at home?", "Steps for making homemade bread dough.", "The stock market fell sharply on Monday.",
        })).Vectors;
        Assert.Equal(384, v[0].Length);
        Assert.True(EmbeddingMath.Cosine(v[0], v[1]) > EmbeddingMath.Cosine(v[0], v[2]) + 0.1, "bread questions should be closer to bread instructions than to stock news");
    }

    [EmbedFact]
    public async Task AnInputThatFillsTheWholeWindowIsAccepted()
    {
        using var rt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_EMBED"), Opts(ctx: 512, embeddings: true), "bge-small-512");
        var r = await rt.EmbedManyAsync(new[] { string.Join(" ", Enumerable.Repeat("token", 1000)) });
        Assert.InRange(r.Tokens[0], 400, 512);
        Assert.Equal(384, r.Vectors[0].Length);
    }

    [EmbedFact]
    public async Task EmbeddingModelsCanBeUsedAsABiEncoderReranker()
    {
        using var rt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_EMBED"), Opts(ctx: 512, embeddings: true), "bge-small-rr");
        var docs = new[] { "Bananas are yellow fruit.", "The capital of France is Paris.", "The Pacific is the largest ocean." };
        var r = await rt.RerankAsync("What is the capital of France?", docs);
        Assert.Equal("embedding", r.Method);
        Assert.Equal(1, Array.IndexOf(r.Scores, r.Scores.Max()));
    }

    // ---- a real reranker (cross-encoder)

    private static readonly string[] RerankDocs =
    {
        "Bananas are a yellow fruit that grows in tropical regions.",
        "Paris is the capital and most populous city of France.",
        "The Pacific Ocean is the largest and deepest of Earth's oceans.",
        "Berlin is the capital of Germany.",
    };

    [RerankFact]
    public async Task ACrossEncoderOrdersDocumentsByRelevance()
    {
        using var rt = await Load(Environment.GetEnvironmentVariable("LLMHOST_TEST_RERANK"), Opts(ctx: 512, embeddings: true), "bge-reranker");
        Assert.True(rt.IsCrossEncoder);
        Assert.True(rt.Capabilities.Rerank);

        var r = await rt.RerankAsync("What is the capital of France?", RerankDocs);
        Assert.Equal("cross-encoder", r.Method);
        Assert.Equal(RerankDocs.Length, r.Scores.Length);
        Assert.Equal(1, Array.IndexOf(r.Scores, r.Scores.Max()));                                      // the Paris sentence
        Assert.True(r.Scores[1] > r.Scores[3], "Paris should beat the other capital");
        Assert.True(r.Scores[3] > r.Scores[0] && r.Scores[3] > r.Scores[2], "a capital city is more relevant than fruit or oceans");

        // the same documents in another order give the same scores (each pair is scored on its own)
        var reversed = await rt.RerankAsync("What is the capital of France?", RerankDocs.Reverse().ToArray());
        for (int i = 0; i < RerankDocs.Length; i++) Assert.InRange(Math.Abs(r.Scores[i] - reversed.Scores[RerankDocs.Length - 1 - i]), 0, 0.15);
    }

    [RerankFact]
    public async Task ARerankerCannotChatAndAnswersTheRerankEndpoint()
    {
        using var mgr = new ModelManager(new ModelLibrary());
        var path = Environment.GetEnvironmentVariable("LLMHOST_TEST_RERANK")!;
        var rt = await mgr.LoadAsync(path, Opts(ctx: 512, embeddings: true));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.ResolveAsync(rt.Identifier, embeddings: false));
        Assert.Contains("reranking model", ex.Message);
        Assert.Contains("/v1/rerank", ex.Message);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class EmbeddingApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public EmbeddingApiTests(ServerFixture f) => _f = f;

    private async Task<string> Load(string envVar, string id)
    {
        var path = Environment.GetEnvironmentVariable(envVar)!;
        var rt = _f.Host.Models.Loaded.FirstOrDefault(r => r.Identifier == id)
                 ?? await _f.Host.Models.LoadAsync(path, new LoadOptions { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 8, SkipFitCheck = true, Vision = false, Embeddings = true, Identifier = id });
        return rt.Identifier;
    }

    [EmbedFact]
    public async Task EmbeddingsEndpointHonoursDimensionsBase64AndTruncate()
    {
        string model = await Load("LLMHOST_TEST_EMBED", "api-bge-small");
        var plain = await _f.Http.PostAsJsonAsync("/v1/embeddings", new { model, input = new[] { "hello world", "goodbye" } });
        var j = JsonNode.Parse(await plain.Content.ReadAsStringAsync())!;
        Assert.Equal(2, j["data"]!.AsArray().Count);
        Assert.Equal(new[] { 0, 1 }, j["data"]!.AsArray().Select(d => (int)d!["index"]!).ToArray());
        Assert.Equal(384, j["data"]![0]!["embedding"]!.AsArray().Count);
        Assert.True((int)j["usage"]!["prompt_tokens"]! > 0);

        var small = JsonNode.Parse(await (await _f.Http.PostAsJsonAsync("/v1/embeddings", new { model, input = "hello world", dimensions = 64 })).Content.ReadAsStringAsync())!;
        var vec = small["data"]![0]!["embedding"]!.AsArray().Select(x => (float)x!).ToArray();
        Assert.Equal(64, vec.Length);
        Assert.Equal(1.0, Math.Sqrt(vec.Sum(x => (double)x * x)), 3);

        var b64 = JsonNode.Parse(await (await _f.Http.PostAsJsonAsync("/v1/embeddings", new { model, input = "hello world", encoding_format = "base64" })).Content.ReadAsStringAsync())!;
        var decoded = EmbeddingMath.FromBase64((string)b64["data"]![0]!["embedding"]!);
        var first = j["data"]![0]!["embedding"]!.AsArray().Select(x => (float)x!).ToArray();
        Assert.Equal(first.Length, decoded.Length);
        Assert.True(Math.Abs(first[0] - decoded[0]) < 1e-5);

        var tooMany = await _f.Http.PostAsJsonAsync("/v1/embeddings", new { model, input = "x", dimensions = 5000 });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, tooMany.StatusCode);

        var tooLong = await _f.Http.PostAsJsonAsync("/v1/embeddings", new { model, input = string.Join(" ", Enumerable.Repeat("word", 3000)), truncate = false });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [RerankFact]
    public async Task RerankEndpointReturnsOrderedResultsInTheJinaShape()
    {
        string model = await Load("LLMHOST_TEST_RERANK", "api-reranker");
        var docs = new object[] { "Bananas are yellow.", new { text = "Paris is the capital of France." }, "The Pacific is an ocean." };
        foreach (var path in new[] { "/v1/rerank", "/rerank" })
        {
            var r = await _f.Http.PostAsJsonAsync(path, new { model, query = "capital of France", documents = docs, top_n = 2 });
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
            Assert.True(r.IsSuccessStatusCode, j.ToJsonString());
            var results = j["results"]!.AsArray();
            Assert.Equal(2, results.Count);                                                           // top_n
            Assert.Equal(1, (int)results[0]!["index"]!);                                              // the Paris document first
            Assert.Equal("Paris is the capital of France.", (string)results[0]!["document"]!["text"]!);
            Assert.True((double)results[0]!["relevance_score"]! >= (double)results[1]!["relevance_score"]!);
            Assert.Equal("cross-encoder", (string)j["method"]!);
        }

        var noDocs = JsonNode.Parse(await (await _f.Http.PostAsJsonAsync("/v1/rerank", new { model, query = "q", documents = new[] { "a", "b" }, return_documents = false })).Content.ReadAsStringAsync())!;
        Assert.Null(noDocs["results"]![0]!["document"]);

        var bad = await _f.Http.PostAsJsonAsync("/v1/rerank", new { model, documents = new[] { "a" } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
