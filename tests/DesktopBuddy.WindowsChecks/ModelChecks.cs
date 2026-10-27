using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using DesktopBuddy;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;
using DesktopBuddy.UI;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint", ExactSpelling = true)]
    private static extern nint ModelWindowFromPoint(Native.POINT point);

    private static async Task RunModelUiChecksAsync(Application app, string[] args)
    {
        var temp = Path.Combine(Path.GetTempPath(), "Qianmeng-Models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp); Native.GetCursorPos(out var originalCursor);
        MainWindow? pet = null; ControlCenterWindow? center = null;
        var metrics = new List<object>(); var success = false; var snapshots = new List<string>();
        var process = Process.GetCurrentProcess(); long peakPrivate = 0, peakWorkingSet = 0;
        using var sampler = new System.Threading.Timer(_ =>
        {
            var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() }; if (K32GetProcessMemoryInfo(process.Handle, ref counters, counters.Size)) lock (metrics) { peakPrivate = Math.Max(peakPrivate, (long)counters.PrivateUsage); peakWorkingSet = Math.Max(peakWorkingSet, (long)counters.PeakWorkingSet); }
        }, null, 0, 50);
        object Memory(string phase)
        {
            process.Refresh(); return new { phase, privateMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1), workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 1), managedMiB = Math.Round(GC.GetTotalMemory(false) / 1048576d, 1), gcCommittedMiB = Math.Round(GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576d, 1) };
        }
        async Task WaitUntil(Func<bool> condition, int milliseconds = 15000)
        { for (var i = 0; i < milliseconds / 100 && !condition(); i++) await Task.Delay(100); Check("async-operation-completed", condition(), "Timed out waiting for model operation."); }
        try
        {
            Check("model-interactive-desktop", Environment.UserInteractive, "Requires logged-in console.");
            var arg = Array.IndexOf(args, "--model-file");
            if(arg<0||arg+1>=args.Length)throw new ArgumentException("--model-file is required; no model is bundled with the public source.");
            var source = Path.GetFullPath(args[arg + 1]);
            if(!File.Exists(source))throw new FileNotFoundException("The supplied local test model was not found.",source);
            var profile = Path.Combine(temp, "profile"); var store = new LocalStore(profile);
            pet = new MainWindow(store, _ => { }, new ChatService(() => null));
            pet.Settings.AutoHideFullscreen = false; pet.Left = SystemParameters.WorkArea.Right - pet.Width - 30; pet.Top = SystemParameters.WorkArea.Bottom - pet.Height - 30;
            pet.Show(); pet.OpenModelManager(); await Task.Delay(700);
            center = app.Windows.OfType<ControlCenterWindow>().Single();
            Check("model-manager-navigation", GetField<TextBlock>(center, "_pageTitle").Text == "模型", "Model entry did not navigate to model page.");
            var replace = GetField<Button>(center, "_replaceModel");
            await WaitUntil(()=>!pet.IsModelLoading&&replace.IsEnabled);
            Check("model-picker-entry", replace.IsVisible && replace.IsEnabled, "Replace button unavailable.");
            metrics.Add(Memory("before-import"));
            // Drive the actual file picker with keyboard input; never reads or changes the clipboard.
            await FocusWindowAsync(new WindowInteropHelper(center).Handle);
            var typeFile = Task.Run(() =>
            {
                Thread.Sleep(900); Native.KeyInput(0x12, false); Native.KeyInput(0x4E, false); Native.KeyInput(0x4E, true); Native.KeyInput(0x12, true);
                Thread.Sleep(150); Native.TextInput(source); Native.KeyInput(0x0D, false); Native.KeyInput(0x0D, true);
            });
            await ClickAsync(ElementPoint(replace)); await typeFile;
            await WaitUntil(() => pet.ModelScene is not null && !pet.IsModelLoading && !string.IsNullOrWhiteSpace(pet.Settings.PetModelFile));
            var cacheAsset=GlbReader.Load(source);
            if(cacheAsset.Rig is { Clips.Length: > 0})
            {
                var cachedPose=RigScenePlayer.CreateFrozenPose(cacheAsset,CancellationToken.None);
                var repeatedPose=RigScenePlayer.CreateFrozenPose(cacheAsset,CancellationToken.None);
                Check("rig-frozen-pose-cache-reuses-scene",ReferenceEquals(cachedPose,repeatedPose),"Repeated import rebuilt the live animated base scene.");
            }
            Check("model-persisted-selection", File.Exists(Path.Combine(store.ModelsDirectory, pet.Settings.PetModelFile)), "Optimized model was not persisted.");
            var view = (PetModelView)pet.FindName("PetModel"); var emoji = (TextBlock)pet.FindName("PetEmoji");
            Check("model-replaces-emoji", view.Visibility == Visibility.Visible && emoji.Visibility == Visibility.Collapsed, "Model not selected as visible representation.");
            Check("model-preview-shares-scene",ReferenceEquals(GetField<PetModelView>(center,"_modelPreview").Scene,pet.ModelScene),"Preview duplicated the live scene.");
            if(cacheAsset.Rig is { Clips.Length: > 0})
            {
                var animation=GetField<RigScenePlayer>(pet,"_animationPlayer");
                var frozen=GetField<Model3DGroup>(animation,"_basePose");var shared=frozen.IsFrozen;
                for(var m=0;m<cacheAsset.Meshes.Length;m++)
                {
                    var original=(GeometryModel3D)frozen.Children[m];var live=(GeometryModel3D)pet.ModelScene!.Children[m];
                    var sourceMesh=(MeshGeometry3D)original.Geometry;var liveMesh=(MeshGeometry3D)live.Geometry;
                    shared&=ReferenceEquals(sourceMesh.TriangleIndices,liveMesh.TriangleIndices)&&ReferenceEquals(sourceMesh.TextureCoordinates,liveMesh.TextureCoordinates)&&ReferenceEquals(original.Material,live.Material);
                }
                Check("model-animated-shares-frozen-topology-and-materials",shared&&!pet.ModelScene!.IsFrozen,"Animation duplicated immutable mesh data or froze its mutable pose.");
            }
            else Check("model-static-scene-frozen",pet.ModelScene!.IsFrozen,"Static scene was not frozen.");
            await Task.Delay(350);
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); var opaque = 0; long brightness = 0;
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i + 3] > 10) { opaque++; brightness += pixels[i] + pixels[i + 1] + pixels[i + 2]; }
            Check("model-visible-pixels", opaque > 700, "Scene exists but renders no meaningful visible pixels.");
            Check("model-not-black", brightness / (double)Math.Max(1, opaque * 3) > 18, "Model rendered black.");
            var surface = new RenderTargetBitmap((int)pet.ActualWidth, (int)pet.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            surface.Render(pet); var surfacePixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
            surface.CopyPixels(surfacePixels, surface.PixelWidth * 4, 0);
            Check("pet-no-background-card", surfacePixels[(100 * surface.PixelWidth + 10) * 4 + 3] == 0, "Former card area is still painted.");
            var emptyPoint = pet.PointToScreen(new Point(10, 100));
            Check("pet-empty-area-passes-through", ModelWindowFromPoint(new Native.POINT { X = (int)emptyPoint.X, Y = (int)emptyPoint.Y }) != new WindowInteropHelper(pet).Handle, "Transparent former card area intercepts mouse.");
            var modelShot = Path.ChangeExtension(_report, ".pet.png"); SaveWindowImage(pet, modelShot); snapshots.Add(modelShot);
            var panelShot = Path.ChangeExtension(_report, ".panel.png"); SaveWindowImage(center, panelShot); snapshots.Add(panelShot);
            metrics.Add(Memory("after-first-import")); var firstResident = process.PrivateMemorySize64; metrics.Add(new { phase = "disposable-worker", peakPrivateMiB = pet.LastImportWorkerPeakMiB });
            pet.SetPetSize(1.4); await Task.Delay(100);
            Check("model-size-scales-window", Math.Abs(pet.Width - 392) < 1 && Math.Abs(pet.Height - 532) < 1, "GLB size did not scale.");
            await SeparateGestureAsync(); await ClickAsync(ElementPoint(view));
            Check("scaled-model-click-remains-visible", pet.ModelScene is not null && view.IsVisible && Mouse.Captured is null, "Scaled model input broke.");
            pet.SetPetSize(1); await Task.Delay(100);
            Check("model-size-persists", Math.Abs(store.LoadSettings().PetSize - 1) < 0.001, "Model size not saved.");
            var before = Bounds(pet); var point = ElementPoint(view);
            await SeparateGestureAsync();
            await InputAsync(() =>
            {
                Native.SetCursorPos(point.X, point.Y); Native.LeftDown(); Thread.Sleep(100);
                for (var step = 1; step <= 6; step++) { Native.SetCursorPos(point.X - step * 16, point.Y - step * 9); Thread.Sleep(80); } Native.LeftUp();
            });
            await Task.Delay(450); var after = Bounds(pet);
            Check("model-drag-native", Math.Abs(after.Left - before.Left) > 30 && Math.Abs(after.Top - before.Top) > 15, "Dragging visible model did not move pet.");
            Check("model-drag-releases-capture", Mouse.Captured is null, "Mouse remained captured after drag.");
            await SeparateGestureAsync(); await ClickAsync(ElementPoint(view));
            Check("model-survives-single-click", pet.ModelScene is not null && view.Scene is not null && view.IsVisible, "Click feedback discarded the selected model.");
            var clicks = new List<string>();
            pet.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => clicks.Add($"{e.ClickCount}:{e.OriginalSource?.GetType().Name}:{e.GetPosition(pet)}")), true);
            point = ElementPoint(view); center.WindowState = WindowState.Minimized;
            await SeparateGestureAsync();
            point = ElementPoint(view);
            var hit = ModelWindowFromPoint(point);
            var petHwnd = new WindowInteropHelper(pet).Handle;
            var screenPoint = pet.PointFromScreen(new Point(point.X, point.Y));
            SaveWindowImage(pet, Path.ChangeExtension(_report, ".before-double.png"));
            await InputAsync(() => { Native.SetCursorPos(point.X, point.Y); Native.LeftDown(); Native.LeftUp(); Thread.Sleep(80); Native.LeftDown(); Native.LeftUp(); });
            await Task.Delay(350);
            Check("model-double-click-restores-panel", center.WindowState != WindowState.Minimized && center.IsVisible && Mouse.Captured is null, "Double clicking model did not restore control center. clicks=" + string.Join(";", clicks) + "; state=" + center.WindowState + "; visible=" + center.IsVisible + "; point=" + point.X + "," + point.Y + "; hit=" + hit + "; pet=" + petHwnd + "; local=" + screenPoint + "; petVisible=" + pet.IsVisible);
            await SeparateGestureAsync();
            await InputAsync(() => { Native.SetCursorPos(point.X, point.Y); Native.RightDown(); Native.RightUp(); });
            await Task.Delay(200);
            Check("model-right-click-menu", pet.ContextMenu.IsOpen, "Model right click did not open menu.");
            await InputAsync(() => { Native.KeyInput(0x1B, false); Native.KeyInput(0x1B, true); });
            var yaw = GetField<Slider>(center, "_modelYaw");
            yaw.BringIntoView(); await Task.Delay(180); await ClickAsync(ElementPoint(yaw));
            Check("model-click-panel-focus", center.IsActive && Mouse.Captured is null, "Clicking pet prevented panel interaction.");
            var targetYaw = yaw.Value;
            var savePoseButton = GetField<Button>(center, "_savePose"); savePoseButton.BringIntoView(); await Task.Delay(180); await ClickAsync(ElementPoint(savePoseButton));
            Check("model-save-pose", Math.Abs(store.LoadSettings().PetModelYaw - targetYaw) < 0.1, "View pose was not saved.");
            // Leaving the model page must release the preview scene reference.
            var navigation = GetField<ListBox>(center, "_navigation"); navigation.SelectedIndex = 0; await Task.Delay(150);
            Check("hidden-preview-releases-scene", GetField<PetModelView>(center, "_modelPreview").Scene is null, "Hidden preview retained scene.");
            center.NavigateToModels(); await Task.Delay(150);
            var previousScene = pet.ModelScene; var previousFile = pet.Settings.PetModelFile;
            var broken = Path.Combine(temp, "broken.glb"); File.WriteAllText(broken, "invalid-glb");
            var rejected = false; try { await pet.ImportPetModelAsync(broken); } catch { rejected = true; }
            Check("bad-model-preserves-visible-selection", rejected && ReferenceEquals(previousScene, pet.ModelScene) && previousFile == pet.Settings.PetModelFile, "Failed import discarded existing model.");
            var transparent = Path.Combine(temp, "transparent.glb");
            var triangle = new DesktopBuddy.ModelCore.ModelMesh([new(-1,-1,0), new(1,-1,0), new(0,1,0)], [System.Numerics.Vector3.UnitZ,System.Numerics.Vector3.UnitZ,System.Numerics.Vector3.UnitZ], [System.Numerics.Vector2.Zero,System.Numerics.Vector2.UnitX,System.Numerics.Vector2.UnitY], [0,1,2], new(new System.Numerics.Vector4(1,1,1,0), null, "image/png", "BLEND", 0.5f));
            DesktopBuddy.ModelCore.GlbWriter.Write(new([triangle],1,"test",false),transparent);
            rejected=false; try { await pet.ImportPetModelAsync(transparent); } catch { rejected=true; }
            Check("transparent-model-preserves-current", rejected && ReferenceEquals(previousScene,pet.ModelScene), "Invisible material replaced the visible pet.");
            var cancelled = pet.ImportPetModelAsync(source); pet.CancelModelImport(); await cancelled;
            Check("cancel-keeps-current-model", !pet.IsModelLoading && ReferenceEquals(previousScene, pet.ModelScene), "Cancelled import replaced current model.");
            Check("cancel-cleans-import-job", Directory.GetDirectories(store.ModelsDirectory, ".import-*").Length == 0, "Cancelled worker left a job directory.");
            for (var i = 0; i < 8; i++)
            { await pet.ImportPetModelAsync(source); await Task.Delay(100); metrics.Add(Memory("replace-" + (i + 1))); }
            metrics.Add(Memory("before-idle"));
            Check("repeat-replace-memory-budget", process.PrivateMemorySize64 <= firstResident + 64L * 1048576, "Repeated replacements grew private memory by more than 64 MiB.");
            sampler.Change(Timeout.Infinite, Timeout.Infinite); await Task.Delay(400);
            process.Refresh(); var startCpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew(); await Task.Delay(8000); process.Refresh();
            metrics.Add(new { phase = "idle-cpu", oneCorePercent = Math.Round((process.TotalProcessorTime - startCpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100, 2) });
            metrics.Add(Memory("after-idle-no-forced-GC"));
            var idleCpuWithinBudget=(process.TotalProcessorTime-startCpu).TotalMilliseconds/clock.Elapsed.TotalMilliseconds<0.04;
            // Keep the same budget failure, but collect restart/fallback evidence
            // before reporting it so a CPU failure cannot hide those regressions.
            // Forced collection here is diagnostic only: distinguish retained objects from GC reserve.

            var reloadName = pet.Settings.PetModelFile;
            center.Close(); center = null;
            await Task.Delay(300); process.Refresh(); var petCpuStart = process.TotalProcessorTime; var petClock = Stopwatch.StartNew(); await Task.Delay(4000); process.Refresh();
            metrics.Add(new { phase = "pet-only-idle-cpu", oneCorePercent = Math.Round((process.TotalProcessorTime - petCpuStart).TotalMilliseconds / petClock.Elapsed.TotalMilliseconds * 100, 2) }); metrics.Add(Memory("pet-only-preview-closed-no-GC"));
            var residentWithinBudget=process.PrivateMemorySize64<256L*1048576;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(200); metrics.Add(Memory("after-diagnostic-GC"));
            Check("closed-preview-releases-scene", !app.Windows.OfType<ControlCenterWindow>().Any(), "Closed preview window still registered.");
            CloseModelPet(pet); pet = new MainWindow(new LocalStore(profile), _ => { }, new ChatService(() => null)); pet.Settings.AutoHideFullscreen = false; pet.Show();
            await WaitUntil(() => pet.ModelScene is not null && !pet.IsModelLoading);
            Check("restart-restores-model", pet.Settings.PetModelFile == reloadName, "Restart lost model selection.");
            var missingCache = Path.Combine(store.ModelsDirectory,pet.Settings.PetModelFile); CloseModelPet(pet); File.Delete(missingCache);
            pet = new MainWindow(new LocalStore(profile), _=>{}, new ChatService(()=>null)); pet.Settings.AutoHideFullscreen=false; pet.Show();
            await WaitUntil(()=>!pet.IsModelLoading && pet.ModelStatus.Contains("加载失败"));
            var bundledDefault=File.Exists(Path.Combine(AppContext.BaseDirectory,"models","default-companion.glb"));
            Check("missing-cache-falls-back-visible", bundledDefault ? pet.ModelScene is not null : pet.ModelScene is null && ((TextBlock)pet.FindName("PetEmoji")).Visibility == Visibility.Visible, "Missing cache made pet invisible.");
            pet.ResetPetModel();
            await WaitUntil(()=>!pet.IsModelLoading);
            Check("reset-default-visible", (bundledDefault ? pet.ModelScene is not null : pet.ModelScene is null && ((TextBlock)pet.FindName("PetEmoji")).Visibility == Visibility.Visible) && store.LoadSettings().PetModelFile == "", "Default shape did not restore.");
            var race = pet.ImportPetModelAsync(source); pet.ResetPetModel(); await race;
            await WaitUntil(()=>!pet.IsModelLoading);
            Check("reset-wins-inflight-import", (bundledDefault ? pet.ModelScene is not null : pet.ModelScene is null) && pet.Settings.PetModelFile == "", "Late import overwrote reset.");
            var readonlyDir = Path.Combine(temp, "readonly"); Directory.CreateDirectory(readonlyDir); var future = "{\"SchemaVersion\":99}";
            File.WriteAllText(Path.Combine(readonlyDir, "settings.json"), future);
            var locked = new MainWindow(new LocalStore(readonlyDir), _ => { }, new ChatService(() => null));
            var protectedSettings = false; try { locked.SaveModelPose(90, 1); } catch { protectedSettings = true; }
            Check("readonly-model-settings-no-false-success", protectedSettings && File.ReadAllText(Path.Combine(readonlyDir, "settings.json")) == future, "Read-only selection falsely succeeded.");
            CloseModelPet(locked);
            var budgetFailures=new List<string>();
            try{Check("idle-cpu-budget",idleCpuWithinBudget,"Idle CPU exceeded 4% of one logical core without the sampler.");}catch(Exception ex){budgetFailures.Add(ex.Message);}
            try{Check("resident-memory-budget",residentWithinBudget,"Steady private memory exceeds the 256 MiB budget on this test device.");}catch(Exception ex){budgetFailures.Add(ex.Message);}
            if(budgetFailures.Count>0)throw new InvalidOperationException(string.Join("; ",budgetFailures));
            success = true;
        }
        catch (Exception ex) { Results.Add(new { name = "failure", passed = false, detail = ex.ToString() }); }
        finally
        {
            sampler.Change(Timeout.Infinite, Timeout.Infinite);
            try { Native.LeftUp(); Mouse.Capture(null); center?.Close(); if (pet is not null) CloseModelPet(pet); Native.SetCursorPos(originalCursor.X, originalCursor.Y); } catch { }
            try { Directory.Delete(temp, true); } catch { }
            File.WriteAllText(_report, JsonSerializer.Serialize(new { success, windows = Environment.OSVersion.VersionString, finishedAt = DateTimeOffset.Now, peakPrivateMiB = Math.Round(peakPrivate / 1048576d, 1), peakWorkingSetMiB = Math.Round(peakWorkingSet / 1048576d, 1), checks = Results, metrics, snapshots }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(success ? 0 : 1);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged, QuotaPeakNonPaged, QuotaNonPaged, Pagefile, PeakPagefile, PrivateUsage;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(nint handle, ref MemoryCounters counters, uint size);
    private static void CloseModelPet(MainWindow pet)
    { typeof(MainWindow).GetField("_exitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pet, true); pet.Close(); }

    private static partial class Native
    {
        internal static void RightDown() => MouseInput(0x0008);
        internal static void RightUp() => MouseInput(0x0010);
        internal static void TextInput(string text)
        {
            foreach (var ch in text)
            {
                var down = new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { scan = ch, flags = 4 } } };
                var up = down; up.data.keyboard.flags = 6;
                if (SendInput(2, [down, up], Marshal.SizeOf<INPUT>()) != 2) throw new InvalidOperationException("Unicode input failed.");
            }
        }
    }
}
