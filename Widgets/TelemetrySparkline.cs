using System.Windows;
using System.Windows.Media;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Fixed-capacity ring buffer storing normalized [0, 1] telemetry samples with zero per-sample allocations.
/// </summary>
internal sealed class MetricRingBuffer
{
    private readonly double[] _buffer;
    private int _head;
    private int _count;

    public MetricRingBuffer(int capacity = 45)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new double[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;
    public double Latest => _count == 0 ? 0 : _buffer[(_head - 1 + _buffer.Length) % _buffer.Length];

    public void Push(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        _buffer[_head] = normalized;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    public int CopyTo(Span<double> destination)
    {
        var toCopy = Math.Min(_count, destination.Length);
        if (toCopy == 0) return 0;

        var start = (_head - _count + _buffer.Length) % _buffer.Length;
        for (var i = 0; i < toCopy; i++)
        {
            destination[i] = _buffer[(start + i) % _buffer.Length];
        }

        return toCopy;
    }
}

/// <summary>
/// Lightweight vector sparkline with area fill and live terminal dot.
/// Instantiated only inside an open telemetry flyout so closed widgets do zero chart rendering work.
/// </summary>
internal sealed class TelemetrySparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(1.25, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _samples = [];
    private int _sampleCount;

    private Pen? _linePen;
    private Brush? _linePenBrush;
    private double _linePenThickness;
    private Pen? _baselinePen;
    private Brush? _gridPenBrush;

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public TelemetrySparkline()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public void UpdateFrom(MetricRingBuffer buffer)
    {
        if (_samples.Length < buffer.Capacity)
            _samples = new double[buffer.Capacity];
        _sampleCount = buffer.CopyTo(_samples);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 6 || height <= 6) return;

        const double padLeft = 1.0;
        const double padRight = 3.5;
        const double padTop = 3.5;
        const double padBottom = 2.0;

        var plotWidth = Math.Max(1.0, width - padLeft - padRight);
        var plotHeight = Math.Max(1.0, height - padTop - padBottom);
        var bottomY = padTop + plotHeight;
        var gridBrush = GridBrush;
        if (_baselinePen is null || !ReferenceEquals(_gridPenBrush, gridBrush))
        {
            _gridPenBrush = gridBrush;
            _baselinePen = new Pen(gridBrush, 1);
        }
        dc.DrawLine(_baselinePen, new Point(padLeft, bottomY), new Point(padLeft + plotWidth, bottomY));

        if (_sampleCount == 0) return;

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            var firstY = bottomY - _samples[0] * plotHeight;
            geometry.BeginFigure(new Point(padLeft, firstY), isFilled: false, isClosed: false);
            if (_sampleCount == 1)
            {
                geometry.LineTo(new Point(padLeft + plotWidth, firstY), isStroked: true, isSmoothJoin: true);
            }
            else
            {
                var step = plotWidth / (_sampleCount - 1);
                for (var i = 1; i < _sampleCount; i++)
                {
                    geometry.LineTo(new Point(padLeft + i * step, bottomY - _samples[i] * plotHeight),
                        isStroked: true, isSmoothJoin: true);
                }
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, GetLinePen(StrokeBrush, StrokeThickness), line);
    }

    private Pen GetLinePen(Brush brush, double thickness)
    {
        if (_linePen is not null && ReferenceEquals(_linePenBrush, brush) && Math.Abs(_linePenThickness - thickness) < 0.001)
            return _linePen;

        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (brush.IsFrozen) pen.Freeze();
        _linePen = pen;
        _linePenBrush = brush;
        _linePenThickness = thickness;
        return pen;
    }
}
