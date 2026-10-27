using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy;
public sealed partial class ControlCenterWindow
{
    private readonly ComboBox _animationClip=new(){MinWidth=180,IsEditable=false};
    private readonly Slider _animationSpeed=new(){Minimum=.5,Maximum=1.5,TickFrequency=.1,SmallChange=.1,IsSnapToTickEnabled=true};
    private readonly ComboBox _animationFps=new(){ItemsSource=new[]{15,24,30},MinWidth=100};
    private readonly CheckBox _animationEnabled=new(){Content="启用动作与互动反馈"};
    private readonly TextBlock _animationCaption=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,10)};
    private readonly TextBlock _animationSpeedCaption=new();
    private readonly List<Button> _animationCommands=[];
    private readonly List<Button> _companionActionCommands=[];
    private UIElement BuildAnimationControls()
    {
        var stack=new StackPanel();stack.Children.Add(Heading("动作播放"));stack.Children.Add(_animationCaption);
        stack.Children.Add(Labeled("可用动作",_animationClip));
        _animationEnabled.IsChecked=_settings.AnimationEnabled;stack.Children.Add(_animationEnabled);
        _animationSpeed.Value=_settings.AnimationSpeed;_animationSpeedCaption.Text=$"{_animationSpeed.Value:F1}×";
        _animationSpeed.ValueChanged+=(_,_)=>_animationSpeedCaption.Text=$"{_animationSpeed.Value:F1}×";
        stack.Children.Add(Labeled("动作速度（0.5–1.5×）",_animationSpeed));stack.Children.Add(_animationSpeedCaption);
        _animationFps.SelectedItem=_settings.AnimationFrameRate;stack.Children.Add(Labeled("活动动作帧率上限",_animationFps));
        var commands=new WrapPanel();
        void Command(string label,RoutedEventHandler action)
        {var b=ActionButton(label,action);commands.Children.Add(b);_animationCommands.Add(b);}
        Command("播放所选动作",(_,_)=>{if(_animationClip.SelectedItem is string clip)_owner.PreviewAnimation(clip);});
        Command("暂停动作",(_,_)=>_owner.PauseAnimation());
        Command("跟随日常活动",(_,_)=>_owner.FollowActivityAnimations());
        Command("应用播放设置",(_,_)=>_owner.SaveAnimationOptions(_animationEnabled.IsChecked==true,_animationSpeed.Value,(int?)_animationFps.SelectedItem??30,_animationClip.SelectedItem as string??""));
        stack.Children.Add(commands);
        stack.Children.Add(Heading("桌宠动作预览"));
        var stateOrder=new[]{"waving","jumping","celebrating","failed","waiting","curious","running","running-right","running-left","review","typing","coffee","burger","resting","idle"};
        foreach(var state in stateOrder)
        {
            var definitions=CompanionActionMotion.Library.Where(definition=>definition.Kind is not CompanionActionKind.Idle and not CompanionActionKind.None&&definition.PetDexState==state).ToArray();
            if(definitions.Length==0)continue;
            var group=new StackPanel{Margin=new Thickness(0,2,0,5)};
            group.Children.Add(new TextBlock{Text=$"{PetDexStateLabel(state)} · {definitions.Length} 个",FontSize=11,FontWeight=FontWeights.SemiBold,Foreground=Brush("#6F7890"),Margin=new Thickness(0,2,0,1)});
            var actionPanel=new WrapPanel();
            foreach(var definition in definitions)
            {
                var button=ActionButton(definition.Label,(_,_)=>_owner.PlayCompanionAction(definition.Kind));
                button.ToolTip=definition.Kind switch
                {
                    CompanionActionKind.Typing=>"空手模拟敲键盘，不显示键盘道具。",
                    CompanionActionKind.CoffeeBreak=>"空手模拟举杯，不显示杯子道具。",
                    CompanionActionKind.EatingSnack=>"空手模拟进食，不显示食物道具。",
                    CompanionActionKind.Hugging=>"先张臂邀请，再收拢双臂示意拥抱。",
                    CompanionActionKind.Failed or CompanionActionKind.Shy or CompanionActionKind.Surprised or CompanionActionKind.Laughing
                        =>"用头肩与身体姿态示意情绪；面部保持模型原有表情。",
                    _=>$"{definition.DurationSeconds:F1} 秒",
                };
                actionPanel.Children.Add(button);_companionActionCommands.Add(button);
            }
            group.Children.Add(actionPanel);stack.Children.Add(group);
        }
        stack.Children.Add(new TextBlock{Text=$"当前动作库 {CompanionActionMotion.Library.Count-1} 个，按 PetDex 状态分组。点击轮换问候动作；拖拽会按方向进入跑步，松手后缓动回位。每个动作有起势、主体和回落，适配本地骨骼模型；隐藏或暂停时停止更新，待机降至 6 FPS。FBX 请先转换为带骨骼的 GLB。",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brush("#777E91"),Margin=new Thickness(0,8,0,18)});
        _owner.AnimationStateChanged+=RefreshAnimationControls;
        Closed+=(_,_)=>_owner.AnimationStateChanged-=RefreshAnimationControls;
        RefreshAnimationControls();return stack;
    }
    private static string PetDexStateLabel(string state)=>state switch
    {
        "waving"=>"挥手/问候","jumping"=>"跳跃","celebrating"=>"庆祝/开心","failed"=>"受挫/摇头","waiting"=>"等待","curious"=>"好奇/观察",
        "running"=>"跑动/舞蹈","running-right"=>"向右跑","running-left"=>"向左跑",
        "review"=>"思考/检查","typing"=>"工作/打字","coffee"=>"咖啡休息",
        "burger"=>"进食","resting"=>"休息/睡眠","idle"=>"待机",_=>state
    };
    private void RefreshAnimationControls()
    {
        var names=_owner.AnimationNames;var selected=_animationClip.SelectedItem as string;
        if(!_animationClip.Items.Cast<string>().SequenceEqual(names))_animationClip.ItemsSource=names;
        _animationClip.SelectedItem=names.Contains(selected??"")?selected:names.Contains(_settings.PreferredAnimationClip)?_settings.PreferredAnimationClip:names.FirstOrDefault(n=>(n.Contains("idle",StringComparison.OrdinalIgnoreCase)||n.Contains("待机")))??names.FirstOrDefault();
        _animationCaption.Text=_owner.AnimationSummary;_animationEnabled.IsChecked=_settings.AnimationEnabled;
        foreach(var b in _animationCommands)b.IsEnabled=names.Length>0&&!_owner.IsModelLoading;
        foreach(var b in _companionActionCommands)b.IsEnabled=_owner.CanPlayCompanionActions&&!_owner.IsModelLoading;
        _animationClip.IsEnabled=_animationSpeed.IsEnabled=_animationFps.IsEnabled=names.Length>0;
    }
}
