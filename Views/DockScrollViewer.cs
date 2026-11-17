using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenTaskbar.Views;

public sealed class DockScrollViewer : ScrollViewer
{
    private const double EdgeFadeWidth = 14;
    private Point? _panStart;
    private double _panOffset;
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
        // Adding or removing an item only invalidates the items panel; its ancestors keep their old desired size
        // and stay valid until a layout pass reaches them, so the pending layout runs before the size is read.
        UpdateLayout();
        if (!part.IsMeasureValid) part.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return part.DesiredSize.Width;
    }

    public bool ScrollByWheel(int delta, ModifierKeys modifiers)
    {
        if (delta == 0 || (modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0) return false;
        if (ScrollableHeight > 0)
        {
            var distance = delta / (double)Mouse.MouseWheelDeltaForOneLine * (double)FindResource("DockCellSize");
            ScrollToVerticalOffset(VerticalOffset - distance);
            return true;
        }
        if (ScrollableWidth > 0)
        {
            var distance = delta / (double)Mouse.MouseWheelDeltaForOneLine * (double)FindResource("DockCellSize");
            ScrollToHorizontalOffset(HorizontalOffset - distance);
            return true;
        }
        return false;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollByWheel(e.Delta, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        // Plain wheel (without Shift/Ctrl) also scrolls overflowing dock content directly
        if (e.Delta != 0 && ScrollableWidth > 0)
        {
            var distance = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * (double)FindResource("DockCellSize");
            ScrollToHorizontalOffset(HorizontalOffset - distance);
            e.Handled = true;
            return;
        }
        if (e.Delta != 0 && ScrollableHeight > 0)
        {
            var distance = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * (double)FindResource("DockCellSize");
            ScrollToVerticalOffset(VerticalOffset - distance);
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ScrollableWidth > 0 && CaptureMouse())
        {
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
            ScrollToHorizontalOffset(_panOffset + origin.X - e.GetPosition(this).X);
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
