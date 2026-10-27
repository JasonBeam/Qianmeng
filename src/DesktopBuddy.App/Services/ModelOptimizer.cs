using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows.Media.Media3D;
using DesktopBuddy.ModelCore;

namespace DesktopBuddy.Services;

internal static class ModelOptimizer
{
    internal sealed record AssetInfo(int OriginalTriangles, int TriangleCount, bool HasAnimations, string[] Clips);
    internal sealed record Result(Model3DGroup Scene, AssetInfo Asset, string Name, long Bytes, double WorkerPeakPrivateMiB, ModelAsset? AnimatedAsset = null);
    private static string? _cachedSourceHash, _cachedOutputHash;
    private static AssetInfo? _cachedInfo;
    private static WeakReference<Model3DGroup>? _cachedScene;
    private static double _cachedWorkerPeak;
    private static WeakReference<ModelAsset>? _cachedAnimatedAsset;
    private static async Task<string> FileHash(string path, CancellationToken cancel)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length > GlbReader.MaxFileBytes) throw new ModelImportException("GLB 文件应小于 256 MB。");
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancel));
    }

    public static async Task<Result> ImportAsync(string source, string directory, CancellationToken cancel)
    {
        var sourceHash = await FileHash(source, cancel);
        var cachedName = "model-" + sourceHash[..24] + "-v3.glb";
        var cachedPath = Path.Combine(directory, cachedName);
        if (_cachedSourceHash == sourceHash && _cachedInfo is not null && _cachedScene is not null
            && _cachedScene.TryGetTarget(out var cachedScene) && File.Exists(cachedPath)
            && await FileHash(cachedPath, cancel) == _cachedOutputHash)
        {
            ModelAsset? cachedAsset = null;
            if (_cachedInfo.Clips.Length > 0 && (_cachedAnimatedAsset is null || !_cachedAnimatedAsset.TryGetTarget(out cachedAsset))) { }
            else return new(cachedScene, _cachedInfo, cachedName, new FileInfo(cachedPath).Length, _cachedWorkerPeak, cachedAsset);
        }
        Directory.CreateDirectory(directory);
        var job = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
        var requestPath = Path.Combine(job, "request.json"); var resultPath = Path.Combine(job, "result.json"); var output = Path.Combine(job, "optimized.glb");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new ModelWorkerRequest(Path.GetFullPath(source), output)), cancel);
            var executable = Environment.ProcessPath ?? throw new ModelImportException("无法确定模型辅助进程的启动位置。");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entry = Assembly.GetEntryAssembly()?.GetName().Name ?? throw new ModelImportException("无法确定开发运行配置。");
                start.ArgumentList.Add("exec"); start.ArgumentList.Add("--runtimeconfig"); start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, entry + ".runtimeconfig.json"));
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(App).Assembly.GetName().Name + ".dll"));
            }
            start.ArgumentList.Add("--model-worker"); start.ArgumentList.Add(requestPath); start.ArgumentList.Add(resultPath);
            // Bound disposable import work; this setting is scoped to the worker only.
            start.Environment["DOTNET_GCHeapHardLimit"] = "0x20000000";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            process = Process.Start(start) ?? throw new ModelImportException("无法启动模型优化进程。");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            using var killOnCancel = cancel.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancel);
            await stdout; await stderr;
            cancel.ThrowIfCancellationRequested();
            if (!File.Exists(resultPath)) throw new ModelImportException("模型优化进程未能完成，请检查设备内存和应用控制状态。");
            var response = JsonSerializer.Deserialize<ModelWorkerResult>(await File.ReadAllTextAsync(resultPath, cancel)) ?? throw new ModelImportException("模型优化结果无效。");
            if (!response.Success) throw new ModelImportException(response.Error);
            if (response.SourceHash.Length != 64 || response.SourceHash.Any(c => !Uri.IsHexDigit(c))) throw new ModelImportException("模型缓存校验信息无效。");
            var loaded = await Task.Run(() =>
            {
                var asset = GlbReader.Load(output, cancel) with { OriginalTriangles = response.OriginalTriangles, SourceHash = response.SourceHash, HasAnimations = response.HasAnimations };
                return (Asset: new AssetInfo(asset.OriginalTriangles, asset.TriangleCount, asset.HasAnimations, asset.Rig?.Clips.Select(c=>c.Name).ToArray() ?? []), Scene: ModelSceneFactory.Create(asset, cancel), AnimatedAsset: asset.Rig is null ? null : asset);
            }, cancel);
            cancel.ThrowIfCancellationRequested();
            var name = "model-" + response.SourceHash[..24] + "-v3.glb"; var target = Path.Combine(directory, name);
            File.Move(output, target, true);
            _cachedSourceHash = response.SourceHash; _cachedOutputHash = await FileHash(target, cancel);
            _cachedInfo = loaded.Asset; _cachedScene = new(loaded.Scene); _cachedWorkerPeak = response.PeakPrivateMiB;
            _cachedAnimatedAsset = loaded.AnimatedAsset is null ? null : new(loaded.AnimatedAsset);
            return new(loaded.Scene, loaded.Asset, name, new FileInfo(target).Length, response.PeakPrivateMiB, loaded.AnimatedAsset);
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch { }
                process.Dispose();
            }
            try { Directory.Delete(job, true); } catch { }
        }
    }
}
