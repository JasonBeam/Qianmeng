using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static void RunActionSupportChecks(Application app,string[] args)
    {
        var metrics=new List<object>();var success=false;
        try
        {
            var at=Array.IndexOf(args,"--model-file");
            if(at<0||at+1>=args.Length)throw new ArgumentException("--model-file required");
            var asset=GlbReader.Load(Path.GetFullPath(args[at+1]));var rig=asset.Rig!;
            var feet=new List<(int Mesh,int Vertex)>();
            for(var m=0;m<rig.Meshes.Length;m++)
            {
                var binding=rig.Meshes[m];if(binding.Skin<0)continue;
                for(var v=0;v<binding.Joints.Length;v++)
                {
                    var sum=0f;var weight=binding.Weights[v];
                    for(var k=0;k<4;k++)
                    {
                        var name=rig.Nodes[rig.Skins[binding.Skin].Joints[binding.Joints[v][k]]].Name;
                        if(name is "mixamorig:LeftFoot" or "mixamorig:LeftToeBase" or "mixamorig:RightFoot" or "mixamorig:RightToeBase")
                            sum+=k switch{0=>weight.X,1=>weight.Y,2=>weight.Z,_=>weight.W};
                    }
                    if(sum>=.4f)feet.Add((m,v));
                }
            }
            Check("support-fixture-has-real-shoe-vertices",feet.Count>0,"Model lacks identifiable shoe geometry.");
            var floor=feet.Min(v=>asset.Meshes[v.Mesh].Positions[v.Vertex].Y);
            float Clearance(RigSampler sampler)=>feet.Min(v=>sampler.Positions[v.Mesh][v.Vertex].Y)-floor;
            var idle=RigClipResolver.FindIdle(rig.Clips);var sampler=new RigSampler(asset);var reference=new RigSampler(asset);
            var hips=Array.FindIndex(rig.Nodes,n=>n.Name=="mixamorig:Hips");
            reference.Sample(idle,.5,updateNormals:false);
            var baselineHip=GetField<Matrix4x4[]>(reference,"_world")[hips].M42;
            var timer=Stopwatch.StartNew();
            foreach(var definition in CompanionActionMotion.Library)
            {
                var authored=RigClipResolver.FindAction(rig.Clips,definition.Kind);
                var clip=authored>=0?authored:idle;
                var rate=RigClipResolver.PlaybackRate(rig.Clips[clip]);
                var duration=authored>=0?rig.Clips[clip].Duration/rate:definition.DurationSeconds;
                var samples=Math.Max(2,(int)Math.Ceiling(duration*120)+1);
                var minimum=float.PositiveInfinity;var maximum=float.NegativeInfinity;var correction=0f;var referenceError=0f;
                var minimumHipOffset=float.PositiveInfinity;var maximumHipOffset=float.NegativeInfinity;
                for(var frame=0;frame<samples;frame++)
                {
                    var seconds=duration*frame/(samples-1);
                    var clipTime=authored>=0?Math.Min(seconds*rate,rig.Clips[clip].Duration-.00001):.5;
                    sampler.PoseTransitionProgress=1;
                    sampler.CompanionAction=authored>=0?default:CompanionActionMotion.Sample(definition.Kind,seconds);
                    sampler.Sample(clip,clipTime,updateNormals:false);
                    var clearance=Clearance(sampler);minimum=Math.Min(minimum,clearance);maximum=Math.Max(maximum,clearance);
                    correction=Math.Max(correction,sampler.SoleSupportCorrectionMeters);
                    var hipOffset=GetField<Matrix4x4[]>(sampler,"_world")[hips].M42-baselineHip;
                    minimumHipOffset=Math.Min(minimumHipOffset,hipOffset);maximumHipOffset=Math.Max(maximumHipOffset,hipOffset);
                    if(authored>=0&&frame%20==0)
                    {
                        reference.Sample(clip,clipTime,updateNormals:false);
                        for(var m=0;m<asset.Meshes.Length;m++)for(var v=0;v<asset.Meshes[m].Positions.Length;v++)
                            referenceError=Math.Max(referenceError,Vector3.Distance(sampler.Positions[m][v],reference.Positions[m][v]));
                    }
                }
                Check("actual-sole-120hz-"+definition.Id,minimum>=-.005f,"Shoe penetrated fixed bind floor: "+minimum);
                if(authored>=0)Check("native-unmodified-"+definition.Id,correction==0&&referenceError<.000001f,"Support changed an authored native pose.");
                if(definition.Kind is CompanionActionKind.Curtsy or CompanionActionKind.Squatting or CompanionActionKind.Sneaking)
                {
                    var requiredDescent=definition.Kind==CompanionActionKind.Sneaking?.010f:.015f;
                    Check("actual-crouch-descends-"+definition.Id,minimumHipOffset< -requiredDescent,"Sole support cancelled the intended crouch: "+minimumHipOffset);
                }
                metrics.Add(new{action=definition.Id,samples,minimumClearance=minimum,maximumClearance=maximum,maxRootCorrection=correction,minimumHipOffset,maximumHipOffset,referenceError});
            }
            foreach(var kind in new[]{CompanionActionKind.Curtsy,CompanionActionKind.Squatting,CompanionActionKind.Sneaking,CompanionActionKind.Dancing})
            foreach(var target in new[]{idle,RigClipResolver.FindRunning(rig.Clips)})
            {
                var transition=new RigSampler(asset);CompanionActionMotion.TryGetDefinition(kind,out var definition);
                transition.CompanionAction=CompanionActionMotion.Sample(kind,definition.DurationSeconds*.625);
                transition.Sample(idle,.5,updateNormals:false);transition.BeginPoseTransition();transition.CompanionAction=default;
                var minimum=float.PositiveInfinity;
                for(var frame=0;frame<=30;frame++)
                {
                    transition.PoseTransitionProgress=frame/30f;
                    transition.Sample(target,.4+frame/120d,updateNormals:false);
                    minimum=Math.Min(minimum,Clearance(transition));
                }
                Check("snapshot-exit-fixed-floor-"+kind+"-"+rig.Clips[target].Name,minimum>=-.005f,"Snapshot exit penetrated floor: "+minimum);
                metrics.Add(new{transitionFrom=kind.ToString(),target=rig.Clips[target].Name,samples=31,minimumClearance=minimum});
            }
            metrics.Add(new{elapsedSeconds=timer.Elapsed.TotalSeconds,shoeVertices=feet.Count,sampleRateHz=120});
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="dense-runtime-sampler-real-mesh-floor-and-snapshot-no-window-not-installed",checks=Results,metrics},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
}
