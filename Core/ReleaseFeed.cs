using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMHostProject.Core;

/// <summary>A version as in semver.org: <c>1.4.0</c>, <c>1.4.0-beta.2</c>. Build metadata (<c>+...</c>) is ignored when comparing.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Pre) : IComparable<SemVer>
{
    private static readonly Regex Rx = new(@"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.\d+)?(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$", RegexOptions.Compiled);

    public bool IsPrerelease => Pre.Length > 0;

    public static bool TryParse(string? text, out SemVer v)
    {
        v = default;
        var m = Rx.Match((text ?? "").Trim());
        if (!m.Success) return false;
        v = new SemVer(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0, m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0, m.Groups[4].Value);
        return true;
    }

    public static SemVer Parse(string text) => TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version.");

    public int CompareTo(SemVer o)
    {
        int c = Major.CompareTo(o.Major); if (c != 0) return c;
        c = Minor.CompareTo(o.Minor); if (c != 0) return c;
        c = Patch.CompareTo(o.Patch); if (c != 0) return c;
        // a release comes after its own pre-releases
        if (Pre.Length == 0 && o.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (o.Pre.Length == 0) return -1;
        var a = Pre.Split('.'); var b = o.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = int.TryParse(a[i], out var x), bn = int.TryParse(b[i], out var y);
            c = an && bn ? x.CompareTo(y) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);   // numbers rank below words
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Length > 0 ? "-" + Pre : "");
}

public record ReleaseAsset(string Name, string Url, string? Sha256, long Size);
public record ReleaseInfo(string Version, bool Prerelease, string? Notes, DateTime? Published, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Reads release feeds (GitHub Releases API, our own <c>releases.json</c>, or the older single-version document) and picks the update for a channel.</summary>
public static class ReleaseFeed
{
    /// <summary>The releases in a feed document. Accepts a GitHub API array, <c>{ "releases": [...] }</c>, or <c>{ "version", "url", "notes" }</c>.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = new List<ReleaseInfo>();
        string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("releases", out var rs) && rs.ValueKind == JsonValueKind.Array ? rs.EnumerateArray()
            : root.ValueKind == JsonValueKind.Object ? new[] { root } : Array.Empty<JsonElement>();

        foreach (var r in items)
        {
            string? version = S(r, "version") ?? S(r, "tag_name") ?? S(r, "name");
            if (version == null || !SemVer.TryParse(version, out var sv)) continue;
            bool pre = (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) || (S(r, "channel") is "beta" or "preview") || sv.IsPrerelease;
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            string? notes = S(r, "notes") ?? S(r, "body");
            DateTime? published = DateTime.TryParse(S(r, "published") ?? S(r, "published_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                {
                    string? name = S(a, "name"), url = S(a, "url") is { } u && u.StartsWith("http") ? u : S(a, "browser_download_url");
                    if (name == null || url == null) continue;
                    string? sha = S(a, "sha256") ?? (S(a, "digest") is { } dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? dg[7..] : null);
                    assets.Add(new ReleaseAsset(name, url, sha?.ToLowerInvariant(), a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0));
                }
            else if (S(r, "url") is { } single) assets.Add(new ReleaseAsset(Path.GetFileName(new Uri(single).AbsolutePath), single, S(r, "sha256")?.ToLowerInvariant(), 0));
            list.Add(new ReleaseInfo(sv.ToString(), pre, notes, published, assets));
        }
        return list;
    }

    /// <summary>Lines of a <c>SHA256SUMS</c> file (<c>hash  filename</c>) as a name → hash map.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success) map[m.Groups[2].Value.Trim()] = m.Groups[1].Value.ToLowerInvariant();
        }
        return map;
    }

    /// <summary>
    /// The newest release above <paramref name="current"/> for the channel: <c>stable</c> ignores pre-releases, <c>beta</c> takes the newest of all.
    /// A pre-release of the same number as the current stable build is not an update.
    /// </summary>
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases, string channel, SemVer current) =>
        releases.Where(r => (channel == "beta" || !r.Prerelease) && SemVer.TryParse(r.Version, out var v) && v > current)
                .OrderByDescending(r => SemVer.Parse(r.Version)).FirstOrDefault();

    /// <summary>
    /// The file to install: a portable <c>.zip</c> for win-x64 is preferred (replaced in place), then an <c>.msi</c>, then a setup <c>.exe</c>.
    /// Other platforms' files and checksum / signature files are never chosen.
    /// </summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo r)
    {
        bool Ok(ReleaseAsset a) => !Regex.IsMatch(a.Name, @"(linux|osx|macos|arm64|x86\b)", RegexOptions.IgnoreCase) && !a.Name.EndsWith(".sha256") && !a.Name.EndsWith(".sig") && !a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase);
        foreach (var ext in new[] { ".zip", ".msi", ".exe" })
            if (r.Assets.FirstOrDefault(a => Ok(a) && a.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return null;
    }
}
