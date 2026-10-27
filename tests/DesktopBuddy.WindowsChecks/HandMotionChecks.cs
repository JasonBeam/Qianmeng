using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static void RunHandMotionChecks(Application app,string[] args)
    {
        var success=false;var metrics=new List<object>();
        try
        {
            var asset=GlbReader.Load(Path.GetFullPath(args[Array.IndexOf(args,"--model-file")+1]));var rig=asset.Rig!;
            int Node(string name)=>Array.FindIndex(rig.Nodes,n=>n.Name=="mixamorig:"+name);
            var handVertices=new List<(int Mesh,int Vertex)>[]{[],[]};
            for(var m=0;m<rig.Meshes.Length;m++)
            {
                var binding=rig.Meshes[m];if(binding.Skin<0)continue;
                // Retained original buffers may contain unused vertices after a local mesh repair.
                // Only the indexed visible surface is an anatomical workspace.
                var referenced=asset.Meshes[m].Indices.ToHashSet();
                for(var v=0;v<binding.Joints.Length;v++)for(var side=0;side<2;side++)
                {
                    if(!referenced.Contains(v))continue;
                    var sum=0f;var prefix=side==0?"mixamorig:LeftHand":"mixamorig:RightHand";
                    for(var k=0;k<4;k++)
                        if(rig.Nodes[rig.Skins[binding.Skin].Joints[binding.Joints[v][k]]].Name.StartsWith(prefix,StringComparison.Ordinal))
                            sum+=k switch{0=>binding.Weights[v].X,1=>binding.Weights[v].Y,2=>binding.Weights[v].Z,_=>binding.Weights[v].W};
                    if(sum>=.65f)handVertices[side].Add((m,v));
                }
            }
            Check("hand-fixture-has-both-skinned-hands",handVertices.All(h=>h.Count>20),"Missing hand geometry.");
            var timeAt=Array.IndexOf(args,"--hand-idle-time");var background=timeAt>=0?double.Parse(args[timeAt+1],System.Globalization.CultureInfo.InvariantCulture):.5;
            var sampler=new RigSampler(asset);var idle=RigClipResolver.FindIdle(rig.Clips);sampler.Sample(idle,background,updateNormals:false);
            var baseline=(Matrix4x4[])GetField<Matrix4x4[]>(sampler,"_world").Clone();
            var baselineGeometry=sampler.Positions.Select(p=>(Vector3[])p.Clone()).ToArray();
            var maxNativeError=0f;var maxResetRotation=0f;var maxResetVertex=0f;
            var failures=new List<string>();
            void HandCheck(string name,bool passed,string detail)
            {Results.Add(new{name,passed,detail=passed?"":detail});if(!passed)failures.Add(name+": "+detail);}
            var actionsAt=Array.IndexOf(args,"--hand-actions");
            var kinds=actionsAt>=0?args[actionsAt+1].Split(',').Select(id=>CompanionActionMotion.Library.Single(d=>d.Id==id).Kind).ToArray():
                new[]{CompanionActionKind.CoffeeBreak,CompanionActionKind.EatingSnack,CompanionActionKind.Thinking,CompanionActionKind.Typing};
            foreach(var kind in kinds)
            {
                var definition=CompanionActionMotion.Library.Single(d=>d.Kind==kind);var count=(int)Math.Ceiling(definition.DurationSeconds*120)+1;
                var maxWrist=0f;var maxWholeWrist=0f;var maxWristPhase=0d;var maxWristSide="";var maxWristAxis=Vector3.Zero;var maxPalmAxis=Vector3.Zero;var minGap=float.PositiveInfinity;var maxStep=0f;var finite=true;var minPip=float.PositiveInfinity;var maxPip=float.NegativeInfinity;
                Matrix4x4[]? previous=null;var peakKnuckleAboveHead=float.NegativeInfinity;var stepBone="";var stepPhase=0d;
                for(var frame=0;frame<count;frame++)
                {
                    var seconds=definition.DurationSeconds*frame/(count-1);var pose=CompanionActionMotion.Sample(kind,seconds);
                    sampler.CompanionAction=pose;sampler.Sample(idle,background,updateNormals:false);
                    var world=GetField<Matrix4x4[]>(sampler,"_world");Vector3 P(string name)=>new(world[Node(name)].M41,world[Node(name)].M42,world[Node(name)].M43);
                    finite&=sampler.Positions.All(mesh=>mesh.All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
                    var phase=seconds/definition.DurationSeconds;
                    foreach(var side in kind is CompanionActionKind.Typing or CompanionActionKind.DoubleWaving or CompanionActionKind.Hugging or CompanionActionKind.ConfidentPose?new[]{"Left","Right"}:new[]{"Right"})
                    {
                        var axis=Vector3.Normalize(P(side+"Hand")-P(side+"ForeArm"));var palm=Vector3.Normalize(P(side+"HandMiddle1")-P(side+"Hand"));
                        var wrist=MathF.Acos(Math.Clamp(Vector3.Dot(axis,palm),-1,1))*180/MathF.PI;
                        if(wrist>maxWholeWrist){maxWholeWrist=wrist;maxWristPhase=phase;maxWristSide=side;maxWristAxis=axis;maxPalmAxis=palm;}
                    }
                    if(phase>=.15&&phase<=.79)
                    {
                        foreach(var side in kind==CompanionActionKind.Typing?new[]{"Left","Right"}:new[]{"Right"})
                        {
                            var axis=Vector3.Normalize(P(side+"Hand")-P(side+"ForeArm"));var palm=Vector3.Normalize(P(side+"HandMiddle1")-P(side+"Hand"));
                            maxWrist=Math.Max(maxWrist,MathF.Acos(Math.Clamp(Vector3.Dot(axis,palm),-1,1))*180/MathF.PI);
                        }
                        if(kind==CompanionActionKind.Typing)
                        {
                            var lateral=Vector3.Normalize(P("LeftArm")-P("RightArm"));
                            var gap=handVertices[0].Min(v=>Vector3.Dot(sampler.Positions[v.Mesh][v.Vertex],lateral))-handVertices[1].Max(v=>Vector3.Dot(sampler.Positions[v.Mesh][v.Vertex],lateral));
                            minGap=Math.Min(minGap,gap);
                            var first=Vector3.Normalize(P("RightHandIndex2")-P("RightHandIndex1"));var second=Vector3.Normalize(P("RightHandIndex3")-P("RightHandIndex2"));
                            var pip=MathF.Acos(Math.Clamp(Vector3.Dot(first,second),-1,1))*180/MathF.PI;minPip=Math.Min(minPip,pip);maxPip=Math.Max(maxPip,pip);
                        }
                        else peakKnuckleAboveHead=Math.Max(peakKnuckleAboveHead,P("RightHandMiddle1").Y-P("Head").Y);
                    }
                    if(previous is not null)
                    {
                        var bones=new[]{"LeftShoulder","RightShoulder","LeftArm","RightArm","LeftForeArm","RightForeArm","LeftHand","RightHand"}
                            .Concat(new[]{"Left","Right"}.SelectMany(side=>new[]{"Index","Middle","Ring","Pinky","Thumb"}.SelectMany(finger=>Enumerable.Range(1,3).Select(joint=>side+"Hand"+finger+joint))));
                        foreach(var name in bones)
                        {
                            Matrix4x4.Decompose(previous[Node(name)],out _,out var a,out _);Matrix4x4.Decompose(world[Node(name)],out _,out var b,out _);
                            var step=2*MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a,b)),0,1))*180/MathF.PI;
                            if(step>maxStep){maxStep=step;stepBone=name;stepPhase=phase;}
                        }
                    }
                    previous=(Matrix4x4[])world.Clone();
                }
                metrics.Add(new{action=definition.Id,samples=count,maxWristDegrees=maxWrist,maxWholeMotionWristDegrees=maxWholeWrist,maxWristPhase,maxWristSide,
                    wristForearmAxis=new[]{maxWristAxis.X,maxWristAxis.Y,maxWristAxis.Z},wristPalmAxis=new[]{maxPalmAxis.X,maxPalmAxis.Y,maxPalmAxis.Z},maxFrameRotationDegrees=maxStep,stepBone,stepPhase,
                    minimumHandWorkspaceGap=kind==CompanionActionKind.Typing?(float?)minGap:null,indexPipRangeDegrees=kind==CompanionActionKind.Typing?(float?)(maxPip-minPip):null,
                    maximumKnuckleHeightRelativeHead=kind!=CompanionActionKind.Typing?(float?)peakKnuckleAboveHead:null});
                HandCheck("hands-finite-"+definition.Id,finite,"Nonfinite skinned position.");
                HandCheck("hands-no-one-frame-roll-flip-"+definition.Id,maxStep<12,"Large bone rotation in1/120s: "+maxStep);
                HandCheck("hands-working-wrist-range-"+definition.Id,maxWrist<(kind==CompanionActionKind.Typing?25:55),"Hand axis bends sharply relative to forearm: "+maxWrist);
                HandCheck("hands-whole-motion-wrist-range-"+definition.Id,maxWholeWrist<55,"Entry/exit also must avoid sharp wrist bends: "+maxWholeWrist);
                if(kind==CompanionActionKind.Typing)
                {
                    HandCheck("typing-separated-actual-hand-workspaces",minGap>.02f,"Actual hand geometry converges/overlaps center workspaces: "+minGap);
                    HandCheck("typing-index-bone-articulation",maxPip-minPip>3,"Index finger bone does not articulate independently; skin visibility is reviewed separately.");
                }
                else if(kind is CompanionActionKind.CoffeeBreak or CompanionActionKind.EatingSnack or CompanionActionKind.Thinking)
                    HandCheck("hand-knuckle-below-head-reference-"+definition.Id,peakKnuckleAboveHead<-.035f,"Knuckle rises above the fixed-character head reference allowance; not a face collision test.");
                sampler.CompanionAction=default;sampler.Sample(idle,background,updateNormals:false);var reset=GetField<Matrix4x4[]>(sampler,"_world");
                for(var node=0;node<rig.Nodes.Length;node++)
                {
                    maxNativeError=Math.Max(maxNativeError,Vector3.Distance(new(reset[node].M41,reset[node].M42,reset[node].M43),new(baseline[node].M41,baseline[node].M42,baseline[node].M43)));
                    var a=reset[node];var b=baseline[node];
                    maxResetRotation=Math.Max(maxResetRotation,new[]{a.M11-b.M11,a.M12-b.M12,a.M13-b.M13,a.M21-b.M21,a.M22-b.M22,a.M23-b.M23,a.M31-b.M31,a.M32-b.M32,a.M33-b.M33}.Max(v=>Math.Abs(v)));
                }
                for(var mesh=0;mesh<baselineGeometry.Length;mesh++)for(var vertex=0;vertex<baselineGeometry[mesh].Length;vertex++)
                    maxResetVertex=Math.Max(maxResetVertex,Vector3.Distance(sampler.Positions[mesh][vertex],baselineGeometry[mesh][vertex]));
            }
            HandCheck("hand-actions-reset-without-joint-drift",maxNativeError<1e-6f,"Action did not reset to original native joint positions.");
            HandCheck("hand-actions-reset-leaf-rotations",maxResetRotation<1e-6f,"Hand/leaf bone rotation did not reset.");
            HandCheck("hand-actions-reset-skinned-geometry",maxResetVertex<1e-6f,"Skinned geometry did not reset.");
            if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="120Hz-real-mesh-hand-kinematics-not-visual-collision-or-resource-acceptance",checks=Results,metrics},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
}
