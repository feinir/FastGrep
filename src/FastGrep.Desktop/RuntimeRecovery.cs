using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace FastGrep.Desktop;

internal static class RuntimeRecovery
{
    private static int _processHandlersInstalled;
    private static int _dispatcherHandlerInstalled;
    private static Action<string>? _uiReporter;

    internal static void InstallProcessHandlers()
    {
        if (Interlocked.Exchange(ref _processHandlersInstalled, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Record("进程发生不可恢复异常", args.ExceptionObject as Exception ??
                new InvalidOperationException(args.ExceptionObject?.ToString() ?? "未知异常"));
            SystemLog.FlushForCrash();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record("已隔离未观察的后台任务异常", args.Exception);
            args.SetObserved();
            NotifyUi("后台任务发生异常，程序已隔离该错误并继续运行。详情已写入日志。");
        };
    }

    internal static void InstallDispatcherHandler()
    {
        if (Interlocked.Exchange(ref _dispatcherHandlerInstalled, 1) != 0) return;

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            var exception = Unwrap(args.Exception);
            Record("已恢复 UI 异常", exception);
            if (IsFatal(exception)) return;

            args.Handled = true;
            NotifyUi($"界面操作发生错误，程序已恢复并继续运行：{exception.Message}；详情已写入日志。");
        };
    }

    internal static void SetUiReporter(Action<string>? reporter) =>
        Volatile.Write(ref _uiReporter, reporter);

    internal static void Record(string source, Exception exception)
        => SystemLog.Error("RECOVERY", source, exception);

    private static void NotifyUi(string message)
    {
        try
        {
            var reporter = Volatile.Read(ref _uiReporter);
            if (reporter is null) return;
            if (Dispatcher.UIThread.CheckAccess()) reporter(message);
            else Dispatcher.UIThread.Post(() =>
            {
                try { Volatile.Read(ref _uiReporter)?.Invoke(message); }
                catch (Exception ex) { Record("异常提示写入界面失败", ex); }
            });
        }
        catch (Exception ex)
        {
            Record("异常提示调度失败", ex);
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];
        return exception;
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or
        BadImageFormatException or InvalidProgramException;
}

internal sealed class WindowsOleScope : IDisposable
{
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private readonly bool _initialized;

    private WindowsOleScope(bool initialized) => _initialized = initialized;

    internal static WindowsOleScope Initialize()
    {
        if (!OperatingSystem.IsWindows()) return new WindowsOleScope(false);

        var result = OleInitialize(IntPtr.Zero);
        if (result >= 0) return new WindowsOleScope(true);

        var exception = Marshal.GetExceptionForHR(result) ??
            new COMException($"OleInitialize 失败：0x{result:X8}", result);
        RuntimeRecovery.Record("Windows OLE 初始化失败", exception);
        if (result != RpcEChangedMode) throw exception;
        return new WindowsOleScope(false);
    }

    public void Dispose()
    {
        if (_initialized) OleUninitialize();
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
}
