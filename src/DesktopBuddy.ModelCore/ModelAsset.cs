using System.Numerics;

namespace DesktopBuddy.ModelCore;

public sealed record ModelMaterial(Vector4 Color, byte[]? Image, string MimeType, string AlphaMode, float AlphaCutoff);
public sealed record ModelMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] UV, int[] Indices, ModelMaterial Material);
public sealed record ModelAsset(ModelMesh[] Meshes, int OriginalTriangles, string SourceHash, bool HasAnimations)
{
    public RigAnimation? Rig { get; init; }
    public int TriangleCount => Meshes.Sum(m => m.Indices.Length / 3);
    public int VertexCount => Meshes.Sum(m => m.Positions.Length);
}

public sealed class ModelImportException(string message) : Exception(message);
