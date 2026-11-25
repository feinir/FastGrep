using System.Text;
using System.Text.RegularExpressions;

namespace FastGrep.Core;

public sealed class MergeSplitEngine
{
    public async Task MergeAsync(IEnumerable<string> orderedFiles, string destination, bool ensureNewlineBetweenFiles = true,
        CancellationToken token = default)
    {
        var files = orderedFiles.Select(Path.GetFullPath).ToArray();
        destination = FileOperations.EnsureSeparateOutput(files, destination);
        await using var output = AtomicOutput.Create(destination, new UTF8Encoding(false));
        var stream = output.Writer.BaseStream;
        byte[]? separator = null;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length == 0) continue;
            if (separator is not null) await stream.WriteAsync(separator, token);
            separator = ensureNewlineBetweenFiles ? await GetMissingSeparatorAsync(input, token) : null;
            await input.CopyToAsync(stream, 1024 * 1024, token);
        }
        await output.CommitAsync(token);
    }

    private static async Task<byte[]?> GetMissingSeparatorAsync(FileStream input, CancellationToken token)
    {
        var probe = new byte[4];
        var size = input.Length;
        var count = await input.ReadAtLeastAsync(probe, (int)Math.Min(size, probe.Length), false, token);
        var encoding = probe.AsSpan(0, count) switch
        {
            [0xff, 0xfe, 0, 0] => Encoding.UTF32,
            [0, 0, 0xfe, 0xff] => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
            [0xff, 0xfe, ..] => Encoding.Unicode,
            [0xfe, 0xff, ..] => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8
        };
        input.Position = Math.Max(0, size - probe.Length);
        count = await input.ReadAtLeastAsync(probe, (int)Math.Min(size, probe.Length), false, token);
        input.Position = 0;
        var tail = probe.AsSpan(0, count);
        return tail.EndsWith(encoding.GetBytes("\n")) || tail.EndsWith(encoding.GetBytes("\r"))
            ? null : encoding.GetBytes(Environment.NewLine);
    }

    public async Task<IReadOnlyList<string>> GroupMergeAsync(string sourceDirectory, string targetDirectory,
        string? sourcePrefixToRemove = null, string? sourceSuffixRegexToRemove = null,
        string? targetPrefixToRemove = null, string? targetSuffixRegexToRemove = null,
        CancellationToken token = default)
    {
        var targets = Directory.EnumerateFiles(targetDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => NormalizeKey(Path.GetFileName(path), targetPrefixToRemove, targetSuffixRegexToRemove),
                path => path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            var key = NormalizeKey(Path.GetFileName(source), sourcePrefixToRemove, sourceSuffixRegexToRemove);
            if (!targets.TryGetValue(key, out var target)) target = Path.Combine(targetDirectory, Path.GetFileName(source));
            var staging = target + $".{Guid.NewGuid():N}.tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                if (File.Exists(target))
                {
                    await using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                    await existing.CopyToAsync(output, 1024 * 1024, token);
                }
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                await input.CopyToAsync(output, 1024 * 1024, token);
            }
            File.Move(staging, target, true);
            changed.Add(target);
        }
        return changed;
    }

    private static string NormalizeKey(string name, string? prefix, string? suffixRegex)
    {
        var result = name;
        if (!string.IsNullOrEmpty(prefix) && result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result = result[prefix.Length..];
        if (!string.IsNullOrEmpty(suffixRegex)) result = Regex.Replace(result, suffixRegex + "$", string.Empty,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return result;
    }

    public async Task<IReadOnlyList<string>> SplitAsync(string source, SplitOptions options,
        IProgress<JobProgress>? progress = null, CancellationToken token = default)
    {
        if ((options.LinesPerFile is null) == (options.SoftBytesPerFile is null))
            throw new ArgumentException("Specify exactly one split condition.");
        Directory.CreateDirectory(options.OutputDirectory);
        var encoding = Encodings.Get(options.EncodingName);
        var outputs = new List<string>();
        var stem = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var total = new FileInfo(source).Length;
        using var reader = new StreamReader(source, encoding, true, 1024 * 1024);
        StreamWriter? writer = null;
        long lines = 0, bytes = 0, totalLines = 0;
        try
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1L;
                if (writer is null || (lines > 0 && (options.LinesPerFile is { } maxLines && lines >= maxLines ||
                    options.SoftBytesPerFile is { } maxBytes && bytes + lineBytes > maxBytes)))
                {
                    if (writer is not null) await writer.DisposeAsync();
                    var path = Path.Combine(options.OutputDirectory, $"{stem}.{outputs.Count + 1:D4}{extension}");
                    outputs.Add(path);
                    writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, true),
                        new UTF8Encoding(false), 1024 * 1024);
                    lines = 0; bytes = 0;
                }
                await writer.WriteLineAsync(line.AsMemory(), token);
                lines++; totalLines++; bytes += lineBytes;
                if ((totalLines & 0x3fff) == 0)
                    progress?.Report(new JobProgress("split", source, Math.Min(reader.BaseStream.Position, total), total, totalLines));
            }
        }
        finally { if (writer is not null) await writer.DisposeAsync(); }
        return outputs;
    }
}
