using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopBuddy.Services;
using Media = System.Windows.Media;

namespace DesktopBuddy;

public sealed partial class ControlCenterWindow
{
    private readonly ListBox _navigation = new();
    private readonly ContentControl _pageHost = new();
    private readonly TextBlock _pageTitle = new();
    private readonly TextBlock _pageSubtitle = new();
    private readonly TextBlock _levelText = new();
    private readonly TextBlock _xpText = new();
    private readonly ProgressBar _xpProgress = new();
    private readonly TextBlock _affectionValue = new();
    private readonly TextBlock _energyValue = new();
    private readonly TextBlock _fullnessValue = new();
    private readonly TextBlock _moodValue = new();
    private Button? _sendButton;
    private ScrollViewer? _conversationScroll;

    private sealed record NavigationPage(string Title, string Subtitle, string Glyph, UIElement View);

    private static Media.SolidColorBrush Brush(string color) =>
        new((Media.Color)Media.ColorConverter.ConvertFromString(color));

    private void BuildTabs()
    {
        var root = new Grid { Background = Background };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var rail = new Grid();
        rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rail.RowDefinitions.Add(new RowDefinition());
        rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new StackPanel { Margin = new Thickness(24, 28, 20, 28) };
        brand.Children.Add(new TextBlock { Text = "浅梦", FontSize = 27, FontWeight = FontWeights.SemiBold, Foreground = Brush("#393051") });
        brand.Children.Add(new TextBlock { Text = "你的桌面伙伴", FontSize = 12, Foreground = Brush("#7E7791"), Margin = new Thickness(1, 4, 0, 0) });
        rail.Children.Add(brand);

        _navigation.Background = Media.Brushes.Transparent;
        _navigation.BorderThickness = new Thickness(0);
        _navigation.Padding = new Thickness(0);
        _navigation.ItemContainerStyle = (Style)Resources["NavigationItem"];
        AutomationProperties.SetName(_navigation, "功能导航");
        Grid.SetRow(_navigation, 1);
        rail.Children.Add(_navigation);

        var footer = new StackPanel { Margin = new Thickness(22, 16, 10, 22) };
        footer.Children.Add(new TextBlock { Text = "本地优先", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#5C5671") });
        footer.Children.Add(new TextBlock { Text = "前台应用名仅在本机使用", FontSize = 10, Foreground = Brush("#837E94"), Margin = new Thickness(0, 5, 0, 12) });
        footer.Children.Add(ActionButton("诊断日志", (_, _) => OpenDiagnosticLogs()));
        footer.Children.Add(new TextBlock { Text = "v" + typeof(ControlCenterWindow).Assembly.GetName().Version?.ToString(3) + " · x64", FontSize = 11, Foreground = Brush("#8A8598") });
        Grid.SetRow(footer, 2);
        rail.Children.Add(footer);
        root.Children.Add(new Border { Background = Brush("#F0EFF7"), BorderBrush = Brush("#E5E3EE"), BorderThickness = new Thickness(0, 0, 1, 0), Child = rail });

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        var header = new StackPanel { Margin = new Thickness(26, 24, 24, 22) };
        _pageTitle.FontSize = 26;
        _pageTitle.FontWeight = FontWeights.SemiBold;
        _pageTitle.Foreground = Brush("#262639");
        header.Children.Add(_pageTitle);
        _pageSubtitle.FontSize = 12;
        _pageSubtitle.Foreground = Brush("#777E91");
        _pageSubtitle.Margin = new Thickness(1, 5, 0, 0);
        _pageSubtitle.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_pageSubtitle);
        main.Children.Add(header);
        Grid.SetRow(_pageHost, 1);
        main.Children.Add(_pageHost);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        var pages = new[]
        {
            new NavigationPage("总览", "看看浅梦的状态，或者一起休息一会儿。", "\uE80F", BuildOverview()),
            new NavigationPage("对话", "从一句你好开始。默认使用本地离线回应。", "\uE8F2", BuildChat()),
            new NavigationPage("系统动作", "常用 Windows 操作，在你确认后执行。", "\uE945", BuildSystemActions()),
            new NavigationPage("设置", "让浅梦按你的习惯陪伴。", "\uE713", BuildSettings()),
            new NavigationPage("插件", "使用本地 JSON 台词规则扩展情境回应。", "\uEA86", BuildPlugins()),
            new NavigationPage("模型", "为浅梦换个样子，调整到合适的角度。", "\uE790", BuildModels()),
            new NavigationPage("商店", "用打工赚来的梦币，准备食物、水和新外观。", "\uE719", BuildShop())
        };
        foreach (var page in pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = page.Glyph, FontFamily = new Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 30, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = page.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = page };
            AutomationProperties.SetName(item, page.Title);
            _navigation.Items.Add(item);
        }
        _navigation.SelectionChanged += (_, _) =>
        {
            if (_navigation.SelectedItem is not ListBoxItem { Tag: NavigationPage page }) return;
            _pageTitle.Text = page.Title;
            _pageSubtitle.Text = page.Subtitle;
            _pageHost.Content = page.View;
        };
        _navigation.SelectedIndex = 0;
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && args.Key >= Key.D1 && args.Key <= Key.D7)
            {
                _navigation.SelectedIndex = args.Key - Key.D1;
                args.Handled = true;
            }
        };
        Content = root;
    }

    private UIElement BuildOverview()
    {
        var page = Page();
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        var hello = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hello.Children.Add(new TextBlock { Text = "浅梦正在陪着你", FontSize = 21, FontWeight = FontWeights.SemiBold });
        hello.Children.Add(new TextBlock { Text = "拖动调整位置，双击就能找到这里。", FontSize = 12, Foreground = Brush("#7A7D90"), Margin = new Thickness(0, 7, 0, 14) });
        _statusText.Text = _owner.CurrentStatus;
        _statusText.FontSize = 14;
        _statusText.Foreground = Brush("#6955B3");
        _statusText.TextWrapping = TextWrapping.Wrap;
        hello.Children.Add(_statusText);
        hero.Children.Add(hello);
        var preview = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(new TextBlock { Text = "🐧", FontSize = 58, FontFamily = new Media.FontFamily("Segoe UI Emoji"), HorizontalAlignment = HorizontalAlignment.Center });
        preview.Children.Add(new TextBlock { Text = _settings.PetName, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Foreground = Brush("#7C7293") });
        var portrait = new Border { Background = Brush("#F4F0FC"), CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Margin = new Thickness(12, 0, 0, 0), Child = preview };
        Grid.SetColumn(portrait, 1);
        hero.Children.Add(portrait);
        page.Children.Add(Card(hero));

        var status = new StackPanel();
        _contextStatusText.Text = "本机情境：" + _owner.ContextStatus;
        _contextStatusText.FontWeight = FontWeights.SemiBold;
        _contextStatusText.TextWrapping = TextWrapping.Wrap;
        status.Children.Add(_contextStatusText);
        status.Children.Add(new TextBlock { Text = "只识别白名单应用类别，不读取窗口内容。", FontSize = 11, Foreground = Brush("#7A7D90"), Margin = new Thickness(0, 5, 0, 12) });
        var visibility = new WrapPanel();
        visibility.Children.Add(Primary(ActionButton("恢复鼠标交互", (_, _) => _owner.EnablePetInteraction())));
        visibility.Children.Add(ActionButton("显示桌宠", (_, _) => _owner.RestoreFromTray()));
        visibility.Children.Add(ActionButton("隐藏到托盘", (_, _) => _owner.HideToTray()));
        status.Children.Add(visibility);
        page.Children.Add(Card(status));

        var growth = new StackPanel();
        var levelRow = new DockPanel();
        _xpText.Foreground = Brush("#80788F");
        _xpText.FontSize = 12;
        _xpText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_xpText, Dock.Right);
        levelRow.Children.Add(_xpText);
        _levelText.FontSize = 20;
        _levelText.FontWeight = FontWeights.SemiBold;
        levelRow.Children.Add(_levelText);
        growth.Children.Add(levelRow);
        _xpProgress.Margin = new Thickness(0, 12, 0, 10);
        growth.Children.Add(_xpProgress);
        _growthText.TextWrapping = TextWrapping.Wrap;
        _growthText.FontSize = 11;
        _growthText.Foreground = Brush("#858094");
        growth.Children.Add(_growthText);
        page.Children.Add(Card(growth));
        page.Children.Add(BuildCarePanel());
        page.Children.Add(BuildActivities());
        page.Children.Add(Heading("背包与外观"));
        page.Children.Add(BuildBackpack());
        page.Children.Add(Heading("轻松一下"));
        var fun = new WrapPanel();
        fun.Children.Add(ActionButton("卖个萌", (_, _) => _owner.PlayEmote("🥰", "给你一个抱抱。")));
        fun.Children.Add(ActionButton("跳一小段", (_, _) => _owner.PlayEmote("💃", "哒哒～跳完啦！")));
        fun.Children.Add(ActionButton("讲个笑话", (_, _) => _owner.PlayEmote("😄", "为什么程序员喜欢黑夜？因为白天的 bug 太显眼了。")));
        fun.Children.Add(ActionButton("猜硬币", (_, _) => PlayCoinGame()));
        fun.Children.Add(ActionButton("朗读欢迎语", async (_, _) =>
        {
            try { await SapiSpeechService.SpeakAsync("你好，我是浅梦。我会安静地陪着你。"); }
            catch (Exception ex) { ShowError("Windows 语音不可用", ex); }
        }));
        page.Children.Add(fun);
        page.Children.Add(new TextBlock
        {
            Text = "Ctrl + Alt + Space  显示或隐藏桌宠\nCtrl + Alt + I  切换点击穿透    ·    Ctrl + 1–7  切换页面",
            FontSize = 11, LineHeight = 20, Foreground = Brush("#8A8598"), Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        RefreshGrowth();
        return Scroll(page);
    }

    private static Border Card(UIElement content) => new()
    {
        Background = Media.Brushes.White, BorderBrush = Brush("#E6E7EF"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14), Child = content
    };

    private static Border Metric(string label, TextBlock value, string suffix)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brush("#898395") });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        value.FontSize = 23; value.FontWeight = FontWeights.SemiBold;
        row.Children.Add(value);
        row.Children.Add(new TextBlock { Text = suffix, FontSize = 10, Foreground = Brush("#A09AA9"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 4) });
        content.Children.Add(row);
        return new Border { Background = Media.Brushes.White, BorderBrush = Brush("#E6E7EF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(13), Margin = new Thickness(0, 0, 8, 0), Child = content };
    }

    private static StackPanel Page() => new() { Margin = new Thickness(26, 0, 24, 24), Orientation = Orientation.Vertical };
    private static ScrollViewer Scroll(UIElement content) => new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 12) };
    private static UIElement Labeled(string label, UIElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(control);
        if (control is FrameworkElement element) AutomationProperties.SetName(element, label);
        return stack;
    }

    private Button ActionButton(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetName(button, text);
        button.Click += (sender, args) =>
        {
            try { action(sender, args); }
            catch (Exception ex) { ShowError("操作未完成", ex); }
        };
        return button;
    }

    private static Button Primary(Button button)
    {
        button.SetResourceReference(StyleProperty, "PrimaryButton");
        return button;
    }
}
