using System.Text.RegularExpressions;

namespace FastGrep.Core;

public sealed class OutputSourceConflictException(string path)
    : InvalidOperationException($"Destination cannot be a source file: {path}");

public static class FileOperations
{
    public static string EnsureSeparateOutput(IEnumerable<string> sourceFiles, string destination)
    {
        var fullPath = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var destinationIdentity = FileIdentity.TryRead(fullPath);
        foreach (var source in sourceFiles)
        {
            var sourcePath = Path.GetFullPath(source);
            if (string.Equals(sourcePath, fullPath, comparison) ||
                destinationIdentity is { } id && FileIdentity.TryRead(sourcePath) == id)
                throw new OutputSourceConflictException(fullPath);
        }
        return fullPath;
    }

    public static IReadOnlyList<string> Discover(FileDiscoveryOptions options, CancellationToken token = default)
    {
        if (!Directory.Exists(options.Root)) throw new DirectoryNotFoundException(options.Root);
        var result = new List<string>();
        var includePatterns = CompileGlobs(options.Includes);
        var excludePatterns = CompileGlobs(options.Excludes);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(options.Root));
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); } catch { continue; }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (options.Recursive && !attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(entry);
                    continue;
                }
                var name = Path.GetFileName(entry);
                var extensionless = string.IsNullOrEmpty(Path.GetExtension(name));
                if (extensionless && !options.IncludeExtensionless) continue;
                if (!extensionless && !MatchesAny(name, includePatterns)) continue;
                if (MatchesAny(name, excludePatterns)) continue;
                var length = new FileInfo(entry).Length;
                if (options.MinimumBytes is { } min && length < min) continue;
                if (options.MaximumBytes is { } max && length > max) continue;
                result.Add(entry);
            }
        }
        result.Sort(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return result;
    }

    private static Regex[] CompileGlobs(IReadOnlyList<string> patterns) => patterns.Select(pattern => new Regex(
        GlobToRegex(pattern), RegexOptions.CultureInvariant | RegexOptions.Compiled |
        (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1))).ToArray();

    private static bool MatchesAny(string value, IReadOnlyList<Regex> patterns) => patterns.Any(pattern => pattern.IsMatch(value));

    private static string GlobToRegex(string pattern) => "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

    public static async Task CopyOrMoveAsync(IEnumerable<string> sourceFiles, string sourceRoot, string destinationRoot,
        bool move, bool overwrite, IProgress<JobProgress>? progress = null, CancellationToken token = default)
    {
        var files = sourceFiles.Select(Path.GetFullPath).ToArray();
        sourceRoot = Path.GetFullPath(sourceRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);
        long done = 0;
        var total = files.Sum(f => new FileInfo(f).Length);
        foreach (var source in files)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, source);
            if (relative.StartsWith(".." + Path.DirectorySeparatorChar) || relative == "..")
                throw new InvalidOperationException($"Source is outside source root: {source}");
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!overwrite && File.Exists(destination)) throw new IOException($"Destination exists: {destination}");
            if (move)
            {
                try { File.Move(source, destination, overwrite); }
                catch (IOException)
                {
                    await CopyFileAsync(source, destination, overwrite, token);
                    File.Delete(source);
                }
            }
            else await CopyFileAsync(source, destination, overwrite, token);
            done += new FileInfo(destination).Length;
            progress?.Report(new JobProgress(move ? "move" : "copy", source, done, total, done));
        }
    }

    private static async Task CopyFileAsync(string source, string destination, bool overwrite, CancellationToken token)
    {
        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, mode, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, token);
        await output.FlushAsync(token);
    }
}
