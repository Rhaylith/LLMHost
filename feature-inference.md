# Inference engine and hardware

How models are loaded and run. Design background and the list of deviations from the original plan: [implementation-plan.md](implementation-plan.md) §1 and §2. Back to the [README](README.md).

Everything runs in-process through LLamaSharp (llama.cpp). The core classes are `Inference/ModelManager.cs` (what is loaded, routing, JIT loading) and `Inference/ModelRuntime.cs` (one loaded model: context, scheduler, cache, sampling).

## Where you set these options

* **Web UI:** My Models → *Load* opens the load dialog. Advanced fields appear according to the [interface mode](feature-desktop-app.md#interface-modes).
* **CLI:** `llmhost load <model> [options]` (also `/load` in the shell). Flags are named below.
* **API:** `POST /api/models/load` or `POST /api/v1/models/load` with the same options as JSON.
* Defaults per model: the gear button in My Models, or "save current load settings as default". Applied automatically on the next load.

## Device placement and GPU share

`LoadOptions.GpuRatio` (0–1) sits next to `GpuLayers`. Precedence: exact `GpuLayers` > `GpuRatio` > `Device`. The layer count is worked out from the model at load time: round(ratio × (blocks + 1)), 999 (all) at 1.

* CLI: `load --gpu max|auto|off|0.5`, `--cpu`, `--gpu-layers N`.
* API: `gpu: "max" | "off" | "auto" | 0.5`.
* UI: a "share of the layers" slider with a readout such as `50% ≈ 17 of 33 layers`.
* The "will it fit?" estimate (`Inference/FitEstimator.cs`) uses the same layer count, so the warning and the real load agree.

Vulkan is the only GPU backend (AMD, NVIDIA, Intel). `config backend auto|vulkan|cpu` is process-wide.

## Threads, affinity, priority, NUMA

`Core/CpuTopology.cs` reads the Windows CPU sets (cores, processor groups, NUMA nodes). Generation threads default to the physical cores, prompt-processing threads to all logical processors, both limited by the affinity.

* Load options `Threads`, `BatchThreads`, `CpuAffinity` (`0-7,16`, `node:N`, `physical`, `all`) and `ThreadPriority` (`normal|high|low`).
* CLI: `--batch-threads`, `--cpu-affinity`, `--thread-priority`.
* `Inference/NativeThreadPolicy.cs` pins the engine's compute threads with `SetThreadSelectedCpuSets` (works across processor groups). The policy is process-wide; the last load wins.
* A **Test threads** button (`POST /api/models/benchmark-threads`) measures a few thread counts on the live context.

## Prompt cache

The KV cache is kept between requests, with several slots per model (`LoadOptions.CacheSlots`, 0 = parallel requests + 2). A new request reuses the slot with the longest shared prefix (at least 32 tokens), else an empty slot, else the least recently used. Only the changed tail is evaluated. Usage reports `cached_tokens`.

* Per request: `cache_prompt`. CLI: `/cache on|off|clear`. UI: checkbox.
* Saved caches: `POST /api/models/cache/save|restore`, `GET|DELETE /api/models/cache/saved` (`Inference/PromptCacheStore.cs`). `promptCache.autoSave` writes idle caches on unload and restores them on load; `promptCache.maxDiskMb` caps the folder.

## Speculative decoding

A second, smaller model proposes tokens that the main model verifies in the same batch (`Inference/DraftModel.cs`). Proposals are sampled with the request's own sampler, so grammar, penalties and seeds behave as without speculation; rejected tokens are removed from the cache. Used only while one request is generating and not for image prompts.

* Options `DraftModel`, `DraftTokens` (default 6), `DraftMinP` (0.5). CLI: `load --draft <model> --draft-tokens n`.
* The load dialog lists compatible drafts (`GET /api/models/drafts?model=`, `Models/DraftPairing.cs`: same architecture, vocabulary within 128 tokens, at most 1/6 of the size).
* Acceptance appears in `/api/status` and the native API stats.

## Prompt templates

`LoadOptions.PromptTemplate` overrides the model's chat template: `mode` auto | jinja | manual. The Jinja engine is written in-house (`Inference/Jinja.cs`) because no .NET library covers what chat templates need. If the override fails, the model's own template is used and a log line says so. A sample conversation is rendered at load and problems become load warnings.

* UI: "Prompt template" in the load dialog (Jinja editor, manual prefixes/suffixes, preview). CLI: `template [model]`, `load --template-file`.
* API: `GET /api/models/template?model=`, `POST /api/models/template/preview`.

## Sampling and generation

Temperature, top-k/p, min-p, typical-p, penalties, Mirostat v1/v2, DRY, XTC, top-n-sigma and a configurable sampler order (`Inference/SamplerChain.cs`). Also seed, stop strings, logit bias, GBNF grammar, JSON mode and JSON-schema output (`Inference/SchemaGrammar.cs`).

* `logprobs` / `top_logprobs` (clamped to 20) and `n > 1` work on chat, completions, Responses and the native chat. Token bytes are raw UTF-8, so split characters are correct.
* **Context overflow** (`context_overflow`, also `contextOverflowPolicy`): `dropOldest` (default), `truncateMiddle`, `rollingWindow`, `stopAtLimit`, `error` (HTTP 400 `context_length_exceeded`). `rollingWindow` shifts the model's cache and needs a model that can (otherwise it behaves like `stopAtLimit` and logs why). `keep_first_tokens` sets what is kept.
* **Reasoning:** `<think>` output is separated into `reasoning_content` / Anthropic `thinking` blocks (`Inference/Reasoning.cs`).
* **Tool calling:** template-aware where the model supports it, prompt-and-parse otherwise (`Inference/ToolCalling.cs`).

## MoE, LoRA, instances

* **MoE:** `MoeExpertCount`, `MoeCpuLayers` (-1 = all) and `TensorOverrides` (regex → buffer). CLI: `--cpu-moe`, `--n-cpu-moe N`, `--moe-experts N`, `--override-tensor "regex=CPU;…"`. The load dialog shows the fields only for MoE models.
* **LoRA:** `LoadOptions.Loras` (adapter + scale). Adapters are recognised by their GGUF metadata and kept out of the model list. `POST /api/models/lora` swaps adapters without reloading; saved caches are keyed by model and adapters. CLI: `load --lora "a.gguf=0.8;b.gguf"`.
* **Multiple instances:** loading a loaded model again with `allowDuplicate` creates `name:2`, `name:3`. Instances share the weights (ref-counted pool) and have their own context and cache. `name:2` addresses one instance, `name` goes to the least busy one. CLI: `load --another`; UI: "+ instance".

## Embeddings and reranking

`LoadOptions.Pooling` (auto | none | mean | cls | last | rank), `Normalize`, `EmbedBatchSize`. Up to 32 inputs are packed into one evaluation step. `/v1/embeddings` supports `dimensions`, `encoding_format: base64`, `truncate` and `normalize`.

`POST /v1/rerank` (also `/rerank`, `/v1/reranking`) accepts the Jina/Cohere shape. A rank-pooling model is a cross-encoder; any other embedding model falls back to cosine similarity. `settings.rag.rerankModel` lets chat-with-documents re-order its chunks with a reranker. CLI: `rerank <query> <doc>…`.

## Live hardware monitor

`Core/HardwareMonitor.cs` reads CPU load, memory, and per-GPU engine load and memory from Windows performance counters (vendor-neutral). It samples once a second only while something is subscribed.

* `GET /api/hardware/live` (unavailable counters are `null`, with notes) and `GET /api/hardware/stream` (SSE).
* UI: a CPU/GPU sparkline in the top bar and a "Live load" card in Settings. GPU temperature is not available.
