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
    private static async Task RunFaceLightingDiagnosticAsync(Application app, string[] args)
    {
        var reportPath = Path.GetFullPath(_report);
        var currentImage = Path.ChangeExtension(reportPath, ".current-lighting.png");
        var ambientOnlyImage = Path.ChangeExtension(reportPath, ".ambient-only.png");
        Window? window = null;
        var success = false;
        try
        {
            foreach (var path in new[] { reportPath, currentImage, ambientOnlyImage })
                if (File.Exists(path)) throw new IOException("Refusing to overwrite diagnostic output: " + path);

            var modelAt = Array.IndexOf(args, "--model-file");
            if (modelAt < 0 || modelAt + 1 >= args.Length) throw new ArgumentException("--model-file must name the existing GLB.");
            var modelPath = Path.GetFullPath(args[modelAt + 1]);
            var asset = GlbReader.Load(modelPath);
            var rig = asset.Rig ?? throw new InvalidDataException("The selected GLB has no skeletal rig.");
            var head = Array.FindIndex(rig.Nodes, node => string.Equals(node.Name, "mixamorig:Head", StringComparison.OrdinalIgnoreCase));
            if (head < 0) throw new InvalidDataException("The selected rig has no Mixamo Head bone.");

            var sampler = new RigSampler(asset) { PoseTransitionProgress = 1 };
            sampler.Sample(-1, 0);
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
            foreach (var points in sampler.Positions)
                foreach (var point in points) { sceneMin = Vector3.Min(sceneMin, point); sceneMax = Vector3.Max(sceneMax, point); }
            var sceneCenter = (sceneMin + sceneMax) * .5f;
            var sceneSize = Math.Max((sceneMax - sceneMin).X, Math.Max((sceneMax - sceneMin).Y, (sceneMax - sceneMin).Z));
            if (!float.IsFinite(sceneSize) || sceneSize < 1e-8f) throw new InvalidDataException("Rest-scene normalization bounds are invalid.");
            Vector3 Normalize(Vector3 point) => (point - sceneCenter) / sceneSize;

            var headMin = new Vector3(float.MaxValue);
            var headMax = new Vector3(float.MinValue);
            foreach (var item in headVertices)
            {
                var point = sampler.Positions[item.Mesh][item.Vertex];
                headMin = Vector3.Min(headMin, point);
                headMax = Vector3.Max(headMax, point);
            }
            var focus = Normalize((headMin + headMax) * .5f);
            var headExtent = (headMax - headMin) / sceneSize;
            var viewWidth = Math.Max(Math.Max(headExtent.X, headExtent.Y) * 1.35f, .045f);
            const int imageSize = 760;
            window = new Window
            {
                Title = "浅梦 · 脸部照明 A/B 诊断（测试宿主）",
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

            var staticAsset = MakeStaticAsset(asset, sampler);
            var variants = new[]
            {
                (Name: "directional-70", Image: currentImage, DirectionalIntensity: 70),
                (Name: "directional-50", Image: Path.ChangeExtension(reportPath, ".directional-50.png"), DirectionalIntensity: 50),
                (Name: "directional-30", Image: Path.ChangeExtension(reportPath, ".directional-30.png"), DirectionalIntensity: 30),
                (Name: "directional-0", Image: ambientOnlyImage, DirectionalIntensity: 0)
            };
            foreach (var variant in variants)
                if (File.Exists(variant.Image)) throw new IOException("Refusing to overwrite diagnostic image: " + variant.Image);
            foreach (var variant in variants)
            {
                var scene = ModelSceneFactory.Create(staticAsset, CancellationToken.None, sceneCenter, sceneSize);
                var lights = new Model3DGroup();
                lights.Children.Add(new AmbientLight(Color.FromRgb(160, 160, 160)));
                if (variant.DirectionalIntensity > 0)
                    lights.Children.Add(new DirectionalLight(Color.FromRgb((byte)variant.DirectionalIntensity, (byte)variant.DirectionalIntensity, (byte)variant.DirectionalIntensity), new Vector3D(-.5, -.5, -1)));
                lights.Children.Add(scene);
                visual.Content = lights;
                await window.Dispatcher.InvokeAsync(() => viewport.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
                SaveViewportImage(viewport, variant.Image);
            }

            var report = new
            {
                success = true,
                finishedAt = DateTimeOffset.Now,
                evidenceScope = "WindowsChecks WPF test host; the installed GLB is loaded read-only and its original base-color material, rest-pose geometry, sampler normals, camera and background are held constant. Directional light intensity is swept through RGB 70, 50, 30 and 0 while ambient light remains RGB 160. No production EXE or installer is exercised.",
                modelPath,
                modelSha256 = asset.SourceHash,
                savedOrModifiedModel = false,
                camera = new { target = new[] { focus.X, focus.Y, focus.Z }, orthographicWidth = viewWidth, imageSize },
                lighting = new
                {
                    ambientRgb = new[] { 160, 160, 160 },
                    testedDirectionalRgb = variants.Select(variant => new[] { variant.DirectionalIntensity, variant.DirectionalIntensity, variant.DirectionalIntensity }),
                    currentDirection = new[] { -.5, -.5, -1 },
                    variableChanged = "Directional intensity 70, 50, 30 and 0; ambient light and all model/material/camera inputs are constant."
                },
                images = variants.Select(variant => new { variant.Name, variant.Image }),
                limitation = "This controlled rest-pose lighting sweep can identify how the directional component contributes to face shading. It does not establish the model asset as root cause, full-motion appearance, naturalness, or installed-EXE behavior."
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            success = true;
            Console.WriteLine("Face lighting diagnostic saved: " + reportPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Face lighting diagnostic failed: " + ex);
            if (!File.Exists(reportPath))
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { success = false, error = ex.ToString(), savedOrModifiedModel = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (window is not null) window.Close();
            app.Shutdown(success ? 0 : 1);
        }
    }
}
