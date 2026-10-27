using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopBuddy.Models;
using DesktopBuddy.Services;
using Forms = System.Windows.Forms;

namespace DesktopBuddy;

public partial class MainWindow : System.Windows.Window
{
    private const int HotkeyShowHide = 1;
    private const int HotkeyInteraction = 2;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly uint TaskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    private readonly LocalStore _store;
    private readonly Action<bool> _configureAutoStart;
    private readonly ChatService _chatService;
    private readonly PluginService _pluginService;
    private readonly DispatcherTimer _foregroundTimer;
    private readonly DispatcherTimer _dragSnapTimer;
    private DispatcherTimer? _emoteTimer;
    private AppSettings _settings;
    private PetState _pet;
    private Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayClickThroughItem;
    private Forms.ToolStripMenuItem? _trayPrivacyItem;
    private HwndSource? _source;
    private ControlCenterWindow? _controlCenter;
    private bool _exitRequested;
    private bool _showHotkeyRegistered;
    private bool _interactionHotkeyRegistered;
    private bool _userHidden;
    private bool _hiddenByFullscreen;
    private bool _leftPressed;
    private bool _dragStarted;
    private System.Windows.Point _pressPosition;
    private DateTime? _fullscreenStartedAt;
    private DateTime? _fullscreenEndedAt;
    private DateTime _lastForegroundErrorShownAt = DateTime.MinValue;
    private string _lastProcessName = "";
    private string _currentStatus = "普通待机";

    public MainWindow() : this(new LocalStore())
    {
    }

    internal MainWindow(LocalStore store, Action<bool>? configureAutoStart = null, ChatService? chatService = null)
    {
        _store = store;
        _configureAutoStart = configureAutoStart ?? SystemActions.SetAutoStart;
        _chatService = chatService ?? new ChatService();
        _settings = _store.LoadSettings();
        _pet = _store.LoadPetState();
        _pet.ResetDailyIfNeeded();
        _pluginService = new PluginService(_store.PluginsDirectory);
        InitializeComponent();
        InitializeInteractions();
        PetModel.MouseEnter += (_, _) => RefreshAnimationPlayback();
        PetModel.MouseLeave += (_, _) => RefreshAnimationPlayback();
        ApplySkin();
        ApplyPetSize();
        System.Windows.Automation.AutomationProperties.SetName(this, _settings.PetName);
        PetNameText.Text = _settings.PetName;
        var initialLeft = _settings.WindowLeft ?? SystemParameters.PrimaryScreenWidth - Width - 30;
        var initialTop = _settings.WindowTop ?? SystemParameters.PrimaryScreenHeight - Height - 90;
        var initialPosition = ClampToVirtualScreen(initialLeft, initialTop);
        Left = initialPosition.Left;
        Top = initialPosition.Top;

        _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _foregroundTimer.Tick += ForegroundTimer_Tick;
        _dragSnapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _dragSnapTimer.Tick += DragSnapTimer_Tick;

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Deactivated += (_, _) => ResetPointerInteraction();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { ResetPointerInteraction(); ResetInteractionMotion(); } RefreshAnimationPlayback(); };
        ContextMenuOpening += (_, _) => ResetPointerInteraction();
        SetContextStatus(_settings.PrivacyMode ? "隐私模式：暂停应用识别"
            : _settings.ContextDetectionEnabled ? "等待识别前台应用" : "应用识别已关闭");
        UpdatePetView();
    }

    public AppSettings Settings => _settings;
    public PetState Pet => _pet;
    public LocalStore Store => _store;
    public ChatService Chat => _chatService;
    public PluginService Plugins => _pluginService;
    public string CurrentStatus => _currentStatus;
    public string CurrentCategory { get; private set; } = "unknown";
    public string ContextStatus { get; private set; } = "等待识别前台应用";
    public event Action<string>? StatusChanged;
    public event Action<string>? ContextStatusChanged;

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowMessageHook);
        _showHotkeyRegistered = NativeMethods.RegisterHotKey(hwnd, HotkeyShowHide, NativeMethods.ModControl | NativeMethods.ModAlt, 0x20); // Space
        _interactionHotkeyRegistered = NativeMethods.RegisterHotKey(hwnd, HotkeyInteraction, NativeMethods.ModControl | NativeMethods.ModAlt, 0x49); // I
        ApplyClickThroughStyle();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        _foregroundTimer.Start();
        _ = RestorePetModelAsync();
        if (_store.RecoveryWarnings.Count > 0)
            System.Windows.MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, _store.RecoveryWarnings), "浅梦 · 数据恢复提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (!_showHotkeyRegistered || !_interactionHotkeyRegistered)
        {
            var unavailable = new List<string>();
            if (!_showHotkeyRegistered) unavailable.Add("Ctrl + Alt + Space");
            if (!_interactionHotkeyRegistered) unavailable.Add("Ctrl + Alt + I");
            ShowStatus($"快捷键 {string.Join("、", unavailable)} 已被其他程序占用，可从托盘菜单操作。");
        }
        if (Environment.GetCommandLineArgs().Any(x => string.Equals(x, "--startup-hidden", StringComparison.OrdinalIgnoreCase)))
            HideToTray();
    }

    private void InitializeTrayIcon()
    {
        if (_notifyIcon is not null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示桌宠", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开控制中心", null, (_, _) => OpenControlCenter());
        menu.Items.Add("替换模型…", null, (_, _) => OpenModelManager());
        _trayClickThroughItem = new Forms.ToolStripMenuItem("点击穿透") { CheckOnClick = true, Checked = _settings.ClickThrough };
        _trayClickThroughItem.Click += (_, _) => SetClickThrough(_trayClickThroughItem.Checked);
        menu.Items.Add(_trayClickThroughItem);
        _trayPrivacyItem = new Forms.ToolStripMenuItem("隐私模式") { CheckOnClick = true, Checked = _settings.PrivacyMode };
        _trayPrivacyItem.Click += (_, _) => SetPrivacyMode(_trayPrivacyItem.Checked);
        menu.Items.Add(_trayPrivacyItem);
        menu.Items.Add("立即隐藏", null, (_, _) => HideToTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        try
        {
            // Read our embedded icon directly. Shell extraction on a newly built large
            // single-file EXE can stall the UI thread while shell/security providers inspect it.
            using var stream = typeof(App).Assembly.GetManifestResourceStream("DesktopBuddy.TrayIcon");
            if (stream is not null)
            {
                using var icon = new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
                _trayIcon = (System.Drawing.Icon)icon.Clone();
            }
        }
        catch { _trayIcon = null; }
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "浅梦 · 单击显示桌宠，双击打开控制中心",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) RestoreFromTray();
        };
        _notifyIcon.DoubleClick += (_, _) =>
        {
            RestoreFromTray();
            OpenControlCenter();
        };
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var position = ClampToVirtualScreen(Left, Top);
                Left = position.Left;
                Top = position.Top;
                SaveWindowPosition();
            }));
            return 0;
        }
        if (TaskbarCreatedMessage != 0 && (uint)message == TaskbarCreatedMessage)
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Visible = true;
            }
            handled = true;
            return 0;
        }
        if ((uint)message == NativeMethods.WmAppRestore)
        {
            RestoreFromTray();
            OpenControlCenter();
            handled = true;
            return 0;
        }
        if ((uint)message == NativeMethods.WmAppExit)
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
            handled = true;
            return 0;
        }
        if (message != NativeMethods.WmHotkey) return 0;
        var id = wParam.ToInt32();
        if (id == HotkeyShowHide)
        {
            if (IsVisible && !_userHidden) HideToTray(); else RestoreFromTray();
            handled = true;
        }
        else if (id == HotkeyInteraction)
        {
            SetClickThrough(!_settings.ClickThrough);
            ShowStatus(_settings.ClickThrough ? "已切换为点击穿透" : "已切换为交互模式");
            handled = true;
        }
        return 0;
    }

    private void CheckForeground()
    {
        var canReadProcess = _settings.ContextDetectionEnabled && !_settings.PrivacyMode;
        var snapshot = ForegroundMonitor.GetCurrent(canReadProcess);
        if (snapshot is null) return;
        var ownPid = (uint)Environment.ProcessId;
        var otherApp = snapshot.ProcessId != ownPid;
        var fullscreen = otherApp && snapshot.IsFullscreen && _settings.AutoHideFullscreen;

        if (fullscreen)
        {
            _fullscreenEndedAt = null;
            _fullscreenStartedAt ??= DateTime.UtcNow;
            if (!_hiddenByFullscreen && DateTime.UtcNow - _fullscreenStartedAt.Value >= TimeSpan.FromMilliseconds(700))
            {
                _hiddenByFullscreen = true;
                if (!_userHidden) Hide();
            }
        }
        else
        {
            _fullscreenStartedAt = null;
            if (_hiddenByFullscreen)
            {
                _fullscreenEndedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _fullscreenEndedAt.Value >= TimeSpan.FromMilliseconds(1700))
                {
                    _hiddenByFullscreen = false;
                    _fullscreenEndedAt = null;
                    if (!_userHidden) ShowWithoutActivation();
                }
            }
        }

        if (!canReadProcess || !otherApp) return;
        var processName = snapshot.ExecutableName;
        if (string.IsNullOrWhiteSpace(processName))
        {
            _lastProcessName = "";
            CurrentCategory = "unknown";
            SetContextStatus("当前应用暂不可读，可能受系统权限限制");
            return;
        }
        if (string.Equals(processName, _lastProcessName, StringComparison.OrdinalIgnoreCase)) return;
        _lastProcessName = processName;
        var watched = _settings.WatchedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);
        var context = watched ? ContextCatalog.FromExecutable(processName) : ContextCatalog.FromExecutable("");
        CurrentCategory = context.Category;
        _currentStatus = _settings.PrivacyMode ? "隐私模式" : context.Status;
        SetContextStatus(watched ? context.Status : "当前应用不在识别白名单");
        if (!IsVisible && !_userHidden && !_hiddenByFullscreen) ShowWithoutActivation();
        UpdatePetView(context.Category);
        StatusChanged?.Invoke(_currentStatus);

        var pluginLine = watched ? _pluginService.GetTriggeredLine(processName) : null;
        if (!string.IsNullOrWhiteSpace(pluginLine)) ShowStatus(pluginLine);
    }

    private void ShowWithoutActivation()
    {
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwShownoactivate);
        NativeMethods.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoMove | NativeMethods.SwpNoActivate);
    }

    private void SetClickThrough(bool enabled)
    {
        _settings.ClickThrough = enabled;
        if (_trayClickThroughItem is not null) _trayClickThroughItem.Checked = enabled;
        _controlCenter?.RefreshClickThrough(enabled);
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { ShowStatus("点击穿透已切换，但设置保存失败：" + ex.Message); }
        ApplyClickThroughStyle();
    }

    private void ApplyClickThroughStyle()
    {
        if (_trayClickThroughItem is not null) _trayClickThroughItem.Checked = _settings.ClickThrough;
        if (_settings.ClickThrough) ResetPointerInteraction();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExLayered;
        style = _settings.ClickThrough ? style | NativeMethods.WsExTransparent : style & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new nint(style));
    }

    public void SetPrivacyMode(bool enabled)
    {
        _settings.PrivacyMode = enabled;
        if (_trayPrivacyItem is not null) _trayPrivacyItem.Checked = enabled;
        if (enabled)
        {
            _lastProcessName = "";
            CurrentCategory = "private";
            _currentStatus = "隐私模式：暂停应用识别";
        }
        else
        {
            _lastProcessName = "";
            CurrentCategory = "unknown";
            _currentStatus = "隐私模式已关闭";
        }
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { ShowStatus("隐私模式已切换，但设置保存失败：" + ex.Message); }
        UpdatePetView(enabled ? "private" : CurrentCategory);
        SetContextStatus(enabled ? "隐私模式：暂停应用识别"
            : _settings.ContextDetectionEnabled ? "等待识别前台应用" : "应用识别已关闭");
        _controlCenter?.RefreshPrivacyMode(enabled);
    }

    public void SaveSettings()
    {
        if (_store.SettingsReadOnly)
            throw new InvalidOperationException("设置文件来自较新版本，或损坏文件未能安全备份；为保护原文件，当前无法保存。请查看数据恢复提示，先备份原文件再处理。");
        _lastProcessName = "";
        SetContextStatus(_settings.PrivacyMode ? "隐私模式：暂停应用识别"
            : _settings.ContextDetectionEnabled ? "等待识别前台应用" : "应用识别已关闭");
        if (_settings.PrivacyMode || !_settings.ContextDetectionEnabled)
        {
            CurrentCategory = _settings.PrivacyMode ? "private" : "unknown";
            _currentStatus = _settings.PrivacyMode ? "隐私模式：暂停应用识别" : "普通待机";
            UpdatePetView(CurrentCategory);
        }
        _configureAutoStart(_settings.AutoStart);
        _store.SaveSettings(_settings);
        ApplyClickThroughStyle();
        PetNameText.Text = string.IsNullOrWhiteSpace(_settings.PetName) ? "浅梦" : _settings.PetName;
        System.Windows.Automation.AutomationProperties.SetName(this, PetNameText.Text);
        ShowStatus("设置已保存");
    }

    public void SavePet()
    {
        _pet.ResetDailyIfNeeded();
        try { _store.SavePetState(_pet); }
        catch (Exception ex) { ShowStatus("成长状态已更新，但存档失败：" + ex.Message); }
        UpdatePetView();
    }

    public int RewardInteraction(int xp = 2, int affection = 2)
    {
        var levels = _pet.AddXp(xp);
        _pet.AddAffection(affection);
        SavePet();
        return levels;
    }

    public void ShowStatus(string status)
    {
        _currentStatus = status;
        PetStatusText.Text = status;
        StatusChanged?.Invoke(status);
        if (_notifyIcon is { Visible: true } && !IsVisible)
            _notifyIcon.ShowBalloonTip(2500, "浅梦", status, Forms.ToolTipIcon.Info);
    }

    private void SetContextStatus(string status)
    {
        if (ContextStatus == status) return;
        ContextStatus = status;
        ContextStatusChanged?.Invoke(status);
    }

    private void ForegroundTimer_Tick(object? sender, EventArgs e)
    {
        try { TickCare(); CheckForeground(); }
        catch (Exception ex)
        {
            _lastProcessName = "";
            CurrentCategory = "unknown";
            SetContextStatus("本机检测不可用，请查看诊断日志");
            var logResult = CrashLogger.WriteThrottled("foreground-monitor", ex);
            if (DateTime.UtcNow - _lastForegroundErrorShownAt < TimeSpan.FromMinutes(1)) return;
            _lastForegroundErrorShownAt = DateTime.UtcNow;
            var logStatus = logResult == ThrottledLogResult.Failed
                ? "本地诊断日志当前无法写入。"
                : "详情已记录在本地诊断日志。";
            try { ShowStatus($"本机状态检测暂时不可用；桌宠仍可继续使用。{logStatus}"); }
            catch (Exception statusException) { CrashLogger.WriteThrottled("foreground-status", statusException); }
        }
    }

    private void DragSnapTimer_Tick(object? sender, EventArgs e)
    {
        _dragSnapTimer.Stop();
        try
        {
            SnapToScreenEdge();
            SaveWindowPosition();
        }
        catch (Exception ex)
        {
            CrashLogger.WriteThrottled("window-snap", ex);
        }
    }

    internal void ShutdownAfterFatalError()
    {
        _exitRequested = true;
        System.Windows.Application.Current.Shutdown(1);
    }

    public void PlayEmote(string emoji, string status)
    {
        _emoteTimer?.Stop();
        PetEmoji.Text = emoji;
        ShowStatus(status);
        _emoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _emoteTimer.Tick += (_, _) =>
        {
            _emoteTimer.Stop();
            UpdatePetView(CurrentCategory);
        };
        _emoteTimer.Start();
    }

    public void OpenControlCenter()
    {
        ResetPointerInteraction();
        if (_controlCenter is { IsVisible: true })
        {
            if (_controlCenter.WindowState == WindowState.Minimized)
                _controlCenter.WindowState = WindowState.Normal;
            _controlCenter.Activate();
            return;
        }
        _controlCenter = new ControlCenterWindow(this);
        _controlCenter.Closed += (_, _) => _controlCenter = null;
        _controlCenter.Show();
        _controlCenter.Activate();
    }

    public void HideToTray()
    {
        ResetPointerInteraction();
        _dragSnapTimer.Stop();
        _userHidden = true;
        Hide();
    }

    public void RestoreFromTray()
    {
        _userHidden = false;
        if (!_hiddenByFullscreen) ShowWithoutActivation();
    }

    public void EnablePetInteraction()
    {
        SetClickThrough(false);
        RestoreFromTray();
        ShowStatus("已恢复鼠标交互。按住桌宠即可拖动，双击打开控制中心。");
    }

    public void TogglePrivacyFromMenu() => SetPrivacyMode(!_settings.PrivacyMode);

    private void ToggleClickThrough_Click(object sender, RoutedEventArgs e) => SetClickThrough(!_settings.ClickThrough);
    private void TogglePrivacy_Click(object sender, RoutedEventArgs e) => TogglePrivacyFromMenu();
    private void OpenControlCenter_Click(object sender, RoutedEventArgs e) => OpenControlCenter();
    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void RootGrid_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough) return;
        _dragSnapTimer.Stop();
        ResetPointerInteraction();
        if (e.ClickCount >= 2)
        {
            OpenControlCenter();
            e.Handled = true;
            return;
        }
        _pressPosition = e.GetPosition(this);
        // Capture on the same element that receives move/up, so releasing outside
        // the pet still reaches these handlers instead of trapping the mouse.
        _leftPressed = RootGrid.CaptureMouse();
        PressGesture(_leftPressed);
        RefreshAnimationPlayback();
        e.Handled = true;
    }

    private void RootGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_leftPressed || _dragStarted) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            ResetPointerInteraction();
            return;
        }
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _pressPosition.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _pressPosition.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        ResetPointerInteraction();
        _dragStarted = true;
        BeginDragGesture();
        RefreshAnimationPlayback();
        try { DragMove(); }
        catch (InvalidOperationException ex) { CrashLogger.WriteThrottled("pet-drag", ex); }
        finally
        {
            ResetPointerInteraction();
            EndDragGesture();
            if (IsVisible)
            {
                _dragSnapTimer.Stop();
                _dragSnapTimer.Start();
            }
        }
        e.Handled = true;
    }

    private void RootGrid_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_leftPressed) return;
        ResetPointerInteraction();
        GreetGesture();
        ShowStatus($"浅梦：{InteractionActionLabel}。继续点击可换动作，双击打开控制中心。");
        _dragSnapTimer.Stop();
        e.Handled = true;
    }

    private void RootGrid_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => ResetPointerInteraction();

    private void ResetPointerInteraction()
    {
        _leftPressed = false;
        _dragStarted = false;
        PressGesture(false);
        if (RootGrid.IsMouseCaptured) RootGrid.ReleaseMouseCapture();
        RefreshAnimationPlayback();
    }

    private void SnapToScreenEdge()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0) return;
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = rect.Left;
        var y = rect.Top;
        const int threshold = 22;
        if (Math.Abs(rect.Left - work.Left) < threshold) x = work.Left;
        else if (Math.Abs(work.Right - rect.Right) < threshold) x = work.Right - width;
        if (Math.Abs(rect.Top - work.Top) < threshold) y = work.Top;
        else if (Math.Abs(work.Bottom - rect.Bottom) < threshold) y = work.Bottom - height;
        NativeMethods.SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    private void SaveWindowPosition()
    {
        if (IsVisible)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            try { _store.SaveSettings(_settings); }
            catch (Exception ex) { ShowStatus("桌宠位置保存失败：" + ex.Message); }
        }
    }

    private void UpdatePetView(string? category = null)
    {
        PetNameText.Text = string.IsNullOrWhiteSpace(_settings.PetName) ? "浅梦" : _settings.PetName;
        PetEmoji.Text = category switch
        {
            "coding" => "🐧",
            "gaming" => "🎮",
            "meeting" => "🤫",
            "private" => "🌙",
            _ when _pet.Mood > 70 => "😊",
            _ when _pet.Energy < 20 => "😴",
            _ => "🐧"
        };
        if (_pet.Activity != "") PetEmoji.Text = _pet.Activity switch { "sleep" => "😴", "study" => "📚", "exercise" => "💪", "play" => "🎮", _ => "💼" };
        UpdateActionStatusText();
        RefreshAnimationPlayback();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ResetPointerInteraction();
        if (!_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _modelCancellation?.Cancel();
        PetModel.Scene = null; ModelScene = null;
        SaveWindowPosition();
        try { _store.SaveSettings(_settings); } catch { }
        try { _store.SavePetState(_pet); } catch { }
        ResetInteractionMotion();
        _animationPlayer?.Dispose(); _animationPlayer = null;
        _foregroundTimer.Stop();
        _dragSnapTimer.Stop();
        _emoteTimer?.Stop();
        if (_source is not null)
        {
            if (_showHotkeyRegistered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyShowHide);
            if (_interactionHotkeyRegistered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyInteraction);
            _source.RemoveHook(WindowMessageHook);
        }
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private (double Left, double Top) ClampToVirtualScreen(double left, double top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        if (virtualWidth <= 0 || virtualHeight <= 0)
            return (Math.Max(0, left), Math.Max(0, top));

        // Keep a visible strip of the pet on-screen when a monitor from the previous session is gone.
        const double visible = 72;
        var clampedLeft = Math.Clamp(left, virtualLeft - Width + visible, virtualLeft + virtualWidth - visible);
        var clampedTop = Math.Clamp(top, virtualTop - Height + visible, virtualTop + virtualHeight - visible);
        return (clampedLeft, clampedTop);
    }
}
