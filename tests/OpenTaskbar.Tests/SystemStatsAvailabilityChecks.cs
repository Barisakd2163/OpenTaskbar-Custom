using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

internal static class SystemStatsAvailabilityChecks
{
    private static readonly string[] RequiredTelemetryKeys =
    [
        "StatsUptime",
        "StatsUptimeDaysFormat",
        "StatsUptimeHoursFormat",
        "StatsUptimeMinutesFormat",
        "StatsLogicalCores",
        "StatsMemoryUsed",
        "StatsMemoryAvailable",
        "StatsTopProcesses",
        "StatsTopProcessesEmpty",
        "StatsSamplingProcesses",
        "ResourceMonitor"
    ];

    public static int Run(Application app)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var settings = new DockSettings
        {
            StatsMeters = SystemMeters.Cpu | SystemMeters.Memory | SystemMeters.Gpu | SystemMeters.GpuTemperature
        };
        using var widget = new SystemStatsWidget();
        widget.ApplySettings(settings);
        var widthChanges = 0;
        widget.ContentWidthChanged += (_, _) => widthChanges++;
        widget.Activate();

        Check(widget.GpuButton.Visibility == Visibility.Collapsed
              && widget.GpuTemperatureButton.Visibility == Visibility.Collapsed,
            "A newly activated widget must not show empty sensor tiles before a real reading.");

        widget.ApplySensorReading(widget.GpuButton, 0.1);
        Check(widget.GpuButton.Visibility == Visibility.Collapsed && widthChanges == 0,
            "A single reading must not resize the dock while the meter is still being validated.");
        widget.ApplySensorReading(widget.GpuButton, 0.2);
        Check(widget.GpuButton.Visibility == Visibility.Visible && widthChanges == 1,
            "A validated meter must appear and notify the host to refit the dock.");

        widget.ApplySensorReading(widget.GpuTemperatureButton, 48);
        widget.ApplySensorReading(widget.GpuTemperatureButton, 49);
        Check(widget.GpuTemperatureButton.Visibility == Visibility.Visible && widthChanges == 2,
            "The GPU temperature tile uses the same validation as GPU usage.");

        for (var i = 0; i < 4; i++) widget.ApplySensorReading(widget.GpuButton, null);
        Check(widget.GpuButton.Visibility == Visibility.Visible && widthChanges == 2,
            "A few transient misses must not make the dock jump in width.");
        widget.ApplySensorReading(widget.GpuButton, null);
        Check(widget.GpuButton.Visibility == Visibility.Collapsed && widthChanges == 3,
            "A meter that keeps failing must be hidden and trigger a refit.");

        widget.ApplySensorReading(widget.GpuButton, 0.3);
        Check(widget.GpuButton.Visibility == Visibility.Collapsed,
            "One recovered reading must not bring the meter back immediately.");
        widget.ApplySensorReading(widget.GpuButton, 0.4);
        Check(widget.GpuButton.Visibility == Visibility.Visible && widthChanges == 4,
            "Repeated recovered readings must restore the meter and refit the dock.");

        // Interactive dock card & flyout contracts
        Check(widget.StatsCardButton.Cursor == Cursors.Hand,
            "Redesigned SystemStatsWidget card button must use the interactive Hand cursor.");
        Check(widget.BuildHoverFlyoutContent() is not null,
            "SystemStatsWidget must provide a compact hover flyout summary.");

        var snapshot = SystemStats.SampleMemorySnapshot();
        Check(snapshot is { TotalBytes: > 0 } s && s.UsedBytes + s.AvailableBytes == s.TotalBytes && s.UsageRatio is >= 0 and <= 1,
            "SampleMemorySnapshot must return consistent physical memory totals and usage ratio.");

        // Ring buffer order and wrap-around
        var ring = new MetricRingBuffer(3);
        ring.Push(0.1);
        ring.Push(0.5);
        ring.Push(0.8);
        ring.Push(0.95);
        Span<double> copied = stackalloc double[3];
        var written = ring.CopyTo(copied);
        Check(written == 3 && Math.Abs(copied[0] - 0.5) < 0.0001 && Math.Abs(copied[1] - 0.8) < 0.0001 && Math.Abs(copied[2] - 0.95) < 0.0001,
            "MetricRingBuffer must overwrite oldest samples and copy oldest-to-newest.");

        // Semantic threshold resource keys
        Check(SystemStatsWidget.LoadBrushKey(0.42, "WidgetBlue") == "WidgetBlue"
              && SystemStatsWidget.LoadBrushKey(0.75, "WidgetBlue") == "WidgetWarning"
              && SystemStatsWidget.LoadBrushKey(0.90, "WidgetBlue") == "WidgetFocusAccent",
            "LoadBrushKey must apply design-system thresholds at 75% and 90%.");
        Check(SystemStatsWidget.ThermalBrushKey(55) == "WidgetGreen"
              && SystemStatsWidget.ThermalBrushKey(72) == "WidgetWarning"
              && SystemStatsWidget.ThermalBrushKey(86) == "WidgetFocusAccent",
            "ThermalBrushKey must map cool/warm/hot GPU temperatures to semantic tokens.");
        Check(SystemStatsWidget.FormatProcessMemory(1536L * 1024 * 1024, CultureInfo.InvariantCulture) == "1.5 GB"
              && SystemStatsWidget.FormatProcessMemory(256L * 1024 * 1024, CultureInfo.InvariantCulture) == "256 MB",
            "FormatProcessMemory must format GiB and MiB working sets cleanly.");

        // Rich flyout structure & Dark/Light dynamic theme binding
        var prevLang = UiText.Current.Language;
        try
        {
            UiText.Current.SetLanguage(AppLanguage.English);
            Check(SystemStatsWidget.FormatUptime(new TimeSpan(2, 5, 14, 0)) == "2d 5h 14m"
                  && SystemStatsWidget.FormatUptime(new TimeSpan(0, 3, 9, 0)) == "3h 9m"
                  && SystemStatsWidget.FormatUptime(new TimeSpan(0, 0, 27, 0)) == "27m",
                "FormatUptime must format days, hours, and minutes cleanly.");

            var flyout = widget.BuildPreviewFlyoutContent();
            Check(flyout is not null, "BuildPreviewFlyoutContent must return the telemetry dashboard.");
            var buttons = LogicalDescendants<Button>(flyout!).ToList();
            var buttonNames = buttons.Select(System.Windows.Automation.AutomationProperties.GetName).ToList();
            Check(buttonNames.Contains(UiText.Get("TaskManager")) && buttonNames.Contains(UiText.Get("ResourceMonitor")),
                "Telemetry flyout footer must include Task Manager and Resource Monitor actions.");
            var sparklines = LogicalDescendants<TelemetrySparkline>(flyout!).ToList();
            Check(sparklines.Count == 3,
                "Telemetry flyout must provide CPU, RAM, and GPU sparklines.");

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                var expectedSurface = app.TryFindResource("SurfaceSelected") as Brush;
                Check(expectedSurface is not null, $"SurfaceSelected token must exist in {theme} theme.");
                var themedFlyout = widget.BuildTelemetryFlyout(isPreview: true);
                var cards = LogicalDescendants<Border>(themedFlyout)
                    .Where(b => Math.Abs(b.CornerRadius.TopLeft - 10) < 0.01)
                    .ToList();
                Check(cards.Count >= 3 && cards.All(c => ReferenceEquals(c.Background, expectedSurface)),
                    $"SystemStatsWidget flyout cards must resolve to SurfaceSelected in {theme} theme.");
            }
        }
        finally
        {
            ThemeManager.Apply(AppTheme.Dark);
            UiText.Current.SetLanguage(prevLang);
        }

        // All 14 localization catalogs must contain every new key without UTF-8 BOM
        var assembly = typeof(LanguageCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("OpenTaskbar.Localization.Languages.", StringComparison.Ordinal))
            .ToArray();
        Check(resources.Length == 14, "All 14 language resources must be present.");
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            Check(stream.ReadByte() == 0x7B, $"{resource} must not contain a UTF-8 BOM.");
            stream.Position = 0;
            using var doc = JsonDocument.Parse(stream);
            foreach (var key in RequiredTelemetryKeys)
            {
                Check(doc.RootElement.TryGetProperty(key, out var val)
                      && val.ValueKind == JsonValueKind.String
                      && !string.IsNullOrWhiteSpace(val.GetString()),
                    $"{resource} is missing a non-empty translation for '{key}'.");
            }
        }

        widget.Deactivate();
        return checks;
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in LogicalDescendants<T>(child)) yield return nested;
        }
    }
}
