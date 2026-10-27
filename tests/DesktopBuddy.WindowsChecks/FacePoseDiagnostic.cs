using System.Numerics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private static async Task RunFacePoseDiagnosticAsync(Application app, string[] args)
    {
        var reportPath = Path.GetFullPath(_report);
        var images = new List<string>();
        Window? window = null;
        var success = false;
        try
        {
            if (File.Exists(reportPath)) throw new IOException("Refusing to overwrite diagnostic report: " + reportPath);
            var modelAt = Array.IndexOf(args, "--model-file");
            if (modelAt < 0 || modelAt + 1 >= args.Length) throw new ArgumentException("--model-file must name the existing GLB.");
            var modelPath = Path.GetFullPath(args[modelAt + 1]);
            var asset = GlbReader.Load(modelPath);
            var rig = asset.Rig ?? throw new InvalidDataException("The selected GLB has no skeletal rig.");
            var head = Array.FindIndex(rig.Nodes, n => string.Equals(n.Name, "mixamorig:Head", StringComparison.OrdinalIgnoreCase));
            if (head < 0) throw new InvalidDataException("The selected rig has no Mixamo Head bone.");
            var idleClip = RigClipResolver.FindIdle(rig.Clips);
            if (idleClip < 0) throw new InvalidDataException("The selected GLB has no idle clip.");
            if (!CompanionActionMotion.TryGetDefinition(CompanionActionKind.HeadScratching, out var scratchDefinition))
                throw new InvalidDataException("The current head-scratch action definition is unavailable.");

            var restSampler = new RigSampler(asset);
            restSampler.PoseTransitionProgress = 1;
            restSampler.Sample(-1, 0);
            var headVertices = new List<(int Mesh, int Vertex, float Weight)>();
            for (var meshIndex = 0; meshIndex < asset.Meshes.Length; meshIndex++)
            {
                var binding = rig.Meshes[meshIndex];
                if (binding.Skin < 0 || binding.Skin >= rig.Skins.Length) continue;
                var skin = rig.Skins[binding.Skin];
                for (var vertex = 0; vertex < binding.Joints.Length; vertex++)
                {
                    var ids = binding.Joints[vertex];
                    var weights = binding.Weights[vertex];
                    var influence = 0f;
                    for (var slot = 0; slot < 4; slot++)
                    {
                        var weight = slot switch { 0 => weights.X, 1 => weights.Y, 2 => weights.Z, _ => weights.W };
                        var joint = ids[slot];
                        if (weight > 0 && joint >= 0 && joint < skin.Joints.Length && skin.Joints[joint] == head) influence += weight;
                    }
                    if (influence >= .5f) headVertices.Add((meshIndex, vertex, influence));
                }
            }
            if (headVertices.Count < 3) throw new InvalidDataException("Fewer than three head-dominant vertices were found.");

            var sceneMin = new Vector3(float.MaxValue);
            var sceneMax = new Vector3(float.MinValue);
            foreach (var points in restSampler.Positions)
                foreach (var p in points) { sceneMin = Vector3.Min(sceneMin, p); sceneMax = Vector3.Max(sceneMax, p); }
            var sceneCenter = (sceneMin + sceneMax) * .5f;
            var sceneSize = Math.Max((sceneMax - sceneMin).X, Math.Max((sceneMax - sceneMin).Y, (sceneMax - sceneMin).Z));
            if (!float.IsFinite(sceneSize) || sceneSize < 1e-8f) throw new InvalidDataException("Rest-scene normalization bounds are invalid.");
            Vector3 Normalize(Vector3 p) => (p - sceneCenter) / sceneSize;

            var headMin = new Vector3(float.MaxValue);
            var headMax = new Vector3(float.MinValue);
            foreach (var sample in headVertices)
            {
                var p = restSampler.Positions[sample.Mesh][sample.Vertex];
                headMin = Vector3.Min(headMin, p);
                headMax = Vector3.Max(headMax, p);
            }
            var headCenterRaw = (headMin + headMax) * .5f;
            var headExtent = (headMax - headMin) / sceneSize;
            var focus = Normalize(headCenterRaw);
            var viewWidth = Math.Max(Math.Max(headExtent.X, headExtent.Y) * 1.35f, .045f);

            const int imageSize = 760;
            window = new Window
            {
                Title = "浅梦 · 原模型脸部同角度对照（测试宿主）",
                Width = imageSize,
                Height = imageSize,
                Left = Math.Max(SystemParameters.VirtualScreenLeft + 12, SystemParameters.WorkArea.Right - imageSize - 28),
                Top = Math.Max(SystemParameters.VirtualScreenTop + 12, SystemParameters.WorkArea.Bottom - imageSize - 28),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = new SolidColorBrush(Color.FromRgb(24, 29, 38))
            };
            var viewport = new Viewport3D { ClipToBounds = true, IsHitTestVisible = false };
            viewport.Camera = new OrthographicCamera
            {
                Position = new Point3D(focus.X, focus.Y, focus.Z + .75),
                LookDirection = new Vector3D(0, 0, -.75),
                UpDirection = new Vector3D(0, 1, 0),
                Width = viewWidth,
                NearPlaneDistance = .001,
                FarPlaneDistance = 4
            };
            var visual = new ModelVisual3D();
            viewport.Children.Add(visual);
            window.Content = viewport;
            app.MainWindow = window;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Show();
            await window.Dispatcher.InvokeAsync(() =>
            {
                viewport.Measure(new Size(imageSize, imageSize));
                viewport.Arrange(new Rect(0, 0, imageSize, imageSize));
                viewport.UpdateLayout();
            }, System.Windows.Threading.DispatcherPriority.Render);

            var normalReference = restSampler.Normals;
            var poseReports = new List<object>();
            var idleSeconds = Math.Min(.55, Math.Max(.1, rig.Clips[idleClip].Duration * .25));
            var requested = new[]
            {
                (Name: "rest", Clip: -1, ClipSeconds: 0d, ActionSeconds: (double?)null),
                (Name: "idle", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)null),
                (Name: "scratch-approach", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)(scratchDefinition.DurationSeconds * .30)),
                (Name: "scratch-contact", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)(scratchDefinition.DurationSeconds * .52)),
                (Name: "scratch-release", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)(scratchDefinition.DurationSeconds * .80))
            };

            foreach (var pose in requested)
            {
                if (pose.ActionSeconds is null)
                {
                    var idle = pose.Name == "rest" ? default : CompanionIdleMotion.Sample(pose.ClipSeconds);
                    var sampler = new RigSampler(asset) { PoseTransitionProgress = 1, CompanionIdleBody = idle.Body, CompanionIdleHead = idle.Head, CompanionBreathing = idle.Breathing };
                    sampler.Sample(pose.Clip, pose.ClipSeconds);
                    var staticAsset = MakeStaticAsset(asset, sampler);
                    visual.Content = SceneWithLights(ModelSceneFactory.Create(staticAsset, CancellationToken.None, sceneCenter, sceneSize));
                    await window.Dispatcher.InvokeAsync(() => viewport.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
                    var normalStats = DescribeHeadNormals(asset, sampler, normalReference, headVertices);
                    var imagePath = Path.ChangeExtension(reportPath, "." + pose.Name + ".png");
                    SaveViewportImage(viewport, imagePath);
                    images.Add(imagePath);
                    poseReports.Add(new { pose = pose.Name, clip = pose.Clip < 0 ? "rest" : rig.Clips[pose.Clip].Name, clipSeconds = pose.ClipSeconds, actionSeconds = (double?)null, actionPhase = (double?)null, normalStats });
                }
                else
                {
                    var idle = CompanionIdleMotion.Sample(pose.ClipSeconds);
                    var action = CompanionActionMotion.Sample(CompanionActionKind.HeadScratching, pose.ActionSeconds.Value);
                    var sampler = new RigSampler(asset)
                    {
                        PoseTransitionProgress = 1,
                        CompanionIdleBody = idle.Body,
                        CompanionIdleHead = idle.Head,
                        CompanionBreathing = idle.Breathing,
                        CompanionAction = action
                    };
                    sampler.Sample(pose.Clip, pose.ClipSeconds);
                    var staticAsset = MakeStaticAsset(asset, sampler);
                    visual.Content = SceneWithLights(ModelSceneFactory.Create(staticAsset, CancellationToken.None, sceneCenter, sceneSize));
                    await window.Dispatcher.InvokeAsync(() => viewport.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
                    var normalStats = DescribeHeadNormals(asset, sampler, normalReference, headVertices);
                    var imagePath = Path.ChangeExtension(reportPath, "." + pose.Name + ".png");
                    SaveViewportImage(viewport, imagePath);
                    images.Add(imagePath);
                    poseReports.Add(new { pose = pose.Name, clip = rig.Clips[pose.Clip].Name, clipSeconds = pose.ClipSeconds, actionSeconds = pose.ActionSeconds, actionPhase = pose.ActionSeconds / scratchDefinition.DurationSeconds, normalStats });
                }
            }

            var report = new
            {
                success = true,
                finishedAt = DateTimeOffset.Now,
                evidenceScope = "WindowsChecks WPF test host; the current installed GLB is loaded read-only. Original base-color material and production RigSampler are used. Static pose captures use one fixed orthographic front camera, shared normalization and lighting. No production EXE or installer is exercised.",
                modelPath,
                modelSha256 = asset.SourceHash,
                savedOrModifiedModel = false,
                material = new { colorFactor = new[] { asset.Meshes[0].Material.Color.X, asset.Meshes[0].Material.Color.Y, asset.Meshes[0].Material.Color.Z, asset.Meshes[0].Material.Color.W }, mimeType = asset.Meshes[0].Material.MimeType, embeddedBaseColorBytes = asset.Meshes[0].Material.Image?.Length ?? 0 },
                skeleton = new { headNode = rig.Nodes[head].Name, idleClip = rig.Clips[idleClip].Name, headDominantVertexCount = headVertices.Count, headInfluenceThreshold = .5f },
                scene = new { restBoundsMin = new[] { sceneMin.X, sceneMin.Y, sceneMin.Z }, restBoundsMax = new[] { sceneMax.X, sceneMax.Y, sceneMax.Z }, sceneSize, headBoundsMin = new[] { headMin.X, headMin.Y, headMin.Z }, headBoundsMax = new[] { headMax.X, headMax.Y, headMax.Z } },
                camera = new { position = new[] { focus.X, focus.Y, focus.Z + .75f }, lookDirection = new[] { 0, 0, -.75f }, upDirection = new[] { 0, 1, 0 }, orthographicWidth = viewWidth },
                images,
                poses = poseReports,
                limitation = "These are same-angle stills from the WPF test host. They support appearance/material/normal comparison but do not establish full-speed motion naturalness or actual installed-EXE behavior."
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            success = true;
            Console.WriteLine("Face pose diagnostic saved: " + reportPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Face pose diagnostic failed: " + ex);
            if (!File.Exists(reportPath))
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { success = false, error = ex.ToString(), savedOrModifiedModel = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (window is not null) window.Close();
            app.Shutdown(success ? 0 : 1);
        }
    }

    private static ModelAsset MakeStaticAsset(ModelAsset source, RigSampler sampler)
    {
        var meshes = source.Meshes.Select((mesh, index) => mesh with
        {
            Positions = (Vector3[])sampler.Positions[index].Clone(),
            Normals = (Vector3[])sampler.Normals[index].Clone()
        }).ToArray();
        return source with { Meshes = meshes, Rig = null, HasAnimations = false };
    }

    private static Model3DGroup SceneWithLights(Model3DGroup scene)
    {
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(165, 165, 165)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(145, 145, 145), new Vector3D(-.4, -.7, -1)));
        group.Children.Add(scene);
        return group;
    }

    private static object DescribeHeadNormals(ModelAsset asset, RigSampler sampler, Vector3[][] baseline, List<(int Mesh, int Vertex, float Weight)> vertices)
    {
        var lengths = new List<float>();
        var anglesFromSource = new List<float>();
        var anglesFromRest = new List<float>();
        var invalid = 0;
        foreach (var item in vertices)
        {
            var current = sampler.Normals[item.Mesh][item.Vertex];
            var source = asset.Meshes[item.Mesh].Normals[item.Vertex];
            var rest = baseline[item.Mesh][item.Vertex];
            if (!Finite(current) || current.LengthSquared() < 1e-10f) { invalid++; continue; }
            lengths.Add(current.Length());
            if (Finite(source) && source.LengthSquared() > 1e-10f)
                anglesFromSource.Add(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(source), Vector3.Normalize(current)), -1, 1)) * (180 / MathF.PI));
            if (Finite(rest) && rest.LengthSquared() > 1e-10f)
                anglesFromRest.Add(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(rest), Vector3.Normalize(current)), -1, 1)) * (180 / MathF.PI));
        }
        static object Stats(List<float> values) => values.Count == 0 ? new { count = 0, mean = (float?)null, max = (float?)null } : new { count = values.Count, mean = (float?)values.Average(), max = (float?)values.Max() };
        return new { headWeightedVertices = vertices.Count, invalidOrZeroCurrentNormals = invalid, currentNormalLength = Stats(lengths), degreesFromSourceNormal = Stats(anglesFromSource), degreesFromRestNormal = Stats(anglesFromRest) };
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static void SaveViewportImage(Viewport3D viewport, string imagePath)
    {
        if (File.Exists(imagePath)) throw new IOException("Refusing to overwrite diagnostic image: " + imagePath);
        viewport.UpdateLayout();
        var width = Math.Max(1, (int)Math.Round(viewport.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(viewport.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(imagePath);
        encoder.Save(stream);
    }
}
