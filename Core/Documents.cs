using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace LLMHostProject.Core;

/// <summary>A document attached to a chat: plain text extracted from a .pdf, .docx, .txt or .md file.</summary>
public class StoredDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public int Pages { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Text extraction for the supported file types (no third-party libraries).</summary>
public static class DocumentExtractor
{
    public static readonly string[] Extensions = { ".pdf", ".docx", ".txt", ".md", ".markdown", ".csv", ".json", ".log", ".xml", ".html", ".htm", ".yaml", ".yml", ".ini", ".cfg", ".py", ".cs", ".js", ".ts", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".sql" };

    public static (string Text, int Pages) Extract(string fileName, byte[] data)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        switch (ext)
        {
            case ".pdf": return PdfText.Extract(data);
            case ".docx": return (DocxText(data), 0);
            case ".html": case ".htm":
                return (WebUtilityDecode(Regex.Replace(Regex.Replace(Encoding.UTF8.GetString(data), @"(?is)<(script|style).*?</\1>", " "), @"<[^>]+>", " ")), 0);
            default:
                if (!Extensions.Contains(ext) && LooksBinary(data))
                    throw new InvalidDataException($"'{fileName}' is not a supported document type (use .pdf, .docx, .txt or .md).");
                return (DecodeText(data), 0);
        }
    }

    private static string WebUtilityDecode(string s) => System.Net.WebUtility.HtmlDecode(Regex.Replace(s, @"[ \t]+", " "));

    private static bool LooksBinary(byte[] d)
    {
        int n = Math.Min(d.Length, 4096), bad = 0;
        for (int i = 0; i < n; i++) if (d[i] == 0 || d[i] < 9) bad++;
        return bad > n / 50;
    }

    private static string DecodeText(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) return Encoding.UTF8.GetString(d, 3, d.Length - 3);
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) return Encoding.Unicode.GetString(d, 2, d.Length - 2);
        if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(d, 2, d.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(d); }
        catch { return Encoding.Latin1.GetString(d); }
    }

    private static string DocxText(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("That .docx has no document body.");
        var sb = new StringBuilder();
        using var xr = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element)
            {
                switch (xr.LocalName)
                {
                    case "t": sb.Append(xr.ReadElementContentAsString()); break;
                    case "tab": sb.Append('\t'); break;
                    case "br": case "cr": sb.Append('\n'); break;
                }
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName is "p" or "tr") sb.Append('\n');
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }
}

/// <summary>
/// Small PDF text extractor: object scan (including compressed object streams), Flate content streams, text
/// operators and ToUnicode maps. Handles ordinary text PDFs; scanned (image only) or encrypted files are reported.
/// </summary>
public static class PdfText
{
    private sealed class Obj
    {
        public int Num;
        public string Dict = "";
        public byte[]? Stream;
    }

    public static (string Text, int Pages) Extract(byte[] pdf)
    {
        string latin = Encoding.Latin1.GetString(pdf);
        if (latin.Contains("/Encrypt")) throw new InvalidDataException("This PDF is encrypted and cannot be read.");
        var objs = new Dictionary<int, Obj>();
        ScanObjects(pdf, latin, objs);
        // expand object streams (PDF 1.5+)
        foreach (var o in objs.Values.Where(o => o.Dict.Contains("/ObjStm")).ToList())
        {
            var data = Decode(o);
            if (data == null) continue;
            int n = IntAfter(o.Dict, "/N"), first = IntAfter(o.Dict, "/First");
            string head = Encoding.Latin1.GetString(data, 0, Math.Min(first, data.Length));
            var nums = Regex.Matches(head, @"\d+").Select(m => int.Parse(m.Value)).ToList();
            for (int i = 0; i < n && 2 * i + 1 < nums.Count; i++)
            {
                int start = first + nums[2 * i + 1];
                int end = 2 * i + 3 < nums.Count ? first + nums[2 * i + 3] : data.Length;
                if (start < 0 || start >= data.Length || end > data.Length || end < start) continue;
                objs[nums[2 * i]] = new Obj { Num = nums[2 * i], Dict = Encoding.Latin1.GetString(data, start, end - start) };
            }
        }

        // pages in document order
        var pages = new List<Obj>();
        var root = objs.Values.FirstOrDefault(o => Regex.IsMatch(o.Dict, @"/Type\s*/Catalog"));
        if (root != null && RefAfter(root.Dict, "/Pages") is int pr) Walk(objs, pr, pages, new HashSet<int>());
        if (pages.Count == 0) pages = objs.Values.Where(o => Regex.IsMatch(o.Dict, @"/Type\s*/Page\b")).OrderBy(o => o.Num).ToList();

        var fontMaps = new Dictionary<int, CMap?>();
        var sb = new StringBuilder();
        foreach (var page in pages)
        {
            string text = PageText(objs, page, fontMaps);
            if (text.Length > 0) sb.Append(text.TrimEnd()).Append("\n\n");
        }
        string result = Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n").Trim();
        if (result.Length == 0 && pages.Count > 0)
            throw new InvalidDataException("No text could be extracted from this PDF (it may be a scan; OCR is not supported).");
        return (result, pages.Count);
    }

    private static void ScanObjects(byte[] pdf, string latin, Dictionary<int, Obj> objs)
    {
        foreach (Match m in Regex.Matches(latin, @"(?<![0-9])(\d{1,7})\s+(\d{1,5})\s+obj\b"))
        {
            int num = int.Parse(m.Groups[1].Value);
            int bodyStart = m.Index + m.Length;
            int end = latin.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
            if (end < 0) end = latin.Length;
            int streamAt = latin.IndexOf("stream", bodyStart, StringComparison.Ordinal);
            var o = new Obj { Num = num };
            if (streamAt >= 0 && streamAt < end)
            {
                o.Dict = latin.Substring(bodyStart, streamAt - bodyStart);
                int s = streamAt + 6;
                if (s < pdf.Length && pdf[s] == '\r') s++;
                if (s < pdf.Length && pdf[s] == '\n') s++;
                int e = latin.IndexOf("endstream", s, StringComparison.Ordinal);
                if (e < 0) e = end;
                int len = e - s;
                while (len > 0 && (pdf[s + len - 1] == '\n' || pdf[s + len - 1] == '\r')) len--;
                o.Stream = new byte[Math.Max(0, len)];
                Buffer.BlockCopy(pdf, s, o.Stream, 0, o.Stream.Length);
            }
            else o.Dict = latin.Substring(bodyStart, end - bodyStart);
            objs[num] = o;
        }
    }

    private static int IntAfter(string dict, string key)
    {
        var m = Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static int? RefAfter(string dict, string key)
    {
        var m = Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)\s+\d+\s+R");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static List<int> RefsIn(string s) =>
        Regex.Matches(s, @"(\d+)\s+\d+\s+R").Select(m => int.Parse(m.Groups[1].Value)).ToList();

    private static void Walk(Dictionary<int, Obj> objs, int num, List<Obj> pages, HashSet<int> seen)
    {
        if (!seen.Add(num) || !objs.TryGetValue(num, out var o)) return;
        if (Regex.IsMatch(o.Dict, @"/Type\s*/Pages\b") || o.Dict.Contains("/Kids"))
        {
            var kids = Regex.Match(o.Dict, @"/Kids\s*\[(.*?)\]", RegexOptions.Singleline);
            if (kids.Success) foreach (var k in RefsIn(kids.Groups[1].Value)) Walk(objs, k, pages, seen);
        }
        else pages.Add(o);
    }

    private static byte[]? Decode(Obj o)
    {
        if (o.Stream == null) return null;
        if (!o.Dict.Contains("/FlateDecode") && !o.Dict.Contains("/Fl")) return o.Dict.Contains("/Filter") ? null : o.Stream;
        try
        {
            using var z = new ZLibStream(new MemoryStream(o.Stream), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            // some producers append garbage; try raw deflate after the 2-byte zlib header
            try
            {
                using var d = new DeflateStream(new MemoryStream(o.Stream, 2, Math.Max(0, o.Stream.Length - 2)), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                d.CopyTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ one page

    private static string PageText(Dictionary<int, Obj> objs, Obj page, Dictionary<int, CMap?> fontMaps)
    {
        // fonts of the page: name -> ToUnicode map (null = code is the character)
        var fonts = new Dictionary<string, CMap?>();
        string resDict = page.Dict;
        var resRef = RefAfter(page.Dict, "/Resources");
        if (resRef is int rr && objs.TryGetValue(rr, out var ro)) resDict = ro.Dict;
        string fontPart = Regex.Match(resDict, @"/Font\s*(<<.*?>>|\d+\s+\d+\s+R)", RegexOptions.Singleline).Groups[1].Value;
        if (Regex.IsMatch(fontPart, @"^\d+\s+\d+\s+R$") && objs.TryGetValue(int.Parse(fontPart.Split(' ')[0]), out var fo)) fontPart = fo.Dict;
        foreach (Match fm in Regex.Matches(fontPart, @"/([^\s/<>\[\]()]+)\s+(\d+)\s+\d+\s+R"))
        {
            int fnum = int.Parse(fm.Groups[2].Value);
            if (!fontMaps.TryGetValue(fnum, out var map))
            {
                map = null;
                if (objs.TryGetValue(fnum, out var font) && RefAfter(font.Dict, "/ToUnicode") is int tu && objs.TryGetValue(tu, out var tuo))
                {
                    var cm = Decode(tuo);
                    if (cm != null) map = ParseCMap(Encoding.Latin1.GetString(cm));
                }
                fontMaps[fnum] = map;
            }
            fonts[fm.Groups[1].Value] = map;
        }

        // content streams
        var contentRefs = new List<int>();
        var cm2 = Regex.Match(page.Dict, @"/Contents\s*(\[.*?\]|\d+\s+\d+\s+R)", RegexOptions.Singleline);
        if (cm2.Success) contentRefs.AddRange(RefsIn(cm2.Groups[1].Value));
        var sb = new StringBuilder();
        foreach (var cr in contentRefs)
        {
            if (!objs.TryGetValue(cr, out var co)) continue;
            var data = Decode(co);
            if (data == null) continue;
            sb.Append(ContentText(Encoding.Latin1.GetString(data), fonts));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private sealed class CMap
    {
        public Dictionary<int, string> Map = new();
        /// <summary>Bytes per character code (1 for simple fonts, 2 for CID fonts), from the code space or the entries.</summary>
        public int CodeBytes = 1;
    }

    private static CMap ParseCMap(string cmap)
    {
        var result = new CMap();
        var map = result.Map;
        var width = Regex.Match(cmap, @"begincodespacerange\s*<([0-9A-Fa-f]+)>");
        if (width.Success) result.CodeBytes = Math.Max(1, width.Groups[1].Value.Length / 2);
        foreach (Match block in Regex.Matches(cmap, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
            foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                map[Convert.ToInt32(m.Groups[1].Value, 16)] = HexToString(m.Groups[2].Value);
        foreach (Match block in Regex.Matches(cmap, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
        {
            foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(<([0-9A-Fa-f]+)>|\[(.*?)\])", RegexOptions.Singleline))
            {
                int lo = Convert.ToInt32(m.Groups[1].Value, 16), hi = Convert.ToInt32(m.Groups[2].Value, 16);
                if (hi - lo > 65535) continue;
                if (m.Groups[4].Success)
                {
                    string start = HexToString(m.Groups[4].Value);
                    for (int c = lo; c <= hi; c++)
                    {
                        if (start.Length == 0) break;
                        map[c] = start[..^1] + (char)(start[^1] + (c - lo));
                    }
                }
                else
                {
                    var items = Regex.Matches(m.Groups[5].Value, @"<([0-9A-Fa-f]+)>").Select(x => HexToString(x.Groups[1].Value)).ToList();
                    for (int c = lo; c <= hi && c - lo < items.Count; c++) map[c] = items[c - lo];
                }
            }
        }
        if (!width.Success && map.Count > 0)
        {
            var first = Regex.Match(cmap, @"beginbf(?:char|range)\s*<([0-9A-Fa-f]+)>");
            if (first.Success) result.CodeBytes = Math.Max(1, first.Groups[1].Value.Length / 2);
        }
        return result;
    }

    private static string HexToString(string hex)
    {
        if (hex.Length % 2 == 1) hex += "0";
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
        return b.Length == 1 ? ((char)b[0]).ToString() : Encoding.BigEndianUnicode.GetString(b);
    }

    private static string Map(byte[] raw, CMap? cmap)
    {
        var sb = new StringBuilder();
        if (cmap == null || cmap.Map.Count == 0)
        {
            foreach (var b in raw) sb.Append(b >= 32 || b == 9 ? (char)b : ' ');
            return sb.ToString();
        }
        int width = Math.Clamp(cmap.CodeBytes, 1, 2);
        for (int i = 0; i + width <= raw.Length; i += width)
        {
            int code = width == 2 ? (raw[i] << 8) | raw[i + 1] : raw[i];
            if (cmap.Map.TryGetValue(code, out var s)) sb.Append(s);
            else if (width == 1) sb.Append((char)code);
        }
        return sb.ToString();
    }

    private static string ContentText(string content, Dictionary<string, CMap?> fonts)
    {
        var sb = new StringBuilder();
        CMap? font = null;
        var stack = new List<object>();          // operands: byte[] strings, double numbers, List<object> arrays, names
        int i = 0;
        double lastY = double.NaN;
        while (i < content.Length)
        {
            char c = content[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '%') { while (i < content.Length && content[i] != '\n' && content[i] != '\r') i++; continue; }
            if (c == '(') { stack.Add(ReadLiteral(content, ref i)); continue; }
            if (c == '<' && i + 1 < content.Length && content[i + 1] != '<') { stack.Add(ReadHex(content, ref i)); continue; }
            if (c == '<' || c == '>') { i += i + 1 < content.Length && content[i + 1] == c ? 2 : 1; continue; }
            if (c == '[') { var arr = new List<object>(); i++; ReadArray(content, ref i, arr); stack.Add(arr); continue; }
            if (c == '/') { int s = i + 1; i++; while (i < content.Length && !char.IsWhiteSpace(content[i]) && "/[]()<>".IndexOf(content[i]) < 0) i++; stack.Add("/" + content[s..i]); continue; }
            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                int s = i; i++;
                while (i < content.Length && (char.IsDigit(content[i]) || content[i] == '.')) i++;
                if (double.TryParse(content[s..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) stack.Add(d);
                continue;
            }
            // operator
            int st = i;
            while (i < content.Length && !char.IsWhiteSpace(content[i]) && "/[]()<>%".IndexOf(content[i]) < 0) i++;
            string op = content[st..i];
            switch (op)
            {
                case "Tf":
                    if (stack.Count >= 2 && stack[^2] is string fname && fname.StartsWith('/'))
                        font = fonts.TryGetValue(fname[1..], out var fm) ? fm : null;
                    break;
                case "Tj": case "'": case "\"":
                    if (op != "Tj") sb.Append('\n');
                    if (stack.Count > 0 && stack[^1] is byte[] bs) sb.Append(Map(bs, font));
                    break;
                case "TJ":
                    if (stack.Count > 0 && stack[^1] is List<object> parts)
                        foreach (var p in parts)
                        {
                            if (p is byte[] pb) sb.Append(Map(pb, font));
                            else if (p is double kern && kern < -250) sb.Append(' ');
                        }
                    break;
                case "Td": case "TD":
                    if (stack.Count >= 2 && stack[^1] is double ty && stack[^2] is double)
                    {
                        if (Math.Abs(ty) > 0.1) sb.Append('\n'); else sb.Append(' ');
                    }
                    break;
                case "T*": case "ET": sb.Append('\n'); break;
                case "Tm":
                    if (stack.Count >= 6 && stack[^1] is double y)
                    {
                        if (!double.IsNaN(lastY) && Math.Abs(y - lastY) > 1) sb.Append('\n');
                        lastY = y;
                    }
                    break;
            }
            stack.Clear();
        }
        return Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ");
    }

    private static byte[] ReadLiteral(string s, ref int i)
    {
        var bytes = new List<byte>();
        int depth = 0;
        i++;
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '\\' && i < s.Length)
            {
                char n = s[i++];
                switch (n)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add(8); break;
                    case 'f': bytes.Add(12); break;
                    case '\r': if (i < s.Length && s[i] == '\n') i++; break;
                    case '\n': break;
                    default:
                        if (n >= '0' && n <= '7')
                        {
                            int v = n - '0', k = 0;
                            while (k < 2 && i < s.Length && s[i] >= '0' && s[i] <= '7') { v = v * 8 + (s[i++] - '0'); k++; }
                            bytes.Add((byte)v);
                        }
                        else bytes.Add((byte)n);
                        break;
                }
            }
            else if (c == '(') { depth++; bytes.Add((byte)c); }
            else if (c == ')') { if (depth == 0) break; depth--; bytes.Add((byte)c); }
            else bytes.Add((byte)c);
        }
        return bytes.ToArray();
    }

    private static byte[] ReadHex(string s, ref int i)
    {
        int end = s.IndexOf('>', i);
        if (end < 0) end = s.Length;
        string hex = new string(s.Substring(i + 1, end - i - 1).Where(Uri.IsHexDigit).ToArray());
        i = Math.Min(s.Length, end + 1);
        if (hex.Length % 2 == 1) hex += "0";
        var b = new byte[hex.Length / 2];
        for (int k = 0; k < b.Length; k++) b[k] = Convert.ToByte(hex.Substring(2 * k, 2), 16);
        return b;
    }

    private static void ReadArray(string s, ref int i, List<object> into)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ']') { i++; return; }
            if (c == '(') { into.Add(ReadLiteral(s, ref i)); continue; }
            if (c == '<') { into.Add(ReadHex(s, ref i)); continue; }
            int st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && "[]()<>".IndexOf(s[i]) < 0) i++;
            if (i == st) { i++; continue; }
            if (double.TryParse(s[st..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) into.Add(d);
        }
    }
}

/// <summary>Persists extracted documents under ~/.llmhost/documents and offers retrieval for chats.</summary>
public class DocumentStore
{
    private static string Dir => Path.Combine(AppPaths.Root, "documents");
    private static string PathFor(string id) => Path.Combine(Dir, new string(id.Where(char.IsLetterOrDigit).ToArray()) + ".json");

    public StoredDocument Add(string name, byte[] data)
    {
        var (text, pages) = DocumentExtractor.Extract(name, data);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"'{name}' contains no text.");
        var d = new StoredDocument { Name = name, Text = text, Pages = pages };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(d.Id), JsonSerializer.Serialize(d, Json.Compact));
        return d;
    }

    /// <summary>Copies a document under a new id (a duplicated chat keeps its own attachments).</summary>
    public StoredDocument? Clone(string id)
    {
        var d = Get(id);
        if (d == null) return null;
        var copy = new StoredDocument { Name = d.Name, Text = d.Text, Pages = d.Pages };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(copy.Id), JsonSerializer.Serialize(copy, Json.Compact));
        return copy;
    }

    public StoredDocument? Get(string id)
    {
        try { var p = PathFor(id); return File.Exists(p) ? JsonSerializer.Deserialize<StoredDocument>(File.ReadAllText(p), Json.Options) : null; }
        catch { return null; }
    }

    public bool Delete(string id)
    {
        var p = PathFor(id);
        if (!File.Exists(p)) return false;
        File.Delete(p);
        return true;
    }
}

/// <summary>Splitting text into chunks and ranking them against a question (BM25, or embeddings when available).</summary>
public static class Retrieval
{
    public record Chunk(string DocId, string DocName, int Index, string Text);

    public static List<Chunk> Split(StoredDocument doc, int target = 900, int overlap = 120)
    {
        var chunks = new List<Chunk>();
        var paras = Regex.Split(doc.Text, @"\n{2,}").Where(p => p.Trim().Length > 0).ToList();
        var cur = new StringBuilder();
        void Flush()
        {
            if (cur.Length == 0) return;
            chunks.Add(new Chunk(doc.Id, doc.Name, chunks.Count + 1, cur.ToString().Trim()));
            string tail = cur.Length > overlap ? cur.ToString(cur.Length - overlap, overlap) : "";
            cur.Clear();
            cur.Append(tail);
        }
        foreach (var p in paras)
        {
            // very long paragraphs are cut at sentence-ish boundaries
            string rest = p.Trim();
            while (rest.Length > target * 1.5)
            {
                int cut = rest.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, target);
                if (cut < target / 2) cut = target;
                if (cur.Length + cut > target) Flush();
                cur.Append(rest, 0, cut + 1).Append(' ');
                rest = rest[(cut + 1)..].TrimStart();
                Flush();
            }
            if (cur.Length + rest.Length > target) Flush();
            cur.Append(rest).Append("\n\n");
        }
        Flush();
        return chunks.Where(c => c.Text.Length > 0).ToList();
    }

    private static readonly Regex Word = new(@"[\p{L}\p{N}]{2,}", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    { "the", "and", "for", "are", "was", "with", "that", "this", "from", "have", "has", "not", "you", "your", "what", "which", "who", "how", "when", "where", "into", "about", "can", "will", "all", "any", "des", "der", "die", "und", "les", "une", "est", "que" };

    public static List<string> Terms(string text) =>
        Word.Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(t => !Stop.Contains(t)).ToList();

    /// <summary>BM25 scores of the chunks for a question.</summary>
    public static double[] Bm25(IReadOnlyList<Chunk> chunks, string question)
    {
        var q = Terms(question).Distinct().ToList();
        var docs = chunks.Select(c => Terms(c.Text)).ToList();
        double avg = docs.Count == 0 ? 1 : Math.Max(1, docs.Average(d => d.Count));
        var df = new Dictionary<string, int>();
        foreach (var d in docs) foreach (var t in d.Distinct()) df[t] = df.GetValueOrDefault(t) + 1;
        var scores = new double[chunks.Count];
        for (int i = 0; i < docs.Count; i++)
        {
            var tf = docs[i].GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            foreach (var t in q)
            {
                if (!tf.TryGetValue(t, out int f)) continue;
                double idf = Math.Log(1 + (chunks.Count - df[t] + 0.5) / (df[t] + 0.5));
                scores[i] += idf * f * 2.2 / (f + 1.2 * (0.25 + 0.75 * docs[i].Count / avg));
            }
        }
        return scores;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
