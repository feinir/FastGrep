using System.Diagnostics;
using System.Text;

namespace FastGrep.Core;

public sealed class DedupEngine
{
    private static readonly Encoding OutputEncoding = new UTF8Encoding(false);

    public async Task<DedupSummary> DeduplicateAsync(IEnumerable<string> sourceFiles, string destination, DedupOptions options,
        IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = sourceFiles.Select(Path.GetFullPath).ToArray();
        destination = FileOperations.EnsureSeparateOutput(files, destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        var watch = Stopwatch.StartNew();
        var result = options.Mode == DedupMode.SortedExternal
            ? await ExternalSortedAsync(files, destination, options, progress, cancellationToken)
            : await StableAsync(files, destination, options, progress, cancellationToken);
        watch.Stop();
        return result with { Elapsed = watch.Elapsed };
    }

    private static async Task<DedupSummary> StableAsync(string[] files, string destination, DedupOptions options,
        IProgress<JobProgress>? progress, CancellationToken token)
    {
        var encoding = Encodings.Get(options.EncodingName);
        var comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        long input = 0, outputCount = 0, bytes = 0, estimated = 0;
        var total = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        await using var output = AtomicOutput.Create(destination, OutputEncoding);
        foreach (var file in files)
        {
            using var reader = new StreamReader(file, encoding, true, 1024 * 1024);
            while (await reader.ReadLineAsync(token) is { } raw)
            {
                input++;
                var line = options.Trim ? raw.Trim() : raw;
                if (!seen.Add(line)) continue;
                estimated += EstimateLineBytes(line);
                if (estimated > options.MemoryBudgetBytes)
                    throw new InvalidOperationException("Stable deduplication exceeded the memory budget. Use SortedExternal mode.");
                await output.Writer.WriteLineAsync(line.AsMemory(), token);
                outputCount++;
            }
            bytes += new FileInfo(file).Length;
            progress?.Report(new JobProgress("dedup", file, bytes, total, input));
        }
        await output.CommitAsync(token);
        return new DedupSummary(input, outputCount, input - outputCount, 0, TimeSpan.Zero);
    }

    private static long EstimateLineBytes(string line) => 56L + line.Length * 2L;

    private static async Task<DedupSummary> ExternalSortedAsync(string[] files, string destination, DedupOptions options,
        IProgress<JobProgress>? progress, CancellationToken token)
    {
        var comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var encoding = Encodings.Get(options.EncodingName);
        var tempDir = Path.Combine(Path.GetTempPath(), "fastgrep-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var chunk = new HashSet<string>(comparer);
        var runCount = 0;
        long estimated = 0, input = 0, bytes = 0, largestLine = 0;
        var bufferSize = (int)Math.Clamp(options.MemoryBudgetBytes / 64, 128, 16 * 1024);
        var chunkBudget = Math.Max(1, options.MemoryBudgetBytes / 2);
        var total = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        try
        {
            foreach (var file in files)
            {
                using var reader = new StreamReader(file, encoding, true, bufferSize);
                while (await reader.ReadLineAsync(token) is { } raw)
                {
                    input++;
                    var line = options.Trim ? raw.Trim() : raw;
                    if (chunk.Contains(line)) continue;
                    var lineBytes = EstimateLineBytes(line);
                    largestLine = Math.Max(largestLine, lineBytes);
                    if (chunk.Count > 0 && lineBytes > chunkBudget - estimated)
                    {
                        await WriteRunAsync(chunk, comparer, RunPath(tempDir, runCount++), bufferSize, token);
                        chunk.Clear(); estimated = 0;
                    }
                    chunk.Add(line);
                    estimated += lineBytes;
                }
                bytes += new FileInfo(file).Length;
                progress?.Report(new JobProgress("dedup-runs", file, bytes, total, input));
            }
            if (chunk.Count > 0)
                await WriteRunAsync(chunk, comparer, RunPath(tempDir, runCount++), bufferSize, token);
            chunk = [];
            // Each reader holds its I/O buffers and one queued line. Two inputs are needed
            // even when a single exceptionally long line already exceeds the budget.
            var readerBudget = 4L * bufferSize + 2 * largestLine;
            var fanIn = (int)Math.Clamp(options.MemoryBudgetBytes / 2 / readerBudget, 2, 32);
            var firstRun = 0;
            var remainingRuns = runCount;
            var runIndex = runCount;
            while (remainingRuns > fanIn)
            {
                var nextPass = runIndex;
                for (var offset = 0; offset < remainingRuns;)
                {
                    token.ThrowIfCancellationRequested();
                    var groupSize = Math.Min(fanIn, remainingRuns - offset);
                    var group = PathsForRuns(firstRun + offset, groupSize);
                    offset += groupSize;
                    var path = RunPath(tempDir, runIndex++);
                    if (groupSize == 1)
                    {
                        File.Move(group[0], path);
                        continue;
                    }
                    await using (var writer = new StreamWriter(path, false, OutputEncoding, bufferSize))
                        await MergeRunsAsync(group, writer, comparer, bufferSize, token);
                    foreach (var previous in group) File.Delete(previous);
                }
                firstRun = nextPass;
                remainingRuns = runIndex - nextPass;
            }
            await using var output = AtomicOutput.Create(destination, OutputEncoding, bufferSize: bufferSize);
            var outputCount = await MergeRunsAsync(PathsForRuns(firstRun, remainingRuns),
                output.Writer, comparer, bufferSize, token);
            await output.CommitAsync(token);
            return new DedupSummary(input, outputCount, input - outputCount, runCount, TimeSpan.Zero);

            string[] PathsForRuns(int start, int count) =>
                Enumerable.Range(start, count).Select(index => RunPath(tempDir, index)).ToArray();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string RunPath(string directory, int index) => Path.Combine(directory, $"run-{index:D6}.txt");

    private static async Task WriteRunAsync(HashSet<string> chunk, StringComparer comparer, string path,
        int bufferSize, CancellationToken token)
    {
        await using var writer = new StreamWriter(path, false, OutputEncoding, bufferSize);
        foreach (var line in chunk.Order(comparer)) await writer.WriteLineAsync(line.AsMemory(), token);
    }

    private static async Task<long> MergeRunsAsync(IReadOnlyList<string> runs, StreamWriter output,
        StringComparer comparer, int bufferSize, CancellationToken token)
    {
        var readers = new List<StreamReader>(runs.Count);
        var queue = new PriorityQueue<(string Line, int Reader), (string Line, int Reader)>(
            Comparer<(string Line, int Reader)>.Create((left, right) =>
            {
                var order = comparer.Compare(left.Line, right.Line);
                return order != 0 ? order : left.Reader.CompareTo(right.Reader);
            }));
        try
        {
            foreach (var path in runs)
            {
                var reader = new StreamReader(path, OutputEncoding, detectEncodingFromByteOrderMarks: false, bufferSize);
                var index = readers.Count;
                readers.Add(reader);
                if (await reader.ReadLineAsync(token) is { } line) queue.Enqueue((line, index), (line, index));
            }
            string? previous = null;
            long count = 0;
            while (queue.TryDequeue(out var item, out _))
            {
                if (previous is null || !comparer.Equals(previous, item.Line))
                {
                    await output.WriteLineAsync(item.Line.AsMemory(), token);
                    previous = item.Line; count++;
                }
                if (await readers[item.Reader].ReadLineAsync(token) is { } next)
                    queue.Enqueue((next, item.Reader), (next, item.Reader));
            }
            return count;
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
        }
    }
}
