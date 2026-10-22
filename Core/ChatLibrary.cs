using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMHostProject.Core;

/// <summary>A folder in the chat list. Folders nest through <see cref="ParentId"/>; <see cref="Order"/> sorts siblings.</summary>
public class ChatFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public int Order { get; set; }
}

/// <summary>The folder tree, kept in <c>chat-folders.json</c> (next to, not inside, the chats folder).</summary>
public class ChatFolderStore
{
    private readonly object _gate = new();
    private static string FilePath => Path.Combine(AppPaths.Root, "chat-folders.json");

    private List<ChatFolder> Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var node = JsonNode.Parse(File.ReadAllText(FilePath));
            return node?["items"]?.Deserialize<List<ChatFolder>>(Json.Options) ?? new();
        }
        catch { return new(); }
    }

    private void Write(List<ChatFolder> items)
    {
        AppPaths.EnsureCreated();
        var node = new JsonObject { ["schemaVersion"] = 1, ["items"] = JsonSerializer.SerializeToNode(items, Json.Options) };
        Migrations.WriteAtomic(FilePath, node.ToJsonString(Json.Options));
    }

    public IReadOnlyList<ChatFolder> List() { lock (_gate) return Read().OrderBy(f => f.Order).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(); }

    /// <summary>Creates the folder or changes name / parent / order. Refuses empty names, a missing parent and loops.</summary>
    public ChatFolder Upsert(string id, string name, string? parentId, int? order)
    {
        if (!ChatStore.ValidId(id)) throw new ArgumentException("Invalid folder id.");
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("A folder needs a name.");
        if (name.Length > 80) throw new ArgumentException("The folder name is too long.");
        lock (_gate)
        {
            var all = Read();
            if (!string.IsNullOrEmpty(parentId) && all.All(f => f.Id != parentId)) throw new ArgumentException("The parent folder does not exist.");
            if (!string.IsNullOrEmpty(parentId) && (parentId == id || IsInside(all, parentId, id))) throw new ArgumentException("A folder cannot be placed inside itself.");
            var f = all.FirstOrDefault(x => x.Id == id);
            if (f == null) { f = new ChatFolder { Id = id, Order = all.Count == 0 ? 0 : all.Max(x => x.Order) + 1 }; all.Add(f); }
            f.Name = name;
            f.ParentId = string.IsNullOrEmpty(parentId) ? null : parentId;
            if (order.HasValue) f.Order = order.Value;
            Write(all);
            return f;
        }
    }

    /// <summary>True when <paramref name="folder"/> lies somewhere below <paramref name="ancestor"/>.</summary>
    private static bool IsInside(List<ChatFolder> all, string folder, string ancestor)
    {
        var seen = new HashSet<string>();
        string? cur = folder;
        while (cur != null && seen.Add(cur))
        {
            if (cur == ancestor) return true;
            cur = all.FirstOrDefault(f => f.Id == cur)?.ParentId;
        }
        return false;
    }

    /// <summary>Removes a folder and everything below it; returns the ids of all removed folders (the caller moves or deletes their chats).</summary>
    public IReadOnlyList<string> Delete(string id)
    {
        lock (_gate)
        {
            var all = Read();
            var doomed = all.Where(f => f.Id == id || IsInside(all, f.Id, id)).Select(f => f.Id).ToList();
            if (doomed.Count == 0) return doomed;
            Write(all.Where(f => !doomed.Contains(f.Id)).ToList());
            return doomed;
        }
    }
}

public record ChatHit(string ChatId, string Title, string? MessageId, int MessageIndex, string Role, string Snippet, DateTime UpdatedAt);

/// <summary>
/// Full-text search over all chats. Words are lower-cased and mapped to the chats that contain them (an inverted index built on the first
/// search and kept current through the store's save / delete events); every word of a query must occur in a chat, the last one may be a prefix.
/// </summary>
public class ChatSearchIndex
{
    private readonly ChatStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Conversation> _chats = new();
    private bool _built;

    private static readonly Regex Word = new(@"[\p{L}\p{N}_]+", RegexOptions.Compiled);

    public ChatSearchIndex(ChatStore store)
    {
        _store = store;
        store.Saved += c => { lock (_gate) { if (_built) Put(c); } };
        store.Deleted += id => { lock (_gate) { if (_built) Remove(id); } };
    }

    public static IEnumerable<string> Tokens(string text) => Word.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 1 || char.IsDigit(w[0]));

    private void Remove(string id)
    {
        if (!_chats.Remove(id)) return;
        foreach (var set in _postings.Values) set.Remove(id);
    }

    private void Put(Conversation c)
    {
        Remove(c.Id);
        _chats[c.Id] = c;
        foreach (var w in Tokens(c.Title).Concat(c.Messages.Where(m => m.Role is "user" or "assistant").SelectMany(m => Tokens(m.Content))).Distinct())
        {
            if (!_postings.TryGetValue(w, out var set)) _postings[w] = set = new();
            set.Add(c.Id);
        }
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        foreach (var c in _store.List()) Put(c);
        _built = true;
    }

    public int Count { get { lock (_gate) { EnsureBuilt(); return _chats.Count; } } }

    public IReadOnlyList<ChatHit> Search(string query, int limit = 50)
    {
        var terms = Tokens(query ?? "").ToList();
        if (terms.Count == 0) return Array.Empty<ChatHit>();
        lock (_gate)
        {
            EnsureBuilt();
            HashSet<string>? ids = null;
            for (int i = 0; i < terms.Count; i++)
            {
                string t = terms[i];
                bool prefix = i == terms.Count - 1;
                var found = new HashSet<string>();
                if (_postings.TryGetValue(t, out var exact)) found.UnionWith(exact);
                if (prefix) foreach (var kv in _postings) if (kv.Key.StartsWith(t, StringComparison.Ordinal)) found.UnionWith(kv.Value);
                ids = ids == null ? found : new HashSet<string>(ids.Intersect(found));
                if (ids.Count == 0) return Array.Empty<ChatHit>();
            }
            var hits = new List<ChatHit>();
            foreach (var c in ids!.Select(id => _chats[id]).OrderByDescending(c => c.UpdatedAt).Take(limit))
                hits.Add(Hit(c, terms));
            return hits;
        }
    }

    private static ChatHit Hit(Conversation c, List<string> terms)
    {
        string first = terms[0];
        for (int i = 0; i < c.Messages.Count; i++)
        {
            var m = c.Messages[i];
            if (m.Role is not ("user" or "assistant")) continue;
            int at = m.Content.IndexOf(first, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            return new ChatHit(c.Id, c.Title, m.Id, i, m.Role, Snippet(m.Content, at, first.Length), c.UpdatedAt);
        }
        return new ChatHit(c.Id, c.Title, null, -1, "title", c.Title, c.UpdatedAt);                       // the match is in the title
    }

    /// <summary>About 120 characters around the match, on one line.</summary>
    public static string Snippet(string text, int at, int len)
    {
        int from = Math.Max(0, at - 50), to = Math.Min(text.Length, at + len + 70);
        string s = Regex.Replace(text[from..to], @"\s+", " ").Trim();
        return (from > 0 ? "…" : "") + s + (to < text.Length ? "…" : "");
    }
}

/// <summary>Writing chats out as JSON / Markdown / text, and reading them back from our own JSON and from ChatGPT-style exports.</summary>
public static class ChatTransfer
{
    public static string ToMarkdown(Conversation c)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(c.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(c.SystemPrompt)) sb.AppendLine("> **System:** " + c.SystemPrompt.Replace("\n", "\n> ")).AppendLine();
        foreach (var m in c.ActivePath())
        {
            if (m.Role == "tool") { sb.AppendLine($"**Tool result{(m.Name != null ? " (" + m.Name + ")" : "")}:**").AppendLine().AppendLine("```").AppendLine(m.Content).AppendLine("```").AppendLine(); continue; }
            sb.Append("## ").AppendLine(m.Role == "user" ? "You" : m.Role == "assistant" ? "Assistant" : m.Role).AppendLine();
            if (!string.IsNullOrWhiteSpace(m.Reasoning)) sb.AppendLine("<details><summary>Reasoning</summary>").AppendLine().AppendLine(m.Reasoning).AppendLine().AppendLine("</details>").AppendLine();
            sb.AppendLine(m.Content).AppendLine();
        }
        return sb.ToString();
    }

    public static string ToText(Conversation c)
    {
        var sb = new StringBuilder();
        sb.AppendLine(c.Title).AppendLine(new string('=', Math.Min(60, Math.Max(3, c.Title.Length)))).AppendLine();
        foreach (var m in c.ActivePath())
            sb.Append(m.Role == "user" ? "You: " : m.Role == "assistant" ? "Assistant: " : m.Role + ": ").AppendLine(m.Content).AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Reads one or many chats from JSON: our own format (a chat, or an array of chats), a ChatGPT <c>conversations.json</c>
    /// (<c>mapping</c> tree) or a bare <c>{ "messages": [{role, content}] }</c>. New ids are given, so nothing is overwritten.
    /// </summary>
    public static List<Conversation> Import(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException ex) { throw new ArgumentException("Not valid JSON: " + ex.Message); }
        var items = root is JsonArray arr ? arr.Select(x => x).ToList() : new List<JsonNode?> { root };
        var result = new List<Conversation>();
        foreach (var n in items)
        {
            if (n is not JsonObject o) continue;
            Conversation? c = o["mapping"] is JsonObject map ? FromChatGpt(o, map) : o["messages"] is JsonArray ? FromNative(o) : null;
            if (c != null && c.Messages.Count > 0) result.Add(c);
        }
        if (result.Count == 0) throw new ArgumentException("No conversation found in the file.");
        return result;
    }

    private static Conversation FromNative(JsonObject o)
    {
        var c = o.Deserialize<Conversation>(Json.Options) ?? new Conversation();
        c.Id = Guid.NewGuid().ToString("N")[..12];
        c.FolderId = null; c.Pinned = false; c.Documents = new();                // folders and documents belong to this installation
        if (string.IsNullOrWhiteSpace(c.Title)) c.Title = "Imported chat";
        // chats without message ids (plain {role, content} lists) get a linear tree
        if (c.Messages.All(m => m.Id == null))
        {
            string? parent = null;
            foreach (var m in c.Messages) { m.Id = Guid.NewGuid().ToString("N")[..8]; m.ParentId = parent; parent = m.Id; }
            c.ActiveLeaf = parent;
        }
        c.Messages = c.Messages.Where(m => m.Role is "user" or "assistant" or "system" or "tool").ToList();
        return c;
    }

    private static Conversation? FromChatGpt(JsonObject o, JsonObject mapping)
    {
        // follow the branch that was current when the export was made: from current_node up to the root
        string? cur = o["current_node"]?.GetValue<string>();
        var chain = new List<JsonObject>();
        var seen = new HashSet<string>();
        while (cur != null && mapping[cur] is JsonObject node && seen.Add(cur))
        {
            chain.Add(node);
            cur = node["parent"]?.GetValue<string>();
        }
        chain.Reverse();
        var c = new Conversation { Title = o["title"]?.GetValue<string>() is { Length: > 0 } t ? t : "Imported chat" };
        if (o["create_time"] is JsonValue ct && ct.TryGetValue<double>(out var secs)) c.CreatedAt = DateTimeOffset.FromUnixTimeSeconds((long)secs).UtcDateTime;
        string? parent = null;
        foreach (var node in chain)
        {
            var msg = node["message"] as JsonObject;
            string? role = msg?["author"]?["role"]?.GetValue<string>();
            if (role is not ("user" or "assistant" or "system")) continue;
            string text = string.Join("\n", (msg!["content"]?["parts"] as JsonArray ?? new()).Select(p => p is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0));
            if (text.Length == 0) continue;
            if (role == "system") { c.SystemPrompt = text; continue; }
            var m = new StoredMessage { Id = Guid.NewGuid().ToString("N")[..8], ParentId = parent, Role = role, Content = text };
            c.Messages.Add(m);
            parent = m.Id;
        }
        c.ActiveLeaf = parent;
        return c.Messages.Count == 0 ? null : c;
    }

    /// <summary>Title text from a model's answer: first line, no quotes or trailing dot, at most 60 characters.</summary>
    public static string CleanTitle(string raw)
    {
        string t = (raw ?? "").Trim().Split('\n')[0].Trim().Trim('"', '\'', '“', '”', '*', '#', ' ').TrimEnd('.', ':');
        t = Regex.Replace(t, @"^(title|subject)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
        return t.Length > 60 ? t[..60].TrimEnd() + "…" : t;
    }

    /// <summary>The fallback title: the first 40 characters of what the user wrote.</summary>
    public static string FallbackTitle(string userText)
    {
        string t = Regex.Replace(userText ?? "", @"\s+", " ").Trim();
        return t.Length == 0 ? "New chat" : t.Length > 40 ? t[..40].TrimEnd() + "…" : t;
    }
}
