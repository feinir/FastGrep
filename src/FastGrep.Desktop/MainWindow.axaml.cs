using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FastGrep.Core;

namespace FastGrep.Desktop;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _singleCts, _multiCts, _multiCleanCts, _dedupCts, _splitCts, _findCts, _benchmarkCts;
    private SearchConcurrencyController? _multiSearchConcurrency;
    private SearchIoController? _singleSearchIoController, _multiSearchIoController;
    private readonly SemaphoreSlim _outputFileLock = new(1, 1);
    private string[] _singleSearchSources = [], _multiSearchSources = [];
    private StreamWriter? _resultFileWriter;
    private FileStream? _resultFileStream;
    private string? _openedResultFilePath;
    private long _singleSearchIndex;
    private string? _singleSearchResumeFile;
    private string? _singleSearchResumeSignature;
    private readonly ConcurrentDictionary<string, long> _multiSearchIndexes = new(PathComparer);
    private readonly AvaloniaList<string> _multiFileItems = [];
    private readonly AvaloniaList<string> _multiCleanFileItems = [];
    private readonly AvaloniaList<string> _dedupFileItems = [];
    private readonly AvaloniaList<string> _mergeFileItems = [];
    private readonly AvaloniaList<string> _splitFileItems = [];
    private readonly AvaloniaList<string> _findFileItems = [];
    private readonly ConcurrentQueue<string> _completedMultiFiles = new();
    private int _multiFileRemovalScheduled;
    private readonly Queue<string> _pendingUiResults = new();
    private int _pendingUiChars;
    private bool _showUiResults, _uiResultFlushScheduled, _windowClosed;
    private bool _writeResultsToFile;
    private string? _resultFilePath;
    private const int UiOutputLimit = 10 * 1024 * 1024;
    private const int UiFlushLimit = 128 * 1024;
    private const int UiPendingLimit = 1024 * 1024;
    private static readonly TimeSpan OutputIoTimeout = TimeSpan.FromSeconds(30);

    public MainWindow()
    {
        InitializeComponent();
        MultiFiles.ItemsSource = _multiFileItems;
        MultiCleanFiles.ItemsSource = _multiCleanFileItems;
        DedupFiles.ItemsSource = _dedupFileItems;
        MergeFiles.ItemsSource = _mergeFileItems;
        SplitFiles.ItemsSource = _splitFileItems;
        FindFiles.ItemsSource = _findFileItems;
        RuntimeRecovery.SetUiReporter(AppendInfo);
        _showUiResults = OutputResultsToUi.IsChecked == true;
        Closed += (_, _) =>
        {
            RuntimeRecovery.SetUiReporter(null);
            lock (_pendingUiResults)
            {
                _windowClosed = true;
                _pendingUiResults.Clear();
                _pendingUiChars = 0;
            }
        };
        if (int.TryParse(Environment.GetEnvironmentVariable("FASTGREP_TAB"), out var tab))
        {
            var selectedTab = Math.Clamp(tab, 0, 7);
            MainTabs.SelectedIndex = selectedTab;
            Opened += (_, _) => MainTabs.SelectedIndex = selectedTab;
        }
        AppendInfo("FastGrep 已就绪。所有输出默认使用 UTF-8；处理时不会覆盖输入文件。");
    }

    internal void RunClipboardSmokeTest()
    {
        OutputText.Text = "FastGrep clipboard smoke test";
        OutputText.SelectionStart = 0;
        OutputText.SelectionEnd = OutputText.Text.Length;
        OutputText.Copy();
    }

    internal bool RunFileListSmokeTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "fastgrep-list-smoke");
        var first = Path.Combine(root, "first.txt");
        var second = Path.Combine(root, "second.txt");
        var third = Path.Combine(root, "third.txt");
        AddPaths(MultiFiles, [first, first, second, third], deduplicate: true);
        if (Paths(MultiFiles).Length != 3) return false;
        MultiFiles.SelectedItems!.Add(Path.GetFullPath(first));
        MultiFiles.SelectedItems.Add(Path.GetFullPath(third));
        RemoveSelected(MultiFiles);
        var remaining = Paths(MultiFiles);
        var passed = remaining.Length == 1 && PathComparer.Equals(remaining[0], Path.GetFullPath(second));
        ClearPaths(MultiFiles);
        return passed;
    }

    internal async Task RunOutputSmokeTestAsync()
    {
        var directory = Directory.CreateTempSubdirectory("fastgrep-output-");
        var source = Path.Combine(directory.FullName, "source.txt");
        var destination = Path.Combine(directory.FullName, "results.txt");
        const string original = "keep this source file";
        await File.WriteAllTextAsync(source, original);
        try
        {
            OutputResultsToFile.IsChecked = true;
            OutputFile.Text = source;
            await ExpectConflictAsync(() => ProtectSearchSourcesAsync([source], true, CancellationToken.None));
            OutputFile.Text = destination;
            await ProtectSearchSourcesAsync([source], true, CancellationToken.None);
            OutputFile.Text = source;
            await ExpectConflictAsync(() => AppendResultAsync("must not be written").AsTask());
            if (await File.ReadAllTextAsync(source) != original)
                throw new InvalidOperationException("The search result changed the source file.");

            OutputFile.Text = destination;
            var message = new string('x', 8192);
            for (var i = 0; i < 2048; i++) QueueUiResult(message);
            QueueUiResult("latest result");
            if (_pendingUiChars > UiPendingLimit || _pendingUiResults.Last() != "latest result")
                throw new InvalidOperationException("The UI queue did not keep a bounded tail.");
            OutputResultsToUi.IsChecked = false;
            QueueUiResult("disabled result");
            if (_pendingUiChars != 0 || _pendingUiResults.Count != 0)
                throw new InvalidOperationException("A disabled UI queue retained results.");
            await AppendResultAsync("saved with UI output disabled");
            await CloseResultFileAsync();
            if ((await File.ReadAllLinesAsync(destination)).Single() != "saved with UI output disabled")
                throw new InvalidOperationException("Disabling UI output affected file output.");
        }
        finally
        {
            Volatile.Write(ref _singleSearchSources, []);
            await CloseResultFileAsync();
            directory.Delete(recursive: true);
        }

        static async Task ExpectConflictAsync(Func<Task> operation)
        {
            try { await operation(); }
            catch (OutputSourceConflictException) { return; }
            throw new InvalidOperationException("The search accepted one of its inputs as its output.");
        }
    }

    internal (bool Passed, string Details) RunCompactInputLayoutSmokeTest()
    {
        MainTabs.SelectedIndex = 0;
        UpdateLayout();
        var singleSeparatorWidth = SingleSeparator.Bounds.Width;
        var singleFieldsWidth = SingleFields.Bounds.Width;
        var singleShortWidth = SingleShort.Bounds.Width;
        var singleControlGap = SingleFields.Bounds.Left - SingleSeparator.Bounds.Right;

        MainTabs.SelectedIndex = 2;
        UpdateLayout();
        var multiSeparatorWidth = MultiCleanSeparator.Bounds.Width;
        var multiShortWidth = MultiCleanShort.Bounds.Width;
        var multiControlGap = MultiCleanFields.Bounds.Left - MultiCleanSeparator.Bounds.Right;
        var passed = Math.Abs(singleSeparatorWidth - 32) < 1 &&
                     Math.Abs(singleFieldsWidth - 64) < 1 &&
                     Math.Abs(singleShortWidth - 50) < 1 &&
                     Math.Abs(multiSeparatorWidth - 34) < 1 &&
                     Math.Abs(multiShortWidth - 40) < 1 &&
                     singleControlGap > 0 && multiControlGap > 0;
        return (passed, $"single_separator={singleSeparatorWidth:N1} single_fields={singleFieldsWidth:N1} " +
                        $"single_short={singleShortWidth:N1} single_gap={singleControlGap:N1} " +
                        $"multi_separator={multiSeparatorWidth:N1} multi_short={multiShortWidth:N1} " +
                        $"multi_gap={multiControlGap:N1}");
    }

    private static string[] Lines(TextBox box) => (box.Text ?? "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static AvaloniaList<string> PathItems(ListBox box) =>
        box.ItemsSource as AvaloniaList<string> ??
        throw new InvalidOperationException("文件列表尚未初始化。");

    private static string[] Paths(ListBox box) => PathItems(box).ToArray();

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"请填写{name}。") : value.Trim();

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int IntValue(TextBox box, int fallback, int minimum = 1) =>
        int.TryParse(box.Text, out var value) ? Math.Max(minimum, value) : fallback;
    private static long LongValue(TextBox box, long fallback, long minimum = 0) =>
        long.TryParse(box.Text, out var value) ? Math.Max(minimum, value) : fallback;
    private static string[] Pipes(string? value, string fallback = "") =>
        (string.IsNullOrWhiteSpace(value) ? fallback : value).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void AppendInfo(string message)
    {
        SystemLog.Information("UI", message);
        AppendOutput($"[{DateTime.Now:HH:mm:ss}] {message}", false);
    }

    private void ReportUiOperationFailure(string operation, Exception exception)
    {
        SystemLog.Warning("UI", operation + "失败。", exception);
        AppendInfo(operation + "失败：" + exception.Message + "；详情已写入系统日志。");
    }

    private ValueTask AppendResultAsync(string message) => AppendResultAsync(message, CancellationToken.None);

    private async ValueTask AppendResultAsync(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueUiResult(message);
        if (Volatile.Read(ref _writeResultsToFile) && Volatile.Read(ref _resultFilePath) is { } path)
            await AppendResultFileAsync(path, message, cancellationToken);
    }

    private void QueueUiResult(string message)
    {
        lock (_pendingUiResults)
        {
            if (!_showUiResults || _windowClosed || message.Length == 0) return;
            message = TakeTail(message, UiPendingLimit - Environment.NewLine.Length);
            var chars = message.Length + Environment.NewLine.Length;
            while (_pendingUiChars > UiPendingLimit - chars)
                _pendingUiChars -= _pendingUiResults.Dequeue().Length + Environment.NewLine.Length;
            _pendingUiResults.Enqueue(message);
            _pendingUiChars += chars;
            if (_uiResultFlushScheduled) return;
            _uiResultFlushScheduled = true;
        }
        Dispatcher.UIThread.Post(FlushUiResults, DispatcherPriority.Background);
    }

    private void FlushUiResults()
    {
        var buffer = new StringBuilder(Math.Min(UiFlushLimit, 16 * 1024));
        lock (_pendingUiResults)
        {
            while (buffer.Length < UiFlushLimit && _pendingUiResults.TryDequeue(out var message))
            {
                _pendingUiChars -= message.Length + Environment.NewLine.Length;
                buffer.AppendLine(message);
            }
            _uiResultFlushScheduled = _pendingUiResults.Count > 0;
            if (_uiResultFlushScheduled)
                Dispatcher.UIThread.Post(FlushUiResults, DispatcherPriority.Background);
        }
        if (buffer.Length == 0) return;
        var text = TakeTail(OutputText.Text ?? "", UiOutputLimit - buffer.Length) + buffer;
        OutputText.Text = text;
        OutputText.CaretIndex = text.Length;
    }

    private static string TakeTail(string text, int limit)
    {
        if (text.Length <= limit) return text;
        if (limit == 0) return "";
        var start = text.Length - limit;
        return text[(char.IsLowSurrogate(text[start]) ? start + 1 : start)..];
    }

    private void OutputResultsToUi_Changed(object? s, RoutedEventArgs e)
    {
        lock (_pendingUiResults)
        {
            _showUiResults = ((CheckBox)s!).IsChecked == true;
            if (_showUiResults) return;
            _pendingUiResults.Clear();
            _pendingUiChars = 0;
        }
    }

    private void AppendOutput(string message, bool result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!result || OutputResultsToUi.IsChecked == true)
            {
                var text = (OutputText.Text ?? "") + message + Environment.NewLine;
                text = TakeTail(text, UiOutputLimit);
                OutputText.Text = text;
                OutputText.CaretIndex = text.Length;
            }
        });
    }

    private async Task ProtectSearchSourcesAsync(string[] sources, bool single, CancellationToken token)
    {
        await _outputFileLock.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref _writeResultsToFile) && Volatile.Read(ref _resultFilePath) is { } path)
                FileOperations.EnsureSeparateOutput(sources, path);
            if (single) Volatile.Write(ref _singleSearchSources, sources);
            else Volatile.Write(ref _multiSearchSources, sources);
        }
        finally { _outputFileLock.Release(); }
    }

    private IEnumerable<string> ActiveSearchSources() =>
        Volatile.Read(ref _singleSearchSources).Concat(Volatile.Read(ref _multiSearchSources));

    private async Task AppendResultFileAsync(string path, string message, CancellationToken cancellationToken)
    {
        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ioCts.CancelAfter(OutputIoTimeout);
        var ioToken = ioCts.Token;
        var lockTaken = false;
        try
        {
            await _outputFileLock.WaitAsync(ioToken);
            lockTaken = true;
            if (!Volatile.Read(ref _writeResultsToFile) || !PathComparer.Equals(path, Volatile.Read(ref _resultFilePath)))
                return;
            var fullPath = Path.GetFullPath(path);
            if (_resultFileWriter is null || _resultFileStream is null || !PathComparer.Equals(_openedResultFilePath, fullPath))
            {
                FileOperations.EnsureSeparateOutput(ActiveSearchSources(), fullPath);
                await DisposeResultFileWriterAsync();
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                _resultFileStream = stream;
                _resultFileWriter = new StreamWriter(stream, new UTF8Encoding(false), 1024 * 1024);
                _openedResultFilePath = fullPath;
            }
            using var abort = ioToken.Register(static state =>
            {
                try { ((FileStream?)state)?.Dispose(); }
                catch { }
            }, _resultFileStream);
            await _resultFileWriter.WriteLineAsync(message.AsMemory(), ioToken);
        }
        catch (OutputSourceConflictException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (lockTaken) AbortResultFileWrite();
            throw;
        }
        catch (Exception ex)
        {
            if (lockTaken) AbortResultFileWrite();
            cancellationToken.ThrowIfCancellationRequested();
            SystemLog.Warning("OUTPUT", $"结果文件写入失败：{path}", ex);
            AppendInfo("结果文件写入失败：" + ex.Message);
        }
        finally { if (lockTaken) _outputFileLock.Release(); }
    }

    private async Task FlushResultFileAsync()
    {
        await _outputFileLock.WaitAsync();
        try
        {
            if (_resultFileWriter is not null) await _resultFileWriter.FlushAsync();
        }
        catch (Exception ex)
        {
            AbortResultFileWrite();
            await DisposeResultFileWriterAsync();
            SystemLog.Warning("OUTPUT", "结果文件刷新失败。", ex);
            AppendInfo("结果文件刷新失败：" + ex.Message);
        }
        finally { _outputFileLock.Release(); }
    }

    private async Task CloseResultFileAsync()
    {
        await _outputFileLock.WaitAsync();
        try { await DisposeResultFileWriterAsync(); }
        finally { _outputFileLock.Release(); }
    }

    private async Task DisposeResultFileWriterAsync()
    {
        var writer = _resultFileWriter;
        _resultFileWriter = null;
        _openedResultFilePath = null;
        var stream = Interlocked.Exchange(ref _resultFileStream, null);
        if (stream is not null)
        {
            try
            {
                if (writer is not null) await writer.DisposeAsync();
            }
            finally { stream.Dispose(); }
        }
    }

    private void AbortResultFileWrite()
    {
        try { Interlocked.Exchange(ref _resultFileStream, null)?.Dispose(); }
        catch { }
    }

    private async Task RunAsync(string name, Func<CancellationToken, Task<string>> action, Func<CancellationTokenSource?> current,
        Action<CancellationTokenSource?> assign)
    {
        if (current() is not null) { AppendInfo(name + "已有任务正在运行。"); return; }
        var cts = new CancellationTokenSource(); assign(cts);
        var taskWatch = Stopwatch.StartNew();
        SystemLog.Information("TASK", $"{name}任务创建。工作目录={Environment.CurrentDirectory}");
        AppendInfo(name + "开始。");
        try { AppendInfo(await action(cts.Token)); }
        catch (OperationCanceledException ex)
        {
            SystemLog.Warning("TASK", $"{name}收到取消请求，已运行 {taskWatch.Elapsed}。", ex);
            AppendInfo(name + "已停止，尚未处理的文件仍保留在清单中。");
        }
        catch (OutputSourceConflictException ex)
        {
            SystemLog.Warning("OUTPUT", name + "的结果文件指向输入文件。", ex);
            AppendInfo(name + "已停止：结果文件和输入文件指向同一个文件，请选择其他结果路径。");
        }
        catch (Exception ex)
        {
            RuntimeRecovery.Record(name + "任务失败", ex);
            AppendInfo(name + "失败：" + ex.Message + "；详情已写入日志。");
        }
        finally
        {
            try { await FlushResultFileAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                AbortResultFileWrite();
                SystemLog.Warning("OUTPUT", $"{name}停止时结果文件刷新超过5秒，已关闭输出句柄。");
            }
            var wasCancelled = cts.IsCancellationRequested;
            cts.Dispose();
            assign(null);
            SystemLog.Information("TASK", $"{name}任务结束。总耗时={taskWatch.Elapsed}；已取消={wasCancelled}");
        }
    }

    private static void SetDisplay(Control control, string value)
    {
        if (control is TextBox textBox) textBox.Text = value;
        else if (control is TextBlock textBlock) textBlock.Text = value;
        else if (control is Label label) label.Content = value;
    }

    private IProgress<JobProgress> ProgressFor(string operation, params Control[] totals)
    {
        var lastSample = Stopwatch.GetTimestamp();
        long lastReadBytes = 0;
        long lastComputedBytes = 0;
        double lastReadSpeed = 0;
        double lastComputeSpeed = 0;
        long lastSystemLog = 0;
        var completionLogged = 0;
        return new ThrottledProgress(p =>
        {
            var now = Stopwatch.GetTimestamp();
            var computedBytes = p.ComputedBytes >= 0 ? p.ComputedBytes : p.BytesProcessed;
            var elapsedSeconds = (now - lastSample) / (double)Stopwatch.Frequency;
            if (elapsedSeconds >= 0.2)
            {
                lastReadSpeed = Math.Max(0, p.BytesProcessed - lastReadBytes) / 1024d / 1024d / elapsedSeconds;
                lastComputeSpeed = Math.Max(0, computedBytes - lastComputedBytes) / 1024d / 1024d / elapsedSeconds;
                lastReadBytes = p.BytesProcessed;
                lastComputedBytes = computedBytes;
                lastSample = now;
            }
            var readMb = p.BytesProcessed / 1024d / 1024d;
            var computedMb = computedBytes / 1024d / 1024d;
            if (totals.Length > 0) SetDisplay(totals[0], $"{readMb:N1} MB");
            if (totals.Length > 1) SetDisplay(totals[1], $"{lastReadSpeed:N1} MB/s");
            if (totals.Length > 2) SetDisplay(totals[2], $"{computedMb:N1} MB");
            if (totals.Length > 3) SetDisplay(totals[3], $"{lastComputeSpeed:N1} MB/s");

            var final = p.TotalBytes > 0 && p.BytesProcessed >= p.TotalBytes;
            if ((final && Interlocked.Exchange(ref completionLogged, 1) == 0) ||
                (!final && now - Volatile.Read(ref lastSystemLog) >= Stopwatch.Frequency * 30 &&
                 Interlocked.Exchange(ref lastSystemLog, now) <= now - Stopwatch.Frequency * 30))
            {
                SystemLog.Information("PROGRESS", $"{operation}：阶段={p.Stage}；" +
                    $"已处理={p.BytesProcessed:N0} 字节；总量={p.TotalBytes:N0} 字节；" +
                    $"已计算={computedBytes:N0} 字节；项目={p.ItemsProcessed:N0}；" +
                    $"瞬时读取={lastReadSpeed:N1} MB/s；瞬时计算={lastComputeSpeed:N1} MB/s；" +
                    $"当前文件={p.CurrentFile ?? "-"}；消息={p.Message ?? "-"}");
            }
        });
    }

    private sealed class ThrottledProgress(Action<JobProgress> update) : IProgress<JobProgress>
    {
        private long _lastUpdate;

        public void Report(JobProgress value)
        {
            var now = Stopwatch.GetTimestamp();
            var minimumTicks = Stopwatch.Frequency / 10;
            var final = value.TotalBytes > 0 && value.BytesProcessed >= value.TotalBytes;
            var previous = Volatile.Read(ref _lastUpdate);
            if (!final && now - previous < minimumTicks) return;
            if (!final && Interlocked.CompareExchange(ref _lastUpdate, now, previous) != previous) return;
            if (final) Interlocked.Exchange(ref _lastUpdate, now);
            Dispatcher.UIThread.Post(() => update(value), DispatcherPriority.Background);
        }
    }

    private async Task<string[]> KeywordsAsync(bool fromFile, TextBox text, TextBox file, CancellationToken token)
    {
        if (!fromFile) return Lines(text).Distinct(StringComparer.Ordinal).ToArray();
        var path = Required(file.Text, "关键字文件");
        return (await File.ReadAllLinesAsync(path, token)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private SearchOptions SearchOptionsFor(string[] patterns, bool ignoreCase, int readWorkers, int computeWorkers,
        SearchConcurrencyController? concurrency = null,
        SearchProcessingMode mode = SearchProcessingMode.CharacterBlock) => new()
    {
        Patterns = patterns, IgnoreCase = ignoreCase,
        ReadDegreeOfParallelism = Math.Max(1, readWorkers),
        DegreeOfParallelism = Math.Max(1, computeWorkers),
        ConcurrencyController = concurrency,
        MaxConcurrentReadsPerVolume = 6, ReadBufferSize = 4 * 1024 * 1024, IncludeFileName = true,
        ProcessingMode = mode, ReadStallTimeout = TimeSpan.FromSeconds(30),
        FileSkipped = (file, reason) => SystemLog.Warning("SEARCH", $"跳过文件：{file}；原因={reason}")
    };

    private SearchProcessingMode SingleSearchMode() => SingleModeLineBlock.IsChecked == true
        ? SearchProcessingMode.LineBlock
        : SingleModeLine.IsChecked == true ? SearchProcessingMode.SingleLine : SearchProcessingMode.CharacterBlock;

    private SearchProcessingMode MultiSearchMode() => MultiModeLineBlock.IsChecked == true
        ? SearchProcessingMode.LineBlock
        : MultiModeLine.IsChecked == true ? SearchProcessingMode.SingleLine : SearchProcessingMode.CharacterBlock;

    private static string SingleSearchSignature(string file, IEnumerable<string> patterns, bool ignoreCase,
        SearchProcessingMode mode) => string.Join('\u001f',
        Path.GetFullPath(file), ignoreCase.ToString(), mode.ToString(), string.Join('\u001e', patterns));

    private async ValueTask WriteSearchBatchAsync(IReadOnlyList<SearchMatch> batch, Func<SearchMatch, string> format,
        CancellationToken cancellationToken)
    {
        if (batch.Count > 0 && (Volatile.Read(ref _showUiResults) || Volatile.Read(ref _writeResultsToFile)))
            await AppendResultAsync(string.Join(Environment.NewLine, batch.Select(format)), cancellationToken);
    }

    private async Task WaitForFileRecoveryAsync(string file, string operation, CancellationToken token)
    {
        while (true)
        {
            AppendInfo($"{operation}已暂停；30秒后检查磁盘是否恢复：{file}");
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            if (await CanReadFileAsync(file, token))
            {
                AppendInfo($"检测到磁盘已恢复，{operation}将从最后成功处理位置自动继续：{file}");
                return;
            }
            SystemLog.Warning("SEARCH", $"{operation}恢复检查失败，文件仍不可读取：{file}");
        }
    }

    private static async Task<bool> CanReadFileAsync(string file, CancellationToken token)
    {
        try
        {
            var probe = Task.Run(() =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    4096, FileOptions.SequentialScan);
                _ = stream.ReadByte();
            });
            await probe.WaitAsync(TimeSpan.FromSeconds(5), token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task<IReadOnlyList<IStorageFile>> PickFiles(string title) => await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    { Title = title, AllowMultiple = true });

    private async Task<string?> PickFolder(string title)
    {
        var items = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return items.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSave(string title, string? suggested = null)
    {
        var item = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested });
        return item?.TryGetLocalPath();
    }

    private static void AddPaths(ListBox box, IEnumerable<string?> paths, bool deduplicate = true, bool replace = false)
    {
        var items = PathItems(box);
        var incoming = paths.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.GetFullPath(x!.Trim()));
        if (replace)
        {
            var replacement = (deduplicate ? incoming.Distinct(PathComparer) : incoming).ToArray();
            items.Clear();
            items.AddRange(replacement);
            return;
        }
        if (!deduplicate)
        {
            items.AddRange(incoming);
            return;
        }
        var seen = new HashSet<string>(items, PathComparer);
        items.AddRange(incoming.Where(seen.Add));
    }

    private static void RemoveSelected(ListBox box)
    {
        var selected = box.SelectedItems?.OfType<string>().ToArray() ?? [];
        if (selected.Length == 0) return;
        PathItems(box).RemoveAll(selected);
    }

    private static void ClearPaths(ListBox box) => PathItems(box).Clear();

    private async Task AddFolder(ListBox target, TextBox patterns, bool deduplicate = true, long? minimumBytes = null)
    {
        if (await PickFolder("选择要导入的文件夹") is not { } root) return;
        var includes = Pipes(patterns.Text, "*");
        var files = await Task.Run(() => FileOperations.Discover(new FileDiscoveryOptions
        { Root = root, Includes = includes, Recursive = true, MinimumBytes = minimumBytes }));
        AddPaths(target, files, deduplicate);
    }

    private async Task LoadList(ListBox target, bool deduplicate = true)
    {
        var selected = await PickFiles("选择文件清单");
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) AddPaths(target, await File.ReadAllLinesAsync(path), deduplicate);
    }

    private async Task SaveList(ListBox target)
    {
        if (await PickSave("保存文件清单", "files.txt") is { } path)
            await File.WriteAllLinesAsync(path, Paths(target), new UTF8Encoding(false));
    }

    private void UpdateCount(ListBox list, TextBox count) => count.Text = PathItems(list).Count.ToString("N0");

    // 单文件处理
    private async void PickSingleFile_Click(object? s, RoutedEventArgs e)
    {
        var item = (await PickFiles("选择待处理文件")).FirstOrDefault();
        if (item?.TryGetLocalPath() is not { } path) return;
        SingleFile.Text = path; SingleFileSize.Text = (new FileInfo(path).Length / 1024d).ToString("N0");
    }

    private async void PickSingleKeywordFile_Click(object? s, RoutedEventArgs e) => SingleKeywordFile.Text = (await PickFiles("选择关键字文件")).FirstOrDefault()?.TryGetLocalPath();
    private async void PickSingleCleanOutput_Click(object? s, RoutedEventArgs e) => SingleCleanOutput.Text = await PickSave("选择清理结果文件", "cleaned.txt");
    private void SingleWorkerAdd_Click(object? s, RoutedEventArgs e) => SingleWorkers.Text = (IntValue(SingleWorkers, 16) + 1).ToString();
    private void SingleWorkerSub_Click(object? s, RoutedEventArgs e) => SingleWorkers.Text = Math.Max(1, IntValue(SingleWorkers, 16) - 1).ToString();
    private void SingleStop_Click(object? s, RoutedEventArgs e)
    {
        _singleCts?.Cancel();
        _singleSearchIoController?.AbortPendingReads();
        AbortResultFileWrite();
    }

    private async Task ReadSingle(long offset)
    {
        var file = Required(SingleFile.Text, "待处理文件");
        var bytes = checked(IntValue(SingleReadSize, 1024) * 1024);
        var reader = new TextReaderService();
        var text = offset < 0 ? await reader.ReadTailAsync(file, bytes) : await reader.ReadRangeAsync(file, offset, bytes);
        await AppendResultAsync(text);
        await FlushResultFileAsync();
        AppendInfo($"局部读取完成：{Encoding.UTF8.GetByteCount(text):N0} 字节。");
    }

    private async void SingleReadHead_Click(object? s, RoutedEventArgs e) { try { await ReadSingle(0); } catch (Exception ex) { ReportUiOperationFailure("读取文件头部", ex); } }
    private async void SingleReadTail_Click(object? s, RoutedEventArgs e) { try { await ReadSingle(-1); } catch (Exception ex) { ReportUiOperationFailure("读取文件尾部", ex); } }
    private async void SingleReadMiddle_Click(object? s, RoutedEventArgs e) { try { await ReadSingle(LongValue(SingleReadStart, 0) * 1024); } catch (Exception ex) { ReportUiOperationFailure("读取文件中部", ex); } }

    private async void SingleSearch_Click(object? s, RoutedEventArgs e) => await RunAsync("单文件搜索", async token =>
    {
        SingleSearchStatus.Text = "搜索中"; SingleCurrentWorkers.Text = IntValue(SingleWorkers, 16).ToString();
        var pausedForIo = false;
        try
        {
            var file = Required(SingleFile.Text, "待处理文件");
            var keywordFile = SingleKeywordFileMode.IsChecked == true ? Optional(SingleKeywordFile.Text) : null;
            await ProtectSearchSourcesAsync(keywordFile is null ? [file] : [file, keywordFile], true, token);
            var patterns = await KeywordsAsync(SingleKeywordFileMode.IsChecked == true, SingleKeywordText, SingleKeywordFile, token);
            if (patterns.Length == 0) throw new InvalidOperationException("关键字不能为空。");
            var ignoreCase = SingleIgnoreCase.IsChecked == true;
            var mode = SingleSearchMode();
            var signature = SingleSearchSignature(file, patterns, ignoreCase, mode);
            var resume = PathComparer.Equals(_singleSearchResumeFile, Path.GetFullPath(file)) &&
                         string.Equals(_singleSearchResumeSignature, signature, StringComparison.Ordinal);
            var startOffset = resume ? Math.Max(0, Interlocked.Read(ref _singleSearchIndex)) : 0;
            if (!resume)
            {
                _singleSearchResumeFile = null;
                _singleSearchResumeSignature = null;
                Interlocked.Exchange(ref _singleSearchIndex, 0);
            }
            if (startOffset > 0)
                AppendInfo($"单文件搜索从最后成功处理位置恢复：{startOffset:N0} 字节。");
            var ioController = new SearchIoController();
            _singleSearchIoController = ioController;
            SystemLog.Information("SEARCH", $"单文件搜索参数：文件={file}；关键字={patterns.Length:N0}；" +
                $"忽略大小写={ignoreCase}；模式={mode}；计算线程={IntValue(SingleWorkers, 16)}；" +
                $"起点={startOffset:N0}；读取停滞保护=30秒");
            SearchSummary summary;
            while (true)
            {
                startOffset = Math.Max(0, Interlocked.Read(ref _singleSearchIndex));
                var options = SearchOptionsFor(patterns, SingleIgnoreCase.IsChecked == true, 1,
                    IntValue(SingleWorkers, 16), mode: mode) with
                {
                    StartOffset = startOffset,
                    ProcessedOffsetChanged = (_, offset) => Interlocked.Exchange(ref _singleSearchIndex, offset),
                    IoController = ioController
                };
                var progress = ProgressFor("单文件搜索", SingleReadTotal, SingleReadSpeed,
                    SingleComputeTotal, SingleComputeSpeed);
                try
                {
                    summary = await Task.Run(() => new SearchEngine().SearchAsync([file], options,
                        batch => WriteSearchBatchAsync(batch, m => m.Text, token), progress, token), token);
                    break;
                }
                catch (SearchIoInterruptedException ex)
                {
                    var checkpoint = Math.Max(0, Interlocked.Read(ref _singleSearchIndex));
                    _singleSearchResumeFile = Path.GetFullPath(file);
                    _singleSearchResumeSignature = signature;
                    pausedForIo = true;
                    SingleSearchStatus.Text = "等待磁盘";
                    SystemLog.Warning("SEARCH",
                        $"单文件搜索因I/O异常暂停；成功处理位置={checkpoint:N0}。将自动等待磁盘恢复。", ex);
                    await WaitForFileRecoveryAsync(ex.FilePath, "单文件搜索", token);
                    pausedForIo = false;
                    SingleSearchStatus.Text = "搜索中";
                }
            }
            _singleSearchResumeFile = null;
            _singleSearchResumeSignature = null;
            Interlocked.Exchange(ref _singleSearchIndex, 0);
            return $"单文件搜索完成：{summary.LinesScanned:N0} 行，命中 {summary.MatchCount:N0} 行，用时 {summary.Elapsed.TotalSeconds:N2} 秒。";
        }
        finally
        {
            Volatile.Write(ref _singleSearchSources, []);
            _singleSearchIoController = null;
            if (token.IsCancellationRequested)
            {
                _singleSearchResumeFile = null;
                _singleSearchResumeSignature = null;
                Interlocked.Exchange(ref _singleSearchIndex, 0);
            }
            if (!pausedForIo || token.IsCancellationRequested) SingleSearchStatus.Text = "已停止";
            SingleCurrentWorkers.Text = "0";
        }
    }, () => _singleCts, x => _singleCts = x);

    private CleanOptions SingleCleanOptions() => new()
    {
        RemovePrefix = SingleRemovePrefixOn.IsChecked == true ? Optional(SingleRemovePrefix.Text) : null,
        RemoveContaining = SingleRemoveContainsOn.IsChecked == true ? Optional(SingleRemoveContains.Text) : null,
        RemoveRegex = SingleRemoveRegexOn.IsChecked == true ? Optional(SingleRemoveRegex.Text) : null,
        RemovePrefixIgnoreCase = SinglePrefixIgnoreCase.IsChecked == true,
        RemoveContainingIgnoreCase = SingleContainsIgnoreCase.IsChecked == true,
        Trim = SingleTrim.IsChecked == true, RemoveAllWhitespace = SingleInnerWhitespace.IsChecked == true,
        RemoveBlankLines = SingleBlank.IsChecked == true,
        RemoveLinesAtMost = SingleShortOn.IsChecked == true ? IntValue(SingleShort, 5, 0) : null,
        FieldSeparator = SingleRemoveFieldsOn.IsChecked == true && !string.IsNullOrEmpty(SingleSeparator.Text) ? SingleSeparator.Text[0] : null,
        RemoveFieldNumbers = ParseFields(SingleFields.Text)
    };

    private static IReadOnlySet<int> ParseFields(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.TryParse(x, out var n) ? n : 0).Where(x => x > 0).ToHashSet();

    private async void SingleClean_Click(object? s, RoutedEventArgs e) => await RunAsync("单文件清理", async token =>
    {
        SingleCleanStatus.Text = "清理中";
        var summary = await new CleanEngine().CleanAsync([Required(SingleFile.Text, "待处理文件")], Required(SingleCleanOutput.Text, "结果保存文件"), SingleCleanOptions(), ProgressFor("单文件清理", SingleComputeTotal, SingleComputeSpeed), token,
            AppendResultAsync);
        SingleCleanStatus.Text = "已停止";
        return $"单文件清理完成：输入 {summary.InputLines:N0} 行，输出 {summary.OutputLines:N0} 行，删除 {summary.RemovedLines:N0} 行。";
    }, () => _singleCts, x => _singleCts = x);

    // 多文件搜索
    private async void PickMultiKeywordFile_Click(object? s, RoutedEventArgs e) => MultiKeywordFile.Text = (await PickFiles("选择关键字文件")).FirstOrDefault()?.TryGetLocalPath();
    private async void MultiAddFiles_Click(object? s, RoutedEventArgs e) { AddPaths(MultiFiles, (await PickFiles("导入搜索文件")).Select(x => x.TryGetLocalPath()), MultiDeduplicateImport.IsChecked == true); UpdateMultiFileList(); }
    private async void MultiAddFolder_Click(object? s, RoutedEventArgs e) { await AddFolder(MultiFiles, MultiTypes, MultiDeduplicateImport.IsChecked == true); UpdateMultiFileList(); }
    private async void MultiLoadList_Click(object? s, RoutedEventArgs e) { await LoadList(MultiFiles, MultiDeduplicateImport.IsChecked == true); UpdateMultiFileList(); }
    private async void MultiSaveList_Click(object? s, RoutedEventArgs e) => await SaveList(MultiFiles);
    private void MultiRemoveSelected_Click(object? s, RoutedEventArgs e) { RemoveSelected(MultiFiles); UpdateCount(MultiFiles, MultiFileCount); }
    private void MultiClear_Click(object? s, RoutedEventArgs e) { ClearPaths(MultiFiles); UpdateCount(MultiFiles, MultiFileCount); }

    private void UpdateMultiFileList()
    {
        var files = Paths(MultiFiles);
        MultiFileCount.Text = files.Length.ToString("N0");
        if (_multiSearchConcurrency is null && files.Length > 0)
            MultiReadWorkers.Text = SearchEngine.RecommendReadConcurrency(files).ToString();
    }

    private ValueTask RemoveCompletedMultiFileAsync(string completedFile)
    {
        _multiSearchIndexes.TryRemove(Path.GetFullPath(completedFile), out _);
        _completedMultiFiles.Enqueue(Path.GetFullPath(completedFile));
        ScheduleCompletedMultiFileRemoval();
        return ValueTask.CompletedTask;
    }

    private void ScheduleCompletedMultiFileRemoval()
    {
        if (Interlocked.Exchange(ref _multiFileRemovalScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(FlushCompletedMultiFiles, DispatcherPriority.Background);
    }

    private void FlushCompletedMultiFiles()
    {
        var completed = new HashSet<string>(PathComparer);
        while (_completedMultiFiles.TryDequeue(out var file)) completed.Add(file);
        if (completed.Count > 0)
        {
            var items = PathItems(MultiFiles);
            var completedItems = items
                .Where(path => completed.Contains(Path.GetFullPath(path))).ToArray();
            items.RemoveAll(completedItems);
            MultiFileCount.Text = items.Count.ToString("N0");
        }
        Interlocked.Exchange(ref _multiFileRemovalScheduled, 0);
        if (!_completedMultiFiles.IsEmpty) ScheduleCompletedMultiFileRemoval();
    }

    private void MultiSafeStop_Click(object? s, RoutedEventArgs e)
    {
        AppendInfo("多文件搜索正在安全停止，并关闭可能挂起的读取句柄。");
        _multiCts?.Cancel();
        _multiSearchIoController?.AbortPendingReads();
        AbortResultFileWrite();
    }
    private void MultiForceStop_Click(object? s, RoutedEventArgs e)
    {
        AppendInfo("多文件搜索已请求立即取消任务并关闭读取句柄。");
        _multiCts?.Cancel();
        _multiSearchIoController?.AbortPendingReads();
        AbortResultFileWrite();
    }
    private void MultiReaderAdd_Click(object? s, RoutedEventArgs e) => AdjustMultiReadLimit(1);
    private void MultiReaderSub_Click(object? s, RoutedEventArgs e) => AdjustMultiReadLimit(-1);
    private void MultiWorkerAdd_Click(object? s, RoutedEventArgs e) => AdjustMultiComputeLimit(1);
    private void MultiWorkerSub_Click(object? s, RoutedEventArgs e) => AdjustMultiComputeLimit(-1);

    private void AdjustMultiReadLimit(int delta)
    {
        if (_multiSearchConcurrency is { } runtime)
            MultiCurrentReaders.Text = runtime.AdjustReadLimit(delta).ToString();
        else
            MultiReadWorkers.Text = Math.Clamp(IntValue(MultiReadWorkers, 6) + delta, 1,
                SearchConcurrencyController.MaximumConcurrency).ToString();
    }

    private void AdjustMultiComputeLimit(int delta)
    {
        if (_multiSearchConcurrency is { } runtime)
            MultiCurrentWorkers.Text = runtime.AdjustComputeLimit(delta).ToString();
        else
            MultiComputeWorkers.Text = Math.Clamp(IntValue(MultiComputeWorkers, 16) + delta, 1,
                SearchConcurrencyController.MaximumConcurrency).ToString();
    }

    private async void MultiSearch_Click(object? s, RoutedEventArgs e) => await RunAsync("多文件搜索", async token =>
    {
        SetDisplay(MultiReadSpeed, "0");
        SetDisplay(MultiReadTotal, "0");
        SetDisplay(MultiComputeSpeed, "0");
        SetDisplay(MultiComputeTotal, "0");
        var files = Paths(MultiFiles).Select(Path.GetFullPath).Distinct(PathComparer).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("请导入待搜索文件。");
        // A new click is a new manual run. Automatic recovery remains inside this invocation,
        // while a manual stop deliberately discards the old run's checkpoints.
        _multiSearchIndexes.Clear();
        while (_completedMultiFiles.TryDequeue(out _)) { }
        var readWorkers = IntValue(MultiReadWorkers, 6);
        var computeWorkers = IntValue(MultiComputeWorkers, 16);
        var concurrency = new SearchConcurrencyController(readWorkers, computeWorkers);
        var ioController = new SearchIoController();
        _multiSearchConcurrency = concurrency;
        _multiSearchIoController = ioController;
        MultiCurrentReaders.Text = concurrency.ReadLimit.ToString();
        MultiCurrentWorkers.Text = concurrency.ComputeLimit.ToString();
        MultiQueue.Text = "0";
        using var runtimeTimer = new Timer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_multiSearchConcurrency, concurrency)) return;
            var state = concurrency.State;
            MultiQueue.Text = concurrency.TakeQueuedBlockPeak().ToString("N0");
            MultiCurrentReaders.Text = state.ReadLimit.ToString();
            MultiCurrentWorkers.Text = state.ComputeLimit.ToString();
        }, DispatcherPriority.Background), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        try
        {
            var keywordFile = MultiKeywordFileMode.IsChecked == true ? Optional(MultiKeywordFile.Text) : null;
            var protectedFiles = new HashSet<string>(files, PathComparer);
            if (keywordFile is not null) protectedFiles.Add(keywordFile);
            await ProtectSearchSourcesAsync(protectedFiles.ToArray(), false, token);
            var patterns = await KeywordsAsync(MultiKeywordFileMode.IsChecked == true, MultiKeywordText, MultiKeywordFile, token);
            if (patterns.Length == 0) throw new InvalidOperationException("关键字不能为空。");
            var includeFile = MultiIncludeFile.IsChecked == true;
            SystemLog.Information("SEARCH", $"多文件搜索参数：文件={files.Length:N0}；关键字={patterns.Length:N0}；" +
                $"模式={MultiSearchMode()}；读取线程={readWorkers}；计算线程={computeWorkers}；" +
                $"忽略大小写={MultiIgnoreCase.IsChecked == true}；读取停滞保护=30秒");
            var initialFileCount = files.Length;
            SearchSummary? summary = null;
            while (true)
            {
                FlushCompletedMultiFiles();
                files = Paths(MultiFiles).Select(Path.GetFullPath).Distinct(PathComparer).ToArray();
                if (files.Length == 0) break;
                var previouslyProtected = protectedFiles.Count;
                protectedFiles.UnionWith(files);
                if (protectedFiles.Count != previouslyProtected)
                    await ProtectSearchSourcesAsync(protectedFiles.ToArray(), false, token);
                var offsets = files.ToDictionary(file => file,
                    file => _multiSearchIndexes.TryGetValue(file, out var offset) ? Math.Max(0, offset) : 0,
                    PathComparer);
                var options = SearchOptionsFor(patterns, MultiIgnoreCase.IsChecked == true,
                    readWorkers, computeWorkers, concurrency, MultiSearchMode()) with
                {
                    StartOffsets = offsets,
                    ProcessedOffsetChanged = (file, offset) =>
                        _multiSearchIndexes[Path.GetFullPath(file)] = Math.Max(0, offset),
                    FileCompleted = RemoveCompletedMultiFileAsync,
                    IoController = ioController
                };
                var progress = ProgressFor("多文件搜索", MultiReadTotal, MultiReadSpeed,
                    MultiComputeTotal, MultiComputeSpeed);
                try
                {
                    summary = await Task.Run(() => new SearchEngine().SearchAsync(files, options,
                        batch => WriteSearchBatchAsync(batch,
                            m => includeFile ? $"{m.File}-->{m.Text}" : m.Text, token),
                        progress, token), token);
                    break;
                }
                catch (SearchIoInterruptedException ex)
                {
                    var failedFile = Path.GetFullPath(ex.FilePath);
                    var checkpoint = _multiSearchIndexes.TryGetValue(failedFile, out var offset)
                        ? Math.Max(0, offset) : 0;
                    var remaining = PathItems(MultiFiles).Count;
                    SystemLog.Warning("SEARCH",
                        $"多文件搜索因I/O异常暂停；文件={failedFile}；成功处理位置={checkpoint:N0}；" +
                        $"剩余文件={remaining:N0}。将自动等待磁盘恢复。", ex);
                    await WaitForFileRecoveryAsync(failedFile, "多文件搜索", token);
                }
            }
            return summary is null
                ? $"多文件搜索完成：{initialFileCount:N0} 个文件均已处理。"
                : $"多文件搜索完成：剩余批次 {summary.FilesScanned:N0} 个文件，{summary.LinesScanned:N0} 行，" +
                  $"命中 {summary.MatchCount:N0} 行，错误 {summary.Errors.Count}；总计完成 {initialFileCount:N0} 个文件。";
        }
        finally
        {
            Volatile.Write(ref _multiSearchSources, []);
            FlushCompletedMultiFiles();
            runtimeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (ReferenceEquals(_multiSearchConcurrency, concurrency)) _multiSearchConcurrency = null;
            if (ReferenceEquals(_multiSearchIoController, ioController)) _multiSearchIoController = null;
            MultiQueue.Text = "0";
            MultiCurrentReaders.Text = MultiCurrentWorkers.Text = "0";
            _multiSearchIndexes.Clear();
        }
    }, () => _multiCts, x => _multiCts = x);

    // 多文件清理合并
    private async void PickMultiCleanOutput_Click(object? s, RoutedEventArgs e) => MultiCleanOutput.Text = await PickSave("选择合并输出文件", "cleaned.txt");
    private async void PickMultiCleanDirectory_Click(object? s, RoutedEventArgs e) => MultiCleanDirectory.Text = await PickFolder("选择分组输出目录");
    private async void MultiCleanAddFiles_Click(object? s, RoutedEventArgs e) { AddPaths(MultiCleanFiles, (await PickFiles("导入清理文件")).Select(x => x.TryGetLocalPath()), MultiCleanDeduplicateImport.IsChecked == true); UpdateCount(MultiCleanFiles, MultiCleanFileCount); }
    private async void MultiCleanAddFolder_Click(object? s, RoutedEventArgs e) { await AddFolder(MultiCleanFiles, MultiCleanTypes, MultiCleanDeduplicateImport.IsChecked == true); UpdateCount(MultiCleanFiles, MultiCleanFileCount); }
    private async void MultiCleanLoadList_Click(object? s, RoutedEventArgs e) { await LoadList(MultiCleanFiles, MultiCleanDeduplicateImport.IsChecked == true); UpdateCount(MultiCleanFiles, MultiCleanFileCount); }
    private async void MultiCleanSaveList_Click(object? s, RoutedEventArgs e) => await SaveList(MultiCleanFiles);
    private void MultiCleanRemoveSelected_Click(object? s, RoutedEventArgs e) { RemoveSelected(MultiCleanFiles); UpdateCount(MultiCleanFiles, MultiCleanFileCount); }
    private void MultiCleanClear_Click(object? s, RoutedEventArgs e) { ClearPaths(MultiCleanFiles); UpdateCount(MultiCleanFiles, MultiCleanFileCount); }
    private void MultiCleanSafeStop_Click(object? s, RoutedEventArgs e) { AppendInfo("多文件清理将在原子文件安全点停止，未完成目标不会替换正式文件。"); _multiCleanCts?.Cancel(); }
    private void MultiCleanForceStop_Click(object? s, RoutedEventArgs e) => _multiCleanCts?.Cancel();

    private CleanOptions MultiCleanOptions() => MultiCleanEnabled.IsChecked != true ? new CleanOptions { Trim = false, RemoveBlankLines = false } : new CleanOptions
    {
        RemovePrefix = MultiCleanPrefixOn.IsChecked == true ? Optional(MultiCleanRemovePrefix.Text) : null,
        RemoveContaining = MultiCleanContainsOn.IsChecked == true ? Optional(MultiCleanRemoveContains.Text) : null,
        RemoveRegex = MultiCleanRegexOn.IsChecked == true ? Optional(MultiCleanRemoveRegex.Text) : null,
        RemovePrefixIgnoreCase = MultiCleanPrefixIgnoreCase.IsChecked == true,
        RemoveContainingIgnoreCase = MultiCleanContainsIgnoreCase.IsChecked == true,
        Trim = MultiCleanTrim.IsChecked == true, RemoveAllWhitespace = MultiCleanWhitespace.IsChecked == true,
        RemoveBlankLines = MultiCleanBlank.IsChecked == true,
        RemoveLinesAtMost = MultiCleanShortOn.IsChecked == true ? IntValue(MultiCleanShort, 5, 0) : null,
        FieldSeparator = MultiCleanFieldsOn.IsChecked == true && !string.IsNullOrEmpty(MultiCleanSeparator.Text) ? MultiCleanSeparator.Text[0] : null,
        RemoveFieldNumbers = ParseFields(MultiCleanFields.Text), GroupNonEmailSeparately = MultiCleanCheckEmail.IsChecked == true
    };

    private async void MultiClean_Click(object? s, RoutedEventArgs e) => await RunAsync("多文件清理合并", async token =>
    {
        var files = Paths(MultiCleanFiles); if (files.Length == 0) throw new InvalidOperationException("请导入待清理文件。");
        var engine = new CleanEngine(); var watch = Stopwatch.StartNew(); CleanSummary summary;
        if (MultiCleanGroupMode.IsChecked == true)
            summary = await engine.CleanToGroupsAsync(files, Required(MultiCleanDirectory.Text, "分组输出目录"), MultiCleanOptions(), MultiCleanGroupIndex.SelectedIndex,
                Optional(MultiCleanPrefix.Text) ?? "file_", progress: ProgressFor("多文件分组清理", MultiCleanReadTotal, MultiCleanReadSpeed), cancellationToken: token,
                removedLine: AppendResultAsync);
        else summary = await engine.CleanAsync(files, Required(MultiCleanOutput.Text, "合并输出文件"), MultiCleanOptions(), ProgressFor("多文件清理合并", MultiCleanReadTotal, MultiCleanReadSpeed), token,
            AppendResultAsync);
        SetDisplay(MultiCleanComputeSpeed, $"{summary.InputBytes / 1024d / 1024d / Math.Max(.001, watch.Elapsed.TotalSeconds):N1} MB/s");
        SetDisplay(MultiCleanWriteSpeed, $"{summary.OutputLines / Math.Max(.001, watch.Elapsed.TotalSeconds):N0} 行/s");
        return $"多文件清理完成：{summary.Files:N0} 文件，输入 {summary.InputLines:N0} 行，输出 {summary.OutputLines:N0} 行，删除 {summary.RemovedLines:N0} 行。";
    }, () => _multiCleanCts, x => _multiCleanCts = x);

    // 内容去重
    private async void PickDedupRoot_Click(object? s, RoutedEventArgs e) => DedupRoot.Text = await PickFolder("选择去重输出根目录");
    private async void DedupAddFiles_Click(object? s, RoutedEventArgs e) { AddPaths(DedupFiles, (await PickFiles("导入去重文件")).Select(x => x.TryGetLocalPath()), DedupDeduplicateImport.IsChecked == true); UpdateCount(DedupFiles, DedupFileCount); }
    private async void DedupAddFolder_Click(object? s, RoutedEventArgs e) { await AddFolder(DedupFiles, DedupTypes, DedupDeduplicateImport.IsChecked == true); UpdateCount(DedupFiles, DedupFileCount); }
    private async void DedupLoadList_Click(object? s, RoutedEventArgs e) { await LoadList(DedupFiles, DedupDeduplicateImport.IsChecked == true); UpdateCount(DedupFiles, DedupFileCount); }
    private async void DedupSaveList_Click(object? s, RoutedEventArgs e) => await SaveList(DedupFiles);
    private void DedupRemoveSelected_Click(object? s, RoutedEventArgs e) { RemoveSelected(DedupFiles); UpdateCount(DedupFiles, DedupFileCount); }
    private void DedupClear_Click(object? s, RoutedEventArgs e) { ClearPaths(DedupFiles); UpdateCount(DedupFiles, DedupFileCount); }
    private void DedupStop_Click(object? s, RoutedEventArgs e) => _dedupCts?.Cancel();

    private static string DedupDestination(string source, bool sameFolder, string suffix, string? outputRoot, string? configuredSourceRoot)
    {
        if (sameFolder)
            return Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + suffix + Path.GetExtension(source));
        var root = Required(outputRoot, "新输出目录");
        var sourceRoot = configuredSourceRoot ?? Path.GetPathRoot(Path.GetFullPath(source))!;
        var relative = Path.GetRelativePath(sourceRoot, source);
        if (relative.StartsWith("..")) relative = Path.GetFileName(source);
        var dir = Path.Combine(root, Path.GetDirectoryName(relative) ?? "");
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(relative) + suffix + Path.GetExtension(relative));
    }

    private async void Dedup_Click(object? s, RoutedEventArgs e) => await RunAsync("内容去重", async token =>
    {
        var files = Paths(DedupFiles); if (files.Length == 0) throw new InvalidOperationException("请导入待去重文件。");
        var threadCount = IntValue(DedupThreads, 16); var sameFolder = DedupSameFolder.IsChecked == true;
        var suffix = Optional(DedupSuffix.Text) ?? "_distinct"; var outputRoot = Optional(DedupRoot.Text); var sourceRoot = Optional(DedupSourceRoot.Text);
        var mode = DedupSort.IsChecked == true ? DedupMode.SortedExternal : DedupMode.StableInMemory;
        var trim = DedupTrim.IsChecked == true; var ignoreCase = DedupIgnoreCase.IsChecked == true;
        var budget = LongValue(DedupMemory, 4096, 4) * 1024 * 1024 / Math.Max(1, threadCount);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = threadCount, CancellationToken = token };
        long input = 0, output = 0, duplicate = 0;
        await Parallel.ForEachAsync(files, parallel, async (file, ct) =>
        {
            var destination = DedupDestination(file, sameFolder, suffix, outputRoot, sourceRoot); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var summary = await new DedupEngine().DeduplicateAsync([file], destination, new DedupOptions
            {
                Mode = mode, Trim = trim, IgnoreCase = ignoreCase, MemoryBudgetBytes = budget
            }, cancellationToken: ct);
            Interlocked.Add(ref input, summary.InputLines); Interlocked.Add(ref output, summary.OutputLines); Interlocked.Add(ref duplicate, summary.DuplicateLines);
            AppendInfo($"去重完成：{file} -> {destination}");
        });
        return $"内容去重完成：输入 {input:N0} 行，输出 {output:N0} 行，重复 {duplicate:N0} 行。";
    }, () => _dedupCts, x => _dedupCts = x);

    // 文件合并
    private async void PickMergeGroupSource_Click(object? s, RoutedEventArgs e) => MergeGroupSource.Text = await PickFolder("选择同组合并源目录");
    private async void PickMergeGroupTarget_Click(object? s, RoutedEventArgs e) => MergeGroupTarget.Text = await PickFolder("选择同组合并目标目录");
    private async void PickMergeOutput_Click(object? s, RoutedEventArgs e) => MergeOutput.Text = await PickSave("选择合并目标文件", "merged.txt");
    private async void MergeAddFiles_Click(object? s, RoutedEventArgs e) => AddPaths(MergeFiles, (await PickFiles("按顺序导入合并文件")).Select(x => x.TryGetLocalPath()), false);
    private async void MergeLoadList_Click(object? s, RoutedEventArgs e) => await LoadList(MergeFiles, false);
    private async void MergeSaveList_Click(object? s, RoutedEventArgs e) => await SaveList(MergeFiles);
    private void MergeRemoveSelected_Click(object? s, RoutedEventArgs e) => RemoveSelected(MergeFiles);
    private void MergeClear_Click(object? s, RoutedEventArgs e) => ClearPaths(MergeFiles);
    private void MergeMoveUp_Click(object? s, RoutedEventArgs e) => MoveSelectedLine(MergeFiles, -1);
    private void MergeMoveDown_Click(object? s, RoutedEventArgs e) => MoveSelectedLine(MergeFiles, 1);

    private static void MoveSelectedLine(ListBox box, int direction)
    {
        var items = PathItems(box); if (items.Count < 2) return;
        var index = box.SelectedIndex;
        var other = index + direction; if (index < 0 || index >= items.Count || other < 0 || other >= items.Count) return;
        items.Move(index, other);
        box.SelectedIndex = other;
    }

    private async void Merge_Click(object? s, RoutedEventArgs e) => await RunAsync("文件合并", async token =>
    {
        var files = Paths(MergeFiles); if (files.Length == 0) throw new InvalidOperationException("请导入待合并文件。");
        await new MergeSplitEngine().MergeAsync(files, Required(MergeOutput.Text, "合并目标文件"), MergeNewline.IsChecked == true, token);
        return $"文件合并完成：{files.Length:N0} 个文件。";
    }, () => _multiCleanCts, x => _multiCleanCts = x);

    private async void GroupMerge_Click(object? s, RoutedEventArgs e) => await RunAsync("同组文件合并", async token =>
    {
        var changed = await new MergeSplitEngine().GroupMergeAsync(Required(MergeGroupSource.Text, "源目录"), Required(MergeGroupTarget.Text, "目标目录"),
            Optional(MergeSourcePrefix.Text), Optional(MergeSourceSuffix.Text), Optional(MergeTargetPrefix.Text), Optional(MergeTargetSuffix.Text), token);
        return $"同组文件合并完成：更新 {changed.Count:N0} 个目标文件。";
    }, () => _multiCleanCts, x => _multiCleanCts = x);

    // 文件拆分
    private async void PickSplitDirectory_Click(object? s, RoutedEventArgs e) => SplitDirectory.Text = await PickFolder("选择拆分输出目录");
    private async void SplitAddFiles_Click(object? s, RoutedEventArgs e) => AddPaths(SplitFiles, (await PickFiles("导入待拆分文件")).Select(x => x.TryGetLocalPath()), false);
    private async void SplitAddFolder_Click(object? s, RoutedEventArgs e) => await AddFolder(SplitFiles, SplitTypes);
    private async void SplitPreFind_Click(object? s, RoutedEventArgs e) { ClearPaths(SplitFiles); await AddFolder(SplitFiles, SplitTypes, minimumBytes: LongValue(SplitThreshold, 1024) * 1024 * 1024); }
    private async void SplitLoadList_Click(object? s, RoutedEventArgs e) => await LoadList(SplitFiles, false);
    private async void SplitSaveList_Click(object? s, RoutedEventArgs e) => await SaveList(SplitFiles);
    private void SplitRemoveSelected_Click(object? s, RoutedEventArgs e) => RemoveSelected(SplitFiles);
    private void SplitClear_Click(object? s, RoutedEventArgs e) => ClearPaths(SplitFiles);
    private void SplitStop_Click(object? s, RoutedEventArgs e) => _splitCts?.Cancel();

    private async void Split_Click(object? s, RoutedEventArgs e) => await RunAsync("文件拆分", async token =>
    {
        var files = Paths(SplitFiles); if (files.Length == 0) throw new InvalidOperationException("请导入待拆分文件。");
        var count = 0;
        foreach (var file in files)
        {
            var dir = SplitSameFolder.IsChecked == true ? Path.GetDirectoryName(file)! : Required(SplitDirectory.Text, "拆分输出目录");
            var options = new SplitOptions { OutputDirectory = dir,
                LinesPerFile = SplitByLines.IsChecked == true ? LongValue(SplitLines, 1_000_000, 1) : null,
                SoftBytesPerFile = SplitBySize.IsChecked == true ? LongValue(SplitMb, 2048, 1) * 1024 * 1024 : null };
            count += (await new MergeSplitEngine().SplitAsync(file, options, token: token)).Count;
        }
        return $"文件拆分完成：生成 {count:N0} 个文件。";
    }, () => _splitCts, x => _splitCts = x);

    // 查找文件与复制移动
    private async void PickFindRoot_Click(object? s, RoutedEventArgs e) => FindRoot.Text = await PickFolder("选择查找根目录");
    private async void PickFindDestination_Click(object? s, RoutedEventArgs e) => FindDestination.Text = await PickFolder("选择复制/移动目标目录");
    private async void FindLoadList_Click(object? s, RoutedEventArgs e)
    {
        await LoadList(FindFiles, FindDeduplicateImport.IsChecked == true);
        UpdateCount(FindFiles, FindFileCount);
    }
    private async void FindSaveList_Click(object? s, RoutedEventArgs e) => await SaveList(FindFiles);
    private void FindRemoveSelected_Click(object? s, RoutedEventArgs e) { RemoveSelected(FindFiles); UpdateCount(FindFiles, FindFileCount); }
    private void FindClear_Click(object? s, RoutedEventArgs e) { ClearPaths(FindFiles); UpdateCount(FindFiles, FindFileCount); }
    private void FindStop_Click(object? s, RoutedEventArgs e) => _findCts?.Cancel();
    private async void Find_Click(object? s, RoutedEventArgs e) => await RunAsync("查找文件", async token =>
    {
        var options = new FileDiscoveryOptions
        {
            Root = Required(FindRoot.Text, "查找根目录"),
            Includes = FindIncludeMode.IsChecked == true ? Pipes(FindIncludes.Text, "*") : ["*"],
            Excludes = FindExcludeMode.IsChecked == true ? Pipes(FindExcludes.Text) : [],
            Recursive = FindRecursive.IsChecked == true, IncludeExtensionless = FindExtensionless.IsChecked == true,
            MinimumBytes = long.TryParse(FindMin.Text, out var min) ? min * 1024 * 1024 : null,
            MaximumBytes = long.TryParse(FindMax.Text, out var max) ? max * 1024 * 1024 : null
        };
        var found = await Task.Run(() => FileOperations.Discover(options, token), token);
        AddPaths(FindFiles, found, FindDeduplicateImport.IsChecked == true, replace: true);
        UpdateCount(FindFiles, FindFileCount);
        return $"查找完成：找到 {found.Count:N0} 个文件。";
    }, () => _findCts, x => _findCts = x);

    private void FindToSearch_Click(object? s, RoutedEventArgs e) { AddPaths(MultiFiles, Paths(FindFiles), replace: true); UpdateMultiFileList(); MainTabs.SelectedIndex = 1; }
    private void FindToClean_Click(object? s, RoutedEventArgs e) { AddPaths(MultiCleanFiles, Paths(FindFiles), replace: true); UpdateCount(MultiCleanFiles, MultiCleanFileCount); MainTabs.SelectedIndex = 2; }
    private void FindToDedup_Click(object? s, RoutedEventArgs e) { AddPaths(DedupFiles, Paths(FindFiles), replace: true); UpdateCount(DedupFiles, DedupFileCount); MainTabs.SelectedIndex = 3; }
    private void FindToMerge_Click(object? s, RoutedEventArgs e) { AddPaths(MergeFiles, Paths(FindFiles), deduplicate: false, replace: true); MainTabs.SelectedIndex = 4; }
    private void FindToSplit_Click(object? s, RoutedEventArgs e) { AddPaths(SplitFiles, Paths(FindFiles), deduplicate: false, replace: true); MainTabs.SelectedIndex = 5; }
    private async void FindCopy_Click(object? s, RoutedEventArgs e) => await CopyMoveFound(false);
    private async void FindMove_Click(object? s, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("确认移动", "移动会删除源文件。确认按原目录结构移动当前清单中的文件吗？")) return;
        await CopyMoveFound(true);
    }

    private async Task CopyMoveFound(bool move) => await RunAsync(move ? "移动文件" : "复制文件", async token =>
    {
        await FileOperations.CopyOrMoveAsync(Paths(FindFiles), Required(FindRoot.Text, "源根目录"), Required(FindDestination.Text, "目标目录"), move, false, token: token);
        return move ? "文件移动完成。" : "文件复制完成。";
    }, () => _findCts, x => _findCts = x);

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = false;
        var dialog = new Window { Title = title, Width = 430, Height = 155, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "确认", Width = 90 }; var cancel = new Button { Content = "取消", Width = 90 };
        ok.Click += (_, _) => { yes = true; dialog.Close(); }; cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 16, Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } } } };
        await dialog.ShowDialog(this); return yes;
    }

    // 性能测试
    private async void PickBenchmarkFile_Click(object? s, RoutedEventArgs e) => BenchmarkFile.Text = (await PickFiles("选择性能测试文件")).FirstOrDefault()?.TryGetLocalPath();
    private void BenchmarkStop_Click(object? s, RoutedEventArgs e) => _benchmarkCts?.Cancel();
    private async void BenchmarkLineBlock_Click(object? s, RoutedEventArgs e) => await RunBenchmark("行块模式");
    private async void BenchmarkTextBlock_Click(object? s, RoutedEventArgs e) => await RunBenchmark("字符块模式");
    private async void BenchmarkLine_Click(object? s, RoutedEventArgs e) => await RunBenchmark("单行模式");

    private async Task RunBenchmark(string mode) => await RunAsync("性能测试", async token =>
    {
        var file = Required(BenchmarkFile.Text, "测试文件"); var keyword = Required(BenchmarkKeyword.Text, "关键字");
        var processingMode = mode.StartsWith("行块", StringComparison.Ordinal) ? SearchProcessingMode.LineBlock
            : mode.StartsWith("单行", StringComparison.Ordinal) ? SearchProcessingMode.SingleLine
            : SearchProcessingMode.CharacterBlock;
        var options = SearchOptionsFor([keyword], true, 1, IntValue(BenchmarkWorkers, 16), mode: processingMode);
        var progress = ProgressFor("性能测试-" + mode, BenchmarkReadTotal, BenchmarkReadSpeed);
        var sw = Stopwatch.StartNew();
        var summary = await Task.Run(() => new SearchEngine().SearchAsync([file], options, null, progress, token), token);
        BenchmarkComputeSpeed.Text = $"{summary.BytesScanned / 1024d / 1024d / Math.Max(.001, sw.Elapsed.TotalSeconds):N1} MB/s";
        BenchmarkMatches.Text = summary.MatchCount.ToString("N0");
        var line = $"{mode}：{summary.BytesScanned / 1024d / 1024d:N1} MB，{summary.LinesScanned:N0} 行，{summary.MatchCount:N0} 命中，{sw.Elapsed.TotalSeconds:N3}s";
        BenchmarkLog.Text += line + Environment.NewLine; return line;
    }, () => _benchmarkCts, x => _benchmarkCts = x);

    private void BenchmarkHash_Click(object? s, RoutedEventArgs e) => BenchmarkSet(false);
    private void BenchmarkSorted_Click(object? s, RoutedEventArgs e) => BenchmarkSet(true);
    private void BenchmarkSet(bool sorted)
    {
        var count = Math.Clamp(LongValue(BenchmarkSetCount, 1_000_000, 1), 1, 20_000_000);
        var sw = Stopwatch.StartNew();
        if (sorted) { var set = new SortedSet<long>(); for (long i = 0; i < count; i++) set.Add((i * 2654435761L) % count); }
        else { var set = new HashSet<long>(); for (long i = 0; i < count; i++) set.Add((i * 2654435761L) % count); }
        sw.Stop(); BenchmarkSetResult.Text = $"{(sorted ? "SortedSet" : "HashSet")}：{count:N0} 项，{sw.Elapsed.TotalSeconds:N3} 秒";
    }

    // 全局输出
    private async void OutputResultsToFile_Changed(object? s, RoutedEventArgs e)
    {
        var enabled = OutputResultsToFile.IsChecked == true;
        Volatile.Write(ref _writeResultsToFile, enabled);
        if (!enabled) await CloseResultFileAsync();
    }
    private async void OutputFile_Changing(object? s, TextChangingEventArgs e)
    {
        Volatile.Write(ref _resultFilePath, Optional(((TextBox)s!).Text));
        await CloseResultFileAsync();
    }
    private async void PickOutputFile_Click(object? s, RoutedEventArgs e) => OutputFile.Text = await PickSave("选择计算结果输出文件", "results.txt");
    private async void SaveOutput_Click(object? s, RoutedEventArgs e)
    {
        var path = Optional(OutputFile.Text) ?? await PickSave("保存当前输出", "fastgrep-output.txt");
        if (path is null) return;
        await _outputFileLock.WaitAsync();
        try
        {
            FileOperations.EnsureSeparateOutput(ActiveSearchSources(), path);
            await DisposeResultFileWriterAsync();
            await File.WriteAllTextAsync(path, OutputText.Text ?? "", new UTF8Encoding(false));
            AppendInfo("当前输出已保存。");
        }
        catch (Exception ex) { ReportUiOperationFailure("保存当前输出", ex); }
        finally { _outputFileLock.Release(); }
    }
    private void ClearOutput_Click(object? s, RoutedEventArgs e)
    {
        lock (_pendingUiResults)
        {
            _pendingUiResults.Clear();
            _pendingUiChars = 0;
        }
        OutputText.Clear();
    }
}
