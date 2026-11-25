using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FastGrep.Core;

public sealed class CleanEngine
{
    public async Task<CleanSummary> CleanAsync(IEnumerable<string> sourceFiles, string destination, CleanOptions options,
        IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default,
        Func<string, ValueTask>? removedLine = null)
    {
        var files = sourceFiles.Select(Path.GetFullPath).ToArray();
        var target = FileOperations.EnsureSeparateOutput(files, destination);
        var encoding = Encodings.Get(options.EncodingName);
        var transform = new LineTransform(options);
        var totalBytes = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        var errors = new List<string>();
        long inputLines = 0, outputLines = 0, removed = 0, bytes = 0;
        var sw = Stopwatch.StartNew();
        await using var output = AtomicOutput.Create(target, new UTF8Encoding(false), options.Append);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, encoding, true, 1024 * 1024);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    inputLines++;
                    var result = transform.Apply(line);
                    if (result is null) { removed++; if (removedLine is not null) await removedLine(line); continue; }
                    await output.Writer.WriteLineAsync(result.AsMemory(), cancellationToken);
                    outputLines++;
                }
                bytes += info.Length;
                progress?.Report(new JobProgress("clean", file, bytes, totalBytes, inputLines));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new AggregateException("Cleaning did not complete; destination was not committed.", errors.Select(x => new IOException(x)));
        await output.CommitAsync(cancellationToken);
        sw.Stop();
        return new CleanSummary(files.Length, inputLines, outputLines, removed, bytes, sw.Elapsed, errors);
    }

    public async Task<CleanSummary> CleanToGroupsAsync(IEnumerable<string> sourceFiles, string outputDirectory,
        CleanOptions options, int groupStartIndex = 0, string prefix = "", int maxOpenFiles = 32,
        IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default,
        Func<string, ValueTask>? removedLine = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var files = sourceFiles.Select(Path.GetFullPath).ToArray();
        var encoding = Encodings.Get(options.EncodingName);
        var transform = new LineTransform(options);
        var totalBytes = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        long inputLines = 0, outputLines = 0, removed = 0, bytes = 0;
        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        await using var writers = new LruWriterPool(outputDirectory, prefix, maxOpenFiles, options.Append);
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                using var reader = new StreamReader(file, encoding, true, 1024 * 1024);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    inputLines++;
                    var result = transform.Apply(line);
                    if (result is null) { removed++; if (removedLine is not null) await removedLine(line); continue; }
                    var trimmed = result.AsSpan().Trim();
                    var key = trimmed.Length <= groupStartIndex + 1 ? "@small" :
                        options.GroupNonEmailSeparately && !LooksLikeEmailAtStart(result) ? "@nomail" :
                        $"{GroupFileChar(trimmed[groupStartIndex])}{GroupFileChar(trimmed[groupStartIndex + 1])}";
                    await writers.WriteLineAsync(key, result, cancellationToken);
                    outputLines++;
                }
                bytes += info.Length;
                progress?.Report(new JobProgress("group-clean", file, bytes, totalBytes, inputLines));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new AggregateException("Grouped cleaning did not complete; outputs were not committed.", errors.Select(x => new IOException(x)));
        await writers.CommitAsync(cancellationToken);
        stopwatch.Stop();
        return new CleanSummary(files.Length, inputLines, outputLines, removed, bytes, stopwatch.Elapsed, errors);
    }

    private static char GroupFileChar(char c) => char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) : char.IsAsciiDigit(c) ? c : '#';
    private static readonly Regex EmailAtStart = new(@"^[0-9a-zA-Z\-+_.#!`~$()%*\[\]]*@(?:[0-9a-zA-Z\-_]*\.)+[a-zA-Z]{2,5}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static bool LooksLikeEmailAtStart(string value) => EmailAtStart.IsMatch(value);

    private sealed class LineTransform
    {
        private readonly CleanOptions _options;
        private readonly Regex? _regex;
        private readonly Regex? _whitespace;
        private readonly StringComparison _prefixComparison;
        private readonly StringComparison _containsComparison;

        public LineTransform(CleanOptions options)
        {
            _options = options;
            _prefixComparison = (options.RemovePrefixIgnoreCase ?? options.IgnoreCase) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            _containsComparison = (options.RemoveContainingIgnoreCase ?? options.IgnoreCase) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.IsNullOrEmpty(options.RemoveRegex))
                _regex = new Regex(options.RemoveRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant |
                    (options.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(2));
            if (options.RemoveAllWhitespace) _whitespace = new Regex(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        public string? Apply(string line)
        {
            var result = _options.Trim ? line.Trim() : line;
            if (_options.RemovePrefix is { Length: > 0 } prefix && result.StartsWith(prefix, _prefixComparison)) return null;
            if (_options.RemoveContaining is { Length: > 0 } contain && result.Contains(contain, _containsComparison)) return null;
            if (_regex?.IsMatch(result) == true) return null;
            if (_options.FieldSeparator is { } separator && _options.RemoveFieldNumbers.Count > 0)
                result = RemoveFields(result, separator, _options.RemoveFieldNumbers);
            if (_whitespace is not null) result = _whitespace.Replace(result, string.Empty);
            if (_options.RemoveBlankLines && string.IsNullOrWhiteSpace(result)) return null;
            if (_options.RemoveLinesAtMost is { } length && result.Length <= length) return null;
            return result;
        }

        private static string RemoveFields(string value, char separator, IReadOnlySet<int> fields)
        {
            var parts = value.Split(separator);
            if (!fields.Any(n => n > 0 && n <= parts.Length)) return value;
            var kept = parts.Where((_, index) => !fields.Contains(index + 1));
            return string.Join(separator, kept);
        }
    }

    private sealed class LruWriterPool : IAsyncDisposable
    {
        private sealed record Entry(AtomicOutput Output, LinkedListNode<string> Node);
        private readonly string _directory;
        private readonly string _stagingDirectory;
        private readonly string _prefix;
        private readonly int _capacity;
        private readonly bool _append;
        private readonly Dictionary<string, Entry> _open = [];
        private readonly LinkedList<string> _lru = [];
        private readonly HashSet<string> _created = [];

        private bool _committed;

        public LruWriterPool(string directory, string prefix, int capacity, bool append)
        {
            _directory = Path.GetFullPath(directory);
            _stagingDirectory = Path.Combine(_directory, $".fastgrep-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_stagingDirectory);
            _prefix = prefix;
            _capacity = Math.Max(1, capacity);
            _append = append;
        }

        public async Task WriteLineAsync(string key, string value, CancellationToken token)
        {
            if (!_open.TryGetValue(key, out var entry))
            {
                if (_open.Count >= _capacity)
                {
                    var oldest = _lru.Last!.Value;
                    var victim = _open[oldest];
                    await victim.Output.CommitAsync(token);
                    await victim.Output.DisposeAsync();
                    _open.Remove(oldest);
                    _lru.RemoveLast();
                }
                var path = Path.Combine(_stagingDirectory, $"{_prefix}{key}.txt");
                var output = AtomicOutput.Create(path, new UTF8Encoding(false), append: _created.Contains(key));
                _created.Add(key);
                var node = _lru.AddFirst(key);
                entry = new Entry(output, node);
                _open[key] = entry;
            }
            else
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
            }
            await entry.Output.Writer.WriteLineAsync(value.AsMemory(), token);
        }

        public async Task CommitAsync(CancellationToken token)
        {
            foreach (var entry in _open.Values)
            {
                await entry.Output.CommitAsync(token);
                await entry.Output.DisposeAsync();
            }
            _open.Clear();
            _lru.Clear();

            foreach (var staged in Directory.EnumerateFiles(_stagingDirectory, "*.txt"))
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(_directory, Path.GetFileName(staged));
                if (!_append)
                {
                    File.Move(staged, destination, overwrite: true);
                    continue;
                }
                await using var output = AtomicOutput.Create(destination, new UTF8Encoding(false), append: true);
                using var reader = new StreamReader(staged, new UTF8Encoding(false));
                while (await reader.ReadLineAsync(token) is { } line)
                    await output.Writer.WriteLineAsync(line.AsMemory(), token);
                await output.CommitAsync(token);
            }
            Directory.Delete(_stagingDirectory);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var entry in _open.Values) await entry.Output.DisposeAsync();
            _open.Clear();
            if (!_committed && Directory.Exists(_stagingDirectory)) Directory.Delete(_stagingDirectory, recursive: true);
        }
    }
}
