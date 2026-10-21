using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using LLMHostProject.Core;

namespace LLMHostProject.Tests;

/// <summary>Points the application at a throw-away home folder before anything reads its settings.</summary>
internal static class TestEnvironment
{
    public static string Home { get; } = Path.Combine(Path.GetTempPath(), "llmhost-tests-" + Guid.NewGuid().ToString("N")[..8]);
    // not "models": LLMHOST_HOME/models is the legacy default location and is deliberately ignored by the settings
    public static string Models { get; } = Path.Combine(Home, "library");

    [ModuleInitializer]
    public static void Init()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Models);
        Environment.SetEnvironmentVariable("LLMHOST_HOME", Home);
        AppSettings.Current.ModelsDirectory = Models;
    }
}

/// <summary>Writes small, valid GGUF files so the parser and the memory estimator can be tested without real models.</summary>
internal static class GgufBuilder
{
    public static byte[] Build(string arch = "llama", int blocks = 4, int embd = 64, int heads = 4, int kvHeads = 2, int ctx = 2048,
        int vocab = 5, string? chatTemplate = null, int dataBytesPerTensor = 256, bool truncate = false, int? experts = null,
        string name = "Test Model", int? poolingType = null, bool? clipVision = null, bool? clipAudio = null,
        int? bosId = null, int? eosId = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        void KvStr(string k, string v) { Str(k); w.Write(8u); Str(v); }
        void KvU32(string k, uint v) { Str(k); w.Write(4u); w.Write(v); }

        var tensors = new List<string> { "token_embd.weight", "output_norm.weight", "output.weight" };
        for (int i = 0; i < blocks; i++) { tensors.Add($"blk.{i}.attn_q.weight"); tensors.Add($"blk.{i}.ffn_up.weight"); }
        if (experts.HasValue)
            for (int i = 0; i < blocks; i++) foreach (var kind in new[] { "up", "down", "gate" }) tensors.Add($"blk.{i}.ffn_{kind}_exps.weight");

        var kvs = new List<Action>
        {
            () => KvStr("general.architecture", arch),
            () => KvStr("general.name", name),
            () => KvU32("general.file_type", 2),
            () => KvU32($"{arch}.context_length", (uint)ctx),
            () => KvU32($"{arch}.embedding_length", (uint)embd),
            () => KvU32($"{arch}.block_count", (uint)blocks),
            () => KvU32($"{arch}.attention.head_count", (uint)heads),
            () => KvU32($"{arch}.attention.head_count_kv", (uint)kvHeads),
        };
        if (bosId.HasValue) kvs.Add(() => KvU32("tokenizer.ggml.bos_token_id", (uint)bosId.Value));
        if (eosId.HasValue) kvs.Add(() => KvU32("tokenizer.ggml.eos_token_id", (uint)eosId.Value));
        if (experts.HasValue) kvs.Add(() => KvU32($"{arch}.expert_count", (uint)experts.Value));
        if (poolingType.HasValue) kvs.Add(() => KvU32($"{arch}.pooling_type", (uint)poolingType.Value));
        void KvBool(string k, bool v) { Str(k); w.Write(7u); w.Write((byte)(v ? 1 : 0)); }
        if (clipVision.HasValue) kvs.Add(() => KvBool("clip.has_vision_encoder", clipVision.Value));
        if (clipAudio.HasValue) kvs.Add(() => KvBool("clip.has_audio_encoder", clipAudio.Value));
        if (chatTemplate != null) kvs.Add(() => KvStr("tokenizer.chat_template", chatTemplate));
        kvs.Add(() =>
        {
            Str("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write((ulong)vocab);
            for (int i = 0; i < vocab; i++) Str("t" + i);
        });

        w.Write(Encoding.ASCII.GetBytes("GGUF"));
        w.Write(3u);
        w.Write((ulong)tensors.Count);
        w.Write((ulong)kvs.Count);
        foreach (var k in kvs) k();

        ulong offset = 0;
        foreach (var t in tensors)
        {
            Str(t);
            w.Write(1u);               // n_dims
            w.Write((ulong)(dataBytesPerTensor / 4));
            w.Write(0u);               // F32
            w.Write(offset);
            offset += (ulong)dataBytesPerTensor;
        }
        // align the data section to 32 bytes
        while (ms.Position % 32 != 0) w.Write((byte)0);
        int total = tensors.Count * dataBytesPerTensor;
        w.Write(new byte[truncate ? total / 2 : total]);
        return ms.ToArray();
    }

    public static string WriteFile(string name, byte[] data)
    {
        string path = Path.Combine(TestEnvironment.Home, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }
}

internal static class DocBuilder
{
    public static byte[] Docx(params string[] paragraphs)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            foreach (var p in paragraphs) w.Write($"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>");
            w.Write("</w:body></w:document>");
        }
        return ms.ToArray();
    }

    /// <summary>A PDF with a Flate-compressed content stream and a ToUnicode map (two-byte codes).</summary>
    public static byte[] Pdf(string text, bool compress = true)
    {
        // codes: 'A'.. mapped through a CID-style font: each character c -> code (c + 1)
        var content = new StringBuilder("BT /F1 12 Tf 72 700 Td <");
        foreach (char c in text) content.Append(((int)c + 1).ToString("X4"));
        content.Append("> Tj ET");
        byte[] stream = Encoding.Latin1.GetBytes(content.ToString());
        if (compress)
        {
            var o = new MemoryStream();
            using (var z = new ZLibStream(o, CompressionLevel.Optimal, true)) z.Write(stream);
            stream = o.ToArray();
        }
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        var chars = text.Distinct().ToList();
        cmap.Append($"{chars.Count} beginbfchar\n");
        foreach (char c in chars) cmap.Append($"<{((int)c + 1):X4}> <{(int)c:X4}>\n");
        cmap.Append("endbfchar\nendcmap end end");

        var sb = new MemoryStream();
        void Raw(string s) => sb.Write(Encoding.Latin1.GetBytes(s));
        Raw("%PDF-1.5\n");
        Raw("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        Raw("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        Raw("3 0 obj << /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >> endobj\n");
        Raw($"4 0 obj << /Length {stream.Length}{(compress ? " /Filter /FlateDecode" : "")} >>\nstream\n");
        sb.Write(stream);
        Raw("\nendstream endobj\n");
        Raw("5 0 obj << /Type /Font /Subtype /Type0 /BaseFont /Test /ToUnicode 6 0 R >> endobj\n");
        byte[] cm = Encoding.Latin1.GetBytes(cmap.ToString());
        Raw($"6 0 obj << /Length {cm.Length} >>\nstream\n");
        sb.Write(cm);
        Raw("\nendstream endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");
        return sb.ToArray();
    }
}
