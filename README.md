## NOTE: This is a playground where I'm using AI to create a project. It is currently 100% written by AI, and that is by design to see how far I can push the models until I have to make personal changes to get what I really want.

# LLMHost — a local LLM runner (BlahLLMWrapper–style)

One .NET 10 executable that discovers and downloads GGUF models, runs them on **CPU or GPU (AMD supported
through llama.cpp's Vulkan backend)**, and exposes them through an in-process CLI, an OpenAI-compatible HTTP
API, and a built-in web UI.

Build: `dotnet build LLMHostProject.sln -c Release` → `bin/Release/net10.0/llmhost.exe`

## Quick start

```
llmhost                          # DEFAULT: hosts the web UI + API and shows the UI in its own window (embedded WebView2, no external browser)
llmhost --port 8080 --lan        # same, custom port / API also reachable on the whole network (--no-window = headless, no UI window)
llmhost cli                      # interactive terminal shell (chat + slash commands)
llmhost catalog                  # curated list of major model families
llmhost get qwen2.5-0.5b         # download (resumable), from the catalog key or any HF repo
llmhost chat qwen2.5-0.5b-instruct-q4_k_m --gpu
llmhost serve --model <model> --lan --port 1234   # same server, no browser, preload a model
```

Every command also works inside the interactive shell with a leading `/` (`/load`, `/set temperature 0.3`, …).
`llmhost help` lists everything.

## Features

| Area | What you get |
|---|---|
| Discovery | Curated catalog (Llama, Qwen, Mistral, Gemma, Phi, DeepSeek-R1, gpt-oss, Granite, SmolLM, embeddings), live Hugging Face GGUF search, per-file quantization list with "fits GPU/RAM" hints |
| Download | Resumable background downloads with progress, split-shard support, HF token for gated repos |
| Library | `publisher/repo/file.gguf` layout, GGUF metadata inspection (arch, quant, context, embedding detection) |
| Vision | Image input for multimodal GGUF models: the `mmproj` projector is auto-detected / downloaded, images accepted as OpenAI `image_url` (data URL or http(s)), Anthropic `image` blocks, web UI attach/paste/drop and CLI `/image <file>`; `--no-vision` / `--mmproj` load options |
| Prompt cache | KV cache is kept between requests; only the part of the prompt that changed is evaluated (`cache_prompt` request field, CLI `/cache on\|off\|clear`, UI checkbox). Reports `cached_tokens` in usage |
| Execution | **CPU by default**; opt in to GPU (`--gpu`, all layers), auto (fits as many layers as VRAM allows) or a share of the layers (`--gpu 0.5`) per model; explicit layer count; several models loaded at once; idle TTL auto-unload |
| GPUs (incl. AMD) | Vulkan is the only GPU backend (works on AMD Radeon, and also NVIDIA/Intel); no ROCm/HIP/CUDA install needed. `config backend auto\|vulkan\|cpu` |
| Parameters | temperature, top-k/p, min-p, typical-p, repeat/frequency/presence penalty, max tokens, seed, stop strings, logit bias, GBNF grammar, JSON mode, JSON-schema constrained output, system prompt; presets |
| Load options | context length, batch/ubatch, threads (generation / batch, CPU affinity, priority), flash attention, mmap/mlock, KV-cache quantization (f16/q8_0/q4_0), RoPE base/scale, main GPU |
| Server | OpenAI API: `/v1/chat/completions` (streaming SSE), `/v1/completions`, `/v1/embeddings`, `/v1/models`; **Anthropic API**: `/v1/messages` (streaming events, tool use, `count_tokens`); tool calling (prompt based), API key (`Authorization` or `x-api-key`), CORS, LAN binding |
| CLI | streaming chat, history, undo/regenerate, save/resume/export chats, raw completion, embeddings, token counting, logs |
| Web UI | chat with parameter panel, model manager (load dialog), discover/download, hardware + settings, logs |

## Files

Settings, presets and chats live in `%USERPROFILE%\.llmhost` (override with `LLMHOST_HOME`). Models are downloaded to a
`DownloadedModels` folder in the **working directory** the app is started from (`config modelsdir <path>` to change,
`config modelsdir default` to reset).

## Contributing

Adding a load or generation option follows a fixed checklist (declare → clone → validate → use → API → UI → tests):
see [docs/adding-options.md](docs/adding-options.md). Saved data is versioned (`schemaVersion`) and upgraded on start-up.

## Notes / limits

* GGUF models only (llama.cpp). Vision models need their `mmproj` file next to the model (downloaded automatically with `get`); WAV/MP3/FLAC audio input works with audio-capable projectors (not tested with a real audio model).
* Follow-up turns of a conversation with images are served from the prompt cache (pictures count as stand-in tokens; an image is reused whole or not at all). Models with M-RoPE vision (Qwen2-VL family) still re-evaluate the prompt.
* CPU-vs-GPU backend choice (`config backend auto|vulkan|cpu`) is process wide; per-model CPU/GPU placement is
  controlled with `--cpu`, `--gpu`, `--gpu-layers`.
* The prompt/KV cache keeps several conversations per loaded model (`cacheSlots`, default parallel requests + 2): alternating between chats reuses each one's evaluated prompt, and caches can be saved to disk (`POST /api/models/cache/save`, `promptCache.autoSave`). Idle caches share the context window and are recycled oldest first.
* Tool calling is implemented by prompting + JSON parsing, so quality depends on the model.

## Anthropic-compatible server

The same server also speaks the Anthropic Messages API, so Anthropic SDKs and tools work against local models:

```
export ANTHROPIC_BASE_URL=http://localhost:1234
export ANTHROPIC_API_KEY=anything        # or the key set with `config apikey`
```

Supported: `POST /v1/messages` (non-streaming and SSE streaming with `message_start` / `content_block_delta` /
`message_delta` / `message_stop`), `system` as string or blocks, `stop_sequences`, `top_k`, tool definitions with
`tool_use` / `tool_result` blocks, and `POST /v1/messages/count_tokens`. Model names that aren't loaded
(e.g. `claude-sonnet-4`) are mapped to the currently loaded model. Toggle with `config anthropic on|off`
or the checkbox in Server & Settings.

## Tests

```
dotnet test tests/LLMHost.Tests                                     # unit + API contract tests (no model needed)
$env:LLMHOST_TEST_MODEL = "C:\path\to\small-chat-model.gguf"; dotnet test tests/LLMHost.Tests --filter "Category=Integration"
```

The unit and contract tests cover the GGUF parser, text/think scanner, schema-to-grammar, tool-call formats, OpenAI/Anthropic
mapping, the memory estimator, document extraction/retrieval, download resume with SHA-256 verification, authentication and
the HTTP API shapes. The integration tests (generation, prompt cache reuse, parallel batching, stop strings, logprobs) run
only when `LLMHOST_TEST_MODEL` is set; CI downloads Qwen2.5-0.5B for them (`.github/workflows/ci.yml`).
Conventions (which kind of test goes where, shared helpers): [tests/README.md](tests/README.md).
`tests/fixtures/mcp-test-server.ps1` is a tiny stdio MCP server for trying the MCP host by hand.
