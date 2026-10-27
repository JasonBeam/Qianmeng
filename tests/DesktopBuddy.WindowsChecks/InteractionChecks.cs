using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;
using DesktopBuddy.UI;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static async Task RunInteractionSoakAsync(MainWindow pet,PetModelView model,string source,int seconds,List<object> metrics)
    {
        var process=Process.GetCurrentProcess();process.Refresh();var startPrivate=process.PrivateMemorySize64;
        var startCpu=process.TotalProcessorTime;var previousCpu=startCpu;var timer=Stopwatch.StartNew();var progress=Path.ChangeExtension(_report,".soak-progress.json");
        var startedAt=DateTimeOffset.Now;var points=new List<object>();var privateSamples=new List<double>();var cacheSamples=new List<int>();var previous=timer.Elapsed;
        for(var minute=1;minute<=seconds/60;minute++)
        {
            await pet.ImportPetModelAsync(source);await Task.Delay(150);
            process.Refresh();var idleCpuBefore=process.TotalProcessorTime;var idleFramesBefore=pet.AnimationFrameCount;var idleSample=Stopwatch.StartNew();
            await Task.Delay(4000);process.Refresh();var minuteIdleCpu=100*(process.TotalProcessorTime-idleCpuBefore).TotalSeconds/idleSample.Elapsed.TotalSeconds;
            var minuteIdleFps=(pet.AnimationFrameCount-idleFramesBefore)/idleSample.Elapsed.TotalSeconds;
            await SeparateGestureAsync();var point=ElementPoint(model);
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Thread.Sleep(50);Native.LeftDown();Thread.Sleep(70);Native.LeftUp();});
            await Task.Delay(250);await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(180);
            await SeparateGestureAsync();point=ElementPoint(model);
            await InputAsync(()=>
            {
                Native.SetCursorPos(point.X,point.Y);Thread.Sleep(50);Native.LeftDown();Thread.Sleep(90);
                try{for(var i=1;i<=5;i++){Native.SetCursorPos(point.X-i*12,point.Y+i*2);Thread.Sleep(45);}for(var i=1;i<=5;i++){Native.SetCursorPos(point.X-60+i*12,point.Y+10-i*2);Thread.Sleep(45);}}
                finally{Native.LeftUp();}
            });
            await InputAsync(()=>Native.SetCursorPos(20,20));
            var next=previous.Add(TimeSpan.FromSeconds(60));if(timer.Elapsed<next)await Task.Delay(next-timer.Elapsed);
            process.Refresh();var now=timer.Elapsed;var duration=(now-previous).TotalSeconds;var cpuNow=process.TotalProcessorTime;
            var privateMiB=process.PrivateMemorySize64/1048576d;var workingSetMiB=process.WorkingSet64/1048576d;var cacheFiles=Directory.GetFiles(pet.Store.ModelsDirectory,"*.glb").Length;
            privateSamples.Add(privateMiB);cacheSamples.Add(cacheFiles);
            points.Add(new{minute,seconds=now.TotalSeconds,privateMiB,workingSetMiB,
                oneCoreCpuPercent=100*(cpuNow-startCpu).TotalSeconds/now.TotalSeconds,minuteCpuPercent=100*(cpuNow-previousCpu).TotalSeconds/duration,
                idleSampleCpuPercent=minuteIdleCpu,idleSampleFps=minuteIdleFps,
                gestureRendererStopped=!pet.IsInteractionRendering,captureReleased=Mouse.Captured is null,cacheFiles});
            previous=now;previousCpu=cpuNow;
            File.WriteAllText(progress,JsonSerializer.Serialize(new{phase="running",durationSeconds=seconds,startedAt,points},new JsonSerializerOptions{WriteIndented=true}));
        }
        process.Refresh();var endPrivate=process.PrivateMemorySize64;var growth=endPrivate-startPrivate;var endWorkingSet=process.WorkingSet64;
        var warmupCount=Math.Min(5,privateSamples.Count);var baselineWindowMiB=privateSamples.Take(warmupCount).Average();
        var endingWindowMiB=privateSamples.Skip(Math.Max(0,privateSamples.Count-warmupCount)).Average();
        var steadyGrowthMiB=endingWindowMiB-baselineWindowMiB;var peakGrowthMiB=privateSamples.Max()-baselineWindowMiB;
        var cacheDelta=cacheSamples.Max()-cacheSamples.Min();var durationSeconds=timer.Elapsed.TotalSeconds;
        metrics.Add(new{phase="repeated-model-import-interaction-soak",durationSeconds,startPrivateMiB=startPrivate/1048576d,endPrivateMiB=endPrivate/1048576d,
            endWorkingSetMiB=endWorkingSet/1048576d,privateGrowthMiB=growth/1048576d,baselineWindowMiB,endingWindowMiB,steadyGrowthMiB,peakGrowthMiB,
            oneCoreCpuPercent=100*(process.TotalProcessorTime-startCpu).TotalSeconds/durationSeconds,cacheFiles=cacheSamples[^1],cacheDelta,points});
        Check("soak-memory-plateaus",steadyGrowthMiB<24&&peakGrowthMiB<128,$"Memory plateau delta was {steadyGrowthMiB:F1} MiB; peak over warmup was {peakGrowthMiB:F1} MiB.");
        Check("soak-model-cache-bounded",cacheDelta==0,$"Model cache count changed by {cacheDelta} during repeated imports.");
        File.WriteAllText(progress,JsonSerializer.Serialize(new{phase="finished",durationSeconds,startedAt,baselineWindowMiB,endingWindowMiB,steadyGrowthMiB,peakGrowthMiB,cacheDelta,points},new JsonSerializerOptions{WriteIndented=true}));
        Check("soak-ends-without-capture-or-animation-loop",Mouse.Captured is null&&!pet.IsInteractionRendering,"Input capture or idle interaction loop leaked after soak.");
    }
    private static async Task RunInteractionChecksAsync(Application app,string[] args)
    {
        var temp=Path.Combine(Path.GetTempPath(),"Qianmeng-Interaction-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        Native.GetCursorPos(out var originalCursor);MainWindow? pet=null;var success=false;var metrics=new List<object>();
        try
        {
            var spring=new CompanionMotion{TargetYaw=20,TargetRoll=-8,TargetScale=.97,TargetLift=.018};
            for(var i=0;i<120;i++)spring.Step(1d/60);
            Check("spring-approaches-target",Math.Abs(spring.Yaw-20)<.03&&Math.Abs(spring.Roll+8)<.03,"Drag spring fails to settle.");
            spring.TargetYaw=-20;spring.Step(1d/30);Check("spring-interrupt-does-not-snap",spring.Yaw>0,"Direction reversal snapped to opposite side.");
            spring.TargetYaw=double.NaN;spring.TargetRoll=double.PositiveInfinity;spring.TargetLift=double.NegativeInfinity;
            for(var i=0;i<200;i++)spring.Step(.5);
            Check("spring-rejects-nonfinite-targets",double.IsFinite(spring.Yaw)&&Math.Abs(spring.Yaw)<.03&&Math.Abs(spring.Roll)<.03,"Invalid spring state.");
            spring.Reset();Check("spring-reset-identity",spring.Yaw==0&&spring.Roll==0&&spring.Scale==1&&spring.Lift==0,"Reset not identity.");
            var lookDirections=Enumerable.Range(0,CompanionLookMotion.DirectionCount).Select(i=>{var angle=i*Math.Tau/CompanionLookMotion.DirectionCount;return CompanionLookMotion.Quantize(.8*Math.Cos(angle),.8*Math.Sin(angle)).Sector;}).Distinct().Count();
            Check("interaction-look-covers-sixteen-directions",lookDirections==16,"Pointer gaze did not map to all 16 smooth direction targets: "+lookDirections);
            var visibleLook=CompanionLookMotion.Quantize(.4,.3);
            Check("interaction-look-sector-has-visible-strength",Math.Sqrt(visibleLook.Horizontal*visibleLook.Horizontal+visibleLook.Vertical*visibleLook.Vertical)>.95,"16-way gaze target was too weak to read at desktop size.");
            Check("interaction-look-dead-zone-is-neutral",CompanionLookMotion.Quantize(.02,-.03).Sector==-1,"Centered attention should return to neutral.");
            var dragMotion=new CompanionDragMotion();for(var i=0;i<12;i++)dragMotion.Sample(2,.10);
            Check("interaction-slow-drag-does-not-run",dragMotion.RunDirection==0,"A slow 20 px/s drag incorrectly entered the run gait.");
            dragMotion.Reset();for(var i=0;i<8;i++)dragMotion.Sample(15,.065);
            Check("interaction-fast-drag-enters-run",dragMotion.RunDirection==1,"A brisk right drag did not enter the run gait.");
            dragMotion.Sample(-18,.08);
            Check("interaction-reverse-drag-switches-gait",dragMotion.RunDirection==-1,"A clear direction reversal did not switch the run gait.");
            for(var i=0;i<36;i++)dragMotion.Decay(1d/30);
            Check("interaction-stopped-drag-exits-run",dragMotion.RunDirection==0&&Math.Abs(dragMotion.Velocity)<5,"The run gait did not decay after the pointer stopped.");
            var at=Array.IndexOf(args,"--model-file");var source=Path.GetFullPath(args[at+1]);var asset=GlbReader.Load(source);var sampler=new RigSampler(asset);sampler.Sample(0,0);var first=sampler.Positions.Select(x=>(Vector3[])x.Clone()).ToArray();
            sampler.CompanionGesture=new(.15f,.12f,.8f);sampler.Sample(0,0);
            Check("gesture-recognizes-mixamo",sampler.SupportsCompanionGestures,"Supported head bone missing.");
            Check("gesture-deforms-bones",sampler.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,first[i][j]))).Max()>.01,"Gesture is not deforming bones.");
            Check("gesture-mesh-finite",sampler.Positions.SelectMany(x=>x).All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)),"Invalid deformation.");
            sampler.CompanionGesture=Vector3.Zero;sampler.CompanionWaveSwing=0;sampler.CompanionBodyMotion=new(.18f,.12f,.14f);sampler.Sample(0,0);
            Check("gesture-animates-whole-body",sampler.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,first[i][j]))).Max()>.002,"Body gesture did not deform hips/spine/shoulder bones.");
            sampler.CompanionBodyMotion=Vector3.Zero;sampler.Sample(0,0);
            Check("gesture-no-cumulative-bone-drift",sampler.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,first[i][j]))).Max()<1e-6,"Bone pose accumulated offsets.");
            var store=new LocalStore(Path.Combine(temp,"profile"));pet=new MainWindow(store,_=>{},new ChatService(()=>null));pet.Settings.AutoHideFullscreen=false;
            pet.Left=SystemParameters.WorkArea.Right-340;pet.Top=SystemParameters.WorkArea.Top+40;pet.Show();await Task.Delay(300);
            for(var wait=0;wait<150&&pet.IsModelLoading;wait++)await Task.Delay(100);
            await pet.ImportPetModelAsync(source);
            var model=(PetModelView)pet.FindName("PetModel");await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(1800);
            Check("interaction-system-animation-allowed",SystemParameters.ClientAreaAnimation,"System reduced animation enabled; no setting changes made.");
            Check("interaction-idle-unsubscribes",!pet.IsInteractionRendering,"Gesture renderer ticks without input.");
            var frames=pet.InteractionFrameCount;await Task.Delay(300);Check("interaction-idle-no-extra-frames",frames==pet.InteractionFrameCount,"Idle gesture frame loop leaked.");
            var idleProcess=Process.GetCurrentProcess();idleProcess.Refresh();var idleCpu=idleProcess.TotalProcessorTime;var idleTimer=Stopwatch.StartNew();
            await Task.Delay(5000);idleProcess.Refresh();
            metrics.Add(new{phase="gesture-idle-five-second-sample",privateMiB=idleProcess.PrivateMemorySize64/1048576d,workingSetMiB=idleProcess.WorkingSet64/1048576d,
                oneCoreCpuPercent=100*(idleProcess.TotalProcessorTime-idleCpu).TotalSeconds/idleTimer.Elapsed.TotalSeconds,interactionFrames=pet.InteractionFrameCount});
            var root=(FrameworkElement)pet.FindName("RootGrid");var trace=new List<string>();
            root.PreviewMouseDown+=(_,e)=>trace.Add($"preview-down:{e.ClickCount}:{e.GetPosition(pet)}:{e.OriginalSource?.GetType().Name}");
            root.PreviewMouseUp+=(_,e)=>trace.Add($"preview-up:{e.GetPosition(pet)}");
            root.PreviewMouseMove+=(_,e)=>trace.Add($"move:{e.GetPosition(pet)}");
            root.MouseMove+=(_,e)=>trace.Add($"bubble-move:{e.GetPosition(pet)}:{e.Handled}");
            root.AddHandler(UIElement.MouseMoveEvent,new MouseEventHandler((_,e)=>trace.Add($"handled-move:{e.GetPosition(pet)}")),true);
            root.LostMouseCapture+=(_,_)=>trace.Add("lost-capture");
            root.MouseLeave+=(_,_)=>trace.Add("root-leave");
            pet.Deactivated+=(_,_)=>trace.Add("deactivated");
            var hookSource=HwndSource.FromHwnd(new WindowInteropHelper(pet).Handle)!;
            hookSource.AddHook((nint h,int m,nint w,nint l,ref bool handled)=>{if(m is 0x200 or 0x201 or 0x202 or 0xA1 or 0x21)trace.Add($"wm:{m:X}:{l}");return 0;});
            await SeparateGestureAsync();var point=ElementPoint(model);var local=pet.PointFromScreen(new Point(point.X,point.Y));
            var hit=pet.InputHitTest(local)?.GetType().Name??"null";var windowHit=ModelWindowFromPoint(point);
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Thread.Sleep(50);Native.LeftDown();});await Task.Delay(120);
            Check("interaction-press-feedback",pet.InteractionState=="pressed"&&ReferenceEquals(Mouse.Captured,root),$"Native press has no feedback; state={pet.InteractionState}, captured={Mouse.Captured?.GetType().Name}, isPressed={GetField<bool>(pet,"_leftPressed")}, drag={GetField<bool>(pet,"_dragStarted")}, point={point.X},{point.Y}, local={local}, hit={hit}, windowFromPoint={windowHit}, petHwnd={new WindowInteropHelper(pet).Handle}, fg={NativeMethods.GetForegroundWindow()}, active={pet.IsActive}, visible={pet.IsVisible}, through={pet.Settings.ClickThrough}, trace={string.Join("|",trace)}.");
            await InputAsync(Native.LeftUp);
            await Task.Delay((int)(CompanionActionMotion.Library.First(a=>a.Kind==CompanionActionKind.Waving).DurationSeconds*300));
            Check("interaction-first-click-selects-wave",pet.InteractionActionKind==CompanionActionKind.Waving,"First click did not start the wave action: "+pet.InteractionActionKind);
            // Measure a continuous live interval: screenshot encoding adds variable time,
            // so two arbitrary frames can alias the wave even when the hand moves.
            var waveSamples=new List<(double Seconds,CompanionActionPose Pose)>();
            void SampleWave() => waveSamples.Add((GetField<Stopwatch>(pet,"_gestureClock").Elapsed.TotalSeconds-GetField<double>(pet,"_interactionActionStart"),pet.InteractionActionPose));
            SampleWave();var waveEarly=pet.InteractionActionPose;
            Check("interaction-wave-includes-head-nod",waveEarly.Head.Y>.05f,"Greeting wave omitted its anticipatory nod: "+waveEarly.Head.Y.ToString("F3"));
            var wavePlayer=typeof(MainWindow).GetField("_animationPlayer",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(pet)!;
            var wavePlayerFlags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var waveFpsField=wavePlayer.GetType().GetField("_fps",wavePlayerFlags)!;
            var waveFrameCount=wavePlayer.GetType().GetProperty("FrameCount")!;
            await InputAsync(()=>Native.SetCursorPos(20,20));
            var waveFramesBefore=(long)waveFrameCount.GetValue(wavePlayer)!;
            await Task.Delay(180);
            var waveFpsAfterLeave=(int)waveFpsField.GetValue(wavePlayer)!;
            var waveFramesAfterLeave=(long)waveFrameCount.GetValue(wavePlayer)!;
            var waveFramesWhilePointerAway=waveFramesAfterLeave-waveFramesBefore;
            Check("interaction-wave-keeps-render-cadence-after-pointer-leaves",waveFpsAfterLeave>=20&&waveFramesWhilePointerAway>=3,$"Greeting cadence fell after pointer leave: fps={waveFpsAfterLeave}, frames={waveFramesWhilePointerAway}.");
            metrics.Add(new{phase="wave-cadence-after-pointer-leave",fps=waveFpsAfterLeave,frames=waveFramesWhilePointerAway});
            var waveObserver=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};
            waveObserver.Tick+=(_,_)=>SampleWave();waveObserver.Start();
            try
            {
                var halfWavePeriod=(int)(CompanionActionMotion.Library.First(a=>a.Kind==CompanionActionKind.Waving).DurationSeconds/2.25*500);
                SaveWindowImage(pet,Path.ChangeExtension(_report,".wave-1.png"));await Task.Delay(halfWavePeriod);
                SampleWave();SaveWindowImage(pet,Path.ChangeExtension(_report,".wave-2.png"));await Task.Delay(halfWavePeriod);
                SampleWave();SaveWindowImage(pet,Path.ChangeExtension(_report,".wave-3.png"));
            }
            finally{waveObserver.Stop();}
            var waveShoulderRange=waveSamples.Max(x=>x.Pose.RightArm)-waveSamples.Min(x=>x.Pose.RightArm);
            var waveForearmRange=waveSamples.Max(x=>x.Pose.RightForeArm)-waveSamples.Min(x=>x.Pose.RightForeArm);
            var waveWristMin=waveSamples.Min(x=>x.Pose.RightHand);var waveWristMax=waveSamples.Max(x=>x.Pose.RightHand);
            metrics.Add(new{phase="first-click-wave",shoulderRange=waveShoulderRange,forearmRange=waveForearmRange,wristMin=waveWristMin,wristMax=waveWristMax,samples=waveSamples.Select(x=>new{
                seconds=x.Seconds,shoulder=x.Pose.RightArm,shoulderPitch=x.Pose.RightArmPitch,forearm=x.Pose.RightForeArm,forearmPitch=x.Pose.RightForeArmPitch,wrist=x.Pose.RightHand}).ToArray()});
            Check("interaction-wave-has-moving-shoulder-and-wrist",waveShoulderRange>.10f&&waveForearmRange>.30f&&waveWristMax-waveWristMin>.65f&&waveWristMin<-.15f&&waveWristMax>.15f,
                $"Wave must swing both ways with shoulder and forearm support; shoulderRange={waveShoulderRange:F3}, forearmRange={waveForearmRange:F3}, wrist={waveWristMin:F3}..{waveWristMax:F3}.");
            await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(1400);
            point=ElementPoint(model);var followTargetX=point.X-48;var petHandle=new WindowInteropHelper(pet).Handle;var followTraceStart=trace.Count;await InputAsync(()=>{Native.SetCursorPos(followTargetX,point.Y);Native.PostMouseMove(petHandle,followTargetX,point.Y);});
            await Task.Delay(80);var followEarlyYaw=pet.InteractionYaw;await Task.Delay(80);
            var followLateYaw=pet.InteractionYaw;var followHeadWait=Stopwatch.StartNew();
            while(Math.Abs(pet.InteractionHeadYaw)<.03&&followHeadWait.Elapsed<TimeSpan.FromMilliseconds(450))await Task.Delay(16);
            var followHeadYaw=pet.InteractionHeadYaw;var followRendering=pet.IsInteractionRendering;
            var followMoves=trace.Skip(followTraceStart).Count(x=>x.StartsWith("move:"));var bubbleMoves=trace.Skip(followTraceStart).Count(x=>x.StartsWith("bubble-move:"));var handledMoves=trace.Skip(followTraceStart).Count(x=>x.StartsWith("handled-move:"));var rawMoveMessages=trace.Skip(followTraceStart).Count(x=>x.StartsWith("wm:200:"));
            var lookTarget=GetField<double>(pet,"_lookX");var lookDirection=pet.InteractionLookDirection;var canAnimate=(bool)pet.GetType().GetProperty("CanAnimateInteraction",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(pet)!;
            var followBounds=Bounds(pet);var followLocal=pet.PointFromScreen(new Point(followTargetX,point.Y));var stage=(FrameworkElement)pet.FindName("PetStage");
            metrics.Add(new{phase="mouse-follow",headYaw=followHeadYaw,headYawWaitMs=followHeadWait.Elapsed.TotalMilliseconds,lookTarget,lookDirection,yawAt80ms=followEarlyYaw,yawAt160ms=followLateYaw,rendering=followRendering,state=pet.InteractionState,canAnimate,followMoves,bubbleMoves,handledMoves,rawMoveMessages,targetX=followTargetX,targetY=point.Y,windowLeft=followBounds.Left,windowTop=followBounds.Top,windowRight=followBounds.Right,windowBottom=followBounds.Bottom,localX=followLocal.X,localY=followLocal.Y,stageWidth=stage.ActualWidth,stageHeight=stage.ActualHeight,trace=trace.Skip(followTraceStart).Take(8).ToArray()});
            Check("interaction-live-pointer-selects-look-sector",lookDirection>=0&&lookDirection<CompanionLookMotion.DirectionCount,"Live pointer did not select a 16-way gaze sector: "+lookDirection);
            SaveWindowImage(pet,Path.ChangeExtension(_report,".look.png"));
            Check("interaction-mouse-follow-turns",followMoves>0&&followEarlyYaw<-.05&&followLateYaw<followEarlyYaw&&Math.Abs(followHeadYaw)>.03&&followRendering,$"Pointer follow response was not smooth; moves={followMoves}, bubble={bubbleMoves}, handled={handledMoves}, raw={rawMoveMessages}, target={followTargetX},{point.Y}, look={lookTarget:F3}, head={followHeadYaw:F4}, yaw80={followEarlyYaw:F3}, yaw160={followLateYaw:F3}, rendering={followRendering}, canAnimate={canAnimate}, state={pet.InteractionState}, local={followLocal}, bounds={followBounds.Left},{followBounds.Top},{followBounds.Right},{followBounds.Bottom}, trace={string.Join("|",trace.Skip(followTraceStart).Take(8))}.");
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Native.PostMouseMove(petHandle,point.X,point.Y);});await Task.Delay(1300);
            Check("interaction-mouse-follow-settles",Math.Abs(pet.InteractionYaw)<.1&&Math.Abs(pet.InteractionHeadYaw)<.012&&!pet.IsInteractionRendering,"Mouse-follow spring did not settle.");
            await SeparateGestureAsync();point=ElementPoint(model);
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Thread.Sleep(50);Native.LeftDown();});await Task.Delay(120);
            Check("interaction-click-press-feedback",pet.InteractionState=="pressed"&&ReferenceEquals(Mouse.Captured,root),"Native click press was not captured.");
            await InputAsync(Native.LeftUp);await Task.Delay(250);
            Check("interaction-click-greeting",pet.InteractionState=="greeting","Click did not start greeting.");
            var activePlayer=typeof(MainWindow).GetField("_animationPlayer",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(pet)!;
            var activeSampler=activePlayer.GetType().GetField("_sampler",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(activePlayer)!;
            var greetingPose=(Vector3)activeSampler.GetType().GetProperty(nameof(RigSampler.CompanionGesture))!.GetValue(activeSampler)!;
            var greetingAction=(CompanionActionPose)activeSampler.GetType().GetProperty(nameof(RigSampler.CompanionInteractionAction))!.GetValue(activeSampler)!;
            Check("interaction-second-click-selects-nod",pet.InteractionActionKind==CompanionActionKind.Nodding,"Second click did not select the nod: "+pet.InteractionActionKind);
            Check("interaction-second-click-nod-is-visible",greetingAction.Head.Y>.05f&&greetingAction.Body.Length()>.008f,"Second click nod did not move the head and torso.");
            metrics.Add(new{phase="second-click-nod",action=pet.InteractionActionKind.ToString(),body=greetingAction.Body,head=greetingAction.Head,headYaw=greetingPose.X,headPitch=greetingPose.Y});
            SaveWindowImage(pet,Path.ChangeExtension(_report,".greeting-1.png"));
            await Task.Delay(400);SaveWindowImage(pet,Path.ChangeExtension(_report,".greeting-2.png"));
            await Task.Delay(500);SaveWindowImage(pet,Path.ChangeExtension(_report,".greeting-3.png"));
            await Task.Delay(350);SaveWindowImage(pet,Path.ChangeExtension(_report,".greeting-4.png"));
            await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(1500);
            var residualWave=(float)activeSampler.GetType().GetProperty(nameof(RigSampler.CompanionWaveSwing))!.GetValue(activeSampler)!;
            Check("interaction-greeting-finishes",!pet.IsInteractionRendering&&Mouse.Captured is null,"Greeting did not stop.");
            Check("interaction-greeting-wave-resets",Math.Abs(residualWave)<.0001f,"Greeting hand swing did not return to neutral: "+residualWave.ToString("F4"));
            await SeparateGestureAsync();await ClickAsync(ElementPoint(model));await Task.Delay(240);
            Check("interaction-click-cycles-to-curious",pet.InteractionActionKind==CompanionActionKind.Curious,"Third click did not advance to curious: "+pet.InteractionActionKind);
            var poseBeforeInterrupt=pet.InteractionActionPose;await ClickAsync(ElementPoint(model));await Task.Delay(70);var poseAfterInterrupt=pet.InteractionActionPose;
            Check("interaction-click-interruption-is-finite",poseAfterInterrupt.IsFinite&&pet.InteractionActionKind==CompanionActionKind.Bowing,"Interrupted action did not transition to a finite next pose.");
            Check("interaction-click-interruption-blends",Math.Abs(poseAfterInterrupt.RightArm-poseBeforeInterrupt.RightArm)<1.8f&&Vector3.Distance(poseAfterInterrupt.Body,poseBeforeInterrupt.Body)<.5f,"Action interruption snapped instead of blending.");
            await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(1600);
            double maxYaw=0,minYaw=0,maxRoll=0,maxDragLegMotion=0;var observedDrag=false;var sawRunningLeft=false;var sawRunningRight=false;
            var dragLeftFrame=Path.ChangeExtension(_report,".drag-left.png");var dragRightFrame=Path.ChangeExtension(_report,".drag-right.png");
            var dragLeftCaptured=false;var dragRightCaptured=false;double maxDragBody=0,maxDragHeadYaw=0;
            var observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};
            observer.Tick+=(_,_)=>
            {
                if(pet.InteractionState!="dragging")return;
                observedDrag=true;maxYaw=Math.Max(maxYaw,pet.InteractionYaw);minYaw=Math.Min(minYaw,pet.InteractionYaw);maxRoll=Math.Max(maxRoll,Math.Abs(pet.InteractionRoll));
                var livePose=(Vector3)activeSampler.GetType().GetProperty(nameof(RigSampler.CompanionGesture))!.GetValue(activeSampler)!;
                var liveBody=(Vector3)activeSampler.GetType().GetProperty(nameof(RigSampler.CompanionBodyMotion))!.GetValue(activeSampler)!;
                maxDragHeadYaw=Math.Max(maxDragHeadYaw,Math.Abs(livePose.X));maxDragBody=Math.Max(maxDragBody,liveBody.Length());
                var actionPose=pet.InteractionActionPose;maxDragLegMotion=Math.Max(maxDragLegMotion,Math.Abs(actionPose.LeftLeg)+Math.Abs(actionPose.RightLeg)+Math.Abs(actionPose.LeftKnee)+Math.Abs(actionPose.RightKnee));
                sawRunningLeft|=pet.InteractionActionKind==CompanionActionKind.RunningLeft;sawRunningRight|=pet.InteractionActionKind==CompanionActionKind.RunningRight;
                if(!dragLeftCaptured&&pet.InteractionYaw< -2){SaveWindowImage(pet,dragLeftFrame);dragLeftCaptured=true;}
                if(!dragRightCaptured&&pet.InteractionYaw>2){SaveWindowImage(pet,dragRightFrame);dragRightCaptured=true;}
            };
            observer.Start();var old=Bounds(pet);await SeparateGestureAsync();point=ElementPoint(model);
            await InputAsync(()=>
            {
                Native.SetCursorPos(point.X,point.Y);Thread.Sleep(50);Native.LeftDown();Thread.Sleep(100);
                try
                {
                    for(var i=1;i<=8;i++){Native.SetCursorPos(point.X-i*14,point.Y+i*2);Thread.Sleep(65);}
                    for(var i=1;i<=8;i++){Native.SetCursorPos(point.X-112+i*18,point.Y+16-i*2);Thread.Sleep(65);}
                }
                finally{Native.LeftUp();}
            });observer.Stop();await Task.Delay(80);
            Check("interaction-drag-native-motion",observedDrag&&Math.Abs(Bounds(pet).Left-old.Left)>15,"Drag did not move actual window.");
            Check("interaction-left-right-turn",maxYaw>12&&minYaw< -10,"Directional yaw must be visibly expressive: "+minYaw+" / "+maxYaw);
            Check("interaction-drag-sway",maxRoll>6&&maxRoll<10,$"Drag sway must be visible and remain bounded; observed {maxRoll:F2} degrees.");
            Check("interaction-drag-preview-frames",dragLeftCaptured&&dragRightCaptured,"Could not capture both directional drag poses.");
            Check("interaction-drag-full-body-response",maxDragBody>.05&&maxDragHeadYaw>.05,$"Drag did not animate head and torso; body={maxDragBody:F3}, head={maxDragHeadYaw:F3}.");
            Check("interaction-drag-running-gait-both-directions",sawRunningLeft&&sawRunningRight&&maxDragLegMotion>.12,$"Drag gait was incomplete; left={sawRunningLeft}, right={sawRunningRight}, legMotion={maxDragLegMotion:F3}.");
            SaveWindowImage(pet,Path.ChangeExtension(_report,".release.png"));await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(1600);
            Check("interaction-release-settles",Math.Abs(pet.InteractionYaw)<.1&&Math.Abs(pet.InteractionRoll)<.1&&!pet.IsInteractionRendering&&Mouse.Captured is null,"Release did not settle cleanly.");
            await SeparateGestureAsync();await ClickAsync(ElementPoint(model));await Task.Delay(100);pet.HideToTray();var hiddenFrames=pet.InteractionFrameCount;await Task.Delay(300);
            Check("interaction-hide-stops-rendering",!pet.IsInteractionRendering&&hiddenFrames==pet.InteractionFrameCount,"Hidden interaction renders.");pet.RestoreFromTray();await Task.Delay(200);
            pet.PauseAnimation();await SeparateGestureAsync();await ClickAsync(ElementPoint(model));await Task.Delay(250);
            Check("interaction-disabled-stays-still",!pet.IsInteractionRendering&&!pet.IsAnimationPlaying,"Disable/pause did not suppress interaction.");pet.FollowActivityAnimations();
            await SeparateGestureAsync();point=ElementPoint(model);
            await InputAsync(()=>
            {
                Native.SetCursorPos(point.X,point.Y);Thread.Sleep(50);Native.LeftDown();Thread.Sleep(40);Native.LeftUp();
                Thread.Sleep(80);Native.LeftDown();Thread.Sleep(40);Native.LeftUp();
            });
            await Task.Delay(250);
            var centers=app.Windows.OfType<ControlCenterWindow>().ToArray();
            Check("interaction-double-click-opens-single-center",centers.Length==1&&centers[0].IsVisible&&Mouse.Captured is null&&!GetField<bool>(pet,"_leftPressed")&&!GetField<bool>(pet,"_dragStarted"),
                $"Double-click did not open one responsive control center; count={centers.Length}, capture={Mouse.Captured?.GetType().Name}, leftPressed={GetField<bool>(pet,"_leftPressed")}, drag={GetField<bool>(pet,"_dragStarted")}.");
            if(centers.Length==1)
            {
                var doubleClickCenter=centers[0];
                await FocusWindowAsync(new WindowInteropHelper(doubleClickCenter).Handle);
                var navigation=GetField<System.Windows.Controls.ListBox>(doubleClickCenter,"_navigation");
                var settingsItem=(System.Windows.Controls.ListBoxItem)navigation.Items[3];
                settingsItem.BringIntoView();await Task.Delay(100);
                await ClickAsync(ElementPoint(settingsItem));
                Check("interaction-double-click-center-remains-clickable",navigation.SelectedIndex==3&&GetField<System.Windows.Controls.TextBlock>(doubleClickCenter,"_pageTitle").Text=="设置","Control center did not respond to a navigation click after double-click opening.");
                doubleClickCenter.Close();
            }
            await Task.Delay(200);
            Check("interaction-panel-closes-cleanly",Mouse.Captured is null&&pet.ModelScene is not null,"Panel focus/capture regressed.");
            metrics.Add(new{maxYaw,minYaw,maxRoll,maxDragBody,maxDragHeadYaw,maxDragLegMotion,sawRunningLeft,sawRunningRight,gestureFrames=pet.InteractionFrameCount});
            var soakArg=Array.IndexOf(args,"--interaction-soak-seconds");var soakSeconds=soakArg>=0&&int.TryParse(args[soakArg+1],out var parsedSoak)?Math.Clamp(parsedSoak,60,3600):0;
            if(soakSeconds>0)await RunInteractionSoakAsync(pet,model,source,soakSeconds,metrics);
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            try{Native.LeftUp();Mouse.Capture(null);Native.SetCursorPos(originalCursor.X,originalCursor.Y);foreach(var center in app.Windows.OfType<ControlCenterWindow>().ToArray())center.Close();if(pet is not null)CloseModelPet(pet);}catch{}
            try{Directory.Delete(temp,true);}catch{}
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,finishedAt=DateTimeOffset.Now,checks=Results,metrics},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown(success?0:1);
        }
    }
}
