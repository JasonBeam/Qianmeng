using DesktopBuddy.ModelCore;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DesktopBuddy.Services;

internal static class ModelSceneFactory
{
    private static readonly object CacheLock = new();
    private static string? _lastHash;
    private static WeakReference<Model3DGroup>? _lastScene;

    public static Model3DGroup Create(ModelAsset asset, CancellationToken cancel, Vector3? fixedCenter = null, float fixedSize = 0)
    {
        cancel.ThrowIfCancellationRequested();
        if (asset.Rig is not null) return RigScenePlayer.CreateFrozenPose(asset, cancel);
        // A one-entry weak cache reuses the currently displayed frozen scene on repeat import.
        // It cannot keep a replaced model alive and does not accumulate other models.
        lock (CacheLock)
            if (fixedCenter is null && _lastHash == asset.SourceHash && _lastScene is not null && _lastScene.TryGetTarget(out var existing)) return existing;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var mesh in asset.Meshes) foreach (var p in mesh.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var center = (min + max) / 2; var size = max - min; var longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (longest < 1e-12 || !float.IsFinite(longest)) throw new ModelImportException("模型尺寸无效，无法自动取景。");
        if (fixedCenter is { } stableCenter) { center = stableCenter; longest = fixedSize; }
        var scene = new Model3DGroup();
        foreach (var mesh in asset.Meshes)
        {
            cancel.ThrowIfCancellationRequested();
            var geometry = new MeshGeometry3D
            {
                Positions = new Point3DCollection(mesh.Positions.Select(p => new Point3D((p.X - center.X) / longest, (p.Y - center.Y) / longest, (p.Z - center.Z) / longest))),
                Normals = new Vector3DCollection(mesh.Normals.Select(n => new Vector3D(n.X, n.Y, n.Z))),
                TextureCoordinates = new PointCollection(mesh.UV.Select(uv => new System.Windows.Point(uv.X, uv.Y))),
                TriangleIndices = new Int32Collection(mesh.Indices)
            };
            geometry.Freeze();
            var material = new MaterialGroup();
            var brush = MakeBrush(mesh.Material);
            material.Children.Add(new DiffuseMaterial(brush));
            var glow = brush.Clone(); glow.Opacity *= 0.06;
            material.Children.Add(new EmissiveMaterial(glow));
            material.Freeze();
            scene.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
        }
        scene.Freeze();
        if (fixedCenter is null) lock (CacheLock) { _lastHash = asset.SourceHash; _lastScene = new(scene); }
        return scene;
    }

    private static System.Windows.Media.Brush MakeBrush(ModelMaterial material)
    {
        var factor = material.Color;
        if (material.Image is null)
        {
            var alpha = material.AlphaMode == "OPAQUE" ? (byte)255 : (byte)(factor.W * 255);
            if (alpha == 0) throw new ModelImportException("模型材质完全透明，请修改材质后导入。");
            return new SolidColorBrush(Color.FromArgb(alpha, (byte)(factor.X * 255), (byte)(factor.Y * 255), (byte)(factor.Z * 255)));
        }
        using var input = new MemoryStream(material.Image, false);
        var (width, height) = EncodedImageInfo.Dimensions(material.Image);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) image.DecodePixelWidth = Math.Min(width, 1024); else image.DecodePixelHeight = Math.Min(height, 1024);
        image.StreamSource = input; image.EndInit(); image.Freeze();
        // Detach the display bitmap from WIC's source decoder. Keeping a BitmapImage
        // can retain the original 8K JPEG decoder/cache even when DecodePixelWidth is 1024.
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4; var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        var visible = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * factor.Z); pixels[i + 1] = (byte)(pixels[i + 1] * factor.Y); pixels[i + 2] = (byte)(pixels[i + 2] * factor.X);
            pixels[i + 3] = material.AlphaMode switch
            {
                "OPAQUE" => 255,
                "MASK" => pixels[i + 3] * factor.W / 255f < material.AlphaCutoff ? (byte)0 : (byte)255,
                _ => (byte)(pixels[i + 3] * factor.W)
            };
            visible |= pixels[i + 3] > 0;
        }
        if (!visible) throw new ModelImportException("模型纹理完全透明，请修改材质后导入。");
        var bitmap = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill, TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new System.Windows.Rect(0, 0, 1, 1) };
        brush.Freeze(); return brush;
    }
}
