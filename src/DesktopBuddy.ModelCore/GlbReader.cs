using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopBuddy.ModelCore;

/// <summary>Local-only static glTF 2 renderer input. Never resolves URIs or executes model extras.</summary>
public sealed partial class GlbReader
{
    public const int MaxFileBytes = 256 * 1024 * 1024;
    public const int TriangleBudget = 60_000;
    private readonly JsonElement _root;
    private readonly ReadOnlyMemory<byte> _bin;
    private readonly CancellationToken _cancel;
    private readonly List<ModelMesh> _meshes = [];
    private readonly Dictionary<int, byte[]> _images = [];
    private int _sourceTriangles;
    private int _imageBytes;

    private GlbReader(JsonElement root, ReadOnlyMemory<byte> bin, CancellationToken cancel)
    { _root = root; _bin = bin; _cancel = cancel; }

    public static ModelAsset Load(string path, CancellationToken cancel = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 28 || file.Length > MaxFileBytes) throw Bad("GLB 文件应小于 256 MB，且必须包含有效模型。");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return Load(bytes, cancel);
    }

    public static ModelAsset Load(byte[] bytes, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        try
        {
            if (bytes.Length < 28 || bytes.Length > MaxFileBytes || U32(bytes, 0) != 0x46546C67
                || U32(bytes, 4) != 2 || U32(bytes, 8) != bytes.Length) throw Bad("不是完整的 glTF 2.0 GLB 文件。");
            ReadOnlyMemory<byte> json = default, bin = default;
            for (var at = 12; at < bytes.Length;)
            {
                if (at > bytes.Length - 8) throw Bad("GLB 数据块不完整。");
                var length = checked((int)U32(bytes, at)); var type = U32(bytes, at + 4); at += 8;
                if (length % 4 != 0 || length > bytes.Length - at) throw Bad("GLB 数据块长度无效。");
                if (type == 0x4E4F534A) { if (!json.IsEmpty || at != 20 || length > 8 * 1024 * 1024) throw Bad("GLB JSON 数据块无效。"); json = bytes.AsMemory(at, length); }
                if (type == 0x004E4942) { if (!bin.IsEmpty) throw Bad("不支持多个二进制数据块。"); bin = bytes.AsMemory(at, length); }
                at += length;
            }
            if (json.IsEmpty || bin.IsEmpty) throw Bad("GLB 缺少 JSON 或内嵌模型数据。");
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0") throw Bad("仅支持 glTF 2.0。");
            if (root.TryGetProperty("extensionsRequired", out var required))
                foreach (var ext in required.EnumerateArray())
                    if (ext.GetString() is not "KHR_materials_unlit") throw Bad("模型依赖暂不支持的压缩或材质扩展，请导出未压缩 GLB。");
            var buffers = root.GetProperty("buffers");
            if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _)
                || buffers[0].GetProperty("byteLength").GetInt64() > bin.Length) throw Bad("仅支持数据和纹理全部内嵌的 GLB。");
            var reader = new GlbReader(root, bin, cancel);
            reader.ReadRig();
            var scene = At(root.GetProperty("scenes"), Int(root, "scene", 0));
            foreach (var node in scene.GetProperty("nodes").EnumerateArray()) reader.Visit(node.GetInt32(), Matrix4x4.Identity, []);
            if (reader._meshes.Count == 0) throw Bad("模型没有可显示的三角网格。");
            if (reader._meshes.Sum(m => m.Indices.Length / 3) > TriangleBudget) throw Bad("优化后模型仍然过于复杂，请先减少面数后导入。");
            if (reader._rigNodes is not null && (reader._meshes.Sum(m=>m.Indices.Length/3)>24000 || reader._meshes.Sum(m=>m.Positions.Length)>20000))
                throw Bad("动画模型超过 24,000 面或 20,000 顶点预算，请先保留骨骼简化。");
            return new ModelAsset(reader._meshes.ToArray(), reader._sourceTriangles,
                Convert.ToHexString(SHA256.HashData(bytes)), root.TryGetProperty("animations", out var animations) && animations.GetArrayLength() > 0)
            { Rig = reader._rigNodes is null ? null : new(reader._rigNodes, reader._rigOrder, reader._rigSkins, reader._rigMeshes.ToArray(), reader._rigClips) };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or ArgumentException or IndexOutOfRangeException)
        { throw Bad("GLB 结构或数据索引无效，请重新导出模型。"); }
    }

    private void Visit(int index, Matrix4x4 parent, HashSet<int> ancestors)
    {
        _cancel.ThrowIfCancellationRequested();
        if (ancestors.Count >= 64 || !ancestors.Add(index)) throw Bad("模型节点存在循环或层级过深。");
        var node = At(_root.GetProperty("nodes"), index);
        var local = Matrix4x4.Identity;
        if (node.TryGetProperty("matrix", out var matrix))
        {
            var a = matrix.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (a.Length != 16 || a.Any(x => !float.IsFinite(x))) throw Bad("模型变换矩阵无效。");
            local = new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
        }
        else
        {
            var t = Floats(node, "translation", [0, 0, 0]); var s = Floats(node, "scale", [1, 1, 1]); var r = Floats(node, "rotation", [0, 0, 0, 1]);
            var q = new Quaternion(r[0], r[1], r[2], r[3]);
            if (q.LengthSquared() < 1e-10f) throw Bad("模型旋转数据无效。");
            local = Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
        }
        var world = local * parent;
        var skin = Int(node, "skin", -1);
        if (node.TryGetProperty("mesh", out var meshIndex))
            foreach (var primitive in At(_root.GetProperty("meshes"), meshIndex.GetInt32()).GetProperty("primitives").EnumerateArray()) ReadMesh(primitive, world, index, skin);
        if (node.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) Visit(child.GetInt32(), world, ancestors);
        ancestors.Remove(index);
    }

    private void ReadMesh(JsonElement primitive, Matrix4x4 world, int nodeIndex, int skinIndex)
    {
        if (_meshes.Count >= 64 || Int(primitive, "mode", 4) != 4) throw Bad("仅支持最多 64 个三角网格部件。");
        if (primitive.TryGetProperty("targets", out _)) throw Bad("暂不支持形态键，请先烘焙为骨骼动画。");
        var attributes = primitive.GetProperty("attributes");
        var pos = Access(attributes.GetProperty("POSITION").GetInt32(), "VEC3");
        if (pos.Component != 5126 || pos.Count < 3 || pos.Count > 3_000_000) throw Bad("模型顶点格式无效或顶点数量过多。");
        var normal = attributes.TryGetProperty("NORMAL", out var n) ? Access(n.GetInt32(), "VEC3") : (Accessor?)null;
        var uv = attributes.TryGetProperty("TEXCOORD_0", out var u) ? Access(u.GetInt32(), "VEC2") : (Accessor?)null;
        if ((normal.HasValue && (normal.Value.Count != pos.Count || normal.Value.Component != 5126)) || (uv.HasValue && uv.Value.Count != pos.Count)) throw Bad("顶点属性数量或格式不一致。");
        var indices = primitive.TryGetProperty("indices", out var ix) ? Access(ix.GetInt32(), "SCALAR") : (Accessor?)null;
        var count = indices?.Count ?? pos.Count;
        if (count % 3 != 0 || (indices.HasValue && indices.Value.Component is not (5121 or 5123 or 5125))) throw Bad("三角面索引格式无效。");
        if (_rigNodes is not null && (pos.Count + _meshes.Sum(m=>m.Positions.Length) > 20000 || count/3 + _meshes.Sum(m=>m.Indices.Length/3) > 24000))
            throw Bad("动画模型超过 24,000 面或 20,000 顶点预算，请先保留骨骼简化。");
        _sourceTriangles = checked(_sourceTriangles + count / 3);
        if (_sourceTriangles > 8_000_000) throw Bad("模型超过 800 万个三角面，需先简化后导入。");
        var positions = new Vector3[pos.Count]; var normals = new Vector3[pos.Count]; var texcoords = new Vector2[pos.Count];
        if (!Matrix4x4.Invert(world, out var inverse)) throw Bad("模型包含零缩放或不可逆变换。");
        var normalTransform = Matrix4x4.Transpose(inverse);
        for (var i = 0; i < positions.Length; i++)
        {
            if ((i & 16383) == 0) _cancel.ThrowIfCancellationRequested();
            positions[i] = Vector3.Transform(new(pos.Float(i, 0), pos.Float(i, 1), pos.Float(i, 2)), world);
            normals[i] = normal is { } a ? Vector3.TransformNormal(new(a.Float(i, 0), a.Float(i, 1), a.Float(i, 2)), normalTransform) : Vector3.Zero;
            texcoords[i] = uv is { } b ? new(b.Float(i, 0), b.Float(i, 1)) : Vector2.Zero;
            if (!Finite(positions[i]) || !Finite(normals[i]) || !float.IsFinite(texcoords[i].X) || !float.IsFinite(texcoords[i].Y)
                || MathF.Abs(texcoords[i].X) > 10000 || MathF.Abs(texcoords[i].Y) > 10000) throw Bad("模型包含无效坐标。");
        }
        var triangles = new int[count];
        var reflected = world.GetDeterminant() < 0;
        for (var i = 0; i < count; i++)
        {
            if ((i & 16383) == 0) _cancel.ThrowIfCancellationRequested();
            triangles[i] = indices is { } a ? a.Index(i) : i;
            if (triangles[i] < 0 || triangles[i] >= pos.Count) throw Bad("三角面引用了不存在的顶点。");
        }
        if (reflected) for (var i = 0; i < count; i += 3) (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
        var material = ReadMaterial(Int(primitive, "material", -1));
        var mesh = new ModelMesh(positions, normals, texcoords, triangles, material);
        // Allocate a fair budget to each primitive using the file's complete index counts.
        var total = _root.GetProperty("meshes").EnumerateArray().SelectMany(m => m.GetProperty("primitives").EnumerateArray())
            .Sum(p => p.TryGetProperty("indices", out var a) ? (long)At(_root.GetProperty("accessors"), a.GetInt32()).GetProperty("count").GetInt32() / 3 :
                At(_root.GetProperty("accessors"), p.GetProperty("attributes").GetProperty("POSITION").GetInt32()).GetProperty("count").GetInt32() / 3);
        var budget = Math.Max(100, (int)(TriangleBudget * (count / 3d) / Math.Max(1, total)));
        if (_rigNodes is null) _meshes.Add(MeshReducer.Reduce(mesh, budget, _cancel));
        else { _meshes.Add(mesh); _rigMeshes.Add(ReadMeshRig(primitive, nodeIndex, skinIndex, pos.Count, inverse)); }
    }

    private ModelMaterial ReadMaterial(int index)
    {
        var color = Vector4.One; byte[]? image = null; var mime = "image/png"; var mode = "OPAQUE"; var cutoff = 0.5f;
        if (index >= 0)
        {
            var mat = At(_root.GetProperty("materials"), index);
            mode = mat.TryGetProperty("alphaMode", out var alpha) ? alpha.GetString() ?? "OPAQUE" : mode;
            cutoff = mat.TryGetProperty("alphaCutoff", out var cut) ? cut.GetSingle() : cutoff;
            if (!float.IsFinite(cutoff) || mode is not ("OPAQUE" or "MASK" or "BLEND")) throw Bad("模型透明度设置无效。");
            if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                var c = Floats(pbr, "baseColorFactor", [1, 1, 1, 1]); color = Vector4.Clamp(new(c[0], c[1], c[2], c[3]), Vector4.Zero, Vector4.One);
                if (pbr.TryGetProperty("baseColorTexture", out var tex))
                {
                    if (Int(tex, "texCoord", 0) != 0 || tex.TryGetProperty("extensions", out _)) throw Bad("此版本支持默认 UV 纹理，请先烘焙纹理变换。");
                    var texture = At(_root.GetProperty("textures"), tex.GetProperty("index").GetInt32());
                    var source = texture.GetProperty("source").GetInt32();
                    var img = At(_root.GetProperty("images"), source);
                    if (img.TryGetProperty("uri", out _)) throw Bad("纹理必须内嵌，不能引用外部文件或网址。");
                    mime = img.GetProperty("mimeType").GetString() ?? "";
                    if (mime is not ("image/png" or "image/jpeg")) throw Bad("纹理应为内嵌 PNG 或 JPEG。");
                    if (!_images.TryGetValue(source, out image))
                    {
                        var view = View(img.GetProperty("bufferView").GetInt32());
                        _imageBytes = checked(_imageBytes + view.Length);
                        if (view.Length > 32 * 1024 * 1024 || _imageBytes > 64 * 1024 * 1024) throw Bad("模型内嵌纹理过大。");
                        image = view.ToArray(); _images.Add(source, image);
                    }
                }
            }
        }
        return new(color, image, mime, mode, cutoff);
    }

    private Accessor Access(int index, string type)
    {
        var a = At(_root.GetProperty("accessors"), index);
        if (a.TryGetProperty("sparse", out _) || a.GetProperty("type").GetString() != type) throw Bad("不支持稀疏或不匹配的顶点属性。");
        var viewIndex = a.GetProperty("bufferView").GetInt32(); var view = View(viewIndex);
        var component = a.GetProperty("componentType").GetInt32();
        var bytes = component switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw Bad("不支持此顶点数值格式。") };
        var dimensions = type switch { "MAT4" => 16, "VEC4" => 4, "VEC3" => 3, "VEC2" => 2, _ => 1 };
        var stride = Int(At(_root.GetProperty("bufferViews"), viewIndex), "byteStride", bytes * dimensions);
        var offset = Int(a, "byteOffset", 0); var count = a.GetProperty("count").GetInt32();
        if (count < 0 || offset < 0 || stride < bytes * dimensions || stride > 252
            || (long)offset + Math.Max(0L, count - 1L) * stride + (count > 0 ? bytes * dimensions : 0) > view.Length) throw Bad("顶点属性超出数据边界。");
        return new(view, component, offset, stride, count, a.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean());
    }

    private ReadOnlyMemory<byte> View(int index)
    {
        var v = At(_root.GetProperty("bufferViews"), index);
        var offset = Int(v, "byteOffset", 0); var length = v.GetProperty("byteLength").GetInt32();
        if (Int(v, "buffer", 0) != 0 || offset < 0 || length < 0 || (long)offset + length > _bin.Length) throw Bad("模型数据超出二进制块边界。");
        return _bin.Slice(offset, length);
    }

    private readonly record struct Accessor(ReadOnlyMemory<byte> Data, int Component, int Offset, int Stride, int Count, bool Normalized)
    {
        public float Float(int i, int c)
        {
            var size = Component is 5120 or 5121 ? 1 : Component is 5122 or 5123 ? 2 : 4;
            var span = Data.Span[(Offset + i * Stride + c * size)..];
            return Component switch { 5126 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
                5120 => Normalized ? Math.Max(-1f, unchecked((sbyte)span[0]) / 127f) : unchecked((sbyte)span[0]),
                5122 => Normalized ? Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(span) / 32767f) : BinaryPrimitives.ReadInt16LittleEndian(span),
                5121 => span[0] / (Normalized ? 255f : 1f), 5123 => BinaryPrimitives.ReadUInt16LittleEndian(span) / (Normalized ? 65535f : 1f),
                _ => throw Bad("UV 格式不受支持。") };
        }
        public int Index(int i)
        {
            var s = Data.Span[(Offset + i * Stride)..];
            return Component switch { 5121 => s[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(s), _ => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(s)) };
        }
    }

    private static float[] Floats(JsonElement node, string key, float[] fallback)
    {
        if (!node.TryGetProperty(key, out var value)) return fallback;
        var result = value.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        if (result.Length != fallback.Length || result.Any(x => !float.IsFinite(x))) throw Bad("模型变换或颜色数据无效。");
        return result;
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z))) < 1e12f;
    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
    private static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
    private static JsonElement At(JsonElement array, int index) => index >= 0 && index < array.GetArrayLength() ? array[index] : throw Bad("模型数据索引越界。");
    private static ModelImportException Bad(string text) => new(text);
}
