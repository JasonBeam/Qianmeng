using DesktopBuddy.ModelCore;
using System.Windows.Media.Imaging;

namespace DesktopBuddy.Services;

/// <summary>Cache static textures at the same 1024px resolution used by the renderer, inside the disposable worker.</summary>
internal static class ModelTextureOptimizer
{
    internal static ModelAsset PrepareStatic(ModelAsset asset)
    {
        if(asset.Rig is not null)return asset;
        var cache=new Dictionary<byte[],(byte[] Image,string Mime)>();
        var meshes=asset.Meshes.Select(mesh=>
        {
            var material=mesh.Material;if(material.Image is null)return mesh;
            var dimensions=EncodedImageInfo.Dimensions(material.Image);
            if(Math.Max(dimensions.Width,dimensions.Height)<=1024)return mesh;
            if(!cache.TryGetValue(material.Image,out var prepared))
            {
                using var input=new MemoryStream(material.Image,false);
                var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
                if(dimensions.Width>=dimensions.Height)bitmap.DecodePixelWidth=1024;else bitmap.DecodePixelHeight=1024;
                bitmap.StreamSource=input;bitmap.EndInit();bitmap.Freeze();
                var jpeg=material.AlphaMode=="OPAQUE"&&material.MimeType=="image/jpeg";
                BitmapEncoder encoder=jpeg?new JpegBitmapEncoder{QualityLevel=90}:new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=new MemoryStream();encoder.Save(output);
                prepared=(output.ToArray(),jpeg?"image/jpeg":"image/png");cache.Add(material.Image,prepared);
            }
            return mesh with{Material=material with{Image=prepared.Image,MimeType=prepared.Mime}};
        }).ToArray();
        return asset with{Meshes=meshes};
    }
}
