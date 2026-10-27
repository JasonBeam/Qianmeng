using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopBuddy.UI;

// Transparent windows forward completely clear pixels to the desktop. Accessibility
// must expose a visible pixel, rather than the empty centre of an outline glyph/model.
internal static class PetHitPoint
{
    internal static Point Get(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1)
            return new Point(double.NaN, double.NaN);
        var width = (int)Math.Ceiling(element.ActualWidth); var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var local = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = local, ViewportUnits = BrushMappingMode.Absolute, Viewport = local, Stretch = Stretch.Fill }, null, local);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var best = double.PositiveInfinity; var bx = -1; var by = -1;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < 128) continue;
            var distance = Math.Pow(x + 0.5 - element.ActualWidth / 2, 2) + Math.Pow(y + 0.5 - element.ActualHeight / 2, 2);
            if (distance < best) { best = distance; bx = x; by = y; }
        }
        return bx < 0 ? new Point(double.NaN, double.NaN) : element.PointToScreen(new Point(bx + 0.5, by + 0.5));
    }
}

public sealed class PetGlyph : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GlyphPeer(this);
    private sealed class GlyphPeer(PetGlyph owner) : TextBlockAutomationPeer(owner)
    {
        protected override Point GetClickablePointCore() => PetHitPoint.Get(owner);
    }
}
public sealed class PetFallbackView : Canvas
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FallbackPeer(this);
    private sealed class FallbackPeer(PetFallbackView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PetFallbackView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override bool IsControlElementCore() => true;
        protected override Point GetClickablePointCore() => owner.PointToScreen(new Point(owner.ActualWidth / 2, owner.ActualHeight / 2));
    }
}
