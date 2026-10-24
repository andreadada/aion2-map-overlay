using Aion2MapOverlay.Capture;
using System.Windows;

namespace Aion2MapOverlay.App;

// Persist coordinates relative to the chosen monitor, independent of resolution.
internal sealed record MinimapRegion(double Left, double Top, double Width, double Height)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height)
        && Left >= 0 && Top >= 0 && Width > 0 && Height > 0 && Left + Width <= 1.000001 && Top + Height <= 1.000001;

    public DisplayArea ToDisplayArea(DisplayArea display)
    {
        if (!IsValid) throw new ArgumentException("Invalid minimap selection.");
        var left = display.X + (int)Math.Round(Left * display.Width);
        var top = display.Y + (int)Math.Round(Top * display.Height);
        var right = display.X + (int)Math.Round((Left + Width) * display.Width);
        var bottom = display.Y + (int)Math.Round((Top + Height) * display.Height);
        return new DisplayArea(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public static MinimapRegion? FromScreenRectangle(DisplayArea display, Rect rectangle, int minimumSize = 72)
    {
        if (display.Width <= 0 || display.Height <= 0) return null;
        var left = Math.Clamp(rectangle.Left, display.X, display.X + display.Width);
        var top = Math.Clamp(rectangle.Top, display.Y, display.Y + display.Height);
        var right = Math.Clamp(rectangle.Right, display.X, display.X + display.Width);
        var bottom = Math.Clamp(rectangle.Bottom, display.Y, display.Y + display.Height);
        if (right - left < minimumSize || bottom - top < minimumSize) return null;
        return new((left - display.X) / display.Width, (top - display.Y) / display.Height,
            (right - left) / display.Width, (bottom - top) / display.Height);
    }
}
