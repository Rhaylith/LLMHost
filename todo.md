# TODO — remaining work to reach BlahLLMWrapper feature parity

Scope: everything BlahLLMWrapper offers **except Apple-specific features** (MLX engine, Metal, macOS/iOS integration).
**GPU scope: Vulkan only** — no AMD HIP/ROCm and no CUDA backends are planned (Vulkan runs on AMD, NVIDIA and Intel GPUs).
Priorities: **P1** = core gap users will hit quickly, **P2** = expected by BlahLLMWrapper users, **P3** = polish / nice to have.

## Docs review notes

* **Newest BlahLLMWrapper line is 0.4.x.** 0.4.0 (2026-01-28) added headless server deployment (`llmster`), parallel requests with
  continuous batching, the new native REST API (`/api/v1/*`) and a refreshed UI. 0.3.39 added Open Responses support;
  0.3.37 LFM2 tool calls; MCP host support arrived in 0.3.17.
* **Bionic is a separate product** (BlahLLMWrapper's agent app: Canvas, Skills, Auto Review, session introspection, `/v1/systemone`,
  `/v1/decisions`). It is *not* part of BlahLLMWrapper proper and is treated as **out of scope** here; only the engine-level things it
  relies on (MTP speculative decoding, `lms load --auto`, images in tool results, tool-call argument streaming) are tracked.
* Doc pages I could not read (404 / no detail) so the related items rest on earlier knowledge — re-verify before building:
  MCP-via-API, Idle TTL & Auto-Evict, `model.yaml`, runtime manager, Streaming events spec, Interface modes page.

## Already implemented (for reference — do not redo)

- [x] GGUF model discovery: curated catalog + live Hugging Face search + per-file quantization list with fit hints
- [x] Resumable downloads (background, progress, cancel, split shards, HF token)
- [x] Local library (`publisher/repo/file.gguf` — same layout BlahLLMWrapper uses), GGUF metadata reader
- [x] CPU / GPU / auto device placement, explicit GPU layer count, several models loaded at once, idle TTL, JIT load on request
- [x] GPU support (incl. AMD) via the llama.cpp **Vulkan** backend — Vulkan is the only supported GPU backend (no ROCm/HIP/CUDA)
- [x] Generation params: temperature, top-k/p, min-p, typical-p, repeat/frequency/presence penalty, max tokens, seed, stop strings, logit bias, GBNF grammar, JSON mode, JSON-schema output, system prompt, presets
- [x] Load params: context length, batch/ubatch, threads, flash attention, mmap/mlock, KV cache type, RoPE base/scale, main GPU
- [x] OpenAI-compatible API (`chat/completions`, `completions`, `embeddings`, `models`) with streaming; prompt-based tool calling
- [x] Anthropic-compatible API (`/v1/messages`, streaming, tool use, `count_tokens`)
- [x] Single API key, CORS, LAN binding
- [x] Prompt/KV cache: the evaluated prompt prefix is kept between requests and only the changed tail is re-evaluated (`cache_prompt`, `/cache`, `POST /api/models/cache/clear`, cached-token stats)
- [x] Vision / multimodal input: mmproj auto-detected and downloaded with the model, images via OpenAI `image_url`, Anthropic `image` blocks, web UI attach/paste/drop and CLI `/image`; capability flags in `/v1/models`, `/api/status` and the library
- [x] In-process CLI (chat, history, undo/regen, save/resume/export, params, presets, logs) and sub-commands
- [x] Web UI: chat + parameter panel, model manager, discover/download, settings, logs — shown in a native window hosted by the exe (WebView2), also served over HTTP

---

## 1. Inference engine

- [x] **P1** Parallel requests with continuous batching per model, configurable **max concurrent predictions** (BlahLLMWrapper default 4) — today one request per model at a time
- [x] **P2** Prompt cache follow-ups: several cache slots (`cacheSlots`, default parallel requests + 2) and save/restore to disk (`/api/models/cache/save|restore`, optional auto-save on unload). The llama-server engine cache is not applicable (no such engine) and moves to §2 worker processes
- [x] **P2** Vision follow-ups: images inside tool results, audio input (WAV/MP3/FLAC; untested with a real audio model), per-image detail/resize options, video frames (ffmpeg), reuse of the prompt cache across image turns. Vision for the llama-server engine does not apply (no such engine; see §2 worker processes)
- [x] **P1** Native, template-aware tool calling (model-specific tool-call formats, `tool_choice` incl. `required`/named, parallel calls, streaming tool-call arguments) instead of the generic JSON prompt
- [x] **P1** Reasoning support: `<think>` parsing → separate `reasoning` output item / `reasoning_content` / Anthropic `thinking` blocks, and a `reasoning` effort control (`off|low|medium|high|on`) mapped to each model's template
- [x] **P2** Speculative decoding with a draft model (shared vocabulary, suggested pairings and size limit). MTP-style speculation is only detected, not usable with the bundled llama.cpp; speed-up not yet measured with a real small draft
- [x] **P2** Prompt-template override per model: auto-detect from GGUF metadata, edit as **Jinja** or as **manual** role prefix/suffix, "always show template" toggle 
- [x] **P2** Additional samplers: Mirostat v1/v2, DRY, XTC, top-n-sigma (TFS no longer exists in llama.cpp), configurable sampler order
- [x] **P2** Context-overflow policies (truncate middle, rolling window, stop at limit, error) per request; the default stays "drop oldest turns"
- [x] **P2** MoE controls (number of experts, force expert weights onto CPU / tensor buffer overrides)
- [x] **P2** LoRA adapter loading (per model, with a strength; swap at runtime; synthetic adapters tested, no real trained adapter yet)
- [x] **P2** Multiple instances of the same model (distinct identifiers/contexts; they share the weights)
- [x] **P2** `logprobs` / `top_logprobs`, `n > 1` choices (chat, completions, Responses and native chat; contract-tested)
- [x] **P2** Embedding model options (pooling, normalisation, batching, dimensions, base64); reranking (`/v1/rerank`, cross-encoder and similarity fallback)
- [ ] **P3** Harden the schema→GBNF converter (`$defs` cycles, `pattern`, numeric bounds, `additionalProperties`, string length)
- [ ] **P3** Cancel an in-flight model load; real load phases and prompt-processing progress events

## 2. Hardware & runtimes

- [x] **P1** Runtime/engine manager (BlahLLMWrapper's Ctrl+Shift+R view): list installed llama.cpp builds (CPU AVX/AVX2/AVX512 and Vulkan only — no ROCm/HIP/CUDA), download/update/select them, show the active one, per-model choice, auto-update, hardware survey for compatibility
- [x] **P1** Multi-GPU: select devices, tensor-split ratios, split mode, GPU priority order; reliable Vulkan-device ↔ DXGI/WMI adapter mapping
- [x] **P1** Accurate "will it fit?" estimate (weights + KV cache + compute buffers + MoE, iGPU shared memory) with pre-load warning and the "limit offload to dedicated GPU memory" guardrail
- [x] **P2** GPU offload expressed as a ratio (`--gpu max|auto|0.0–1.0`, `gpu` in the native API, a slider in the UI) in addition to a raw layer count
- [ ] **P2** Switch backend (CPU ↔ Vulkan) at runtime by hosting each backend in its own worker process (native lib is process-wide today)
- [ ] **P2** Live hardware monitor (GPU/CPU utilisation, VRAM/RAM, temperature)
- [ ] **P2** CPU thread pool settings (`threads`, `threads_batch`, affinity/priority), NUMA
- [ ] **P3** Offline-operation audit (no network calls except Hugging Face when the user asks; no telemetry) and a documented system-requirements page

## 3. Model management

- [x] **P1** Per-model default load settings **and** inference defaults (gear dialog in My Models; "save current load settings as default"; applied automatically in UI, API and CLI)
- [x] **P1** Download manager: pause/resume, queue with concurrency limit, persist jobs across restarts, disk-space check, SHA-256 verification, retry/back-off, mmproj companion download
- [x] **P1** Download by URL / `user/model` / pasted Hugging Face link; **import** existing `.gguf` files (`lms import` equivalent: copy/move/link into `publisher/model/file.gguf`)
- [ ] **P2** Capability badges (vision, tool use, reasoning, embedding, MoE) and richer model card (README, license, architecture, max context)
- [ ] **P2** Discover filters/sorting (size, quantization, family, downloads, updated, "fits my GPU"), pagination, updatable curated "staff picks"
- [ ] **P2** Gated-model flow (token validation, access-request guidance)
- [ ] **P2** Model update check (newer revision/quant available)
- [ ] **P2** Models-directory management (change/move, multiple folders, disk usage per model)
- [ ] **P3** `model.yaml` support (virtual model definitions, metadata overrides, suggested configs) and publishing
- [ ] **P3** Aliases / favourites / tags / folders in "My Models"

## 4. Chat experience

- [x] **P1** Edit a message and resubmit, delete a message, regenerate with variants (branching tree)
- [x] **P1** Chat with documents: attach `.pdf`, `.docx`, `.txt` (+ `.md`); put the whole file in context when it fits, otherwise chunk + embed + retrieve (RAG) with citations
- [x] **P1** Image / file attachments in chat (needs vision support)
- [ ] **P2** Chat folders (nested, drag-and-drop), duplicate chat (with attachments), search across chats, rename, auto titles, bulk delete, import/export (JSON/Markdown/text)
- [ ] **P2** Split view: two chats/models side by side, send simultaneously (relies on parallel requests); multi-model compare
- [ ] **P2** Per-chat settings panel (model, system prompt, params) with "save as preset / as default"
- [ ] **P2** Rich rendering: full Markdown, syntax highlighting + copy, LaTeX, tables, collapsible reasoning
- [ ] **P2** Live token counter / context-usage bar; per-message stats (tok/s, TTFT, stop reason, load time)
- [ ] **P2** Keyboard shortcuts (new chat, new folder, runtime manager, quick open)
- [ ] **P3** Prompt library / snippets, voice input

## 5. Presets

- [ ] **P2** Presets as JSON files in a `config-presets`-style folder containing system prompt + all inference params (load params excluded, as in BlahLLMWrapper), with an "unsaved changes / reset to preset" indicator
- [ ] **P2** Import / export a preset from a file or URL; share by file
- [ ] **P3** Hub-style publish, pull updates, push new revisions (needs a hosting target — could be a Git repo or simple registry)
- [ ] **P3** Legacy-preset migration helper (0.2.x format)

## 6. Server / API

- [x] **P1** **Native REST API v1** (`/api/v1/*`): `POST /api/v1/chat`, `GET /api/v1/models`, `POST /api/v1/models/load`, `/unload`, `/download`, `GET /api/v1/models/download/status`
  - [ ] Stateful chats: `response_id` (`resp_…`), `previous_response_id`, `store=false`, branching from any response
  - [ ] Request fields: `input` (text + `image` `data_url` items), `system_prompt`, `temperature/top_p/top_k/min_p/repeat_penalty`, `max_output_tokens`, `reasoning`, `context_length`, `stream`
  - [ ] Response `output[]` items (`message`, `reasoning`, `tool_call`, `invalid_tool_call`) and `stats` (`input_tokens`, `total_output_tokens`, `reasoning_output_tokens`, `tokens_per_second`, `time_to_first_token_seconds`, `model_load_time_seconds`)
  - [ ] SSE streaming events incl. model-load and prompt-processing progress
  - [ ] Load options on `models/load` (context length, flash attention, GPU offload, etc.) and echo of the applied config
- [x] **P1** OpenAI **Responses** API (`/v1/responses`, incl. Open Responses/Codex-style clients) and `n`, `logprobs`, `max_completion_tokens`
- [x] **P1** API tokens: authentication toggle, create/name/revoke multiple tokens, **per-token permissions**, token shown once (current: single static key)
- [x] **P1** Headless daemon mode (`llmster` equivalent): run as background process / Windows service / startup task, `daemon up|down|status|update`, no GUI required
- [x] **P1** MCP host: `mcp.json` (Cursor notation) with local (stdio) and remote (URL + headers) servers, tool listing, per-call confirmation UI, "MCP via API" (`integrations` with `plugin` and `ephemeral_mcp` entries, `allowed_tools`)
- [ ] **P2** JIT loading policy: idle TTL default, **auto-evict** previously JIT-loaded models when another loads, max loaded models, per-request TTL override
- [ ] **P2** Legacy `/api/v0/*` endpoints for older clients (models with state/quant/arch, chat, completions, embeddings)
- [ ] **P2** HTTPS (self-signed or supplied cert), IP allow-list, request size/rate limits
- [ ] **P2** Request/response log viewer with filters and `lms log stream` equivalent; `/metrics` endpoint
- [ ] **P2** Anthropic API gaps: image blocks, images in tool results, `thinking` blocks, `tool_choice` forcing, `cache_control` accepted/ignored
- [ ] **P3** Ollama-compatible endpoints (`/api/chat`, `/api/generate`, `/api/tags`)
- [ ] **P3** OpenAPI document for management + native APIs
- [ ] **P3** Plugin system (BlahLLMWrapper "plugins": tool providers, prompt preprocessors, generators; `lms dev/push/clone`) — optional, large
- [ ] **P3** Client SDKs (Python/TypeScript) — existing OpenAI/Anthropic SDKs already work; first-party wrappers optional

## 7. CLI (parity with `lms`)

- [x] **P1** Missing verbs: `server start|stop|status`, `daemon up|down|status|update`, `log stream`, `import`, `runtime` (list/get/select/update), `load --identifier --gpu max|auto|<0–1> --context-length`, `unload --all`, `ls`/`ps` with `--json`, `get` interactive search+quant picker, `chat` against a running server
- [ ] **P2** `link` verbs (`enable|disable|status|set-device-name|set-preferred-device`) once LM Link-style remoting exists
- [ ] **P2** `login` / `push` / `clone` / `dev` equivalents if a hub/plugin concept is added
- [ ] **P2** Machine-readable output (`--json`), stable exit codes, quiet mode
- [ ] **P3** Shell completion scripts, richer line editing (history, multi-line editor, Ctrl+R)

## 8. Remote / multi-device

- [ ] **P2** **LM Link equivalent**: connect your own devices into a private encrypted network (Tailscale-style or WireGuard), list remote models, route load/chat to a preferred device from UI, CLI and API
- [ ] **P3** Pair mobile/remote clients (web UI already works over LAN; add auth + discovery)

## 9. Desktop application & UX

- [x] **P1** Finish the desktop shell: tray icon, auto-start, single instance, window state persistence, update notifications (the embedded WebView2 window hosting the UI inside the exe is done)
- [ ] **P2** **User / Power User / Developer** interface modes that progressively reveal advanced options (speculative decoding, per-model configs, server settings, logs)
- [ ] **P2** Color themes (light/dark/system) and interface language selection + localisation
- [ ] **P2** First-run onboarding: hardware survey, recommended models, choose models folder
- [ ] **P2** In-app auto-update and release channels; installer (MSIX/MSI) and self-contained single-file publish
- [ ] **P2** Notifications on download/load completion
- [ ] **P3** Accessibility pass, responsive/mobile layout

## 10. Quality, safety & operations

- [x] **P1** Automated tests: unit (GGUF parser, TextScanner, SchemaGrammar, OpenAI/Anthropic mapping, download resume), API contract tests, small-model integration test in CI
- [x] **P1** Robust failure handling: out-of-memory, driver failures, corrupt/partial GGUF, unsupported architectures (clear message + retry with fewer GPU layers)
- [ ] **P2** Settings schema versioning/migration and field validation with UI feedback
- [ ] **P2** Structured file logging with rotation; native llama.cpp log level control
- [ ] **P2** Security review: path traversal on model/chat IDs, body limits, auth on the web UI itself, safe defaults when binding to LAN, MCP server sandbox warnings
- [ ] **P3** Local crash dumps / diagnostic bundle export
- [ ] **P3** User guide, API reference, troubleshooting (Vulkan driver issues, VRAM errors)

## Known limitations of the current implementation (to fix as part of the above)

- Native backend (CPU vs Vulkan vs custom) is chosen once per process; per-model CPU placement works via `gpu_layers = 0`.
- Only one request per loaded model runs at a time.
- Tool calling is prompt-based and model-quality dependent; no reasoning-content field.
- Auth is a single static key accepted via `Authorization` or `x-api-key`.
- Download job list is in-memory only (partial files do resume when re-requested).
- GPU index → Vulkan device mapping is assumed to match DXGI order.
- Web UI Markdown rendering is minimal; no message editing.
