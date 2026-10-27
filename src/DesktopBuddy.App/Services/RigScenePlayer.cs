using System.Diagnostics;
using System.Numerics;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy.Services;

/// <summary>CPU skinning for bounded lightweight meshes; detached double buffers prevent per-frame geometry allocation.</summary>
internal sealed class RigScenePlayer : IDisposable
{
    // Keep one model's small framing metadata strongly; retain its heavier WPF pose only while a view uses it.
    private static readonly object FrozenPoseCacheLock=new();
    private static string? _frozenPoseCacheHash;
    private static WeakReference<Model3DGroup>? _frozenPoseCacheScene;
    private static Vector3 _frozenPoseCacheCenter;
    private static float _frozenPoseCacheSize;
    private static Rect3D _frozenPoseCacheBounds;
    private readonly RigSampler _sampler;
    private readonly Model3DGroup _basePose;
    private readonly ModelAsset _asset;
    private readonly Vector3 _center;
    private readonly float _size;
    private readonly MeshGeometry3D[] _geometry;
    private readonly Point3DCollection[][] _points;
    private readonly Vector3DCollection[][] _normals;
    private bool _playing;
    private readonly DispatcherTimer _frameTimer;
    private double _scheduledInterval;
    private double _interval=1d/30;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private double _last, _seconds, _fromSeconds, _blendSeconds=1;
    private double _poseTransitionStarted=double.NegativeInfinity, _gestureChangedAt=double.NegativeInfinity;
    private void BeginPoseTransition()
    {
        _sampler.BeginPoseTransition();
        _poseTransitionStarted=_clock.Elapsed.TotalSeconds;
    }
    private int _fromClip=-1, _pointBuffer, _normalBuffer;
    private double _speed=1;
    private int _fps=30;
    private int _requestedFps=30;
    private int _adaptiveFps=30;
    private CompanionActionKind _companionAction;
    private CompanionActionKind _lastAmbientAction;
    private double _companionActionElapsed, _nextAmbientAction;
    private int _nativeActionClip=-1, _nativeActionReturnClip=-1;
    private double _nativeActionDuration;
    private int _interactionClip=-1;
    private double _interactionClipSeconds;
    private CompanionActionKind _interactionNativeKind;
    private bool _ambientAction;
    private static readonly CompanionActionKind[] AmbientActions=[
        CompanionActionKind.Waiting, CompanionActionKind.Curious, CompanionActionKind.LookingAround,
        CompanionActionKind.Nodding, CompanionActionKind.Resting, CompanionActionKind.Listening,
        CompanionActionKind.Swaying];
    private static double NextAmbientGap()=>16+Random.Shared.NextDouble()*14;
    public bool Faulted { get; private set; }
    public event Action? PlaybackFailed;
    public event Action? CompanionActionChanged;
    public int ClipIndex { get; private set; }
    public bool IsPlaying=>_playing;
    public bool IsPoseTransitioning=>_sampler.PoseTransitionProgress<1;
    public bool SupportsCompanionActions=>_sampler.SupportsCompanionActions;
    public CompanionActionKind CurrentCompanionAction=>_interactionClip>=0?_interactionNativeKind:_companionAction;
    public long FrameCount { get; private set; }
    public double LastFrameMilliseconds { get; private set; }
    public double WorstFrameMilliseconds { get; private set; }
    public Model3DGroup Scene { get; }=new();
    public Rect3D FramingBounds { get; }
    public RigClip[] Clips=>_asset.Rig!.Clips;
    internal bool UsesAsset(ModelAsset asset,Model3DGroup frozenPose)=>!Faulted&&ReferenceEquals(_asset,asset)&&ReferenceEquals(_basePose,frozenPose);
    public RigScenePlayer(ModelAsset asset,Model3DGroup frozenPose)
    {
        _asset=asset;_basePose=frozenPose;_sampler=new(asset);
        if(TryGetFrozenPoseCache(asset.SourceHash,out _,out var center,out var size,out var framingBounds))
        {_center=center;_size=size;FramingBounds=framingBounds;}
        else
        {var envelope=Envelope(asset,_sampler);(_center,_size)=Bounds(envelope);FramingBounds=Framing(envelope,_center,_size);}
        _geometry=new MeshGeometry3D[asset.Meshes.Length];_points=new Point3DCollection[asset.Meshes.Length][];_normals=new Vector3DCollection[asset.Meshes.Length][];
        for(var m=0;m<_geometry.Length;m++)
        {
            var source=(GeometryModel3D)frozenPose.Children[m];var sourceMesh=(MeshGeometry3D)source.Geometry;var count=asset.Meshes[m].Positions.Length;
            _points[m]=[new(Enumerable.Repeat(new Point3D(),count)),new(Enumerable.Repeat(new Point3D(),count))];
            _normals[m]=[new(Enumerable.Repeat(new Vector3D(0,1,0),count)),new(Enumerable.Repeat(new Vector3D(0,1,0),count))];
            var geometry=new MeshGeometry3D{TextureCoordinates=sourceMesh.TextureCoordinates,TriangleIndices=sourceMesh.TriangleIndices};_geometry[m]=geometry;
            Scene.Children.Add(new GeometryModel3D(geometry,source.Material){BackMaterial=source.BackMaterial});
        }
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(_interval) };
        _frameTimer.Tick += Tick;
        ClipIndex=DefaultClip(asset);Render();_nextAmbientAction=_clock.Elapsed.TotalSeconds+NextAmbientGap();

    }
    public static int DefaultClip(ModelAsset asset)
    {var clips=asset.Rig?.Clips??[];var idle=RigClipResolver.FindIdle(clips);return idle>=0?idle:clips.Length>0?0:-1;}
    public double AuthoredActionDuration(CompanionActionKind action)
    {var index=RigClipResolver.FindAction(Clips,action);return index>=0?Clips[index].Duration/ClipPlaybackRate(index):0;}
    private static (Vector3 Min,Vector3 Max) Envelope(ModelAsset asset,RigSampler sampler)
    {
        var min=new Vector3(float.MaxValue);var max=new Vector3(float.MinValue);var clips=asset.Rig!.Clips;
        for(var c=0;c<Math.Max(1,clips.Length);c++)for(var f=0;f<12;f++)
        {sampler.Sample(clips.Length==0?-1:c,clips.Length==0?0:clips[c].Duration*f/12);foreach(var mesh in sampler.Positions)foreach(var p in mesh){min=Vector3.Min(min,p);max=Vector3.Max(max,p);}}
        return(min,max);
    }
    private static (Vector3 Center,float Size) Bounds((Vector3 Min,Vector3 Max) b)
    {var extent=b.Max-b.Min;var size=Math.Max(extent.X,Math.Max(extent.Y,extent.Z));if(size<1e-8||!float.IsFinite(size))throw new ModelImportException("动画形象尺寸无效。");return((b.Min+b.Max)/2,size);}
    private static Rect3D Framing((Vector3 Min,Vector3 Max) b,Vector3 center,float size)
        =>new((b.Min.X-center.X)/size,(b.Min.Y-center.Y)/size,(b.Min.Z-center.Z)/size,(b.Max.X-b.Min.X)/size,(b.Max.Y-b.Min.Y)/size,(b.Max.Z-b.Min.Z)/size);
    private static bool TryGetFrozenPoseCache(string sourceHash,out Model3DGroup? scene,out Vector3 center,out float size,out Rect3D framingBounds)
    {
        scene=null;center=default;size=0;framingBounds=default;
        if(string.IsNullOrWhiteSpace(sourceHash))return false;
        lock(FrozenPoseCacheLock)
        {
            if(_frozenPoseCacheHash==sourceHash)
            {
                if(_frozenPoseCacheScene is not null&&_frozenPoseCacheScene.TryGetTarget(out var cached))scene=cached;
                center=_frozenPoseCacheCenter;size=_frozenPoseCacheSize;framingBounds=_frozenPoseCacheBounds;return true;
            }
        }
        return false;
    }
    private static void CacheFrozenPose(string sourceHash,Model3DGroup scene,Vector3 center,float size,Rect3D framingBounds)
    {
        if(string.IsNullOrWhiteSpace(sourceHash))return;
        lock(FrozenPoseCacheLock)
        {_frozenPoseCacheHash=sourceHash;_frozenPoseCacheScene=new(scene);_frozenPoseCacheCenter=center;_frozenPoseCacheSize=size;_frozenPoseCacheBounds=framingBounds;}
    }
    public static Model3DGroup CreateFrozenPose(ModelAsset asset,CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var hasCache=TryGetFrozenPoseCache(asset.SourceHash,out var cached,out var center,out var size,out var framingBounds);
        if(cached is not null)return cached;
        var sampler=new RigSampler(asset);
        if(!hasCache)
        {var envelope=Envelope(asset,sampler);var bounds=Bounds(envelope);center=bounds.Center;size=bounds.Size;framingBounds=Framing(envelope,center,size);}
        cancel.ThrowIfCancellationRequested();sampler.Sample(DefaultClip(asset),0);
        var meshes=asset.Meshes.Select((m,i)=>m with{Positions=(Vector3[])sampler.Positions[i].Clone(),Normals=(Vector3[])sampler.Normals[i].Clone()}).ToArray();
        var scene=ModelSceneFactory.Create(asset with{Meshes=meshes,Rig=null},cancel,center,size);
        CacheFrozenPose(asset.SourceHash,scene,center,size,framingBounds);
        return scene;
    }
    public void Select(int index)
    {
        if(index<0||index>=Clips.Length)return;
        if(_nativeActionClip>=0)
        {
            EndNativeAction(false);
            if(_companionAction!=CompanionActionKind.None)
            {_companionAction=CompanionActionKind.None;_companionActionElapsed=0;_ambientAction=false;_sampler.CompanionAction=default;CompanionActionChanged?.Invoke();}
        }
        SwitchClip(index);
    }
    private void SwitchClip(int index,bool snapshotOnly=false)
    {
        if(index<0||index>=Clips.Length||index==ClipIndex)return;
        BeginPoseTransition();
        _fromClip=snapshotOnly?-1:ClipIndex;_fromSeconds=_seconds;_blendSeconds=snapshotOnly?1:0;ClipIndex=index;_seconds=0;
        if(!IsPlaying){_blendSeconds=1;_poseTransitionStarted=double.NegativeInfinity;Render();}
    }
    private void EndNativeAction(bool restoreClip)
    {
        if(_nativeActionClip<0)return;
        var returnClip=_nativeActionReturnClip;
        _nativeActionClip=-1;_nativeActionReturnClip=-1;_nativeActionDuration=0;
        if(restoreClip&&returnClip>=0&&returnClip<Clips.Length)SwitchClip(returnClip);
    }
    public void Configure(bool play,double speed,int fps)
    {
        _speed=Math.Clamp(speed,.5,1.5);if(_requestedFps!=Math.Clamp(fps,12,30))_adaptiveFps=30;_requestedFps=Math.Clamp(fps,12,30);SetInterval();
        if(play&&Clips.Length>0&&!Faulted)
        {
            if(!_playing){BeginPoseTransition();_last=_clock.Elapsed.TotalSeconds;_scheduledInterval=_interval;_frameTimer.Interval=TimeSpan.FromSeconds(_scheduledInterval);_playing=true;_frameTimer.Start();}
        }
        else Stop();
    }
    private static bool IsIdleClip(string name)=>name.Contains("idle",StringComparison.OrdinalIgnoreCase)||name.Contains("\u5f85\u673a",StringComparison.OrdinalIgnoreCase);
    private double ClipPlaybackRate(int index)=>index>=0&&index<Clips.Length?RigClipResolver.PlaybackRate(Clips[index]):1;
    private void SetInterval()
    {
        _fps=Math.Min(_requestedFps,_adaptiveFps);
        if(Clips.Length>0&&_blendSeconds>=.2&&IsIdleClip(Clips[ClipIndex].Name)&&_companionAction==CompanionActionKind.None&&!_sampler.CompanionActionOverride&&_clock.Elapsed.TotalSeconds-_gestureChangedAt>.25&&_clock.Elapsed.TotalSeconds-_poseTransitionStarted>.24)_fps=Math.Min(_fps,6);
        _interval=1d/_fps;
    }
    private static double ClampPlaybackDelta(double actualDelta,double scheduledInterval)=>Math.Clamp(actualDelta,0,Math.Max(.1,scheduledInterval*1.5));
    private void Tick(object? sender,EventArgs e)
    {
        if(!_playing)return;
        var now=_clock.Elapsed.TotalSeconds;
        var actualDelta=now-_last;
        var previousInterval=_scheduledInterval;
        var delta=ClampPlaybackDelta(actualDelta,previousInterval);_last=now;
        _seconds+=delta*_speed*ClipPlaybackRate(ClipIndex);
        if(_fromClip>=0)_fromSeconds+=delta*_speed*ClipPlaybackRate(_fromClip);
        _blendSeconds+=delta;
        var currentName=Clips[ClipIndex].Name;
        var idleClip=IsIdleClip(currentName);
        if(idleClip&&!string.Equals(currentName,"Human_Idle",StringComparison.OrdinalIgnoreCase))
        {
            var idle=CompanionIdleMotion.Sample(now);
            _sampler.CompanionIdleBody=idle.Body;_sampler.CompanionIdleHead=idle.Head;_sampler.CompanionBreathing=idle.Breathing;
        }
        else
        {
            _sampler.CompanionIdleBody=Vector3.Zero;_sampler.CompanionIdleHead=Vector3.Zero;_sampler.CompanionBreathing=0;
        }
        var previousAction=_companionAction;
        UpdateCompanionAction(now,idleClip,delta*_speed);
        if(previousAction!=_companionAction)CompanionActionChanged?.Invoke();
        SetInterval();
        var start=Stopwatch.GetTimestamp();
        try { Render(_companionAction!=CompanionActionKind.None || (FrameCount & 1) == 0); }
        catch (Exception ex) { Faulted=true; Stop(); CrashLogger.WriteThrottled("animation-frame",ex);PlaybackFailed?.Invoke();return; }
        LastFrameMilliseconds=Stopwatch.GetElapsedTime(start).TotalMilliseconds;WorstFrameMilliseconds=Math.Max(WorstFrameMilliseconds,LastFrameMilliseconds);
        // Skip late frames rather than queuing catch-up work ahead of mouse input.
        if(LastFrameMilliseconds>24)_adaptiveFps=15;
        // DispatcherTimer fires after the previous UI/render work; subtract that measured delay so the visual cadence stays near the requested FPS.
        var dispatchOverhead=Math.Clamp(actualDelta-previousInterval,0,_interval*.5);
        _scheduledInterval=Math.Max(1d/240,_interval-dispatchOverhead);
        _frameTimer.Interval=TimeSpan.FromSeconds(_scheduledInterval);
    }
    private void UpdateCompanionAction(double now,bool idleClip,double actionDelta)
    {
        if(_sampler.CompanionActionOverride)return;
        if(!idleClip&&_ambientAction&&_nativeActionClip<0)
        {
            _companionAction=CompanionActionKind.None;_companionActionElapsed=0;_ambientAction=false;_sampler.CompanionAction=default;
        }
        if(_nativeActionClip>=0)
        {
            _companionActionElapsed+=actionDelta;
            if(_companionActionElapsed>=_nativeActionDuration)
            {
                var returnClip=_nativeActionReturnClip;
                EndNativeAction(false);
                _companionAction=CompanionActionKind.None;_companionActionElapsed=0;_ambientAction=false;_sampler.CompanionAction=default;
                if(returnClip>=0&&returnClip<Clips.Length)SwitchClip(returnClip,snapshotOnly:true);
            }
            else _sampler.CompanionAction=default;
            return;
        }
        if(_companionAction!=CompanionActionKind.None&&CompanionActionMotion.TryGetDefinition(_companionAction,out var active))
        {
            _companionActionElapsed+=actionDelta;
            if(_companionActionElapsed>=active.DurationSeconds)
            {
                _companionAction=CompanionActionKind.None;_companionActionElapsed=0;_ambientAction=false;_sampler.CompanionAction=default;
            }
            else _sampler.CompanionAction=CompanionActionMotion.Sample(_companionAction,_companionActionElapsed);
        }
        else
        {
            _sampler.CompanionAction=default;
            if(idleClip&&now>=_nextAmbientAction&&AmbientActions.Length>0)
            {
                var choice=AmbientActions[Random.Shared.Next(AmbientActions.Length)];
                if(choice==_lastAmbientAction)choice=AmbientActions[(Array.IndexOf(AmbientActions,choice)+1)%AmbientActions.Length];
                _lastAmbientAction=choice;SetCompanionAction(choice);_ambientAction=true;
                var duration=_nativeActionClip>=0?_nativeActionDuration:CompanionActionMotion.TryGetDefinition(choice,out var selected)?selected.DurationSeconds:1;
                _nextAmbientAction=now+duration+NextAmbientGap();
            }
        }
    }
    public void SetCompanionAction(CompanionActionKind action)
    {
        if(Faulted||!SupportsCompanionActions)return;
        BeginPoseTransition();
        if(_nativeActionClip>=0)
        {
            var returnClip=_nativeActionReturnClip;
            EndNativeAction(false);
            if(returnClip>=0&&returnClip<Clips.Length){ClipIndex=returnClip;_seconds=_fromClip==returnClip?_fromSeconds:0;_fromClip=-1;_blendSeconds=1;}
        }
        var now=_clock.Elapsed.TotalSeconds;
        if(action is CompanionActionKind.None or CompanionActionKind.Idle||!CompanionActionMotion.TryGetDefinition(action,out var definition))
        {
            _companionAction=CompanionActionKind.None;_companionActionElapsed=0;_ambientAction=false;_sampler.CompanionAction=default;
            _nextAmbientAction=now+NextAmbientGap();RefreshActionCadence(now);CompanionActionChanged?.Invoke();if(!_playing)Render();return;
        }
        _companionAction=action;_companionActionElapsed=0;_ambientAction=false;
        _nextAmbientAction=now+definition.DurationSeconds+NextAmbientGap();
        var nativeRun=RigClipResolver.FindAction(Clips,action);
        if(nativeRun>=0)
        {
            _nativeActionClip=nativeRun;_nativeActionReturnClip=ClipIndex;
            _nativeActionDuration=Clips[nativeRun].Duration/ClipPlaybackRate(nativeRun);
            _nextAmbientAction=now+_nativeActionDuration+NextAmbientGap();
            if(nativeRun==ClipIndex)
            {_fromClip=-1;_fromSeconds=0;_seconds=0;_blendSeconds=1;if(!IsPlaying)Render();}
            else SwitchClip(nativeRun);
            _sampler.CompanionAction=default;
        }
        else _sampler.CompanionAction=CompanionActionMotion.Sample(action,0);
        RefreshActionCadence(now);CompanionActionChanged?.Invoke();
        if(!_playing)Render();
    }
    private void RefreshActionCadence(double now)
    {
        _last=now;SetInterval();
        if(!_playing)return;
        _scheduledInterval=_interval;_frameTimer.Interval=TimeSpan.FromSeconds(_scheduledInterval);
    }
    public void SetCompanionGesture(Vector3 pose,float waveSwing=0)=>SetCompanionGesture(pose,waveSwing,Vector3.Zero,default,false);
    public void SetCompanionGesture(Vector3 pose,float waveSwing,Vector3 bodyMotion)=>SetCompanionGesture(pose,waveSwing,bodyMotion,default,false);
    public void SetCompanionGesture(Vector3 pose,float waveSwing,Vector3 bodyMotion,CompanionActionPose interactionAction,bool overrideAction,CompanionActionKind nativeAction=CompanionActionKind.None,double actionSeconds=0)
    {
        if(Faulted)return;
        var interactionClip=overrideAction&&nativeAction!=CompanionActionKind.None?RigClipResolver.FindAction(Clips,nativeAction):-1;
        var interactionSeconds=double.IsFinite(actionSeconds)?Math.Max(0,actionSeconds)*(interactionClip>=0?ClipPlaybackRate(interactionClip):1):0;
        if(overrideAction&&!_sampler.CompanionActionOverride)
        {
            var returnClip=_nativeActionReturnClip;
            EndNativeAction(false);
            if(returnClip>=0&&returnClip<Clips.Length){ClipIndex=returnClip;_seconds=_fromClip==returnClip?_fromSeconds:0;_fromClip=-1;_blendSeconds=1;}
            _companionAction=CompanionActionKind.None;_companionActionElapsed=0;_ambientAction=false;_sampler.CompanionAction=default;
        }
        var nativeChanged=interactionClip!=_interactionClip||(interactionClip>=0&&interactionSeconds+.001<_interactionClipSeconds);
        if(nativeChanged)BeginPoseTransition();
        if(interactionClip>=0)interactionAction=default;
        if((_sampler.CompanionGesture==pose&&_sampler.CompanionWaveSwing==waveSwing&&_sampler.CompanionBodyMotion==bodyMotion&&
            _sampler.CompanionInteractionAction==interactionAction&&_sampler.CompanionActionOverride==overrideAction&&!nativeChanged&&
            (interactionClip<0||interactionSeconds==_interactionClipSeconds)&&!IsPoseTransitioning))return;
        _interactionClip=interactionClip;_interactionClipSeconds=interactionSeconds;_interactionNativeKind=interactionClip>=0?nativeAction:CompanionActionKind.None;
        var cadenceChanged=_sampler.CompanionActionOverride!=overrideAction;
        if(cadenceChanged&&!overrideAction)_nextAmbientAction=_clock.Elapsed.TotalSeconds+NextAmbientGap();
        if(cadenceChanged)BeginPoseTransition();
        _gestureChangedAt=_clock.Elapsed.TotalSeconds;
        _sampler.CompanionGesture=pose;_sampler.CompanionWaveSwing=waveSwing;_sampler.CompanionBodyMotion=bodyMotion;
        _sampler.CompanionInteractionAction=interactionAction;_sampler.CompanionActionOverride=overrideAction;
        if(cadenceChanged||_fps<Math.Min(_requestedFps,_adaptiveFps))RefreshActionCadence(_clock.Elapsed.TotalSeconds);
        // Base clip can be held during pointer interaction while bones still acknowledge the gesture.
        if(nativeChanged)CompanionActionChanged?.Invoke();
        if(!_playing&&(_sampler.SupportsCompanionGestures||_interactionClip>=0))Render();
    }
    private void Render(bool updateNormals=true)
    {
        _sampler.PoseTransitionProgress=(float)Math.Clamp((_clock.Elapsed.TotalSeconds-_poseTransitionStarted)/.24,0,1);
        var blend=(float)Math.Clamp(_blendSeconds/.2,0,1);blend=blend*blend*(3-2*blend);
        _sampler.Sample(_interactionClip>=0?_interactionClip:ClipIndex,_interactionClip>=0?_interactionClipSeconds:_seconds,
            _interactionClip>=0?-1:_fromClip,_fromSeconds,_interactionClip>=0?1:blend,updateNormals);_pointBuffer^=1;if(updateNormals)_normalBuffer^=1;
        for(var m=0;m<_geometry.Length;m++)
        {
            var points=_points[m][_pointBuffer];var normals=_normals[m][_normalBuffer];var ps=_sampler.Positions[m];var ns=_sampler.Normals[m];
            for(var i=0;i<ps.Length;i++)
            {var p=(ps[i]-_center)/_size;var n=updateNormals?ns[i]:Vector3.Zero;
                if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)||(updateNormals&&(!float.IsFinite(n.X)||!float.IsFinite(n.Y)||!float.IsFinite(n.Z))))throw new ModelImportException("动作产生了非有限坐标，已停止播放。");
                points[i]=new(p.X,p.Y,p.Z);if(updateNormals)normals[i]=new(n.X,n.Y,n.Z);}
            _geometry[m].Positions=points;if(updateNormals)_geometry[m].Normals=normals;
        }
        FrameCount++;
    }
    private void Stop(){if(!_playing)return;_frameTimer.Stop();_playing=false;}
    public void Dispose(){Stop();_interactionClip=-1;_interactionClipSeconds=0;_interactionNativeKind=CompanionActionKind.None;_sampler.CompanionAction=default;_frameTimer.Tick-=Tick;PlaybackFailed=null;CompanionActionChanged=null;}
}
