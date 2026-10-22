# Server and APIs

The HTTP server (Kestrel, `Server/ApiServer.cs`). Design background: [implementation-plan.md](implementation-plan.md) §6. Back to the [README](README.md).

Start it with `llmhost` (window + server), `llmhost serve` (no window) or `llmhost daemon up` (background). Default port 1234; `--lan` makes it reachable from the network.

## Endpoints

| Family | Routes | Notes |
|---|---|---|
| OpenAI | `/v1/chat/completions`, `/v1/completions`, `/v1/responses`, `/v1/embeddings`, `/v1/rerank`, `/v1/models` | SSE streaming, tools, `n`, `logprobs`, images |
| Anthropic | `/v1/messages`, `/v1/messages/count_tokens` | See below |
| Native | `/api/v1/chat`, `/api/v1/models`, `/api/v1/models/load`, … | Per-token permissions |
| Legacy | `/api/v0/models`, `/api/v0/chat/completions`, `/completions`, `/embeddings` | For older clients (`Server/LegacyApi.cs`): same handlers as v1 plus `model_info` and `runtime`; same permissions |
| Operations | `/metrics`, `/api/requests` | Below |
| Linked devices | `/api/link/*` | [feature-linked-devices.md](feature-linked-devices.md) |

## API tokens and permissions

Settings → API tokens (Developer mode) or `llmhost token …`. Several named tokens, each with permissions; the secret is shown once. Send `Authorization: Bearer <token>` or `x-api-key`. `Server/ApiAuth.cs` maps each route to the permission it needs (chat, embeddings, models:read, admin …).

## Loading models on demand (JIT)

`settings.jit { enabled, defaultTtlMinutes, autoEvict, maxLoadedModels, evictOrder }` (Settings → Loading on demand; `ModelManager.JitLoadAsync`).

* A request for a model that is not loaded loads it. When `maxLoadedModels` is reached, or the memory check refuses, idle on-demand models are unloaded first (least recently used or oldest). Models you loaded by hand and busy models are never touched.
* If nothing can be unloaded the answer is HTTP 503 `model_load_blocked` with the reason.
* Idle time: the request's `ttl` (body field or `x-llmhost-ttl` header, seconds) beats the load setting, which beats `jit.defaultTtlMinutes`. `ttl=0` keeps the model.
* The loaded list shows an "on demand" tag and an "unloads in …" countdown.

## Network security

`Core/NetworkSecurity.cs`, `settings.network` (Settings → Network and limits).

* **Allow-list:** `loopback`, `private`, `any`, an address or CIDR (IPv4/IPv6). With no entries: loopback only, plus private ranges when listening on a non-loopback address. Others get 403.
* **Limits:** `maxBodyMb` (32) and `maxUploadMb` (256) → 413 `request_too_large`; a token-bucket rate limit per token and per address (429 with `Retry-After`; 0 = off); caps on `n` and on max tokens (400). Connection limits need a restart.
* **HTTPS:** `tls.mode` off | selfSigned | file. A self-signed ECDSA certificate (two years, SANs for localhost, the machine name and local addresses) is created in `~/.llmhost/tls` and renewed 30 days before it ends; `file` takes a `.pfx` or PEM pair. The HTTPS port is `tls.port`; the HTTP port keeps working. `GET /api/tls/cert` serves the certificate so clients can trust it.

## Request log and metrics

`Core/RequestLog.cs`. Every call to the model routes is recorded in a ring buffer (`logging.requestLogSize`, 2000): time, route, status, duration, model, token name, address, token counts, stream flag, error. Bodies are kept only with `logging.logBodies`, redacted and cut to 8 KB.

* UI: the **Requests** tab (filters, live refresh, detail drawer). CLI: `log stream --source request [--json] [--filter text]`.
* API: `GET /api/requests?model=&status=&since=&q=&limit=`, `/api/requests/{id}`, `/api/requests/stream` (SSE), `DELETE /api/requests`.
* `GET /metrics` (Prometheus text): request counts and duration histogram, token counters, tokens/s, loaded models, active requests, queue depth, KV cache tokens, GPU memory. Needs a token with `models:read` unless `metricsPublic` is on.

## Anthropic compatibility

`Server/AnthropicApi.cs`, `Server/AnthropicThinking.cs`.

```
export ANTHROPIC_BASE_URL=http://localhost:1234
export ANTHROPIC_API_KEY=anything
```

Supported: streaming events, `system` blocks, `stop_sequences`, `top_k`, image blocks (also inside tool results), `tool_choice` auto/any/tool/none with `disable_parallel_tool_use`, `thinking` (`budget_tokens` sets reasoning effort and a hard limit; thinking blocks carry an HMAC `signature`, echoed blocks are dropped from the prompt), `cache_control` accepted with `cache_read_input_tokens` / `cache_creation_input_tokens` in usage, `count_tokens`. Unloaded model names such as `claude-sonnet-4` map to the loaded model. Message Batches and the Files API do not exist (404). Toggle with `config anthropic on|off`.

## MCP host

`Core/Mcp.cs`, `Server/McpApi.cs`. `mcp.json` in Cursor notation with local (stdio) and remote (URL + headers) servers. Tools are listed in the chat UI with a per-call confirmation. CLI: `mcp`. `tests/fixtures/mcp-test-server.ps1` is a small test server.
