using System.Text.Json;
using LLMHostProject.Inference;
using LLMHostProject.Models;

namespace LLMHostProject.Core;

public class StoredMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public double? TokensPerSecond { get; set; }
    public int? CompletionTokens { get; set; }
    /// <summary>Attached images as data URLs (vision chats).</summary>
    public List<string>? Images { get; set; }
    /// <summary>The model's reasoning ("thinking"), kept apart from the answer.</summary>
    public string? Reasoning { get; set; }
    /// <summary>Tool calls the assistant made in this turn (OpenAI shape: id, name, arguments).</summary>
    public System.Text.Json.Nodes.JsonNode? ToolCalls { get; set; }
    /// <summary>For role "tool": which call this answers, and the tool name.</summary>
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }
    /// <summary>Identity inside the conversation tree; edits and regenerations become siblings (same <see cref="ParentId"/>).</summary>
    public string? Id { get; set; }
    public string? ParentId { get; set; }
    /// <summary>What the generation of this assistant message measured (tokens, speed, time to first token, stop reason, cache hits, draft acceptance).</summary>
    public MessageStats? Stats { get; set; }
}

public class ConversationDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Chars { get; set; }
}

public class Conversation
{
    /// <summary>Format version of the chat file (see <see cref="Migrations"/>).</summary>
    public int SchemaVersion { get; set; } = Migrations.CurrentVersion(Migrations.Chat);
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = "New chat";
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? SystemPrompt { get; set; }
    public GenerationOptions? Generation { get; set; }
    /// <summary>All messages of the conversation tree (every variant); see <see cref="ActivePath"/>.</summary>
    public List<StoredMessage> Messages { get; set; } = new();
    /// <summary>Id of the last message of the branch currently shown (null = the last message).</summary>
    public string? ActiveLeaf { get; set; }
    /// <summary>Documents attached to the conversation (ids in the document store).</summary>
    public List<ConversationDocument> Documents { get; set; } = new();
    /// <summary>Folder in the chat list (null = top level) and whether the chat is pinned to the top.</summary>
    public string? FolderId { get; set; }
    public bool Pinned { get; set; }
    /// <summary>The title was made up by the app (first words or a model suggestion) and may be replaced; a title the user typed is never touched.</summary>
    public bool TitleAuto { get; set; }
    /// <summary>Per-chat settings: the preset followed, the fields changed for this chat, and load options kept for a later reload (see <see cref="SettingsResolver"/>).</summary>
    public ChatSettings? Settings { get; set; }

    /// <summary>The branch that is currently active, root first. Chats saved without ids are one linear branch.</summary>
    public List<StoredMessage> ActivePath()
    {
        if (Messages.Count == 0 || Messages.All(m => m.Id == null)) return Messages.ToList();
        var byId = Messages.Where(m => m.Id != null).GroupBy(m => m.Id!).ToDictionary(g => g.Key, g => g.First());
        string? cur = ActiveLeaf != null && byId.ContainsKey(ActiveLeaf) ? ActiveLeaf : Messages.Last().Id;
        var path = new List<StoredMessage>();
        var seen = new HashSet<string>();
        while (cur != null && byId.TryGetValue(cur, out var m) && seen.Add(cur)) { path.Add(m); cur = m.ParentId; }
        path.Reverse();
        return path;
    }
}

/// <summary>Persists conversations as one JSON file each under ~/.llmhost/chats.</summary>
public class ChatStore
{
    /// <summary>Raised after a chat was written or deleted (the search index follows these).</summary>
    public event Action<Conversation>? Saved;
    public event Action<string>? Deleted;

    /// <summary>Chat ids are letters and digits only; anything else could not name a file inside the chats folder.</summary>
    public static bool ValidId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(char.IsAsciiLetterOrDigit);

    private static string PathFor(string id) => Path.Combine(AppPaths.ChatsDir, Sanitize(id) + ".json");
    private static string Sanitize(string id) => new(id.Where(char.IsLetterOrDigit).ToArray());

    public IReadOnlyList<Conversation> List()
    {
        AppPaths.EnsureCreated();
        var list = new List<Conversation>();
        foreach (var f in Directory.EnumerateFiles(AppPaths.ChatsDir, "*.json"))
        {
            try
            {
                var c = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(f), Json.Options);
                if (c != null) list.Add(c);
            }
            catch { }
        }
        return list.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public Conversation? Get(string id)
    {
        var p = PathFor(id);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<Conversation>(File.ReadAllText(p), Json.Options); }
        catch { return null; }
    }

    public Conversation Save(Conversation c)
    {
        AppPaths.EnsureCreated();
        c.UpdatedAt = DateTime.UtcNow;
        c.SchemaVersion = Migrations.CurrentVersion(Migrations.Chat);
        if (c.Title is "New chat" or "" && c.Messages.FirstOrDefault(m => m.Role == "user") is { } first)
        {
            c.Title = first.Content.Length > 60 ? first.Content[..60] + "…" : first.Content;
            c.TitleAuto = true;
        }
        File.WriteAllText(PathFor(c.Id), JsonSerializer.Serialize(c, Json.Options));
        Saved?.Invoke(c);
        return c;
    }

    public bool Delete(string id)
    {
        var p = PathFor(id);
        if (!File.Exists(p)) return false;
        File.Delete(p);
        Deleted?.Invoke(id);
        return true;
    }
}

/// <summary>Everything the CLI and the HTTP server share.</summary>
public class AppHost : IDisposable
{
    public AppSettings Settings => AppSettings.Current;
    public ModelLibrary Library { get; } = new();
    public ModelManager Models { get; }
    public DownloadManager Downloads { get; }
    public ChatStore Chats { get; } = new();
    public ChatFolderStore ChatFolders { get; } = new();
    public ChatSearchIndex ChatSearch { get; }
    public PresetStore Presets { get; } = new();
    public ModelDefaultsStore ModelDefaults { get; } = new();
    public McpManager Mcp { get; } = new();
    public ModelUpdateChecker Updates { get; }
    public ModelDirectoryManager Directories { get; }
    private readonly System.Threading.Timer _updateTimer;
    /// <summary>Completed when something (the API, a service stop) asks the process to shut down.</summary>
    public TaskCompletionSource Shutdown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AppHost()
    {
        AppPaths.EnsureCreated();
        Migrations.RunAll();
        Directory.CreateDirectory(Settings.ResolvedModelsDirectory);
        Models = new ModelManager(Library, ModelDefaults);
        Downloads = new DownloadManager { MaxConcurrent = Settings.MaxConcurrentDownloads };
        Updates = new ModelUpdateChecker(Library);
        ChatSearch = new ChatSearchIndex(Chats);
        Directories = new ModelDirectoryManager(Library, Downloads, () => Models.Loaded.Select(r => r.Model.Path));
        // the daily check (when switched on): first one a few minutes after start, then every 24 hours
        _updateTimer = new System.Threading.Timer(_ =>
        {
            if (!Settings.CheckModelUpdates) return;
            try { Updates.CheckAsync().GetAwaiter().GetResult(); } catch (Exception ex) { LogBuffer.Add("download", "Update check failed: " + ex.Message); }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
    }

    /// <summary>Generation defaults for a request to <paramref name="modelId"/>: per-model settings, else the global ones.</summary>
    public GenerationOptions GenerationDefaultsFor(string? modelId) =>
        (modelId != null ? ModelDefaults.GenerationFor(modelId) : null) ?? Settings.DefaultGeneration;

    public void Dispose() { _updateTimer.Dispose(); Mcp.Dispose(); Models.Dispose(); }
}

/// <summary>Per-message numbers kept with an assistant message; all optional, older chats have none.</summary>
public class MessageStats
{
    public int? Tokens { get; set; }
    public int? PromptTokens { get; set; }
    public double? TokensPerSecond { get; set; }
    public double? TtftSeconds { get; set; }
    public double? GenerationSeconds { get; set; }
    public string? StopReason { get; set; }
    public int? PromptCachedTokens { get; set; }
    public int? DraftTokens { get; set; }
    public int? DraftAccepted { get; set; }
    /// <summary>Seconds the model needed to load for this request (only when it was loaded on demand).</summary>
    public double? LoadSeconds { get; set; }
    /// <summary>How long the model reasoned before the answer started ("Thought for 12 s").</summary>
    public double? ThinkSeconds { get; set; }
}
