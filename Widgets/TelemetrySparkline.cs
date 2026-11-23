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
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _samples = [];
    private int _sampleCount;

    private Pen? _linePen;
    private Brush? _linePenBrush;
    private double _linePenThickness;
    private Brush? _areaBrush;
    private Brush? _haloBrush;
    private Color? _cachedStrokeColor;
    private Pen? _gridPen;
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
        var midY = padTop + plotHeight * 0.5;

        EnsureGridPens(GridBrush);
        if (_gridPen is not null)
            dc.DrawLine(_gridPen, new Point(padLeft, midY), new Point(padLeft + plotWidth, midY));
        if (_baselinePen is not null)
            dc.DrawLine(_baselinePen, new Point(padLeft, bottomY), new Point(padLeft + plotWidth, bottomY));

        if (_sampleCount <= 0) return;

        var strokeBrush = StrokeBrush;
        var linePen = GetLinePen(strokeBrush, StrokeThickness);
        EnsureDerivedFills(strokeBrush);

        var lineGeometry = new StreamGeometry();
        var areaGeometry = new StreamGeometry();
        Point lastPoint;

        if (_sampleCount == 1)
        {
            var y = bottomY - _samples[0] * plotHeight;
            var start = new Point(padLeft, y);
            var end = new Point(padLeft + plotWidth, y);
            lastPoint = end;

            using (var lg = lineGeometry.Open())
            {
                lg.BeginFigure(start, isFilled: false, isClosed: false);
                lg.LineTo(end, isStroked: true, isSmoothJoin: true);
            }

            using (var ag = areaGeometry.Open())
            {
                ag.BeginFigure(new Point(padLeft, bottomY), isFilled: true, isClosed: true);
                ag.LineTo(start, isStroked: false, isSmoothJoin: false);
                ag.LineTo(end, isStroked: false, isSmoothJoin: false);
                ag.LineTo(new Point(padLeft + plotWidth, bottomY), isStroked: false, isSmoothJoin: false);
            }
        }
        else
        {
            var stepX = plotWidth / (_sampleCount - 1);
            var firstY = bottomY - _samples[0] * plotHeight;
            var firstPoint = new Point(padLeft, firstY);
            lastPoint = firstPoint;

            using (var lg = lineGeometry.Open())
            using (var ag = areaGeometry.Open())
            {
                lg.BeginFigure(firstPoint, isFilled: false, isClosed: false);
                ag.BeginFigure(new Point(padLeft, bottomY), isFilled: true, isClosed: true);
                ag.LineTo(firstPoint, isStroked: false, isSmoothJoin: false);

                for (var i = 1; i < _sampleCount; i++)
                {
                    var pt = new Point(padLeft + i * stepX, bottomY - _samples[i] * plotHeight);
                    lg.LineTo(pt, isStroked: true, isSmoothJoin: true);
                    ag.LineTo(pt, isStroked: false, isSmoothJoin: false);
                    lastPoint = pt;
                }

                ag.LineTo(new Point(lastPoint.X, bottomY), isStroked: false, isSmoothJoin: false);
            }
        }

        lineGeometry.Freeze();
        areaGeometry.Freeze();

        if (_areaBrush is not null)
            dc.DrawGeometry(_areaBrush, null, areaGeometry);
        dc.DrawGeometry(null, linePen, lineGeometry);

        // Live terminal point with soft translucent halo.
        if (_haloBrush is not null)
            dc.DrawEllipse(_haloBrush, null, lastPoint, 4.5, 4.5);
        dc.DrawEllipse(strokeBrush, null, lastPoint, 2.2, 2.2);
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

    private void EnsureDerivedFills(Brush strokeBrush)
    {
        var color = ExtractAccentColor(strokeBrush);
        if (_cachedStrokeColor == color && _areaBrush is not null && _haloBrush is not null)
            return;

        _cachedStrokeColor = color;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0x46, color.R, color.G, color.B), 0.0),
                new GradientStop(Color.FromArgb(0x16, color.R, color.G, color.B), 0.65),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0)
            ]
        };
        gradient.Freeze();
        _areaBrush = gradient;

        var halo = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B));
        halo.Freeze();
        _haloBrush = halo;
    }

    private void EnsureGridPens(Brush gridBrush)
    {
        if (_gridPen is not null && ReferenceEquals(_gridPenBrush, gridBrush))
            return;

        _gridPenBrush = gridBrush;
        var baseColor = ExtractAccentColor(gridBrush);

        var midBrush = new SolidColorBrush(Color.FromArgb((byte)(baseColor.A * 0.55), baseColor.R, baseColor.G, baseColor.B));
        midBrush.Freeze();
        var midPen = new Pen(midBrush, 1.0) { DashStyle = new DashStyle([2.5, 3.5], 0) };
        midPen.Freeze();
        _gridPen = midPen;

        var baseLineBrush = new SolidColorBrush(Color.FromArgb((byte)(baseColor.A * 0.7), baseColor.R, baseColor.G, baseColor.B));
        baseLineBrush.Freeze();
        var basePen = new Pen(baseLineBrush, 1.0);
        basePen.Freeze();
        _baselinePen = basePen;
    }

    private static Color ExtractAccentColor(Brush brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops[0].Color,
        _ => Colors.DeepSkyBlue
    };
}
