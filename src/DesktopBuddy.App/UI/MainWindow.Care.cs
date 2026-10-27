using System.Text.Json;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopBuddy.Models;
using DesktopBuddy.Services;

namespace DesktopBuddy;

public partial class MainWindow
{
    public event Action? CareChanged;
    private DateTimeOffset _lastCareCheck;
    private string? ActivityCaption
    {
        get
        {
            var a = PetCareService.Activities.FirstOrDefault(x => x.Id == _pet.Activity);
            if (a is null) return null;
            var seconds = Math.Max(0, (int)Math.Ceiling(((_pet.ActivityEndsUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds));
            return $"正在{a.Name} · {seconds / 60:D2}:{seconds % 60:D2}";
        }
    }
    public string CareAction(Func<PetState, string> action)
    {
        if (_store.PetStateReadOnly) throw new InvalidOperationException("存档受只读保护，无法修改成长数据。");
        var next = JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(_pet))!;
        PetCareService.Advance(next, DateTimeOffset.UtcNow);
        var message = action(next);
        _store.SavePetState(next); // Persist the complete purchase/consumption/reward atomically first.
        _pet = next; ApplySkin(); CareChanged?.Invoke(); ShowStatus(message); UpdatePetView(CurrentCategory); return message;
    }
    private void TickCare()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCareCheck < TimeSpan.FromSeconds(5)) return;
        _lastCareCheck = now;
        if (_store.PetStateReadOnly) return;
        var next = JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(_pet))!;
        if (!PetCareService.Advance(next, now)) { if (_pet.Activity != "") UpdatePetView(CurrentCategory); CareChanged?.Invoke(); return; }
        try
        {
            var completed = _pet.Activity != "" && next.Activity == "";
            _store.SavePetState(next); _pet = next; UpdatePetView(CurrentCategory); CareChanged?.Invoke();
            if (completed) ShowStatus("活动完成，奖励已存入成长存档。");
        }
        catch (Exception ex) { CrashLogger.WriteThrottled("care-save", ex); }
    }
    public void SetPetSize(double size)
    {
        if (!double.IsFinite(size) || _store.SettingsReadOnly) throw new InvalidOperationException("无法保存桌宠大小。");
        var old = _settings.PetSize; _settings.PetSize = Math.Clamp(size, 0.6, 1.8);
        try { _store.SaveSettings(_settings); } catch { _settings.PetSize = old; throw; }
        ApplyPetSize();
    }
    private void ApplyPetSize()
    {
        var model = ModelScene is not null;
        var right = Left + Width; var bottom = Top + Height;
        PetStage.Width = model ? 240 : 150; PetStage.Height = model ? 300 : 145;
        PetContent.LayoutTransform = new ScaleTransform(_settings.PetSize, _settings.PetSize);
        Width = (model ? 280 : 190) * _settings.PetSize; Height = (model ? 380 : 220) * _settings.PetSize;
        var captionScale=_settings.PetSize<1?1/_settings.PetSize:1;
        PetCaption.LayoutTransform=new ScaleTransform(captionScale,captionScale);
        PetCaption.MaxWidth=_settings.PetSize<1?Math.Min(185,Width-24*_settings.PetSize):185;
        if(_settings.PetSize<1)Height+=48*(1-_settings.PetSize);
        Left = right - Width; Top = bottom - Height;
        var pos = ClampToVirtualScreen(Left, Top); Left = pos.Left; Top = pos.Top;
    }
    private void ApplySkin()
    {
        var color = _pet.Skin switch { "plum" => "#8167A3", "mint" => "#438D83", _ => "#365F7F" };
        foreach (var shape in DefaultPet.Children.OfType<Shape>())
        {
            if (shape.Tag as string != "body") continue;
            shape.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
    public void ChooseDefaultSkin(string skin)
    {
        if (!_pet.OwnedSkins.Contains(skin)) throw new InvalidOperationException("尚未拥有此外观。");
        if (IsModelLoading) throw new InvalidOperationException("请先完成或取消模型导入。");
        var old = _settings.UseDefaultAppearance; _settings.UseDefaultAppearance = true;
        try { if (_store.SettingsReadOnly) throw new InvalidOperationException("设置受只读保护。"); _store.SaveSettings(_settings); }
        catch { _settings.UseDefaultAppearance = old; throw; }
        try { CareAction(p => { p.Skin = skin; return "已恢复浅梦默认人物；旧版配色存档已保留。"; }); }
        catch { _settings.UseDefaultAppearance = old; _store.SaveSettings(_settings); throw; }
        _ = RestorePetModelAsync();
    }
    public async Task ChooseImportedAppearanceAsync()
    {
        if (IsModelLoading) throw new InvalidOperationException("模型正在处理中。");
        if (!LocalStore.IsModelFileName(_settings.PetModelFile)) throw new InvalidOperationException("请先到模型页导入 GLB。");
        if (_store.SettingsReadOnly) throw new InvalidOperationException("设置受只读保护。");
        var old = _settings.UseDefaultAppearance; _settings.UseDefaultAppearance = false;
        try { _store.SaveSettings(_settings); } catch { _settings.UseDefaultAppearance = old; throw; }
        await RestorePetModelAsync();
    }
    public void ResetCare()
    {
        CareAction(p => "");
        var next = new PetState(); _store.SavePetState(next); _pet = next; ApplySkin(); CareChanged?.Invoke();
    }
}
