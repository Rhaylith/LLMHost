# Adding an option (load or generation setting)

Every new knob follows the same path. Most of it is automatic because the option classes are plain objects that are
serialised whole; only the places marked **manual** need code. Work through the list top to bottom — each step names the file.

Two kinds of option:

* **Load option** — how a model is put in memory (`LoadOptions`: context size, GPU layers, …). Changing it needs a reload.
* **Generation option** — per request (`GenerationOptions`: temperature, samplers, …). Changing it never reloads.

## Checklist

| # | Step | Load | Generation | How |
|---|------|:----:|:----------:|-----|
| 1 | **Declare** the property with a doc comment and a default that keeps old behaviour | ✔ | ✔ | [Core/Settings.cs](../Core/Settings.cs) — `LoadOptions` / `GenerationOptions`. Public get/set, camelCase on the wire. Use `[JsonIgnore]` for computed helpers. |
| 2 | **Clone** reference types (lists, dictionaries) | ✔ | ✔ | Same file, `Clone()`: `MemberwiseClone` shares references. A guard test (`OptionsPlumbingTests`) fails if you forget. |
| 3 | **Validate** ranges / allowed values | ✔ | ✔ | **manual** — [Core/SettingsValidator.cs](../Core/SettingsValidator.cs) `ValidateLoad` / `ValidateGeneration`. Field paths are the JSON paths (`defaultLoad.contextSize`). |
| 4 | **Use it** | ✔ | ✔ | Load: `ModelRuntime.LoadAsync` (and `FitEstimator` if it changes memory). Generation: `ModelRuntime.BuildSampler` / the scheduler. |
| 5 | **Request fields** (API) | | ✔ | **manual** — [Server/OpenAiMapping.cs](../Server/OpenAiMapping.cs) `ReadGeneration` (OpenAI names; for llama.cpp-specific knobs use the llama-server field name), then the Anthropic mapping ([Server/AnthropicApi.cs](../Server/AnthropicApi.cs)) and native API ([Server/NativeApi.cs](../Server/NativeApi.cs)) if the field makes sense there. |
| 6 | **Load request fields** (API) | ✔ | | Automatic for `POST /api/models/load` and `/api/models/defaults` (the body is deserialised into `LoadOptions`). **manual** for the native v1 API: [Server/NativeApi.cs](../Server/NativeApi.cs) `models/load`. |
| 7 | **Defaults, presets, chats** | ✔ | ✔ | Automatic — `ModelDefaultsStore`, `PresetStore` and `Conversation` serialise the whole object. If the *meaning* of stored data changes, add a migration step (see below). |
| 8 | **CLI** | ✔ | ✔ | `set <name> <value>` / `params` work through reflection ([Cli/PropertyEditor.cs](../Cli/PropertyEditor.cs)) — nothing to do. A dedicated `load` flag is **manual** in [Cli/Shell.cs](../Cli/Shell.cs) (`LoadAsync` flag parsing, the `help` text and the boolean-flag list at the end of the file). |
| 9 | **Web UI** | ✔ | ✔ | **manual** — [Web/index.html](../Web/index.html): control in the parameter panel (`p-<name>`) or load dialog (`l-<name>`), read/write it in the JS that builds the request, and add a tooltip entry in `TIPS`. |
| 10 | **Tests** | ✔ | ✔ | Unit test for the logic; extend `OptionsPlumbingTests` expectations if the option needs special handling; API contract test if a request field was added; model-backed test (`[ModelFact]`) only if behaviour needs a real model. |
| 11 | **Docs** | ✔ | ✔ | README feature table, CLI `help` text, and tick the item in `todo.md`. |

## Changing how data is stored

Settings, presets, per-model defaults and chats are versioned (`schemaVersion`). Adding a property with a sensible default
needs **no** migration. Renaming, removing or re-interpreting data does:

1. Append one step to `Migrations.Steps[<kind>]` in [Core/Migrations.cs](../Core/Migrations.cs) (step *i* upgrades version *i* to *i + 1*).
2. Add a test next to `MigrationTests` with a file in the old shape.

The original file is kept as `<file>.bak-v<old>` before it is rewritten.

## Conventions

* Names: C# `PascalCase` property ↔ `camelCase` JSON. Booleans positive (`useMmap`, not `noMmap`).
* "Unset" is `null` (or `0` for counts where 0 is meaningless) and means *use the model / engine default*.
* Strings with a fixed set of values (`"auto" | "on" | "off"`) are validated in step 3, never silently coerced.
* A new option must not change behaviour for existing users unless they set it.
