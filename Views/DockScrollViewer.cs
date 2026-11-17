using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenTaskbar.Views;

public sealed class DockScrollViewer : ScrollViewer
{
    private const double EdgeFadeWidth = 14;
    private Point? _panStart;
    private double _panOffset;
    private double _targetHorizontalOffset;
    private double _targetVerticalOffset;

    public static readonly DependencyProperty CurrentHorizontalOffsetProperty = DependencyProperty.Register(
        nameof(CurrentHorizontalOffset), typeof(double), typeof(DockScrollViewer),
        new PropertyMetadata(0d, (d, e) => ((DockScrollViewer)d).ScrollToHorizontalOffset((double)e.NewValue)));

    public static readonly DependencyProperty CurrentVerticalOffsetProperty = DependencyProperty.Register(
        nameof(CurrentVerticalOffset), typeof(double), typeof(DockScrollViewer),
        new PropertyMetadata(0d, (d, e) => ((DockScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    public double CurrentHorizontalOffset
    {
        get => (double)GetValue(CurrentHorizontalOffsetProperty);
        set => SetValue(CurrentHorizontalOffsetProperty, value);
    }

    public double CurrentVerticalOffset
    {
        get => (double)GetValue(CurrentVerticalOffsetProperty);
        set => SetValue(CurrentVerticalOffsetProperty, value);
    }

    public static readonly DependencyProperty HoverHeadroomProperty = DependencyProperty.Register(
        nameof(HoverHeadroom), typeof(double), typeof(DockScrollViewer),
        new PropertyMetadata(0d, LayoutChanged), value => value is double height && double.IsFinite(height) && height >= 0);
    public static readonly DependencyProperty ContentInsetProperty = DependencyProperty.Register(
        nameof(ContentInset), typeof(Thickness), typeof(DockScrollViewer), new PropertyMetadata(new Thickness(), LayoutChanged));

    public double HoverHeadroom { get => (double)GetValue(HoverHeadroomProperty); set => SetValue(HoverHeadroomProperty, value); }
    public Thickness ContentInset { get => (Thickness)GetValue(ContentInsetProperty); set => SetValue(ContentInsetProperty, value); }
    public bool IsPanning => _panStart is not null;

    /// <summary>Returns the width a part of the content needs to be shown without scrolling.</summary>
    public double MeasureContentWidth(UIElement part)
    {
        UpdateLayout();
        part.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return part.DesiredSize.Width;
    }

    public bool ScrollByWheel(int delta, ModifierKeys modifiers, bool animate = false)
    {
        if (delta == 0) return false;
        // When only the horizontal axis overflows (e.g. too many dock items), a plain wheel scrolls it.
        var horizontalOnly = ScrollableHeight <= 0 && ScrollableWidth > 0;
        if (!horizontalOnly && (modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0) return false;
        var cellSize = TryFindResource("DockCellSize") as double? ?? 52d;
        var distance = delta / (double)Mouse.MouseWheelDeltaForOneLine * cellSize;
        if (!horizontalOnly && ScrollableHeight > 0)
        {
            if (animate && SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = Math.Abs(_targetVerticalOffset - VerticalOffset) > 100 ? VerticalOffset : _targetVerticalOffset;
                AnimateScrollVertical(baseOffset - distance);
            }
            else
            {
                ScrollToVerticalOffset(VerticalOffset - distance);
                _targetVerticalOffset = VerticalOffset;
            }
            return true;
        }
        if (ScrollableWidth > 0)
        {
            if (animate && SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = Math.Abs(_targetHorizontalOffset - HorizontalOffset) > 100 ? HorizontalOffset : _targetHorizontalOffset;
                AnimateScrollHorizontal(baseOffset - distance);
            }
            else
            {
                ScrollToHorizontalOffset(HorizontalOffset - distance);
                _targetHorizontalOffset = HorizontalOffset;
            }
            return true;
        }
        return false;
    }

    private void AnimateScrollHorizontal(double target)
    {
        target = Math.Clamp(target, 0, ScrollableWidth);
        _targetHorizontalOffset = target;
        if (!SystemParameters.ClientAreaAnimation)
        {
            ScrollToHorizontalOffset(target);
            return;
        }

        var animation = new DoubleAnimation
        {
            From = HorizontalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(CurrentHorizontalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateScrollVertical(double target)
    {
        target = Math.Clamp(target, 0, ScrollableHeight);
        _targetVerticalOffset = target;
        if (!SystemParameters.ClientAreaAnimation)
        {
            ScrollToVerticalOffset(target);
            return;
        }

        var animation = new DoubleAnimation
        {
            From = VerticalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(CurrentVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollByWheel(e.Delta, Keyboard.Modifiers, animate: true))
        {
            e.Handled = true;
            return;
        }

        // Plain wheel (without Shift/Ctrl) also scrolls overflowing dock content directly
        if (e.Delta != 0 && ScrollableWidth > 0)
        {
            var cellSize = TryFindResource("DockCellSize") as double? ?? 52d;
            var distance = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * cellSize;
            if (SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = Math.Abs(_targetHorizontalOffset - HorizontalOffset) > 100 ? HorizontalOffset : _targetHorizontalOffset;
                AnimateScrollHorizontal(baseOffset - distance);
            }
            else
            {
                ScrollToHorizontalOffset(HorizontalOffset - distance);
                _targetHorizontalOffset = HorizontalOffset;
            }
            e.Handled = true;
            return;
        }
        if (e.Delta != 0 && ScrollableHeight > 0)
        {
            var cellSize = TryFindResource("DockCellSize") as double? ?? 52d;
            var distance = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * cellSize;
            if (SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = Math.Abs(_targetVerticalOffset - VerticalOffset) > 100 ? VerticalOffset : _targetVerticalOffset;
                AnimateScrollVertical(baseOffset - distance);
            }
            else
            {
                ScrollToVerticalOffset(VerticalOffset - distance);
                _targetVerticalOffset = VerticalOffset;
            }
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ScrollableWidth > 0 && CaptureMouse())
        {
            BeginAnimation(CurrentHorizontalOffsetProperty, null);
            BeginAnimation(CurrentVerticalOffsetProperty, null);
            _panStart = e.GetPosition(this);
            _panOffset = HorizontalOffset;
            e.Handled = true;
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panStart is { } origin)
        {
            var target = _panOffset + origin.X - e.GetPosition(this).X;
            ScrollToHorizontalOffset(target);
            _targetHorizontalOffset = HorizontalOffset;
            e.Handled = true;
        }
        base.OnMouseMove(e);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && IsPanning)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnPreviewMouseUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _panStart = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        UpdateEdgeFade();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateEdgeFade();
    }

    /// <summary>Fades the side that has more content, since the scrollbar is hidden and overflow would otherwise go unnoticed.</summary>
    private void UpdateEdgeFade()
    {
        if (ScrollableHeight > 0)
        {
            var vHiddenBefore = VerticalOffset > 0.5;
            var vHiddenAfter = VerticalOffset < ScrollableHeight - 0.5;
            if (ActualHeight <= EdgeFadeWidth * 2 || (!vHiddenBefore && !vHiddenAfter))
            {
                ClearValue(OpacityMaskProperty);
                return;
            }
            var vFade = EdgeFadeWidth / ActualHeight;
            var vMask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            vMask.GradientStops.Add(new GradientStop(Color.FromArgb(vHiddenBefore ? (byte)0 : (byte)255, 0, 0, 0), 0));
            vMask.GradientStops.Add(new GradientStop(Colors.Black, vFade));
            vMask.GradientStops.Add(new GradientStop(Colors.Black, 1 - vFade));
            vMask.GradientStops.Add(new GradientStop(Color.FromArgb(vHiddenAfter ? (byte)0 : (byte)255, 0, 0, 0), 1));
            vMask.Freeze();
            OpacityMask = vMask;
            return;
        }

        var hiddenBefore = HorizontalOffset > 0.5;
        var hiddenAfter = HorizontalOffset < ScrollableWidth - 0.5;
        if (ActualWidth <= EdgeFadeWidth * 2 || (!hiddenBefore && !hiddenAfter))
        {
            ClearValue(OpacityMaskProperty);
            return;
        }

        var fade = EdgeFadeWidth / ActualWidth;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(hiddenBefore ? (byte)0 : (byte)255, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, fade));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - fade));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(hiddenAfter ? (byte)0 : (byte)255, 0, 0, 0), 1));
        mask.Freeze();
        OpacityMask = mask;
    }

    private static void LayoutChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not DockScrollViewer viewer) return;
        var inset = viewer.ContentInset;
        // Expand the clipping viewport upward, then keep the shelf content at its original position.
        viewer.SetCurrentValue(MarginProperty, new Thickness(inset.Left, inset.Top - viewer.HoverHeadroom, inset.Right, inset.Bottom));
        viewer.SetCurrentValue(PaddingProperty, new Thickness(0, viewer.HoverHeadroom, 0, 0));
    }
}
