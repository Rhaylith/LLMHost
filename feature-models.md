# Models: discovery, downloads, library

How models are found, downloaded, stored and kept up to date. Design background: [implementation-plan.md](implementation-plan.md) §3. Back to the [README](README.md).

## Discover

Web UI → **Discover**; CLI `catalog`, `search`, `get`.

* **Curated catalog** (`Models/catalog.json`, embedded; `Models/Catalog.cs` validates it). `settings.catalogUrl` can point to a newer copy (`POST /api/models/catalog/refresh`, ETag revalidation, a bad list is refused). It is empty by default, so no remote refresh happens.
* **Hugging Face search:** `GET /api/models/search` with `q`, `sort` (trending, downloads, likes, updated), `family`, `pipeline`, `vision`, `tools`, `reasoning`, `limit`, `cursor`. Capability filters are guesses from tags and names.
* **File list per repository:** `GET /api/models/files` with `quant`, `minSize`, `maxSize`, `fits=gpu|ram`; each file reports `fitsGpu` / `fitsRam` (approximate, 85 % rule).
* The UI remembers the filters and pages with "Load more".

## Model card and badges

`Models/ModelCard.cs`. Badges (vision, audio, tools, reasoning, MoE, embedding, rerank) show in Discover and My Models. A detail drawer shows the README, licence (with a warning for restricted or non-commercial licences), base model and, for downloaded models, an architecture table (`GET /api/models/info?id=`). Remote README: `GET /api/models/card?repo=` (cached 24 h, HTML-escaped before display). Badges of remote models are tag-based guesses; downloaded ones come from the GGUF header.

## Downloads

`Models/DownloadManager.cs`. Resumable background downloads with a queue and concurrency limit, pause/resume, retry, split shards, disk-space check and SHA-256 verification. Jobs survive restarts. You can download by catalog key, `user/model`, a pasted Hugging Face link or a URL.

* Web UI: Discover and the downloads list. CLI: `get`, `downloads`, `pause`, `resume`, `cancel`.
* Finished and failed downloads raise a [notification](feature-desktop-app.md#notifications).

### Gated models

A 401/403 from Hugging Face becomes a `needs-access` state with an `accessUrl` (API answer: 403 `gated`). The UI shows "Open on Hugging Face" and "Retry". Settings has a token field with a **Check** button (`POST /api/hf/whoami`); CLI `login --hf-token`. The token is stored encrypted with the Windows user's DPAPI (`Core/SecretStore.cs`).

## Library and folders

`Models/ModelLibrary.cs`: `publisher/repo/file.gguf` layout, GGUF metadata (architecture, quant, context, embedding detection), capabilities. LoRA adapters are listed separately.

`Models/ModelDirectories.cs`:

* `settings.modelsDirectory` is the main folder and download target; `settings.extraModelsDirectories` adds more. The first folder wins for a duplicate id.
* `GET /api/models/disk` reports usage per folder, free space, stale `.part` bytes and the largest models; `POST /api/models/disk/clean-partials` removes unused partials.
* `POST /api/models/directory/add|remove` and `POST /api/models/directory/move {to, mode: move|copy|switchOnly}` (background job with progress and cancel). A move refuses a loaded model, running downloads, a target inside the source and too little free space; cross-drive moves copy first and delete the original only after checking the length.
* UI: the "Model folders and disk" card in Settings. CLI: `models dir [list|add|remove|move [--copy]|switch|clean]`. Changing folders needs the admin permission.
* Import existing files: `import` (copy, move or link into the library).

## Update check

`Models/ModelUpdates.cs`. Each Hugging Face download writes `<file>.llmhost.json` with the SHA-256 of each part. `POST /api/models/updates/check` compares those with the repository (current / update / missing / unknown, renamed repositories noticed); `POST /api/models/updates/apply` downloads the newer file to `.part`, verifies it and only then replaces the old file. `settings.checkModelUpdates` (off by default) checks five minutes after start and then daily. UI: "Check for updates" in My Models with a badge per model. Files downloaded earlier or imported show *unknown*.

## Per-model defaults and load estimate

The gear dialog in My Models stores load settings and inference defaults per model (`Core/ModelDefaults.cs`); they apply at the next load. Before a load, `/api/models/estimate` reports whether the model fits (weights, KV cache, compute buffers, MoE, shared memory of integrated GPUs). See [feature-inference.md](feature-inference.md).
