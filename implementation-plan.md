# Implementation plan — P2 items

Companion to [todo.md](todo.md). Covers **every open P2 item**, referenced as `§<section>.<n>` (n = position of the item inside
that section of `todo.md`). P1 is finished; P3 is out of scope here. This plan replaces the earlier plan, whose "host
`llama-server` as child processes" strategy no longer matches the code: the app embeds llama.cpp in-process through
LLamaSharp 0.27 and already has continuous batching, per-slot KV reuse, vision (mtmd), reasoning, native tool calling,
runtime manager and multi-GPU. The plan builds on that.

Effort (one engineer, includes tests): **S** ≤ 2 d · **M** 3–5 d · **L** 6–10 d · **XL** > 10 d.

**Status convention:** a finished item gets ✅ at the very start of its heading or table row (never at the end), with its commit
hash in the status note or heading; tick its `- [x]` checkbox in [todo.md](todo.md), and update the progress line below.

**Progress:** section 2 prerequisites X1–X5 done. Section 4 items done: §1.4 samplers, §1.1 prompt-cache follow-ups, §1.2 vision follow-ups, §1.2b speculative decoding, §1.3 prompt-template override, §1.5 context-overflow policies, §1.6 MoE controls, §1.7 LoRA adapters, §1.8 multiple instances, §1.11 logprobs and n, §1.12 embeddings and reranking, §2.4 GPU offload ratio, §2.3 live hardware monitor, §2.6 CPU thread pool and affinity, §3.1 capability badges and model card, §3.2 Discover filters and staff picks, §3.3 gated-model flow, §3.4 model update check, §3.5 models-directory management, §9.0 UI split into files, §4.1 chat management, §4.5 token counter and message stats, §4.6 keyboard shortcuts, §4.3 per-chat settings, §4.4 rich rendering, §4.2 split view and compare, untested, §5.1 file-based presets, §5.2 preset import and export (28 of ~50).

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

#### ✅ §1.2 Vision follow-ups — **L** (`35b1cf0`, `f1467c8`, `68d3505`, `b49c817`, audio: `58d62b4`)
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

#### ✅ §1.2b Speculative decoding (todo §1.5) — **L**
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

**As built:** `Inference/DraftModel.cs` (second model + context, greedy proposals, follows the main model's accepted text) and the scheduler changes in `ModelRuntime` (`TryDraft`, `VerifyDrafts`): proposals ride in the same batch behind the real token, are sampled in order with the request's own sampler (so grammar, penalties and seeds behave exactly as without speculation) and the rejected tail is removed from the KV cache. Used only while one request is generating, not for image prompts and not with a reasoning-token budget. Options `LoadOptions.DraftModel` / `DraftTokens` (default 6) / `DraftMinP` (default 0.5); CLI `load --draft <model> --draft-tokens n`; load dialog picker fed by `GET /api/models/drafts?model=` (`Models/DraftPairing.cs`: same architecture, vocabulary within 128 tokens, ≤ 1/6 of the size, plus suggested downloads per family); the memory estimate counts the draft. Stats: `GenerationStats.DraftTokens/DraftAccepted`, `/api/status` (`draftModel`, `draftProposed`, `draftAccepted`), native API `stats.draft_tokens_*`.
Deviations: an unusable draft (vocabulary mismatch, missing file) produces a load **warning** and the model loads without it, instead of refusing the load; pairings live in `DraftPairing` rather than `Catalog.cs`; no Discover "draft" badge. A model that cannot drop a cache tail (recurrent / sliding-window) switches speculation off with a message. **MTP:** only detected (`GgufInfo.MtpLayers` from `nextn_predict_layers`); the pinned llama.cpp build in LLamaSharp 0.27 has no use for it, so nothing is gated or enabled.
Verified (SmolVLM-256M as its own draft): identical text at temperature 0 and with a fixed seed, every proposal accepted when the draft equals the model, a corrupted draft (test hook) is rolled back without changing the output and leaves a consistent cache, limits and finish reasons unchanged, incompatible draft ignored with a warning. **Speed-up not measured:** the ≥ 1.3× target needs a real small draft (e.g. Qwen3-0.6B for Qwen3-14B), which is not downloaded here.

#### ✅ §1.3 Prompt-template override — **M** (`ee39e47`)
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

**As built:** `LoadOptions.PromptTemplate` (`PromptTemplateSettings`: `mode` auto|jinja|manual, `jinjaText`, `manualFormat` with system/user/assistant/tool prefix+suffix, `bos`, `generationPrefix`, `stopStrings`) in `Inference/PromptTemplates.cs`; the Jinja engine is **written in-house** (`Inference/Jinja.cs`, ~800 lines: if/for/set/macro/raw, namespace, slices, filters, tests, string/list/dict methods, `tojson`, `strftime_now`, `raise_exception`, Hugging Face's trim_blocks/lstrip_blocks) because Fluid/Scriban are not Jinja and the one Jinja library on NuGet (Jinja2.NET) lacks `namespace`, slices, method calls and `is defined`. `ModelRuntime.ApplyChatTemplate` uses the override first and falls back to the model's own template (with a log line) when the override fails; a sample conversation is rendered at load and problems become load warnings; reasoning/tool detection read the effective template; manual stop strings are added to every request; the model's own BOS text is dropped from the prompt when the tokenizer adds it. API: `GET /api/models/template?model=` and `POST /api/models/template/preview`; UI: "Prompt template" section in the load dialog (mode, Jinja editor with "start from the model's template", manual fields, preview) and the setting `alwaysShowTemplate`; CLI `template [model]` and `load --template-file`. `GgufInfo` now records the BOS/EOS ids and where the token list starts (`ReadTokenText`).
Deviations: no separate `eos` field in the manual format (put it in the assistant suffix); the preview of an unloaded model with the *auto* template uses the built-in Jinja engine instead of llama.cpp (the response says so in `engine`).
**Verification:** tests ran once Smart App Control stopped blocking them. They found two engine bugs, both fixed: integer literals and integer arithmetic came out as floats (C# conditional-expression typing), and `abs`. The parity tests (built-in Jinja engine vs llama.cpp on the real templates) pass for Qwen3-14B; for SmolVLM the engine follows the model's Jinja template, whereas llama.cpp's built-in shim folds a system message into the first user turn, so that comparison skips conversations with a system message. Templates that need typed content (list of `{type, text}` parts, e.g. SmolVLM) are detected by a probe render like llama.cpp does. The model-loading integration test classes now share one xUnit collection so they cannot interfere through the global settings (the auto-save test had been flaky when run in parallel).

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

#### ✅ §1.5 Context-overflow policies — **M**
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

**As built:** `GenerationOptions.ContextOverflow` (dropOldest | truncateMiddle | rollingWindow | stopAtLimit | error; spellings in camelCase, snake_case and kebab-case) and `KeepFirstTokens`; request fields `context_overflow` / `context_overflow_policy` / `contextOverflowPolicy` (LM Studio's name) and `keep_first_tokens`; web UI selector "When the context is full"; `Inference/ContextOverflow.cs`. `BuildFittingPrompt` takes the policy: truncateMiddle keeps the system prompt, the first question and the newest turns and adds "[… N earlier messages omitted …]" to the first question; stopAtLimit never touches the conversation; error throws `ContextLengthException` → HTTP 400 with error type `context_length_exceeded`. rollingWindow lets an answer run past the window (up to 8× the window when no token limit is given): `MakeRoom` first recycles idle caches of other conversations and then discards the older half of the request's own cache after the first `KeepFirstTokens` (default 32) tokens (`MemorySequenceRemove` + `MemorySequenceAdd`); when the model cannot shift its cache (`MemoryCanShift`) the policy behaves like stopAtLimit and logs why. The native API reports `stats.context_shifts`.
Deviations: no `KeepLastTokens` (the newest message is always kept); precedence request > chat > model default > global works through the existing settings layers but chats do not store the policy yet; dropped-message counts are not returned by the API (only logged by the CLI/chat endpoints that already had them).
Verified on SmolVLM-256M with a 512-token window: error / dropOldest / truncateMiddle / stopAtLimit behave as described, and a 700-token answer succeeds with the rolling window (cache shifted) but stops short without it.

#### ✅ §1.6 MoE controls — **M**
* `LoadOptions`: `MoeExpertCount` (override `*.expert_used_count` metadata key via `llama_model_kv_override`),
  `MoeCpuLayers` (`--n-cpu-moe` equivalent) and `TensorOverrides: [{pattern, buffer}]` (e.g. `\.ffn_.*_exps\.=CPU`).
  LLamaSharp: `LLamaModelParams.tensor_buft_overrides` — verify it is exposed in 0.27; otherwise pin to marshal the struct
  ourselves (small P/Invoke addition in `NativeBackend.cs`).
* "Force expert weights onto CPU" = convenience that generates the standard pattern; combined with `Device=gpu` it keeps
  attention+KV on GPU. `FitEstimator` must treat overridden tensors as host memory (estimate MoE expert bytes from GGUF tensor
  list names `ffn_*_exps`) — extend `GgufInfo` with per-tensor sizes (the reader already walks tensor infos).
* UI: shown only for MoE models (capability from X3); tooltip with expected VRAM saving; Power-User mode (§9.1).
* Tests: estimator math on a synthetic tensor list; load with override on a MoE fixture if available (else unit-only).

**As built:** `LoadOptions.MoeExpertCount` (metadata override `<arch>.expert_used_count`), `MoeCpuLayers` (-1 = all layers, N = first N layers) and the general `TensorOverrides` (regex → buffer) in `Inference/TensorOverrides.cs`; LLamaSharp 0.27 exposes both overrides (`ModelParams.MetadataOverrides`, `TensorBufferOverrides`), so no P/Invoke was needed. The expert pattern is `blk\.(0|1|…)\.ffn_(up|down|gate|gate_up)(_ch)?_exps\.` (shared experts stay on the GPU). The memory estimate (`FitEstimator`) moves the bytes of host-placed tensors from the GPU side to system memory per offloaded layer; MoE options on a dense model are ignored with a warning, an expert count above the model's is ignored. UI: two fields in the load dialog, shown only for MoE models (`capabilities.moe`); CLI `--cpu-moe`, `--n-cpu-moe N`, `--moe-experts N`, `--override-tensor "regex=CPU;…"`.
Deviation: no per-tensor sizes were added to `GgufInfo` — the estimator already reads the tensor table.
Verified on Qwen3-30B-A3B-Q4_K_M (17 GB): the expert-count override shows up in llama.cpp's log (`validate_override … expert_used_count`), and with the experts forced into system memory the model loads on the Vulkan GPU and answers (llama.cpp logs "buffer type overridden"); the estimate arithmetic is unit-tested.

#### ✅ §1.7 LoRA adapters — **M**
* `LoadOptions.Loras: [{path, scale}]`. After model load, `llama_adapter_lora_init` + `llama_set_adapters_lora` on the context
  (LLamaSharp `LoraAdapter`/`context.LoadLoraAdapter`). Validate base-model match (GGUF `general.architecture`, adapter's
  `adapter.lora.alpha`), refuse mismatches.
* Library: scan `*.gguf` with `general.type = adapter` into a separate **Adapters** list (`ModelLibrary` currently lists them as
  models — filter them out and show under the base model). Download/import reuse existing flows.
* Hot swap: `POST /api/models/lora {identifier, adapters:[{id,scale}]}` — changing scale does not need reload (re-apply to
  context; clear caches because KV is invalid).
* FitEstimator: add adapter file sizes. Per-request LoRA is out of scope (document).

**As built:** `LoadOptions.Loras` (`LoraSetting`: library id, file name or path + `scale`). LLamaSharp 0.27 has `LoadLoraFromFile` and `SetLoraAdapters`, used in `ModelRuntime.ApplyLoras`. Adapters are recognised by their GGUF metadata (`general.type = adapter` / `adapter.type = lora`, `adapter.lora.alpha`) and are now kept out of the model list: `ModelLibrary.List()` no longer shows them, `ListAdapters()` / `FindAdapter()` do. Before the weights are loaded each adapter is resolved and checked — missing, not an adapter, or a different architecture all fail the load with a message (`ResolveLoras`). `POST /api/models/lora {id, adapters:[{id, scale}]}` swaps adapters on a loaded model without reloading (only while idle; all cached prompts are dropped); `GET /api/models/lora?id=`, `GET /api/models/adapters?model=` (compatible adapters). Prompt caches saved to disk are keyed by the model **and** its adapters (`CacheModel`). The memory estimate adds the adapter file sizes. UI: adapter checklist with a strength per adapter in the load dialog (only when compatible adapters exist); CLI `load --lora "a.gguf=0.8;b.gguf"`.
Deviations: adapters are not shown under their base model in My Models (only in the load dialog); no per-request LoRA (as planned); download/import reuse the normal flows (an imported adapter is picked up automatically).
Verified with synthetic rank-4 adapters written by the tests (`LoraBuilder`) on SmolVLM-256M: strength 0 gives the plain output, strength 4 changes it, swapping and removing adapters at runtime works and restores the original output, a swapped model does not reuse the old prompt cache, a wrong-architecture adapter is refused. Not tested with a real trained adapter.

#### ✅ §1.8 Multiple instances of the same model — **M**
* `ModelManager.LoadAsync` already supports `options.Identifier`, but `Get(id)` returning an existing instance and the
  `/v1/models` listing assume 1 per model. Work: (a) never dedupe by file, only by identifier; (b) auto-suffix identifiers
  (`name`, `name:2`) when requested with `allowDuplicate`; (c) share mmapped weights between instances — keep a
  ref-counted `LLamaWeights` cache keyed by `{path, gpuConfig}` so a second instance only pays for context + KV;
  (d) request routing: `model` field may be the base name — pick the least-loaded instance (`ActiveRequests + QueuedRequests`);
  (e) unload by identifier only unloads weights when the refcount reaches 0.
* Fit check must count only the incremental cost (context + compute buffers) for a shared-weights instance.
* UI: "Load another instance" in the loaded-models list with distinct context size / params; CLI `load --identifier`.
* Tests: two instances with different context sizes answer in parallel; unloading one keeps the other.

**As built:** loading an already loaded model with another identifier was already possible; what was missing is now there. `ModelManager.LoadAsync(…, allowDuplicate: true)` names a new instance `name:2`, `name:3` … (an explicit identifier that is taken is refused). Instances **share their weights**: `ModelRuntime.WeightsKey` (file, GPU layers, main GPU, split, mmap/mlock, MoE and tensor overrides) selects a ref-counted `LLamaWeights` in a static pool; a second instance only creates its own context, cache and projector, and the last instance to go frees the weights (`WeightSharers`, `/api/status.weightSharers`). The fit check for such a load counts no weights (`FitEstimator … weightsAlreadyLoaded`, `SharedWeightsBytes`). Routing: `ModelManager.Instances` / `ResolveAsync` — `name:2` addresses that instance only, the pool name `name` (or library id / file name) goes to the least busy instance (`LeastLoaded`: running + waiting requests, ties to the longest idle). A load that fails after the weights were acquired now releases them. API `POST /api/models/load {allowDuplicate:true}` and `/api/v1/models/load {allow_duplicate:true}`; CLI `load --another`; UI "+ instance" button in the loaded list (opens the load dialog with its own context size and settings).
Deviations: instances with different weight placement (other GPU layers, mmap …) do not share and load separately (as expected); `/v1/models` lists every instance under its identifier rather than grouping them.
Verified on SmolVLM-256M: two instances with different context sizes (1024 / 2048) share one set of weights, answer in parallel with identical greedy output; unloading one keeps the other working and the weights are freed with the last one; routing by pool name picks the idle instance while the other is busy; `name:2` is honoured explicitly.

#### ✅ §1.11 `logprobs` / `n` — **S** (close-out)
Verify existing OpenAI chat/completions implementation against the spec (bytes arrays, `top_logprobs` ≤ 20, streaming chunk
shape, `n` with stream and `index`); add Responses API `top_logprobs` + `include: ["message.output_text.logprobs"]` and
`/api/v1/chat` pass-through if missing; add contract tests; then tick the item.

**As built:** the existing OpenAI chat / completions support was checked against the spec with contract tests on a real model, which found three gaps, now fixed: (1) `bytes` were derived from the decoded string, so a character split over two tokens produced U+FFFD bytes — tokens now carry their raw UTF-8 bytes (`TokenLogprob.Bytes`, `TopLogprob`, taken with `TokenToSpan`); (2) the answer text has its leading whitespace removed, so the token bytes did not add up to the text — the same trim is applied to the log-probabilities (`LeadingTrim`, one instance per stream); (3) the `include_usage` stream put the usage on the last finish chunk — it is now the separate final chunk with `choices: []` that the spec describes, and the chat object has `refusal: null`. Reasoning tokens are no longer part of the answer's `logprobs` (`TokenLogprob.Reasoning`). `top_logprobs` is clamped to 20.
Added: **Responses API** — `include: ["message.output_text.logprobs"]` (+ `top_logprobs`) puts `logprobs` on the `output_text` part, on every `response.output_text.delta` event and on `response.output_text.done`; **native `/api/v1/chat`** — `logprobs: true` / `top_logprobs` return `logprobs` on the message item and on `message.delta` events. All shapes live in `Server/LogprobsFormat.cs`.
Verified on SmolVLM-256M over HTTP: for chat (plain and streamed), legacy completions, Responses (plain and streamed) and native chat, the bytes of the returned log-probabilities spell exactly the returned text; `n = 3` returns indices 0..2 and summed usage; a streamed `n = 2` request has per-index role / content / finish chunks and one usage chunk; `top_logprobs` stays ≤ 20.
Deviations: `top_logprobs` above 20 is clamped instead of rejected with HTTP 400; the alternatives (`top_logprobs`) of the very first token keep their leading space.

#### ✅ §1.12 Embeddings options & reranking — **M**
* Embeddings: `LoadOptions.Pooling (auto|none|mean|cls|last|rank)`, `Normalize` (default true → L2), `EmbedBatchSize`; request
  fields `dimensions` (truncate + renormalise, Matryoshka), `encoding_format: float|base64`, `truncate`. Batch many inputs into
  one decode via multiple sequences (current `EmbedAsync` is one text at a time). Detect embedding GGUFs (X3) and prevent chat
  use with a clear error.
* Reranking: `POST /v1/rerank` and `/rerank` (Jina/Cohere shape: `query`, `documents`, `top_n`, `return_documents`) using
  pooling `rank` models (bge-reranker GGUF). Score = classifier-head output; order by score. Add the catalog entries
  (bge-reranker-v2-m3, Qwen3-Reranker).
* RAG (`Documents.cs`) can optionally rerank retrieved chunks (`settings.rag.rerankModel`).
* Tests: vectors identical (±1e-4) between batch and single; normalisation unit-length; rerank ordering on a toy set.

**As built:** `LoadOptions.Pooling` (auto | none | mean | cls | last | rank), `Normalize` (default on), `EmbedBatchSize`. Embedding no longer goes through `LLamaEmbedder` one text at a time: `ModelRuntime.EmbedManyAsync` tokenizes every input and packs up to 32 of them into one evaluation step (one sequence each, shared cache, `RunPooled`), reads the pooled vector per sequence and clears the cache. A model loaded for embeddings uses its own context (batch = ubatch = context length up to 8192, so long inputs work; they used to be capped by the 512-token batch); a chat model asked for vectors gets a separate mean-pooling context. `auto` leaves pooling to the model file, uses mean for ordinary language models and rank for rerankers. `/v1/embeddings` supports `dimensions` (cut + renormalise), `encoding_format: base64`, `truncate` (false = HTTP 400 when an input is too long) and `normalize`; token-array input is refused with a clear message. **Reranking:** `POST /v1/rerank`, `/rerank`, `/v1/reranking` (Jina / Cohere shape: `query`, `documents` as strings or `{text}`, `top_n`, `return_documents`; results sorted with `relevance_score`, plus `method`). A model with rank pooling is a cross-encoder: the pair is `[CLS] query [SEP] document [SEP]` from the vocabulary's special tokens, or the model's own `tokenizer.chat_template.rerank` with `{query}` / `{document}` when the file has one; the score is the raw head output. Any other embedding model falls back to cosine similarity (`method: "embedding"`). Embedding and reranking models now refuse chat requests with a message that names the right endpoint (`ModelManager.ResolveAsync`). `settings.rag.rerankModel` (+ `rerankCandidates`, default 20) lets chat-with-documents re-order its best chunks with a reranker (`method` becomes e.g. `embeddings+rerank`). Catalog: bge-small, bge-m3, bge-reranker-v2-m3, Qwen3-Reranker-0.6B. CLI: `load --pooling --no-normalize --embed-batch`, `rerank <query> <doc>…`; UI: pooling / batch / normalise fields in the load dialog for embedding models.
Deviations: a batch and one-at-a-time vectors agree to cosine > 0.999, not to ±1e-4 — quantised kernels round differently for different batch shapes (the test asserts the realistic bound); the cross-encoder score is not passed through a sigmoid; only the first output of a classifier head is used.
Verified with real models: bge-small-en-v1.5 (BERT, CLS pooling from the file: 384-d vectors, semantic ordering, a 1000-token input accepted at 512), bge-reranker-v2-m3 (the Paris sentence is ranked first among four, scores stable when the document order is reversed, `/v1/rerank` and `/rerank` return the Jina shape), and SmolVLM as a chat model producing embeddings. **Not tried:** the model-supplied rerank template (no such model here), Qwen3-Reranker (catalog entry only), and the RAG rerank stage end to end.
**Found while doing this:** the load dialog code that §1.7 added to `Web/index.html` had been mangled by a faulty script edit (stray text before `<!doctype html>`, adapters never sent or restored). Repaired here, and `UiPageTests` now guards the page start and the options `collectLoad` sends.

### §2 Hardware & runtimes

#### ✅ §2.4 GPU offload as ratio — **S**
`LoadOptions.GpuRatio (0–1)` and `Device: "max"|"auto"|<ratio>`; `PlanGpuLayers`
([ModelRuntime.cs:89](Inference/ModelRuntime.cs:89)) converts: `max` = all layers, `auto` = VRAM-fit result from
`FitEstimator`, ratio = `round(ratio × nLayers)`. Accept `--gpu max|auto|0.0–1.0` in CLI `load`, `"gpu"` in
`/api/v1/models/load`; UI slider with layer-count readout; keep `GpuLayers` as the explicit override (wins when both set).
Tests for the conversion.

**As built:** `LoadOptions.GpuRatio` (0–1, null = off) next to `GpuLayers`; precedence is exact `GpuLayers` > `GpuRatio` > `Device`. The layer count is worked out from the model itself at load time: `LoadOptions.LayersForRatio` = round(ratio × (blocks + 1)), the output layer counting as one more, 999 (all) at 1 or when rounding reaches the output layer, 0 at 0 (`RequestedGpuLayers` is used by `PlanGpuLayers` and by the memory estimate, so the fit check and the real load agree). `Device` accepts `max` (= `gpu`) and a number written as text (`"0.5"`). One parser for the common notation, `LoadOptions.SetGpu`: `off|cpu`, `max|all`, `auto`, or a ratio — used by CLI `load/fit --gpu max|auto|off|0.5` (it used to compute a fixed layer count in the shell), native `POST /api/v1/models/load {gpu: "max"|"off"|"auto"|0.5}` (string or JSON number) and the estimate endpoint (`gpuRatio=`). Nonsense values (`2`, `most`) are refused with a message. UI: a "share of the layers" checkbox + slider in the load dialog with a readout such as `50% ≈ 17 of 33 layers` (same rounding as the server, using the block count from the live memory estimate), saved with the model defaults. While doing this: native `num_experts` now sets `moeExpertCount` (it was a placeholder).
Verified: conversion table (rounding, 0, 1, rounding up to "all", unknown layer count), precedence, notation parsing, validation, planner and memory-estimate results for a 32-block model, and a real GPU load of SmolVLM-256M with `GpuRatio = 0.5` that offloads exactly the computed number of layers. `UiPageTests` also gained a guard that every help tip in the page is valid JavaScript (an apostrophe in a tip had just broken the whole script).
Deviation: the ratio is a share of layers, not of memory — layers differ in size, so 50 % of the layers is not exactly 50 % of the weights.

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

#### ✅ §2.3 Live hardware monitor — **M**
* Sampler thread (1 Hz while any UI/SSE client listens, 0 otherwise): CPU % (PDH `\Processor Information(_Total)\% Processor
  Utility`), RAM (existing `MemoryStatus`), process working set, per-GPU dedicated/shared memory used and engine utilisation via
  PDH counters `\GPU Engine(*)\Utilization Percentage` and `\GPU Adapter Memory(*)\Dedicated Usage` (works for AMD/NVIDIA/Intel;
  map `luid_…` → adapter using DXGI LUID already read in `Hardware.cs`). Temperature: not available via PDH — try vendor
  libs optionally (NVAPI/ADL are out of scope; show "n/a"); optionally LibreHardwareMonitorLib behind a settings toggle.
* `GET /api/hardware/live` (snapshot) and `GET /api/hardware/stream` (SSE). UI: small sparkline panel in the header and a
  full panel in Settings → Hardware; per-loaded-model VRAM estimate vs. actual.
* Tests: counter-name parser unit tests; endpoint returns schema even when counters are missing.

**As built:** `Core/HardwareMonitor.cs`. CPU load from the Windows counter `Processor Information\% Processor Utility` (falls back to `Processor\% Processor Time`); memory from `MemoryStatus`; this process's working set / private bytes; per GPU the engine load, adapter memory and this process's GPU memory. The GPU numbers come from the WMI views of the same counters (`Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine / GPUAdapterMemory / GPUProcessMemory`) rather than raw PDH wildcards: simpler and still vendor-neutral (AMD / NVIDIA / Intel). Counter instances such as `pid_1234_luid_0x00000000_0x0000F5A3_phys_0_eng_0_engtype_3D` are parsed (`GpuCounterName`) and matched to a GPU through the adapter LUID that DXGI reports (`GpuInfo.Luid`, new); processes are summed per engine and the busiest engine is the adapter's load, like the task manager. `HardwareMonitor.Shared` samples once a second **only while something is subscribed**; a plain request takes a fresh reading when the last one is older than 0.9 s. API: `GET /api/hardware/live` (always the same fields; unavailable counters are explicit `null`s, `notes` explains why) and `GET /api/hardware/stream` (SSE, one reading per second, sampling stops when the client leaves). The reading also lists each loaded model's estimated GPU / system-memory need, next to this process's real GPU memory. UI: a live strip with a CPU / GPU sparkline in the top bar (polls every 2 s while the page is visible) and a "Live load" card in Settings with bars per resource, the VRAM of this app and the estimates of loaded models.
Deviations: **temperature is always `null` / "n/a"** — Windows has no vendor-neutral counter and no vendor library was added (LibreHardwareMonitor was optional in the plan and is not included); the header strip polls instead of using the SSE stream (an `EventSource` cannot send the API key header).
Verified on this machine (two RX 7900 XTX): CPU / RAM / process figures, GPU 0 load and VRAM, the LUID mapping for every GPU, and that a model loaded on the GPU shows up as more than 100 MB of this process's dedicated GPU memory. The second GPU reports no counters while idle (`null`), as Windows lists none for it. **Correction:** the GPU tests written for §2.4 returned early because the native backend was not yet initialised in the test process, so they had never really run; they initialise it now and pass (planner gives 17 layers for a ratio of 0.5 on 32 blocks, a real SmolVLM GPU load offloads the computed count).

#### ✅ §2.6 CPU thread pool, affinity, NUMA — **M**
* `LoadOptions` additions: `ThreadsBatch` (exists as `BatchThreads`; expose consistently as `threads_batch`), `CpuAffinityMask`
  / `CpuList`, `ThreadPriority (normal|high|low)`, `NumaMode (off|distribute|isolate|numactl)`, `Poll (0–100)`.
* llama.cpp's own thread pool is created per context: LLamaSharp exposes only `n_threads`. Implement affinity/priority by
  enumerating process threads after load and when the scheduler thread starts (Win32 `SetThreadAffinityMask`/
  `SetThreadPriority`; for processor groups > 64 logical CPUs use `SetThreadGroupAffinity`). NUMA: call
  `llama_numa_init` once at backend init (setting `numa`), before first model load — takes effect after restart, show a banner.
* Defaults: physical cores for generation, all logical cores for prompt processing (detect via
  `GetLogicalProcessorInformationEx`), replacing the current 75 % heuristic ([Hardware.cs:205](Core/Hardware.cs:205)).
* UI in Power-User mode; "Test thread count" benchmark button (runs a 128-token generation at 2–3 settings).

**As built:** `Core/CpuTopology.cs` reads the Windows CPU sets (`GetSystemCpuSetInformation`): logical processors, physical cores, processor groups and NUMA nodes, numbered 0..N-1 across groups. `CpuAffinity.Parse` understands `0-7,16`, `node:N`, `physical` (one thread per core) and `all`. Defaults changed: generation threads = physical cores, prompt-processing threads = every logical processor, both limited by the affinity (replaces the 75 % rule). `LoadOptions` gained `CpuAffinity` and `ThreadPriority` (`normal|high|low`); `BatchThreads` already existed. `Inference/NativeThreadPolicy.cs` finds the engine's compute threads by their start address (inside vcomp / ggml / llama / mtmd) and pins them with `SetThreadSelectedCpuSets`, which works across processor groups (the dev machine has 192 logical processors, so `SetThreadAffinityMask` would not have), and sets their priority; a 1 Hz scan catches threads created later, the policy is process-wide (last load wins) and an unload restores default scheduling. Surfaces: load dialog fields (threads, batch threads, affinity, priority), CLI `--batch-threads`, `--cpu-affinity`, `--thread-priority`, validator checks, `cpuTopology` in `GET /api/hardware`, and a **Test threads** button (`POST /api/models/benchmark-threads`, 3 default counts, changes the live context's thread count and restores it).
Deviations: no `Poll` option and no `llama_numa_init` (LLamaSharp does not expose it); NUMA is handled by pinning to a node (`node:N`) instead, which needs no restart. Generation is memory-bound, so more threads than physical cores seldom helps; the benchmark shows it for the machine at hand. Verified: engine threads of a real SmolVLM load carry exactly the chosen CPU set and are released on unload.

### §3 Model management

#### ✅ §3.1 Capability badges & richer model card — **M** (needs X3)
* `Gguf.cs` already parses KV metadata: add architecture, parameter count, context length, expert count, embedding length,
  license (`general.license`), languages, base model; chat-template feature scan (`tools`, `enable_thinking`).
* `HuggingFace.cs`: `GET /api/models/{id}` → card data (`cardData`, `tags`, `license`, `downloads`, `likes`, `lastModified`)
  plus README (`/raw/main/README.md`, strip front-matter, sanitize rendered HTML/Markdown, cache 24 h under `cache/hf/`).
* UI: badge row (👁 vision · 🔧 tools · 🧠 reasoning · ⚙ MoE · 📐 embedding) in Discover, My Models, model picker;
  detail drawer with README, files/quants with fit hint, license (warning on non-commercial), architecture table.
* Tests: capability derivation from fixture metadata dictionaries.

**As built:** `Models/ModelCard.cs`. `GgufInfo` now also reads `general.license`, `general.base_model.0.name`, `general.repo_url` and `general.organization`. `GET /api/models/info?id=` returns the architecture table of a downloaded model (layers, heads, experts, context, vocabulary, licence, base model). `GET /api/models/card?repo=` returns card data and the README of a Hugging Face repository (front matter stripped, cached 24 h under `cache/hf/`, stale copy used when offline, repository ids validated, a 401/403 answers `gated`). `GET /api/models/search` now carries licence and `capabilities` guessed from HF tags and the name (`RemoteCapabilities.FromTags`). `LicenseInfo.Classify` sorts licence ids into open / restricted / noncommercial / unknown and produces a warning for the last two. UI: badge row (👁 vision, 🎧 audio, 🔧 tools, 🧠 reasoning, ⚙ MoE, 📐 embedding, ↕ rerank) in Discover results and My Models, a detail drawer with README, licence warning, base model and, for downloaded models, the architecture table. The README is shown through a small escaping renderer (everything HTML-escaped first, links only for http/https), so no HTML from a README reaches the page. Deviations: badges in the chat model picker were not added; the remote badges are tag-based guesses (flagged by the "unknown" licence / no tags), the downloaded ones come from the real GGUF headers.

#### ✅ §3.2 Discover filters, sorting, pagination, staff picks — **M**
* `/api/models/search` parameters: `sort (trending|downloads|likes|updated)`, `quant`, `minSize/maxSize`, `family` (HF
  `tags`/`pipeline_tag`), `fits=gpu|ram|any`, `vision/tools/reasoning` (tag-based), `limit`, `cursor` (HF returns `Link` header
  for pagination → pass through). "Fits my GPU" is client-side from the per-file list using `FitEstimator`
  (post-filter, flagged as approximate).
* Staff picks: move the hard-coded catalog in `Catalog.cs` to `catalog.json` embedded as default + optional remote refresh
  (`settings.catalogUrl`, default a raw-file URL of the project repo; off in offline mode, ETag caching, signature not needed
  — schema-validated only). UI shows "updated <date>".
* Persist filter state in `localStorage`.

**As built:** `GET /api/models/search` takes `q`, `sort` (trending|downloads|likes|updated), `family` (an HF tag such as `llama` or `qwen2`), `pipeline`, `vision` (pipeline `image-text-to-text`), `tools`, `reasoning` (judged from tags and names, so approximate), `limit` and `cursor`; it answers `{items, next}` where `next` is the cursor from Hugging Face's `Link` header (`HuggingFace.SearchUrl` / `NextCursor`; user text is escaped and only safe values are forwarded). `GET /api/models/files` takes `quant`, `minSize`, `maxSize` (GB) and `fits=gpu|ram` and reports `fitsGpu` / `fitsRam` per file (largest usable VRAM and free RAM, 85 % rule: approximate). The staff picks moved to the embedded `Models/catalog.json`; `Catalog` validates the schema (repo ids, unique keys), can fetch a newer copy from `settings.catalogUrl` (`POST /api/models/catalog/refresh`, ETag revalidation, cached in `cache/catalog.json`, a bad list is refused and the old one stays) and reports `updated` and `source`. The UI has sort / family / capability / quant / size / fits controls, "Load more" paging, an "updated <date>" line with a Refresh button, and remembers the filters in `localStorage`. Deviation: `settings.catalogUrl` defaults to empty (no project repository URL exists yet), so the remote refresh is off until one is set; there is no separate offline-mode switch to honour.

#### ✅ §3.3 Gated-model flow — **S**
* Token validation: `GET https://huggingface.co/api/whoami-v2` with Bearer → show account name or "invalid" in Settings.
* On 401/403 from `resolve/` or repo-info (`gated: "auto"|"manual"` field), return structured error
  `{code: "gated", repo, accessUrl}`; UI shows "This model requires accepting a licence on Hugging Face" with button opening
  `https://huggingface.co/<repo>` (external browser) and a "Retry" button; DownloadManager marks job `needs-access` instead of
  failing generically. Token stored encrypted with DPAPI (`ProtectedData`) rather than plain `settings.json` (migration via X1).

**As built:** `HuggingFace.WhoAmIAsync` and `POST /api/hf/whoami` (`{token?}`; checks the typed token before it is saved, or the saved one) report the account name or why the token is invalid; the Settings token field has a **Check** button. A 401/403 from the repo listing, the card, or the file download throws `GatedModelException`: the API answers **403 `{error:{type:"gated", code:"gated", repo, accessUrl}}`**, and a download job ends in the new state **`needs-access`** (JSON) with `accessUrl` instead of a generic failure. The UI shows "This model requires accepting a licence on Hugging Face" with **Open on Hugging Face** (https://huggingface.co only, `noopener`) and **Retry** in the file list, the detail drawer and the downloads list; a job can be resumed from that state. The token is stored with **DPAPI** (`Core/SecretStore.cs`, current Windows user, `dpapi:` prefix) in `settings.json`; it stays plain text in memory and in `GET /api/settings`. A clear-text token from an older version is encrypted the first time the file is read; a token that cannot be decrypted (other Windows user) is dropped with a log line. Deviation: the migration is done in `AppSettings.Load` instead of a numbered X1 step, because it depends on the Windows account, not on the file format.

#### ✅ §3.4 Model update check — **M**
* At download time store `{repo, file, revision(sha), lfsSha256, etag}` in a sidecar `<file>.llmhost.json` (backfill for
  existing files by hashing is **not** done; those show "unknown").
* `POST /api/models/updates/check` (and background daily check if enabled): for each tracked file query
  `/api/models/<repo>/tree/main` → compare LFS sha256; also report newer **quant files** with the same base name and repos
  whose name changed (`-GGUF` successor via `modelId` redirect). Result `updatesAvailable[]`; UI badge in My Models;
  "Update" = new download job that replaces atomically (download to `.part`, verify SHA, swap, keep old until success).
* Rate-limit and honour offline mode.

**As built:** `Models/ModelUpdates.cs`. Every finished Hugging Face download writes `<file>.llmhost.json` (repo, file name, LFS SHA-256 of each part, later the repository's commit once it is known; deleted with the model). Files without one (downloaded earlier, imported) show **unknown**; they are not hashed afterwards. `POST /api/models/updates/check {id?}` asks the tree API once per repository (300 ms pause between repositories, gated/unreachable repos report `error`), compares the checksums (`ModelUpdateChecker.Compare`: current / update / missing / unknown, every shard of a split model must match) and notices a moved repository (`renamed`); `GET /api/models/updates` returns the last results and `POST /api/models/updates/apply {id}` starts the download of the newer file as a job with `Replace`: it goes to `.part`, is verified against the published SHA-256 and only then moved over the old file, so the old one survives a failed or cancelled update (refused while the model is loaded). `settings.checkModelUpdates` (off by default) runs the check five minutes after start and then daily. UI: "Check for updates" in My Models, a badge per model (update available + Update button, repo moved, not in repo any more, up to date, unknown) and the daily-check box in Settings. Deviation: "newer quant files of the same base name" are not reported (the tree API has no dates to say which quant is newer); there is no offline-mode switch to honour, the check is simply off unless asked for.

#### ✅ §3.5 Models-directory management — **M**
* Multiple library roots: `settings.modelsDirectories[]` (first = download target); `ModelLibrary` merges them, ids stay
  `publisher/repo/file`, duplicates resolved by first root. Change/move: `POST /api/models/directory/move {to, mode:
  move|copy|switchOnly}` with progress job (reuse DownloadManager job list for visibility), refuses to move while models are
  loaded from the folder, handles cross-volume.
* Disk usage: per-model size column, per-root free space, "largest models" sort, total chart in Settings; stale `.part`
  files cleanup action.
* CLI: `models dir [add|remove|move]`. Tests with temp directories.

**As built:** `Models/ModelDirectories.cs`. `settings.extraModelsDirectories` lists more library folders; `settings.modelsDirectory` stays the main folder and download target (`AppSettings.ModelRoots` = main first, then extras). `ModelLibrary` scans all of them; ids stay relative to their folder (`publisher/repo/file`) and the first folder wins for an id found twice; every model carries its `Root`. `GET /api/models/disk` reports per folder: models, size, drive free/total, stale `.part` bytes, plus the largest models; `POST /api/models/disk/clean-partials` deletes `.part` files no unfinished download uses. `POST /api/models/directory/add|remove` manages extras (the main folder, or a folder with a loaded model, cannot be removed). `POST /api/models/directory/move {to, mode: move|copy|switchOnly}` starts a background job (`GET` shows bytes / files / current file, `DELETE` cancels): it refuses a target inside the folder (or the reverse), running downloads, a loaded model when moving, different files with the same name in the target and too little free space; same-drive moves are renames, cross-drive moves copy to `*.moving`, check the length and only then delete the original; models, mmproj files, sidecars and partials all travel; after a copy the old folder stays readable as an extra folder. UI: a "Model folders and disk" card in Settings (folders with usage, add / remove, move with progress and cancel, largest models, partial cleanup) and a "largest first" sort in My Models. CLI: `models dir [list|add|remove|move [--copy]|switch|clean]`. The three directory endpoints that change things need the admin permission. Deviation: the plan's single `modelsDirectories[]` array became main + `extraModelsDirectories` so existing settings files keep working without a migration; there is no total-size chart, the table and the largest-models list carry the numbers.

### §4 Chat experience (Phase D; after splitting the UI — see §9.0)

#### ✅ §9.0 UI restructuring (prerequisite for most of §4 and §9) — **L**
`Web/index.html` is a single 1365-line file. Convert to **Preact + HTM or Vite build** whose output is embedded as resources
(no Node needed at runtime; build step in CSProj `Exec` guarded by `node` presence, committed `dist/` fallback so building
without Node still works). Split into modules: `api.js`, `store.js` (signals), `views/{chat,models,discover,settings,logs,
runtimes}.js`, `components/*`. Keep the existing tooltip help (`p-*` keys) data-driven. Do it incrementally view by view; ship
behind no feature flag (same URL). Add a Playwright smoke test (launch host, open `/`, send a prompt with the fixture model).

**As built:** the page is no longer one file. `Web/index.html` holds the markup; the stylesheet is `Web/app.css` and the script is split by section into `Web/ui/{core,shell,chat,models,discover,settings,system,help,hardware}.js`, all embedded as resources and served by `GET /ui/{name}` (names restricted to `[a-z0-9_-]+.(js|css)`, `Cache-Control: no-cache`). The scripts are plain classic scripts loaded in order, so they share one global scope exactly as before: no behaviour changed. Deviations: **no Preact / Vite / Node build** (Node is not installed here and the build would have had to be re-verified for every view); the split gives the per-view files the plan asked for and later items can add modules, a framework can still be introduced behind the same URLs. There is no Playwright smoke test; `UiPageTests` now loads the page together with all its scripts (and checks that every referenced file is served and that bad names give 404), and the page was opened in the built-in browser to confirm the scripts run without console errors.

#### ✅ §4.1 Chat management — **L**
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

**As built:** chats gained `folderId`, `pinned` and `titleAuto` (chat schema v2; a v1 file simply has none of them). Folders live in `chat-folders.json` (`Core/ChatLibrary.cs`: `ChatFolderStore`; names required, parent must exist, loops refused) with `GET /api/chat-folders`, `PUT /api/chat-folders/{id}` (create / rename / reparent / reorder) and `DELETE` (chats move up one level, or `?chats=delete`). Chat API: `PUT /api/chats/{id}/rename` (a typed title is never replaced by an automatic one), `PUT .../meta` (`folderId`, `pinned`), `POST .../duplicate` (messages, tree and **documents** copied under new ids via `DocumentStore.Clone`), `POST /api/chats/bulk-delete`, `GET .../export?format=json|markdown|text` and `POST /api/chats/import` (our JSON, an array of chats, a bare `{messages:[{role,content}]}` or a ChatGPT `conversations.json`, which is read along its current branch; imports always get new ids). Saving the messages of a chat (`PUT /api/chats/{id}`) keeps its folder, pin and creation date. Deleting a chat now deletes its attachments too. Ids are validated (letters and digits, at most 64) in every chat route. Search: `GET /api/chats/search?q=` over an in-memory inverted index (`ChatSearchIndex`, built on first use, kept current by the store's save / delete events; all words must occur, the last may be a prefix) returns title, snippet and message id, and the UI jumps to that message. Auto titles: after the first answer the UI calls `POST /api/chats/{id}/auto-title`, which asks the loaded model for at most 16 tokens (`ModelRuntime.SuggestTitleAsync`, skipped while the model is busy) and falls back to the first 40 characters of the question; `settings.autoTitleChats` (on by default) switches it off. UI (`Web/ui/chats.js`): folder tree with expand / collapse, pinned chats first, search box, context menu (rename, pin, duplicate, move, export, delete), Ctrl / Shift selection with bulk delete, import button, drag and drop of chats and folders onto folders or the empty list. Deviations: chat ids stay 12 characters of letters and digits (the plan's 36-character GUID pattern never matched how chats are named); sibling folders are re-ordered with Move up / Move down in the menu, chats are ordered pinned-first then by date (no manual chat order); SQLite FTS5 was not added.

#### ✅ §4.2 Split view & compare — **L** (needs §1.8 for same-model compare, parallel requests already exist)
* UI: 2 (later up to 4) chat panes sharing one input box with "send to all" toggle; each pane has its own model + params
  header. Compare mode = one prompt fans out to N panes; show tok/s and TTFT diff table and a "pick best / keep" action.
* State: `panes: [{chatId, model, overrides}]` in the store; each pane uses its own SSE stream; shared prompt text. Panes on
  the same model → parallel requests (continuous batching); different models → different runtimes (JIT-load with
  progress; warn about memory from `FitEstimator` before loading a second one).
* Persistence: layout in `localStorage`; each pane is an ordinary chat.

**As built (not yet run):** `Web/ui/panes.js`, a **⫴** button above the chat list. Compare mode replaces the conversation and parameter columns with 1-4 panes (`+ pane`, ✕), each with its own model (loaded models, and library models marked "loads on demand"), temperature, max tokens and system prompt, plus one shared input with a **send to all** switch (off = only the focused pane). Every pane streams its own `/v1/chat/completions` request, so panes on the same model run in parallel on the server and different models are loaded on demand; before a model that is not loaded is used, the estimate (`/api/models/estimate`) is shown and confirmed when it does not fit or another model already holds memory. After a round a table compares tokens, tokens/s (best in green), time to first token and stop reason; **Keep this** saves the pane as a chat and opens it in the normal view. Every pane is an ordinary chat (`PUT /api/chats/{id}` after each answer, so it shows up in the chat list, search and export); the layout (panes, models, parameters, whether compare mode is on) is kept in `localStorage`. **Status:** written and compiled, but Windows Smart App Control blocked the new `llmhost.dll` at the time, so neither the tests nor a browser check could run; only static checks (`UiPageTests`: markup and script present) exist. Still to do when the build is allowed again: open compare mode in the browser with a loaded model, send to two panes, check the table and Keep. Deviations: panes use plain linear chats (no variant tree inside a pane); the memory warning is a confirm dialog before the first request, not a progress bar for the load.

#### ✅ §4.3 Per-chat settings panel — **M**
* Chat JSON gains `settings { model, systemPrompt, generation:{…}, load? }` (load options shown but applied only on explicit
  "Reload model"). Resolution order: request override > chat > preset > model default > global (single resolver function
  `SettingsResolver.Effective(chat, preset, model)` shared by UI and server so the API can echo the effective config).
* UI: side panel with sections, per-field "differs from default" dot, buttons *Save as preset*, *Save as model default*,
  *Reset to preset*. (Relies on §5.1 for the preset dirty-indicator.)

**As built:** `Core/SettingsResolver.cs` is the single resolver: `Effective(chatOverrides, preset, modelDefault, global, requestOverride)` returns the merged generation settings and, per field, its source (`request | chat | preset | model | global`). A preset, a model default and the global settings are complete sets (the first that exists is the base, as before); the chat stores only what it changed (`Conversation.Settings { preset, overrides, load }`, chat schema v3). The page sends its whole form as `settings.generation` with every save and the server keeps just the fields that differ from the chat's base (`Diff`, only real generation settings; a float written back as a double is not a change); saving messages without a `settings` block keeps what is stored. API: `GET /api/effective-settings?chat=&model=&preset=` (also for a chat that is not saved yet) returns `base`, `overrides`, `effective` and `sources`; `POST /api/chats/{id}/settings/reset` (`?preset=1` also leaves the preset), `POST /api/chats/{id}/settings/save-as` with `{target:"preset", name}` (the chat then follows the new preset) or `{target:"modelDefault"}`. UI (`Web/ui/chatsettings.js`): the Parameters panel marks every field that differs from the preset / model default / global setting with a ● (tooltip names the base), a note counts the differences, and **Save as model default** and **Reset** sit under the presets; the preset chosen with Load is remembered per chat. Deviations: the stored `load` options and "Reload model" have no panel of their own (the existing load dialog is the place for them; the field is kept in the chat file and API); a request to `/v1/chat/completions` still takes its own parameters as before, the request layer of the resolver is available to the endpoint and used by `/api/effective-settings` callers but the OpenAI routes were not rewired to read a chat id.

#### ✅ §4.4 Rich rendering — **M**
* Replace the minimal renderer with `marked` (or `markdown-it`) + `DOMPurify` + `highlight.js` (core + ~20 languages, copy
  button per block, language label), `KaTeX` for `$…$`/`$$…$$`/`\(…\)`, GFM tables, task lists, links opening in the external
  browser (WebView2 `NewWindowRequested`). Libraries are **vendored into the embedded resources** (offline-first: no CDN).
* Streaming-safe: render incrementally with a throttle (50 ms) and handle unclosed fences/math without flicker.
* Reasoning: collapsible `<details>` block, auto-collapsed when answer starts, shows "Thought for 12 s".
* Tests: renderer unit tests in a headless JS test (vitest) for XSS payloads (`<img onerror>`, `javascript:` links).

**As built:** vendored, unmodified minified builds in `Web/vendor/` (licences in `LICENSES.txt`): marked 12.0.2, DOMPurify 3.1.6, highlight.js 11.9.0 (the "common" bundle, about 40 languages) and KaTeX 0.16.11 whose CSS has its 20 woff2 fonts inlined as data URIs, so nothing is fetched from the internet and no font routes are needed; they are embedded resources served from `/ui/`. `Web/ui/render.js`: `renderMarkdown` (GitHub-flavoured Markdown with tables, task lists, strike-through, line breaks) and `renderAnswer` (also folds a `<think>…</think>` written inside the answer). Safety: raw HTML in a model answer is escaped, the result goes through DOMPurify (no `style`, forms, frames; links only `http(s)`, `mailto`, `#`, always `target=_blank rel=noopener noreferrer`, which the desktop window opens in the default browser), remote images are not loaded (shown as a link, `data:image` allowed), KaTeX runs with `trust:false`. Code blocks get a language label, highlight.js colouring and a Copy button; math (`$…$`, `$$…$$`, `\(…\)`, `\[…\]`) is cut out before Markdown so `_` and `*` survive, prices like `$5 and $10` stay text, and an unclosed fence or formula stays plain text until it closes. Streaming: re-rendering is limited to one pass per 50 ms and the HTML of each text is cached, so only the growing message is converted again. Reasoning is a `<details>` block that is open while the model thinks, folds when the answer starts and reads "Thought for 12 s" (`stats.thinkSeconds`, now stored with the message). Verified in the built-in browser: tables, highlighted code, inline and display math, task lists, unclosed code fence and the reasoning block render correctly, and `renderSelfTest()` (nine XSS payloads: `<img onerror>`, `<script>`, `javascript:` and `data:` links, `<svg onload>`, remote image, KaTeX `\href`, injection through a code-fence language, link rel) returns no failures. Deviation: no vitest suite (Node is not installed here), so the payload check is the in-page `renderSelfTest()` plus C# tests that the libraries are served, every script and style comes from `/ui/`, and `render.js` still contains the escaping / sanitising switches.

#### ✅ §4.5 Token counter & per-message stats — **S**
* `POST /v1/tokenize` already exists; add `POST /api/chats/{id}/tokens` (or use client-side debounce 300 ms) returning
  `{prompt, context, remaining}` for the composed prompt incl. system prompt, attachments (RAG-in-context size) and tools.
  Context bar under the input: green < 70 %, amber < 90 %, red ≥ 90 %, tooltip with breakdown; warning shows the active
  overflow policy (§1.5).
* Per-message stats: persist `{tokens, tokensPerSecond, ttftSeconds, stopReason, loadSeconds, promptCachedTokens,
  draftAccepted}` on each assistant message (already produced by `GenerationStats`); small footer on hover/expanded.

**As built:** `POST /api/context/usage` (`model?`, `system?`, `messages[]`, `input?` = the text being typed, `tools?`, `documentIds[]`, `maxTokens?`) renders the conversation with the loaded model's chat template and counts it: `{prompt, tools, documents, used, context, remaining, percent, overflowPolicy}` (`loaded:false` when no chat model is loaded). Attached documents are an estimate (about 3.5 characters per token, at most a third of the window) and are marked as such. UI: a bar under the input, updated 300 ms after typing, a message change or a model change; green below 70 %, amber below 90 %, red from 90 %, with the overflow policy named once it is nearly full and the breakdown in the tooltip. Per-message stats: the final stream chunk (and the non-streaming `stats` object) now also carry `completion_tokens`, `prompt_tokens`, `finish_reason`, `draft_tokens` and `draft_accepted`; the UI stores them on the assistant message (`StoredMessage.Stats`: tokens, tok/s, time to first token, generation time, stop reason, prompt and cached tokens, draft acceptance) and shows a footer that expands on hover. Deviation: `loadSeconds` is part of the stored format but is not filled in yet (the request path does not report how long an on-demand load took), and tool-call turns are counted from the messages as sent, not from the tool schemas the server later adds.

#### ✅ §4.6 Keyboard shortcuts — **S**
Central `shortcuts.js` registry with a help overlay (`?`) and rebinding stored in settings: `Ctrl+N` new chat,
`Ctrl+Shift+N` new folder, `Ctrl+Shift+R` runtime manager, `Ctrl+K`/`Ctrl+P` quick open (fuzzy: chats, models, commands),
`Ctrl+Enter` send, `Esc` stop generation, `Ctrl+,` settings, `Ctrl+L` focus input, `Ctrl+Shift+C` copy last answer, `Alt+←/→`
switch branch variant. WebView2: set `AreBrowserAcceleratorKeysEnabled=false` for conflicting keys, forward `Ctrl+Shift+R` from
`MainWindow.cs`. Conflicts detector in the rebinding UI.

**As built:** `Core/Shortcuts.cs` is the registry: 13 actions with default keys (`Ctrl+N` new chat, `Ctrl+Shift+N` new folder, `Ctrl+K` and `Ctrl+P` quick open, `Ctrl+,` settings, `Ctrl+Shift+R` runtime manager, `?` help, `Ctrl+L` focus input, `Ctrl+Enter` send, `Esc` stop, `Ctrl+Shift+C` copy last answer, `Alt+←/→` answer variants), the combination syntax (`IsValid`, `Normalize`) and the conflict check, which the settings validator also uses (`settings.shortcuts` holds only the user's changes; an empty text switches an action off). `GET /api/shortcuts` returns every action with its default and effective keys. `Web/ui/shortcuts.js` binds them in one capture-phase handler (plain keys never hijack typing in a text box, except Esc; a text selection wins over "copy last answer"), shows the help overlay (`?`), the quick-open palette (fuzzy match over chats, loadable models and commands, arrow keys + Enter) and a "Keyboard shortcuts" card in Settings where **Change** captures the next key press, **Off** and **Default** reset it, and a combination already in use is refused before saving. The desktop window sets `AreBrowserAcceleratorKeysEnabled = false` so WebView2 no longer claims Ctrl+N / Ctrl+P / Ctrl+R first, which also makes forwarding `Ctrl+Shift+R` from `MainWindow.cs` unnecessary. Deviations: F12 / reload accelerators of the embedded browser are off with that setting; the shortcuts are fixed to the 13 actions (no user-defined commands); the key handler itself has no automated test (no JS test runner here), it was checked in the built-in browser, the registry and validation are unit tested.

### §5 Presets

#### ✅ §5.1 File-based presets — **M**
* Move from one `presets.json` to `presets/<name>.json` (name sanitised; folder watched with `FileSystemWatcher` so edits on
  disk appear live). Schema `{ "schemaVersion":1, "name", "description", "systemPrompt", "inference": { …GenerationOptions… } }`.
  **Load params are excluded** (as in BlahLLMWrapper) — a validator drops/flags `load.*` keys. Migrate the old file (X1).
* Dirty tracking: UI store keeps `activePresetId` + a deep-compare of current vs preset (`modified` dot, *Save*,
  *Save as new*, *Reset to preset*). Chats remember the preset id and field overrides (§4.3).
* API: `GET/PUT/DELETE /api/presets/{name}` (exist) + `POST /api/presets/{name}/duplicate`; CLI `preset save|load|rm|export`.
* Tests: round-trip, invalid keys, name sanitisation (no path traversal).

**As built:** `Core/PresetFiles.cs`. Every preset is `presets/<name>.json` (`{schemaVersion, name, description, systemPrompt, inference:{…}}`); `PresetStore` reads the folder on every request, watches it with a `FileSystemWatcher` and bumps `Version` on any change (the page polls `GET /api/preset-list` every 3 s and refreshes its list, so edits on disk appear live). Names are made file-safe (`PresetRules.SafeName`: letters, digits, space, `. _ ( ) -`, no path parts, Windows device names suffixed); only a preset that really exists can be deleted. `PresetRules.Check` validates a document: `load` and every load-only key (the `LoadOptions` properties that generation settings do not have, such as `contextSize`, `gpuLayers`) are dropped and reported, unknown keys are reported and ignored, wrong value types and missing names are refused; unreadable files in the folder are skipped with a log line. The old `presets.json` is split into files on first start and kept as `presets.json.migrated`. API: `GET /api/presets` (name → settings, as before), `GET /api/presets/{name}` (the file document), `PUT` (now also takes `description`), `POST /api/presets/{name}/duplicate`, `DELETE`, `GET /api/preset-list`. CLI: `preset save|load|rm|export <name> [file]|duplicate <name> [new]`. UI: the preset chosen with Load is the chat's active preset (stored per chat, §4.3); fields that differ get a ●, the note says "Preset … modified", and **Update preset** (overwrite the file) and **Reset to preset** sit beside Save (save as new). Deviation: dirty tracking is done in `chatsettings.js` (form compared with the preset) rather than a separate store, and a hand-made file whose `name` differs from its file name is read correctly but deleting it removes `<name>.json`.

#### ✅ §5.2 Import/export & sharing — **S** (needs §5.1)
* Export: download `name.preset.json`. Import: file picker/drag-drop, paste JSON, or URL (`POST /api/presets/import {url}`;
  HTTPS only, ≤ 256 KB, content-type JSON, schema-validated; **user confirmation dialog listing the fields** before saving, name
  collision → rename/overwrite prompt). Also accept GitHub `blob` URLs by rewriting to `raw`. CLI `preset import <file|url>`.
* Share-by-file: register `.llmpreset` extension → open with the app (single-instance message to running host).

**As built:** `Core/PresetShare.cs`. Export: `GET /api/presets/{name}/export` downloads `<name>.preset.json` (the same document the importer reads). Import has two steps: `POST /api/presets/import/preview` takes `{text}` or `{url}` and returns what the preset would set (every field, the system prompt, the ignored load and unknown keys, whether the name exists and a free name), storing nothing; `POST /api/presets/import` saves it (`name` to rename, `overwrite` to replace; a taken name answers 409). Web addresses: https only, GitHub `blob` links are rewritten to `raw.githubusercontent.com`, at most 256 KB (checked while reading), the content must be JSON (or plain text, which raw GitHub serves for `.json`), at most three redirects and each must stay on https; loopback, private, link-local and CGNAT addresses are refused at connect time (`SocketsHttpHandler.ConnectCallback`), so a name cannot resolve differently after the check. The UI has **Export / Import… / Duplicate** under the presets, an import dialog (paste, file picker, address, or drop a `.preset.json` / `.llmpreset` file on the window) that shows the preview and offers Save, or Overwrite / Save under the suggested name on a collision. CLI: `preset import <file|https-url> [--name n] [--overwrite]` (prints the same preview), `preset associate` / `preset unassociate` (registers `.llmpreset` for the current user under HKCU, opening a file runs `llmhost preset import "<file>"`), and `llmhost <file>.llmpreset` imports directly. Deviation: no single-instance message to a running window is needed: the import writes the file and the running app's folder watcher (§5.1) shows the preset within seconds; associating the extension is an explicit CLI command, not done by an installer.

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
