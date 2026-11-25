using FastGrep.Core;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Help(); return 0; }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            var map = Parse(args[1..]);
            return args[0].ToLowerInvariant() switch
            {
                "search" => await Search(map, cts.Token),
                "clean" => await Clean(map, cts.Token),
                "dedup" => await Dedup(map, cts.Token),
                "find" => Find(map),
                "copy" => await CopyMove(map, false, cts.Token),
                "move" => await CopyMove(map, true, cts.Token),
                "merge" => await Merge(map, cts.Token),
                "group-merge" => await GroupMerge(map, cts.Token),
                "split" => await Split(map, cts.Token),
                "read" => await Read(map, cts.Token),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return 1; }
    }

    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"Expected option, got: {args[i]}");
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            if (!map.TryGetValue(key, out var values)) map[key] = values = [];
            values.Add(value);
        }
        return map;
    }

    private static string One(Dictionary<string, List<string>> map, string key, string? fallback = null)
        => map.TryGetValue(key, out var values) ? values[^1] : fallback ?? throw new ArgumentException($"Missing --{key}");
    private static string[] Many(Dictionary<string, List<string>> map, string key) => map.TryGetValue(key, out var values) ? values.ToArray() : [];
    private static bool Flag(Dictionary<string, List<string>> map, string key) => map.TryGetValue(key, out var value) && bool.Parse(value[^1]);

    private static string[] Inputs(Dictionary<string, List<string>> map)
    {
        var files = Many(map, "file").ToList();
        foreach (var root in Many(map, "dir")) files.AddRange(FileOperations.Discover(new FileDiscoveryOptions
        { Root = root, Includes = One(map, "include", "*").Split('|'), Excludes = One(map, "exclude", "").Split('|', StringSplitOptions.RemoveEmptyEntries) }));
        if (files.Count == 0) throw new ArgumentException("Specify --file and/or --dir.");
        return files.ToArray();
    }

    private static async Task<int> Search(Dictionary<string, List<string>> map, CancellationToken token)
    {
        var patterns = Many(map, "pattern").ToList();
        var keywordFiles = Many(map, "pattern-file");
        var outputPath = map.ContainsKey("output") ? One(map, "output") : null;
        if (!string.IsNullOrWhiteSpace(outputPath))
            outputPath = FileOperations.EnsureSeparateOutput(keywordFiles, outputPath);
        foreach (var file in keywordFiles) patterns.AddRange(await File.ReadAllLinesAsync(file, token));
        var computeThreads = int.Parse(One(map, "threads", Math.Max(1, Environment.ProcessorCount / 2).ToString()));
        var options = new SearchOptions
        {
            Patterns = patterns.Where(x => x.Length > 0).Distinct().ToArray(),
            IgnoreCase = Flag(map, "ignore-case"),
            UseRegex = Flag(map, "regex"),
            IncludeFileName = !Flag(map, "no-filename"),
            OutputPath = outputPath,
            FirstMatchPerFile = Flag(map, "first"),
            MaxResults = long.Parse(One(map, "max-results", long.MaxValue.ToString())),
            DegreeOfParallelism = computeThreads,
            ReadDegreeOfParallelism = int.Parse(One(map, "read-threads", computeThreads.ToString())),
            MaxConcurrentReadsPerVolume = int.Parse(One(map, "reads-per-volume", "6")),
            ReadBufferSize = checked(int.Parse(One(map, "buffer-kb", "4096")) * 1024),
            ProcessingMode = One(map, "mode", "character").ToLowerInvariant() switch
            {
                "line-block" or "lineblock" => SearchProcessingMode.LineBlock,
                "single-line" or "singleline" => SearchProcessingMode.SingleLine,
                "character" or "character-block" or "text-block" => SearchProcessingMode.CharacterBlock,
                var mode => throw new ArgumentException($"Unknown search mode: {mode}")
            },
            EncodingName = One(map, "encoding", "utf-8")
        };
        var summary = await new SearchEngine().SearchAsync(Inputs(map), options,
            options.OutputPath is null ? batch => { foreach (var item in batch) Console.WriteLine(item.ToDisplayString(options.IncludeFileName)); return ValueTask.CompletedTask; }
        : null,
            cancellationToken: token);
        Console.Error.WriteLine($"Files={summary.FilesScanned} Lines={summary.LinesScanned} Matches={summary.MatchCount} Bytes={summary.BytesScanned} Time={summary.Elapsed}");
        foreach (var error in summary.Errors) Console.Error.WriteLine(error);
        return summary.Errors.Count == 0 ? 0 : 2;
    }

    private static CleanOptions MakeCleanOptions(Dictionary<string, List<string>> map) => new()
    {
        RemovePrefix = map.ContainsKey("remove-prefix") ? One(map, "remove-prefix") : null,
        RemoveContaining = map.ContainsKey("remove-containing") ? One(map, "remove-containing") : null,
        RemoveRegex = map.ContainsKey("remove-regex") ? One(map, "remove-regex") : null,
        IgnoreCase = Flag(map, "ignore-case"),
        Trim = !Flag(map, "no-trim"),
        RemoveAllWhitespace = Flag(map, "remove-whitespace"),
        RemoveBlankLines = !Flag(map, "keep-blank"),
        RemoveLinesAtMost = map.ContainsKey("remove-short") ? int.Parse(One(map, "remove-short")) : null,
        FieldSeparator = map.ContainsKey("separator") ? One(map, "separator")[0] : null,
        RemoveFieldNumbers = Many(map, "remove-field").Select(int.Parse).ToHashSet(),
        EncodingName = One(map, "encoding", "utf-8"),
        Append = Flag(map, "append")
    };

    private static async Task<int> Clean(Dictionary<string, List<string>> map, CancellationToken token)
    {
        var engine = new CleanEngine();
        if (map.ContainsKey("group-dir"))
            await engine.CleanToGroupsAsync(Inputs(map), One(map, "group-dir"), MakeCleanOptions(map), int.Parse(One(map, "group-index", "0")), One(map, "prefix", ""), cancellationToken: token);
        else await engine.CleanAsync(Inputs(map), One(map, "output"), MakeCleanOptions(map), cancellationToken: token);
        return 0;
    }

    private static async Task<int> Dedup(Dictionary<string, List<string>> map, CancellationToken token)
    {
        var summary = await new DedupEngine().DeduplicateAsync(Inputs(map), One(map, "output"), new DedupOptions
        {
            Mode = Flag(map, "sorted") ? DedupMode.SortedExternal : DedupMode.StableInMemory,
            Trim = !Flag(map, "no-trim"),
            IgnoreCase = Flag(map, "ignore-case"),
            MemoryBudgetBytes = long.Parse(One(map, "memory-mb", "256")) * 1024 * 1024,
            EncodingName = One(map, "encoding", "utf-8")
        }, cancellationToken: token);
        Console.Error.WriteLine($"Input={summary.InputLines} Output={summary.OutputLines} Duplicates={summary.DuplicateLines} Runs={summary.RunFiles}");
        return 0;
    }

    private static int Find(Dictionary<string, List<string>> map)
    {
        var files = FileOperations.Discover(new FileDiscoveryOptions
        {
            Root = One(map, "dir"),
            Includes = One(map, "include", "*").Split('|'),
            Excludes = One(map, "exclude", "").Split('|', StringSplitOptions.RemoveEmptyEntries),
            Recursive = !Flag(map, "top-only"),
            IncludeExtensionless = !Flag(map, "no-extensionless"),
            MinimumBytes = map.ContainsKey("min-mb") ? long.Parse(One(map, "min-mb")) * 1024 * 1024 : null,
            MaximumBytes = map.ContainsKey("max-mb") ? long.Parse(One(map, "max-mb")) * 1024 * 1024 : null
        });
        foreach (var file in files) Console.WriteLine(file);
        return 0;
    }

    private static async Task<int> CopyMove(Dictionary<string, List<string>> map, bool move, CancellationToken token)
    { await FileOperations.CopyOrMoveAsync(Inputs(map), One(map, "root"), One(map, "output-dir"), move, Flag(map, "overwrite"), token: token); return 0; }
    private static async Task<int> Merge(Dictionary<string, List<string>> map, CancellationToken token)
    { await new MergeSplitEngine().MergeAsync(Inputs(map), One(map, "output"), !Flag(map, "no-separator-newline"), token); return 0; }
    private static async Task<int> GroupMerge(Dictionary<string, List<string>> map, CancellationToken token)
    { await new MergeSplitEngine().GroupMergeAsync(One(map, "source-dir"), One(map, "target-dir"), map.ContainsKey("source-prefix") ? One(map, "source-prefix") : null, map.ContainsKey("source-suffix-regex") ? One(map, "source-suffix-regex") : null, map.ContainsKey("target-prefix") ? One(map, "target-prefix") : null, map.ContainsKey("target-suffix-regex") ? One(map, "target-suffix-regex") : null, token); return 0; }

    private static async Task<int> Split(Dictionary<string, List<string>> map, CancellationToken token)
    {
        var result = await new MergeSplitEngine().SplitAsync(One(map, "file"), new SplitOptions
        {
            OutputDirectory = One(map, "output-dir"),
            LinesPerFile = map.ContainsKey("lines") ? long.Parse(One(map, "lines")) : null,
            SoftBytesPerFile = map.ContainsKey("size-mb") ? long.Parse(One(map, "size-mb")) * 1024 * 1024 : null,
            EncodingName = One(map, "encoding", "utf-8")
        }, token: token);
        foreach (var file in result) Console.WriteLine(file); return 0;
    }

    private static async Task<int> Read(Dictionary<string, List<string>> map, CancellationToken token)
    {
        var reader = new TextReaderService(); var size = int.Parse(One(map, "bytes", "1048576"));
        var text = Flag(map, "tail") ? await reader.ReadTailAsync(One(map, "file"), size, One(map, "encoding", "utf-8"), token)
            : await reader.ReadRangeAsync(One(map, "file"), long.Parse(One(map, "offset", "0")), size, One(map, "encoding", "utf-8"), Flag(map, "align-line"), token);
        Console.Write(text); return 0;
    }

    private static void Help() => Console.WriteLine("""
FastGrep - cross-platform large text toolkit

  search --file PATH...|--dir DIR --pattern TEXT... [--ignore-case] [--regex] [--output FILE]
         [--threads N] [--read-threads N] [--reads-per-volume N] [--buffer-kb N]
         [--mode line-block|character|single-line]
         (defaults: NAS 4 reads, Thunderbolt SSD 6 reads, 4096 KiB buffer)
  clean  --file PATH... --output FILE [--remove-prefix X] [--remove-containing X] [--remove-regex X]
  dedup  --file PATH... --output FILE [--sorted] [--memory-mb 256]
  find   --dir DIR [--include *.txt|*.log] [--exclude *.tmp] [--min-mb N] [--max-mb N]
  copy|move --file PATH... --root DIR --output-dir DIR [--overwrite]
  merge  --file PATH... --output FILE
  group-merge --source-dir DIR --target-dir DIR [prefix/suffix options]
  split  --file PATH --output-dir DIR (--lines N | --size-mb N)
  read   --file PATH [--offset N|--tail] [--bytes N] [--align-line]

Repeat --file and --pattern for multiple values. Ctrl+C safely cancels a running job.
""");
}
