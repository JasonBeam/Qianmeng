using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopBuddy.ModelCore;

var report = new List<object>();
void Check(string name, bool ok) { report.Add(new { name, ok }); if (!ok) throw new Exception(name); }
var temp = Path.Combine(Path.GetTempPath(), "Qianmeng-ModelChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
try
{
    var fixture = new ModelAsset([new ModelMesh([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2], new(Vector4.One, null, "image/png", "OPAQUE", 0.5f))], 1, "fixture", false);
    var file = Path.Combine(temp, "fixture.glb"); GlbWriter.Write(fixture, file);
    var bytes = File.ReadAllBytes(file); var parsed = GlbReader.Load(bytes);
    Check("triangle-roundtrip", parsed.TriangleCount == 1 && parsed.Meshes[0].Positions.SequenceEqual(fixture.Meshes[0].Positions));
    bool Reject(byte[] input) { try { GlbReader.Load(input); return false; } catch (ModelImportException) { return true; } }
    byte[] Change(Action<JsonObject> edit)
    {
        var jsonLen = BitConverter.ToInt32(bytes, 12); var json = JsonNode.Parse(bytes.AsSpan(20, jsonLen))!.AsObject(); edit(json);
        var changed = JsonSerializer.SerializeToUtf8Bytes(json); var length = (changed.Length + 3) & ~3;
        using var m = new MemoryStream(); using var w = new BinaryWriter(m);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(bytes.Length - jsonLen + length)); w.Write(length); w.Write(0x4E4F534Au); w.Write(changed);
        for (var i = changed.Length; i < length; i++) w.Write((byte)' '); w.Write(bytes.AsSpan(20 + jsonLen)); return m.ToArray();
    }
    Check("reject-truncated-file", Reject(bytes[..^1]));
    Check("reject-accessor-overflow", Reject(Change(j => j["accessors"]![0]!["byteOffset"] = int.MaxValue)));
    Check("reject-invalid-indices", Reject(Change(j => j["accessors"]![3]!["count"] = int.MaxValue)));
    Check("reject-node-cycle", Reject(Change(j => j["nodes"]![0]!["children"] = new JsonArray(0))));
    Check("reject-external-buffer", Reject(Change(j => j["buffers"]![0]!["uri"] = "https://example.invalid/model.bin")));
    Check("reject-required-compression", Reject(Change(j => j["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression"))));
    Check("reject-zero-scale", Reject(Change(j => j["nodes"]![0]!["scale"] = new JsonArray(0, 0, 0))));
    var translated = GlbReader.Load(Change(j => j["nodes"]![0]!["translation"] = new JsonArray(12, 23, 34)));
    Check("bake-node-transform", translated.Meshes[0].Positions[0] == new Vector3(11, 22, 34));
    var cts = new CancellationTokenSource(); cts.Cancel(); var cancelled = false;
    try { GlbReader.Load(bytes, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check("cancellation", cancelled);
    var png = new byte[33]; new byte[] {137,80,78,71,13,10,26,10}.CopyTo(png,0); png[11]=13; "IHDR"u8.CopyTo(png.AsSpan(12)); png[19]=32; png[23]=16;
    Check("png-header-dimensions", EncodedImageInfo.Dimensions(png) == (32,16));
    var jpeg = new byte[] {255,216,255,192,0,8,8,0,16,0,32,1};
    Check("jpeg-header-dimensions", EncodedImageInfo.Dimensions(jpeg) == (32,16));
    bool RejectImage(byte[] image) { try { EncodedImageInfo.Dimensions(image); return false; } catch(ModelImportException) { return true; } }
    Check("reject-truncated-jpeg-header", RejectImage(jpeg[..^1]));
    png[16]=127; Check("reject-oversize-image-dimensions", RejectImage(png));
    if (args.Length > 0)
    {
        var clock = Stopwatch.StartNew(); var asset = GlbReader.Load(args[0]); clock.Stop();
        Check("user-model-visible-geometry", asset.TriangleCount > 100 && asset.TriangleCount <= GlbReader.TriangleBudget);
        Check("user-model-attributes", asset.Meshes.All(m => m.Positions.Length == m.Normals.Length && m.Positions.Length == m.UV.Length && m.Indices.All(i => i >= 0 && i < m.Positions.Length)));
        Check("user-model-texture", asset.Meshes.Any(m => m.Material.Image is { Length: > 0 }));
        var output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(temp, "optimized.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); GlbWriter.Write(asset, output);
        var reloadClock = Stopwatch.StartNew(); var reload = GlbReader.Load(output); reloadClock.Stop();
        Check("optimized-cache-roundtrip", asset.TriangleCount == reload.TriangleCount && asset.Meshes.Zip(reload.Meshes).All(p => p.First.Positions.SequenceEqual(p.Second.Positions) && p.First.Indices.SequenceEqual(p.Second.Indices) && p.First.Material.Image!.SequenceEqual(p.Second.Material.Image!)));
        report.Add(new { name = "user-model-metrics", asset.OriginalTriangles, asset.TriangleCount, asset.VertexCount,
            originalBytes = new FileInfo(args[0]).Length, cacheBytes = new FileInfo(output).Length, importMs = clock.ElapsedMilliseconds, reloadMs = reloadClock.ElapsedMilliseconds, output });
    }
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
finally { Directory.Delete(temp, true); }
