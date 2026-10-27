using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy.Services;

internal static class NativeModelOptimizer
{
    // Official meshoptimizer v1.3 Windows release; invoke only this pinned local tool.
    private const string ToolHash = "A082A0DB2332908133100332CAEEDDAA304AFCF5E8094ADC755EBDEA8C720984";
    internal sealed record Metadata(string Hash, int Triangles, bool Animations, bool Rigged);
    internal static Metadata Inspect(string path)
    {
        using var f = File.OpenRead(path);
        if (f.Length < 28 || f.Length > GlbReader.MaxFileBytes) throw new ModelImportException("GLB 文件应小于 256 MB。");
        Span<byte> h = stackalloc byte[20]; f.ReadExactly(h);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x46546C67 || BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) != f.Length || BinaryPrimitives.ReadUInt32LittleEndian(h[16..]) != 0x4E4F534A)
            throw new ModelImportException("不是完整的 glTF 2.0 GLB 文件。");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        if (length > 8 * 1024 * 1024 || length > f.Length - 28 || length % 4 != 0) throw new ModelImportException("GLB JSON 长度无效。");
        var bytes = new byte[(int)length]; f.ReadExactly(bytes);
        using var doc = JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64}); var root=doc.RootElement;
        void LocalOnly(JsonElement e)
        {
            if(e.ValueKind==JsonValueKind.Object) foreach(var v in e.EnumerateObject())
            { if(v.NameEquals("uri"))throw new ModelImportException("仅支持数据和纹理全部内嵌的 GLB，不读取外部路径或网址。"); LocalOnly(v.Value); }
            else if(e.ValueKind==JsonValueKind.Array) foreach(var v in e.EnumerateArray())LocalOnly(v);
        }
        LocalOnly(root);
        if(root.GetProperty("asset").GetProperty("version").GetString()!="2.0" || root.GetProperty("buffers").GetArrayLength()!=1)
            throw new ModelImportException("仅支持单一内嵌缓冲区的 glTF 2.0。");
        if(root.TryGetProperty("extensionsRequired",out var extensions))foreach(var e in extensions.EnumerateArray())
            if(e.GetString() is not "KHR_materials_unlit")throw new ModelImportException("请使用未压缩且纹理内嵌的静态 GLB。");
        var rigged=root.TryGetProperty("skins",out var skins)&&skins.GetArrayLength()>0;
        long triangles=0;var accessors=root.GetProperty("accessors");
        foreach(var mesh in root.GetProperty("meshes").EnumerateArray())foreach(var primitive in mesh.GetProperty("primitives").EnumerateArray())
        {
            if(primitive.TryGetProperty("mode",out var mode)&&mode.GetInt32()!=4)throw new ModelImportException("仅支持三角网格。");
            var index=primitive.TryGetProperty("indices",out var ix)?ix.GetInt32():primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
            triangles+=accessors[index].GetProperty("count").GetInt64()/3;
        }
        if(triangles<=0||triangles>8_000_000)throw new ModelImportException("模型面数超出导入预算，请先在建模工具中简化。");
        f.Position=0;return new(Convert.ToHexString(SHA256.HashData(f)),(int)triangles,root.TryGetProperty("animations",out var a)&&a.GetArrayLength()>0,rigged);
    }
    internal static (Metadata Metadata, double NativePeakMiB) Optimize(string source,string output)
    {
        // Copy once to a private job before inspecting; the native parser cannot race a replaced source.
        var input=Path.Combine(Path.GetDirectoryName(output)!,"input.glb");File.Copy(source,input,true);
        var info=Inspect(input);var nativePeak=0d;
        try
        {
            if(info.Triangles<=((info.Rigged||info.Animations)?24000:GlbReader.TriangleBudget)) {File.Copy(input,output,true);return(info,0);}
            var tool=Path.Combine(AppContext.BaseDirectory,"tools","gltfpack.exe");
            if(!File.Exists(tool))throw new ModelImportException("缺少本地模型优化组件，请重新安装完整 Setup 包。");
            using(var t=File.OpenRead(tool))if(Convert.ToHexString(SHA256.HashData(t))!=ToolHash)throw new ModelImportException("模型优化组件校验失败，请重新安装。");
            var start=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(var arg in new[]{"-i",input,"-o",output,"-si",Math.Min(1,((info.Rigged||info.Animations)?18000d:50000d)/info.Triangles).ToString("G17",System.Globalization.CultureInfo.InvariantCulture),"-se",(info.Rigged||info.Animations)?"0.01":"0.002","-slb","-noq","-kn","-km","-af","60"})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new ModelImportException("无法启动本地模型优化组件。");
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();var watch=Stopwatch.StartNew();
            while(!process.WaitForExit(50))
            {
                process.Refresh();nativePeak=Math.Max(nativePeak,process.PrivateMemorySize64/1048576d);
                if(watch.Elapsed>TimeSpan.FromSeconds(90)||nativePeak>1024){process.Kill(true);process.WaitForExit();throw new ModelImportException("模型优化超时或超过临时内存预算，请先简化模型。");}
            }
            // Peak private usage was sampled while the process was alive.
            Task.WhenAll(stdout,stderr).GetAwaiter().GetResult();
            if(process.ExitCode!=0||!File.Exists(output))throw new ModelImportException("模型无法在保留接缝的条件下优化，请在建模工具中简化后重试。");
            return(info,nativePeak);
        }
        finally{try{File.Delete(input);}catch{}}
    }
}
