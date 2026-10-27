using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DesktopBuddy.ModelCore;

/// <summary>Writes a self-contained, uncompressed static GLB cache. No source path is persisted.</summary>
public static class GlbWriter
{
    public static void Write(ModelAsset asset, string path, CancellationToken cancel = default)
    {
        if (asset.Rig is not null) throw new ModelImportException("静态缓存写入器不能丢弃骨骼或动画数据。");
        using var data = new MemoryStream(); using var binary = new BinaryWriter(data);
        var views = new List<object>(); var accessors = new List<object>(); var meshes = new List<object>();
        var images = new List<object>(); var textures = new List<object>(); var materials = new List<object>();
        int View(Action action)
        {
            while (data.Length % 4 != 0) binary.Write((byte)0);
            var start = checked((int)data.Length); action(); var id = views.Count;
            views.Add(new { buffer = 0, byteOffset = start, byteLength = checked((int)data.Length) - start }); return id;
        }
        int Attribute(int view, string type, int count, int component, float[]? min = null, float[]? max = null)
        {
            var id = accessors.Count;
            var a = new Dictionary<string, object> { ["bufferView"] = view, ["componentType"] = component, ["count"] = count, ["type"] = type };
            if (min != null) a["min"] = min; if (max != null) a["max"] = max;
            accessors.Add(a); return id;
        }
        foreach (var mesh in asset.Meshes)
        {
            cancel.ThrowIfCancellationRequested();
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            var pv = View(() => { foreach (var p in mesh.Positions) { binary.Write(p.X); binary.Write(p.Y); binary.Write(p.Z); min = Vector3.Min(min, p); max = Vector3.Max(max, p); } });
            var pidx = Attribute(pv, "VEC3", mesh.Positions.Length, 5126, [min.X, min.Y, min.Z], [max.X, max.Y, max.Z]);
            var nidx = Attribute(View(() => { foreach (var n in mesh.Normals) { binary.Write(n.X); binary.Write(n.Y); binary.Write(n.Z); } }), "VEC3", mesh.Normals.Length, 5126);
            var uidx = Attribute(View(() => { foreach (var uv in mesh.UV) { binary.Write(uv.X); binary.Write(uv.Y); } }), "VEC2", mesh.UV.Length, 5126);
            var iidx = Attribute(View(() => { foreach (var i in mesh.Indices) binary.Write((uint)i); }), "SCALAR", mesh.Indices.Length, 5125);
            var mat = mesh.Material;
            var pbr = new Dictionary<string, object> { ["baseColorFactor"] = new[] { mat.Color.X, mat.Color.Y, mat.Color.Z, mat.Color.W }, ["metallicFactor"] = 0, ["roughnessFactor"] = 1 };
            if (mat.Image != null)
            {
                var imageIndex = images.Count; images.Add(new { bufferView = View(() => binary.Write(mat.Image)), mimeType = mat.MimeType });
                var textureIndex = textures.Count; textures.Add(new { source = imageIndex }); pbr["baseColorTexture"] = new { index = textureIndex };
            }
            var materialIndex = materials.Count;
            materials.Add(new { pbrMetallicRoughness = pbr, doubleSided = true, alphaMode = mat.AlphaMode, alphaCutoff = mat.AlphaCutoff });
            meshes.Add(new { primitives = new[] { new { attributes = new { POSITION = pidx, NORMAL = nidx, TEXCOORD_0 = uidx }, indices = iidx, material = materialIndex } } });
        }
        var nodes = Enumerable.Range(0, meshes.Count).Select(i => new { mesh = i }).ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new { asset = new { version = "2.0", generator = "Qianmeng static model cache v1" }, scene = 0,
            scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Length).ToArray() } }, nodes, meshes, materials, images, textures, accessors, bufferViews = views,
            buffers = new[] { new { byteLength = checked((int)data.Length) } } });
        var jsonLength = (json.Length + 3) & ~3; var binLength = ((int)data.Length + 3) & ~3;
        cancel.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); using var output = new BinaryWriter(file);
        output.Write(0x46546C67u); output.Write(2u); output.Write(checked((uint)(28 + jsonLength + binLength)));
        output.Write((uint)jsonLength); output.Write(0x4E4F534Au); output.Write(json);
        for (var i = json.Length; i < jsonLength; i++) output.Write((byte)' ');
        output.Write((uint)binLength); output.Write(0x004E4942u); data.Position = 0; data.CopyTo(file);
        for (var i = data.Length; i < binLength; i++) output.Write((byte)0);
        file.Flush(true);
    }
}
