# Chat and presets

The Chat tab of the web UI. Design background: [implementation-plan.md](implementation-plan.md) §4 and §5. Back to the [README](README.md).

## How the page is built

`Web/index.html` is the markup, `Web/app.css` the style, and the scripts are split into `Web/ui/*.js` (classic scripts sharing one global scope, no build step). All are embedded in the executable and served from `GET /ui/{name}`. `UiPageTests` loads the page with every script.

## Chats

`Core/ChatLibrary.cs`. A chat has an optional folder, a pinned flag and an automatic title.

* **Folders:** nested, drag and drop of chats and folders (`/api/chat-folders`). Deleting a folder moves its chats up one level.
* **Chat actions** (context menu, `Web/ui/chats.js`): rename, pin, duplicate (with attachments), move, export, delete; Ctrl/Shift selection with bulk delete.
* **Search** across all chats (`GET /api/chats/search?q=`): an in-memory index over titles and messages; the UI jumps to the matching message.
* **Auto titles:** after the first answer the loaded model suggests a title (at most 16 tokens). `settings.autoTitleChats` turns it off; a title you typed is never replaced.
* **Export / import:** `GET /api/chats/{id}/export?format=json|markdown|text`; `POST /api/chats/import` reads our JSON, a message array or a ChatGPT `conversations.json`.
* **Editing:** edit a message and resubmit, delete a message, regenerate to make variants; variants form a branching tree (Alt+←/→ switches).
* CLI: `chats`, `/save`, `/resume`, `/export`, `/undo`, `/regen`.

## Attachments and documents

Images attach, paste or drop into the input (vision models). `.pdf`, `.docx`, `.txt` and `.md` go through `Core/Documents.cs`: the whole text goes into the context when it fits, otherwise it is chunked, embedded and the best chunks are retrieved (optionally re-ranked, see [feature-inference.md](feature-inference.md#embeddings-and-reranking)). API: `Server/DocumentsApi.cs`.

## Per-chat settings

`Core/SettingsResolver.cs` merges, in order, global settings, model default, preset and the chat's own changes. A chat stores only what it changed. The Parameters panel marks changed fields with ● and offers **Save as model default**, **Reset** and **Update preset**.

* API: `GET /api/effective-settings?chat=&model=&preset=` (merged values and the source of each), `POST /api/chats/{id}/settings/reset`, `POST /api/chats/{id}/settings/save-as {target: "preset" | "modelDefault", name}`.
* Requests to `/v1/chat/completions` still carry their own parameters.

## Split view and compare

The **⫴** button above the chat list (`Web/ui/panes.js`) opens compare mode: 1–4 panes, each with its own model, temperature, max tokens and system prompt, plus one shared input with a **send to all** switch. Panes on the same model run in parallel on the server; a model that is not loaded is loaded on demand after a memory estimate. A table compares tokens, tokens/s, time to first token and stop reason. **Keep this** saves a pane as a normal chat. Every pane is an ordinary chat in the list.

## Rendering

`Web/ui/render.js` with unmodified vendored libraries in `Web/vendor/` (marked, DOMPurify, highlight.js, KaTeX with inlined fonts; licences in `LICENSES.txt`). Nothing is fetched from the internet.

* GitHub-flavoured Markdown, tables, task lists, highlighted code with Copy, LaTeX (`$…$`, `$$…$$`, `\(…\)`, `\[…\]`), collapsible reasoning ("Thought for 12 s").
* Safety: HTML in an answer is escaped, then sanitised; links open in the default browser with `noopener`; remote images are not loaded; `renderSelfTest()` runs XSS payloads in the page.

## Token counter and message stats

`POST /api/context/usage` counts the conversation with the model's own template. A bar under the input shows context use (green below 70 %, amber below 90 %, red above; the overflow policy is named when nearly full). Each assistant message stores tokens, tok/s, time to first token, stop reason, cached tokens and draft acceptance, shown in a footer on hover.

## Keyboard shortcuts

`Core/Shortcuts.cs` defines 13 actions: `Ctrl+N` new chat, `Ctrl+Shift+N` new folder, `Ctrl+K` / `Ctrl+P` quick open, `Ctrl+,` settings, `Ctrl+Shift+R` runtime manager, `?` help, `Ctrl+L` focus input, `Ctrl+Enter` send, `Esc` stop, `Ctrl+Shift+C` copy last answer, `Alt+←/→` variants. Change or switch them off in Settings → Keyboard shortcuts (conflicts are refused). `GET /api/shortcuts` lists the effective keys.

## Presets

`Core/PresetFiles.cs`. A preset is a file `presets/<name>.json` with a description, system prompt and inference parameters (never load options). The folder is watched and the page polls `GET /api/preset-list` every 3 s, so editing a file on disk shows up live. The old `presets.json` was split into files on first start.

* UI: Save (as new), **Update preset**, **Reset to preset**, Duplicate, Export, Import.
* CLI: `preset save|load|rm|export|duplicate|import`.
* API: `/api/presets`, `/api/presets/{name}`, `/api/presets/{name}/duplicate`, `/api/preset-list`.

### Sharing

`Core/PresetShare.cs`. Export downloads `<name>.preset.json`. Import (`POST /api/presets/import/preview`, then `/import`) takes pasted text, a file, a dropped `.preset.json` / `.llmpreset` file or an https address (GitHub links are rewritten to raw; at most 256 KB; private and loopback addresses are refused at connect time). The preview shows what would change, including keys that are ignored; a name clash offers Overwrite or a free name. CLI: `preset import <file|url>`, `preset associate` registers `.llmpreset` for the current user.
