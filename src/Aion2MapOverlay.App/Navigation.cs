using Aion2MapOverlay.Capture;
using Aion2MapOverlay.Core.Markers;
using Aion2MapOverlay.Vision;

namespace Aion2MapOverlay.App;

internal sealed record GuidanceFrame(double CenterX, double CenterY, double Radius, double Angle, string Name, string GroupKey, bool Large, DateTime CapturedAt);

internal sealed record MinimapFrame(PlacedMarker[] Markers, double Left, double Top, double Width, double Height, bool Large, DateTime CapturedAt, string? TargetId = null, IReadOnlySet<string>? ObservedIds = null);

internal static class Navigation
{
    public static DisplayArea MinimapArea(DisplayArea display, bool large, MinimapRegion? custom = null)
    {
        if (custom?.IsValid == true) return custom.ToDisplayArea(display);
        return new(display.X + (int)(display.Width * (large ? .859 : .868)),
            display.Y + (int)(display.Height * .089),
            (int)(display.Width * (large ? .130 : .122)),
            (int)(display.Height * (large ? .338 : .138)));
    }

    public static MapMarker? Nearest(IEnumerable<MapMarker> markers, HashSet<string> enabled, MinimapFix fix, MapMarker? previous = null)
    {
        var candidates = markers.Where(m => m.Type == "gatherable" && enabled.Contains(m.GroupKey));
        static double Distance(MapMarker m, MinimapFix f) => Math.Pow(m.ReferenceX - f.PlayerX, 2) + Math.Pow(m.ReferenceY - f.PlayerY, 2);
        var nearest = candidates.MinBy(m => Distance(m, fix));
        // Keep the current target when two nodes are almost tied; prevent pointer flicker.
        if (nearest is not null && previous is not null && enabled.Contains(previous.GroupKey) &&
            Distance(previous, fix) <= Distance(nearest, fix) * 1.21) return previous;
        return nearest;
    }

    public static GuidanceFrame Guide(MapMarker target, MinimapFix fix, DisplayArea display, DisplayArea area, bool large, DateTime capturedAt, double cameraHeading)
    {
        // The cone heading is measured in minimap axes; straight ahead on the HUD is up.
        var bearing = Math.Atan2(target.ReferenceY - fix.PlayerY, target.ReferenceX - fix.PlayerX) + fix.Rotation;
        var angle = bearing - cameraHeading - Math.PI / 2;
        return new(.5, .60, .045,
            Math.Atan2(Math.Sin(angle), Math.Cos(angle)),
            target.Name, target.GroupKey, large, capturedAt);
    }
    // The minimap is north-up: point along the map bearing instead of estimating camera heading.
    public static GuidanceFrame GuideOnMinimap(MapMarker target, MinimapFix fix, DisplayArea display,
        DisplayArea area, bool large, DateTime capturedAt, bool visuallyObserved)
    {
        var bearing = Math.Atan2(target.ReferenceY - fix.PlayerY, target.ReferenceX - fix.PlayerX) + fix.Rotation;
        var x = (area.X - display.X + area.Width / 2.0) / display.Width;
        var y = (area.Y - display.Y + area.Height / 2.0) / display.Height;
        var radius = Math.Min(area.Width, area.Height) * .20 / display.Width;
        return new(x, y, radius, Math.Atan2(Math.Sin(bearing), Math.Cos(bearing)),
            target.Name + (visuallyObserved ? " · icon seen" : " · possible spawn"), target.GroupKey, large, capturedAt);
    }

    public static MinimapFrame ProjectMinimap(MarkerIndex index, HashSet<string> enabled, MinimapFix fix,
        DisplayArea display, DisplayArea area, bool large, DateTime capturedAt)
    {
        var reach = Math.Sqrt(area.Width * area.Width + area.Height * area.Height) / (2 * fix.Scale);
        var cosine = Math.Cos(fix.Rotation); var sine = Math.Sin(fix.Rotation);
        var markers = index.Query(fix.PlayerX - reach, fix.PlayerY - reach, fix.PlayerX + reach, fix.PlayerY + reach)
            .Where(m => enabled.Contains(m.GroupKey)).Select(m =>
            {
                var dx = m.ReferenceX - fix.PlayerX; var dy = m.ReferenceY - fix.PlayerY;
                var x = area.Width / 2.0 + (cosine * dx - sine * dy) * fix.Scale;
                var y = area.Height / 2.0 + (sine * dx + cosine * dy) * fix.Scale;
                return new PlacedMarker(x, y, m.GroupKey, m.Id);
            }).Where(m => m.X >= 8 && m.X <= area.Width - 8 && m.Y >= 8 && m.Y <= area.Height - 22)
            .Select(m => m with { X = (area.X - display.X + m.X) / display.Width, Y = (area.Y - display.Y + m.Y) / display.Height }).ToArray();
        return new(markers, (area.X - display.X) / (double)display.Width, (area.Y - display.Y) / (double)display.Height,
            area.Width / (double)display.Width, (area.Height - 16) / (double)display.Height, large, capturedAt);
    }

}

internal sealed class NavigationSession(MarkerIndex index, DisplayArea display) : IDisposable
{
    private MinimapTracker? tracker;
    private DesktopCapture? capture;
    private MapMarker? target;

    public (GuidanceFrame? Frame, MinimapFrame? Minimap, string Status) Process(bool large, HashSet<string> enabled, bool direction = true, bool showIcons = true, MinimapRegion? region = null)
    {
        if (enabled.Count == 0) return (null, null, "Select a resource or collectible.");
        tracker ??= new MinimapTracker(Paths.Reference);
        var area = Navigation.MinimapArea(display, large, region);
        if (capture?.Display != area)
        {
            capture?.Dispose(); capture = new DesktopCapture(area); target = null;
        }
        var capturedAt = DateTime.UtcNow;
        using var gray = capture.GrabGray();
        var fix = tracker.Process(gray);
        if (fix is null) { target = null; return (null, null, "Minimap alignment lost. Check your minimap preset."); }
        var projected = Navigation.ProjectMinimap(index, enabled, fix, display, area, large, capturedAt);
        target = direction ? Navigation.Nearest(index.Markers, enabled, fix, target) : null;
        var guide = target is not null ? Navigation.GuideOnMinimap(target, fix, display, area, large, capturedAt, false) : null;
        return (guide, showIcons ? projected with { TargetId = target?.Id } : null, $"Minimap · {fix.Inliers} terrain points" +
            (target is null ? "" : $" · Nearest mapped spawn: {target.Name}"));
    }

    public void Dispose()
    {
        tracker?.Dispose(); tracker = null; capture?.Dispose(); capture = null; target = null;
    }
}
