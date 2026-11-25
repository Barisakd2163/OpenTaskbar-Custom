using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OpenTaskbar;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

internal static class DockEdgeTransitionChecks
{
    public static int Run(App app)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var preferencesField = typeof(App).GetField("_preferences", flags)!;
        var originalPreferences = preferencesField.GetValue(app);
        var inspectionProperty = typeof(App).GetProperty(nameof(App.IsInspection))!;
        var originalInspection = app.IsInspection;
        var preferences = new ApplicationPreferences(new DockSettingsStore(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json")));
        var settings = new DockSettings(Widgets: [], InstalledWidgets: [], ShowOnAllMonitors: false,
            ShowRecycleBin: false, CheckUpdates: false, AnimationsEnabled: false, MagnificationEnabled: false);
        var currentProperty = typeof(ApplicationPreferences).GetProperty(nameof(ApplicationPreferences.Current))!;
        currentProperty.SetValue(preferences, settings);
        preferencesField.SetValue(app, preferences);
        inspectionProperty.SetValue(app, true);
        MainWindow? window = null;
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        try
        {
            // Never Show/EnsureHandle: this exercises real WPF sizing without touching Explorer or AppBars.
            window = new MainWindow();
            var fit = typeof(MainWindow).GetMethod("FitDockToContent", flags)!;
            var applyEdge = typeof(MainWindow).GetMethod("ApplyEdge", flags)!;
            var applyDesktop = typeof(MainWindow).GetMethod("ApplyShowDesktopButtonLayout", flags)!;
            var strip = (Button)window.FindName("ShowDesktopButton");
            var viewer = (ScrollViewer)window.FindName("DockScrollViewer");
            var layout = (FrameworkElement)window.FindName("DockLayoutRoot");
            applyEdge.Invoke(window, null);
            foreach (var edge in new[] { DockEdge.Left, DockEdge.Bottom, DockEdge.Right, DockEdge.Bottom })
            {
                settings = settings with { Edge = edge };
                currentProperty.SetValue(preferences, settings);
                // Reproduce the settings callback ordering: fit uses the new edge while strip dimensions
                // still describe the previous edge (one axis is WPF Auto/NaN).
                Check(double.IsNaN(edge == DockEdge.Bottom ? strip.Width : strip.Height),
                    $"The {edge} transition must exercise an Auto-sized strip on the new axis.");
                applyDesktop.Invoke(window, null);
                fit.Invoke(window, null);
                var limit = edge == DockEdge.Bottom ? viewer.MaxWidth : viewer.MaxHeight;
                Check(double.IsFinite(limit) && limit > 0,
                    $"Switching to {edge} must leave a finite positive application scroll area before axis layout catches up.");
                Check(edge == DockEdge.Bottom ? layout.Margin.Right == 7 : layout.Margin.Bottom == 7,
                    $"Switching to {edge} must reserve the actual Show Desktop strip instead of propagating Auto/NaN.");
                applyEdge.Invoke(window, null);
            }
            settings = settings with { ShowDesktopButton = false };
            currentProperty.SetValue(preferences, settings);
            applyDesktop.Invoke(window, null);
            fit.Invoke(window, null);
            Check(layout.Margin.Right == 0 && layout.Margin.Bottom == 0 && double.IsFinite(viewer.MaxWidth),
                "Hiding Show Desktop after an edge transition must release its inset and keep app space usable.");
            return checks;
        }
        finally
        {
            if (window is not null)
            {
                // The hidden test window never owned taskbar state; skip the production shell restore hook.
                var closing = typeof(MainWindow).GetMethod("Window_Closing", flags)!;
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, closing);
                window.Close();
            }
            preferencesField.SetValue(app, originalPreferences);
            inspectionProperty.SetValue(app, originalInspection);
        }
    }
}
