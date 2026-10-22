# Desktop application

The window, appearance, onboarding, updates and notifications. Design background: [implementation-plan.md](implementation-plan.md) §9. Back to the [README](README.md).

`llmhost` starts the server and shows the web UI in its own window (embedded WebView2, `Desktop/MainWindow.cs`) with a tray icon, optional auto-start, a single running instance and a remembered window size. `--no-window` runs headless. The same UI works in any browser at `http://localhost:1234/`.

## Interface modes

`settings.uiMode` = `user | power | developer` (default `power`). Switch in the sidebar footer. `Web/ui/features.js` holds one table mapping each control to its lowest mode; CSS hides what is above the current mode, never disables it. While the Settings search box has text, every setting is shown.

| Mode | Adds |
|---|---|
| User | Chat, models, discover, temperature / top-p / max tokens, system prompt, shortcuts |
| Power | Other samplers, presets editor, per-model settings (draft model, threads, GPU share, KV types, MoE, LoRA), split view, live load, linked devices, loading on demand, model folders, runtime manager |
| Developer | Server and application settings, network and limits, API tokens, MCP, the API sample, Requests and Logs, raw template editor |

The CLI is not affected. New options register in the table: [docs/adding-options.md](docs/adding-options.md).

## Themes and language

* `settings.uiTheme` light | dark | system, `uiAccent` (`#rrggbb`), `uiLanguage` (`auto` or a code). Settings → Appearance. All colours are CSS variables in `Web/app.css`; a script in `<head>` applies the stored theme before first paint. The window follows too (WebView2 colour scheme, dark title bar). A test requires 4.5 : 1 contrast for every text/background pair of both palettes.
* Translations: `Web/locales/en.json` and `de.json`. Elements carry `data-i18n`, `data-i18n-title` or `data-i18n-placeholder`; scripts call `t('key', {n})`. Missing keys fall back to English. `LocaleTests` checks that used keys exist and that both files match. The help page, per-parameter tooltips, and server and CLI messages stay English.

## First-run wizard

`Core/Onboarding.cs`, `Web/ui/onboarding.js`. Opens by itself on an installation with no models and no chats; start it again from Settings → Appearance. Steps: welcome and interface mode; hardware survey (CPU, memory, GPUs, instruction sets, Vulkan, recommended runtime); models folder with free-space warning; recommended small, medium and vision models that fit the memory budget, one click each to download; optional Hugging Face token; finish (loads the first model, opens the chat). Sizes are estimated from the catalog's parameter labels. API: `GET /api/onboarding`, `POST /api/onboarding/complete|reset`.

## Updates and packaging

`Core/ReleaseFeed.cs`, `Core/UpdateService.cs`, `Core/UpdateApply.cs`, `Updater/`.

* **Feed:** your own address in `updateFeedUrl` (there is no built-in one): a GitHub Releases list or a `releases.json`. `settings.update.channel` is `stable` or `beta`. A win-x64 `.zip` is preferred, then `.msi`, then a setup `.exe`. The SHA-256 comes from the asset or a `SHA256SUMS` file.
* **Verification:** https only; the hash must match (without one the package cannot be installed from the app); installers need a valid Authenticode signature (`update.requireSignature`). A failing file is deleted.
* **Install:** `LLMHost.Updater.exe` (built next to `llmhost.exe`) is copied to a temp folder and waits for the app to exit. It unpacks, keeps replaced files in `<install>.backup`, copies over, restarts the program and, if it does not stay up for 8 s or any step fails, restores the old files. Window, daemon and Windows service modes are handled.
* **Surfaces:** Settings → Updates, tray/window prompt, CLI `update check|install`, API `GET /api/update`, `POST /api/update/check|install`.
* **Packaging:** `pwsh build\publish.ps1 -Version x.y.z` makes the single-file win-x64 zip and `SHA256SUMS` in `artifacts\` (signs with `SIGN_CERT_PFX` if set). `installer/Package.wxs` (MSI) and `.github/workflows/release.yml` are written but have not been run in CI.

## Notifications

`Core/Notifications.cs` (`NotificationHub`: the last 100 events, per-kind switches and a master switch). Sources: finished or failed downloads, models that took at least `notifications.loadSeconds` (10) to load, a new update (once per version), and answers that finish while the page is not in front (reported by the page through `POST /api/notifications`, which accepts only the `generation` and `test` kinds). `GET /api/notifications?since=` returns new events.

* The window shows tray balloons (only when minimised if `onlyWhenMinimised`); a click opens the related view. A browser tab uses the Notifications API after permission (`Web/ui/notifications.js`).
* Settings → Notifications: switches per kind, "only when minimised", load threshold, test button.
