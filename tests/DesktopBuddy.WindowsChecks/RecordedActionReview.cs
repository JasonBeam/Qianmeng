using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using DesktopBuddy;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private readonly record struct RecordedSurfaceFace(int Mesh,int VertexA,int VertexB,int VertexC);
    private readonly record struct RecordedContinuityFrame(
        int FrameIndex,double VideoSeconds,double ActionSeconds,
        float? ReachWeight,float? FingertipContactWeight,float? EffectiveContactFraction,float? CorrectionGate,
        bool ContactSampleAvailable,int IndexSampleCount,int MiddleSampleCount,
        bool SurfaceContactActive,Vector3? WristWorld,Vector3? ElbowWorld,Vector3? HeadSurfaceNormal,RecordedSurfaceFace? SurfaceFace);

    private sealed class RecordedReachPassTraceCapture
    {
        public RecordedReachPassTraceCapture(double requestedActionSeconds)=>RequestedActionSeconds=requestedActionSeconds;
        public double RequestedActionSeconds { get; }
        public long SelectedFrame { get; set; }=-1;
        public double SelectedActionSeconds { get; set; }=double.NaN;
        public List<int> PassIndices { get; }=[];
        public List<object> Passes { get; }=[];
    }
    private static void ApplyHeadSurfaceSolverOverrides(RigSampler sampler,string[] args,Func<string,double,double> optional)
    {
        var enableOneRing=args.Contains("--use-head-surface-one-ring-normal");var disableOneRing=args.Contains("--disable-head-surface-one-ring-normal");
        if(enableOneRing&&disableOneRing)throw new ArgumentException("Choose only one of --use-head-surface-one-ring-normal and --disable-head-surface-one-ring-normal.");
        var topologyRadiusIndex=Array.IndexOf(args,"--head-surface-topology-radius");var disableTopology=args.Contains("--disable-head-surface-topology");
        if(topologyRadiusIndex>=0&&disableTopology)throw new ArgumentException("Choose only one of --head-surface-topology-radius and --disable-head-surface-topology.");
        var normalRateIndex=Array.IndexOf(args,"--head-surface-normal-rate-limit-radians-per-second");var disableNormalRate=args.Contains("--disable-head-surface-normal-rate-limit");
        if(normalRateIndex>=0&&disableNormalRate)throw new ArgumentException("Choose only one of --head-surface-normal-rate-limit-radians-per-second and --disable-head-surface-normal-rate-limit.");
        var enableInterpolated=args.Contains("--use-interpolated-head-surface-normal");var disableInterpolated=args.Contains("--disable-interpolated-head-surface-normal");
        if(enableInterpolated&&disableInterpolated)throw new ArgumentException("Choose only one of --use-interpolated-head-surface-normal and --disable-interpolated-head-surface-normal.");

        if(enableOneRing)sampler.UseHeadSurfaceOneRingNormalForSolver=true;
        else if(disableOneRing)sampler.UseHeadSurfaceOneRingNormalForSolver=false;
        if(topologyRadiusIndex>=0)
        {
            sampler.UseHeadSurfaceTopologicalContinuity=true;
            sampler.HeadSurfaceTopologicalSearchRadius=(int)Math.Clamp(optional("--head-surface-topology-radius",sampler.HeadSurfaceTopologicalSearchRadius),1,3);
        }
        else if(disableTopology)sampler.UseHeadSurfaceTopologicalContinuity=false;
        if(normalRateIndex>=0)sampler.HeadSurfaceNormalAngularRateLimitRadiansPerSecond=(float)Math.Max(0,optional("--head-surface-normal-rate-limit-radians-per-second",sampler.HeadSurfaceNormalAngularRateLimitRadiansPerSecond));
        else if(disableNormalRate)sampler.HeadSurfaceNormalAngularRateLimitRadiansPerSecond=float.PositiveInfinity;
        if(enableInterpolated)sampler.UseInterpolatedHeadSurfaceNormalForSolver=true;
        else if(disableInterpolated)sampler.UseInterpolatedHeadSurfaceNormalForSolver=false;
    }

    private static object CaptureHeadSurfaceSolverParameters(RigSampler sampler)=>new
    {
        useHeadSurfaceOneRingNormalForSolver=sampler.UseHeadSurfaceOneRingNormalForSolver,
        useHeadSurfaceTopologicalContinuity=sampler.UseHeadSurfaceTopologicalContinuity,
        headSurfaceTopologicalSearchRadius=sampler.HeadSurfaceTopologicalSearchRadius,
        headSurfaceNormalAngularRateLimitRadiansPerSecond=sampler.HeadSurfaceNormalAngularRateLimitRadiansPerSecond,
        headSurfaceNormalAngularRateLimitUnlimited=float.IsPositiveInfinity(sampler.HeadSurfaceNormalAngularRateLimitRadiansPerSecond),
        useInterpolatedHeadSurfaceNormalForSolver=sampler.UseInterpolatedHeadSurfaceNormalForSolver
    };

    private static async Task RunRecordedActionReviewAsync(Application app,string[] args)
    {
        if(args.Contains("--deterministic-fixed-step"))
        {
            await RunDeterministicRecordedActionReviewAsync(app,args);
            return;
        }
        MainWindow? pet=null;Process? recorder=null;var clips=new List<object>();var success=false;
        Native.GetCursorPos(out var cursor);
        try
        {
            string Arg(string key)=>args[Array.IndexOf(args,key)+1];
            foreach(var key in new[]{"--model-file","--motion-profile-dir","--ffmpeg"})
                if(Array.IndexOf(args,key)<0)throw new ArgumentException(key+" is required.");
            var profile=Path.GetFullPath(Arg("--motion-profile-dir"));
            if(Directory.Exists(profile)||File.Exists(profile))throw new ArgumentException("Refuse existing profile.");
            var ffmpeg=Path.GetFullPath(Arg("--ffmpeg"));
            if(!File.Exists(ffmpeg))throw new FileNotFoundException("Local recorder not found.",ffmpeg);
            var modelPath=Path.GetFullPath(Arg("--model-file"));
            if(!File.Exists(modelPath))throw new FileNotFoundException("Read-only review model not found.",modelPath);
            var modelSourceHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(modelPath)));
            pet=new MainWindow(new LocalStore(profile),_=>{},new ChatService(()=>null))
                {Title="Qianmeng bounded motion recording",Topmost=true,Left=40,Top=40};
            pet.Settings.AutoHideFullscreen=false;pet.Settings.ContextDetectionEnabled=false;
            pet.Settings.AutomaticActivityAnimation=false;app.MainWindow=pet;pet.Show();
            ((System.Windows.Controls.Grid)pet.FindName("RootGrid")).Background=new SolidColorBrush(Color.FromRgb(32,38,50));
            await Task.Delay(250);for(var i=0;i<150&&pet.IsModelLoading;i++)await Task.Delay(100);
            await pet.ImportPetModelAsync(modelPath);
            var player=GetField<RigScenePlayer>(pet,"_animationPlayer");
            var idleClip=RigClipResolver.FindIdle(player.Clips);
            if(idleClip<0)throw new InvalidOperationException("The model has no playable idle clip.");
            var idleName=player.Clips[idleClip].Name;
            await InputAsync(()=>Native.SetCursorPos(20,20));pet.PreviewAnimation(idleName);await Task.Delay(300);
            // Import resizes/recenters the transparent pet. Place it only after that completes.
            pet.Left=SystemParameters.WorkArea.Left+80;pet.Top=SystemParameters.WorkArea.Top+80;
            await Task.Delay(200);
            var sampler=GetField<RigSampler>(player,"_sampler");var rig=GetField<RigAnimation>(sampler,"_rig");
            float[]? ContactPoint(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)?new[]{p.X,p.Y,p.Z}:null;
            var joints=rig.Nodes.Select((n,i)=>(n.Name,i)).Where(n=>n.Name is "mixamorig:Head" or "mixamorig:LeftArm" or "mixamorig:RightShoulder" or "mixamorig:RightArm" or "mixamorig:RightForeArm" or "mixamorig:LeftHand" or "mixamorig:RightHand" or
                "mixamorig:RightHandIndex1" or "mixamorig:RightHandIndex2" or "mixamorig:RightHandIndex3" or "mixamorig:RightHandMiddle1" or "mixamorig:RightHandMiddle2" or "mixamorig:RightHandMiddle3").ToArray();
            var ids=Array.IndexOf(args,"--live-actions")>=0?Arg("--live-actions").Split(','):new[]{"hugging","shivering","stretching"};
            var capturePadGeometry=args.Contains("--pad-geometry-diagnostic");
            var captureReachPassTrace=args.Contains("--reach-pass-trace");
            double OptionalSeconds(string key,double fallback)
            {
                var index=Array.IndexOf(args,key);
                return index>=0&&index+1<args.Length?double.Parse(args[index+1],CultureInfo.InvariantCulture):fallback;
            }
            var reachPassTraceAt=OptionalSeconds("--reach-pass-trace-at",2.051885);
            var palmRollLimitRadians=(float)Math.Clamp(OptionalSeconds("--palm-roll-limit-radians",sampler.PalmRollLimitRadians),0,Math.PI);
            var palmRollRateRadiansPerSecond=(float)Math.Max(0,OptionalSeconds("--palm-roll-rate-radians-per-second",sampler.PalmRollRateRadiansPerSecond));
            sampler.PalmRollLimitRadians=palmRollLimitRadians;sampler.PalmRollRateRadiansPerSecond=palmRollRateRadiansPerSecond;
            sampler.PalmRollForearmShare=(float)Math.Clamp(OptionalSeconds("--palm-roll-forearm-share",sampler.PalmRollForearmShare),0,1);
            sampler.PalmRollWristLimitRadians=(float)Math.Clamp(OptionalSeconds("--palm-roll-wrist-limit-radians",sampler.PalmRollWristLimitRadians),0,Math.PI);
            ApplyHeadSurfaceSolverOverrides(sampler,args,OptionalSeconds);
            sampler.TraceFingertipPalmOrientation=args.Contains("--trace-fingertip-palm-orientation");
            var reachPassTraceTolerance=OptionalSeconds("--reach-pass-trace-tolerance",.025);
            var padGeometryAt=OptionalSeconds("--pad-geometry-at",reachPassTraceAt);
            var padGeometryTolerance=OptionalSeconds("--pad-geometry-tolerance",reachPassTraceTolerance);
            var reachPassTrace=new List<object>();var reachPassTraceIndices=new List<int>();
            long reachPassTraceFrame=-1;var reachPassTraceActionSeconds=double.NaN;
            if(captureReachPassTrace)
            {
                sampler.ShouldCaptureRightReachPassDiagnostic=()=>(
                    Math.Abs(GetField<double>(player,"_companionActionElapsed")-reachPassTraceAt)<=reachPassTraceTolerance)&&
                    (reachPassTraceFrame<0||player.FrameCount+1==reachPassTraceFrame);
                sampler.RightReachPassDiagnosticObserver=pass=>
                {
                    var actionSeconds=GetField<double>(player,"_companionActionElapsed");
                    var frame=player.FrameCount+1;
                    if(reachPassTraceFrame<0){reachPassTraceFrame=frame;reachPassTraceActionSeconds=actionSeconds;}
                    if(frame!=reachPassTraceFrame)return;
                    var passIndex=Convert.ToInt32(pass.GetType().GetProperty("correctionPass")?.GetValue(pass)??-1);
                    reachPassTraceIndices.Add(passIndex);
                    reachPassTrace.Add(new{frame,actionSeconds,passIndex,details=pass});
                };
            }
            foreach(var id in ids)
            {
                var definition=CompanionActionMotion.Library.First(d=>d.Id==id);
                var nativeDuration=player.AuthoredActionDuration(definition.Kind);
                var duration=nativeDuration>0?nativeDuration:definition.DurationSeconds;
                pet.PlayCompanionAction(CompanionActionKind.Idle);await Task.Delay(300);
                var video=Path.ChangeExtension(_report,null)+"."+id+".mp4";
                if(File.Exists(video))throw new IOException("Refuse existing video: "+video);
                var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
                NativeMethods.GetWindowRect(new WindowInteropHelper(pet).Handle,out var rect);
                foreach(var a in new[]{"-hide_banner","-nostdin","-n","-f","gdigrab","-framerate","30","-draw_mouse","0",
                    "-offset_x",rect.Left.ToString(),"-offset_y",rect.Top.ToString(),"-video_size",$"{rect.Right-rect.Left}x{rect.Bottom-rect.Top}","-i","desktop",
                    "-t",(duration+1.8).ToString(CultureInfo.InvariantCulture),"-an","-vf","pad=ceil(iw/2)*2:ceil(ih/2)*2",
                    "-c:v","libx264","-preset","ultrafast","-crf","18","-pix_fmt","yuv420p",video})start.ArgumentList.Add(a);
                recorder=Process.Start(start)!;var stderr=recorder.StandardError.ReadToEndAsync();var recordingClock=Stopwatch.StartNew();
                await Task.Delay(700);var actionStartedAt=recordingClock.Elapsed.TotalSeconds;
                pet.PlayCompanionAction(definition.Kind);var clock=Stopwatch.StartNew();var first=player.FrameCount;
                var samples=new List<object>();var finite=true;object? contactGeometryDiagnostic=null;
                while(clock.Elapsed.TotalSeconds<duration+.3)
                {
                    var world=GetField<Matrix4x4[]>(sampler,"_world");
                    var actionSeconds=GetField<double>(player,"_companionActionElapsed");
                    if(capturePadGeometry&&id=="head-scratching"&&contactGeometryDiagnostic is null&&Math.Abs(actionSeconds-padGeometryAt)<=padGeometryTolerance)
                        contactGeometryDiagnostic=CaptureRightIndexPadGeometry(sampler,rig,actionSeconds);
                    samples.Add(new{seconds=clock.Elapsed.TotalSeconds,recordingSeconds=recordingClock.Elapsed.TotalSeconds,
                        frame=player.FrameCount,actionSeconds,
                        fingertipContactErrorMeters=float.IsFinite(sampler.RightFingertipContactErrorMeters)?sampler.RightFingertipContactErrorMeters:(float?)null,
                        fingertipContactTargetWorld=ContactPoint(sampler.RightFingertipContactTargetWorld),
                        fingertipSurfaceCenterWorld=ContactPoint(sampler.RightFingertipSurfaceCenterWorld),
                        indexFingertipSurfaceCenterWorld=ContactPoint(sampler.RightIndexFingertipSurfaceCenterWorld),
                        middleFingertipSurfaceCenterWorld=ContactPoint(sampler.RightMiddleFingertipSurfaceCenterWorld),
                        fingertipCorrectionPass=sampler.RightFingertipCorrectionPass,
                        fingertipCorrectionGate=float.IsFinite(sampler.RightFingertipCorrectionGate)?sampler.RightFingertipCorrectionGate:(float?)null,
                        fingertipIndexFraction=float.IsFinite(sampler.RightFingertipIndexFraction)?sampler.RightFingertipIndexFraction:(float?)null,
                        rightFingertipContactSampleAvailable=sampler.RightFingertipContactSampleAvailable,
                        rightIndexFingertipSurfaceSampleCount=sampler.RightIndexFingertipSurfaceSampleCount,
                        rightMiddleFingertipSurfaceSampleCount=sampler.RightMiddleFingertipSurfaceSampleCount,
                        headSurfaceContactActive=sampler.RightHeadSurfaceContactActive,
                        headSurfacePointWorld=ContactPoint(sampler.RightHeadSurfacePointWorld),
                        headSurfaceNormalWorld=ContactPoint(sampler.RightHeadSurfaceNormalWorld),
                        headSurfaceSeedDistanceMeters=float.IsFinite(sampler.RightHeadSurfaceSeedDistanceMeters)?sampler.RightHeadSurfaceSeedDistanceMeters:(float?)null,
                        headSurfaceSignedSelectedFingertipClearanceMeters=float.IsFinite(sampler.RightHeadSurfaceSignedClearanceMeters)?sampler.RightHeadSurfaceSignedClearanceMeters:(float?)null,
                        headSurfacePalmAlignmentDot=float.IsFinite(sampler.RightHeadSurfacePalmAlignmentDot)?sampler.RightHeadSurfacePalmAlignmentDot:(float?)null,
                        headSurfacePatchTriangleCount=sampler.HeadSurfacePatchTriangleCount,
                        headSurfaceTopologySearchRestricted=sampler.RightHeadSurfaceTopologySearchRestricted,
                        headSurfaceTopologySearchSucceeded=sampler.RightHeadSurfaceTopologySearchSucceeded,
                        headSurfaceTopologyCandidateCount=sampler.RightHeadSurfaceTopologyCandidateCount,
                        headSurfaceContactTriangle=new[]{sampler.RightHeadSurfaceContactMesh,sampler.RightHeadSurfaceContactTriangleA,sampler.RightHeadSurfaceContactTriangleB,sampler.RightHeadSurfaceContactTriangleC},
                        fingertipPreAimAlignmentDot=float.IsFinite(sampler.RightFingertipPreAimAlignmentDot)?sampler.RightFingertipPreAimAlignmentDot:(float?)null,
                        fingertipPalmRollPrincipalRadians=float.IsFinite(sampler.RightFingertipPalmRollPrincipalRadians)?sampler.RightFingertipPalmRollPrincipalRadians:(float?)null,
                        fingertipPalmRollAngleRadians=float.IsFinite(sampler.RightFingertipPalmRollAngleRadians)?sampler.RightFingertipPalmRollAngleRadians:(float?)null,
                        fingertipAppliedPalmRollRadians=float.IsFinite(sampler.RightFingertipAppliedPalmRollRadians)?sampler.RightFingertipAppliedPalmRollRadians:(float?)null,
                        fingertipDesiredPalmWorld=ContactPoint(sampler.RightFingertipDesiredPalmWorld),
                        fingertipPalmNormalPreWristRollWorld=ContactPoint(sampler.RightFingertipPalmNormalPreWristRollWorld),
                        fingertipPalmNormalPostWristRollWorld=ContactPoint(sampler.RightFingertipPalmNormalPostWristRollWorld),
                        fingertipPalmNormalDot=float.IsFinite(sampler.RightFingertipPalmNormalDot)?sampler.RightFingertipPalmNormalDot:(float?)null,
                        fingertipReachWeight=float.IsFinite(sampler.RightFingertipReachWeight)?sampler.RightFingertipReachWeight:(float?)null,
                        fingertipWristTargetWorld=ContactPoint(sampler.RightFingertipWristTargetWorld),
                        fingertipTargetBiasWorld=ContactPoint(sampler.RightFingertipTargetBiasWorld),
                        fingertipPass0ErrorMeters=float.IsFinite(sampler.RightFingertipPass0ErrorMeters)?sampler.RightFingertipPass0ErrorMeters:(float?)null,
                        fingertipPass1ErrorMeters=float.IsFinite(sampler.RightFingertipPass1ErrorMeters)?sampler.RightFingertipPass1ErrorMeters:(float?)null,
                        fingertipPass2ErrorMeters=float.IsFinite(sampler.RightFingertipPass2ErrorMeters)?sampler.RightFingertipPass2ErrorMeters:(float?)null,
                        fingertipPass3ErrorMeters=float.IsFinite(sampler.RightFingertipPass3ErrorMeters)?sampler.RightFingertipPass3ErrorMeters:(float?)null,
                        fingertipPass4ErrorMeters=float.IsFinite(sampler.RightFingertipPass4ErrorMeters)?sampler.RightFingertipPass4ErrorMeters:(float?)null,
                        joints=joints.Select(j=>new{name=j.Name,position=new[]{world[j.i].M41,world[j.i].M42,world[j.i].M43}}).ToArray()});
                    finite&=joints.All(j=>float.IsFinite(world[j.i].M41)&&float.IsFinite(world[j.i].M42)&&float.IsFinite(world[j.i].M43));
                    await Task.Delay(20);
                }
                await recorder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                File.WriteAllText(video+".ffmpeg.log",await stderr);
                Check("recorded-"+id,recorder.ExitCode==0&&File.Exists(video)&&new FileInfo(video).Length>1000,"Window recorder failed.");
                var decode=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var a in new[]{"-hide_banner","-loglevel","error","-ss","1.2","-i",video,"-frames:v","1","-f","rawvideo","-pix_fmt","gray","pipe:1"})decode.ArgumentList.Add(a);
                using var decoder=Process.Start(decode)!;var decodeErrors=decoder.StandardError.ReadToEndAsync();
                using var pixels=new MemoryStream();await decoder.StandardOutput.BaseStream.CopyToAsync(pixels);await decoder.WaitForExitAsync();
                var bytes=pixels.ToArray();var range=bytes.Length>0?bytes.Max()-bytes.Min():0;
                Check("recorded-nonblank-pixels-"+id,decoder.ExitCode==0&&range>32,"Video was blank or undecodable: "+await decodeErrors);
                Check("recorded-action-finite-"+id,finite,"Non-finite joint.");
                Check("recorded-action-return-"+id,player.CurrentCompanionAction==CompanionActionKind.None&&player.Clips[player.ClipIndex].Name==idleName,"Action did not return to the selected idle clip.");
                Check("contact-pad-geometry-captured-"+id,!capturePadGeometry||id!="head-scratching"||contactGeometryDiagnostic is not null,"Requested single-pose pad geometry was not captured.");
                var reachPassTraceComplete=reachPassTraceIndices.Count>0&&reachPassTraceIndices.Count<=5&&reachPassTraceIndices.SequenceEqual(Enumerable.Range(0,reachPassTraceIndices.Count));
                Check("right-reach-pass-trace-captured-"+id,!captureReachPassTrace||id!="head-scratching"||reachPassTraceComplete,
                    "The fixed frame did not produce a contiguous trace from pass 0: "+string.Join(',',reachPassTraceIndices));
                clips.Add(new{id,video,duration,samplingMode="live-as-sampled",actionStartedAt,pixelRange=range,renderedFrames=player.FrameCount-first,palmRollLimitRadians=sampler.PalmRollLimitRadians,palmRollRateRadiansPerSecond=sampler.PalmRollRateRadiansPerSecond,palmRollForearmShare=sampler.PalmRollForearmShare,palmRollWristLimitRadians=sampler.PalmRollWristLimitRadians,
                    headSurfaceNormalSolver=sampler.UseHeadSurfaceOneRingNormalForSolver?"oriented-area-weighted-same-mesh-edge-one-ring":sampler.UseInterpolatedHeadSurfaceNormalForSolver?"oriented-interpolated-skinned-vertex-normal":"oriented-triangle-face-normal",
                    headSurfaceTopologicalContinuity=sampler.UseHeadSurfaceTopologicalContinuity,headSurfaceTopologicalSearchRadius=sampler.HeadSurfaceTopologicalSearchRadius,
                    actualSolverParameters=CaptureHeadSurfaceSolverParameters(sampler),modelSourceHash,traceFingertipPalmOrientation=sampler.TraceFingertipPalmOrientation,
                    authoredActionDuration=player.AuthoredActionDuration(definition.Kind),nativeActionClipIndex=RigClipResolver.FindAction(player.Clips,definition.Kind),
                    actionRoute=RigClipResolver.FindAction(player.Clips,definition.Kind)>=0?"authored-clip":"procedural-fallback",samples,contactGeometryDiagnostic,
                    padGeometryRequestedActionSeconds=capturePadGeometry&&id=="head-scratching"?padGeometryAt:(double?)null,
                    reachPassTrace=captureReachPassTrace&&id=="head-scratching"?new{requestedActionSeconds=reachPassTraceAt,toleranceSeconds=reachPassTraceTolerance,
                        selectedFrame=reachPassTraceFrame,selectedActionSeconds=double.IsFinite(reachPassTraceActionSeconds)?reachPassTraceActionSeconds:(double?)null,
                        passIndices=reachPassTraceIndices,passes=reachPassTrace} : null});
                File.WriteAllText(Path.ChangeExtension(_report,".progress.json"),JsonSerializer.Serialize(new{complete=false,clips},new JsonSerializerOptions{WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals}));
                recorder.Dispose();recorder=null;
            }
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            if(recorder is not null){try{if(!recorder.HasExited)recorder.Kill();}catch{}recorder.Dispose();}
            if(pet is not null)CloseModelPet(pet);Native.SetCursorPos(cursor.X,cursor.Y);
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,
                mode="real-shown-WPF-opaque-test-background-desktop-crop-gdigrab-30fps-with-recording-overhead-not-installed-EXE",checks=Results,clips},new JsonSerializerOptions{WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals}));
            app.Shutdown(success?0:1);
        }
    }

    // Deterministic test-host capture: sample the same idle-clip phase and action time at exact
    // 1/30s steps, render each shown WPF pose, then encode those frames as a normal-speed video.
    // This isolates animation parameters from timer jitter, startup delays, and the prior idle phase.
    private static async Task RunDeterministicRecordedActionReviewAsync(Application app,string[] args)
    {
        MainWindow? pet=null;Process? encoder=null;var clips=new List<object>();var success=false;
        Native.GetCursorPos(out var cursor);
        try
        {
            var exportRecordedMotionPoses=args.Contains("--export-recorded-motion-poses");
            var poseTimesIndex=Array.IndexOf(args,"--recorded-motion-pose-times");
            if(!exportRecordedMotionPoses&&poseTimesIndex>=0)throw new ArgumentException("--recorded-motion-pose-times requires --export-recorded-motion-poses.");
            var requestedPoseTimes=poseTimesIndex>=0&&poseTimesIndex+1<args.Length
                ?args[poseTimesIndex+1].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(s=>double.Parse(s,CultureInfo.InvariantCulture)).ToArray()
                :Array.Empty<double>();
            if(poseTimesIndex>=0&&(requestedPoseTimes.Length==0||requestedPoseTimes.Any(t=>!double.IsFinite(t)||t<0)))
                throw new ArgumentException("Selected pose times must contain one or more finite, non-negative action seconds.");
            var poseExportPath=exportRecordedMotionPoses?Path.ChangeExtension(_report,".recorded-motion-poses.json"):null;
            if(poseExportPath is not null&&File.Exists(poseExportPath))throw new IOException("Refuse existing recorded pose export: "+poseExportPath);
            var recordedPoseFrames=new List<object>();
            string Arg(string key)=>args[Array.IndexOf(args,key)+1];
            foreach(var key in new[]{"--model-file","--motion-profile-dir","--ffmpeg"})
                if(Array.IndexOf(args,key)<0)throw new ArgumentException(key+" is required.");
            var profile=Path.GetFullPath(Arg("--motion-profile-dir"));
            if(Directory.Exists(profile)||File.Exists(profile))throw new ArgumentException("Refuse existing profile.");
            var ffmpeg=Path.GetFullPath(Arg("--ffmpeg"));
            if(!File.Exists(ffmpeg))throw new FileNotFoundException("Local recorder not found.",ffmpeg);
            var baseIdleSeconds=Optional("--base-idle-seconds",.65);
            var traceAt=Optional("--reach-pass-trace-at",2.05);
            var traceTolerance=Optional("--reach-pass-trace-tolerance",.018);
            var traceTimesListIndex=Array.IndexOf(args,"--reach-pass-trace-at-list");
            var multiTraceTimes=traceTimesListIndex>=0;
            if(multiTraceTimes&&!args.Contains("--reach-pass-trace"))throw new ArgumentException("--reach-pass-trace-at-list requires --reach-pass-trace.");
            if(multiTraceTimes&&traceTimesListIndex+1>=args.Length)throw new ArgumentException("--reach-pass-trace-at-list requires comma-separated action times.");
            var reachPassTraceTimes=multiTraceTimes
                ?args[traceTimesListIndex+1].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(value=>double.Parse(value,CultureInfo.InvariantCulture)).ToArray()
                :new[]{traceAt};
            if(reachPassTraceTimes.Length==0||reachPassTraceTimes.Any(value=>!double.IsFinite(value)||value<0))
                throw new ArgumentException("--reach-pass-trace-at-list must contain finite non-negative action times.");
            if(reachPassTraceTimes.Distinct().Count()!=reachPassTraceTimes.Length)
                throw new ArgumentException("--reach-pass-trace-at-list cannot contain duplicate times.");
            if(reachPassTraceTimes.Select(value=>(int)Math.Round(value*30,MidpointRounding.AwayFromZero)).Distinct().Count()!=reachPassTraceTimes.Length)
                throw new ArgumentException("--reach-pass-trace-at-list values must map to distinct 30 fps samples.");
            var padListIndex=Array.IndexOf(args,"--pad-geometry-at-list");
            var padAtValues=padListIndex>=0&&padListIndex+1<args.Length
                ?args[padListIndex+1].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(s=>double.Parse(s,CultureInfo.InvariantCulture)).Distinct().ToArray()
                :new[]{Optional("--pad-geometry-at",traceAt)};
            var padTolerance=Optional("--pad-geometry-tolerance",traceTolerance);
            var testScratchIndexCurlEnabled=Array.IndexOf(args,"--test-scratch-index-curl")>=0;
            var testScratchIndexCurl=(float)Math.Clamp(Optional("--test-scratch-index-curl",.65),0,1);
            var testScratchForwardOffset=(float)Math.Clamp(Optional("--test-scratch-forward-offset",0),-.20,.20);
            var testScratchClearance=Array.IndexOf(args,"--test-scratch-clearance")>=0
                ?(float)Math.Clamp(Optional("--test-scratch-clearance",.002),0,.05):float.NaN;
            var testScratchSurfaceForwardOffset=(float)Math.Clamp(Optional("--test-scratch-surface-forward-offset",0),-.10,.10);
            var palmRollLimit=(float)Math.Clamp(Optional("--palm-roll-limit-radians",1.15),0,Math.PI);
            var palmRollRate=(float)Math.Max(0,Optional("--palm-roll-rate-radians-per-second",2.8));
            var forearmShare=(float)Math.Clamp(Optional("--palm-roll-forearm-share",0),0,1);
            var wristLimit=(float)Math.Clamp(Optional("--palm-roll-wrist-limit-radians",1.15),0,Math.PI);
            var testFingertipPalmRollOffsetValue=Optional("--test-fingertip-palm-roll-offset-radians",0);
            if(!double.IsFinite(testFingertipPalmRollOffsetValue))throw new ArgumentException("--test-fingertip-palm-roll-offset-radians must be finite.");
            var testFingertipPalmRollOffsetRadians=(float)Math.Clamp(testFingertipPalmRollOffsetValue,-.15,.15);
            double Optional(string key,double fallback)
            {
                var index=Array.IndexOf(args,key);
                return index>=0&&index+1<args.Length?double.Parse(args[index+1],CultureInfo.InvariantCulture):fallback;
            }

            pet=new MainWindow(new LocalStore(profile),_=>{},new ChatService(()=>null))
                {Title="Qianmeng deterministic motion review",Topmost=true,Left=40,Top=40};
            pet.Settings.AutoHideFullscreen=false;pet.Settings.ContextDetectionEnabled=false;
            pet.Settings.AutomaticActivityAnimation=false;app.MainWindow=pet;pet.Show();
            ((System.Windows.Controls.Grid)pet.FindName("RootGrid")).Background=new SolidColorBrush(Color.FromRgb(32,38,50));
            await Task.Delay(250);for(var i=0;i<150&&pet.IsModelLoading;i++)await Task.Delay(100);
            await pet.ImportPetModelAsync(Path.GetFullPath(Arg("--model-file")));
            var testPetSize=Optional("--test-pet-size",1);
            var testModelScale=Optional("--test-model-scale",1);
            var testModelYaw=Optional("--test-model-yaw",0);
            if(Array.IndexOf(args,"--test-pet-size")>=0)pet.SetPetSize(testPetSize);
            if(Array.IndexOf(args,"--test-model-scale")>=0||Array.IndexOf(args,"--test-model-yaw")>=0)
                pet.SaveModelPose(testModelYaw,testModelScale);
            if(exportRecordedMotionPoses&&(Math.Abs(pet.Settings.PetSize-.6)>1e-6||Math.Abs(pet.Settings.PetModelScale-.7)>1e-6))
                throw new ArgumentException("Recorded full-pose export requires test display settings PetSize=0.6 and ModelScale=0.7.");
            var player=GetField<RigScenePlayer>(pet,"_animationPlayer");
            var idleClip=RigClipResolver.FindIdle(player.Clips);
            if(idleClip<0)throw new InvalidOperationException("The model has no playable idle clip.");
            var idleName=player.Clips[idleClip].Name;
            await InputAsync(()=>Native.SetCursorPos(20,20));pet.PreviewAnimation(idleName);
            pet.Left=SystemParameters.WorkArea.Left+80;pet.Top=SystemParameters.WorkArea.Top+80;
            await Task.Delay(150);

            var sampler=GetField<RigSampler>(player,"_sampler");
            var rig=GetField<RigAnimation>(sampler,"_rig");
            static float[] MatrixValues(Matrix4x4 m)=>new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44};
            var sourceModelPath=Path.GetFullPath(Arg("--model-file"));
            object? recordedPoseNodes=null;
            if(exportRecordedMotionPoses)
            {
                var restWorld=new Matrix4x4[rig.Nodes.Length];
                foreach(var nodeIndex in rig.Order)
                    restWorld[nodeIndex]=rig.Nodes[nodeIndex].Rest.Matrix*(rig.Nodes[nodeIndex].Parent<0?Matrix4x4.Identity:restWorld[rig.Nodes[nodeIndex].Parent]);
                recordedPoseNodes=rig.Nodes.Select((node,index)=>new{name=node.Name,restWorld=MatrixValues(restWorld[index])}).ToArray();
            }
            var rightWristNode=Array.FindIndex(rig.Nodes,node=>node.Name=="mixamorig:RightHand");
            var rightElbowNode=Array.FindIndex(rig.Nodes,node=>node.Name=="mixamorig:RightForeArm");
            var playerFlags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            void SetPlayer<T>(string name,T value)=>player.GetType().GetField(name,playerFlags)!.SetValue(player,value);
            var rate=RigClipResolver.PlaybackRate(player.Clips[idleClip]);
            player.Configure(false,1,30);
            player.GetType().GetProperty("ClipIndex")!.SetValue(player,idleClip);
            SetPlayer("_fromClip",-1);SetPlayer("_fromSeconds",0d);SetPlayer("_blendSeconds",1d);
            SetPlayer("_poseTransitionStarted",double.NegativeInfinity);SetPlayer("_interactionClip",-1);
            SetPlayer("_interactionClipSeconds",0d);SetPlayer("_nativeActionClip",-1);
            SetPlayer("_nativeActionReturnClip",-1);SetPlayer("_companionAction",CompanionActionKind.None);
            SetPlayer("_companionActionElapsed",0d);SetPlayer("_ambientAction",false);
            sampler.CompanionActionOverride=false;sampler.CompanionInteractionAction=default;
            sampler.CompanionGesture=Vector3.Zero;sampler.CompanionWaveSwing=0;sampler.CompanionBodyMotion=Vector3.Zero;
            sampler.CompanionIdleBody=Vector3.Zero;sampler.CompanionIdleHead=Vector3.Zero;sampler.CompanionBreathing=0;
            sampler.PalmRollLimitRadians=palmRollLimit;sampler.PalmRollRateRadiansPerSecond=palmRollRate;
            sampler.PalmRollForearmShare=forearmShare;sampler.PalmRollWristLimitRadians=wristLimit;
            sampler.FingertipPalmRollDiagnosticOffsetRadians=testFingertipPalmRollOffsetRadians;
            ApplyHeadSurfaceSolverOverrides(sampler,args,Optional);
            sampler.UsePreviousHeadSurfaceSamplePositions=args.Contains("--previous-head-surface");
            sampler.TraceHeadSurfaceSmoothNormals=args.Contains("--trace-head-surface-smooth-normals");
            sampler.TraceHeadSurfaceOneRingNormal=args.Contains("--trace-head-surface-one-ring-normal");
            sampler.TraceFingertipPalmOrientation=args.Contains("--trace-fingertip-palm-orientation");

            var captureContactOverlay=args.Contains("--contact-overlay-diagnostic");
            var petModelView=captureContactOverlay?(DesktopBuddy.UI.PetModelView)pet.FindName("PetModel"):null;
            var contactViewport=petModelView?.Children.OfType<Viewport3D>().Single();
            var renderedModel=contactViewport?.Children.OfType<ModelVisual3D>().Last();
            var rigDisplayCenter=GetField<Vector3>(player,"_center");
            var rigDisplaySize=GetField<float>(player,"_size");
            Canvas? contactOverlay=null;
            if(petModelView is not null)
            {
                contactOverlay=new Canvas{IsHitTestVisible=false,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
                Panel.SetZIndex(contactOverlay,10);petModelView.Children.Add(contactOverlay);
            }
            object? UpdateContactOverlay()
            {
                if(contactOverlay is null||contactViewport is null||renderedModel is null||contactViewport.Camera is not OrthographicCamera camera||camera.Width<=0||!float.IsFinite(rigDisplaySize)||rigDisplaySize<=1e-8f)return null;
                contactOverlay.Children.Clear();
                var target=sampler.RightFingertipContactTargetWorld;
                var middle=sampler.RightMiddleFingertipSurfaceCenterWorld;
                var index=sampler.RightIndexFingertipSurfaceCenterWorld;
                var activeContactCenter=sampler.RightFingertipSurfaceCenterWorld;
                var surface=sampler.RightHeadSurfacePointWorld;
                var normal=sampler.RightHeadSurfaceNormalWorld;
                static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
                if(!sampler.RightFingertipContactSampleAvailable||!Finite(target)||!Finite(activeContactCenter)||!Finite(surface)||!Finite(normal))return null;
                var modelTransform=renderedModel.Transform??Transform3D.Identity;
                var modelLinearScale=modelTransform.Transform(new Vector3D(1,0,0)).Length;
                var modelScaleY=modelTransform.Transform(new Vector3D(0,1,0)).Length;
                var modelScaleZ=modelTransform.Transform(new Vector3D(0,0,1)).Length;
                if(!double.IsFinite(modelLinearScale)||modelLinearScale<=1e-8||!double.IsFinite(modelScaleY)||!double.IsFinite(modelScaleZ))return null;
                var renderedGroup=renderedModel.Content as Model3DGroup;
                var headInfluenceMethod=sampler.GetType().GetMethod("HeadInfluence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var patchField=sampler.GetType().GetField("_headSurfacePatch",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var patchArray=patchField?.GetValue(sampler) as Array;
                var activePatchKeys=new HashSet<string>(StringComparer.Ordinal);
                string TriangleKey(int mesh,int a,int b,int c)
                {
                    var vertices=new[]{a,b,c};Array.Sort(vertices);
                    return $"{mesh}:{vertices[0]},{vertices[1]},{vertices[2]}";
                }
                int ReadTriangleMember(object? value,string name)
                {
                    if(value is null)return -1;
                    var type=value.GetType();var member=type.GetProperty(name)?.GetValue(value)??type.GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public)?.GetValue(value);
                    return member is null?-1:Convert.ToInt32(member,CultureInfo.InvariantCulture);
                }
                if(patchArray is not null)foreach(var entry in patchArray)
                {
                    var mesh=ReadTriangleMember(entry,"Mesh");var a=ReadTriangleMember(entry,"A");var b=ReadTriangleMember(entry,"B");var c=ReadTriangleMember(entry,"C");
                    if(mesh>=0&&a>=0&&b>=0&&c>=0)activePatchKeys.Add(TriangleKey(mesh,a,b,c));
                }
                int MeshIndex(RayMeshGeometry3DHitTestResult ray)
                {
                    if(renderedGroup is null)return -1;
                    for(var meshIndex=0;meshIndex<renderedGroup.Children.Count;meshIndex++)
                        if(renderedGroup.Children[meshIndex] is GeometryModel3D geometryModel&&
                            (ReferenceEquals(geometryModel.Geometry,ray.MeshHit)||ReferenceEquals(geometryModel,ray.ModelHit)))return meshIndex;
                    return -1;
                }
                double HeadInfluenceAt(int meshIndex,int vertex)
                {
                    if(meshIndex<0||headInfluenceMethod is null)return double.NaN;
                    return Convert.ToDouble(headInfluenceMethod.Invoke(sampler,new object[]{meshIndex,vertex})??double.NaN,CultureInfo.InvariantCulture);
                }
                object TriangleSkinData(RayMeshGeometry3DHitTestResult ray,Vector3 samplePoint)
                {
                    var meshIndex=MeshIndex(ray);
                    var vertices=new[]{ray.VertexIndex1,ray.VertexIndex2,ray.VertexIndex3};
                    var headWeights=vertices.Select(vertex=>HeadInfluenceAt(meshIndex,vertex)).ToArray();
                    var barycentric=new[]{ray.VertexWeight1,ray.VertexWeight2,ray.VertexWeight3};
                    var interpolatedHeadInfluence=headWeights.All(double.IsFinite)?headWeights[0]*barycentric[0]+headWeights[1]*barycentric[1]+headWeights[2]*barycentric[2]:double.NaN;
                    var positions=meshIndex>=0&&meshIndex<sampler.Positions.Length?sampler.Positions[meshIndex]:[];
                    var vertexWorldPositions=vertices.All(vertex=>vertex>=0&&vertex<positions.Length)?vertices.Select(vertex=>
                    {var point=positions[vertex];return new[]{point.X,point.Y,point.Z};}).ToArray():[];
                    var triangleCenter=vertexWorldPositions.Length==3?new[]{vertexWorldPositions.Average(v=>v[0]),vertexWorldPositions.Average(v=>v[1]),vertexWorldPositions.Average(v=>v[2])}:[];
                    var triangleCenterDistance=triangleCenter.Length==3?Vector3.Distance(samplePoint,new((float)triangleCenter[0],(float)triangleCenter[1],(float)triangleCenter[2])):(float?)null;
                    var activePatchContainsTriangle=activePatchKeys.Contains(TriangleKey(meshIndex,vertices[0],vertices[1],vertices[2]));
                    return new{meshIndex,headWeights,barycentric,interpolatedHeadInfluence,activePatchContainsTriangle,vertexWorldPositions,triangleCenter,triangleCenterDistance,
                        modelHitType=ray.ModelHit?.GetType().Name,meshHitIsRenderedGeometry=meshIndex>=0};
                }
                Point3D ToView(Vector3 p)
                {
                    // RigScenePlayer normalizes each sampled vertex by the exact cached center/size before placing it in WPF geometry.
                    var normalized=(p-rigDisplayCenter)/rigDisplaySize;
                    return modelTransform.Transform(new Point3D(normalized.X,normalized.Y,normalized.Z));
                }
                var direction=camera.LookDirection;direction.Normalize();
                var up=camera.UpDirection-direction*Vector3D.DotProduct(camera.UpDirection,direction);up.Normalize();
                var right=Vector3D.CrossProduct(direction,up);right.Normalize();up=Vector3D.CrossProduct(right,direction);up.Normalize();
                var pixelsPerUnit=contactViewport.ActualWidth/camera.Width;
                Point Project(Vector3 p)
                {
                    var viewPoint=ToView(p);var relative=viewPoint-camera.Position;
                    return new Point(contactViewport.ActualWidth*.5+Vector3D.DotProduct(relative,right)*pixelsPerUnit,
                        contactViewport.ActualHeight*.5-Vector3D.DotProduct(relative,up)*pixelsPerUnit);
                }
                object HitAt(Vector3 p,Point screen)
                {
                    var viewPoint=ToView(p);var expected=Vector3D.DotProduct(viewPoint-camera.Position,direction);
                    var inverseModelTransform=modelTransform.Inverse;
                    if(inverseModelTransform is null)return new{hit=false,reason="model transform is not invertible"};
                    // PointHit is in the hit Visual3D's coordinate system. Transform it
                    // before comparing camera-axis depth; DistanceToRayOrigin begins
                    // on WPF's near plane and includes a separate distance adjustment.
                    // Transform the camera axis back through the inverse model transform
                    // so this remains valid if the display transform is not uniform.
                    var inverseOrigin=inverseModelTransform.Transform(new Point3D(0,0,0));
                    var inverseDirectionTip=inverseModelTransform.Transform(new Point3D(direction.X,direction.Y,direction.Z));
                    var sourceUnitsPerViewUnit=rigDisplaySize*(inverseDirectionTip-inverseOrigin).Length;
                    var intersections=new List<(RayMeshGeometry3DHitTestResult Hit,Point3D ViewPoint,double Depth)>();
                    var rawHitCount=0;var hitStackTruncated=false;
                    VisualTreeHelper.HitTest(contactViewport,null,result=>
                    {
                        if(result is RayMeshGeometry3DHitTestResult ray&&ReferenceEquals(ray.VisualHit,renderedModel))
                        {
                            rawHitCount++;
                            var pointView=modelTransform.Transform(ray.PointHit);
                            var depth=Vector3D.DotProduct(pointView-camera.Position,direction);
                            if(!intersections.Any(existing=>Math.Abs(existing.Depth-depth)<1e-5))
                            {
                                if(intersections.Count<64)intersections.Add((ray,pointView,depth));
                                else hitStackTruncated=true;
                            }
                        }
                        return hitStackTruncated?HitTestResultBehavior.Stop:HitTestResultBehavior.Continue;
                    },new PointHitTestParameters(screen));
                    intersections.Sort((a,b)=>a.Depth.CompareTo(b.Depth));
                    var hit=intersections.Count==0?null:intersections[0].Hit;
                    var hitPointView=intersections.Count==0?default:intersections[0].ViewPoint;
                    var hitPointDepth=intersections.Count==0?(double?)null:intersections[0].Depth;
                    var hitPointProjected=intersections.Count==0?default:ProjectViewPoint(hitPointView);
                    var nearestToExpected=intersections.OrderBy(x=>Math.Abs(x.Depth-expected)).FirstOrDefault();
                    var hasIntersection=intersections.Count>0;
                    return new{hit=hit is not null,expectedDepthViewUnits=expected,
                        hitVisualMatchesRenderedModel=hit is not null&&ReferenceEquals(hit.VisualHit,renderedModel),
                        firstHitRayDistanceFromNearPlaneViewUnits=hit is null?(double?)null:hit.DistanceToRayOrigin,
                        firstHitPointDepthViewUnits=hitPointDepth,
                        firstHitPointMinusExpectedDepthViewUnits=hit is null?(double?)null:hitPointDepth-expected,
                        firstHitPointMinusExpectedSourceUnits=hit is null?(double?)null:(hitPointDepth-expected)*sourceUnitsPerViewUnit,
                        firstHitReprojectionErrorPixels=hit is null?(double?)null:Math.Sqrt(Math.Pow(hitPointProjected.X-screen.X,2)+Math.Pow(hitPointProjected.Y-screen.Y,2)),
                        nearestIntersectionToExpectedDepthSourceUnits=hasIntersection?(nearestToExpected.Depth-expected)*sourceUnitsPerViewUnit:(double?)null,
                        rawHitCount,uniqueDepthLayerCount=intersections.Count,hitStackTruncated,
                        modelLinearScaleX=modelLinearScale,modelLinearScaleY=modelScaleY,modelLinearScaleZ=modelScaleZ,
                        sourceUnitsPerViewUnitAlongCamera=sourceUnitsPerViewUnit,
                        depthSignMeaning="negative means a closer visible surface is in front of the projected sample",
                        hitVertices=hit is null?null:new[]{hit.VertexIndex1,hit.VertexIndex2,hit.VertexIndex3},
                        intersections=intersections.Select((entry,index)=>new{rank=index,depthViewUnits=entry.Depth,
                            depthMinusExpectedSourceUnits=(entry.Depth-expected)*sourceUnitsPerViewUnit,
                            vertices=new[]{entry.Hit.VertexIndex1,entry.Hit.VertexIndex2,entry.Hit.VertexIndex3},
                            triangleSkin=TriangleSkinData(entry.Hit,p),
                            visualMatchesRenderedModel=ReferenceEquals(entry.Hit.VisualHit,renderedModel)}).ToArray()};
                }
                Point ProjectViewPoint(Point3D viewPoint)
                {
                    var relative=viewPoint-camera.Position;
                    var pixelsPerUnit=contactViewport.ActualWidth/camera.Width;
                    return new Point(contactViewport.ActualWidth*.5+Vector3D.DotProduct(relative,right)*pixelsPerUnit,
                        contactViewport.ActualHeight*.5-Vector3D.DotProduct(relative,up)*pixelsPerUnit);
                }
                void Marker(string label,Vector3 position,Color color)
                {
                    var point=Project(position);
                    if(!double.IsFinite(point.X)||!double.IsFinite(point.Y))return;
                    var circle=new System.Windows.Shapes.Ellipse{Width=6,Height=6,Stroke=new SolidColorBrush(color),Fill=new SolidColorBrush(Color.FromArgb(180,color.R,color.G,color.B)),StrokeThickness=1.2};
                    Canvas.SetLeft(circle,point.X-3);Canvas.SetTop(circle,point.Y-3);contactOverlay.Children.Add(circle);
                    var text=new TextBlock{Text=label,FontSize=8,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(color),Background=new SolidColorBrush(Color.FromArgb(190,8,10,14))};
                    Canvas.SetLeft(text,point.X+3);Canvas.SetTop(text,point.Y-9);contactOverlay.Children.Add(text);
                }
                var targetScreen=Project(target);var activeContactScreen=Project(activeContactCenter);var surfaceScreen=Project(surface);
                var targetToContact=new System.Windows.Shapes.Line{X1=targetScreen.X,Y1=targetScreen.Y,X2=activeContactScreen.X,Y2=activeContactScreen.Y,Stroke=new SolidColorBrush(Color.FromRgb(255,230,0)),StrokeThickness=1};contactOverlay.Children.Add(targetToContact);
                Marker("T",target,Color.FromRgb(255,230,0));Marker("C",activeContactCenter,Color.FromRgb(0,240,255));Marker("H",surface,Color.FromRgb(255,75,60));
                if(sampler.RightMiddleFingertipSurfaceSampleCount>0&&Finite(middle))Marker("M",middle,Color.FromRgb(120,220,255));
                if(sampler.RightIndexFingertipSurfaceSampleCount>0&&Finite(index))Marker("I",index,Color.FromRgb(100,255,100));
                var normalEnd=surface+Vector3.Normalize(normal)*.035f;var normalScreen=Project(normalEnd);
                contactOverlay.Children.Add(new System.Windows.Shapes.Line{X1=surfaceScreen.X,Y1=surfaceScreen.Y,X2=normalScreen.X,Y2=normalScreen.Y,Stroke=new SolidColorBrush(Color.FromRgb(255,80,220)),StrokeThickness=1.2});
                return new{targetScreen=new[]{targetScreen.X,targetScreen.Y},activeContactCenterScreen=new[]{activeContactScreen.X,activeContactScreen.Y},
                    middleTipScreen=Finite(middle)?new[]{Project(middle).X,Project(middle).Y}:null,
                    indexTipScreen=sampler.RightIndexFingertipSurfaceSampleCount>0&&Finite(index)?new[]{Project(index).X,Project(index).Y}:null,surfaceScreen=new[]{surfaceScreen.X,surfaceScreen.Y},
                    contactSampleAvailable=sampler.RightFingertipContactSampleAvailable,
                    indexSampleCount=sampler.RightIndexFingertipSurfaceSampleCount,middleSampleCount=sampler.RightMiddleFingertipSurfaceSampleCount,
                    rigDisplayNormalizationSize=rigDisplaySize,modelLinearDisplayScale=modelLinearScale,
                    activeHeadSurfacePatchTriangleCount=activePatchKeys.Count,headSurfacePatchMembershipAvailable=patchArray is not null,
                    targetHit=HitAt(target,targetScreen),activeContactHit=HitAt(activeContactCenter,activeContactScreen),
                    middleTipHit=Finite(middle)?HitAt(middle,Project(middle)):null,surfaceHit=HitAt(surface,surfaceScreen)};
            }

            var actionIds=Array.IndexOf(args,"--live-actions")>=0?Arg("--live-actions").Split(','):new[]{"head-scratching"};
            var capturePad=args.Contains("--pad-geometry-diagnostic");
            var captureTrace=args.Contains("--reach-pass-trace");
            var captureHeadLocalPadTrajectory=args.Contains("--head-local-active-pad-trajectory");
            var radialHandTargetBlendDiagnostic=args.Contains("--radial-hand-target-blend-diagnostic");
            foreach(var id in actionIds)
            {
                sampler.UseRadialHandTargetBlendForDiagnostic=radialHandTargetBlendDiagnostic&&id=="head-scratching";
                var definition=CompanionActionMotion.Library.First(d=>d.Id==id);
                var nativeDuration=player.AuthoredActionDuration(definition.Kind);
                var duration=nativeDuration>0?nativeDuration:definition.DurationSeconds;
                var fps=30;var leadFrames=21;var tailSeconds=1.1;
                var actionFrames=(int)Math.Ceiling((duration+tailSeconds)*fps);
                var frameCount=leadFrames+actionFrames;
                var selectedPoseFrames=new Dictionary<int,double>();
                if(exportRecordedMotionPoses&&poseTimesIndex>=0)
                foreach(var requestedSeconds in requestedPoseTimes)
                {
                    if(requestedSeconds>duration+0.5/fps)throw new ArgumentOutOfRangeException("--recorded-motion-pose-times","A requested action time falls outside the selected action duration: "+requestedSeconds.ToString("F6",CultureInfo.InvariantCulture));
                    var actionFrame=(int)Math.Round(requestedSeconds*fps,MidpointRounding.AwayFromZero);
                    var sampledSeconds=actionFrame/(double)fps;
                    if(sampledSeconds>duration+1e-9)throw new ArgumentOutOfRangeException("--recorded-motion-pose-times","A requested time maps to a frame after the selected action duration: "+requestedSeconds.ToString("F6",CultureInfo.InvariantCulture));
                    if(Math.Abs(requestedSeconds-sampledSeconds)>=0.5/fps-1e-9)throw new ArgumentException("A requested pose time is exactly between deterministic 30 fps frames: "+requestedSeconds.ToString("F6",CultureInfo.InvariantCulture));
                    if(!selectedPoseFrames.TryAdd(actionFrame,requestedSeconds))throw new ArgumentException("Two requested pose times map to the same deterministic 30 fps frame.");
                }
                var video=Path.ChangeExtension(_report,null)+"."+id+".mp4";
                if(File.Exists(video))throw new IOException("Refuse existing video: "+video);
                var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true};
                foreach(var a in new[]{"-hide_banner","-loglevel","error","-n","-f","image2pipe","-vcodec","png","-framerate",fps.ToString(CultureInfo.InvariantCulture),"-i","pipe:0",
                    "-frames:v",frameCount.ToString(CultureInfo.InvariantCulture),"-an","-vf","pad=ceil(iw/2)*2:ceil(ih/2)*2","-c:v","libx264","-preset","ultrafast","-crf","18","-pix_fmt","yuv420p",video})start.ArgumentList.Add(a);
                encoder=Process.Start(start)!;var stderr=encoder.StandardError.ReadToEndAsync();
                var samples=new List<object>(frameCount);
                var continuityFrames=new List<RecordedContinuityFrame>(frameCount);
                var poseFramesBeforeAction=recordedPoseFrames.Count;
                var multiAngleOverlayResults=new List<object>();object? traceContactOverlay=null;
                var passTraceCaptures=captureTrace?reachPassTraceTimes.Select(time=>new RecordedReachPassTraceCapture(time)).ToArray():Array.Empty<RecordedReachPassTraceCapture>();var padGeometry=new List<(double Requested,double Actual,object Diagnostic)>();var sampledPadTimes=new HashSet<double>();
                if(captureTrace)
                {
                    sampler.ShouldCaptureRightReachPassDiagnostic=()=>
                    {
                        var actionSeconds=GetField<double>(player,"_companionActionElapsed");var frame=player.FrameCount+1;
                        return passTraceCaptures.Any(capture=>Math.Abs(actionSeconds-capture.RequestedActionSeconds)<=traceTolerance&&
                            (capture.SelectedFrame<0||frame==capture.SelectedFrame));
                    };
                    sampler.RightReachPassDiagnosticObserver=pass=>
                    {
                        var selectedAction=GetField<double>(player,"_companionActionElapsed");var frame=player.FrameCount+1;
                        var capture=passTraceCaptures.FirstOrDefault(item=>Math.Abs(selectedAction-item.RequestedActionSeconds)<=traceTolerance&&
                            (item.SelectedFrame<0||frame==item.SelectedFrame));
                        if(capture is null)return;
                        if(capture.SelectedFrame<0){capture.SelectedFrame=frame;capture.SelectedActionSeconds=selectedAction;}
                        if(frame!=capture.SelectedFrame)return;
                        capture.PassIndices.Add(Convert.ToInt32(pass.GetType().GetProperty("correctionPass")?.GetValue(pass)??-1));
                        capture.Passes.Add(new{frame,actionSeconds=selectedAction,details=pass});
                    };
                }
                static Vector3? WorldPosition(Matrix4x4[] matrices,int node)
                {
                    if(node<0||node>=matrices.Length)return null;
                    var matrix=matrices[node];
                    return float.IsFinite(matrix.M41)&&float.IsFinite(matrix.M42)&&float.IsFinite(matrix.M43)
                        ?new Vector3(matrix.M41,matrix.M42,matrix.M43):(Vector3?)null;
                }
                for(var frameIndex=0;frameIndex<frameCount;frameIndex++)
                {
                    var videoSeconds=frameIndex/(double)fps;var actionSeconds=videoSeconds-leadFrames/(double)fps;
                    var active=actionSeconds>=0&&actionSeconds<duration;
                    var clampedActionSeconds=Math.Clamp(actionSeconds,0,duration);
                    SetPlayer("_seconds",baseIdleSeconds+videoSeconds*rate);
                    SetPlayer("_companionAction",active?definition.Kind:CompanionActionKind.None);
                    SetPlayer("_companionActionElapsed",clampedActionSeconds);
                    var sampledAction=active?CompanionActionMotion.Sample(definition.Kind,clampedActionSeconds):default;
                    if(active&&id=="head-scratching"&&Math.Abs(testScratchForwardOffset)>1e-6f)
                    {
                        var reach=sampledAction.RightReach;
                        sampledAction=sampledAction with {RightReach=reach with {Hand=reach.Hand+new Vector3(0,0,testScratchForwardOffset*reach.Weight)}};
                    }
                    if(active&&id=="head-scratching"&&float.IsFinite(testScratchClearance))
                    {
                        var reach=sampledAction.RightReach;
                        sampledAction=sampledAction with {RightReach=reach with {HeadSurfaceClearance=testScratchClearance*reach.Weight}};
                    }
                    if(active&&id=="head-scratching"&&Math.Abs(testScratchSurfaceForwardOffset)>1e-6f)
                    {
                        var reach=sampledAction.RightReach;
                        sampledAction=sampledAction with {RightReach=reach with {HeadSurfaceTangentOffset=reach.HeadSurfaceTangentOffset+new Vector2(0,testScratchSurfaceForwardOffset*reach.Weight)}};
                    }
                    if(active&&id=="head-scratching"&&Array.IndexOf(args,"--test-scratch-contact-index-fraction")>=0)
                    {
                        var contactFraction=(float)Math.Clamp(Optional("--test-scratch-contact-index-fraction",0),0,1);
                        sampledAction=sampledAction with {RightReach=sampledAction.RightReach with {FingertipIndexFraction=contactFraction}};
                    }
                    if(active&&id=="head-scratching"&&testScratchIndexCurlEnabled)
                    {
                        var reach=sampledAction.RightReach;
                        var curlContactWeight=Math.Clamp(reach.FingertipContactWeight/Math.Max(reach.Weight,1e-6f),0,1);
                        var indexCurl=reach.Curl.X+(testScratchIndexCurl-reach.Curl.X)*curlContactWeight;
                        sampledAction=sampledAction with {RightReach=reach with {Curl=new Vector4(indexCurl,reach.Curl.Y,reach.Curl.Z,reach.Curl.W)}};
                    }
                    sampler.CompanionAction=sampledAction;
                    Invoke(player,"Render",true);
                    var contactOverlaySample=captureContactOverlay?UpdateContactOverlay():null;

                    if(capturePad&&id=="head-scratching")
                    foreach(var requestedAt in padAtValues.Where(t=>!sampledPadTimes.Contains(t)&&Math.Abs(actionSeconds-t)<=padTolerance))
                    {
                        padGeometry.Add((requestedAt,actionSeconds,CaptureRightIndexPadGeometry(sampler,rig,actionSeconds)));
                        sampledPadTimes.Add(requestedAt);
                    }
                    if(Math.Abs(actionSeconds-traceAt)<=1d/fps/2)
                    {
                        traceContactOverlay=contactOverlaySample;
                        var keyframe=Path.ChangeExtension(_report,$".{id}.t{actionSeconds:F3}.png");
                        if(!File.Exists(keyframe))SaveWindowImage(pet,keyframe);
                        if(args.Contains("--multi-angle-keyframes"))
                        {
                            var modelView=(DesktopBuddy.UI.PetModelView)pet.FindName("PetModel");
                            var originalYaw=GetField<double>(modelView,"_yaw");
                            var scale=GetField<ScaleTransform3D>(modelView,"_baseScale").ScaleX;
                            try
                            {
                                foreach(var angle in new[]{45d,90d,135d,180d,225d,270d,315d})
                                {
                                    modelView.SetPose(originalYaw+angle,scale);
                                    var overlay=UpdateContactOverlay();
                                    if(overlay is not null)multiAngleOverlayResults.Add(new{yaw=originalYaw+angle,overlay});
                                    SaveWindowImage(pet,Path.ChangeExtension(_report,$".{id}.t{actionSeconds:F3}.yaw{angle:000}.png"));
                                }
                            }
                            finally{modelView.SetPose(originalYaw,scale);UpdateContactOverlay();}
                        }
                    }
                    var world=GetField<Matrix4x4[]>(sampler,"_world");
                    var reachWeight=sampledAction.RightReach.Weight;
                    var contactWeight=sampledAction.RightReach.FingertipContactWeight;
                    var effectiveContactFraction=float.IsFinite(reachWeight)&&reachWeight>1e-6f&&float.IsFinite(contactWeight)
                        ?Math.Clamp(contactWeight/reachWeight,0,1):(float?)null;
                    var headNormal=sampler.RightHeadSurfaceNormalWorld;
                    Vector3? validHeadNormal=float.IsFinite(headNormal.X)&&float.IsFinite(headNormal.Y)&&float.IsFinite(headNormal.Z)&&headNormal.LengthSquared()>1e-12f
                        ?headNormal:(Vector3?)null;
                    RecordedSurfaceFace? surfaceFace=null;
                    var faceMesh=sampler.RightHeadSurfaceContactMesh;var faceA=sampler.RightHeadSurfaceContactTriangleA;var faceB=sampler.RightHeadSurfaceContactTriangleB;var faceC=sampler.RightHeadSurfaceContactTriangleC;
                    if(faceMesh>=0&&faceA>=0&&faceB>=0&&faceC>=0)
                    {
                        var vertices=new[]{faceA,faceB,faceC};Array.Sort(vertices);
                        surfaceFace=new RecordedSurfaceFace(faceMesh,vertices[0],vertices[1],vertices[2]);
                    }
                    continuityFrames.Add(new RecordedContinuityFrame(frameIndex,videoSeconds,clampedActionSeconds,
                        float.IsFinite(reachWeight)?reachWeight:null,float.IsFinite(contactWeight)?contactWeight:null,effectiveContactFraction,
                        FiniteOrNull(sampler.RightFingertipCorrectionGate),sampler.RightFingertipContactSampleAvailable,
                        sampler.RightIndexFingertipSurfaceSampleCount,sampler.RightMiddleFingertipSurfaceSampleCount,sampler.RightHeadSurfaceContactActive,
                        WorldPosition(world,rightWristNode),WorldPosition(world,rightElbowNode),validHeadNormal,surfaceFace));
                    var selectedPoseTimeExists=selectedPoseFrames.TryGetValue(frameIndex-leadFrames,out var requestedSeconds);
                    if(exportRecordedMotionPoses&&(poseTimesIndex<0||selectedPoseTimeExists))
                    {
                        var matrices=world.Select(MatrixValues).ToArray();
                        if(matrices.Length!=rig.Nodes.Length||matrices.SelectMany(matrix=>matrix).Any(value=>!float.IsFinite(value)))
                            throw new InvalidOperationException("A selected full-pose frame contains a non-finite world matrix.");
                        recordedPoseFrames.Add(new{action=id,frame=frameIndex,seconds=videoSeconds,videoSeconds,actionSeconds=clampedActionSeconds,actionActive=active,requestedActionSeconds=selectedPoseTimeExists?requestedSeconds:(double?)null,world=matrices});
                    }
                    var headLocalActivePad=captureHeadLocalPadTrajectory&&id=="head-scratching"
                        ?CaptureHeadLocalActivePadKinematics(sampler,rig,world):null;
                    var jointSet=rig.Nodes.Select((n,i)=>(n.Name,i)).Where(n=>n.Name is "mixamorig:Head" or "mixamorig:LeftArm" or "mixamorig:RightShoulder" or "mixamorig:RightArm" or "mixamorig:RightForeArm" or "mixamorig:LeftHand" or "mixamorig:RightHand" or
                        "mixamorig:RightHandIndex1" or "mixamorig:RightHandIndex2" or "mixamorig:RightHandIndex3" or "mixamorig:RightHandMiddle1" or "mixamorig:RightHandMiddle2" or "mixamorig:RightHandMiddle3").ToArray();
                    var finite=jointSet.All(j=>float.IsFinite(world[j.i].M41)&&float.IsFinite(world[j.i].M42)&&float.IsFinite(world[j.i].M43));
                    byte[] png=CaptureWindowPng(pet);await encoder.StandardInput.BaseStream.WriteAsync(png);
                    samples.Add(new{frame=player.FrameCount,videoSeconds,actionSeconds=clampedActionSeconds,actionActive=active,sourceClipSeconds=baseIdleSeconds+videoSeconds*rate,finite,
                        testFingertipPalmRollOffsetRadians,
                        requestedRightReachWeight=sampledAction.RightReach.Weight,
                        requestedRightFingertipContactWeight=sampledAction.RightReach.FingertipContactWeight,
                        requestedRightFingertipIndexFraction=sampledAction.RightReach.FingertipIndexFraction,
                        requestedRightHeadSurfaceTangentOffsetMillimeters=new[]{sampledAction.RightReach.HeadSurfaceTangentOffset.X*1000,sampledAction.RightReach.HeadSurfaceTangentOffset.Y*1000},
                        requestedRightReachCurl=new[]{sampledAction.RightReach.Curl.X,sampledAction.RightReach.Curl.Y,sampledAction.RightReach.Curl.Z,sampledAction.RightReach.Curl.W},
                        contactOverlay=contactOverlaySample,
                        solverPoseFingertipContactErrorMeters=FiniteOrNull(sampler.RightFingertipContactErrorMeters),fingertipContactTargetWorld=ContactPoint(sampler.RightFingertipContactTargetWorld),
                        fingertipSurfaceCenterWorld=ContactPoint(sampler.RightFingertipSurfaceCenterWorld),indexFingertipSurfaceCenterWorld=ContactPoint(sampler.RightIndexFingertipSurfaceCenterWorld),middleFingertipSurfaceCenterWorld=ContactPoint(sampler.RightMiddleFingertipSurfaceCenterWorld),
                        headSurfaceContactActive=sampler.RightHeadSurfaceContactActive,headSurfacePointWorld=ContactPoint(sampler.RightHeadSurfacePointWorld),
                        headSurfaceContactTargetWorld=ContactPoint(sampler.RightHeadSurfaceContactTargetWorld),
                        headSurfaceNormalWorld=ContactPoint(sampler.RightHeadSurfaceNormalWorld),headSurfaceRawNormalWorld=ContactPoint(sampler.RightHeadSurfaceRawNormalWorld),
                        headSurfaceContactFaceNormalWorld=ContactPoint(sampler.RightHeadSurfaceContactFaceNormalWorld),
                        headSurfaceInterpolatedSkinnedVertexNormalWorld=ContactPoint(sampler.RightHeadSurfaceInterpolatedNormalWorld),
                        headSurfaceOneRingFaceNormalWorld=ContactPoint(sampler.RightHeadSurfaceOneRingNormalWorld),
                        headSurfaceOneRingTriangleCount=sampler.RightHeadSurfaceOneRingTriangleCount,
                        headSurfaceFaceOneRingNormalDot=FiniteOrNull(sampler.RightHeadSurfaceFaceOneRingNormalDot),
                        headSurfaceReachUpProjectionLength=FiniteOrNull(sampler.RightHeadSurfaceReachUpProjectionLength),
                        headSurfaceFingerUpProjectionLength=FiniteOrNull(sampler.RightHeadSurfaceFingerUpProjectionLength),
                        headSurfaceTopologySearchRestricted=sampler.RightHeadSurfaceTopologySearchRestricted,
                        headSurfaceTopologySearchSucceeded=sampler.RightHeadSurfaceTopologySearchSucceeded,
                        headSurfaceTopologyCandidateCount=sampler.RightHeadSurfaceTopologyCandidateCount,
                        headSurfaceContactBarycentric=ContactPoint(sampler.RightHeadSurfaceContactBarycentric),
                        headSurfaceInterpolatedOutwardDot=FiniteOrNull(sampler.RightHeadSurfaceInterpolatedOutwardDot),
                        headSurfaceFaceInterpolatedNormalDot=FiniteOrNull(sampler.RightHeadSurfaceFaceInterpolatedNormalDot),
                        headSurfacePalmNormalWorld=ContactPoint(sampler.RightHeadSurfacePalmNormalWorld),headSurfaceRawOutwardDot=FiniteOrNull(sampler.RightHeadSurfaceRawOutwardDot),
                        headSurfaceContactTriangle=new[]{sampler.RightHeadSurfaceContactMesh,sampler.RightHeadSurfaceContactTriangleA,sampler.RightHeadSurfaceContactTriangleB,sampler.RightHeadSurfaceContactTriangleC},
                        headSurfaceSeedDistanceMeters=FiniteOrNull(sampler.RightHeadSurfaceSeedDistanceMeters),
                        solverPoseHeadSurfaceSignedSelectedFingertipClearanceMeters=FiniteOrNull(sampler.RightHeadSurfaceSignedClearanceMeters),
                        solverPoseHeadSurfaceFaceSignedClearanceMeters=FiniteOrNull(sampler.RightHeadSurfaceFaceSignedClearanceMeters),
                        headSurfacePalmAlignmentDot=FiniteOrNull(sampler.RightHeadSurfacePalmAlignmentDot),fingertipCorrectionPass=sampler.RightFingertipCorrectionPass,
                        fingertipCorrectionGate=FiniteOrNull(sampler.RightFingertipCorrectionGate),
                        headSurfaceFacePalmAlignmentDot=FiniteOrNull(sampler.RightHeadSurfaceFacePalmAlignmentDot),
                        rightFingertipContactSampleAvailable=sampler.RightFingertipContactSampleAvailable,
                        rightIndexFingertipSurfaceSampleCount=sampler.RightIndexFingertipSurfaceSampleCount,
                        rightMiddleFingertipSurfaceSampleCount=sampler.RightMiddleFingertipSurfaceSampleCount,
                        fingertipIndexFraction=FiniteOrNull(sampler.RightFingertipIndexFraction),fingertipPalmRollAngleRadians=FiniteOrNull(sampler.RightFingertipPalmRollAngleRadians),
                        fingertipPalmRollPrincipalRadians=FiniteOrNull(sampler.RightFingertipPalmRollPrincipalRadians),
                        fingertipPalmRollDiagnosticOffsetRadians=sampler.RightFingertipPalmRollDiagnosticOffsetRadians,
                        fingertipPalmDesiredWorld=ContactPoint(sampler.RightFingertipDesiredPalmWorld),
                        fingertipPalmNormalPreWristRollWorld=ContactPoint(sampler.RightFingertipPalmNormalPreWristRollWorld),
                        fingertipPalmNormalPostWristRollWorld=ContactPoint(sampler.RightFingertipPalmNormalPostWristRollWorld),
                        fingertipAppliedPalmRollRadians=FiniteOrNull(sampler.RightFingertipAppliedPalmRollRadians),fingertipPalmForearmAppliedRadians=FiniteOrNull(sampler.RightFingertipPalmForearmAppliedRadians),
                        fingertipPalmWristAppliedRadians=FiniteOrNull(sampler.RightFingertipPalmWristAppliedRadians),fingertipPreRollPalmNormalDot=FiniteOrNull(sampler.RightFingertipPalmNormalDot),
                        fingertipPass0ErrorMeters=FiniteOrNull(sampler.RightFingertipPass0ErrorMeters),fingertipPass1ErrorMeters=FiniteOrNull(sampler.RightFingertipPass1ErrorMeters),
                        fingertipPass2ErrorMeters=FiniteOrNull(sampler.RightFingertipPass2ErrorMeters),fingertipPass3ErrorMeters=FiniteOrNull(sampler.RightFingertipPass3ErrorMeters),
                        fingertipPass4ErrorMeters=FiniteOrNull(sampler.RightFingertipPass4ErrorMeters),headLocalActivePadKinematics=headLocalActivePad,
                        joints=jointSet.Select(j=>new{name=j.Name,position=new[]{world[j.i].M41,world[j.i].M42,world[j.i].M43}}).ToArray()});
                    if(!finite)throw new InvalidOperationException("Deterministic sample contained a non-finite joint.");
                }
                var expectedPoseFrameCount=poseTimesIndex>=0?selectedPoseFrames.Count:frameCount;
                if(exportRecordedMotionPoses&&recordedPoseFrames.Count-poseFramesBeforeAction!=expectedPoseFrameCount)
                    throw new InvalidOperationException($"Full-pose export selected {recordedPoseFrames.Count-poseFramesBeforeAction} frames for {id}; expected {expectedPoseFrameCount}.");
                await encoder.StandardInput.BaseStream.FlushAsync();encoder.StandardInput.Close();
                await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                File.WriteAllText(video+".ffmpeg.log",await stderr);
                Check("deterministic-video-"+id,encoder.ExitCode==0&&File.Exists(video)&&new FileInfo(video).Length>1000,"Image-sequence encoder failed.");
                var lastSeconds=(frameCount-1)/(double)fps-leadFrames/(double)fps;
                SetPlayer("_companionAction",CompanionActionKind.None);SetPlayer("_companionActionElapsed",duration);
                sampler.CompanionAction=default;SetPlayer("_seconds",baseIdleSeconds+(frameCount-1)/(double)fps*rate);Invoke(player,"Render",true);
                Check("deterministic-action-return-"+id,player.CurrentCompanionAction==CompanionActionKind.None&&player.Clips[player.ClipIndex].Name==idleName,"Action did not return to the selected idle clip.");
                if(captureTrace&&id=="head-scratching")
                {
                    var complete=passTraceCaptures.Length>0&&passTraceCaptures.All(capture=>capture.SelectedFrame>=0&&capture.PassIndices.Count>0&&
                        capture.PassIndices.Count<=5&&capture.PassIndices.SequenceEqual(Enumerable.Range(0,capture.PassIndices.Count)));
                    Check("deterministic-reach-pass-trace-"+id,complete,"Missing or non-contiguous pass trace at: "+
                        string.Join(", ",passTraceCaptures.Where(capture=>capture.SelectedFrame<0||capture.PassIndices.Count==0||
                            capture.PassIndices.Count>5||!capture.PassIndices.SequenceEqual(Enumerable.Range(0,capture.PassIndices.Count)))
                            .Select(capture=>capture.RequestedActionSeconds.ToString("F6",CultureInfo.InvariantCulture))));
                }
                if(capturePad&&id=="head-scratching")Check("deterministic-pad-geometry-"+id,padGeometry.Count>0&&padGeometry.Count==padAtValues.Length,"Pad geometry was not sampled at every requested action time.");
                object? reachPassTraceOutput=null;
                if(captureTrace&&id=="head-scratching")
                {
                    var captureRecords=passTraceCaptures.Select(capture=>new
                    {
                        requestedActionSeconds=capture.RequestedActionSeconds,toleranceSeconds=traceTolerance,
                        selectedFrame=capture.SelectedFrame,
                        selectedActionSeconds=double.IsFinite(capture.SelectedActionSeconds)?capture.SelectedActionSeconds:(double?)null,
                        passIndices=capture.PassIndices,passes=capture.Passes
                    }).ToArray();
                    var first=passTraceCaptures[0];
                    if(multiTraceTimes)
                        reachPassTraceOutput=new
                        {
                            requestedActionSeconds=first.RequestedActionSeconds,toleranceSeconds=traceTolerance,
                            selectedFrame=first.SelectedFrame,
                            selectedActionSeconds=double.IsFinite(first.SelectedActionSeconds)?first.SelectedActionSeconds:(double?)null,
                            passIndices=first.PassIndices,passes=first.Passes,captures=captureRecords
                        };
                    else reachPassTraceOutput=captureRecords[0];
                }
                var actualSolverParameters=CaptureHeadSurfaceSolverParameters(sampler);
                clips.Add(new{id,video,duration,frameRate=fps,frameCount,samplingMode="deterministic-exact-30fps",deterministicSampling=true,baseIdleSeconds,sourceClip=idleName,actionStartedAt=leadFrames/(double)fps,
                    palmRollLimitRadians=palmRollLimit,palmRollRateRadiansPerSecond=palmRollRate,palmRollForearmShare=forearmShare,palmRollWristLimitRadians=wristLimit,
                    testFingertipPalmRollOffsetRadians,
                    testScratchIndexCurl=testScratchIndexCurlEnabled?testScratchIndexCurl:(float?)null,
                    testScratchForwardOffset,
                    testScratchClearance=float.IsFinite(testScratchClearance)?testScratchClearance:(float?)null,
                    testScratchSurfaceForwardOffset,
                    testPetSize=pet.Settings.PetSize,testModelDisplayScale=pet.Settings.PetModelScale,
                    radialHandTargetBlendDiagnostic=sampler.UseRadialHandTargetBlendForDiagnostic,
                    contactOverlayDiagnostic=captureContactOverlay,traceContactOverlay,multiAngleOverlayResults,
                    headSurfaceGeometrySource=sampler.UsePreviousHeadSurfaceSamplePositions?"previous-rendered-skin":"current-sampled-pose",
                    headSurfaceNormalSolver=sampler.UseHeadSurfaceOneRingNormalForSolver?"oriented-area-weighted-same-mesh-edge-one-ring":sampler.UseInterpolatedHeadSurfaceNormalForSolver?"oriented-interpolated-skinned-vertex-normal":"oriented-triangle-face-normal",
                    headSurfaceTopologicalContinuity=sampler.UseHeadSurfaceTopologicalContinuity,
                    headSurfaceTopologicalSearchRadius=sampler.HeadSurfaceTopologicalSearchRadius,
                    headSurfaceNormalAngularRateLimitRadiansPerSecond=sampler.HeadSurfaceNormalAngularRateLimitRadiansPerSecond,
                    traceHeadSurfaceSmoothNormals=sampler.TraceHeadSurfaceSmoothNormals,
                    traceHeadSurfaceOneRingNormal=sampler.TraceHeadSurfaceOneRingNormal,
                    actualSolverParameters,continuitySummary=BuildRecordedContinuitySummary(continuityFrames),
                    sampledEndActionSeconds=lastSeconds,headLocalActivePadTrajectory=captureHeadLocalPadTrajectory,samples,padGeometryDiagnostic=padGeometry.Count>0?padGeometry[0].Diagnostic:null,
                    padGeometryDiagnostics=padGeometry.Select(p=>new{requestedActionSeconds=p.Requested,actualActionSeconds=p.Actual,diagnostic=p.Diagnostic}).ToArray(),
                    reachPassTrace=reachPassTraceOutput});
                File.WriteAllText(Path.ChangeExtension(_report,".progress.json"),JsonSerializer.Serialize(new{complete=false,clips},new JsonSerializerOptions{WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals}));
                encoder.Dispose();encoder=null;
            }
            if(exportRecordedMotionPoses)
            {
                var outputPath=poseExportPath??throw new InvalidOperationException("Recorded pose export path was not initialized.");
                var payload=new
                {
                    schemaVersion=1,
                    schema="desktopbuddy.rig-sampler-world-matrices",
                    source=sourceModelPath,
                    fps=30,
                    timebase="encoded-clip-video-seconds",
                    selection=poseTimesIndex>=0?new{mode="selected-action-seconds",requestedSeconds=requestedPoseTimes}:new{mode="all-deterministic-frames",requestedSeconds=Array.Empty<double>()},
                    display=new{petSize=pet.Settings.PetSize,modelScale=pet.Settings.PetModelScale},
                    coordinateUnits="RigSampler world matrices; translations use runtime model units (meters for this model).",
                    nodes=recordedPoseNodes??throw new InvalidOperationException("Recorded pose node rest matrices were not initialized."),
                    frames=recordedPoseFrames
                };
                var poseJsonOptions=new JsonSerializerOptions{WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
                await using var poseStream=new FileStream(outputPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                await JsonSerializer.SerializeAsync(poseStream,payload,poseJsonOptions);
            }
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            if(encoder is not null){try{if(!encoder.HasExited)encoder.Kill();}catch{}encoder.Dispose();}
            if(pet is not null)CloseModelPet(pet);Native.SetCursorPos(cursor.X,cursor.Y);
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,
                mode="deterministic-fixed-1-30s-WPF-rendered-image-sequence-test-host-not-installed-EXE",checks=Results,clips},new JsonSerializerOptions{WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals}));
            app.Shutdown(success?0:1);
        }

        static float? FiniteOrNull(float value)=>float.IsFinite(value)?value:null;
        static float[]? ContactPoint(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)?new[]{p.X,p.Y,p.Z}:null;
    }

    private static object BuildRecordedContinuitySummary(IReadOnlyList<RecordedContinuityFrame> frames)
    {
        var adjacentFrames=new List<object>(Math.Max(0,frames.Count-1));
        var faceSwitchCount=0;var faceComparableCount=0;var faceUnknownCount=0;
        for(var i=1;i<frames.Count;i++)
        {
            var previous=frames[i-1];var current=frames[i];
            var wristDelta=DeltaWorldMillimeters(previous.WristWorld,current.WristWorld);
            var elbowDelta=DeltaWorldMillimeters(previous.ElbowWorld,current.ElbowWorld);
            double? normalAngleDegrees=null;
            if(previous.HeadSurfaceNormal is Vector3 previousNormal&&current.HeadSurfaceNormal is Vector3 currentNormal)
            {
                previousNormal=Vector3.Normalize(previousNormal);currentNormal=Vector3.Normalize(currentNormal);
                normalAngleDegrees=Math.Acos(Math.Clamp(Vector3.Dot(previousNormal,currentNormal),-1f,1f))*180/Math.PI;
            }
            bool? faceChanged=null;
            if(previous.SurfaceFace is RecordedSurfaceFace previousFace&&current.SurfaceFace is RecordedSurfaceFace currentFace)
            {faceChanged=previousFace!=currentFace;faceComparableCount++;if(faceChanged.Value)faceSwitchCount++;}
            else faceUnknownCount++;
            adjacentFrames.Add(new
            {
                fromFrame=previous.FrameIndex,toFrame=current.FrameIndex,
                fromVideoSeconds=previous.VideoSeconds,toVideoSeconds=current.VideoSeconds,
                deltaVideoSeconds=current.VideoSeconds-previous.VideoSeconds,
                fromActionSeconds=previous.ActionSeconds,toActionSeconds=current.ActionSeconds,
                deltaActionSeconds=current.ActionSeconds-previous.ActionSeconds,
                rightWristDeltaWorldMillimeters=wristDelta,
                rightWristDisplacementMillimeters=DeltaMagnitude(wristDelta),
                rightElbowDeltaWorldMillimeters=elbowDelta,
                rightElbowDisplacementMillimeters=DeltaMagnitude(elbowDelta),
                selectedHeadSurfaceNormalAngleDegrees=normalAngleDegrees,
                headSurfaceFaceChanged=faceChanged,
                fromHeadSurfaceFace=FaceDescription(previous.SurfaceFace),toHeadSurfaceFace=FaceDescription(current.SurfaceFace),
                contactSampleAvailableFrom=previous.ContactSampleAvailable,contactSampleAvailableTo=current.ContactSampleAvailable,
                indexSampleCountFrom=previous.IndexSampleCount,indexSampleCountTo=current.IndexSampleCount,
                middleSampleCountFrom=previous.MiddleSampleCount,middleSampleCountTo=current.MiddleSampleCount,
                headSurfaceContactActiveFrom=previous.SurfaceContactActive,headSurfaceContactActiveTo=current.SurfaceContactActive
            });
        }

        var activeContactTransitions=BuildRecordedBooleanTransitions(frames,frame=>frame.SurfaceContactActive,"headSurfaceContactActive");
        var normalAvailabilityTransitions=BuildRecordedBooleanTransitions(frames,frame=>frame.HeadSurfaceNormal.HasValue,"headSurfaceNormalAvailable");
        return new
        {
            sampling="Derived from the already-rendered deterministic samples; no sampler resampling or state mutation.",
            units=new
            {
                time="seconds; videoSeconds advances at 30 fps and actionSeconds is clamped to [0, action duration] outside the active action.",
                jointPositions="RigSampler world coordinates in meters for this model; adjacent vector deltas are emitted in millimeters (meters multiplied by 1000).",
                normalAngle="degrees between normalized RightHeadSurfaceNormalWorld vectors.",
                faceIdentity="head surface mesh index plus the sorted three source vertex indices of the selected face."
            },
            definitions=new
            {
                requestedReachWeight="CompanionReach.Weight as sampled for this frame.",
                requestedFingertipContactWeight="CompanionReach.FingertipContactWeight as sampled for this frame; this includes the reach envelope.",
                effectiveContactFraction="clamp(FingertipContactWeight / ReachWeight, 0, 1); null if reach weight is missing or <= 1e-6.",
                observedCorrectionGate="RigSampler.RightFingertipCorrectionGate. The current solver multiplier is the quintic smoothstep of reach weight 0.80 to 1.00 multiplied by the normalized effective contact fraction. This is a solver gate value, not a naturalness or acceptance threshold; undefined sampler values remain null.",
                headSurfaceContactState="RigSampler.RightHeadSurfaceContactActive, reported with enter/exit transitions separately from the correction gate and normal availability.",
                wristsAndElbows="RightHand joint translation is the wrist proxy; RightForeArm joint translation is the elbow proxy."
            },
            weightRanges=new
            {
                requestedReachWeight=RecordedValueRange(frames.Select(frame=>frame.ReachWeight)),
                requestedFingertipContactWeight=RecordedValueRange(frames.Select(frame=>frame.FingertipContactWeight)),
                effectiveContactFraction=RecordedValueRange(frames.Select(frame=>frame.EffectiveContactFraction)),
                effectiveContactFractionWhileHeadSurfaceActive=RecordedValueRange(frames.Where(frame=>frame.SurfaceContactActive).Select(frame=>frame.EffectiveContactFraction)),
                observedCorrectionGate=RecordedValueRange(frames.Select(frame=>frame.CorrectionGate))
            },
            transitionSummary=new
            {
                headSurfaceContact=activeContactTransitions,
                headSurfaceNormalAvailability=normalAvailabilityTransitions,
                comparableFacePairs=faceComparableCount,faceSwitches=faceSwitchCount,facePairsWithMissingIdentity=faceUnknownCount
            },
            contactSampleSummary=new
            {
                availableFrameCount=frames.Count(frame=>frame.ContactSampleAvailable),unavailableFrameCount=frames.Count(frame=>!frame.ContactSampleAvailable),
                indexSampleCountRange=new{minimum=frames.Count>0?(int?)frames.Min(frame=>frame.IndexSampleCount):null,maximum=frames.Count>0?(int?)frames.Max(frame=>frame.IndexSampleCount):null},
                middleSampleCountRange=new{minimum=frames.Count>0?(int?)frames.Min(frame=>frame.MiddleSampleCount):null,maximum=frames.Count>0?(int?)frames.Max(frame=>frame.MiddleSampleCount):null}
            },
            adjacentFrames
        };
    }

    private static object BuildRecordedBooleanTransitions(IReadOnlyList<RecordedContinuityFrame> frames,Func<RecordedContinuityFrame,bool> stateSelector,string signal)
    {
        var events=new List<object>();var entries=0;var exits=0;
        if(frames.Count==0)return new{signal,entryCount=entries,exitCount=exits,initialValue=(bool?)null,finalValue=(bool?)null,events};
        var initial=stateSelector(frames[0]);
        if(initial)events.Add(new{transition="active-at-first-sample",frame=frames[0].FrameIndex,videoSeconds=frames[0].VideoSeconds,actionSeconds=frames[0].ActionSeconds});
        for(var i=1;i<frames.Count;i++)
        {
            var before=stateSelector(frames[i-1]);var after=stateSelector(frames[i]);
            if(before==after)continue;
            if(after)entries++;else exits++;
            events.Add(new{transition=after?"enter":"exit",frame=frames[i].FrameIndex,videoSeconds=frames[i].VideoSeconds,actionSeconds=frames[i].ActionSeconds});
        }
        return new{signal,entryCount=entries,exitCount=exits,initialValue=(bool?)initial,finalValue=(bool?)stateSelector(frames[^1]),events};
    }

    private static object RecordedValueRange(IEnumerable<float?> source)
    {
        var values=source.ToArray();var valid=values.Where(value=>value.HasValue&&float.IsFinite(value.Value)).Select(value=>(double)value!.Value).ToArray();
        double? minimum=valid.Length>0?valid.Min():null;double? maximum=valid.Length>0?valid.Max():null;
        return new{validSampleCount=valid.Length,missingOrNonFiniteSampleCount=values.Length-valid.Length,minimum,maximum};
    }

    private static float[]? DeltaWorldMillimeters(Vector3? from,Vector3? to)
    {
        if(from is not Vector3 start||to is not Vector3 end)return null;
        return new[]{(end.X-start.X)*1000,(end.Y-start.Y)*1000,(end.Z-start.Z)*1000};
    }

    private static double? DeltaMagnitude(float[]? delta)=>delta is {Length:3}
        ?Math.Sqrt((double)delta[0]*delta[0]+(double)delta[1]*delta[1]+(double)delta[2]*delta[2]):null;

    private static object? FaceDescription(RecordedSurfaceFace? face)=>face is RecordedSurfaceFace value
        ?new{mesh=value.Mesh,vertices=new[]{value.VertexA,value.VertexB,value.VertexC}}:null;

    private static byte[] CaptureWindowPng(Window window)
    {
        window.UpdateLayout();var content=(FrameworkElement)window.Content;var dpi=VisualTreeHelper.GetDpi(content);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth*dpi.DpiScaleX*_captureScale),(int)Math.Ceiling(content.ActualHeight*dpi.DpiScaleY*_captureScale),
            96*dpi.DpiScaleX*_captureScale,96*dpi.DpiScaleY*_captureScale,PixelFormats.Pbgra32);
        bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();
    }

    private static object CaptureHeadLocalActivePadKinematics(RigSampler sampler,RigAnimation rig,Matrix4x4[] world)
    {
        static float[] Point(Vector3 p)=>new[]{p.X,p.Y,p.Z};
        var head=Array.FindIndex(rig.Nodes,n=>string.Equals(n.Name,"mixamorig:Head",StringComparison.OrdinalIgnoreCase));
        if(head<0||head>=world.Length||!Matrix4x4.Invert(world[head],out var headWorldInverse))
            return new{available=false,reason="Head world transform is missing or non-invertible."};
        if(sampler.Positions is not {Length:>0} positions)
            return new{available=false,reason="Final skinned sampler positions are unavailable."};
        var cachedField=typeof(RigSampler).GetField("_fingertipSurfaceSamples",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        if(cachedField?.GetValue(sampler) is not Array bySide||bySide.Length<=1||bySide.GetValue(1) is not Array rightByFinger||rightByFinger.Length<2)
            return new{available=false,reason="Cached right Index/Middle contact samples are unavailable."};

        object CaptureFinger(int finger,out Vector3 center,out bool available)
        {
            center=default;available=false;
            if(rightByFinger.GetValue(finger) is not Array fingerSamples)
                return new{finger=finger==0?"Index":"Middle",sampleCount=0,centerWorld=(float[]?)null,centerHeadLocal=(float[]?)null,samples=Array.Empty<object>()};
            var sampleType=fingerSamples.GetType().GetElementType();
            var meshProperty=sampleType?.GetProperty("Mesh",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            var vertexProperty=sampleType?.GetProperty("Vertex",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            var influenceProperty=sampleType?.GetProperty("Influence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            if(meshProperty is null||vertexProperty is null||influenceProperty is null)
                return new{finger=finger==0?"Index":"Middle",sampleCount=0,centerWorld=(float[]?)null,centerHeadLocal=(float[]?)null,samples=Array.Empty<object>()};
            var rows=new List<object>();var sum=Vector3.Zero;var total=0f;
            foreach(var item in fingerSamples)
            {
                if(item is null)continue;
                var mesh=(int)meshProperty.GetValue(item)!;var vertex=(int)vertexProperty.GetValue(item)!;var influence=(float)influenceProperty.GetValue(item)!;
                if(mesh<0||mesh>=positions.Length||vertex<0||vertex>=positions[mesh].Length||!float.IsFinite(influence)||influence<=0)continue;
                var position=positions[mesh][vertex];var headLocal=Vector3.Transform(position,headWorldInverse);
                if(!float.IsFinite(position.X)||!float.IsFinite(position.Y)||!float.IsFinite(position.Z)||!float.IsFinite(headLocal.X)||!float.IsFinite(headLocal.Y)||!float.IsFinite(headLocal.Z))continue;
                sum+=position*influence;total+=influence;
                rows.Add(new{mesh,vertex,influence,positionWorld=Point(position),positionHeadLocal=Point(headLocal)});
            }
            available=rows.Count>0&&total>1e-8f;
            if(available)center=sum/total;
            var localCenter=available?Vector3.Transform(center,headWorldInverse):new Vector3(float.NaN);
            return new{finger=finger==0?"Index":"Middle",sampleCount=rows.Count,centerWorld=available?Point(center):null,centerHeadLocal=available?Point(localCenter):null,samples=rows.ToArray()};
        }

        var indexData=CaptureFinger(0,out var indexCenter,out var indexAvailable);
        var middleData=CaptureFinger(1,out var middleCenter,out var middleAvailable);
        var fractionAvailable=float.IsFinite(sampler.RightFingertipIndexFraction);
        var fraction=fractionAvailable?Math.Clamp(sampler.RightFingertipIndexFraction,0,1):(float?)null;
        Vector3? activeCenter=null;
        if(sampler.RightFingertipContactSampleAvailable&&fraction is float activeFraction)
        {
            if(activeFraction<=1e-4f&&middleAvailable)activeCenter=middleCenter;
            else if(activeFraction>=.9999f&&indexAvailable)activeCenter=indexCenter;
            else if(indexAvailable&&middleAvailable)activeCenter=Vector3.Lerp(middleCenter,indexCenter,activeFraction);
        }
        var activeLocal=activeCenter.HasValue?Vector3.Transform(activeCenter.Value,headWorldInverse):(Vector3?)null;
        var m=world[head];
        return new
        {
            available=activeCenter.HasValue,
            reason=activeCenter.HasValue?null:!fractionAvailable?"The active Index/Middle blend fraction is unavailable.":"The cached sample required by the active Index/Middle blend is unavailable.",
            source="final RigSampler.Positions weighted by cached fingertip sample influences; transformed by inverse same-frame mixamorig:Head world matrix",
            coordinateUnits="model world units; app reports distances in millimeters after multiplying by 1000",
            matrixConvention="System.Numerics Matrix4x4 row-vector transform; 16 values are M11..M14, M21..M24, M31..M34, M41..M44",
            headWorldMatrix=new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44},
            headWorldOrigin=Point(new Vector3(m.M41,m.M42,m.M43)),
            contactSampleAvailable=sampler.RightFingertipContactSampleAvailable,
            indexSampleCount=sampler.RightIndexFingertipSurfaceSampleCount,
            middleSampleCount=sampler.RightMiddleFingertipSurfaceSampleCount,
            indexFraction=fraction,
            activePadFinger=fraction is float activeBlend?activeBlend<=1e-4f?"Middle":activeBlend>=.9999f?"Index":"Middle+Index":null,
            activePadCenterWorld=activeCenter.HasValue?Point(activeCenter.Value):null,
            activePadCenterHeadLocal=activeLocal.HasValue?Point(activeLocal.Value):null,
            index=indexData,middle=middleData
        };
    }

    private static object CaptureRightIndexPadGeometry(RigSampler sampler,RigAnimation rig,double actionSeconds)
    {
        var asset=GetField<ModelAsset>(sampler,"_asset");var world=GetField<Matrix4x4[]>(sampler,"_world");
        var positions=sampler.Positions;var normals=sampler.Normals;
        int Find(string name)=>Array.FindIndex(rig.Nodes,n=>string.Equals(n.Name,name,StringComparison.OrdinalIgnoreCase));
        Vector3 Joint(int node)=>node<0||node>=world.Length?new(float.NaN):new(world[node].M41,world[node].M42,world[node].M43);
        static float Weight(Vector4 w,int k)=>k switch{0=>w.X,1=>w.Y,2=>w.Z,_=>w.W};
        static float[] Point(Vector3 p)=>new[]{p.X,p.Y,p.Z};
        static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
        static float[]? PointOrNull(Vector3 p)=>Finite(p)?Point(p):null;
        static float[] Uv(Vector2 uv)=>new[]{uv.X,uv.Y};
        static Vector3 Unit(Vector3 v)=>v.LengthSquared()>1e-10f?Vector3.Normalize(v):Vector3.Zero;
        var hand=Find("mixamorig:RightHand");var index1=Find("mixamorig:RightHandIndex1");var index2=Find("mixamorig:RightHandIndex2");var index3=Find("mixamorig:RightHandIndex3");
        var middle1=Find("mixamorig:RightHandMiddle1");var pinky1=Find("mixamorig:RightHandPinky1");var head=Find("mixamorig:Head");
        if(new[]{hand,index1,index2,index3,middle1,pinky1,head}.Any(i=>i<0))return new{available=false,actionSeconds,reason="Required hand/head bones are missing."};
        var handPoint=Joint(hand);var axis=Unit(Joint(index3)-Joint(index2));var headSurface=sampler.RightHeadSurfacePointWorld;var headNormal=Unit(sampler.RightHeadSurfaceNormalWorld);
        if(axis.LengthSquared()<1e-10f||headNormal.LengthSquared()<1e-10f)return new{available=false,actionSeconds,reason="Non-finite current index axis or head normal."};
        var terminalInfluence=new float[asset.Meshes.Length][];var headInfluence=new float[asset.Meshes.Length][];
        var candidates=new List<(int Mesh,int Vertex,float Influence,float Projection,Vector3 Position,Vector3 Normal)>();
        for(var meshIndex=0;meshIndex<asset.Meshes.Length;meshIndex++)
        {
            var binding=rig.Meshes[meshIndex];var mesh=asset.Meshes[meshIndex];terminalInfluence[meshIndex]=new float[mesh.Positions.Length];headInfluence[meshIndex]=new float[mesh.Positions.Length];
            if(binding.Skin<0||binding.Skin>=rig.Skins.Length)continue;var skin=rig.Skins[binding.Skin];
            for(var vertex=0;vertex<mesh.Positions.Length;vertex++)
            {
                var ids=binding.Joints[vertex];var ws=binding.Weights[vertex];var ti=0f;var hi=0f;
                for(var k=0;k<4;k++)
                {
                    var w=Weight(ws,k);var joint=ids[k];if(w<=0||joint<0||joint>=skin.Joints.Length)continue;
                    var node=skin.Joints[joint];if(node==index3)ti+=w;if(node==head)hi+=w;
                }
                terminalInfluence[meshIndex][vertex]=ti;headInfluence[meshIndex][vertex]=hi;
                if(ti<.08f||meshIndex>=positions.Length||vertex>=positions[meshIndex].Length)continue;
                var p=positions[meshIndex][vertex];var projection=Vector3.Dot(p-Joint(index3),axis);if(projection<0)continue;
                var n=meshIndex<normals.Length&&vertex<normals[meshIndex].Length?Unit(normals[meshIndex][vertex]):Vector3.Zero;
                candidates.Add((meshIndex,vertex,ti,projection,p,n));
            }
        }
        if(candidates.Count==0)return new{available=false,actionSeconds,reason="No index terminal surface vertices passed the runtime influence threshold."};
        var maxProjection=candidates.Max(c=>c.Projection);var minProjection=candidates.Min(c=>c.Projection);var band=Math.Clamp((maxProjection-minProjection)*.15f,.0025f,.008f);
        var selected=candidates.Where(c=>c.Projection>=maxProjection-band).OrderByDescending(c=>c.Influence).ThenByDescending(c=>c.Projection).Take(12).ToArray();
        var total=selected.Sum(c=>c.Influence);var tipCenter=selected.Aggregate(Vector3.Zero,(sum,c)=>sum+c.Position*c.Influence)/Math.Max(total,1e-8f);
        var palmCenter=(handPoint+Joint(index1)+Joint(middle1)+Joint(pinky1))*.25f;
        var selectedContactCenter=sampler.RightFingertipSurfaceCenterWorld;var selectedContactAvailable=sampler.RightFingertipContactSampleAvailable&&Finite(selectedContactCenter);
        var towardPalm=selectedContactAvailable?palmCenter-selectedContactCenter:Vector3.Zero;towardPalm-=axis*Vector3.Dot(towardPalm,axis);towardPalm=selectedContactAvailable?Unit(towardPalm):Vector3.Zero;
        var padCandidates=selected.Select(c=>new{mesh=c.Mesh,vertex=c.Vertex,influence=c.Influence,projectionMm=c.Projection*1000,positionWorld=Point(c.Position),normalWorld=Point(c.Normal),normalTowardPalmDot=selectedContactAvailable?(float?)Vector3.Dot(c.Normal,towardPalm):null,selectedPlaneGapMm=Vector3.Dot(c.Position-headSurface,headNormal)*1000}).ToArray();
        var allTerminalCandidates=candidates.Select(c=>new{mesh=c.Mesh,vertex=c.Vertex,influence=c.Influence,projectionMm=c.Projection*1000,positionWorld=Point(c.Position),normalWorld=Point(c.Normal),normalTowardPalmDot=selectedContactAvailable?(float?)Vector3.Dot(c.Normal,towardPalm):null,normalHeadOutwardDot=Vector3.Dot(c.Normal,headNormal),selectedPlaneGapMm=Vector3.Dot(c.Position-headSurface,headNormal)*1000}).ToArray();
        var facing=selected.Where(c=>selectedContactAvailable&&c.Normal.LengthSquared()>1e-10f&&Vector3.Dot(c.Normal,towardPalm)>.20f).ToArray();
        var facingAllCandidates=candidates.Where(c=>selectedContactAvailable&&c.Normal.LengthSquared()>1e-10f&&Vector3.Dot(c.Normal,towardPalm)>.20f).ToArray();
        var padNormal=facing.Length>0?Unit(facing.Aggregate(Vector3.Zero,(sum,c)=>sum+c.Normal*c.Influence)):Vector3.Zero;
        var cachedField=typeof(RigSampler).GetField("_fingertipSurfaceSamples",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        List<(int Mesh,int Vertex,float Influence,Vector3 Position,Vector3 Normal)> ReadCachedSamples(int finger)
        {
            var result=new List<(int Mesh,int Vertex,float Influence,Vector3 Position,Vector3 Normal)>();
            if(cachedField?.GetValue(sampler) is not Array bySide||bySide.Length<=1||bySide.GetValue(1) is not Array rightByFinger||rightByFinger.Length<=finger||rightByFinger.GetValue(finger) is not Array fingerSamples)return result;
            var recordType=fingerSamples.GetType().GetElementType();
            var meshProperty=recordType?.GetProperty("Mesh",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            var vertexProperty=recordType?.GetProperty("Vertex",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            var influenceProperty=recordType?.GetProperty("Influence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            foreach(var item in fingerSamples)
            {
                if(item is null||meshProperty is null||vertexProperty is null||influenceProperty is null)continue;
                var mesh=(int)meshProperty.GetValue(item)!;var vertex=(int)vertexProperty.GetValue(item)!;var influence=(float)influenceProperty.GetValue(item)!;
                if(mesh<0||mesh>=positions.Length||vertex<0||vertex>=positions[mesh].Length||!float.IsFinite(influence)||influence<=0)continue;
                var normal=mesh<normals.Length&&vertex<normals[mesh].Length?Unit(normals[mesh][vertex]):Vector3.Zero;
                result.Add((mesh,vertex,influence,positions[mesh][vertex],normal));
            }
            return result;
        }
        var cachedSamples=ReadCachedSamples(0);
        var cachedMiddleSamples=ReadCachedSamples(1);
        var cachedTotal=cachedSamples.Sum(c=>c.Influence);var cachedCenter=cachedSamples.Count>0?cachedSamples.Aggregate(Vector3.Zero,(sum,c)=>sum+c.Position*c.Influence)/Math.Max(cachedTotal,1e-8f):new Vector3(float.NaN);
        // Recompute the production cached sample directly from the sampler's current
        // local positions and world matrices. The public contact value is written
        // during IK, before Sample applies its optional final pose-transition blend.
        var localPositions=GetField<Vector3[][]>(sampler,"_localPositions");
        Vector3 FormulaCenter(IEnumerable<(int Mesh,int Vertex,float Influence,Vector3 Position,Vector3 Normal)> sourceSamples)
        {
            var sum=Vector3.Zero;var total=0f;
            foreach(var sample in sourceSamples)
            {
                var binding=rig.Meshes[sample.Mesh];if(binding.Skin<0||binding.Skin>=rig.Skins.Length)continue;var skin=rig.Skins[binding.Skin];
                var weights=binding.Weights[sample.Vertex];var ids=binding.Joints[sample.Vertex];var point=Vector3.Zero;
                for(var k=0;k<4;k++)
                {
                    var weight=Weight(weights,k);var joint=ids[k];if(weight<=0||joint<0||joint>=skin.Joints.Length)continue;
                    point+=Vector3.Transform(localPositions[sample.Mesh][sample.Vertex],skin.InverseBind[joint]*world[skin.Joints[joint]])*weight;
                }
                sum+=point*sample.Influence;total+=sample.Influence;
            }
            return total>1e-8f?sum/total:new Vector3(float.NaN);
        }
        var formulaCenter=FormulaCenter(cachedSamples);var middleFormulaCenter=FormulaCenter(cachedMiddleSamples);
        var recordedIndexFractionAvailable=float.IsFinite(sampler.RightFingertipIndexFraction);
        float? recordedIndexFraction=recordedIndexFractionAvailable?Math.Clamp(sampler.RightFingertipIndexFraction,0,1):null;
        Vector3? activeFormulaCenter=null;
        if(sampler.RightFingertipContactSampleAvailable&&recordedIndexFraction is float activeFraction)
        {
            if(activeFraction<=1e-4f&&cachedMiddleSamples.Count>0&&Finite(middleFormulaCenter))activeFormulaCenter=middleFormulaCenter;
            else if(activeFraction>=.9999f&&cachedSamples.Count>0&&Finite(formulaCenter))activeFormulaCenter=formulaCenter;
            else if(cachedSamples.Count>0&&cachedMiddleSamples.Count>0&&Finite(formulaCenter)&&Finite(middleFormulaCenter))
                activeFormulaCenter=Vector3.Lerp(middleFormulaCenter,formulaCenter,activeFraction);
        }
        var publishedActiveCenter=sampler.RightFingertipSurfaceCenterWorld;
        var rootStageShiftAvailable=activeFormulaCenter.HasValue&&Finite(publishedActiveCenter);
        var rootStageShift=rootStageShiftAvailable?activeFormulaCenter!.Value-publishedActiveCenter:Vector3.Zero;
        var rootShiftedHeadTarget=headSurface+(rootStageShiftAvailable?rootStageShift:Vector3.Zero);
        var cachedCenterAvailable=cachedSamples.Count>0&&Finite(cachedCenter);
        var cachedTowardPalm=cachedCenterAvailable?palmCenter-cachedCenter:Vector3.Zero;cachedTowardPalm-=axis*Vector3.Dot(cachedTowardPalm,axis);cachedTowardPalm=cachedCenterAvailable?Unit(cachedTowardPalm):Vector3.Zero;
        var cachedFacing=cachedSamples.Where(c=>cachedCenterAvailable&&c.Normal.LengthSquared()>1e-10f&&Vector3.Dot(c.Normal,cachedTowardPalm)>.20f).ToArray();
        var cachedPadVertices=cachedSamples.Select(c=>new{mesh=c.Mesh,vertex=c.Vertex,influence=c.Influence,positionWorld=Point(c.Position),normalWorld=Point(c.Normal),normalTowardPalmDot=Vector3.Dot(c.Normal,cachedTowardPalm),selectedPlaneGapMm=Vector3.Dot(c.Position-headSurface,headNormal)*1000}).ToArray();
        var fingertipSamplesByFinger=new List<object>();var fingerNames=new[]{"Index","Middle","Ring","Pinky","Thumb"};
        var contactFingerSamples=new Dictionary<int,List<(int Mesh,int Vertex,float Influence,Vector3 Position,Vector3 Normal)>>();
        if(cachedField?.GetValue(sampler) is Array allSideSamples&&allSideSamples.Length>1&&allSideSamples.GetValue(1) is Array allRightFingerSamples)
        {
            for(var finger=0;finger<Math.Min(fingerNames.Length,allRightFingerSamples.Length);finger++)
            {
                if(allRightFingerSamples.GetValue(finger) is not Array fingerSamples)continue;
                var sampleType=fingerSamples.GetType().GetElementType();
                var fingerMeshProperty=sampleType?.GetProperty("Mesh",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
                var fingerVertexProperty=sampleType?.GetProperty("Vertex",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
                var fingerInfluenceProperty=sampleType?.GetProperty("Influence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
                var rows=new List<object>();var center=Vector3.Zero;var fingerTotal=0f;
                if(finger<2)contactFingerSamples[finger]=new List<(int Mesh,int Vertex,float Influence,Vector3 Position,Vector3 Normal)>();
                foreach(var item in fingerSamples)
                {
                    if(item is null||fingerMeshProperty is null||fingerVertexProperty is null||fingerInfluenceProperty is null)continue;
                    var m=(int)fingerMeshProperty.GetValue(item)!;var v=(int)fingerVertexProperty.GetValue(item)!;var influence=(float)fingerInfluenceProperty.GetValue(item)!;
                    if(m<0||m>=positions.Length||v<0||v>=positions[m].Length||m>=rig.Meshes.Length)continue;
                    var normal=m<normals.Length&&v<normals[m].Length?Unit(normals[m][v]):Vector3.Zero;var binding=rig.Meshes[m];var jointWeights=new List<object>();
                    if(binding.Skin>=0&&binding.Skin<rig.Skins.Length)
                    {
                        var skin=rig.Skins[binding.Skin];var ids=binding.Joints[v];var ws=binding.Weights[v];
                        for(var k=0;k<4;k++)
                        {
                            var weight=Weight(ws,k);var joint=ids[k];if(weight<=0||joint<0||joint>=skin.Joints.Length)continue;
                            var node=skin.Joints[joint];jointWeights.Add(new{joint=rig.Nodes[node].Name,weight});
                        }
                    }
                    var modelMesh=asset.Meshes[m];center+=positions[m][v]*influence;fingerTotal+=influence;
                    if(finger<2)contactFingerSamples[finger].Add((m,v,influence,positions[m][v],normal));
                    rows.Add(new{mesh=m,vertex=v,influence,positionWorld=Point(positions[m][v]),normalWorld=Point(normal),uv=v<modelMesh.UV.Length?Uv(modelMesh.UV[v]):null,jointWeights=jointWeights.ToArray()});
                }
                var fingerCenterAvailable=rows.Count>0&&fingerTotal>1e-8f;
                center=fingerCenterAvailable?center/fingerTotal:default;
                fingertipSamplesByFinger.Add(new{finger=fingerNames[finger],sampleCount=rows.Count,sampleCenterWorld=fingerCenterAvailable?Point(center):null,samples=rows.ToArray()});
            }
        }
        var surfaceTriangles=new List<(int Mesh,int Triangle,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal)>();
        var allHeadSurfaceTriangles=new List<(int Mesh,int Triangle,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal)>();
        var nearHeadIntersectionTriangles=new List<(int Mesh,int Triangle,int IA,int IB,int IC,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal)>();var headPoint=Joint(head);
        for(var meshIndex=0;meshIndex<asset.Meshes.Length;meshIndex++)
        {
            var binding=rig.Meshes[meshIndex];if(binding.Skin<0)continue;var mesh=asset.Meshes[meshIndex];
            for(var offset=0;offset+2<mesh.Indices.Length;offset+=3)
            {
                var ia=mesh.Indices[offset];var ib=mesh.Indices[offset+1];var ic=mesh.Indices[offset+2];var wa=headInfluence[meshIndex][ia];var wb=headInfluence[meshIndex][ib];var wc=headInfluence[meshIndex][ic];
                if(Math.Min(wa,Math.Min(wb,wc))<.15f||(wa+wb+wc)/3<.55f)continue;
                var a=positions[meshIndex][ia];var b=positions[meshIndex][ib];var c=positions[meshIndex][ic];var center=(a+b+c)/3;var n=Unit(Vector3.Cross(b-a,c-a));if(n.LengthSquared()<1e-10f)continue;
                if(Vector3.Dot(n,center-headPoint)<0)n=-n;
                var triangle=(meshIndex,offset/3,a,b,c,n);allHeadSurfaceTriangles.Add(triangle);
                if(Vector3.DistanceSquared(ClosestPointForPadDiagnostic(rootShiftedHeadTarget,a,b,c),rootShiftedHeadTarget)<=.12f*.12f)
                    nearHeadIntersectionTriangles.Add((meshIndex,offset/3,ia,ib,ic,a,b,c,n));
                if(Vector3.DistanceSquared(center,headSurface)>.0025f||Vector3.Dot(n,headNormal)<.20f)continue;
                surfaceTriangles.Add(triangle);
            }
        }
        Vector3 WeightedCenter(List<(int Mesh,int Vertex,float Influence,Vector3 Position,Vector3 Normal)> samples)
        {
            var sum=Vector3.Zero;var total=0f;
            foreach(var sample in samples){sum+=sample.Position*sample.Influence;total+=sample.Influence;}
            return total>1e-8f?sum/total:new(float.NaN);
        }
        (int Mesh,int Triangle,int IA,int IB,int IC,Vector3 Point,Vector3 Normal,float Distance,float SignedSide)? ClosestHead(Vector3 query)
        {
            var bestDistanceSquared=float.PositiveInfinity;(int Mesh,int Triangle,int IA,int IB,int IC,Vector3 Point,Vector3 Normal,float Distance,float SignedSide)? best=null;
            foreach(var tri in nearHeadIntersectionTriangles)
            {
                var point=ClosestPointForPadDiagnostic(query,tri.A,tri.B,tri.C);var distanceSquared=Vector3.DistanceSquared(query,point);
                if(distanceSquared<bestDistanceSquared)
                {bestDistanceSquared=distanceSquared;best=(tri.Mesh,tri.Triangle,tri.IA,tri.IB,tri.IC,point,tri.Normal,MathF.Sqrt(distanceSquared),Vector3.Dot(query-point,tri.Normal));}
            }
            return best;
        }
        var indexContactSamples=contactFingerSamples.GetValueOrDefault(0)??new();
        var middleContactSamples=contactFingerSamples.GetValueOrDefault(1)??new();
        var indexContactCenter=WeightedCenter(indexContactSamples);var middleContactCenter=WeightedCenter(middleContactSamples);
        Vector3? activeContactCenter=null;
        if(sampler.RightFingertipContactSampleAvailable&&recordedIndexFraction is float semanticContactBlend)
        {
            if(semanticContactBlend<=1e-4f&&middleContactSamples.Count>0&&Finite(middleContactCenter))activeContactCenter=middleContactCenter;
            else if(semanticContactBlend>=.9999f&&indexContactSamples.Count>0&&Finite(indexContactCenter))activeContactCenter=indexContactCenter;
            else if(indexContactSamples.Count>0&&middleContactSamples.Count>0&&Finite(indexContactCenter)&&Finite(middleContactCenter))
                activeContactCenter=Vector3.Lerp(middleContactCenter,indexContactCenter,semanticContactBlend);
        }
        var activeContactSamples=new List<object>();
        if(activeContactCenter.HasValue&&recordedIndexFraction is float blend)
        {
            var blendSamples=blend<=1e-4f
                ?middleContactSamples.Select(sample=>(Finger:"Middle",Sample:sample,Blend:1f))
                :blend>=.9999f
                    ?indexContactSamples.Select(sample=>(Finger:"Index",Sample:sample,Blend:1f))
                    :middleContactSamples.Select(sample=>(Finger:"Middle",Sample:sample,Blend:1-blend)).Concat(indexContactSamples.Select(sample=>(Finger:"Index",Sample:sample,Blend:blend)));
            foreach(var item in blendSamples.Where(sample=>sample.Blend>1e-5f))
            {
                var nearest=ClosestHead(item.Sample.Position);
                activeContactSamples.Add(new{finger=item.Finger,mesh=item.Sample.Mesh,vertex=item.Sample.Vertex,influence=item.Sample.Influence,contactBlend=item.Blend,
                    finalSkinnedPositionWorld=Point(item.Sample.Position),surfaceNormalWorld=Point(item.Sample.Normal),nearestHeadWeightedTriangle=nearest is { } hit?new{mesh=hit.Mesh,triangle=hit.Triangle,vertices=new[]{hit.IA,hit.IB,hit.IC},pointWorld=Point(hit.Point),normalWorld=Point(hit.Normal),distanceMm=hit.Distance*1000,signedSideMm=hit.SignedSide*1000}:null});
            }
        }
        var activeCenterNearest=activeContactCenter is Vector3 activeCenter?ClosestHead(activeCenter):null;
        object ProjectFinalHead(Vector3 query)
        {
            (int Mesh,int Triangle,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal)? best=null;var bestD=float.PositiveInfinity;var bestPoint=Vector3.Zero;
            foreach(var tri in allHeadSurfaceTriangles)
            {
                var q=ClosestPointForPadDiagnostic(query,tri.A,tri.B,tri.C);var d=Vector3.DistanceSquared(query,q);
                if(d<bestD){bestD=d;best=tri;bestPoint=q;}
            }
            return new{queryWorld=PointOrNull(query),triangleMesh=best?.Mesh,triangle=best?.Triangle,pointWorld=best.HasValue?Point(bestPoint):null,
                normalWorld=best.HasValue?Point(best.Value.Normal):null,distanceMm=float.IsFinite(bestD)?MathF.Sqrt(bestD)*1000:(float?)null,
                localSignedSideMm=best.HasValue?Vector3.Dot(query-bestPoint,best.Value.Normal)*1000:(float?)null};
        }
        var rightHandNodes=new HashSet<int>();
        foreach(var (name,node) in rig.Nodes.Select((node,index)=>(node.Name,index)))
            if(name.Equals("mixamorig:RightHand",StringComparison.OrdinalIgnoreCase)||
               name.StartsWith("mixamorig:RightHandIndex",StringComparison.OrdinalIgnoreCase)||
               name.StartsWith("mixamorig:RightHandMiddle",StringComparison.OrdinalIgnoreCase)||
               name.StartsWith("mixamorig:RightHandRing",StringComparison.OrdinalIgnoreCase)||
               name.StartsWith("mixamorig:RightHandPinky",StringComparison.OrdinalIgnoreCase)||
               name.StartsWith("mixamorig:RightHandThumb",StringComparison.OrdinalIgnoreCase))rightHandNodes.Add(node);
        var handVertices=new List<(int Mesh,int Vertex,float Influence,Vector3 Position)>();
        var handInfluenceByMesh=new float[asset.Meshes.Length][];
        for(var meshIndex=0;meshIndex<asset.Meshes.Length;meshIndex++)
        {
            var binding=rig.Meshes[meshIndex];handInfluenceByMesh[meshIndex]=new float[binding.Joints.Length];if(binding.Skin<0||binding.Skin>=rig.Skins.Length)continue;var skin=rig.Skins[binding.Skin];
            for(var vertex=0;vertex<binding.Joints.Length;vertex++)
            {
                var ids=binding.Joints[vertex];var weights=binding.Weights[vertex];var influence=0f;
                for(var k=0;k<4;k++)
                {var weight=Weight(weights,k);var joint=ids[k];if(weight>0&&joint>=0&&joint<skin.Joints.Length&&rightHandNodes.Contains(skin.Joints[joint]))influence+=weight;}
                handInfluenceByMesh[meshIndex][vertex]=influence;
                if(influence<.08f||meshIndex>=positions.Length||vertex>=positions[meshIndex].Length)continue;
                var point=positions[meshIndex][vertex];if(Vector3.DistanceSquared(point,headSurface)>.20f*.20f)continue;
                handVertices.Add((meshIndex,vertex,influence,point));
            }
        }
        var handSurfaceDistances=new List<(int Mesh,int Vertex,float Influence,Vector3 Position,int HeadMesh,int HeadTriangle,Vector3 ClosestPoint,Vector3 Normal,float DistanceMm,float LocalSignedSideMm)>();
        foreach(var handVertex in handVertices)
        {
            var bestD=float.PositiveInfinity;var bestPoint=Vector3.Zero;var bestNormal=Vector3.Zero;var bestMesh=-1;var bestTriangle=-1;
            foreach(var tri in allHeadSurfaceTriangles)
            {
                var q=ClosestPointForPadDiagnostic(handVertex.Position,tri.A,tri.B,tri.C);var d=Vector3.DistanceSquared(handVertex.Position,q);
                if(d<bestD){bestD=d;bestPoint=q;bestNormal=tri.Normal;bestMesh=tri.Mesh;bestTriangle=tri.Triangle;}
            }
            if(float.IsFinite(bestD))handSurfaceDistances.Add((handVertex.Mesh,handVertex.Vertex,handVertex.Influence,handVertex.Position,bestMesh,bestTriangle,bestPoint,bestNormal,
                MathF.Sqrt(bestD)*1000,Vector3.Dot(handVertex.Position-bestPoint,bestNormal)*1000));
        }
        var sortedHandDistances=handSurfaceDistances.Select(d=>d.DistanceMm).OrderBy(d=>d).ToArray();
        float? HandQuantile(double q)=>sortedHandDistances.Length==0?null:sortedHandDistances[(int)Math.Clamp(Math.Round((sortedHandDistances.Length-1)*q),0,sortedHandDistances.Length-1)];
        string? activeContactFingerBlend=recordedIndexFraction is float blendForLabel?blendForLabel<=1e-4f?"Middle":blendForLabel>=.9999f?"Index":"Middle+Index":null;
        float? rawCandidateCenterErrorVsSemanticIndexMm=cachedCenterAvailable&&Finite(sampler.RightIndexFingertipSurfaceCenterWorld)
            ?Vector3.Distance(tipCenter,sampler.RightIndexFingertipSurfaceCenterWorld)*1000:null;
        var nearestHandSurface=handSurfaceDistances.OrderBy(d=>d.DistanceMm).Take(16).Select(d=>new{mesh=d.Mesh,vertex=d.Vertex,rightHandInfluence=d.Influence,
            positionWorld=Point(d.Position),headMesh=d.HeadMesh,headTriangle=d.HeadTriangle,closestPointWorld=Point(d.ClosestPoint),headNormalWorld=Point(d.Normal),
            distanceMm=d.DistanceMm,localSignedSideMm=d.LocalSignedSideMm}).ToArray();
        var nearHandIntersectionTriangles=new List<(int Mesh,int Triangle,int IA,int IB,int IC,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal,float HA,float HB,float HC)>();
        for(var meshIndex=0;meshIndex<asset.Meshes.Length;meshIndex++)
        {
            var mesh=asset.Meshes[meshIndex];var handWeights=handInfluenceByMesh[meshIndex];
            for(var offset=0;offset+2<mesh.Indices.Length;offset+=3)
            {
                var ia=mesh.Indices[offset];var ib=mesh.Indices[offset+1];var ic=mesh.Indices[offset+2];
                var ha=handWeights[ia];var hb=handWeights[ib];var hc=handWeights[ic];
                if(Math.Max(ha,Math.Max(hb,hc))<.35f||(ha+hb+hc)/3<.10f)continue;
                var a=positions[meshIndex][ia];var b=positions[meshIndex][ib];var c=positions[meshIndex][ic];
                if(Vector3.DistanceSquared(ClosestPointForPadDiagnostic(rootShiftedHeadTarget,a,b,c),rootShiftedHeadTarget)>.12f*.12f)continue;
                var normal=Unit(Vector3.Cross(b-a,c-a));if(normal.LengthSquared()<1e-10f)continue;
                nearHandIntersectionTriangles.Add((meshIndex,offset/3,ia,ib,ic,a,b,c,normal,ha,hb,hc));
            }
        }
        var materialImageHashes=asset.Meshes.Select(m=>m.Material.Image is {Length:>0} image?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)):null).ToArray();
        var triangleIntersectionPairs=new List<object>();var intersectionPairCount=0;var sharedTopologyPairsSkipped=0;var boundsRejectedPairs=0;var trianglePairsTested=0;
        foreach(var handTriangle in nearHandIntersectionTriangles)
        foreach(var headTriangle in nearHeadIntersectionTriangles)
        {
            if(handTriangle.Mesh==headTriangle.Mesh&&
               new[]{handTriangle.IA,handTriangle.IB,handTriangle.IC}.Intersect(new[]{headTriangle.IA,headTriangle.IB,headTriangle.IC}).Any())
            {sharedTopologyPairsSkipped++;continue;}
            if(!DiagnosticTriangleBoundsOverlap(handTriangle.A,handTriangle.B,handTriangle.C,headTriangle.A,headTriangle.B,headTriangle.C,1e-6f))
            {boundsRejectedPairs++;continue;}
            trianglePairsTested++;
            var intersection=IntersectTrianglesForDiagnostic(handTriangle.A,handTriangle.B,handTriangle.C,headTriangle.A,headTriangle.B,headTriangle.C,2e-6f);
            if(intersection.Points.Length==0)continue;
            intersectionPairCount++;
            if(triangleIntersectionPairs.Count>=100)continue;
            var hm=asset.Meshes[handTriangle.Mesh];var sm=asset.Meshes[headTriangle.Mesh];
            var pointArray=intersection.Points.Select(Point).ToArray();var span=TriangleIntersectionSpanMm(intersection.Points);
            triangleIntersectionPairs.Add(new{coplanar=intersection.Coplanar,intersectionPointsWorld=pointArray,intersectionSpanMm=span,
                hand=new{mesh=handTriangle.Mesh,triangle=handTriangle.Triangle,vertices=new[]{handTriangle.IA,handTriangle.IB,handTriangle.IC},
                    positionsWorld=new[]{Point(handTriangle.A),Point(handTriangle.B),Point(handTriangle.C)},geometricNormalWorld=Point(handTriangle.Normal),
                    rightHandInfluence=new[]{handTriangle.HA,handTriangle.HB,handTriangle.HC},uv=new[]{hm.UV[handTriangle.IA],hm.UV[handTriangle.IB],hm.UV[handTriangle.IC]}.Select(Uv).ToArray(),
                    materialImageSha256=materialImageHashes[handTriangle.Mesh],mimeType=hm.Material.MimeType},
                head=new{mesh=headTriangle.Mesh,triangle=headTriangle.Triangle,vertices=new[]{headTriangle.IA,headTriangle.IB,headTriangle.IC},
                    positionsWorld=new[]{Point(headTriangle.A),Point(headTriangle.B),Point(headTriangle.C)},geometricNormalWorld=Point(headTriangle.Normal),
                    headInfluence=new[]{headInfluence[headTriangle.Mesh][headTriangle.IA],headInfluence[headTriangle.Mesh][headTriangle.IB],headInfluence[headTriangle.Mesh][headTriangle.IC]},
                    uv=new[]{sm.UV[headTriangle.IA],sm.UV[headTriangle.IB],sm.UV[headTriangle.IC]}.Select(Uv).ToArray(),
                    materialImageSha256=materialImageHashes[headTriangle.Mesh],mimeType=sm.Material.MimeType}});
        }
        var nearestSurface=candidates.Select(c=>
        {
            (int Mesh,int Triangle,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal)? best=null;var bestD=float.PositiveInfinity;var bestPoint=Vector3.Zero;
            foreach(var tri in surfaceTriangles)
            {
                var q=ClosestPointForPadDiagnostic(c.Position,tri.A,tri.B,tri.C);var d=Vector3.DistanceSquared(c.Position,q);
                if(d<bestD){bestD=d;best=tri;bestPoint=q;}
            }
            return new{mesh=c.Mesh,vertex=c.Vertex,closestHeadMesh=best?.Mesh,closestHeadTriangle=best?.Triangle,closestPointWorld=best.HasValue?Point(bestPoint):null,normalWorld=best.HasValue?Point(best.Value.Normal):null,distanceMm=float.IsFinite(bestD)?MathF.Sqrt(bestD)*1000:(float?)null,signedDistanceMm=best.HasValue?Vector3.Dot(c.Position-bestPoint,best.Value.Normal)*1000:(float?)null};
        }).ToArray();
        var nearestCachedHeadSurface=cachedSamples.Select(c=>
        {
            (int Mesh,int Triangle,Vector3 A,Vector3 B,Vector3 C,Vector3 Normal)? best=null;var bestD=float.PositiveInfinity;var bestPoint=Vector3.Zero;
            foreach(var tri in surfaceTriangles)
            {
                var q=ClosestPointForPadDiagnostic(c.Position,tri.A,tri.B,tri.C);var d=Vector3.DistanceSquared(c.Position,q);
                if(d<bestD){bestD=d;best=tri;bestPoint=q;}
            }
            return new{mesh=c.Mesh,vertex=c.Vertex,closestHeadMesh=best?.Mesh,closestHeadTriangle=best?.Triangle,closestPointWorld=best.HasValue?Point(bestPoint):null,normalWorld=best.HasValue?Point(best.Value.Normal):null,distanceMm=float.IsFinite(bestD)?MathF.Sqrt(bestD)*1000:(float?)null,signedDistanceMm=best.HasValue?Vector3.Dot(c.Position-bestPoint,best.Value.Normal)*1000:(float?)null};
        }).ToArray();
        return new{available=true,actionSeconds,poseTransitionProgress=sampler.PoseTransitionProgress,soleSupportCorrectionMeters=sampler.SoleSupportCorrectionMeters,modelSourceHash=asset.SourceHash,
            rightIndexSurfaceSampleCount=sampler.RightIndexFingertipSurfaceSampleCount,selectedRawIndexTerminalCandidateCount=selected.Length,rawIndexTerminalCandidateCount=candidates.Count,
            solverSemanticIndexSampleCount=sampler.RightIndexFingertipSurfaceSampleCount,solverSemanticMiddleSampleCount=sampler.RightMiddleFingertipSurfaceSampleCount,
            contactSampleAvailable=sampler.RightFingertipContactSampleAvailable,
            tipCenterWorld=(float[]?)null,rawIndexTerminalCandidateCenterWorld=PointOrNull(tipCenter),rawIndexTerminalCandidateIsSemanticContact=false,
            recordedTipCenterWorld=PointOrNull(sampler.RightIndexFingertipSurfaceCenterWorld),tipCenterErrorVsRecordedMm=(float?)null,rawCandidateCenterErrorVsSemanticIndexMm,
            activeContactPadSource="final-skinned positions weighted by the cached semantic Index/Middle samples selected by RigSampler; this can be Middle-only when index fraction is 0",
            activeContactIndexFraction=recordedIndexFraction,activeContactFingerBlend,
            activeContactPadCenterWorld=activeContactCenter.HasValue?Point(activeContactCenter.Value):null,
            activeContactPadCenterToSelectedSurfaceMm=activeContactCenter is Vector3 activeCenterForDistance&&Finite(headSurface)?Vector3.Distance(activeCenterForDistance,headSurface)*1000:(float?)null,
            activeContactPadCenterSelectedSolverPlaneGapMm=activeContactCenter is Vector3 activeCenterForGap&&Finite(headSurface)&&Finite(headNormal)?Vector3.Dot(activeCenterForGap-headSurface,headNormal)*1000:(float?)null,
            activeContactPadCenterNearestHeadTriangle=activeCenterNearest is { } activeHead?new{mesh=activeHead.Mesh,triangle=activeHead.Triangle,vertices=new[]{activeHead.IA,activeHead.IB,activeHead.IC},pointWorld=Point(activeHead.Point),normalWorld=Point(activeHead.Normal),distanceMm=activeHead.Distance*1000,signedSideMm=activeHead.SignedSide*1000}:null,
            activeContactPadSampleCount=activeContactSamples.Count,activeContactPadSamples=activeContactSamples,
            headSurfacePointWorld=Point(headSurface),headSurfaceNormalWorld=Point(headNormal),towardPalmWorld=selectedContactAvailable?Point(towardPalm):null,palmFacingVertexCount=facing.Length,palmFacingCandidateCount=facingAllCandidates.Length,palmFacingNormalEstimateWorld=facing.Length>0?Point(padNormal):null,
            solverCachedIndexSampleCount=cachedSamples.Count,solverCachedTipCenterWorld=cachedSamples.Count>0?Point(cachedCenter):null,
            solverCachedCenterErrorVsSamplerMm=cachedCenterAvailable&&Finite(sampler.RightIndexFingertipSurfaceCenterWorld)?Vector3.Distance(cachedCenter,sampler.RightIndexFingertipSurfaceCenterWorld)*1000:(float?)null,
            fingertipSamplesByFinger,
            oneRingPatchGeometry=selectedContactAvailable?CaptureHandPatchOneRing(sampler,rig,asset,positions,normals,towardPalm,headNormal,Joint,actionSeconds):(object)new{available=false,reason="The active semantic contact center is unavailable; no raw Index candidate was used as a substitute."},
            productionFormulaCachedTipCenterWorld=cachedSamples.Count>0&&Finite(formulaCenter)?Point(formulaCenter):null,
            productionFormulaErrorVsPositionsMm=cachedCenterAvailable&&Finite(formulaCenter)?Vector3.Distance(formulaCenter,cachedCenter)*1000:(float?)null,
            productionFormulaErrorVsPublishedPropertyMm=cachedCenterAvailable&&Finite(sampler.RightIndexFingertipSurfaceCenterWorld)?Vector3.Distance(formulaCenter,sampler.RightIndexFingertipSurfaceCenterWorld)*1000:(float?)null,
            finalHeadReprojectionOriginalTarget=ProjectFinalHead(headSurface),finalHeadReprojectionRootShiftedTarget=rootStageShiftAvailable?ProjectFinalHead(rootShiftedHeadTarget):null,
            rootStageShiftAvailable,rootStageShiftWorld=rootStageShiftAvailable?Point(rootStageShift):null,headWeightedTriangleCount=allHeadSurfaceTriangles.Count,
            nearbyRightHandSurfaceVertexCount=handSurfaceDistances.Count,rightHandSurfaceDistanceMm=new{min=HandQuantile(0),p05=HandQuantile(.05),median=HandQuantile(.50),p95=HandQuantile(.95),max=HandQuantile(1),localSignedSideNegativeCount=handSurfaceDistances.Count(d=>d.LocalSignedSideMm<-.5f),localSignedSidePositiveCount=handSurfaceDistances.Count(d=>d.LocalSignedSideMm>.5f)},
            nearestRightHandSurfaceVertices=nearestHandSurface,
            triangleIntersectionScope=new{nearHeadSurfaceTriangleCount=nearHeadIntersectionTriangles.Count,nearRightHandTriangleCount=nearHandIntersectionTriangles.Count,
                queryRadiusMm=120,handTriangleRule="max right-hand joint influence >= .35 and vertex-average >= .10; current centroid-nearest-point within 120mm of root-shifted head target",
                headTriangleRule="all three vertices head influence >= .15 and average >= .55; current triangle within 120mm of root-shifted head target",
                testedPairs=trianglePairsTested,boundsRejectedPairs,sharedTopologyPairsSkipped,intersectionPairCount,reportedPairCount=triangleIntersectionPairs.Count},
            triangleIntersections=triangleIntersectionPairs,
            solverCachedTowardPalmWorld=cachedCenterAvailable?Point(cachedTowardPalm):null,solverCachedPalmFacingVertexCount=cachedFacing.Length,solverCachedPadVertices=cachedPadVertices,nearestHeadSurfaceByCachedPadVertex=nearestCachedHeadSurface,
            padVertices=padCandidates,allIndexTerminalCandidates=allTerminalCandidates,nearbyHeadSurfaceTriangleCount=surfaceTriangles.Count,nearestHeadSurfaceByCandidate=nearestSurface,
            limitations=new[]{"Raw RightHandIndex3 influence candidates are exploratory geometry only and are not semantic Index contact; only RigSampler's cached semantic samples feed the active contact center.","Final head and right-hand distances use final skinned Positions, but surface side is only the nearest head triangle normal; this is not a watertight inside/outside test.","Head-weight thresholds identify a bone-weighted surface, not semantic scalp/skin versus hair; geometry and normal proxies do not certify physical contact."}};
    }
    private static Vector3 ClosestPointForPadDiagnostic(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
    {
        var ab=b-a;var ac=c-a;var ap=p-a;var d1=Vector3.Dot(ab,ap);var d2=Vector3.Dot(ac,ap);if(d1<=0&&d2<=0)return a;
        var bp=p-b;var d3=Vector3.Dot(ab,bp);var d4=Vector3.Dot(ac,bp);if(d3>=0&&d4<=d3)return b;
        var vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
        var cp=p-c;var d5=Vector3.Dot(ab,cp);var d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6)return c;
        var vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
        var va=d3*d6-d5*d4;if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0)return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
        var denom=1/(va+vb+vc);var v=vb*denom;var w=vc*denom;return a+ab*v+ac*w;
    }
    private static bool DiagnosticTriangleBoundsOverlap(Vector3 a0,Vector3 a1,Vector3 a2,Vector3 b0,Vector3 b1,Vector3 b2,float tolerance)
    {
        var amin=Vector3.Min(a0,Vector3.Min(a1,a2))-new Vector3(tolerance);var amax=Vector3.Max(a0,Vector3.Max(a1,a2))+new Vector3(tolerance);
        var bmin=Vector3.Min(b0,Vector3.Min(b1,b2));var bmax=Vector3.Max(b0,Vector3.Max(b1,b2));
        return amin.X<=bmax.X&&amax.X>=bmin.X&&amin.Y<=bmax.Y&&amax.Y>=bmin.Y&&amin.Z<=bmax.Z&&amax.Z>=bmin.Z;
    }
    private static (bool Coplanar,Vector3[] Points) IntersectTrianglesForDiagnostic(Vector3 a0,Vector3 a1,Vector3 a2,Vector3 b0,Vector3 b1,Vector3 b2,float tolerance)
    {
        var nA=Vector3.Cross(a1-a0,a2-a0);var nB=Vector3.Cross(b1-b0,b2-b0);
        if(nA.LengthSquared()<1e-14f||nB.LengthSquared()<1e-14f)return(false,Array.Empty<Vector3>());
        nA=Vector3.Normalize(nA);nB=Vector3.Normalize(nB);
        var dA=new[]{Vector3.Dot(a0-b0,nB),Vector3.Dot(a1-b0,nB),Vector3.Dot(a2-b0,nB)};
        var dB=new[]{Vector3.Dot(b0-a0,nA),Vector3.Dot(b1-a0,nA),Vector3.Dot(b2-a0,nA)};
        if(dA.Min()>tolerance||dA.Max()<-tolerance||dB.Min()>tolerance||dB.Max()<-tolerance)return(false,Array.Empty<Vector3>());
        var nearlyParallel=Math.Abs(Vector3.Dot(nA,nB))>.99999f;
        var coplanar=nearlyParallel&&dA.Max(Math.Abs)<=tolerance&&dB.Max(Math.Abs)<=tolerance;
        var points=new List<Vector3>();
        if(coplanar)
        {
            var axis=Math.Abs(nA.X)>Math.Abs(nA.Y)?(Math.Abs(nA.X)>Math.Abs(nA.Z)?0:2):(Math.Abs(nA.Y)>Math.Abs(nA.Z)?1:2);
            Vector2 Project(Vector3 p)=>axis switch{0=>new(p.Y,p.Z),1=>new(p.X,p.Z),_=>new(p.X,p.Y)};
            var ta=new[]{a0,a1,a2};var tb=new[]{b0,b1,b2};var pa=ta.Select(Project).ToArray();var pb=tb.Select(Project).ToArray();
            for(var i=0;i<3;i++)if(PointInTriangleDiagnostic2D(pa[i],pb[0],pb[1],pb[2],tolerance))AddUniqueDiagnostic(points,ta[i],tolerance);
            for(var i=0;i<3;i++)if(PointInTriangleDiagnostic2D(pb[i],pa[0],pa[1],pa[2],tolerance))AddUniqueDiagnostic(points,tb[i],tolerance);
            for(var i=0;i<3;i++)for(var j=0;j<3;j++)
                if(SegmentIntersectionDiagnostic2D(pa[i],pa[(i+1)%3],pb[j],pb[(j+1)%3],tolerance,out var t))
                    AddUniqueDiagnostic(points,Vector3.Lerp(ta[i],ta[(i+1)%3],t),tolerance);
            return(true,points.ToArray());
        }
        CollectPlaneCrossings(a0,a1,a2,b0,nB,dA,b0,b1,b2,tolerance,points);
        CollectPlaneCrossings(b0,b1,b2,a0,nA,dB,a0,a1,a2,tolerance,points);
        return(false,points.ToArray());
    }
    private static void CollectPlaneCrossings(Vector3 p0,Vector3 p1,Vector3 p2,Vector3 planePoint,Vector3 planeNormal,float[] signedDistances,
        Vector3 t0,Vector3 t1,Vector3 t2,float tolerance,List<Vector3> points)
    {
        var vertices=new[]{p0,p1,p2};
        for(var edge=0;edge<3;edge++)
        {
            var start=vertices[edge];var end=vertices[(edge+1)%3];var d0=signedDistances[edge];var d1=signedDistances[(edge+1)%3];
            if(Math.Abs(d0)<=tolerance&&PointInTriangleDiagnostic3D(start,t0,t1,t2,tolerance))AddUniqueDiagnostic(points,start,tolerance);
            if((d0<-tolerance&&d1>tolerance)||(d0>tolerance&&d1<-tolerance))
            {
                var t=d0/(d0-d1);var hit=Vector3.Lerp(start,end,t);
                if(PointInTriangleDiagnostic3D(hit,t0,t1,t2,tolerance))AddUniqueDiagnostic(points,hit,tolerance);
            }
        }
    }
    private static bool PointInTriangleDiagnostic3D(Vector3 p,Vector3 a,Vector3 b,Vector3 c,float tolerance)
    {
        var v0=b-a;var v1=c-a;var v2=p-a;var d00=Vector3.Dot(v0,v0);var d01=Vector3.Dot(v0,v1);var d11=Vector3.Dot(v1,v1);
        var d20=Vector3.Dot(v2,v0);var d21=Vector3.Dot(v2,v1);var denom=d00*d11-d01*d01;if(Math.Abs(denom)<1e-16)return false;
        var v=(d11*d20-d01*d21)/denom;var w=(d00*d21-d01*d20)/denom;var u=1-v-w;var baryTolerance=Math.Max(1e-5,tolerance*10);
        return u>=-baryTolerance&&v>=-baryTolerance&&w>=-baryTolerance;
    }
    private static bool PointInTriangleDiagnostic2D(Vector2 p,Vector2 a,Vector2 b,Vector2 c,float tolerance)
    {
        static float Cross2(Vector2 x,Vector2 y)=>x.X*y.Y-x.Y*y.X;
        var s0=Cross2(b-a,p-a);var s1=Cross2(c-b,p-b);var s2=Cross2(a-c,p-c);var eps=Math.Max(1e-8f,tolerance*10);
        return (s0>=-eps&&s1>=-eps&&s2>=-eps)||(s0<=eps&&s1<=eps&&s2<=eps);
    }
    private static bool SegmentIntersectionDiagnostic2D(Vector2 a,Vector2 b,Vector2 c,Vector2 d,float tolerance,out float t)
    {
        static float Cross2(Vector2 x,Vector2 y)=>x.X*y.Y-x.Y*y.X;
        var r=b-a;var s=d-c;var denom=Cross2(r,s);t=0;if(Math.Abs(denom)<Math.Max(1e-12,tolerance*tolerance))return false;
        var q=c-a;var at=Cross2(q,s)/denom;var bt=Cross2(q,r)/denom;var e=Math.Max(1e-6,tolerance*10);
        if(at<-e||at>1+e||bt<-e||bt>1+e)return false;t=Math.Clamp(at,0,1);return true;
    }
    private static void AddUniqueDiagnostic(List<Vector3> points,Vector3 point,float tolerance)
    {
        var threshold=Math.Max(1e-10,tolerance*tolerance*4);if(points.All(existing=>Vector3.DistanceSquared(existing,point)>threshold))points.Add(point);
    }
    private static float TriangleIntersectionSpanMm(Vector3[] points)
    {
        var max=0f;for(var i=0;i<points.Length;i++)for(var j=i+1;j<points.Length;j++)max=Math.Max(max,Vector3.Distance(points[i],points[j]));return max*1000;
    }
}
