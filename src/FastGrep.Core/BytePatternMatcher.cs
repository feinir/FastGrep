using System.Buffers;
using System.Text;

namespace FastGrep.Core;

/// <summary>
/// Searches encoded bytes directly. ASCII-compatible patterns never require source decoding;
/// non-ASCII patterns are compiled to common encodings so mixed text collections can be searched
/// without choosing one source encoding up front.
/// </summary>
internal sealed class BytePatternMatcher
{
    private readonly Variant[] _narrow;
    private readonly Variant[] _wide;
    private readonly Automaton? _narrowExactAutomaton;
    private readonly Automaton? _narrowFoldedAutomaton;
    private readonly Automaton? _wideExactAutomaton;
    private readonly Automaton? _wideFoldedAutomaton;

    public BytePatternMatcher(IReadOnlyList<string> patterns, bool ignoreCase, bool useAutomaton = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var narrow = new List<Variant>();
        var wide = new List<Variant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = 0; id < patterns.Count; id++)
        {
            var pattern = patterns[id];
            if (pattern.Length == 0) throw new ArgumentException("Patterns cannot be empty.");
            var asciiFold = ignoreCase && pattern.All(char.IsAscii);
            var forms = ignoreCase && !asciiFold
                ? new[] { pattern, pattern.ToUpperInvariant(), pattern.ToLowerInvariant() }.Distinct(StringComparer.Ordinal)
                : [pattern];
            foreach (var form in forms)
            {
                Add(Encoding.UTF8, form, id, asciiFold, false);
                Add(Encoding.Unicode, form, id, asciiFold, true);
                Add(Encoding.BigEndianUnicode, form, id, asciiFold, true);
                if (!form.All(char.IsAscii))
                {
                    AddCodePage(54936, form, id); // GB18030
                    AddCodePage(950, form, id);   // Big5
                    AddCodePage(1251, form, id);  // Cyrillic
                    AddCodePage(1252, form, id);  // Western European
                }
            }
        }
        _narrow = narrow.ToArray();
        _wide = wide.ToArray();
        if (useAutomaton)
        {
            _narrowExactAutomaton = Automaton.Create(_narrow.Where(x => !x.AsciiIgnoreCase), false);
            _narrowFoldedAutomaton = Automaton.Create(_narrow.Where(x => x.AsciiIgnoreCase), true);
            _wideExactAutomaton = Automaton.Create(_wide.Where(x => !x.AsciiIgnoreCase), false);
            _wideFoldedAutomaton = Automaton.Create(_wide.Where(x => x.AsciiIgnoreCase), true);
        }

        void AddCodePage(int codePage, string text, int id)
        {
            try { Add(Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), text, id, false, false); }
            catch (EncoderFallbackException) { }
            catch (ArgumentException) { }
        }

        void Add(Encoding encoding, string text, int id, bool asciiIgnoreCase, bool wideEncoding)
        {
            byte[] bytes;
            try { bytes = encoding.GetBytes(text); }
            catch (EncoderFallbackException) { return; }
            var key = $"{id}:{asciiIgnoreCase}:{Convert.ToHexString(bytes)}";
            if (!seen.Add(key)) return;
            var variant = new Variant(id, bytes, asciiIgnoreCase);
            (wideEncoding ? wide : narrow).Add(variant);
        }
    }

    public IReadOnlyList<int> Find(ReadOnlySpan<byte> text, bool wideText = false)
    {
        List<int>? found = null;
        if (_narrowExactAutomaton is not null || _narrowFoldedAutomaton is not null ||
            _wideExactAutomaton is not null || _wideFoldedAutomaton is not null)
        {
            (wideText ? _wideExactAutomaton : _narrowExactAutomaton)?.Find(text, ref found);
            (wideText ? _wideFoldedAutomaton : _narrowFoldedAutomaton)?.Find(text, ref found);
        }
        else
        {
            FindIn(wideText ? _wide : _narrow, text, ref found);
        }
        if (found is null) return [];
        found.Sort();
        return found;
    }

    public IReadOnlyList<BytePatternHit> FindAll(ReadOnlySpan<byte> text, bool wideText = false)
    {
        var variants = wideText ? _wide : _narrow;
        List<BytePatternHit>? hits = null;
        HashSet<(int Offset, int PatternId)>? seen = null;
        foreach (var variant in variants)
        {
            var offset = 0;
            while (offset <= text.Length - variant.Bytes.Length)
            {
                var relative = variant.AsciiIgnoreCase
                    ? IndexOfAsciiIgnoreCase(text[offset..], variant.Bytes, variant.FirstBytes)
                    : text[offset..].IndexOf(variant.Bytes);
                if (relative < 0) break;
                var absolute = offset + relative;
                seen ??= [];
                if (seen.Add((absolute, variant.PatternId)))
                    (hits ??= []).Add(new BytePatternHit(absolute, variant.Bytes.Length, variant.PatternId));
                offset = absolute + Math.Max(1, variant.Bytes.Length);
            }
        }
        if (hits is null) return [];
        hits.Sort(static (left, right) => left.Offset != right.Offset
            ? left.Offset.CompareTo(right.Offset)
            : left.PatternId.CompareTo(right.PatternId));
        return hits;
    }

    private static void FindIn(Variant[] variants, ReadOnlySpan<byte> text, ref List<int>? found)
    {
        foreach (var variant in variants)
        {
            if (found?.Contains(variant.PatternId) == true) continue;
            var matched = variant.AsciiIgnoreCase
                ? ContainsAsciiIgnoreCase(text, variant.Bytes, variant.FirstBytes)
                : text.IndexOf(variant.Bytes) >= 0;
            if (matched) (found ??= []).Add(variant.PatternId);
        }
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> text, byte[] pattern, SearchValues<byte>? firstBytes)
        => IndexOfAsciiIgnoreCase(text, pattern, firstBytes) >= 0;

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> text, byte[] pattern, SearchValues<byte>? firstBytes)
    {
        if (pattern.Length > text.Length) return -1;
        var offset = 0;
        while (offset <= text.Length - pattern.Length)
        {
            var relative = firstBytes is null
                ? text[offset..].IndexOf(pattern[0])
                : text[offset..].IndexOfAny(firstBytes);
            if (relative < 0) return -1;
            offset += relative;
            if (offset <= text.Length - pattern.Length && EqualsAsciiIgnoreCase(text.Slice(offset, pattern.Length), pattern)) return offset;
            offset++;
        }
        return -1;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> pattern)
    {
        for (var index = 0; index < pattern.Length; index++)
            if (FoldAscii(text[index]) != FoldAscii(pattern[index])) return false;
        return true;
    }

    private static byte FoldAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private sealed class Variant
    {
        public Variant(int patternId, byte[] bytes, bool asciiIgnoreCase)
        {
            PatternId = patternId;
            Bytes = bytes;
            AsciiIgnoreCase = asciiIgnoreCase;
            if (asciiIgnoreCase && bytes[0] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')
                FirstBytes = SearchValues.Create([(byte)char.ToLowerInvariant((char)bytes[0]), (byte)char.ToUpperInvariant((char)bytes[0])]);
        }

        public int PatternId { get; }
        public byte[] Bytes { get; }
        public bool AsciiIgnoreCase { get; }
        public SearchValues<byte>? FirstBytes { get; }
    }

    private sealed class Automaton
    {
        private sealed class Node
        {
            public Dictionary<byte, int> Next { get; } = [];
            public int Failure { get; set; }
            public List<int> Output { get; } = [];
        }

        private readonly List<Node> _nodes = [new Node()];
        private readonly bool _foldAscii;
        private readonly int[] _rootTransitions = new int[256];

        private Automaton(IEnumerable<Variant> variants, bool foldAscii)
        {
            _foldAscii = foldAscii;
            foreach (var variant in variants)
            {
                var state = 0;
                foreach (var raw in variant.Bytes)
                {
                    var value = Fold(raw);
                    if (!_nodes[state].Next.TryGetValue(value, out var next))
                    {
                        next = _nodes.Count;
                        _nodes[state].Next[value] = next;
                        _nodes.Add(new Node());
                    }
                    state = next;
                }
                if (!_nodes[state].Output.Contains(variant.PatternId)) _nodes[state].Output.Add(variant.PatternId);
            }

            var queue = new Queue<int>();
            foreach (var (value, child) in _nodes[0].Next)
            {
                _rootTransitions[value] = child;
                queue.Enqueue(child);
            }
            while (queue.TryDequeue(out var current))
            {
                foreach (var (value, next) in _nodes[current].Next)
                {
                    queue.Enqueue(next);
                    var failure = _nodes[current].Failure;
                    while (failure != 0 && !_nodes[failure].Next.ContainsKey(value)) failure = _nodes[failure].Failure;
                    if (_nodes[failure].Next.TryGetValue(value, out var target) && target != next)
                        _nodes[next].Failure = target;
                    foreach (var id in _nodes[_nodes[next].Failure].Output)
                        if (!_nodes[next].Output.Contains(id)) _nodes[next].Output.Add(id);
                }
            }
        }

        public static Automaton? Create(IEnumerable<Variant> variants, bool foldAscii)
        {
            var materialized = variants.ToArray();
            return materialized.Length == 0 ? null : new Automaton(materialized, foldAscii);
        }

        public void Find(ReadOnlySpan<byte> text, ref List<int>? found)
        {
            var state = 0;
            foreach (var raw in text)
            {
                var value = Fold(raw);
                if (state == 0)
                {
                    state = _rootTransitions[value];
                }
                else
                {
                    while (state != 0 && !_nodes[state].Next.ContainsKey(value)) state = _nodes[state].Failure;
                    if (state == 0) state = _rootTransitions[value];
                    else if (_nodes[state].Next.TryGetValue(value, out var next)) state = next;
                }
                foreach (var id in _nodes[state].Output)
                    if (found?.Contains(id) != true) (found ??= []).Add(id);
            }
        }

        private byte Fold(byte value) => _foldAscii ? FoldAscii(value) : value;
    }
}

internal readonly record struct BytePatternHit(int Offset, int Length, int PatternId);

internal static class TolerantLineDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Gb18030 = Create(54936);
    private static readonly Encoding Windows1251 = Create(1251);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.IsEmpty && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { }
        // GB18030 covers GBK and is the most common non-UTF8 encoding in the target collections.
        try { return Gb18030.GetString(bytes); }
        catch (DecoderFallbackException) { }
        try { return Windows1251.GetString(bytes); }
        catch (DecoderFallbackException) { }
        return Encoding.Latin1.GetString(bytes);
    }

    private static Encoding Create(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
