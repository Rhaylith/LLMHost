using System.Net;
using System.Net.Sockets;

namespace LLMHostProject.Server;

/// <summary>Turns image references from API requests (data URLs, base64, http(s) URLs) into raw bytes.</summary>
public static class ImageInput
{
    public const int MaxImagesPerRequest = 8;
    public const int MaxBytes = 20 * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Accepts <c>data:image/png;base64,...</c>, a bare base64 string, or an http(s) URL.</summary>
    public static byte[] FromUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = url.IndexOf(',');
            if (comma < 0) throw new ArgumentException("Malformed data URL for image.");
            string meta = url[5..comma];
            string payload = url[(comma + 1)..];
            return meta.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? FromBase64(payload)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Download(new Uri(url));
        // bare base64
        return FromBase64(url);
    }

    public static byte[] FromBase64(string data)
    {
        try
        {
            var bytes = Convert.FromBase64String(data.Trim());
            if (bytes.Length > MaxBytes) throw new ArgumentException($"Image is larger than {MaxBytes / (1024 * 1024)} MB.");
            if (bytes.Length == 0) throw new ArgumentException("Image data is empty.");
            return bytes;
        }
        catch (FormatException) { throw new ArgumentException("Image data is not valid base64."); }
    }

    private static byte[] Download(Uri uri)
    {
        // basic SSRF protection: never fetch loopback / private / link-local addresses on behalf of a request
        foreach (var addr in Dns.GetHostAddresses(uri.Host))
            if (IsPrivate(addr))
                throw new ArgumentException("Fetching images from private or loopback addresses is not allowed; send a data URL instead.");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
            throw new ArgumentException($"Could not fetch image ({(int)resp.StatusCode}).");
        if (resp.Content.Headers.ContentLength is > MaxBytes)
            throw new ArgumentException($"Image is larger than {MaxBytes / (1024 * 1024)} MB.");
        using var s = resp.Content.ReadAsStream();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytes) throw new ArgumentException($"Image is larger than {MaxBytes / (1024 * 1024)} MB.");
        }
        return ms.ToArray();
    }

    private static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal || a.Equals(IPAddress.IPv6Any);
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }
}
