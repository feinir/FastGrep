using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace FastGrep.Core;

public sealed class SearchEngine
{
    public static int RecommendReadConcurrency(IEnumerable<string> files)
    {
        var roots = GetDriveDescriptors();
        foreach (var file in files)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(file); }
            catch { continue; }
            var drive = roots.FirstOrDefault(item => IsWithinRoot(fullPath, item.Root));
            if (drive.Type == DriveType.Network) return 4;
        }
        return 6;
    }

    public async Task<SearchSummary> SearchAsync(IEnumerable<string> sourceFiles, SearchOptions options,
        Func<IReadOnlyList<SearchMatch>, ValueTask>? onMatches = null,
        IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        var files = sourceFiles.Select(Path.GetFullPath).Distinct(PathComparer.Instance).ToArray();
        var outputPath = string.IsNullOrWhiteSpace(options.OutputPath) ? null :
            FileOperations.EnsureSeparateOutput(files, options.OutputPath);
        if (options.MaxConcurrentReadsPerVolume < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentReadsPerVolume));
        if (options.ReadBufferSize < 4096)
            throw new ArgumentOutOfRangeException(nameof(options.ReadBufferSize));
        if (options.LineBlockLines < 1)
            throw new ArgumentOutOfRangeException(nameof(options.LineBlockLines));
        if (options.CharacterBlockBoundaryTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(options.CharacterBlockBoundaryTolerance));
        if (options.StartOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(options.StartOffset));
        if (options.StartOffsets?.Values.Any(offset => offset < 0) == true)
            throw new ArgumentOutOfRangeException(nameof(options.StartOffsets));
        if (options.ReadStallTimeout <= TimeSpan.Zero && options.ReadStallTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options.ReadStallTimeout));
        var textMatcher = options.UseRegex ? PatternMatcher.Create(options) : null;
        var byteMatcher = options.UseRegex ? null : new BytePatternMatcher(options.Patterns, options.IgnoreCase,
            options.ProcessingMode == SearchProcessingMode.LineBlock);
        var errors = new ConcurrentQueue<string>();
        var volumeDescriptors = GetDriveDescriptors();
        var filesWithMatch = options.FirstMatchPerFile
            ? new ConcurrentDictionary<string, byte>(PathComparer.Instance) : null;
        using var volumeReads = new VolumeReadLimiter(options.MaxConcurrentReadsPerVolume);
        files = volumeReads.InterleaveByVolume(files);
        var concurrency = options.ConcurrencyController ?? new SearchConcurrencyController(
            options.ReadDegreeOfParallelism ?? options.DegreeOfParallelism,
            options.DegreeOfParallelism);
        long StartOffsetFor(string file)
        {
            if (options.StartOffsets is not null && options.StartOffsets.TryGetValue(file, out var offset))
                return Math.Max(0, offset);
            return files.Length == 1 ? options.StartOffset : 0;
        }
        var totalBytes = GetTotalBytes(files, StartOffsetFor);
        long filesScanned = 0, bytesScanned = 0, bytesComputed = 0, linesScanned = 0, matchCount = 0;
        var stopwatch = Stopwatch.StartNew();
        string? currentFile = null;
        using var progressTimer = progress is null ? null : new Timer(_ =>
        {
            progress.Report(new JobProgress("search", Volatile.Read(ref currentFile),
                Interlocked.Read(ref bytesScanned), totalBytes, Interlocked.Read(ref linesScanned),
                ComputedBytes: Interlocked.Read(ref bytesComputed)));
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        var matches = Channel.CreateBounded<SearchMatch>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var blocks = Channel.CreateBounded<LineBlock>(new BoundedChannelOptions(
            Math.Clamp(concurrency.ComputeLimit * 2, 16, 128))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = pipelineCts.Token;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        SearchIoInterruptedException? ioInterruption = null;
        var ioController = options.IoController ?? new SearchIoController();
        var confirmedOffsets = new ConcurrentDictionary<string, long>(PathComparer.Instance);
        long lastReadProgress = Stopwatch.GetTimestamp();
        var fileStates = new ConcurrentDictionary<string, FilePipelineState>(PathComparer.Instance);
        var consumer = ConsumeMatchesAsync(matches.Reader, options, files, outputPath, onMatches, token).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) pipelineCts.Cancel();
            return task;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        var producer = ProduceAsync().ContinueWith(task =>
        {
            var failure = task.Exception?.GetBaseException();
            blocks.Writer.TryComplete(failure);
            if (!task.IsCompletedSuccessfully && failure is not SearchIoInterruptedException) pipelineCts.Cancel();
            return task;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();

        async Task ProduceAsync()
        {
            try
            {
                await RunReadSchedulerAsync(files, concurrency, async (file, readToken) =>
                {
                    var confirmedOffset = 0L;
                    var startOffset = StartOffsetFor(file);
                    var fileState = fileStates.GetOrAdd(file, path => new FilePipelineState(path, startOffset,
                        options.ProcessedOffsetChanged, options.FileCompleted));
                    try
                    {
                        using var volumeLease = await volumeReads.AcquireAsync(file, readToken);
                        Volatile.Write(ref currentFile, file);
                        var info = new FileInfo(file);
                        if (!info.Exists) throw new FileNotFoundException("File not found.", file);
                        if (info.Length == 0)
                        {
                            await SkipFileAsync("空文件，已跳过。", countAsScanned: true);
                            return;
                        }
                        confirmedOffset = Math.Min(startOffset, info.Length);
                        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                            options.ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        ioController.Register(file, stream);
                        confirmedOffsets[file] = confirmedOffset;
                        using var stalledReadCts = CancellationTokenSource.CreateLinkedTokenSource(readToken);
                        if (confirmedOffset > 0) stream.Seek(confirmedOffset, SeekOrigin.Begin);
                        options.ReadOffsetChanged?.Invoke(file, confirmedOffset);
                        var initialCapacity = options.ProcessingMode == SearchProcessingMode.CharacterBlock
                            ? checked(options.ReadBufferSize + options.CharacterBlockBoundaryTolerance)
                            : options.ReadBufferSize;
                        var buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
                        var buffered = 0;
                        var characterBoundaryScanStart = 0;
                        var bufferStartOffset = confirmedOffset;
                        long firstLineNumber = 1;
                        LineFraming? framing = null;
                        try
                        {
                            while (true)
                            {
                                var emitLength = framing is null ? 0 : GetEmitLength(buffer.AsSpan(0, buffered), framing,
                                    options, endOfFile: false, ref characterBoundaryScanStart);
                                if (emitLength > 0)
                                {
                                    await EmitAsync(emitLength, countTrailingLine: false);
                                    continue;
                                }
                                if (buffered == buffer.Length)
                                {
                                    var expanded = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                                    buffer.AsSpan(0, buffered).CopyTo(expanded);
                                    ArrayPool<byte>.Shared.Return(buffer);
                                    buffer = expanded;
                                }

                                var available = buffer.Length - buffered;
                                var readLength = Math.Min(options.ReadBufferSize, available);
                                if (options.ProcessingMode == SearchProcessingMode.CharacterBlock)
                                {
                                    var required = checked(options.ReadBufferSize + options.CharacterBlockBoundaryTolerance);
                                    if (buffered < required) readLength = Math.Min(readLength, required - buffered);
                                }
                                if (options.ReadStallTimeout != Timeout.InfiniteTimeSpan)
                                    stalledReadCts.CancelAfter(options.ReadStallTimeout);
                                int read;
                                try
                                {
                                    read = await stream.ReadAsync(buffer.AsMemory(buffered, readLength), stalledReadCts.Token);
                                }
                                catch (OperationCanceledException ex) when (!readToken.IsCancellationRequested &&
                                                                           stalledReadCts.IsCancellationRequested)
                                {
                                    throw new IOException(
                                        $"连续 {options.ReadStallTimeout.TotalSeconds:N0} 秒没有读取到新数据，已触发安全恢复。", ex);
                                }
                                finally
                                {
                                    if (!stalledReadCts.IsCancellationRequested)
                                        stalledReadCts.CancelAfter(Timeout.InfiniteTimeSpan);
                                }
                                if (read == 0) break;
                                buffered += read;
                                confirmedOffset += read;
                                confirmedOffsets[file] = confirmedOffset;
                                Interlocked.Exchange(ref lastReadProgress, Stopwatch.GetTimestamp());
                                options.ReadOffsetChanged?.Invoke(file, confirmedOffset);
                                Interlocked.Add(ref bytesScanned, read);
                                framing ??= LineFraming.Detect(buffer.AsSpan(0, buffered));
                            }
                            framing ??= LineFraming.SingleByte;
                            int finalEmit;
                            while ((finalEmit = GetEmitLength(buffer.AsSpan(0, buffered), framing, options,
                                       endOfFile: true, ref characterBoundaryScanStart)) > 0 && finalEmit < buffered)
                                await EmitAsync(finalEmit, countTrailingLine: false);
                            if (buffered > 0)
                            {
                                var owned = buffer;
                                buffer = null!;
                                var completeLines = framing.CountNewlines(owned.AsSpan(0, buffered));
                                var endOffset = bufferStartOffset + buffered;
                                var sequence = fileState.BlockQueued();
                                await QueueTrackedBlockAsync(
                                    new LineBlock(file, owned, buffered, firstLineNumber, framing, true, completeLines,
                                        sequence, endOffset));
                            }

                            async ValueTask EmitAsync(int length, bool countTrailingLine)
                            {
                                var owned = buffer;
                                var remaining = buffered - length;
                                var next = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, remaining));
                                owned.AsSpan(length, remaining).CopyTo(next);
                                buffer = next;
                                buffered = remaining;
                                characterBoundaryScanStart = 0;
                                var lineCount = framing!.CountNewlines(owned.AsSpan(0, length));
                                var blockLine = firstLineNumber;
                                firstLineNumber += lineCount;
                                var endOffset = bufferStartOffset + length;
                                bufferStartOffset = endOffset;
                                var sequence = fileState.BlockQueued();
                                await QueueTrackedBlockAsync(
                                    new LineBlock(file, owned, length, blockLine, framing, countTrailingLine, lineCount,
                                        sequence, endOffset));
                            }

                            async ValueTask QueueTrackedBlockAsync(LineBlock block)
                            {
                                try { await QueueBlockAsync(blocks.Writer, block, concurrency, readToken); }
                                catch
                                {
                                    fileState.BlockAbandoned();
                                    throw;
                                }
                            }
                        }
                        finally
                        {
                            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
                        }
                        Interlocked.Increment(ref filesScanned);
                        await fileState.ReaderFinishedAsync();
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    catch (OperationCanceledException) when (readCts.IsCancellationRequested &&
                                                              Volatile.Read(ref ioInterruption) is not null)
                    {
                        // Another reader reported an I/O failure. Keep this file and its last
                        // successfully processed checkpoint for the automatic recovery attempt.
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        await SkipFileAsync("没有读取权限，已跳过。", ex, countAsScanned: true);
                    }
                    catch (System.Security.SecurityException ex)
                    {
                        await SkipFileAsync("安全策略禁止读取，已跳过。", ex, countAsScanned: true);
                    }
                    catch (IOException ex) when (!ShouldPauseForIo(file, ex, volumeDescriptors))
                    {
                        await SkipFileAsync($"文件无法读取，已跳过：{ex.Message}", ex, countAsScanned: true);
                    }
                    catch (IOException ex)
                    {
                        var interruption = new SearchIoInterruptedException(file, confirmedOffset, ex);
                        if (Interlocked.CompareExchange(ref ioInterruption, interruption, null) is null)
                        {
                            readCts.Cancel();
                            ioController.AbortPendingReads();
                        }
                    }
                    catch (Exception ex) { errors.Enqueue($"{file}: {ex.Message}"); }
                    finally
                    {
                        // The stream may already have been disposed by the watchdog or a stop button.
                        ioController.Unregister(file);
                    }

                    async ValueTask SkipFileAsync(string reason, Exception? exception = null,
                        bool countAsScanned = false)
                    {
                        var detail = exception is null ? reason : $"{reason} ({exception.GetType().Name})";
                        errors.Enqueue($"{file}: {detail}");
                        options.FileSkipped?.Invoke(file, detail);
                        if (countAsScanned) Interlocked.Increment(ref filesScanned);
                        await fileState.ReaderFinishedAsync();
                    }
                }, readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                      Volatile.Read(ref ioInterruption) is not null)
            {
                // Converted below to the first concrete I/O interruption.
            }

            if (Volatile.Read(ref ioInterruption) is { } interruption) throw interruption;
        }

        var computer = RunComputeSchedulerAsync(blocks.Reader, concurrency, async (block, computeToken) =>
        {
            var processed = false;
            try
            {
                computeToken.ThrowIfCancellationRequested();
                var data = block.Buffer.AsMemory(0, block.Length);
                if (options.ProcessingMode == SearchProcessingMode.CharacterBlock && byteMatcher is not null &&
                    block.Length <= options.ReadBufferSize + options.CharacterBlockBoundaryTolerance)
                {
                    var blockHits = byteMatcher.FindAll(data.Span, block.Framing.IsWide);
                    var logicalLines = block.CompleteLineCount;
                    if (block.CountTrailingLine && !block.Framing.EndsWithNewline(data.Span)) logicalLines++;
                    Interlocked.Add(ref linesScanned, logicalLines);
                    if (blockHits.Count == 0)
                    {
                        processed = true;
                        return;
                    }

                    var hitIndex = 0;
                    var blockOffset = 0;
                    var blockLineNumber = block.FirstLineNumber;
                    while (blockOffset < data.Length && hitIndex < blockHits.Count)
                    {
                        computeToken.ThrowIfCancellationRequested();
                        var remaining = data.Span[blockOffset..];
                        var newline = block.Framing.IndexOfNewline(remaining);
                        var rawLength = newline < 0 ? remaining.Length : newline;
                        ReadOnlySpan<byte> line = block.Framing.TrimCarriageReturn(remaining[..rawLength]);
                        var lineEnd = blockOffset + line.Length;
                        while (hitIndex < blockHits.Count && blockHits[hitIndex].Offset < blockOffset) hitIndex++;
                        List<int>? ids = null;
                        var candidate = hitIndex;
                        while (candidate < blockHits.Count && blockHits[candidate].Offset < lineEnd)
                        {
                            var hit = blockHits[candidate++];
                            if (hit.Offset + hit.Length <= lineEnd && ids?.Contains(hit.PatternId) != true)
                                (ids ??= []).Add(hit.PatternId);
                        }
                        hitIndex = candidate;
                        if (ids is not null &&
                            (filesWithMatch is null || filesWithMatch.TryAdd(block.File, 0)) &&
                            TryReserveResult(ref matchCount, options.MaxResults))
                        {
                            ids.Sort();
                            await matches.Writer.WriteAsync(new SearchMatch(block.File, blockLineNumber, -1,
                                block.Framing.Decode(line), ids), computeToken);
                        }
                        if (newline >= 0) blockLineNumber++;
                        if (newline < 0) break;
                        blockOffset += newline + block.Framing.NewlineLength;
                    }
                    processed = true;
                    return;
                }
                var offset = 0;
                var lineNumber = block.FirstLineNumber;
                long blockLines = 0;
                while (offset < data.Length)
                {
                    if ((blockLines & 1023) == 0) computeToken.ThrowIfCancellationRequested();
                    var remaining = data.Span[offset..];
                    var newline = block.Framing.IndexOfNewline(remaining);
                    var length = newline < 0 ? remaining.Length : newline;
                    ReadOnlySpan<byte> line = remaining[..length];
                    line = block.Framing.TrimCarriageReturn(line);
                    var ids = byteMatcher is not null
                        ? byteMatcher.Find(line, block.Framing.IsWide)
                        : textMatcher!.Find(block.Framing.Decode(line));
                    if (ids.Count > 0 &&
                        (filesWithMatch is null || filesWithMatch.TryAdd(block.File, 0)) &&
                        TryReserveResult(ref matchCount, options.MaxResults))
                    {
                        var text = block.Framing.Decode(line);
                        await matches.Writer.WriteAsync(
                            new SearchMatch(block.File, lineNumber, -1, text, ids), computeToken);
                    }
                    if (newline >= 0 || block.CountTrailingLine) blockLines++;
                    if (newline >= 0) lineNumber++;
                    if (newline < 0) break;
                    offset += newline + block.Framing.NewlineLength;
                }
                Interlocked.Add(ref linesScanned, blockLines);
                processed = true;
            }
            finally
            {
                if (processed) Interlocked.Add(ref bytesComputed, block.Length);
                block.Dispose();
                if (fileStates.TryGetValue(block.File, out var fileState))
                {
                    if (processed) await fileState.BlockProcessedAsync(block.Sequence, block.EndOffset);
                    else fileState.BlockAbandoned();
                }
            }
        }, token).ContinueWith(task =>
        {
            var failure = task.Exception?.GetBaseException();
            matches.Writer.TryComplete(failure);
            if (!task.IsCompletedSuccessfully && failure is not SearchIoInterruptedException) pipelineCts.Cancel();
            return task;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();

        using var watchdogCts = new CancellationTokenSource();
        var watchdog = WatchForReadStallAsync(watchdogCts.Token);

        async Task WatchForReadStallAsync(CancellationToken watchdogToken)
        {
            if (options.ReadStallTimeout == Timeout.InfiniteTimeSpan) return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(watchdogToken))
                {
                    if (cancellationToken.IsCancellationRequested || readCts.IsCancellationRequested) return;
                    var inactiveTicks = Stopwatch.GetTimestamp() - Interlocked.Read(ref lastReadProgress);
                    if (inactiveTicks < options.ReadStallTimeout.TotalSeconds * Stopwatch.Frequency) continue;

                    var state = concurrency.State;
                    var readBytes = Interlocked.Read(ref bytesScanned);
                    var computedBytes = Interlocked.Read(ref bytesComputed);
                    if (state.ActiveReaders == 0 || state.QueuedBlocks != 0 || computedBytes < readBytes) continue;

                    var file = ioController.ActiveFiles.FirstOrDefault() ?? Volatile.Read(ref currentFile) ?? files[0];
                    confirmedOffsets.TryGetValue(file, out var offset);
                    var timeout = new TimeoutException(
                        $"读取总量连续 {options.ReadStallTimeout.TotalSeconds:N0} 秒没有变化，队列为空且计算已追平读取。");
                    var interruption = new SearchIoInterruptedException(file, offset, timeout);
                    if (Interlocked.CompareExchange(ref ioInterruption, interruption, null) is null)
                    {
                        readCts.Cancel();
                        ioController.AbortPendingReads();
                    }
                    return;
                }
            }
            catch (OperationCanceledException) when (watchdogToken.IsCancellationRequested) { }
        }

        var pipeline = Task.WhenAll(producer, computer, consumer);
        try
        {
            await pipeline.WaitAsync(cancellationToken);
        }
        catch
        {
            pipelineCts.Cancel();
            ioController.AbortPendingReads();
            try { await pipeline.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }
        finally
        {
            watchdogCts.Cancel();
            try { await watchdog; } catch { }
            if (pipeline.IsCompleted) DrainAbandonedBlocks();
            else _ = pipeline.ContinueWith(task =>
            {
                _ = task.Exception;
                DrainAbandonedBlocks();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        progressTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        progress?.Report(new JobProgress("search", Volatile.Read(ref currentFile), Interlocked.Read(ref bytesScanned),
            totalBytes, Interlocked.Read(ref linesScanned), ComputedBytes: Interlocked.Read(ref bytesComputed)));
        stopwatch.Stop();
        return new SearchSummary(filesScanned, bytesScanned, linesScanned, matchCount, stopwatch.Elapsed, errors.ToArray());

        void DrainAbandonedBlocks()
        {
            while (blocks.Reader.TryRead(out var abandoned))
            {
                concurrency.BlockDequeued();
                abandoned.Dispose();
                if (fileStates.TryGetValue(abandoned.File, out var fileState)) fileState.BlockAbandoned();
            }
        }
    }

    private static int GetEmitLength(ReadOnlySpan<byte> data, LineFraming framing, SearchOptions options,
        bool endOfFile, ref int characterBoundaryScanStart)
    {
        if (data.IsEmpty) return 0;
        if (options.ProcessingMode == SearchProcessingMode.LineBlock)
            return framing.FindEndOfNthLine(data, options.LineBlockLines);

        if (options.ProcessingMode == SearchProcessingMode.SingleLine)
        {
            if (!endOfFile && data.Length < options.ReadBufferSize) return 0;
            return framing.FindFirstLineEndAtOrAfter(data, options.ReadBufferSize);
        }

        var target = options.ReadBufferSize;
        var tolerance = options.CharacterBlockBoundaryTolerance;
        if (!endOfFile && data.Length < target + tolerance) return 0;
        if (data.Length <= target && endOfFile) return data.Length;
        if (characterBoundaryScanStart == 0)
        {
            var natural = framing.FindNearestLineEnd(data, target, tolerance);
            if (natural > 0) return natural;
            var preceding = framing.FindLastLineEnd(data[..Math.Min(target, data.Length)]);
            if (preceding > 0) return preceding;
        }
        // Keep a long line intact for literals, regexes, line numbers, and resume offsets.
        // Remember the scanned prefix so another read only searches newly available bytes.
        var following = framing.FindFirstLineEndAtOrAfter(data, Math.Max(target, characterBoundaryScanStart));
        if (following > 0) return following;
        characterBoundaryScanStart = framing.AlignCodeUnit(data.Length);
        return 0;
    }

    private sealed class LineFraming
    {
        public static readonly LineFraming SingleByte = new([(byte)'\n'], [(byte)'\r'], null);
        private static readonly LineFraming Utf16Le = new([(byte)'\n', 0], [(byte)'\r', 0], Encoding.Unicode);
        private static readonly LineFraming Utf16Be = new([0, (byte)'\n'], [0, (byte)'\r'], Encoding.BigEndianUnicode);
        private readonly byte[] _newline;
        private readonly byte[] _carriageReturn;
        private readonly Encoding? _encoding;

        private LineFraming(byte[] newline, byte[] carriageReturn, Encoding? encoding)
        {
            _newline = newline;
            _carriageReturn = carriageReturn;
            _encoding = encoding;
        }

        public int NewlineLength => _newline.Length;
        public bool IsWide => _encoding is not null;

        public static LineFraming Detect(ReadOnlySpan<byte> sample)
        {
            if (sample.Length >= 2 && sample[0] == 0xff && sample[1] == 0xfe) return Utf16Le;
            if (sample.Length >= 2 && sample[0] == 0xfe && sample[1] == 0xff) return Utf16Be;
            var le = 0;
            var be = 0;
            var zeroes = 0;
            for (var i = 0; i < sample.Length; i++)
            {
                if (sample[i] == 0) zeroes++;
                if (i + 1 >= sample.Length) continue;
                if (sample[i] == (byte)'\n' && sample[i + 1] == 0 && (i & 1) == 0) le++;
                if (sample[i] == 0 && sample[i + 1] == (byte)'\n' && (i & 1) == 0) be++;
            }
            var enoughZeroes = zeroes * 10 >= sample.Length;
            if (le > 0 && (enoughZeroes || le > be)) return Utf16Le;
            if (be > 0 && (enoughZeroes || be > le)) return Utf16Be;
            return SingleByte;
        }

        public int IndexOfNewline(ReadOnlySpan<byte> data)
        {
            if (_newline.Length == 1) return data.IndexOf(_newline[0]);
            for (var start = 0; start < data.Length;)
            {
                var found = data[start..].IndexOf(_newline);
                if (found < 0) return -1;
                if ((found & 1) == 0) return start + found;
                start += found + 1;
            }
            return -1;
        }

        public int CountNewlines(ReadOnlySpan<byte> data)
        {
            if (_newline.Length == 1) return data.Count(_newline[0]);
            var count = 0;
            var offset = 0;
            while (offset <= data.Length - _newline.Length)
            {
                var found = IndexOfNewline(data[offset..]);
                if (found < 0) break;
                count++;
                offset += found + _newline.Length;
            }
            return count;
        }

        public bool EndsWithNewline(ReadOnlySpan<byte> data) =>
            AlignCodeUnit(data.Length) == data.Length && data.EndsWith(_newline);

        public int FindEndOfNthLine(ReadOnlySpan<byte> data, int lineCount)
        {
            var offset = 0;
            for (var count = 0; count < lineCount; count++)
            {
                var found = IndexOfNewline(data[offset..]);
                if (found < 0) return 0;
                offset += found + _newline.Length;
            }
            return offset;
        }

        public int FindFirstLineEndAtOrAfter(ReadOnlySpan<byte> data, int target)
        {
            var start = Math.Max(0, Math.Min(target, data.Length) - _newline.Length);
            start = AlignCodeUnit(start);
            while (start <= data.Length - _newline.Length)
            {
                var found = IndexOfNewline(data[start..]);
                if (found < 0) return 0;
                var end = start + found + _newline.Length;
                if (end >= target) return end;
                start = end;
            }
            return 0;
        }

        public int FindNearestLineEnd(ReadOnlySpan<byte> data, int target, int tolerance)
        {
            var minimum = Math.Max(0, target - tolerance);
            var maximum = Math.Min(data.Length, target + tolerance);
            var best = 0;
            var bestDistance = int.MaxValue;
            var offset = AlignCodeUnit(Math.Max(0, minimum - _newline.Length));
            while (offset <= maximum - _newline.Length)
            {
                var found = IndexOfNewline(data[offset..maximum]);
                if (found < 0) break;
                var end = offset + found + _newline.Length;
                if (end >= minimum)
                {
                    var distance = Math.Abs(end - target);
                    if (distance < bestDistance) { best = end; bestDistance = distance; }
                }
                offset = end;
            }
            return best;
        }

        public int FindLastLineEnd(ReadOnlySpan<byte> data)
        {
            for (var end = AlignCodeUnit(data.Length); end >= _newline.Length;)
            {
                var found = data[..end].LastIndexOf(_newline);
                if (found < 0) return 0;
                if (AlignCodeUnit(found) == found) return found + _newline.Length;
                end = found + _newline.Length - 1;
            }
            return 0;
        }

        public int AlignCodeUnit(int length) => _newline.Length == 2 ? length & ~1 : length;

        public ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line) =>
            line.EndsWith(_carriageReturn) ? line[..^_carriageReturn.Length] : line;

        public string Decode(ReadOnlySpan<byte> line)
        {
            if (_encoding is null) return TolerantLineDecoder.Decode(line);
            if (line.Length >= 2 && (line[0] == 0xff && line[1] == 0xfe || line[0] == 0xfe && line[1] == 0xff))
                line = line[2..];
            return _encoding.GetString(line);
        }
    }

    private sealed class LineBlock(string file, byte[] buffer, int length, long firstLineNumber,
        LineFraming framing, bool countTrailingLine, int completeLineCount, long sequence, long endOffset) : IDisposable
    {
        private byte[]? _buffer = buffer;
        public string File { get; } = file;
        public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(LineBlock));
        public int Length { get; } = length;
        public long FirstLineNumber { get; } = firstLineNumber;
        public LineFraming Framing { get; } = framing;
        public bool CountTrailingLine { get; } = countTrailingLine;
        public int CompleteLineCount { get; } = completeLineCount;
        public long Sequence { get; } = sequence;
        public long EndOffset { get; } = endOffset;
        public void Dispose()
        {
            var rented = Interlocked.Exchange(ref _buffer, null);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async ValueTask QueueBlockAsync(ChannelWriter<LineBlock> writer, LineBlock block,
        SearchConcurrencyController concurrency, CancellationToken token)
    {
        concurrency.BlockQueued();
        try { await writer.WriteAsync(block, token); }
        catch
        {
            concurrency.BlockDequeued();
            block.Dispose();
            throw;
        }
    }

    private static async Task RunReadSchedulerAsync(string[] files, SearchConcurrencyController concurrency,
        Func<string, CancellationToken, Task> process, CancellationToken token)
    {
        var active = new List<Task>();
        var next = 0;
        while (next < files.Length || active.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            await RemoveCompletedAsync(active);
            while (next < files.Length && active.Count < concurrency.ReadLimit)
            {
                var file = files[next++];
                active.Add(RunObservedAsync(file, concurrency.ReaderStarted, concurrency.ReaderStopped, process, token));
            }
            if (next >= files.Length && active.Count == 0) break;

            var observed = concurrency.Version;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var changed = concurrency.WaitForChangeAsync(observed, waitCts.Token);
            var winner = await Task.WhenAny(active.Append(changed));
            waitCts.Cancel();
            await winner;
        }
    }

    private static async Task RunComputeSchedulerAsync(ChannelReader<LineBlock> reader,
        SearchConcurrencyController concurrency, Func<LineBlock, CancellationToken, Task> process,
        CancellationToken token)
    {
        var active = new List<Task>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await RemoveCompletedAsync(active);
            while (active.Count < concurrency.ComputeLimit && reader.TryRead(out var block))
            {
                concurrency.BlockDequeued();
                active.Add(Task.Run(() => RunObservedAsync(block, concurrency.ComputationStarted,
                    concurrency.ComputationStopped, process, token)));
            }
            if (active.Count == 0 && reader.Completion.IsCompleted)
            {
                await reader.Completion;
                break;
            }

            var observed = concurrency.Version;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var auxiliaries = new List<Task> { concurrency.WaitForChangeAsync(observed, waitCts.Token) };
            if (active.Count < concurrency.ComputeLimit)
                auxiliaries.Add(reader.WaitToReadAsync(waitCts.Token).AsTask());
            var winner = await Task.WhenAny(active.Concat(auxiliaries));
            waitCts.Cancel();
            await winner;
        }
    }

    private static async Task RunObservedAsync<T>(T item, Action started, Action stopped,
        Func<T, CancellationToken, Task> process, CancellationToken token)
    {
        started();
        try { await process(item, token); }
        finally { stopped(); }
    }

    private static async Task RemoveCompletedAsync(List<Task> tasks)
    {
        for (var index = tasks.Count - 1; index >= 0; index--)
        {
            if (!tasks[index].IsCompleted) continue;
            var task = tasks[index];
            tasks.RemoveAt(index);
            await task;
        }
    }

    private static long GetTotalBytes(IReadOnlyList<string> files, Func<string, long> startOffsetFor)
    {
        long total = 0;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    var start = Math.Min(startOffsetFor(file), info.Length);
                    total = checked(total + info.Length - start);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OverflowException)
            {
                // The actual scan reports the file error. Progress totals must not prevent the
                // remaining files from being searched when a removable volume disappears.
            }
        }
        return total;
    }

    private sealed class FilePipelineState(string file, long initialOffset,
        Action<string, long>? onProcessedOffsetChanged, Func<string, ValueTask>? onCompleted)
    {
        private readonly object _checkpointSync = new();
        private readonly SortedDictionary<long, long> _processedOutOfOrder = [];
        private int _pendingBlocks;
        private int _readerFinished;
        private int _completionStarted;
        private long _nextSequence;
        private long _nextCommitSequence;
        private long _committedOffset = initialOffset;

        public long BlockQueued()
        {
            Interlocked.Increment(ref _pendingBlocks);
            return Interlocked.Increment(ref _nextSequence) - 1;
        }

        public void BlockAbandoned() => Interlocked.Decrement(ref _pendingBlocks);

        public async ValueTask BlockProcessedAsync(long sequence, long endOffset)
        {
            long? committed = null;
            lock (_checkpointSync)
            {
                _processedOutOfOrder[sequence] = endOffset;
                while (_processedOutOfOrder.Remove(_nextCommitSequence, out var contiguousEnd))
                {
                    _committedOffset = contiguousEnd;
                    _nextCommitSequence++;
                    committed = contiguousEnd;
                }
            }
            if (committed is { } offset) onProcessedOffsetChanged?.Invoke(file, offset);
            if (Interlocked.Decrement(ref _pendingBlocks) == 0 && Volatile.Read(ref _readerFinished) != 0)
                await CompleteAsync();
        }

        public async ValueTask ReaderFinishedAsync()
        {
            Volatile.Write(ref _readerFinished, 1);
            if (Volatile.Read(ref _pendingBlocks) == 0) await CompleteAsync();
        }

        private async ValueTask CompleteAsync()
        {
            if (Interlocked.Exchange(ref _completionStarted, 1) != 0 || onCompleted is null) return;
            await onCompleted(file);
        }
    }

    private static bool ShouldPauseForIo(string file, IOException exception,
        IReadOnlyList<(string Root, DriveType Type)> volumeDescriptors)
    {
        // A source read that itself reached the stall timeout is always a device-level
        // interruption. The global watchdog follows the same pause path independently.
        if (exception.InnerException is OperationCanceledException) return true;

        // Windows exposes several removable/network-volume failures through stable Win32
        // error codes. They must pause even if a stale mount directory is still visible.
        var nativeCode = exception.HResult & 0xffff;
        if (nativeCode is 21 or 53 or 55 or 59 or 64 or 67 or 121 or 1231 or 1167) return true;

        var fullPath = Path.GetFullPath(file);
        var volumeRoot = volumeDescriptors
            .Where(item => IsWithinRoot(fullPath, item.Root))
            .OrderByDescending(item => item.Root.Length)
            .Select(item => item.Root)
            .FirstOrDefault();
        var onlyGenericUnixRoot = volumeRoot is not null &&
                                  Path.TrimEndingDirectorySeparator(volumeRoot) ==
                                  Path.DirectorySeparatorChar.ToString();
        if ((volumeRoot is null || onlyGenericUnixRoot) && OperatingSystem.IsMacOS() &&
            fullPath.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var separator = fullPath.IndexOf(Path.DirectorySeparatorChar, "/Volumes/".Length);
            volumeRoot = separator < 0 ? fullPath : fullPath[..separator];
        }
        if ((volumeRoot is null || onlyGenericUnixRoot) && OperatingSystem.IsLinux())
        {
            if (fullPath.StartsWith("/mnt/", StringComparison.Ordinal))
            {
                var separator = fullPath.IndexOf(Path.DirectorySeparatorChar, "/mnt/".Length);
                volumeRoot = separator < 0 ? fullPath : fullPath[..separator];
            }
            else if (fullPath.StartsWith("/media/", StringComparison.Ordinal))
            {
                var userSeparator = fullPath.IndexOf(Path.DirectorySeparatorChar, "/media/".Length);
                if (userSeparator > 0)
                {
                    var volumeSeparator = fullPath.IndexOf(Path.DirectorySeparatorChar, userSeparator + 1);
                    volumeRoot = volumeSeparator < 0 ? fullPath : fullPath[..volumeSeparator];
                }
            }
        }
        volumeRoot ??= Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(volumeRoot)) return false;

        try
        {
            if (!Directory.Exists(volumeRoot)) return true;
            var currentDrive = DriveInfo.GetDrives().FirstOrDefault(drive =>
                PathComparer.Instance.Equals(Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName),
                    Path.TrimEndingDirectorySeparator(volumeRoot)));
            return currentDrive is not null && !currentDrive.IsReady;
        }
        catch (UnauthorizedAccessException)
        {
            // The volume exists but the current account cannot inspect it. Treat the source
            // as a per-file permission problem instead of waiting forever for a remount.
            return false;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static (string Root, DriveType Type)[] GetDriveDescriptors()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Select(drive => (drive.RootDirectory.FullName, drive.DriveType))
                .OrderByDescending(item => item.FullName.Length)
                .Select(item => (item.FullName, item.DriveType))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (normalizedRoot.Length == 0) normalizedRoot = Path.DirectorySeparatorChar.ToString();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path, normalizedRoot, comparison)) return true;
        if (normalizedRoot == Path.DirectorySeparatorChar.ToString()) return path.StartsWith(normalizedRoot, comparison);
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool TryReserveResult(ref long count, long maximum)
    {
        while (true)
        {
            var current = Volatile.Read(ref count);
            if (current >= maximum) return false;
            if (Interlocked.CompareExchange(ref count, current + 1, current) == current) return true;
        }
    }

    private static async Task ConsumeMatchesAsync(ChannelReader<SearchMatch> reader, SearchOptions options,
        string[] sourceFiles, string? outputPath, Func<IReadOnlyList<SearchMatch>, ValueTask>? onMatches, CancellationToken token)
    {
        AtomicOutput? output = null;
        try
        {
            if (outputPath is not null) output = AtomicOutput.Create(outputPath, new UTF8Encoding(false));
            var batch = new List<SearchMatch>(256);
            await foreach (var match in reader.ReadAllAsync(token))
            {
                if (output is not null) await output.Writer.WriteLineAsync(match.ToDisplayString(options.IncludeFileName).AsMemory(), token);
                if (onMatches is not null)
                {
                    batch.Add(match);
                    if (batch.Count >= 256)
                    {
                        await onMatches(batch.ToArray());
                        batch.Clear();
                    }
                }
            }
            if (batch.Count > 0 && onMatches is not null) await onMatches(batch);
            if (output is not null)
                await output.CommitAsync(token, () => FileOperations.EnsureSeparateOutput(sourceFiles, outputPath!));
        }
        finally
        {
            if (output is not null) await output.DisposeAsync();
        }
    }

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public static readonly PathComparer Instance = new();
        private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        public bool Equals(string? x, string? y) => Comparer.Equals(x, y);
        public int GetHashCode(string obj) => Comparer.GetHashCode(obj);
    }

    private sealed class ProgressReadStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0) onRead(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0) onRead(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            if (read > 0) onRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0) onRead(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class VolumeReadLimiter(int maximumReadsPerVolume) : IDisposable
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(PathComparer.Instance);
        private readonly string[] _volumeRoots = GetVolumeRoots();

        public string[] InterleaveByVolume(IEnumerable<string> files)
        {
            var queues = files.GroupBy(GetVolumeKey, PathComparer.Instance)
                .Select(group => new Queue<string>(group))
                .ToArray();
            var ordered = new List<string>();
            while (queues.Any(queue => queue.Count > 0))
                foreach (var queue in queues)
                    if (queue.Count > 0) ordered.Add(queue.Dequeue());
            return ordered.ToArray();
        }

        public async ValueTask<IDisposable> AcquireAsync(string file, CancellationToken token)
        {
            var gate = _gates.GetOrAdd(GetVolumeKey(file), _ => new SemaphoreSlim(maximumReadsPerVolume));
            await gate.WaitAsync(token);
            return new SemaphoreLease(gate);
        }

        public void Dispose()
        {
            foreach (var gate in _gates.Values) gate.Dispose();
        }

        private string GetVolumeKey(string file)
        {
            var fullPath = Path.GetFullPath(file);
            if (OperatingSystem.IsWindows()) return Path.GetPathRoot(fullPath) ?? fullPath;

            var bestRoot = _volumeRoots.FirstOrDefault(root => IsWithinRoot(fullPath, root));
            if (bestRoot is not null) return bestRoot;

            if (OperatingSystem.IsMacOS())
            {
                const string volumes = "/Volumes/";
                if (fullPath.StartsWith(volumes, StringComparison.Ordinal))
                {
                    var separator = fullPath.IndexOf(Path.DirectorySeparatorChar, volumes.Length);
                    return separator < 0 ? fullPath : fullPath[..separator];
                }
            }

            return Path.GetPathRoot(fullPath) ?? fullPath;
        }

        private static string[] GetVolumeRoots()
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Select(drive => drive.RootDirectory.FullName)
                    .OrderByDescending(root => root.Length)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return [];
            }
        }

        private static bool IsWithinRoot(string path, string root)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (normalizedRoot.Length == 0) normalizedRoot = Path.DirectorySeparatorChar.ToString();
            if ((OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Equals(path, normalizedRoot)) return true;
            if (normalizedRoot == Path.DirectorySeparatorChar.ToString()) return path.StartsWith(normalizedRoot, StringComparison.Ordinal);
            return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private sealed class SemaphoreLease(SemaphoreSlim gate) : IDisposable
        {
            private SemaphoreSlim? _gate = gate;
            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
