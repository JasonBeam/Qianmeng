using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy;
public partial class MainWindow
{
    // Keep greetings free of contact/prop actions until those have passed visual review.
    private static readonly CompanionActionKind[] ClickActions=
    [
        CompanionActionKind.Waving, CompanionActionKind.Nodding, CompanionActionKind.Curious,
        CompanionActionKind.Bowing, CompanionActionKind.LookingAround, CompanionActionKind.ShakingHead,
        CompanionActionKind.Listening, CompanionActionKind.DoubleWaving,
    ];
    private readonly CompanionMotion _gesture = new();
    private readonly CompanionMotion _lookGesture = new();
    private readonly CompanionDragMotion _dragMotion = new();
    private readonly Stopwatch _gestureClock = Stopwatch.StartNew();
    private readonly RotateTransform _fallbackRoll = new();
    private readonly ScaleTransform _fallbackScale = new();
    private readonly TranslateTransform _fallbackLift = new();
    private bool _gestureTicking, _gestureDragging, _gesturePressed, _interactionActionLooping, _actionOverrideActive;
    private double _gestureLast, _gestureNext, _dragLast, _interactionActionStart=-10, _interactionActionDuration, _actionBlendStart=-10;
    private double _interactionActionSeconds, _interactionActionLast, _interactionActionSpeed=1;
    private bool _interactionActionAuthored;
    private double _lookX, _lookY;
    private int _lookDirection = -1;
    private Point _dragLocation;
    private int _nextClickAction=-1, _dragDirection;
    private CompanionActionKind _interactionActionKind;
    private CompanionActionPose _interactionActionPose, _actionBlendFrom;
    public bool IsInteractionRendering => _gestureTicking;
    public string InteractionState => _gestureDragging ? "dragging" : _gesturePressed ? "pressed" : _interactionActionKind!=CompanionActionKind.None&&!_interactionActionLooping ? "greeting" : _gestureTicking ? "settling" : "rest";
    public CompanionActionKind InteractionActionKind => _interactionActionKind;
    public string InteractionActionLabel => CompanionActionMotion.TryGetDefinition(_interactionActionKind,out var definition)?definition.Label:"回应";
    public CompanionActionPose InteractionActionPose => _interactionActionPose;
    public int InteractionLookDirection => _lookDirection;
    public double InteractionYaw => _gesture.Yaw;
    public double InteractionHeadYaw => _lookGesture.Yaw;
    public double InteractionRoll => _gesture.Roll;
    public long InteractionFrameCount { get; private set; }
    private bool CanAnimateInteraction => IsVisible && !_exitRequested && !_settings.ClickThrough && _settings.AnimationEnabled && SystemParameters.ClientAreaAnimation;
    private void InitializeInteractions()
    {
        var transforms=new TransformGroup();transforms.Children.Add(_fallbackScale);transforms.Children.Add(_fallbackRoll);transforms.Children.Add(_fallbackLift);
        DefaultPet.RenderTransformOrigin=new Point(.5,.5);DefaultPet.RenderTransform=transforms;
        LocationChanged+=(_,_)=>UpdateDragGesture();
        RootGrid.PreviewMouseMove+=(_,e)=>TrackPetPointer(e);
        RootGrid.MouseLeave+=(_,_)=>{if(!_gestureDragging){_lookX=_lookY=0;_lookDirection=-1;SetGestureTargets();StartGestureRendering();}};
    }
    private void TrackPetPointer(System.Windows.Input.MouseEventArgs e)
    {
        if(_gestureDragging||_gesturePressed||!CanAnimateInteraction)return;
        var p=e.GetPosition(PetStage);
        var look=CompanionLookMotion.Quantize(
            Math.Clamp((p.X/Math.Max(1,PetStage.ActualWidth)-.5)*2,-1,1),
            Math.Clamp((p.Y/Math.Max(1,PetStage.ActualHeight)-.4)*2,-1,1));
        _lookX=look.Horizontal;_lookY=look.Vertical;_lookDirection=look.Sector;
        SetGestureTargets();StartGestureRendering();
    }
    private void PressGesture(bool pressed)
    {_gesturePressed=pressed;SetGestureTargets();if(CanAnimateInteraction)StartGestureRendering();}
    private void GreetGesture()
    {
        if(!CanAnimateInteraction)return;
        _nextClickAction=(_nextClickAction+1)%ClickActions.Length;
        StartInteractionAction(ClickActions[_nextClickAction],false,_gestureClock.Elapsed.TotalSeconds);
        SetGestureTargets();StartGestureRendering();
    }
    private void StartInteractionAction(CompanionActionKind kind,bool loop,double now)
    {
        _interactionActionPose=SampleInteractionAction(now);_actionBlendFrom=_interactionActionPose;
        var continueRun=loop&&_interactionActionLooping&&
            (_interactionActionKind is CompanionActionKind.RunningLeft or CompanionActionKind.RunningRight)&&
            (kind is CompanionActionKind.RunningLeft or CompanionActionKind.RunningRight);
        _interactionActionKind=kind;_interactionActionLooping=loop;
        if(!continueRun){_interactionActionStart=now;_interactionActionSeconds=0;_interactionActionLast=now;}
        _interactionActionDuration=CompanionActionMotion.TryGetDefinition(kind,out var definition)?definition.DurationSeconds:0;
        var authoredDuration=!loop?(_animationPlayer?.AuthoredActionDuration(kind)??0):0;
        _interactionActionAuthored=authoredDuration>0;_interactionActionSpeed=_settings.AnimationSpeed;
        if(_interactionActionAuthored)_interactionActionDuration=authoredDuration;
        _actionBlendStart=now;_actionOverrideActive=true;
    }
    private void ClearInteractionAction(double now)
    {
        _interactionActionPose=SampleInteractionAction(now);
        if(_interactionActionKind==CompanionActionKind.None&&!_actionOverrideActive)return;
        _actionBlendFrom=_interactionActionPose;_interactionActionKind=CompanionActionKind.None;_interactionActionLooping=false;
        _interactionActionDuration=0;_actionBlendStart=now;_actionOverrideActive=true;
    }
    private CompanionActionPose SampleInteractionAction(double now)
    {
        AdvanceInteractionActionClock(now);
        var target=default(CompanionActionPose);
        if(_interactionActionKind!=CompanionActionKind.None)
        {
            var elapsed=_interactionActionAuthored?_interactionActionSeconds:Math.Max(0,now-_interactionActionStart);
            if(_interactionActionLooping)target=CompanionActionMotion.SampleRunningLoop(_interactionActionKind,elapsed);
            else if(elapsed<_interactionActionDuration)
            {
                if((_animationPlayer?.AuthoredActionDuration(_interactionActionKind)??0)<=0)
                    target=CompanionActionMotion.Sample(_interactionActionKind,elapsed);
            }
            else
            {
                _interactionActionKind=CompanionActionKind.None;_interactionActionLooping=false;_interactionActionDuration=0;
                _actionBlendFrom=_interactionActionPose;_actionBlendStart=now;
            }
        }
        var blend=SmootherStep((now-_actionBlendStart)/.16);
        _interactionActionPose=CompanionActionPose.Lerp(_actionBlendFrom,target,(float)blend);
        _actionOverrideActive=_interactionActionKind!=CompanionActionKind.None||blend<.9999;
        if(!_actionOverrideActive){_interactionActionPose=default;_actionBlendFrom=default;}
        return _interactionActionPose;
    }
    private void AdvanceInteractionActionClock(double now)
    {
        if(_interactionActionAuthored&&_interactionActionKind!=CompanionActionKind.None)
            _interactionActionSeconds+=Math.Max(0,now-_interactionActionLast)*_interactionActionSpeed;
        _interactionActionLast=now;
    }
    private void UpdateInteractionActionSpeed()
    {
        AdvanceInteractionActionClock(_gestureClock.Elapsed.TotalSeconds);
        _interactionActionSpeed=_settings.AnimationSpeed;
    }
    private void BeginDragGesture()
    {
        var now=_gestureClock.Elapsed.TotalSeconds;ClearInteractionAction(now);
        _gestureDragging=true;_gesturePressed=false;_lookX=_lookY=0;_lookDirection=-1;_dragDirection=0;_dragMotion.Reset();
        _lookGesture.TargetYaw=_lookGesture.TargetRoll=0;
        _dragLocation=new Point(Left,Top);_dragLast=now;
        _gesture.TargetLift=.018;StartGestureRendering();
    }
    private void UpdateDragGesture()
    {
        if(!_gestureDragging||!CanAnimateInteraction)return;
        var now=_gestureClock.Elapsed.TotalSeconds;var dt=Math.Max(.012,now-_dragLast);
        var velocity=_dragMotion.Sample(Left-_dragLocation.X,dt);
        SetDragGestureTargets(velocity,now);
        _gesture.TargetScale=.98;_gesture.TargetLift=.018;
        _dragLocation=new Point(Left,Top);_dragLast=now;StartGestureRendering();
    }
    private void SetDragGestureTargets(double velocity,double now)
    {
        _gesture.TargetYaw=Math.Clamp(velocity*.07,-20,20);
        _gesture.TargetRoll=Math.Clamp(-velocity*.032,-8,8);
        var direction=_dragMotion.RunDirection;
        if(direction==_dragDirection)return;
        _dragDirection=direction;
        if(direction==0){ClearInteractionAction(now);return;}
        var action=direction>0?CompanionActionKind.RunningRight:CompanionActionKind.RunningLeft;
        StartInteractionAction(action,true,now);
    }
    private void EndDragGesture()
    {
        ClearInteractionAction(_gestureClock.Elapsed.TotalSeconds);_dragDirection=0;_dragMotion.Reset();
        _gestureDragging=false;_gesturePressed=false;_lookX=_lookY=0;_lookDirection=-1;
        SetGestureTargets();StartGestureRendering();
    }
    private void SetGestureTargets()
    {
        _lookGesture.TargetYaw=_lookX*.55;_lookGesture.TargetRoll=_lookY*.32;
        if(_gestureDragging)return;
        _gesture.TargetYaw=_lookX*10;_gesture.TargetRoll=0;
        _gesture.TargetScale=_gesturePressed?.97:1;_gesture.TargetLift=0;
    }
    private void StartGestureRendering()
    {
        if(!CanAnimateInteraction){ResetInteractionMotion();return;}
        if(_gestureTicking)return;
        _gestureLast=_gestureClock.Elapsed.TotalSeconds;_gestureNext=_gestureLast;
        _gestureTicking=true;CompositionTarget.Rendering+=RenderInteraction;
    }
    private static double SmootherStep(double value)
    {var t=Math.Clamp(value,0,1);return t*t*t*(t*(t*6-15)+10);}
    private void RenderInteraction(object? sender,EventArgs e)
    {
        if(!CanAnimateInteraction){ResetInteractionMotion();return;}
        var now=_gestureClock.Elapsed.TotalSeconds;if(now+.0005<_gestureNext)return;
        _gestureNext+=1d/30;if(now-_gestureNext>1d/15)_gestureNext=now+1d/30;var delta=now-_gestureLast;_gestureLast=now;
        if(_gestureDragging&&now-_dragLast>.04)
        {
            var velocity=_dragMotion.Decay(delta);
            SetDragGestureTargets(velocity,now);
        }
        var actionPose=SampleInteractionAction(now);var moving=_gesture.Step(delta);var lookMoving=_lookGesture.Step(delta);
        var dragTurn=_gestureDragging ? Math.Clamp(_gesture.Yaw/20,-1,1):0;
        var bodyMotion=new Vector3(
            (float)(_lookGesture.Roll*.25+(_gestureDragging ? -_gesture.Roll*.012 : 0)),
            (float)(_lookGesture.Roll*.10+(_gesturePressed ? .025 : 0)+(_gestureDragging ? -.045*Math.Abs(dragTurn) : 0)),
            (float)(_lookGesture.Yaw*.30+dragTurn*.15));
        var headYaw=_lookGesture.Yaw+dragTurn*.18;
        var headPitch=_lookGesture.Roll+(_gestureDragging ? -.035*Math.Abs(dragTurn) : 0)+(_gesturePressed ? .025 : 0);
        try
        {
            PetModel.SetInteractionPose(_gesture.Yaw,_gesture.Roll,_gesture.Scale,_gesture.Lift);
            _fallbackRoll.Angle=-_gesture.Roll;
            _fallbackScale.ScaleX=_fallbackScale.ScaleY=_gesture.Scale;_fallbackLift.Y=-_gesture.Lift*PetStage.ActualHeight;
            _animationPlayer?.SetCompanionGesture(new Vector3((float)headYaw,(float)headPitch,0),0,bodyMotion,actionPose,_actionOverrideActive,
                !_interactionActionLooping?_interactionActionKind:CompanionActionKind.None,
                _interactionActionAuthored?_interactionActionSeconds:Math.Max(0,now-_interactionActionStart));
            InteractionFrameCount++;
        }
        catch(Exception ex){DesktopBuddy.Services.CrashLogger.WriteThrottled("interaction-frame",ex);ResetInteractionMotion();return;}
        if(!moving&&!lookMoving&&_interactionActionKind==CompanionActionKind.None&&!_actionOverrideActive&&!_gestureDragging&&!_gesturePressed&&
            !(_animationPlayer?.IsPoseTransitioning??false))StopGestureRendering();
    }
    private void StopGestureRendering()
    {if(!_gestureTicking)return;CompositionTarget.Rendering-=RenderInteraction;_gestureTicking=false;}
    private void ResetInteractionMotion()
    {
        StopGestureRendering();_gestureDragging=_gesturePressed=false;_lookX=_lookY=0;_lookDirection=-1;_dragMotion.Reset();_gesture.Reset();_lookGesture.Reset();
        _interactionActionKind=CompanionActionKind.None;_interactionActionLooping=false;_interactionActionDuration=0;
        _interactionActionPose=_actionBlendFrom=default;_actionOverrideActive=false;_actionBlendStart=-10;_dragDirection=0;
        PetModel.SetInteractionPose(0,0,1,0);_fallbackRoll.Angle=0;_fallbackScale.ScaleX=_fallbackScale.ScaleY=1;_fallbackLift.Y=0;
        try{_animationPlayer?.SetCompanionGesture(Vector3.Zero);}catch(Exception ex){DesktopBuddy.Services.CrashLogger.WriteThrottled("interaction-reset",ex);}
    }
}
