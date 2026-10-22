using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMHostProject.Core;
using Microsoft.AspNetCore.Http;

namespace LLMHostProject.Server;

/// <summary>A named API token. Only a hash of the secret is stored; the secret is shown once when it is created.</summary>
public class ApiToken
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Hex SHA-256 of the secret.</summary>
    public string Hash { get; set; } = "";
    /// <summary>First characters of the secret, to recognise it in a list.</summary>
    public string Prefix { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public bool Disabled { get; set; }
}

public static class Scopes
{
    public const string Chat = "chat";               // chat/completions, completions, messages, responses, tokenize
    public const string Embeddings = "embeddings";
    public const string ModelsRead = "models:read";  // list models, status, hardware, catalog, downloads
    public const string ModelsManage = "models:manage"; // load / unload / download / import / delete
    public const string App = "app";                 // chats, presets, logs (the web UI's own data)
    public const string Admin = "admin";             // settings, tokens, runtimes; implies everything

    public static readonly string[] All = { Chat, Embeddings, ModelsRead, ModelsManage, App, Admin };
    public static readonly string[] Default = { Chat, Embeddings, ModelsRead };

    /// <summary>The permission an HTTP request needs (null = public).</summary>
    public static string? Required(string method, string path)
    {
        path = path.ToLowerInvariant();
        bool read = method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (path.StartsWith("/v1/embeddings") || path.StartsWith("/api/v0/embeddings")) return Embeddings;
        if (path == "/api/tls/cert") return null;
        if (path == "/metrics") return ModelsRead;                                // a public certificate is not a secret
        if (path.StartsWith("/api/v0/models")) return ModelsRead;
        if (path.StartsWith("/api/v0/")) return Chat;
        if (path.StartsWith("/v1/models")) return ModelsRead;
        if (path.StartsWith("/v1/")) return Chat;
        if (path.StartsWith("/api/v1/chat")) return Chat;
        if (path.StartsWith("/api/v1/models/load") || path.StartsWith("/api/v1/models/unload") || path.StartsWith("/api/v1/models/download"))
            return read ? ModelsRead : ModelsManage;
        if (path.StartsWith("/api/v1/models")) return ModelsRead;
        if (path.StartsWith("/api/settings") || path.StartsWith("/api/tokens") || path.StartsWith("/api/runtimes")
            || path.StartsWith("/api/mcp") || path.StartsWith("/api/shutdown")) return read && !path.StartsWith("/api/tokens") && !path.StartsWith("/api/settings") ? ModelsRead : Admin;
        if (path.StartsWith("/api/chats") || path.StartsWith("/api/chat-folders") || path.StartsWith("/api/context") || path == "/api/effective-settings" || path == "/api/shortcuts" || path.StartsWith("/api/presets") || path.StartsWith("/api/preset-list") || path.StartsWith("/api/logs") || path.StartsWith("/api/documents")) return App;
        if (path == "/api/status" || path == "/api/hardware") return ModelsRead;
        if (!read && (path.StartsWith("/api/models/directory") || path.StartsWith("/api/models/disk"))) return Admin;     // changes where files live
        if (path.StartsWith("/api/models/") || path.StartsWith("/api/downloads"))
        {
            if (read) return ModelsRead;
            return ModelsManage;
        }
        if (path.StartsWith("/api/")) return Admin;
        return null;
    }
}

/// <summary>Token store + request authorization.</summary>
public class ApiAuth
{
    private readonly AppHost _host;
    private readonly object _gate = new();
    private List<ApiToken>? _tokens;
    /// <summary>Per-process secret handed to the embedded web UI window; never stored, grants full access.</summary>
    public string UiToken { get; } = "ui_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public ApiAuth(AppHost host) { _host = host; }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private List<ApiToken> Load()
    {
        if (_tokens != null) return _tokens;
        try
        {
            if (File.Exists(AppPaths.TokensFile))
                _tokens = JsonSerializer.Deserialize<List<ApiToken>>(File.ReadAllText(AppPaths.TokensFile), Json.Options);
        }
        catch { }
        return _tokens ??= new List<ApiToken>();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.TokensFile, JsonSerializer.Serialize(_tokens, Json.Options));
    }

    public IReadOnlyList<ApiToken> List() { lock (_gate) return Load().Select(t => t).ToList(); }

    /// <summary>Creates a token. The returned secret is the only time it can be seen.</summary>
    public (ApiToken Token, string Secret) Create(string name, IEnumerable<string>? permissions)
    {
        var perms = (permissions ?? Scopes.Default).Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct().ToList();
        var bad = perms.Where(p => !Scopes.All.Contains(p)).ToList();
        if (bad.Count > 0) throw new ArgumentException($"Unknown permission(s): {string.Join(", ", bad)}. Valid: {string.Join(", ", Scopes.All)}.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A token needs a name.");
        string secret = "ait_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var t = new ApiToken
        {
            Id = Guid.NewGuid().ToString("N")[..8], Name = name.Trim(), Hash = Hash(secret), Prefix = secret[..8],
            Permissions = perms,
        };
        lock (_gate) { Load().Add(t); Save(); }
        return (t, secret);
    }

    public bool Revoke(string idOrName)
    {
        lock (_gate)
        {
            var list = Load();
            int n = list.RemoveAll(t => t.Id == idOrName || t.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
            return n > 0;
        }
    }

    public ApiToken? Update(string id, string? name, IEnumerable<string>? permissions, bool? disabled)
    {
        lock (_gate)
        {
            var t = Load().FirstOrDefault(x => x.Id == id);
            if (t == null) return null;
            if (!string.IsNullOrWhiteSpace(name)) t.Name = name.Trim();
            if (permissions != null)
            {
                var perms = permissions.Select(p => p.Trim().ToLowerInvariant()).Where(p => Scopes.All.Contains(p)).Distinct().ToList();
                t.Permissions = perms;
            }
            if (disabled.HasValue) t.Disabled = disabled.Value;
            Save();
            return t;
        }
    }

    /// <summary>True when requests must carry a token (the toggle, or the older single key).</summary>
    public bool Required => _host.Settings.RequireAuth || !string.IsNullOrEmpty(_host.Settings.ApiKey);

    private static bool Eq(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>0 = allowed, 401 = no / unknown token, 403 = token lacks the permission.</summary>
    /// <summary>Who a request is from, for the request log: "ui", "key" (the single API key), a token's name, or null when no secret was sent.</summary>
    public string? Identity(HttpRequest req)
    {
        string secret = req.Headers.Authorization.ToString();
        secret = secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? secret[7..].Trim() : "";
        if (secret.Length == 0) secret = req.Headers["x-api-key"].ToString();
        if (secret.Length == 0) return null;
        if (Eq(secret, UiToken)) return "ui";
        if (!string.IsNullOrEmpty(_host.Settings.ApiKey) && Eq(secret, _host.Settings.ApiKey)) return "key";
        string h = Hash(secret);
        lock (_gate) return Load().FirstOrDefault(x => Eq(x.Hash, h))?.Name ?? "unknown";
    }

    public int Authorize(HttpRequest req, out string message)
    {
        message = "";
        string? needed = Scopes.Required(req.Method, req.Path.Value ?? "");
        if (needed == null || !Required) return 0;

        string secret = req.Headers.Authorization.ToString();
        if (secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) secret = secret[7..].Trim();
        else secret = "";
        if (secret.Length == 0) secret = req.Headers["x-api-key"].ToString();
        if (secret.Length == 0) { message = "Missing API token (send 'Authorization: Bearer <token>')"; return 401; }

        if (Eq(secret, UiToken)) return 0;
        var legacy = _host.Settings.ApiKey;
        if (!string.IsNullOrEmpty(legacy) && Eq(secret, legacy)) return 0;     // the single key keeps full access

        string h = Hash(secret);
        lock (_gate)
        {
            var t = Load().FirstOrDefault(x => Eq(x.Hash, h));
            if (t == null || t.Disabled) { message = "Invalid or revoked API token"; return 401; }
            if (!t.Permissions.Contains(Scopes.Admin) && !t.Permissions.Contains(needed))
            { message = $"This token does not have the '{needed}' permission"; return 403; }
            t.LastUsedAt = DateTime.UtcNow;
            return 0;
        }
    }
}
