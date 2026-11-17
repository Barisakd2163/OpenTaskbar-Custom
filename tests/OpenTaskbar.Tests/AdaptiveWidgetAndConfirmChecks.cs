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
        CheckDockItemOrderPersistence(Check);
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

        // Tight space must not truncate widgets — the dock scrolls instead.
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 1200, appsContentWidth: 720, settings);
        check(!qs.IsCompact && !stats.IsCompact && !audio.IsCompact,
            "Widgets must never be auto-compacted to save space; overflow scrolls instead.");

        // Per-widget overrides still control density explicitly.
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

        // Scale and width budget must not affect widget density.
        coordinator.UpdateAdaptiveDensity(maxTotalWidth: 1640, appsContentWidth: 650, settings, widgetScale: 1.35);
        check(!qs.IsCompact && !stats.IsCompact && !audio.IsCompact,
            "Widget scale and available width must never compact widgets.");

        check(!coordinator.TryCompactNextAutoWidget(settings),
            "Automatic single-step compaction must stay disabled.");
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
