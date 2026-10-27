using System.Numerics;

namespace DesktopBuddy.ModelCore;

/// <summary>Compact validated geometry without changing positions or UV seams. High-poly reduction runs in a separate native worker.</summary>
internal static class MeshReducer
{

    public static ModelMesh Reduce(ModelMesh source, int budget, CancellationToken cancel)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var p in source.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var extent = max - min; var size = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        if (!float.IsFinite(size) || size < 1e-12f) throw new ModelImportException("模型尺寸为零，无法显示。");
        if (source.Indices.Length / 3 <= budget) return Compact(source, cancel);
        throw new ModelImportException("模型超过 60,000 面预算，请通过模型管理页进行保留边界的导入优化。");
    }

    private static ModelMesh Compact(ModelMesh source, CancellationToken cancel)
    {
        var map = new Dictionary<int, int>(); var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var indices = new List<int>();
        foreach (var old in source.Indices)
        {
            if ((indices.Count & 16383) == 0) cancel.ThrowIfCancellationRequested();
            if (!map.TryGetValue(old, out var id)) { id = positions.Count; map.Add(old, id); positions.Add(source.Positions[old]); normals.Add(source.Normals[old]); uv.Add(source.UV[old]); }
            indices.Add(id);
        }
        if (indices.Count == 0) throw new ModelImportException("优化后的网格为空，请换用完整模型。");
        var generated = new Vector3[positions.Count];
        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            generated[a] += n; generated[b] += n; generated[c] += n;
        }
        for (var i = 0; i < normals.Count; i++)
        {
            var n = normals[i].LengthSquared() > 1e-12f ? normals[i] : generated[i];
            normals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }
        return new(positions.ToArray(), normals.ToArray(), uv.ToArray(), indices.ToArray(), source.Material);
    }
}
