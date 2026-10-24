using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;

namespace Aion2MapOverlay.App;

internal static class SmokeCheck
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        CameraHeadingChecks.Run();
        var monitor = new Aion2MapOverlay.Capture.DisplayArea(-1920, 120, 1920, 1080);
        var selection = MinimapRegion.FromScreenRectangle(monitor, new System.Windows.Rect(-290, 245, 220, 175))
            ?? throw new Exception("Guided minimap selection failed");
        if (!selection.IsValid || selection.ToDisplayArea(monitor) != new Aion2MapOverlay.Capture.DisplayArea(-290, 245, 220, 175) ||
            MinimapRegion.FromScreenRectangle(monitor, new System.Windows.Rect(-290, 245, 5, 5)) is not null)
            throw new Exception("Minimap selection normalization or size validation failed");
        if (Navigation.MinimapArea(monitor, true, selection) != selection.ToDisplayArea(monitor))
            throw new Exception("Custom minimap was not used");
        var hudCode = HudGuide.Code;
        _ = Convert.FromBase64String(hudCode);
        if (!hudCode.EndsWith('=') || hudCode.Any(char.IsWhiteSpace) ||
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hudCode))).ToLowerInvariant() != "f02fa6f38784b3098afdae76194bbbbf770297f7df3e5242538b02dc76a2b8cb")
            throw new Exception("HUD code differs from the supplied code or contains trailing characters");
        var recovery = new FrameRecovery();
        var captureFailure = new Aion2MapOverlay.Capture.ScreenCaptureException("Simulated desktop interruption", 5);
        if (!recovery.TryRetry(captureFailure, out var firstDelay) || firstDelay <= TimeSpan.Zero) throw new Exception("Temporary capture failure must retry");
        recovery.Succeeded();
        if (recovery.Failures != 0) throw new Exception("Successful frames must reset recovery");
        for (var i = 0; i < 8; i++)
            if (!recovery.TryRetry(captureFailure, out var delay) || delay > TimeSpan.FromSeconds(3)) throw new Exception("Capture retries must be bounded and backed off");
        if (recovery.TryRetry(captureFailure, out _) || recovery.TryRetry(new InvalidOperationException("Unexpected failure"), out _) ||
            recovery.TryRetry(new OperationCanceledException(), out _)) throw new Exception("Permanent errors and cancellation must not silently retry");
        using var image = Mat.Zeros(64, 64, MatType.CV_8UC1).ToMat();
        Cv2.Circle(image, new(32, 32), 10, Scalar.White, -1);
        if (Cv2.CountNonZero(image) < 100) throw new Exception("Native OpenCV runtime unavailable.");
        foreach (var path in new[] { "../outside.dll", "data/map.json", "settings.local.json", "captures/image.png", "C:/app.dll", "/root.dll", "sub/../../app.dll", ".git/config" })
        {
            var rejected = false;
            try { AppUpdates.SafePath(output, path); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Unsafe update path accepted: " + path);
        }
        var testRoot = Path.Combine(output, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var entries = new Dictionary<string, byte[]> { ["aion2-map-overlay.exe"] = [1, 2, 3], ["ApplyUpdate.ps1"] = [4, 5, 6] };
        var files = entries.Select(p => new UpdateFile(p.Key, p.Value.Length, Convert.ToHexString(SHA256.HashData(p.Value)))).ToArray();
        var manifest = new UpdateManifest("0.1.0", files);
        var zipPath = Path.Combine(testRoot, "valid.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var item in entries) { using var stream = zip.CreateEntry(item.Key).Open(); stream.Write(item.Value); }
            using var writer = new StreamWriter(zip.CreateEntry("update-manifest.json").Open()); writer.Write(JsonSerializer.Serialize(manifest));
        }
        AppUpdates.ExtractVerified(zipPath, Path.Combine(testRoot, "verified"), new Version(0, 1, 0));
        var badZipPath = Path.Combine(testRoot, "tampered.zip"); File.Copy(zipPath, badZipPath);
        using (var zip = ZipFile.Open(badZipPath, ZipArchiveMode.Update))
        {
            zip.GetEntry("aion2-map-overlay.exe")!.Delete();
            using var stream = zip.CreateEntry("aion2-map-overlay.exe").Open(); stream.Write([9, 9, 9]);
        }
        var tamperRejected = false;
        try { AppUpdates.ExtractVerified(badZipPath, Path.Combine(testRoot, "tampered"), new Version(0, 1, 0)); }
        catch (InvalidDataException) { tamperRejected = true; }
        if (!tamperRejected) throw new Exception("Tampered update accepted.");
        var originalDataRoot = Paths.DataRoot;
        Paths.DataRoot = Path.Combine(testRoot, "no-data");
        try
        {
            var window = new MainWindow(); window.PrepareForCheck();
            var setup = (System.Windows.FrameworkElement)window.FindName("SetupPanel");
            var start = (System.Windows.Controls.Button)window.FindName("StartButton");
            if (setup.Visibility != System.Windows.Visibility.Visible || start.IsEnabled) throw new Exception("First-run setup must appear before tracking is enabled.");
            var content = (System.Windows.FrameworkElement)window.Content; window.Content = null;
            var border = new System.Windows.Controls.Border { Background = window.Background, Child = content, Resources = window.Resources };
            System.Windows.Documents.TextElement.SetForeground(border, window.Foreground);
            System.Windows.Documents.TextElement.SetFontFamily(border, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(border, window.FontSize);
            border.Measure(new System.Windows.Size(760, 1020)); border.Arrange(new System.Windows.Rect(0, 0, 760, 1020)); border.UpdateLayout();
            var preview = new System.Windows.Media.Imaging.RenderTargetBitmap(760, 1020, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); preview.Render(border);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using (var stream = File.Create(Path.Combine(output, "first-run-ui.png"))) encoder.Save(stream);
            window.Close();
        }
        finally { Paths.DataRoot = originalDataRoot; }
        File.WriteAllText(Path.Combine(output, "smoke-test.json"), JsonSerializer.Serialize(new {
            version = AppUpdates.CurrentVersion.ToString(), nativeOpenCvPassed = true, cameraHeadingPassed = true, hudCodeVerified = true, captureRecoveryPassed = true,
            unsafePathsRejected = 8, validPackageAccepted = true, tamperedPackageRejected = true, firstRunSetupPassed = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
