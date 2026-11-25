using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace FastGrep.Core;

public sealed record JobProgress(string Stage, string? CurrentFile, long BytesProcessed, long TotalBytes,
    long ItemsProcessed, string? Message = null, long ComputedBytes = -1)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesProcessed / TotalBytes, 0, 1);
}

public sealed record SearchMatch(string File, long LineNumber, long ByteOffset, string Text, IReadOnlyList<int> PatternIds)
{
    public string ToDisplayString(bool includeFile = true) =>
        includeFile ? ByteOffset >= 0 ? $"{File}:{LineNumber}:{ByteOffset}: {Text}" : $"{File}:{LineNumber}: {Text}" : Text;
}

public enum SearchProcessingMode
{
    LineBlock,
    CharacterBlock,
    SingleLine
}

public sealed record SearchOptions
{
    public required IReadOnlyList<string> Patterns { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public bool UseRegex { get; init; }
    public bool IncludeFileName { get; init; } = true;
    public bool FirstMatchPerFile { get; init; }
    public long MaxResults { get; init; } = long.MaxValue;
    /// <summary>Total number of files that may be read concurrently.</summary>
    public int? ReadDegreeOfParallelism { get; init; }
    public int DegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    /// <summary>Optional live controller used to change read and compute concurrency while searching.</summary>
    public SearchConcurrencyController? ConcurrencyController { get; init; }
    /// <summary>Allows the host to close active source handles when an operating-system read ignores cancellation.</summary>
    public SearchIoController? IoController { get; init; }
    /// <summary>
    /// Limits simultaneous file reads that target the same mounted volume. Keeping this low
    /// avoids seek storms and excessive command queue pressure on removable USB/Thunderbolt disks.
    /// </summary>
    public int MaxConcurrentReadsPerVolume { get; init; } = 6;
    /// <summary>Sequential input buffer size. Four MiB reduces SMB round trips while remaining efficient on SSDs.</summary>
    public int ReadBufferSize { get; init; } = 4 * 1024 * 1024;
    public SearchProcessingMode ProcessingMode { get; init; } = SearchProcessingMode.CharacterBlock;
    /// <summary>Number of complete source lines grouped into one line block.</summary>
    public int LineBlockLines { get; init; } = 10_000;
    /// <summary>Character-block look-ahead/look-behind window used to find a natural newline.</summary>
    public int CharacterBlockBoundaryTolerance { get; init; } = 4 * 1024;
    /// <summary>
    /// Maximum time one source read may make no progress before the search is treated as an I/O
    /// interruption. This feeds the automatic pause-and-resume recovery path.
    /// </summary>
    public TimeSpan ReadStallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Byte offset used when resuming a single-file search.
    /// </summary>
    public long StartOffset { get; init; }
    /// <summary>Optional per-file processed offsets used when resuming a multi-file search.</summary>
    public IReadOnlyDictionary<string, long>? StartOffsets { get; init; }
    /// <summary>Reports the last byte offset confirmed by a successful source read.</summary>
    public Action<string, long>? ReadOffsetChanged { get; init; }
    /// <summary>
    /// Reports the highest contiguous byte offset whose blocks have completed computation.
    /// This is the safe, non-duplicating resume checkpoint.
    /// </summary>
    public Action<string, long>? ProcessedOffsetChanged { get; init; }
    /// <summary>Runs after every queued block for a successfully read file has been computed.</summary>
    public Func<string, ValueTask>? FileCompleted { get; init; }
    /// <summary>Reports a file that was skipped while its containing volume remained available.</summary>
    public Action<string, string>? FileSkipped { get; init; }
    public string EncodingName { get; init; } = "utf-8";
    public string? OutputPath { get; init; }
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed class SearchIoController
{
    private readonly ConcurrentDictionary<string, FileStream> _activeStreams =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public int ActiveStreamCount => _activeStreams.Count;
    public IReadOnlyList<string> ActiveFiles => _activeStreams.Keys.ToArray();

    internal void Register(string file, FileStream stream) => _activeStreams[file] = stream;

    internal void Unregister(string file) => _activeStreams.TryRemove(file, out _);

    public void AbortPendingReads()
    {
        foreach (var stream in _activeStreams.Values)
        {
            try { stream.Dispose(); }
            catch { }
        }
    }
}

public sealed record SearchSummary(long FilesScanned, long BytesScanned, long LinesScanned, long MatchCount, TimeSpan Elapsed, IReadOnlyList<string> Errors);

public sealed class SearchIoInterruptedException : IOException
{
    public SearchIoInterruptedException(string filePath, long confirmedOffset, Exception innerException)
        : base($"读取设备发生 I/O 异常：{filePath}（最后确认偏移 {confirmedOffset:N0} 字节）。", innerException)
    {
        FilePath = filePath;
        ConfirmedOffset = confirmedOffset;
    }

    public string FilePath { get; }
    public long ConfirmedOffset { get; }
}

public sealed record CleanOptions
{
    public string? RemovePrefix { get; init; }
    public string? RemoveContaining { get; init; }
    public string? RemoveRegex { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public bool? RemovePrefixIgnoreCase { get; init; }
    public bool? RemoveContainingIgnoreCase { get; init; }
    public bool Trim { get; init; } = true;
    public bool RemoveAllWhitespace { get; init; }
    public bool RemoveBlankLines { get; init; } = true;
    public int? RemoveLinesAtMost { get; init; }
    public char? FieldSeparator { get; init; }
    public IReadOnlySet<int> RemoveFieldNumbers { get; init; } = new HashSet<int>();
    public string EncodingName { get; init; } = "utf-8";
    public bool Append { get; init; }
    public bool GroupNonEmailSeparately { get; init; }
}

public sealed record CleanSummary(long Files, long InputLines, long OutputLines, long RemovedLines, long InputBytes, TimeSpan Elapsed, IReadOnlyList<string> Errors);

public enum DedupMode { StableInMemory, SortedExternal }

public sealed record DedupOptions
{
    public DedupMode Mode { get; init; } = DedupMode.StableInMemory;
    public bool Trim { get; init; } = true;
    public bool IgnoreCase { get; init; }
    public long MemoryBudgetBytes { get; init; } = 256L * 1024 * 1024;
    public string EncodingName { get; init; } = "utf-8";
}

public sealed record DedupSummary(long InputLines, long OutputLines, long DuplicateLines, int RunFiles, TimeSpan Elapsed);

public sealed record FileDiscoveryOptions
{
    public required string Root { get; init; }
    public IReadOnlyList<string> Includes { get; init; } = ["*"];
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public bool Recursive { get; init; } = true;
    public long? MinimumBytes { get; init; }
    public long? MaximumBytes { get; init; }
    public bool IncludeExtensionless { get; init; } = true;
}

public sealed record SplitOptions
{
    public long? LinesPerFile { get; init; }
    public long? SoftBytesPerFile { get; init; }
    public required string OutputDirectory { get; init; }
    public string EncodingName { get; init; } = "utf-8";
}

public static class Encodings
{
    static Encodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Get(string name) => new EncodingWrapper(Encoding.GetEncoding(name,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));

    private sealed class EncodingWrapper(Encoding inner) : Encoding
    {
        public override string WebName => inner.WebName;
        public override int GetByteCount(char[] chars, int index, int count) => inner.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => inner.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override int GetMaxByteCount(int charCount) => inner.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => inner.GetMaxCharCount(byteCount);
        public override byte[] GetPreamble() => inner.GetPreamble();
        public override Decoder GetDecoder() => inner.GetDecoder();
        public override Encoder GetEncoder() => inner.GetEncoder();
    }
}
