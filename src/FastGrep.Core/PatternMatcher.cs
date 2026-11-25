using System.Text.RegularExpressions;

namespace FastGrep.Core;

internal interface IPatternMatcher
{
    IReadOnlyList<int> Find(string text);
}

internal static class PatternMatcher
{
    public static IPatternMatcher Create(SearchOptions options)
    {
        if (options.Patterns.Count == 0) throw new ArgumentException("At least one pattern is required.");
        if (options.UseRegex) return new RegexMatcher(options);
        // OrdinalIgnoreCase has Unicode semantics that cannot be reproduced by folding
        // individual UTF-16 code units. Keep Aho-Corasick's fast path for the overwhelmingly
        // common ASCII log patterns, and use the exact BCL comparison for non-ASCII patterns.
        if (options.Patterns.Count <= 4 || options.IgnoreCase && options.Patterns.Any(p => !p.All(char.IsAscii)))
            return new LiteralMatcher(options.Patterns, options.IgnoreCase);
        return new AhoCorasickMatcher(options.Patterns, options.IgnoreCase);
    }

    private sealed class LiteralMatcher(IReadOnlyList<string> patterns, bool ignoreCase) : IPatternMatcher
    {
        private readonly StringComparison _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public IReadOnlyList<int> Find(string text)
        {
            List<int>? found = null;
            for (var i = 0; i < patterns.Count; i++)
                if (text.Contains(patterns[i], _comparison)) (found ??= []).Add(i);
            return found ?? [];
        }
    }

    private sealed class RegexMatcher : IPatternMatcher
    {
        private readonly Regex[] _patterns;
        public RegexMatcher(SearchOptions options)
        {
            var flags = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (options.IgnoreCase) flags |= RegexOptions.IgnoreCase;
            _patterns = options.Patterns.Select(p => new Regex(p, flags, options.RegexTimeout)).ToArray();
        }
        public IReadOnlyList<int> Find(string text)
        {
            List<int>? found = null;
            for (var i = 0; i < _patterns.Length; i++)
                if (_patterns[i].IsMatch(text)) (found ??= []).Add(i);
            return found ?? [];
        }
    }

    private sealed class AhoCorasickMatcher : IPatternMatcher
    {
        private sealed class Node
        {
            public Dictionary<char, int> Next { get; } = [];
            public int Fail { get; set; }
            public List<int> Output { get; } = [];
        }

        private readonly List<Node> _nodes = [new Node()];
        private readonly bool _ignoreCase;

        public AhoCorasickMatcher(IReadOnlyList<string> patterns, bool ignoreCase)
        {
            _ignoreCase = ignoreCase;
            for (var id = 0; id < patterns.Count; id++)
            {
                if (patterns[id].Length == 0) throw new ArgumentException("Patterns cannot be empty.");
                var state = 0;
                foreach (var raw in patterns[id])
                {
                    var c = Fold(raw);
                    if (!_nodes[state].Next.TryGetValue(c, out var next))
                    {
                        next = _nodes.Count;
                        _nodes[state].Next[c] = next;
                        _nodes.Add(new Node());
                    }
                    state = next;
                }
                _nodes[state].Output.Add(id);
            }

            var queue = new Queue<int>();
            foreach (var child in _nodes[0].Next.Values) queue.Enqueue(child);
            while (queue.TryDequeue(out var current))
            {
                foreach (var (c, next) in _nodes[current].Next)
                {
                    queue.Enqueue(next);
                    var fail = _nodes[current].Fail;
                    while (fail != 0 && !_nodes[fail].Next.ContainsKey(c)) fail = _nodes[fail].Fail;
                    if (_nodes[fail].Next.TryGetValue(c, out var target) && target != next) _nodes[next].Fail = target;
                    _nodes[next].Output.AddRange(_nodes[_nodes[next].Fail].Output);
                }
            }
        }

        private char Fold(char value) => _ignoreCase ? char.ToUpperInvariant(value) : value;

        public IReadOnlyList<int> Find(string text)
        {
            HashSet<int>? found = null;
            var state = 0;
            foreach (var raw in text)
            {
                var c = Fold(raw);
                while (state != 0 && !_nodes[state].Next.ContainsKey(c)) state = _nodes[state].Fail;
                if (_nodes[state].Next.TryGetValue(c, out var next)) state = next;
                if (_nodes[state].Output.Count > 0)
                {
                    found ??= [];
                    foreach (var id in _nodes[state].Output) found.Add(id);
                }
            }
            return found?.Order().ToArray() ?? [];
        }
    }
}
