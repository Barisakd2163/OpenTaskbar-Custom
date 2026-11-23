using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Interactive System Resources widget: refined dock telemetry segments with live micro load bars and semantic
/// thermal/load accenting, backed by a rich DockFlyout with live rolling sparklines, CPU/RAM/GPU breakdown cards,
/// lazily sampled top processes, and Task Manager / Resource Monitor quick actions.
/// </summary>
public partial class SystemStatsWidget : DockWidget
{
    private const double TemperatureScaleCelsius = 100.0;
    private const double WarningThreshold = 0.75;
    private const double CriticalThreshold = 0.90;
    private const double ThermalWarningCelsius = 70.0;
    private const double ThermalCriticalCelsius = 85.0;
    private const double ValueEpsilon = 0.005;
    private const long FastSamplePeriodMs = 900;
    private const long SensorSamplePeriodMs = 1900;
    private const long ProcessSamplePeriodMs = 2000;
    private const int SensorHideMisses = 5;
    private const int SensorShowSamples = 2;
    private const int SparklineCapacity = 45;
    private const int TopProcessCount = 5;

    private struct MetricState
    {
        public bool Initialized;
        public int Shown;
        public double Value;
        public string? AccentKey;
        public string? TextBrushKey;
        public AppLanguage Language;
    }

    private sealed class TelemetryFlyoutView
    {
        public required TextBlock UptimeText { get; init; }
        public required TextBlock CpuValueText { get; init; }
        public required Ellipse CpuDot { get; init; }
        public required TelemetrySparkline CpuSparkline { get; init; }

        public required TextBlock RamValueText { get; init; }
        public required TextBlock RamPairText { get; init; }
        public required TextBlock RamUsedText { get; init; }
        public required TextBlock RamAvailableText { get; init; }
        public required Ellipse RamDot { get; init; }
        public required Border RamBarFill { get; init; }
        public required ColumnDefinition RamBarFillCol { get; init; }
        public required ColumnDefinition RamBarEmptyCol { get; init; }
        public required TelemetrySparkline RamSparkline { get; init; }

        public required Border GpuCard { get; init; }
        public required TextBlock GpuValueText { get; init; }
        public required Border GpuTempBadge { get; init; }
        public required TextBlock GpuTempText { get; init; }
        public required Ellipse GpuDot { get; init; }
        public required TelemetrySparkline GpuSparkline { get; init; }

        public required StackPanel ProcessesList { get; init; }
    }

    private sealed class HoverFlyoutView
    {
        public required Border Container { get; init; }
        public required TextBlock UptimeText { get; init; }
        public required TextBlock CpuValueText { get; init; }
        public required Border CpuBarFill { get; init; }
        public required ColumnDefinition CpuBarFillCol { get; init; }
        public required ColumnDefinition CpuBarEmptyCol { get; init; }
        public required TextBlock RamValueText { get; init; }
        public required TextBlock RamDetailText { get; init; }
        public required Border RamBarFill { get; init; }
        public required ColumnDefinition RamBarFillCol { get; init; }
        public required ColumnDefinition RamBarEmptyCol { get; init; }
        public required StackPanel GpuRow { get; init; }
        public required TextBlock GpuValueText { get; init; }
        public required Border GpuBarFill { get; init; }
        public required ColumnDefinition GpuBarFillCol { get; init; }
        public required ColumnDefinition GpuBarEmptyCol { get; init; }
    }

    private readonly SystemStats _stats = new();
    private readonly DockFlyout _flyout = new();
    private readonly object _sensorGate = new();
    private readonly MetricRingBuffer _cpuHistory = new(SparklineCapacity);
    private readonly MetricRingBuffer _memoryHistory = new(SparklineCapacity);
    private readonly MetricRingBuffer _gpuHistory = new(SparklineCapacity);
    private readonly Dictionary<FrameworkElement, int> _missingSensorSamples = [];
    private readonly Dictionary<FrameworkElement, int> _validSensorSamples = [];

    private SystemSensors? _sensors;
    private int _sensorGeneration;
    private int _sensorSampling;
    private long _lastFastSampleTickMs;
    private long _lastSensorSampleTickMs;

    private CancellationTokenSource? _processSampleCts;
    private int _processGeneration;
    private int _processSampling;
    private long _lastProcessSampleTickMs;
    private IReadOnlyList<ProcessMemoryEntry> _lastTopProcesses = [];

    private MetricState _cpuState;
    private MetricState _memoryState;
    private MetricState _gpuState;
    private MetricState _gpuTempState;
    private MemorySnapshot? _lastMemorySnapshot;
    private bool _hasGpu;
    private bool _hasGpuTemp;
    private SystemMeters _configuredMeters;
    private bool _lastAppliedCompact;
    private bool _lastAppliedVertical;
    private string? _lastStatsIconKey;

    private TelemetryFlyoutView? _clickFlyout;
    private HoverFlyoutView? _hoverFlyout;

    internal DockFlyout ClickFlyout => _flyout;
    internal Func<int, CancellationToken, IReadOnlyList<ProcessMemoryEntry>> TopProcessesReader { get; set; } = SystemStats.SampleTopProcesses;
    internal Action OpenTaskManagerAction { get; set; }
    internal Action OpenResourceMonitorAction { get; set; }

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 30;
    public override double CompactWidthEstimate => 100;
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
            return Math.Max(104, 42 + count * 50);
        }
    }

    public SystemStatsWidget()
    {
        InitializeComponent();
        OpenTaskManagerAction = () => Report(ShellShortcuts.OpenTaskManager());
        OpenResourceMonitorAction = () => Report(ShellShortcuts.OpenResourceMonitor());
        _flyout.Closed += Flyout_Closed;
    }

    protected override void OnCompactChanged(bool compact) => SyncMeterVisibility();
    protected override void OnHoverExpandedChanged(bool expanded) => SyncMeterVisibility();

    public override UIElement? BuildHoverFlyoutContent()
    {
        if (_flyout.IsOpen) return null;
        var view = CreateHoverFlyoutView();
        _hoverFlyout = view;
        view.Container.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_hoverFlyout, view)) _hoverFlyout = null;
        };
        RefreshHoverFlyout(view);
        return view.Container;
    }

    public override UIElement? BuildPreviewFlyoutContent() => BuildTelemetryFlyout(isPreview: true);

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
        _flyout.Close(restoreFocus: false);
        CancelProcessSampling();
        _clickFlyout = null;
        _hoverFlyout = null;

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
        _lastProcessSampleTickMs = 0;
        UpdateLeadingIconAccent();
    }

    protected override void OnDisposed()
    {
        _flyout.Closed -= Flyout_Closed;
        _flyout.Close(restoreFocus: false);
        CancelProcessSampling();
        _clickFlyout = null;
        _hoverFlyout = null;

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

    protected override void OnLanguageChanged()
    {
        UpdateTooltip();
        if (_flyout.IsOpen)
        {
            _flyout.Close(restoreFocus: false);
        }
        if (_hoverFlyout is not null && IsHoverFlyoutOpen)
        {
            RefreshHoverFlyout(_hoverFlyout);
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

        if (_flyout.IsOpen && _clickFlyout is not null)
        {
            RefreshTelemetryFlyout(_clickFlyout);
            if (_lastProcessSampleTickMs == 0 || tickMs - _lastProcessSampleTickMs >= ProcessSamplePeriodMs)
            {
                _lastProcessSampleTickMs = tickMs;
                if (_processSampleCts is { IsCancellationRequested: false } cts)
                {
                    QueueProcessSample(cts.Token);
                }
            }
        }
        else if (IsHoverFlyoutOpen && _hoverFlyout is not null)
        {
            RefreshHoverFlyout(_hoverFlyout);
        }
    }

    private void StatsCardButton_Click(object sender, RoutedEventArgs e)
    {
        _flyout.Toggle(StatsCardButton, () => BuildTelemetryFlyout(isPreview: false), animate: true, focusable: false);
        UpdateLeadingIconAccent();
    }

    private void Flyout_Closed(object? sender, EventArgs e)
    {
        CancelProcessSampling();
        _clickFlyout = null;
        _lastProcessSampleTickMs = 0;
        UpdateLeadingIconAccent();
    }

    private void SampleFastCounters(bool sampleCpu)
    {
        if (sampleCpu && _stats.SampleCpu() is { } cpu)
        {
            _cpuHistory.Push(cpu);
            SetUsage(CpuValueText, CpuBarFill, CpuBarFillCol, CpuBarEmptyCol, ref _cpuState, cpu, "WidgetBlue");
        }

        if (SystemStats.SampleMemorySnapshot() is { } memorySnapshot)
        {
            _lastMemorySnapshot = memorySnapshot;
            _memoryHistory.Push(memorySnapshot.UsageRatio);
            SetUsage(RamValueText, RamBarFill, RamBarFillCol, RamBarEmptyCol, ref _memoryState, memorySnapshot.UsageRatio, "WidgetAccent");
        }

        UpdateLeadingIconAccent();
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
            usage =>
            {
                _gpuHistory.Push(usage);
                SetUsage(GpuValueText, GpuBarFill, GpuBarFillCol, GpuBarEmptyCol, ref _gpuState, usage, "AccentBrush");
            });
        UpdateSensor(GpuTemperatureButton, currentMeters.HasFlag(SystemMeters.GpuTemperature) ? gpuTemp : null,
            celsius => SetTemperature(GpuTempValueText, GpuTempBarFill, GpuTempBarFillCol, GpuTempBarEmptyCol, ref _gpuTempState, celsius));

        UpdateLeadingIconAccent();
        UpdateTooltip();

        if (_flyout.IsOpen && _clickFlyout is not null)
            RefreshTelemetryFlyout(_clickFlyout);
        else if (IsHoverFlyoutOpen && _hoverFlyout is not null)
            RefreshHoverFlyout(_hoverFlyout);
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
            if (!meters.HasFlag(SystemMeters.Gpu)) _gpuHistory.Clear();
        }
        SyncMeterVisibility();
    }

    private void SyncMeterVisibility()
    {
        var compact = IsCompact && !IsHoverExpanded;
        var vertical = Settings.Edge != DockEdge.Bottom;
        var meters = Settings.StatsMeters;
        var changed = false;

        if (_lastAppliedCompact != compact)
        {
            _lastAppliedCompact = compact;
            changed = true;
        }

        if (_lastAppliedVertical != vertical)
        {
            _lastAppliedVertical = vertical;
            changed = true;
        }

        var showCpu = meters.HasFlag(SystemMeters.Cpu);
        var showRam = meters.HasFlag(SystemMeters.Memory);
        var showGpu = !compact && meters.HasFlag(SystemMeters.Gpu) && _hasGpu;
        var showGpuTemp = !compact && meters.HasFlag(SystemMeters.GpuTemperature) && _hasGpuTemp;

        MetricsPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        StatsCardButton.Padding = vertical
            ? (compact ? new Thickness(6, 5, 6, 5) : new Thickness(8, 6, 8, 6))
            : (compact ? new Thickness(7, 0, 7, 0) : new Thickness(10, 0, 10, 0));
        StatsIcon.Margin = vertical
            ? new Thickness(0, 0, 0, compact ? 4 : 6)
            : new Thickness(0, 0, compact ? 5 : 8, 0);

        var dividerMargin = vertical
            ? new Thickness(0, compact ? 4 : 5, 0, compact ? 4 : 5)
            : new Thickness(compact ? 5 : 8, 0, compact ? 5 : 8, 0);
        foreach (var divider in new[] { RamDivider, GpuDivider, GpuTempDivider })
        {
            divider.Width = vertical ? 22 : 1;
            divider.Height = vertical ? 1 : (compact ? 16 : 20);
            divider.Margin = dividerMargin;
        }

        var metricMinWidth = compact ? 28.0 : 36.0;
        CpuButton.MinWidth = metricMinWidth;
        MemoryButton.MinWidth = metricMinWidth;
        GpuButton.MinWidth = metricMinWidth;
        GpuTemperatureButton.MinWidth = compact ? 26.0 : 32.0;

        Set(CpuLabel, !compact);
        Set(RamLabel, !compact);
        Set(GpuLabel, !compact);
        Set(GpuTempLabel, !compact);

        Set(CpuButton, showCpu);
        Set(RamDivider, showCpu && showRam);
        Set(MemoryButton, showRam);
        Set(GpuDivider, (showCpu || showRam) && showGpu);
        Set(GpuButton, showGpu);
        Set(GpuTempDivider, (showCpu || showRam || showGpu) && showGpuTemp);
        Set(GpuTemperatureButton, showGpuTemp);

        UpdateLeadingIconAccent();
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

    private void SetUsage(
        TextBlock target,
        Border fillBar,
        ColumnDefinition fillCol,
        ColumnDefinition emptyCol,
        ref MetricState state,
        double value,
        string normalAccentKey)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var percent = (int)Math.Round(clamped * 100);
        var lang = Settings.Language;
        var accentKey = LoadBrushKey(clamped, normalAccentKey);
        var textKey = LoadTextBrushKey(clamped);

        if (state.Initialized
            && state.Shown == percent
            && state.Language == lang
            && state.AccentKey == accentKey
            && state.TextBrushKey == textKey
            && Math.Abs(clamped - state.Value) < ValueEpsilon)
            return;

        if (state.AccentKey != accentKey)
            fillBar.SetResourceReference(Border.BackgroundProperty, accentKey);
        if (state.TextBrushKey != textKey)
            target.SetResourceReference(TextBlock.ForegroundProperty, textKey);

        state.Value = clamped;
        state.Shown = percent;
        state.Language = lang;
        state.AccentKey = accentKey;
        state.TextBrushKey = textKey;
        state.Initialized = true;

        target.Text = $"{percent.ToString(CultureInfo.InvariantCulture)}%";
        SetProgressColumns(fillCol, emptyCol, clamped);
    }

    private void SetTemperature(
        TextBlock target,
        Border fillBar,
        ColumnDefinition fillCol,
        ColumnDefinition emptyCol,
        ref MetricState state,
        double celsius)
    {
        var rounded = (int)Math.Round(celsius);
        var normalized = celsius / TemperatureScaleCelsius;
        var barRatio = NormalizeTemperatureBar(celsius);
        var lang = Settings.Language;
        var accentKey = ThermalBrushKey(celsius);
        var textKey = ThermalTextBrushKey(celsius);

        if (state.Initialized
            && state.Shown == rounded
            && state.Language == lang
            && state.AccentKey == accentKey
            && state.TextBrushKey == textKey
            && Math.Abs(normalized - state.Value) < ValueEpsilon)
            return;

        if (state.AccentKey != accentKey)
            fillBar.SetResourceReference(Border.BackgroundProperty, accentKey);
        if (state.TextBrushKey != textKey)
            target.SetResourceReference(TextBlock.ForegroundProperty, textKey);

        state.Value = normalized;
        state.Shown = rounded;
        state.Language = lang;
        state.AccentKey = accentKey;
        state.TextBrushKey = textKey;
        state.Initialized = true;

        target.Text = $"{rounded.ToString(CultureInfo.InvariantCulture)}°";
        SetProgressColumns(fillCol, emptyCol, barRatio);
    }

    private void UpdateLeadingIconAccent()
    {
        string targetKey;
        if (_flyout.IsOpen)
        {
            targetKey = "WidgetSelectedForeground";
        }
        else
        {
            var meters = Settings.StatsMeters;
            var isCritical = (meters.HasFlag(SystemMeters.Cpu) && _cpuState.Initialized && _cpuState.Value >= CriticalThreshold)
                || (meters.HasFlag(SystemMeters.Memory) && _memoryState.Initialized && _memoryState.Value >= CriticalThreshold)
                || (meters.HasFlag(SystemMeters.Gpu) && _hasGpu && _gpuState.Initialized && _gpuState.Value >= CriticalThreshold)
                || (meters.HasFlag(SystemMeters.GpuTemperature) && _hasGpuTemp && _gpuTempState.Initialized && _gpuTempState.Shown >= ThermalCriticalCelsius);

            var isWarning = !isCritical && (
                (meters.HasFlag(SystemMeters.Cpu) && _cpuState.Initialized && _cpuState.Value >= WarningThreshold)
                || (meters.HasFlag(SystemMeters.Memory) && _memoryState.Initialized && _memoryState.Value >= WarningThreshold)
                || (meters.HasFlag(SystemMeters.Gpu) && _hasGpu && _gpuState.Initialized && _gpuState.Value >= WarningThreshold)
                || (meters.HasFlag(SystemMeters.GpuTemperature) && _hasGpuTemp && _gpuTempState.Initialized && _gpuTempState.Shown >= ThermalWarningCelsius));

            targetKey = isCritical ? "WidgetDangerForeground" : (isWarning ? "WidgetWarning" : "TextSecondary");
        }

        if (_lastStatsIconKey == targetKey) return;
        _lastStatsIconKey = targetKey;
        StatsIcon.SetResourceReference(ForegroundProperty, targetKey);
    }

    private void UpdateTooltip()
    {
        var parts = new List<string>(4);
        if (_cpuState.Initialized) parts.Add($"CPU {_cpuState.Shown}%");
        if (_memoryState.Initialized) parts.Add($"RAM {_memoryState.Shown}%");
        if (_hasGpu && _gpuState.Initialized) parts.Add($"GPU {_gpuState.Shown}%");
        if (_hasGpuTemp && _gpuTempState.Initialized) parts.Add($"GPU {_gpuTempState.Shown}°C");
        var summary = parts.Count > 0 ? string.Join(" · ", parts) : UiText.Get("WidgetSystemStats");
        StatsCardButton.ToolTip = summary;
        AutomationProperties.SetName(StatsCardButton, $"{UiText.Get("WidgetSystemStats")}: {summary}");
    }

    /// <summary>
    /// Builds the interactive telemetry flyout with rolling sparklines, CPU/RAM/GPU breakdown cards,
    /// top processes (lazily sampled off the UI thread while open), and system tool quick actions.
    /// </summary>
    internal UIElement BuildTelemetryFlyout(bool isPreview = false)
    {
        var root = new StackPanel();
        var container = new Border { Width = 332, Child = root };

        // 1. Header bar: icon + title on left, live system uptime pill on right.
        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };

        var uptimeText = new TextBlock
        {
            Text = FormatUptime(SystemStats.GetSystemUptime()),
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        uptimeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var uptimePill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiText.Get("StatsUptime")
        };
        uptimePill.SetResourceReference(Border.BackgroundProperty, "SurfaceSelected");
        uptimePill.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");
        var uptimeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var uptimeIcon = CreateIcon("IconClock", 11, "TextSecondary");
        uptimeIcon.Margin = new Thickness(0, 0, 5, 0);
        uptimeRow.Children.Add(uptimeIcon);
        uptimeRow.Children.Add(uptimeText);
        uptimePill.Child = uptimeRow;
        DockPanel.SetDock(uptimePill, Dock.Right);
        header.Children.Add(uptimePill);

        var titleGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var headerIcon = CreateIcon("IconCpu", 15, "WidgetSelectedForeground");
        headerIcon.Margin = new Thickness(0, 0, 7, 0);
        var titleBlock = new TextBlock
        {
            Text = UiText.Get("WidgetSystemStats"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        titleGroup.Children.Add(headerIcon);
        titleGroup.Children.Add(titleBlock);
        header.Children.Add(titleGroup);
        root.Children.Add(header);

        // 2. CPU breakdown card + rolling sparkline
        var cpuDot = CreateStatusDot("WidgetBlue");
        var cpuValueText = CreateCardValueText();
        var cpuSparkline = CreateSparkline("WidgetBlue", 34);
        var cpuCard = CreateMetricCard();
        var cpuStack = new StackPanel();
        var cpuTop = new DockPanel();
        DockPanel.SetDock(cpuValueText, Dock.Right);
        cpuTop.Children.Add(cpuValueText);

        var cpuTitleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        cpuTitleRow.Children.Add(cpuDot);
        var cpuTitle = new TextBlock
        {
            Text = UiText.Get("Cpu"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        cpuTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var cpuCores = new TextBlock
        {
            Text = $"· {UiText.Format("StatsLogicalCores", Environment.ProcessorCount)}",
            FontSize = 10.5,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        cpuCores.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        cpuTitleRow.Children.Add(cpuTitle);
        cpuTitleRow.Children.Add(cpuCores);
        cpuTop.Children.Add(cpuTitleRow);

        cpuStack.Children.Add(cpuTop);
        cpuStack.Children.Add(cpuSparkline);
        cpuCard.Child = cpuStack;
        root.Children.Add(cpuCard);

        // 3. RAM breakdown card + percentage bar + used/available GB + rolling sparkline
        var ramDot = CreateStatusDot("WidgetAccent");
        var ramValueText = CreateCardValueText();
        var ramPairText = new TextBlock
        {
            Text = "—",
            FontSize = 10.5,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        ramPairText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var (ramTrack, ramBarFill, ramBarFillCol, ramBarEmptyCol) = CreateProgressBar("WidgetAccent", height: 5, new Thickness(0, 6, 0, 5));

        var ramUsedText = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        ramUsedText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var ramAvailText = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        ramAvailText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var ramSubRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(ramAvailText, Dock.Right);
        ramSubRow.Children.Add(ramAvailText);
        ramSubRow.Children.Add(ramUsedText);

        var ramSparkline = CreateSparkline("WidgetAccent", 28);
        var ramCard = CreateMetricCard();
        var ramStack = new StackPanel();
        var ramTop = new DockPanel();
        DockPanel.SetDock(ramValueText, Dock.Right);
        ramTop.Children.Add(ramValueText);

        var ramTitleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ramTitleRow.Children.Add(ramDot);
        var ramTitle = new TextBlock
        {
            Text = UiText.Get("Memory"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        ramTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        ramTitleRow.Children.Add(ramTitle);
        ramTitleRow.Children.Add(ramPairText);
        ramTop.Children.Add(ramTitleRow);

        ramStack.Children.Add(ramTop);
        ramStack.Children.Add(ramTrack);
        ramStack.Children.Add(ramSubRow);
        ramStack.Children.Add(ramSparkline);
        ramCard.Child = ramStack;
        root.Children.Add(ramCard);

        // 4. GPU card (usage % + temperature pill + rolling sparkline, shown when GPU sensors report data)
        var gpuDot = CreateStatusDot("AccentBrush");
        var gpuValueText = CreateCardValueText();
        var gpuTempText = new TextBlock
        {
            Text = "—",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        gpuTempText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        var gpuTempBadge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = gpuTempText,
            ToolTip = UiText.Get("GpuTemperature")
        };
        gpuTempBadge.SetResourceReference(Border.BackgroundProperty, "SurfaceSelectedStrong");
        gpuTempBadge.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");

        var gpuSparkline = CreateSparkline("AccentBrush", 28);
        var gpuCard = CreateMetricCard();
        var gpuStack = new StackPanel();
        var gpuTop = new DockPanel();

        var gpuRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        gpuRight.Children.Add(gpuTempBadge);
        gpuRight.Children.Add(gpuValueText);
        DockPanel.SetDock(gpuRight, Dock.Right);
        gpuTop.Children.Add(gpuRight);

        var gpuTitleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        gpuTitleRow.Children.Add(gpuDot);
        var gpuTitle = new TextBlock
        {
            Text = UiText.Get("Gpu"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        gpuTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        gpuTitleRow.Children.Add(gpuTitle);
        gpuTop.Children.Add(gpuTitleRow);

        gpuStack.Children.Add(gpuTop);
        gpuStack.Children.Add(gpuSparkline);
        gpuCard.Child = gpuStack;
        root.Children.Add(gpuCard);

        // 5. Top Processes card (sampled asynchronously off UI thread only while flyout is open)
        var procHeader = new DockPanel { Margin = new Thickness(2, 2, 2, 5) };
        var procMemHeading = new TextBlock
        {
            Text = UiText.Get("Memory"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        procMemHeading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        DockPanel.SetDock(procMemHeading, Dock.Right);
        procHeader.Children.Add(procMemHeading);

        var procTitle = new TextBlock
        {
            Text = UiText.Get("StatsTopProcesses"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        procTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        procHeader.Children.Add(procTitle);
        root.Children.Add(procHeader);

        var processesList = new StackPanel();
        var processesCard = CreateMetricCard(bottomMargin: 10);
        processesCard.Padding = new Thickness(9, 7, 9, 7);
        processesCard.Child = processesList;
        root.Children.Add(processesCard);

        // 6. Quick Action Footer: Task Manager + Resource Monitor
        var footer = new UniformGrid { Columns = 2 };
        var taskManagerBtn = CreateFooterActionButton("IconTaskManager", UiText.Get("TaskManager"), primary: true, new Thickness(0, 0, 4, 0));
        taskManagerBtn.Click += (_, _) =>
        {
            _flyout.Close(restoreFocus: false);
            OpenTaskManagerAction();
        };

        var resourceMonitorBtn = CreateFooterActionButton("IconMonitor", UiText.Get("ResourceMonitor"), primary: false, new Thickness(4, 0, 0, 0));
        resourceMonitorBtn.Click += (_, _) =>
        {
            _flyout.Close(restoreFocus: false);
            OpenResourceMonitorAction();
        };

        footer.Children.Add(taskManagerBtn);
        footer.Children.Add(resourceMonitorBtn);
        root.Children.Add(footer);

        var view = new TelemetryFlyoutView
        {
            UptimeText = uptimeText,
            CpuValueText = cpuValueText,
            CpuDot = cpuDot,
            CpuSparkline = cpuSparkline,
            RamValueText = ramValueText,
            RamPairText = ramPairText,
            RamUsedText = ramUsedText,
            RamAvailableText = ramAvailText,
            RamDot = ramDot,
            RamBarFill = ramBarFill,
            RamBarFillCol = ramBarFillCol,
            RamBarEmptyCol = ramBarEmptyCol,
            RamSparkline = ramSparkline,
            GpuCard = gpuCard,
            GpuValueText = gpuValueText,
            GpuTempBadge = gpuTempBadge,
            GpuTempText = gpuTempText,
            GpuDot = gpuDot,
            GpuSparkline = gpuSparkline,
            ProcessesList = processesList
        };

        RefreshTelemetryFlyout(view);

        if (isPreview)
        {
            RenderTopProcesses(processesList, _lastTopProcesses, isLoading: _lastTopProcesses.Count == 0);
            AttachPreviewOneShotProcessFetch(container, processesList);
        }
        else
        {
            _clickFlyout = view;
            RenderTopProcesses(processesList, _lastTopProcesses, isLoading: _lastTopProcesses.Count == 0);
            StartFlyoutProcessSampling();
        }

        return container;
    }

    private void AttachPreviewOneShotProcessFetch(Border container, StackPanel processesList)
    {
        CancellationTokenSource? previewCts = null;
        container.Loaded += (_, _) =>
        {
            if (_lastTopProcesses.Count > 0)
            {
                RenderTopProcesses(processesList, _lastTopProcesses, isLoading: false);
                return;
            }

            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = new CancellationTokenSource();
            var ct = previewCts.Token;
            var reader = TopProcessesReader;
            _ = Task.Run(() =>
            {
                try { return reader(TopProcessCount, ct); }
                catch (OperationCanceledException) { return Array.Empty<ProcessMemoryEntry>(); }
            }, ct).ContinueWith(task =>
            {
                if (ct.IsCancellationRequested || !task.IsCompletedSuccessfully) return;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (ct.IsCancellationRequested || !container.IsLoaded) return;
                    _lastTopProcesses = task.Result;
                    RenderTopProcesses(processesList, task.Result, isLoading: false);
                });
            }, TaskScheduler.Default);
        };

        container.Unloaded += (_, _) =>
        {
            if (previewCts is null) return;
            previewCts.Cancel();
            previewCts.Dispose();
            previewCts = null;
        };
    }

    private void StartFlyoutProcessSampling()
    {
        CancelProcessSampling();
        _processSampleCts = new CancellationTokenSource();
        _lastProcessSampleTickMs = Environment.TickCount64;
        QueueProcessSample(_processSampleCts.Token);
    }

    private void CancelProcessSampling()
    {
        Interlocked.Increment(ref _processGeneration);
        if (_processSampleCts is null) return;
        try { _processSampleCts.Cancel(); } catch { }
        _processSampleCts.Dispose();
        _processSampleCts = null;
    }

    private void QueueProcessSample(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref _processSampling, 1, 0) != 0) return;

        var generation = Volatile.Read(ref _processGeneration);
        var reader = TopProcessesReader;

        // Always run the delegate (do not pass ct to Task.Run) so the finally block is guaranteed to clear _processSampling.
        _ = Task.Run(() =>
        {
            IReadOnlyList<ProcessMemoryEntry> entries = [];
            try
            {
                if (ct.IsCancellationRequested || generation != Volatile.Read(ref _processGeneration))
                    return;
                entries = reader(TopProcessCount, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Top processes sampling failed: {ex.Message}");
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _processSampling, 0);
            }

            if (ct.IsCancellationRequested || generation != Volatile.Read(ref _processGeneration))
                return;

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!IsActive || !_flyout.IsOpen || _clickFlyout is null
                    || generation != Volatile.Read(ref _processGeneration))
                    return;

                _lastTopProcesses = entries;
                RenderTopProcesses(_clickFlyout.ProcessesList, entries, isLoading: false);
            });
        });
    }

    private void RefreshTelemetryFlyout(TelemetryFlyoutView view)
    {
        var culture = Settings.Language.Culture;
        view.UptimeText.Text = FormatUptime(SystemStats.GetSystemUptime());

        // CPU
        var cpuRatio = _cpuState.Initialized ? _cpuState.Value : 0;
        var cpuAccent = LoadBrushKey(cpuRatio, "WidgetBlue");
        var cpuTextKey = _cpuState.Initialized ? LoadTextBrushKey(cpuRatio) : "TextPrimary";
        view.CpuValueText.Text = _cpuState.Initialized ? $"{_cpuState.Shown.ToString(CultureInfo.InvariantCulture)}%" : "—";
        view.CpuValueText.SetResourceReference(TextBlock.ForegroundProperty, cpuTextKey);
        view.CpuDot.SetResourceReference(Shape.FillProperty, cpuAccent);
        view.CpuSparkline.SetResourceReference(TelemetrySparkline.StrokeBrushProperty, cpuAccent);
        view.CpuSparkline.UpdateFrom(_cpuHistory);

        // RAM
        var mem = _lastMemorySnapshot ?? SystemStats.SampleMemorySnapshot();
        var ramRatio = mem?.UsageRatio ?? (_memoryState.Initialized ? _memoryState.Value : 0);
        var ramShown = mem.HasValue ? (int)Math.Round(ramRatio * 100) : _memoryState.Shown;
        var ramHasValue = mem.HasValue || _memoryState.Initialized;
        var ramAccent = LoadBrushKey(ramRatio, "WidgetAccent");
        var ramTextKey = ramHasValue ? LoadTextBrushKey(ramRatio) : "TextPrimary";
        view.RamValueText.Text = ramHasValue ? $"{ramShown.ToString(CultureInfo.InvariantCulture)}%" : "—";
        view.RamValueText.SetResourceReference(TextBlock.ForegroundProperty, ramTextKey);
        view.RamDot.SetResourceReference(Shape.FillProperty, ramAccent);
        view.RamBarFill.SetResourceReference(Border.BackgroundProperty, ramAccent);
        SetProgressColumns(view.RamBarFillCol, view.RamBarEmptyCol, ramHasValue ? ramRatio : 0);

        if (mem is { TotalBytes: > 0 } snap)
        {
            var usedStr = FormatBytesGigabytes(snap.UsedBytes, culture);
            var totalStr = FormatBytesGigabytes(snap.TotalBytes, culture);
            var availStr = FormatBytesGigabytes(snap.AvailableBytes, culture);
            view.RamPairText.Text = $"· {usedStr} / {totalStr}";
            view.RamUsedText.Text = UiText.Format("StatsMemoryUsed", usedStr);
            view.RamAvailableText.Text = UiText.Format("StatsMemoryAvailable", availStr);
        }
        else
        {
            view.RamPairText.Text = string.Empty;
            view.RamUsedText.Text = "—";
            view.RamAvailableText.Text = string.Empty;
        }

        view.RamSparkline.SetResourceReference(TelemetrySparkline.StrokeBrushProperty, ramAccent);
        view.RamSparkline.UpdateFrom(_memoryHistory);

        // GPU
        var showGpuCard = _hasGpu || _hasGpuTemp;
        view.GpuCard.Visibility = showGpuCard ? Visibility.Visible : Visibility.Collapsed;
        if (showGpuCard)
        {
            var gpuRatio = _gpuState.Initialized ? _gpuState.Value : 0;
            var gpuAccent = _hasGpu
                ? LoadBrushKey(gpuRatio, "AccentBrush")
                : (_hasGpuTemp && _gpuTempState.Initialized ? ThermalBrushKey(_gpuTempState.Shown) : "AccentBrush");
            view.GpuDot.SetResourceReference(Shape.FillProperty, gpuAccent);

            view.GpuValueText.Visibility = _hasGpu ? Visibility.Visible : Visibility.Collapsed;
            if (_hasGpu)
            {
                var gpuTextKey = _gpuState.Initialized ? LoadTextBrushKey(gpuRatio) : "TextPrimary";
                view.GpuValueText.Text = _gpuState.Initialized ? $"{_gpuState.Shown.ToString(CultureInfo.InvariantCulture)}%" : "—";
                view.GpuValueText.SetResourceReference(TextBlock.ForegroundProperty, gpuTextKey);
            }

            view.GpuTempBadge.Visibility = _hasGpuTemp ? Visibility.Visible : Visibility.Collapsed;
            view.GpuTempBadge.Margin = _hasGpu ? new Thickness(0, 0, 6, 0) : new Thickness(0);
            if (_hasGpuTemp)
            {
                var tempKey = _gpuTempState.Initialized ? ThermalTextBrushKey(_gpuTempState.Shown) : "TextPrimary";
                view.GpuTempText.Text = _gpuTempState.Initialized
                    ? $"{_gpuTempState.Shown.ToString(CultureInfo.InvariantCulture)}°C"
                    : "—";
                view.GpuTempText.SetResourceReference(TextBlock.ForegroundProperty, tempKey);
            }

            view.GpuSparkline.Visibility = _hasGpu ? Visibility.Visible : Visibility.Collapsed;
            if (_hasGpu)
            {
                view.GpuSparkline.SetResourceReference(TelemetrySparkline.StrokeBrushProperty, gpuAccent);
                view.GpuSparkline.UpdateFrom(_gpuHistory);
            }
        }
    }

    private void RenderTopProcesses(StackPanel container, IReadOnlyList<ProcessMemoryEntry> entries, bool isLoading)
    {
        container.Children.Clear();
        if (isLoading)
        {
            var loading = new TextBlock
            {
                Text = UiText.Get("StatsSamplingProcesses"),
                FontSize = 11,
                Margin = new Thickness(2, 4, 2, 4)
            };
            loading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            container.Children.Add(loading);
            return;
        }

        if (entries.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = UiText.Get("StatsTopProcessesEmpty"),
                FontSize = 11,
                Margin = new Thickness(2, 4, 2, 4)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            container.Children.Add(empty);
            return;
        }

        var culture = Settings.Language.Culture;
        var maxWorkingSet = Math.Max(1L, entries[0].WorkingSetBytes);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var rowStack = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };

            var textRow = new DockPanel();
            var memText = new TextBlock
            {
                Text = FormatProcessMemory(entry.WorkingSetBytes, culture),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
            };
            memText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            DockPanel.SetDock(memText, Dock.Right);
            textRow.Children.Add(memText);

            var namePanel = new DockPanel { LastChildFill = true };
            if (entry.InstanceCount > 1)
            {
                var countText = new TextBlock
                {
                    Text = $"×{entry.InstanceCount.ToString(CultureInfo.InvariantCulture)}",
                    FontSize = 9.5,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
                };
                countText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                DockPanel.SetDock(countText, Dock.Right);
                namePanel.Children.Add(countText);
            }

            var nameText = new TextBlock
            {
                Text = entry.Name,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            namePanel.Children.Add(nameText);
            textRow.Children.Add(namePanel);
            rowStack.Children.Add(textRow);

            var ratio = Math.Clamp((double)entry.WorkingSetBytes / maxWorkingSet, 0.04, 1.0);
            var (barTrack, barFill, fillCol, emptyCol) = CreateProgressBar("WidgetAccent", height: 2.2, new Thickness(0, 3, 0, 0));
            barFill.Opacity = 0.78;
            SetProgressColumns(fillCol, emptyCol, ratio);
            rowStack.Children.Add(barTrack);

            container.Children.Add(rowStack);
        }
    }

    private HoverFlyoutView CreateHoverFlyoutView()
    {
        var root = new StackPanel();
        var container = new Border { Width = 248, Child = root };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var uptimeText = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        uptimeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        DockPanel.SetDock(uptimeText, Dock.Right);
        header.Children.Add(uptimeText);

        var title = new TextBlock
        {
            Text = UiText.Get("WidgetSystemStats"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        header.Children.Add(title);
        root.Children.Add(header);

        var (cpuRow, cpuVal, cpuFill, cpuFillCol, cpuEmptyCol) = CreateCompactMetricRow(UiText.Get("Cpu"), "WidgetBlue");
        var (ramRow, ramVal, ramFill, ramFillCol, ramEmptyCol) = CreateCompactMetricRow(UiText.Get("Memory"), "WidgetAccent");
        var ramDetail = new TextBlock
        {
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        ramDetail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        ramRow.Children.Add(ramDetail);

        var (gpuRow, gpuVal, gpuFill, gpuFillCol, gpuEmptyCol) = CreateCompactMetricRow(UiText.Get("Gpu"), "AccentBrush");

        root.Children.Add(cpuRow);
        ramRow.Margin = new Thickness(0, 7, 0, 0);
        root.Children.Add(ramRow);
        gpuRow.Margin = new Thickness(0, 7, 0, 0);
        root.Children.Add(gpuRow);

        return new HoverFlyoutView
        {
            Container = container,
            UptimeText = uptimeText,
            CpuValueText = cpuVal,
            CpuBarFill = cpuFill,
            CpuBarFillCol = cpuFillCol,
            CpuBarEmptyCol = cpuEmptyCol,
            RamValueText = ramVal,
            RamDetailText = ramDetail,
            RamBarFill = ramFill,
            RamBarFillCol = ramFillCol,
            RamBarEmptyCol = ramEmptyCol,
            GpuRow = gpuRow,
            GpuValueText = gpuVal,
            GpuBarFill = gpuFill,
            GpuBarFillCol = gpuFillCol,
            GpuBarEmptyCol = gpuEmptyCol
        };
    }

    private void RefreshHoverFlyout(HoverFlyoutView view)
    {
        var culture = Settings.Language.Culture;
        view.UptimeText.Text = FormatUptime(SystemStats.GetSystemUptime());

        var cpuRatio = _cpuState.Initialized ? _cpuState.Value : 0;
        view.CpuValueText.Text = _cpuState.Initialized ? $"{_cpuState.Shown.ToString(CultureInfo.InvariantCulture)}%" : "—";
        view.CpuBarFill.SetResourceReference(Border.BackgroundProperty, LoadBrushKey(cpuRatio, "WidgetBlue"));
        SetProgressColumns(view.CpuBarFillCol, view.CpuBarEmptyCol, _cpuState.Initialized ? cpuRatio : 0);

        var mem = _lastMemorySnapshot ?? SystemStats.SampleMemorySnapshot();
        var ramRatio = mem?.UsageRatio ?? (_memoryState.Initialized ? _memoryState.Value : 0);
        var ramHas = mem.HasValue || _memoryState.Initialized;
        view.RamValueText.Text = ramHas ? $"{((int)Math.Round(ramRatio * 100)).ToString(CultureInfo.InvariantCulture)}%" : "—";
        view.RamBarFill.SetResourceReference(Border.BackgroundProperty, LoadBrushKey(ramRatio, "WidgetAccent"));
        SetProgressColumns(view.RamBarFillCol, view.RamBarEmptyCol, ramHas ? ramRatio : 0);
        view.RamDetailText.Text = mem is { TotalBytes: > 0 } snap
            ? $"{FormatBytesGigabytes(snap.UsedBytes, culture)} / {FormatBytesGigabytes(snap.TotalBytes, culture)}"
            : string.Empty;

        var showGpu = _hasGpu || _hasGpuTemp;
        view.GpuRow.Visibility = showGpu ? Visibility.Visible : Visibility.Collapsed;
        if (showGpu)
        {
            var gpuRatio = _gpuState.Initialized ? _gpuState.Value : 0;
            var parts = new List<string>(2);
            if (_hasGpu && _gpuState.Initialized) parts.Add($"{_gpuState.Shown.ToString(CultureInfo.InvariantCulture)}%");
            if (_hasGpuTemp && _gpuTempState.Initialized) parts.Add($"{_gpuTempState.Shown.ToString(CultureInfo.InvariantCulture)}°C");
            view.GpuValueText.Text = parts.Count > 0 ? string.Join(" · ", parts) : "—";
            view.GpuBarFill.SetResourceReference(Border.BackgroundProperty,
                _hasGpu
                    ? LoadBrushKey(gpuRatio, "AccentBrush")
                    : (_gpuTempState.Initialized ? ThermalBrushKey(_gpuTempState.Shown) : "WidgetGreen"));
            SetProgressColumns(view.GpuBarFillCol, view.GpuBarEmptyCol,
                _hasGpu
                    ? (_gpuState.Initialized ? gpuRatio : 0)
                    : (_hasGpuTemp && _gpuTempState.Initialized ? NormalizeTemperatureBar(_gpuTempState.Shown) : 0));
        }
    }

    private (StackPanel Row, TextBlock ValueText, Border FillBar, ColumnDefinition FillCol, ColumnDefinition EmptyCol)
        CreateCompactMetricRow(string label, string accentKey)
    {
        var row = new StackPanel();
        var header = new DockPanel();
        var valueText = new TextBlock
        {
            Text = "—",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        DockPanel.SetDock(valueText, Dock.Right);
        header.Children.Add(valueText);

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        header.Children.Add(labelText);

        var (track, fill, fillCol, emptyCol) = CreateProgressBar(accentKey, height: 4, new Thickness(0, 4, 0, 0));
        row.Children.Add(header);
        row.Children.Add(track);
        return (row, valueText, fill, fillCol, emptyCol);
    }

    private Border CreateMetricCard(double bottomMargin = 8)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, bottomMargin),
            BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceSelected");
        card.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");
        return card;
    }

    private Button CreateFooterActionButton(string iconKey, string label, bool primary, Thickness margin)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = CreateIcon(iconKey, 13, primary ? "WidgetSelectedForeground" : "TextSecondary");
        icon.Margin = new Thickness(0, 0, 6, 0);
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        content.Children.Add(icon);
        content.Children.Add(text);

        var button = new Button
        {
            Content = content,
            Margin = margin,
            Padding = new Thickness(8, 0, 8, 0),
            ToolTip = label
        };
        button.SetResourceReference(StyleProperty, primary ? "WidgetFlyoutPrimaryButton" : "WidgetFlyoutActionButton");
        AutomationProperties.SetName(button, label);
        return button;
    }

    private ContentControl CreateIcon(string resourceKey, double size, string foregroundKey)
    {
        var icon = new ContentControl
        {
            Content = TryFindResource(resourceKey),
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(StyleProperty, "WidgetIcon");
        icon.SetResourceReference(ForegroundProperty, foregroundKey);
        return icon;
    }

    private static Ellipse CreateStatusDot(string brushKey)
    {
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        dot.SetResourceReference(Shape.FillProperty, brushKey);
        return dot;
    }

    private static TextBlock CreateCardValueText()
    {
        var text = new TextBlock
        {
            Text = "—",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return text;
    }

    private static TelemetrySparkline CreateSparkline(string brushKey, double height)
    {
        var sparkline = new TelemetrySparkline
        {
            Height = height,
            Margin = new Thickness(0, 6, 0, 0)
        };
        sparkline.SetResourceReference(TelemetrySparkline.StrokeBrushProperty, brushKey);
        sparkline.SetResourceReference(TelemetrySparkline.GridBrushProperty, "WidgetTrack");
        return sparkline;
    }

    private static (Border Track, Border Fill, ColumnDefinition FillCol, ColumnDefinition EmptyCol)
        CreateProgressBar(string brushKey, double height, Thickness margin)
    {
        var radius = height / 2.0;
        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Margin = margin,
            ClipToBounds = true
        };
        track.SetResourceReference(Border.BackgroundProperty, "WidgetTrack");

        var grid = new Grid();
        var fillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var emptyCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        grid.ColumnDefinitions.Add(fillCol);
        grid.ColumnDefinitions.Add(emptyCol);

        var fill = new Border { CornerRadius = new CornerRadius(radius) };
        fill.SetResourceReference(Border.BackgroundProperty, brushKey);
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;

        return (track, fill, fillCol, emptyCol);
    }

    private static void SetProgressColumns(ColumnDefinition fillCol, ColumnDefinition emptyCol, double ratio)
    {
        var clamped = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        var visual = clamped > 0.005 ? Math.Max(0.04, clamped) : 0;
        fillCol.Width = new GridLength(visual, GridUnitType.Star);
        emptyCol.Width = new GridLength(Math.Max(0, 1.0 - visual), GridUnitType.Star);
    }

    internal static string LoadBrushKey(double ratio, string normalKey) => ratio switch
    {
        >= CriticalThreshold => "WidgetFocusAccent",
        >= WarningThreshold => "WidgetWarning",
        _ => normalKey
    };

    internal static string LoadTextBrushKey(double ratio) => ratio switch
    {
        >= CriticalThreshold => "WidgetDangerForeground",
        >= WarningThreshold => "WidgetWarning",
        _ => "TextPrimary"
    };

    internal static string ThermalBrushKey(double celsius) => celsius switch
    {
        >= ThermalCriticalCelsius => "WidgetFocusAccent",
        >= ThermalWarningCelsius => "WidgetWarning",
        _ => "WidgetGreen"
    };

    internal static string ThermalTextBrushKey(double celsius) => celsius switch
    {
        >= ThermalCriticalCelsius => "WidgetDangerForeground",
        >= ThermalWarningCelsius => "WidgetWarning",
        _ => "TextPrimary"
    };

    internal static double NormalizeTemperatureBar(double celsius) =>
        Math.Clamp((celsius - 25.0) / 70.0, 0.06, 1.0);

    internal static string FormatBytesGigabytes(ulong bytes, CultureInfo culture)
    {
        var gb = bytes / (1024.0 * 1024.0 * 1024.0);
        var formatted = gb >= 100 ? gb.ToString("0", culture) : gb.ToString("0.0", culture);
        return $"{formatted} GB";
    }

    internal static string FormatProcessMemory(long bytes, CultureInfo culture)
    {
        if (bytes <= 0) return "0 MB";
        const double gib = 1024.0 * 1024.0 * 1024.0;
        const double mib = 1024.0 * 1024.0;
        if (bytes >= gib)
            return $"{(bytes / gib).ToString("0.0", culture)} GB";
        var mb = Math.Max(1, (long)Math.Round(bytes / mib));
        return $"{mb.ToString(CultureInfo.InvariantCulture)} MB";
    }

    internal static string FormatUptime(TimeSpan uptime)
    {
        var safe = uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime;
        if (safe.Days > 0)
            return UiText.Format("StatsUptimeDaysFormat", safe.Days, safe.Hours, safe.Minutes);
        if (safe.Hours > 0)
            return UiText.Format("StatsUptimeHoursFormat", safe.Hours, safe.Minutes);
        return UiText.Format("StatsUptimeMinutesFormat", safe.Minutes);
    }
}
