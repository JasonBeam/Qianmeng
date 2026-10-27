using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using Media = System.Windows.Media;
using System.Windows.Input;
using DesktopBuddy.Models;
using DesktopBuddy.Services;

namespace DesktopBuddy;

public sealed partial class ControlCenterWindow : System.Windows.Window
{
    private readonly MainWindow _owner;
    private readonly AppSettings _settings;
    private readonly List<ChatLine> _history = [];
    private readonly StackPanel _conversation = new();
    private readonly TextBox _messageInput = new();
    private readonly TextBox _endpointInput = new();
    private readonly TextBox _modelInput = new();
    private readonly PasswordBox _apiKeyInput = new();
    private readonly TextBlock _growthText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _contextStatusText = new();
    private readonly ListBox _targetsList = new();
    private readonly ListBox _pluginsList = new();
    private readonly CheckBox _clickThrough = new() { Content = "鼠标穿过桌宠，不接收点击" };
    private readonly CheckBox _hideFullscreen = new() { Content = "全屏应用时自动隐藏桌宠" };
    private readonly CheckBox _contextDetection = new() { Content = "允许本机识别前台应用类别" };
    private readonly CheckBox _privacyMode = new() { Content = "隐私模式（暂停前台应用名读取）" };
    private readonly CheckBox _autoStart = new() { Content = "登录 Windows 后启动（最小化到托盘）" };
    private readonly CheckBox _speakReplies = new() { Content = "发送回复后使用 Windows SAPI 朗读" };
    private readonly TextBox _processNames = new() { AcceptsReturn = true, Height = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private bool _chatBusy;
    private CancellationTokenSource? _chatCancellation;

    public ControlCenterWindow(MainWindow owner)
    {
        _owner = owner;
        _settings = owner.Settings;
        Title = "浅梦 · 控制中心";
        Width = 940;
        Height = 780;
        MinWidth = 820;
        MinHeight = 650;
        FontFamily = new Media.FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DesktopBuddy;component/UI/ControlCenterTheme.xaml", UriKind.Relative)
        });
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new Media.SolidColorBrush(Media.Color.FromRgb(248, 249, 252));
        Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(35, 39, 52));
        ShowInTaskbar = true;
        BuildTabs();
        _owner.StatusChanged += Owner_StatusChanged;
        _owner.ContextStatusChanged += Owner_ContextStatusChanged;
        _owner.CareChanged += RefreshCare;
        RefreshCare();
        Closed += (_, _) =>
        {
            _owner.StatusChanged -= Owner_StatusChanged;
            _owner.ContextStatusChanged -= Owner_ContextStatusChanged;
            _owner.CareChanged -= RefreshCare;
            _chatCancellation?.Cancel();
            _chatCancellation?.Dispose();
        };
    }

    private UIElement BuildChat()
    {
        var root = new DockPanel { Margin = new Thickness(26, 0, 24, 24) };
        var config = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        config.Children.Add(new TextBlock
        {
            Text = "本地离线回应默认可用。接入兼容 OpenAI Chat Completions 的接口后，只有你输入的聊天文本和本页会话上下文会发给该服务；不会附带前台应用名。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        _endpointInput.Text = _settings.ChatEndpoint;
        _endpointInput.MaxLength = 2048;
        _endpointInput.ToolTip = "完整接口地址，例如 https://host/v1/chat/completions；本机 HTTP 也可使用";
        config.Children.Add(Labeled("接口地址（留空为离线）", _endpointInput));
        _modelInput.Text = _settings.ChatModel;
        _modelInput.MaxLength = 128;
        _apiKeyInput.MaxLength = 4096;
        config.Children.Add(Labeled("模型名称", _modelInput));
        var keyRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var saveKey = ActionButton("保存密钥", (_, _) => SaveApiKey());
        DockPanel.SetDock(saveKey, Dock.Right);
        keyRow.Children.Add(saveKey);
        keyRow.Children.Add(_apiKeyInput);
        var deleteKey = ActionButton("删除已保存密钥", (_, _) =>
        {
            CredentialStore.Delete();
            _apiKeyInput.Clear();
            _owner.ShowStatus("已删除 Windows 凭据管理器中的 API Key");
        });
        config.Children.Add(Labeled("API Key（保存在 Windows 凭据管理器）", keyRow));
        config.Children.Add(deleteKey);
        var consent = ActionButton("撤销远程聊天授权", (_, _) =>
        {
            _settings.RemoteChatConsent = false;
            _settings.RemoteChatConsentEndpoint = "";
            _owner.SaveSettings();
            _owner.ShowStatus("远程聊天授权已撤销");
        });
        config.Children.Add(consent);
        var connection = new Expander { Header = "模型连接与隐私授权", Content = config, IsExpanded = false };
        DockPanel.SetDock(connection, Dock.Top);
        root.Children.Add(connection);

        var sendArea = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _messageInput.AcceptsReturn = true;
        _messageInput.MaxLength = 4000;
        _messageInput.ToolTip = "Enter 发送 · Shift + Enter 换行 · 输入法选词时不会发送";
        System.Windows.Automation.AutomationProperties.SetName(_messageInput, "聊天输入");
        _messageInput.TextWrapping = TextWrapping.Wrap;
        _messageInput.MinHeight = 56;
        _messageInput.MaxHeight = 110;
        _messageInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _messageInput.PreviewKeyDown += MessageInput_KeyDown;
        sendArea.Children.Add(_messageInput);
        sendArea.Children.Add(new TextBlock { Text = "Enter 发送 · Shift + Enter 换行", FontSize = 11, Foreground = Brush("#777E91"), Margin = new Thickness(0, 4, 0, 0) });
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _sendButton = Primary(ActionButton("发送", async (_, _) => await SendMessageAsync()));
        buttons.Children.Add(_sendButton);
        buttons.Children.Add(ActionButton("朗读最近回复", async (_, _) => await SpeakLatestAsync()));
        buttons.Children.Add(ActionButton("清空本次对话", (_, _) => ClearConversation()));
        sendArea.Children.Add(buttons);
        DockPanel.SetDock(sendArea, Dock.Bottom);
        root.Children.Add(sendArea);

        _conversationScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _conversation,
            Background = Media.Brushes.White,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(14)
        };
        root.Children.Add(new Border { Background = Media.Brushes.White, BorderBrush = Brush("#E6E7EF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = _conversationScroll });
        AddChatLine("assistant", "你好，我是浅梦。你可以直接和我聊；当前默认使用本地离线回应。", scroll: _conversationScroll);
        return root;
    }

    private UIElement BuildSystemActions()
    {
        var page = Page();
        page.Children.Add(Heading("已批准的打开目标"));
        page.Children.Add(new TextBlock
        {
            Text = "桌伴只会打开你添加到列表中的 exe、lnk、http/https/mailto 链接或 UWP AUMID。每次启动前都会再次确认。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        _targetsList.Height = 150;
        page.Children.Add(_targetsList);
        var targetButtons = new WrapPanel();
        targetButtons.Children.Add(ActionButton("添加 exe/lnk", (_, _) => AddPathTarget()));
        targetButtons.Children.Add(ActionButton("添加网址", (_, _) => AddTextTarget("url", "添加网址", "输入 http、https 或 mailto 地址")));
        targetButtons.Children.Add(ActionButton("添加 UWP AUMID", (_, _) => AddTextTarget("uwp", "添加 UWP 应用", "输入应用的 AUMID，例如 PackageFamilyName!ApplicationId")));
        targetButtons.Children.Add(ActionButton("移除选中项", (_, _) => RemoveSelectedTarget()));
        targetButtons.Children.Add(ActionButton("打开选中项", (_, _) => OpenSelectedTarget()));
        page.Children.Add(targetButtons);
        page.Children.Add(Heading("Windows 快捷动作"));
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("锁定电脑", (_, _) => LockComputer()));
        actions.Children.Add(ActionButton("音量 −", (_, _) => ChangeVolume(-0.05f)));
        actions.Children.Add(ActionButton("音量 +", (_, _) => ChangeVolume(0.05f)));
        actions.Children.Add(ActionButton("截取全部屏幕", (_, _) => CaptureScreen()));
        actions.Children.Add(ActionButton("读取剪贴板到对话框", (_, _) => ReadClipboard()));
        actions.Children.Add(ActionButton("复制浅梦问候语", (_, _) => CopyGreeting()));
        page.Children.Add(actions);
        RefreshTargets();
        return Scroll(page);
    }

    private UIElement BuildSettings()
    {
        var page = Page();
        page.Children.Add(Heading("桌宠大小"));
        var size = new Slider { Minimum = 0.6, Maximum = 1.8, Value = _settings.PetSize, TickFrequency = 0.1, SmallChange = 0.05, LargeChange = 0.1, IsSnapToTickEnabled = true };
        var sizeLabel = new TextBlock { Text = $"{size.Value:P0} · 默认形象与 GLB 均适用", Margin = new Thickness(0, 4, 0, 8) };
        size.ValueChanged += (_, _) => sizeLabel.Text = $"{size.Value:P0} · 点击应用大小后生效";
        page.Children.Add(Labeled("桌宠整体大小（60%–180%）", size)); page.Children.Add(sizeLabel);
        page.Children.Add(ActionButton("应用大小", (_, _) => { _owner.SetPetSize(size.Value); sizeLabel.Text = $"{size.Value:P0} · 已保存"; }));
        page.Children.Add(Heading("桌面与隐私"));
        _clickThrough.IsChecked = _settings.ClickThrough;
        _hideFullscreen.IsChecked = _settings.AutoHideFullscreen;
        _contextDetection.IsChecked = _settings.ContextDetectionEnabled;
        _privacyMode.IsChecked = _settings.PrivacyMode;
        _autoStart.IsChecked = _settings.AutoStart;
        _speakReplies.IsChecked = _settings.SpeakReplies;
        page.Children.Add(_clickThrough);
        page.Children.Add(_hideFullscreen);
        page.Children.Add(_contextDetection);
        page.Children.Add(_privacyMode);
        page.Children.Add(_autoStart);
        page.Children.Add(_speakReplies);
        page.Children.Add(new TextBlock
        {
            Text = "隐私模式会停止读取前台进程映像名；全屏隐藏仍仅根据窗口大小与显示器边界在本机判断。任何应用名都不会上传。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10)
        });
        var petName = new TextBox { Text = _settings.PetName, MaxLength = 40 };
        page.Children.Add(Labeled("桌宠名称", petName));
        _processNames.Text = string.Join(Environment.NewLine, _settings.WatchedProcesses);
        page.Children.Add(Labeled("参与本机情境识别的进程名（每行一个）", _processNames));
        page.Children.Add(new TextBlock
        {
            Text = "这里只保存你配置的进程名白名单。窗口标题、命令行、网页地址和文档内容不会读取。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        page.Children.Add(Heading("本地诊断日志"));
        page.Children.Add(new TextBlock
        {
            Text = "日志只保存在这台电脑，不包含聊天内容或前台应用名。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        page.Children.Add(ActionButton("打开诊断日志文件夹", (_, _) => OpenDiagnosticLogs()));
        page.Children.Add(ActionButton("保存设置", (_, _) =>
        {
            _settings.ClickThrough = _clickThrough.IsChecked == true;
            _settings.AutoHideFullscreen = _hideFullscreen.IsChecked == true;
            _settings.ContextDetectionEnabled = _contextDetection.IsChecked == true;
            _settings.AutoStart = _autoStart.IsChecked == true;
            _settings.SpeakReplies = _speakReplies.IsChecked == true;
            _settings.PetName = string.IsNullOrWhiteSpace(petName.Text) ? "浅梦" : petName.Text.Trim();
            _settings.WatchedProcesses = _processNames.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !x.Contains('/') && !x.Contains('\\'))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
            try
            {
                _owner.SetPrivacyMode(_privacyMode.IsChecked == true);
                _owner.SaveSettings();
                _owner.ShowStatus("设置已保存");
                _statusText.Text = "设置已保存";
            }
            catch (Exception ex) { ShowError("保存设置失败", ex); }
        }));
        page.Children.Add(ActionButton("清除全部成长数据", (_, _) => ResetGrowth()));
        return Scroll(page);
    }

    private UIElement BuildPlugins()
    {
        var page = Page();
        page.Children.Add(Heading("JSON 插件"));
        page.Children.Add(new TextBlock
        {
            Text = "MVP 插件只包含 JSON 数据，可按进程名触发一条本地台词；不执行脚本、命令、网络请求或文件操作。权限目前仅支持 pet.say。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        _pluginsList.Height = 300;
        page.Children.Add(_pluginsList);
        page.Children.Add(ActionButton("导入 JSON 插件", (_, _) => ImportPlugin()));
        page.Children.Add(ActionButton("移除选中插件", (_, _) => RemoveSelectedPlugin()));
        page.Children.Add(ActionButton("刷新插件列表", (_, _) => RefreshPlugins()));
        RefreshPlugins();
        return Scroll(page);
    }

    private void RefreshGrowth()
    {
        var pet = _owner.Pet;
        _levelText.Text = $"Lv. {pet.Level}";
        _xpText.Text = $"{pet.Xp} / {pet.XpToNextLevel} XP";
        _xpProgress.Maximum = pet.XpToNextLevel;
        _xpProgress.Value = pet.Xp;
        _growthText.Text = $"今日互动经验 {pet.DailyXp} / 25  ·  学习兑换单独计算  ·  今日好感 +{pet.DailyAffection} / 20";
        _affectionValue.Text = pet.Affection.ToString();
        _energyValue.Text = pet.Energy.ToString();
        _fullnessValue.Text = pet.Fullness.ToString();
        _moodValue.Text = pet.Mood.ToString();
    }

    private void Owner_StatusChanged(string status) => _statusText.Text = status;

    private void Owner_ContextStatusChanged(string status) => _contextStatusText.Text = "本机情境：" + status;

    private void OpenDiagnosticLogs()
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBuddy", "logs");
            Directory.CreateDirectory(logDirectory);
            Process.Start(new ProcessStartInfo(logDirectory) { UseShellExecute = true });
            _owner.ShowStatus("已打开本地诊断日志文件夹");
        }
        catch (Exception ex) { ShowError("打开诊断日志文件夹失败", ex); }
    }

    public void RefreshClickThrough(bool enabled) => _clickThrough.IsChecked = enabled;

    public void RefreshPrivacyMode(bool enabled) => _privacyMode.IsChecked = enabled;

    private async Task SendMessageAsync()
    {
        if (_chatBusy) return;
        var text = _messageInput.Text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 4000)
        {
            MessageBox.Show(this, "单条消息最多 4000 个字符。", "消息过长", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.ChatEndpoint = _endpointInput.Text.Trim();
        _settings.ChatModel = string.IsNullOrWhiteSpace(_modelInput.Text) ? "gpt-4o-mini" : _modelInput.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.ChatEndpoint)
            && (!_settings.RemoteChatConsent || !string.Equals(_settings.RemoteChatConsentEndpoint, _settings.ChatEndpoint, StringComparison.Ordinal)))
        {
            var answer = MessageBox.Show(this,
                "这条消息和本页最近几轮对话将发送到你填写的模型接口。桌伴不会发送前台应用名或系统情境。是否授权？",
                "确认远程聊天", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                MessageBox.Show(this, "已取消发送。清空接口地址即可使用本地离线回应。", "未发送", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _settings.RemoteChatConsent = true;
            _settings.RemoteChatConsentEndpoint = _settings.ChatEndpoint;
        }
        try { _owner.SaveSettings(); }
        catch (Exception ex)
        {
            ShowError("保存聊天设置失败", ex);
            return;
        }
        AddChatLine("user", text);
        _history.Add(new ChatLine("user", text));
        _messageInput.Clear();
        _chatBusy = true;
        _chatCancellation?.Dispose();
        _chatCancellation = new CancellationTokenSource();
        var cancellationToken = _chatCancellation.Token;
        if (_sendButton is not null) { _sendButton.IsEnabled = false; _sendButton.Content = "正在回复…"; }
        try
        {
            var response = await _owner.Chat.ReplyAsync(text, _history.Take(_history.Count - 1).ToArray(),
                _settings.ChatEndpoint, _settings.ChatModel, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AddChatLine("assistant", response);
            _history.Add(new ChatLine("assistant", response));
            var levels = _owner.RewardInteraction();
            RefreshGrowth();
            if (levels > 0) _owner.ShowStatus($"升到 { _owner.Pet.Level } 级了！");
            if (_settings.SpeakReplies)
            {
                try { await SapiSpeechService.SpeakAsync(response); }
                catch (Exception ex) { AddChatLine("assistant", "文字回复已完成，但系统朗读失败：" + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Clearing the conversation or closing this panel cancels pending work.
        }
        catch (Exception ex)
        {
            AddChatLine("assistant", "这次没有连上聊天服务。" + Environment.NewLine + ex.Message);
        }
        finally
        {
            _chatBusy = false;
            if (_sendButton is not null) { _sendButton.IsEnabled = true; _sendButton.Content = "发送"; }
        }
    }

    private void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control))
        {
            e.Handled = true;
            _ = SendMessageAsync();
        }
    }

    private async Task SpeakLatestAsync()
    {
        var last = _history.LastOrDefault(x => x.Role == "assistant");
        if (last is null) return;
        try { await SapiSpeechService.SpeakAsync(last.Content); }
        catch (Exception ex) { ShowError("Windows 语音不可用", ex); }
    }

    private void SaveApiKey()
    {
        if (string.IsNullOrWhiteSpace(_apiKeyInput.Password))
        {
            MessageBox.Show(this, "请输入新的 API Key。留空不会删除已有密钥，请使用“删除已保存密钥”。", "没有输入密钥", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            CredentialStore.Save(_apiKeyInput.Password);
            _apiKeyInput.Clear();
            _owner.ShowStatus("API Key 已保存到 Windows 凭据管理器");
        }
        catch (Exception ex) { ShowError("保存密钥失败", ex); }
    }

    private void ClearConversation()
    {
        _chatCancellation?.Cancel();
        _history.Clear();
        _conversation.Children.Clear();
        AddChatLine("assistant", "本次对话已清空。聊天内容不会写入本地历史。 ");
    }

    private void AddChatLine(string role, string text, ScrollViewer? scroll = null)
    {
        var card = new Border
        {
            Background = role == "user" ? new Media.SolidColorBrush(Media.Color.FromRgb(237, 233, 255)) : new Media.SolidColorBrush(Media.Color.FromRgb(242, 244, 248)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(8, 7, 8, 0),
            HorizontalAlignment = role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 450,
            Child = new TextBlock { Text = (role == "user" ? "你：" : "浅梦：") + text, TextWrapping = TextWrapping.Wrap }
        };
        _conversation.Children.Add(card);
        scroll ??= _conversationScroll ?? FindConversationScroll();
        if (scroll is not null) scroll.ScrollToEnd();
    }

    private ScrollViewer? FindConversationScroll()
    {
        return FindVisualParent<ScrollViewer>(_conversation);
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = Media.VisualTreeHelper.GetParent(child);
        while (parent is not null && parent is not T) parent = Media.VisualTreeHelper.GetParent(parent);
        return parent as T;
    }

    private void RefreshTargets()
    {
        _targetsList.ItemsSource = null;
        _targetsList.ItemsSource = _settings.ApprovedTargets;
        _targetsList.DisplayMemberPath = nameof(ApprovedTarget.Name);
    }

    private void AddPathTarget()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要加入白名单的 exe 或 lnk",
            Filter = "程序与快捷方式|*.exe;*.lnk",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        AddTarget(new ApprovedTarget { Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), Value = dialog.FileName, Kind = "path" });
    }

    private void AddTextTarget(string kind, string title, string prompt)
    {
        var value = Prompt(title, prompt);
        if (string.IsNullOrWhiteSpace(value)) return;
        var name = kind == "url" ? value : value.Split('!').LastOrDefault() ?? "UWP 应用";
        AddTarget(new ApprovedTarget { Name = name, Value = value.Trim(), Kind = kind });
    }

    private void AddTarget(ApprovedTarget target)
    {
        if (_settings.ApprovedTargets.Any(x => x.Kind == target.Kind && string.Equals(x.Value, target.Value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "这个目标已经在白名单中。", "重复目标", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _ = SystemActions.CreateStartInfo(target);
            _settings.ApprovedTargets.Add(target);
            try { _owner.SaveSettings(); }
            catch { _settings.ApprovedTargets.Remove(target); throw; }
            RefreshTargets();
        }
        catch (Exception ex) { ShowError("目标未添加", ex); }
    }

    private void RemoveSelectedTarget()
    {
        if (_targetsList.SelectedItem is not ApprovedTarget target) return;
        var index = _settings.ApprovedTargets.IndexOf(target);
        _settings.ApprovedTargets.Remove(target);
        try { _owner.SaveSettings(); RefreshTargets(); }
        catch (Exception ex)
        {
            _settings.ApprovedTargets.Insert(index, target);
            ShowError("目标未移除", ex);
        }
    }

    private void OpenSelectedTarget()
    {
        if (_targetsList.SelectedItem is not ApprovedTarget target) return;
        if (MessageBox.Show(this, $"将打开“{target.Name}”。\n\n目标：{target.Value}\n\n确认继续？", "确认打开", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { SystemActions.OpenApprovedTarget(target); }
        catch (Exception ex) { ShowError("无法打开目标", ex); }
    }

    private void LockComputer()
    {
        if (MessageBox.Show(this, "Windows 将立即锁定。是否继续？", "确认锁屏", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { SystemActions.LockComputer(); }
        catch (Exception ex) { ShowError("锁屏失败", ex); }
    }

    private void ChangeVolume(float delta)
    {
        try
        {
            AudioEndpointService.ChangeVolume(delta);
            _owner.ShowStatus($"系统音量 {AudioEndpointService.GetVolume():P0}");
        }
        catch (Exception ex) { ShowError("无法调整系统音量", ex); }
    }

    private void CopyGreeting()
    {
        if (MessageBox.Show(this, "这会用浅梦问候语替换当前剪贴板内容。是否继续？", "确认写入剪贴板", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            Clipboard.SetText("你好，我是浅梦。我会安静地陪着你。 ");
            _owner.ShowStatus("问候语已复制到剪贴板");
        }
        catch (Exception ex) { ShowError("写入剪贴板失败", ex); }
    }

    private void PlayCoinGame()
    {
        var guess = Prompt("猜硬币", "输入“正面”或“反面”，看看能不能猜中：");
        if (string.IsNullOrWhiteSpace(guess)) return;
        var result = Random.Shared.Next(2) == 0 ? "正面" : "反面";
        if (string.Equals(guess.Trim(), result, StringComparison.Ordinal))
        {
            var levels = _owner.RewardInteraction(5, 3);
            RefreshGrowth();
            _owner.PlayEmote("🎉", $"猜中了，是{result}！获得 5 XP。" + (levels > 0 ? $"升到 {_owner.Pet.Level} 级！" : ""));
        }
        else
        {
            _owner.PlayEmote("🤭", $"这次是{result}，下次再来。没有扣分。 ");
        }
    }

    private void CaptureScreen()
    {
        if (MessageBox.Show(this, "截图会捕获所有显示器当前画面，并在保存前询问位置。确认截图？", "确认截图", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _owner.ShowStatus(SystemActions.CaptureVirtualDesktop()); }
        catch (Exception ex) { ShowError("截图失败", ex); }
    }

    private void ReadClipboard()
    {
        if (MessageBox.Show(this, "桌伴只在本次操作中读取剪贴板，不会保存或监听变化。是否读取？", "确认读取剪贴板", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var value = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (string.IsNullOrEmpty(value))
            {
                MessageBox.Show(this, "剪贴板没有文本内容。", "剪贴板", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (value.Length > 4000)
            {
                if (MessageBox.Show(this, "剪贴板文本超过 4000 个字符。只放入前 4000 个字符？", "剪贴板文本较长", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                value = value[..4000];
            }
            _messageInput.Text = value;
            _navigation.SelectedIndex = 1;
            _messageInput.Focus();
            _owner.ShowStatus("剪贴板文字已放入对话输入框，没有写入历史");
        }
        catch (Exception ex) { ShowError("读取剪贴板失败", ex); }
    }

    private void ImportPlugin()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入 JSON 插件", Filter = "JSON 文件|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var preview = _owner.Plugins.Inspect(dialog.FileName);
            var isReplacement = _owner.Plugins.LoadAll().Any(x => string.Equals(x.Id, preview.Id, StringComparison.OrdinalIgnoreCase));
            var prompt = isReplacement
                ? $"插件“{preview.Name}”的 ID 已存在。是否用所选 JSON 替换它？"
                : $"导入“{preview.Name}”？插件仅包含本地台词规则，不会执行代码或网络请求。";
            if (MessageBox.Show(this, prompt, isReplacement ? "确认替换插件" : "确认导入插件", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var plugin = _owner.Plugins.Import(dialog.FileName, replaceExisting: isReplacement);
            RefreshPlugins();
            MessageBox.Show(this, $"已导入“{plugin.Name}”。它只能按规则显示本地台词。", "插件已导入", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("插件未导入", ex); }
    }

    private void RefreshPlugins()
    {
        _pluginsList.ItemsSource = _owner.Plugins.LoadAll();
    }

    private void RemoveSelectedPlugin()
    {
        if (_pluginsList.SelectedItem is not PluginManifest plugin) return;
        if (MessageBox.Show(this, $"移除插件“{plugin.Name}”？", "确认移除插件", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!_owner.Plugins.Remove(plugin))
        {
            MessageBox.Show(this, "无法移除插件文件。", "移除失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        RefreshPlugins();
        _owner.ShowStatus($"已移除插件“{plugin.Name}”");
    }

    private void ResetGrowth()
    {
        if (MessageBox.Show(this, "这会清除等级、经验、好感、货币、背包、已购配色和活动状态。不能撤销。模型与聊天设置会保留。确认清除？", "清除成长数据", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _owner.ResetCare();
        RefreshGrowth();
    }

    private string? Prompt(string title, string prompt)
    {
        var dialog = new System.Windows.Window
        {
            Title = title,
            Width = 430,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = this
        };
        var input = new TextBox { Margin = new Thickness(0, 8, 0, 10) };
        var ok = new Button { Content = "确定", Width = 90, IsDefault = true, Margin = new Thickness(5) };
        var cancel = new Button { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(5) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(14), Children = { new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap }, input, buttons } };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void ShowError(string title, Exception ex) =>
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
