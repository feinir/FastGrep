using System.Text;
using FastGrep.Core;

var root = Path.Combine(Path.GetTempPath(), "fastgrep-tests-" + Guid.NewGuid().ToString("N"));
var pathComparerForTests = OperatingSystem.IsWindows()
    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
Directory.CreateDirectory(root);
try
{
    var a = Path.Combine(root, "a.txt");
    var b = Path.Combine(root, "b.log");
    await File.WriteAllTextAsync(a, "Alpha\r\nbeta apple\n中文苹果\nDUP\ndup\n", new UTF8Encoding(false));
    await File.WriteAllTextAsync(b, "zero\nneedle here\nomega\n", new UTF8Encoding(false));

    var matches = new List<SearchMatch>();
    var search = await new SearchEngine().SearchAsync([a, b], new SearchOptions
    {
        Patterns = ["APPLE", "needle", "不存在", "中文", "omega"],
        IgnoreCase = true,
        DegreeOfParallelism = 2
    }, batch => { matches.AddRange(batch); return ValueTask.CompletedTask; });
    Equal(4L, search.MatchCount, "multi-pattern search count");
    Equal(4, matches.Count, "search callback count");
    True(matches.Any(x => x.Text == "beta apple"), "ASCII ignore-case search");
    True(matches.Any(x => x.Text == "中文苹果"), "Unicode search");
    True(matches.All(x => x.ByteOffset == -1), "search does not report inaccurate buffered byte offsets");
    Equal(new FileInfo(a).Length + new FileInfo(b).Length, search.BytesScanned, "search reports actual bytes read");

    var original = await File.ReadAllBytesAsync(a);
    var inPlace = new SearchOptions { Patterns = ["Alpha"], OutputPath = a };
    await ThrowsAsync<OutputSourceConflictException>(() => new SearchEngine().SearchAsync([a, b], inPlace),
        "search rejects an output that is also an input");
    var afterConflict = await File.ReadAllBytesAsync(a);
    True(original.SequenceEqual(afterConflict), "same-path search leaves the source unchanged");
    if (!OperatingSystem.IsWindows())
    {
        var link = Path.Combine(root, "alias.txt");
        File.CreateSymbolicLink(link, a);
        await ThrowsAsync<OutputSourceConflictException>(() => new SearchEngine().SearchAsync([a], inPlace with { OutputPath = link }),
            "search rejects a symbolic link to an input");
        await ThrowsAsync<OutputSourceConflictException>(() => new SearchEngine().SearchAsync([link], inPlace),
            "search rejects replacing the file that an input link points to");
        File.Delete(link);
        var output = Path.Combine(root, "replace-during-search.txt");
        await ThrowsAsync<OutputSourceConflictException>(() => new SearchEngine().SearchAsync([a],
            inPlace with { OutputPath = output }, _ =>
            {
                File.CreateSymbolicLink(output, a);
                return ValueTask.CompletedTask;
            }), "search rechecks the destination before publishing results");
        var afterLateConflict = await File.ReadAllBytesAsync(a);
        True(original.SequenceEqual(afterLateConflict) &&
             new FileInfo(output).LinkTarget == a, "late output conflict leaves the input and link unchanged");
        True(!Directory.EnumerateFiles(root, "replace-during-search.txt.*.tmp").Any(),
            "search removes staged output after a late conflict");
        File.Delete(output);
    }

    JobProgress? finalSearchProgress = null;
    await new SearchEngine().SearchAsync([a], new SearchOptions { Patterns = ["Alpha"] },
        progress: new SynchronousProgress<JobProgress>(value => finalSearchProgress = value));
    Equal(new FileInfo(a).Length, finalSearchProgress?.ComputedBytes ?? -1,
        "search progress reports independently computed bytes");

    var cappedMatches = new List<SearchMatch>();
    var capped = await new SearchEngine().SearchAsync([a, b], new SearchOptions
    {
        Patterns = ["a"],
        IgnoreCase = true,
        DegreeOfParallelism = 8,
        MaxResults = 2
    }, batch => { cappedMatches.AddRange(batch); return ValueTask.CompletedTask; });
    Equal(2L, capped.MatchCount, "parallel maximum result count");
    Equal(2, cappedMatches.Count, "parallel maximum callback count");

    await ThrowsAsync<ArgumentOutOfRangeException>(() => new SearchEngine().SearchAsync([a], new SearchOptions
    {
        Patterns = ["Alpha"],
        MaxConcurrentReadsPerVolume = 0
    }), "per-volume read limit validation");

    await ThrowsAsync<ArgumentOutOfRangeException>(() => new SearchEngine().SearchAsync([a], new SearchOptions
    {
        Patterns = ["Alpha"],
        ReadBufferSize = 1024
    }), "read buffer validation");

    await ThrowsAsync<ArgumentOutOfRangeException>(() => new SearchEngine().SearchAsync([a], new SearchOptions
    {
        Patterns = ["Alpha"],
        ReadStallTimeout = TimeSpan.Zero
    }), "read stall timeout validation");

    var missing = Path.Combine(root, "missing-file.txt");
    var skippedFiles = new List<string>();
    var skippedReasons = new List<string>();
    var missingSummary = await new SearchEngine().SearchAsync([missing], new SearchOptions
    {
        Patterns = ["needle"],
        FileSkipped = (file, reason) => { skippedFiles.Add(file); skippedReasons.Add(reason); }
    });
    Equal(1L, missingSummary.FilesScanned, "missing file on an available volume is skipped");
    Equal(1, missingSummary.Errors.Count, "skipped missing file is recorded as an error");
    EqualLines([Path.GetFullPath(missing)], skippedFiles.ToArray(), "missing file invokes skip reporting");
    True(skippedReasons.Single().Contains("已跳过", StringComparison.Ordinal), "missing file skip reason is recorded");

    var empty = Path.Combine(root, "empty.txt");
    await File.WriteAllBytesAsync(empty, []);
    var emptyCompleted = new List<string>();
    var emptySummary = await new SearchEngine().SearchAsync([empty], new SearchOptions
    {
        Patterns = ["needle"],
        FileCompleted = file => { emptyCompleted.Add(file); return ValueTask.CompletedTask; }
    });
    Equal(1L, emptySummary.FilesScanned, "empty file is skipped without pausing");
    Equal(1, emptySummary.Errors.Count, "empty file skip is recorded");
    EqualLines([Path.GetFullPath(empty)], emptyCompleted.ToArray(), "empty file is marked completed");

    var unreadableSource = Path.Combine(root, "directory-instead-of-file");
    Directory.CreateDirectory(unreadableSource);
    var unreadableSummary = await new SearchEngine().SearchAsync([unreadableSource], new SearchOptions
    {
        Patterns = ["needle"]
    });
    Equal(1L, unreadableSummary.FilesScanned, "unreadable source on an available volume is skipped");
    Equal(1, unreadableSummary.Errors.Count, "unreadable source skip is recorded");

    if (!OperatingSystem.IsWindows())
    {
        var permissionDenied = Path.Combine(root, "permission-denied.txt");
        await File.WriteAllTextAsync(permissionDenied, "needle\n", new UTF8Encoding(false));
        File.SetUnixFileMode(permissionDenied, UnixFileMode.None);
        try
        {
            var permissionSummary = await new SearchEngine().SearchAsync([permissionDenied], new SearchOptions
            {
                Patterns = ["needle"]
            });
            Equal(1L, permissionSummary.FilesScanned, "permission-denied file is skipped without pausing");
            Equal(1, permissionSummary.Errors.Count, "permission-denied file skip is recorded");
        }
        finally
        {
            File.SetUnixFileMode(permissionDenied, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    var missingVolumeRoot = OperatingSystem.IsWindows()
        ? Enumerable.Range('D', 23).Select(value => $"{(char)value}:\\")
            .First(path => !Directory.Exists(path))
        : OperatingSystem.IsMacOS()
            ? Path.Combine("/Volumes", "fastgrep-missing-" + Guid.NewGuid().ToString("N"))
            : Path.Combine("/mnt", "fastgrep-missing-" + Guid.NewGuid().ToString("N"));
    var missingVolumeFile = Path.Combine(missingVolumeRoot, "input.txt");
    SearchIoInterruptedException? interruption = null;
    try
    {
        await new SearchEngine().SearchAsync([missingVolumeFile], new SearchOptions { Patterns = ["needle"] });
    }
    catch (SearchIoInterruptedException ex) { interruption = ex; }
    True(interruption is not null, "unavailable volume pauses search as an I/O interruption");
    Equal(Path.GetFullPath(missingVolumeFile), interruption!.FilePath, "I/O interruption reports failed file");
    Equal(0L, interruption.ConfirmedOffset, "I/O interruption reports confirmed byte offset");

    var resumeFile = Path.Combine(root, "resume-offset.txt");
    var resumePrefix = "before needle\n" + new string('x', 5000) + "\n";
    await File.WriteAllTextAsync(resumeFile, resumePrefix + "after needle\n", new UTF8Encoding(false));
    long reportedOffset = -1, processedOffset = -1;
    var completedFiles = new List<string>();
    var resumed = await new SearchEngine().SearchAsync([resumeFile], new SearchOptions
    {
        Patterns = ["needle"], IgnoreCase = false, ReadBufferSize = 4096,
        StartOffset = Encoding.UTF8.GetByteCount(resumePrefix),
        ReadOffsetChanged = (_, offset) => reportedOffset = offset,
        ProcessedOffsetChanged = (_, offset) => processedOffset = offset,
        FileCompleted = file => { completedFiles.Add(file); return ValueTask.CompletedTask; }
    });
    Equal(1L, resumed.MatchCount, "single-file resume starts from configured byte offset");
    Equal(new FileInfo(resumeFile).Length, reportedOffset, "single-file resume records confirmed byte offset");
    Equal(new FileInfo(resumeFile).Length, processedOffset,
        "single-file resume records successfully processed byte offset");
    EqualLines([resumeFile], completedFiles.ToArray(), "file completion callback runs after computation");

    var multiResumeA = Path.Combine(root, "multi-resume-a.txt");
    var multiResumeB = Path.Combine(root, "multi-resume-b.txt");
    var multiPrefixA = "old needle A\n" + new string('a', 5000) + "\n";
    var multiPrefixB = "old needle B\n" + new string('b', 5000) + "\n";
    await File.WriteAllTextAsync(multiResumeA, multiPrefixA + "new needle A\n", new UTF8Encoding(false));
    await File.WriteAllTextAsync(multiResumeB, multiPrefixB + "new needle B\n", new UTF8Encoding(false));
    var multiProcessed = new Dictionary<string, long>(pathComparerForTests);
    var multiResume = await new SearchEngine().SearchAsync([multiResumeA, multiResumeB], new SearchOptions
    {
        Patterns = ["needle"], ReadBufferSize = 4096,
        StartOffsets = new Dictionary<string, long>(pathComparerForTests)
        {
            [Path.GetFullPath(multiResumeA)] = Encoding.UTF8.GetByteCount(multiPrefixA),
            [Path.GetFullPath(multiResumeB)] = Encoding.UTF8.GetByteCount(multiPrefixB)
        },
        ProcessedOffsetChanged = (file, offset) => multiProcessed[file] = offset
    });
    Equal(2L, multiResume.MatchCount, "multi-file resume uses each file's processed byte offset");
    Equal(new FileInfo(multiResumeA).Length, multiProcessed[Path.GetFullPath(multiResumeA)],
        "first multi-file checkpoint reaches the processed end");
    Equal(new FileInfo(multiResumeB).Length, multiProcessed[Path.GetFullPath(multiResumeB)],
        "second multi-file checkpoint reaches the processed end");

    var cancellationFile = Path.Combine(root, "cancellation-sparse.txt");
    await using (var cancellationSource = new FileStream(cancellationFile, FileMode.Create, FileAccess.Write,
                     FileShare.Read, 4096, FileOptions.Asynchronous))
        cancellationSource.SetLength(1024L * 1024 * 1024);
    var cancellationIo = new SearchIoController();
    using var cancellationCts = new CancellationTokenSource();
    var cancellationSearch = new SearchEngine().SearchAsync([cancellationFile], new SearchOptions
    {
        Patterns = ["not-present"], ReadBufferSize = 4096, DegreeOfParallelism = 1,
        ReadDegreeOfParallelism = 1, IoController = cancellationIo
    }, cancellationToken: cancellationCts.Token);
    while (cancellationIo.ActiveStreamCount == 0 && !cancellationSearch.IsCompleted) await Task.Yield();
    True(!cancellationSearch.IsCompleted, "cancellation test reaches an active search");
    cancellationCts.Cancel();
    cancellationIo.AbortPendingReads();
    await ThrowsAsync<OperationCanceledException>(() => cancellationSearch.WaitAsync(TimeSpan.FromSeconds(3)),
        "cancelled schedulers exit without spinning");

    var regex = await new SearchEngine().SearchAsync([a], new SearchOptions
    { Patterns = ["^A.*a$"], UseRegex = true, IgnoreCase = false }, _ => ValueTask.CompletedTask);
    Equal(1L, regex.MatchCount, "regex search");

    var gb18030File = Path.Combine(root, "gb18030.txt");
    var cp1251File = Path.Combine(root, "cp1251.txt");
    var mixedBytesFile = Path.Combine(root, "mixed-bytes.txt");
    await File.WriteAllBytesAsync(gb18030File, Encodings.Get("gb18030").GetBytes("中文密码\r\n"));
    await File.WriteAllBytesAsync(cp1251File, Encodings.Get("windows-1251").GetBytes("пароль\r\n"));
    await File.WriteAllBytesAsync(mixedBytesFile, [0xff, 0x80, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e', (byte)'\n']);
    var mixedEncodingSearch = await new SearchEngine().SearchAsync(
        [gb18030File, cp1251File, mixedBytesFile],
        new SearchOptions { Patterns = ["中文密码", "пароль", "needle"], IgnoreCase = false });
    Equal(3L, mixedEncodingSearch.MatchCount, "mixed encoding byte search");
    Equal(0, mixedEncodingSearch.Errors.Count, "mixed encoding files do not fail decoding");

    var utf16CjkFile = Path.Combine(root, "utf16-cjk.txt");
    await File.WriteAllBytesAsync(utf16CjkFile, Encoding.Unicode.GetBytes("中文密码\r\n上海\n"));
    var utf16Matches = new List<SearchMatch>();
    var utf16Search = await new SearchEngine().SearchAsync([utf16CjkFile], new SearchOptions
    {
        Patterns = ["中文密码", "上海"],
        IgnoreCase = false,
        ProcessingMode = SearchProcessingMode.SingleLine,
        ReadBufferSize = 4096
    }, batch => { utf16Matches.AddRange(batch); return ValueTask.CompletedTask; });
    Equal(2L, utf16Search.MatchCount, "UTF-16 CJK search without BOM");
    True(utf16Matches.Any(x => x.Text == "中文密码") && utf16Matches.Any(x => x.Text == "上海"),
        "UTF-16 matched lines decode correctly");

    var boundaryFile = Path.Combine(root, "mode-boundary.txt");
    await File.WriteAllTextAsync(boundaryFile, new string('x', 4093) + "needle" + new string('y', 500) + "\nlast\n");
    foreach (var mode in Enum.GetValues<SearchProcessingMode>())
    {
        var boundarySearch = await new SearchEngine().SearchAsync([boundaryFile], new SearchOptions
        {
            Patterns = ["needle"],
            IgnoreCase = false,
            ProcessingMode = mode,
            ReadBufferSize = 4096,
            CharacterBlockBoundaryTolerance = 1024,
            LineBlockLines = 2
        });
        Equal(1L, boundarySearch.MatchCount, $"{mode} preserves keyword at a natural block boundary");
    }

    var longLineFile = Path.Combine(root, "long-line.txt");
    await File.WriteAllTextAsync(longLineFile, new string('x', 128 * 1024 - 3) + "needle" + "\n");
    foreach (var mode in Enum.GetValues<SearchProcessingMode>())
    {
        var longLineSearch = await new SearchEngine().SearchAsync([longLineFile], new SearchOptions
        {
            Patterns = ["needle"], IgnoreCase = false, ProcessingMode = mode, ReadBufferSize = 4096,
            LineBlockLines = 32
        });
        Equal(1L, longLineSearch.MatchCount, $"{mode} does not truncate an oversized line");
        Equal(1L, longLineSearch.LinesScanned, $"{mode} keeps oversized line count");
    }

    var forcedCharacterFile = Path.Combine(root, "forced-character-boundary.txt");
    var oversizedLine = new string('x', 4093) + "needle" + new string('y', 4096);
    await File.WriteAllTextAsync(forcedCharacterFile, oversizedLine);
    var oversizedMatches = new List<SearchMatch>();
    var forcedCharacterSearch = await new SearchEngine().SearchAsync([forcedCharacterFile], new SearchOptions
    {
        Patterns = ["needle"], IgnoreCase = false, ProcessingMode = SearchProcessingMode.CharacterBlock,
        ReadBufferSize = 4096, CharacterBlockBoundaryTolerance = 1024
    }, batch => { oversizedMatches.AddRange(batch); return ValueTask.CompletedTask; });
    Equal(1L, forcedCharacterSearch.MatchCount, "character mode finds a keyword crossing its target boundary");
    Equal(1L, forcedCharacterSearch.LinesScanned, "character mode counts an oversized unterminated line once");
    Equal(oversizedLine, oversizedMatches.Single().Text, "character mode returns the complete oversized line");
    foreach (var encoding in new[] { Encoding.Unicode, Encoding.BigEndianUnicode })
    {
        var wideLine = new string('\u0a42', 2046) + "needle" + new string('z', 5000);
        var wideFile = Path.Combine(root, "wide-oversized.txt");
        await File.WriteAllTextAsync(wideFile, wideLine + "\r\nsecond needle", encoding);
        var wideMatches = new List<SearchMatch>();
        await new SearchEngine().SearchAsync([wideFile], new SearchOptions
        {
            Patterns = ["needle"], IgnoreCase = false, ProcessingMode = SearchProcessingMode.CharacterBlock,
            ReadBufferSize = 4096, CharacterBlockBoundaryTolerance = 1024
        }, batch => { wideMatches.AddRange(batch); return ValueTask.CompletedTask; });
        EqualLines([wideLine, "second needle"], wideMatches.OrderBy(x => x.LineNumber).Select(x => x.Text).ToArray(),
            $"character mode preserves oversized {encoding.WebName} lines");
        True(wideMatches.Select(x => x.LineNumber).Order().SequenceEqual([1L, 2L]),
            $"character mode keeps line numbers after oversized {encoding.WebName} lines");
    }

    var manyPatterns = Enumerable.Range(0, 2000).Select(i => $"key-{i:D4}").Append("target-key").ToArray();
    var manyPatternSearch = await new SearchEngine().SearchAsync([b], new SearchOptions
    {
        Patterns = manyPatterns, IgnoreCase = false, ProcessingMode = SearchProcessingMode.LineBlock,
        ReadBufferSize = 4096, LineBlockLines = 2
    });
    Equal(0L, manyPatternSearch.MatchCount, "line-block automaton handles a large absent keyword set");
    var manyPatternHit = await new SearchEngine().SearchAsync([a], new SearchOptions
    {
        Patterns = manyPatterns.Append("中文苹果").ToArray(), IgnoreCase = false,
        ProcessingMode = SearchProcessingMode.LineBlock, ReadBufferSize = 4096, LineBlockLines = 2
    });
    Equal(1L, manyPatternHit.MatchCount, "line-block automaton finds a keyword from a large set");

    var dynamicRoot = Path.Combine(root, "dynamic-search");
    Directory.CreateDirectory(dynamicRoot);
    var dynamicContent = string.Join('\n', Enumerable.Repeat(new string('x', 1024), 512));
    var dynamicFiles = Enumerable.Range(0, 24).Select(index => Path.Combine(dynamicRoot, $"{index}.txt")).ToArray();
    foreach (var file in dynamicFiles) await File.WriteAllTextAsync(file, dynamicContent);
    var dynamicConcurrency = new SearchConcurrencyController(1, 1);
    var peakReaders = 0;
    var peakComputations = 0;
    var peakQueuedBlocks = 0;
    dynamicConcurrency.StateChanged += state =>
    {
        InterlockedExtensions.Max(ref peakReaders, state.ActiveReaders);
        InterlockedExtensions.Max(ref peakComputations, state.ActiveComputations);
        InterlockedExtensions.Max(ref peakQueuedBlocks, state.QueuedBlocks);
    };
    var dynamicSearch = new SearchEngine().SearchAsync(dynamicFiles, new SearchOptions
    {
        Patterns = ["not-present"],
        ReadDegreeOfParallelism = 1,
        DegreeOfParallelism = 1,
        MaxConcurrentReadsPerVolume = 6,
        ReadBufferSize = 4 * 1024,
        ConcurrencyController = dynamicConcurrency
    });
    while (!dynamicSearch.IsCompleted && dynamicConcurrency.State.ActiveReaders == 0) await Task.Yield();
    Equal(4, dynamicConcurrency.SetReadLimit(4), "live read limit adjustment");
    Equal(4, dynamicConcurrency.SetComputeLimit(4), "live compute limit adjustment");
    await dynamicSearch;
    True(peakReaders >= 2, "live read scheduler expands active readers");
    True(peakComputations >= 2, "live compute scheduler expands active computations");
    True(peakQueuedBlocks > 0, "search reports queued blocks");
    Equal(0, dynamicConcurrency.State.QueuedBlocks, "queued block count drains to zero");

    var cleaned = Path.Combine(root, "cleaned.txt");
    var removedLines = new List<string>();
    var clean = await new CleanEngine().CleanAsync([a], cleaned, new CleanOptions
    { RemovePrefix = "alpha", IgnoreCase = true, RemoveContaining = "apple", Trim = true, RemoveBlankLines = true },
        removedLine: line => { removedLines.Add(line); return ValueTask.CompletedTask; });
    Equal(3L, clean.OutputLines, "clean output line count");
    EqualLines(["中文苹果", "DUP", "dup"], await File.ReadAllLinesAsync(cleaned), "clean content and order");
    EqualLines(["Alpha", "beta apple"], removedLines.ToArray(), "removed clean lines callback");

    var groupedDirectory = Path.Combine(root, "grouped-clean");
    var grouped = await new CleanEngine().CleanToGroupsAsync([a], groupedDirectory,
        new CleanOptions { Trim = true }, groupStartIndex: 0, prefix: "group-", maxOpenFiles: 1);
    Equal(5L, grouped.OutputLines, "group clean count");
    EqualLines(["Alpha"], await File.ReadAllLinesAsync(Path.Combine(groupedDirectory, "group-AL.txt")), "group clean key");

    var separateCase = Path.Combine(root, "separate-case.txt");
    var separateCaseSummary = await new CleanEngine().CleanAsync([a], separateCase, new CleanOptions
    { RemovePrefix = "alpha", RemovePrefixIgnoreCase = false, RemoveContaining = "APPLE", RemoveContainingIgnoreCase = true });
    Equal(4L, separateCaseSummary.OutputLines, "independent clean case options");
    True((await File.ReadAllLinesAsync(separateCase)).Contains("Alpha"), "case-sensitive prefix is preserved");

    var mailSource = Path.Combine(root, "mail.txt");
    await File.WriteAllTextAsync(mailSource, "user@example.com:pwd\nnot-an-email\n");
    var mailGroups = Path.Combine(root, "mail-groups");
    await new CleanEngine().CleanToGroupsAsync([mailSource], mailGroups,
        new CleanOptions { GroupNonEmailSeparately = true }, prefix: "file_");
    True(File.Exists(Path.Combine(mailGroups, "file_@nomail.txt")), "non-email group compatibility");

    var stable = Path.Combine(root, "stable.txt");
    var stableSummary = await new DedupEngine().DeduplicateAsync([a], stable, new DedupOptions
    { Mode = DedupMode.StableInMemory, IgnoreCase = true });
    Equal(4L, stableSummary.OutputLines, "stable dedup count");
    EqualLines(["Alpha", "beta apple", "中文苹果", "DUP"], await File.ReadAllLinesAsync(stable), "stable dedup order");

    var sorted = Path.Combine(root, "sorted.txt");
    var sortedSummary = await new DedupEngine().DeduplicateAsync([a, b], sorted, new DedupOptions
    { Mode = DedupMode.SortedExternal, IgnoreCase = false, MemoryBudgetBytes = 1024 });
    Equal(8L, sortedSummary.OutputLines, "external dedup count");
    var sortedLines = await File.ReadAllLinesAsync(sorted);
    EqualLines(sortedLines.Order(StringComparer.Ordinal).ToArray(), sortedLines, "external dedup sorting");
    var manyRunSource = Path.Combine(root, "dedup-many-runs.txt");
    var uniqueRunLines = Enumerable.Range(0, 40).Select(i => $"{i:D3}" + new string('X', 240)).ToArray();
    await File.WriteAllLinesAsync(manyRunSource, uniqueRunLines.Concat(uniqueRunLines.Reverse().Select(x => x.ToLowerInvariant())));
    var manyRuns = await new DedupEngine().DeduplicateAsync([manyRunSource], sorted, new DedupOptions
    { Mode = DedupMode.SortedExternal, IgnoreCase = true, MemoryBudgetBytes = 1024 });
    True(manyRuns.RunFiles > 32, "external dedup creates multiple merge passes with a small budget");
    Equal(40L, manyRuns.DuplicateLines, "external dedup removes duplicates across merge passes");
    EqualLines(uniqueRunLines, await File.ReadAllLinesAsync(sorted), "external dedup keeps the first spelling across runs");

    var splitDir = Path.Combine(root, "split");
    var parts = await new MergeSplitEngine().SplitAsync(b, new SplitOptions { OutputDirectory = splitDir, LinesPerFile = 2 });
    Equal(2, parts.Count, "split part count");
    var merged = Path.Combine(root, "merged.txt");
    await new MergeSplitEngine().MergeAsync(parts, merged, ensureNewlineBetweenFiles: false);
    EqualLines(await File.ReadAllLinesAsync(b), await File.ReadAllLinesAsync(merged), "split and merge roundtrip");
    var unterminated = Path.Combine(root, "unterminated.txt");
    var terminated = Path.Combine(root, "terminated.txt");
    var emptyForMerge = Path.Combine(root, "merge-empty.txt");
    await File.WriteAllTextAsync(unterminated, "first");
    await File.WriteAllTextAsync(terminated, "second\n");
    await File.WriteAllTextAsync(emptyForMerge, "");
    await new MergeSplitEngine().MergeAsync([unterminated, emptyForMerge, terminated], merged);
    Equal("first" + Environment.NewLine + "second\n", await File.ReadAllTextAsync(merged),
        "merge inserts a newline between nonempty files only when needed");
    await new MergeSplitEngine().MergeAsync([terminated, unterminated], merged);
    Equal("second\nfirst", await File.ReadAllTextAsync(merged), "merge does not duplicate an existing newline");

    var groupMergeSource = Path.Combine(root, "group-merge-source");
    var groupMergeTarget = Path.Combine(root, "group-merge-target");
    Directory.CreateDirectory(groupMergeSource);
    Directory.CreateDirectory(groupMergeTarget);
    await File.WriteAllTextAsync(Path.Combine(groupMergeSource, "incoming-one.txt"), "new\n");
    await File.WriteAllTextAsync(Path.Combine(groupMergeTarget, "one.txt"), "old\n");
    var changed = await new MergeSplitEngine().GroupMergeAsync(groupMergeSource, groupMergeTarget,
        sourcePrefixToRemove: "incoming-");
    Equal(1, changed.Count, "group merge target count");
    EqualLines(["old", "new"], await File.ReadAllLinesAsync(Path.Combine(groupMergeTarget, "one.txt")), "group merge appends by normalized key");

    var found = FileOperations.Discover(new FileDiscoveryOptions { Root = root, Includes = ["*.txt"], Recursive = false });
    True(found.Contains(a), "file discovery include");
    True(!found.Contains(b), "file discovery excludes nonmatching extension");
    var extensionless = Path.Combine(root, "LICENSE");
    await File.WriteAllTextAsync(extensionless, "license");
    var withExtensionless = FileOperations.Discover(new FileDiscoveryOptions
    { Root = root, Includes = ["*.txt"], Recursive = false, IncludeExtensionless = true });
    True(withExtensionless.Contains(extensionless), "file discovery explicitly includes extensionless files");
    var withoutExtensionless = FileOperations.Discover(new FileDiscoveryOptions
    { Root = root, Includes = ["*"], Recursive = false, IncludeExtensionless = false });
    True(!withoutExtensionless.Contains(extensionless), "file discovery explicitly excludes extensionless files");

    var copyDir = Path.Combine(root, "copy");
    await FileOperations.CopyOrMoveAsync([a], root, copyDir, move: false, overwrite: false);
    Equal(await File.ReadAllTextAsync(a), await File.ReadAllTextAsync(Path.Combine(copyDir, "a.txt")), "copy preserves content");

    var tail = await new TextReaderService().ReadTailAsync(b, 8);
    True(tail.Contains("omega"), "tail reader");

    Console.WriteLine("All FastGrep integration tests passed.");
    return 0;
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

static void True(bool value, string name)
{
    if (!value) throw new InvalidOperationException($"FAILED: {name}");
    Console.WriteLine($"PASS: {name}");
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"FAILED: {name}; expected={expected}, actual={actual}");
    Console.WriteLine($"PASS: {name}");
}

static void EqualLines(string[] expected, string[] actual, string name)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        throw new InvalidOperationException($"FAILED: {name}; expected=[{string.Join(',', expected)}], actual=[{string.Join(',', actual)}]");
    Console.WriteLine($"PASS: {name}");
}

static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Console.WriteLine($"PASS: {name}"); return; }
    throw new InvalidOperationException($"FAILED: {name}; expected {typeof(T).Name}");
}

static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}

sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
