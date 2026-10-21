using System.Net;
using System.Net.Sockets;
using LLMHostProject.Core;

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

    // ------------------------------------------------------------------ size / detail

    /// <summary>
    /// Longest edge allowed for an image with the given OpenAI-style <c>detail</c>: "low" uses the low-detail edge
    /// (default 512), "high" and "auto" the general limit from the vision settings (0 = keep the original size).
    /// </summary>
    public static int EdgeFor(string? detail, VisionSettings? v = null)
    {
        v ??= AppSettings.Current.Vision;
        return string.Equals(detail, "low", StringComparison.OrdinalIgnoreCase) ? Math.Max(32, v.LowDetailEdge) : Math.Max(0, v.MaxImageEdge);
    }

    /// <summary>Same as <see cref="FromUrl(string)"/>, then shrinks the picture to the edge that <paramref name="detail"/> allows.</summary>
    public static byte[] FromUrl(string url, string? detail) => Resize(FromUrl(url), EdgeFor(detail));

    public static byte[] FromBase64(string data, string? detail) => Resize(FromBase64(data), EdgeFor(detail));

    /// <summary>
    /// Scales the image down (never up) so its longest edge is at most <paramref name="maxEdge"/> pixels. JPEGs stay JPEG, other
    /// formats become PNG. Data the system cannot decode is returned unchanged (the vision encoder may still read it).
    /// </summary>
    public static byte[] Resize(byte[] data, int maxEdge)
    {
        if (maxEdge <= 0) return data;
        try
        {
            using var input = new MemoryStream(data);
            using var src = System.Drawing.Image.FromStream(input);
            int w = src.Width, h = src.Height;
            if (Math.Max(w, h) <= maxEdge) return data;
            double scale = (double)maxEdge / Math.Max(w, h);
            int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
            using var bmp = new System.Drawing.Bitmap(nw, nh);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            using var output = new MemoryStream();
            if (src.RawFormat.Equals(System.Drawing.Imaging.ImageFormat.Jpeg))
            {
                var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using var ps = new System.Drawing.Imaging.EncoderParameters(1);
                ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                bmp.Save(output, enc, ps);
            }
            else bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch { return data; }
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
