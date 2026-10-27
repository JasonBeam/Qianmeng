using System.Windows.Media.Media3D;
using DesktopBuddy.Services;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy;
public partial class MainWindow
{
    private RigScenePlayer? _animationPlayer;
    private bool _manualAnimation;
    private string _lastAnimationSummary="";
    public event Action? AnimationStateChanged;
    public string[] AnimationNames=>_animationPlayer?.Clips.Select(c=>c.Name).ToArray()??[];
    public Rect3D? ModelFramingBounds=>_animationPlayer?.FramingBounds;
    public bool IsAnimationPlaying=>_animationPlayer?.IsPlaying??false;
    public long AnimationFrameCount=>_animationPlayer?.FrameCount??0;
    public bool CanPlayCompanionActions=>Settings.AnimationEnabled&&_animationPlayer?.SupportsCompanionActions==true;
    public CompanionActionKind CurrentCompanionAction=>_animationPlayer?.CurrentCompanionAction??CompanionActionKind.None;
    public double LastAnimationFrameMilliseconds=>_animationPlayer?.LastFrameMilliseconds??0;
    public double WorstAnimationFrameMilliseconds=>_animationPlayer?.WorstFrameMilliseconds??0;
    public string AnimationSummary
    {
        get
        {
            if(_animationPlayer?.Faulted==true)return "动画播放失败，已保留画面；请重新导入模型。";
            if(_animationPlayer is null)return "当前形象没有可播放的骨骼动作。";
            var action=CurrentCompanionAction;
            var actionLabel=CompanionActionMotion.TryGetDefinition(action,out var definition)?$" · {definition.Label}":"";
            return $"{_animationPlayer.Clips[_animationPlayer.ClipIndex].Name} · {(_animationPlayer.IsPlaying?"播放中":!IsVisible?"隐藏时暂停":PetModel.IsMouseOver&&Settings.AnimationEnabled?"鼠标互动（基础动作暂停）":"已暂停")}{actionLabel} · {Settings.AnimationSpeed:F2}×";
        }
    }
    private void RefreshAnimationPlayback(bool preserveCurrentAction=false)
    {
        if(_animationPlayer is null){NotifyAnimation();return;}
        var names=_animationPlayer.Clips;
        var index=Array.FindIndex(names,c=>string.Equals(c.Name,Settings.PreferredAnimationClip,StringComparison.Ordinal));
        if(!_manualAnimation&&Settings.AutomaticActivityAnimation)
        {
            var running=Pet.Activity is "exercise" or "play";
            var mapped=running?RigClipResolver.FindRunning(names):RigClipResolver.FindIdle(names);
            index=mapped>=0?mapped:index;
        }
        if(index<0)index=0;
        if(!preserveCurrentAction)_animationPlayer.Select(index);
        _animationPlayer.Configure(Settings.AnimationEnabled&&IsVisible&&!_exitRequested&&!_leftPressed&&!_dragStarted&&!PetModel.IsMouseOver,Settings.AnimationSpeed,Settings.AnimationFrameRate);
        NotifyAnimation();
    }
    private void NotifyAnimation()
    {
        var text=AnimationSummary;if(text==_lastAnimationSummary)return;
        _lastAnimationSummary=text;UpdateActionStatusText();AnimationStateChanged?.Invoke();
    }
    private void UpdateActionStatusText()
    {
        if(ActivityCaption is { } caption){PetStatusText.Text=caption;return;}
        if(Settings.PrivacyMode){PetStatusText.Text="隐私模式";return;}
        PetStatusText.Text=CompanionActionMotion.TryGetDefinition(CurrentCompanionAction,out var definition)?definition.Label:_currentStatus;
    }
    public void PlayCompanionAction(CompanionActionKind action)
    {
        if(!CanPlayCompanionActions)return;
        ResetInteractionMotion();
        _animationPlayer?.SetCompanionAction(action);
        NotifyAnimation();
    }
    public void PreviewAnimation(string name)
    {
        if(_animationPlayer?.Faulted==true)throw new InvalidOperationException("动作播放失败，请重新导入模型后重试。");
        if(!AnimationNames.Contains(name))throw new InvalidOperationException("动作不存在。");
        ResetInteractionMotion();
        SaveAnimationOptions(true,Settings.AnimationSpeed,Settings.AnimationFrameRate,name);_manualAnimation=true;RefreshAnimationPlayback();
    }
    public void PauseAnimation()=>SaveAnimationOptions(false,Settings.AnimationSpeed,Settings.AnimationFrameRate,Settings.PreferredAnimationClip);
    public void FollowActivityAnimations()
    {
        ResetInteractionMotion();
        var previous=(_manualAnimation,Settings.AutomaticActivityAnimation);_manualAnimation=false;Settings.AutomaticActivityAnimation=true;
        try{SaveAnimationOptions(true,Settings.AnimationSpeed,Settings.AnimationFrameRate,Settings.PreferredAnimationClip);}
        catch{(_manualAnimation,Settings.AutomaticActivityAnimation)=previous;throw;}
    }
    public void SaveAnimationOptions(bool enabled,double speed,int fps,string clip)
    {
        if(_store.SettingsReadOnly)throw new InvalidOperationException("设置受只读保护，不能保存动作选项。");
        var previous=(Settings.AnimationEnabled,Settings.AnimationSpeed,Settings.AnimationFrameRate,Settings.PreferredAnimationClip);
        Settings.AnimationEnabled=enabled;Settings.AnimationSpeed=double.IsFinite(speed)?Math.Clamp(speed,.5,1.5):1;Settings.AnimationFrameRate=fps is 15 or 24 or 30?fps:30;Settings.PreferredAnimationClip=clip;
        try{_store.SaveSettings(Settings);}catch{(Settings.AnimationEnabled,Settings.AnimationSpeed,Settings.AnimationFrameRate,Settings.PreferredAnimationClip)=previous;throw;}
        UpdateInteractionActionSpeed();
        RefreshAnimationPlayback(preserveCurrentAction:string.Equals(previous.PreferredAnimationClip,clip,StringComparison.Ordinal));
        if(!enabled)ResetInteractionMotion();
    }
}
