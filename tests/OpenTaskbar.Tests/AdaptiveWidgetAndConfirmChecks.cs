using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Widgets;
using OpenTaskbar.Widgets;

internal static class AdaptiveWidgetAndConfirmChecks
{
    public static int Run(Application application)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        CheckPreviewDimensions(application, Check);
        CheckDensityPersistence(Check);
        CheckDockItemOrderPersistence(Check);
        CheckAdaptiveDensityAndHysteresis(application.Dispatcher, Check);
        CheckScrollableAppBudget(Check);
        CheckVisualBalanceAndTrayFiltering(application, Check);
        CheckEnhancedAudioCenter(application, Check);
        CheckWidgetCompactModes(Check);
        return checks;
    }

    private static void CheckPreviewDimensions(Application application, Action<bool, string> check)
    {
        var width = (double)application.FindResource("PreviewWidth");
        var height = (double)application.FindResource("PreviewHeight");
        check(width <= 240 && height <= 160,
            $"Window thumbnail previews must stay compact (got {width}x{height}).");
    }

    private static void CheckDensityPersistence(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentaskbar-density-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(path);
            var settings = new DockSettings(
                GlobalWidgetDensity: WidgetDensityMode.Compact,
                WidgetDensityOverrides: new Dictionary<string, WidgetDensityMode>
                {
                    ["Audio"] = WidgetDensityMode.Full,
                    ["SystemStats"] = WidgetDensityMode.Auto
                });
            check(store.Save(settings) is null, "Density settings must save cleanly.");
            var loaded = store.Load();
            check(loaded.Error is null
                && loaded.Settings.GlobalWidgetDensity == WidgetDensityMode.Compact
                && loaded.Settings.WidgetDensityOverrides?["Audio"] == WidgetDensityMode.Full
                && loaded.Settings.WidgetDensityOverrides?["SystemStats"] == WidgetDensityMode.Auto,
                "Global and per-widget density settings must persist and reload accurately.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    private static void CheckDockItemOrderPersistence(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentaskbar-dock-order-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(path);
            var order = new[] { "widget:Media", "start", "app:C:\\Tools\\app.exe", "stack:C:\\Docs", "bin" };
            check(store.Save(new DockSettings(DockItemOrder: order)) is null,
                "Free-form dock order must save cleanly.");
            var loaded = store.Load();
            check(loaded.Error is null && loaded.Settings.DockItemOrder?.SequenceEqual(order) == true,
                "Free-form dock order must round-trip exactly.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    private static void CheckAdaptiveDensityAndHysteresis(Dispatcher dispatcher, Action<bool, string> check)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        // Use side-effect-free, always-visible widgets so adaptive density tests never activate live
        // Windows Location, Core Audio, SMTC, PDH/GPU sensors, or Wi-Fi/Bluetooth radios.
        using var coordinator = new WidgetCoordinator(strip, dispatcher, _ => { }, lazy: true);
        var widgets = new[] { "Focus", "DayProgress", "Clock" };
        var settings = new DockSettings(
            Widgets: widgets,
            InstalledWidgets: widgets,
            GlobalWidgetDensity: WidgetDensityMode.Auto);
        coordinator.Apply(settings);

        var focus = strip.Children.OfType<FocusWidget>().Single();
        var clock = strip.Children.OfType<ClockWidget>().Single();
        var autoCandidates = strip.Children.OfType<OpenTaskbar.Widgets.DockWidget>()
            .Where(widget => widget.Visibility == Visibility.Visible && widget.SupportsCompactMode)
            .OrderBy(widget => widget.ShrinkPriority).ToArray();

        check(autoCandidates.Length > 0,
            "The widget strip must contain at least one compact-capable widget.");

        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 2000, appsContentWidth: 400, settings);
        check(autoCandidates.All(widget => !widget.IsCompact),
            "Auto widgets must stay full when there is enough room.");

        var saving = autoCandidates[0].FullWidthEstimate - autoCandidates[0].CompactWidthEstimate;
        var singleOverflowWidth = 650 + Math.Max(1, Math.Floor(saving * 0.7));
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 650, appsContentWidth: singleOverflowWidth, settings);
        check(autoCandidates[0].IsCompact,
            "Auto density must compact the highest-priority eligible widget when space runs out.");
        check(autoCandidates.Skip(1).All(widget => !widget.IsCompact),
            "Auto density must leave other widgets full when one compaction is enough.");

        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 650, appsContentWidth: singleOverflowWidth - saving, settings);
        check(autoCandidates[0].IsCompact,
            "Auto density must not oscillate after measuring compact widgets.");
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: singleOverflowWidth + 10, appsContentWidth: singleOverflowWidth - saving, settings);
        check(autoCandidates[0].IsCompact,
            "Hysteresis must prevent immediate expansion near the width limit.");
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: singleOverflowWidth + 80, appsContentWidth: singleOverflowWidth - saving, settings);
        check(autoCandidates.All(widget => !widget.IsCompact),
            "Auto density must expand widgets when enough space returns.");

        var globallyCompact = settings with { GlobalWidgetDensity = WidgetDensityMode.Compact };
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 2000, appsContentWidth: 400, globallyCompact);
        check(focus.IsCompact && clock.IsCompact,
            "Global Compact density must compact every eligible widget.");

        var globallyFull = settings with { GlobalWidgetDensity = WidgetDensityMode.Full };
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 650, appsContentWidth: 720, globallyFull);
        check(!focus.IsCompact && !clock.IsCompact,
            "Global Full density must keep every widget full.");

        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 650, appsContentWidth: 720, settings, widgetScale: 1.35);
        check(autoCandidates[0].IsCompact,
            "Scaled Auto widgets must compact when the dock runs out of room.");
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 2000, appsContentWidth: 400, settings);
        check(autoCandidates.All(widget => !widget.IsCompact),
            "Auto widgets must return to full size when there is enough room.");
        check(!coordinator.TryCompactNextAutoWidget(settings),
            "Single-step compaction is not used; density is recalculated for the whole strip.");
    }

    private static void CheckScrollableAppBudget(Action<bool, string> check)
    {
        const double floor = 104;

        check(WidgetCoordinator.EffectiveAppsWidth(600, 400, floor) == 400,
            "Apps that fit without scrolling must keep their full width in density calculations.");
        check(WidgetCoordinator.EffectiveAppsWidth(600, 160, floor) == 160,
            "Scrollable app overflow must not compact widgets while apps still have more than the reserved floor.");
        check(WidgetCoordinator.EffectiveAppsWidth(600, 0, floor) == floor,
            "Once apps would be squeezed below the reserved floor, the remaining overflow must reach widget density.");
        check(WidgetCoordinator.EffectiveAppsWidth(60, 0, floor) == 60,
            "A short app strip must never be padded up to the reserved floor.");
    }

    private static void CheckVisualBalanceAndTrayFiltering(Application application, Action<bool, string> check)
    {
        var startLogoSize = application.TryFindResource("StartLogoSize");
        check(startLogoSize is double d && d > 0,
            "Theme resources must expose StartLogoSize for proportional scaling across dock sizes.");

        foreach (var dockSize in new[] { DockSize.Compact, DockSize.Medium, DockSize.Large })
        {
            var p = DockMetrics.For(dockSize);
            // At 1.5x the icon deliberately exceeds its cell; neighbours are pushed aside by
            // RenderMagnification instead, so the layout stays unclipped.
            check(p.Icon * DockMetrics.HoverScale <= p.Cell * 1.6,
                $"Magnified icons in {dockSize} mode must stay within the displacement budget.");
        }

        using var hover = new WidgetHoverCoordinator(application.Dispatcher, _ => false, _ => { });
        using var qsWidget = new QuickSettingsWidget();
        using var audioWidget = new AudioWidget();
        hover.Attach(qsWidget);
        hover.Attach(audioWidget);
        check(!hover.IsHoverActive, "WidgetHoverCoordinator must remain inactive at rest.");
        check(qsWidget.BuildHoverFlyoutContent() is null && audioWidget.BuildHoverFlyoutContent() is not null,
            "QuickSettings toggles directly without a hover flyout; Audio produces its mixer flyout cleanly.");
        hover.CollapseActive();
        check(!hover.IsHoverActive, "CollapseActive must reset hover active state.");
    }

    private static void CheckEnhancedAudioCenter(Application application, Action<bool, string> check)
    {
        // 1. Localization keys for enhanced audio controls in Turkish and English
        UiText.Current.SetLanguage(AppLanguage.Turkish);
        check(!string.IsNullOrWhiteSpace(UiText.Get("AudioOutputDevices")), "AudioOutputDevices key must exist in Turkish.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("AudioInputDevices")), "AudioInputDevices key must exist in Turkish.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("Microphone")), "Microphone key must exist in Turkish.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("MicMuteToggle")), "MicMuteToggle key must exist in Turkish.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("AppVolumeMixer")), "AppVolumeMixer key must exist in Turkish.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("NoActiveAudioApps")), "NoActiveAudioApps key must exist in Turkish.");

        UiText.Current.SetLanguage(AppLanguage.English);
        check(!string.IsNullOrWhiteSpace(UiText.Get("AudioOutputDevices")), "AudioOutputDevices key must exist in English.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("AudioInputDevices")), "AudioInputDevices key must exist in English.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("Microphone")), "Microphone key must exist in English.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("MicMuteToggle")), "MicMuteToggle key must exist in English.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("AppVolumeMixer")), "AppVolumeMixer key must exist in English.");
        check(!string.IsNullOrWhiteSpace(UiText.Get("NoActiveAudioApps")), "NoActiveAudioApps key must exist in English.");

        // 2. Data contracts
        check(Marshal.OffsetOf<AudioEndpointMonitor.PropVariant>(nameof(AudioEndpointMonitor.PropVariant.Pointer)) == 8,
            "Core Audio must read the native endpoint name pointer at offset eight on both architectures.");
        check(Marshal.SizeOf<AudioEndpointMonitor.PropVariant>() == (IntPtr.Size == 4 ? 16 : 24),
            "Core Audio must reserve the native PROPVARIANT size for the current architecture.");
        var dev = new AudioDeviceInfo("dev-1", "Test Headphones", true, false);
        check(dev.Id == "dev-1" && dev.Name == "Test Headphones" && dev.IsDefault && !dev.IsInput,
            "AudioDeviceInfo contract must retain device identity, state, and flow.");

        var volumeSet = 0f;
        var muteSet = false;
        var session = new AppAudioSession(1234, "Spotify", null, 0.75f, false,
            vol => { volumeSet = vol; return true; },
            m => { muteSet = m; return true; });
        check(session.ProcessId == 1234 && session.Name == "Spotify" && Math.Abs(session.Volume - 0.75f) < 0.001f && !session.IsMuted,
            "AppAudioSession contract must retain session metadata and initial state.");
        session.SetVolume(0.5f);
        session.SetMute(true);
        check(Math.Abs(volumeSet - 0.5f) < 0.001f && muteSet,
            "AppAudioSession delegates must successfully propagate volume and mute commands.");

        // 3. Audio widget flyout construction
        using var audioWidget = new AudioWidget();
        var flyout = audioWidget.BuildHoverFlyoutContent();
        check(flyout is Border container && container.Child is StackPanel root && root.Children.Count >= 4,
            "AudioWidget flyout must build a multi-section audio center container.");
    }

    private static void CheckWidgetCompactModes(Action<bool, string> check)
    {
        static double MeasureWidth(FrameworkElement element)
        {
            element.Visibility = Visibility.Visible;
            element.InvalidateMeasure();
            element.Measure(new Size(double.PositiveInfinity, 46));
            element.Arrange(new Rect(0, 0, element.DesiredSize.Width, 46));
            element.UpdateLayout();
            return element.DesiredSize.Width;
        }

        static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T match) yield return match;
                foreach (var nested in LogicalDescendants<T>(child)) yield return nested;
            }
        }

        // 1. BatteryWidget: compact sizing, width reduction, ContentWidthChanged, and hover expansion restore
        using (var battery = new BatteryWidget
               {
                   BatteryReader = () => new BatteryStatus(Present: true, Percent: 82, Charging: true, PluggedIn: true)
               })
        {
            battery.ApplySettings(new DockSettings(Language: AppLanguage.English));
            battery.RenderPreview();
            var widthEvents = 0;
            battery.ContentWidthChanged += (_, _) => widthEvents++;

            check(battery.SupportsCompactMode && battery.ShrinkPriority == 36
                  && battery.FullWidthEstimate == 100 && battery.CompactWidthEstimate == 60,
                "BatteryWidget must advertise compact mode, ShrinkPriority=36, and 100/60 width estimates.");

            var card = (Border)battery.Content!;
            var surface = (Grid)card.Child!;
            var percent = LogicalDescendants<TextBlock>(battery).Last();
            var bolt = LogicalDescendants<ContentControl>(battery).Last();
            var fullWidth = MeasureWidth(battery);
            check(card.MinWidth == 96 && surface.MinWidth == 96 && percent.FontSize == 13 && bolt.Width == 11,
                "BatteryWidget full mode must use 96px MinWidth, 13px percent font, and 11px charging bolt.");

            battery.SetCompactState(true);
            var compactWidth = MeasureWidth(battery);
            check(widthEvents == 1 && card.MinWidth == 56 && surface.MinWidth == 56
                  && percent.FontSize == 11.5 && bolt.Width == 9.5 && compactWidth < fullWidth,
                $"BatteryWidget compact mode failed: events={widthEvents}, cardMin={card.MinWidth}, surfaceMin={surface.MinWidth}, font={percent.FontSize}, bolt={bolt.Width}, fullW={fullWidth}, compactW={compactWidth}.");

            battery.SetHoverExpanded(true);
            var hoverWidth = MeasureWidth(battery);
            check(widthEvents == 2 && card.MinWidth == 96 && surface.MinWidth == 96
                  && percent.FontSize == 13 && bolt.Width == 11 && hoverWidth > compactWidth,
                "BatteryWidget hover expansion must restore full sizing and raise ContentWidthChanged.");

            battery.SetHoverExpanded(false);
            check(widthEvents == 3 && card.MinWidth == 56 && percent.FontSize == 11.5,
                "BatteryWidget ending hover expansion must return to compact sizing.");

            battery.SetCompactState(false);
            check(widthEvents == 4 && card.MinWidth == 96 && percent.FontSize == 13,
                "BatteryWidget exiting compact mode must restore full sizing.");

            battery.Activate();
            battery.SetDockVisible(false);
            battery.SetDockVisible(true);
            battery.Deactivate();
        }

        // 2. ClockWidget: compact & hover expansion while inactive (!IsActive Settings preview behavior)
        using (var clock = new ClockWidget())
        {
            clock.ApplySettings(new DockSettings(Language: AppLanguage.English, DateStyle: ClockDateStyle.DayAndShortDate));
            clock.RenderPreview();
            var widthEvents = 0;
            clock.ContentWidthChanged += (_, _) => widthEvents++;

            var dateColumn = (FrameworkElement)clock.FindName("DateColumn")!;
            var fullWidth = MeasureWidth(clock);
            check(dateColumn.Visibility == Visibility.Visible,
                "ClockWidget preview must show DateColumn in full mode when DateStyle is enabled.");

            clock.SetCompactState(true);
            var compactWidth = MeasureWidth(clock);
            check(widthEvents == 1 && dateColumn.Visibility == Visibility.Collapsed && compactWidth < fullWidth,
                "ClockWidget must collapse DateColumn and raise ContentWidthChanged when compacted while inactive.");

            clock.SetHoverExpanded(true);
            var hoverWidth = MeasureWidth(clock);
            check(widthEvents == 2 && dateColumn.Visibility == Visibility.Visible && hoverWidth > compactWidth,
                "ClockWidget must restore DateColumn when hover-expanded while inactive.");

            clock.SetHoverExpanded(false);
            check(widthEvents == 3 && dateColumn.Visibility == Visibility.Collapsed,
                "ClockWidget must re-collapse DateColumn when hover expansion ends.");

            clock.SetCompactState(false);
            check(widthEvents == 4 && dateColumn.Visibility == Visibility.Visible,
                "ClockWidget must restore DateColumn when exiting compact mode.");
        }

        // 3. Legacy TimedWidgetTile + DockWidgetItem.IsCompact (Stopwatch & Countdown)
        var stopwatch = new StopwatchDockWidget(TimeProvider.System);
        stopwatch.Refresh();
        var compactNotifications = 0;
        stopwatch.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DockWidgetItem.IsCompact)) compactNotifications++;
        };
        var tile = new TimedWidgetTile { DataContext = stopwatch, Title = stopwatch.Label };
        var tileBorder = (Border)tile.Content!;
        var tileIcon = (FrameworkElement)tile.FindName("TileIcon")!;
        var quickAction = (Button)tile.FindName("QuickActionButton")!;

        var fullTileWidth = MeasureWidth(tile);
        check(quickAction.Visibility == Visibility.Visible
              && tileBorder.Padding == new Thickness(10, 0, 6, 0)
              && tileIcon.Margin == new Thickness(0, 0, 7, 0),
            "TimedWidgetTile full mode must show QuickActionButton with default padding and icon margin.");

        stopwatch.IsCompact = true;
        var compactTileWidth = MeasureWidth(tile);
        check(compactNotifications == 1
              && quickAction.Visibility == Visibility.Collapsed
              && tileBorder.Padding == new Thickness(8, 0, 8, 0)
              && tileIcon.Margin == new Thickness(0, 0, 5, 0)
              && compactTileWidth < fullTileWidth,
            "TimedWidgetTile compact mode must collapse QuickActionButton, tighten padding/icon margin, and shrink width.");

        stopwatch.IsCompact = false;
        var restoredTileWidth = MeasureWidth(tile);
        check(compactNotifications == 2
              && quickAction.Visibility == Visibility.Visible
              && tileBorder.Padding == new Thickness(10, 0, 6, 0)
              && tileIcon.Margin == new Thickness(0, 0, 7, 0)
              && Math.Abs(restoredTileWidth - fullTileWidth) < 0.5,
            "TimedWidgetTile exiting compact mode must restore QuickActionButton, padding, and icon margin.");

        // 4. Existing compact widgets audit: visible compaction + hover restore + ContentWidthChanged
        using (var audio = new AudioWidget())
        {
            var events = 0;
            audio.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(audio);
            audio.SetCompactState(true);
            var compactW = MeasureWidth(audio);
            var volText = (FrameworkElement)audio.FindName("VolumeText")!;
            check(events == 1 && volText.Visibility == Visibility.Collapsed && compactW < fullW,
                "AudioWidget compact mode must hide VolumeText, narrow layout, and raise ContentWidthChanged.");
            audio.SetHoverExpanded(true);
            check(events == 2 && volText.Visibility == Visibility.Visible,
                "AudioWidget hover expansion must restore VolumeText and raise ContentWidthChanged.");
        }

        using (var bt = new BluetoothBatteryWidget
               {
                   BatteryReader = () => Task.FromResult<IReadOnlyList<BluetoothDeviceBatteryInfo>>(
                   [
                       new("MX Master", "bt1", true, 75, BluetoothDeviceKind.Mouse)
                   ])
               })
        {
            bt.RefreshBatteriesAsync().GetAwaiter().GetResult();
            var events = 0;
            bt.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(bt);
            bt.SetCompactState(true);
            var compactW = MeasureWidth(bt);
            check(events == 1 && compactW < fullW,
                "BluetoothBatteryWidget compact mode must narrow layout and raise ContentWidthChanged.");
            bt.SetHoverExpanded(true);
            check(events == 2 && MeasureWidth(bt) > compactW,
                "BluetoothBatteryWidget hover expansion must restore width and raise ContentWidthChanged.");
        }

        using (var focus = new FocusWidget())
        {
            focus.ApplySettings(new DockSettings(Language: AppLanguage.English, FocusMinutes: 25));
            focus.RenderPreview();
            var events = 0;
            focus.ContentWidthChanged += (_, _) => events++;
            var playBtn = (FrameworkElement)focus.FindName("PlayButton")!;
            var fullW = MeasureWidth(focus);
            focus.SetCompactState(true);
            var compactW = MeasureWidth(focus);
            check(events == 1 && playBtn.Visibility == Visibility.Collapsed && compactW < fullW,
                "FocusWidget compact mode must collapse PlayButton, shrink width, and raise ContentWidthChanged.");
            focus.SetHoverExpanded(true);
            check(events == 2 && playBtn.Visibility == Visibility.Visible && MeasureWidth(focus) > compactW,
                "FocusWidget hover expansion must restore PlayButton and raise ContentWidthChanged.");
        }

        using (var media = new MediaWidget())
        {
            var events = 0;
            media.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(media);
            media.SetCompactState(true);
            var compactW = MeasureWidth(media);
            check(events == 1 && compactW < fullW,
                "MediaWidget compact mode must shrink width and raise ContentWidthChanged.");
            media.SetHoverExpanded(true);
            check(events == 2 && MeasureWidth(media) > compactW,
                "MediaWidget hover expansion must restore width and raise ContentWidthChanged.");
        }

        using (var qs = new QuickSettingsWidget())
        {
            var events = 0;
            qs.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(qs);
            qs.SetCompactState(true);
            var compactW = MeasureWidth(qs);
            var nightLight = (FrameworkElement)qs.FindName("NightLightPill")!;
            check(events == 1 && nightLight.Visibility == Visibility.Collapsed && compactW < fullW,
                "QuickSettingsWidget compact mode must hide NightLightPill, shrink width, and raise ContentWidthChanged.");
            qs.SetHoverExpanded(true);
            check(events == 2 && nightLight.Visibility == Visibility.Visible && MeasureWidth(qs) > compactW,
                "QuickSettingsWidget hover expansion must restore NightLightPill and raise ContentWidthChanged.");
        }

        using (var stats = new SystemStatsWidget())
        {
            stats.ApplySettings(new DockSettings(StatsMeters: SystemMeters.Cpu | SystemMeters.Memory));
            var events = 0;
            stats.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(stats);
            stats.SetCompactState(true);
            var compactW = MeasureWidth(stats);
            var cpuLabel = (FrameworkElement)stats.FindName("CpuLabel")!;
            check(events == 1 && cpuLabel.Visibility == Visibility.Collapsed && compactW < fullW,
                "SystemStatsWidget compact mode must collapse labels, narrow width, and raise ContentWidthChanged even with CPU+RAM only.");
            stats.SetHoverExpanded(true);
            check(events == 2 && cpuLabel.Visibility == Visibility.Visible && MeasureWidth(stats) > compactW,
                "SystemStatsWidget hover expansion must restore labels and raise ContentWidthChanged.");
        }

        using (var weather = new WeatherWidget())
        {
            weather.ApplySettings(new DockSettings(Language: AppLanguage.English));
            weather.ApplyReport(new WeatherReport(
                "Ankara", 20, 0, 19,
                [new WeatherDay(new DateOnly(2026, 11, 22), 0, 12, 23)],
                Humidity: 50,
                WindSpeed: 10));
            var events = 0;
            weather.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(weather);
            weather.SetCompactState(true);
            var compactW = MeasureWidth(weather);
            check(events == 1 && compactW < fullW && !LogicalDescendants<TextBlock>(weather).Any(t => t.Text == "Ankara"),
                "WeatherWidget compact mode must hide place/range, shrink width, and raise ContentWidthChanged.");
            weather.SetHoverExpanded(true);
            check(events == 2 && MeasureWidth(weather) > compactW && LogicalDescendants<TextBlock>(weather).Any(t => t.Text == "Ankara"),
                "WeatherWidget hover expansion must restore place/range and raise ContentWidthChanged.");
        }

        using (var storage = new StorageWidget())
        {
            storage.ApplySettings(new DockSettings(Language: AppLanguage.English));
            storage.ApplyDrives([
                new DriveStorageInfo("C:", 213L * 1024 * 1024 * 1024, 476L * 1024 * 1024 * 1024)
            ]);
            var events = 0;
            storage.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(storage);
            storage.SetCompactState(true);
            var compactW = MeasureWidth(storage);
            var compactTexts = LogicalDescendants<TextBlock>(storage).Select(t => t.Text).ToList();
            check(events == 1 && compactW < fullW && compactTexts.Contains("213 GB") && !compactTexts.Any(t => t.Contains('/')),
                "Single-drive StorageWidget compact mode must show concise free space and narrow width.");
            storage.SetHoverExpanded(true);
            var expandedTexts = LogicalDescendants<TextBlock>(storage).Select(t => t.Text).ToList();
            check(events == 2 && MeasureWidth(storage) > compactW && expandedTexts.Contains("213 / 476 GB"),
                "StorageWidget hover expansion must restore full capacity pair.");
        }

        using (var net = new NetSpeedWidget())
        {
            var events = 0;
            net.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(net);
            net.SetCompactState(true);
            var compactW = MeasureWidth(net);
            check(events == 1 && compactW < fullW,
                "NetSpeedWidget compact mode must visibly shrink horizontal width and raise ContentWidthChanged.");
            net.SetHoverExpanded(true);
            check(events == 2 && MeasureWidth(net) > compactW,
                "NetSpeedWidget hover expansion must restore full horizontal width and raise ContentWidthChanged.");
        }

        using (var day = new DayProgressWidget())
        {
            day.ApplySettings(new DockSettings(Language: AppLanguage.English));
            day.Render(new DateTime(2026, 11, 22, 12, 0, 0), force: true);
            var events = 0;
            day.ContentWidthChanged += (_, _) => events++;
            var fullW = MeasureWidth(day);
            day.SetCompactState(true);
            var compactW = MeasureWidth(day);
            check(events == 1 && compactW < fullW,
                "DayProgressWidget compact mode must hide unit label, shrink width, and raise ContentWidthChanged.");
            day.SetHoverExpanded(true);
            check(events == 2 && MeasureWidth(day) > compactW,
                "DayProgressWidget hover expansion must restore unit label and raise ContentWidthChanged.");
        }
    }
}
