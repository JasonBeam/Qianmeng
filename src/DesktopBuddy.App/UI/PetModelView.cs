using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DesktopBuddy.UI;

/// <summary>Retained WPF scene: no frame timer, browser process, video loop or separate HWND.</summary>
public sealed class PetModelView : Grid
{
    private readonly ModelVisual3D _model = new();
    private double _yaw;
    private readonly AxisAngleRotation3D _baseYaw = new(new Vector3D(0,1,0),0);
    private readonly ScaleTransform3D _baseScale = new();
    private readonly AxisAngleRotation3D _gestureYaw = new(new Vector3D(0,1,0),0);
    private readonly AxisAngleRotation3D _gestureRoll = new(new Vector3D(0,0,1),0);
    private readonly ScaleTransform3D _gestureScale = new();
    private readonly TranslateTransform3D _gestureLift = new();
    private readonly OrthographicCamera _camera = new(new Point3D(0, 0, 3), new Vector3D(0, 0, -3), new Vector3D(0, 1, 0), 1.45)
    { NearPlaneDistance = 0.01, FarPlaneDistance = 20 };

    public PetModelView()
    {
        var pose = new Transform3DGroup();
        pose.Children.Add(new RotateTransform3D(_baseYaw));pose.Children.Add(_baseScale);
        pose.Children.Add(new RotateTransform3D(_gestureYaw));pose.Children.Add(new RotateTransform3D(_gestureRoll));
        pose.Children.Add(_gestureScale);pose.Children.Add(_gestureLift);_model.Transform=pose;
        ClipToBounds = true;
        var viewport = new Viewport3D { Camera = _camera, ClipToBounds = true };
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(160, 160, 160)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(30, 30, 30), new Vector3D(-0.5, -0.5, -1)));
        lights.Freeze(); viewport.Children.Add(new ModelVisual3D { Content = lights }); viewport.Children.Add(_model); Children.Add(viewport);
        SizeChanged += (_, _) => FitCamera();
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PetPeer(this);

    private sealed class PetPeer(PetModelView owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PetModelView);
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Custom;
        protected override bool IsControlElementCore() => true;
        protected override Point GetClickablePointCore() => PetHitPoint.Get(owner);
    }

    public Model3DGroup? Scene { get => _model.Content as Model3DGroup; set { _model.Content = value; FitCamera(); } }

    private Rect3D? _framingBounds;
    public Rect3D? FramingBounds { get => _framingBounds; set { _framingBounds = value; FitCamera(); } }
    private void FitCamera()
    {
        var bounds = FramingBounds ?? Scene?.Bounds ?? Rect3D.Empty;
        if (bounds.IsEmpty || ActualWidth < 1 || ActualHeight < 1) return;
        var radians = _yaw * Math.PI / 180;
        var projectedWidth = Math.Abs(Math.Cos(radians)) * bounds.SizeX + Math.Abs(Math.Sin(radians)) * bounds.SizeZ;
        var aspect = ActualWidth / ActualHeight;
        // Width is horizontal in OrthographicCamera: portrait viewports need a smaller
        // width, otherwise a tall character wastes almost half of its stage height.
        _camera.Width = 1.2 * Math.Max(0.01, Math.Max(projectedWidth, bounds.SizeY * aspect));
    }
    public void SetPose(double yaw, double scale)
    {
        _yaw = yaw;
        _baseYaw.Angle=yaw;_baseScale.ScaleX=_baseScale.ScaleY=_baseScale.ScaleZ=scale;FitCamera();
    }
    public void SetInteractionPose(double yaw,double roll,double scale,double lift)
    {
        _gestureYaw.Angle=yaw;_gestureRoll.Angle=roll;
        _gestureScale.ScaleX=_gestureScale.ScaleY=_gestureScale.ScaleZ=scale;_gestureLift.OffsetY=lift;
    }
}
