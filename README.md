## NOTE: This is a playground where I'm using AI to create a project. It is currently 100% written by AI, and that is by design to see how far I can push the models until I have to make personal changes to get what I really want.

# LLMHost — a local LLM runner (BlahLLMWrapper–style)

One .NET 10 executable that discovers and downloads GGUF models, runs them in-process with llama.cpp on **CPU or GPU
(AMD supported through the Vulkan backend)**, and exposes them through a desktop window with a web UI, an interactive
CLI, and OpenAI-, Anthropic- and native HTTP APIs.

Build: `dotnet build LLMHostProject.sln -c Release` → `bin/Release/net10.0-windows/llmhost.exe`

## Where the details are

This page says what exists and how to reach it. How each feature is built and why is written down in these files:

| File | What it holds |
|---|---|
| [feature-inference.md](feature-inference.md) | Loading and running models: GPU share, threads, prompt cache, speculative decoding, templates, samplers, MoE, LoRA, embeddings, hardware monitor. |
| [feature-models.md](feature-models.md) | Discover, downloads, gated models, library folders, update check, per-model defaults. |
| [feature-chat.md](feature-chat.md) | Chats, folders, search, attachments and documents, compare mode, rendering, shortcuts, presets and sharing. |
| [feature-server-api.md](feature-server-api.md) | OpenAI / Anthropic / native / legacy APIs, tokens, JIT loading, HTTPS and limits, request log, metrics, MCP. |
| [feature-cli.md](feature-cli.md) | Verbs, `--json`, exit codes. |
| [feature-linked-devices.md](feature-linked-devices.md) | Pairing and using models of your other computers. |
| [feature-desktop-app.md](feature-desktop-app.md) | Window, interface modes, themes, languages, first-run wizard, updates, notifications. |
| [implementation-plan.md](implementation-plan.md) | The design of every feature, one section per item (§1 inference … §9 desktop). Each finished item ends with an **As built** paragraph that says what was actually done, where the code is and what was left out. Section numbers are used below, e.g. "plan §6.3". |
| [todo.md](todo.md) | The feature checklist: what is done (`[x]`) and what is still open. |
| [docs/adding-options.md](docs/adding-options.md) | The checklist for adding a load or generation option (declare → clone → validate → use → API → UI → tests) and how the interface modes hide advanced options. |
| [docs/link-threat-model.md](docs/link-threat-model.md) | Linked devices: what the pairing and signing protect against, and what they do not. |
| [tests/README.md](tests/README.md) | Which kind of test goes where, shared helpers, how to run the model-gated tests. |

## Quick start

```
llmhost                          # DEFAULT: hosts the web UI + API and shows the UI in its own window (embedded WebView2, no external browser)
llmhost --port 8080 --lan        # custom port / API also reachable on the network (--no-window = headless, no UI window)
llmhost cli                      # interactive terminal shell (chat + slash commands)
llmhost catalog                  # curated list of major model families
llmhost get qwen2.5-0.5b         # download (resumable), from the catalog key or any HF repo
llmhost chat qwen2.5-0.5b-instruct-q4_k_m --gpu
llmhost serve --model <model> --lan --port 1234   # same server, no window, preload a model
```

Every command also works inside the interactive shell with a leading `/` (`/load`, `/set temperature 0.3`, …).
`llmhost help` lists everything. On the first start the **first-run wizard** surveys the hardware, recommends models that
fit and lets you choose the models folder (plan §9.3). The default port is 1234; the UI is also at `http://localhost:1234/`
in any browser.

## How the program is reached

| Surface | How to get there | Used for |
|---|---|---|
| Desktop window | `llmhost` (tray icon, single instance, remembers window size) | The web UI in its own window |
| Web UI | `http://localhost:1234/` | Same UI from a browser or another machine (`--lan`) |
| Interactive shell | `llmhost cli` | Chatting and managing models from a terminal |
| One-shot commands | `llmhost <verb> …` (add `--json` for scripts) | Scripting; see [the CLI section](#command-line) |
| HTTP APIs | `http://localhost:1234` | OpenAI, Anthropic, native `/api/v1`, legacy `/api/v0`, `/metrics` |
| Background | `llmhost daemon up\|down\|status\|update` | Headless server as a background process, startup task or Windows service |

## The web UI

The tabs along the top:

| Tab | What you do there |
|---|---|
| **Chat** | Chat with a loaded model. Folders and search across chats, edit/resubmit/delete/regenerate with branching variants, attachments (images, PDF, DOCX, TXT, MD with retrieval for long files), split view and multi-model **compare**, per-chat settings (model, system prompt, parameters, "save as preset"), live token counter and per-message stats (tok/s, time to first token, stop reason), Markdown with syntax highlighting, LaTeX, tables and collapsible reasoning. Plan §4. |
| **My Models** | The library: load dialog (device, GPU ratio, context, threads, cache, speculative decoding, LoRA, MoE …), per-model default settings, capability badges, prompt-template override, disk usage, models folders, import of existing `.gguf` files, update check. Plan §1, §3. |
| **Discover** | Curated catalog and live Hugging Face search with filters, sorting, fit hints, staff picks and the gated-model flow; resumable downloads with a queue. Plan §3. |
| **Requests** | Live request/response log with filters; same data as `/metrics`. Plan §6.4. |
| **Logs** | Application log with a level filter. |
| **Settings** | Server (port, LAN, API tokens and permissions, HTTPS, allow-list, limits, JIT policy), hardware and live monitor, runtimes, **interface mode**, theme/accent/language, MCP servers, updates, linked devices, notifications. |
| **Help** | Built-in help. |

Keyboard shortcuts (new chat, new folder, quick open, runtime manager …) are listed in Help and can be changed; plan §4.6.

### Interface modes, themes, language

* **Interface modes**: *User*, *Power user* and *Developer* progressively reveal advanced options (Settings → Interface). Developer mode adds the request log and raw controls. Plan §9.1; how an option is assigned a mode: [docs/adding-options.md](docs/adding-options.md).
* **Themes**: light, dark or follow the system, plus an accent colour. **Languages**: English and German (`Web/locales/*.json`; a missing key falls back to English). Plan §9.2.
* **Notifications**: finished/failed downloads, slow model loads, available updates and finished answers. In the desktop window they appear as tray balloons (only when minimised, if you choose); in a browser the Notifications API is used after you allow it. Settings → Notifications. Plan §9.5.
* **Updates**: Settings → Updates checks the feed for the chosen channel (stable/beta), downloads the package, verifies its SHA-256 and signature, installs through the separate updater program and rolls back if the new version does not start. CLI: `llmhost update`. Plan §9.4.

## Features

| Area | What you get |
|---|---|
| Discovery | Curated catalog (Llama, Qwen, Mistral, Gemma, Phi, DeepSeek-R1, gpt-oss, Granite, SmolLM, embeddings), live Hugging Face GGUF search, per-file quantization list with "fits GPU/RAM" hints, filters, sorting, gated-model flow |
| Download | Resumable background downloads with queue, pause/resume, disk-space check, SHA-256 verification, split-shard support, HF token for gated repos, download by URL / `user/model` |
| Library | `publisher/repo/file.gguf` layout, GGUF metadata inspection, several model folders, move/import, per-model defaults, update check |
| Vision & audio | Image input for multimodal GGUF models: the `mmproj` projector is auto-detected / downloaded, images accepted as OpenAI `image_url` (data URL or http(s)), Anthropic `image` blocks, UI attach/paste/drop and CLI `/image <file>`; audio input with audio-capable projectors |
| Prompt cache | KV cache is kept between requests, with several slots per model; only the changed part of the prompt is evaluated (`cache_prompt`, CLI `/cache`, UI checkbox); caches can be saved to disk. Reports `cached_tokens` |
| Execution | **CPU by default**; opt in to GPU (`--gpu`, all layers), auto, or a share of the layers (`--gpu 0.5`); multi-GPU with tensor split; several models loaded at once; multiple instances of one model; idle TTL; **JIT loading** with auto-eviction (plan §6.1) |
| GPUs (incl. AMD) | Vulkan is the only GPU backend (AMD Radeon, NVIDIA, Intel); no ROCm/HIP/CUDA install needed. `config backend auto\|vulkan\|cpu`. Live utilisation/VRAM monitor |
| Parameters | temperature, top-k/p, min-p, typical-p, Mirostat, DRY, XTC, top-n-sigma, sampler order, penalties, max tokens, seed, stop strings, logit bias, `logprobs`, `n`, GBNF grammar, JSON mode, JSON-schema output, context-overflow policy, system prompt |
| Load options | context length, batch/ubatch, threads (affinity, priority, NUMA), flash attention, mmap/mlock, KV-cache quantization, RoPE, main GPU, MoE experts, LoRA adapters, speculative decoding with a draft model, prompt-template override |
| Presets | JSON files with live reload, dirty indicator, duplicate, import/export from file or URL |
| Documents | Attach `.pdf`, `.docx`, `.txt`, `.md`: whole file in context when it fits, otherwise chunked, embedded and retrieved |
| Tools | Prompt- or template-based tool calling (`tool_choice`, parallel calls), reasoning (`<think>`) as separate output, **MCP host** (`mcp.json`, stdio and remote servers, per-call confirmation) |
| Embeddings | `/v1/embeddings` with pooling/normalisation/dimensions options and `/v1/rerank` |
| Server | See [Server and APIs](#server-and-apis) |
| Linked devices | Use the models of your other computers over a signed, paired connection. See [Linked devices](#linked-devices) |
| Desktop | Tray icon, auto-start, single instance, window state, first-run wizard, auto-update, installer |

## Server and APIs

| API | Endpoints |
|---|---|
| OpenAI | `POST /v1/chat/completions` (SSE streaming), `/v1/completions`, `/v1/responses`, `/v1/embeddings`, `/v1/rerank`, `GET /v1/models` |
| Anthropic | `POST /v1/messages` (streaming, tool use, images, `thinking`, `tool_choice`), `/v1/messages/count_tokens` |
| Native | `/api/v1/*`: chat, models list/load/unload, downloads (token permissions apply) |
| Legacy | `/api/v0/*` for older clients (models with state/quant/arch, chat, completions, embeddings) |
| Operations | `GET /metrics` (Prometheus), `/api/requests` (request log, `/stream` for live) |

Security and limits (Settings → Server; plan §6.3):

* **API tokens**: create, name and revoke several tokens with per-token permissions; the secret is shown once. `Authorization: Bearer …` or `x-api-key`. CLI: `llmhost token …`.
* **HTTPS** with a generated self-signed certificate or your own, **IP allow-list**, request-size caps and per-client rate limits, CORS, LAN binding. Metrics can be made public.
* **JIT loading**: a request for a model that is not loaded loads it; the previous JIT-loaded model is evicted, with an idle TTL (per request via `ttl`). Plan §6.1.

Anthropic SDKs and tools work against local models:

```
export ANTHROPIC_BASE_URL=http://localhost:1234
export ANTHROPIC_API_KEY=anything        # or a token created in Settings
```

Model names that aren't loaded (e.g. `claude-sonnet-4`) are mapped to the currently loaded model. Toggle with
`config anthropic on|off` or the checkbox in Settings. Details and gaps: plan §6.5.

## Command line

Global flags work with every verb: `--json` (results as JSON on stdout, everything meant for people on stderr),
`--quiet` / `-q`, `--no-color`, `--verbose`. Lists and tables (`ls`, `preset`, `chats`, `downloads`, `ps`, `hardware`,
`daemon status` …) become JSON arrays or objects with stable keys; streaming verbs such as
`log stream --source request --json` print one JSON document per line. Plan §7.3.

Main verbs: `catalog`, `search`, `get`/`clone`, `downloads`, `ls`, `import`, `load`/`unload`/`ps`, `chat`, `cli`,
`serve`/`server`, `daemon`, `preset`, `chats`, `config`, `token`, `mcp`, `runtime`, `hardware`, `log`, `update`,
`login`, `link`. `llmhost help <verb>` shows the options.

| Exit code | Meaning |
|---|---|
| 0 | ok |
| 1 | generic failure |
| 2 | usage error, or not available in this build (`push`, `dev`) |
| 3 | not found (model, preset, file) |
| 4 | not authorised (bad or missing token) |
| 5 | server unreachable |
| 6 | out of memory / the model does not fit |
| 7 | cancelled |
| 8 | conflict / already exists / already running |

`login --hf-token <token>` checks a Hugging Face token and stores it encrypted (Windows DPAPI); `clone <user/model>` is `get`.

## Linked devices

Off by default. `llmhost link enable`, then `llmhost link pair start` on one computer and `link pair <address> <code>` on the other (or Settings → Linked devices):
one device shows a six-digit code, the other types it. Each device has its own key pair; every request between devices
is signed, permissions are set per device (`chat`, `load`, `manage`), and models of a linked device appear in the model
list and can be used for requests. CLI: `link enable|disable|status|set-device-name|set-preferred-device|peers|unpair|permissions`.
How it works: plan §8.1. What it does and does not protect against: [docs/link-threat-model.md](docs/link-threat-model.md).
mDNS discovery and QR pairing are not built.

## Files

Settings, presets, chats, link keys and the request log live in `%USERPROFILE%\.llmhost` (override with
`LLMHOST_HOME`). Models are downloaded to a `DownloadedModels` folder in the **working directory** the app is started
from (`config modelsdir <path>` to change, `config modelsdir default` to reset; more folders can be added in
Settings → Models). Saved data is versioned (`schemaVersion`) and upgraded on start-up.

## Building, packaging, tests

```
dotnet build LLMHostProject.sln -c Release
pwsh build\publish.ps1 -Version 1.2.0     # single-file win-x64 zip + SHA256SUMS in artifacts\ (the package the in-app updater installs)
dotnet test tests/LLMHost.Tests           # unit + API contract tests (no model needed)
$env:LLMHOST_TEST_MODEL = "C:\path\to\small-chat-model.gguf"; dotnet test tests/LLMHost.Tests --filter "Category=Integration"
```

* The updater is a separate small program (`Updater/`) built next to `llmhost.exe`. `installer/Package.wxs` (WiX MSI) and `.github/workflows/release.yml` are written but have not been run in CI. Plan §9.4.
* Integration tests run only when the model environment variables are set (`LLMHOST_TEST_MODEL`, and the vision/embedding ones listed in [tests/README.md](tests/README.md)); CI downloads Qwen2.5-0.5B for them (`.github/workflows/ci.yml`). The full suite is about 800 tests.
* To add a load or generation option follow [docs/adding-options.md](docs/adding-options.md).
* `tests/fixtures/mcp-test-server.ps1` is a tiny stdio MCP server for trying the MCP host by hand.

## Notes / limits

* GGUF models only (llama.cpp). Vision models need their `mmproj` file next to the model (downloaded automatically with `get`); audio input is not tested with a real audio model.
* Follow-up turns of a conversation with images are served from the prompt cache; models with M-RoPE vision (Qwen2-VL family) still re-evaluate the prompt.
* CPU-vs-GPU backend choice (`config backend auto|vulkan|cpu`) is process wide; per-model placement is `--cpu`, `--gpu`, `--gpu-layers`.
* Tool calling is implemented by prompting + JSON parsing for models without a native format, so quality depends on the model.
* Not built yet: per-backend worker processes (plan §2.5), the security review (plan §10.3), mDNS/QR for linked devices, translations of the help page and tooltips. Open items are in [todo.md](todo.md).
