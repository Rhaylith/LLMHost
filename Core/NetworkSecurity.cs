using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LLMHostProject.Core;

/// <summary>Who may reach the server, how much they may send and how, settings.json → <c>network</c>.</summary>
public class NetworkSettings
{
    /// <summary>Addresses allowed to connect: <c>loopback</c>, <c>private</c>, <c>any</c>, a single address or CIDR (<c>192.168.1.0/24</c>, <c>fd00::/8</c>). Empty = loopback only, plus private ranges when the server listens on the LAN.</summary>
    public List<string> AllowedNetworks { get; set; } = new();
    public LimitSettings Limits { get; set; } = new();
    public TlsSettings Tls { get; set; } = new();
}

public class LimitSettings
{
    /// <summary>Largest request body in MB for chat and other API calls, and for file uploads (documents, model import).</summary>
    public int MaxBodyMb { get; set; } = 32;
    public int MaxUploadMb { get; set; } = 256;
    /// <summary>Most simultaneous connections (0 = no limit); takes effect after a restart.</summary>
    public int MaxConnections { get; set; }
    /// <summary>Requests per minute for one API token / one client address (0 = no limit). The app's own window is exempt.</summary>
    public int RequestsPerMinutePerToken { get; set; }
    public int RequestsPerMinutePerIp { get; set; }
    /// <summary>Largest <c>n</c> and <c>max_tokens</c> a request may ask for (0 = no cap).</summary>
    public int MaxChoices { get; set; }
    public int MaxTokens { get; set; }
}

public class TlsSettings
{
    /// <summary>off | selfSigned | file</summary>
    public string Mode { get; set; } = "off";
    public int Port { get; set; } = 1235;
    /// <summary>file mode: a .pfx (with <see cref="PfxPassword"/>) or a PEM certificate (with <see cref="KeyPath"/>).</summary>
    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }
    public string? PfxPassword { get; set; }
}

/// <summary>Matches client addresses against the allow-list.</summary>
public sealed class IpAllowList
{
    private readonly List<(IPAddress Net, int Bits)> _ranges = new();
    private bool _loopback, _private, _any;

    public IpAllowList(IEnumerable<string> entries)
    {
        foreach (var raw in entries)
        {
            string e = raw.Trim();
            if (e.Length == 0) continue;
            switch (e.ToLowerInvariant())
            {
                case "loopback": _loopback = true; continue;
                case "private": _private = true; continue;
                case "any": case "*": _any = true; continue;
            }
            string addr = e; int? bits = null;
            int slash = e.IndexOf('/');
            if (slash >= 0)
            {
                addr = e[..slash];
                if (!int.TryParse(e[(slash + 1)..], out var b)) throw new ArgumentException($"'{e}' is not a valid network (use 192.168.1.0/24).");
                bits = b;
            }
            if (!IPAddress.TryParse(addr, out var ip)) throw new ArgumentException($"'{e}' is not an IP address or network.");
            ip = Unmap(ip);
            int max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (bits is < 0 || bits > max) throw new ArgumentException($"'{e}': the prefix must be 0-{max}.");
            _ranges.Add((ip, bits ?? max));
        }
    }

    /// <summary>The default list: this machine only, plus the private ranges when the server listens on the network.</summary>
    public static string[] DefaultFor(string bindHost) =>
        bindHost is "127.0.0.1" or "localhost" or "::1" ? new[] { "loopback" } : new[] { "loopback", "private" };

    private static IPAddress Unmap(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public static bool IsPrivate(IPAddress ip)
    {
        ip = Unmap(ip);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    public bool Allows(IPAddress? ip)
    {
        if (_any) return true;
        if (ip == null) return false;
        ip = Unmap(ip);
        if (_loopback && IPAddress.IsLoopback(ip)) return true;
        if (_private && IsPrivate(ip)) return true;
        foreach (var (net, bits) in _ranges)
            if (net.AddressFamily == ip.AddressFamily && Matches(net.GetAddressBytes(), ip.GetAddressBytes(), bits)) return true;
        return false;
    }

    private static bool Matches(byte[] a, byte[] b, int bits)
    {
        int full = bits / 8, rest = bits % 8;
        for (int i = 0; i < full; i++) if (a[i] != b[i]) return false;
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        return (a[full] & mask) == (b[full] & mask);
    }
}

/// <summary>Token bucket per key: <c>perMinute</c> requests may come in a burst, and the bucket refills evenly over the minute.</summary>
public sealed class RequestRateLimiter
{
    private sealed class Bucket { public double Tokens; public long Last; }
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly Func<long> _nowMs;

    public RequestRateLimiter(Func<long>? clockMs = null) => _nowMs = clockMs ?? (() => Environment.TickCount64);

    /// <summary>Takes one request from the bucket of <paramref name="key"/>; false (with the seconds to wait) when it is empty.</summary>
    public bool TryAcquire(string key, int perMinute, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (perMinute <= 0) return true;
        var b = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = perMinute, Last = _nowMs() });
        lock (b)
        {
            long now = _nowMs();
            b.Tokens = Math.Min(perMinute, b.Tokens + (now - b.Last) / 60000.0 * perMinute);
            b.Last = now;
            if (b.Tokens >= 1) { b.Tokens -= 1; return true; }
            retryAfterSeconds = (int)Math.Ceiling((1 - b.Tokens) / perMinute * 60);
            return false;
        }
    }

    /// <summary>Forgets buckets that have been full for a while (called now and then so the table does not grow without end).</summary>
    public void Trim(int perMinute)
    {
        long now = _nowMs();
        foreach (var kv in _buckets) if (now - kv.Value.Last > 120_000 && _buckets.TryRemove(kv.Key, out _)) { }
    }
}

/// <summary>The self-signed server certificate for HTTPS: ECDSA P-256, valid two years, names for localhost, this machine and its addresses.</summary>
public static class TlsCerts
{
    private static string Dir => Path.Combine(AppPaths.Root, "tls");
    private static string PfxPath => Path.Combine(Dir, "server.pfx");
    private static string PassPath => Path.Combine(Dir, "server.pass");

    public static IReadOnlyList<string> LocalNames()
    {
        var names = new List<string> { "localhost", Environment.MachineName.ToLowerInvariant(), "127.0.0.1", "::1" };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal) names.Add(a.Address.ToString());
        }
        catch { }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static X509Certificate2 Generate(IEnumerable<string> names, int days = 730)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=LLMHost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names) { if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(n); }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(days));
        // re-import so the private key is usable by Kestrel (ephemeral keys cannot be used for TLS on Windows)
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    /// <summary>The stored certificate, made anew when none exists or it expires within 30 days.</summary>
    public static X509Certificate2 LoadOrCreate(DateTime? now = null)
    {
        Directory.CreateDirectory(Dir);
        string pass = File.Exists(PassPath) ? SecretStore.Unprotect(File.ReadAllText(PassPath)) ?? "" : "";
        if (File.Exists(PfxPath) && pass.Length > 0)
        {
            try
            {
                var c = new X509Certificate2(PfxPath, pass, X509KeyStorageFlags.Exportable);
                if (c.NotAfter > (now ?? DateTime.Now).AddDays(30)) return c;
                c.Dispose();
            }
            catch (CryptographicException) { }
        }
        pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var cert = Generate(LocalNames());
        File.WriteAllBytes(PfxPath, cert.Export(X509ContentType.Pfx, pass));
        File.WriteAllText(PassPath, SecretStore.Protect(pass));
        return cert;
    }

    public static X509Certificate2 FromSettings(TlsSettings t)
    {
        if (t.Mode == "selfSigned") return LoadOrCreate();
        if (string.IsNullOrWhiteSpace(t.CertPath) || !File.Exists(t.CertPath)) throw new FileNotFoundException("The certificate file was not found: " + t.CertPath);
        X509Certificate2 c = t.CertPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || t.CertPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
            ? new X509Certificate2(t.CertPath, t.PfxPassword, X509KeyStorageFlags.Exportable)
            : X509Certificate2.CreateFromPemFile(t.CertPath, string.IsNullOrWhiteSpace(t.KeyPath) ? null : t.KeyPath);
        return new X509Certificate2(c.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    /// <summary>The public certificate in PEM, for clients that should trust a self-signed server.</summary>
    public static string ExportPem(X509Certificate2 c) =>
        "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(c.RawData, Base64FormattingOptions.InsertLineBreaks) + "\n-----END CERTIFICATE-----\n";

    public static IReadOnlyList<string> Names(X509Certificate2 c)
    {
        var ext = c.Extensions.OfType<X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        return ext == null ? Array.Empty<string>() : new AsnEncodedData(ext.Oid!, ext.RawData).Format(true).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
    }
}

/// <summary>The caps on how much one request may ask for.</summary>
public static class RequestCaps
{
    /// <summary>A message when the body asks for more choices (<c>n</c>) or tokens than allowed, else null. 0 = no cap.</summary>
    public static string? Check(System.Text.Json.Nodes.JsonObject body, int maxChoices, int maxTokens)
    {
        long? Num(string key) => body[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
        if (maxChoices > 0 && Num("n") is { } n && n > maxChoices) return $"n is {n}; this server allows at most {maxChoices}.";
        if (maxTokens > 0)
            foreach (var key in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens", "n_predict" })
                if (Num(key) is { } t && t > maxTokens) return $"{key} is {t}; this server allows at most {maxTokens}.";
        return null;
    }
}
