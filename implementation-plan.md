# Implementation plan — P2 items

Companion to [todo.md](todo.md). Covers **every open P2 item**, referenced as `§<section>.<n>` (n = position of the item inside
that section of `todo.md`). P1 is finished; P3 is out of scope here. This plan replaces the earlier plan, whose "host
`llama-server` as child processes" strategy no longer matches the code: the app embeds llama.cpp in-process through
LLamaSharp 0.27 and already has continuous batching, per-slot KV reuse, vision (mtmd), reasoning, native tool calling,
runtime manager and multi-GPU. The plan builds on that.

Effort (one engineer, includes tests): **S** ≤ 2 d · **M** 3–5 d · **L** 6–10 d · **XL** > 10 d.

**Status convention:** a finished item gets ✅ at the very start of its heading or table row (never at the end), with its commit
hash in the status note or heading; tick its `- [x]` checkbox in [todo.md](todo.md), and update the progress line below.

**Progress:** section 2 prerequisites X1–X5 done. Section 4 items done: §1.4 samplers, §1.1 prompt-cache follow-ups, §1.2 vision follow-ups (3 of ~50).

---

## 1. Ground truth about the current code (what the plan hooks into)

| Area | Where | Relevant facts |
|---|---|---|
| Engine | [Inference/ModelRuntime.cs](Inference/ModelRuntime.cs) | One `LLamaContext` per loaded model, scheduler task builds one batch per iteration over `Slot`s (≤ `MaxConcurrent`, unified KV pool). Each slot keeps its last token list (prefix reuse). Sampler is `DefaultSamplingPipeline` built in `BuildSampler`. |
| Options | [Core/Settings.cs](Core/Settings.cs) | `LoadOptions` (per load), `GenerationOptions` (per request), `AppSettings`, `PresetStore` (single `presets.json`), `AppPaths`. |
| Manager | [Inference/ModelManager.cs](Inference/ModelManager.cs) | `_loaded` dictionary keyed by identifier, `_loadGate`, idle timer using `LoadOptions.TtlMinutes`. |
| Native | [Inference/NativeBackend.cs](Inference/NativeBackend.cs), [RuntimeManager.cs](Inference/RuntimeManager.cs) | Native library chosen **once per process**. |
| API | [Server/ApiServer.cs](Server/ApiServer.cs) (+ NativeApi, ResponsesApi, AnthropicApi, McpApi, DocumentsApi, ApiAuth) | Minimal-API `Map*` routes; `/api/*` management routes; `LogBuffer` ring buffer for `/api/logs`. |
| UI | [Web/index.html](Web/index.html) (1365 lines, single file), [Desktop/MainWindow.cs](Desktop/MainWindow.cs) | Chats persisted server-side in `chats/<id>.json` via `/api/chats`. |
| CLI | [Cli/Shell.cs](Cli/Shell.cs) (1541 lines) | In-process shell + sub-commands, `Daemon.cs` for background mode. |

**Already done but still unchecked in todo.md** (verify, add tests if missing, then tick):
`logprobs`/`top_logprobs` and `n > 1` (OpenAI chat + completions, [ApiServer.cs:290](Server/ApiServer.cs:290)) → close the
first half of §1.11; Anthropic `image` and `thinking` blocks ([AnthropicApi.cs:110](Server/AnthropicApi.cs:110)) → first
half of §6.9 (remaining: images in tool results, `tool_choice`, `cache_control`).

---

## 2. Cross-cutting prerequisites (do first; unblock many items)

| ID | Work | Effort | Unblocks | Status |
|---|---|---|---|---|
| ✅ **X1** Settings schema version + migration + validation (§10.1) | Add `schemaVersion` to `settings.json`, `presets`, `model-defaults`, `chats`; `Migrations` list run at startup (backup file `.bak-vN` before migrating); field-level validators returning `{field, message}`; `PUT /api/settings` returns 400 with that list. | M | every item that adds settings/files (X1 first so later items only add a migration step) | Done `40690ec` — `Core/Migrations.cs`, `Core/SettingsValidator.cs`; inline errors in the settings page; unknown keys kept in `settings.json.invalid` |
| ✅ **X2** Structured logging (§10.2) | Replace ad-hoc `LogBuffer.Add` sinks with a thin logger writing JSON-lines to `logs/llmhost-YYYYMMDD.log` (size+age rotation, N files), keep `LogBuffer` as the live tail. Wire llama.cpp native log callback with level filter. | M | §6.12 log viewer, §10.3 | Done `16cb207` — `Core/FileLog.cs`, redaction, `logging.*` settings, `log level`/`log path` CLI |
| ✅ **X3** Capability model | `ModelCapabilities { Vision, Audio, Tools, Reasoning, Embedding, Moe, Rerank }` computed from GGUF metadata + chat template + mmproj; exposed in `/v1/models`, `/api/models/local`, `/api/status`. | S | §3.1 badges, §1.12 embedding/rerank, §4 UI | Done `48259a2` — `Models/ModelCapabilities.cs`; UI badges are §3.1 |
| ✅ **X4** Option-plumbing pattern | One place that maps a new field through: `LoadOptions`/`GenerationOptions` → model defaults → presets → OpenAI/Anthropic/native mapping → CLI `params`/`load` → UI param panel → tests. Document it in README so each item below is a checklist, not a design. | S | all | Done `d343da1` — `docs/adding-options.md` + `OptionsPlumbingTests` (found and fixed a shared `LogitBias` in `Clone()`) |
| ✅ **X5** Test conventions | Pure logic gets unit tests in `UnitTests.cs`; HTTP surface in `ApiTests.cs`; anything needing a model uses the Qwen-0.5B fixture in `IntegrationTests.cs` (skipped when absent). | — | all | Done `2cc6d80` — `tests/README.md`, `Category=Unit|Api` traits |

---

## 3. Phasing

| Phase | Theme | Items |
|---|---|---|
| **A** (2–3 wk) | Foundations + cheap engine wins | X1–X4, §1.4 sampler set, §1.5 overflow policies, §1.11 close-out, §4 live token counter data, §6.1 JIT policy |
| **B** (3–4 wk) | Model management + presets | §3.1–§3.5, §5.1–§5.2, §2.4 GPU ratio, §2.6 thread pool |
| **C** (4–5 wk) | Engine depth | §1.3 prompt templates, §1.1 cache slots/disk, §1.6 MoE, §1.7 LoRA, §1.8 multi-instance, §1.12 embeddings/rerank, §1.2 vision follow-ups, §1.2b speculative decoding |
| **D** (4–5 wk) | UI | componentise `index.html`, then §4.1–§4.6, §9.1–§9.5 |
| **E** (3–4 wk) | Server hardening + ecosystem | §6.2–§6.5, §10.3, §7, §2.3 hardware monitor, §2.5 worker processes |
| **F** (optional, XL) | Remote | §8.1 LM-Link-style network |

Order inside a phase is free; dependencies are listed per item.

---

## 4. Item plans

### §1 Inference engine

#### ✅ §1.1 Prompt-cache follow-ups — **L** (`026bd55`)
Goal: multiple cache slots, persist to disk, (llama-server cache: see note).

**As built:** `LoadOptions.CacheSlots` (0 = parallel requests + 2) sets the number of sequences; only `MaxConcurrent` may generate at once. `PickSlot` reuses the longest shared prefix (≥ 32 tokens, or the whole short prompt), else an empty slot, else the least recently used. Saved caches: `Inference/PromptCacheStore.cs`, `POST /api/models/cache/save|restore`, `GET /api/models/cache/saved`, `DELETE /api/models/cache/saved`; `promptCache.autoSave` writes idle caches as `auto-N` on unload and restores them on load; `promptCache.maxDiskMb` caps the folder. Deviations: auto-save is per model, not per chat id; no `modelSha` header (the folder key is library id + file size); the llama-server part stays deferred to §2.5. Found while testing: unloading used to wipe caches before they could be saved.
* **Several slots.** Today `Slot.Cache` is the only per-sequence history, and slots are bounded by `MaxConcurrent`. Decouple
  *cache entries* from *active slots*: `PromptCachePool` per runtime holding up to `CacheSlots` (new `LoadOptions.CacheSlots`,
  default = `MaxConcurrent`+2) entries `{Tokens, SeqId, LastUsed, ImageKey}`. On admission pick the entry with the longest common
  prefix (LCP); if none ≥ 32 tokens reuse LRU. Needs spare `n_seq_max` in the context (`max(MaxConcurrent, CacheSlots)`) and a
  KV-budget check, since all sequences share the unified pool (`while (InUse() + need > ContextSize)` loop at
  [ModelRuntime.cs:727](Inference/ModelRuntime.cs:727) must evict idle cache entries before blocking active requests).
* **Disk save/restore.** LLamaSharp exposes `State.SaveState`/`SaveSeqState` (`llama_state_seq_save_file`). Add
  `POST /api/models/cache/save {identifier, name}` and `/restore`, files under `cache/<model-hash>/<name>.kv` with header
  `{modelSha, ctx, kvTypes, tokens}`; reject restore on mismatch. Option `autoSaveCache` writes the longest entry on unload and
  restores on next load of the same chat (key = chat id sent as `x-cache-key`/`user`). Disk cap in settings (default 2 GB, LRU).
* **llama-server engine cache.** Not applicable until §2.5 (worker processes) exists; if workers use `llama-server`, pass
  `--slot-save-path` and `cache_prompt` through. Defer; record in §2.5.
* **Tests:** two alternating prompts keep ≥ 90 % cached tokens; evicting under pressure never drops an active slot; restore of
  a mismatched file is rejected.
* Risks: unified-KV fragmentation → run `llama_kv_cache_defrag` when `cached/ctx > 0.8`.

#### ✅ §1.2 Vision follow-ups — **L** (`35b1cf0`, `f1467c8`, `68d3505`, `b49c817`, audio: see below)
1. ✅ **Per-image options (S).** `detail: low|high|auto` on `image_url` → max edge (e.g. 512 / 1344) resized in `ImageInput`
   As built: `System.Drawing` (the app is Windows-only); `vision.maxImageEdge` (0 = off) and `vision.lowDetailEdge` (512) in settings; `detail` read from OpenAI chat, Responses and native `image` parts; the CLI `/image` applies the general limit.
2. ✅ **Encoded-image cache (M).** Hash image bytes (xxhash) → cache the `mtmd` encoded chunk/embeddings per runtime (LRU, N MB);
   lets a request with images reuse the KV prefix too: store `ImageKey` list on cache entries (§1.1) and treat an image chunk as a
   token run of known length. Removes "requests with images always re-evaluate".
   As built: the KV reuse only (`ImageSignature`, `EvaluateWithReuse` in `ModelRuntime`): each image becomes N stand-in tokens from the hash of its bytes, the usual longest-prefix logic picks the slot, and evaluation resumes at the first changed chunk. An embedding-only cache for an image that moved position was not built. Not used for M-RoPE models (`UsesMRope`: position count differs from token count), which keep the old behaviour. Verified on SmolVLM-256M: follow-up turns reuse the image, and a warm answer equals a cold one.
3. ✅ **Images in tool results (M).** Accept tool-result content arrays containing images in OpenAI `tool` messages, Responses
   `function_call_output`, Anthropic `tool_result` blocks; map to a synthetic user message carrying the image after the tool
   message when the template cannot embed images in tool turns. Add MCP tool results that return `image` content.
   As built: always the synthetic user turn (`ToolImagesMessage`), never inside the tool turn; MCP results keep `image` content (`McpToolResult.Images`) and the native tool loop feeds it back; `POST /api/mcp/call` returns `images` as data URLs. The web UI's own MCP loop still sends text only.
4. ✅ **Audio input (L).** Check `mtmd` audio support in the LLamaSharp 0.27 native build (`mtmd_support_audio`); add
   `input_audio` parts (OpenAI) → decode WAV/MP3 (NAudio) to 16 kHz mono PCM → `mtmd_bitmap_init_from_audio`. Capability flag
   `audio` from X3. UI: attach audio file. Blocked if the pinned native lib lacks audio — then gate behind runtime update.
   As built: no NAudio decoding step — the multimodal loader decodes WAV, MP3 and FLAC itself, so audio travels in the same media list as images (`ImageInput.IsAudio` / `FromAudio`, `input_audio` parts in OpenAI chat and Responses, CLI `/audio`); `ModelRuntime.SupportsAudio` follows the projector and drives the `audio` capability; a vision-only model answers with a clear error. **Not verified with a real audio model** (none available locally; needs a multi-GB Omni/Voxtral-class model): only the mapping, format checks and the refusal path are tested. The web UI has no audio attach button yet.
5. ✅ **Video frames (S).** Client-side/CLI: `/video file.mp4 --fps 1 --max-frames 8` extracts frames (ffmpeg if found, else
   Media Foundation) and sends them as ordered images. No new engine work.
   As built: CLI `/video <file> [--fps 1] [--frames 8]` via ffmpeg only (next to the app, on PATH, or `vision.ffmpegPath`); Media Foundation was not implemented. Not exercised against a real video here (no ffmpeg on this machine): only argument building and error paths are tested.
* llama-server vision: out of scope until §2.5.

#### §1.2b Speculative decoding (todo §1.5) — **L**
* **Draft model (L).** Add `LoadOptions.DraftModel`, `DraftTokens` (default 8), `DraftMinP`. Load a second `LLamaWeights` +
  context; vocab compatibility check (compare token count + first/last 512 tokens' text; refuse otherwise with a clear message).
  Algorithm in the scheduler: for a *single generating slot* draft k tokens greedily, evaluate them in one target batch, accept
  the longest matching prefix, roll back KV with `llama_kv_cache_seq_rm`. Disable automatically when > 1 slot is generating
  (batching is already efficient) and with grammar/logit-bias samplers unless verified token-by-token through the same sampler.
  Check whether LLamaSharp exposes `llama_speculative_*`; if not, implement in C# (≈300 lines).
* **Suggested pairings (S).** Static table in `Catalog.cs` (`family → draft candidates`, e.g. Qwen 0.5B for Qwen 7B+) plus a
  "draft" badge in the Discover list; UI picker filters local models by same arch/vocab and size ≤ 1/8 of target; show estimated
  extra memory via `FitEstimator`.
* **MTP-style speculation (M, research first).** Models with built-in MTP heads (spot via GGUF `*.nextn_predict_layers` key)
  need upstream llama.cpp support; implement only if the pinned native build supports it, otherwise leave as a checklist item
  gated on the runtime version in `RuntimeManager`.
* Stats: report `draft_accepted/draft_total` in `GenerationStats`, shown in UI and `/api/status`.
* **Acceptance:** ≥ 1.3× tok/s on a repeating-text benchmark with Qwen 0.5B → 7B; output identical with temperature 0.

#### §1.3 Prompt-template override — **M**
* Today `ApplyChatTemplate` uses the GGUF template. Add `LoadOptions.PromptTemplate { Mode: auto|jinja|manual, Jinja, Manual }`
  where `Manual = { system:{prefix,suffix}, user:{…}, assistant:{…}, tool:{…}, bos, eos, stopStrings[] }`.
* Jinja rendering: LLamaSharp's `LLamaTemplate` supports llama.cpp's built-in set only. For arbitrary Jinja add
  **Fluid** or **Scriban** (+ `tojson`, `strftime_now`, `raise_exception` helpers). Keep llama.cpp's renderer as default;
  use Jinja only for an explicit override. Test against 5 templates (ChatML, Llama 3, Gemma, Mistral, Qwen3 with thinking).
* Manual mode renders without any engine: unit-testable string builder; auto-adds the manual stop strings to
  `GenerationOptions.Stop`.
* Auto-detect: read `tokenizer.chat_template` in `Gguf.cs` (already parsed) and show it; "Always show template" toggle
  (`settings.alwaysShowTemplate`) in the load dialog. Validation endpoint `POST /api/models/template/preview {messages}` returns
  the rendered prompt so the UI can show a live preview.
* Interaction: reasoning/tool formatting in `Reasoning.cs`/`ToolCalling.cs` detect model families from the template text — they
  must keep working with an override (feed them the *effective* template).

#### ✅ §1.4 Additional samplers — **M** (`f5326bb`)
* Extend `GenerationOptions`: `Mirostat (0|1|2)`, `MirostatTau`, `MirostatEta`, `DryMultiplier`, `DryBase`, `DryAllowedLength`,
  `DryPenaltyLastN`, `DrySequenceBreakers`, `XtcProbability`, `XtcThreshold`, `TopNSigma`, `SamplerOrder` (string list).
* `DefaultSamplingPipeline` fixes the chain. Replace `BuildSampler` with a **custom `ISamplingPipeline`** composed from the
  native chain API (`llama_sampler_chain_add`, `llama_sampler_init_mirostat[_v2]`, `_dry`, `_xtc`, `_top_n_sigma`, `_typical`,
  `_temp_ext`, `_grammar`, `_logit_bias`, `_penalties`, `_dist`). Check LLamaSharp 0.27 exposes these in `NativeApi`/
  `SafeLLamaSamplerChainHandle`; where a sampler is missing (TFS was removed upstream, so it is not offered) implement as an `ISamplerStage` over the
  candidate array in C#.
* Default order mirrors llama.cpp: `penalties → dry → top_n_sigma → top_k → typ_p → top_p → min_p → xtc → temperature`;
  mirostat replaces the truncation + temperature stages. `SamplerOrder` = ordered subset of those names; validated.
* Grammar must always be applied first (unchanged semantics); logprobs (`RecordLogprob`) must be taken from the pre-sampler
  distribution — keep that working.
* Surface: OpenAI extension fields (`mirostat`, `dry_multiplier`, `xtc_probability`, … same names as llama-server for client
  compatibility), native API, CLI `params`, UI "Advanced sampling" collapsible, presets.
* Tests: deterministic seed + temperature 0 unchanged; DRY reduces repeats on a loop prompt; sampler-order validation.

#### §1.5 Context-overflow policies — **M**
* `GenerationOptions.ContextOverflow = dropOldest (current) | truncateMiddle | rollingWindow | stopAtLimit | error`, plus
  `KeepFirstTokens` (default: system prompt) and `KeepLastTokens`.
* Prompt-time policies live in `BuildFittingPrompt` ([ModelRuntime.cs:337](Inference/ModelRuntime.cs:337)):
  `truncateMiddle` removes whole messages from the middle (keeps system + first user + latest N), inserting an
  `[… N earlier messages omitted …]` marker; `error` returns HTTP 400 `context_length_exceeded` (OpenAI error code).
* Generation-time policies in the scheduler: `rollingWindow` shifts the KV cache (`seq_rm` of the oldest `n_discard` tokens after
  `KeepFirstTokens`, `seq_add` the rest negative, update `Slot.Cache`) when a slot reaches the context; `stopAtLimit` ends with
  `finish_reason: length`. Requires RoPE-shift support (check `llama_kv_self_can_shift`; refuse with message otherwise).
* Report applied policy and dropped counts in stats (`context_dropped_messages`, `context_shifts`).
* Setting precedence: request > chat > model default > global.

#### §1.6 MoE controls — **M**
* `LoadOptions`: `MoeExpertCount` (override `*.expert_used_count` metadata key via `llama_model_kv_override`),
  `MoeCpuLayers` (`--n-cpu-moe` equivalent) and `TensorOverrides: [{pattern, buffer}]` (e.g. `\.ffn_.*_exps\.=CPU`).
  LLamaSharp: `LLamaModelParams.tensor_buft_overrides` — verify it is exposed in 0.27; otherwise pin to marshal the struct
  ourselves (small P/Invoke addition in `NativeBackend.cs`).
* "Force expert weights onto CPU" = convenience that generates the standard pattern; combined with `Device=gpu` it keeps
  attention+KV on GPU. `FitEstimator` must treat overridden tensors as host memory (estimate MoE expert bytes from GGUF tensor
  list names `ffn_*_exps`) — extend `GgufInfo` with per-tensor sizes (the reader already walks tensor infos).
* UI: shown only for MoE models (capability from X3); tooltip with expected VRAM saving; Power-User mode (§9.1).
* Tests: estimator math on a synthetic tensor list; load with override on a MoE fixture if available (else unit-only).

#### §1.7 LoRA adapters — **M**
* `LoadOptions.Loras: [{path, scale}]`. After model load, `llama_adapter_lora_init` + `llama_set_adapters_lora` on the context
  (LLamaSharp `LoraAdapter`/`context.LoadLoraAdapter`). Validate base-model match (GGUF `general.architecture`, adapter's
  `adapter.lora.alpha`), refuse mismatches.
* Library: scan `*.gguf` with `general.type = adapter` into a separate **Adapters** list (`ModelLibrary` currently lists them as
  models — filter them out and show under the base model). Download/import reuse existing flows.
* Hot swap: `POST /api/models/lora {identifier, adapters:[{id,scale}]}` — changing scale does not need reload (re-apply to
  context; clear caches because KV is invalid).
* FitEstimator: add adapter file sizes. Per-request LoRA is out of scope (document).

#### §1.8 Multiple instances of the same model — **M**
* `ModelManager.LoadAsync` already supports `options.Identifier`, but `Get(id)` returning an existing instance and the
  `/v1/models` listing assume 1 per model. Work: (a) never dedupe by file, only by identifier; (b) auto-suffix identifiers
  (`name`, `name:2`) when requested with `allowDuplicate`; (c) share mmapped weights between instances — keep a
  ref-counted `LLamaWeights` cache keyed by `{path, gpuConfig}` so a second instance only pays for context + KV;
  (d) request routing: `model` field may be the base name — pick the least-loaded instance (`ActiveRequests + QueuedRequests`);
  (e) unload by identifier only unloads weights when the refcount reaches 0.
* Fit check must count only the incremental cost (context + compute buffers) for a shared-weights instance.
* UI: "Load another instance" in the loaded-models list with distinct context size / params; CLI `load --identifier`.
* Tests: two instances with different context sizes answer in parallel; unloading one keeps the other.

#### §1.11 `logprobs` / `n` — **S** (close-out)
Verify existing OpenAI chat/completions implementation against the spec (bytes arrays, `top_logprobs` ≤ 20, streaming chunk
shape, `n` with stream and `index`); add Responses API `top_logprobs` + `include: ["message.output_text.logprobs"]` and
`/api/v1/chat` pass-through if missing; add contract tests; then tick the item.

#### §1.12 Embeddings options & reranking — **M**
* Embeddings: `LoadOptions.Pooling (auto|none|mean|cls|last|rank)`, `Normalize` (default true → L2), `EmbedBatchSize`; request
  fields `dimensions` (truncate + renormalise, Matryoshka), `encoding_format: float|base64`, `truncate`. Batch many inputs into
  one decode via multiple sequences (current `EmbedAsync` is one text at a time). Detect embedding GGUFs (X3) and prevent chat
  use with a clear error.
* Reranking: `POST /v1/rerank` and `/rerank` (Jina/Cohere shape: `query`, `documents`, `top_n`, `return_documents`) using
  pooling `rank` models (bge-reranker GGUF). Score = classifier-head output; order by score. Add the catalog entries
  (bge-reranker-v2-m3, Qwen3-Reranker).
* RAG (`Documents.cs`) can optionally rerank retrieved chunks (`settings.rag.rerankModel`).
* Tests: vectors identical (±1e-4) between batch and single; normalisation unit-length; rerank ordering on a toy set.

### §2 Hardware & runtimes

#### §2.4 GPU offload as ratio — **S**
`LoadOptions.GpuRatio (0–1)` and `Device: "max"|"auto"|<ratio>`; `PlanGpuLayers`
([ModelRuntime.cs:89](Inference/ModelRuntime.cs:89)) converts: `max` = all layers, `auto` = VRAM-fit result from
`FitEstimator`, ratio = `round(ratio × nLayers)`. Accept `--gpu max|auto|0.0–1.0` in CLI `load`, `"gpu"` in
`/api/v1/models/load`; UI slider with layer-count readout; keep `GpuLayers` as the explicit override (wins when both set).
Tests for the conversion.

#### §2.5 Per-backend worker processes (CPU ↔ Vulkan at runtime) — **XL**
Problem: the native library is loaded once per process ([NativeBackend.cs](Inference/NativeBackend.cs)).
* Introduce `IModelHost` with two implementations: `InProcessHost` (today's `ModelRuntime`) and `WorkerHost` (child process
  `llmhost.exe --worker --backend vulkan|cpu-avx2 --pipe <name>`). Worker contains only `ModelRuntime` + a small RPC server
  over a named pipe / localhost TCP with token (messages: `Load`, `Unload`, `Generate` stream, `Embed`, `Tokenize`, `Cache*`,
  `Stats`). Reuse the canonical types by moving `GenerationOptions`, `LoadOptions`, `ChatMessage`, `TextSegment` to a shared
  namespace (already plain POCOs).
* `ModelManager` picks the host per model: backend requested in `LoadOptions.Backend` (`auto|cpu|vulkan`); one worker per
  distinct backend+runtime id, started lazily, restarted on crash (the loaded model is reported as failed with the exit reason —
  also gives OOM/driver-crash isolation).
* The first worker for a runtime reuses `RuntimeManager` to pick the native library directory (set `LLAMA_NATIVE_PATH` for the
  child).
* Everything that touches `ModelRuntime` directly (ApiServer, NativeApi, CLI) must go through an interface (`IModelHost` →
  `ILoadedModel`): do this refactor **first** as a no-behaviour-change PR (M), then add `WorkerHost` (L).
* `llama-server` as an alternative worker engine and the llama-server prompt-cache item are intentionally **not** done; the
  worker protocol leaves room for it.
* Acceptance: load Qwen-0.5B on CPU and (concurrently) Vulkan in one running instance; kill a worker → host stays up; no
  latency regression > 5 % vs. in-process.

#### §2.3 Live hardware monitor — **M**
* Sampler thread (1 Hz while any UI/SSE client listens, 0 otherwise): CPU % (PDH `\Processor Information(_Total)\% Processor
  Utility`), RAM (existing `MemoryStatus`), process working set, per-GPU dedicated/shared memory used and engine utilisation via
  PDH counters `\GPU Engine(*)\Utilization Percentage` and `\GPU Adapter Memory(*)\Dedicated Usage` (works for AMD/NVIDIA/Intel;
  map `luid_…` → adapter using DXGI LUID already read in `Hardware.cs`). Temperature: not available via PDH — try vendor
  libs optionally (NVAPI/ADL are out of scope; show "n/a"); optionally LibreHardwareMonitorLib behind a settings toggle.
* `GET /api/hardware/live` (snapshot) and `GET /api/hardware/stream` (SSE). UI: small sparkline panel in the header and a
  full panel in Settings → Hardware; per-loaded-model VRAM estimate vs. actual.
* Tests: counter-name parser unit tests; endpoint returns schema even when counters are missing.

#### §2.6 CPU thread pool, affinity, NUMA — **M**
* `LoadOptions` additions: `ThreadsBatch` (exists as `BatchThreads`; expose consistently as `threads_batch`), `CpuAffinityMask`
  / `CpuList`, `ThreadPriority (normal|high|low)`, `NumaMode (off|distribute|isolate|numactl)`, `Poll (0–100)`.
* llama.cpp's own thread pool is created per context: LLamaSharp exposes only `n_threads`. Implement affinity/priority by
  enumerating process threads after load and when the scheduler thread starts (Win32 `SetThreadAffinityMask`/
  `SetThreadPriority`; for processor groups > 64 logical CPUs use `SetThreadGroupAffinity`). NUMA: call
  `llama_numa_init` once at backend init (setting `numa`), before first model load — takes effect after restart, show a banner.
* Defaults: physical cores for generation, all logical cores for prompt processing (detect via
  `GetLogicalProcessorInformationEx`), replacing the current 75 % heuristic ([Hardware.cs:205](Core/Hardware.cs:205)).
* UI in Power-User mode; "Test thread count" benchmark button (runs a 128-token generation at 2–3 settings).

### §3 Model management

#### §3.1 Capability badges & richer model card — **M** (needs X3)
* `Gguf.cs` already parses KV metadata: add architecture, parameter count, context length, expert count, embedding length,
  license (`general.license`), languages, base model; chat-template feature scan (`tools`, `enable_thinking`).
* `HuggingFace.cs`: `GET /api/models/{id}` → card data (`cardData`, `tags`, `license`, `downloads`, `likes`, `lastModified`)
  plus README (`/raw/main/README.md`, strip front-matter, sanitize rendered HTML/Markdown, cache 24 h under `cache/hf/`).
* UI: badge row (👁 vision · 🔧 tools · 🧠 reasoning · ⚙ MoE · 📐 embedding) in Discover, My Models, model picker;
  detail drawer with README, files/quants with fit hint, license (warning on non-commercial), architecture table.
* Tests: capability derivation from fixture metadata dictionaries.

#### §3.2 Discover filters, sorting, pagination, staff picks — **M**
* `/api/models/search` parameters: `sort (trending|downloads|likes|updated)`, `quant`, `minSize/maxSize`, `family` (HF
  `tags`/`pipeline_tag`), `fits=gpu|ram|any`, `vision/tools/reasoning` (tag-based), `limit`, `cursor` (HF returns `Link` header
  for pagination → pass through). "Fits my GPU" is client-side from the per-file list using `FitEstimator`
  (post-filter, flagged as approximate).
* Staff picks: move the hard-coded catalog in `Catalog.cs` to `catalog.json` embedded as default + optional remote refresh
  (`settings.catalogUrl`, default a raw-file URL of the project repo; off in offline mode, ETag caching, signature not needed
  — schema-validated only). UI shows "updated <date>".
* Persist filter state in `localStorage`.

#### §3.3 Gated-model flow — **S**
* Token validation: `GET https://huggingface.co/api/whoami-v2` with Bearer → show account name or "invalid" in Settings.
* On 401/403 from `resolve/` or repo-info (`gated: "auto"|"manual"` field), return structured error
  `{code: "gated", repo, accessUrl}`; UI shows "This model requires accepting a licence on Hugging Face" with button opening
  `https://huggingface.co/<repo>` (external browser) and a "Retry" button; DownloadManager marks job `needs-access` instead of
  failing generically. Token stored encrypted with DPAPI (`ProtectedData`) rather than plain `settings.json` (migration via X1).

#### §3.4 Model update check — **M**
* At download time store `{repo, file, revision(sha), lfsSha256, etag}` in a sidecar `<file>.llmhost.json` (backfill for
  existing files by hashing is **not** done; those show "unknown").
* `POST /api/models/updates/check` (and background daily check if enabled): for each tracked file query
  `/api/models/<repo>/tree/main` → compare LFS sha256; also report newer **quant files** with the same base name and repos
  whose name changed (`-GGUF` successor via `modelId` redirect). Result `updatesAvailable[]`; UI badge in My Models;
  "Update" = new download job that replaces atomically (download to `.part`, verify SHA, swap, keep old until success).
* Rate-limit and honour offline mode.

#### §3.5 Models-directory management — **M**
* Multiple library roots: `settings.modelsDirectories[]` (first = download target); `ModelLibrary` merges them, ids stay
  `publisher/repo/file`, duplicates resolved by first root. Change/move: `POST /api/models/directory/move {to, mode:
  move|copy|switchOnly}` with progress job (reuse DownloadManager job list for visibility), refuses to move while models are
  loaded from the folder, handles cross-volume.
* Disk usage: per-model size column, per-root free space, "largest models" sort, total chart in Settings; stale `.part`
  files cleanup action.
* CLI: `models dir [add|remove|move]`. Tests with temp directories.

### §4 Chat experience (Phase D; after splitting the UI — see §9.0)

#### §9.0 UI restructuring (prerequisite for most of §4 and §9) — **L**
`Web/index.html` is a single 1365-line file. Convert to **Preact + HTM or Vite build** whose output is embedded as resources
(no Node needed at runtime; build step in CSProj `Exec` guarded by `node` presence, committed `dist/` fallback so building
without Node still works). Split into modules: `api.js`, `store.js` (signals), `views/{chat,models,discover,settings,logs,
runtimes}.js`, `components/*`. Keep the existing tooltip help (`p-*` keys) data-driven. Do it incrementally view by view; ship
behind no feature flag (same URL). Add a Playwright smoke test (launch host, open `/`, send a prompt with the fixture model).

#### §4.1 Chat management — **L**
* Storage: `chats/` becomes a tree: add `folders.json` (`{id, name, parentId, order}`) and `folderId`, `title`, `titleAuto`,
  `pinned`, `updatedAt`, `deleted?` on chat JSON (migration via X1). API: `GET/PUT/DELETE /api/chat-folders`,
  `POST /api/chats/{id}/duplicate` (copies messages, tree, and **documents/attachments** under new ids — `Documents.cs`
  chunk store needs a `Clone(chatId→newId)`), `POST /api/chats/bulk-delete`, `PUT /api/chats/{id}/rename`.
* Auto titles: after the first assistant reply run a 12-token "Title this conversation" request on the loaded model with
  low priority (skip if the queue is busy; setting to disable; fall back to first 40 chars of user text).
* Search: server-side full text over `chats/*.json` (in-memory inverted index built at startup, updated on save; matches
  show snippet + jump to message). Optional later: SQLite FTS5.
* Import/export: JSON (native lossless), Markdown, plain text; import accepts native JSON and OpenAI-style conversation
  exports. Drag-and-drop folder/chat reordering in the sidebar (HTML5 DnD).
* Safety: validate chat ids (`[0-9a-f-]{36}` only — ties to §10.3 path traversal).

#### §4.2 Split view & compare — **L** (needs §1.8 for same-model compare, parallel requests already exist)
* UI: 2 (later up to 4) chat panes sharing one input box with "send to all" toggle; each pane has its own model + params
  header. Compare mode = one prompt fans out to N panes; show tok/s and TTFT diff table and a "pick best / keep" action.
* State: `panes: [{chatId, model, overrides}]` in the store; each pane uses its own SSE stream; shared prompt text. Panes on
  the same model → parallel requests (continuous batching); different models → different runtimes (JIT-load with
  progress; warn about memory from `FitEstimator` before loading a second one).
* Persistence: layout in `localStorage`; each pane is an ordinary chat.

#### §4.3 Per-chat settings panel — **M**
* Chat JSON gains `settings { model, systemPrompt, generation:{…}, load? }` (load options shown but applied only on explicit
  "Reload model"). Resolution order: request override > chat > preset > model default > global (single resolver function
  `SettingsResolver.Effective(chat, preset, model)` shared by UI and server so the API can echo the effective config).
* UI: side panel with sections, per-field "differs from default" dot, buttons *Save as preset*, *Save as model default*,
  *Reset to preset*. (Relies on §5.1 for the preset dirty-indicator.)

#### §4.4 Rich rendering — **M**
* Replace the minimal renderer with `marked` (or `markdown-it`) + `DOMPurify` + `highlight.js` (core + ~20 languages, copy
  button per block, language label), `KaTeX` for `$…$`/`$$…$$`/`\(…\)`, GFM tables, task lists, links opening in the external
  browser (WebView2 `NewWindowRequested`). Libraries are **vendored into the embedded resources** (offline-first: no CDN).
* Streaming-safe: render incrementally with a throttle (50 ms) and handle unclosed fences/math without flicker.
* Reasoning: collapsible `<details>` block, auto-collapsed when answer starts, shows "Thought for 12 s".
* Tests: renderer unit tests in a headless JS test (vitest) for XSS payloads (`<img onerror>`, `javascript:` links).

#### §4.5 Token counter & per-message stats — **S**
* `POST /v1/tokenize` already exists; add `POST /api/chats/{id}/tokens` (or use client-side debounce 300 ms) returning
  `{prompt, context, remaining}` for the composed prompt incl. system prompt, attachments (RAG-in-context size) and tools.
  Context bar under the input: green < 70 %, amber < 90 %, red ≥ 90 %, tooltip with breakdown; warning shows the active
  overflow policy (§1.5).
* Per-message stats: persist `{tokens, tokensPerSecond, ttftSeconds, stopReason, loadSeconds, promptCachedTokens,
  draftAccepted}` on each assistant message (already produced by `GenerationStats`); small footer on hover/expanded.

#### §4.6 Keyboard shortcuts — **S**
Central `shortcuts.js` registry with a help overlay (`?`) and rebinding stored in settings: `Ctrl+N` new chat,
`Ctrl+Shift+N` new folder, `Ctrl+Shift+R` runtime manager, `Ctrl+K`/`Ctrl+P` quick open (fuzzy: chats, models, commands),
`Ctrl+Enter` send, `Esc` stop generation, `Ctrl+,` settings, `Ctrl+L` focus input, `Ctrl+Shift+C` copy last answer, `Alt+←/→`
switch branch variant. WebView2: set `AreBrowserAcceleratorKeysEnabled=false` for conflicting keys, forward `Ctrl+Shift+R` from
`MainWindow.cs`. Conflicts detector in the rebinding UI.

### §5 Presets

#### §5.1 File-based presets — **M**
* Move from one `presets.json` to `presets/<name>.json` (name sanitised; folder watched with `FileSystemWatcher` so edits on
  disk appear live). Schema `{ "schemaVersion":1, "name", "description", "systemPrompt", "inference": { …GenerationOptions… } }`.
  **Load params are excluded** (as in BlahLLMWrapper) — a validator drops/flags `load.*` keys. Migrate the old file (X1).
* Dirty tracking: UI store keeps `activePresetId` + a deep-compare of current vs preset (`modified` dot, *Save*,
  *Save as new*, *Reset to preset*). Chats remember the preset id and field overrides (§4.3).
* API: `GET/PUT/DELETE /api/presets/{name}` (exist) + `POST /api/presets/{name}/duplicate`; CLI `preset save|load|rm|export`.
* Tests: round-trip, invalid keys, name sanitisation (no path traversal).

#### §5.2 Import/export & sharing — **S** (needs §5.1)
* Export: download `name.preset.json`. Import: file picker/drag-drop, paste JSON, or URL (`POST /api/presets/import {url}`;
  HTTPS only, ≤ 256 KB, content-type JSON, schema-validated; **user confirmation dialog listing the fields** before saving, name
  collision → rename/overwrite prompt). Also accept GitHub `blob` URLs by rewriting to `raw`. CLI `preset import <file|url>`.
* Share-by-file: register `.llmpreset` extension → open with the app (single-instance message to running host).

### §6 Server / API

#### §6.1 JIT loading policy — **M**
* Settings `jit { enabled, defaultTtlMinutes, autoEvict, maxLoadedModels, evictOrder: lru|oldest }`; per-request `ttl`
  (seconds) in native API, `x-llmhost-ttl` header and `ttl` body field on OpenAI routes (BlahLLMWrapper-compatible).
* `ModelManager.LoadAsync`: track `LoadedBy = manual|jit`. When loading would exceed `maxLoadedModels` — or `FitEstimator`
  says the new model will not fit alongside — and `autoEvict`, unload JIT-loaded idle models (LRU) first; never evict manual
  loads or busy runtimes; if still not enough, return a 503 `model_load_blocked` with the reason. Serialise through the
  existing `_loadGate`.
* TTL timer already runs ([ModelManager.cs:117](Inference/ModelManager.cs:117)): apply `defaultTtlMinutes` when options have 0,
  refresh `LastUsed` on every request (also queued ones).
* UI settings group + loaded-models list shows origin/TTL countdown. Tests with a fake runtime (needs `IModelHost` from §2.5
  refactor, or a thin `ILoadedModel` test double).

#### §6.2 Legacy `/api/v0/*` — **S**
Thin adapters: `GET /api/v0/models` & `/{id}` (adds `state: loaded|not-loaded`, `type: llm|vlm|embeddings`, `arch`,
`quantization`, `max_context_length`, `loaded_context_length`, `publisher`, `capabilities[]`), `POST /api/v0/chat/completions`,
`/completions`, `/embeddings` → same handlers as `/v1/*`, with the v0 response extras (`stats {tokens_per_second,
time_to_first_token, generation_time, stop_reason}`, `model_info`, `runtime`). Put in `Server/LegacyApi.cs`; contract tests
compare field sets.

#### §6.3 HTTPS, allow-list, limits — **M**
* **HTTPS:** `settings.server.tls { mode: off|selfSigned|file, certPath, keyPath/pfxPassword, port }`. Self-signed: generate
  RSA-2048/ECDSA P-256 cert with SANs (localhost, machine name, LAN IPs) via `CertificateRequest`, store PFX in
  `AppPaths.Root\tls\` (DPAPI-protected password), export the public cert for client trust (`GET /api/tls/cert`), regenerate
  before expiry. Kestrel `ListenAnyIP(port, o => o.UseHttps(cert))`; HTTP→HTTPS redirect optional; WebView2 shell keeps using
  loopback HTTP/HTTPS with cert-error handling only for loopback self-signed.
* **IP allow-list:** middleware (before auth) checking `RemoteIpAddress` against CIDR list (IPv4/IPv6, `::1`), default
  `loopback only` unless LAN binding is on (then `private ranges`); 403 + log line.
* **Limits:** `MaxRequestBodySize` per route class (default 32 MB chat, 256 MB uploads — currently global 256 MB at
  [ApiServer.cs:42](Server/ApiServer.cs:42)), max concurrent connections, per-token + per-IP token-bucket rate limit
  (`System.Threading.RateLimiting`), `Retry-After` header, max `n`/`max_tokens` caps. Config validated through X1.
* Tests: allow-list matcher, rate limiter 429 behaviour, TLS cert generation (parse + SANs).

#### §6.4 Request/response log viewer & metrics — **M** (needs X2)
* **Request log:** middleware records `{id, time, method, path, status, durationMs, model, token, inputTokens, outputTokens,
  stream, error}` into a ring buffer (default 2 000) and, optionally, bodies (`settings.logging.logBodies`, redacted
  `Authorization`/API keys, truncated to 8 KB, off by default for privacy).
* `GET /api/requests?model=&status=&since=&q=` + SSE `/api/requests/stream`; UI "Requests" tab with filters, detail drawer
  (headers, bodies, timings, stats). CLI `log stream [--source server|load|request] [--json] [--filter]` (extend the existing
  verb). 
* **`/metrics`** (Prometheus text): `llmhost_requests_total{route,status}`, `llmhost_request_duration_seconds` histogram,
  `llmhost_tokens_generated_total{model}`, `llmhost_prompt_tokens_total`, `llmhost_tokens_per_second`,
  `llmhost_loaded_models`, `llmhost_active_requests{model}`, `llmhost_queue_depth{model}`, `llmhost_kv_cache_tokens{model}`,
  `llmhost_gpu_memory_bytes{gpu}` (from §2.3). Behind auth unless `metrics.public=true`. Hand-written exporter (no library
  needed, ~120 lines).

#### §6.5 Anthropic API gaps — **M**
Already present: `image` blocks (user messages) and `thinking` output blocks. Remaining:
1. **Images in tool results:** parse `tool_result.content` arrays with `image` blocks (share logic with §1.2-3).
2. **`tool_choice`:** `{type:auto|any|tool|none, name, disable_parallel_tool_use}` → existing native tool-calling
   (`ToolCalling.cs`) modes (`required`/named already supported for OpenAI). Test forced tool and `none`.
3. **`thinking` request param:** `thinking: {type: enabled, budget_tokens}` → `ReasoningEffort` mapping (budget thresholds
   → off/low/medium/high) plus hard thinking-token budget; `type: disabled` → `off`. Return `signature` as an HMAC of the text
   (clients echo it back) and accept `thinking`/`redacted_thinking` blocks in assistant history (drop them from the prompt or
   re-inject as reasoning depending on template).
4. **`cache_control`:** accept (and ignore) on system, message content and tool blocks without error; report
   `usage.cache_read_input_tokens` / `cache_creation_input_tokens` from our actual prefix-cache stats (maps naturally to
   `CachedTokens`).
5. Missing request fields tolerated: `metadata`, `service_tier`, `top_k`, `container`, `betas` header. Error format
   `{type:"error", error:{type,message}}`; `anthropic-version` header accepted.
6. Message Batches and Files API stay out of scope (document).
Contract tests with captured Claude Code CLI requests.

### §7 CLI

#### §7.1 `link` verbs — **S** (stub now, real once §8.1 exists)
Add `link enable|disable|status|set-device-name|set-preferred-device` that call `/api/link/*`. Until §8.1 lands the commands
return "LM Link-style remoting is not available in this build" with exit code 2 (documented), so scripts can detect it.
Do the plumbing and tests now; implement bodies in §8.1.

#### §7.2 `login` / `push` / `clone` / `dev` — **S** (decision item)
These only make sense with a hub or plugin system, both P3. Plan: reserve the verbs, implement **`login`** for Hugging Face
(`login --hf-token` stores the encrypted token, shares storage with §3.3) and **`clone <user/model>`** as alias of `get`;
`push`/`dev` are documented as not applicable (exit code 2 with explanation). Revisit when §5 P3 Hub-style publish is scheduled.

#### §7.3 Machine-readable output & exit codes — **M**
* Global flags `--json`, `--quiet/-q`, `--no-color`, `--verbose`; `--json` prints one JSON document (or JSON-lines for
  streaming verbs); all human output goes to stderr in that mode. Extend `ls`/`ps` (exist) to every verb that prints a list
  or status (`models`, `runtime`, `hardware`, `presets`, `chats`, `downloads`, `server status`, `daemon status`).
* Stable exit codes in `Cli/ExitCodes.cs`: 0 ok · 1 generic · 2 usage/unsupported · 3 not found · 4 unauthorised ·
  5 server unreachable · 6 out of memory/doesn't fit · 7 cancelled · 8 conflict/already running. Documented in README.
* Refactor `Shell.cs` (1541 lines): split output into an `IOutput` (Human/Json/Quiet) so verbs call
  `Out.Table(...)`/`Out.Result(obj)` once; do verb-by-verb. Golden-file tests for JSON schemas.

### §8 Remote / multi-device

#### §8.1 Private device network (LM-Link equivalent) — **XL**, optional (Phase F)
Decision first (S, 2 d): **don't build a VPN.** Options: (a) rely on the user's Tailscale/ZeroTier/WireGuard and provide
discovery + routing on top (recommended, M); (b) embed `tsnet`-like userspace networking (needs Go sidecar — rejected);
(c) own relay + Noise-protocol tunnel (XL, security-critical — not recommended).
Plan for (a):
* **Identity & pairing:** each install has a device keypair (Ed25519) and name (`settings.link.deviceName`). Pair by entering
  a 6-digit code / scanning a QR displayed by the other device; pairing exchanges public keys and stores `peers.json`.
  All peer calls are mutually authenticated (signed request headers with timestamp + nonce, or mTLS using §6.3 certs pinned
  per peer). Tokens from §P1 API-tokens get a `link` permission.
* **Discovery:** configured peer addresses (Tailscale MagicDNS names/IPs) + mDNS (`_llmhost._tcp`) on the LAN; periodic
  `GET /api/link/info` (version, models, loaded, capabilities, load/GPU headroom).
* **Remote models:** `/v1/models` and `/api/models/local` gain `device` field; the UI model picker groups by device;
  requests with `model: "<device>/<model>"` or a "preferred device" setting are proxied (streaming pass-through, cancellation
  propagation, auth rewrite) by `LinkRouter`. Load/unload/download of remote models via the same proxy with explicit user
  action. Failover: if the preferred device is offline, fall back to local with a visible notice (setting).
* **Security:** off by default; per-peer permission (chat / load / manage); audit entries in the request log (§6.4);
  rate limits (§6.3). Threat-model doc before shipping.
* CLI (`link …` from §7.1) and API (`/api/link/*`) complete; tests with two in-process hosts on loopback ports.

### §9 Desktop application & UX

#### §9.1 Interface modes — **M** (after §9.0)
`settings.uiMode = user | power | developer`. A `features.js` table maps every control to its minimum mode: *User* = chat,
models, discover, basic params; *Power User* = adds samplers (§1.4), per-model configs, MoE/LoRA/threads, speculative
decoding, presets editor, runtime manager; *Developer* = adds server settings, tokens, MCP, request logs (§6.4), logs, metrics,
API playground, raw template editor. Controls are hidden, not disabled; a "Show all settings" search ignores mode. Mode
switch in the footer and first-run (§9.3); CLI unaffected. Each new setting in this plan registers its mode.

#### §9.2 Themes & localisation — **M**
* **Themes:** extract colours into CSS variables (`--bg`, `--fg`, `--accent`, …); `light|dark|system` (listens to
  `prefers-color-scheme`; the WebView2 shell sets `PreferredColorScheme`, title bar via `DwmSetWindowAttribute`
  immersive-dark). Optional custom accent colour. Contrast check ≥ 4.5:1 in both themes.
* **i18n:** all UI strings moved to `locales/<lang>.json` with keys (`t('chat.send')`), ICU-style plurals via
  `Intl.PluralRules`, `Intl` formatting for numbers/dates/bytes. Ship `en` + one more (e.g. `de` or `es`) to prove the
  mechanism; language auto-detect from `navigator.language`; fallback to `en`. Tooltip help text (`p-*` keys) included.
  Server/CLI messages stay English (documented). A script checks for missing/unused keys in CI.

#### §9.3 First-run onboarding — **M**
Wizard shown when `settings.onboardingDone=false`: (1) welcome + mode choice (§9.1); (2) **hardware survey** from
`/api/hardware` and `RuntimeManager.Survey()` (CPU features, RAM, GPUs, Vulkan present, recommended runtime, offer to
download Vulkan runtime); (3) **models folder** picker with free-space check (§3.5); (4) **recommended models** chosen by
`FitEstimator` against detected memory (one small, one medium, one vision) with one-click download; (5) optional HF token
(§3.3); (6) done → open chat with the model loading. Re-runnable from Settings. Never blocks: "Skip" at every step.

#### §9.4 Auto-update & installer — **L**
* **Release channels:** `settings.update.channel = stable|beta`; `UpdateChecker.cs` already polls — extend to read a
  `releases.json`/GitHub Releases API, compare semver, show changelog, support signature check (SHA-256 + Authenticode of the
  package).
* **In-app update:** download installer/zip to temp, verify, then hand off to an **updater helper** (`LLMHost.Updater.exe`)
  that waits for the app to exit, replaces files (or runs the installer silently), restarts, rolls back on failure.
  Services/daemon mode: stop service → update → start.
* **Packaging:** self-contained single-file publish (`PublishSingleFile`, `SelfContained`, `win-x64`; keep native llama DLLs
  beside the exe since they are runtime-selected — `IncludeNativeLibrariesForSelfExtract=false`), plus **MSIX** (needs signing
  certificate; ship MSI via WiX v4 as the unsigned-friendly alternative). CI job produces artifacts per tag. Per-user install
  (no admin) to `%LOCALAPPDATA%\Programs\LLMHost`; keep data in `%USERPROFILE%\.llmhost`.
* Risks: code-signing cost/process, SmartScreen reputation, WebView2 runtime prerequisite (evergreen bootstrapper).

#### §9.5 Completion notifications — **S**
Windows toast via `Microsoft.Toolkit.Uwp.Notifications` / `AppNotificationManager` (unpackaged apps need an AUMID +
Start-menu shortcut — done by installer §9.4, fallback to tray balloon from `MainWindow.cs`). Events: download finished/failed,
model loaded (when load > 10 s), generation finished while window unfocused, update available. Settings per event + "only when
minimised". Clicking a toast focuses the relevant view. Web (non-shell) clients use the Notifications API with the same
setting.

### §10 Quality, safety & operations

#### ✅ §10.1 Settings versioning & validation — **M** → implemented as **X1** above (done as X1, `40690ec`)
UI: validation errors shown inline per field (red outline + message) and as a summary; invalid keys in a hand-edited
`settings.json` are preserved in `settings.json.invalid` and reported at startup instead of silently dropped.

#### ✅ §10.2 Structured logging & native log level — **M** → implemented as **X2** (done as X2, `16cb207`)
Settings: `logging.level` (trace…error), `logging.native` (off|error|warn|info|debug), `logging.retainDays`,
`logging.maxFileMb`. CLI `log level <x>`. "Open logs folder" button + diagnostic export (P3) reuses the same files.

#### §10.3 Security review & fixes — **L**
Work through a checklist, fix, and add regression tests:
1. **Path traversal:** chat ids, preset names, model ids in `DELETE /api/models/local`, import/copy targets, runtime ids,
   download destination (`publisher/repo/file` must be sanitised: reject `..`, absolute paths, drive letters, reserved names,
   alternate data streams), zip/archive extraction in `RuntimeManager.InstallAsync` (zip-slip), document uploads.
   Central `SafePath.Combine(root, parts…)` that throws if the result leaves `root`; use everywhere.
2. **Body limits:** per-route limits (§6.3), JSON depth limit, max image size / count per request, max tool-call args.
3. **Auth on the web UI itself:** today `/` and `/api/*` management routes — decide policy: loopback-only unauthenticated
   (WebView2 shell uses a per-launch random token passed via header/cookie), any non-loopback access requires a token.
   Management routes (`/api/*`) require a token with `manage` permission; inference routes with `inference`. CSRF: management
   mutations require a custom header (`x-llmhost-csrf`) or `Content-Type: application/json` plus `Origin` check against allowed
   origins (blocks drive-by requests from web pages to `localhost`); CORS default restricted to the UI origin, wildcard only
   by explicit setting. DNS-rebinding defence: `Host` header allow-list.
4. **Safe defaults for LAN binding:** enabling LAN requires auth enabled + at least one token (forced), shows a warning dialog,
   binds only to selected interfaces, allow-list default `private ranges` (§6.3). Refuse `0.0.0.0` with auth off.
5. **MCP sandbox warnings:** first-use confirmation per server showing command line / URL; stdio servers run with a scrubbed
   environment (explicit allow-list of env vars), working dir = a temp dir; display "can run code on your machine" warning;
   `ephemeral_mcp` over the API restricted to an allow-list setting and `allowed_tools` required; tool-call confirmation UI
   already exists — make "always allow" scoped per tool and expiring; log every call.
6. **Secrets:** HF token and API tokens at rest (DPAPI / hashed), never logged (redaction filter in X2), not returned by
   `GET /api/settings`.
7. **SSRF:** URL downloads/preset import/image URL fetching (`ImageInput.FromUrl`): block loopback/link-local/private targets
   unless explicitly allowed, size + time limits, redirect limit.
8. **Dependency scan** (`dotnet list package --vulnerable`) in CI.
Deliverable: `SECURITY.md` + test project `SecurityTests`.

---

## 5. Cross-cutting requirements for every item

* **Offline-first:** no new network call without a setting and a mention in the offline audit (P3 §2.7); vendored JS/CSS only.
* **Back-compat:** old settings/chats keep loading (X1 migrations); new request fields are optional; API additions follow
  OpenAI/Anthropic/llama-server names where a de-facto standard exists.
* **Docs:** each item updates the in-UI help (`p-*`/`l-*` tooltips), README and the CLI `help` text.
* **Tests:** per X5; an item is not done without at least one automated test and a manual check on the RX 7900 XTX Vulkan
  path **and** CPU path.
* **Definition of done:** feature works in UI + API + CLI where applicable, mode (§9.1) assigned, `todo.md` ticked.

## 6. Risk register

| Risk | Impact | Mitigation |
|---|---|---|
| LLamaSharp 0.27 lacks native entry points (tensor overrides, newer samplers, LoRA hot-swap, audio, `state_seq`) | §1.1, 1.2, 1.4, 1.6, 1.7 slip | 1-day spike per item first; fall back to direct P/Invoke on `llama.dll` (already loaded by `NativeBackend`); budget a LLamaSharp bump (re-run integration tests) |
| Custom sampler chain regresses grammar/logprobs behaviour | Wrong output | Keep old `DefaultSamplingPipeline` path behind `legacySampler` flag one release; golden tests with fixed seeds |
| Unified-KV pool exhaustion with extra cache slots / instances | Stalls, OOM | Single budget accountant in `ModelRuntime`; evict idle cache before blocking; load-time warning |
| Worker-process refactor (§2.5) touches every call site | Long-lived branch | Interface extraction PR first, no behaviour change; then add workers |
| UI rewrite (§9.0) blocks Phase D | Schedule | Incremental per-view migration; keep old views working until replaced |
| Updater/installer signing and SmartScreen | Distribution friction | Start with unsigned zip + in-app updater; add MSIX/MSI when a cert exists |
| Security hardening (§10.3) breaks existing clients (CORS/CSRF/auth defaults) | Support load | Settings migration keeps current behaviour for existing installs with a one-time banner; new installs get safe defaults |
| LM-Link scope creep | Never finishes | Phase F, separate decision gate; use external VPN overlay |

## 7. Effort summary

| Section | Items | Effort |
|---|---|---|
| ✅ X1–X4 foundations | 4 | M+M+S+S ≈ 2.5 wk |
| §1 Inference | 1.1 L · 1.2 L · 1.2b L · 1.3 M · 1.4 M · 1.5 M · 1.6 M · 1.7 M · 1.8 M · 1.11 S · 1.12 M | ≈ 10 wk |
| §2 Hardware | 2.3 M · 2.4 S · 2.5 XL · 2.6 M | ≈ 5 wk |
| §3 Models | 3.1 M · 3.2 M · 3.3 S · 3.4 M · 3.5 M | ≈ 3.5 wk |
| §4 Chat (+§9.0 UI base) | 9.0 L · 4.1 L · 4.2 L · 4.3 M · 4.4 M · 4.5 S · 4.6 S | ≈ 6.5 wk |
| §5 Presets | 5.1 M · 5.2 S | ≈ 1.5 wk |
| §6 Server/API | 6.1 M · 6.2 S · 6.3 M · 6.4 M · 6.5 M | ≈ 3.5 wk |
| §7 CLI | 7.1 S · 7.2 S · 7.3 M | ≈ 1.5 wk |
| §8 Remote | 8.1 XL (optional) | ≈ 3–4 wk |
| §9 Desktop | 9.1 M · 9.2 M · 9.3 M · 9.4 L · 9.5 S | ≈ 4.5 wk |
| §10 Security/ops | 10.3 L (10.1/10.2 counted in X) | ≈ 1.5 wk |
| **Total** | | **≈ 40–45 engineer-weeks** (one engineer; ~25 wk with two, since §1/§2 and §4/§9 are independent tracks) |

## 8. Suggested first 4 weeks

1. **Week 1:** X1 (versioning/validation), X3 (capabilities), X4 (checklist doc); spikes: LLamaSharp native-API coverage
   (samplers, tensor overrides, LoRA, state_seq, audio) → write findings into this file's risk table.
2. **Week 2:** §1.4 samplers, §1.5 overflow policies, §1.11 close-out, §2.4 GPU ratio.
3. **Week 3:** X2 logging, §6.1 JIT policy, §6.2 `/api/v0`, §10.3 items 1–2 (path safety + body limits — cheap and high value).
4. **Week 4:** §5.1/§5.2 presets, §3.3 gated models, §6.5 Anthropic gaps; start `IModelHost` interface extraction (§2.5 step 1).
