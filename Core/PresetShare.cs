using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMHostProject.Core;

/// <summary>What the user is shown before an imported preset is saved.</summary>
public record PresetPreview(string Name, string? Description, string? SystemPrompt, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Dropped, IReadOnlyList<string> Unknown, bool Exists, string SuggestedName);

/// <summary>Getting a preset from a file, pasted text or an https address, and checking it before anything is stored.</summary>
public static class PresetShare
{
    public const int MaxBytes = 256 * 1024;

    /// <summary>GitHub "blob" pages show HTML; the same file is served raw from raw.githubusercontent.com.</summary>
    public static string RewriteGitHub(string url)
    {
        var m = Regex.Match(url, @"^https://github\.com/([^/]+)/([^/]+)/blob/(.+)$", RegexOptions.IgnoreCase);
        return m.Success ? $"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}" : url;
    }

    /// <summary>Whether an address may be contacted: loopback, private, link-local and unspecified addresses are not (a preset URL must not reach into the user's network).</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    private static HttpClient CreateClient(Func<string, Task<IPAddress[]>>? resolver = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (ctx, ct) =>
            {
                // the address is checked at the moment of connecting, so a name cannot answer differently afterwards
                var addrs = resolver != null ? await resolver(ctx.DnsEndPoint.Host) : await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                var ok = addrs.FirstOrDefault(IsPublicAddress) ?? throw new HttpRequestException("That address points into a private network and is not contacted.");
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await s.ConnectAsync(new IPEndPoint(ok, ctx.DnsEndPoint.Port), ct); return new NetworkStream(s, ownsSocket: true); }
                catch { s.Dispose(); throw; }
            },
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("llmhost/1.0");
        return c;
    }

    /// <summary>Downloads a preset document: https only, at most 256 KB, JSON content, redirects (at most 3) must stay on https.</summary>
    public static async Task<string> FetchAsync(string url, CancellationToken ct = default, HttpClient? client = null)
    {
        url = RewriteGitHub((url ?? "").Trim());
        using var own = client == null ? CreateClient() : null;
        var http = client ?? own!;
        for (int hop = 0; hop <= 3; hop++)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Only https:// addresses can be imported.");
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc)
            {
                url = RewriteGitHub(new Uri(uri, loc).ToString());
                continue;
            }
            resp.EnsureSuccessStatusCode();
            string type = resp.Content.Headers.ContentType?.MediaType ?? "";
            // raw.githubusercontent.com serves .json as text/plain; anything that is not text or JSON is refused
            if (!(type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The address does not serve JSON (it says '{type}').");
            if (resp.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("The file is larger than 256 KB.");
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBytes) throw new InvalidDataException("The file is larger than 256 KB.");
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        throw new InvalidDataException("Too many redirects.");
    }

    /// <summary>Checks a document and lists what it would set, so the user can confirm before it is saved.</summary>
    public static PresetPreview Preview(string json, PresetStore store, string? fallbackName = null)
    {
        if (json.Length > MaxBytes) throw new ArgumentException("The preset is larger than 256 KB.");
        var r = PresetRules.Check(json, fallbackName);
        if (r.Preset == null) throw new ArgumentException(r.Error);
        var fields = r.Preset.Inference.ToDictionary(kv => kv.Key, kv => kv.Value?.ToJsonString() ?? "null");
        bool exists = store.Get(r.Preset.Name) != null;
        string suggested = r.Preset.Name;
        for (int i = 2; store.Get(suggested) != null; i++) suggested = PresetRules.SafeName($"{r.Preset.Name} ({i})")!;
        return new PresetPreview(r.Preset.Name, r.Preset.Description, r.Preset.SystemPrompt, fields, r.Dropped, r.Unknown, exists, suggested);
    }

    /// <summary>Saves a checked preset. A name collision needs an explicit <paramref name="overwrite"/> or another <paramref name="name"/>.</summary>
    public static string Import(string json, PresetStore store, string? name = null, bool overwrite = false, string? fallbackName = null)
    {
        var r = PresetRules.Check(json, fallbackName);
        if (r.Preset == null) throw new ArgumentException(r.Error);
        var p = r.Preset;
        if (!string.IsNullOrWhiteSpace(name)) p.Name = PresetRules.SafeName(name) ?? throw new ArgumentException("Invalid name.");
        if (!overwrite && store.Get(p.Name) != null) throw new InvalidOperationException($"A preset named '{p.Name}' already exists.");
        return store.Save(p);
    }

    /// <summary>The file a preset is exported as: pretty JSON the importer reads back.</summary>
    public static string Export(PresetFile p) => JsonSerializer.Serialize(p, Json.Options);

    // ---- .llmpreset file association (current user only: HKCU\Software\Classes)

    public static void RegisterFileType(string exePath)
    {
        using var ext = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\.llmpreset");
        ext.SetValue("", "LLMHost.Preset");
        using var prog = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\LLMHost.Preset");
        prog.SetValue("", "LLMHost preset");
        using var cmd = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\LLMHost.Preset\shell\open\command");
        cmd.SetValue("", $"\"{exePath}\" preset import \"%1\"");
    }

    public static void UnregisterFileType()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.llmpreset", false);
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\LLMHost.Preset", false);
    }
}
