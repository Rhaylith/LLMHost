# Test conventions

```
dotnet test tests/LLMHost.Tests                                  # everything that needs no model
dotnet test tests/LLMHost.Tests --filter "Category=Unit"         # pure logic, milliseconds
dotnet test tests/LLMHost.Tests --filter "Category=Api"          # real HTTP server, no model loaded
$env:LLMHOST_TEST_MODEL = "C:\path\small-chat-model.gguf"
dotnet test tests/LLMHost.Tests --filter "Category=Integration"  # needs a real GGUF (Qwen2.5-0.5B in CI)
```

## Where a test goes

| Kind | Put it in | Mark with | Use when |
|------|-----------|-----------|----------|
| **Unit** | the file for the area (`UnitTests.cs`, `SchemaTests.cs`, `LoggingTests.cs`, `CapabilityTests.cs`, …) | `[Trait("Category", "Unit")]` on the class | pure logic: parsers, mapping, validation, migrations, string builders |
| **API contract** | `ApiTests.cs` or a new file with `IClassFixture<ServerFixture>` | `[Trait("Category", "Api")]` | an HTTP route, status code, JSON shape, auth. The fixture starts the real server on a free port with **no model loaded** |
| **Integration** | `IntegrationTests.cs` | `[Trait("Category", "Integration")]` + `[ModelFact]` | behaviour that needs a real model: generation, cache reuse, batching, sampling. Skipped (not failed) when `LLMHOST_TEST_MODEL` is unset |

Rules of thumb: prefer the cheapest kind that can show the behaviour; every new feature gets at least one test; every bug fix
gets a test that failed before the fix. A feature that needs a model also gets a unit test for its decision logic so the
default CI run covers it.

## Shared helpers (`TestSupport.cs`)

* `TestEnvironment` — points `LLMHOST_HOME` at a throw-away folder **before** anything reads settings, so tests never touch
  `~/.llmhost`. `TestEnvironment.Home` / `.Models` are the folders to write fixtures into.
* `GgufBuilder.Build(...)` / `.WriteFile(...)` — writes a tiny valid GGUF (architecture, sizes, chat template, experts,
  pooling type, `clip.*` projector keys…) so parsers, the memory estimator and capability detection are tested without
  real models. Extend its parameters when a feature needs another metadata key.
* `DocBuilder` — in-memory `.docx`/`.pdf` for the document tests.
* `ServerFixture` (`ApiTests.cs`) — a running `ApiServer` + `HttpClient`.

## Writing durable tests

* The settings object is process-wide (`AppSettings.Current`): restore anything you change, or use the throw-away `Home`.
* Files are shared between tests in a run — use unique names (`Guid`) for folders you create.
* No sleeps for synchronisation; await the thing or use a timeout on a task.
* Test names say the behaviour (`RollsOverWhenTheFileIsFull`), not the method.
* New option? `OptionsPlumbingTests` already checks clone / JSON / unknown-key handling for it — see
  [docs/adding-options.md](../docs/adding-options.md).
