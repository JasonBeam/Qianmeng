using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static void RunAuthoredUpperBodyChecks(Application app,string[] args)
    {
        var success=false;var metrics=new List<object>();
        try
        {
            var path=Path.GetFullPath(args[Array.IndexOf(args,"--model-file")+1]);var bytes=File.ReadAllBytes(path);
            var asset=GlbReader.Load(bytes);var rig=asset.Rig!;var idle=RigClipResolver.FindIdle(rig.Clips);
            Check("authored-upper-body-ten-clips-load",rig.Clips.Length==10,"Expected original eight plus two native gestures.");
            var n=BitConverter.ToInt32(bytes,12);var source=JsonNode.Parse(bytes.AsSpan(20,n))!;
            var binAt=20+n;var bin=bytes.AsSpan(binAt+8,BitConverter.ToInt32(bytes,binAt)).ToArray();
            byte[] Encode(JsonNode doc,byte[] data)
            {
                doc["buffers"]![0]!["byteLength"]=data.Length;var json=Encoding.UTF8.GetBytes(doc.ToJsonString());
                using var memory=new MemoryStream();using var writer=new BinaryWriter(memory);
                var jl=(json.Length+3)&~3;var bl=(data.Length+3)&~3;
                writer.Write(0x46546c67);writer.Write(2);writer.Write(28+jl+bl);writer.Write(jl);writer.Write(0x4e4f534a);writer.Write(json);
                for(var i=json.Length;i<jl;i++)writer.Write((byte)32);
                writer.Write(bl);writer.Write(0x004e4942);writer.Write(data);for(var i=data.Length;i<bl;i++)writer.Write((byte)0);
                return memory.ToArray();
            }
            var slots=source.DeepClone();var animations=(JsonArray)slots["animations"]!;
            while(animations.Count<16){var clone=animations[0]!.DeepClone();clone["name"]="CapacitySlot"+animations.Count;animations.Add(clone);}
            Check("sixteen-clips-within-key-budget-load",GlbReader.Load(Encode(slots,bin)).Rig!.Clips.Length==16,"Bounded new clip capacity failed.");
            animations.Add(animations[0]!.DeepClone());var rejected=false;
            try{GlbReader.Load(Encode(slots,bin));}catch(ModelImportException ex){rejected=ex.Message.Contains("16");}
            Check("seventeen-clips-still-rejected",rejected,"Clip count is no longer bounded.");
            var oversized=source.DeepClone();using var data=new MemoryStream();data.Write(bin);using var bw=new BinaryWriter(data);
            int AddAccessor(int count,string type,Action write)
            {
                while(data.Length%4!=0)bw.Write((byte)0);var start=data.Length;write();var views=(JsonArray)oversized["bufferViews"]!;var view=views.Count;
                views.Add(new JsonObject{["buffer"]=0,["byteOffset"]=start,["byteLength"]=data.Length-start});var accessors=(JsonArray)oversized["accessors"]!;var index=accessors.Count;
                accessors.Add(new JsonObject{["bufferView"]=view,["componentType"]=5126,["count"]=count,["type"]=type});return index;
            }
            const int tooMany=100001;var input=AddAccessor(tooMany,"SCALAR",()=>{for(var k=0;k<tooMany;k++)bw.Write(k/1000f);});
            var output=AddAccessor(tooMany,"VEC4",()=>{for(var k=0;k<tooMany;k++){bw.Write(0f);bw.Write(0f);bw.Write(0f);bw.Write(1f);}});
            oversized["animations"]=JsonNode.Parse($"[{{\"name\":\"KeyBudgetProbe\",\"samplers\":[{{\"input\":{input},\"output\":{output}}}],\"channels\":[{{\"sampler\":0,\"target\":{{\"node\":0,\"path\":\"rotation\"}}}}]}}]");
            rejected=false;try{GlbReader.Load(Encode(oversized,data.ToArray()));}catch(ModelImportException ex){rejected=ex.Message.Contains("10 万");}
            Check("shared-100000-key-budget-unchanged",rejected,"Animation key budget was widened.");
            var exact=oversized.DeepClone();
            exact["accessors"]![input]!["count"]=100000;exact["accessors"]![output]!["count"]=100000;
            Check("exact-100000-key-budget-load",GlbReader.Load(Encode(exact,data.ToArray())).Rig!.Clips.Length==1,"Exact boundary should load.");
            var cumulative=exact.DeepClone();
            cumulative["accessors"]![input]!["count"]=50000;cumulative["accessors"]![output]!["count"]=50000;
            var shared=(JsonArray)cumulative["animations"]!;shared.Add(shared[0]!.DeepClone());
            Check("shared-input-cross-clip-100000-load",GlbReader.Load(Encode(cumulative,data.ToArray())).Rig!.Clips.Length==2,"Shared inputs still count once per channel.");
            shared.Add(shared[0]!.DeepClone());rejected=false;
            try{GlbReader.Load(Encode(cumulative,data.ToArray()));}catch(ModelImportException ex){rejected=ex.Message.Contains("10 万");}
            Check("shared-input-cross-clip-budget-rejected",rejected,"Shared input must not bypass cumulative key budget.");
            var sampler=new RigSampler(asset);var baseline=new RigSampler(asset);
            int Node(string name)=>Array.FindIndex(rig.Nodes,x=>x.Name=="mixamorig:"+name);
            var failures=new List<string>();
            void Gate(string name,bool passed,string detail){Results.Add(new{name,passed,detail=passed?"":detail});if(!passed)failures.Add(name+": "+detail);}
            foreach(var (action,role) in new[]{(CompanionActionKind.HeadScratching,"Human_HeadScratch"),(CompanionActionKind.DoubleWaving,"Human_DoubleWave")})
            {
                var clip=RigClipResolver.FindAction(rig.Clips,action);Gate("authored-role-"+role,clip>=0&&rig.Clips[clip].Name==role,"Native role not selected.");if(clip<0)continue;
                var count=(int)Math.Ceiling(rig.Clips[clip].Duration*120)+1;Matrix4x4[]? previous=null;var maxStep=0f;var maxStepNode="";var maxStepPhase=0d;var maxWrist=0f;var footError=0f;var finite=true;
                var leftElbows=new List<Vector3>();var rightElbows=new List<Vector3>();
                for(var frame=0;frame<count;frame++)
                {
                    var time=rig.Clips[clip].Duration*frame/(count-1d);sampler.Sample(clip,time,updateNormals:false);baseline.Sample(idle,time%rig.Clips[idle].Duration,updateNormals:false);
                    var world=GetField<Matrix4x4[]>(sampler,"_world");var sourceWorld=GetField<Matrix4x4[]>(baseline,"_world");
                    Vector3 P(string name)=>new(world[Node(name)].M41,world[Node(name)].M42,world[Node(name)].M43);
                    finite&=sampler.Positions.All(m=>m.All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
                    foreach(var side in new[]{"Left","Right"})
                    {
                        var foot=Node(side+"Foot");footError=Math.Max(footError,Vector3.Distance(new(world[foot].M41,world[foot].M42,world[foot].M43),new(sourceWorld[foot].M41,sourceWorld[foot].M42,sourceWorld[foot].M43)));
                        var arm=Vector3.Normalize(P(side+"Hand")-P(side+"ForeArm"));var palm=Vector3.Normalize(P(side+"HandMiddle1")-P(side+"Hand"));
                        maxWrist=Math.Max(maxWrist,MathF.Acos(Math.Clamp(Vector3.Dot(arm,palm),-1,1))*180/MathF.PI);
                    }
                    if(time/rig.Clips[clip].Duration is >.25 and <.75){leftElbows.Add(P("LeftForeArm"));rightElbows.Add(P("RightForeArm"));}
                    if(previous is not null)for(var i=0;i<world.Length;i++)
                    {Matrix4x4.Decompose(previous[i],out _,out var a,out _);Matrix4x4.Decompose(world[i],out _,out var b,out _);var step=2*MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a,b)),0,1))*180/MathF.PI;if(step>maxStep){maxStep=step;maxStepNode=rig.Nodes[i].Name;maxStepPhase=time/rig.Clips[clip].Duration;}}
                    previous=(Matrix4x4[])world.Clone();
                }
                float Span(List<Vector3> points)=>points.Max(p=>Vector3.Distance(p,points[0]));
                metrics.Add(new{role,samples=count,maxStepDegrees=maxStep,maxStepNode,maxStepPhase,maxWristDegrees=maxWrist,maxIdleFootErrorMeters=footError,leftMidElbowTravel=Span(leftElbows),rightMidElbowTravel=Span(rightElbows)});
                Gate("native-finite-"+role,finite,"Nonfinite skinned position.");Gate("native-continuity-"+role,maxStep<12,"Large 120Hz bone step: "+maxStep);
                Gate("native-wrist-"+role,maxWrist<55,"Wrist exceeds existing guard: "+maxWrist);Gate("native-idle-feet-preserved-"+role,footError<.00001f,"Idle foot position changed: "+footError);
            }
            if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally{File.WriteAllText(_report,JsonSerializer.Serialize(new{success,checks=Results,metrics,scope="native FK continuity/capacity/idle-foot checks; not visual/collision/resource acceptance"},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown(success?0:1);}
    }
}
