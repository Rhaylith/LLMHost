using System.Text;

namespace LLMHostProject.Models;

/// <summary>One tensor of a GGUF file: its name and its size in bytes on disk (= in memory once loaded).</summary>
public record TensorEntry(string Name, long Size);

/// <summary>Thrown for GGUF files that are damaged, truncated or not GGUF at all.</summary>
public class GgufFormatException : IOException
{
    public GgufFormatException(string message) : base(message) { }
}

/// <summary>Minimal GGUF header reader: extracts the metadata needed to describe a model without loading it.</summary>
public class GgufInfo
{
    public int Version { get; set; }
    public ulong TensorCount { get; set; }
    public string Architecture { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SizeLabel { get; set; }
    public string? Quantization { get; set; }
    public long ContextLength { get; set; }
    public long EmbeddingLength { get; set; }
    public long BlockCount { get; set; }
    public long HeadCount { get; set; }
    public long HeadCountKv { get; set; }
    /// <summary>Attention key / value head dimensions when the model states them (otherwise embedding / heads).</summary>
    public long KeyLength { get; set; }
    public long ValueLength { get; set; }
    /// <summary>Mixture-of-experts: total experts and experts used per token (0 = dense model).</summary>
    public long ExpertCount { get; set; }
    public long ExpertUsedCount { get; set; }
    public long SlidingWindow { get; set; }
    public long VocabSize { get; set; }
    public bool HasChatTemplate { get; set; }
    public string? ChatTemplate { get; set; }
    public bool IsEmbeddingModel { get; set; }
    /// <summary>llama.cpp pooling type (0 none, 1 mean, 2 cls, 3 last, 4 rank = reranker); -1 when the model does not say.</summary>
    public long PoolingType { get; set; } = -1;
    /// <summary>Built-in multi-token-prediction heads (GGUF <c>nextn_predict_layers</c>); 0 when the model has none. Detected only: the pinned engine cannot use them yet.</summary>
    public long MtpLayers { get; set; }
    /// <summary>Reranker models can carry a prompt format with {query} and {document} (<c>tokenizer.chat_template.rerank</c>).</summary>
    public string? RerankTemplate { get; set; }
    /// <summary>LoRA adapter files: <c>general.type</c> = "adapter", <c>adapter.type</c> = "lora" and <c>adapter.lora.alpha</c>.</summary>
    public string? GeneralType { get; set; }
    public string? AdapterType { get; set; }
    public float LoraAlpha { get; set; }
    public bool IsAdapter => GeneralType == "adapter" || AdapterType == "lora";
    /// <summary>Ids of the BOS / EOS tokens (-1 when the file does not say) and where the token list starts in the file.</summary>
    public long BosId { get; set; } = -1;
    public long EosId { get; set; } = -1;
    public long TokensOffset { get; set; } = -1;
    /// <summary>Multimodal projector (mmproj) files: which encoders they carry.</summary>
    public bool HasVisionEncoder { get; set; }
    public bool HasAudioEncoder { get; set; }
    public bool IsMoe => ExpertCount > 1;
    /// <summary>Number of bytes of the header (metadata + tensor table), i.e. where the tensor data starts, before alignment.</summary>
    public long DataOffset { get; set; }
    public long FileLength { get; set; }
    public long Alignment { get; set; } = 32;

    private static readonly string[] FileTypeNames =
    {
        "F32","F16","Q4_0","Q4_1","Q4_1_F16","Q4_2","Q4_3","Q8_0","Q5_0","Q5_1","Q2_K","Q3_K_S","Q3_K_M","Q3_K_L",
        "Q4_K_S","Q4_K_M","Q5_K_S","Q5_K_M","Q6_K","IQ2_XXS","IQ2_XS","Q2_K_S","IQ3_XS","IQ3_XXS","IQ1_S","IQ4_NL",
        "IQ3_S","IQ3_M","IQ2_S","IQ2_M","IQ4_XS","IQ1_M","BF16"
    };

    /// <summary>Estimated KV-cache size in bytes for a given context length at f16.</summary>
    public long EstimateKvBytes(long ctx, double bytesPerElement = 2.0)
    {
        if (BlockCount == 0 || EmbeddingLength == 0 || HeadCount == 0) return 0;
        long kvHeads = HeadCountKv > 0 ? HeadCountKv : HeadCount;
        long embdKv = EmbeddingLength / HeadCount * kvHeads;
        return (long)(2.0 * BlockCount * ctx * embdKv * bytesPerElement);
    }

    /// <summary>Marker for an array value that was skipped while reading (only its length is kept).</summary>
    private sealed record SkippedArray(long Count);

    public static GgufInfo? TryRead(string path)
    {
        try { return Read(path); } catch { return null; }
    }

    public static GgufInfo Read(string path) => Read(path, out _);

    /// <summary>Reads the header; <paramref name="tensors"/> receives the tensor table (name + size from offset differences).</summary>
    public static GgufInfo Read(string path, out List<TensorEntry> tensors)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (fs.Length < 24) throw new GgufFormatException("The file is too small to be a GGUF model (incomplete download?).");
        if (r.ReadUInt32() != 0x46554747) throw new GgufFormatException("Not a GGUF file (bad magic number). It may be damaged or a different format.");
        var info = new GgufInfo { Version = r.ReadInt32(), FileLength = fs.Length };
        if (info.Version is < 1 or > 3) throw new GgufFormatException($"Unsupported GGUF version {info.Version}.");
        info.TensorCount = r.ReadUInt64();
        ulong kvCount = r.ReadUInt64();
        if (kvCount > 1_000_000 || info.TensorCount > 1_000_000) throw new GgufFormatException("The GGUF header is damaged (implausible counts).");
        long? fileType = null;
        try
        {
            for (ulong i = 0; i < kvCount; i++)
            {
                string key = ReadString(r);
                uint type = r.ReadUInt32();
                long valuePos = fs.Position;
                object? v = ReadValue(r, type);
                switch (key)
                {
                    case "tokenizer.chat_template.rerank": info.RerankTemplate = v as string; break;
                    case "general.type": info.GeneralType = v as string; break;
                    case "adapter.type": info.AdapterType = v as string; break;
                    case "adapter.lora.alpha": info.LoraAlpha = Convert.ToSingle(v); break;
                    case "general.architecture": info.Architecture = v as string ?? ""; break;
                    case "general.name": info.Name = v as string ?? ""; break;
                    case "general.size_label": info.SizeLabel = v as string; break;
                    case "general.file_type": fileType = Convert.ToInt64(v); break;
                    case "general.alignment": info.Alignment = Math.Max(1, ToLong(v)); break;
                    case "tokenizer.chat_template": info.HasChatTemplate = true; info.ChatTemplate = v as string; break;
                    case "clip.has_vision_encoder": info.HasVisionEncoder = v is true; break;
                    case "clip.has_audio_encoder": info.HasAudioEncoder = v is true; break;
                    case "tokenizer.ggml.bos_token_id": info.BosId = ToLong(v); break;
                    case "tokenizer.ggml.eos_token_id": info.EosId = ToLong(v); break;
                    case "tokenizer.ggml.tokens": info.VocabSize = v is SkippedArray sa ? sa.Count : 0; info.TokensOffset = valuePos; break;
                    default:
                        if (info.Architecture.Length > 0 && key.StartsWith(info.Architecture + "."))
                        {
                            string sub = key[(info.Architecture.Length + 1)..];
                            switch (sub)
                            {
                                case "context_length": info.ContextLength = ToLong(v); break;
                                case "embedding_length": info.EmbeddingLength = ToLong(v); break;
                                case "block_count": info.BlockCount = ToLong(v); break;
                                case "attention.head_count": info.HeadCount = ToLong(v); break;
                                case "attention.head_count_kv": info.HeadCountKv = ToLong(v); break;
                                case "attention.key_length": info.KeyLength = ToLong(v); break;
                                case "attention.value_length": info.ValueLength = ToLong(v); break;
                                case "attention.sliding_window": info.SlidingWindow = ToLong(v); break;
                                case "expert_count": info.ExpertCount = ToLong(v); break;
                                case "expert_used_count": info.ExpertUsedCount = ToLong(v); break;
                                case "nextn_predict_layers": info.MtpLayers = ToLong(v); break;
                                case "pooling_type": info.IsEmbeddingModel = true; info.PoolingType = ToLong(v); break;
                            }
                        }
                        break;
                }
            }

            // ---- tensor table
            var raw = new List<(string Name, long Offset)>((int)info.TensorCount);
            for (ulong i = 0; i < info.TensorCount; i++)
            {
                string name = ReadString(r);
                uint nDims = r.ReadUInt32();
                if (nDims > 8) throw new GgufFormatException("The GGUF tensor table is damaged.");
                for (uint d = 0; d < nDims; d++) r.ReadUInt64();
                r.ReadUInt32();                       // ggml type
                long offset = (long)r.ReadUInt64();
                raw.Add((name, offset));
            }
            info.DataOffset = fs.Position;
            long dataStart = (fs.Position + info.Alignment - 1) / info.Alignment * info.Alignment;
            long dataLen = fs.Length - dataStart;
            tensors = new List<TensorEntry>(raw.Count);
            var sorted = raw.Select((t, idx) => (t.Name, t.Offset, idx)).OrderBy(t => t.Offset).ToList();
            var sizes = new long[raw.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                long next = i + 1 < sorted.Count ? sorted[i + 1].Offset : dataLen;
                sizes[sorted[i].idx] = Math.Max(0, next - sorted[i].Offset);
            }
            for (int i = 0; i < raw.Count; i++) tensors.Add(new TensorEntry(raw[i].Name, sizes[i]));
            if (raw.Count > 0 && raw.Max(t => t.Offset) > dataLen)
                throw new GgufFormatException("The file is truncated: tensor data extends past the end of the file (incomplete download?).");
        }
        catch (EndOfStreamException)
        {
            throw new GgufFormatException("The file ends in the middle of its header (incomplete or damaged download).");
        }
        if (fileType is >= 0 && fileType < FileTypeNames.Length) info.Quantization = FileTypeNames[fileType.Value];
        if (info.Architecture is "bert" or "nomic-bert" or "jina-bert-v2" or "modern-bert") info.IsEmbeddingModel = true;
        return info;
    }

    /// <summary>Text of the requested tokens, read from the token list of the file (which the header reader skips).</summary>
    public static Dictionary<long, string> ReadTokenText(string path, GgufInfo info, IEnumerable<long> ids)
    {
        var wanted = new HashSet<long>(ids);
        var result = new Dictionary<long, string>();
        if (info.TokensOffset < 0 || wanted.Count == 0) return result;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            fs.Seek(info.TokensOffset, SeekOrigin.Begin);
            if (r.ReadUInt32() != 8) return result;                       // array of strings
            ulong n = r.ReadUInt64();
            long last = wanted.Max();
            for (ulong i = 0; i < n && (long)i <= last; i++)
            {
                ulong len = r.ReadUInt64();
                if (wanted.Contains((long)i) && len < 4096) { result[(long)i] = Encoding.UTF8.GetString(r.ReadBytes((int)len)); }
                else fs.Seek((long)len, SeekOrigin.Current);
            }
        }
        catch { }
        return result;
    }

    private static long ToLong(object? v) => v switch { null => 0, string => 0, SkippedArray => 0, _ => Convert.ToInt64(v) };

    private static string ReadString(BinaryReader r)
    {
        ulong len = r.ReadUInt64();
        if (len > 64 * 1024 * 1024) throw new GgufFormatException("The GGUF header is damaged (bad string length).");
        var bytes = r.ReadBytes((int)len);
        if (bytes.Length != (int)len) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static object? ReadValue(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0: return r.ReadByte();
            case 1: return r.ReadSByte();
            case 2: return r.ReadUInt16();
            case 3: return r.ReadInt16();
            case 4: return r.ReadUInt32();
            case 5: return r.ReadInt32();
            case 6: return r.ReadSingle();
            case 7: return r.ReadByte() != 0;
            case 8: return ReadString(r);
            case 10: return r.ReadUInt64();
            case 11: return r.ReadInt64();
            case 12: return r.ReadDouble();
            case 9:
                uint et = r.ReadUInt32();
                ulong n = r.ReadUInt64();
                if (et == 8)
                {
                    for (ulong i = 0; i < n; i++)
                    {
                        ulong len = r.ReadUInt64();
                        r.BaseStream.Seek((long)len, SeekOrigin.Current);
                    }
                }
                else
                {
                    int size = et switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, _ => 8 };
                    r.BaseStream.Seek((long)n * size, SeekOrigin.Current);
                }
                return new SkippedArray((long)n);
            default: throw new GgufFormatException($"Unknown GGUF value type {type}: the file is damaged or newer than this reader.");
        }
    }

    /// <summary>Paths of every file of a (possibly split) model, starting from the first shard.</summary>
    public static IReadOnlyList<string> ShardFiles(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"^(.*)-(\d{5})-of-(\d{5})\.gguf$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return new[] { path };
        string dir = Path.GetDirectoryName(path)!;
        int total = int.Parse(m.Groups[3].Value);
        var list = new List<string>();
        for (int i = 1; i <= total; i++)
            list.Add(Path.Combine(dir, $"{m.Groups[1].Value}-{i:D5}-of-{total:D5}.gguf"));
        return list;
    }
}
