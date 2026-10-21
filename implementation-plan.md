# Implementation plan — BlahLLMWrapper parity for LLMHost

Companion to [todo.md](todo.md). Every item in the todo list appears here as `§<section>.<n>` where `<n>` is the item's
position inside that section of `todo.md` (e.g. **§6.3** = third item under "6. Server / API").

Effort scale (one engineer, includes tests): **S** ≤ 2 days · **M** 3–5 days · **L** 6–10 days · **XL** > 10 days.
All estimates assume the foundations in Phase 0/1 are done first.

---

## 1. Strategic decisions (make these before writing feature code)

### D1 — Inference engine strategy (the decision that drives ~40% of the list)

Today the app embeds llama.cpp through **LLamaSharp** in-process. That wrapper lags llama.cpp and exposes only part of it.
Several todo items (continuous batching, prompt-cache reuse, vision via `mmproj`, native Jinja tool calling, reasoning
parsing, speculative decoding, extra samplers, LoRA, MoE tensor overrides, multi-GPU split, logprobs) are already
implemented and maintained inside llama.cpp's own **`llama-server`**.

| Option | Pros | Cons |
|---|---|---|
| **A. Keep LLamaSharp, build every feature on `BatchedExecutor`/native API** | single process, no downloads | we re-implement templates, tools, reasoning, vision, batching, caching ourselves; wrapper lags upstream; backend fixed per process |
| **B. Host `llama-server` as managed child processes (recommended)** | features arrive by upgrading a pinned binary; backend (CPU or Vulkan) chosen **per model** by choosing a different binary; crash/OOM isolation; matches BlahLLMWrapper's "runtime" concept 1:1 | must download/ship runtimes, manage processes and ports, translate our API ↔ llama-server API |
| **C. Hybrid** — B as the primary engine, LLamaSharp kept as a zero-download "built-in" engine | first run works offline; graceful fallback | two code paths to test |

**Decision: C, with B as the default.** All engine access goes through an `IEngine` abstraction (see §2). The built-in engine
is frozen at its current feature level (chat, completions, embeddings, grammar) and everything new is built against
`LlamaServerEngine`.

**Spike (M, do first):** run official `llama-server` Windows builds (Vulkan and CPU) against the RX 7900 XTX with the
Qwen 0.5B test model; confirm `--jinja`, `--parallel`, `--mmproj`, `--model-draft`, `--reasoning-format`, `--metrics`,
`--slots` behave as documented for the pinned build number. Exit criteria: tokens/s on par with the current Vulkan path,
clean shutdown, port handoff works. _Flag names in this plan come from upstream docs; verify against `llama-server --help` of
the pinned build._

### D2 — One canonical request model, thin API adapters

Replace the ad-hoc mapping in `Server/OpenAiMapping.cs` and `Server/AnthropicApi.cs` with an internal model:
`ChatRequest { Messages[ContentPart: text|image|tool_call|tool_result|reasoning], Tools, ToolChoice, Sampling, Output(format/schema), Reasoning, Stream, Cache hints }`
and `ChatEvent` stream (`Start`, `TextDelta`, `ReasoningDelta`, `ToolCallDelta`, `ToolCallDone`, `Usage`, `Stats`, `Done`).
OpenAI chat, Responses, Anthropic, native v1, v0 and Ollama become adapters in/out of this model. Prevents N×M feature drift.

### D3 — Process model

* One executable `llmhost` (host: API gateway, UI, CLI, downloads, MCP, RAG, link) + N engine child processes.
* Daemon/headless = same exe (`llmhost daemon up`), optionally registered as a Windows Service/startup task.
* Desktop shell is a separate thin project that hosts the web UI in WebView2 and talks to the same host over localhost.

### D4 — Storage layout (`%USERPROFILE%\.llmhost`, versioned)

```
settings.json          (schemaVersion)       tokens.json   (hashed, per-token permissions)
DownloadedModels\<publisher>\<model>\*.gguf  (default: ./DownloadedModels in the working directory; configurable)
                                              model-defaults\<model-id>.json
runtimes\<engine>-<build>\                   presets\*.json
conversations\*.json   (message tree)        mcp.json
rag\<chat-id>\         (chunks + vectors)    logs\*.log (rotated)
```

### D5 — UI strategy

Keep **one** UI: the web UI, hosted in a WebView2 shell. The single `index.html` will not survive branching chats, RAG,
split view and a runtime manager, so in Phase 4 move to a small component framework (Preact + Vite, output embedded as
resources, no runtime Node dependency). No separate WPF UI.

### D6 — Solution layout (replaces the single project)

```
LLMHostProject.sln
  src/LLMHost.Core        settings, paths, canonical model, storage, hardware, logging
  src/LLMHost.Engines     IEngine, LlamaServerEngine, BuiltinEngine, RuntimeManager, ModelHost
  src/LLMHost.Services    downloads, library, RAG, MCP host, presets, tokens, link
  src/LLMHost.Server      ASP.NET Core adapters (OpenAI, Anthropic, native v1/v0, Responses, Ollama), UI hosting
  src/LLMHost.Cli         `llmhost` exe (shell + sub-commands + daemon)
  src/LLMHost.Desktop     WebView2 shell + tray
  web/                   Vite frontend → embedded
  tests/*                unit + contract + integration
```
(The original "solution and project in one folder" requirement applied to the initial scaffold; splitting is a deliberate change.)

### D7 — Testing strategy

* **Unit:** GGUF reader, TextScanner, SchemaGrammar, adapters, download resume, settings migration.
* **Contract:** golden JSON/SSE fixtures per API surface; run the official OpenAI/Anthropic SDKs against a fake engine.
* **Integration:** a tiny model (Qwen2.5-0.5B Q4_K_M) cached in CI; smoke test per engine (CPU always, Vulkan on a GPU runner).
* **Benchmarks:** tokens/s, TTFT, concurrent-request throughput tracked per build.

---

## 2. Target architecture

```
 ┌─────────────── clients ───────────────┐
 │ web UI/WebView2 │ CLI │ OpenAI/Anthropic/LMS SDKs │ MCP clients │ other devices (Link)
 └───────┬─────────┴──┬──┴───────────────┬───────────┴───────────────────────────────┘
         ▼            ▼                  ▼
   ┌──────────────────────── LLMHost.Server (adapters) ─────────────────────────┐
   │ /v1 OpenAI · /v1/messages · /api/v1 native · /api/v0 · /v1/responses · Ollama │
   └───────────────┬───────────────────────────────────────────────────────────┘
                   ▼  canonical ChatRequest / ChatEvent
   ┌──────────────── LLMHost.Services ───────────────────────────────────────────┐
   │ ConversationService · RagService · McpHost · PresetStore · TokenStore       │
   │ ModelLibrary · DownloadManager · DefaultsStore · LinkService                │
   └───────────────┬───────────────────────────────────────────────────────────┘
                   ▼
   ┌──────────────── LLMHost.Engines ────────────────────────────────────────────┐
   │ ModelHost (JIT, TTL, auto-evict, fit estimator, scheduler)                  │
   │ RuntimeManager (download/select builds)                                     │
   │ IEngine ─ LlamaServerEngine (child process per instance) │ BuiltinEngine   │
   └─────────────────────────────────────────────────────────────────────────────┘
```

```csharp
interface IEngine {
    string Id { get; }                                   // "llama-server:vulkan-b6xxx", "builtin"
    EngineCapabilities Capabilities { get; }             // vision, tools, parallel, speculative, lora...
    Task<IModelInstance> LoadAsync(ModelSpec m, LoadOptions o, IProgress<LoadProgress> p, CancellationToken ct);
}
interface IModelInstance : IAsyncDisposable {
    ModelInfo Info { get; }                              // ctx, arch, size, device, layers, vram used
    IAsyncEnumerable<ChatEvent> ChatAsync(ChatRequest r, CancellationToken ct);
    Task<CompletionResult> CompleteAsync(CompletionRequest r, CancellationToken ct);
    Task<float[][]> EmbedAsync(string[] inputs, CancellationToken ct);
    Task<int[]> TokenizeAsync(string text, bool special, CancellationToken ct);
}
```

---

## 3. Phases, ordering and exit criteria

| Phase | Theme | Items | Exit criteria | Effort |
|---|---|---|---|---|
| **0** | Foundations | §10.1 §10.2 §10.3 §10.4, D2 refactor, D6 split | tests green in CI; existing features unchanged behind new abstractions | 3–4 wks |
| **1** | Engine & runtimes | §1.1–§1.16, §2.1–§2.8 | runtime manager installs Vulkan/CPU builds; parallel + cache reuse working on 7900 XTX | 5–6 wks |
| **2** | Model management | §3.1–§3.10 | defaults, queued downloads, import, badges | 3 wks |
| **3** | API surface | §6.1–§6.12 (except MCP/plugins) | native v1, Responses, tokens, v0, HTTPS, metrics | 4 wks |
| **4** | Chat, presets, RAG | §4.*, §5.*, D5 frontend rebuild | branching chat, folders, attachments, RAG, presets | 5–6 wks |
| **5** | Agents & ops | §6.MCP, §6 plugins, §7.*, daemon | MCP host, `lms`-parity CLI, headless service | 4 wks |
| **6** | Desktop | §9.* | installer, tray, modes, themes, i18n, updater | 3–4 wks |
| **7** | Remote | §8.* | Link between two machines | 3 wks |
| **8** | Hardening | §10.5–§10.7, perf, docs, security review | release candidate | 2–3 wks |

Critical path: **D1 spike → Phase 0 → runtime manager (§2.1) → engine features (§1.x) → native v1 API (§6.1) → chat UX (§4.x)**.
Phases 2 and 3 can run in parallel with Phase 1 once `IEngine` exists.

---

## 4. Item-by-item plan

Format: **Design** (how) · **Work** (concrete steps/files) · **Done when** · **Effort** · **Deps**.

### §1 Inference engine

**§1.1 Parallel requests with continuous batching + max concurrent predictions** — L
* Design: `LlamaServerEngine` starts `llama-server --parallel N` (default 4, same as BlahLLMWrapper); each slot serves one request, server batches continuously. Built-in engine: serialize (document limitation).
* Work: add `MaxConcurrentPredictions` to `LoadOptions` + UI/CLI; scheduler in `ModelHost` queues beyond N with a fair queue and 429/`Retry-After` when queue cap exceeded; surface slot usage via `/slots`; split-view in UI sends two requests simultaneously.
* Done when: 4 simultaneous streams each progress; aggregate tok/s > sequential; cancel of one does not affect others.
* Deps: D1, §2.1.

**§1.2 Prompt/KV-cache reuse between turns** — M _(built-in engine: DONE — single slot, longest-common-prefix reuse in `ModelRuntime.StreamAsync`; remaining: multi-slot LRU, disk save/restore, llama-server engine parity)_
* Design: rely on llama-server `cache_prompt: true` + slot affinity (route a conversation to the same slot by `id_slot`/prompt-prefix hash); `--cache-reuse` for shifted prefixes. For stateful native chats keep the slot id in the response record.
* Work: `SlotAffinity` in scheduler (map conversationKey → slot); send `cache_prompt`; expose cache-hit tokens in `stats`.
* Done when: turn 20 of a long chat has TTFT ≈ turn 2 (prompt tokens evaluated ≈ only the new tokens).
* Deps: §1.1.

**§1.3 Vision / multimodal** — L _(built-in engine: DONE via LLamaSharp `MtmdWeights` — mmproj pairing + auto download, OpenAI/Anthropic image parts, UI/CLI attachments; remaining: native v1 `data_url` items, images in tool results, audio, llama-server parity)_
* Design: capability detected from a sibling `mmproj*.gguf`; llama-server started with `--mmproj`. Accept images in OpenAI `image_url` (data URL or http), Anthropic `image` blocks, native `data_url`; downscale/validate in host.
* Work: `ContentPart.Image`; mmproj pairing logic in library (same repo folder, name match) + auto download companion (§3.2); `capabilities.vision` flag; UI attachment button; reject images for non-vision models with a clear error.
* Done when: describe-image works for a vision GGUF via UI, OpenAI SDK and Anthropic SDK.
* Deps: D1, D2, §3.2.

**§1.4 Native template-aware tool calling** — L
* Design: start with `--jinja` so llama.cpp renders the model's own template with `tools` and parses tool calls into OpenAI `tool_calls`; map to canonical `ToolCall` events. Fall back to the current prompt-based method for models without tool templates.
* Work: capability detection (template contains tools section); `tool_choice` (`auto|none|required|{name}`) mapping (GBNF forcing for `required`); parallel tool calls; streaming `ToolCallDelta` (argument fragments); round-trip of `tool` role messages; adapter tests for OpenAI/Anthropic/native v1.
* Done when: multi-tool, multi-turn loop passes with Llama 3.1, Qwen3 and Mistral templates; fallback still works on the 0.5B test model.
* Deps: D1, D2.

**§1.5 Reasoning support + effort control** — M
* Design: `--reasoning-format deepseek` (or parse `<think>` / harmony channels ourselves) → `ReasoningDelta`. Effort `off|low|medium|high|on` mapped per template: `enable_thinking` kwarg (Qwen3), `reasoning_effort` (gpt-oss), `--reasoning-budget` for off.
* Work: `ReasoningMode` in `ChatRequest`; adapters emit `reasoning_content` (OpenAI), `thinking` blocks (Anthropic), `reasoning` output item (native v1), collapsible UI; stats `reasoning_output_tokens`.
* Done when: DeepSeek-R1-distill and Qwen3 reasoning is separated in all three APIs; `off` suppresses thinking where the template supports it.
* Deps: D2.

**§1.6 Speculative decoding (draft model, MTP)** — M
* Design: `--model-draft <gguf> --draft-max/--draft-min`; validate vocab compatibility from GGUF tokenizer metadata (vocab size + tokenizer model/pre) before load; suggest pairings by same family and ≤ size rules (1B for 7B, 3B for 14B, 7B for 32B). MTP: enable when the runtime build reports support (capability probe via `--help` parse / `/props`).
* Work: `DraftModel` in `LoadOptions`; vocab check in `GgufInfo`; UI "Speculative decoding" section (Power User mode); report acceptance rate in stats if exposed.
* Done when: Qwen2.5-14B + 0.5B draft shows measurable speed-up on predictable prompts; mismatch is rejected with explanation.
* Deps: D1, §9.2 (mode gating, optional).

**§1.7 Prompt-template override per model** — M
* Design: precedence = per-model override → GGUF `tokenizer.chat_template` → ChatML fallback. Jinja mode: pass `--chat-template-file`; Manual mode: role prefixes/suffixes + BOS/EOS + stop strings rendered by our own template engine (no Jinja needed).
* Work: `PromptTemplateConfig` stored in `model-defaults`; template editor with live preview (render sample conversation via `/apply-template`-style call); "always show template" toggle; stop-string defaults derived from template.
* Done when: overriding a broken template fixes output; preview matches real prompt byte-for-byte.
* Deps: §3.1.

**§1.8 Additional samplers (Mirostat, DRY, XTC, TFS, top-n-sigma, order)** — S
* Design: pass-through to llama-server per-request fields (`mirostat`, `mirostat_tau/eta`, `dry_*`, `xtc_*`, `top_n_sigma`, `samplers` order). Built-in engine: ignore with warning.
* Work: extend `GenerationOptions` + validation ranges; UI "Samplers" accordion with drag-to-reorder; CLI `set` keys; preset schema bump.
* Done when: values reach the server (verified by `/slots` params dump) and change output deterministically with a fixed seed.
* Deps: D1.

**§1.9 Context-overflow policies** — M
* Design: policy enum `TruncateMiddle | RollingWindow | StopAtLimit | Error`, applied in host before dispatch using the tokenize endpoint; system prompt and last user turn always kept; RollingWindow may also use server context shift.
* Work: `ContextPolicy` in generation options (per chat/request/default), `ContextWarning` event so UI shows "N messages dropped"; API param `context_overflow_policy`.
* Done when: each policy yields the documented prompt on a unit test with a fake tokenizer.
* Deps: D2.

**§1.10 MoE controls** — S
* Design: `--n-cpu-moe N` / `--cpu-moe` and `--override-tensor "exps=CPU"`; expose "experts to keep on CPU" and "number of experts used" (GGUF KV override `--override-kv`).
* Work: `LoadOptions.MoeCpuLayers`, `ExpertCount`; detect MoE from GGUF (`expert_count` key) and only show for MoE models; feed estimator (§2.3).
* Done when: Qwen3-30B-A3B loads with experts on CPU and fits in less VRAM than all-GPU.
* Deps: D1, §2.3.

**§1.11 LoRA adapters** — S
* Design: `--lora path[:scale]` (multiple); adapters stored under `models/<publisher>/<model>/adapters`.
* Work: library scan, load-dialog multi-select with scale sliders, API `load` option `lora`.
* Done when: adapter changes output; unload removes it.
* Deps: D1.

**§1.12 Multiple instances of the same model** — S
* Design: instance key = identifier (not file); each instance its own server process/ctx; shared mmap pages reduce RAM.
* Work: `ModelHost` keyed by identifier; auto-suffix `:2` when duplicate; UI shows instances; fit estimator accounts for each.
* Done when: two instances with different ctx run concurrently.
* Deps: §2.3.

**§1.13 `logprobs` / `top_logprobs`, `n > 1`** — S
* Design: `n_probs` on llama-server → map to OpenAI `logprobs.content[]`; `n>1` as n parallel slots (needs §1.1) or sequential on built-in.
* Work: adapter fields + tests against OpenAI SDK parsing.
* Done when: SDK parses logprobs; `n=3` returns 3 choices.
* Deps: §1.1.

**§1.14 Embedding options & reranking** — M
* Design: separate engine instance with `--embedding --pooling <type>`; `/v1/embeddings` accepts arrays, `encoding_format=float|base64`, `dimensions` (truncate + renormalise); reranking via `--reranking` + `/v1/rerank` (Jina/Cohere-style).
* Work: pooling/normalise options in load dialog; batching; model-type detection (embedding vs reranker from GGUF arch/pooling).
* Done when: embeddings stable vs reference model outputs; rerank endpoint orders a known query set correctly.
* Deps: D1.

**§1.15 Harden schema→GBNF** — M
* Design: replace `SchemaGrammar` with llama.cpp's own `json_schema` support (server converts) and keep ours only for built-in engine; extend ours with `$defs`/`$ref` cycles (bounded depth), `pattern` subset, `minimum/maximum` (digit-count approximation), `additionalProperties`, string length.
* Work: conformance suite of ~60 schemas validated with a JSON-schema validator on generated outputs.
* Done when: suite ≥ 95% valid; unsupported keywords reported, not silently ignored.
* Deps: none.

**§1.16 Cancel model load; progress events** — S
* Design: killing the child process cancels a load; parse llama-server stderr/`/health` for phases (loading tensors %, warm-up); emit `LoadProgress` events via SSE.
* Work: cancellation endpoint `DELETE /api/v1/models/load/{id}`; UI cancel button; prompt-processing progress via `return_progress`.
* Done when: cancel frees VRAM within 2 s; progress bar monotonic.
* Deps: D1.

### §2 Hardware & runtimes

**§2.1 Runtime/engine manager** — XL
* Design: a *runtime* = a pinned llama.cpp release zip (`win-cpu-x64` and `win-vulkan-x64` only; CPU variants avx2/avx512 where offered — HIP/ROCm and CUDA builds are explicitly out of scope) extracted to `runtimes\<name>-<build>\` with a `runtime.json` manifest (build, backends, sha256, capabilities).
* Work:
  1. `RuntimeCatalog`: query GitHub Releases API (cache, offline-safe), map assets→runtime descriptors, show only compatible ones (hardware survey: Vulkan device present, AVX flags).
  2. `RuntimeManager`: download+verify+extract, keep N versions, mark "preferred" per engine type and per model (`LoadOptions.Runtime`), auto-update policy (off/notify/auto), rollback.
  3. Selection logic: `auto` = Vulkan if a usable Vulkan device exists, else CPU, with fallback to CPU on load failure.
  4. UI "Runtimes" view (list, update, select, active badge, per-model override); CLI `runtime ls|get|select|update`.
  5. Built-in LLamaSharp engine listed as runtime "builtin".
* Done when: a fresh install can fetch Vulkan runtime, load a model on the GPU, and switch the same model to CPU runtime without restarting the host.
* Deps: D1 spike.

**§2.2 Multi-GPU** — M
* Design: enumerate devices from `llama-server --list-devices` (authoritative Vulkan device ordering) and join with DXGI by LUID/name to fix mapping; options `--device`, `--tensor-split`, `--split-mode`, `--main-gpu`.
* Work: `GpuDevice` model {runtimeIndex, name, vram, integrated}; load dialog with device checkboxes, split sliders, priority order; persistence in model defaults.
* Done when: on a 2-GPU test box layers split per ratio; single-GPU box UI hides split controls.
* Deps: §2.1.

**§2.3 Accurate fit estimate + dedicated-memory guardrail** — M
* Design: estimator = weights (file size, mmproj, draft) + KV (`2·layers·ctx·kvHeads·headDim·bytes(type)`, SWA aware) + compute buffer (empirical per arch/batch) + overhead; read free VRAM/RAM live (DXGI `QueryVideoMemoryInfo`). Calibrate against llama-server's own load log ("model buffer size", "KV buffer size", "compute buffer size").
* Work: `FitEstimator` returning {vram, ram, offloadable layers, verdict}; "limit offload to dedicated GPU memory" toggle (don't spill to shared memory on iGPU); pre-load warning in UI/CLI/API (`409` with details unless `force`).
* Done when: estimate within ±10% of actual for 5 reference models on the 7900 XTX.
* Deps: §2.1.

**§2.4 GPU offload as ratio** — S
* Work: parse `max|off|auto|0.0–1.0` → layers via block count; same in API (`gpu_offload`) and CLI (`--gpu`).
* Done when: `0.5` on a 32-layer model sends 16 layers.
* Deps: §2.3.

**§2.5 Switch backend at runtime** — covered by §2.1 (child processes per runtime). Remaining work: M — remove process-wide `NativeBackend` assumptions from the built-in engine (it stays CPU-or-Vulkan fixed per host process, flagged in UI), ensure two instances on different runtimes coexist.

**§2.6 Live hardware monitor** — M
* Design: sample every 1 s: CPU via PerformanceCounter; GPU util/VRAM via DXGI `IDXGIAdapter3::QueryVideoMemoryInfo` + GPU Engine perf counters (`\GPU Engine(*)\Utilization Percentage`, `\GPU Adapter Memory(*)`); temps via AMD ADL if present (optional).
* Work: `HardwareMonitor` service, SSE `/api/v1/hardware/stream`, UI sparkline panel, `llmhost hw --watch`.
* Done when: values track Task Manager within a few %.

**§2.7 CPU thread pool settings** — S
* Work: `threads`, `threads_batch`, `cpu-mask`/`--cpu-strict`, `--prio`, `--numa` in `LoadOptions`; defaults = physical cores (not logical).
* Done when: settings visible in server args and logged.

**§2.8 Offline audit + system requirements doc** — S
* Work: network-call inventory (HF API, GitHub releases, update check, link) each behind a setting; "offline mode" flag that disables all and makes failures friendly; write `docs/system-requirements.md`; hardware survey wizard reuses §2.3.
* Done when: with the firewall blocking everything the app works for local models and shows no errors.

### §3 Model management

**§3.1 Per-model defaults (load + inference)** — M
* Design: `model-defaults/<model-id>.json` {load, inference, promptTemplate, lora, draft}. Resolution order for a load: request → model defaults → global defaults.
* Work: `DefaultsStore`; gear dialog in My Models; "save as default" button in load dialog and chat sidebar; applied in API JIT loads and `lms load`.
* Done when: JIT load via API honours defaults; deleting defaults resets.

**§3.2 Download manager (pause/resume/queue/persist/verify)** — L
* Design: persisted job table (`downloads.json`); worker pool (default 2 concurrent), per-file `.part` + `.meta` (etag, size, sha256); pause = cancel token with state `paused`; verify sha256 via HF LFS `oid`; retries with exponential back-off; disk space check against `DriveInfo.AvailableFreeSpace`.
* Work: refactor `DownloadManager`; queue ordering; companion files (mmproj, extra shards); events to UI/CLI; bandwidth limit option.
* Done when: kill the process mid-download, restart, job resumes; corrupted part detected and re-fetched.

**§3.3 Download by URL / shorthand; import files** — M
* Design: input parser accepts `user/model`, `user/model:Q4_K_M`, `hf.co/…`, full `resolve/` URLs; import = copy/move/hardlink into `publisher/model/file.gguf` (publisher inferred or prompted).
* Work: `ModelRefParser`; drag-and-drop import in UI; `llmhost import <path> [--move|--link] [--publisher] [--name]`; GGUF validation before import.
* Done when: all five input forms resolve; importing a file makes it appear in `ls` without restart.

**§3.4 Capability badges + richer model card** — M
* Design: capabilities from GGUF (arch, expert_count, chat template contents, `vision` via mmproj) + HF metadata (tags, license, README via `https://huggingface.co/{repo}/raw/main/README.md`, cached).
* Work: `ModelInfo` DTO; badge rendering; markdown README viewer; license warning for gated/non-commercial.
* Done when: Qwen3 shows reasoning+tools, Gemma3 vision, Qwen3-30B-A3B MoE.

**§3.5 Discover filters/sorting/pagination + updatable picks** — M
* Work: HF API params (`sort`, `direction`, `limit`, `offset` via `Link` header), client filters (size range, quant, family, fits GPU), curated list moved to a remote JSON (`catalog.json` fetched from repo/CDN with embedded fallback and signature/hash check).
* Done when: filter "fits my GPU" hides oversized quants; catalog updates without a rebuild.

**§3.6 Gated-model flow** — S
* Work: token validation call (`/api/whoami-v2`), detect 401/403 on a repo and show "request access at …" with link; token stored encrypted (DPAPI).
* Done when: gated Llama download fails with actionable message, succeeds after token set.

**§3.7 Model update check** — S
* Work: store repo + commit sha at download; weekly (or manual) check `GET /api/models/{repo}` `sha`/file `lfs.oid`; badge "update available"; re-download keeps old until verified.
* Done when: changed upstream file flagged in tests with a mocked HF.

**§3.8 Models-directory management** — S
* Work: multiple library roots (primary + extra read-only), move-directory wizard with progress and symlink-safe copy, per-model disk usage + total.
* Done when: moving 10 GB library works and models stay loadable.

**§3.9 `model.yaml` virtual models** — M
* Design: YAML descriptor {model id, base sources (HF refs), metadataOverrides, config defaults, suggestions}; resolves to concrete GGUFs through the library; used for curated "model families" and the catalog.
* Work: parser (YamlDotNet), resolver, catalog migration to model.yaml, import/export.
* Done when: catalog entries are driven by model.yaml files.
* Deps: §3.1.

**§3.10 Aliases / favourites / tags / folders** — S
* Work: `library-meta.json` with per-model {alias, favourite, tags, folder}; alias used as API identifier; UI filters.

### §4 Chat experience

**§4.1 Edit / delete / regenerate with branching** — L
* Design: conversation = **message tree** (`id`, `parentId`, `children`, `activeChild`), linearised via the active path when prompting. Native v1 `previous_response_id` maps to a node id.
* Work: storage migration from flat list; UI controls (edit-resubmit creates sibling branch, `< 2/3 >` switcher, delete subtree); API for tree ops.
* Done when: edit a middle message → new branch, old branch intact; prompts use only the active path.
* Deps: D5.

**§4.2 Chat with documents (RAG)** — XL
* Design: ingest `.pdf` (PdfPig), `.docx` (OpenXml), `.txt/.md`; if tokens ≤ ~60% of context → inline whole file; else chunk (≈400–800 tokens, 10% overlap, structure-aware), embed with a user-selected local embedding model (via §1.14), store vectors (flat float32 + cosine; sqlite-vec optional), retrieve top-k (default 5) with MMR, inject with citations `[doc, page]`.
* Work: `DocumentIngestor`, `Chunker`, `VectorIndex`, `RagService`, retrieval settings (k, threshold, chunk size), citations UI, per-chat attachment lifecycle (duplicated with chat), progress for embedding, API: `documents` in native v1 chat.
* Done when: question answerable only from a 200-page PDF returns the right passage with citation; small file path doesn't call the embedder.
* Deps: §1.14, §4.3 (attachments UI).

**§4.3 Image / file attachments** — M
* Work: attach button + drag/drop + paste; images → vision (§1.3), documents → RAG (§4.2); thumbnail previews; size limits; stored under `conversations/<id>/attachments`.
* Deps: §1.3, §4.2.

**§4.4 Folders, duplicate, search, rename, titles, bulk ops, import/export** — M
* Work: folder tree in metadata; duplicate (copies attachments); full-text search (SQLite FTS5 or simple inverted index); auto title via the loaded model (background, short prompt); export JSON/Markdown/TXT; import from JSON (and BlahLLMWrapper conversations format best-effort).
* Done when: search finds text in 1,000 chats < 200 ms.

**§4.5 Split view / multi-model compare** — M
* Work: two chat panes bound to separate models/instances; "send to both" box; concurrent streaming (needs §1.1 or two instances); synced scroll toggle.
* Deps: §1.1, D5.

**§4.6 Per-chat settings panel** — S
* Work: chat stores {model, system prompt, generation overrides}; "reset to preset/default" per field; "save as preset/default".
* Deps: §5.1.

**§4.7 Rich rendering** — M
* Work: markdown-it + highlight.js (embedded, offline) + KaTeX; copy buttons; sanitization (DOMPurify); collapsible reasoning blocks; streaming-safe incremental render.
* Done when: code, tables, LaTeX render during streaming without flicker or XSS.

**§4.8 Token counter / context bar / per-message stats** — S
* Work: `/api/v1/tokenize` live count (debounced) incl. system prompt + history; bar with overflow policy hint; per-message stats from `ChatEvent.Stats`.

**§4.9 Keyboard shortcuts** — S
* Work: central keymap (Ctrl+N new chat, Ctrl+Shift+N folder, Ctrl+Shift+R runtimes, Ctrl+P quick open), command palette, user remap file.

**§4.10 Prompt library / snippets; voice input** — M
* Work: snippets store + `/` menu in composer; voice via Web Speech API when available (online) or optional local Whisper runtime (later, flagged experimental).

### §5 Presets

**§5.1 Presets as files (system prompt + inference params; no load params)** — S
* Work: migrate `presets.json` → `presets\<name>.json` with `schemaVersion`; "modified" dot + reset in UI; preset picker in chat header; CLI `preset`.
* Done when: preset round-trips; load params excluded.

**§5.2 Import/export (file or URL)** — S
* Work: drag-drop JSON, "import from URL" (validate schema, size cap, show diff before apply), export button.

**§5.3 Hub-style publish/pull/push** — L (optional)
* Design: define a simple registry = Git repo or static index (`presets/index.json` + files) with versioned revisions; `publish`, `pull` (update check by revision hash), `push` (PR/commit via git or HTTP API).
* Work: `PresetRegistryClient`, revision diff UI, auth token. Defer until a hosting target is chosen.

**§5.4 Legacy preset migration** — S
* Work: importer for BlahLLMWrapper 0.2.x/0.3 preset JSON (map known fields, drop unknown with report).

### §6 Server / API

**§6.1 Native REST API v1** — XL (split into sub-tasks)
* Design: new adapter `NativeV1Api` over the canonical model; endpoints `POST /api/v1/chat`, `GET /api/v1/models`, `POST /api/v1/models/load`, `POST /api/v1/models/unload`, `POST /api/v1/models/download`, `GET /api/v1/models/download/status`.
  * **Stateful chats** — `ConversationState` store keyed by `resp_<id>`; each response persists {messages, model, ids}; `previous_response_id` loads the chain; `store=false` skips persistence; retention policy (TTL, max count).
  * **Request fields** — `input` string|array (`text`, `image` with `data_url`), `system_prompt`, sampling fields, `max_output_tokens`, `reasoning`, `context_length` (JIT-load with that ctx or reload), `stream`, `store`, `integrations`.
  * **Response** — `model_instance_id`, `output[]` (`message`, `reasoning`, `tool_call`, `invalid_tool_call`), `stats` {`input_tokens`, `total_output_tokens`, `reasoning_output_tokens`, `tokens_per_second`, `time_to_first_token_seconds`, `model_load_time_seconds`}, `response_id`.
  * **SSE events** — typed events: `model_load.progress`, `prompt_processing.progress`, `reasoning.delta`, `message.delta`, `tool_call.*`, `chat.end` (verify exact names against the Streaming-events doc page, which could not be read).
  * **Load options** — context length, flash attention, GPU offload, eval batch, mmap, KV types, parallel, `echo_load_config`.
* Done when: official BlahLLMWrapper TS/Python SDK smoke tests (if available) or recorded fixtures pass; stateful branching works via `previous_response_id`.
* Deps: D2, §1.1–§1.5, §6.3.

**§6.2 OpenAI Responses API** — M
* Design: `/v1/responses` adapter: `input` (string | items incl. `message`, `function_call`, `function_call_output`), `instructions`, `previous_response_id`, `store`, streaming events (`response.created`, `response.output_text.delta`, `response.function_call_arguments.delta`, `response.completed`, …); share state store with §6.1. Include the Open Responses variant behaviours Codex-style clients need.
* Done when: official OpenAI SDK `client.responses.create` (streaming and not) and a Codex-style tool loop work.
* Deps: §6.1 state store, §1.4.

**§6.3 API tokens + permissions** — M
* Design: `tokens.json` stores `{id, name, sha256(token), permissions[], createdAt, lastUsed}`; permissions: `chat`, `embeddings`, `models:read`, `models:load`, `models:download`, `admin`, `mcp`. Middleware resolves `Authorization: Bearer` or `x-api-key`. Legacy single key becomes an `admin` token on migration.
* Work: create/revoke/edit endpoints + UI modal (token shown once); per-token rate limits (§6.8); audit log entry.
* Done when: restricted token can chat but cannot load/download.

**§6.4 Headless daemon / service** — L
* Design: `llmhost daemon up|down|status|update`: starts host without UI, writes `daemon.json` (pid, port), IPC for control; Windows Service via `Microsoft.Extensions.Hosting.WindowsServices` (`llmhost daemon install-service`) or per-user Scheduled Task at logon; no GUI dependency (tray optional).
* Work: lifecycle commands, graceful shutdown (drain requests, unload models), log files, single-instance mutex, `update` = runtimes + self-update check.
* Done when: reboot → service up → API reachable → `lms ps`-equivalent shows models.

**§6.5 MCP host** — XL
* Design: `McpHost` using the C# `ModelContextProtocol` SDK: stdio and streamable-HTTP/SSE transports; `mcp.json` in Cursor notation (`mcpServers`: command/args/env or url/headers); lifecycle (lazy start, restart, health); tool discovery cached; **tool loop** in `ConversationService`: model emits tool call → confirmation policy (always ask / per-server allow / allow-all per chat) → execute → feed result → continue (max iterations).
* Work: UI "Program/Integrations" tab (edit mcp.json with validation, per-tool toggles, server logs), confirmation dialog (shows server + args), token/context-cost warning, API: `integrations` entries `plugin` and `ephemeral_mcp` (`server_label`, `server_url`, `allowed_tools`, `headers`) in native v1; security warnings, env redaction, working-dir restrictions, timeouts.
* Done when: filesystem and HF MCP servers usable from UI and API; denied confirmations return an `invalid_tool_call`-style result; headers never logged.
* Deps: §1.4, §6.1, §10.5.

**§6.6 JIT policy: TTL defaults, auto-evict, max loaded, per-request TTL** — M
* Design: `ModelHost` memory governor: before JIT load run estimator; if over budget evict LRU **JIT-loaded, idle** instances first (never user-pinned), then fail with explanation. Defaults: JIT TTL 60 min (configurable), `ttl` per request/load, `max_loaded_models`.
* Done when: loading model B via API evicts idle JIT model A; manually loaded A is never evicted.
* Deps: §2.3.

**§6.7 Legacy `/api/v0/*`** — S
* Work: adapter returning BlahLLMWrapper v0 shapes (`/api/v0/models` with `state`, `quantization`, `arch`, `max_context_length`, `loaded_context_length`, `capabilities`), `/api/v0/chat/completions`, `/completions`, `/embeddings`.

**§6.8 HTTPS, IP allow-list, limits** — M
* Work: Kestrel cert from PFX/PEM or auto self-signed (stored, fingerprint shown); `Cidrs` allow-list middleware; max body size, max concurrent requests, per-token rate limit (sliding window); settings UI with warnings when exposing to LAN without a token.

**§6.9 Request/response log viewer, `log stream`, `/metrics`** — M
* Work: structured request log (ring buffer + file, redaction of content optional), UI filters (model, status, token), SSE `/api/v1/logs/stream`; Prometheus text `/metrics` (requests, tokens, TTFT histogram, queue depth, VRAM).

**§6.10 Anthropic API gaps** — M
* Work: `image` blocks, images in `tool_result`, `thinking` blocks (from §1.5) and `thinking` request param, `tool_choice` any/tool/none/auto, accept `cache_control`/`metadata`/`service_tier` (ignored), `/v1/models` Anthropic shape (`has_more`, `first_id`), `anthropic-beta` header tolerance, error-event streaming.
* Done when: official `anthropic` SDK sample suite (text, stream, tools, image, thinking) passes.

**§6.11 Ollama-compatible endpoints** — S
* Work: `/api/chat`, `/api/generate`, `/api/tags`, `/api/show`, `/api/pull` (map to downloads), `/api/embed`, NDJSON streaming; opt-in toggle (port 11434 optional).

**§6.12 OpenAPI document** — S
* Work: generate with `Microsoft.AspNetCore.OpenApi` from typed minimal-API endpoints (requires typed request DTOs — a by-product of D2); serve `/openapi.json` + Swagger UI page.

**§6.13 Plugin system (tool providers, prompt preprocessors, generators; `dev/push/clone`)** — XL (optional)
* Design: plugins = out-of-process executables speaking JSON-RPC over stdio (reuse MCP plumbing) with a manifest `plugin.json` {id, type, permissions}; hook points: `tools`, `preprocess(prompt)`, `generate` (custom backend). `llmhost dev` = run plugin in watch mode against the live host.
* Defer until MCP host ships; revisit demand.

**§6.14 Client SDKs** — S (optional)
* Work: thin typed wrappers over the native v1 API (C# NuGet, Python, TS) generated from the OpenAPI doc; existing OpenAI/Anthropic SDKs remain the primary path.

### §7 CLI (`lms` parity)

**§7.1 Missing verbs** — L
* Work (one command per class under `Cli/Commands`, shared parser): `server start|stop|status` (talks to daemon via `daemon.json`), `daemon up|down|status|update`, `log stream`, `import`, `runtime ls|get|select|update|remove`, `load <model> --identifier --gpu max|off|auto|0–1 --context-length --ttl --parallel`, `unload --all`, `ls --json`, `ps --json`, `get` with interactive search + quant picker (arrow-key list, fit hints), `chat` attaching to a running server instead of loading in-process (`--server URL`).
* Done when: scripted workflow `daemon up → get → load → chat → unload → daemon down` works unattended.
* Deps: §6.4, §2.1, §3.3.

**§7.2 `link` verbs** — S — `enable|disable|status|set-device-name|set-preferred-device` once §8.1 exists.

**§7.3 `login/push/clone/dev`** — S — thin commands over preset registry (§5.3) and plugin dev (§6.13); skip if those are deferred.

**§7.4 Machine-readable output** — S
* Work: global `--json`, `--quiet`, NDJSON for streams; exit-code table (0 ok, 1 error, 2 usage, 3 not found, 4 unavailable); output contract tests.

**§7.5 Completions + line editing** — S
* Work: `llmhost completion powershell|bash|zsh` generator; replace `Console.ReadLine` with a small line editor (history file, Ctrl+R, multi-line via Alt+Enter) using PrettyPrompt or ReadLine library.

### §8 Remote / multi-device

**§8.1 LM Link equivalent** — XL
* Design: devices form a private link via **Tailscale** (or embedded WireGuard as v2). Each host publishes `/api/v1/link/info` (device name, models, load, preferred flag) over the tailnet; `LinkService` discovers peers (Tailscale local API / MagicDNS), authenticates with a link token, and registers **remote model instances** in `ModelHost` that proxy requests (`RemoteModelInstance : IModelInstance`).
* Work: pairing flow (code/QR + token exchange), device list UI, preferred-device routing rule, remote model badges, streaming proxy with back-pressure and cancellation, failover to local when peer offline, encryption relies on tailnet (also require TLS + token).
* Done when: laptop chats with a model loaded on the desktop via UI, CLI and API; unplugging the peer yields a clear error.
* Deps: §6.3, §6.8, §1 abstractions.

**§8.2 Pair mobile/remote clients** — M
* Work: QR code with URL+token in settings, PWA manifest + responsive layout for the web UI, optional mDNS advertisement on LAN.

### §9 Desktop application & UX

**§9.1 Native desktop shell** — M (window already implemented: WinForms + WebView2 in `Desktop/MainWindow.cs`; remaining = tray/lifecycle)
* Work (remaining): tray icon (status, load/unload, open, quit), start host in-process or attach to daemon, single-instance named mutex, window state persistence, protocol handler (`llmhost://`), update toast.
* Done when: launching the shell starts everything; closing to tray keeps the server running.

**§9.2 User / Power User / Developer modes** — M
* Work: `UiMode` setting + `FeatureFlag` map (which panels/fields each mode reveals); gate speculative decoding, per-model defaults, runtimes, server settings, logs; mode switcher in footer; API stays fully available.

**§9.3 Themes + language** — M
* Work: CSS-variable theme tokens (light/dark/system + accent), i18n resource files (`en` base; extraction script; community translation PR flow), RTL-safe layout.

**§9.4 First-run onboarding** — S
* Work: wizard: hardware survey → recommended runtime + models that fit (§2.3/§3.4) → models folder → optional MCP/API token setup.

**§9.5 Auto-update + installer + single-file publish** — L
* Work: MSIX (or WiX MSI) packaging, self-contained single-file publish (`PublishSingleFile`, trimming off for reflection-heavy parts), Velopack/Squirrel-style delta updates with signed manifest, release channels (stable/beta), runtimes updated independently of the app.

**§9.6 Notifications** — S — Windows toast (download/load complete, errors) with per-type toggles.

**§9.7 Accessibility + responsive layout** — M — ARIA roles/labels, focus order, contrast checks (axe CI), min-width 360 px layout, reduced-motion.

### §10 Quality, safety & operations

**§10.1 Automated tests** — L (start Phase 0, continues forever)
* Work: test projects per D7; fake `IEngine` (scripted tokens) for adapter/contract tests; fixture recorder; CI matrix (windows-latest CPU; self-hosted AMD runner for Vulkan); coverage gate on Core/Server.

**§10.2 Robust failure handling** — M
* Work: classify failures from engine stderr/exit codes (OOM, device lost, unsupported arch, corrupt file); auto-retry loader ladder (fewer layers → smaller ctx → CPU) with user-visible explanation; engine process watchdog + restart; typed error codes in all APIs.
* Done when: forcing OOM yields a successful lower-offload load and a clear message.

**§10.3 Settings schema versioning + validation** — S
* Work: `schemaVersion`, ordered migrations, JSON-schema validation of settings/presets/defaults with field-level errors surfaced in UI; backup before migration.

**§10.4 Structured logging** — S
* Work: `Microsoft.Extensions.Logging` + Serilog file sink (rotating, JSON), categories (engine, api, download, mcp), correlation id per request, runtime log level control (UI + `config loglevel`), llama.cpp output captured per instance.

**§10.5 Security review** — M
* Work: threat model (LAN exposure, malicious GGUF/mmproj, prompt injection → MCP tools, SSRF via `image_url`/`ephemeral_mcp`/URL imports), fixes: path normalisation on all IDs, body/size/time limits, deny private-range fetches for remote URLs unless allowed, auth on the web UI session (cookie/token) when not loopback, CSRF protection for state-changing UI routes, secrets in DPAPI, MCP command allow-list/confirmations, dependency scanning, signed binaries.

**§10.6 Crash dumps / diagnostic bundle** — S — `llmhost diag` zips logs (redacted), settings (secrets stripped), hardware, runtime versions, recent request metadata.

**§10.7 Documentation** — M — user guide (install, models, GPU/AMD setup), API reference generated from OpenAPI, troubleshooting (Vulkan driver, VRAM errors), contributor guide, changelog.

---

## 5. Cross-cutting requirements

* **Compatibility tests:** keep a `compat/` suite with recorded request/response fixtures from OpenAI, Anthropic and BlahLLMWrapper SDKs; run on every PR.
* **Performance budget:** TTFT on a warm 4k context ≤ 300 ms for 0.5B model; API overhead (host proxy) < 5 ms p50; UI stream render 60 fps.
* **Backward compatibility:** keep existing CLI verbs, `/v1` and `/api/*` management routes for one release behind `/api/legacy` aliases; settings migration (§10.3).
* **Feature flags:** every Phase-4+ feature behind a flag until its tests are green.
* **Telemetry:** none by default; any future opt-in must be explicit and documented (§2.8).

## 6. Risk register

| Risk | Impact | Mitigation |
|---|---|---|
| llama-server flags/behaviour change between builds | engine breaks silently | pin build per app release; capability probe at runtime start; contract tests against the pinned binary |
| Child-process management (ports, zombies, AV false positives) | flaky loads | job object (kill-on-close), random free ports on loopback, restart watchdog, code-sign binaries |
| Memory estimator inaccurate | OOM / needless CPU offload | calibrate on logs, keep retry ladder (§10.2), user override |
| RAG quality/latency | poor answers | pluggable chunking/embedding, evaluation set, expose settings |
| MCP security (arbitrary code execution) | local compromise | confirmations, allow-lists, sandbox warnings, no auto-install |
| Scope size | schedule slip | phase gates; defer optional items (§5.3, §6.13, §6.14, §8.2) |
| BlahLLMWrapper API drift (docs partially unreadable) | incompatible shapes | re-verify unread pages (streaming events, MCP-via-API, TTL/evict, model.yaml, runtimes) before building §6.1/§6.5/§6.6/§3.9/§2.1 |

## 7. Effort summary

| Area | Items | Rough total |
|---|---|---|
| Foundations (D1 spike, D2, D6, §10.1–§10.4) | 7 | 7–9 wks |
| Engine (§1) + hardware/runtimes (§2) | 24 | 12–15 wks |
| Model management (§3) | 10 | 4–5 wks |
| API surface (§6) incl. MCP, excl. plugins/SDK | 12 | 9–11 wks |
| Chat, presets, RAG (§4, §5) | 14 | 9–11 wks |
| CLI + daemon (§7, §6.4) | 5 | 3–4 wks |
| Desktop (§9) | 7 | 5–7 wks |
| Remote (§8) | 2 | 4–5 wks |
| Hardening/docs (§10.5–§10.7) | 3 | 3 wks |
| **Total (single engineer, sequential)** | ~84 | **≈ 56–70 wks**; ≈ 28–35 wks with two engineers working in parallel after Phase 0 |

## 8. Suggested first 6 weeks (concrete)

1. **Week 1:** D1 spike (llama-server Vulkan + CPU on the 7900 XTX), decide C/B, pin build, write `IEngine` interfaces.
2. **Week 2:** solution split (D6), canonical request model (D2), port OpenAI + Anthropic adapters, test project + CI skeleton (§10.1).
3. **Week 3:** `LlamaServerEngine` (spawn, health, proxy, shutdown with job object), `BuiltinEngine` wrapper, settings versioning + logging (§10.3, §10.4).
4. **Week 4:** `RuntimeManager` MVP (download/select Vulkan + CPU), runtime selection in load dialog/CLI (§2.1, §2.5).
5. **Week 5:** parallel requests + cache reuse (§1.1, §1.2) and fit estimator v1 (§2.3).
6. **Week 6:** native tool calling + reasoning (§1.4, §1.5) with adapters; demo milestone M1: _"BlahLLMWrapper-class engine on AMD, existing UI/CLI/API unchanged"_.
