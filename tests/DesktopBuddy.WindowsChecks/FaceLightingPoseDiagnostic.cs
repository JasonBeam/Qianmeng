using System.Numerics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private static async Task RunFaceLightingPoseDiagnosticAsync(Application app, string[] args)
    {
        var reportPath = Path.GetFullPath(_report);
        Window? window = null;
        var images = new List<object>();
        var success = false;
        try
        {
            if (File.Exists(reportPath)) throw new IOException("Refusing to overwrite diagnostic report: " + reportPath);
            var modelAt = Array.IndexOf(args, "--model-file");
            if (modelAt < 0 || modelAt + 1 >= args.Length) throw new ArgumentException("--model-file must name the existing GLB.");
            var modelPath = Path.GetFullPath(args[modelAt + 1]);
            var asset = GlbReader.Load(modelPath);
            var rig = asset.Rig ?? throw new InvalidDataException("The selected GLB has no skeletal rig.");
            var head = Array.FindIndex(rig.Nodes, node => string.Equals(node.Name, "mixamorig:Head", StringComparison.OrdinalIgnoreCase));
            if (head < 0) throw new InvalidDataException("The selected rig has no Mixamo Head bone.");
            var idleClip = RigClipResolver.FindIdle(rig.Clips);
            if (idleClip < 0) throw new InvalidDataException("The selected GLB has no idle clip.");
            if (!CompanionActionMotion.TryGetDefinition(CompanionActionKind.HeadScratching, out var scratchDefinition))
                throw new InvalidDataException("The current head-scratch action definition is unavailable.");

            var restSampler = new RigSampler(asset) { PoseTransitionProgress = 1 };
            restSampler.Sample(-1, 0);
            var headVertices = new List<(int Mesh, int Vertex)>();
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
                    if (influence >= .5f) headVertices.Add((meshIndex, vertex));
                }
            }
            if (headVertices.Count < 3) throw new InvalidDataException("Fewer than three head-dominant vertices were found.");

            var sceneMin = new Vector3(float.MaxValue);
            var sceneMax = new Vector3(float.MinValue);
            foreach (var points in restSampler.Positions)
                foreach (var point in points) { sceneMin = Vector3.Min(sceneMin, point); sceneMax = Vector3.Max(sceneMax, point); }
            var sceneCenter = (sceneMin + sceneMax) * .5f;
            var sceneSize = Math.Max((sceneMax - sceneMin).X, Math.Max((sceneMax - sceneMin).Y, (sceneMax - sceneMin).Z));
            if (!float.IsFinite(sceneSize) || sceneSize < 1e-8f) throw new InvalidDataException("Rest-scene normalization bounds are invalid.");
            Vector3 Normalize(Vector3 point) => (point - sceneCenter) / sceneSize;

            var headMin = new Vector3(float.MaxValue);
            var headMax = new Vector3(float.MinValue);
            foreach (var item in headVertices)
            {
                var point = restSampler.Positions[item.Mesh][item.Vertex];
                headMin = Vector3.Min(headMin, point);
                headMax = Vector3.Max(headMax, point);
            }
            var focus = Normalize((headMin + headMax) * .5f);
            var headExtent = (headMax - headMin) / sceneSize;
            var viewWidth = Math.Max(Math.Max(headExtent.X, headExtent.Y) * 1.35f, .045f);
            const int imageSize = 760;
            window = new Window
            {
                Title = "浅梦 · 脸部照明姿态对比（测试宿主）",
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
            var camera = new OrthographicCamera { UpDirection = new Vector3D(0, 1, 0), Width = viewWidth, NearPlaneDistance = .001, FarPlaneDistance = 4 };
            viewport.Camera = camera;
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

            var idleSeconds = Math.Min(.55, Math.Max(.1, rig.Clips[idleClip].Duration * .25));
            var poses = new[]
            {
                (Name: "rest", Clip: -1, ClipSeconds: 0d, ActionSeconds: (double?)null),
                (Name: "idle", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)null),
                (Name: "scratch-approach", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)(scratchDefinition.DurationSeconds * .30)),
                (Name: "scratch-contact", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)(scratchDefinition.DurationSeconds * .52)),
                (Name: "scratch-release", Clip: idleClip, ClipSeconds: idleSeconds, ActionSeconds: (double?)(scratchDefinition.DurationSeconds * .80))
            };
            var viewAngles = new[] { (Name: "front", Degrees: 0d), (Name: "three-quarter", Degrees: 35d) };
            var lightIntensities = new[] { 70, 30 };
            var poseSummary = new List<object>();
            foreach (var pose in poses)
            {
                var idle = pose.Name == "rest" ? default : CompanionIdleMotion.Sample(pose.ClipSeconds);
                CompanionActionPose action = pose.ActionSeconds is null ? default : CompanionActionMotion.Sample(CompanionActionKind.HeadScratching, pose.ActionSeconds.Value);
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
                foreach (var view in viewAngles)
                {
                    var yaw = view.Degrees * Math.PI / 180;
                    var offset = new Vector3(MathF.Sin((float)yaw) * .75f, 0, MathF.Cos((float)yaw) * .75f);
                    camera.Position = new Point3D(focus.X + offset.X, focus.Y + offset.Y, focus.Z + offset.Z);
                    camera.LookDirection = new Vector3D(-offset.X, -offset.Y, -offset.Z);
                    foreach (var intensity in lightIntensities)
                    {
                        var output = Path.ChangeExtension(reportPath, $".light-{intensity}.{view.Name}.{pose.Name}.png");
                        if (File.Exists(output)) throw new IOException("Refusing to overwrite diagnostic image: " + output);
                        var scene = ModelSceneFactory.Create(staticAsset, CancellationToken.None, sceneCenter, sceneSize);
                        var lights = new Model3DGroup();
                        lights.Children.Add(new AmbientLight(Color.FromRgb(160, 160, 160)));
                        lights.Children.Add(new DirectionalLight(Color.FromRgb((byte)intensity, (byte)intensity, (byte)intensity), new Vector3D(-.5, -.5, -1)));
                        lights.Children.Add(scene);
                        visual.Content = lights;
                        await window.Dispatcher.InvokeAsync(() => viewport.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
                        SaveViewportImage(viewport, output);
                        images.Add(new { pose = pose.Name, clipSeconds = pose.ClipSeconds, actionSeconds = pose.ActionSeconds, view = view.Name, viewYawDegrees = view.Degrees, directionalIntensity = intensity, image = output });
                    }
                }
                poseSummary.Add(new { pose = pose.Name, clip = pose.Clip < 0 ? "rest" : rig.Clips[pose.Clip].Name, clipSeconds = pose.ClipSeconds, actionSeconds = pose.ActionSeconds, actionPhase = pose.ActionSeconds / scratchDefinition.DurationSeconds });
            }

            var report = new
            {
                success = true,
                finishedAt = DateTimeOffset.Now,
                evidenceScope = "WindowsChecks WPF test host; the installed GLB is loaded read-only. Static rest/idle/head-scratch samples use production RigSampler and CompanionActionMotion. Ambient light, original material/texture, normalization, camera framing and background stay fixed; directional intensity 70 versus 30 and front versus three-quarter camera are compared. No production EXE or installer is exercised.",
                modelPath,
                modelSha256 = asset.SourceHash,
                savedOrModifiedModel = false,
                lighting = new { ambientRgb = new[] { 160, 160, 160 }, directionalRgbLevels = lightIntensities.Select(value => new[] { value, value, value }), direction = new[] { -.5, -.5, -1 } },
                camera = new { target = new[] { focus.X, focus.Y, focus.Z }, orthographicWidth = viewWidth, views = viewAngles },
                poses = poseSummary,
                images,
                limitation = "These are discrete WPF test-host stills, not a normal-speed continuous recording. They support visual comparison across sampled poses and camera angles but do not establish motion naturalness, head-contact correctness, source-asset correctness, or actual installed-EXE behavior."
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            success = true;
            Console.WriteLine("Face lighting pose diagnostic saved: " + reportPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Face lighting pose diagnostic failed: " + ex);
            if (!File.Exists(reportPath))
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { success = false, error = ex.ToString(), images, savedOrModifiedModel = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (window is not null) window.Close();
            app.Shutdown(success ? 0 : 1);
        }
    }
}
