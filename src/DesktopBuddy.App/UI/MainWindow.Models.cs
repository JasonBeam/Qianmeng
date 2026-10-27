using System.Windows;
using System.Windows.Media.Media3D;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy;

public partial class MainWindow
{
    private CancellationTokenSource? _modelCancellation;
    public double LastImportWorkerPeakMiB { get; private set; }
    public bool IsModelLoading { get; private set; }
    public Model3DGroup? ModelScene { get; private set; }
    public string ModelStatus { get; private set; } = "使用默认形象";
    public string ModelDetails { get; private set; } = "选择一个 GLB，为浅梦换个样子。";
    public event Action? ModelChanged;

    internal object StartupDiagnostics() => new
    {
        loaded = IsLoaded, visible = IsVisible, width = ActualWidth, height = ActualHeight,
        hooked = _source is not null, timer = _foregroundTimer.IsEnabled, showHotkey = _showHotkeyRegistered,
        through = _settings.ClickThrough, opacity = Opacity, stageWidth = PetStage.ActualWidth, stageHeight = PetStage.ActualHeight,
        rootLoaded = RootGrid.IsLoaded, emojiVisibility = PetEmoji.Visibility.ToString(), emojiWidth = PetEmoji.ActualWidth, emojiHeight = PetEmoji.ActualHeight,
        modelVisibility = PetModel.Visibility.ToString(), hasModel = ModelScene is not null,
        animationClips = AnimationNames.Length, animationPlaying = IsAnimationPlaying, renderedAnimationFrames = AnimationFrameCount,
        lastAnimationFrameMs = LastAnimationFrameMilliseconds, renderTier = System.Windows.Media.RenderCapability.Tier >> 16,
        style = NativeMethods.GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(this).Handle, NativeMethods.GwlExStyle).ToInt64()
    };

    internal async Task RestorePetModelAsync()
    {
        var useDefault=_settings.UseDefaultAppearance || !LocalStore.IsModelFileName(_settings.PetModelFile);
        var defaultFile=Path.Combine(AppContext.BaseDirectory,"models","default-companion.glb");
        if(useDefault&&!File.Exists(defaultFile))
        {
            SetModelScene(null);
            ModelStatus="默认人物模型未随此构建提供";
            ModelDetails="请选择本机 GLB 模型；原有模型文件不会删除。";
            ModelChanged?.Invoke();return;
        }
        var cancellation = BeginModelLoad("正在加载已保存的形象…");
        try
        {
            var file = useDefault ? defaultFile : Path.Combine(_store.ModelsDirectory, _settings.PetModelFile);
            var result = await Task.Run(() =>
            {
                var asset = GlbReader.Load(file, cancellation.Token);
                return (Scene: ModelSceneFactory.Create(asset, cancellation.Token), Asset: asset, Bytes: new FileInfo(file).Length);
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_exitRequested) return;
            SetModelScene(result.Scene, result.Asset);
            ModelStatus = useDefault ? "使用浅梦默认形象" : "已加载 · " + _settings.PetModelName;
            ModelDetails = $"{result.Asset.TriangleCount:N0} 个三角面 · 缓存 {result.Bytes / 1048576d:F1} MB · " + (result.Asset.Rig?.Clips.Length > 0 ? $"{result.Asset.Rig.Clips.Length} 段骨骼动作" : "静态形象");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_modelCancellation, cancellation))
            {
                SetModelScene(null);
                ModelStatus = "模型加载失败，已显示默认形象";
                ModelDetails = FriendlyModelError(ex) + " 可重新替换模型，或恢复默认形象。";
                CrashLogger.WriteThrottled("model-restore", ex);
                if(!useDefault&&File.Exists(defaultFile))
                {
                    try
                    {
                        var fallback=await Task.Run(()=>
                        {
                            var asset=GlbReader.Load(defaultFile,cancellation.Token);
                            return (Scene:ModelSceneFactory.Create(asset,cancellation.Token),Asset:asset);
                        },cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if(!_exitRequested&&ReferenceEquals(_modelCancellation,cancellation))
                        {
                            SetModelScene(fallback.Scene,fallback.Asset);
                            ModelStatus="模型加载失败，已恢复浅梦默认形象";
                        }
                    }
                    catch(OperationCanceledException) { }
                    catch(Exception fallbackError){CrashLogger.WriteThrottled("model-default-restore",fallbackError);}
                }
            }
        }
        finally { EndModelLoad(cancellation); }
    }

    public async Task ImportPetModelAsync(string source)
    {
        if (IsModelLoading) throw new ModelImportException("模型正在处理中，请等待完成或先取消。");
        if (_store.SettingsReadOnly) throw new ModelImportException("当前设置处于只读保护状态，无法保存模型选择。");
        if (!string.Equals(Path.GetExtension(source), ".glb", StringComparison.OrdinalIgnoreCase)) throw new ModelImportException("请选择 .glb 格式的模型。");
        var cancellation = BeginModelLoad("正在读取和优化模型…");
        var previousStatus = ModelScene is null ? "使用默认形象" : "已加载 · " + _settings.PetModelName;
        try
        {
            var result = await ModelOptimizer.ImportAsync(source, _store.ModelsDirectory, cancellation.Token);
            LastImportWorkerPeakMiB = result.WorkerPeakPrivateMiB;
            cancellation.Token.ThrowIfCancellationRequested();
            if (_exitRequested) return;
            var displayName = Path.GetFileNameWithoutExtension(source);
            if (displayName.Length > 120) displayName = displayName[..120];
            SaveModelSelection(result.Name, displayName, 0, 1);
            SetModelScene(result.Scene, result.AnimatedAsset);
            ModelStatus = "已加载 · " + displayName;
            ModelDetails = $"{result.Asset.OriginalTriangles:N0} → {result.Asset.TriangleCount:N0} 个三角面 · 缓存 {result.Bytes / 1048576d:F1} MB · "
                + (result.Asset.Clips.Length > 0 ? $"{result.Asset.Clips.Length} 段骨骼动作" : "静态形象");
            ShowStatus("形象已替换。拖动移动，双击打开控制中心。");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_modelCancellation, cancellation)) ModelStatus = "已取消 · " + previousStatus;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_modelCancellation, cancellation))
            {
                ModelStatus = "替换失败，原形象已保留";
                ModelDetails = FriendlyModelError(ex);
                CrashLogger.WriteThrottled("model-import", ex);
            }
            throw new ModelImportException(FriendlyModelError(ex));
        }
        finally { EndModelLoad(cancellation); }
    }

    public void CancelModelImport() => _modelCancellation?.Cancel();

    public void ResetPetModel()
    {
        // Save first: a read-only or full disk must not discard the displayed model.
        SaveModelSelection("", "", 0, 1);
        _modelCancellation?.Cancel(); _modelCancellation = null; IsModelLoading = false;
        ModelStatus = "正在恢复浅梦默认形象…"; ModelDetails = "原模型文件已保留。"; ModelChanged?.Invoke();
        _ = RestorePetModelAsync();
    }

    public void PreviewModelPose(double yaw, double scale)
    {
        if (!double.IsFinite(yaw) || !double.IsFinite(scale)) return;
        PetModel.SetPose(Math.Clamp(yaw, -180, 180), Math.Clamp(scale, 0.5, 1.25));
    }

    public void SaveModelPose(double yaw, double scale)
    {
        if (!double.IsFinite(yaw) || !double.IsFinite(scale)) throw new ModelImportException("视角数值无效。");
        SaveModelSelection(_settings.PetModelFile, _settings.PetModelName, Math.Clamp(yaw, -180, 180), Math.Clamp(scale, 0.5, 1.25));
        PreviewModelPose(_settings.PetModelYaw, _settings.PetModelScale);
    }

    private void SaveModelSelection(string file, string name, double yaw, double scale)
    {
        if (_store.SettingsReadOnly) throw new ModelImportException("设置处于只读保护状态，无法保存模型设置。");
        var previous = (_settings.PetModelFile, _settings.PetModelName, _settings.PetModelYaw, _settings.PetModelScale, _settings.UseDefaultAppearance);
        _settings.UseDefaultAppearance = false;
        _settings.PetModelFile = file; _settings.PetModelName = name; _settings.PetModelYaw = yaw; _settings.PetModelScale = scale;
        try { _store.SaveSettings(_settings); }
        catch
        {
            (_settings.PetModelFile, _settings.PetModelName, _settings.PetModelYaw, _settings.PetModelScale, _settings.UseDefaultAppearance) = previous;
            throw;
        }
    }

    private CancellationTokenSource BeginModelLoad(string status)
    {
        _modelCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _modelCancellation = cancellation;
        IsModelLoading = true; ModelStatus = status; ModelChanged?.Invoke(); return cancellation;
    }

    private void EndModelLoad(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_modelCancellation, cancellation))
        { _modelCancellation = null; IsModelLoading = false; ModelChanged?.Invoke(); }
        cancellation.Dispose();
    }

    private void SetModelScene(Model3DGroup? scene, ModelAsset? asset = null)
    {
        // A verified same-file import can return the exact cached asset and base
        // pose. Keep its live geometry buffers instead of replacing the renderer.
        var reusePlayer=scene is not null&&asset is not null&&_animationPlayer?.UsesAsset(asset,scene)==true;
        if(!reusePlayer){_animationPlayer?.Dispose(); _animationPlayer=null;}
        if (!reusePlayer&&scene is not null && asset?.Rig is { Clips.Length: > 0 })
        {
            _animationPlayer = new RigScenePlayer(asset, scene);
            _animationPlayer.PlaybackFailed += () => { NotifyAnimation(); ShowStatus("动作播放已停止，请重新导入模型；其他功能仍可使用。"); };
            _animationPlayer.CompanionActionChanged += NotifyAnimation;
        }
        ModelScene = _animationPlayer?.Scene ?? scene; PetModel.Scene = ModelScene;
        PetModel.FramingBounds = _animationPlayer?.FramingBounds;
        ApplyPetSize();
        PetModel.SetPose(_settings.PetModelYaw, _settings.PetModelScale);
        PetModel.Visibility = scene is null ? Visibility.Collapsed : Visibility.Visible;
        PetEmoji.Visibility = scene is null ? Visibility.Visible : Visibility.Collapsed;
        RefreshAnimationPlayback();
    }

    private static string FriendlyModelError(Exception ex) => ex switch
    {
        ModelImportException => ex.Message,
        FileNotFoundException or DirectoryNotFoundException => "找不到模型缓存，请重新选择原始 GLB。",
        UnauthorizedAccessException => "无法读取模型或写入本地模型目录，请检查文件权限。",
        IOException => "读取或保存模型失败，请检查磁盘空间和文件是否可用。",
        _ => "无法解析模型或纹理，请导出静态、未压缩且内嵌 PNG/JPEG 纹理的 GLB 后重试。"
    };

    public void OpenModelManager()
    {
        OpenControlCenter(); _controlCenter?.NavigateToModels();
    }

    private void OpenModels_Click(object sender, RoutedEventArgs e) => OpenModelManager();
}
