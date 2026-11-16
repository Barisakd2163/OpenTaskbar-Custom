using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class StopwatchDockWidget(TimeProvider timeProvider) : DockWidget(DockWidgetService.StopwatchId, UiText.Get("WidgetStopwatch")), ITimedDockWidget
{
    private TimeSpan _elapsed;
    private long _startedAt;

    public bool IsRunning { get; private set; }
    public bool CanToggle => true;
    public TimeSpan Elapsed => _elapsed + (IsRunning ? timeProvider.GetElapsedTime(_startedAt) : TimeSpan.Zero);

    public void Toggle()
    {
        if (IsRunning)
            _elapsed = Elapsed;
        else
            _startedAt = timeProvider.GetTimestamp();
        IsRunning = !IsRunning;
        NotifyChanged(nameof(IsRunning));
        Refresh();
    }

    public void Reset()
    {
        IsRunning = false;
        _elapsed = TimeSpan.Zero;
        NotifyChanged(nameof(IsRunning));
        Refresh();
    }

    public void Refresh()
    {
        var elapsed = Elapsed;
        Value = $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
