using System.IO;

using System.Windows.Automation;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static async Task RunPackageCareUiAsync(AutomationElement center)
    {
        await ClickUiAsync(FindUi(center, ControlType.ListItem, "商店"));
        var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopBuddy");
        var before=new LocalStore(profile).LoadPetState();
        var riceBefore=before.Inventory.GetValueOrDefault("rice");
        await ClickUiAsync(FindUi(center, ControlType.Button, "购买暖心饭团"));
        var state=new LocalStore(profile).LoadPetState();
        Check("installed-shop-purchase",state.Coins==before.Coins-12&&state.Inventory.GetValueOrDefault("rice")==riceBefore+1,"Installed shop failed.");
        await ClickUiAsync(FindUi(center,ControlType.ListItem,"设置"));
        var size=FindUi(center,ControlType.Slider,"桌宠整体大小（60%–180%）");
        ((RangeValuePattern)size.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(1.2);
        await ClickUiAsync(FindUi(center,ControlType.Button,"应用大小"));
        Check("installed-size-persisted",Math.Abs(new LocalStore(profile).LoadSettings().PetSize-1.2)<0.001,"Installed size not saved.");
        ((RangeValuePattern)size.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(1);
        await ClickUiAsync(FindUi(center,ControlType.Button,"应用大小"));
        await ClickUiAsync(FindUi(center,ControlType.ListItem,"对话"));
        var input=FindUi(center,ControlType.Edit,"聊天输入");
        ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("安装版回车发送测试");
        input.SetFocus();
        await InputAsync(()=>{Native.KeyInput(0x0D,false);Native.KeyInput(0x0D,true);});await Task.Delay(250);
        Check("installed-enter-sends",((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).Current.Value=="","Installed Enter did not send.");
        await ClickUiAsync(FindUi(center,ControlType.ListItem,"总览"));
    }
}
