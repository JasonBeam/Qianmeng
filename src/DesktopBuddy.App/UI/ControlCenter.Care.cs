using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopBuddy.Models;
using DesktopBuddy.Services;

namespace DesktopBuddy;

public sealed partial class ControlCenterWindow
{
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Value)> _careBars = new();
    private readonly StackPanel _backpack = new();
    private readonly StackPanel _shop = new();
    private readonly TextBlock _wallet = new();
    private readonly TextBlock _activityStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _careFeedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly List<(Button Button, PetActivity Activity)> _activityButtons = new();
    private Button? _cancelActivity;
    private Button? _spendSkill;
    private string _inventoryFingerprint = "";
    private int _shopBalance = -1;
    private string _shopSkins = "";
    private readonly List<(Button Button, ShopItem Item)> _buyButtons = new();

    private UIElement BuildCarePanel()
    {
        var stack = new StackPanel(); stack.Children.Add(Heading("状态属性"));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var (id, label, color) in new[] { ("food", "饱食", "#D5A154"), ("water", "水分", "#5D9EC6"), ("mood", "心情", "#B489BD"), ("health", "健康", "#69A995"), ("energy", "精力", "#8B80C9"), ("affection", "好感", "#CE879F") })
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 18, 15) };
            var row = new DockPanel(); var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush("#6C7085") });
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 7, 0, 0), Foreground = Brush(color), Background = Brush("#EFEEF5"), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(bar, label + "状态");
            cell.Children.Add(row); cell.Children.Add(bar); grid.Children.Add(cell); _careBars[id] = (bar, value);
        }
        stack.Children.Add(grid);
        stack.Children.Add(new TextBlock { Text = "饱食和水分越高越好。缺少食物或水会影响健康；离线最多结算 1 小时自然消耗。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#7A7D90") });
        return Card(stack);
    }
    private UIElement BuildActivities()
    {
        var stack = new StackPanel(); stack.Children.Add(Heading("日常活动"));
        stack.Children.Add(_wallet); stack.Children.Add(_activityStatus);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var activity in PetCareService.Activities)
        {
            var button = ActionButton($"{activity.Name} · {activity.Minutes} 分钟", (_, _) => RunCare(p => PetCareService.Start(p, activity.Id, DateTimeOffset.UtcNow)));
            button.ToolTip = $"精力 −{activity.Energy}\n完成后：{activity.Reward}";
            _activityButtons.Add((button, activity)); actions.Children.Add(button);
        }
        stack.Children.Add(actions);
        var commands = new WrapPanel();
        _cancelActivity = ActionButton("提前结束", (_, _) => RunCare(PetCareService.Cancel));
        _cancelActivity.ToolTip = "提前结束不获得奖励，已消耗精力不退还"; commands.Children.Add(_cancelActivity);
        _spendSkill = ActionButton("1 技能点 → 15 XP", (_, _) => RunCare(PetCareService.SpendSkill)); commands.Children.Add(_spendSkill);
        stack.Children.Add(commands); stack.Children.Add(_careFeedback);
        stack.Children.Add(new TextBlock { Text = "打工赚梦币 · 学习获得技能点 · 运动减缓消耗并提升健康上限\n玩耍恢复心情 · 睡觉恢复精力。活动互斥，完成后自动结算，重启不会重复领奖。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#7A7D90") });
        return Card(stack);
    }
    private UIElement BuildBackpack() => Card(_backpack);
    private UIElement BuildShop()
    {
        var page = Page();
        page.Children.Add(new TextBlock { Text = "所有投喂物品都需要用梦币购买，购买后在总览 → 背包使用。首次赠送 60 梦币。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#777E91"), Margin = new Thickness(0, 0, 0, 14) });
        page.Children.Add(_shop);
        foreach (var item in PetCareService.Items)
        {
            var row = new DockPanel();
            var button = ActionButton($"购买 · {item.Price} 梦币", (_, _) => RunCare(p => PetCareService.Buy(p, item.Id)));
            AutomationProperties.SetName(button, "购买" + item.Name); DockPanel.SetDock(button, Dock.Right); row.Children.Add(button); _buyButtons.Add((button, item));
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = item.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
            info.Children.Add(new TextBlock { Text = item.Description, FontSize = 12, Foreground = Brush("#777E91"), Margin = new Thickness(0, 6, 12, 0), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(info); page.Children.Add(Card(row));
        }
        var shopFeedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#6955B3") };
        shopFeedback.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = _careFeedback });
        page.Children.Insert(2, shopFeedback); return Scroll(page);
    }
    private void RunCare(Func<PetState, string> action)
    {
        try { _careFeedback.Text = _owner.CareAction(action); _careFeedback.Foreground = Brush("#4C8072"); RefreshCare(); }
        catch (Exception ex) { _careFeedback.Text = ex.Message; _careFeedback.Foreground = Brush("#A04E57"); }
    }
    private void RefreshCare()
    {
        RefreshGrowth(); var p = _owner.Pet;
        void Bar(string id, int value, int maximum, string? text = null)
        {
            if (!_careBars.TryGetValue(id, out var c)) return;
            c.Bar.Maximum = maximum; c.Bar.Value = value; c.Value.Text = text ?? $"{value} / {maximum}";
            AutomationProperties.SetHelpText(c.Bar, c.Value.Text);
        }
        Bar("food", p.Fullness, 100); Bar("water", p.Hydration, 100); Bar("mood", p.Mood, 100);
        Bar("health", p.Health, p.MaxHealth); Bar("energy", p.Energy, 100); Bar("affection", p.Affection, 1000);
        _wallet.Text = $"梦币 {p.Coins:N0}   ·   技能点 {p.SkillPoints}   ·   体能 {p.Fitness}（消耗减缓 {Math.Min(40, p.Fitness * 0.4):F1}%）";
        var activity = PetCareService.Activities.FirstOrDefault(x => x.Id == p.Activity);
        _activityStatus.Text = activity is null ? "当前空闲，选择一项活动吧。" : $"正在{activity.Name} · 剩余 {Math.Max(0, (int)Math.Ceiling(((p.ActivityEndsUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds)) / 60:D2} 分 {Math.Max(0, (int)((p.ActivityEndsUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds) % 60:D2} 秒 · 完成后 {activity.Reward}";
        foreach (var (button, a) in _activityButtons) button.IsEnabled = activity is null && p.Energy >= a.Energy;
        if (_cancelActivity is not null) _cancelActivity.IsEnabled = activity is not null;
        if (_spendSkill is not null) _spendSkill.IsEnabled = p.SkillPoints > 0;
        var skins = string.Join(",", p.OwnedSkins);
        if (_shopBalance != p.Coins || _shopSkins != skins)
        {
            _shopBalance = p.Coins; _shopSkins = skins; _shop.Children.Clear();
            _shop.Children.Add(new TextBlock { Text = $"可用梦币  {p.Coins:N0}", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
            foreach (var (button, item) in _buyButtons)
            {
                var owned = item.Skin != "" && p.OwnedSkins.Contains(item.Skin);
                button.IsEnabled = !owned && p.Coins >= item.Price;
                button.Content = owned ? "已拥有" : $"购买 · {item.Price} 梦币";
            }
        }
        var fingerprint = string.Join(";", p.Inventory.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) + skins + p.Skin + _settings.UseDefaultAppearance;
        if (_inventoryFingerprint == fingerprint) return;
        _inventoryFingerprint = fingerprint; _backpack.Children.Clear();
        if (p.Inventory.Count == 0) _backpack.Children.Add(new TextBlock { Text = "背包空空的。去商店准备一点食物和水吧。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#7A7D90"), Margin = new Thickness(0, 0, 0, 10) });
        foreach (var item in PetCareService.Items.Where(x => p.Inventory.GetValueOrDefault(x.Id) > 0))
        {
            var button = ActionButton($"使用 {item.Name} ×{p.Inventory[item.Id]}", (_, _) => RunCare(pet => PetCareService.Consume(pet, item.Id)));
            button.ToolTip = item.Description; _backpack.Children.Add(button);
        }
        var appearances = new WrapPanel();
        appearances.Children.Add(ActionButton("使用浅梦默认人物", (_, _) =>
        {
            try { _owner.ResetPetModel(); _inventoryFingerprint=""; RefreshCare(); }
            catch(Exception ex){_careFeedback.Text=ex.Message;}
        }));
        appearances.Children.Add(ActionButton("使用已导入模型", async (_, _) =>
        {
            try { await _owner.ChooseImportedAppearanceAsync(); _inventoryFingerprint = ""; RefreshCare(); }
            catch (Exception ex) { _careFeedback.Text = ex.Message; }
        }));
        _backpack.Children.Add(Heading("已拥有外观")); _backpack.Children.Add(appearances);
        _backpack.Children.Add(new TextBlock { Text = "默认形象为浅梦人物。原有配色购买记录保留，不会修改人物纹理。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#7A7D90") });
    }
}
