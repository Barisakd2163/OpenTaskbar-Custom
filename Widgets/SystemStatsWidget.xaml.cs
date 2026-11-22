using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// V19 System Stats widget: compact horizontal CPU / RAM / GPU metrics on the dock,
/// with a dedicated metrics flyout showing progress bars, temperatures, and Task Manager shortcut.
/// </summary>
public partial class SystemStatsWidget : DockWidget
{
    private const double TemperatureScaleCelsius = 100;
    private const double ValueEpsilon = 0.005;
    private const long FastSamplePeriodMs = 900;
    private const long SensorSamplePeriodMs = 1900;
    private const int SensorHideMisses = 5;
    private const int SensorShowSamples = 2;

    private struct MetricState
    {
        public bool Initialized;
        public int Shown;
        public double Value;
        public AppLanguage Language;
    }

    private readonly SystemStats _stats = new();
    private readonly object _sensorGate = new();
    private SystemSensors? _sensors;
    private int _sensorGeneration;
    private int _sensorSampling;
    private long _lastFastSampleTickMs;
    private long _lastSensorSampleTickMs;

    private MetricState _cpuState;
    private MetricState _memoryState;
    private MetricState _gpuState;
    private MetricState _gpuTempState;
    private bool _hasGpu;
    private bool _hasGpuTemp;
    private SystemMeters _configuredMeters;
    private readonly Dictionary<FrameworkElement, int> _missingSensorSamples = [];
    private readonly Dictionary<FrameworkElement, int> _validSensorSamples = [];

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 30;
    public override double CompactWidthEstimate => 112;
    public override double FullWidthEstimate
    {
        get
        {
            var meters = Settings.StatsMeters;
            var count = 0;
            if (meters.HasFlag(SystemMeters.Cpu)) count++;
            if (meters.HasFlag(SystemMeters.Memory)) count++;
            if (meters.HasFlag(SystemMeters.Gpu) && _hasGpu) count++;
            if (meters.HasFlag(SystemMeters.GpuTemperature) && _hasGpuTemp) count++;
            return Math.Max(96, 36 + count * 48);
        }
    }

    public SystemStatsWidget() => InitializeComponent();

    protected override void OnCompactChanged(bool compact) => SyncMeterVisibility();
    protected override void OnHoverExpandedChanged(bool expanded) => SyncMeterVisibility();

    // System Meters is a pure info widget: metrics are already visible on the dock, so no hover/click flyout.
    public override UIElement? BuildHoverFlyoutContent() => null;

    protected override void OnActivated()
    {
        _stats.SampleCpu();
        Interlocked.Increment(ref _sensorGeneration);
        ApplyMeterSelection();
        SampleFastCounters(sampleCpu: false);
        var nowMs = Environment.TickCount64;
        _lastFastSampleTickMs = nowMs;
        _lastSensorSampleTickMs = nowMs - 1000;
        PrimeSensorBaselineIfNeeded();
    }

    protected override void OnDeactivated()
    {
        var generation = Interlocked.Increment(ref _sensorGeneration);
        _ = Task.Run(() =>
        {
            lock (_sensorGate)
            {
                if (generation != Volatile.Read(ref _sensorGeneration)) return;
                _sensors?.Dispose();
                _sensors = null;
            }
        });
        _lastFastSampleTickMs = 0;
        _lastSensorSampleTickMs = 0;
    }



    protected override void OnSettingsChanged()
    {
        Interlocked.Increment(ref _sensorGeneration);
        ApplyMeterSelection();
        if (IsActive)
        {
            _lastFastSampleTickMs = 0;
            _lastSensorSampleTickMs = 0;
            OnTick(DateTime.Now);
        }
    }

    protected override void OnTick(DateTime now)
    {
        var tickMs = Environment.TickCount64;
        if (_lastFastSampleTickMs == 0 || tickMs - _lastFastSampleTickMs >= FastSamplePeriodMs)
        {
            _lastFastSampleTickMs = tickMs;
            SampleFastCounters(sampleCpu: true);
        }

        if (_lastSensorSampleTickMs == 0 || tickMs - _lastSensorSampleTickMs >= SensorSamplePeriodMs)
        {
            _lastSensorSampleTickMs = tickMs;
            QueueSensorSample(Settings.StatsMeters);
        }
    }

    private void SampleFastCounters(bool sampleCpu)
    {
        var meters = Settings.StatsMeters;
        if (sampleCpu && meters.HasFlag(SystemMeters.Cpu) && _stats.SampleCpu() is { } cpu)
            SetUsage(CpuValueText, ref _cpuState, cpu);
        if (meters.HasFlag(SystemMeters.Memory) && SystemStats.SampleMemory() is { } memory)
            SetUsage(RamValueText, ref _memoryState, memory);
        UpdateTooltip();
    }

    private void PrimeSensorBaselineIfNeeded()
    {
        var meters = Settings.StatsMeters;
        if ((meters & (SystemMeters.Gpu | SystemMeters.GpuTemperature)) == 0) return;
        if (Interlocked.CompareExchange(ref _sensorSampling, 1, 0) != 0) return;
        var generation = Volatile.Read(ref _sensorGeneration);
        _ = Task.Run(async () =>
        {
            double? gpu = null;
            double? gpuTemp = null;
            try
            {
                lock (_sensorGate)
                {
                    if (generation != Volatile.Read(ref _sensorGeneration)) return;
                    _sensors ??= new SystemSensors(meters);
                    _sensors.Configure(meters);
                    if (meters.HasFlag(SystemMeters.Gpu)) _sensors.SampleGpuUsage();
                }

                await Task.Delay(350).ConfigureAwait(false);

                lock (_sensorGate)
                {
                    if (generation != Volatile.Read(ref _sensorGeneration)) return;
                    if (meters.HasFlag(SystemMeters.Gpu)) gpu = _sensors?.SampleGpuUsage();
                    if (meters.HasFlag(SystemMeters.GpuTemperature)) gpuTemp = _sensors?.SampleGpuTemperature();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Sensor baseline/initial sample failed: {ex.Message}");
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _sensorSampling, 0);
            }

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!IsActive || generation != Volatile.Read(ref _sensorGeneration)) return;
                ApplySensorBatch(gpu, gpuTemp);
            });
        });
    }

    private void QueueSensorSample(SystemMeters meters)
    {
        if ((meters & (SystemMeters.Gpu | SystemMeters.GpuTemperature)) == 0) return;
        if (Interlocked.CompareExchange(ref _sensorSampling, 1, 0) != 0) return;
        var generation = Volatile.Read(ref _sensorGeneration);

        _ = Task.Run(() =>
        {
            double? gpu = null;
            double? gpuTemp = null;
            try
            {
                lock (_sensorGate)
                {
                    if (generation != Volatile.Read(ref _sensorGeneration)) return;
                    _sensors ??= new SystemSensors(meters);
                    _sensors.Configure(meters);
                    if (meters.HasFlag(SystemMeters.Gpu)) gpu = _sensors.SampleGpuUsage();
                    if (meters.HasFlag(SystemMeters.GpuTemperature)) gpuTemp = _sensors.SampleGpuTemperature();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Sensor sampling failed: {ex.Message}");
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _sensorSampling, 0);
            }

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!IsActive || generation != Volatile.Read(ref _sensorGeneration)) return;
                ApplySensorBatch(gpu, gpuTemp);
            });
        });
    }

    private void ApplySensorBatch(double? gpu, double? gpuTemp)
    {
        var currentMeters = Settings.StatsMeters;
        UpdateSensor(GpuButton, currentMeters.HasFlag(SystemMeters.Gpu) ? gpu : null,
            usage => SetUsage(GpuValueText, ref _gpuState, usage));
        UpdateSensor(GpuTemperatureButton, currentMeters.HasFlag(SystemMeters.GpuTemperature) ? gpuTemp : null,
            celsius => SetTemperature(GpuTempValueText, ref _gpuTempState, celsius));
        UpdateTooltip();
    }

    private void ApplyMeterSelection()
    {
        var meters = Settings.StatsMeters;
        if (_configuredMeters != meters)
        {
            _configuredMeters = meters;
            _hasGpu = false;
            _hasGpuTemp = false;
            _missingSensorSamples.Clear();
            _validSensorSamples.Clear();
        }
        SyncMeterVisibility();
    }

    private void SyncMeterVisibility()
    {
        var compact = IsCompact && !IsHoverExpanded;
        var meters = Settings.StatsMeters;
        var changed = false;

        var showCpu = meters.HasFlag(SystemMeters.Cpu);
        var showRam = meters.HasFlag(SystemMeters.Memory);
        var showGpu = !compact && meters.HasFlag(SystemMeters.Gpu) && _hasGpu;
        var showGpuTemp = !compact && meters.HasFlag(SystemMeters.GpuTemperature) && _hasGpuTemp;

        StatsCardButton.Padding = compact ? new Thickness(7, 0, 7, 0) : new Thickness(10, 0, 10, 0);
        RamDivider.Margin = compact ? new Thickness(6, 0, 6, 0) : new Thickness(9, 0, 9, 0);

        Set(CpuLabel, !compact);
        Set(RamLabel, !compact);
        Set(GpuLabel, !compact);

        Set(CpuButton, showCpu);
        Set(RamDivider, showCpu && showRam);
        Set(MemoryButton, showRam);
        Set(GpuDivider, (showCpu || showRam) && showGpu);
        Set(GpuButton, showGpu);
        Set(GpuTempDivider, (showCpu || showRam || showGpu) && showGpuTemp);
        Set(GpuTemperatureButton, showGpuTemp);

        if (changed) RaiseContentWidthChanged();

        void Set(FrameworkElement element, bool visible)
        {
            var target = visible ? Visibility.Visible : Visibility.Collapsed;
            if (element.Visibility == target) return;
            element.Visibility = target;
            changed = true;
        }
    }

    private void UpdateSensor(FrameworkElement button, double? reading, Action<double> render)
    {
        if (reading is null)
        {
            var misses = Math.Min(_missingSensorSamples.GetValueOrDefault(button) + 1, SensorHideMisses);
            _missingSensorSamples[button] = misses;
            _validSensorSamples.Remove(button);
            if (misses >= SensorHideMisses) SetAvailability(button, false);
            return;
        }

        _missingSensorSamples.Remove(button);
        var valid = Math.Min(_validSensorSamples.GetValueOrDefault(button) + 1, SensorShowSamples);
        _validSensorSamples[button] = valid;
        render(reading.Value);
        if (valid >= SensorShowSamples) SetAvailability(button, true);
    }

    /// <summary>Test seam: feeds a reading through the same availability logic as the native sensors.</summary>
    internal void ApplySensorReading(FrameworkElement button, double? reading, Action<double>? render = null) =>
        UpdateSensor(button, reading, render ?? (_ => { }));

    private void SetAvailability(FrameworkElement button, bool available)
    {
        if (button == GpuButton)
        {
            if (_hasGpu == available) return;
            _hasGpu = available;
        }
        else if (button == GpuTemperatureButton)
        {
            if (_hasGpuTemp == available) return;
            _hasGpuTemp = available;
        }
        else return;

        SyncMeterVisibility();
    }

    private void SetUsage(TextBlock target, ref MetricState state, double value)
    {
        var percent = (int)Math.Round(value * 100);
        var lang = Settings.Language;
        if (state.Initialized && state.Shown == percent && state.Language == lang && Math.Abs(value - state.Value) < ValueEpsilon)
            return;

        state.Value = value;
        state.Shown = percent;
        state.Language = lang;
        state.Initialized = true;
        target.Text = $"{percent.ToString(CultureInfo.InvariantCulture)}%";
    }

    private void SetTemperature(TextBlock target, ref MetricState state, double celsius)
    {
        var rounded = (int)Math.Round(celsius);
        var normalized = celsius / TemperatureScaleCelsius;
        var lang = Settings.Language;
        if (state.Initialized && state.Shown == rounded && state.Language == lang && Math.Abs(normalized - state.Value) < ValueEpsilon)
            return;

        state.Value = normalized;
        state.Shown = rounded;
        state.Language = lang;
        state.Initialized = true;
        target.Text = $"{rounded.ToString(CultureInfo.InvariantCulture)}°";
    }

    private void UpdateTooltip()
    {
        var parts = new List<string>();
        if (_cpuState.Initialized) parts.Add($"CPU {_cpuState.Shown}%");
        if (_memoryState.Initialized) parts.Add($"RAM {_memoryState.Shown}%");
        if (_hasGpu && _gpuState.Initialized) parts.Add($"GPU {_gpuState.Shown}%");
        if (_hasGpuTemp && _gpuTempState.Initialized) parts.Add($"GPU {_gpuTempState.Shown}°C");
        StatsCardButton.ToolTip = parts.Count > 0 ? string.Join(" · ", parts) : UiText.Get("WidgetSystemStats");
    }
}
