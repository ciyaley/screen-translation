using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenTranslate.App;

internal static class Program
{
    private const string MutexName = @"Local\ScreenTranslate_SingleInstance_Mutex";
    private const string ActivateEventName = @"Local\ScreenTranslate_Activate_Event";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--uninstall"))
        {
            DesktopInstaller.UninstallQuietly();
            return 0;
        }

        if (args.Contains("--install"))
        {
            var (ok, _) = DesktopInstaller.InstallAndCreateShortcuts();
            return ok ? 0 : 1;
        }

        bool selfTestUi = args.Contains("--self-test-ui");
        bool selfTestDaemon = args.Contains("--self-test-daemon");
        bool isSelfTest = selfTestUi || selfTestDaemon;

        Mutex? instanceMutex = null;
        EventWaitHandle? activateEvent = null;

        if (!isSelfTest)
        {
            instanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // 既に起動中のインスタンスがある場合はウィンドウを前面へ呼び出して即終了
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                    evt.Set();
                }
                catch { }
                instanceMutex.Dispose();
                return 0;
            }

            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }

        var (runtimeDir, projectRoot) = ResolveAppDataAndProjectRoot();
        Directory.CreateDirectory(runtimeDir);

        string cmdPath = Path.Combine(runtimeDir, "control.cmd");
        string pidPath = Path.Combine(runtimeDir, "pid");
        try { File.WriteAllText(cmdPath, string.Empty, Encoding.UTF8); } catch { }
        try { File.WriteAllText(pidPath, $"{Environment.ProcessId}\n", Encoding.UTF8); } catch { }

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        var mainWin = new MainTranslationWindow(runtimeDir, projectRoot, isSelfTest);
        app.MainWindow = mainWin;

        if (activateEvent is not null)
        {
            var bgWaitThread = new Thread(() =>
            {
                try
                {
                    while (activateEvent.WaitOne())
                    {
                        mainWin.Dispatcher.InvokeAsync(() =>
                        {
                            if (!mainWin.IsVisible) mainWin.Show();
                            IntPtr hwnd = new WindowInteropHelper(mainWin).Handle;
                            if (hwnd != IntPtr.Zero)
                            {
                                _ = Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);
                                _ = Win32Interop.SetForegroundWindow(hwnd);
                            }
                            mainWin.Activate();
                        });
                    }
                }
                catch { }
            })
            {
                IsBackground = true,
                Name = "ScreenTranslate-SingleInstanceListener"
            };
            bgWaitThread.Start();
        }

        var controlTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            (_, _) =>
            {
                try
                {
                    if (!File.Exists(cmdPath)) return;
                    string txt = File.ReadAllText(cmdPath, Encoding.UTF8).Trim();
                    if (txt is "quit" or "close")
                    {
                        try { File.WriteAllText(cmdPath, string.Empty, Encoding.UTF8); } catch { }
                        mainWin.PerformCleanShutdown(0);
                    }
                }
                catch { }
            },
            Dispatcher.CurrentDispatcher);
        controlTimer.Start();

        if (selfTestUi)
        {
            mainWin.Loaded += (_, _) =>
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(mainWin.RunAutomatedSelfTest));
            };
        }

        if (!selfTestDaemon)
        {
            mainWin.Show();
        }

        try
        {
            return app.Run();
        }
        finally
        {
            activateEvent?.Dispose();
            if (instanceMutex is not null)
            {
                try { instanceMutex.ReleaseMutex(); } catch { }
                instanceMutex.Dispose();
            }
        }
    }

    private static (string RuntimeDir, string? ProjectRoot) ResolveAppDataAndProjectRoot()
    {
        string? projectRoot = null;
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ScreenTranslate.sln")))
                {
                    projectRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }
        }
        catch { }

        string? envRuntime = Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(envRuntime))
            return (envRuntime, projectRoot);

        // 通常のWindowsデスクトップアプリと同様に %LOCALAPPDATA%\ScreenTranslate へ設定・モデルを保存
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            return (Path.Combine(localAppData, "ScreenTranslate"), projectRoot);

        return (Path.Combine(Path.GetTempPath(), "ScreenTranslate"), projectRoot);
    }
}
