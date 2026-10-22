# Command line

Design background: [implementation-plan.md](implementation-plan.md) §7. Back to the [README](README.md).

`llmhost <verb> …` runs one command; `llmhost cli` opens the interactive shell (`Cli/Shell.cs`), where every verb also works with a leading `/`. `llmhost help [verb]` lists everything. The CLI talks to the running daemon or server when there is one.

## Global flags

`Cli/Out.cs` reads these before the verb runs:

| Flag | Effect |
|---|---|
| `--json` | Results as JSON on stdout. Everything meant for people (info lines, errors) goes to stderr. Tables become arrays of objects with lower-cased column names as keys; `hardware`, `daemon status`, `ps`, `ls` have explicit documents. |
| `--quiet`, `-q` | No informational lines; tables and errors stay. |
| `--no-color` | Plain output. |
| `--verbose` | Log lines on stderr. |

Verbs that print free text (for example `fit`, parts of `runtime list`) still print text, to stderr in JSON mode. Streaming verbs such as `log stream --source request --json` print one JSON document per line. Golden files in `tests/LLMHost.Tests/Golden/` pin the key sets of `hardware`, `preset` and `downloads`.

## Exit codes

`Cli/ExitCodes.cs`:

| Code | Meaning |
|---|---|
| 0 | ok |
| 1 | generic failure |
| 2 | usage error, or not available (`push`, `dev`) |
| 3 | not found (model, preset, file) |
| 4 | not authorised (bad or missing token) |
| 5 | server unreachable |
| 6 | out of memory / the model does not fit |
| 7 | cancelled |
| 8 | conflict / already exists / already running |

## Verbs by area

| Area | Verbs |
|---|---|
| Models | `catalog`, `search`, `get` (alias `clone`), `downloads`, `pause`, `resume`, `cancel`, `ls`, `import`, `models dir …`, `fit`, `estimate` |
| Running | `load` (with the options in [feature-inference.md](feature-inference.md)), `unload`, `ps`, `chat`, `complete`, `embed`, `rerank`, `template` |
| Server | `serve`, `server start\|stop\|status`, `daemon up\|down\|status\|update`, `autostart`, `token`, `config`, `mcp` |
| Data | `preset …`, `chats`, `history`, `/save`, `/resume`, `/export` |
| System | `hardware`, `runtime …`, `log`, `update check\|install`, `login`, `version` |
| Linking | `link enable\|disable\|status\|set-device-name\|set-preferred-device\|pair\|peers\|unpair\|permissions` ([feature-linked-devices.md](feature-linked-devices.md)) |

## Details

* **`login --hf-token <token>`** (or a bare token, or `HF_TOKEN`) checks the token with Hugging Face, stores it encrypted and prints the account name. A rejected token exits 4, an unreachable Hugging Face 5, `--force` stores it unchecked.
* **`push` and `dev`** are reserved: they explain why they do not apply (no hub, no plugin system) and exit 2.
* **`update check|install`** uses the update feed, see [feature-desktop-app.md](feature-desktop-app.md#updates-and-packaging).
* **Daemon:** `daemon up` starts the server as a background process, a startup task or the Windows service; `daemon update` hands over to the updater.
* Opening a `.llmpreset` file runs `llmhost preset import "<file>"`.
