using System.Diagnostics;
using System.Text.Json;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy;

internal static class EntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--model-worker") return RunModelWorker(args);
        var app = new App(); app.InitializeComponent(); return app.Run();
    }

    private static int RunModelWorker(string[] args)
    {
        if (args.Length != 3) return 2;
        ModelWorkerResult result;
        try
        {
            var request = JsonSerializer.Deserialize<ModelWorkerRequest>(File.ReadAllText(args[1])) ?? throw new InvalidDataException();
            var prepared = NativeModelOptimizer.Optimize(request.Source, request.Output);
            var asset = ModelTextureOptimizer.PrepareStatic(GlbReader.Load(request.Output));
            // Validate the native output with our bounded reader before exposing it to WPF.
            if (asset.Rig is null)
            {
                GlbWriter.Write(asset, request.Output + ".checked");
                File.Move(request.Output + ".checked", request.Output, true);
            }
            using var process = Process.GetCurrentProcess(); process.Refresh();
            result = new(true, prepared.Metadata.Hash, prepared.Metadata.Triangles, prepared.Metadata.Animations, "", process.PeakPagedMemorySize64 / 1048576d + prepared.NativePeakMiB, process.PeakWorkingSet64 / 1048576d);
        }
        catch (Exception ex)
        {
            var error = ex switch
            {
                ModelImportException => ex.Message,
                OutOfMemoryException => "模型超过导入内存预算，请先简化模型后重试。",
                IOException => "无法读取模型或写入优化缓存，请检查磁盘空间。",
                UnauthorizedAccessException => "模型文件或本地缓存目录没有访问权限。",
                _ => "模型优化失败，请使用完整、未压缩且纹理内嵌的静态 GLB。"
            };
            result = new(false, "", 0, false, error, 0, 0);
        }
        try { File.WriteAllText(args[2], JsonSerializer.Serialize(result)); } catch { return 3; }
        return result.Success ? 0 : 1;
    }
}

internal sealed record ModelWorkerRequest(string Source, string Output);
internal sealed record ModelWorkerResult(bool Success, string SourceHash, int OriginalTriangles, bool HasAnimations, string Error, double PeakPrivateMiB, double PeakWorkingSetMiB);
