using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using Aion2MapOverlay.Capture;
using Aion2MapOverlay.Core.Markers;
using Aion2MapOverlay.Data;
using Aion2MapOverlay.Vision;
using Microsoft.Win32;

namespace Aion2MapOverlay.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MarkerGroup> groups = [];
    private Dictionary<string, ImageSource> icons = [];
    private MarkerIndex? index;
    private HashSet<string> selected = [];
    private OverlayWindow? overlay;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer resourceTimer;
    private readonly Process ownProcess = Process.GetCurrentProcess();
    private readonly Stopwatch resourceClock = Stopwatch.StartNew();
    private TimeSpan previousCpuTime;
    private double previousSampleSeconds;
    private bool ready, closing;
    private volatile bool capturePaused;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private ScreenshotHotkey? screenshotHotkey;
    private bool screenshotTransition;
    private DateTime lastStatus;
    private sealed record SavedSettings(string[] Enabled, double Size, double Opacity, int Display, bool Navigation = false, int MinimapPreset = 1, bool AutomaticUpdates = true, bool MinimapIcons = true, MinimapRegion? CustomMinimap = null);
    private MinimapRegion? customMinimap;
    private readonly CancellationTokenSource lifetime = new();
    private AvailableUpdate? availableUpdate;
    private bool checkingUpdates, setupBusy;
    private sealed record NavigationOptions(bool Enabled, bool Large, bool Icons = true, MinimapRegion? Region = null);
    private NavigationOptions navigationOptions = new(false, true);

    public MainWindow()
    {
        InitializeComponent();
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        previousCpuTime = ownProcess.TotalProcessorTime;
        previousSampleSeconds = resourceClock.Elapsed.TotalSeconds;
        resourceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        resourceTimer.Tick += (_, _) => UpdateResourceUsage();
        resourceTimer.Start();
        Closed += (_, _) => { lifetime.Cancel(); resourceTimer.Stop(); ownProcess.Dispose(); screenshotHotkey?.Dispose(); };
        DisplayPicker.ItemsSource = DesktopCapture.Displays(); DisplayPicker.SelectedIndex = 0;
        for (var i = 0; i < DisplayPicker.Items.Count; i++)
            if (DisplayPicker.Items[i] is DisplayArea { X: 0, Y: 0 }) DisplayPicker.SelectedIndex = i;
        SourceInitialized += (_, _) =>
        {
            OverlayWindow.Exclude(this);
            screenshotHotkey = new ScreenshotHotkey(new WindowInteropHelper(this).Handle, () => ToggleScreenshotMode());
            if (!screenshotHotkey.Registered) ScreenshotModeText.Text = "F8 is in use. Use the screenshot mode button.";
        };
        Loaded += async (_, _) => { LoadData(); if (AutomaticUpdateToggle.IsChecked == true) await CheckUpdatesAsync(); };
        Closing += OnClosing;
    }

    private void LoadData()
    {
        try
        {
            SavedSettings? saved = null;
            if (File.Exists(Paths.Settings))
            {
                try { saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(Paths.Settings)); }
                catch (JsonException) { StatusText.Text = "Previous settings could not be read; using defaults."; }
            }
            customMinimap = saved?.CustomMinimap?.IsValid == true ? saved.CustomMinimap : null;
            RefreshMinimapRegionLabel();
            AutomaticUpdateToggle.IsChecked = saved?.AutomaticUpdates ?? true;
            UpdateText.Text = $"v{AppUpdates.CurrentVersion} · Updates via GitHub";
            if (!DataSetup.HasData)
            {
                StartButton.IsEnabled = false;
                SetupPanel.Visibility = Visibility.Visible;
                SetupButton.Content = "Download map data";
                SetupText.Text = "First run: download markers, map artwork, and icons directly from Questlog for local use. No account is needed.";
                StatusText.Text = "Map data has not been downloaded yet.";
                return;
            }
            index = QuestlogData.LoadVerteron(Paths.Markers, TerrainTracker.ReferenceSize);
            icons = IconCache.Load();
            groups.Clear();
            foreach (var group in index.Markers.GroupBy(m => m.GroupKey).OrderBy(g => g.First().Type).ThenBy(g => g.First().Name))
            {
                var first = group.First();
                var item = new MarkerGroup { Key = group.Key, Name = first.Name, Type = first.Type, Count = group.Count(),
                    Icon = icons.GetValueOrDefault(group.Key), Enabled = saved is null ? first.Name == "Sapphire Gemstone" : saved.Enabled.Contains(group.Key) };
                item.PropertyChanged += (_, _) => SelectionChanged(); groups.Add(item);
            }
            if (saved is not null)
            {
                IconSize.Value = Math.Clamp(saved.Size, 10, 36); IconOpacity.Value = Math.Clamp(saved.Opacity, .25, 1);
                DisplayPicker.SelectedIndex = Math.Clamp(saved.Display, 0, Math.Max(0, DisplayPicker.Items.Count - 1));
                NavigationToggle.IsChecked = saved.Navigation;
                MinimapIconsToggle.IsChecked = saved.MinimapIcons;
                MinimapPresetPicker.SelectedIndex = Math.Clamp(saved.MinimapPreset, 0, 1);
            }
            Volatile.Write(ref navigationOptions, new(NavigationToggle.IsChecked == true, MinimapPresetPicker.SelectedIndex == 1, MinimapIconsToggle.IsChecked == true, customMinimap));
            GroupsList.ItemsSource = groups; ready = true; SelectionChanged();
            StatusText.Text = $"{index.Markers.Count:N0} locations · {groups.Count} toggleable categories. Ready.";
            UpdateResourceUsage();
            StartButton.IsEnabled = File.Exists(Paths.Title);
            SetupPanel.Visibility = StartButton.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
            if (!StartButton.IsEnabled)
            {
                SetupButton.Content = "Calibrate map (3 seconds)";
                SetupText.Text = "One-time setup: click Calibrate, then return to Aion 2 and open the Verteron map. A small title reference is saved locally.";
                StatusText.Text = "Data downloaded. Calibrate the map title once before starting.";
            }
        }
        catch (Exception error) { StartButton.IsEnabled = false; StatusText.Text = "Data could not be loaded: " + error.Message; }
    }

    internal void PrepareForCheck() => LoadData();

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (setupBusy) return;
        setupBusy = true; SetupButton.IsEnabled = false;
        try
        {
            if (!DataSetup.HasData)
                await Task.Run(() => DataSetup.DownloadAsync(new Progress<string>(message => Dispatcher.InvokeAsync(() => SetupText.Text = message)), lifetime.Token));
            else
            {
                if (MessageBox.Show(this,
                    "Open the full Verteron map in Aion 2 first.\n\nClick OK when ready, then switch back to Aion 2. Calibration captures the map after 3 seconds. Keep the map open until the countdown finishes.",
                    "Calibrate map", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                var display = (DisplayArea)DisplayPicker.SelectedItem;
                for (var seconds = 3; seconds > 0; seconds--)
                {
                    SetupText.Text = $"Return to Aion 2 and open the Verteron map: {seconds}…";
                    await Task.Delay(1000, lifetime.Token);
                }
                await Task.Run(() =>
                {
                    if (!OverlayWindow.IsGameForeground()) throw new InvalidOperationException("Aion 2 must be in front during calibration.");
                    using var capture = new DesktopCapture(display); using var gray = capture.GrabGray();
                    using var tracker = new TerrainTracker(Paths.Reference, Paths.Title);
                    if (tracker.Process(gray, false) is null) throw new InvalidOperationException("Verteron terrain was not recognized. Open its full map and try again.");
                    using var title = new OpenCvSharp.Mat(gray, new OpenCvSharp.Rect(10, 13, 205, 32));
                    Directory.CreateDirectory(Path.GetDirectoryName(Paths.Title)!);
                    if (!OpenCvSharp.Cv2.ImWrite(Paths.Title, title)) throw new IOException("Could not save the local map title reference.");
                }, lifetime.Token);
            }
            LoadData();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetupText.Text = "Setup failed: " + error.Message + " You can retry."; }
        finally { setupBusy = false; SetupButton.IsEnabled = true; }
    }

    private async Task CheckUpdatesAsync()
    {
        if (checkingUpdates) return;
        checkingUpdates = true; UpdateButton.IsEnabled = false;
        UpdateText.Text = "Checking GitHub Releases…";
        try
        {
            availableUpdate = await AppUpdates.CheckAsync(lifetime.Token);
            UpdateButton.Content = availableUpdate is null ? "Check for updates" : "Install & restart";
            UpdateText.Text = availableUpdate is null ? $"v{AppUpdates.CurrentVersion} · No newer stable release" : $"v{availableUpdate.Version} available · Current: {AppUpdates.CurrentVersion}";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { UpdateText.Text = "Update check failed: " + error.Message; }
        finally { checkingUpdates = false; UpdateButton.IsEnabled = true; }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (checkingUpdates || setupBusy) return;
        if (availableUpdate is null) { await CheckUpdatesAsync(); return; }
        checkingUpdates = true; UpdateButton.IsEnabled = false; SetupButton.IsEnabled = false;
        try
        {
            UpdateText.Text = "Downloading and verifying the update…";
            var stage = await AppUpdates.PrepareAsync(availableUpdate, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            SaveSettings();
            AppUpdates.InstallAndRestart(stage);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { UpdateText.Text = "Update failed: " + error.Message; }
        finally { checkingUpdates = false; UpdateButton.IsEnabled = true; SetupButton.IsEnabled = true; }
    }

    private void UpdatePreference_Changed(object sender, RoutedEventArgs e)
    {
        if (ready) { saveTimer.Stop(); saveTimer.Start(); }
        else if (IsInitialized)
        {
            try
            {
                var settings = File.Exists(Paths.Settings) ? JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(Paths.Settings)) : null;
                settings = (settings ?? new SavedSettings(["gatherable|Sapphire Gemstone"], 20, .9, 0)) with { AutomaticUpdates = AutomaticUpdateToggle.IsChecked == true };
                File.WriteAllText(Paths.Settings + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(Paths.Settings + ".tmp", Paths.Settings, true);
            }
            catch (Exception error) { UpdateText.Text = "Could not save update preference: " + error.Message; }
        }
    }

    private void UpdateResourceUsage()
    {
        ownProcess.Refresh();
        var seconds = resourceClock.Elapsed.TotalSeconds;
        var cpuTime = ownProcess.TotalProcessorTime;
        var elapsed = seconds - previousSampleSeconds;
        var cpu = elapsed > 0 ? (cpuTime - previousCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100 : 0;
        ResourceUsageText.Text = $"App CPU {Math.Clamp(cpu, 0, 100):F1}% · RAM {ownProcess.WorkingSet64 / (1024.0 * 1024):F0} MiB";
        previousCpuTime = cpuTime;
        previousSampleSeconds = seconds;
    }

    private void SelectionChanged()
    {
        Volatile.Write(ref selected, groups.Where(g => g.Enabled).Select(g => g.Key).ToHashSet());
        SelectionText.Text = $"{groups.Count(g => g.Enabled)} / {groups.Count} selected";
        overlay?.Update(null, IconSize.Value, IconOpacity.Value);
        if (ready) { saveTimer.Stop(); saveTimer.Start(); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (index is null || DisplayPicker.SelectedItem is not DisplayArea display || worker is not null) return;
        overlay = new OverlayWindow(display, icons); overlay.Show(); overlay.EnsureCaptureExcluded();
        if (!overlay.CaptureExcluded)
        {
            overlay.Close(); overlay = null;
            StatusText.Text = "Windows could not exclude the overlay from capture. Live capture was not started."; return;
        }
        cancellation = new(); StartButton.IsEnabled = false; StopButton.IsEnabled = true; DisplayPicker.IsEnabled = false;
        capturePaused = false;
        StatusText.Text = "Preparing reference terrain…";
        var token = cancellation.Token;
        worker = Task.Run(async () =>
        {
            OpenCvSharp.Cv2.SetNumThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
            using var tracker = new TerrainTracker(Paths.Reference, Paths.Title);
            using var capture = new DesktopCapture(display);
            using var navigation = new NavigationSession(index, display);
            var recovery = new FrameRecovery();
            while (!token.IsCancellationRequested)
            {
                if (capturePaused) { await Task.Delay(50, token); continue; }
                var watch = Stopwatch.StartNew();
                await captureGate.WaitAsync(token);
                try
                {
                    if (capturePaused) continue;
                    if (!OverlayWindow.IsGameForeground())
                    {
                        tracker.Reset(); await Deliver(null, "Waiting for Aion 2 to be in front.");
                        await Task.Delay(200, token); continue;
                    }
                    var capturedAt = DateTime.UtcNow;
                    using var gray = capture.GrabGray();
                    var aligned = tracker.Process(gray);
                    OverlayFrame? frame = null;
                    GuidanceFrame? guidance = null;
                    MinimapFrame? minimap = null;
                    string? guidanceStatus = null;
                    var navigationSettings = Volatile.Read(ref navigationOptions);
                    if (!navigationSettings.Enabled && !navigationSettings.Icons) navigation.Dispose();
                    else if (aligned is null && !tracker.IsMapVisible(gray))
                    {
                        (guidance, minimap, guidanceStatus) = navigation.Process(navigationSettings.Large, Volatile.Read(ref selected), navigationSettings.Enabled, navigationSettings.Icons, navigationSettings.Region);
                    }
                    if (aligned is not null)
                    {
                        var t = aligned.Transform;
                        var start = t.Inverse(capture.Width * .18 - 30, capture.Height * .12 - 30);
                        var end = t.Inverse(capture.Width * .94 + 30, capture.Height * .94 + 30);
                        var enabled = Volatile.Read(ref selected);
                        var markers = index.Query(start.X, start.Y, end.X, end.Y).Where(m => enabled.Contains(m.GroupKey))
                            .Select(m => { var point = t.Apply(m.ReferenceX, m.ReferenceY); return new PlacedMarker(point.X, point.Y, m.GroupKey, m.Id); }).ToArray();
                        frame = new(markers, capture.Width, capture.Height, capturedAt);
                    }
                    recovery.Succeeded();
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    await Deliver(frame, aligned is null ? guidanceStatus ?? "Verteron map not visible, or alignment lost." :
                        $"{aligned.Method} · {aligned.Inliers} points · {elapsed:F0} ms · {frame!.Markers.Length} visible markers", guidance, minimap);
                    var interval = aligned is null ? (navigationSettings.Enabled || navigationSettings.Icons ? 100 : 200) : 33;
                    await Task.Delay(Math.Max(1, (int)(interval - watch.Elapsed.TotalMilliseconds)), token);
                }
                catch (Exception error) when (error is ScreenCaptureException or OpenCvSharp.OpenCVException)
                {
                    tracker.Reset(); capture.Reset(); navigation.Dispose();
                    RuntimeLog.Write($"Frame failed on display {display}", error);
                    if (!recovery.TryRetry(error, out var delay)) throw new InvalidOperationException("Capture/recognition failed repeatedly. Try Start again. Details: logs/overlay.log", error);
                    await Deliver(null, $"Capture interrupted; retrying {recovery.Failures}/8. {error.Message}");
                    await Task.Delay(delay, token);
                }
                finally { captureGate.Release(); }
            }
        }, token);
        try { await worker; }
        catch (OperationCanceledException) { }
        catch (Exception error) { RuntimeLog.Write("Overlay stopped", error); StatusText.Text = "Overlay stopped: " + error.Message + " · Details: logs/overlay.log"; }
        finally
        {
            overlay?.Close(); overlay = null; worker = null; cancellation?.Dispose(); cancellation = null;
            capturePaused = false;
            ScreenshotModeText.Text = "F8 → Win+Shift+S → F8 to resume";
            ScreenshotModeButton.Content = "Screenshot mode (F8)";
            StartButton.IsEnabled = true; StopButton.IsEnabled = false; DisplayPicker.IsEnabled = true;
        }
    }

    private async Task Deliver(OverlayFrame? frame, string status, GuidanceFrame? guidance = null, MinimapFrame? minimap = null)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            // A setting may have changed while the background frame was being computed.
            var options = Volatile.Read(ref navigationOptions);
            if (!options.Enabled || (guidance is not null && (guidance.Large != options.Large || !Volatile.Read(ref selected).Contains(guidance.GroupKey)))) guidance = null;
            if (!options.Icons || minimap?.Large != options.Large) minimap = null;
            else if (minimap is not null) minimap = minimap with { Markers = minimap.Markers.Where(m => Volatile.Read(ref selected).Contains(m.Key)).ToArray() };
            overlay?.Update(frame, IconSize.Value, IconOpacity.Value, guidance, minimap);
            if (DateTime.UtcNow - lastStatus > TimeSpan.FromMilliseconds(250)) { StatusText.Text = status; lastStatus = DateTime.UtcNow; }
        }, DispatcherPriority.Render);
    }

    private async void ToggleScreenshotMode()
    {
        if (screenshotTransition || overlay is null || cancellation?.IsCancellationRequested != false) return;
        screenshotTransition = true;
        capturePaused = true;
        try
        {
            // Wait for the in-flight frame to finish before making icons capturable.
            // Normal capture cannot run again until exclusion has been restored.
            await captureGate.WaitAsync();
            try
            {
                if (overlay is null) { capturePaused = false; return; }
                if (overlay.ScreenshotMode)
                {
                    if (!overlay.EndScreenshotMode())
                    {
                        cancellation?.Cancel(); overlay.Hide();
                        ScreenshotModeText.Text = "Could not restore capture exclusion. Overlay stopped."; return;
                    }
                    capturePaused = false;
                    ScreenshotModeButton.Content = "Screenshot mode (F8)";
                    ScreenshotModeText.Text = "Tracking resumed. F8 → Win+Shift+S → F8.";
                }
                else if (OverlayWindow.IsGameForeground() && overlay.BeginScreenshotMode())
                {
                    ScreenshotModeButton.Content = "Resume tracking (F8)";
                    ScreenshotModeText.Text = "Screenshot mode: use Win+Shift+S, then F8 to resume.";
                }
                else
                {
                    capturePaused = false;
                    ScreenshotModeText.Text = "Wait for map or minimap markers, then press F8 in Aion 2.";
                }
            }
            finally { captureGate.Release(); }
        }
        finally { screenshotTransition = false; }
    }

    private async void ScreenshotMode_Click(object sender, RoutedEventArgs e)
    {
        if (overlay is null) { ScreenshotModeText.Text = "Start the overlay first."; return; }
        if (overlay.ScreenshotMode) { ToggleScreenshotMode(); return; }
        ScreenshotModeButton.IsEnabled = false;
        try
        {
            for (var remaining = 3; remaining > 0; remaining--)
            {
                ScreenshotModeText.Text = $"Return to Aion 2: {remaining}…";
                await Task.Delay(1000);
                if (closing || overlay is null) return;
            }
            ToggleScreenshotMode();
        }
        finally { ScreenshotModeButton.IsEnabled = true; }
    }


    private void RefreshMinimapRegionLabel()
    {
        if (MinimapRegionText is null) return;
        MinimapRegionText.Text = customMinimap is null
            ? "Using preset area · Drag over the minimap to calibrate its position"
            : $"Custom minimap · {customMinimap.Width:P1} × {customMinimap.Height:P1} of screen · saved";
    }

    private void SelectMinimap_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPicker.SelectedItem is not DisplayArea display) return;
        // Hide both application windows so the underlying game is visible for the guided drag.
        var wasOverlayVisible = overlay?.IsVisible == true;
        if (wasOverlayVisible) overlay!.Hide();
        Hide();
        try
        {
            var picker = new ScreenRegionPicker(display,
                "MINIMAP CALIBRATION\nDrag a rectangle around the visible minimap terrain. Keep the player centered; avoid surrounding HUD widgets.");
            if (picker.ShowDialog() == true && picker.Selection is { } region)
            {
                customMinimap = region;
                RefreshMinimapRegionLabel();
                UpdateNavigationOptions();
            }
        }
        finally
        {
            Show();
            if (wasOverlayVisible) overlay?.Show();
        }
    }

    private void ResetMinimap_Click(object sender, RoutedEventArgs e)
    {
        customMinimap = null;
        RefreshMinimapRegionLabel();
        UpdateNavigationOptions();
    }

    private void HudGuide_Click(object sender, RoutedEventArgs e) => HudGuide.Show(this);

    private void Stop_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); overlay?.Update(null, 20, 1); StatusText.Text = "Stopped."; }
    private void Appearance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready) SelectionChanged(); }
    private void Navigation_Changed(object sender, RoutedEventArgs e) => UpdateNavigationOptions();
    private void MinimapPreset_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateNavigationOptions();
    private void UpdateNavigationOptions()
    {
        if (!ready) return;
        Volatile.Write(ref navigationOptions, new(NavigationToggle.IsChecked == true, MinimapPresetPicker.SelectedIndex == 1, MinimapIconsToggle.IsChecked == true, customMinimap));
        overlay?.ClearGuidance();
        saveTimer.Stop(); saveTimer.Start();
    }
    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (GroupsList?.ItemsSource is null) return;
        CollectionViewSource.GetDefaultView(GroupsList.ItemsSource).Filter = item => ((MarkerGroup)item).Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
    }
    private void GroupsList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetGridColumns(GroupsList, GroupsList.ActualWidth >= 860 ? 4 : 3);
    }

    private static void SetGridColumns(DependencyObject parent, int columns)
    {
        if (parent is System.Windows.Controls.Primitives.UniformGrid grid) { grid.Columns = columns; return; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            SetGridColumns(VisualTreeHelper.GetChild(parent, i), columns);
    }
    private void SetGroups(Func<MarkerGroup, bool> predicate) { foreach (var group in groups) group.Enabled = predicate(group); }
    private void All_Click(object sender, RoutedEventArgs e) => SetGroups(_ => true);
    private void None_Click(object sender, RoutedEventArgs e) => SetGroups(_ => false);
    private void Gatherables_Click(object sender, RoutedEventArgs e) => SetGroups(g => g.Type == "gatherable");
    private void Collectibles_Click(object sender, RoutedEventArgs e) => SetGroups(g => g.Type == "collectable");

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Screenshots|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var output = Path.Combine(AppContext.BaseDirectory, "captures", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        StatusText.Text = "Matching screenshots…";
        try
        {
            await Task.Run(() => Diagnostics.Run(dialog.FileNames, output, false));
            StatusText.Text = "Screenshot results saved to " + output;
            Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
        }
        catch (Exception error) { StatusText.Text = "Screenshot test failed: " + error.Message; }
    }

    private void SaveSettings()
    {
        if (!ready) return;
        try
        {
            var settings = new SavedSettings(groups.Where(g => g.Enabled).Select(g => g.Key).ToArray(), IconSize.Value, IconOpacity.Value, DisplayPicker.SelectedIndex,
                NavigationToggle.IsChecked == true, MinimapPresetPicker.SelectedIndex, AutomaticUpdateToggle.IsChecked == true, MinimapIconsToggle.IsChecked == true, customMinimap);
            var temporary = Paths.Settings + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, Paths.Settings, true);
        }
        catch (Exception error) { StatusText.Text = "Settings could not be saved: " + error.Message; }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        SaveSettings(); saveTimer.Stop();
        if (worker is null) { overlay?.Close(); return; }
        e.Cancel = true; closing = true; cancellation?.Cancel();
        try { await worker; } catch { }
        overlay?.Close(); Close();
    }
}
