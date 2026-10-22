using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using LLMHostProject.Server;

namespace LLMHostProject.Cli;

/// <summary>
/// In-process command line interface. Every command works both as a sub-command
/// (<c>llmhost get qwen2.5-0.5b</c>) and as a slash command inside the interactive session (<c>/get qwen2.5-0.5b</c>).
/// </summary>
public class Shell
{
    private readonly AppHost _host;
    private readonly ApiServer _server;
    private readonly GenerationOptions _gen;
    private readonly LoadOptions _load;
    private readonly List<ChatMessage> _history = new();
    private Conversation _conversation = new();
    private string? _activeModel;
    private CancellationTokenSource? _genCts;
    private bool _quit;
    private readonly List<byte[]> _pendingImages = new();

    public Shell(AppHost host)
    {
        _host = host;
        _server = new ApiServer(host);
        _gen = host.Settings.DefaultGeneration.Clone();
        _load = host.Settings.DefaultLoad.Clone();
    }

    // ===================================================================== entry points

    public async Task<int> RunInteractiveAsync(string? initialModel = null)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            if (_genCts is { IsCancellationRequested: false }) { e.Cancel = true; _genCts.Cancel(); }
        };

        Ui.Info(Ui.Bold("LLMHost LLM runner") + Ui.Dim("  — type a message to chat, /help for commands, /quit to exit"));
        var hw = Hardware.Detect();
        var gpu = hw.BestGpu;
        Ui.Info(Ui.Dim($"CPU: {hw.Cpu} ({hw.LogicalCores} threads)  RAM: {Ui.Bytes(hw.TotalRamBytes)}"));
        Ui.Info(Ui.Dim(gpu != null
            ? $"GPU: {gpu.Name} ({(gpu.UsableBytes > 0 ? Ui.Bytes(gpu.UsableBytes) : "VRAM unknown")}{(gpu.Integrated && gpu.SharedBytes > 0 ? ", incl. shared" : "")})  backend: {_host.Settings.Backend}"
            : "GPU: none detected — running on CPU"));
        foreach (var n in hw.Npus)
            Ui.Info(Ui.Dim($"NPU: {n.Name} ({n.Vendor} {n.Architecture}) — detected, not used for inference"));

        if (initialModel != null) await DispatchAsync("load", new[] { initialModel });
        else if (_host.Library.List().Count == 0)
            Ui.Info(Yellow("No models yet. Try: /catalog, then /get qwen2.5-0.5b"));

        while (!_quit)
        {
            Console.Write(Ui.Cyan(_activeModel != null ? $"{_activeModel}> " : "> "));
            string? line = Console.ReadLine();
            if (line == null) break;
            var sb = new StringBuilder(line);
            while (sb.Length > 0 && sb[^1] == '\\')
            {
                sb.Length--;
                sb.Append('\n');
                Console.Write(Ui.Dim("... "));
                var more = Console.ReadLine();
                if (more == null) break;
                sb.Append(more);
            }
            string input = sb.ToString().Trim();
            if (input.Length == 0) continue;

            try
            {
                if (input.StartsWith('/'))
                {
                    var parts = Tokenize(input[1..]);
                    if (parts.Count == 0) continue;
                    await DispatchAsync(parts[0], parts.Skip(1).ToArray());
                }
                else
                {
                    await ChatAsync(input);
                }
            }
            catch (OperationCanceledException) { Ui.Info(Ui.Dim("\n[cancelled]")); }
            catch (Exception ex) { Ui.Error(ex.Message); }
        }
        await _server.StopAsync();
        return 0;
    }

    private static string Yellow(string s) => Ui.Yellow(s);

    public async Task<int> RunCommandAsync(string command, string[] args)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            if (_genCts is { IsCancellationRequested: false }) { e.Cancel = true; _genCts.Cancel(); }
        };
        try
        {
            return await DispatchAsync(command, args) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }
    }

    // ===================================================================== dispatch

    public async Task<bool> DispatchAsync(string cmd, string[] args)
    {
        var a = new Args(args);
        switch (cmd.ToLowerInvariant())
        {
            case "help": case "h": case "?": PrintHelp(); return true;
            case "quit": case "exit": case "q": _quit = true; return true;
            case "hardware": case "hw": return Hardware_();
            case "models": case "ls": case "list": return ListModels(a);
            case "catalog": case "cat": return ShowCatalog(a);
            case "search": return await SearchAsync(a);
            case "files": return await FilesAsync(a);
            case "get": case "download": case "pull": return await GetAsync(a);
            case "downloads": case "dl": return ShowDownloads();
            case "cancel": return Cancel(a);
            case "pause": return Pause(a, pause: true);
            case "resume-download": case "continue": return Pause(a, pause: false);
            case "import": return ImportModel(a);
            case "fit": case "estimate": return Fit(a);
            case "runtime": case "runtimes": return await RuntimeCommandAsync(a);
            case "token": case "api-token": return TokenCommand(a);
            case "mcp": return await McpCommandAsync(a);
            case "daemon": return await DaemonCommandAsync(a);
            case "claude": case "claude-code": return await ClaudeCodeCommandAsync(a);
            case "update": return await UpdateCommandAsync();
            case "version": Ui.Info("llmhost " + LLMHostProject.Desktop.UpdateChecker.CurrentVersion); return true;
            case "rm": case "delete": return Remove(a);
            case "load": return await LoadAsync(a);
            case "unload": return Unload(a);
            case "ps": case "loaded": return Ps(a);
            case "use": case "model": return Use(a);
            case "params": case "show": return ShowParams();
            case "set": return SetParam(a);
            case "reset": ResetParams(); return true;
            case "system": return SetSystem(a);
            case "preset": case "presets": return Preset(a);
            case "config": case "settings": return Config(a);
            case "save-defaults": SaveDefaults(); return true;
            case "defaults": return ModelDefaultsCommand(a);
            case "serve": return await ServeAsync(a);
            case "server": return await ServerVerbAsync(a);
            case "stop": await _server.StopAsync(); return true;
            case "logs": case "log":
                if (a.Positional.FirstOrDefault() is "stream" or "follow" || a.Flag("follow") || a.Flag("f")) return await LogStreamAsync(a);
                if (a.Positional.FirstOrDefault() is "level") return LogLevelCommand(a);
                if (a.Positional.FirstOrDefault() is "path" or "dir") { Ui.Info(LogBuffer.Sink.Directory); return true; }
                return Logs(a);
            case "clear": case "new": NewConversation(); Ui.Info(Ui.Dim("Conversation cleared.")); return true;
            case "history": return ShowHistory();
            case "chats": return ListChats();
            case "resume": return Resume(a);
            case "save": _host.Chats.Save(_conversation); Ui.Info($"Saved chat {_conversation.Id}"); return true;
            case "undo": return Undo();
            case "regen": case "retry": return await RegenAsync();
            case "export": return Export(a);
            case "complete": return await CompleteAsync(a);
            case "embed": return await EmbedAsync(a);
            case "rerank": return await RerankAsync(a);
            case "tokens": return await TokensAsync(a);
            case "cache": return await CacheAsync(a);
            case "image": case "img": return AttachImage(a);
            case "video": return await AttachVideoAsync(a);
            case "audio": return AttachAudio(a);
            case "template": return ShowTemplate(a);
            case "chat": return await ChatCommandAsync(a);
            default:
                Ui.Error($"unknown command '{cmd}'. Type /help.");
                return false;
        }
    }

    // ===================================================================== help

    private static void PrintHelp()
    {
        const string text = """
            Models
              ls [--json]                list downloaded models
              models dir [add|remove|move|switch|clean]   library folders, disk usage, move or copy the models folder
              catalog [filter]           curated list of major model families
              search <query>             search Hugging Face for GGUF models
              files <repo|key>           list quantizations in a repository
              get [search term]          interactive picker: search Hugging Face, choose a model and a quantization
              get <repo|key|link|url> [quant|file] [--bg]   download a model (resumable, SHA-256 verified).
                                         accepts user/model[:quant], user/model/file.gguf, a huggingface.co link or a .gguf URL
              downloads / cancel <id>    show or cancel downloads;  pause <id> / continue <id>
              import <file.gguf> [--copy|--move|--link] [--publisher p] [--name n]   add an existing .gguf file to the library
              rm <model>                 delete a downloaded model
            Running
              load <model> [--cpu|--gpu max|auto|off|0.5] [--gpu-layers N] [--identifier name] [--context-length N] [--ctx N] [--threads N (default: physical cores)] [--batch-threads N] [--cpu-affinity 0-7|node:1|physical] [--thread-priority normal|high|low] [--batch N]
                           [--flash] [--no-mmap] [--mlock] [--kv-k q8_0] [--kv-v q8_0] [--no-vision] [--mmproj <file>] [--draft <small model>] [--draft-tokens 6] [--template-file chat.jinja]
                           [--another] [--pooling mean/cls/last/rank/none] [--no-normalize] [--embed-batch N] [--lora "adapter.gguf=0.8;other.gguf"] [--cpu-moe | --n-cpu-moe N] [--moe-experts N] [--override-tensor "regex=CPU;regex2=CPU"]
                           [--id name] [--ttl minutes] [--embeddings]
                           [--gpus 0,1] [--split 3,1] [--split-mode layer|row|none]   multi-GPU: devices (priority order), shares, mode
                           [--parallel N]   requests generated at the same time (continuous batching, default 4)
              token list|create <name> [--perm a,b]|revoke <id>   API tokens with per-token permissions (config auth on to enforce)
              mcp list|tools|call <server> <tool> [json]|reload   MCP servers of mcp.json (local stdio and remote URL)
              runtime list|select <id|auto>|install <zip|url|folder>|update|remove <id>   llama.cpp builds (CPU AVX variants, Vulkan)
              fit <model> [same load flags]   memory estimate (weights + KV cache + compute) before loading;  --skip-fit-check overrides the guardrail
              unload [id|--all]  ps [--json]   unload / list loaded models
              use <id>                  switch the active chat model
              hardware                  CPU / GPU / backend information
            Chat
              <text>                    send a message to the active model (end a line with \ to continue)
              /system <prompt>          set the system prompt
              /image <file> [...]       attach image(s) to the next message (vision models)
              /video <file> [--fps 1] [--frames 8]   attach frames of a video (needs ffmpeg) to the next message
              /audio <file> [...]       attach WAV / MP3 / FLAC audio to the next message (audio-capable models)
              rerank <query> <doc> [<doc> ...]   order documents by relevance (reranker or embedding model)
              template [model]           show the chat template stored in a model file and a sample prompt
              /clear  /history  /undo  /regen  /save  /chats  /resume <id>  /export <file>
              /cache [on|off|clear]     prompt/KV cache: reuse the evaluated prompt prefix between turns
              /complete <text>          raw (non-chat) completion
              /embed <text>  /tokens <text>
            Parameters
              params                    show generation + load parameters
              set <name> <value>        e.g. set temperature 0.3 | set topk 20 | set stop "###"
              set load.<name> <value>   defaults for the next /load e.g. set load.ctx 8192
              reset  preset save|load|list|delete <name>  save-defaults
              defaults [show|save|clear] <model>   per-model default load + inference settings (applied by UI, API and CLI)
            Server
              serve [--host 0.0.0.0] [--port 1234] [--model <m>]   headless OpenAI/Anthropic API + web UI (HTTP only)
              (running `llmhost` with no arguments = serve + show the web UI in its own window; `llmhost cli` = this shell)
              server start|stop|status [--json]   control the background server (daemon); also: daemon up|down|status|update|install|uninstall
              chat [model] [--server url] [--token t]   terminal chat; against the running daemon when there is one
              log stream                follow the daemon log live
              log level [lvl] [--native lvl]   show / set how much goes to the log files (trace|debug|info|warn|error|off); log path = folder
              claude [folder] [--model id]   open PowerShell with Claude Code using the running server and a loaded model
              stop                      stop the server (in this shell)
              config [key] [value]      backend, port, host, apikey, modelsdir, hftoken, cors, anthropic
              logs [n]
            """;
        Ui.Info(text);
    }

    // ===================================================================== models

    private bool Hardware_()
    {
        var hw = Hardware.Detect();
        Ui.Info($"CPU      {hw.Cpu}  ({hw.LogicalCores} logical cores)");
        Ui.Info($"RAM      {Ui.Bytes(hw.TotalRamBytes)}");
        foreach (var g in hw.Gpus)
        {
            var dev = GpuDevices.For(g);
            Ui.Info($"GPU {g.Index}    {g.Name}  [{g.Vendor}{(g.Integrated ? ", integrated" : "")}]  VRAM {(g.VramBytes > 0 ? Ui.Bytes(g.VramBytes) : "unknown")}{(g.Integrated && g.SharedBytes > 0 ? " + " + Ui.Bytes(g.SharedBytes) + " shared" : "")}  driver {g.Driver}" +
                    (dev != null ? Ui.Dim($"  → backend {dev.Name} (#{dev.GpuOrdinal}), {Ui.Bytes(GpuDevices.CurrentFreeBytes(dev))} free") : ""));
        }
        foreach (var n in hw.Npus)
            Ui.Info($"NPU      {n.Name}  [{n.Vendor} {n.Architecture}]  driver {(n.Driver != "" ? n.Driver : "unknown")}  status {n.Status}  " + Ui.Dim("(detected only; not used for inference)"));
        Ui.Info($"Backend  requested={_host.Settings.Backend}" + (NativeBackend.SystemInfo != "" ? $" active={NativeBackend.Active}" : " (initialises on first model load)"));
        if (NativeBackend.SystemInfo != "") Ui.Info(Ui.Dim(NativeBackend.SystemInfo));
        Ui.Info(Ui.Dim($"Models   {_host.Settings.ResolvedModelsDirectory}"));
        return true;
    }

    /// <summary>models dir [list | add &lt;path&gt; | remove &lt;path&gt; | move &lt;path&gt; [--copy] | switch &lt;path&gt; | clean]</summary>
    private bool ModelsDir(Args a)
    {
        string sub = a.Positional.Count > 1 ? a.Positional[1].ToLowerInvariant() : "list";
        string? path = a.Positional.Count > 2 ? a.Positional[2] : null;
        var dirs = _host.Directories;
        switch (sub)
        {
            case "list": case "ls":
            {
                var r = dirs.Report();
                Ui.Table(r.Roots.Select(x => new[] { x.Path, x.Role == "download" ? "main" : "extra", x.ModelCount.ToString(), Ui.Bytes(x.ModelBytes), Ui.Bytes(x.FreeBytes) + " free", x.Exists ? "" : Ui.Red("missing") }),
                    "FOLDER", "ROLE", "MODELS", "SIZE", "DRIVE", "");
                foreach (var m in r.Largest.Take(5)) Ui.Info(Ui.Dim($"  {Ui.Bytes(m.Bytes),10}  {m.Id}"));
                if (r.Partials.Count > 0) Ui.Info(Ui.Yellow($"{r.Partials.Count} leftover partial download(s), {Ui.Bytes(r.Partials.Sum(p => p.Bytes))}: 'models dir clean' removes them."));
                return true;
            }
            case "add": dirs.AddRoot(path ?? throw new ArgumentException("models dir add <path>")); Ui.Info("Added."); return true;
            case "remove": case "rm": dirs.RemoveRoot(path ?? throw new ArgumentException("models dir remove <path>")); Ui.Info("Removed."); return true;
            case "clean": { var (n, b) = dirs.CleanPartials(); Ui.Info($"Deleted {n} file(s), {Ui.Bytes(b)}."); return true; }
            case "move": case "switch":
            {
                string mode = sub == "switch" ? "switchOnly" : a.Flag("copy") ? "copy" : "move";
                var job = dirs.StartMove(path ?? throw new ArgumentException($"models dir {sub} <path>"), mode);
                while (job.State == "running") { Console.Write($"\r{job.Progress * 100:0.0}% {job.FilesDone}/{job.Files} files   "); Thread.Sleep(300); }
                Console.WriteLine();
                if (job.State != "completed") { Ui.Error(job.Error ?? job.State); return false; }
                Ui.Info($"The main folder is now {job.To}.");
                return true;
            }
            default: Ui.Error("models dir [list | add <path> | remove <path> | move <path> [--copy] | switch <path> | clean]"); return false;
        }
    }

    private bool ListModels(Args a)
    {
        if (a.Positional.FirstOrDefault()?.Equals("dir", StringComparison.OrdinalIgnoreCase) == true) return ModelsDir(a);
        if (a.Flag("json"))
        {
            var loadedJson = _host.Models.Loaded;
            Ui.Info(JsonSerializer.Serialize(_host.Library.List().Select(m => new
            {
                m.Id, m.FileName, m.SizeBytes, m.Architecture, m.Quantization, m.ContextLength, m.IsEmbedding,
                vision = m.SupportsVision, capabilities = m.Capabilities.Names(), loaded = loadedJson.Any(l => l.Model.Path == m.Path),
            }), Json.Options));
            return true;
        }
        var loaded = _host.Models.Loaded;
        var rows = _host.Library.List().Select(m => new[]
        {
            m.Id, Ui.Bytes(m.SizeBytes), m.Architecture, m.Quantization ?? "", m.ContextLength > 0 ? m.ContextLength.ToString() : "",
            string.Join(",", m.Capabilities.Names()), loaded.Any(l => l.Model.Path == m.Path) ? Ui.Green("loaded") : "",
        }).ToList();
        if (rows.Count == 0) { Ui.Info($"No models in {_host.Settings.ResolvedModelsDirectory}. Use 'catalog' and 'get'."); return true; }
        Ui.Table(rows, "MODEL", "SIZE", "ARCH", "QUANT", "CTX", "TYPE", "STATE");
        return true;
    }

    private bool ShowCatalog(Args a)
    {
        string? filter = a.Positional.FirstOrDefault();
        var matches = Catalog.Entries
            .Where(e => filter == null || (e.Family + e.Name + e.Key + string.Join(' ', e.Tags)).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var (title, vision) in new[] { ("LLMs", false), ("Image models (vision)", true) })
        {
            var rows = matches.Where(e => e.IsVision == vision)
                .Select(e => new[] { e.Key, e.Family, e.Name, e.Params, e.RecommendedQuant, string.Join(',', e.Tags) }).ToList();
            if (rows.Count == 0) continue;
            Ui.Info(title);
            Ui.Table(rows, "KEY", "FAMILY", "NAME", "PARAMS", "QUANT", "TAGS");
        }
        Ui.Info(Ui.Dim("Download with: get <key>   (e.g. get qwen2.5-0.5b)"));
        return true;
    }

    private async Task<bool> SearchAsync(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Error("usage: search <query>"); return false; }
        var found = await HuggingFace.SearchAsync(string.Join(' ', a.Positional), 25);
        Ui.Table(found.Select(m => new[] { m.Id, m.Downloads.ToString("N0"), m.Likes.ToString("N0") }), "REPOSITORY", "DOWNLOADS", "LIKES");
        return true;
    }

    private static string ResolveRepo(string keyOrRepo) => Catalog.Find(keyOrRepo)?.Repo ?? keyOrRepo;

    private async Task<bool> FilesAsync(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Error("usage: files <repo|catalog-key>"); return false; }
        string repo = ResolveRepo(a.Positional[0]);
        var files = await HuggingFace.ListFilesAsync(repo);
        if (files.Count == 0) { Ui.Info("No GGUF files found."); return true; }
        var hw = Hardware.Detect();
        long vram = hw.BestGpu?.UsableBytes ?? 0;
        Ui.Table(files.Select(f => new[]
        {
            f.Quant, Ui.Bytes(f.SizeBytes), f.FileName + (f.IsSplit ? $" (+{f.Shards.Count - 1} shards)" : "") + (f.HasVision ? Ui.Cyan(" [vision +" + Ui.Bytes(f.MmprojSize) + "]") : ""),
            vram > 0 && f.SizeBytes < vram * 0.85 ? Ui.Green("fits GPU") : f.SizeBytes < hw.TotalRamBytes * 0.8 ? "fits RAM" : Ui.Red("too large"),
        }), "QUANT", "SIZE", "FILE", "FIT");
        return true;
    }

    private async Task<bool> GetAsync(Args a)
    {
        if (a.Positional.Count == 0) return await GetInteractiveAsync(null, a);
        string first = a.Positional[0];
        var entry = Catalog.Find(first);
        // a bare search term (no slash, not a catalog key, not a link): pick from search results
        if (entry == null && !first.Contains('/') && !first.Contains("://") && !first.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !Console.IsInputRedirected)
            return await GetInteractiveAsync(string.Join(' ', a.Positional), a);
        string repo = entry?.Repo ?? first;
        string? want = a.Positional.Count > 1 ? a.Positional[1] : entry?.RecommendedQuant;

        if (entry == null)
        {
            // a Hugging Face link, user/model[:quant], user/model/file.gguf or a direct .gguf URL
            var reference = HuggingFace.ParseReference(first);
            if (reference.Kind == "url")
            {
                var urlJob = _host.Downloads.StartUrl(reference.Url!);
                Ui.Info($"Downloading {urlJob.FileName} from {new Uri(reference.Url!).Host}");
                if (a.Flag("bg")) { Ui.Info($"Started in background as job {urlJob.Id}. Check with: downloads"); return true; }
                return await WaitForDownload(urlJob);
            }
            repo = reference.Repo!;
            if (reference.Kind == "file") want = reference.File;
            else want ??= reference.Quant;
        }

        var files = await HuggingFace.ListFilesAsync(repo);
        RemoteFile? pick = null;
        if (want != null)
            pick = files.FirstOrDefault(f => f.FileName.Equals(want, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f.FileName).Equals(want, StringComparison.OrdinalIgnoreCase));
        pick ??= Catalog.PickFile(files, want);
        if (pick == null) { Ui.Error($"no GGUF files found in {repo}"); return false; }

        Ui.Info($"Downloading {pick.FileName} ({Ui.Bytes(pick.SizeBytes)}) from {repo}");
        if (pick.HasVision)
            Ui.Info($"Vision model: also fetching {Path.GetFileName(pick.MmprojFile!)} ({Ui.Bytes(pick.MmprojSize)}) for image input" + (a.Flag("no-mmproj") ? " — skipped (--no-mmproj)" : ""));
        var job = _host.Downloads.Start(pick, !a.Flag("no-mmproj"));
        if (a.Flag("bg"))
        {
            Ui.Info($"Started in background as job {job.Id}. Check with: downloads");
            return true;
        }
        return await WaitForDownload(job);
    }

    private async Task<bool> WaitForDownload(DownloadJob job)
    {
        var cancelled = false;
        ConsoleCancelEventHandler h = (_, e) => { e.Cancel = true; cancelled = true; _host.Downloads.Cancel(job.Id); };
        Console.CancelKeyPress += h;
        try
        {
            while (job.State is DownloadState.Queued or DownloadState.Downloading)
            {
                Console.Write($"\r{Ui.Bar(job.Progress)} {job.Progress * 100,5:0.0}%  {Ui.Bytes(Interlocked.Read(ref job.BytesDone))}/{Ui.Bytes(job.TotalBytes)}  {Ui.Bytes((long)job.BytesPerSecond)}/s   ");
                await Task.Delay(250);
            }
        }
        finally { Console.CancelKeyPress -= h; }
        Console.WriteLine();
        switch (job.State)
        {
            case DownloadState.Completed:
                Ui.Info(Ui.Green("Done: ") + job.FinalPath);
                return true;
            case DownloadState.Cancelled:
                Ui.Info(cancelled ? "Cancelled. Re-run the same command to resume." : "Cancelled.");
                return false;
            case DownloadState.Paused:
                Ui.Info("Paused. Re-run the same command (or 'continue " + job.Id + "') to resume.");
                return false;
            default:
                Ui.Error(job.Error ?? "download failed");
                return false;
        }
    }

    private bool ShowDownloads()
    {
        var jobs = _host.Downloads.Jobs;
        if (jobs.Count == 0) { Ui.Info("No downloads in this session."); return true; }
        Ui.Table(jobs.Select(j => new[]
        {
            j.Id, j.State.ToString() + (j.Note != null ? " (" + j.Note + ")" : ""), $"{j.Progress * 100:0.0}%",
            $"{Ui.Bytes(Interlocked.Read(ref j.BytesDone))}/{Ui.Bytes(j.TotalBytes)}", j.Repo + "/" + j.FileName,
        }), "ID", "STATE", "PROGRESS", "BYTES", "FILE");
        return true;
    }

    private bool Cancel(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Error("usage: cancel <download id>"); return false; }
        return _host.Downloads.Cancel(a.Positional[0]);
    }

    private bool Pause(Args a, bool pause)
    {
        if (a.Positional.Count == 0) { Ui.Error($"usage: {(pause ? "pause" : "continue")} <download id>"); return false; }
        bool ok = pause ? _host.Downloads.Pause(a.Positional[0]) : _host.Downloads.Resume(a.Positional[0]);
        Ui.Info(ok ? (pause ? "Paused." : "Resumed.") : "No such download, or it is not in a state that can change.");
        return ok;
    }

    private async Task<bool> UpdateCommandAsync()
    {
        Ui.Info($"This is llmhost {LLMHostProject.Desktop.UpdateChecker.CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(_host.Settings.UpdateFeedUrl)) { Ui.Info("No update feed is configured (config updatefeed <url>)."); return true; }
        var u = await LLMHostProject.Desktop.UpdateChecker.CheckAsync();
        if (u != null) Ui.Info(Ui.Green("Update available: ") + $"{u.Version}  {u.Url}" + (u.Notes != null ? "\n  " + u.Notes : ""));
        else Ui.Info(LLMHostProject.Desktop.UpdateChecker.LastError != null ? Ui.Red("Update check failed: ") + LLMHostProject.Desktop.UpdateChecker.LastError : "You are up to date.");
        return true;
    }

    /// <summary>claude [folder] [--model id] [--server url] — PowerShell + Claude Code wired to the running server.</summary>
    private async Task<bool> ClaudeCodeCommandAsync(Args a)
    {
        string? url = a.Value("server") ?? ServerUrl;
        string token = a.Value("token") ?? "local";
        if (url == null)
        {
            var d = await Daemon.ProbeAsync();
            if (d == null) { Ui.Error("No server is running. Start one with: server start   (then load a model)"); return false; }
            url = d.Value.Info.Url;
            if (a.Value("token") == null && !string.IsNullOrEmpty(d.Value.Info.Token)) token = d.Value.Info.Token;
        }
        string? model = a.Value("model");
        if (model == null)
        {
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(8) };
            try
            {
                var data = JsonNode.Parse(await http.GetStringAsync("/v1/models"))?["data"] as JsonArray;
                model = data?.FirstOrDefault(m => m?["state"]?.GetValue<string>() == "loaded")?["id"]?.GetValue<string>();
            }
            catch { }
        }
        if (model == null) { Ui.Error("No model is loaded. Load one first (load <model> --ctx 32768)."); return false; }
        ClaudeCodeLauncher.Launch(url, token, model, a.Positional.FirstOrDefault());
        Ui.Info($"Opened PowerShell with Claude Code using {model} at {url}");
        return true;
    }

    /// <summary>daemon up [serve options] | down | status | update | install [--service] | uninstall</summary>
    private async Task<bool> DaemonCommandAsync(Args a)
    {
        string sub = a.Positional.FirstOrDefault() ?? "status";
        var serveArgs = new List<string>();
        if (a.Value("host") is { } h) { serveArgs.Add("--host"); serveArgs.Add(h); }
        if (a.Value("port") is { } p) { serveArgs.Add("--port"); serveArgs.Add(p); }
        if (a.Flag("lan")) serveArgs.Add("--lan");
        if (a.Value("model") is { } m) { serveArgs.Add("--model"); serveArgs.Add(m); }
        if (a.Value("ctx") is { } c) { serveArgs.Add("--ctx"); serveArgs.Add(c); }
        if (a.Flag("cpu")) serveArgs.Add("--cpu");
        switch (sub)
        {
            case "up": case "start":
            {
                var before = await Daemon.ProbeAsync();
                var info = await Daemon.UpAsync(serveArgs);
                Ui.Info(before != null ? $"Daemon already running (pid {info.Pid}) at {info.Url}" : Ui.Green("Daemon started ") + $"(pid {info.Pid}) at {info.Url}");
                return true;
            }
            case "down": case "stop":
                Ui.Info(await Daemon.DownAsync() ? "Daemon stopped." : "The daemon is not running.");
                return true;
            case "status":
            {
                var st = await Daemon.ProbeAsync();
                if (st == null) { Ui.Info("Daemon: not running" + (Daemon.IsInstalledAsTask() ? "  (starts at logon)" : "")); return a.Flag("json") ? true : true; }
                var (i, status) = st.Value;
                if (a.Flag("json")) { Ui.Info(status.GetRawText()); return true; }
                int loaded = status.TryGetProperty("loaded", out var l) ? l.GetArrayLength() : 0;
                var up = DateTime.UtcNow - i.StartedAt;
                Ui.Info(Ui.Green("Daemon running") + $"  pid {i.Pid}  {i.Url}  up {(int)up.TotalHours}h{up.Minutes:00}m  {loaded} model(s) loaded");
                return true;
            }
            case "update": case "restart":
            {
                bool was = await Daemon.DownAsync();
                var info = await Daemon.UpAsync(serveArgs);
                Ui.Info((was ? "Restarted" : "Started") + $" the daemon (pid {info.Pid}) with the installed version at {info.Url}");
                return true;
            }
            case "install":
            {
                // the daemon is launched from elsewhere (logon task / service): pin the models folder it should use
                if (string.IsNullOrWhiteSpace(_host.Settings.ModelsDirectory))
                {
                    _host.Settings.ModelsDirectory = _host.Settings.ResolvedModelsDirectory;
                    _host.Settings.Save();
                }
                Ui.Info(Daemon.Install(a.Flag("service"), serveArgs));
                return true;
            }
            case "uninstall": case "remove":
                Ui.Info(Daemon.Uninstall());
                return true;
            default:
                Ui.Error("usage: daemon up [--port N] [--lan] [--model m] | down | status [--json] | update | install [--service] | uninstall");
                return false;
        }
    }

    /// <summary>mcp list | tools [server] | call &lt;server&gt; &lt;tool&gt; [json] | reload — MCP servers of mcp.json.</summary>
    private async Task<bool> McpCommandAsync(Args a)
    {
        string sub = a.Positional.FirstOrDefault() ?? "list";
        switch (sub)
        {
            case "list": case "ls":
            {
                var tools = await _host.Mcp.AllToolsAsync(onlyServers: a.Positional.Skip(1).FirstOrDefault() is { } s ? new[] { s } : null);
                var servers = _host.Mcp.Servers;
                if (servers.Count == 0) { Ui.Info($"No MCP servers. Edit {McpManager.ConfigPath} (Cursor notation: {{\"mcpServers\": {{...}}}})."); return true; }
                Ui.Table(servers.Select(sv => new[] { sv.Name, sv.Transport, sv.State == "ready" ? Ui.Green("ready") : Ui.Red(sv.State + (sv.Error != null ? ": " + sv.Error : "")),
                    sv.Tools.Count.ToString(), string.Join(", ", sv.Tools.Take(6).Select(t => t.Name)) }), "SERVER", "TYPE", "STATE", "TOOLS", "");
                return true;
            }
            case "tools":
            {
                var tools = await _host.Mcp.AllToolsAsync(onlyServers: a.Positional.Skip(1).FirstOrDefault() is { } s ? new[] { s } : null);
                Ui.Table(tools.Select(t => new[] { t.QualifiedName, t.Description.Length > 70 ? t.Description[..70] + "…" : t.Description }), "TOOL", "DESCRIPTION");
                return true;
            }
            case "call" when a.Positional.Count >= 3:
            {
                JsonNode? args = null;
                if (a.Positional.Count > 3) args = JsonNode.Parse(string.Join(' ', a.Positional.Skip(3)));
                var r = await _host.Mcp.CallAsync(a.Positional[1], a.Positional[2], args);
                Ui.Info(r.IsError ? Ui.Red("error: ") + r.Text : r.Text);
                return !r.IsError;
            }
            case "reload":
                _host.Mcp.Reload(); Ui.Info("mcp.json reloaded."); return true;
            default:
                Ui.Error("usage: mcp list [server] | tools [server] | call <server> <tool> [json args] | reload");
                return false;
        }
    }

    /// <summary>token list | create &lt;name&gt; [--perm chat,embeddings,models:read,...] | revoke &lt;id|name&gt; | disable &lt;id&gt; | enable &lt;id&gt;</summary>
    private bool TokenCommand(Args a)
    {
        var auth = new ApiAuth(_host);
        string sub = a.Positional.FirstOrDefault() ?? "list";
        string? arg = a.Positional.Skip(1).FirstOrDefault();
        switch (sub)
        {
            case "list": case "ls":
            {
                var all = auth.List();
                Ui.Info(Ui.Dim($"authentication: {(_host.Settings.RequireAuth ? "required" : "off")}" +
                               (string.IsNullOrEmpty(_host.Settings.ApiKey) ? "" : "  (legacy single API key is set)")));
                if (all.Count == 0) { Ui.Info("No tokens. Create one with: token create <name>"); return true; }
                Ui.Table(all.Select(t => new[] { t.Id, t.Name, t.Prefix + "…", string.Join(',', t.Permissions),
                    t.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never", t.Disabled ? "disabled" : "" }),
                    "ID", "NAME", "TOKEN", "PERMISSIONS", "LAST USED", "");
                return true;
            }
            case "create" when arg != null:
            {
                var perms = a.Value("perm", "permissions")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var (t, secret) = auth.Create(arg, perms);
                Ui.Info(Ui.Green("Created token ") + $"{t.Name} ({t.Id}) with permissions: {string.Join(", ", t.Permissions)}");
                Ui.Info("Secret (shown only now): " + Ui.Bold(secret));
                if (!_host.Settings.RequireAuth)
                    Ui.Info(Ui.Dim("Authentication is off, so tokens are not checked yet. Turn it on with: config auth on"));
                return true;
            }
            case "revoke" or "delete" or "rm" when arg != null:
                Ui.Info(auth.Revoke(arg) ? "Revoked." : "No such token.");
                return true;
            case "disable" or "enable" when arg != null:
                var u = auth.Update(arg, null, null, sub == "disable");
                Ui.Info(u == null ? "No such token." : $"{u.Name}: {(u.Disabled ? "disabled" : "enabled")}");
                return u != null;
            default:
                Ui.Error("usage: token list | create <name> [--perm a,b] | revoke <id> | disable <id> | enable <id>   (permissions: " + string.Join(", ", Scopes.All) + ")");
                return false;
        }
    }

    /// <summary>runtime list | get | select &lt;id|auto&gt; | install &lt;zip|url|folder&gt; [--name n] | update | remove &lt;id&gt;</summary>
    private async Task<bool> RuntimeCommandAsync(Args a)
    {
        string sub = a.Positional.FirstOrDefault() ?? "list";
        string? arg = a.Positional.Skip(1).FirstOrDefault();
        switch (sub)
        {
            case "list": case "ls": case "get":
            {
                var s = RuntimeManager.Survey();
                Ui.Info(Ui.Dim($"CPU: AVX {(s.Avx ? "yes" : "no")}, AVX2 {(s.Avx2 ? "yes" : "no")}, AVX-512 {(s.Avx512 ? "yes" : "no")} · Vulkan loader {(s.Vulkan ? "yes" : "no")}"));
                Ui.Table(RuntimeManager.List().Select(r => new[]
                {
                    (r.Active ? "* " : r.Selected ? "> " : "  ") + r.Id, r.Name, r.Source, r.Version ?? "", Ui.Bytes(r.SizeBytes),
                    r.Compatible ? (r.Recommended ? Ui.Green("recommended") : "ok") : Ui.Red(r.Reason ?? "incompatible"),
                }), "RUNTIME", "NAME", "SOURCE", "VERSION", "SIZE", "STATUS");
                Ui.Info(Ui.Dim($"* active, > selected (applies after a restart). Selection: {_host.Settings.Runtime}"));
                return true;
            }
            case "select" when arg != null:
            {
                var r = RuntimeManager.Select(arg);
                Ui.Info(r == null ? "Runtime selection: automatic (applies after a restart)." : $"Selected {r.Name} — applies after a restart.");
                return true;
            }
            case "install" when arg != null:
            {
                var r = await RuntimeManager.InstallAsync(arg, a.Value("name"));
                Ui.Info(Ui.Green("Installed ") + $"{r.Id} ({r.Name}). Select it with: runtime select {r.Id}");
                return true;
            }
            case "update":
            {
                var u = await RuntimeManager.UpdateAsync();
                Ui.Info(u.Count == 0 ? "All installed runtimes are up to date." : "Updated: " + string.Join(", ", u));
                return true;
            }
            case "remove" when arg != null:
                Ui.Info(RuntimeManager.Remove(arg) ? "Removed." : "Only user-installed runtimes can be removed.");
                return true;
            default:
                Ui.Error("usage: runtime list | select <id|auto> | install <zip|url|folder> [--name n] | update | remove <id>");
                return false;
        }
    }

    /// <summary>fit &lt;model&gt; [--ctx N] [--gpu-layers N|--cpu|--gpu] [--kv-k t] [--kv-v t] [--flash] — memory estimate before loading.</summary>
    private bool Fit(Args a)
    {
        var m = _host.Library.Find(string.Join(' ', a.Positional));
        if (m == null) { Ui.Error("usage: fit <model> [--ctx N] [--gpu-layers N | --cpu | --gpu] [--kv-k q8_0] [--kv-v q8_0] [--flash]"); return false; }
        var o = (_host.ModelDefaults.LoadFor(m.Id) ?? _load).Clone();
        if (a.Flag("cpu")) o.Device = "cpu";
        ApplyGpuFlag(a, o);
        if (a.Value("ctx", "context") is { } c) o.ContextSize = uint.Parse(c);
        if (a.Value("gpu-layers", "ngl") is { } n) o.GpuLayers = int.Parse(n);
        if (a.Value("kv-k") is { } kk) o.KvCacheTypeK = kk;
        if (a.Value("kv-v") is { } kv) o.KvCacheTypeV = kv;
        if (a.Flag("flash")) o.FlashAttentionMode = a.Value("flash") is "off" or "auto" ? a.Value("flash")! : "on";
        if (a.Flag("no-flash")) o.FlashAttentionMode = "off";
        if (a.Value("gpus", "devices") is { } gs) o.GpuDevices = gs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        if (a.Value("split", "tensor-split") is { } ts) o.TensorSplit = ts.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (a.Value("split-mode") is { } sm) o.SplitMode = sm;
        var e = FitEstimator.Estimate(m, o);
        string color(string s) => e.Verdict == "fits" ? Ui.Green(s) : e.Verdict == "tight" ? Ui.Yellow(s) : Ui.Red(s);
        Ui.Info($"{m.Id}  ctx {o.ContextSize}  {e.OffloadedLayers}/{e.BlockCount} layers on GPU{(e.Moe ? "  (MoE)" : "")}");
        Ui.Info($"  weights {Ui.Bytes(e.WeightsBytes)}  KV cache {Ui.Bytes(e.KvCacheBytes)}  compute {Ui.Bytes(e.ComputeBytes)}" +
                (e.ProjectorBytes > 0 ? $"  vision {Ui.Bytes(e.ProjectorBytes)}" : ""));
        foreach (var d in e.Devices) Ui.Info($"  {d.Name,-34} needs {Ui.Bytes(d.NeedBytes),10} of {Ui.Bytes(d.BudgetBytes),10}  ({d.Ratio * 100:0}%)");
        if (e.MaxGpuLayers >= 0) Ui.Info($"  GPU layers that fit: {(e.MaxGpuLayers >= 999 ? "all" : e.MaxGpuLayers.ToString())}");
        if (e.SuggestedContext > 0) Ui.Info($"  a context of {e.SuggestedContext} would fit");
        foreach (var w in e.Warnings) Ui.Info(Ui.Dim("  " + w));
        Ui.Info(color(e.Verdict switch { "fits" => "Should fit.", "tight" => "Tight: may fit, but it leaves little headroom.", _ => "Will not fit." }));
        return true;
    }

    /// <summary>import &lt;file.gguf&gt; [--move|--link] [--publisher name] [--name model] [--no-mmproj]</summary>
    private bool ImportModel(Args a)
    {
        if (a.Positional.Count == 0)
        {
            Ui.Error("usage: import <file.gguf> [--copy|--move|--link] [--publisher <name>] [--name <model>] [--no-mmproj]");
            return false;
        }
        string mode = a.Flag("move") ? "move" : a.Flag("link") ? "link" : "copy";
        var r = _host.Library.Import(string.Join(' ', a.Positional), mode, a.Value("publisher", "user"), a.Value("name", "model"), !a.Flag("no-mmproj"));
        Ui.Info(Ui.Green("Imported ") + r.Id + Ui.Dim($"  ({r.Files.Count} file(s), {mode})"));
        return true;
    }

    private bool Remove(Args a)
    {
        var m = _host.Library.Find(string.Join(' ', a.Positional));
        if (m == null) { Ui.Error("model not found"); return false; }
        if (_host.Models.Loaded.Any(l => l.Model.Path == m.Path)) { Ui.Error("unload it first"); return false; }
        if (!a.Flag("y"))
        {
            Console.Write($"Delete {m.Id} ({Ui.Bytes(m.SizeBytes)})? [y/N] ");
            if (!(Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        }
        bool ok = _host.Library.Delete(m);
        Ui.Info(ok ? "Deleted." : "Could not delete the file.");
        return ok;
    }

    // ===================================================================== load / unload

    /// <summary>--gpu [off|max|auto|0.0-1.0] (a bare --gpu means max).</summary>
    private void ApplyGpuFlag(Args a, LoadOptions o)
    {
        if (!a.Flag("gpu")) return;
        o.SetGpu(a.Value("gpu") ?? "max");
    }

    private async Task<bool> LoadAsync(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Error("usage: load <model> [--cpu|--gpu] [--gpu-layers N] [--ctx N] ..."); return false; }
        // the model's own saved defaults win over the session defaults; explicit flags win over both
        var target = _host.Library.Find(string.Join(' ', a.Positional));
        var saved = target != null ? _host.ModelDefaults.LoadFor(target.Id) : null;
        var o = (saved ?? _load).Clone();
        if (a.Flag("cpu")) o.Device = "cpu";
        ApplyGpuFlag(a, o);
        if (a.Value("device") is { } dev) o.Device = dev;
        if (a.Value("gpu-layers", "ngl") is { } ngl) o.GpuLayers = int.Parse(ngl);
        if (a.Value("ctx", "context", "context-length") is { } ctx) o.ContextSize = uint.Parse(ctx);
        if (a.Value("threads") is { } th) o.Threads = int.Parse(th);
        if (a.Value("batch-threads") is { } bth) o.BatchThreads = int.Parse(bth);
        if (a.Value("cpu-affinity") is { } aff) o.CpuAffinity = aff;
        if (a.Value("thread-priority") is { } prio) o.ThreadPriority = prio;
        if (a.Value("batch") is { } bt) o.BatchSize = o.UBatchSize = uint.Parse(bt);
        if (a.Value("main-gpu") is { } mg) o.MainGpu = int.Parse(mg);
        if (a.Value("parallel", "max-concurrent") is { } par) o.MaxConcurrent = int.Parse(par);
        if (a.Value("gpus", "devices") is { } gs) o.GpuDevices = gs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        if (a.Value("split", "tensor-split") is { } ts) o.TensorSplit = ts.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (a.Value("split-mode") is { } sm) o.SplitMode = sm;
        if (a.Flag("flash")) o.FlashAttentionMode = a.Value("flash") is "off" or "auto" ? a.Value("flash")! : "on";
        if (a.Flag("no-flash")) o.FlashAttentionMode = "off";
        if (a.Flag("no-mmap")) o.UseMmap = false;
        if (a.Flag("mlock")) o.UseMlock = true;
        if (a.Flag("no-vision")) o.Vision = false;
        if (a.Flag("skip-fit-check") || a.Flag("force")) o.SkipFitCheck = true;
        if (a.Value("runtime") is { } rtName) o.Runtime = rtName;
        if (a.Value("mmproj") is { } mmp) o.MmprojPath = mmp;
        if (a.Flag("embeddings")) o.Embeddings = true;
        if (a.Value("kv-k") is { } kk) o.KvCacheTypeK = kk;
        if (a.Value("kv-v") is { } kv) o.KvCacheTypeV = kv;
        if (a.Value("id", "identifier") is { } id) o.Identifier = id;
        if (a.Value("ttl") is { } ttl) o.TtlMinutes = int.Parse(ttl);
        if (a.Value("draft") is { } dm) o.DraftModel = dm;
        if (a.Value("draft-tokens") is { } dt) o.DraftTokens = int.Parse(dt);
        if (a.Value("lora") is { } lo)
            foreach (var item in lo.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int at = item.LastIndexOf('=');
                float scale = 1f;
                string loraId = item;
                if (at > 0 && float.TryParse(item[(at + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc)) { loraId = item[..at]; scale = sc; }
                o.Loras.Add(new LoraSetting { Id = loraId.Trim(), Scale = scale });
            }
        if (a.Value("pooling") is { } pool) o.Pooling = pool;
        if (a.Flag("no-normalize")) o.Normalize = false;
        if (a.Value("embed-batch") is { } eb) o.EmbedBatchSize = int.Parse(eb);
        if (a.Flag("cpu-moe")) o.MoeCpuLayers = -1;
        if (a.Value("n-cpu-moe") is { } ncm) o.MoeCpuLayers = int.Parse(ncm);
        if (a.Value("moe-experts") is { } mex) o.MoeExpertCount = int.Parse(mex);
        if (a.Value("override-tensor") is { } ot)
            foreach (var rule in ot.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = rule.LastIndexOf('=');
                if (eq <= 0) { Ui.Error($"--override-tensor expects pattern=BUFFER (e.g. exps=CPU), got '{rule}'"); return false; }
                o.TensorOverrides.Add(new TensorOverrideSetting { Pattern = rule[..eq], Buffer = rule[(eq + 1)..] });
            }
        if (a.Value("template-file") is { } tf)
        {
            if (!File.Exists(tf)) { Ui.Error($"template file not found: {tf}"); return false; }
            o.PromptTemplate = new PromptTemplateSettings { Mode = PromptTemplateSettings.Jinja, JinjaText = File.ReadAllText(tf) };
        }
        if (a.Value("rope-base") is { } rb) o.RopeFrequencyBase = float.Parse(rb, System.Globalization.CultureInfo.InvariantCulture);

        var query = string.Join(' ', a.Positional);
        if (_host.Library.Find(query) == null)
        {
            Ui.Error($"'{query}' is not downloaded. Use 'get <repo>' first.");
            return false;
        }

        Ui.Info($"Loading {query} ...");
        int lastPct = -1;
        void OnProgress(string _, float p)
        {
            int pct = (int)(p * 100);
            if (pct == lastPct) return;
            lastPct = pct;
            Console.Write($"\r{Ui.Bar(p)} {pct}%  ");
        }
        _host.Models.LoadProgress += OnProgress;
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource();
            var rt = await _host.Models.LoadAsync(query, o, cts.Token, a.Flag("another"));
            Console.WriteLine();
            string layers = rt.GpuLayers >= 999 ? "all layers" : $"{rt.GpuLayers} layers";
            Ui.Info(Ui.Green("Loaded ") + $"{rt.Identifier} in {sw.Elapsed.TotalSeconds:0.0}s — " +
                    (rt.GpuLayers > 0 ? $"GPU ({layers})" : "CPU") + $", context {rt.ContextSize}");
            foreach (var w in rt.LoadWarnings) Ui.Info(Ui.Yellow("  ! " + w));
            if (!rt.Model.IsEmbedding && !rt.Options.Embeddings)
            {
                _activeModel = rt.Identifier;
                var genDefaults = _host.ModelDefaults.GenerationFor(rt.Model.Id);
                if (genDefaults != null)
                {
                    foreach (var prop in typeof(GenerationOptions).GetProperties().Where(x => x.CanWrite))
                        prop.SetValue(_gen, prop.GetValue(genDefaults));
                    Ui.Info(Ui.Dim("Applied the saved inference defaults of this model."));
                }
            }
            return true;
        }
        finally { _host.Models.LoadProgress -= OnProgress; }
    }

    /// <summary>--gpu [max|auto|off|0..1]: device choice, or the fraction of layers to offload.</summary>

    private bool Unload(Args a)
    {
        if (a.Positional.Count == 0 || a.Flag("all"))
        {
            _host.Models.UnloadAll();
            _activeModel = null;
            Ui.Info("Unloaded all models.");
            return true;
        }
        string id = a.Positional[0];
        bool ok = _host.Models.Unload(id);
        if (_activeModel == id) _activeModel = _host.Models.Loaded.FirstOrDefault()?.Identifier;
        Ui.Info(ok ? $"Unloaded {id}" : $"'{id}' is not loaded");
        return ok;
    }

    private bool Ps(Args a)
    {
        var l = _host.Models.Loaded;
        if (a.Flag("json"))
        {
            Ui.Info(JsonSerializer.Serialize(l.Select(r => new
            {
                id = r.Identifier, file = r.Model.FileName, device = r.GpuLayers > 0 ? "gpu" : "cpu", r.GpuLayers,
                contextSize = r.ContextSize, sizeBytes = r.Model.SizeBytes, r.Requests, active = r.ActiveRequests, queued = r.QueuedRequests,
                parallel = r.MaxConcurrent, ttlMinutes = r.Options.TtlMinutes, vision = r.SupportsVision,
            }), Json.Options));
            return true;
        }
        if (l.Count == 0) { Ui.Info("No models loaded."); return true; }
        Ui.Table(l.Select(r => new[]
        {
            (r.Identifier == _activeModel ? "* " : "  ") + r.Identifier, r.GpuLayers > 0 ? "GPU" : "CPU",
            r.GpuLayers >= 999 ? "all" : r.GpuLayers.ToString(), r.Threads.ToString(), r.ContextSize.ToString(), Ui.Bytes(r.Model.SizeBytes),
            r.Requests.ToString(), r.Options.TtlMinutes > 0 ? r.Options.TtlMinutes + "m" : "-",
        }), "MODEL", "DEVICE", "GPU LAYERS", "THREADS", "CTX", "SIZE", "REQUESTS", "TTL");
        return true;
    }

    private bool Use(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Info(_activeModel ?? "(none)"); return true; }
        var r = _host.Models.Get(a.Positional[0]);
        if (r == null) { Ui.Error("not loaded; use /load first"); return false; }
        _activeModel = r.Identifier;
        return true;
    }

    // ===================================================================== parameters

    private bool ShowParams()
    {
        Ui.Info(Ui.Bold("Generation"));
        foreach (var (n, v) in PropertyEditor.Dump(_gen)) Ui.Info($"  {n,-18} {v}");
        Ui.Info(Ui.Bold("Load defaults (set load.<name>)"));
        foreach (var (n, v) in PropertyEditor.Dump(_load)) Ui.Info($"  {n,-18} {v}");
        return true;
    }

    private bool SetParam(Args a)
    {
        if (a.Positional.Count < 2) { Ui.Error("usage: set <name> <value>   (see: params)"); return false; }
        string name = a.Positional[0];
        string value = string.Join(' ', a.Positional.Skip(1));
        object target = _gen;
        if (name.StartsWith("load.", StringComparison.OrdinalIgnoreCase)) { target = _load; name = name[5..]; }
        else if (PropertyEditor.Find(typeof(GenerationOptions), name) == null && PropertyEditor.Find(typeof(LoadOptions), name) != null)
            target = _load;
        if (!PropertyEditor.TrySet(target, name, value, out var err)) { Ui.Error(err); return false; }
        var p = PropertyEditor.Find(target.GetType(), name)!;
        Ui.Info($"{p.Name} = {PropertyEditor.Format(p.GetValue(target))}");
        return true;
    }

    private void ResetParams()
    {
        var d = new GenerationOptions();
        foreach (var p in typeof(GenerationOptions).GetProperties().Where(p => p.CanWrite))
            p.SetValue(_gen, p.GetValue(d));
        Ui.Info("Generation parameters reset to built-in defaults.");
    }

    private bool SetSystem(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Info(_gen.SystemPrompt ?? "(no system prompt)"); return true; }
        _gen.SystemPrompt = string.Join(' ', a.Positional);
        Ui.Info("System prompt set.");
        return true;
    }

    private bool Preset(Args a)
    {
        string sub = a.Positional.FirstOrDefault() ?? "list";
        string? name = a.Positional.Skip(1).FirstOrDefault();
        switch (sub)
        {
            case "save" when name != null:
                _host.Presets.Save(name, _gen.Clone()); Ui.Info($"Saved preset '{name}'."); return true;
            case "load" when name != null:
                if (!_host.Presets.All().TryGetValue(name, out var p)) { Ui.Error("no such preset"); return false; }
                foreach (var prop in typeof(GenerationOptions).GetProperties().Where(x => x.CanWrite))
                    prop.SetValue(_gen, prop.GetValue(p));
                Ui.Info($"Loaded preset '{name}'."); return true;
            case "delete" when name != null:
                return _host.Presets.Delete(name);
            default:
                foreach (var kv in _host.Presets.All())
                    Ui.Info($"  {kv.Key}  (temp {kv.Value.Temperature}, top_p {kv.Value.TopP}, top_k {kv.Value.TopK})");
                return true;
        }
    }

    private void SaveDefaults()
    {
        _host.Settings.DefaultGeneration = _gen.Clone();
        _host.Settings.DefaultLoad = _load.Clone();
        _host.Settings.Save();
        Ui.Info("Saved as the default parameters.");
    }

    /// <summary>defaults [model] [show|save|clear] — per-model load + inference defaults.</summary>
    private bool ModelDefaultsCommand(Args a)
    {
        string sub = a.Positional.FirstOrDefault() is "show" or "save" or "clear" ? a.Positional[0] : "show";
        var rest = a.Positional.Where(p => p is not ("show" or "save" or "clear")).ToList();
        string query = rest.Count > 0 ? string.Join(' ', rest) : _activeModel ?? "";
        var m = _host.Library.Find(query) ?? (_host.Models.Get(query)?.Model);
        if (m == null) { Ui.Error("usage: defaults [show|save|clear] <model>   (or load a model first)"); return false; }
        switch (sub)
        {
            case "save":
                _host.ModelDefaults.Set(m.Id, _load.Clone(), _gen.Clone());
                Ui.Info($"Saved the current load and inference settings as the defaults of {m.Id}.");
                return true;
            case "clear":
                Ui.Info(_host.ModelDefaults.Clear(m.Id) ? $"Cleared the defaults of {m.Id}." : "No saved defaults.");
                return true;
            default:
                var c = _host.ModelDefaults.Get(m.Id);
                if (c == null) { Ui.Info($"{m.Id}: no saved defaults (the global defaults apply)."); return true; }
                if (c.Load != null) { Ui.Info(Ui.Bold("Load")); foreach (var (n, v) in PropertyEditor.Dump(c.Load)) Ui.Info($"  {n,-18} {v}"); }
                if (c.Generation != null) { Ui.Info(Ui.Bold("Inference")); foreach (var (n, v) in PropertyEditor.Dump(c.Generation)) Ui.Info($"  {n,-18} {v}"); }
                return true;
        }
    }

    private bool Config(Args a)
    {
        var s = _host.Settings;
        if (a.Positional.Count == 0)
        {
            Ui.Info($"settings file: {AppPaths.SettingsFile}");
            foreach (var key in new[] { "modelsdir", "host", "port", "apikey", "cors", "anthropic", "backend", "hftoken", "autoload", "guardrail", "dedicated-gpu-only", "auth", "tray", "updates", "updatefeed" })
                Ui.Info($"  {key,-10} {ConfigGet(key)}");
            return true;
        }
        string k = a.Positional[0].ToLowerInvariant();
        if (a.Positional.Count == 1) { Ui.Info(ConfigGet(k)); return true; }
        string v = string.Join(' ', a.Positional.Skip(1));
        switch (k)
        {
            case "modelsdir":
                s.ModelsDirectory = v is "default" or "none" ? null : v;
                Directory.CreateDirectory(s.ResolvedModelsDirectory);
                break;
            case "host": s.Host = v; break;
            case "port": s.Port = int.Parse(v); break;
            case "apikey": s.ApiKey = v is "none" or "" ? null : v; break;
            case "cors": s.Cors = v is "on" or "true" or "1"; break;
            case "anthropic": s.AnthropicApi = v is "on" or "true" or "1"; break;
            case "backend":
                if (v is not ("auto" or "vulkan" or "cpu")) { Ui.Error("backend must be auto, vulkan or cpu"); return false; }
                s.Backend = v; Ui.Info(Ui.Dim("(takes effect after restart)")); break;
            case "hftoken": s.HuggingFaceToken = v is "none" or "" ? null : v; break;
            case "autoload": s.AutoLoadOnRequest = v is "on" or "true" or "1"; break;
            case "auth": s.RequireAuth = v is "on" or "true" or "1"; break;
            case "debugrequests": s.DebugRequests = v is "on" or "true" or "1"; break;
            case "anthropic-reasoning":
                if (v is not ("text" or "blocks")) { Ui.Error("anthropic-reasoning must be text or blocks"); return false; }
                s.AnthropicReasoningAsText = v == "text"; break;
            case "autostart":
                LLMHostProject.Desktop.MainWindow.SetAutoStart(v is "on" or "true" or "1"); break;
            case "tray": s.MinimizeToTray = v is "on" or "true" or "1"; break;
            case "updates": s.CheckUpdates = v is "on" or "true" or "1"; break;
            case "updatefeed": s.UpdateFeedUrl = v is "none" or "" ? null : v; break;
            case "guardrail":
                if (v is not ("off" or "balanced" or "strict")) { Ui.Error("guardrail must be off, balanced or strict"); return false; }
                s.LoadGuardrail = v; break;
            case "dedicated-gpu-only": s.LimitToDedicatedGpuMemory = v is "on" or "true" or "1"; break;
            default: Ui.Error($"unknown setting '{k}'"); return false;
        }
        s.Save();
        Ui.Info($"{k} = {ConfigGet(k)}");
        return true;
    }

    private string ConfigGet(string key)
    {
        var s = _host.Settings;
        return key switch
        {
            "modelsdir" => s.ResolvedModelsDirectory + (string.IsNullOrWhiteSpace(s.ModelsDirectory) ? "  (default: ./DownloadedModels)" : ""), "host" => s.Host, "port" => s.Port.ToString(),
            "apikey" => string.IsNullOrEmpty(s.ApiKey) ? "(none)" : "(set)", "cors" => s.Cors ? "on" : "off", "anthropic" => s.AnthropicApi ? "on" : "off",
            "backend" => s.Backend,
            "hftoken" => string.IsNullOrEmpty(s.HuggingFaceToken) ? "(none)" : "(set)",
            "autoload" => s.AutoLoadOnRequest ? "on" : "off", "guardrail" => s.LoadGuardrail, "auth" => s.RequireAuth ? "on" : "off", "debugrequests" => s.DebugRequests ? "on" : "off", "anthropic-reasoning" => s.AnthropicReasoningAsText ? "text" : "blocks", "tray" => s.MinimizeToTray ? "on" : "off", "updates" => s.CheckUpdates ? "on" : "off",
            "updatefeed" => s.UpdateFeedUrl ?? "(none)", "autostart" => "(see the tray menu)",
            "dedicated-gpu-only" => s.LimitToDedicatedGpuMemory ? "on" : "off", _ => "?",
        };
    }

    // ===================================================================== server

    private async Task<bool> ServeAsync(Args a)
    {
        var s = _host.Settings;
        string? bindHost = a.Value("host");
        int? port = a.Value("port") is { } p ? int.Parse(p) : null;
        if (a.Value("api-key") is { } key) { s.ApiKey = key; }
        if (a.Flag("lan")) bindHost = "0.0.0.0";
        _daemonMode = a.Flag("daemon");

        if (a.Value("model") is { } model)
        {
            var o = _load.Clone();
            if (a.Flag("cpu")) o.Device = "cpu";
            if (a.Value("ctx") is { } c) o.ContextSize = uint.Parse(c);
            if (a.Value("gpu-layers") is { } l) o.GpuLayers = int.Parse(l);
            await _host.Models.LoadAsync(model, o);
        }
        if (_server.Running) { Ui.Info($"Already serving on {_server.Url}"); return true; }
        await _server.StartAsync(bindHost, port);
        int resumed = _host.Downloads.ResumeInterrupted();
        if (resumed > 0) Ui.Info(Ui.Dim($"Resuming {resumed} interrupted download(s)."));
        Ui.Info(Ui.Green("Server running: ") + _server.Url);
        Ui.Info(Ui.Dim($"  OpenAI API:  {_server.Url}/v1   (chat/completions, completions, embeddings, models)"));
        if (s.AnthropicApi) Ui.Info(Ui.Dim($"  Anthropic:   {_server.Url}/v1/messages   (use base URL {_server.Url})"));
        Ui.Info(Ui.Dim($"  Web UI:      {_server.Url}/"));
        if (!string.IsNullOrEmpty(s.ApiKey)) Ui.Info(Ui.Dim("  API key required (Authorization: Bearer <key>)"));
        if (_daemonMode)
            Daemon.Write(new DaemonInfo { Pid = Environment.ProcessId, Url = _server.Url!, Token = _server.Auth.UiToken, StartedAt = DateTime.UtcNow, Exe = Environment.ProcessPath ?? "" });

        return true;
    }

    /// <summary>Base URL of the running server (null when not serving).</summary>
    public string? ServerUrl => _server.Running ? _server.Url : null;

    /// <summary>Server URL including the per-process UI token (for the embedded window).</summary>
    public string? UiUrl => _server.Running ? _server.UiUrl : null;

    public Task StopServerAsync() => _server.StopAsync();

    /// <summary>Blocks until Ctrl+C, a service stop, or a shutdown request through the API (the "serve" sub-command).</summary>
    public async Task WaitForShutdownAsync()
    {
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
        if (!_daemonMode) Ui.Info(Ui.Dim("Press Ctrl+C to stop."));
        await Task.WhenAny(tcs.Task, _host.Shutdown.Task);
        await _server.StopAsync();
        Daemon.Clear(Environment.ProcessId);
    }

    private bool _daemonMode;

    /// <summary>log level [&lt;level&gt;] [--native &lt;level&gt;] — shows or sets how much is written to the log files.</summary>
    private bool LogLevelCommand(Args a)
    {
        var cfg = _host.Settings.Logging;
        string? level = a.Positional.Count > 1 ? a.Positional[1] : null;
        string? native = a.Value("native");
        string[] levels = { "trace", "debug", "info", "warn", "error", "off" };
        if (level != null && !levels.Contains(level.ToLowerInvariant())) throw new ArgumentException($"Level must be one of: {string.Join(", ", levels)}");
        if (native != null && !levels.Skip(1).Contains(native.ToLowerInvariant())) throw new ArgumentException($"Native level must be one of: {string.Join(", ", levels.Skip(1))}");
        if (level != null) cfg.Level = level.ToLowerInvariant();
        if (native != null) cfg.Native = native.ToLowerInvariant();
        if (level != null || native != null) _host.Settings.Save();
        Ui.Info($"log level: {cfg.Level}   native (llama.cpp) level: {cfg.Native}   files: {LogBuffer.Sink.Directory}");
        return true;
    }

    private bool Logs(Args a)
    {
        int n = a.Positional.Count > 0 && int.TryParse(a.Positional[0], out var v) ? v : 40;
        foreach (var l in LogBuffer.Tail(n)) Ui.Info(Ui.Dim(l));
        return true;
    }

    // ===================================================================== chat

    private void NewConversation()
    {
        _history.Clear();
        _conversation = new Conversation();
    }

    private async Task<bool> ChatCommandAsync(Args a)
    {
        if (a.Positional.Count > 0) await LoadAsync(a);
        // the sub-command continues into the interactive loop
        return true;
    }

    private async Task ChatAsync(string userText)
    {
        var rt = await _host.Models.ResolveAsync(_activeModel);
        _activeModel = rt.Identifier;
        if (_pendingImages.Count > 0 && !rt.SupportsVision)
        {
            Ui.Error($"'{rt.Identifier}' cannot read images (no mmproj loaded). Attached images were discarded.");
            _pendingImages.Clear();
        }
        var images = _pendingImages.Count > 0 ? _pendingImages.ToList() : null;
        _pendingImages.Clear();
        _history.Add(new ChatMessage("user", userText, images));
        await GenerateReplyAsync(rt);
    }

    private async Task GenerateReplyAsync(ModelRuntime rt)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(_gen.SystemPrompt)) messages.Add(new ChatMessage("system", _gen.SystemPrompt));
        messages.AddRange(_history);

        int reserve = _gen.MaxTokens > 0 ? _gen.MaxTokens : (int)Math.Min(512, rt.ContextSize / 4);
        string prompt = rt.BuildFittingPrompt(messages, reserve, out int dropped, out var images, _gen.ReasoningEffort, _gen.ContextOverflow);
        if (dropped > 0) Ui.Info(Ui.Dim($"[context full: {dropped} oldest message(s) not sent to the model]"));

        var stats = new GenerationStats();
        var reply = new StringBuilder();
        _genCts = new CancellationTokenSource();
        try
        {
            await foreach (var piece in rt.StreamAsync(prompt, _gen, stats, _genCts.Token, images))
            {
                Console.Write(piece);
                reply.Append(piece);
            }
        }
        catch (OperationCanceledException) { Console.Write(Ui.Dim(" [stopped]")); }
        finally { _genCts = null; }

        Console.WriteLine();
        Ui.Info(Ui.Dim($"[{stats.CompletionTokens} tokens · {stats.TokensPerSecond:0.0} tok/s · first token {stats.TimeToFirstTokenMs / 1000:0.00}s · prompt {stats.PromptTokens} tokens, {stats.CachedTokens} from cache · {stats.FinishReason}]"));
        if (reply.Length > 0)
        {
            _history.Add(new ChatMessage("assistant", reply.ToString()));
            PersistConversation(rt, stats);
        }
    }

    private void PersistConversation(ModelRuntime rt, GenerationStats stats)
    {
        _conversation.Model = rt.Identifier;
        _conversation.SystemPrompt = _gen.SystemPrompt;
        _conversation.Generation = _gen.Clone();
        _conversation.Messages = _history.Select(m => new StoredMessage { Role = m.Role, Content = m.Content }).ToList();
        if (_conversation.Messages.Count > 0)
        {
            _conversation.Messages[^1].TokensPerSecond = Math.Round(stats.TokensPerSecond, 1);
            _conversation.Messages[^1].CompletionTokens = stats.CompletionTokens;
        }
        _host.Chats.Save(_conversation);
    }

    private bool Undo()
    {
        while (_history.Count > 0)
        {
            var last = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (last.Role == "user") break;
        }
        Ui.Info(Ui.Dim("Removed the last exchange."));
        return true;
    }

    private async Task<bool> RegenAsync()
    {
        if (_history.Count > 0 && _history[^1].Role == "assistant") _history.RemoveAt(_history.Count - 1);
        if (_history.Count == 0) { Ui.Error("nothing to regenerate"); return false; }
        var rt = await _host.Models.ResolveAsync(_activeModel);
        await GenerateReplyAsync(rt);
        return true;
    }

    private bool ShowHistory()
    {
        foreach (var m in _history)
            Ui.Info((m.Role == "user" ? Ui.Cyan("you: ") : Ui.Green("ai:  ")) + m.Content);
        if (_history.Count == 0) Ui.Info(Ui.Dim("(empty)"));
        return true;
    }

    private bool ListChats()
    {
        Ui.Table(_host.Chats.List().Take(30).Select(c => new[]
            { c.Id, c.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), c.Model ?? "", c.Messages.Count.ToString(), c.Title }),
            "ID", "UPDATED", "MODEL", "MSGS", "TITLE");
        return true;
    }

    private bool Resume(Args a)
    {
        var c = a.Positional.Count > 0 ? _host.Chats.Get(a.Positional[0]) : null;
        if (c == null) { Ui.Error("usage: resume <chat id> (see /chats)"); return false; }
        _conversation = c;
        _history.Clear();
        _history.AddRange(c.ActivePath().Where(m => m.Role is "user" or "assistant" or "system").Select(m => new ChatMessage(m.Role, m.Content)));
        if (c.SystemPrompt != null) _gen.SystemPrompt = c.SystemPrompt;
        Ui.Info($"Resumed '{c.Title}' ({c.Messages.Count} messages).");
        return true;
    }

    private bool Export(Args a)
    {
        string file = a.Positional.FirstOrDefault() ?? $"chat-{_conversation.Id}.md";
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_gen.SystemPrompt)) sb.AppendLine($"> **System:** {_gen.SystemPrompt}\n");
        foreach (var m in _history) sb.AppendLine($"**{(m.Role == "user" ? "You" : "Assistant")}:**\n\n{m.Content}\n");
        File.WriteAllText(file, sb.ToString());
        Ui.Info($"Exported to {Path.GetFullPath(file)}");
        return true;
    }

    // ===================================================================== raw utilities

    private async Task<bool> CompleteAsync(Args a)
    {
        var rt = await _host.Models.ResolveAsync(_activeModel);
        var stats = new GenerationStats();
        _genCts = new CancellationTokenSource();
        try
        {
            await foreach (var piece in rt.StreamAsync(string.Join(' ', a.Positional), _gen, stats, _genCts.Token))
                Console.Write(piece);
        }
        finally { _genCts = null; }
        Console.WriteLine();
        Ui.Info(Ui.Dim($"[{stats.CompletionTokens} tokens · {stats.TokensPerSecond:0.0} tok/s]"));
        return true;
    }

    /// <summary>rerank &lt;query&gt; &lt;document&gt;... [--model m] — orders the documents by relevance to the query.</summary>
    private async Task<bool> RerankAsync(Args a)
    {
        if (a.Positional.Count < 2) { Ui.Error("usage: rerank <query> <document> [<document> ...] [--model <reranker or embedding model>]"); return false; }
        var rt = await _host.Models.ResolveAsync(a.Value("model"), embeddings: true);
        string query = a.Positional[0];
        var docs = a.Positional.Skip(1).ToList();
        var res = await rt.RerankAsync(query, docs);
        Ui.Info(Ui.Dim($"{rt.Identifier} ({res.Method})"));
        foreach (var i in Enumerable.Range(0, docs.Count).OrderByDescending(i => res.Scores[i]))
            Ui.Info($"{res.Scores[i],9:F4}  [{i}] {docs[i]}");
        return true;
    }

    private async Task<bool> EmbedAsync(Args a)
    {
        var rt = await _host.Models.ResolveAsync(a.Value("model"), embeddings: true);
        var v = await rt.EmbedAsync(string.Join(' ', a.Positional));
        Ui.Info($"{v.Length} dimensions: [{string.Join(", ", v.Take(8).Select(x => x.ToString("0.0000")))}, ...]");
        return true;
    }

    private async Task<bool> AttachVideoAsync(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Info("Usage: /video <file> [--fps 1] [--frames 8]   (frames are sent as images; needs ffmpeg)"); return true; }
        double fps = a.Value("fps") is { } f ? double.Parse(f, System.Globalization.CultureInfo.InvariantCulture) : 1;
        int frames = a.Value("frames") is { } n ? int.Parse(n) : VideoFrames.DefaultMaxFrames;
        try
        {
            var imgs = await VideoFrames.ExtractAsync(a.Positional[0], fps, frames, ImageInput.EdgeFor(null) is > 0 and var e ? e : 1024);
            _pendingImages.AddRange(imgs);
            Ui.Info($"{imgs.Count} frame(s) from {Path.GetFileName(a.Positional[0])} queued ({_pendingImages.Count} image(s) in total).");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or ArgumentException or FormatException)
        { Ui.Error(ex.Message); return false; }
    }

    private bool AttachAudio(Args a)
    {
        if (a.Positional.Count == 0) { Ui.Info("Usage: /audio <file.wav|mp3|flac> [more files]"); return true; }
        foreach (var path in a.Positional)
        {
            if (!File.Exists(path)) { Ui.Error($"file not found: {path}"); return false; }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > ImageInput.MaxBytes) { Ui.Error($"{path} is larger than {ImageInput.MaxBytes / (1024 * 1024)} MB"); return false; }
            if (!ImageInput.IsAudio(bytes)) { Ui.Error($"{path} is not WAV, MP3 or FLAC audio"); return false; }
            _pendingImages.Add(bytes);
        }
        var rt = _activeModel != null ? _host.Models.Get(_activeModel) : null;
        Ui.Info($"{_pendingImages.Count} attachment(s) will be sent with your next message." +
                (rt != null && !rt.SupportsAudio ? Ui.Yellow(" Warning: the active model cannot listen to audio.") : ""));
        return true;
    }

    /// <summary>template [model] — prints the chat template stored in a model file and the prompt it produces for a sample chat.</summary>
    private bool ShowTemplate(Args a)
    {
        string? query = a.Positional.FirstOrDefault() ?? _activeModel;
        var model = query == null ? null : _host.Library.Find(query);
        if (model == null) { Ui.Error("usage: template <model>   (a downloaded model)"); return false; }
        var info = FitEstimator.ReadModel(model.Path).Info;
        if (!info.HasChatTemplate) { Ui.Info("The model file has no chat template (ChatML is used)."); return true; }
        Ui.Info(Ui.Dim("--- chat template ---"));
        Ui.Info(info.ChatTemplate!);
        try
        {
            var (prompt, engine) = PromptTemplates.Preview(model, new PromptTemplateSettings(), PromptTemplates.SampleConversation, true);
            Ui.Info(Ui.Dim($"--- sample prompt ({engine}) ---"));
            Ui.Info(prompt);
        }
        catch (Exception ex) when (ex is JinjaException or InvalidOperationException) { Ui.Info(Ui.Yellow("Could not render a sample: " + ex.Message)); }
        return true;
    }

    private bool AttachImage(Args a)
    {
        if (a.Positional.Count == 0)
        {
            Ui.Info($"{_pendingImages.Count} image(s) queued for the next message. Usage: /image <file> [more files]   (/image clear)");
            return true;
        }
        if (a.Positional[0] == "clear") { _pendingImages.Clear(); Ui.Info("Cleared."); return true; }
        foreach (var path in a.Positional)
        {
            if (!File.Exists(path)) { Ui.Error($"file not found: {path}"); return false; }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > ImageInput.MaxBytes) { Ui.Error($"{path} is larger than {ImageInput.MaxBytes / (1024 * 1024)} MB"); return false; }
            _pendingImages.Add(ImageInput.Resize(bytes, ImageInput.EdgeFor(null)));
        }
        var rt = _activeModel != null ? _host.Models.Get(_activeModel) : null;
        Ui.Info($"{_pendingImages.Count} image(s) will be sent with your next message." +
                (rt != null && !rt.SupportsVision ? Ui.Yellow(" Warning: the active model has no vision support.") : ""));
        return true;
    }

    private async Task<bool> CacheAsync(Args a)
    {
        var loaded = _host.Models.Loaded;
        if (a.Positional.FirstOrDefault() == "clear")
        {
            foreach (var r in loaded) await r.ClearCacheAsync();
            Ui.Info("Prompt cache cleared.");
            return true;
        }
        if (a.Positional.FirstOrDefault() is "on" or "off")
        {
            _gen.CachePrompt = a.Positional[0] == "on";
            Ui.Info($"Prompt cache {(_gen.CachePrompt ? "enabled" : "disabled")}.");
            return true;
        }
        Ui.Info($"Prompt cache: {(_gen.CachePrompt ? "on" : "off")}   (cache on|off, cache clear)");
        foreach (var r in loaded)
            Ui.Info($"  {r.Identifier}: {r.CachedTokenCount} of {r.ContextSize} tokens cached");
        return true;
    }

    private async Task<bool> TokensAsync(Args a)
    {
        var rt = await _host.Models.ResolveAsync(_activeModel);
        Ui.Info($"{rt.CountTokens(string.Join(' ', a.Positional))} tokens");
        return true;
    }

    // ===================================================================== server / logs / remote chat / interactive get

    /// <summary>server start|stop|status — the daemon under another name (lms server ...).</summary>
    private async Task<bool> ServerVerbAsync(Args a)
    {
        string sub = a.Positional.FirstOrDefault() ?? "";
        var rest = new List<string>(a.Positional.Skip(1));
        string verb = sub switch { "start" => "up", "stop" => "down", "status" => "status", _ => "" };
        if (verb == "") return await ServeAsync(a);
        var args = new List<string> { verb };
        args.AddRange(rest);
        if (a.Value("port") is { } p) { args.Add("--port"); args.Add(p); }
        if (a.Flag("lan")) args.Add("--lan");
        if (a.Flag("json")) args.Add("--json");
        return await DaemonCommandAsync(new Args(args.ToArray()));
    }

    /// <summary>log stream [--local] — follows the log of the running daemon (or of this process).</summary>
    private async Task<bool> LogStreamAsync(Args a)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var daemon = a.Flag("local") ? null : await Daemon.ProbeAsync();
        if (daemon == null)
        {
            Ui.Info(Ui.Dim("Streaming this process's log (Ctrl+C to stop)."));
            foreach (var l in LogBuffer.Tail(20)) Ui.Info(Ui.Dim(l));
            void OnLine(string l) => Console.WriteLine(Ui.Dim(l));
            LogBuffer.Written += OnLine;
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
            LogBuffer.Written -= OnLine;
            return true;
        }
        Ui.Info(Ui.Dim($"Streaming the log of the daemon at {daemon.Value.Info.Url} (Ctrl+C to stop)."));
        using var http = new HttpClient { BaseAddress = new Uri(daemon.Value.Info.Url), Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrEmpty(daemon.Value.Info.Token))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", daemon.Value.Info.Token);
        string? last = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var lines = JsonSerializer.Deserialize<string[]>(await http.GetStringAsync("/api/logs?n=300", cts.Token)) ?? Array.Empty<string>();
                int start = 0;
                if (last != null)
                {
                    int idx = Array.LastIndexOf(lines, last);
                    start = idx >= 0 ? idx + 1 : 0;
                }
                else start = Math.Max(0, lines.Length - 20);
                for (int i = start; i < lines.Length; i++) Console.WriteLine(Ui.Dim(lines[i]));
                if (lines.Length > 0) last = lines[^1];
                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Ui.Error("lost the daemon: " + ex.Message); return false; }
        }
        return true;
    }

    /// <summary>Search Hugging Face, pick a repository, pick a quantization, download.</summary>
    private async Task<bool> GetInteractiveAsync(string? term, Args a)
    {
        if (Console.IsInputRedirected) { Ui.Error("usage: get <repo|catalog-key|link|url> [quant|file]  (the interactive picker needs a terminal)"); return false; }
        if (string.IsNullOrWhiteSpace(term))
        {
            Console.Write("Search Hugging Face for GGUF models: ");
            term = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(term)) return false;
        }
        var found = await HuggingFace.SearchAsync(term, 15);
        if (found.Count == 0) { Ui.Info("Nothing found."); return false; }
        for (int i = 0; i < found.Count; i++) Ui.Info($"  {i + 1,2}. {found[i].Id}  {Ui.Dim($"{found[i].Downloads:N0} downloads")}");
        Console.Write("Pick a model [1-" + found.Count + "], Enter to cancel: ");
        if (!int.TryParse(Console.ReadLine(), out int pick) || pick < 1 || pick > found.Count) return false;
        string repo = found[pick - 1].Id;

        var files = await HuggingFace.ListFilesAsync(repo);
        if (files.Count == 0) { Ui.Info("That repository has no GGUF files."); return false; }
        var hw = Hardware.Detect();
        long vram = hw.Gpus.Sum(g => g.UsableBytes), ram = hw.TotalRamBytes;
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            string fit = vram > 0 && f.SizeBytes < vram * 0.85 ? Ui.Green("fits GPU") : f.SizeBytes < ram * 0.8 ? "fits RAM" : Ui.Red("too large");
            Ui.Info($"  {i + 1,2}. {f.Quant,-8} {Ui.Bytes(f.SizeBytes),10}  {fit}  {Ui.Dim(f.FileName)}{(f.HasVision ? Ui.Cyan(" [vision]") : "")}");
        }
        Console.Write("Pick a file [1-" + files.Count + "], Enter to cancel: ");
        if (!int.TryParse(Console.ReadLine(), out int fp) || fp < 1 || fp > files.Count) return false;
        var chosen = files[fp - 1];
        Ui.Info($"Downloading {chosen.FileName} ({Ui.Bytes(chosen.SizeBytes)}) from {repo}");
        var job = _host.Downloads.Start(chosen, !a.Flag("no-mmproj"));
        return await WaitForDownload(job);
    }

    /// <summary>chat --server &lt;url&gt; [--token t] [model]: a terminal chat against a running (daemon) server.</summary>
    public async Task<int> RemoteChatAsync(string baseUrl, string? token, string? model)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        try
        {
            var ml = JsonNode.Parse(await http.GetStringAsync("/v1/models"))?["data"] as JsonArray;
            var loaded = ml?.Where(m => m?["state"]?.GetValue<string>() == "loaded").Select(m => m!["id"]!.GetValue<string>()).ToList() ?? new List<string>();
            model ??= loaded.FirstOrDefault();
            Ui.Info(Ui.Bold("LLMHost chat") + Ui.Dim($"  — connected to {baseUrl}" + (model != null ? $", model {model}" : ", no model loaded (it loads on the first request)")));
        }
        catch (Exception ex) { Ui.Error($"Cannot reach {baseUrl}: {ex.Message}"); return 1; }

        var history = new JsonArray();
        string? system = null;
        using var cancelCts = new CancellationTokenSource();
        CancellationTokenSource? turn = null;
        Console.CancelKeyPress += (_, e) => { if (turn is { IsCancellationRequested: false }) { e.Cancel = true; turn.Cancel(); } };
        Ui.Info(Ui.Dim("Type a message. /clear, /model <id>, /system <text>, /quit"));
        while (true)
        {
            Console.Write(Ui.Cyan((model ?? "server") + "> "));
            string? line = Console.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line is "/quit" or "/exit" or "/q") break;
            if (line == "/clear") { history.Clear(); Ui.Info(Ui.Dim("Conversation cleared.")); continue; }
            if (line.StartsWith("/model ")) { model = line[7..].Trim(); continue; }
            if (line.StartsWith("/system ")) { system = line[8..].Trim(); continue; }

            history.Add(new JsonObject { ["role"] = "user", ["content"] = line });
            var msgs = new JsonArray();
            if (!string.IsNullOrEmpty(system)) msgs.Add(new JsonObject { ["role"] = "system", ["content"] = system });
            foreach (var m in history) msgs.Add(m!.DeepClone());
            var req = new JsonObject { ["messages"] = msgs, ["stream"] = true };
            if (model != null) req["model"] = model;

            turn = new CancellationTokenSource();
            var answer = new StringBuilder();
            try
            {
                using var content = new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content },
                    HttpCompletionOption.ResponseHeadersRead, turn.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    Ui.Error((JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["error"]?["message"]?.GetValue<string>()) ?? resp.ReasonPhrase ?? "request failed");
                    history.RemoveAt(history.Count - 1);
                    continue;
                }
                using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(turn.Token));
                string? l;
                string tps = "";
                while ((l = await reader.ReadLineAsync(turn.Token)) != null)
                {
                    if (!l.StartsWith("data:")) continue;
                    string d = l[5..].Trim();
                    if (d == "[DONE]") break;
                    var j = JsonNode.Parse(d);
                    var delta = j?["choices"]?[0]?["delta"];
                    if (delta?["reasoning_content"]?.GetValue<string>() is { } r) Console.Write(Ui.Dim(r));
                    if (delta?["content"]?.GetValue<string>() is { } c) { Console.Write(c); answer.Append(c); }
                    if (j?["stats"]?["tokens_per_second"] is { } t) tps = $"{t.GetValue<double>():0.0} tok/s";
                }
                Console.WriteLine();
                if (tps.Length > 0) Ui.Info(Ui.Dim("[" + tps + "]"));
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer.ToString() });
            }
            catch (OperationCanceledException)
            {
                Console.Write(Ui.Dim(" [stopped]\n"));
                if (answer.Length > 0) history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer.ToString() });
                else history.RemoveAt(history.Count - 1);
            }
            catch (Exception ex) { Ui.Error(ex.Message); history.RemoveAt(history.Count - 1); }
            finally { turn = null; }
        }
        return 0;
    }

    // ===================================================================== parsing

    public static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false, has = false;
        char quote = '"';
        foreach (var ch in line)
        {
            if (inQuote)
            {
                if (ch == quote) inQuote = false; else cur.Append(ch);
            }
            else if (ch is '"' or '\'') { inQuote = true; quote = ch; has = true; }
            else if (char.IsWhiteSpace(ch))
            {
                if (has || cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); has = false; }
            }
            else cur.Append(ch);
        }
        if (has || cur.Length > 0) result.Add(cur.ToString());
        return result;
    }
}

/// <summary>Positional arguments plus --flag / --key value options.</summary>
public class Args
{
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Booleans = new(StringComparer.OrdinalIgnoreCase)
        { "cpu", "cpu-moe", "another", "no-normalize", "no-flash", "no-mmap", "mlock", "embeddings", "bg", "y", "lan", "no-vision", "no-mmproj", "move", "link", "copy", "skip-fit-check", "force", "daemon", "service", "json", "all", "follow", "f", "local" };

    public Args(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var s = args[i];
            if (s.StartsWith("--") && s.Length > 2)
            {
                string key = s[2..];
                int eq = key.IndexOf('=');
                if (eq > 0) { _opts[key[..eq]] = key[(eq + 1)..]; continue; }
                if (key.Equals("gpu", StringComparison.OrdinalIgnoreCase))
                {
                    // --gpu max|auto|off|<0..1> carries a value; a bare --gpu means "all layers"
                    bool val = i + 1 < args.Length && System.Text.RegularExpressions.Regex.IsMatch(args[i + 1], @"^(max|all|auto|off|cpu|0|1|0?\.\d+|1\.0+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    _opts[key] = val ? args[++i] : null;
                }
                else if (Booleans.Contains(key) || i + 1 >= args.Length || args[i + 1].StartsWith("--")) _opts[key] = null;
                else _opts[key] = args[++i];
            }
            else Positional.Add(s);
        }
    }

    public bool Flag(string name) => _opts.ContainsKey(name);
    public string? Value(params string[] names)
    {
        foreach (var n in names) if (_opts.TryGetValue(n, out var v) && v != null) return v;
        return null;
    }
}
