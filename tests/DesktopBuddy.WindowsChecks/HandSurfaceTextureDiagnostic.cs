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
    private static async Task RunHandSurfaceTextureDiagnosticAsync(Application app, string[] args)
    {
        var success = false;
        var images = new List<string>();
        var reportPath = Path.GetFullPath(_report);
        try
        {
            if (File.Exists(reportPath)) throw new IOException("Refusing to overwrite diagnostic report: " + reportPath);
            var modelAt = Array.IndexOf(args, "--model-file");
            if (modelAt < 0 || modelAt + 1 >= args.Length) throw new ArgumentException("--model-file must name the existing GLB.");
            var modelPath = Path.GetFullPath(args[modelAt + 1]);
            var asset = GlbReader.Load(modelPath);
            if (asset.Rig is null || asset.Meshes.Length == 0 || asset.Rig.Meshes.Length == 0)
                throw new InvalidDataException("The model has no rigged mesh for the requested hand-face inspection.");

            const int meshIndex = 0;
            var rig = asset.Rig;
            var binding = rig.Meshes[meshIndex];
            if (binding.Skin < 0 || binding.Skin >= rig.Skins.Length || asset.Meshes[meshIndex].Indices.Length < 69)
                throw new InvalidDataException("The expected skinned mesh or triangle 21/22 is missing.");
            var skin = rig.Skins[binding.Skin];
            var restSampler = new RigSampler(asset);
            restSampler.Sample(-1, 0);
            var restMeshes = asset.Meshes.Select((sourceMesh, index) => sourceMesh with
            {
                Positions = (Vector3[])restSampler.Positions[index].Clone(),
                Normals = (Vector3[])restSampler.Normals[index].Clone()
            }).ToArray();
            var restAsset = asset with { Meshes = restMeshes, Rig = null, HasAnimations = false };
            var mesh = restAsset.Meshes[meshIndex];
            int[] targetVertices = [13, 14, 15, 16];
            if (targetVertices.Any(v => v < 0 || v >= mesh.Positions.Length)) throw new InvalidDataException("Target vertex is outside mesh zero.");
            var targetCenter = targetVertices.Select(v => mesh.Positions[v]).Aggregate(Vector3.Zero, (sum, point) => sum + point) / targetVertices.Length;

            var minimum = new Vector3(float.MaxValue);
            var maximum = new Vector3(float.MinValue);
            foreach (var sourceMesh in restAsset.Meshes)
                foreach (var point in sourceMesh.Positions)
                { minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point); }
            var center = (minimum + maximum) * .5f;
            var size = Math.Max((maximum - minimum).X, Math.Max((maximum - minimum).Y, (maximum - minimum).Z));
            if (!float.IsFinite(size) || size <= 1e-8f) throw new InvalidDataException("Model normalization bounds are invalid.");
            Vector3 Normalize(Vector3 p) => (p - center) / size;

            float HandInfluence(int vertex)
            {
                var joints = binding.Joints[vertex];
                var weights = binding.Weights[vertex];
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var localJoint = joints[k];
                    if (localJoint < 0 || localJoint >= skin.Joints.Length) continue;
                    var node = skin.Joints[localJoint];
                    if (node < 0 || node >= rig.Nodes.Length) continue;
                    if (rig.Nodes[node].Name.Contains(":RightHand", StringComparison.OrdinalIgnoreCase))
                        sum += k switch { 0 => weights.X, 1 => weights.Y, 2 => weights.Z, _ => weights.W };
                }
                return sum;
            }

            var selectedIndices = new List<int>();
            var selectedVertices = new HashSet<int>();
            var includedFaces = new List<int>();
            for (var at = 0; at + 2 < mesh.Indices.Length; at += 3)
            {
                var face = at / 3;
                var ids = new[] { mesh.Indices[at], mesh.Indices[at + 1], mesh.Indices[at + 2] };
                var centroid = (mesh.Positions[ids[0]] + mesh.Positions[ids[1]] + mesh.Positions[ids[2]]) / 3f;
                var influence = ids.Average(HandInfluence);
                if (influence < .08f || (Vector3.Distance(centroid, targetCenter) > .16f && face is not (21 or 22))) continue;
                selectedIndices.AddRange(ids);
                foreach (var id in ids) selectedVertices.Add(id);
                includedFaces.Add(face);
            }
            if (!includedFaces.Contains(21) || !includedFaces.Contains(22) || selectedIndices.Count == 0)
                throw new InvalidDataException("The hand close-up selection did not retain target faces 21/22.");

            var fullScene = ModelSceneFactory.Create(restAsset, CancellationToken.None);
            if (fullScene.Children.Count <= meshIndex || fullScene.Children[meshIndex] is not GeometryModel3D sourceModel || sourceModel.Geometry is not MeshGeometry3D sourceGeometry)
                throw new InvalidDataException("Could not obtain the original material and normalized WPF geometry.");

            // The optional full-scene view checks the exact product geometry/material path.
            // The diagnostic-only submesh remains available for a close crop; neither is written back.
            var fullSceneView = args.Contains("--full-scene");
            Model3DGroup scene;
            if (fullSceneView) scene = (Model3DGroup)fullScene.Clone();
            else
            {
                var closeUpGeometry = new MeshGeometry3D
                {
                    Positions = sourceGeometry.Positions,
                    Normals = sourceGeometry.Normals,
                    TextureCoordinates = sourceGeometry.TextureCoordinates,
                    TriangleIndices = new Int32Collection(selectedIndices)
                };
                scene = new Model3DGroup();
                scene.Children.Add(new GeometryModel3D(closeUpGeometry, sourceModel.Material) { BackMaterial = sourceModel.Material });
            }
            var markerColors = new Dictionary<string, Color>
            {
                ["v13"] = Color.FromRgb(255, 220, 0),
                ["v15"] = Color.FromRgb(0, 235, 255),
                ["v16"] = Color.FromRgb(255, 0, 210),
                ["t21"] = Color.FromRgb(255, 70, 45),
                ["t22"] = Color.FromRgb(80, 255, 100)
            };
            var wireLabels = args.Contains("--wire-labels");
            if (!wireLabels)
                foreach (var marker in new[]
                {
                    ("v13", Normalize(mesh.Positions[13]), .0024f),
                    ("v15", Normalize(mesh.Positions[15]), .0024f),
                    ("v16", Normalize(mesh.Positions[16]), .0024f),
                    ("t21", Normalize((mesh.Positions[mesh.Indices[63]] + mesh.Positions[mesh.Indices[64]] + mesh.Positions[mesh.Indices[65]]) / 3f), .0032f),
                    ("t22", Normalize((mesh.Positions[mesh.Indices[66]] + mesh.Positions[mesh.Indices[67]] + mesh.Positions[mesh.Indices[68]]) / 3f), .0032f)
                })
                    scene.Children.Add(new GeometryModel3D(Sphere(marker.Item2, marker.Item3), MarkerMaterial(markerColors[marker.Item1]))
                        { BackMaterial = MarkerMaterial(markerColors[marker.Item1]) });

            var selectedPoints = selectedVertices.Select(v => Normalize(mesh.Positions[v])).ToArray();
            var handMin = selectedPoints.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var handMax = selectedPoints.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            var focus = Normalize(targetCenter);
            var width = Math.Max(handMax.X - handMin.X, handMax.Y - handMin.Y) * 1.3;
            width = Math.Max(width, .04);
            var window = new Window
            {
                Title = "浅梦 · 原材质手部静态近景诊断",
                Width = 1100,
                Height = 850,
                Left = Math.Max(SystemParameters.VirtualScreenLeft + 12, SystemParameters.WorkArea.Right - 1120),
                Top = Math.Max(SystemParameters.VirtualScreenTop + 12, SystemParameters.WorkArea.Bottom - 870),
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(24, 29, 38))
            };
            if (wireLabels && !fullSceneView) throw new InvalidOperationException("Wire labels require the full original scene for occlusion checks.");
            var targetGeometry = scene.Children[meshIndex] is GeometryModel3D targetModel ? targetModel.Geometry as MeshGeometry3D : null;
            if (targetGeometry is null) throw new InvalidDataException("Target mesh is missing from the diagnostic scene.");
            var viewport = new Viewport3D { ClipToBounds = true, IsHitTestVisible = wireLabels };
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(165, 165, 165)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(145, 145, 145), new Vector3D(-.4, -.7, -1)));
            viewport.Children.Add(new ModelVisual3D { Content = lights });
            viewport.Children.Add(new ModelVisual3D { Content = scene });
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            legend.Children.Add(Legend(wireLabels ? "原材质静态近景；精确面线/绕序法线：" : "原材质静态近景；仅诊断标记：", Colors.White));
            foreach (var key in new[] { "v13", "v15", "v16", "t21", "t22" }) legend.Children.Add(Legend(key, markerColors[key]));
            var annotationCanvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            var root = new Grid();
            root.Children.Add(viewport);
            root.Children.Add(annotationCanvas);
            root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(210, 18, 22, 30)), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = legend });
            window.Content = root;
            app.MainWindow = window;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { root.Measure(new Size(window.Width, window.Height)); root.Arrange(new Rect(0, 0, window.Width, window.Height)); root.UpdateLayout(); }, System.Windows.Threading.DispatcherPriority.Render);

            var cameras = new List<object>();
            var annotationVisibility = new List<object>();
            void SetCamera(string viewName, Vector3D cameraOffset)
            {
                cameraOffset.Normalize();
                var distance = Math.Max(.5, width * 4);
                var camera = new OrthographicCamera
                {
                    Position = new Point3D(focus.X + cameraOffset.X * distance, focus.Y + cameraOffset.Y * distance, focus.Z + cameraOffset.Z * distance),
                    LookDirection = new Vector3D(-cameraOffset.X * distance, -cameraOffset.Y * distance, -cameraOffset.Z * distance),
                    UpDirection = new Vector3D(0, 1, 0),
                    Width = width * 1.3,
                    NearPlaneDistance = .001,
                    FarPlaneDistance = 20
                };
                var viewDirection = camera.LookDirection; viewDirection.Normalize();
                var viewUp = camera.UpDirection - viewDirection * Vector3D.DotProduct(camera.UpDirection, viewDirection); viewUp.Normalize();
                var viewRight = Vector3D.CrossProduct(viewDirection, viewUp); viewRight.Normalize();
                viewUp = Vector3D.CrossProduct(viewRight, viewDirection); viewUp.Normalize();
                double ProjectedSpan(Vector3D axis)
                {
                    var values = selectedPoints.Select(p => (p.X - focus.X) * axis.X + (p.Y - focus.Y) * axis.Y + (p.Z - focus.Z) * axis.Z).ToArray();
                    return values.Max() - values.Min();
                }
                var viewAspect = Math.Max(.1, viewport.ActualWidth / Math.Max(1, viewport.ActualHeight));
                camera.Width = Math.Max(ProjectedSpan(viewRight), ProjectedSpan(viewUp) * viewAspect) * 1.3;
                camera.Width = Math.Max(camera.Width, .04);
                viewport.Camera = camera;
                window.Title = "浅梦 · 原材质手部静态近景诊断 · " + viewName;
                cameras.Add(new { view = viewName, cameraPosition = new[] { camera.Position.X, camera.Position.Y, camera.Position.Z }, lookDirection = new[] { camera.LookDirection.X, camera.LookDirection.Y, camera.LookDirection.Z }, orthographicWidth = camera.Width });
            }

            Point Project(Vector3 p, OrthographicCamera camera)
            {
                var direction = camera.LookDirection; direction.Normalize();
                var up = camera.UpDirection - direction * Vector3D.DotProduct(camera.UpDirection, direction); up.Normalize();
                var right = Vector3D.CrossProduct(direction, up); right.Normalize();
                up = Vector3D.CrossProduct(right, direction); up.Normalize();
                var relative = new Vector3D(p.X - camera.Position.X, p.Y - camera.Position.Y, p.Z - camera.Position.Z);
                var scale = viewport.ActualWidth / camera.Width;
                return new Point(viewport.ActualWidth * .5 + Vector3D.DotProduct(relative, right) * scale,
                    viewport.ActualHeight * .5 - Vector3D.DotProduct(relative, up) * scale);
            }

            bool VisibleAt(Vector3 point, int expectedFace, OrthographicCamera camera, out Point screen, out int hitTriangle, out double depthError)
            {
                screen = Project(point, camera); hitTriangle = -1; depthError = double.NaN;
                if (screen.X < 0 || screen.Y < 0 || screen.X >= viewport.ActualWidth || screen.Y >= viewport.ActualHeight) return false;
                var hit = VisualTreeHelper.HitTest(viewport, screen) as RayMeshGeometry3DHitTestResult;
                if (hit is null) return false;
                var hitIds = new[] { hit.VertexIndex1, hit.VertexIndex2, hit.VertexIndex3 }.OrderBy(v => v).ToArray();
                var face21 = new[] { mesh.Indices[63], mesh.Indices[64], mesh.Indices[65] }.OrderBy(v => v);
                var face22 = new[] { mesh.Indices[66], mesh.Indices[67], mesh.Indices[68] }.OrderBy(v => v);
                hitTriangle = hitIds.SequenceEqual(face21) ? 21 : hitIds.SequenceEqual(face22) ? 22 : -1;
                var direction = camera.LookDirection; direction.Normalize();
                var expected = Vector3D.DotProduct(new Vector3D(point.X - camera.Position.X, point.Y - camera.Position.Y, point.Z - camera.Position.Z), direction);
                depthError = hit.DistanceToRayOrigin - expected;
                return ReferenceEquals(hit.MeshHit, targetGeometry) && Math.Abs(depthError) < .004 && (expectedFace < 0 || hitTriangle == expectedFace);
            }

            void AddLabel(string text, Point position, Color color, double dx = 5, double dy = -12)
            {
                var label = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color), Background = new SolidColorBrush(Color.FromArgb(170, 8, 10, 14)), Padding = new Thickness(2, 0, 2, 0) };
                Canvas.SetLeft(label, position.X + dx); Canvas.SetTop(label, position.Y + dy); annotationCanvas.Children.Add(label);
            }

            void DrawAnnotations(string viewName, OrthographicCamera camera)
            {
                var faceResults = new List<object>();
                foreach (var face in new[] { 21, 22 })
                {
                    var ids = new[] { mesh.Indices[face * 3], mesh.Indices[face * 3 + 1], mesh.Indices[face * 3 + 2] };
                    var points = ids.Select(v => Normalize(mesh.Positions[v])).ToArray();
                    var centroid = (points[0] + points[1] + points[2]) / 3f;
                    var centerVisible = VisibleAt(centroid, face, camera, out var centerScreen, out var hitTriangle, out var centerDepthError);
                    var vertexVisible = new List<bool>();
                    var projected = new List<Point>();
                    for (var i = 0; i < 3; i++)
                    {
                        var visible = VisibleAt(points[i], -1, camera, out var pixel, out _, out _);
                        vertexVisible.Add(visible); projected.Add(pixel);
                    }
                    var color = markerColors["t" + face];
                    if (centerVisible && vertexVisible.All(v => v))
                    {
                        var outline = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.4, Points = new PointCollection(projected) };
                        outline.Points.Add(projected[0]); annotationCanvas.Children.Add(outline);
                        AddLabel("face " + face, centerScreen, color);
                        var normal = Vector3.Normalize(Vector3.Cross(points[1] - points[0], points[2] - points[0]));
                        var normalEnd = centroid + normal * .012f;
                        var endScreen = Project(normalEnd, camera);
                        annotationCanvas.Children.Add(new System.Windows.Shapes.Line { X1 = centerScreen.X, Y1 = centerScreen.Y, X2 = endScreen.X, Y2 = endScreen.Y, Stroke = new SolidColorBrush(color), StrokeThickness = 1.3 });
                        AddLabel("winding normal", endScreen, color, 3, -9);
                    }
                    if (centerVisible) AddLabel("f" + face, centerScreen, color, 4, 4);
                    faceResults.Add(new { face, vertices = ids, centerVisible, vertexVisible, hitTriangle, centerDepthError, projectedTriangleDrawn = centerVisible && vertexVisible.All(v => v) });
                }
                foreach (var vertex in new[] { 13, 15, 16 })
                {
                    var point = Normalize(mesh.Positions[vertex]);
                    var visible = VisibleAt(point, -1, camera, out var screen, out var hitTriangle, out var depthError);
                    if (visible)
                    {
                        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Stroke = new SolidColorBrush(markerColors["v" + vertex]), StrokeThickness = 1.2 };
                        Canvas.SetLeft(dot, screen.X - 3); Canvas.SetTop(dot, screen.Y - 3); annotationCanvas.Children.Add(dot);
                        AddLabel("v" + vertex, screen, markerColors["v" + vertex]);
                    }
                    faceResults.Add(new { vertex, visible, hitTriangle, depthError });
                }
                annotationVisibility.Add(new { view = viewName, surfaces = faceResults });
            }

            foreach (var (view, offset) in new[] { ("wpf-oblique-plus-z", new Vector3D(.35, .25, .90)), ("wpf-oblique-minus-z", new Vector3D(-.35, .25, -.90)) })
            {
                var imagePath = Path.ChangeExtension(reportPath, "." + view + ".png");
                if (File.Exists(imagePath)) throw new IOException("Refusing to overwrite diagnostic image: " + imagePath);
                SetCamera(view, offset);
                await window.Dispatcher.InvokeAsync(() => { root.Measure(new Size(window.Width, window.Height)); root.Arrange(new Rect(0, 0, window.Width, window.Height)); root.UpdateLayout(); }, System.Windows.Threading.DispatcherPriority.Render);
                await Task.Delay(120);
                annotationCanvas.Children.Clear();
                if (wireLabels && viewport.Camera is OrthographicCamera camera) DrawAnnotations(view, camera);
                SaveWindowImage(window, imagePath);
                images.Add(imagePath);
            }
            window.Close();
            success = true;
            var report = new
            {
                success,
                finishedAt = DateTimeOffset.Now,
                evidenceScope = "WPF WindowsChecks static rest-skinned pose using RigSampler.Sample(-1,0), ModelSceneFactory and the source material; no animation, production EXE, NSIS, or runtime action changes.",
                renderedGeometry = fullSceneView ? "full original scene clone" : "diagnostic hand submesh",
                modelPath,
                sourceHash = asset.SourceHash,
                savedOrModifiedModel = false,
                selectedTriangleCount = includedFaces.Count,
                selectedFaceRange = new { minimum = includedFaces.Min(), maximum = includedFaces.Max() },
                targetFaces21and22Included = true,
                selectedVertices = selectedVertices.Count,
                uvCoordinates = targetVertices.ToDictionary(v => v.ToString(), v => new[] { mesh.UV[v].X, mesh.UV[v].Y }),
                targetVertices = targetVertices.ToDictionary(v => v.ToString(), v => new[] { mesh.Positions[v].X, mesh.Positions[v].Y, mesh.Positions[v].Z }),
                candidateFace21Vertices = new[] { mesh.Indices[63], mesh.Indices[64], mesh.Indices[65] },
                candidateFace22Vertices = new[] { mesh.Indices[66], mesh.Indices[67], mesh.Indices[68] },
                cameraViews = cameras,
                wireLabels,
                annotationVisibility,
                renderedImages = images,
                markerLegend = markerColors.Keys.ToArray(),
                limitation = "This rest-pose close-up can confirm visible material mapping and surface placement only; it cannot certify head-contact semantics or action naturalness."
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(0);
        }
        catch (Exception ex)
        {
            if (File.Exists(reportPath)) throw;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { success = false, error = ex.ToString(), renderedImages = images }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(1);
        }

        static Material MarkerMaterial(Color color)
        {
            var brush = new SolidColorBrush(color);
            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(brush));
            group.Children.Add(new EmissiveMaterial(brush));
            group.Freeze();
            return group;
        }

        static FrameworkElement Legend(string text, Color color) => new TextBlock
        {
            Text = text + "  ",
            Foreground = new SolidColorBrush(color),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(5, 1, 3, 1)
        };

        static MeshGeometry3D Sphere(Vector3 center, float radius)
        {
            const int latitudes = 8;
            const int longitudes = 12;
            var geometry = new MeshGeometry3D();
            for (var lat = 0; lat <= latitudes; lat++)
            {
                var phi = Math.PI * lat / latitudes;
                for (var lon = 0; lon <= longitudes; lon++)
                {
                    var theta = 2 * Math.PI * lon / longitudes;
                    var normal = new Vector3(MathF.Sin((float)phi) * MathF.Cos((float)theta), MathF.Cos((float)phi), MathF.Sin((float)phi) * MathF.Sin((float)theta));
                    geometry.Positions.Add(new Point3D(center.X + normal.X * radius, center.Y + normal.Y * radius, center.Z + normal.Z * radius));
                    geometry.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
                }
            }
            for (var lat = 0; lat < latitudes; lat++)
                for (var lon = 0; lon < longitudes; lon++)
                {
                    var a = lat * (longitudes + 1) + lon;
                    var b = a + longitudes + 1;
                    geometry.TriangleIndices.Add(a); geometry.TriangleIndices.Add(b); geometry.TriangleIndices.Add(a + 1);
                    geometry.TriangleIndices.Add(a + 1); geometry.TriangleIndices.Add(b); geometry.TriangleIndices.Add(b + 1);
                }
            return geometry;
        }
    }
}
