using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private sealed record ResourceSample(string phase,double durationSeconds,double privateStartMiB,double privateEndMiB,double privateDeltaMiB,double workingSetStartMiB,double workingSetEndMiB,double oneCoreCpuPercent,int pointerMoves);
    private static async Task RunPackageChecksAsync(Application app, bool lifecycle, string? executable = null)
    {
        var success = false;
        var resourceMetrics=new List<ResourceSample>();
        Native.GetCursorPos(out var cursor);
        try
        {
            var exe = executable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DesktopBuddy", "DesktopBuddy.exe");
            await StartFixtureAsync();
            for (var i = 0; i < 300 && (NativeMethods.FindPetWindow() == 0 || !NativeMethods.IsWindowVisible(NativeMethods.FindPetWindow())); i++) await Task.Delay(100);
            var running = Process.GetProcessesByName("DesktopBuddy");
            Check("installed-process-single", running.Length == 1 && running[0].MainModule?.FileName == exe, "Installed EXE is not running uniquely.");
            running[0].EnableRaisingEvents = true;
            var hwnd = NativeMethods.FindPetWindow();
            Check("installed-pet-window", hwnd != 0 && NativeMethods.IsWindowVisible(hwnd), "Installed pet window missing.");
            NativeMethods.GetWindowRect(hwnd, out var before);
            var point = PackagePetPoint(hwnd);
            CaptureNativeWindow(hwnd, Path.ChangeExtension(_report, ".initial-pet.png"));
            await CloseControlCenterAsync();
            var idleCursor = new Native.POINT { X = 20, Y = 20 };
            if (idleCursor.X >= before.Left && idleCursor.X < before.Right && idleCursor.Y >= before.Top && idleCursor.Y < before.Bottom)
                idleCursor = new Native.POINT { X = before.Right + 40, Y = before.Bottom + 40 };
            var idleCursorOutsidePet = idleCursor.X < before.Left || idleCursor.X >= before.Right || idleCursor.Y < before.Top || idleCursor.Y >= before.Bottom;
            Check("idle-cursor-away-from-pet", idleCursorOutsidePet, "Idle CPU sample would include pet hover interaction.");
            await InputAsync(() => Native.SetCursorPos(idleCursor.X, idleCursor.Y));
            await Task.Delay(700);
            var visibleIdleWarmup = await SampleProcessAsync(running[0], "visible-idle-startup-warmup-10s", 10000);
            resourceMetrics.Add(visibleIdleWarmup);
            var visibleIdle = await SampleProcessAsync(running[0], "visible-idle-panel-closed-10s", 10000);
            resourceMetrics.Add(visibleIdle);
            Check("installed-visible-idle-cpu-budget",visibleIdle.oneCoreCpuPercent<4,$"Visible idle CPU was {visibleIdle.oneCoreCpuPercent:F2}% of one core.");
            var pointerSample=await SamplePointerInteractionAsync(running[0],hwnd,5000);resourceMetrics.Add(pointerSample);
            Check("installed-pointer-interaction-cpu-budget",pointerSample.oneCoreCpuPercent<30,$"Pointer-follow CPU was {pointerSample.oneCoreCpuPercent:F2}% of one core.");
            await SeparateGestureAsync();
            await InputAsync(() =>
            {
                Native.SetCursorPos(point.X, point.Y); Native.LeftDown();
                try
                {
                    Thread.Sleep(100);
                    for (var i = 1; i <= 6; i++) { Native.SetCursorPos(point.X - i * 22, point.Y - i * 10); Thread.Sleep(80); }
                }
                finally { Native.LeftUp(); }
            });
            await Task.Delay(500);
            NativeMethods.GetWindowRect(hwnd, out var after);
            Check("installed-exe-native-drag", Math.Abs(after.Left - before.Left) > 50, "Installed pet cannot be dragged. hit=" + ModelWindowFromPoint(point) + "; pet=" + hwnd + "; point=" + point.X + "," + point.Y + "; before=" + before.Left + "," + before.Top + "; after=" + after.Left + "," + after.Top);
            using (var duplicate = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!)
            {
                await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                Check("installed-exe-second-launch-exits", duplicate.ExitCode == 0, "Second launch failed.");
            }
            await Task.Delay(400);
            var center = NativeMethods.FindWindow(null, "浅梦 · 控制中心");
            Check("installed-single-instance-restores-panel", center != 0 && Process.GetProcessesByName("DesktopBuddy").Length == 1,
                "Second launch did not wake the first control center.");
            var ui = AutomationElement.FromHandle(center);
            NativeMethods.GetWindowRect(hwnd, out after);
            point = PackagePetPoint(hwnd);
            await SeparateGestureAsync();
            await ClickAsync(point);
            await ClickUiAsync(FindUi(ui, ControlType.Button, "隐藏到托盘"));
            Check("installed-panel-click-after-pet-click", !NativeMethods.IsWindowVisible(hwnd), "Installed panel became unclickable after pet click.");
            await ClickUiAsync(FindUi(ui, ControlType.Button, "显示桌宠"));
            Check("installed-panel-show-pet", NativeMethods.IsWindowVisible(hwnd), "Installed panel cannot restore pet.");
            NativeMethods.GetWindowRect(hwnd, out var restored);
            Check("installed-show-keeps-position", after.Left == restored.Left && after.Top == restored.Top, "Installed pet jumped on restore.");
            await ClickUiAsync(FindUi(ui, ControlType.ListItem, "对话"));
            var input = ui.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().First(x => !x.Current.IsOffscreen);
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("你好");
            await ClickUiAsync(FindUi(ui, ControlType.Button, "发送"));
            Check("installed-offline-chat", ui.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Any(x => x.Current.Name.Contains("现在是离线陪伴模式")), "Installed chat did not reply.");
            await ClickUiAsync(FindUi(ui, ControlType.ListItem, "总览"));
            await RunPackageCareUiAsync(ui);
            var temp = Path.Combine(Path.GetTempPath(), "DesktopBuddy-PackageBrowser-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var browser = await StartBrowserAsync(temp);
                await Task.Delay(1600);
                Check("installed-live-chrome-context", ui.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>().Any(x => x.Current.Name == "本机情境：浏览陪伴"), "Installed EXE did not detect Chrome.");
                browser.Kill(entireProcessTree: true); browser.WaitForExit(3000);
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
            await NativeHotkeyAsync(0x20);
            Check("installed-global-hotkey-hide", !NativeMethods.IsWindowVisible(hwnd), "Installed hotkey did not hide pet.");
            await CloseControlCenterAsync();
            var hiddenSample=await SampleProcessAsync(running[0],"all-windows-hidden-10s",10000);resourceMetrics.Add(hiddenSample);
            Check("installed-hidden-cpu-budget",hiddenSample.oneCoreCpuPercent<2,$"Hidden CPU was {hiddenSample.oneCoreCpuPercent:F2}% of one core.");
            await NativeHotkeyAsync(0x20);
            Check("installed-global-hotkey-show", NativeMethods.IsWindowVisible(hwnd), "Installed hotkey did not restore pet.");
            center=await RestoreControlCenterAsync(hwnd);
            Check("installed-control-center-restores-after-hidden",center!=0,"Control center did not reopen after hiding the pet.");
            ui=AutomationElement.FromHandle(center);
            if (PackageModelSource is not null) await RunPackageModelUiChecksAsync(ui, PackageModelSource,running[0],resourceMetrics);
            if (lifecycle) await RunInstallerLifecycleAsync(running[0], exe);
            else
            {
                NativeMethods.PostMessage(hwnd, NativeMethods.WmAppExit, 0, 0);
                await running[0].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Check("installed-graceful-exit", running[0].ExitCode == 0, "Installed IPC exit failed.");
            }
            Check("installed-growth-persisted", File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBuddy", "pet-state.json")), "Growth file not saved.");
            success = true;
        }
        catch (Exception ex) { Results.Add(new { name = "failure", passed = false, detail = ex.ToString() }); }
        finally
        {
            try { Native.LeftUp(); } catch { }
            foreach (var process in Fixtures)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
            try
            {
                var petWindow = NativeMethods.FindPetWindow();
                if (petWindow != 0) NativeMethods.PostMessage(petWindow, NativeMethods.WmAppExit, 0, 0);
                for (var i = 0; i < 80 && Process.GetProcessesByName("DesktopBuddy").Length > 0; i++) await Task.Delay(100);
            }
            catch { }
            Native.SetCursorPos(cursor.X, cursor.Y);
            File.WriteAllText(_report, JsonSerializer.Serialize(new
            {
                success, mode = lifecycle ? "installed-exe-and-NSIS-lifecycle" : "installed-self-contained-exe", finishedAt = DateTimeOffset.Now,
                checks = Results,metrics=resourceMetrics
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(success ? 0 : 1);
        }
    }

    private static async Task<ResourceSample> SampleProcessAsync(Process process,string phase,int milliseconds)
    {
        process.Refresh();var privateStart=process.PrivateMemorySize64;var workingStart=process.WorkingSet64;var cpuStart=process.TotalProcessorTime;var watch=Stopwatch.StartNew();
        await Task.Delay(milliseconds);process.Refresh();var cpuPercent=100*(process.TotalProcessorTime-cpuStart).TotalSeconds/watch.Elapsed.TotalSeconds;
        var privateEnd=process.PrivateMemorySize64;var workingEnd=process.WorkingSet64;
        return new(phase,Math.Round(watch.Elapsed.TotalSeconds,2),Math.Round(privateStart/1048576d,1),Math.Round(privateEnd/1048576d,1),Math.Round((privateEnd-privateStart)/1048576d,1),Math.Round(workingStart/1048576d,1),Math.Round(workingEnd/1048576d,1),Math.Round(cpuPercent,2),0);
    }

    private static async Task<ResourceSample> SamplePointerInteractionAsync(Process process,nint petWindow,int milliseconds)
    {
        var point=PackagePetPoint(petWindow);process.Refresh();var privateStart=process.PrivateMemorySize64;var workingStart=process.WorkingSet64;var cpuStart=process.TotalProcessorTime;var watch=Stopwatch.StartNew();var moves=0;
        while(watch.ElapsedMilliseconds<milliseconds)
        {
            var angle=moves*.22;var x=point.X+(int)(Math.Cos(angle)*24);var y=point.Y+(int)(Math.Sin(angle)*12);
            Native.SetCursorPos(x,y);Native.PostMouseMove(petWindow,x,y);moves++;await Task.Delay(40);
        }
        await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(500);process.Refresh();var cpuPercent=100*(process.TotalProcessorTime-cpuStart).TotalSeconds/watch.Elapsed.TotalSeconds;
        var privateEnd=process.PrivateMemorySize64;var workingEnd=process.WorkingSet64;
        return new("pointer-follow-interaction",Math.Round(watch.Elapsed.TotalSeconds,2),Math.Round(privateStart/1048576d,1),Math.Round(privateEnd/1048576d,1),Math.Round((privateEnd-privateStart)/1048576d,1),Math.Round(workingStart/1048576d,1),Math.Round(workingEnd/1048576d,1),Math.Round(cpuPercent,2),moves);
    }

    private static async Task CloseControlCenterAsync()
    {
        var center=NativeMethods.FindWindow(null,"浅梦 · 控制中心");if(center==0)return;
        if(NativeMethods.IsWindowVisible(center))NativeMethods.PostMessage(center,0x0010,0,0);
        for(var i=0;i<50&&NativeMethods.IsWindowVisible(center);i++)await Task.Delay(100);
    }

    private static async Task<nint> RestoreControlCenterAsync(nint petWindow)
    {
        NativeMethods.PostMessage(petWindow,NativeMethods.WmAppRestore,0,0);
        for(var i=0;i<50;i++){var center=NativeMethods.FindWindow(null,"浅梦 · 控制中心");if(center!=0&&NativeMethods.IsWindowVisible(center))return center;await Task.Delay(100);}
        return 0;
    }

    private static Native.POINT PackagePetPoint(nint hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var controls = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>();
        var target = controls.First(x => !x.Current.IsOffscreen && (x.Current.AutomationId == "PetModel" || x.Current.AutomationId == "DefaultPet"));
        var p = target.GetClickablePoint();
        return new Native.POINT { X = (int)p.X, Y = (int)p.Y };
    }

    private static async Task RunInstallerLifecycleAsync(Process pet, string exe)
    {
        var installer = PackageInstallerPath ?? Path.Combine(Path.GetDirectoryName(_report)!, "DesktopBuddy-Setup-0.5.13-win-x64.exe");
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBuddy");
        var growth = Path.Combine(profile, "pet-state.json");
        var growthBefore = SHA256.HashData(File.ReadAllBytes(growth));
        await RunInstallerAsync(installer, "upgrade-installer-exit-zero", Path.GetDirectoryName(exe)!);
        await pet.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Check("upgrade-closes-pet-gracefully", pet.ExitCode == 0, "Upgrade force-terminated or crashed the pet. Exit code=" + pet.ExitCode);
        Check("upgrade-keeps-growth", growthBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(growth))), "Upgrade changed growth data.");
        var before = new[] { "settings.json", "pet-state.json" }.ToDictionary(name => name,
            name => SHA256.HashData(File.ReadAllBytes(Path.Combine(profile, name))));
        await RunInstallerAsync(Path.Combine(Path.GetDirectoryName(exe)!, "Uninstall.exe"), "uninstall-exit-zero");
        for (var i = 0; i < 80 && File.Exists(exe); i++) await Task.Delay(100);
        Check("uninstall-removes-exe", !File.Exists(exe), "Uninstaller retained the executable.");
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DesktopBuddy"))
            Check("uninstall-removes-registration", key is null, "Uninstaller retained its registration.");
        foreach (var item in before)
            Check("silent-uninstall-keeps-" + item.Key, item.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(profile, item.Key)))),
                "Silent uninstall changed user data.");
        await RunInstallerAsync(installer, "reinstall-exit-zero", Path.GetDirectoryName(exe)!);
        foreach (var item in before)
            Check("reinstall-keeps-" + item.Key, item.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(profile, item.Key)))),
                "Reinstall changed user data.");
        Check("reinstall-start-menu-qianmeng", File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "浅梦", "浅梦.lnk")),
            "Renamed start menu shortcut missing.");
        var restarted = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!;
        await Task.Delay(1200);
        Check("reinstall-exe-starts", !restarted.HasExited && NativeMethods.FindPetWindow() != 0, "Reinstalled executable failed to start.");
        NativeMethods.PostMessage(NativeMethods.FindPetWindow(), NativeMethods.WmAppRestore, 0, 0);
        await Task.Delay(700);
        var panel = NativeMethods.FindWindow(null, "浅梦 · 控制中心");
        Check("reinstall-panel-restores", panel != 0, "Reinstalled control center did not restore.");
        if (PackageModelSource is not null)
        {
            var root = AutomationElement.FromHandle(panel); await ClickUiAsync(FindUi(root, ControlType.ListItem, "模型")); await Task.Delay(300);
            Check("reinstall-model-restores", root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Any(x => x.Current.Name.StartsWith("已加载 · ")), "Reinstall lost the imported model.");
        }
        NativeMethods.PostMessage(NativeMethods.FindPetWindow(), NativeMethods.WmAppExit, 0, 0);
        await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Check("reinstall-graceful-exit", restarted.ExitCode == 0, "Reinstalled product did not exit gracefully.");
    }

    private static async Task RunInstallerAsync(string file, string check, string? installDirectory = null)
    {
        var arguments = installDirectory is null ? "/S" : "/S /D=" + installDirectory;
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Check(check, process.ExitCode == 0, "Installer exit=" + process.ExitCode);
    }

    private static AutomationElement FindUi(AutomationElement root, ControlType type, string name) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.NameProperty, name)))
        ?? throw new InvalidOperationException("Missing UI element: " + name);

    private static Task ClickUiAsync(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        return ClickAsync(new Native.POINT { X = (int)(rect.Left + rect.Width / 2), Y = (int)(rect.Top + rect.Height / 2) });
    }
}
