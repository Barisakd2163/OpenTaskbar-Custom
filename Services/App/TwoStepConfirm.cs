using System.Windows.Threading;

namespace OpenTaskbar.Services;

/// <summary>
/// Requires two deliberate clicks within a short window before executing sensitive actions
/// (such as toggling Wi-Fi/Bluetooth or opening Task Manager). Leaves no timer running when idle.
/// </summary>
public sealed class TwoStepConfirm : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<long>? _clockMs;
    private readonly int _windowMs;
    private object? _armedKey;
    private Action? _onDisarm;
    private long _armedUntilMs;
    private bool _disposed;

    public TwoStepConfirm(Dispatcher dispatcher, int windowMs = 2500, Func<long>? clockMs = null)
    {
        _windowMs = Math.Max(300, windowMs);
        _clockMs = clockMs;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(_windowMs)
        };
        _timer.Tick += (_, _) => Disarm();
    }

    public bool IsArmed(object key) => _armedKey is not null && Equals(_armedKey, key) && NowMs() <= _armedUntilMs;

    /// <summary>
    /// First click arms <paramref name="key"/> and invokes <paramref name="onArm"/> (returns false).
    /// Second click on the same key within the confirmation window disarms and returns true.
    /// </summary>
    public bool ConsumeOrArm(object key, Action onArm, Action? onDisarm = null)
    {
        if (_disposed) return false;
        if (IsArmed(key))
        {
            Disarm();
            return true;
        }

        Disarm();
        _armedKey = key;
        _onDisarm = onDisarm;
        _armedUntilMs = NowMs() + _windowMs;
        _timer.Start();
        onArm();
        return false;
    }

    public void Disarm()
    {
        _timer.Stop();
        var disarm = _onDisarm;
        _armedKey = null;
        _onDisarm = null;
        _armedUntilMs = 0;
        disarm?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disarm();
    }

    private long NowMs() => _clockMs?.Invoke() ?? Environment.TickCount64;
}
