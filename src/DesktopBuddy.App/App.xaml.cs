using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DesktopBuddy.Services;

namespace DesktopBuddy;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var profileAt=Array.IndexOf(e.Args,"--acceptance-profile");
        string? acceptanceProfile=null;
        if(profileAt>=0)
        {
            // Validate before handlers/uninstall processing so a diagnostic failure
            // never logs to, restores, or removes the real user's profile.
            if(e.Args.Any(x=>string.Equals(x,"--uninstall-remove-user-data",StringComparison.OrdinalIgnoreCase))||
                profileAt+1>=e.Args.Length||!Path.IsPathFullyQualified(e.Args[profileAt+1])||e.Args[profileAt+1].StartsWith(@"\\",StringComparison.Ordinal))
            {Shutdown(2);return;}
            try{acceptanceProfile=Path.GetFullPath(e.Args[profileAt+1]);CrashLogger.SetAcceptanceLogDirectory(Path.Combine(acceptanceProfile,"logs"));}
            catch{Shutdown(2);return;}
        }
        RegisterExceptionHandlers();
        base.OnStartup(e);
        if (e.Args.Any(x => string.Equals(x, "--uninstall-remove-user-data", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                CredentialStore.Delete();
                var dataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBuddy");
                if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法完整移除桌伴用户数据：{ex.Message}", "浅梦 · 卸载", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        _singleInstanceMutex = new Mutex(true, "DesktopBuddy.SingleInstance", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            if(acceptanceProfile is not null){Shutdown(2);return;}
            var existingWindow = NativeMethods.FindPetWindow();
            if (existingWindow != 0)
                NativeMethods.PostMessage(existingWindow, NativeMethods.WmAppRestore, 0, 0);
            Shutdown();
            return;
        }

        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(new nint(-4)); // PER_MONITOR_AWARE_V2
            MainWindow window;
            if(acceptanceProfile is not null)
            {
                window=new MainWindow(new LocalStore(acceptanceProfile),_=>{},new ChatService(()=>null));
            }
            else window=new MainWindow();
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--diagnose-startup"))
            {
                var diagnostics = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                diagnostics.Tick += (_, _) =>
                {
                    diagnostics.Stop();
                    try
                    {
                        var logs = Path.Combine(window.Store.DataDirectory, "logs"); Directory.CreateDirectory(logs);
                        File.WriteAllText(Path.Combine(logs, "startup-state.json"), System.Text.Json.JsonSerializer.Serialize(window.StartupDiagnostics()));
                    }
                    catch { }
                };
                diagnostics.Start();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Write("startup", ex);
            System.Windows.MessageBox.Show($"浅梦无法完成启动：{ex.Message}", "浅梦 · 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                CrashLogger.Write("appdomain", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogger.WriteThrottled("unobserved-task", args.Exception);
            args.SetObserved();
        };
    }

    private bool _handlingDispatcherException;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Write("dispatcher", e.Exception);
        e.Handled = true;
        if (_handlingDispatcherException) return;
        _handlingDispatcherException = true;

        try
        {
            System.Windows.MessageBox.Show(
                "浅梦遇到未预期错误，将安全退出。诊断日志（不含异常消息、前台应用名或聊天内容）保存在 %LOCALAPPDATA%\\DesktopBuddy\\logs。",
                "浅梦 · 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* Continue to shutdown if the dialog cannot be displayed. */ }

        try
        {
            if (MainWindow is MainWindow window) window.ShutdownAfterFatalError();
            else Shutdown(1);
        }
        catch
        {
            Environment.Exit(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
