using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Owns the dock widgets: builds them into the strip, applies visibility from settings, and drives a shared 1 s tick.
/// </summary>
public sealed class WidgetCoordinator : IDisposable
{
    private readonly List<(DockWidget Widget, Func<DockSettings, bool> IsEnabled)> _widgets;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public WidgetCoordinator(Panel strip, Dispatcher dispatcher, Action<string> reportError)
    {
        _widgets =
        [
            (new MediaWidget(), s => s.ShowMedia),
            (new SystemStatsWidget(), s => s.ShowSystemStats),
            (new FocusWidget(), s => s.ShowFocus),
            (new AudioWidget(), s => s.ShowAudio),
            (new QuickSettingsWidget(), s => s.ShowQuickSettings),
            (new ClockWidget(), s => s.ShowClock)
        ];
        foreach (var (widget, _) in _widgets)
        {
            widget.ReportError = reportError;
            strip.Children.Add(widget);
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
    }

    public void Apply(DockSettings settings)
    {
        if (_disposed) return;
        var anyActive = false;
        foreach (var (widget, isEnabled) in _widgets)
        {
            widget.ApplySettings(settings);
            if (isEnabled(settings))
            {
                widget.Activate();
                anyActive = true;
            }
            else
            {
                widget.Deactivate();
            }
        }

        if (anyActive)
        {
            Timer_Tick(null, EventArgs.Empty);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        foreach (var (widget, _) in _widgets) widget.Tick(now);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        foreach (var (widget, _) in _widgets) widget.Dispose();
    }
}
