using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMHostProject.Core;

/// <summary>Linking your own devices (settings.json → <c>link</c>). Off by default.</summary>
public class LinkSettings
{
    public bool Enabled { get; set; }
    /// <summary>How this device is called by the others (default: the machine name).</summary>
    public string DeviceName { get; set; } = "";
    /// <summary>Send requests to this device when it has the model (blank = use this device).</summary>
    public string PreferredDevice { get; set; } = "";
    /// <summary>When the preferred device is offline, answer from this device and say so.</summary>
    public bool FailoverToLocal { get; set; } = true;
}

/// <summary>A paired device. <see cref="Permissions"/>: chat (use the models), load (load / unload), manage (downloads and the rest of model management).</summary>
public class LinkPeer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Base address of the peer (for example <c>http://100.64.0.7:1234</c> on a Tailscale network).</summary>
    public string Address { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public List<string> Permissions { get; set; } = new() { "chat" };
    public DateTime PairedUtc { get; set; } = DateTime.UtcNow;

    public bool Allows(string method, string path)
    {
        path = path.ToLowerInvariant();
        bool read = method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        if (path.StartsWith("/api/link/info") || path.StartsWith("/v1/models") && read) return Permissions.Count > 0;
        if (path.StartsWith("/v1/")) return Permissions.Contains("chat") || Permissions.Contains("manage");
        if (path is "/api/models/load" or "/api/models/unload") return Permissions.Contains("load") || Permissions.Contains("manage");
        if (path.StartsWith("/api/models/") || path.StartsWith("/api/downloads")) return Permissions.Contains("manage");
        return false;
    }
}

/// <summary>What a device tells its peers about itself.</summary>
public record LinkInfo(string DeviceId, string Name, string Version, IReadOnlyList<LinkModel> Models, long RamFreeBytes, long VramFreeBytes);
public record LinkModel(string Id, bool Loaded, string Type);

/// <summary>A peer as the page and the CLI see it, with what the last check found.</summary>
public record LinkPeerView(string Id, string Name, string Address, IReadOnlyList<string> Permissions, bool Online, DateTime? LastSeenUtc, IReadOnlyList<LinkModel> Models);

/// <summary>
/// Identity, pairing and signed requests between your own devices. The network itself (Tailscale, ZeroTier, WireGuard, a LAN) is yours; this
/// adds who-is-who on top: every device has an ECDSA P-256 key, pairing swaps public keys under a short code, and every request between devices
/// is signed (method, path, time, nonce, body hash) and checked against the paired keys.
/// </summary>
public sealed class LinkManager
{
    private readonly string _dir;
    private readonly Func<LinkSettings> _settings;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, DateTime> _nonces = new();
    private readonly ConcurrentDictionary<string, (bool Online, DateTime? Seen, List<LinkModel> Models)> _status = new();
    private ECDsa? _key;
    private List<LinkPeer>? _peers;
    private (string Code, DateTime Expires, int Tries)? _pairing;

    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(60);

    public LinkManager(string directory, Func<LinkSettings> settings) { _dir = directory; _settings = settings; }

    public bool Enabled => _settings().Enabled;
    public string DeviceName => string.IsNullOrWhiteSpace(_settings().DeviceName) ? Environment.MachineName : _settings().DeviceName.Trim();
    private string PeersFile => Path.Combine(_dir, "peers.json");
    private string KeyFile => Path.Combine(_dir, "device.key");

    // ---- identity

    private ECDsa Key
    {
        get
        {
            lock (_gate)
            {
                if (_key != null) return _key;
                Directory.CreateDirectory(_dir);
                var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                try
                {
                    if (File.Exists(KeyFile) && SecretStore.Unprotect(File.ReadAllText(KeyFile)) is { } stored) k.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
                    else File.WriteAllText(KeyFile, SecretStore.Protect(Convert.ToBase64String(k.ExportPkcs8PrivateKey())));
                }
                catch (CryptographicException) { k = ECDsa.Create(ECCurve.NamedCurves.nistP256); File.WriteAllText(KeyFile, SecretStore.Protect(Convert.ToBase64String(k.ExportPkcs8PrivateKey()))); }
                return _key = k;
            }
        }
    }

    public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    public static string IdOf(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey)))[..16].ToLowerInvariant();
    public string DeviceId => IdOf(PublicKey);

    // ---- peers

    public IReadOnlyList<LinkPeer> Peers { get { lock (_gate) return LoadPeers().ToList(); } }
    private List<LinkPeer> LoadPeers()
    {
        if (_peers != null) return _peers;
        try { _peers = File.Exists(PeersFile) ? JsonSerializer.Deserialize<List<LinkPeer>>(File.ReadAllText(PeersFile), Json.Options) ?? new() : new(); }
        catch { _peers = new(); }
        return _peers;
    }
    private void SavePeers() { Directory.CreateDirectory(_dir); File.WriteAllText(PeersFile, JsonSerializer.Serialize(_peers, Json.Options)); }

    public LinkPeer? FindPeer(string idOrName) { lock (_gate) return LoadPeers().FirstOrDefault(p => p.Id == idOrName || p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)); }

    public void AddPeer(LinkPeer p)
    {
        lock (_gate)
        {
            var all = LoadPeers();
            all.RemoveAll(x => x.Id == p.Id);
            all.Add(p); SavePeers();
        }
    }

    public bool RemovePeer(string idOrName)
    {
        lock (_gate)
        {
            var all = LoadPeers();
            int n = all.RemoveAll(p => p.Id == idOrName || p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (n > 0) SavePeers();
            return n > 0;
        }
    }

    public bool SetPermissions(string idOrName, IEnumerable<string> permissions)
    {
        var valid = new[] { "chat", "load", "manage" };
        var list = permissions.Select(p => p.Trim().ToLowerInvariant()).Distinct().ToList();
        if (list.Any(p => !valid.Contains(p))) throw new ArgumentException("Permissions are chat, load and manage.");
        lock (_gate)
        {
            var p = FindPeer(idOrName); if (p == null) return false;
            p.Permissions = list; SavePeers(); return true;
        }
    }

    public IReadOnlyList<LinkPeerView> Views() => Peers.Select(p =>
    {
        _status.TryGetValue(p.Id, out var s);
        return new LinkPeerView(p.Id, p.Name, p.Address, p.Permissions, s.Online, s.Seen, (IReadOnlyList<LinkModel>?)s.Models ?? Array.Empty<LinkModel>());
    }).ToList();

    public void SetStatus(string peerId, bool online, IEnumerable<LinkModel>? models = null)
    {
        _status.AddOrUpdate(peerId, _ => (online, online ? DateTime.UtcNow : null, models?.ToList() ?? new()),
            (_, old) => (online, online ? DateTime.UtcNow : old.Seen, online ? models?.ToList() ?? old.Models : old.Models));
    }

    public IReadOnlyList<LinkModel> ModelsOf(string peerId) => _status.TryGetValue(peerId, out var s) && s.Online ? s.Models : Array.Empty<LinkModel>();
    public bool IsOnline(string peerId) => _status.TryGetValue(peerId, out var s) && s.Online;

    // ---- pairing: a six-digit code shown on one device and typed on the other; both sides prove they know it

    /// <summary>A new code, valid five minutes, for one successful pairing (five wrong tries end it).</summary>
    public string StartPairing()
    {
        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
        lock (_gate) _pairing = (code, DateTime.UtcNow.AddMinutes(5), 0);
        return code;
    }

    private static string Mac(string code, string data) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(data)));

    public record PairRequest(string Name, string Address, string PublicKey, string Mac);
    public record PairReply(string Name, string PublicKey, string Mac);

    /// <summary>The device that showed the code receives the other's request. Returns null when the code is wrong, expired or used up.</summary>
    public PairReply? AcceptPairing(PairRequest r)
    {
        lock (_gate)
        {
            if (_pairing is not { } p || DateTime.UtcNow > p.Expires) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Mac(p.Code, r.PublicKey)), Encoding.UTF8.GetBytes(r.Mac ?? "")))
            {
                _pairing = p.Tries + 1 >= 5 ? null : (p.Code, p.Expires, p.Tries + 1);
                return null;
            }
            _pairing = null;
            AddPeer(new LinkPeer { Id = IdOf(r.PublicKey), Name = r.Name, Address = r.Address.TrimEnd('/'), PublicKey = r.PublicKey });
            return new PairReply(DeviceName, PublicKey, Mac(p.Code, PublicKey + "|" + r.PublicKey));
        }
    }

    /// <summary>The device that types the code: asks the other one and, when its answer proves it knew the code too, stores it.</summary>
    public async Task<LinkPeer> PairAsync(string address, string code, string ownAddress, HttpClient? http = null, CancellationToken ct = default)
    {
        address = address.TrimEnd('/');
        using var own = http == null ? CreateClient() : null;
        var client = http ?? own!;
        var req = new PairRequest(DeviceName, ownAddress, PublicKey, Mac(code.Trim(), PublicKey));
        using var resp = await client.PostAsync(address + "/api/link/pair/accept", new StringContent(JsonSerializer.Serialize(req, Json.Options), Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("The other device did not accept the code (wrong, expired, or pairing is not switched on there).");
        var reply = JsonSerializer.Deserialize<PairReply>(await resp.Content.ReadAsStringAsync(ct), Json.Options)!;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Mac(code.Trim(), reply.PublicKey + "|" + PublicKey)), Encoding.UTF8.GetBytes(reply.Mac ?? "")))
            throw new InvalidOperationException("The answer did not prove knowledge of the code: this may not be the device you meant. Nothing was stored.");
        var peer = new LinkPeer { Id = IdOf(reply.PublicKey), Name = reply.Name, Address = address, PublicKey = reply.PublicKey };
        AddPeer(peer);
        return peer;
    }

    // ---- signed requests

    public static HttpClient CreateClient()
    {
        // peers may answer with a self-signed certificate; requests are signed, so the certificate is not what identifies them
        var h = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        return new HttpClient(h) { Timeout = TimeSpan.FromMinutes(30) };
    }

    private static string Canonical(string method, string pathAndQuery, string ts, string nonce, byte[] body) =>
        $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{ts}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(body))}";

    /// <summary>Adds the identity and signature headers to a request that goes to a peer.</summary>
    public void Sign(HttpRequestMessage req, byte[] body)
    {
        string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        string sig = Convert.ToBase64String(Key.SignData(Encoding.UTF8.GetBytes(Canonical(req.Method.Method, req.RequestUri!.PathAndQuery, ts, nonce, body)), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        req.Headers.Add("X-Link-Device", DeviceId);
        req.Headers.Add("X-Link-Time", ts);
        req.Headers.Add("X-Link-Nonce", nonce);
        req.Headers.Add("X-Link-Signature", sig);
    }

    /// <summary>The paired device that sent a signed request, or null with the reason. A request can be used once, within a minute.</summary>
    public (LinkPeer? Peer, string? Error) Verify(string method, string pathAndQuery, Func<string, string?> header, byte[] body, DateTime? now = null)
    {
        string? id = header("X-Link-Device"), ts = header("X-Link-Time"), nonce = header("X-Link-Nonce"), sig = header("X-Link-Signature");
        if (id == null || ts == null || nonce == null || sig == null) return (null, "Missing link headers.");
        if (!Enabled) return (null, "Linking is switched off on this device.");
        var peer = FindPeer(id);
        if (peer == null) return (null, "Unknown device.");
        if (!long.TryParse(ts, out var ms)) return (null, "Bad time.");
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        if ((now ?? DateTime.UtcNow) - t > MaxSkew || t - (now ?? DateTime.UtcNow) > MaxSkew) return (null, "The request is too old or too new (check the clocks).");
        try
        {
            using var pub = ECDsa.Create();
            pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peer.PublicKey), out _);
            if (!pub.VerifyData(Encoding.UTF8.GetBytes(Canonical(method, pathAndQuery, ts, nonce, body)), Convert.FromBase64String(sig), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return (null, "Bad signature.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return (null, "Bad signature."); }
        // a nonce is accepted once; old ones are forgotten
        foreach (var kv in _nonces) if ((now ?? DateTime.UtcNow) - kv.Value > TimeSpan.FromMinutes(5)) _nonces.TryRemove(kv.Key, out _);
        if (!_nonces.TryAdd(id + nonce, now ?? DateTime.UtcNow)) return (null, "This request was already used.");
        return (peer, null);
    }

    /// <summary>"device/model" → (peer, model) when the first part names a paired device.</summary>
    public (LinkPeer? Peer, string Model) SplitModel(string? requested)
    {
        if (string.IsNullOrEmpty(requested)) return (null, requested ?? "");
        int slash = requested.IndexOf('/');
        if (slash <= 0) return (null, requested);
        var peer = FindPeer(requested[..slash]);
        return peer == null ? (null, requested) : (peer, requested[(slash + 1)..]);
    }
}
