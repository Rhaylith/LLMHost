using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LLMHostProject.Server;

/// <summary>Document attachments for chats: upload, and retrieval of the context to give the model.</summary>
public partial class ApiServer
{
    private readonly DocumentStore _documents = new();
    // chunk embeddings by "embedding model | text hash"
    private readonly ConcurrentDictionary<string, float[]> _chunkEmbeddings = new();

    private void MapDocuments(WebApplication app)
    {
        app.MapPost("/api/documents", async ctx =>
        {
            var body = await ReadBody(ctx);
            string name = body["name"]?.GetValue<string>() ?? throw new ArgumentException("'name' is required");
            string b64 = body["data"]?.GetValue<string>() ?? throw new ArgumentException("'data' (base64) is required");
            int comma = b64.IndexOf("base64,", StringComparison.Ordinal);
            if (comma >= 0) b64 = b64[(comma + 7)..];
            var doc = _documents.Add(name, Convert.FromBase64String(b64));
            await WriteJson(ctx, new { id = doc.Id, name = doc.Name, chars = doc.Text.Length, pages = doc.Pages });
        });

        app.MapGet("/api/documents/{id}", async ctx =>
        {
            var d = _documents.Get((string)ctx.Request.RouteValues["id"]!);
            if (d == null) { await WriteError(ctx, 404, "Document not found", "not_found"); return; }
            await WriteJson(ctx, new { id = d.Id, name = d.Name, chars = d.Text.Length, pages = d.Pages, text = d.Text });
        });

        app.MapDelete("/api/documents/{id}", async ctx =>
            await WriteJson(ctx, new { deleted = _documents.Delete((string)ctx.Request.RouteValues["id"]!) }));

        // what to put in front of the model for this question: the whole documents when they fit, else the best chunks
        app.MapPost("/api/documents/context", async ctx =>
        {
            var body = await ReadBody(ctx);
            var ids = (body["ids"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new List<string>();
            string query = body["query"]?.GetValue<string>() ?? "";
            var docs = ids.Select(i => _documents.Get(i)).Where(d => d != null).Select(d => d!).ToList();
            if (docs.Count == 0) { await WriteJson(ctx, new { mode = "none", text = "", citations = Array.Empty<object>(), tokens = 0 }); return; }

            // the chat model decides how much room there is
            string? modelId = body["model"]?.GetValue<string>();
            var chat = _host.Models.Loaded.FirstOrDefault(r => modelId != null && r.Identifier.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                       ?? _host.Models.Loaded.FirstOrDefault(r => !r.Model.IsEmbedding && !r.Options.Embeddings);
            int contextTokens = chat != null ? (int)chat.ContextSize : 4096;
            int budget = body["maxTokens"] is JsonValue mv && mv.TryGetValue<int>(out var mt) ? mt : Math.Max(400, (int)(contextTokens * 0.55));
            int Tokens(string s) => chat != null ? chat.CountTokens(s) : s.Length / 4 + 1;

            string full = string.Join("\n\n", docs.Select(d => $"### Document: {d.Name}\n{d.Text}"));
            int fullTokens = Tokens(full);
            if (fullTokens <= budget)
            {
                await WriteJson(ctx, new
                {
                    mode = "full", tokens = fullTokens,
                    text = "Reference documents are provided below. Use them to answer the user's questions.\n\n" + full,
                    citations = docs.Select((d, i) => new { n = i + 1, docId = d.Id, doc = d.Name, chunk = 0, preview = d.Text[..Math.Min(160, d.Text.Length)] }),
                });
                return;
            }

            // retrieval: embeddings when an embedding model is loaded, otherwise BM25
            var chunks = docs.SelectMany(d => Retrieval.Split(d)).ToList();
            double[] scores;
            string method = "bm25";
            var embedder = _host.Models.Loaded.FirstOrDefault(r => r.Model.IsEmbedding || r.Options.Embeddings);
            if (embedder != null && query.Trim().Length > 0)
            {
                try
                {
                    var qv = await embedder.EmbedAsync(query, ctx.RequestAborted);
                    scores = new double[chunks.Count];
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        string key = embedder.Identifier + "|" + chunks[i].DocId + "|" + chunks[i].Index;
                        if (!_chunkEmbeddings.TryGetValue(key, out var v))
                        {
                            v = await embedder.EmbedAsync(chunks[i].Text, ctx.RequestAborted);
                            if (_chunkEmbeddings.Count > 20000) _chunkEmbeddings.Clear();
                            _chunkEmbeddings[key] = v;
                        }
                        scores[i] = Retrieval.Cosine(qv, v);
                    }
                    method = "embeddings";
                }
                catch (Exception ex)
                {
                    LogBuffer.Add("documents", $"Embedding retrieval failed ({ex.Message}); using keyword search.");
                    scores = Retrieval.Bm25(chunks, query);
                }
            }
            else scores = Retrieval.Bm25(chunks, query);

            var order = Enumerable.Range(0, chunks.Count).OrderByDescending(i => scores[i]).ToList();
            var chosen = new List<int>();
            int used = 0;
            foreach (var i in order)
            {
                if (scores[i] <= 0 && chosen.Count >= 3) break;          // nothing relevant left
                int t = Tokens(chunks[i].Text) + 12;
                if (used + t > budget && chosen.Count > 0) continue;
                chosen.Add(i); used += t;
                if (used >= budget) break;
            }
            chosen.Sort();   // reading order
            var sb = new StringBuilder("Excerpts from the reference documents are provided below, each tagged [n]. Use them to answer; cite the tag(s) you rely on, like [1].\n\n");
            var cites = new List<object>();
            int n = 1;
            foreach (var i in chosen)
            {
                var c = chunks[i];
                sb.Append('[').Append(n).Append("] (").Append(c.DocName).Append(", part ").Append(c.Index).Append(")\n").Append(c.Text).Append("\n\n");
                cites.Add(new { n, docId = c.DocId, doc = c.DocName, chunk = c.Index, preview = c.Text[..Math.Min(160, c.Text.Length)] });
                n++;
            }
            await WriteJson(ctx, new { mode = "rag", method, tokens = used, text = sb.ToString().TrimEnd(), citations = cites, chunksTotal = chunks.Count });
        });
    }
}
