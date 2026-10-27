using System.Numerics;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static object CaptureHandPatchOneRing(
        RigSampler sampler,
        RigAnimation rig,
        ModelAsset asset,
        Vector3[][] positions,
        Vector3[][] normals,
        Vector3 towardPalm,
        Vector3 headNormal,
        Func<int,Vector3> currentJoint,
        double actionSeconds)
    {
        var targetVertices=new HashSet<int>{0,1,2,16,31,32,97,99};
        var restWorld=new Matrix4x4[rig.Nodes.Length];
        foreach(var i in rig.Order)
        {
            var node=rig.Nodes[i];
            restWorld[i]=node.Parent<0?node.Rest.Matrix:node.Rest.Matrix*restWorld[node.Parent];
        }
        int Find(string name)=>Array.FindIndex(rig.Nodes,n=>string.Equals(n.Name,name,StringComparison.OrdinalIgnoreCase));
        Vector3 RestJoint(int node)=>node<0||node>=restWorld.Length?new(float.NaN):new(restWorld[node].M41,restWorld[node].M42,restWorld[node].M43);
        static float[] Point(Vector3 p)=>new[]{p.X,p.Y,p.Z};
        static float[] Uv(Vector2 p)=>new[]{p.X,p.Y};
        static Vector3 Unit(Vector3 v)=>v.LengthSquared()>1e-10f?Vector3.Normalize(v):Vector3.Zero;
        static float Dot(Vector3 a,Vector3 b)=>a.LengthSquared()>1e-10f&&b.LengthSquared()>1e-10f?Vector3.Dot(Unit(a),Unit(b)):float.NaN;
        var hand=Find("mixamorig:RightHand");var index1=Find("mixamorig:RightHandIndex1");var middle1=Find("mixamorig:RightHandMiddle1");var pinky1=Find("mixamorig:RightHandPinky1");
        Vector3 Palm(Vector3 h,Vector3 ix,Vector3 mid,Vector3 pink)=>Unit(Vector3.Cross(ix-pink,mid-h));
        var restPalm=Palm(RestJoint(hand),RestJoint(index1),RestJoint(middle1),RestJoint(pinky1));
        var finalPalm=Palm(currentJoint(hand),currentJoint(index1),currentJoint(middle1),currentJoint(pinky1));
        var localPositions=GetField<Vector3[][]>(sampler,"_localPositions");
        Vector3 RestSkinned(int meshIndex,int vertex)
        {
            var binding=rig.Meshes[meshIndex];if(binding.Skin<0||binding.Skin>=rig.Skins.Length)return asset.Meshes[meshIndex].Positions[vertex];
            var skin=rig.Skins[binding.Skin];var ids=binding.Joints[vertex];var ws=binding.Weights[vertex];var local=localPositions[meshIndex][vertex];var sum=Vector3.Zero;
            for(var k=0;k<4;k++)
            {
                var weight=k switch{0=>ws.X,1=>ws.Y,2=>ws.Z,_=>ws.W};if(weight<=0)continue;
                var joint=ids[k];if(joint<0||joint>=skin.Joints.Length)continue;
                var node=skin.Joints[joint];sum+=Vector3.Transform(local,skin.InverseBind[joint]*restWorld[node])*weight;
            }
            return sum;
        }
        static float Weight(Vector4 w,int k)=>k switch{0=>w.X,1=>w.Y,2=>w.Z,_=>w.W};
        var boneNames=new[]{"Index","Middle","Ring","Pinky","Thumb"};var boneAxes=new List<object>();
        foreach(var finger in boneNames)
        {
            var prev=Find("mixamorig:RightHand"+finger+"2");var tip=Find("mixamorig:RightHand"+finger+"3");
            boneAxes.Add(new{name=finger,restAxis=Point(Unit(RestJoint(tip)-RestJoint(prev))),finalAxis=Point(Unit(currentJoint(tip)-currentJoint(prev)))});
        }
        var oneRingTriangles=new List<object>();var sourceTriangleCount=0;
        for(var meshIndex=0;meshIndex<asset.Meshes.Length;meshIndex++)
        {
            if(meshIndex!=0)continue;var modelMesh=asset.Meshes[meshIndex];var binding=rig.Meshes[meshIndex];RigSkin? skin=binding.Skin>=0&&binding.Skin<rig.Skins.Length?rig.Skins[binding.Skin]:null;
            for(var at=0;at+2<modelMesh.Indices.Length;at+=3)
            {
                var ids=new[]{modelMesh.Indices[at],modelMesh.Indices[at+1],modelMesh.Indices[at+2]};
                if(!ids.Any(targetVertices.Contains))continue;
                var rest=ids.Select(v=>RestSkinned(meshIndex,v)).ToArray();var final=ids.Select(v=>positions[meshIndex][v]).ToArray();
                var restCross=Vector3.Cross(rest[1]-rest[0],rest[2]-rest[0]);var finalCross=Vector3.Cross(final[1]-final[0],final[2]-final[0]);
                var restFace=Unit(restCross);var finalFace=Unit(finalCross);var vertexData=new List<object>();
                foreach(var vertex in ids)
                {
                    var jointWeights=new List<object>();
                    if(skin is not null)
                    {
                        var j=binding.Joints[vertex];var w=binding.Weights[vertex];
                        for(var k=0;k<4;k++)
                        {
                            var amount=Weight(w,k);var localJoint=j[k];if(amount<=0||localJoint<0||localJoint>=skin.Joints.Length)continue;
                            var node=skin.Joints[localJoint];jointWeights.Add(new{bone=rig.Nodes[node].Name,weight=amount});
                        }
                    }
                    vertexData.Add(new{vertex,meshNodePosition=Point(modelMesh.Positions[vertex]),restSkinnedWorld=Point(rest[Array.IndexOf(ids,vertex)]),finalSkinnedWorld=Point(positions[meshIndex][vertex]),finalVertexNormal=vertex<normals[meshIndex].Length?Point(normals[meshIndex][vertex]):null,uv=vertex<modelMesh.UV.Length?Uv(modelMesh.UV[vertex]):null,jointWeights=jointWeights.ToArray()});
                }
                var restMismatch=ids.Select((v,i)=>Vector3.Distance(asset.Meshes[meshIndex].Positions[v],rest[i])).DefaultIfEmpty().Max();
                oneRingTriangles.Add(new{mesh=meshIndex,triangle=at/3,vertices=ids,restAreaMm2=restCross.Length()*.5e6f,finalAreaMm2=finalCross.Length()*.5e6f,restGeometricNormal=Point(restFace),finalGeometricNormal=Point(finalFace),restNormalDotRestPalm=Dot(restFace,restPalm),finalNormalDotFinalPalm=Dot(finalFace,finalPalm),finalNormalDotTowardPalm=Dot(finalFace,towardPalm),finalNormalDotHeadOutward=Dot(finalFace,headNormal),restSkinnedToMeshNodeMaxMismatchMm=restMismatch*1000,vertexData=vertexData.ToArray()});
                sourceTriangleCount++;
            }
        }
        return new{available=true,poseActionSeconds=actionSeconds,modelSourceHash=asset.SourceHash,meshZeroTargetVertexIds=targetVertices.OrderBy(v=>v).ToArray(),targetVertexCount=targetVertices.Count,oneRingTriangleCount=sourceTriangleCount,restPalmNormal=Point(restPalm),finalPalmNormal=Point(finalPalm),towardPalmWorld=Point(towardPalm),headSurfaceNormalWorld=Point(headNormal),fingerBoneAxes=boneAxes.ToArray(),triangles=oneRingTriangles.ToArray(),limitations=new[]{"Rest pose is reconstructed from the imported rig rest transforms and inverse-bind matrices; the rest-to-mesh-node mismatch is recorded per triangle.","Current final-pose triangle geometry is from RigSampler.Positions at the captured WPF host action time; this is not installed-EXE evidence.","Face normals, UVs, and bone weights do not alone certify visual anatomical identity."}};
    }
}
