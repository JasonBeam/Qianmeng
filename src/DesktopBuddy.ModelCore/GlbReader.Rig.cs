using System.Numerics;
using System.Text.Json;

namespace DesktopBuddy.ModelCore;

public sealed partial class GlbReader
{
    private RigNode[]? _rigNodes;
    private int[] _rigOrder=[];
    private RigSkin[] _rigSkins=[];
    private readonly List<RigMesh> _rigMeshes=[];
    private RigClip[] _rigClips=[];
    private void ReadRig()
    {
        var animated=_root.TryGetProperty("animations",out var anims)&&anims.GetArrayLength()>0;
        var skinned=_root.TryGetProperty("skins",out var skins)&&skins.GetArrayLength()>0;
        if(!animated&&!skinned)return;
        var nodes=_root.GetProperty("nodes");var n=nodes.GetArrayLength();if(n<1||n>256)throw Bad("动画模型最多支持 256 个节点。");
        var parents=Enumerable.Repeat(-1,n).ToArray();
        for(var i=0;i<n;i++)if(nodes[i].TryGetProperty("children",out var children))foreach(var c in children.EnumerateArray())
        {var child=c.GetInt32();if(child<0||child>=n||parents[child]!=-1)throw Bad("动画节点索引或父子关系无效。");parents[child]=i;}
        _rigNodes=new RigNode[n];
        for(var i=0;i<n;i++)
        {
            var node=nodes[i];var local=Matrix4x4.Identity;
            if(node.TryGetProperty("matrix",out _))
            {var a=Floats(node,"matrix",new float[16]);local=new(a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7],a[8],a[9],a[10],a[11],a[12],a[13],a[14],a[15]);}
            else
            {var t=Floats(node,"translation",[0,0,0]);var r=Floats(node,"rotation",[0,0,0,1]);var s=Floats(node,"scale",[1,1,1]);var q=new Quaternion(r[0],r[1],r[2],r[3]);if(q.LengthSquared()<1e-10)throw Bad("动画旋转无效。");local=Matrix4x4.CreateScale(s[0],s[1],s[2])*Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q))*Matrix4x4.CreateTranslation(t[0],t[1],t[2]);}
            if(!Matrix4x4.Decompose(local,out var scale,out var rotation,out var translation)||!Finite(scale)||!Finite(translation)||MathF.Abs(scale.X*scale.Y*scale.Z)<1e-12)throw Bad("动画节点变换不可分解或零缩放。");
            _rigNodes[i]=new(node.TryGetProperty("name",out var name)?(name.GetString()??"node")[..Math.Min(64,(name.GetString()??"node").Length)]:"node",parents[i],new(translation,rotation,scale));
        }
        var order=new List<int>();var colors=new byte[n];
        void VisitParent(int i,int depth)
        {if(depth>64||colors[i]==1)throw Bad("骨骼层级存在循环或过深。");if(colors[i]==2)return;colors[i]=1;if(parents[i]>=0)VisitParent(parents[i],depth+1);colors[i]=2;order.Add(i);}
        for(var i=0;i<n;i++)VisitParent(i,0);_rigOrder=order.ToArray();
        if(skinned)
        {
            if(skins.GetArrayLength()>16)throw Bad("动画模型蒙皮数量过多。");var result=new List<RigSkin>();
            foreach(var skin in skins.EnumerateArray())
            {
                var joints=skin.GetProperty("joints").EnumerateArray().Select(x=>x.GetInt32()).ToArray();if(joints.Length<1||joints.Length>128||joints.Any(j=>j<0||j>=n)||joints.Distinct().Count()!=joints.Length)throw Bad("骨骼关节索引无效或超过 128 根。");
                var bind=Enumerable.Repeat(Matrix4x4.Identity,joints.Length).ToArray();
                if(skin.TryGetProperty("inverseBindMatrices",out var index))
                {
                    var a=Access(index.GetInt32(),"MAT4");if(a.Component!=5126||a.Count!=joints.Length)throw Bad("骨骼绑定矩阵数量无效。");
                    for(var i=0;i<a.Count;i++)
                    {var v=Enumerable.Range(0,16).Select(c=>a.Float(i,c)).ToArray();if(v.Any(x=>!float.IsFinite(x)))throw Bad("骨骼绑定矩阵非有限值。");bind[i]=new(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);if(!Matrix4x4.Invert(bind[i],out _))throw Bad("骨骼绑定矩阵不可逆。");}
                }
                result.Add(new(joints,bind));
            }
            _rigSkins=result.ToArray();
        }
        if(!animated)return;
        // Clip slots are feature capacity; the shared 100000-key budget still bounds memory.
        if(anims.GetArrayLength()>16)throw Bad("最多支持 16 段动画。");var clips=new List<RigClip>();long totalKeys=0;
        foreach(var animation in anims.EnumerateArray())
        {
            var tracks=new List<RigTrack>();var samplers=animation.GetProperty("samplers");var targets=new HashSet<(int,string)>();var duration=0f;
            foreach(var channel in animation.GetProperty("channels").EnumerateArray())
            {
                var target=channel.GetProperty("target");var node=target.GetProperty("node").GetInt32();var path=target.GetProperty("path").GetString()??"";
                if(node<0||node>=n||path is not ("translation" or "rotation" or "scale")||!targets.Add((node,path)))throw Bad("动画目标或通道无效，不支持形态键动画。");
                var sampler=At(samplers,channel.GetProperty("sampler").GetInt32());var interpolation=sampler.TryGetProperty("interpolation",out var ip)?ip.GetString():"LINEAR";
                if(interpolation is not ("LINEAR" or "STEP"))throw Bad("请先将动画烘焙为 LINEAR 或 STEP 采样。");
                var input=Access(sampler.GetProperty("input").GetInt32(),"SCALAR");var output=Access(sampler.GetProperty("output").GetInt32(),path=="rotation"?"VEC4":"VEC3");
                totalKeys+=input.Count;if(input.Component!=5126||!(output.Component==5126||path=="rotation"&&output.Normalized&&output.Component is 5120 or 5122)||input.Count<1||input.Count!=output.Count||totalKeys>100000)throw Bad("动画采样格式无效或超过 10 万关键帧。");
                var times=new float[input.Count];var values=new Vector4[input.Count];
                for(var k=0;k<times.Length;k++)
                {
                    times[k]=input.Float(k,0);if(!float.IsFinite(times[k])||times[k]<0||times[k]>120||k>0&&times[k]<=times[k-1])throw Bad("动画时间必须递增，且不超过 120 秒。");
                    var v=new Vector4(output.Float(k,0),output.Float(k,1),output.Float(k,2),path=="rotation"?output.Float(k,3):0);
                    if(!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z)||!float.IsFinite(v.W)||Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)))>1e6)throw Bad("动画关键帧数值无效。");
                    if(path=="rotation"){if(!float.IsFinite(v.LengthSquared())||v.LengthSquared()<1e-10)throw Bad("动画四元数无效。");v=Vector4.Normalize(v);}
                    if(path=="scale"&&Math.Abs(v.X*v.Y*v.Z)<1e-12)throw Bad("动画包含零缩放。");values[k]=v;
                }
                duration=Math.Max(duration,times[^1]);tracks.Add(new(node,path,interpolation=="STEP",times,values));
            }
            if(duration<=0||tracks.Count==0)throw Bad("动画片段时长为空。");var name=animation.TryGetProperty("name",out var title)?title.GetString()??"":"";name=name.Trim();
            if(name.Length==0)name=$"动作 {clips.Count+1}";name=name[..Math.Min(64,name.Length)];var baseName=name;var suffix=2;
            while(clips.Any(c=>c.Name==name))name=baseName[..Math.Min(56,baseName.Length)]+$" ({suffix++})";
            clips.Add(new(name,duration,tracks.ToArray()));
        }
        _rigClips=clips.ToArray();
    }
    private RigMesh ReadMeshRig(JsonElement primitive,int node,int skin,int vertices,Matrix4x4 inverseWorld)
    {
        if(skin<0)return new(node,-1,inverseWorld,[],[]);
        if(skin>=_rigSkins.Length)throw Bad("蒙皮索引越界。");var attributes=primitive.GetProperty("attributes");
        if(attributes.TryGetProperty("JOINTS_1",out _)||attributes.TryGetProperty("WEIGHTS_1",out _))throw Bad("每顶点最多支持 4 根骨骼，请先限制并归一化权重。");
        var j=Access(attributes.GetProperty("JOINTS_0").GetInt32(),"VEC4");var w=Access(attributes.GetProperty("WEIGHTS_0").GetInt32(),"VEC4");
        if(j.Count!=vertices||w.Count!=vertices||j.Component is not (5121 or 5123)||j.Normalized||w.Component is not (5121 or 5123 or 5126))throw Bad("关节索引或蒙皮权重格式无效。");
        var joints=new Joint4[vertices];var weights=new Vector4[vertices];
        for(var i=0;i<vertices;i++)
        {
            var js=new Joint4((int)j.Float(i,0),(int)j.Float(i,1),(int)j.Float(i,2),(int)j.Float(i,3));for(var k=0;k<4;k++)if(js[k]<0||js[k]>=_rigSkins[skin].Joints.Length)throw Bad("顶点关节索引越界。");
            var ws=new Vector4(w.Float(i,0),w.Float(i,1),w.Float(i,2),w.Float(i,3));var sum=ws.X+ws.Y+ws.Z+ws.W;
            if(!float.IsFinite(sum)||Math.Min(Math.Min(ws.X,ws.Y),Math.Min(ws.Z,ws.W))<0||Math.Abs(sum-1)>.05)throw Bad("骨骼权重未归一化或包含非法值，请先修复。");
            joints[i]=js;weights[i]=ws/sum;
        }
        return new(node,skin,inverseWorld,joints,weights);
    }
}
