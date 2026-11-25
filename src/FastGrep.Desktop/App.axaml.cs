using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace FastGrep.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RuntimeRecovery.InstallDispatcherHandler();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            MacDockIcon.TryApply(Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.png"));

            if (Program.HasArgument("--recovery-smoke-test"))
            {
                mainWindow.Opened += (_, _) =>
                {
                    Dispatcher.UIThread.Post(() => throw new IOException("FastGrep UI recovery smoke test"));
                    DispatcherTimer.RunOnce(() =>
                    {
                        Console.WriteLine("RECOVERY_SMOKE_TEST_OK");
                        desktop.Shutdown(0);
                    }, TimeSpan.FromMilliseconds(500));
                };
            }
            else if (Program.HasArgument("--compact-input-layout-smoke-test"))
            {
                mainWindow.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    var result = mainWindow.RunCompactInputLayoutSmokeTest();
                    Console.WriteLine((result.Passed ? "COMPACT_INPUT_LAYOUT_SMOKE_TEST_OK " :
                        "COMPACT_INPUT_LAYOUT_SMOKE_TEST_FAILED ") + result.Details);
                    desktop.Shutdown(result.Passed ? 0 : 1);
                }, TimeSpan.FromMilliseconds(250));
            }
            else if (Program.HasArgument("--file-list-smoke-test"))
            {
                mainWindow.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    var passed = mainWindow.RunFileListSmokeTest();
                    Console.WriteLine(passed ? "FILE_LIST_SMOKE_TEST_OK" : "FILE_LIST_SMOKE_TEST_FAILED");
                    desktop.Shutdown(passed ? 0 : 1);
                }, TimeSpan.FromMilliseconds(250));
            }
            else if (Program.HasArgument("--output-smoke-test"))
            {
                mainWindow.Opened += (_, _) => DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        await mainWindow.RunOutputSmokeTestAsync();
                        Console.WriteLine("OUTPUT_SMOKE_TEST_OK");
                        desktop.Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("OUTPUT_SMOKE_TEST_FAILED " + ex);
                        desktop.Shutdown(1);
                    }
                }, TimeSpan.FromMilliseconds(250));
            }
            else if (Program.HasArgument("--clipboard-smoke-test"))
            {
                mainWindow.Opened += (_, _) =>
                {
                    mainWindow.RunClipboardSmokeTest();
                    DispatcherTimer.RunOnce(() =>
                    {
                        Console.WriteLine("CLIPBOARD_SMOKE_TEST_OK");
                        desktop.Shutdown(0);
                    }, TimeSpan.FromSeconds(1));
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
