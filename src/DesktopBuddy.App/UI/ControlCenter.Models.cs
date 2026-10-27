using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopBuddy.UI;

namespace DesktopBuddy;

public sealed partial class ControlCenterWindow
{
    private readonly PetModelView _modelPreview = new() { Height = 230 };
    private readonly TextBlock _modelState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _modelInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
    private readonly TextBlock _modelFallback = new() { Text = "🐧", FontSize = 86, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _modelProgress = new() { IsIndeterminate = false, Height = 4, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Slider _modelYaw = new() { Minimum = -180, Maximum = 180, TickFrequency = 15, SmallChange = 15, LargeChange = 45 };
    private readonly Slider _modelScale = new() { Minimum = 0.5, Maximum = 1.25, TickFrequency = 0.05, SmallChange = 0.05, LargeChange = 0.1 };
    private readonly TextBlock _poseFeedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private Button? _replaceModel;
    private Button? _cancelModel;
    private Button? _savePose;
    private bool _refreshingModels;
    private StackPanel? _modelsPage;
    private ScrollViewer? _modelsScroll;

    private UIElement BuildModels()
    {
        var page = Page(); _modelsPage = page;
        _modelsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = page };
        _modelsScroll.ScrollChanged += (_, _) => RefreshModelPreviewVisibility();
        var stage = new Grid(); stage.Children.Add(_modelPreview); stage.Children.Add(_modelFallback);
        page.Children.Add(new Border { Child = stage, Background = Brush("#F1EEF8"), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 0, 14) });
        AutomationProperties.SetName(stage, "模型预览");
        page.Children.Add(_modelState); page.Children.Add(_modelInfo); page.Children.Add(_modelProgress);
        var commands = new WrapPanel();
        _replaceModel = Primary(ActionButton("选择 / 替换模型…", async (_, _) => await ChoosePetModelAsync()));
        AutomationProperties.SetName(_replaceModel, "替换模型");
        commands.Children.Add(_replaceModel);
        commands.Children.Add(ActionButton("恢复默认形象", (_, _) =>
        {
            try { _owner.ResetPetModel(); }
            catch (Exception ex) { ShowError("恢复默认形象失败", ex); }
        }));
        _cancelModel = ActionButton("取消导入", (_, _) => _owner.CancelModelImport()); commands.Children.Add(_cancelModel);
        commands.Children.Add(ActionButton("显示桌宠", (_, _) => _owner.RestoreFromTray()));
        page.Children.Add(commands);
        page.Children.Add(Heading("调整视角"));
        AutomationProperties.SetName(_modelYaw, "模型朝向"); AutomationProperties.SetName(_modelScale, "模型大小");
        page.Children.Add(Labeled("朝向（左右旋转）", _modelYaw)); page.Children.Add(Labeled("显示大小", _modelScale));
        var poseCommands = new WrapPanel();
        _savePose = ActionButton("保存视角", (_, _) =>
        {
            try { _owner.SaveModelPose(_modelYaw.Value, _modelScale.Value); _poseFeedback.Text = "视角已保存，下次启动自动沿用。"; }
            catch (Exception ex) { ShowError("保存视角失败", ex); }
        });
        poseCommands.Children.Add(_savePose);
        poseCommands.Children.Add(ActionButton("重置视角", (_, _) =>
        {
            try { _owner.SaveModelPose(0, 1); RefreshModelPage(); _poseFeedback.Text = "已恢复默认视角。"; }
            catch (Exception ex) { ShowError("重置视角失败", ex); }
        }));
        page.Children.Add(poseCommands); page.Children.Add(_poseFeedback);
        page.Children.Add(BuildAnimationControls());
        page.Children.Add(new TextBlock
        {
            Text = "GLB 模型只在本机处理，导入后自动取景并优化面数。支持静态形象和轻量骨骼动画 GLB，纹理须内嵌 PNG/JPEG；无动作片段的模型保持静态。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#777E91"), Margin = new Thickness(0, 18, 0, 0), FontSize = 12
        });
        _modelYaw.ValueChanged += (_, _) => PreviewPose(); _modelScale.ValueChanged += (_, _) => PreviewPose();
        page.IsVisibleChanged += (_, _) =>
        {
            if (page.IsVisible) RefreshModelPage();
            else { _modelPreview.Scene = null; _owner.PreviewModelPose(_settings.PetModelYaw, _settings.PetModelScale); }
        };
        _owner.ModelChanged += RefreshModelPage;
        Closed += (_, _) =>
        {
            _owner.ModelChanged -= RefreshModelPage; _modelPreview.Scene = null;
            _owner.PreviewModelPose(_settings.PetModelYaw, _settings.PetModelScale);
        };
        RefreshModelPage(); return _modelsScroll;
    }

    public void NavigateToModels()
    {
        foreach (var item in _navigation.Items.OfType<ListBoxItem>())
            if (item.Tag is NavigationPage { Title: "模型" }) { _navigation.SelectedItem = item; break; }
    }

    private void RefreshModelPage()
    {
        _refreshingModels = true;
        try
        {
            _modelState.Text = _owner.ModelStatus; _modelInfo.Text = _owner.ModelDetails;
            _modelProgress.IsIndeterminate = _owner.IsModelLoading;
            _modelProgress.Visibility = _owner.IsModelLoading ? Visibility.Visible : Visibility.Collapsed;
            if (_replaceModel is not null) _replaceModel.IsEnabled = !_owner.IsModelLoading;
            if (_cancelModel is not null) _cancelModel.Visibility = _owner.IsModelLoading ? Visibility.Visible : Visibility.Collapsed;
            var hasModel = _owner.ModelScene is not null;
            _modelFallback.Visibility = hasModel ? Visibility.Collapsed : Visibility.Visible;
            RefreshModelPreviewVisibility();
            _modelYaw.Value = _settings.PetModelYaw; _modelScale.Value = _settings.PetModelScale;
            _modelYaw.IsEnabled = _modelScale.IsEnabled = hasModel && !_owner.IsModelLoading;
            if (_savePose is not null) _savePose.IsEnabled = hasModel && !_owner.IsModelLoading;
            _modelPreview.FramingBounds = _owner.ModelFramingBounds;
            _modelPreview.SetPose(_settings.PetModelYaw, _settings.PetModelScale);
            RefreshAnimationControls();
            _poseFeedback.Text = hasModel ? "调整后点击“保存视角”；离开页面会还原未保存的调整。" : "导入模型后可以调整方向和大小。";
        }
        finally { _refreshingModels = false; }
    }

    private void RefreshModelPreviewVisibility()
    {
        if (_modelsPage?.IsVisible != true || _modelsScroll is null) { _modelPreview.Scene = null; return; }
        var visible = true;
        if (_modelsScroll.ActualHeight > 0 && _modelPreview.ActualHeight > 0)
        {
            try
            {
                var rect = _modelPreview.TransformToAncestor(_modelsScroll).TransformBounds(new Rect(0, 0, _modelPreview.ActualWidth, _modelPreview.ActualHeight));
                visible = rect.IntersectsWith(new Rect(0, 0, _modelsScroll.ActualWidth, _modelsScroll.ActualHeight));
            }
            catch (InvalidOperationException) { visible = false; }
        }
        _modelPreview.Scene = visible ? _owner.ModelScene : null;
    }

    private void PreviewPose()
    {
        if (_refreshingModels || _modelsPage?.IsVisible != true) return;
        _modelPreview.SetPose(_modelYaw.Value, _modelScale.Value); _owner.PreviewModelPose(_modelYaw.Value, _modelScale.Value);
        _poseFeedback.Text = $"预览：{_modelYaw.Value:F0}° · {_modelScale.Value:P0}，尚未保存。";
    }

    private async Task ChoosePetModelAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "为浅梦选择 GLB 模型", Filter = "GLB 3D 模型 (*.glb)|*.glb", DefaultExt = ".glb", Multiselect = false, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await _owner.ImportPetModelAsync(dialog.FileName); }
        catch (Exception ex) { if (IsLoaded) ShowError("替换模型失败", ex); }
    }
}
