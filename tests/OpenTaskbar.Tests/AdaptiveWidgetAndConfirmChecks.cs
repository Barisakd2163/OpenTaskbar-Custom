using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
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

        CheckTwoStepConfirm(application.Dispatcher, Check);
        CheckPreviewDimensions(application, Check);
        CheckDensityPersistence(Check);
        CheckAdaptiveDensityAndHysteresis(application.Dispatcher, Check);
        CheckVisualBalanceAndTrayFiltering(application, Check);
        return checks;
    }

    private static void CheckTwoStepConfirm(Dispatcher dispatcher, Action<bool, string> check)
    {
        long fakeNow = 1000;
        var armed = 0;
        var disarmed = 0;
        var key = new object();
        using var confirm = new TwoStepConfirm(dispatcher, windowMs: 2500, clockMs: () => fakeNow);

        var first = confirm.ConsumeOrArm(key, () => armed++, () => disarmed++);
        check(!first && armed == 1 && confirm.IsArmed(key),
            "First click must arm confirmation without executing immediately.");

        fakeNow += 1200;
        var second = confirm.ConsumeOrArm(key, () => armed++, () => disarmed++);
        check(second && armed == 1 && disarmed == 1 && !confirm.IsArmed(key),
            "Second click within confirmation window must execute and disarm.");

        fakeNow += 100;
        var rearmed = confirm.ConsumeOrArm(key, () => armed++, () => disarmed++);
        check(!rearmed && armed == 2 && confirm.IsArmed(key),
            "Subsequent click after execution must require arming again.");

        fakeNow += 3000;
        check(!confirm.IsArmed(key), "Confirmation arm state must expire after the window elapses.");
        var expiredClick = confirm.ConsumeOrArm(key, () => armed++, () => disarmed++);
        check(!expiredClick && armed == 3, "Click after expiration must re-arm rather than execute.");
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

    private static void CheckAdaptiveDensityAndHysteresis(Dispatcher dispatcher, Action<bool, string> check)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        using var coordinator = new WidgetCoordinator(strip, dispatcher, _ => { }, lazy: false);
        var widgets = new[] { "Media", "QuickSettings", "SystemStats", "Audio", "Focus", "Desktops", "Clock" };
        var settings = new DockSettings(
            Widgets: widgets,
            InstalledWidgets: widgets,
            GlobalWidgetDensity: WidgetDensityMode.Auto);
        coordinator.Apply(settings);

        var audio = strip.Children.OfType<AudioWidget>().Single();
        var stats = strip.Children.OfType<SystemStatsWidget>().Single();
        var qs = strip.Children.OfType<QuickSettingsWidget>().Single();

        // Generous space keeps all Auto widgets full.
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 2000, appsContentWidth: 400, settings);
        check(!audio.IsCompact && !stats.IsCompact && !qs.IsCompact,
            "Auto widgets must stay full when generous space is available.");

        // Tight space shrinks high-priority widgets cleanly.
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 1200, appsContentWidth: 720, settings);
        check(qs.IsCompact && stats.IsCompact && audio.IsCompact,
            "Tight space must automatically compact bulky widgets.");

        // Hysteresis prevents immediate oscillation when budget increases by a tiny margin.
        var compactSum = strip.Children.OfType<OpenTaskbar.Widgets.DockWidget>()
            .Where(w => w.Visibility == Visibility.Visible)
            .Sum(w => w.IsCompact ? w.CompactWidthEstimate : w.FullWidthEstimate);
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 720 + compactSum + 10, appsContentWidth: 720, settings, hysteresisMargin: 32);
        check(qs.IsCompact && stats.IsCompact && audio.IsCompact,
            "Tiny free-space increases inside hysteresis margin must not bounce widgets back to full.");

        // Per-widget overrides beat global density.
        var overridden = settings with
        {
            GlobalWidgetDensity = WidgetDensityMode.Auto,
            WidgetDensityOverrides = new Dictionary<string, WidgetDensityMode>
            {
                ["Audio"] = WidgetDensityMode.Compact,
                ["QuickSettings"] = WidgetDensityMode.Full
            }
        };
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 2000, appsContentWidth: 400, overridden);
        check(audio.IsCompact && !qs.IsCompact,
            "Per-widget Compact/Full overrides must take precedence over global Auto mode.");

        // Large widgetScale (1.35x) consumes proportionally more horizontal budget than 1.0x.
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 2200, appsContentWidth: 200, settings, widgetScale: 1.0);
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 1650, appsContentWidth: 650, settings, widgetScale: 0.78);
        var compactAtSmallScale = qs.IsCompact || stats.IsCompact || audio.IsCompact;
        // 1640 keeps the budget just below the 1.35x-scaled widget total in the integrated layout.
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 1640, appsContentWidth: 650, settings, widgetScale: 1.35);
        check(!compactAtSmallScale && (qs.IsCompact || stats.IsCompact || audio.IsCompact),
            $"Larger WidgetScale must factor scaled widget widths into adaptive compaction (small={compactAtSmallScale}, qs={qs.IsCompact}, stats={stats.IsCompact}, audio={audio.IsCompact}).");

        // Fallback single-step compaction helper compacts Auto widgets incrementally when real measurements overflow.
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 2000, appsContentWidth: 200, settings);
        check(!qs.IsCompact && !stats.IsCompact && !audio.IsCompact, "Reset to full before step compaction test.");
        check(coordinator.TryCompactNextAutoWidget(settings), "TryCompactNextAutoWidget must compact highest-priority Auto widget.");
    }

    private static void CheckVisualBalanceAndTrayFiltering(Application application, Action<bool, string> check)
    {
        var startLogoSize = application.TryFindResource("StartLogoSize");
        check(startLogoSize is double d && d > 0,
            "Theme resources must expose StartLogoSize for proportional scaling across dock sizes.");

        foreach (var dockSize in new[] { DockSize.Compact, DockSize.Medium, DockSize.Large })
        {
            var p = DockMetrics.For(dockSize);
            check(p.Icon * DockMetrics.HoverScale < p.Cell,
                $"Magnified icons in {dockSize} mode must fit inside cell spacing without overlapping adjacent apps.");
        }

        using var hover = new WidgetHoverCoordinator(application.Dispatcher, _ => false, _ => { });
        using var qsWidget = new QuickSettingsWidget();
        using var audioWidget = new AudioWidget();
        hover.Attach(qsWidget);
        hover.Attach(audioWidget);
        check(!hover.IsHoverActive, "WidgetHoverCoordinator must remain inactive at rest.");
        check(qsWidget.BuildHoverFlyoutContent() is not null && audioWidget.BuildHoverFlyoutContent() is not null,
            "Compact-capable widgets must produce hover flyout content cleanly.");
        hover.CollapseActive();
        check(!hover.IsHoverActive, "CollapseActive must reset hover active state.");
    }
}
