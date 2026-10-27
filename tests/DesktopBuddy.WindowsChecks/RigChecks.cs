using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;
using DesktopBuddy.UI;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static byte[] RigFixture(Action<JsonObject>? edit=null)
    {
        using var bin=new MemoryStream();using var writer=new BinaryWriter(bin);var views=new List<object>();var accessors=new List<object>();
        int Add(string type,int count,int component,Action write)
        {while(bin.Length%4!=0)writer.Write((byte)0);var offset=(int)bin.Length;write();var view=views.Count;views.Add(new{buffer=0,byteOffset=offset,byteLength=(int)bin.Length-offset});var index=accessors.Count;accessors.Add(new{bufferView=view,type,count,componentType=component});return index;}
        var position=Add("VEC3",3,5126,()=>{foreach(var x in new float[]{0,0,0,1,0,0,0,1,0})writer.Write(x);});
        var normal=Add("VEC3",3,5126,()=>{for(var i=0;i<3;i++){writer.Write(0f);writer.Write(0f);writer.Write(1f);}});
        var joints=Add("VEC4",3,5123,()=>{for(var i=0;i<12;i++)writer.Write((ushort)0);});
        var weights=Add("VEC4",3,5126,()=>{for(var i=0;i<3;i++){writer.Write(1f);writer.Write(0f);writer.Write(0f);writer.Write(0f);}});
        var indices=Add("SCALAR",3,5123,()=>{writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)2);});
        var ibm=Add("MAT4",1,5126,()=>{for(var i=0;i<16;i++)writer.Write(i%5==0?1f:0f);});
        var times=Add("SCALAR",2,5126,()=>{writer.Write(0f);writer.Write(1f);});
        var translation=Add("VEC3",2,5126,()=>{foreach(var v in new float[]{0,0,0,1,0,0})writer.Write(v);});
        var root=JsonNode.Parse(JsonSerializer.Serialize(new{asset=new{version="2.0"},scene=0,scenes=new[]{new{nodes=new[]{0,1}}},nodes=new object[]{new{name="joint"},new{mesh=0,skin=0}},skins=new[]{new{joints=new[]{0},inverseBindMatrices=ibm}},meshes=new[]{new{primitives=new[]{new{attributes=new{POSITION=position,NORMAL=normal,JOINTS_0=joints,WEIGHTS_0=weights},indices}}}},animations=new[]{new{name="Fixture",samplers=new[]{new{input=times,output=translation,interpolation="LINEAR"}},channels=new[]{new{sampler=0,target=new{node=0,path="translation"}}}}},accessors,bufferViews=views,buffers=new[]{new{byteLength=(int)bin.Length}}}))!.AsObject();edit?.Invoke(root);
        var json=JsonSerializer.SerializeToUtf8Bytes(root);var jl=(json.Length+3)&~3;var bl=((int)bin.Length+3)&~3;
        using var output=new MemoryStream();using var w=new BinaryWriter(output);w.Write(0x46546C67u);w.Write(2u);w.Write((uint)(28+jl+bl));w.Write(jl);w.Write(0x4E4F534Au);w.Write(json);for(var i=json.Length;i<jl;i++)w.Write((byte)' ');w.Write(bl);w.Write(0x004E4942u);w.Write(bin.ToArray());for(var i=bin.Length;i<bl;i++)w.Write((byte)0);return output.ToArray();
    }
    private static void RigCoreChecks(string model,string temp,List<object> metrics)
    {
        var playerType=typeof(MainWindow).Assembly.GetType("DesktopBuddy.Services.RigScenePlayer",true)!;
        var clampMethod=playerType.GetMethod("ClampPlaybackDelta",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        double PlaybackDelta(double actual,double interval)=>(double)clampMethod.Invoke(null,new object[]{actual,interval})!;
        var elapsedAtSixFps=Enumerable.Range(0,6).Sum(_=>PlaybackDelta(1d/6,1d/6));
        var elapsedAtThirtyFps=Enumerable.Range(0,30).Sum(_=>PlaybackDelta(1d/30,1d/30));
        Check("rig-playback-clock-keeps-wall-time-at-6fps",Math.Abs(elapsedAtSixFps-1)<1e-9&&Math.Abs(elapsedAtThirtyFps-1)<1e-9,$"Animation advanced {elapsedAtSixFps:F3}s at 6 FPS and {elapsedAtThirtyFps:F3}s at 30 FPS over one second.");
        Check("rig-playback-clock-bounds-render-stalls",PlaybackDelta(5,1d/6)<=.25+1e-9,"A long UI stall caused an unbounded animation time jump.");
        var asset=GlbReader.Load(RigFixture());Check("rig-fixture-parses",asset.Rig?.Clips.Length==1&&asset.Rig.Skins.Length==1,"Rig missing.");
        var sampler=new RigSampler(asset);sampler.Sample(0,.5);Check("rig-interpolates-translation",Math.Abs(sampler.Positions[0][1].X-1.5)<1e-5,"Skin matrix order or interpolation wrong.");
        sampler.Sample(0,1.5);Check("rig-loop-wraps",Math.Abs(sampler.Positions[0][1].X-1.5)<1e-5,"Loop wrap wrong.");
        sampler.Sample(0,0);Check("rig-bind-pose-preserved",Vector3.Distance(sampler.Positions[0][1],Vector3.UnitX)<1e-5,"Bind pose moved.");
        var rejected=false;try{GlbWriter.Write(asset,Path.Combine(temp,"must-not-strip.glb"));}catch(ModelImportException){rejected=true;}
        Check("rig-writer-never-strips-animation",rejected&&!File.Exists(Path.Combine(temp,"must-not-strip.glb")),"Static writer dropped rig.");
        void Reject(string name,Action<JsonObject> edit)
        {var failed=false;try{GlbReader.Load(RigFixture(edit));}catch(ModelImportException){failed=true;}Check(name,failed,"Invalid rig accepted.");}
        Reject("rig-reject-invalid-skin-index",j=>j["nodes"]![1]!["skin"]=99);
        Reject("rig-reject-invalid-joint-node",j=>j["skins"]![0]!["joints"]![0]=99);
        Reject("rig-reject-bind-count-mismatch",j=>j["accessors"]![5]!["count"]=2);
        Reject("rig-reject-cubic-without-bake",j=>j["animations"]![0]!["samplers"]![0]!["interpolation"]="CUBICSPLINE");
        Reject("rig-reject-morph-animation",j=>j["animations"]![0]!["channels"]![0]!["target"]!["path"]="weights");
        Reject("rig-reject-external-buffer",j=>j["buffers"]![0]!["uri"]="https://example.invalid/rig.bin");
        Reject("rig-reject-cycle",j=>j["nodes"]![0]!["children"]=new JsonArray(0));
        Reject("rig-reject-extra-influences",j=>j["meshes"]![0]!["primitives"]![0]!["attributes"]!["JOINTS_1"]=2);
        var actual=GlbReader.Load(model);Check("user-rig-and-clips",actual.Rig is{Clips.Length:2}&&actual.Rig.Clips.Any(c=>c.Name.Contains("Idle"))&&actual.Rig.Clips.Any(c=>c.Name.Contains("Running")),"User clips absent.");
        Check("user-animated-budget",actual.TriangleCount<=24000&&actual.VertexCount<=20000,"Animation mesh exceeds safe budget.");
        Check("user-weights-four-normalized",actual.Rig!.Meshes.Where(m=>m.Skin>=0).All(m=>m.Weights.All(w=>Math.Abs(w.X+w.Y+w.Z+w.W-1)<1e-4)),"Invalid repaired weights.");
        var idleA=CompanionIdleMotion.Sample(1.25);var idleRepeat=CompanionIdleMotion.Sample(1.25);var idleB=CompanionIdleMotion.Sample(6.75);
        Check("idle-motion-deterministic",idleA==idleRepeat,"Same idle timestamp produced a different pose.");
        var maxBody=Vector3.Zero;var maxHead=Vector3.Zero;var maxHeadGlance=0f;var maxIdleStep=0f;
        for(var i=0;i<450;i++)
        {
            var t=i*.2;var pose=CompanionIdleMotion.Sample(t);var next=CompanionIdleMotion.Sample(t+.04);
            maxBody=Vector3.Max(maxBody,Vector3.Abs(pose.Body));maxHead=Vector3.Max(maxHead,Vector3.Abs(pose.Head));
            maxHeadGlance=Math.Max(maxHeadGlance,Math.Abs(pose.Head.X));
            maxIdleStep=Math.Max(maxIdleStep,Vector3.Distance(pose.Body,next.Body)+Vector3.Distance(pose.Head,next.Head));
        }
        Check("idle-motion-bounded",float.IsFinite(idleA.Breathing)&&maxBody.X<.09&&maxBody.Y<.03&&maxBody.Z<.05&&maxHead.X<.16&&maxHead.Y<.04&&maxHead.Z<.04,"Idle motion exceeded its safe range.");
        Check("idle-attention-glance-visible",maxHeadGlance>.08,"Idle attention glance was not perceptible.");
        Check("idle-motion-smooth-per-frame",maxIdleStep<.025,"Idle motion changes too abruptly between frames.");
        var jumpPeak=CompanionActionMotion.Sample(CompanionActionKind.Jumping,.42);
        Check("jump-pose-has-airborne-shape",jumpPeak.LeftArm>.9&&jumpPeak.RightArm<-.9&&Math.Abs(jumpPeak.LeftLeg-jumpPeak.RightLeg)<1e-5&&jumpPeak.LeftLeg<0&&Math.Abs(jumpPeak.LeftKnee-jumpPeak.RightKnee)<1e-5&&jumpPeak.LeftKnee>.15&&jumpPeak.HipLift>.07,"Jump peak lacks a clear arm raise, knee tuck, or lift.");
        var handNodes=new[]{Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":LeftHand",StringComparison.Ordinal)),Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":RightHand",StringComparison.Ordinal))};
        var shoulderNodes=new[]{Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":LeftShoulder",StringComparison.Ordinal)),Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":RightShoulder",StringComparison.Ordinal))};
        var probeClip=Array.FindIndex(actual.Rig.Clips,c=>c.Name.Contains("Idle",StringComparison.OrdinalIgnoreCase));var probe=new RigSampler(actual);probe.Sample(probeClip,.5);
        Vector3 SurfaceHandCenter(int node){var sum=Vector3.Zero;var total=0f;for(var mesh=0;mesh<actual.Rig.Meshes.Length;mesh++){var binding=actual.Rig.Meshes[mesh];if(binding.Skin<0)continue;var palette=Array.IndexOf(actual.Rig.Skins[binding.Skin].Joints,node);if(palette<0)continue;for(var vertex=0;vertex<binding.Weights.Length;vertex++){var joints=binding.Joints[vertex];var weights=binding.Weights[vertex];var influence=(joints.X==palette?weights.X:0)+(joints.Y==palette?weights.Y:0)+(joints.Z==palette?weights.Z:0)+(joints.W==palette?weights.W:0);if(influence<=0)continue;sum+=probe.Positions[mesh][vertex]*influence;total+=influence;}}return total>0?sum/total:new Vector3(float.NaN);}
        var neutralHands=handNodes.Select(SurfaceHandCenter).ToArray();var neutralShoulders=shoulderNodes.Select(SurfaceHandCenter).ToArray();
        var transitionProbe=new RigSampler(actual);
        var transitionReference=new RigSampler(actual);
        var waveDefinition=CompanionActionMotion.Library.First(a=>a.Kind==CompanionActionKind.Waving);
        var clapDefinition=CompanionActionMotion.Library.First(a=>a.Kind==CompanionActionKind.Clapping);
        transitionProbe.CompanionAction=CompanionActionMotion.Sample(CompanionActionKind.Waving,waveDefinition.DurationSeconds*.5);
        transitionProbe.Sample(probeClip,.5);
        var visiblePose=transitionProbe.Positions.Select(p=>(Vector3[])p.Clone()).ToArray();
        float PoseError(Vector3[][] expected)=>transitionProbe.Positions.SelectMany((mesh,m)=>mesh.Select((p,v)=>Vector3.Distance(p,expected[m][v]))).Max();
        transitionProbe.BeginPoseTransition();
        transitionProbe.CompanionAction=CompanionActionMotion.Sample(CompanionActionKind.Clapping,clapDefinition.DurationSeconds*.5);
        transitionProbe.Sample(probeClip,.5);
        Check("action-interrupt-starts-at-displayed-skeleton",PoseError(visiblePose)<1e-5,"Interrupting a raised hand jumped away from its displayed pose.");
        transitionProbe.PoseTransitionProgress=.5f;transitionProbe.Sample(probeClip,.5);
        var interruptedPose=transitionProbe.Positions.Select(p=>(Vector3[])p.Clone()).ToArray();
        transitionProbe.BeginPoseTransition();transitionProbe.CompanionAction=default;transitionProbe.Sample(probeClip,.5);
        Check("action-reinterrupt-preserves-intermediate-skeleton",PoseError(interruptedPose)<1e-5,"A second interruption reset the in-flight skeleton.");
        transitionProbe.PoseTransitionProgress=1;transitionProbe.Sample(probeClip,.5);transitionReference.Sample(probeClip,.5);
        Check("action-transition-finishes-without-residual-offset",PoseError(transitionReference.Positions)<1e-5,"A completed action transition left an offset.");
        var headNode=Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":Head",StringComparison.Ordinal));var headCenter=SurfaceHandCenter(headNode);var faceTarget=headCenter+new Vector3(.08f,-.10f,.08f);
        var targetLeft=new Vector3(neutralHands[0].X*.40f,(neutralHands[0].Y+neutralShoulders[0].Y)*.5f,neutralHands[0].Z);var targetRight=new Vector3(neutralHands[1].X*.40f,(neutralHands[1].Y+neutralShoulders[1].Y)*.5f,neutralHands[1].Z);
        var leftArmCandidates=new List<(float Cost,float Arm,float Forearm,Vector3 Hand)>();var rightArmCandidates=new List<(float Cost,float Arm,float Forearm,Vector3 Hand)>();
        var coffeeCandidates=new List<(float Cost,float Arm,float Forearm,Vector3 Hand)>();
        for(var armStep=-8;armStep<=8;armStep++)for(var forearmStep=-6;forearmStep<=6;forearmStep++)
        {
            var arm=armStep*.30f;var forearm=forearmStep*.30f;
            probe.CompanionAction=new(default,default,arm,forearm,0,0,0,0,0,0,0,0,0);probe.Sample(probeClip,.5);var left=SurfaceHandCenter(handNodes[0]);leftArmCandidates.Add((Vector3.Distance(left,targetLeft),arm,forearm,left));
            probe.CompanionAction=new(default,default,0,0,0,arm,forearm,0,0,0,0,0,0);probe.Sample(probeClip,.5);var right=SurfaceHandCenter(handNodes[1]);rightArmCandidates.Add((Vector3.Distance(right,targetRight),arm,forearm,right));coffeeCandidates.Add((Vector3.Distance(right,faceTarget),arm,forearm,right));
        }
        metrics.Add(new{phase="pose-grid-search",head=new[]{headCenter.X,headCenter.Y,headCenter.Z},neutralLeft=new[]{neutralHands[0].X,neutralHands[0].Y,neutralHands[0].Z},neutralRight=new[]{neutralHands[1].X,neutralHands[1].Y,neutralHands[1].Z},shoulderLeft=new[]{neutralShoulders[0].X,neutralShoulders[0].Y,neutralShoulders[0].Z},shoulderRight=new[]{neutralShoulders[1].X,neutralShoulders[1].Y,neutralShoulders[1].Z},faceTarget=new[]{faceTarget.X,faceTarget.Y,faceTarget.Z},typingLeftTarget=new[]{targetLeft.X,targetLeft.Y,targetLeft.Z},typingRightTarget=new[]{targetRight.X,targetRight.Y,targetRight.Z},coffee=coffeeCandidates.OrderBy(x=>x.Cost).Take(6).Select(x=>new{x.Cost,x.Arm,x.Forearm,hand=new[]{x.Hand.X,x.Hand.Y,x.Hand.Z}}),typingLeft=leftArmCandidates.OrderBy(x=>x.Cost).Take(6).Select(x=>new{x.Cost,x.Arm,x.Forearm,hand=new[]{x.Hand.X,x.Hand.Y,x.Hand.Z}}),typingRight=rightArmCandidates.OrderBy(x=>x.Cost).Take(6).Select(x=>new{x.Cost,x.Arm,x.Forearm,hand=new[]{x.Hand.X,x.Hand.Y,x.Hand.Z}})});
        var hipLeftTarget = new Vector3(.16f, .90f, .08f); var hipRightTarget = new Vector3(-.16f, .90f, .08f);
        var hipLeftCandidates = new List<(float Cost, float Arm, float Forearm, Vector3 Hand)>(); var hipRightCandidates = new List<(float Cost, float Arm, float Forearm, Vector3 Hand)>();
        for (var armStep = -8; armStep <= 8; armStep++) for (var forearmStep = -6; forearmStep <= 6; forearmStep++)
        {
            var arm = armStep * .30f; var forearm = forearmStep * .30f;
            probe.CompanionAction = new(default, default, arm, forearm, 0, 0, 0, 0, 0, 0, 0, 0, 0); probe.Sample(probeClip, .5); var left = SurfaceHandCenter(handNodes[0]); hipLeftCandidates.Add((Vector3.Distance(left, hipLeftTarget), arm, forearm, left));
            probe.CompanionAction = new(default, default, 0, 0, 0, arm, forearm, 0, 0, 0, 0, 0, 0); probe.Sample(probeClip, .5); var right = SurfaceHandCenter(handNodes[1]); hipRightCandidates.Add((Vector3.Distance(right, hipRightTarget), arm, forearm, right));
        }
        metrics.Add(new { phase = "hip-pose-grid-search", left = hipLeftCandidates.OrderBy(x => x.Cost).Take(8).Select(x => new { x.Cost, x.Arm, x.Forearm, hand = new[] { x.Hand.X, x.Hand.Y, x.Hand.Z } }), right = hipRightCandidates.OrderBy(x => x.Cost).Take(8).Select(x => new { x.Cost, x.Arm, x.Forearm, hand = new[] { x.Hand.X, x.Hand.Y, x.Hand.Z } }) });
        foreach (var entry in new[] { (Kind: CompanionActionKind.AirGuitar, Progress: .32), (Kind: CompanionActionKind.HeartHands, Progress: .5), (Kind: CompanionActionKind.Shivering, Progress: .3), (Kind: CompanionActionKind.ConfidentPose, Progress: .5), (Kind: CompanionActionKind.SendingLove, Progress: .68) })
        {
            CompanionActionMotion.TryGetDefinition(entry.Kind, out var poseDefinition); probe.CompanionAction = CompanionActionMotion.Sample(entry.Kind, poseDefinition.DurationSeconds * entry.Progress); probe.Sample(probeClip, .5);
            var hands = handNodes.Select(SurfaceHandCenter).ToArray(); var handDistance = Vector3.Distance(hands[0], hands[1]);
            metrics.Add(new { phase = $"{poseDefinition.Id}-surface-hands-before-assert", left = new[] { hands[0].X, hands[0].Y, hands[0].Z }, right = new[] { hands[1].X, hands[1].Y, hands[1].Z }, handDistance });
            if (entry.Kind == CompanionActionKind.AirGuitar) Check("air-guitar-hands-hold-front-instrument", hands.All(hand => hand.Y > 1.0f && hand.Z > neutralHands[0].Z + .10f) && handDistance > .10f && handDistance < .24f, "Air guitar should place separated hands in front of the torso.");
            if (entry.Kind == CompanionActionKind.HeartHands) Check("heart-hands-meet-in-front-of-chest", Math.Abs(hands[0].X) < .10f && Math.Abs(hands[1].X) < .10f && hands[0].Y > 1.0f && hands[1].Y > 1.0f && hands[0].Z > neutralHands[0].Z + .10f && hands[1].Z > neutralHands[1].Z + .10f && handDistance < .10f, "Heart hands should meet visibly in front of the chest.");
            if (entry.Kind == CompanionActionKind.Shivering) Check("shivering-crosses-arms-in-front", hands.All(hand => Math.Abs(hand.X) < .15f && hand.Y > .95f && hand.Z > neutralHands[0].Z + .10f), "Shivering arms should fold in front of the body.");
            if (entry.Kind == CompanionActionKind.ConfidentPose)
            {
                var world=GetField<Matrix4x4[]>(probe,"_world");
                Vector3 Landmark(string suffix)
                {var node=Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":"+suffix,StringComparison.Ordinal));return new(world[node].M41,world[node].M42,world[node].M43);}
                var pelvis=Landmark("Hips");var waist=Landmark("Spine");
                // Hands rest at the anatomical waistband, above the pelvis, rather than an
                // absolute world-height band which ended at this fixture's Hips joint.
                var minimumY=Math.Min(pelvis.Y,waist.Y)-.02f;var maximumY=Math.Max(pelvis.Y,waist.Y)+.02f;
                metrics.Add(new{phase="confident-waist-landmarks",pelvisY=pelvis.Y,waistY=waist.Y,waistZ=waist.Z,minimumY,maximumY});
                Check("confident-hands-rest-at-hips",hands[0].X>.12f&&hands[1].X<-.12f&&hands.All(hand=>hand.Y>minimumY&&hand.Y<maximumY&&Math.Abs(hand.Z-waist.Z)<.12f),"Confident hands must flank the waist between pelvis and first spine joint, not reach forward.");
            }
            if (entry.Kind == CompanionActionKind.SendingLove) Check("sending-love-opens-both-arms", hands[0].X > .40f && hands[1].X < -.40f && hands[0].Y > 1.15f && hands[1].Y > 1.15f && handDistance > .90f, "Sending love should visibly open both arms after gathering.");
            metrics.Add(new { phase = $"{poseDefinition.Id}-surface-hands", left = new[] { hands[0].X, hands[0].Y, hands[0].Z }, right = new[] { hands[1].X, hands[1].Y, hands[1].Z }, leftFront = hands[0].Z - neutralHands[0].Z, rightFront = hands[1].Z - neutralHands[1].Z, handDistance });
        }
        probe.CompanionAction=default;probe.Sample(probeClip,.5);probe.CompanionAction=jumpPeak;probe.Sample(probeClip,.5);var jumpHands=handNodes.Select(SurfaceHandCenter).ToArray();var jumpShoulders=shoulderNodes.Select(SurfaceHandCenter).ToArray();
        Check("jump-raises-and-opens-hands",jumpHands[0].Y>neutralHands[0].Y+.20f&&jumpHands[1].Y>neutralHands[1].Y+.20f&&Math.Abs(jumpHands[0].Y-jumpHands[1].Y)<.06f&&jumpHands[0].X>neutralHands[0].X+.10f&&jumpHands[1].X<neutralHands[1].X-.10f,"Jump must visibly lift both hands from rest with matched heights and open arms.");
        var celebration=CompanionActionMotion.Sample(CompanionActionKind.Celebrating,.625);probe.CompanionAction=celebration;probe.Sample(probeClip,.5);var celebrationHands=handNodes.Select(SurfaceHandCenter).ToArray();var celebrationShoulders=shoulderNodes.Select(SurfaceHandCenter).ToArray();Check("celebration-raises-hands-above-shoulders",celebrationHands[0].Y>celebrationShoulders[0].Y+.05f&&celebrationHands[1].Y>celebrationShoulders[1].Y+.05f,"Celebration should visibly raise both arms above the shoulders.");metrics.Add(new{phase="celebration-hand-height",leftDeltaY=celebrationHands[0].Y-celebrationShoulders[0].Y,rightDeltaY=celebrationHands[1].Y-celebrationShoulders[1].Y});
        metrics.Add(new{phase="jump-hand-mesh",leftRestX=neutralHands[0].X,leftPeakX=jumpHands[0].X,leftDeltaY=jumpHands[0].Y-neutralHands[0].Y,leftDeltaX=jumpHands[0].X-neutralHands[0].X,rightRestX=neutralHands[1].X,rightPeakX=jumpHands[1].X,rightDeltaY=jumpHands[1].Y-neutralHands[1].Y,rightDeltaX=jumpHands[1].X-neutralHands[1].X});
        foreach(var kind in new[]{CompanionActionKind.Typing,CompanionActionKind.CoffeeBreak,CompanionActionKind.EatingSnack,CompanionActionKind.Resting,CompanionActionKind.Yawning})
        {
            // The quiet tasks now have a distinct preparation/approach before the contact dwell.
            var taskProgress=kind is CompanionActionKind.CoffeeBreak or CompanionActionKind.EatingSnack?.55:.45;
            CompanionActionMotion.TryGetDefinition(kind,out var definition);probe.CompanionAction=CompanionActionMotion.Sample(kind,definition.DurationSeconds*taskProgress);probe.Sample(probeClip,.5);
            var hands=handNodes.Select(SurfaceHandCenter).ToArray();var shoulders=shoulderNodes.Select(SurfaceHandCenter).ToArray();
            metrics.Add(new{phase=definition.Id+"-surface-before-task-assert",left=new[]{hands[0].X,hands[0].Y,hands[0].Z},right=new[]{hands[1].X,hands[1].Y,hands[1].Z},mouthDistance=Vector3.Distance(hands[1],faceTarget)});
            if(kind==CompanionActionKind.Typing)
            {
                var world=GetField<Matrix4x4[]>(probe,"_world");
                Vector3 Joint(string suffix){var node=Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":"+suffix,StringComparison.Ordinal));return new(world[node].M41,world[node].M42,world[node].M43);}
                var centre=(Joint("LeftArm")+Joint("RightArm"))*.5f;
                var lateral=Vector3.Normalize(Joint("LeftArm")-Joint("RightArm"));var up=Vector3.Normalize(Joint("Head")-Joint("Hips"));
                var forward=Vector3.Normalize(Vector3.Cross(lateral,up));var pelvisHeight=Vector3.Dot(Joint("Hips")-centre,up);
                var left=Vector3.Dot(hands[0]-centre,lateral);var right=Vector3.Dot(hands[1]-centre,lateral);
                var width=Vector3.Distance(Joint("LeftArm"),Joint("RightArm"))*.5f+.10f;
                metrics.Add(new{phase="typing-separated-torso-workspaces",left,right,width,pelvisHeight});
                Check("typing-hands-reach-forward-workspace",left>.04f&&right<-.04f&&left<width&&right> -width&&left-right>.12f&&
                    hands.All(hand=>Vector3.Dot(hand-centre,up)>pelvisHeight-.02f&&Vector3.Dot(hand-centre,up)<.04f)&&
                    Vector3.Dot(hands[0]-neutralHands[0],forward)>.10f&&Vector3.Dot(hands[1]-neutralHands[1],forward)>.10f,
                    "Typing requires distinct left/right spaces in front of the torso between pelvis and shoulders, not hands meeting at the centre.");
            }
            if(kind is CompanionActionKind.CoffeeBreak or CompanionActionKind.EatingSnack)
            {
                var world=GetField<Matrix4x4[]>(probe,"_world");
                Vector3 Joint(string suffix){var node=Array.FindIndex(actual.Rig.Nodes,n=>n.Name.EndsWith(":"+suffix,StringComparison.Ordinal));return new(world[node].M41,world[node].M42,world[node].M43);}
                var up=Vector3.Normalize(Joint("Head")-Joint("Hips"));var lateral=Vector3.Normalize(Joint("LeftArm")-Joint("RightArm"));
                // Fixed-character lower-face reference: the old weighted Head mesh centre
                // includes long hair, and its +8cm X offset is not the mouth centre.
                var lowerFace=Joint("Head")+Vector3.Normalize(Vector3.Cross(lateral,up))*.10f-up*.05f;
                var distance=Vector3.Distance(hands[1],lowerFace);
                metrics.Add(new{phase=definition.Id+"-lower-face-reference",lowerFace=new[]{lowerFace.X,lowerFace.Y,lowerFace.Z},distance,oldReferenceDistance=Vector3.Distance(hands[1],faceTarget)});
                Check(kind==CompanionActionKind.CoffeeBreak?"coffee-hand-reaches-mouth":"snack-hand-reaches-mouth",distance<(kind==CompanionActionKind.CoffeeBreak?.16f:.20f),"The grip region must remain near the fixed character's lower face; no palm-over-eyes requirement.");
            }
            if(kind==CompanionActionKind.Yawning)Check("yawn-hand-reaches-mouth",Vector3.Distance(hands[1],faceTarget)<.20f,"Yawning hand should reach the mouth area.");
            metrics.Add(new{phase=$"{definition.Id}-hand-pose",leftDeltaY=hands[0].Y-neutralHands[0].Y,rightDeltaY=hands[1].Y-neutralHands[1].Y,leftDeltaX=hands[0].X-neutralHands[0].X,rightDeltaX=hands[1].X-neutralHands[1].X,leftDeltaZ=hands[0].Z-neutralHands[0].Z,rightDeltaZ=hands[1].Z-neutralHands[1].Z,leftAboveShoulder=hands[0].Y-shoulders[0].Y,rightAboveShoulder=hands[1].Y-shoulders[1].Y,mouthDistance=Vector3.Distance(hands[1],faceTarget)});
        }
        probe.CompanionAction=default;probe.Sample(probeClip,.5);
        var actionLibrary=CompanionActionMotion.Library;
        var requiredStates=new[]{"idle","running-right","running-left","waving","jumping","failed","waiting","running","review","typing","coffee","burger","resting"};
        Check("companion-actions-cover-petdex-states",requiredStates.All(state=>actionLibrary.Any(action=>action.PetDexState==state)),"A PetDex base state has no procedural rig action.");
        Check("companion-action-library-has-variety",actionLibrary.Count>=56&&actionLibrary.Select(action=>action.Id).Distinct(StringComparer.Ordinal).Count()==actionLibrary.Count,"The companion action set is too small or has duplicate IDs.");
        // A greeting with two waves needs time to raise, hold and lower the hand.
        // The old 2.5-second cap rewarded rushed motion; keep a finite one-shot cap.
        Check("companion-action-durations-bounded",actionLibrary.All(action=>action.DurationSeconds>=.65&&action.DurationSeconds<=5),"A one-shot action duration is outside the bounded range.");
        Check("greeting-does-not-rush-two-waves",CompanionActionMotion.Library.First(a=>a.Kind==CompanionActionKind.Waving).DurationSeconds>=2.4,"A two-wave greeting must include unhurried entry and recovery.");
        CompanionActionPose ActionAt(CompanionActionKind kind, double progress)
        {
            return CompanionActionMotion.TryGetDefinition(kind, out var action)
                ? CompanionActionMotion.Sample(kind, action.DurationSeconds * progress)
                : default;
        }
        var beckon = ActionAt(CompanionActionKind.Beckoning, .5);
        Check("beckoning-has-arm-wrist-cue", beckon.RightArm > .9f && beckon.RightForeArm > .8f && Math.Abs(beckon.RightHand) > .08f, "Beckoning should reach out and pulse the wrist.");
        var hug = ActionAt(CompanionActionKind.Hugging, .5);
        Check("hugging-crosses-arms", hug.LeftArm < -.8f && hug.RightArm > .8f && hug.LeftForeArm > .65f && hug.RightForeArm < -.65f, "Hug should close both arms around the chest.");
        var pointPose = ActionAt(CompanionActionKind.Pointing, .5);
        Check("pointing-has-directional-arm", Math.Abs(pointPose.RightArm) > 1.1f && Math.Abs(pointPose.RightHand) > .12f, "Pointing should extend one arm with a wrist cue.");
        var footTap = ActionAt(CompanionActionKind.ImpatientFootTap, .35);
        Check("foot-tap-has-alternating-steps", Math.Abs(footTap.LeftLeg) + Math.Abs(footTap.RightLeg) > .08f && Math.Abs(footTap.LeftKnee - footTap.RightKnee) > .05f, "Foot tapping should alternate legs.");
        var shrug = ActionAt(CompanionActionKind.Shrugging, .5);
        Check("shrug-has-shoulder-and-head-cue", Math.Abs(shrug.Head.Z) > .08f && Math.Abs(shrug.LeftForeArm) > .35f && Math.Abs(shrug.RightForeArm) > .35f, "Shrugging should lift both arms and tilt the head.");
        var sneak = ActionAt(CompanionActionKind.Sneaking, .45);
        Check("sneak-has-supported-knee-flexion", sneak.SupportWeight > .95f && sneak.LeftKnee > .2f && sneak.RightKnee > .2f && sneak.LeftLeg<0 && sneak.RightLeg<0, "Sneaking must coordinate both hips, knees and foot support; actual pelvis descent is checked by the dense mesh suite.");
        var listen = ActionAt(CompanionActionKind.Listening, .5);
        Check("listening-cups-ear", listen.RightArm > .8f && listen.RightForeArm > 1.0f && listen.Head.Z > .08f, "Listening should raise a hand beside the tilted head.");
        var cheer = ActionAt(CompanionActionKind.Cheering, .4);
        Check("cheering-alternates-fists", Math.Abs(cheer.LeftArm) + Math.Abs(cheer.RightArm) > 2.0f && Math.Abs(cheer.HipLift) > .01f, "Cheering should alternate raised fists and a bounce.");
        var facepalm = ActionAt(CompanionActionKind.Facepalming, .46);
        Check("facepalm-covers-face", facepalm.RightArm > .9f && facepalm.RightForeArm > 1.2f && facepalm.Head.X > .08f, "Facepalm should bring the hand to the face while the head drops.");
        var doubleWaveEarly = ActionAt(CompanionActionKind.DoubleWaving, .26);
        var doubleWaveLate = ActionAt(CompanionActionKind.DoubleWaving, .47);
        Check("double-wave-raises-and-waves-both-hands", doubleWaveEarly.LeftArm > 1.0f && doubleWaveEarly.RightArm < -1.0f && Math.Abs(doubleWaveEarly.LeftArm+doubleWaveEarly.RightArm)<1e-5f && doubleWaveEarly.LeftForeArm > 1.25f && doubleWaveEarly.RightForeArm < -1.25f && Math.Abs(doubleWaveEarly.LeftForeArm+doubleWaveEarly.RightForeArm)<1e-5f && doubleWaveEarly.LeftHand > .07f && doubleWaveEarly.RightHand < -.07f && doubleWaveLate.LeftHand < -.07f && doubleWaveLate.RightHand > .07f, "Double wave should keep both arms lifted, bend the elbows, and swing both wrists with a gentle stagger.");
        var sideStep = ActionAt(CompanionActionKind.SideStepping, .25);
        var oppositeSideStep = ActionAt(CompanionActionKind.SideStepping, .75);
        Check("side-step-alternates-support-foot", sideStep.LeftLegSide < -.15f && Math.Abs(sideStep.RightLegSide) < 1e-5f && oppositeSideStep.RightLegSide > .15f && Math.Abs(oppositeSideStep.LeftLegSide) < 1e-5f && sideStep.LeftFootContact < .05f && sideStep.RightFootContact > .95f && oppositeSideStep.RightFootContact < .05f && oppositeSideStep.LeftFootContact > .95f && sideStep.HipLift == 0 && sideStep.SupportWeight > 0 && sideStep.HorizontalSupportWeight > .95f, "Side step should clearly alternate the free leg while keeping the planted foot as support.");
        var jumpMotionPeak = ActionAt(CompanionActionKind.Jumping, .43);
        var jumpMotionLanding = ActionAt(CompanionActionKind.Jumping, .72);
        Check("jump-has-arc-and-landing", jumpMotionPeak.HipLift > .06f && jumpMotionPeak.HipLift > jumpMotionLanding.HipLift + .05f && Math.Abs(jumpMotionPeak.LeftArm + jumpMotionPeak.RightArm) < .05f, "Jump should have a distinct airborne arc, landing phase, and symmetric arm reach.");
        var airGuitar = ActionAt(CompanionActionKind.AirGuitar, .32);
        Check("air-guitar-uses-two-hand-strum", airGuitar.LeftArm < -.4f && airGuitar.RightArm > .6f && Math.Abs(airGuitar.LeftHand) + Math.Abs(airGuitar.RightHand) > .2f, "Air guitar should keep a fretting hand and a moving strumming hand.");
        var heartHands = ActionAt(CompanionActionKind.HeartHands, .5);
        Check("heart-hands-meet-at-chest", heartHands.LeftArm < -.6f && heartHands.RightArm > .6f && heartHands.LeftForeArm > .8f && heartHands.RightForeArm < -.8f, "Heart pose should fold both forearms toward the chest.");
        var shiverA = ActionAt(CompanionActionKind.Shivering, .25);
        var shiverB = ActionAt(CompanionActionKind.Shivering, .30);
        Check("shivering-has-fast-secondary-motion", Vector3.Distance(shiverA.Body, shiverB.Body) > .015f && Vector3.Distance(shiverA.Head, shiverB.Head) > .015f, "Shivering needs a visible short tremor in body and head.");
        var confident = ActionAt(CompanionActionKind.ConfidentPose, .5);
        Check("confident-pose-has-akimbo-shape", confident.LeftArm < -.2f && confident.LeftForeArm > .5f && confident.RightArm > .4f && confident.RightForeArm < -.7f && Math.Abs(confident.Body.Z) < .08f, "Confident pose should settle into a readable hand-on-hip stance.");
        var sendingLove = ActionAt(CompanionActionKind.SendingLove, .68);
        Check("sending-love-opens-arms-after-gather", sendingLove.LeftArm > .9f && sendingLove.RightArm < - .9f && Math.Abs(sendingLove.Body.X) > .02f, "Sending love should transition from gathered hands into an open release.");
        probe.CompanionAction = ActionAt(CompanionActionKind.Cheering, .15); probe.Sample(probeClip, .5);
        var cheerLeftHand = SurfaceHandCenter(handNodes[0]); var cheerLeftShoulder = SurfaceHandCenter(shoulderNodes[0]);
        probe.CompanionAction = ActionAt(CompanionActionKind.Cheering, .45); probe.Sample(probeClip, .5);
        var cheerRightHand = SurfaceHandCenter(handNodes[1]); var cheerRightShoulder = SurfaceHandCenter(shoulderNodes[1]);
        Check("cheering-lifts-each-hand-above-shoulders", cheerLeftHand.Y > cheerLeftShoulder.Y + .04f && cheerRightHand.Y > cheerRightShoulder.Y + .04f, "Alternating cheers should lift both hands, not hold a horizontal arm pose.");
        metrics.Add(new{phase="cheering-hand-height",leftDeltaY=cheerLeftHand.Y-cheerLeftShoulder.Y,rightDeltaY=cheerRightHand.Y-cheerRightShoulder.Y});
        probe.CompanionAction = ActionAt(CompanionActionKind.Facepalming, .46); probe.Sample(probeClip, .5);
        var facepalmHand = SurfaceHandCenter(handNodes[1]);
        Check("facepalm-hand-reaches-face", Vector3.Distance(facepalmHand, faceTarget) < .24f, "Facepalm hand should visibly reach the face.");
        var idleClip=Array.FindIndex(actual.Rig.Clips,c=>c.Name.Contains("Idle",StringComparison.OrdinalIgnoreCase));
        var actionSampler=new RigSampler(actual);var referenceSampler=new RigSampler(actual);var actionMetrics=new List<object>();var allActionsFinite=true;var allActionsVisible=true;var actionReturnsToRest=true;
        foreach(var definition in actionLibrary.Where(action=>action.Kind!=CompanionActionKind.Idle))
        {
            var maximumDifference=0f;
            for(var frame=1;frame<24;frame++)
            {
                var at=definition.DurationSeconds*frame/24;var pose=CompanionActionMotion.Sample(definition.Kind,at);
                allActionsFinite&=pose.IsFinite;actionSampler.CompanionAction=pose;actionSampler.Sample(idleClip,at);referenceSampler.Sample(idleClip,at);
                for(var mesh=0;mesh<actionSampler.Positions.Length;mesh++)for(var vertex=0;vertex<actionSampler.Positions[mesh].Length;vertex++)
                {
                    var point=actionSampler.Positions[mesh][vertex];if(!float.IsFinite(point.X)||!float.IsFinite(point.Y)||!float.IsFinite(point.Z))allActionsFinite=false;
                    maximumDifference=Math.Max(maximumDifference,Vector3.Distance(point,referenceSampler.Positions[mesh][vertex]));
                }
            }
            allActionsVisible&=maximumDifference>.00001f;actionMetrics.Add(new{id=definition.Id,durationSeconds=definition.DurationSeconds,maxVertexOffset=maximumDifference});
            actionSampler.CompanionAction=default;actionSampler.Sample(idleClip,0);referenceSampler.Sample(idleClip,0);
            var resetError=actionSampler.Positions.SelectMany((mesh,index)=>mesh.Select((point,vertex)=>Vector3.Distance(point,referenceSampler.Positions[index][vertex]))).Max();
            actionReturnsToRest&=resetError<1e-6f;
        }
        Check("companion-actions-deform-real-rig",allActionsVisible,"At least one procedural action did not visibly deform the imported Mixamo rig.");
        Check("companion-actions-remain-finite",allActionsFinite,"A sampled action produced non-finite bone or vertex data.");
        Check("companion-actions-reset-without-drift",actionReturnsToRest,"A procedural action left residual pose offsets.");
        metrics.Add(new{phase="petdex-inspired-action-library",actionCount=actionLibrary.Count,states=actionLibrary.Select(action=>new{id=action.Id,label=action.Label,action.PetDexState,action.DurationSeconds}),samples=actionMetrics});
        var idleSampler=new RigSampler(actual);idleSampler.Sample(idleClip,1.4);var neutralIdle=idleSampler.Positions.Select(m=>(Vector3[])m.Clone()).ToArray();
        idleSampler.CompanionIdleBody=idleB.Body;idleSampler.CompanionIdleHead=idleB.Head;idleSampler.CompanionBreathing=idleB.Breathing;idleSampler.Sample(idleClip,1.4);
        var idleDeformation=idleSampler.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,neutralIdle[i][j]))).Max();
        Check("idle-micro-motion-deforms-rig",idleDeformation>.0001&&idleSampler.Positions.SelectMany(m=>m).All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)),"Idle micro-motion did not safely deform the model.");
        var ambientPose=idleSampler.Positions.Select(m=>(Vector3[])m.Clone()).ToArray();idleSampler.Sample(idleClip,1.4);
        var idleRepeatError=idleSampler.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,ambientPose[i][j]))).Max();
        Check("idle-micro-motion-no-drift",idleRepeatError<1e-6,"Idle overlay accumulated bone drift.");

        var real=new RigSampler(actual);var running=Array.FindIndex(actual.Rig.Clips,c=>c.Name.Contains("Running"));real.Sample(running,0);var initial=real.Positions.Select(x=>(Vector3[])x.Clone()).ToArray();real.Sample(running,.2);
        Check("user-bones-deform-mesh",real.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,initial[i][j]))).Max()>.01,"Animation does not deform vertices.");
        var fullNormalFrame=new RigSampler(actual);fullNormalFrame.Sample(running,.24);
        var positionOnlyFrame=new RigSampler(actual);positionOnlyFrame.Sample(running,.24,updateNormals:false);
        var positionOnlyError=fullNormalFrame.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,positionOnlyFrame.Positions[i][j]))).Max();
        Check("position-only-skinning-matches-full",positionOnlyError<1e-6,"Skipping idle normal updates changed vertex positions.");
        Check("position-only-skinning-skips-normal-buffer",positionOnlyFrame.Normals.SelectMany(m=>m).All(n=>n==Vector3.Zero),"Position-only frame unexpectedly recalculated normals.");

        real.Sample(running,0);var firstPose=real.Positions.Select(x=>(Vector3[])x.Clone()).ToArray();real.Sample(running,actual.Rig.Clips[running].Duration-0.00001);
        var loopError=real.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,firstPose[i][j]))).Max();
        Check("user-loop-seam-under-one-millimeter",loopError<.001,"Loop boundary is discontinuous.");
        var poseScene=ModelSceneFactory.Create(actual,CancellationToken.None);
        var painted=(MaterialGroup)((GeometryModel3D)poseScene.Children[0]).Material;
        Check("uv-brush-uses-absolute-coordinates",painted.Children.OfType<DiffuseMaterial>().Any(m=>m.Brush is ImageBrush{ViewportUnits:BrushMappingMode.Absolute}),"Atlas was stretched to cropped UV bounds.");
        Check("user-deformed-positions-finite",real.Positions.SelectMany(m=>m).All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)),"NaN positions.");
        for(var i=0;i<10;i++)real.Sample(running,i*.01);var bytes=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();for(var i=0;i<180;i++)real.Sample(running,i/30d);watch.Stop();var allocation=GC.GetAllocatedBytesForCurrentThread()-bytes;
        Check("sampler-bounded-per-frame-allocation",allocation<32768,"Sampler allocates per-frame geometry.");
        metrics.Add(new{phase="core-sampler",actual.TriangleCount,actual.VertexCount,bones=actual.Rig.Skins[0].Joints.Length,frames=180,totalMilliseconds=watch.Elapsed.TotalMilliseconds,meanFrameMilliseconds=watch.Elapsed.TotalMilliseconds/180,allocatedBytes=allocation,clips=actual.Rig.Clips.Select(c=>new{c.Name,c.Duration})});
    }
    private static void RunRigCoreChecks(Application app,string[] args)
    {
        var temp=Path.Combine(Path.GetTempPath(),"Qianmeng-RigCore-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);var success=false;var metrics=new List<object>();
        try
        {
            var at=Array.IndexOf(args,"--model-file");if(at<0||at+1>=args.Length)throw new ArgumentException("--model-file must name the imported GLB.");
            RigCoreChecks(Path.GetFullPath(args[at+1]),temp,metrics);success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            try{Directory.Delete(temp,true);}catch{}
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,finishedAt=DateTimeOffset.Now,checks=Results,metrics},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown(success?0:1);
        }
    }
    private static async Task RunRigMotionPreviewAsync(Application app, string[] args)
    {
        var modelAt = Array.IndexOf(args, "--model-file");
        var success = false;
        var error = (string?)null;
        MainWindow? pet = null;
        var captures = new List<object>();
        object? reverseTransition = null;
        var profileAt = Array.IndexOf(args, "--motion-profile-dir");
        var preserveProfile = profileAt >= 0;
        var temp = preserveProfile && profileAt + 1 < args.Length
            ? Path.GetFullPath(args[profileAt + 1])
            : Path.Combine(Path.GetTempPath(), "Qianmeng-MotionPreview-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (modelAt < 0 || modelAt + 1 >= args.Length) throw new ArgumentException("--model-file must name the imported GLB.");
            if (preserveProfile && profileAt + 1 >= args.Length) throw new ArgumentException("--motion-profile-dir requires a new isolated directory.");
            if (preserveProfile && (Directory.Exists(temp) || File.Exists(temp))) throw new ArgumentException("Refusing to reuse an existing motion preview profile: " + temp);
            Directory.CreateDirectory(temp);
            var source = Path.GetFullPath(args[modelAt + 1]);
            var store = new LocalStore(Path.Combine(temp, "profile"));
            pet = new MainWindow(store, _ => { }, new ChatService(() => null))
            {
                Topmost = false,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = SystemParameters.WorkArea.Right - 320,
                Top = SystemParameters.WorkArea.Bottom - 460,
            };
            app.MainWindow = pet;
            pet.Show();
            await Task.Delay(250);
            for(var wait=0;wait<150&&pet.IsModelLoading;wait++)await Task.Delay(100);
            await pet.ImportPetModelAsync(source);
            var runtimeRouting=args.Contains("--runtime-action-routing-preview");
            var idle = runtimeRouting?pet.AnimationNames[RigClipResolver.FindIdle(GetField<RigScenePlayer>(pet,"_animationPlayer").Clips)]:pet.AnimationNames.First(name => name.Contains("Idle", StringComparison.OrdinalIgnoreCase));
            pet.PreviewAnimation(idle);
            await Task.Delay(250);
            // Freeze the temporary preview window. File encoding must not advance action time.
            pet.PauseAnimation();
            var player = GetField<object>(pet, "_animationPlayer");
            var sampler = GetField<RigSampler>(player, "_sampler");
            var clips = (RigClip[])player.GetType().GetProperty("Clips")!.GetValue(player)!;
            var poseFrames=new List<object>();
            var poseRig=GetField<RigAnimation>(sampler,"_rig");
            var restWorld=new Matrix4x4[poseRig.Nodes.Length];
            foreach(var node in poseRig.Order)restWorld[node]=poseRig.Nodes[node].Rest.Matrix*(poseRig.Nodes[node].Parent<0?Matrix4x4.Identity:restWorld[poseRig.Nodes[node].Parent]);
            static float[] MatrixValues(Matrix4x4 m)=>new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44};
            var idleClip = Array.FindIndex(clips, clip => clip.Name == idle);
            var runningClip = runtimeRouting?RigClipResolver.FindRunning(clips):Array.FindIndex(clips, clip => clip.Name.Contains("Running", StringComparison.OrdinalIgnoreCase));
            var nativeOverrideClip=-1;
            var modelView = (PetModelView)pet.FindName("PetModel");
            if(args.Contains("--motion-ground-grid"))
            {
                var reviewCamera=GetField<System.Windows.Media.Media3D.OrthographicCamera>(modelView,"_camera");
                reviewCamera.Position=new(0,.8,3);reviewCamera.LookDirection=new(0,-.8,-3);
                var asset=GetField<ModelAsset>(player,"_asset");
                var center=GetField<Vector3>(player,"_center");var size=GetField<float>(player,"_size");
                var floor=(asset.Meshes.SelectMany(m=>m.Positions).Min(p=>p.Y)-center.Y)/size;
                var grid=new System.Windows.Media.Media3D.MeshGeometry3D();
                void Strip(double x0,double z0,double x1,double z1)
                {
                    var offset=grid.Positions.Count;
                    grid.Positions.Add(new(x0,floor,z0));grid.Positions.Add(new(x1,floor,z0));grid.Positions.Add(new(x1,floor,z1));grid.Positions.Add(new(x0,floor,z1));
                    foreach(var index in new[]{0,1,2,0,2,3})grid.TriangleIndices.Add(offset+index);
                }
                for(var line=-10;line<=10;line++)
                {var at=line*.05;Strip(at-.001,-.5,at+.001,.5);Strip(-.5,at-.006,.5,at+.006);}
                var material=new System.Windows.Media.Media3D.EmissiveMaterial(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(180,150,160,180)));
                var scene=(System.Windows.Media.Media3D.Model3DGroup)player.GetType().GetProperty("Scene")!.GetValue(player)!;
                scene.Children.Add(new System.Windows.Media.Media3D.GeometryModel3D(grid,material){BackMaterial=material});
            }
            var originalYaw = GetField<double>(modelView, "_yaw");
            var scale = pet.Settings.PetModelScale;
            async Task CapturePoseImage(double yaw,string path)
            {
                modelView.SetPose(yaw,scale);
                await pet.Dispatcher.InvokeAsync(pet.UpdateLayout,System.Windows.Threading.DispatcherPriority.Render);
                await Task.Delay(34);
                SaveWindowImage(pet,path);
            }
            void SetPlayerField(string name, object value) => player.GetType()
                .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(player, value);
            void SampleFrame(CompanionActionKind kind, double actionSeconds, double clipSeconds)
            {
                // Running uses the model's native clip; the other actions use one frozen Idle frame.
                var clip = nativeOverrideClip>=0?nativeOverrideClip:kind == CompanionActionKind.Running ? runningClip : idleClip;
                if (clip < 0) throw new InvalidOperationException($"Missing source clip for {kind}.");
                player.GetType().GetMethod("Select")!.Invoke(player, new object[] { clip });
                GetField<System.Windows.Threading.DispatcherTimer>(player, "_frameTimer").Stop();
                SetPlayerField("_playing", false);
                SetPlayerField("_seconds", clipSeconds);
                SetPlayerField("_fromClip", -1);
                SetPlayerField("_fromSeconds", 0d);
                SetPlayerField("_blendSeconds", 1d);
                SetPlayerField("_poseTransitionStarted", double.NegativeInfinity);
                SetPlayerField("_companionAction", kind);
                SetPlayerField("_companionActionElapsed", actionSeconds);
                SetPlayerField("_ambientAction", false);
                sampler.CompanionActionOverride = false;
                sampler.CompanionInteractionAction = default;
                sampler.CompanionGesture = Vector3.Zero;
                sampler.CompanionWaveSwing = 0;
                sampler.CompanionBodyMotion = Vector3.Zero;
                sampler.CompanionIdleBody = Vector3.Zero;
                sampler.CompanionIdleHead = Vector3.Zero;
                sampler.CompanionBreathing = 0;
                sampler.CompanionAction = nativeOverrideClip>=0||kind == CompanionActionKind.Running ? default : CompanionActionMotion.Sample(kind, actionSeconds);
                modelView.SetInteractionPose(0, 0, 1, 0);
                Invoke(player, "Render", true);
                Invoke(pet, "NotifyAnimation");
            }
            static string ImageHash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            SampleFrame(CompanionActionKind.None, 0, .5);
            SaveWindowImage(pet, Path.ChangeExtension(_report, ".idle.png"));

            if (args.Contains("--reverse-transition-preview"))
            {
                // Exercise the actual interaction blend with a synthetic seconds clock.
                // Five seconds of established gait makes a reset-to-zero reversal visible.
                var sampleMethod = pet.GetType().GetMethod("SampleInteractionAction",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                const double runStart = 100, reverseAt = 105, exitAt = 105.24;
                CompanionActionPose InteractionAt(double now) => (CompanionActionPose)sampleMethod.Invoke(pet, new object[] { now })!;
                object CaptureInteraction(string action, int frame, double now)
                {
                    var pose = InteractionAt(now);
                    SampleFrame(CompanionActionKind.None, 0, .5);
                    sampler.CompanionInteractionAction = pose;
                    sampler.CompanionActionOverride = true;
                    Invoke(player, "Render", true);
                    var prefix = Path.Combine(Path.GetDirectoryName(_report)!, Path.GetFileNameWithoutExtension(_report));
                    var image = $"{prefix}.{action}.frame-{frame:D2}.png";
                    var sideImage = $"{prefix}.{action}.side.frame-{frame:D2}.png";
                    try
                    {
                        modelView.SetPose(originalYaw, scale); SaveWindowImage(pet, image);
                        modelView.SetPose(originalYaw + 90, scale); SaveWindowImage(pet, sideImage);
                    }
                    finally { modelView.SetPose(originalYaw, scale); }
                    return new { frame, sampleClockSeconds = now, actionSeconds = now - runStart, clipSeconds = .5,
                        nativeRunningWeight = pose.NativeRunningWeight, nativeRunningDirection = pose.NativeRunningDirection,
                        representedGaitSeconds = pose.NativeRunningWeight > 1e-5f ? (double?)(pose.NativeRunningSeconds / pose.NativeRunningWeight) : null,
                        frontSha256 = ImageHash(image), sideSha256 = ImageHash(sideImage) };
                }
                async Task CaptureStage(string action, double anchor, double[] offsets)
                {
                    var samples = new List<object>();
                    for (var frame = 0; frame < offsets.Length; frame++)
                    {
                        samples.Add(CaptureInteraction(action, frame, anchor + offsets[frame]));
                        // Keep each frozen pose presented long enough for GPU counters to observe this process.
                        await Task.Delay(150);
                    }
                    captures.Add(new { action, sampling = "interaction-synthetic-clock", sourceClip = clips[idleClip].Name,
                        frontYaw = originalYaw, sideYaw = originalYaw + 90, frames = offsets.Length, sideFrames = offsets.Length, sampledFrames = samples });
                }
                Invoke(pet, "StartInteractionAction", CompanionActionKind.RunningRight, true, runStart);
                await CaptureStage("reverse-before", reverseAt, new[] { -.04, 0d });
                var startBefore = GetField<double>(pet, "_interactionActionStart");
                Invoke(pet, "StartInteractionAction", CompanionActionKind.RunningLeft, true, reverseAt);
                var startAfter = GetField<double>(pet, "_interactionActionStart");
                await CaptureStage("reverse-switch", reverseAt, new[] { 0d, .04, .08, .12, .16, .20, .24 });
                Invoke(pet, "ClearInteractionAction", exitAt);
                await CaptureStage("reverse-exit", exitAt, new[] { 0d, .04, .08, .12, .16, .20 });
                reverseTransition = new { syntheticClock = true, realPointerDragPerformed = false, runStart, reverseAt, exitAt,
                    startBefore, startAfter, gaitClockPreserved = Math.Abs(startBefore - startAfter) < 1e-9,
                    blendDurationSeconds = .16, entryPoint = "StartInteractionAction / SampleInteractionAction / ClearInteractionAction" };
            }
            else
            {
                var previewKinds = new[]
                {
                    CompanionActionKind.Running,
                    CompanionActionKind.RunningRight,
                    CompanionActionKind.RunningLeft,
                    CompanionActionKind.Jumping,
                    CompanionActionKind.Waving,
                    CompanionActionKind.DoubleWaving,
                    CompanionActionKind.SideStepping,
                };
                var actionsAt=Array.IndexOf(args,"--motion-actions");
                if(actionsAt>=0&&actionsAt+1<args.Length)
                {
                    var requested=args[actionsAt+1].Split(',');
                    previewKinds=requested.Select(id=>CompanionActionMotion.Library.First(d=>d.Id==id).Kind).ToArray();
                }
                foreach (var kind in previewKinds)
                {
                    if (!CompanionActionMotion.TryGetDefinition(kind, out var definition)) throw new InvalidOperationException($"Missing preview definition for {kind}.");
                    nativeOverrideClip=-1;
                    if(runtimeRouting)nativeOverrideClip=RigClipResolver.FindAction(clips,kind);
                    if(args.Contains("--retargeted-motion-preview"))
                    {
                        var name=kind switch{CompanionActionKind.Idle=>"Human_Idle",CompanionActionKind.Waving=>"Human_Greeting",CompanionActionKind.Nodding=>"Human_Nod",CompanionActionKind.Listening=>"Human_Listening",CompanionActionKind.Bowing=>"Human_Bow",CompanionActionKind.RunningRight=>"Human_Walk",CompanionActionKind.Running=>"Human_Run",CompanionActionKind.Jumping=>"Human_Jump",_=>throw new ArgumentException("No retarget preview mapping for "+kind)};
                        nativeOverrideClip=Array.FindIndex(clips,c=>c.Name==name);
                        if(nativeOverrideClip<0)throw new ArgumentException("Missing retargeted clip "+name);
                    }
                    var native = nativeOverrideClip>=0||kind == CompanionActionKind.Running;
                    if (native && runningClip < 0) throw new InvalidOperationException("Missing native Running clip.");
                    var selectedClip=nativeOverrideClip>=0?nativeOverrideClip:native?runningClip:idleClip;
                    var duration = native ? clips[selectedClip].Duration : definition.DurationSeconds;
                    var firstFrame = pet.AnimationFrameCount;
                    var sampledFrames = new List<object>();
                    var samplesAt=Array.IndexOf(args,"--motion-samples");
                    var sampleCount=samplesAt>=0&&samplesAt+1<args.Length?Math.Clamp(int.Parse(args[samplesAt+1]),9,181):9;
                    for (var frame = 0; frame < sampleCount; frame++)
                    {
                        var progress = frame / (double)(sampleCount-1);
                        var actionSeconds = duration * progress;
                        // Do not wrap the native clip's final sample back to frame zero.
                        var clipSeconds = native ? Math.Min(actionSeconds, Math.Max(0, duration - .00001)) : .5;
                        SampleFrame(kind, actionSeconds, clipSeconds);
                        if(args.Contains("--export-motion-poses"))poseFrames.Add(new{action=definition.Id,frame,seconds=actionSeconds,world=GetField<Matrix4x4[]>(sampler,"_world").Select(MatrixValues).ToArray()});
                        var image = Path.Combine(Path.GetDirectoryName(_report)!,
                            $"{Path.GetFileNameWithoutExtension(_report)}.{definition.Id}.frame-{frame:D2}.png");
                        var sideImage = Path.Combine(Path.GetDirectoryName(_report)!,
                            $"{Path.GetFileNameWithoutExtension(_report)}.{definition.Id}.side.frame-{frame:D2}.png");
                        try
                        {
                            await CapturePoseImage(originalYaw,image);
                            await CapturePoseImage(originalYaw+90,sideImage);
                            if(args.Contains("--motion-review-views"))
                            {
                                await CapturePoseImage(originalYaw+45,image.Replace(".png",".three-quarter.png"));
                                await CapturePoseImage(originalYaw+180,image.Replace(".png",".rear.png"));
                            }
                        }
                        finally { modelView.SetPose(originalYaw, scale); }
                        sampledFrames.Add(new { frame, progress, actionSeconds, clipSeconds, frontSha256 = ImageHash(image), sideSha256 = ImageHash(sideImage) });
                    }
                    captures.Add(new { action = definition.Id, sampling = native ? "native-clip" : "procedural-action", durationSeconds = duration,
                        sourceClip = clips[selectedClip].Name, frontYaw = originalYaw, sideYaw = originalYaw + 90,
                        frames = sampleCount, sideFrames = sampleCount, frameCountAdvanced = pet.AnimationFrameCount - firstFrame, sampledFrames });
                }
            }
            if(args.Contains("--export-motion-poses"))File.WriteAllText(Path.ChangeExtension(_report,".poses.json"),JsonSerializer.Serialize(new{source,nodes=poseRig.Nodes.Select((n,i)=>new{name=n.Name,restWorld=MatrixValues(restWorld[i])}),frames=poseFrames}));
            success = true;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            try { pet?.Close(); } catch { }
            if (!preserveProfile) { try { Directory.Delete(temp, true); } catch { } }
            File.WriteAllText(_report, JsonSerializer.Serialize(new
            {
                success,
                finishedAt = DateTimeOffset.Now,
                windows = Environment.OSVersion.VersionString,
                windowShown = true,
                captureMode = "WPF RenderTargetBitmap; deterministic poses, not realtime video",
                captureScale = _captureScale,
                deterministicSampling = true,
                groundGridRequested=args.Contains("--motion-ground-grid"),
                renderTier = RenderCapability.Tier >> 16,
                captures,
                reverseTransition,
                error,
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(success ? 0 : 1);
        }
    }

    private static async Task RunRigUiChecksAsync(Application app,string[] args)
    {
        var temp=Path.Combine(Path.GetTempPath(),"Qianmeng-Rig-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);Native.GetCursorPos(out var originalCursor);MainWindow? pet=null;ControlCenterWindow? center=null;var success=false;var metrics=new List<object>();
        try
        {
            var at=Array.IndexOf(args,"--model-file");var source=Path.GetFullPath(args[at+1]);RigCoreChecks(source,temp,metrics);
            var store=new LocalStore(Path.Combine(temp,"profile"));pet=new MainWindow(store,_=>{},new ChatService(()=>null));pet.Settings.AutoHideFullscreen=false;pet.Left=SystemParameters.WorkArea.Right-310;pet.Top=SystemParameters.WorkArea.Top+20;pet.Show();await Task.Delay(300);
            await pet.ImportPetModelAsync(source);await Task.Delay(600);
            Check("animated-import-real-worker",pet.AnimationNames.Length==2&&pet.IsAnimationPlaying,"Worker stripped rig or player did not start.");
            Check("animated-cache-version",pet.Settings.PetModelFile.EndsWith("-v3.glb"),"Cache version not upgraded.");
            var model=(PetModelView)pet.FindName("PetModel");Check("animated-view-visible",model.IsVisible&&model.Scene is not null,"Animated model hidden.");
            SaveWindowImage(pet,Path.ChangeExtension(_report,".idle.png"));var before=pet.AnimationFrameCount;await Task.Delay(800);Check("idle-ticks-with-cap",pet.AnimationFrameCount>before&&pet.AnimationFrameCount-before<18,"Idle frame cap wrong.");
            pet.OpenModelManager();await Task.Delay(300);center=app.Windows.OfType<ControlCenterWindow>().Single();
            var combo=GetField<ComboBox>(center,"_animationClip");combo.SelectedItem=pet.AnimationNames.First(n=>n.Contains("Running"));
            var play=Descendants<Button>(center).Single(b=>Equals(b.Content,"播放所选动作"));play.BringIntoView();await Task.Delay(100);await FocusWindowAsync(new WindowInteropHelper(center).Handle);await ClickAsync(ElementPoint(play));
            Check("native-play-selected-clip",pet.IsAnimationPlaying&&pet.AnimationSummary.Contains("Running"),"Native play button failed.");
            pet.PreviewAnimation(pet.AnimationNames.First(name=>name.Contains("Idle",StringComparison.OrdinalIgnoreCase)));await Task.Delay(300);SaveWindowImage(pet,Path.ChangeExtension(_report,".idle-before-jump.png"));
            var actionButtons=Descendants<Button>(center).ToArray();
            SaveWindowImage(center,Path.ChangeExtension(_report,".action-center.png"));
            Check("control-center-exposes-action-library",actionButtons.Any(b=>Equals(b.Content,"跳跃"))&&actionButtons.Any(b=>Equals(b.Content,"跳舞"))&&actionButtons.Any(b=>Equals(b.Content,"俏皮")),"The model panel is missing the new action preview controls.");
            Check("control-center-exposes-full-action-library",actionButtons.Count(button=>CompanionActionMotion.Library.Any(definition=>Equals(button.Content,definition.Label)))>=CompanionActionMotion.Library.Count(definition=>definition.Kind is not CompanionActionKind.Idle and not CompanionActionKind.None),"Not every procedural action has a preview button.");
            var jumpButton=actionButtons.Single(b=>Equals(b.Content,"跳跃"));jumpButton.BringIntoView();await Task.Delay(100);await FocusWindowAsync(new WindowInteropHelper(center).Handle);var jumpFramesBefore=pet.AnimationFrameCount;await ClickAsync(ElementPoint(jumpButton));
            await Task.Delay(120);Check("manual-procedural-action-starts",pet.CurrentCompanionAction==CompanionActionKind.Jumping&&pet.IsAnimationPlaying,"Jump preview did not start on the active rig.");
            SaveWindowImage(pet,Path.ChangeExtension(_report,".jump-anticipation.png"));await Task.Delay(220);Check("procedural-action-runs-above-idle-cap",pet.AnimationFrameCount-jumpFramesBefore>=6&&pet.CurrentCompanionAction==CompanionActionKind.Jumping,"A one-shot action remained on the reduced idle cadence.");SaveWindowImage(pet,Path.ChangeExtension(_report,".jump-peak.png"));
            await Task.Delay(650);Check("manual-procedural-action-returns-to-rest",pet.CurrentCompanionAction==CompanionActionKind.None,"One-shot action did not return to idle.");
            var actionCenter=center??throw new InvalidOperationException("The model action panel closed during preview.");
            async Task PreviewAction(string label,CompanionActionKind kind,string suffix)
            {
                var button=actionButtons.Single(candidate=>Equals(candidate.Content,label));button.BringIntoView();await Task.Delay(250);await FocusWindowAsync(new WindowInteropHelper(actionCenter).Handle);
                var clickPoint=ElementPoint(button);await ClickAsync(clickPoint);
                for(var attempt=0;attempt<12&&pet.CurrentCompanionAction!=kind;attempt++)await Task.Delay(50);
                CompanionActionMotion.TryGetDefinition(kind,out var definition);
                var buttonBounds=button.TransformToAncestor(actionCenter).TransformBounds(new Rect(0,0,button.ActualWidth,button.ActualHeight));
                Check($"action-preview-{kind.ToString().ToLowerInvariant()}-starts",pet.CurrentCompanionAction==kind,$"Preview button {label} did not trigger its action; current={pet.CurrentCompanionAction}; enabled={button.IsEnabled}; visible={button.IsVisible}; size={button.ActualWidth:F0}x{button.ActualHeight:F0}; centerPoint={buttonBounds.X:F0},{buttonBounds.Y:F0}; screenPoint={clickPoint.X},{clickPoint.Y}.");
                Check($"action-preview-{kind.ToString().ToLowerInvariant()}-shows-label",pet.AnimationSummary.Contains(definition.Label,StringComparison.Ordinal),$"Action caption did not expose {definition.Label}.");
                var petStatus=pet.FindName("PetStatusText") as TextBlock;
                Check($"action-preview-{kind.ToString().ToLowerInvariant()}-shows-on-pet",petStatus?.Text==definition.Label,$"On-pet status did not expose {definition.Label}; current={petStatus?.Text}.");
                if(kind is CompanionActionKind.Typing or CompanionActionKind.CoffeeBreak or CompanionActionKind.EatingSnack or CompanionActionKind.Beckoning or CompanionActionKind.Hugging or CompanionActionKind.Pointing or CompanionActionKind.ImpatientFootTap or CompanionActionKind.Shrugging or CompanionActionKind.Sneaking or CompanionActionKind.Listening or CompanionActionKind.Cheering or CompanionActionKind.Facepalming or CompanionActionKind.DoubleWaving or CompanionActionKind.SideStepping or CompanionActionKind.AirGuitar or CompanionActionKind.HeartHands or CompanionActionKind.Shivering or CompanionActionKind.ConfidentPose or CompanionActionKind.SendingLove)
                {
                    const int frames=8;var initialWait=(int)(definition.DurationSeconds*100)-120;if(initialWait>0)await Task.Delay(initialWait);var frameStem=Path.ChangeExtension(_report,null)+"."+definition.Id;
                    for(var frame=0;frame<frames;frame++)
                    {
                        if(frame>0)await Task.Delay((int)(definition.DurationSeconds*750/(frames-1)));
                        SaveWindowImage(pet,$"{frameStem}.frame-{frame:D2}.png");
                        if(frame==4)SaveWindowImage(pet,Path.ChangeExtension(_report,suffix));
                    }
                }
                else {var remaining=(int)(definition.DurationSeconds*450)-120;if(remaining>0)await Task.Delay(remaining);SaveWindowImage(pet,Path.ChangeExtension(_report,suffix));}
            }
            await PreviewAction("挥手",CompanionActionKind.Waving,".waving.png");
            await PreviewAction("欢呼",CompanionActionKind.Celebrating,".celebrating.png");
            await PreviewAction("鼓掌",CompanionActionKind.Clapping,".clapping.png");
            await PreviewAction("屈膝礼",CompanionActionKind.Curtsy,".curtsy.png");
            await PreviewAction("点头",CompanionActionKind.Nodding,".nodding.png");
            await PreviewAction("模拟打字",CompanionActionKind.Typing,".typing.png");
            await PreviewAction("举杯示意",CompanionActionKind.CoffeeBreak,".coffee.png");
            await PreviewAction("进食示意",CompanionActionKind.EatingSnack,".snack.png");
            await PreviewAction("放松休息",CompanionActionKind.Resting,".resting.png");
            await PreviewAction("敬礼",CompanionActionKind.Saluting,".saluting.png");
            await PreviewAction("挠头思考",CompanionActionKind.HeadScratching,".head-scratching.png");
            await PreviewAction("摇头否认",CompanionActionKind.ShakingHead,".shaking-head.png");
            await PreviewAction("打哈欠",CompanionActionKind.Yawning,".yawning.png");
            await PreviewAction("睡醒伸展",CompanionActionKind.WakingUp,".waking-up.png");
            await PreviewAction("轻轻摇摆",CompanionActionKind.Swaying,".swaying.png");
            await PreviewAction("踉跄站稳",CompanionActionKind.StumbleRecover,".stumble-recover.png");
            await PreviewAction("举手提问",CompanionActionKind.AskingQuestion,".asking-question.png");
            await PreviewAction("单手胜利",CompanionActionKind.VictoryPose,".victory-pose.png");
            await PreviewAction("蹲下观察",CompanionActionKind.Squatting,".squatting.png");
            await PreviewAction("招手示意",CompanionActionKind.Beckoning,".beckoning.png");
            await PreviewAction("拥抱",CompanionActionKind.Hugging,".hugging.png");
            await PreviewAction("指引方向",CompanionActionKind.Pointing,".pointing.png");
            await PreviewAction("等得跺脚",CompanionActionKind.ImpatientFootTap,".impatient-foot-tap.png");
            await PreviewAction("无奈耸肩",CompanionActionKind.Shrugging,".shrugging.png");
            await PreviewAction("踮步观察",CompanionActionKind.Sneaking,".sneaking.png");
            await PreviewAction("侧耳倾听",CompanionActionKind.Listening,".listening.png");
            await PreviewAction("交替欢呼",CompanionActionKind.Cheering,".cheering.png");
            await PreviewAction("捂脸",CompanionActionKind.Facepalming,".facepalming.png");
            await PreviewAction("双手挥挥",CompanionActionKind.DoubleWaving,".double-waving.png");
            await PreviewAction("左右踏步",CompanionActionKind.SideStepping,".side-stepping.png");
            await PreviewAction("空气吉他",CompanionActionKind.AirGuitar,".air-guitar.png");
            await PreviewAction("捧心",CompanionActionKind.HeartHands,".heart-hands.png");
            await PreviewAction("冷得发抖",CompanionActionKind.Shivering,".shivering.png");
            await PreviewAction("得意叉腰",CompanionActionKind.ConfidentPose,".confident-pose.png");
            await PreviewAction("送出爱心",CompanionActionKind.SendingLove,".sending-love.png");
            pet.PlayCompanionAction(CompanionActionKind.None);
            var runningClip=pet.AnimationNames.First(name=>name.Contains("Running",StringComparison.OrdinalIgnoreCase));pet.PreviewAnimation(runningClip);await Task.Delay(350);Check("running-clip-restored-after-action",pet.AnimationSummary.Contains("Running"),"Running clip was not explicitly restored after preview.");SaveWindowImage(pet,Path.ChangeExtension(_report,".running.png"));
            var proc=Process.GetCurrentProcess();proc.Refresh();var cpu=proc.TotalProcessorTime;var clock=Stopwatch.StartNew();before=pet.AnimationFrameCount;await Task.Delay(4000);proc.Refresh();
            metrics.Add(new{phase="running-with-model-page",previewRendering=GetField<PetModelView>(center,"_modelPreview").Scene is not null,renderedFrames=pet.AnimationFrameCount-before,sampleSeconds=clock.Elapsed.TotalSeconds,meanFps=(pet.AnimationFrameCount-before)/clock.Elapsed.TotalSeconds,oneCoreCpuPercent=(proc.TotalProcessorTime-cpu).TotalMilliseconds/clock.Elapsed.TotalMilliseconds*100,privateMiB=proc.PrivateMemorySize64/1048576d,workingSetMiB=proc.WorkingSet64/1048576d,pet.LastAnimationFrameMilliseconds,pet.WorstAnimationFrameMilliseconds});
            Check("running-advances-smoothly",pet.AnimationFrameCount-before>=50,"Animation cannot sustain 12.5 FPS.");
            var pause=Descendants<Button>(center).Single(b=>Equals(b.Content,"暂停动作"));pause.BringIntoView();await Task.Delay(100);await FocusWindowAsync(new WindowInteropHelper(center).Handle);await ClickAsync(ElementPoint(pause));before=pet.AnimationFrameCount;await Task.Delay(350);var pausedFrames=pet.AnimationFrameCount;Check("native-pause-stops-frames",!pet.IsAnimationPlaying&&pausedFrames==before,$"Pause still consumes frames (playing={pet.IsAnimationPlaying}, before={before}, after={pausedFrames}).");
            pet.PreviewAnimation(combo.SelectedItem as string??"Running");pet.HideToTray();before=pet.AnimationFrameCount;await Task.Delay(400);Check("hidden-stops-animation",!pet.IsAnimationPlaying&&pet.AnimationFrameCount==before,"Hidden model updates.");pet.RestoreFromTray();await Task.Delay(250);Check("show-resumes-animation",pet.IsAnimationPlaying&&pet.AnimationFrameCount>before,"Show did not resume.");
            center.Close();center=null;await Task.Delay(300);
            await SeparateGestureAsync();var point=ElementPoint(model);await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Native.LeftDown();});await Task.Delay(100);
            Check("press-pauses-animation",!pet.IsAnimationPlaying&&Mouse.Captured is not null,"Press does not hold moving hit target; pressed="+GetField<bool>(pet,"_leftPressed")+"; drag="+GetField<bool>(pet,"_dragStarted")+"; hover="+model.IsMouseOver+"; hit="+ModelWindowFromPoint(point)+"; pet="+new WindowInteropHelper(pet).Handle);await InputAsync(Native.LeftUp);await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(200);Check("release-resumes-animation",pet.IsAnimationPlaying&&Mouse.Captured is null,"Release broke playback.");
            proc.Refresh();cpu=proc.TotalProcessorTime;clock.Restart();before=pet.AnimationFrameCount;
            await Task.Delay(6000);proc.Refresh();
            metrics.Add(new{phase="running-pet-only",renderedFrames=pet.AnimationFrameCount-before,sampleSeconds=clock.Elapsed.TotalSeconds,meanFps=(pet.AnimationFrameCount-before)/clock.Elapsed.TotalSeconds,oneCoreCpuPercent=(proc.TotalProcessorTime-cpu).TotalMilliseconds/clock.Elapsed.TotalMilliseconds*100,privateMiB=proc.PrivateMemorySize64/1048576d,workingSetMiB=proc.WorkingSet64/1048576d});
            Check("pet-only-running-near-30fps",pet.AnimationFrameCount-before>=150,"Solo running missed 25 FPS.");
            var old=Bounds(pet);await SeparateGestureAsync();point=ElementPoint(model);await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Native.LeftDown();Thread.Sleep(100);for(var i=1;i<=6;i++){Native.SetCursorPos(point.X-i*15,point.Y+i*8);Thread.Sleep(60);}Native.LeftUp();});await Task.Delay(300);
            Check("animated-native-drag",Math.Abs(Bounds(pet).Left-old.Left)>30&&Mouse.Captured is null,"Moving character cannot be dragged.");
            await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(100);
            pet.FollowActivityAnimations();pet.CareAction(p=>PetCareService.Start(p,"exercise",DateTimeOffset.UtcNow));Check("activity-maps-running",pet.AnimationSummary.Contains("Running"),"Exercise not mapped.");pet.CareAction(PetCareService.Cancel);Check("activity-returns-idle",pet.AnimationSummary.Contains("Idle"),"Activity stop did not return idle.");
            for(var i=0;i<4;i++){await pet.ImportPetModelAsync(source);await Task.Delay(100);}Check("animated-repeat-import-retains-clips",pet.AnimationNames.Length==2&&pet.IsAnimationPlaying,"Repeat import broke animation.");
            pet.PauseAnimation();await Task.Delay(1500);proc.Refresh();var idleCpu=proc.TotalProcessorTime;var idleClock=Stopwatch.StartNew();await Task.Delay(6000);proc.Refresh();metrics.Add(new{phase="paused-no-preview",oneCoreCpuPercent=(proc.TotalProcessorTime-idleCpu).TotalMilliseconds/idleClock.Elapsed.TotalMilliseconds*100,privateMiB=proc.PrivateMemorySize64/1048576d});
            var cacheName=pet.Settings.PetModelFile;CloseModelPet(pet);pet=new MainWindow(new LocalStore(store.DataDirectory),_=>{},new ChatService(()=>null));pet.Settings.AutoHideFullscreen=false;pet.Show();for(var i=0;i<80&&(pet.AnimationNames.Length==0||pet.IsModelLoading);i++)await Task.Delay(100);
            Check("restart-restores-rig-and-pause",pet.AnimationNames.Length==2&&!pet.IsAnimationPlaying&&pet.Settings.PetModelFile==cacheName,"Restart lost animation or pause preference.");
            pet.ResetPetModel();Check("reset-stops-and-releases-player",pet.AnimationNames.Length==0&&!pet.IsAnimationPlaying,"Reset left player ticking.");success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            try{Native.LeftUp();Mouse.Capture(null);Native.SetCursorPos(originalCursor.X,originalCursor.Y);center?.Close();if(pet is not null)CloseModelPet(pet);}catch{}try{Directory.Delete(temp,true);}catch{}
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,finishedAt=DateTimeOffset.Now,windows=Environment.OSVersion.VersionString,checks=Results,metrics},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown(success?0:1);
        }
    }
}
