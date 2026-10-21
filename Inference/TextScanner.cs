using System.Text;

namespace LLMHostProject.Inference;

public enum SegmentKind { Content, Reasoning }

/// <summary>A piece of generated text, tagged as visible answer or as the model's reasoning.</summary>
public readonly record struct TextSegment(SegmentKind Kind, string Text);

/// <summary>
/// Streaming post-processor: separates &lt;think&gt; blocks from the answer and cuts generation at stop strings,
/// holding back partial matches so markers are never leaked mid-stream. Stop strings only apply to the answer.
/// </summary>
public class TextScanner
{
    public const string ThinkOpen = "<think>";
    public const string ThinkClose = "</think>";

    private readonly List<string> _stops;
    private readonly StringBuilder _pending = new();
    private bool _inThink;

    public bool Stopped { get; private set; }
    public string? MatchedStop { get; private set; }
    /// <summary>True while inside a think block (so far).</summary>
    public bool InThink => _inThink;

    /// <param name="startInThink">The prompt already ended inside an open &lt;think&gt; block.</param>
    public TextScanner(IEnumerable<string> stops, bool startInThink = false)
    {
        _stops = stops.Where(s => !string.IsNullOrEmpty(s)).ToList();
        _inThink = startInThink;
    }

    /// <summary>Marks the think block as closed from outside (reasoning budget exhausted).</summary>
    public void ForceCloseThink() => _inThink = false;

    public List<TextSegment> Feed(string piece)
    {
        var result = new List<TextSegment>();
        if (Stopped) return result;
        _pending.Append(piece);
        Drain(final: false, result);
        return result;
    }

    public List<TextSegment> Flush()
    {
        var result = new List<TextSegment>();
        if (!Stopped) Drain(final: true, result);
        return result;
    }

    private bool _trimContent = true, _trimReasoning = true;

    private void Emit(List<TextSegment> into, SegmentKind kind, string text)
    {
        if (kind == SegmentKind.Content && _trimContent) { text = text.TrimStart('\n', '\r', ' '); if (text.Length > 0) _trimContent = false; }
        else if (kind == SegmentKind.Reasoning && _trimReasoning) { text = text.TrimStart('\n', '\r', ' '); if (text.Length > 0) _trimReasoning = false; }
        if (text.Length == 0) return;
        if (into.Count > 0 && into[^1].Kind == kind) into[^1] = new TextSegment(kind, into[^1].Text + text);
        else into.Add(new TextSegment(kind, text));
    }

    private void Drain(bool final, List<TextSegment> output)
    {
        while (true)
        {
            string text = _pending.ToString();
            if (_inThink)
            {
                int close = text.IndexOf(ThinkClose, StringComparison.Ordinal);
                if (close >= 0)
                {
                    Emit(output, SegmentKind.Reasoning, text[..close]);
                    _pending.Remove(0, close + ThinkClose.Length);
                    _inThink = false;
                    // models usually put a blank line after the closing tag
                    TrimLeadingNewlines();
                    continue;
                }
                int keep = final ? 0 : PartialSuffix(text, new[] { ThinkClose });
                Emit(output, SegmentKind.Reasoning, text[..(text.Length - keep)]);
                _pending.Remove(0, text.Length - keep);
                break;
            }

            int stopIdx = -1;
            string? matched = null;
            foreach (var s in _stops)
            {
                int i = text.IndexOf(s, StringComparison.Ordinal);
                if (i >= 0 && (stopIdx < 0 || i < stopIdx)) { stopIdx = i; matched = s; }
            }
            int thinkIdx = text.IndexOf(ThinkOpen, StringComparison.Ordinal);

            if (stopIdx >= 0 && (thinkIdx < 0 || stopIdx < thinkIdx))
            {
                Emit(output, SegmentKind.Content, text[..stopIdx]);
                _pending.Clear();
                Stopped = true;
                MatchedStop = matched;
                break;
            }
            if (thinkIdx >= 0)
            {
                Emit(output, SegmentKind.Content, text[..thinkIdx]);
                _pending.Remove(0, thinkIdx + ThinkOpen.Length);
                _inThink = true;
                continue;
            }

            var markers = new List<string>(_stops) { ThinkOpen };
            int hold = final ? 0 : PartialSuffix(text, markers);
            Emit(output, SegmentKind.Content, text[..(text.Length - hold)]);
            _pending.Remove(0, text.Length - hold);
            break;
        }
    }

    private void TrimLeadingNewlines()
    {
        int n = 0;
        while (n < _pending.Length && _pending[n] is '\n' or '\r') n++;
        if (n > 0) _pending.Remove(0, n);
    }

    /// <summary>Length of the longest suffix of <paramref name="text"/> that is a proper prefix of any marker.</summary>
    private static int PartialSuffix(string text, IEnumerable<string> markers)
    {
        int best = 0;
        foreach (var m in markers)
        {
            int max = Math.Min(m.Length - 1, text.Length);
            for (int len = max; len > best; len--)
            {
                if (string.CompareOrdinal(text, text.Length - len, m, 0, len) == 0) { best = len; break; }
            }
        }
        return best;
    }
}
