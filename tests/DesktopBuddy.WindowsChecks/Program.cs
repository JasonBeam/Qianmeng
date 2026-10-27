using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopBuddy;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static readonly List<Process> Fixtures = [];
    private static string _report = "";
    private static int _captureScale=1;
    private static string BrowserImage = "chrome.exe";
    private static string? PackageInstallerPath;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--model-worker", StringComparison.Ordinal))
        {
            var workerEntry = typeof(MainWindow).Assembly.GetType("DesktopBuddy.EntryPoint", throwOnError: true)!;
            var workerMain = workerEntry.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;
            return (int)(workerMain.Invoke(null, new object[] { args }) ?? 3);
        }
        NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
        var scaleAt=Array.IndexOf(args,"--capture-scale");
        if(scaleAt>=0&&scaleAt+1<args.Length)_captureScale=Math.Clamp(int.Parse(args[scaleAt+1]),1,4);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Contains("--pet-exit-fixture"))
        {
            var directory = Path.Combine(Path.GetTempPath(), "DesktopBuddy-Exit-" + Environment.ProcessId);
            var exitPet = new MainWindow(new LocalStore(directory), _ => { }, new ChatService(() => null))
            {
                Title = "DesktopBuddy pet exit fixture"
            };
            app.Exit += (_, _) => { try { Directory.Delete(directory, true); } catch { } };
            return app.Run(exitPet);
        }
        if (args.Contains("--fixture"))
        {
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var fixture = new Window
            {
                Title = "DesktopBuddy synthetic foreground fixture",
                Left = 40, Top = 40, Width = 320, Height = 180,
                Content = new TextBlock
                {
                    Text = "Synthetic test window\n" + Path.GetFileName(Environment.ProcessPath),
                    Margin = new Thickness(20), FontSize = 18
                }
            };
            if (args.Contains("--fullscreen"))
            {
                fixture.WindowStyle = WindowStyle.None;
                fixture.ResizeMode = ResizeMode.NoResize;
                fixture.WindowState = WindowState.Maximized;
                fixture.Topmost = true;
            }
            return app.Run(fixture);
        }
        var index = Array.IndexOf(args, "--report");
        _report = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "windows-checks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_report)!);
        app.Startup += async (_, _) =>
        {
            if (args.Contains("--face-lighting-pose-diagnostic")) { await RunFaceLightingPoseDiagnosticAsync(app,args); return; }
            if (args.Contains("--face-lighting-diagnostic")) { await RunFaceLightingDiagnosticAsync(app,args); return; }
            if (args.Contains("--face-pose-diagnostic")) { await RunFacePoseDiagnosticAsync(app,args); return; }
            if (args.Contains("--hand-surface-texture-diagnostic")) { await RunHandSurfaceTextureDiagnosticAsync(app,args); return; }
            if (args.Contains("--resource-diagnosis")) { await RunResourceDiagnosisAsync(app,args); return; }
            if (args.Contains("--appearance-checks")) { await RunAppearanceChecksAsync(app); return; }
            if (args.Contains("--authored-action-checks")) { await RunAuthoredActionChecksAsync(app,args); return; }
            if (args.Contains("--action-support-checks")) { RunActionSupportChecks(app,args); return; }
            if (args.Contains("--live-action-review")) { await RunLiveActionReviewAsync(app,args); return; }
            if (args.Contains("--recorded-action-review")) { await RunRecordedActionReviewAsync(app,args); return; }
            if (args.Contains("--published-motion-checks")) { await RunPublishedMotionChecksAsync(app,args); return; }
            if (args.Contains("--hand-motion-checks")) { RunHandMotionChecks(app,args); return; }
            if (args.Contains("--authored-upper-body-checks")) { RunAuthoredUpperBodyChecks(app,args); return; }
            if (args.Contains("--live-motion-checks")) { await RunLiveMotionChecksAsync(app,args); return; }
            if (args.Contains("--animation-core-checks")) { RunRigCoreChecks(app,args); return; }
            if (args.Contains("--motion-preview")) { await RunRigMotionPreviewAsync(app,args); return; }
            if (args.Contains("--interaction-checks")) { await RunInteractionChecksAsync(app,args); return; }
            if (args.Contains("--animation-checks")) { await RunRigUiChecksAsync(app,args); return; }
            if (args.Contains("--care-checks")) { await RunCareChecksAsync(app); return; }
            if (args.Contains("--model-checks")) { await RunModelUiChecksAsync(app, args); return; }
            if (args.Contains("--package-checks"))
            {
                var executableIndex = Array.IndexOf(args, "--package-executable");
                var executable = executableIndex >= 0 && executableIndex + 1 < args.Length ? args[executableIndex + 1] : null;
                var modelSourceIndex = Array.IndexOf(args, "--package-model"); PackageModelSource = modelSourceIndex >= 0 ? args[modelSourceIndex + 1] : null;
                var installerIndex = Array.IndexOf(args, "--package-installer"); PackageInstallerPath = installerIndex >= 0 && installerIndex + 1 < args.Length ? args[installerIndex + 1] : null;
                await RunPackageChecksAsync(app, args.Contains("--package-lifecycle"), executable);
            }
            else await RunAsync(app);
        };
        return app.Run();
    }

    private static async Task RunAsync(Application app)
    {
        var temp = Path.Combine(Path.GetTempPath(), "DesktopBuddy-Checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Native.GetCursorPos(out var originalCursor);
        MainWindow? pet = null;
        var success = false;
        try
        {
            Check("interactive-desktop", Environment.UserInteractive, "UI checks require the logged-in desktop session.");
            Check("sendinput-layout", Marshal.SizeOf<Native.INPUT>() == (IntPtr.Size == 8 ? 40 : 28), "Invalid SendInput ABI layout.");
            var import = typeof(NativeMethods).GetMethod(nameof(NativeMethods.QueryFullProcessImageNameW), BindingFlags.NonPublic | BindingFlags.Static)!
                .GetCustomAttribute<DllImportAttribute>()!;
            Check("process-image-binding", import.Value == "kernel32.dll" && import.EntryPoint == "QueryFullProcessImageNameW" && import.ExactSpelling,
                "Process-image API must bind to the exact kernel32 export.");
            var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)Environment.ProcessId);
            Check("open-current-process", handle != 0, "Cannot open the test process.");
            try
            {
                var buffer = new StringBuilder(1024);
                var length = buffer.Capacity;
                Check("query-process-image-native", NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref length)
                    && string.Equals(Path.GetFileName(buffer.ToString()), Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase),
                    "Native process-image lookup returned an unexpected result.");
            }
            finally { NativeMethods.CloseHandle(handle); }

            // All product settings and growth writes go to this temporary store.
            // The real DesktopBuddy profile and startup registry value are untouched.
            RunDataChecks(temp);
            pet = new MainWindow(new LocalStore(Path.Combine(temp, "profile")), _ => { }, new ChatService(() => null));
            var work = SystemParameters.WorkArea;
            pet.Left = work.Right - pet.Width - 12;
            pet.Top = work.Bottom - pet.Height - 12;
            app.MainWindow = pet;
            pet.Show();
            await Task.Delay(700);
            var root = (FrameworkElement)pet.FindName("RootGrid");

            var captureTrace = new List<string>();
            root.PreviewMouseDown += (_,e) => captureTrace.Add($"down:{e.ClickCount}:{e.GetPosition(pet)}:{e.OriginalSource?.GetType().Name}");
            root.LostMouseCapture += (_,_) => captureTrace.Add("lost");
            pet.Deactivated += (_,_) => captureTrace.Add("deactivated");
            HwndSource.FromHwnd(new WindowInteropHelper(pet).Handle).AddHook((nint h,int m,nint w,nint l,ref bool handled)=>{if(m is 0x201 or 0x202 or 0xA1 or 0x21)captureTrace.Add($"wm:{m:X}:{l}");return 0;});
            var point = PetPoint(pet);
            var localHit=pet.PointFromScreen(new Point(point.X,point.Y));
            captureTrace.Add("hit:"+pet.InputHitTest(localHit)?.GetType().Name+":"+localHit+":"+pet.IsVisible+":"+pet.Settings.ClickThrough);
            await InputAsync(() => { Native.SetCursorPos(point.X, point.Y); Native.LeftDown(); });
            await Task.Delay(100);
            Check("press-captures-event-grid", ReferenceEquals(Mouse.Captured, root), "The grid must receive captured move/up events. point=" + point.X + "," + point.Y + "; hit=" + ModelWindowFromPoint(point) + "; pet=" + new WindowInteropHelper(pet).Handle + "; pressed=" + GetField<bool>(pet,"_leftPressed") + "; drag=" + GetField<bool>(pet,"_dragStarted") + "; trace=" + string.Join("|",captureTrace));
            await InputAsync(Native.LeftUp);
            await Task.Delay(100);
            Check("click-releases-capture", Mouse.Captured is null, "Mouse capture remained after mouse-up.");

            await SeparateGestureAsync();
            point = PetPoint(pet);
            await InputAsync(() => { Native.SetCursorPos(point.X, point.Y); Native.LeftDown(); });
            await Task.Delay(80);
            Check("cancelled-press-captures-grid", ReferenceEquals(Mouse.Captured, root), "The cancellation test did not start a press.");
            Mouse.Capture(null);
            Check("lost-capture-clears-gesture", !GetField<bool>(pet, "_leftPressed"), "A cancelled capture left a pending press.");
            await InputAsync(Native.LeftUp);

            await SeparateGestureAsync();
            var before = Bounds(pet);
            point = PetPoint(pet);
            await InputAsync(() =>
            {
                Native.SetCursorPos(point.X, point.Y);
                Native.LeftDown();
                try
                {
                    Thread.Sleep(120);
                    for (var step = 1; step <= 6; step++)
                    {
                        Native.SetCursorPos(point.X - step * 22, point.Y - step * 8);
                        Thread.Sleep(90);
                    }
                }
                finally { Native.LeftUp(); }
            });
            await Task.Delay(500);
            var after = Bounds(pet);
            Check("native-drag-moves-pet", Math.Abs(after.Left - before.Left) > 50,
                $"The pet did not move when dragged: before=({before.Left},{before.Top}), after=({after.Left},{after.Top}).");
            Check("drag-releases-capture", Mouse.Captured is null, "Drag completion retained mouse capture.");

            pet.OpenControlCenter();
            await Task.Delay(400);
            var center = app.Windows.OfType<ControlCenterWindow>().Single();
            // Move the pet clear of the panel before exercising native clicks.
            pet.Left = work.Right - pet.Width - 12;
            pet.Top = work.Top + 12;
            await SeparateGestureAsync();
            await ClickAsync(PetPoint(pet));
            Check("pet-click-with-panel-releases-capture", Mouse.Captured is null, "Pet click trapped panel input.");
            var positionBeforeHide = Bounds(pet);
            var hide = Descendants<Button>(center).Single(x => Equals(x.Content, "隐藏到托盘"));
            await ClickAsync(ElementPoint(hide));
            Check("panel-click-after-pet-click", !pet.IsVisible, "The panel hide button could not receive the click.");
            var show = Descendants<Button>(center).Single(x => Equals(x.Content, "显示桌宠"));
            await ClickAsync(ElementPoint(show));
            Check("panel-restores-pet", pet.IsVisible, "The panel show button did not restore the pet.");
            var positionAfterShow = Bounds(pet);
            Check("restore-keeps-pet-position", positionBeforeHide.Left == positionAfterShow.Left && positionBeforeHide.Top == positionAfterShow.Top,
                "Showing the pet changed its screen position.");

            await SeparateGestureAsync();
            point = PetPoint(pet);
            await InputAsync(() => { Native.SetCursorPos(point.X, point.Y); Native.LeftDown(); });
            await Task.Delay(80);
            Invoke(pet, "SetClickThrough", true);
            Check("click-through-releases-capture", Mouse.Captured is null, "Switching to click-through retained capture.");
            await InputAsync(Native.LeftUp);
            var style = NativeMethods.GetWindowLongPtr(new WindowInteropHelper(pet).Handle, NativeMethods.GwlExStyle).ToInt64();
            Check("click-through-native-style", (style & NativeMethods.WsExTransparent) != 0, "Click-through style was not applied.");
            Invoke(pet, "SetClickThrough", false);

            foreach (var (name, category, status) in new[]
            {
                ("Code.exe", "coding", "专注陪伴"),
                ("chrome.exe", "browser", "浏览陪伴"),
                ("WeChat.exe", "messaging", "沟通情境")
            })
            {
                var context = ContextCatalog.FromExecutable(name);
                Check("category-mapping-" + category, context.Category == category && context.Status == status,
                    "The process-name classification rule is incorrect.");
            }

            var unlistedPlugin = Path.Combine(pet.Store.PluginsDirectory, "unlisted-check.json");
            File.WriteAllText(unlistedPlugin, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = "test.unlisted", name = "Unlisted rule fixture", version = "1.0",
                permissions = new[] { "pet.say" },
                triggers = new[] { new { id = "rule", processNames = new[] { Path.GetFileName(Environment.ProcessPath) }, say = "UNAPPROVED_PLUGIN" } }
            }));
            var fixture = await StartFixtureAsync();
            await Task.Delay(1500);
            var snapshot = ForegroundMonitor.GetCurrent();
            Check("foreground-image-fixture", snapshot is not null && snapshot.ProcessId == fixture.Id
                && string.Equals(snapshot.ExecutableName, Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase),
                $"Expected fixture PID={fixture.Id}; actual PID={snapshot?.ProcessId}; host PID={Environment.ProcessId}; image matches={string.Equals(snapshot?.ExecutableName, Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)}.");
            Check("unlisted-app-status", pet.CurrentCategory == "unknown" && pet.ContextStatus == "当前应用不在识别白名单",
                "An unlisted app retained a stale category.");
            Check("unlisted-app-cannot-trigger-plugin", pet.CurrentStatus != "UNAPPROVED_PLUGIN", "A plugin bypassed the application whitelist.");
            File.Delete(unlistedPlugin);
            fixture.Kill();
            fixture.WaitForExit(3000);

            var browser = await StartBrowserAsync(temp);
            await Task.Delay(1500);
            snapshot = ForegroundMonitor.GetCurrent();
            Check("foreground-real-browser-image", snapshot is not null
                && snapshot.ExecutableName.Equals(BrowserImage, StringComparison.OrdinalIgnoreCase),
                "Native foreground lookup did not identify the isolated Chrome window.");
            Check("foreground-browser-category", pet.CurrentCategory == "browser" && pet.ContextStatus == "浏览陪伴",
                "The running product did not classify the real browser.");
            var label = GetField<TextBlock>(center, "_contextStatusText");
            Check("panel-context-refresh", label.Text == "本机情境：浏览陪伴", "The panel did not receive the live context update.");
            Check("privacy-omits-image", ForegroundMonitor.GetCurrent(false)?.ExecutableName == "", "Privacy mode returned a process name.");
            pet.SetPrivacyMode(true);
            await Task.Delay(1100);
            Check("privacy-pauses-classification", pet.CurrentCategory == "private", "Classification continued in privacy mode.");
            pet.SetPrivacyMode(false);
            await Task.Delay(1500);
            Check("privacy-resumes-same-app", pet.CurrentCategory == "browser", "Classification did not recover for the same foreground app.");
            if (!browser.HasExited) { browser.Kill(entireProcessTree: true); browser.WaitForExit(3000); }

            center.WindowState = WindowState.Minimized;
            pet.OpenControlCenter();
            await Task.Delay(250);
            Check("reopen-restores-minimized-panel", center.WindowState == WindowState.Normal, "The minimized panel did not restore.");
            await RunExtendedUiChecksAsync(pet, center, temp);
            center = app.Windows.OfType<ControlCenterWindow>().Single();
            SaveWindowImage(center, Path.ChangeExtension(_report, ".png"));
            success = true;
        }
        catch (Exception ex)
        {
            Results.Add(new { name = "failure", passed = false, detail = ex.ToString() });
            try
            {
                var visibleCenter = app.Windows.OfType<ControlCenterWindow>().FirstOrDefault(x => x.IsVisible);
                if (visibleCenter is not null) SaveWindowImage(visibleCenter, Path.ChangeExtension(_report, ".failure.png"));
            }
            catch { }
        }
        finally
        {
            try { Native.LeftUp(); Mouse.Capture(null); } catch { }
            foreach (var fixture in Fixtures)
            {
                try { if (!fixture.HasExited) { fixture.Kill(entireProcessTree: true); fixture.WaitForExit(3000); } }
                catch { }
                fixture.Dispose();
            }
            if (pet is not null)
            {
                typeof(MainWindow).GetField("_exitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pet, true);
                foreach (var center in app.Windows.OfType<ControlCenterWindow>().ToArray()) center.Close();
                pet.Close();
            }
            Native.SetCursorPos(originalCursor.X, originalCursor.Y);
            try { Directory.Delete(temp, true); } catch { }
            File.WriteAllText(_report, JsonSerializer.Serialize(new
            {
                success,
                productVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                windows = Environment.OSVersion.VersionString,
                finishedAt = DateTimeOffset.Now,
                note = "Native foreground checks use a synthetic window and installed Chrome with a temporary profile; process mapping checks use static cases. Product settings use an isolated temporary store.",
                checks = Results
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(success ? 0 : 1);
        }
    }

    private static async Task<Process> StartFixtureAsync(bool fullscreen = false)
    {
        var arguments = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + typeof(Program).Assembly.Location + "\" --fixture" : "--fixture";
        if (fullscreen) arguments += " --fullscreen";
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        Fixtures.Add(process);
        nint hwnd = 0;
        for (var i = 0; i < 60 && hwnd == 0 && !process.HasExited; i++)
        {
            await Task.Delay(100);
            hwnd = NativeMethods.FindWindow(null, "DesktopBuddy synthetic foreground fixture");
        }
        Check("fixture-window", hwnd != 0, "Synthetic fixture window did not open.");
        await FocusWindowAsync(hwnd);
        return process;
    }

    private static async Task<Process> StartBrowserAsync(string directory)
    {
        var chrome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe");
        if (!File.Exists(chrome))
            chrome = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
                .Select(root => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists) ?? "";
        Check("installed-browser", File.Exists(chrome), "Chrome or Edge is required for the isolated real-browser check.");
        BrowserImage = Path.GetFileName(chrome);
        var page = Path.Combine(directory, "fixture.html");
        File.WriteAllText(page, "<!doctype html><title>DesktopBuddy browser fixture</title><h1>DesktopBuddy local browser check</h1><p>Temporary isolated profile.</p>");
        var profile = Path.Combine(directory, "chrome-profile");
        var args = $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --app=\"{new Uri(page).AbsoluteUri}\"";
        var browser = Process.Start(new ProcessStartInfo(chrome, args) { UseShellExecute = false })!;
        Fixtures.Add(browser);
        nint hwnd = 0;
        for (var i = 0; i < 50 && hwnd == 0; i++)
        {
            await Task.Delay(100);
            hwnd = NativeMethods.FindWindow(null, "DesktopBuddy browser fixture");
            if (hwnd == 0) hwnd = NativeMethods.FindWindow(null, "DesktopBuddy browser fixture - Google Chrome");
            if (hwnd == 0) hwnd = NativeMethods.FindWindow(null, "DesktopBuddy browser fixture - Microsoft Edge");
        }
        Check("real-browser-window", hwnd != 0, "The isolated Chrome fixture window did not open.");
        await FocusWindowAsync(hwnd);
        return browser;
    }

    private static async Task FocusWindowAsync(nint hwnd)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Native.SetForegroundWindow(hwnd);
            NativeMethods.GetWindowRect(hwnd, out var rect);
            await ClickAsync(new Native.POINT { X = rect.Left + 100, Y = rect.Top + 80 });
            if (NativeMethods.GetForegroundWindow() == hwnd) return;
        }
        Check("fixture-foreground-focus", false, "The fixture could not obtain foreground input focus.");
    }

    private static void Check(string name, bool passed, string detail)
    {
        Results.Add(new { name, passed });
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    private static Task InputAsync(Action action) => Task.Run(action);

    private static Task SeparateGestureAsync() => Task.Delay((int)Native.GetDoubleClickTime() + 100);

    private static async Task ClickAsync(Native.POINT point)
    {
        await InputAsync(() =>
        {
            Native.SetCursorPos(point.X, point.Y);
            Thread.Sleep(50);
            Native.LeftDown();
            Thread.Sleep(80);
            Native.LeftUp();
        });
        await Task.Delay(180);
    }

    private static Native.POINT PetPoint(MainWindow pet)
    {
        var element = (FrameworkElement)pet.FindName("DefaultPet");
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new Native.POINT { X = (int)p.X, Y = (int)p.Y };
    }

    private static Native.POINT ElementPoint(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new Native.POINT { X = (int)point.X, Y = (int)point.Y };
    }

    private static NativeMethods.RECT Bounds(Window window)
    {
        NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        return rect;
    }

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SaveWindowImage(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX*_captureScale), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY*_captureScale),
            96 * dpi.DpiScaleX*_captureScale, 96 * dpi.DpiScaleY*_captureScale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT
        {
            internal int dx, dy;
            internal uint mouseData, dwFlags, time;
            internal nuint dwExtraInfo;
        }
        [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION
        {
            [FieldOffset(0)] internal MOUSEINPUT mouse;
            [FieldOffset(0)] internal KEYBDINPUT keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT
        {
            internal ushort vk, scan;
            internal uint flags, time;
            internal nuint extra;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT
        {
            internal uint type;
            internal INPUTUNION data;
        }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(nint hwnd, ref POINT point);
        [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        internal static void PostMouseMove(nint hwnd,int screenX,int screenY)
        {
            var point=new POINT{X=screenX,Y=screenY};
            if(!ScreenToClient(hwnd,ref point))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot convert test mouse coordinates.");
            var packed=unchecked((nint)(((point.Y&0xffff)<<16)|(point.X&0xffff)));
            if(!PostMessage(hwnd,0x0200,0,packed))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot post WPF mouse-move test message.");
        }
        internal static void LeftDown() => MouseInput(0x0002);
        internal static void LeftUp() => MouseInput(0x0004);
        internal static void KeyInput(ushort key, bool up)
        {
            var input = new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { vk = key, flags = up ? 2u : 0u } } };
            if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private static void MouseInput(uint flags)
        {
            var input = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { dwFlags = flags } } };
            if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot inject input on this desktop.");
        }
    }
}
