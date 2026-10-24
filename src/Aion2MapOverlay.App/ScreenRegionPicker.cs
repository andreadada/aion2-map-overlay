using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Aion2MapOverlay.Capture;

namespace Aion2MapOverlay.App;

// Full-monitor, visible drag guide. Never reads game memory or sends game input.
internal sealed class ScreenRegionPicker : Window
{
    private readonly DisplayArea display;
    private readonly int minimumSize;
    private readonly Canvas canvas = new();
    private readonly Rectangle outline = new();
    private Point? anchor;
    public MinimapRegion? Selection { get; private set; }

    public ScreenRegionPicker(DisplayArea display, string instruction, int minimumSize = 72)
    {
        this.display = display;
        this.minimumSize = minimumSize;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(86, 8, 18, 30));
        Cursor = Cursors.Cross;
        Width = display.Width; Height = display.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = display.X; Top = display.Y;
        canvas.Background = Brushes.Transparent;
        outline.Stroke = Brushes.DeepSkyBlue;
        outline.StrokeThickness = 3;
        outline.StrokeDashArray = new DoubleCollection { 4, 2 };
        outline.Fill = new SolidColorBrush(Color.FromArgb(35, 0, 190, 255));
        outline.Visibility = Visibility.Collapsed;
        canvas.Children.Add(outline);
        var message = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 12, 24, 39)),
            BorderBrush = Brushes.DeepSkyBlue, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
            Child = new TextBlock
            {
                Text = instruction + "\nDrag to select · ESC to cancel",
                Foreground = Brushes.White, FontSize = 16, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            }
        };
        Canvas.SetLeft(message, 24); Canvas.SetTop(message, 24);
        canvas.Children.Add(message);
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, -1, display.X, display.Y, display.Width, display.Height, 0x0040);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            anchor = e.GetPosition(canvas);
            canvas.CaptureMouse();
            UpdateSelection(anchor.Value, anchor.Value);
        };
        canvas.MouseMove += (_, e) =>
        {
            if (anchor is { } start && e.LeftButton == MouseButtonState.Pressed) UpdateSelection(start, e.GetPosition(canvas));
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (anchor is not { } start) return;
            var end = e.GetPosition(canvas);
            canvas.ReleaseMouseCapture();
            anchor = null;
            var p1 = PointToScreen(start);
            var p2 = PointToScreen(end);
            var rect = new Rect(p1, p2);
            Selection = MinimapRegion.FromScreenRectangle(display, rect, minimumSize);
            if (Selection is not null) DialogResult = true;
            else outline.Visibility = Visibility.Collapsed;
        };
    }

    private void UpdateSelection(Point a, Point b)
    {
        var box = new Rect(a, b);
        Canvas.SetLeft(outline, box.Left);
        Canvas.SetTop(outline, box.Top);
        outline.Width = box.Width;
        outline.Height = box.Height;
        outline.Visibility = Visibility.Visible;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
