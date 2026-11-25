using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace FastGrep.Desktop;

internal enum SystemLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

internal static class SystemLog
{
    private const int FlushIntervalSeconds = 2;
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const int RetentionDays = 30;
    private const int MaximumQueuedChars = 4 * 1024 * 1024;
    private const int MaximumMessageChars = 8 * 1024;
    private const int MaximumBatchEntries = 512;
    private static readonly Channel<LogEntry> Queue = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(16 * 1024) { SingleReader = true, SingleWriter = false });
    private static readonly TaskCompletionSource StopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task? _writerTask;
    private static int _initialized;
    private static int _stopping;
    private static string? _currentLogPath;
    private static int _queuedChars;
    private static long _droppedEntries;

    internal static string LogDirectory { get; } = ResolveLogDirectory();
    internal static string? CurrentLogPath => Volatile.Read(ref _currentLogPath);

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            DeleteExpiredLogs();
        }
        catch (Exception ex)
        {
            WriteFallback("系统日志初始化失败", ex);
        }
        _writerTask = Task.Run(WriterLoopAsync);
        Trace.Listeners.Add(new SystemLogTraceListener());
        Information("SYSTEM", $"FastGrep 启动。PID={Environment.ProcessId}；" +
            $"OS={RuntimeInformation.OSDescription}；Runtime={RuntimeInformation.FrameworkDescription}；" +
            $"进程架构={RuntimeInformation.ProcessArchitecture}；CPU={Environment.ProcessorCount}；" +
            $"程序目录={AppContext.BaseDirectory}；日志目录={LogDirectory}");
    }

    internal static void Debug(string category, string message) => Write(SystemLogLevel.Debug, category, message);
    internal static void Information(string category, string message) => Write(SystemLogLevel.Information, category, message);
    internal static void Warning(string category, string message, Exception? exception = null) =>
        Write(SystemLogLevel.Warning, category, message, exception);
    internal static void Error(string category, string message, Exception? exception = null) =>
        Write(SystemLogLevel.Error, category, message, exception);

    internal static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized) == 0 || Volatile.Read(ref _stopping) != 0) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Queue.Writer.WriteAsync(LogEntry.Barrier(completion), timeout.Token);
            await completion.Task.WaitAsync(timeout.Token);
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _stopping) != 0) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("系统日志刷新超时。");
        }
    }

    internal static void FlushForCrash()
    {
        try { FlushAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { WriteFallback("崩溃前刷新系统日志失败", ex); }
    }

    internal static void Shutdown()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        try
        {
            InformationCore("SYSTEM", "FastGrep 正常退出，正在刷新系统日志。", null);
            Queue.Writer.TryComplete();
            StopSignal.TrySetResult();
            _writerTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            WriteFallback("系统日志关闭失败", ex);
        }
    }

    private static void Write(SystemLogLevel level, string category, string message, Exception? exception = null)
    {
        if (Volatile.Read(ref _initialized) == 0 || Volatile.Read(ref _stopping) != 0) return;
        InformationCore(category, message, exception, level);
    }

    private static void InformationCore(string category, string message, Exception? exception,
        SystemLogLevel level = SystemLogLevel.Information)
    {
        try
        {
            message = Limit(message, MaximumMessageChars);
            category = Limit(category, 256);
            var detail = exception is null ? null : Limit(exception.ToString(), MaximumMessageChars);
            var chars = 128 + category.Length + message.Length + (detail?.Length ?? 0);
            if (Interlocked.Add(ref _queuedChars, chars) <= MaximumQueuedChars &&
                Queue.Writer.TryWrite(new LogEntry(DateTimeOffset.Now, level, category, message,
                    detail, Environment.CurrentManagedThreadId, null, chars))) return;
            Interlocked.Add(ref _queuedChars, -chars);
            Interlocked.Increment(ref _droppedEntries);
        }
        catch (Exception ex)
        {
            WriteFallback("系统日志入队失败", ex);
        }
    }

    private static string Limit(string text, int limit) => text.Length <= limit ? text : text[..(limit - 1)] + "…";

    private static async Task WriterLoopAsync()
    {
        StreamWriter? writer = null;
        var pending = new List<LogEntry>(MaximumBatchEntries);
        try
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(FlushIntervalSeconds)), StopSignal.Task);
            while (true)
            {
                while (pending.Count < MaximumBatchEntries && Queue.Reader.TryRead(out var entry)) pending.Add(entry);
                if (pending.Count == 0)
                {
                    if (Queue.Reader.Completion.IsCompleted) break;
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(FlushIntervalSeconds)), StopSignal.Task);
                    continue;
                }

                var dropped = Interlocked.Exchange(ref _droppedEntries, 0);
                try
                {
                    writer = await EnsureWriterAsync(writer, pending);
                    if (dropped > 0)
                        await writer.WriteLineAsync($"[{DateTimeOffset.Now:O}] [WARN ] [SYSTEM] 日志队列已满，丢弃 {dropped:N0} 条日志。");
                    await WriteBatchAsync(writer, pending);
                    foreach (var entry in pending)
                    {
                        Interlocked.Add(ref _queuedChars, -entry.Chars);
                        entry.FlushCompletion?.TrySetResult();
                    }
                    pending.Clear();
                }
                catch (Exception ex)
                {
                    Interlocked.Add(ref _droppedEntries, dropped);
                    WriteFallback("系统日志批量写入失败，将重试有界队列", ex);
                    if (writer is not null)
                    {
                        try { await writer.DisposeAsync(); } catch { }
                        writer = null;
                    }
                    if (Volatile.Read(ref _stopping) != 0) throw;
                    await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(500)), StopSignal.Task);
                }
            }
        }
        catch (Exception ex)
        {
            Queue.Writer.TryComplete(ex);
            foreach (var entry in pending) Discard(entry);
            while (Queue.Reader.TryRead(out var entry)) Discard(entry);
            WriteFallback("系统日志后台写入失败", ex);
            void Discard(LogEntry entry)
            {
                Interlocked.Add(ref _queuedChars, -entry.Chars);
                entry.FlushCompletion?.TrySetCanceled();
            }
        }
        finally
        {
            if (writer is not null)
            {
                try { await writer.FlushAsync(); await writer.DisposeAsync(); }
                catch (Exception ex) { WriteFallback("系统日志文件关闭失败", ex); }
            }
        }
    }

    private static async Task<StreamWriter> EnsureWriterAsync(StreamWriter? current, IReadOnlyList<LogEntry> batch)
    {
        var estimatedBytes = 0L;
        foreach (var entry in batch)
            estimatedBytes += entry.FlushCompletion is null
                ? 160L + entry.Message.Length * 3L + (entry.ExceptionText?.Length ?? 0) * 3L
                : 0;

        if (current is not null && CurrentLogPath is { } currentPath &&
            Path.GetFileName(currentPath).StartsWith($"fastgrep-{DateTime.Now:yyyyMMdd}", StringComparison.Ordinal) &&
            new FileInfo(currentPath).Length + estimatedBytes <= MaximumFileBytes)
            return current;

        if (current is not null)
        {
            await current.FlushAsync();
            await current.DisposeAsync();
        }

        Directory.CreateDirectory(LogDirectory);
        DeleteExpiredLogs();
        var path = SelectLogPath(estimatedBytes);
        Volatile.Write(ref _currentLogPath, path);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(false), 128 * 1024) { AutoFlush = false };
    }

    private static async Task WriteBatchAsync(StreamWriter writer, IReadOnlyList<LogEntry> batch)
    {
        var text = new StringBuilder(Math.Min(1024 * 1024, batch.Count * 160));
        foreach (var entry in batch)
        {
            if (entry.FlushCompletion is not null) continue;
            text.Append(Format(entry));
            if (text.Length < 1024 * 1024) continue;
            await writer.WriteAsync(text.ToString());
            text.Clear();
        }
        if (text.Length > 0) await writer.WriteAsync(text.ToString());
        await writer.FlushAsync();
    }

    private static string Format(LogEntry entry)
    {
        if (entry.FlushCompletion is not null) return string.Empty;
        var builder = new StringBuilder(256);
        builder.Append('[').Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append("] [").Append(LevelName(entry.Level)).Append("] [T")
            .Append(entry.ThreadId).Append("] [").Append(entry.Category).Append("] ")
            .AppendLine(entry.Message);
        if (entry.ExceptionText is not null) builder.AppendLine(entry.ExceptionText);
        return builder.ToString();
    }

    private static string LevelName(SystemLogLevel level) => level switch
    {
        SystemLogLevel.Debug => "DEBUG",
        SystemLogLevel.Information => "INFO ",
        SystemLogLevel.Warning => "WARN ",
        SystemLogLevel.Error => "ERROR",
        _ => "INFO "
    };

    private static string SelectLogPath(long incomingBytes)
    {
        var stem = $"fastgrep-{DateTime.Now:yyyyMMdd}";
        for (var index = 0; index < 10_000; index++)
        {
            var suffix = index == 0 ? string.Empty : $"-{index:D3}";
            var path = Path.Combine(LogDirectory, stem + suffix + ".log");
            if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaximumFileBytes)
                return path;
        }
        throw new IOException("系统日志滚动文件数量已超过限制。");
    }

    private static void DeleteExpiredLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var path in Directory.EnumerateFiles(LogDirectory, "fastgrep-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (Exception ex) { WriteFallback($"无法清理过期日志：{path}", ex); }
        }
    }

    private static string ResolveLogDirectory()
    {
        var executableDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (OperatingSystem.IsMacOS() &&
            string.Equals(executableDirectory.Name, "MacOS", StringComparison.Ordinal) &&
            string.Equals(executableDirectory.Parent?.Name, "Contents", StringComparison.Ordinal) &&
            executableDirectory.Parent?.Parent is { } appBundle &&
            appBundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
            appBundle.Parent is { } appDirectory)
            return Path.Combine(appDirectory.FullName, "log");

        return Path.Combine(executableDirectory.FullName, "log");
    }

    private static void WriteFallback(string message, Exception exception)
    {
        try { Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {message}: {exception}"); }
        catch { }
    }

    private sealed record LogEntry(DateTimeOffset Timestamp, SystemLogLevel Level, string Category,
        string Message, string? ExceptionText, int ThreadId, TaskCompletionSource? FlushCompletion, int Chars)
    {
        internal static LogEntry Barrier(TaskCompletionSource completion) =>
            new(DateTimeOffset.Now, SystemLogLevel.Debug, "FLUSH", string.Empty, null,
                Environment.CurrentManagedThreadId, completion, 0);
    }
}

internal sealed class SystemLogTraceListener : TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) SystemLog.Debug("TRACE", message);
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) SystemLog.Debug("TRACE", message);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
        int id, string? message)
    {
        var text = $"{source} ({id})：{message}";
        if (eventType is TraceEventType.Critical or TraceEventType.Error)
            SystemLog.Error("TRACE", text);
        else if (eventType is TraceEventType.Warning)
            SystemLog.Warning("TRACE", text);
        else if (eventType is TraceEventType.Information)
            SystemLog.Information("TRACE", text);
        else
            SystemLog.Debug("TRACE", text);
    }
}
