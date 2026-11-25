using Avalonia;
using FastGrep.Core;
using System.Text;

namespace FastGrep.Desktop;

class Program
{
    private static string[] _arguments = [];

    internal static bool HasArgument(string value) =>
        _arguments.Contains(value, StringComparer.OrdinalIgnoreCase);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        _arguments = args;
        SystemLog.Initialize();
        RuntimeRecovery.InstallProcessHandlers();

        try
        {
            using var ole = WindowsOleScope.Initialize();
            if (args.Contains("--system-log-smoke-test", StringComparer.OrdinalIgnoreCase))
                return RunSystemLogSmokeTestAsync().GetAwaiter().GetResult();
            if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
                return RunSmokeTestAsync().GetAwaiter().GetResult();

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            RuntimeRecovery.Record("桌面程序主循环终止", ex);
            return 1;
        }
        finally
        {
            SystemLog.Shutdown();
        }
    }

    private static async Task<int> RunSystemLogSmokeTestAsync()
    {
        var marker = "SYSTEM_LOG_SMOKE_" + Guid.NewGuid().ToString("N");
        SystemLog.Information("TEST", marker + "_INFO");
        SystemLog.Warning("TEST", marker + "_WARN");
        SystemLog.Error("TEST", marker + "_ERROR", new IOException("system log smoke exception"));
        var enqueueWatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
            SystemLog.Debug("STRESS", $"{marker}_BULK_{i:D5}");
        enqueueWatch.Stop();
        await SystemLog.FlushAsync();
        var path = SystemLog.CurrentLogPath;
        var content = path is not null && File.Exists(path) ? await ReadSharedTextAsync(path) : string.Empty;
        if (path is null || !content.Contains(marker + "_INFO", StringComparison.Ordinal) ||
            !content.Contains(marker + "_BULK_09999", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("SYSTEM_LOG_SMOKE_TEST_FAILED");
            return 1;
        }
        Console.WriteLine($"SYSTEM_LOG_SMOKE_TEST_OK entries=10003 enqueue_ms={enqueueWatch.Elapsed.TotalMilliseconds:N1} path={path}");
        return 0;
    }

    private static async Task<string> ReadSharedTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024);
        return await reader.ReadToEndAsync();
    }

    private static async Task<int> RunSmokeTestAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fastgrep-desktop-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 12; i++)
                await File.WriteAllTextAsync(Path.Combine(root, $"input-{i:D2}.txt"),
                    $"header {i}\nneedle-{i}\ntail {i}\n", new UTF8Encoding(false));

            var files = FileOperations.Discover(new FileDiscoveryOptions
            {
                Root = root,
                Includes = ["*.txt"],
                Recursive = true
            });
            var output = Path.Combine(root, "matches.out");
            var summary = await new SearchEngine().SearchAsync(files, new SearchOptions
            {
                Patterns = ["needle-"],
                IgnoreCase = false,
                DegreeOfParallelism = 6,
                MaxConcurrentReadsPerVolume = 6,
                ReadBufferSize = 1024 * 1024,
                OutputPath = output
            });
            var outputLines = await File.ReadAllLinesAsync(output);
            if (summary.FilesScanned != 12 || summary.MatchCount != 12 || summary.Errors.Count != 0 || outputLines.Length != 12)
            {
                Console.Error.WriteLine($"SMOKE_TEST_FAILED files={summary.FilesScanned} matches={summary.MatchCount} errors={summary.Errors.Count} output={outputLines.Length}");
                return 1;
            }

            Console.WriteLine($"SMOKE_TEST_OK files={summary.FilesScanned} lines={summary.LinesScanned} matches={summary.MatchCount}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SMOKE_TEST_FAILED " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
