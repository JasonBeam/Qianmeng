using DesktopBuddy.Services;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private static string? PackageModelSource;

    private static async Task RunPackageModelUiChecksAsync(AutomationElement center,string source,Process product,List<ResourceSample> resourceMetrics)
    {
        await EnsureControlCenterForegroundAsync(center);
        await ClickUiAsync(FindUi(center, ControlType.ListItem, "模型"));
        var button = FindUi(center, ControlType.Button, "替换模型");
        Check("package-model-replace-entry", !button.Current.IsOffscreen, "Replace entry missing in packaged EXE.");
        var input = Task.Run(() =>
        {
            Thread.Sleep(900); Native.KeyInput(0x12, false); Native.KeyInput(0x4E, false); Native.KeyInput(0x4E, true); Native.KeyInput(0x12, true);
            Thread.Sleep(150); Native.TextInput(Path.GetFullPath(source)); Native.KeyInput(0x0D, false); Native.KeyInput(0x0D, true);
        });
        await ClickUiAsync(button); await input;
        var expectedModelName = Path.GetFileNameWithoutExtension(source);
        var loaded = false;
        bool ModelImportVisible()
        {
            try
            {
                var panel = NativeMethods.FindWindow(null, "浅梦 · 控制中心");
                if (panel != 0) center = AutomationElement.FromHandle(panel);
                var labels = center.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                foreach (AutomationElement label in labels)
                {
                    try
                    {
                        if (label.Current.Name.Contains("已加载 · " + expectedModelName, StringComparison.Ordinal)) return true;
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            catch (ElementNotAvailableException) { }
            return false;
        }
        for (var i = 0; i < 150 && !loaded; i++)
        {
            await Task.Delay(100);
            loaded = ModelImportVisible();
        }
        Check("package-single-file-model-import", loaded, "Single-file EXE did not load the requested model: " + expectedModelName + ".");
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBuddy");
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "settings.json")));
        var name = settings.RootElement.GetProperty("PetModelFile").GetString()!;
        Check("package-model-cache", File.Exists(Path.Combine(profile, "models", name)), "Packaged model cache missing.");
        Check("package-worker-exits", Process.GetProcessesByName("DesktopBuddy").Length == 1, "Worker did not exit after import.");
        var isRig = DesktopBuddy.ModelCore.GlbReader.Load(source).Rig?.Clips.Length > 0;
        if (isRig) center=await RunInstalledAnimationControlsAsync(center,product,resourceMetrics);
        var pet = NativeMethods.FindPetWindow(); NativeMethods.GetWindowRect(pet, out var before);
        var point = PackagePetPoint(pet);
        await SeparateGestureAsync();
        await InputAsync(() =>
        {
            Native.SetCursorPos(point.X, point.Y); Native.LeftDown();
            try { Thread.Sleep(100); for (var i = 1; i <= 6; i++) { Native.SetCursorPos(point.X - i * 16, point.Y - i * 8); Thread.Sleep(80); } }
            finally { Native.LeftUp(); }
        });
        await Task.Delay(400); NativeMethods.GetWindowRect(pet, out var after);
        Check("package-model-native-drag", Math.Abs(after.Left - before.Left) > 30, "Packaged model cannot be dragged.");
        CaptureNativeWindow(pet, Path.ChangeExtension(_report, ".model-pet.png"));
        var hwnd = new nint(center.Current.NativeWindowHandle);
        CaptureNativeWindow(hwnd, Path.ChangeExtension(_report, ".model-panel.png"));
    }

    private static async Task<AutomationElement> RunInstalledAnimationControlsAsync(AutomationElement center,Process product,List<ResourceSample> resourceMetrics)
    {
        foreach(var element in center.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>())
        {
            if(element.TryGetCurrentPattern(ScrollPattern.Pattern,out var pattern)&&((ScrollPattern)pattern).Current.VerticallyScrollable)
                ((ScrollPattern)pattern).SetScrollPercent(ScrollPattern.NoScroll,100);
        }
        await Task.Delay(150);
        await EnsureControlCenterForegroundAsync(center);await ClickUiAsync(FindUi(center,ControlType.Button,"跟随日常活动"));await Task.Delay(250);
        bool TextContains(string text)=>center.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().Any(x=>x.Current.Name.Contains(text));
        var settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopBuddy","settings.json");
        bool AnimationDisabledInSettings()
        {
            if(!File.Exists(settingsPath))return false;
            using var settings=JsonDocument.Parse(File.ReadAllText(settingsPath));
            return settings.RootElement.TryGetProperty("AnimationEnabled",out var value)&&value.ValueKind==JsonValueKind.False;
        }
        Check("installed-idle-activity-playback-status",TextContains("Idle · 播放中"),"Installed idle activity did not start before resource sampling.");
        await CloseControlCenterAsync();
        var rigIdleSample=await SampleProcessAsync(product,"rig-idle-visible-panel-closed-10s",10000);resourceMetrics.Add(rigIdleSample);
        Check("installed-rig-idle-cpu-budget",rigIdleSample.oneCoreCpuPercent<20,"Idle rig CPU was "+rigIdleSample.oneCoreCpuPercent.ToString("F2")+"% of one core.");
        var idleCenter=await RestoreControlCenterAsync(NativeMethods.FindPetWindow());
        Check("installed-control-center-restores-after-idle-sample",idleCenter!=0,"Control center did not reopen after idle rig sampling.");
        center=AutomationElement.FromHandle(idleCenter);
        await EnsureControlCenterForegroundAsync(center);
        await ClickUiAsync(FindUi(center,ControlType.ListItem,"模型"));
        await Task.Delay(150);
        var combo=FindUi(center,ControlType.ComboBox,"可用动作");
        if(combo.TryGetCurrentPattern(ScrollItemPattern.Pattern,out var comboScrollItem))
            ((ScrollItemPattern)comboScrollItem).ScrollIntoView();
        await Task.Delay(200);
        var comboPanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");
        if(comboPanel!=0)center=AutomationElement.FromHandle(comboPanel);
        combo=FindUi(center,ControlType.ComboBox,"可用动作");
        if(combo.Current.IsOffscreen)
        {
            foreach(var element in center.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>())
                if(element.TryGetCurrentPattern(ScrollPattern.Pattern,out var pattern)&&((ScrollPattern)pattern).Current.VerticallyScrollable)
                    ((ScrollPattern)pattern).SetScrollPercent(ScrollPattern.NoScroll,35);
            await Task.Delay(200);
            comboPanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");
            if(comboPanel!=0)center=AutomationElement.FromHandle(comboPanel);
            combo=FindUi(center,ControlType.ComboBox,"可用动作");
        }
        var comboRect=combo.Current.BoundingRectangle;
        Check("installed-animation-selector-visible",!combo.Current.IsOffscreen&&comboRect.Width>1&&comboRect.Height>1,
            $"Animation selector was outside the viewport: offscreen={combo.Current.IsOffscreen}, rect={comboRect}.");
        await EnsureControlCenterForegroundAsync(center);
        await ClickUiAsync(combo);
        await Task.Delay(150);
        comboPanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");
        if(comboPanel!=0)center=AutomationElement.FromHandle(comboPanel);
        combo=FindUi(center,ControlType.ComboBox,"可用动作");
        if(combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out var comboExpand))
        {
            var pattern=(ExpandCollapsePattern)comboExpand;
            if(pattern.Current.ExpandCollapseState==ExpandCollapseState.Collapsed)pattern.Expand();
        }
        await Task.Delay(150);
        CaptureNativeWindow(new nint(center.Current.NativeWindowHandle),Path.ChangeExtension(_report,".animation-clip-dropdown.png"));
        var runningOption=AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty,"Running")));
        if(runningOption is not null&&runningOption.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var runningSelection))
            ((SelectionItemPattern)runningSelection).Select();
        else
        {
            combo.SetFocus();
            await InputAsync(()=>{Native.KeyInput(0x24,false);Native.KeyInput(0x24,true);Native.KeyInput(0x28,false);Native.KeyInput(0x28,true);Native.KeyInput(0x0D,false);Native.KeyInput(0x0D,true);});
        }
        await Task.Delay(150);
        comboPanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");
        if(comboPanel!=0)center=AutomationElement.FromHandle(comboPanel);
        combo=FindUi(center,ControlType.ComboBox,"可用动作");
        var selectedClip="";
        if(combo.TryGetCurrentPattern(SelectionPattern.Pattern,out var clipSelection))
            selectedClip=string.Join(",",((SelectionPattern)clipSelection).Current.GetSelection().Select(item=>item.Current.Name));
        File.WriteAllLines(Path.ChangeExtension(_report,".animation-clip-selected.txt"),new[]{"combo="+combo.Current.Name,"selection="+selectedClip,"runningOptionFound="+(runningOption is not null)});
        await Task.Delay(150);
        await ScrollControlCenterToActionControlsAsync(center);
        var freshPanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");
        var freshCenter=AutomationElement.FromHandle(freshPanel!=0?freshPanel:new nint(center.Current.NativeWindowHandle));center=freshCenter;
        var playButton=FindUi(freshCenter,ControlType.Button,"播放所选动作");
        var animationButtons=freshCenter.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button))
            .Cast<AutomationElement>().Select(x=>$"{x.Current.Name} | offscreen={x.Current.IsOffscreen} | rect={x.Current.BoundingRectangle}").ToArray();
        File.WriteAllLines(Path.ChangeExtension(_report,".animation-button-tree.txt"),animationButtons);
        CaptureNativeWindow(new nint(freshCenter.Current.NativeWindowHandle),Path.ChangeExtension(_report,".animation-controls.png"));
        var panelHandle=new nint(freshCenter.Current.NativeWindowHandle);NativeMethods.GetWindowRect(panelHandle,out var panelRect);var playRect=playButton.Current.BoundingRectangle;
        var playVisible=playRect.Width>1&&playRect.Height>1&&playRect.Left>=panelRect.Left&&playRect.Right<=panelRect.Right&&playRect.Top>=panelRect.Top&&playRect.Bottom<=panelRect.Bottom;
        Check("installed-play-control-scrolls-into-view",playVisible,$"Play control remained outside the panel: rect={playRect}; panel={panelRect}.");
        await EnsureControlCenterForegroundAsync(freshCenter);await ClickUiAsync(playButton);
        string PlaybackStatusText()
        {
            try
            {
                var livePanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");if(livePanel!=0)center=AutomationElement.FromHandle(livePanel);
                var values=new List<string>();var textElements=center.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text));
                foreach(AutomationElement element in textElements)
                {
                    try{var value=element.Current.Name;if(value.Contains("Idle",StringComparison.OrdinalIgnoreCase)||value.Contains("Running",StringComparison.OrdinalIgnoreCase))values.Add(value);}
                    catch(ElementNotAvailableException) { }
                }
                return string.Join(" | ",values);
            }
            catch(ElementNotAvailableException){return string.Empty;}
        }
        var playbackClock=Stopwatch.StartNew();var playbackStatus=string.Empty;
        while(playbackClock.Elapsed<TimeSpan.FromSeconds(3))
        {
            await Task.Delay(100);playbackStatus=PlaybackStatusText();
            if(playbackStatus.Contains("Running",StringComparison.OrdinalIgnoreCase)&&playbackStatus.Contains("播放中",StringComparison.Ordinal))break;
        }
        var statusPanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");if(statusPanel!=0)CaptureNativeWindow(statusPanel,Path.ChangeExtension(_report,".playback-status.png"));
        File.WriteAllLines(Path.ChangeExtension(_report,".playback-status.txt"),new[]{"elapsedMs="+playbackClock.Elapsed.TotalMilliseconds.ToString("F0"),"status="+playbackStatus});
        Check("installed-running-playback-status",playbackStatus.Contains("Running",StringComparison.OrdinalIgnoreCase)&&playbackStatus.Contains("播放中",StringComparison.Ordinal),"Installed player failed to select or play Running; status="+playbackStatus);
        var pet=NativeMethods.FindPetWindow();var a=Path.ChangeExtension(_report,".running-a.png");var b=Path.ChangeExtension(_report,".running-b.png");
        CaptureNativeWindow(pet,a);await Task.Delay(230);CaptureNativeWindow(pet,b);
        Check("installed-animation-pixels-change",!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(a)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(b))),"Installed animation frame did not change.");
        await CloseControlCenterAsync();
        var activeSample=await SampleProcessAsync(product,"rig-running-visible-panel-closed-10s",10000);resourceMetrics.Add(activeSample);
        Check("installed-rig-running-cpu-budget",activeSample.oneCoreCpuPercent<30,"Running rig CPU was "+activeSample.oneCoreCpuPercent.ToString("F2")+"% of one core.");
        var activeCenter=await RestoreControlCenterAsync(NativeMethods.FindPetWindow());
        Check("installed-control-center-restores-after-running-sample",activeCenter!=0,"Control center did not reopen after running rig sampling.");
        center=AutomationElement.FromHandle(activeCenter);
        await EnsureControlCenterForegroundAsync(center);await ClickUiAsync(FindUi(center,ControlType.ListItem,"模型"));await ScrollControlCenterToActionControlsAsync(center);
        await EnsureControlCenterForegroundAsync(center);
        var pauseButton=FindUi(center,ControlType.Button,"暂停动作");
        Check("installed-animation-pause-button-visible",!pauseButton.Current.IsOffscreen,"Pause button remained outside the viewport after reopening Model settings.");
        await ClickUiAsync(pauseButton);
        var pauseWait=Stopwatch.StartNew();
        while((!TextContains("Running · 已暂停")||!AnimationDisabledInSettings())&&pauseWait.Elapsed<TimeSpan.FromSeconds(3))
        {
            var livePanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");if(livePanel!=0)center=AutomationElement.FromHandle(livePanel);
            await Task.Delay(50);
        }
        var pauseStatusUpdated=TextContains("Running · 已暂停")&&AnimationDisabledInSettings();
        if(!pauseStatusUpdated)
        {
            var pausePanel=NativeMethods.FindWindow(null,"浅梦 · 控制中心");if(pausePanel!=0)CaptureNativeWindow(pausePanel,Path.ChangeExtension(_report,".pause-state.png"));
            var pauseTexts=center.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().Select(x=>x.Current.Name).ToArray();
            File.WriteAllLines(Path.ChangeExtension(_report,".pause-state.txt"),new[]{"waitMs="+pauseWait.Elapsed.TotalMilliseconds.ToString("F0"),"AnimationDisabled="+AnimationDisabledInSettings(),"texts="+string.Join(" | ",pauseTexts)});
        }
        Check("installed-animation-pauses",pauseStatusUpdated,$"Installed pause did not persist to the UI and settings within {pauseWait.Elapsed.TotalMilliseconds:F0}ms.");
        await CloseControlCenterAsync();
        var pausedSample=await SampleProcessAsync(product,"animation-paused-pet-visible-panel-closed-10s",10000);resourceMetrics.Add(pausedSample);
        Check("installed-paused-animation-cpu-budget",pausedSample.oneCoreCpuPercent<4,$"Paused animation CPU was {pausedSample.oneCoreCpuPercent:F2}% of one core.");
        var restoredCenter=await RestoreControlCenterAsync(NativeMethods.FindPetWindow());
        Check("installed-control-center-restores-after-pause",restoredCenter!=0,"Control center did not reopen after pausing animation.");
        center=AutomationElement.FromHandle(restoredCenter);
        await EnsureControlCenterForegroundAsync(center);await ClickUiAsync(FindUi(center,ControlType.ListItem,"模型"));await ScrollControlCenterToActionControlsAsync(center);
        await EnsureControlCenterForegroundAsync(center);
        var followButton=FindUi(center,ControlType.Button,"跟随日常活动");
        Check("installed-animation-follow-button-visible",!followButton.Current.IsOffscreen,"Follow button remained outside the viewport after reopening Model settings.");
        await ClickUiAsync(followButton);await Task.Delay(250);
        Check("installed-animation-follows-idle",TextContains("Idle · 播放中"),"Installed activity idle failed.");
        return center;
    }

    private static async Task ScrollControlCenterToActionControlsAsync(AutomationElement center)
    {
        foreach(var element in center.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>())
        {
            if(element.TryGetCurrentPattern(ScrollPattern.Pattern,out var pattern)&&((ScrollPattern)pattern).Current.VerticallyScrollable)
                ((ScrollPattern)pattern).SetScrollPercent(ScrollPattern.NoScroll,55);
        }
        await Task.Delay(150);
    }

    private static async Task EnsureControlCenterForegroundAsync(AutomationElement center)
    {
        var hwnd = new nint(center.Current.NativeWindowHandle);
        if (hwnd == 0) hwnd = NativeMethods.FindWindow(null, "浅梦 · 控制中心");
        if (hwnd == 0 || !NativeMethods.IsWindowVisible(hwnd)) throw new InvalidOperationException("Control center is not visible before package UI input.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Native.SetForegroundWindow(hwnd);
            NativeMethods.GetWindowRect(hwnd, out var rect);
            await ClickAsync(new Native.POINT { X = rect.Left + Math.Min(240, Math.Max(10, rect.Right - rect.Left - 10)), Y = rect.Top + 20 });
            if (NativeMethods.GetForegroundWindow() == hwnd) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Control center could not become foreground before package UI input.");
    }
    private static void CaptureNativeWindow(nint hwnd, string path)
    {
        NativeMethods.GetWindowRect(hwnd, out var r); var width = r.Right - r.Left; var height = r.Bottom - r.Top;
        var screen = CaptureNative.GetDC(0); var memory = CaptureNative.CreateCompatibleDC(screen); var bitmap = CaptureNative.CreateCompatibleBitmap(screen, width, height);
        var previous = CaptureNative.SelectObject(memory, bitmap);
        try
        {
            if (!CaptureNative.BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, 0x40CC0020)) throw new InvalidOperationException("Screen capture failed.");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
        }
        finally { CaptureNative.SelectObject(memory, previous); CaptureNative.DeleteObject(bitmap); CaptureNative.DeleteDC(memory); CaptureNative.ReleaseDC(0, screen); }
    }

    private static class CaptureNative
    {
        [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
        [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint dc, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    }
}
