using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

namespace OpenTaskbar.Views;

public sealed class DockScrollViewer : ScrollViewer
{
    private const double EdgeFadeWidth = 34;
    private Point? _panStart;
    private double _panOffset;
    private double _targetHorizontalOffset;
    private double _targetVerticalOffset;
    // Continuous inertial scrolling: one render-clock loop eases the current offset toward the
    // wheel target. Unlike a fresh 100ms animation per tick, rapid wheel events just move the
    // target, so motion stays smooth and continuous instead of restarting and stuttering.
    private bool _smoothScrolling;
    private TimeSpan? _lastSmoothFrame;
    private ScrollContentPresenter? _scrollContentPresenter;
    private Button? _previousButton;
    private Button? _nextButton;
    private Button? _externalPreviousButton;
    private Button? _externalNextButton;
    private double _externalIndicatorCornerRadius = 19;
    private double _externalNextIndicatorInset;
    private bool? _lastExternalVertical;
    private double _lastExternalRadius = double.NaN;
    private double _lastExternalNextIndicatorInset = double.NaN;
    private bool _suppressOverflowIndicators;

    // The viewer is the single owner of the desired offset. Every write path (wheel, pan, step,
    // reset, external ScrollTo/DP writes) funnels through the helpers below, so an in-flight
    // smooth-scroll target can never fight an explicit reset. Gesture scopes are also the
    // refcounted guard the layout code uses to keep a transient "no overflow" measurement from
    // yanking the strip back to offset 0 while the user is still scrolling.
    private int _gestureDepth;
    private bool _suppressScrollReset;
    // Conservative default: until a caller reports otherwise, assume the strip may overflow so a
    // layout measurement made before the first real signal never resets the user's position.
    private bool _contentOverflows = true;

    // Edge-fade brushes are recreated on every scroll frame today (~6 allocations + a render
    // invalidation per wheel tick). Cache one frozen brush per (axis, before, after) combination
    // and reuse it while the viewport extent is unchanged.
    private readonly LinearGradientBrush?[] _cachedFadeMasks = new LinearGradientBrush?[8];
    private readonly double[] _cachedFadeExtents = new double[8];
    private bool _lastIndicatorVertical;
    private double _lastIndicatorTopOffset = double.NaN;
    private double _lastIndicatorRadius = double.NaN;

    public DockScrollViewer()
    {
        Unloaded += (_, _) => StopSmoothScroll();
    }

    /// <summary>
    /// Temporarily suppresses overflow scroll indicators and edge fades while magnification expands the dock
    /// from an unscrolled state, preventing sub-pixel layout jitter from flickering the scroll buttons.
    /// </summary>
    public bool SuppressOverflowIndicators
    {
        get => _suppressOverflowIndicators;
        set
        {
            if (_suppressOverflowIndicators == value) return;
            _suppressOverflowIndicators = value;
            UpdateEdgeFade();
        }
    }

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
    public static readonly DependencyProperty DockEdgeProperty = DependencyProperty.Register(
        nameof(DockEdge), typeof(OpenTaskbar.Models.DockEdge), typeof(DockScrollViewer),
        new PropertyMetadata(OpenTaskbar.Models.DockEdge.Bottom, LayoutChanged));

    public double HoverHeadroom { get => (double)GetValue(HoverHeadroomProperty); set => SetValue(HoverHeadroomProperty, value); }
    public Thickness ContentInset { get => (Thickness)GetValue(ContentInsetProperty); set => SetValue(ContentInsetProperty, value); }
    public OpenTaskbar.Models.DockEdge DockEdge { get => (OpenTaskbar.Models.DockEdge)GetValue(DockEdgeProperty); set => SetValue(DockEdgeProperty, value); }
    public bool IsPanning => _panStart is not null;

    /// <summary>Desired (target) horizontal offset owned by this viewer, even while smooth scrolling.</summary>
    public double DesiredHorizontalOffset => _targetHorizontalOffset;

    /// <summary>Desired (target) vertical offset owned by this viewer, even while smooth scrolling.</summary>
    public double DesiredVerticalOffset => _targetVerticalOffset;

    /// <summary>Last authoritative overflow signal from layout (true also means "unknown").</summary>
    public bool ContentOverflows => _contentOverflows;

    /// <summary>
    /// True while a user gesture or an explicit external scope is active. Layout code must not reset the
    /// strip to offset 0 during this window, because a mid-gesture layout measurement can briefly report
    /// "no overflow" (magnification expansion, deferred resize) and would otherwise yank the user back.
    /// </summary>
    public bool IsGestureActive => _gestureDepth > 0 || _panStart is not null || _smoothScrolling;

    /// <summary>
    /// Hard guard for external callers: while true, <see cref="ResetHorizontalScroll"/> and
    /// <see cref="SetContentOverflow"/> keep the current offset instead of resetting it.
    /// </summary>
    public bool SuppressScrollReset
    {
        get => _suppressScrollReset;
        set
        {
            if (_suppressScrollReset == value) return;
            _suppressScrollReset = value;
            ShellTelemetry.Event("ScrollReset", ("reason", value ? "suppress-enabled" : "suppress-released"),
                ("before", HorizontalOffset), ("after", HorizontalOffset));
        }
    }

    /// <summary>Opens a refcounted scope in which resets are forbidden. Always pair with <see cref="EndGesture"/>.</summary>
    public void BeginGesture()
    {
        _gestureDepth++;
        ShellTelemetry.Event("ScrollGesture", ("phase", "begin"), ("depth", _gestureDepth),
            ("offset", HorizontalOffset), ("target", _targetHorizontalOffset));
    }

    /// <summary>Closes one gesture scope. Clamps (never rewrites) the desired offset to the current range.</summary>
    public void EndGesture()
    {
        if (_gestureDepth == 0) return;
        _gestureDepth--;
        ClampTargetsToRange();
        ShellTelemetry.Event("ScrollGesture", ("phase", "end"), ("depth", _gestureDepth),
            ("offset", HorizontalOffset), ("target", _targetHorizontalOffset));
    }

    /// <summary>
    /// Reports whether the apps strip genuinely overflows. This is the only reset signal external layout
    /// code should send: the viewer decides if and when returning to offset 0 is safe. Passing
    /// <c>true</c> (or "unknown") always preserves the current offset; passing <c>false</c> only resets
    /// when no gesture/deferred resize is in flight and the caller is not suppressing resets.
    /// </summary>
    public void SetContentOverflow(bool overflows)
    {
        var before = HorizontalOffset;
        _contentOverflows = overflows;
        if (overflows)
        {
            // An overflow signal may shrink the range; clamp the target but never move the strip.
            ClampTargetsToRange();
            return;
        }

        if (SuppressScrollReset || IsGestureActive)
        {
            // Stop the render loop so a frame cannot drag us to a stale (or already clamped-to-zero) target,
            // but keep the offset exactly where the user left it.
            StopSmoothScroll();
            _targetHorizontalOffset = HorizontalOffset;
            ShellTelemetry.Event("ScrollReset", ("reason", "content-no-overflow-deferred"),
                ("before", before), ("after", HorizontalOffset),
                ("gesture", IsGestureActive), ("suppressed", SuppressScrollReset));
            return;
        }

        ResetHorizontalScrollCore("content-no-overflow", before);
    }

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
        // When only one axis overflows (horizontal bottom dock or vertical side dock), a plain wheel scrolls it.
        var horizontalOnly = ScrollableHeight <= 0 && ScrollableWidth > 0;
        var verticalOnly = ScrollableWidth <= 0 && ScrollableHeight > 0;
        if (!horizontalOnly && !verticalOnly && (modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0) return false;
        var cellSize = TryFindResource("DockCellSize") as double? ?? 52d;
        var multiplier = animate ? 2.0 : 1.0;
        var distance = delta / (double)Mouse.MouseWheelDeltaForOneLine * cellSize * multiplier;
        if (!horizontalOnly && ScrollableHeight > 0)
        {
            if (animate && SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = (Math.Sign(distance) == Math.Sign(VerticalOffset - _targetVerticalOffset))
                    ? _targetVerticalOffset : VerticalOffset;
                AnimateScrollVertical(baseOffset - distance);
            }
            else
            {
                ApplyVerticalOffset(VerticalOffset - distance);
            }
            return true;
        }
        if (ScrollableWidth > 0)
        {
            if (animate && SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = (Math.Sign(distance) == Math.Sign(HorizontalOffset - _targetHorizontalOffset))
                    ? _targetHorizontalOffset : HorizontalOffset;
                AnimateScrollHorizontal(baseOffset - distance);
            }
            else
            {
                ApplyHorizontalOffset(HorizontalOffset - distance);
            }
            return true;
        }
        return false;
    }

    /// <summary>Single write path for immediate horizontal offset changes.</summary>
    private void ApplyHorizontalOffset(double value)
    {
        var max = Math.Max(0.0, ScrollableWidth);
        ScrollToHorizontalOffset(double.IsFinite(value) ? Math.Clamp(value, 0, max) : 0);
        // WPF may clamp to a different value than we requested; the actual offset is authoritative.
        _targetHorizontalOffset = HorizontalOffset;
    }

    /// <summary>Single write path for immediate vertical offset changes.</summary>
    private void ApplyVerticalOffset(double value)
    {
        var max = Math.Max(0.0, ScrollableHeight);
        ScrollToVerticalOffset(double.IsFinite(value) ? Math.Clamp(value, 0, max) : 0);
        _targetVerticalOffset = VerticalOffset;
    }

    /// <summary>Single write path for animated horizontal targets.</summary>
    private void AnimateScrollHorizontal(double target)
    {
        _targetHorizontalOffset = double.IsFinite(target) ? Math.Clamp(target, 0, Math.Max(0.0, ScrollableWidth)) : 0;
        if (!SystemParameters.ClientAreaAnimation)
        {
            ScrollToHorizontalOffset(_targetHorizontalOffset);
            return;
        }
        BeginSmoothScroll();
    }

    /// <summary>Single write path for animated vertical targets.</summary>
    private void AnimateScrollVertical(double target)
    {
        _targetVerticalOffset = double.IsFinite(target) ? Math.Clamp(target, 0, Math.Max(0.0, ScrollableHeight)) : 0;
        if (!SystemParameters.ClientAreaAnimation)
        {
            ScrollToVerticalOffset(_targetVerticalOffset);
            return;
        }
        BeginSmoothScroll();
    }

    private void ClampTargetsToRange()
    {
        var maxH = Math.Max(0.0, ScrollableWidth);
        var maxV = Math.Max(0.0, ScrollableHeight);
        _targetHorizontalOffset = double.IsFinite(_targetHorizontalOffset) ? Math.Clamp(_targetHorizontalOffset, 0, maxH) : 0;
        _targetVerticalOffset = double.IsFinite(_targetVerticalOffset) ? Math.Clamp(_targetVerticalOffset, 0, maxV) : 0;
    }

    private void BeginSmoothScroll()
    {
        if (_smoothScrolling) return;
        // Cancel any in-flight property animation so the render loop owns the offset exclusively.
        BeginAnimation(CurrentHorizontalOffsetProperty, null);
        BeginAnimation(CurrentVerticalOffsetProperty, null);
        _smoothScrolling = true;
        _lastSmoothFrame = null;
        CompositionTarget.Rendering += SmoothScrollFrame;
    }

    private void SmoothScrollFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        var dt = _lastSmoothFrame is { } last ? (now - last).TotalSeconds : 1.0 / 60.0;
        _lastSmoothFrame = now;
        // Frame-rate independent exponential approach: ~92% of the remaining distance per 60Hz frame.
        var t = 1.0 - Math.Exp(-dt / 0.045);

        var horizontal = ScrollableWidth > 0 && ScrollableHeight <= 0;
        // A layout/magnification change can shrink the range mid-gesture. Clamp the desired offset to
        // the live range instead of chasing a target that no longer exists; stopping below handles the
        // case where the strip stopped overflowing altogether (WPF clamps the actual offset itself).
        if (!horizontal && ScrollableHeight <= 0)
        {
            _targetHorizontalOffset = HorizontalOffset;
            _targetVerticalOffset = VerticalOffset;
            StopSmoothScroll();
            return;
        }

        var max = horizontal ? ScrollableWidth : ScrollableHeight;
        var target = horizontal ? _targetHorizontalOffset : _targetVerticalOffset;
        if (target > max) target = max;
        else if (target < 0) target = 0;
        if (horizontal) _targetHorizontalOffset = target;
        else _targetVerticalOffset = target;

        var current = horizontal ? HorizontalOffset : VerticalOffset;
        var next = current + (target - current) * t;

        if (Math.Abs(target - next) < 0.3)
        {
            next = target;
            if (horizontal) ScrollToHorizontalOffset(next);
            else ScrollToVerticalOffset(next);
            StopSmoothScroll();
            return;
        }

        if (horizontal) ScrollToHorizontalOffset(next);
        else ScrollToVerticalOffset(next);
    }

    private void StopSmoothScroll()
    {
        if (!_smoothScrolling) return;
        CompositionTarget.Rendering -= SmoothScrollFrame;
        _smoothScrolling = false;
        _lastSmoothFrame = null;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (IsAudioWidgetSource(e.OriginalSource as DependencyObject)) return;

        if (ScrollByWheel(e.Delta, Keyboard.Modifiers, animate: true))
        {
            e.Handled = true;
            return;
        }

        // Plain wheel (without Shift/Ctrl) also scrolls overflowing dock content directly
        if (e.Delta != 0 && ScrollableWidth > 0)
        {
            var cellSize = TryFindResource("DockCellSize") as double? ?? 52d;
            var distance = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * cellSize * 2.0;
            if (SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = (Math.Sign(distance) == Math.Sign(HorizontalOffset - _targetHorizontalOffset))
                    ? _targetHorizontalOffset : HorizontalOffset;
                AnimateScrollHorizontal(baseOffset - distance);
            }
            else
            {
                ApplyHorizontalOffset(HorizontalOffset - distance);
            }
            e.Handled = true;
            return;
        }
        if (e.Delta != 0 && ScrollableHeight > 0)
        {
            var cellSize = TryFindResource("DockCellSize") as double? ?? 52d;
            var distance = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * cellSize * 2.0;
            if (SystemParameters.ClientAreaAnimation)
            {
                var baseOffset = (Math.Sign(distance) == Math.Sign(VerticalOffset - _targetVerticalOffset))
                    ? _targetVerticalOffset : VerticalOffset;
                AnimateScrollVertical(baseOffset - distance);
            }
            else
            {
                ApplyVerticalOffset(VerticalOffset - distance);
            }
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    private static bool IsAudioWidgetSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is AudioWidget) return true;
            source = source switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(source),
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }

        return false;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // Middle button is reserved for panning the dock. It must capture even when nothing overflows,
        // otherwise the gesture falls through to a dock icon's MouseUp and launches a new app instance.
        if (e.ChangedButton == MouseButton.Middle && CaptureMouse())
        {
            StopSmoothScroll();
            BeginAnimation(CurrentHorizontalOffsetProperty, null);
            BeginAnimation(CurrentVerticalOffsetProperty, null);
            _panStart = e.GetPosition(this);
            _panOffset = (ScrollableHeight > 0 && ScrollableWidth <= 0) ? VerticalOffset : HorizontalOffset;
            e.Handled = true;
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panStart is { } origin)
        {
            if (ScrollableHeight > 0 && ScrollableWidth <= 0)
            {
                var target = _panOffset + origin.Y - e.GetPosition(this).Y;
                ApplyVerticalOffset(target);
            }
            else
            {
                var target = _panOffset + origin.X - e.GetPosition(this).X;
                ApplyHorizontalOffset(target);
            }
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

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_previousButton is not null)
            _previousButton.Click -= OnPreviousButtonClick;
        if (_nextButton is not null)
            _nextButton.Click -= OnNextButtonClick;

        _scrollContentPresenter = GetTemplateChild("PART_ScrollContentPresenter") as ScrollContentPresenter;
        _previousButton = GetTemplateChild("PART_PreviousButton") as Button;
        _nextButton = GetTemplateChild("PART_NextButton") as Button;

        if (_previousButton is not null)
            _previousButton.Click += OnPreviousButtonClick;
        if (_nextButton is not null)
            _nextButton.Click += OnNextButtonClick;

        UpdateIndicatorLayout();
        UpdateIndicators();
    }

    private void OnPreviousButtonClick(object sender, RoutedEventArgs e) => ScrollStep(forward: false);
    private void OnNextButtonClick(object sender, RoutedEventArgs e) => ScrollStep(forward: true);

    /// <summary>
    /// Registers external overlay buttons (e.g. placed at the outer edges of DockSurface) that
    /// render the scroll scrim and chevron in place of the internal template buttons.
    /// </summary>
    public void SetExternalIndicators(Button? previous, Button? next)
    {
        _externalPreviousButton = previous;
        _externalNextButton = next;
        _lastExternalVertical = null;
        _lastExternalRadius = double.NaN;
        _lastIndicatorTopOffset = double.NaN;
        UpdateIndicatorLayout();
        UpdateIndicators();
    }

    /// <summary>Inner corner radius matching DockFrame so external edge scrims align cleanly with the dock corners.</summary>
    public double ExternalIndicatorCornerRadius
    {
        get => _externalIndicatorCornerRadius;
        set
        {
            var clamped = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (Math.Abs(_externalIndicatorCornerRadius - clamped) < 0.01) return;
            _externalIndicatorCornerRadius = clamped;
            UpdateIndicatorLayout();
        }
    }

    public void SetExternalNextIndicatorInset(double inset)
    {
        var clamped = double.IsFinite(inset) ? Math.Max(0, inset) : 0;
        if (Math.Abs(_externalNextIndicatorInset - clamped) < 0.01) return;
        _externalNextIndicatorInset = clamped;
        _lastExternalNextIndicatorInset = double.NaN;
        UpdateExternalIndicatorLayout(DockEdge != OpenTaskbar.Models.DockEdge.Bottom);
    }

    public void ScrollStepBack() => ScrollStep(forward: false);
    public void ScrollStepForward() => ScrollStep(forward: true);

    /// <summary>
    /// Returns the apps strip to its start after the content stopped overflowing. Also clears any
    /// in-flight smooth-scroll target and offset animation, otherwise the render loop would drag
    /// the strip back toward the old offset after this reset.
    /// </summary>
    public void ResetHorizontalScroll()
    {
        if (SuppressScrollReset || IsGestureActive)
        {
            // A reset requested from inside a scroll/resize gesture is the feedback loop this class now
            // breaks. Keep the offset; the caller should use SetContentOverflow for layout-driven resets.
            ShellTelemetry.Event("ScrollReset", ("reason", "reset-suppressed"),
                ("before", HorizontalOffset), ("after", HorizontalOffset),
                ("gesture", IsGestureActive), ("suppressed", SuppressScrollReset));
            return;
        }
        ResetHorizontalScrollCore("explicit", HorizontalOffset);
    }

    /// <summary>
    /// Unconditional reset used by the viewer itself (and by SetContentOverflow after its guards pass).
    /// Stops the render loop and cancels DP animations first, so no target can pull the strip back.
    /// </summary>
    private void ResetHorizontalScrollCore(string reason, double before)
    {
        var targetBefore = _targetHorizontalOffset;
        StopSmoothScroll();
        BeginAnimation(CurrentHorizontalOffsetProperty, null);
        _targetHorizontalOffset = 0;
        _contentOverflows = false;
        ScrollToHorizontalOffset(0);
        // Adopt WPF's clamped result so target and actual offset stay identical after the reset.
        _targetHorizontalOffset = HorizontalOffset;
        ShellTelemetry.Event("ScrollReset", ("reason", reason), ("before", before),
            ("after", HorizontalOffset), ("targetBefore", targetBefore));
    }

    private void ScrollStep(bool forward)
    {
        var cellSize = TryFindResource("DockCellSize") as double? ?? 52d;
        var step = cellSize * 2.5;
        if (ScrollableHeight > 0)
        {
            var current = (Math.Abs(VerticalOffset - _targetVerticalOffset) > 1) ? _targetVerticalOffset : VerticalOffset;
            var next = forward ? current + step : current - step;
            AnimateScrollVertical(next);
        }
        else if (ScrollableWidth > 0)
        {
            var current = (Math.Abs(HorizontalOffset - _targetHorizontalOffset) > 1) ? _targetHorizontalOffset : HorizontalOffset;
            var next = forward ? current + step : current - step;
            AnimateScrollHorizontal(next);
        }
    }

    private void UpdateIndicatorLayout()
    {
        var topOffset = HoverHeadroom;
        var vertical = ScrollableHeight > 0;
        var radius = _externalIndicatorCornerRadius;
        // These values only change on resize/edge/hover-headroom changes, never during a scroll. Reapplying
        // a dozen template properties (with resource lookups and Thickness/CornerRadius allocations) on
        // every scroll frame was the second-largest cost after the gradient brush.
        if (_lastIndicatorVertical == vertical && Math.Abs(_lastIndicatorTopOffset - topOffset) < 0.01
            && Math.Abs(_lastIndicatorRadius - radius) < 0.01) return;
        _lastIndicatorVertical = vertical;
        _lastIndicatorTopOffset = topOffset;
        _lastIndicatorRadius = radius;
        if (vertical)
        {
            if (_previousButton is not null)
            {
                _previousButton.Content = TryFindResource("IconChevronUp");
                _previousButton.SetResourceReference(BackgroundProperty, "DockScrollScrimTop");
                _previousButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                _previousButton.VerticalAlignment = VerticalAlignment.Top;
                _previousButton.Width = double.NaN;
                _previousButton.Height = 28;
                _previousButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                _previousButton.VerticalContentAlignment = VerticalAlignment.Top;
                _previousButton.Padding = new Thickness(0, 4, 0, 0);
                _previousButton.Margin = new Thickness(0, topOffset, 0, 0);
                _previousButton.Tag = new CornerRadius(16, 16, 0, 0);
            }
            if (_nextButton is not null)
            {
                _nextButton.Content = TryFindResource("IconChevronDown");
                _nextButton.SetResourceReference(BackgroundProperty, "DockScrollScrimBottom");
                _nextButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                _nextButton.VerticalAlignment = VerticalAlignment.Bottom;
                _nextButton.Width = double.NaN;
                _nextButton.Height = 28;
                _nextButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                _nextButton.VerticalContentAlignment = VerticalAlignment.Bottom;
                _nextButton.Padding = new Thickness(0, 0, 0, 4);
                _nextButton.Margin = new Thickness(0);
                _nextButton.Tag = new CornerRadius(0, 0, 16, 16);
            }
        }
        else
        {
            if (_previousButton is not null)
            {
                _previousButton.Content = TryFindResource("IconChevronLeft");
                _previousButton.SetResourceReference(BackgroundProperty, "DockScrollScrimStart");
                _previousButton.HorizontalAlignment = HorizontalAlignment.Left;
                _previousButton.VerticalAlignment = VerticalAlignment.Stretch;
                _previousButton.Width = 28;
                _previousButton.Height = double.NaN;
                _previousButton.HorizontalContentAlignment = HorizontalAlignment.Left;
                _previousButton.VerticalContentAlignment = VerticalAlignment.Center;
                _previousButton.Padding = new Thickness(5, 0, 0, 0);
                _previousButton.Margin = new Thickness(0, topOffset, 0, 0);
                _previousButton.Tag = new CornerRadius(16, 0, 0, 16);
            }
            if (_nextButton is not null)
            {
                _nextButton.Content = TryFindResource("IconChevronRight");
                _nextButton.SetResourceReference(BackgroundProperty, "DockScrollScrimEnd");
                _nextButton.HorizontalAlignment = HorizontalAlignment.Right;
                _nextButton.VerticalAlignment = VerticalAlignment.Stretch;
                _nextButton.Width = 28;
                _nextButton.Height = double.NaN;
                _nextButton.HorizontalContentAlignment = HorizontalAlignment.Right;
                _nextButton.VerticalContentAlignment = VerticalAlignment.Center;
                _nextButton.Padding = new Thickness(0, 0, 5, 0);
                _nextButton.Margin = new Thickness(0, topOffset, 0, 0);
                _nextButton.Tag = new CornerRadius(0, 16, 16, 0);
            }
        }

        UpdateExternalIndicatorLayout(vertical);
    }

    private void UpdateExternalIndicatorLayout(bool vertical)
    {
        if (_externalPreviousButton is null && _externalNextButton is null) return;
        var radius = _externalIndicatorCornerRadius;
        if (_lastExternalVertical == vertical
            && Math.Abs(_lastExternalRadius - radius) < 0.01
            && Math.Abs(_lastExternalNextIndicatorInset - _externalNextIndicatorInset) < 0.01) return;
        _lastExternalVertical = vertical;
        _lastExternalRadius = radius;
        _lastExternalNextIndicatorInset = _externalNextIndicatorInset;

        // Inset by 1 DIP (ControlStroke) so edge scrims stay cleanly inside DockFrame's border.
        if (vertical)
        {
            if (_externalPreviousButton is not null)
            {
                _externalPreviousButton.Content = TryFindResource("IconChevronUp");
                _externalPreviousButton.SetResourceReference(BackgroundProperty, "DockScrollScrimTop");
                _externalPreviousButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                _externalPreviousButton.VerticalAlignment = VerticalAlignment.Top;
                _externalPreviousButton.Width = double.NaN;
                _externalPreviousButton.Height = 28;
                _externalPreviousButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                _externalPreviousButton.VerticalContentAlignment = VerticalAlignment.Top;
                _externalPreviousButton.Padding = new Thickness(0, 6, 0, 0);
                _externalPreviousButton.Margin = new Thickness(1, 1, 1, 0);
                _externalPreviousButton.Tag = new CornerRadius(radius, radius, 0, 0);
            }
            if (_externalNextButton is not null)
            {
                _externalNextButton.Content = TryFindResource("IconChevronDown");
                _externalNextButton.SetResourceReference(BackgroundProperty, "DockScrollScrimBottom");
                _externalNextButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                _externalNextButton.VerticalAlignment = VerticalAlignment.Bottom;
                _externalNextButton.Width = double.NaN;
                _externalNextButton.Height = 28;
                _externalNextButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                _externalNextButton.VerticalContentAlignment = VerticalAlignment.Bottom;
                _externalNextButton.Padding = new Thickness(0, 0, 0, 6);
                _externalNextButton.Margin = new Thickness(1, 0, 1, 1 + _externalNextIndicatorInset);
                _externalNextButton.Tag = new CornerRadius(0, 0, radius, radius);
            }
        }
        else
        {
            if (_externalPreviousButton is not null)
            {
                _externalPreviousButton.Content = TryFindResource("IconChevronLeft");
                _externalPreviousButton.SetResourceReference(BackgroundProperty, "DockScrollScrimStart");
                _externalPreviousButton.HorizontalAlignment = HorizontalAlignment.Left;
                _externalPreviousButton.VerticalAlignment = VerticalAlignment.Stretch;
                _externalPreviousButton.Width = 28;
                _externalPreviousButton.Height = double.NaN;
                _externalPreviousButton.HorizontalContentAlignment = HorizontalAlignment.Left;
                _externalPreviousButton.VerticalContentAlignment = VerticalAlignment.Center;
                // 1 DIP margin + 7 DIP padding places the chevron right beside the 8 DIP resize grip.
                _externalPreviousButton.Padding = new Thickness(7, 0, 0, 0);
                _externalPreviousButton.Margin = new Thickness(1, 1, 0, 1);
                _externalPreviousButton.Tag = new CornerRadius(radius, 0, 0, radius);
            }
            if (_externalNextButton is not null)
            {
                _externalNextButton.Content = TryFindResource("IconChevronRight");
                _externalNextButton.SetResourceReference(BackgroundProperty, "DockScrollScrimEnd");
                _externalNextButton.HorizontalAlignment = HorizontalAlignment.Right;
                _externalNextButton.VerticalAlignment = VerticalAlignment.Stretch;
                _externalNextButton.Width = 28;
                _externalNextButton.Height = double.NaN;
                _externalNextButton.HorizontalContentAlignment = HorizontalAlignment.Right;
                _externalNextButton.VerticalContentAlignment = VerticalAlignment.Center;
                _externalNextButton.Padding = new Thickness(0, 0, 7, 0);
                _externalNextButton.Margin = new Thickness(0, 1, 1 + _externalNextIndicatorInset, 1);
                _externalNextButton.Tag = new CornerRadius(0, radius, radius, 0);
            }
        }
    }

    private static void SetVisibility(UIElement? element, Visibility value)
    {
        if (element is not null && element.Visibility != value) element.Visibility = value;
    }

    private void UpdateIndicators()
    {
        if (_previousButton is null && _nextButton is null
            && _externalPreviousButton is null && _externalNextButton is null) return;

        if (_suppressOverflowIndicators && HorizontalOffset <= 0.5 && VerticalOffset <= 0.5)
        {
            SetVisibility(_previousButton, Visibility.Collapsed);
            SetVisibility(_nextButton, Visibility.Collapsed);
            SetVisibility(_externalPreviousButton, Visibility.Collapsed);
            SetVisibility(_externalNextButton, Visibility.Collapsed);
            return;
        }

        if (ScrollableHeight > 1.0)
        {
            var vHiddenBefore = VerticalOffset > 0.5;
            var vHiddenAfter = VerticalOffset < ScrollableHeight - 0.5;
            var prevVis = vHiddenBefore ? Visibility.Visible : Visibility.Collapsed;
            var nextVis = vHiddenAfter ? Visibility.Visible : Visibility.Collapsed;
            // Template buttons: keep in sync for tests; hide visually when external overlay is active.
            SetVisibility(_previousButton, _externalPreviousButton is null ? prevVis : Visibility.Collapsed);
            SetVisibility(_nextButton, _externalNextButton is null ? nextVis : Visibility.Collapsed);
            SetVisibility(_externalPreviousButton, prevVis);
            SetVisibility(_externalNextButton, nextVis);
            return;
        }

        if (ScrollableWidth > 1.0)
        {
            var hiddenBefore = HorizontalOffset > 0.5;
            var hiddenAfter = HorizontalOffset < ScrollableWidth - 0.5;
            var prevVis = hiddenBefore ? Visibility.Visible : Visibility.Collapsed;
            var nextVis = hiddenAfter ? Visibility.Visible : Visibility.Collapsed;
            SetVisibility(_previousButton, _externalPreviousButton is null ? prevVis : Visibility.Collapsed);
            SetVisibility(_nextButton, _externalNextButton is null ? nextVis : Visibility.Collapsed);
            SetVisibility(_externalPreviousButton, prevVis);
            SetVisibility(_externalNextButton, nextVis);
            return;
        }

        SetVisibility(_previousButton, Visibility.Collapsed);
        SetVisibility(_nextButton, Visibility.Collapsed);
        SetVisibility(_externalPreviousButton, Visibility.Collapsed);
        SetVisibility(_externalNextButton, Visibility.Collapsed);
    }

    private void ApplyOpacityMask(Brush? mask)
    {
        if (_scrollContentPresenter is not null)
        {
            if (!ReferenceEquals(_scrollContentPresenter.OpacityMask, mask))
                _scrollContentPresenter.OpacityMask = mask;
            if (ReadLocalValue(OpacityMaskProperty) != DependencyProperty.UnsetValue)
                ClearValue(OpacityMaskProperty);
        }
        else if (!ReferenceEquals(OpacityMask, mask))
        {
            OpacityMask = mask;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _panStart = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        // External ScrollTo / CurrentHorizontalOffset writes bypass the write helpers above. Adopt their
        // result as the desired offset so no stale target survives them — except while the render loop
        // owns the offset (its per-frame ScrollTo raises this event too).
        if (!_smoothScrolling && !IsPanning)
        {
            _targetHorizontalOffset = HorizontalOffset;
            _targetVerticalOffset = VerticalOffset;
        }
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
        UpdateIndicatorLayout();
        UpdateIndicators();

        // When external edge scrim overlays (ScrollPrevOverlay / ScrollNextOverlay) are attached, they already
        // draw the edge fade. Applying an OpacityMask to ScrollContentPresenter forces WPF to rasterize the entire
        // dock strip into an offscreen intermediate bitmap whose bounds resize every frame when an icon magnifies
        // above the bar, making Start and widgets visibly wobble vertically and blur/rescale.
        if (_externalPreviousButton is not null || _externalNextButton is not null)
        {
            ApplyOpacityMask(null);
            return;
        }

        if (_suppressOverflowIndicators && HorizontalOffset <= 0.5 && VerticalOffset <= 0.5)
        {
            ApplyOpacityMask(null);
            return;
        }

        if (ScrollableHeight > 1.0)
        {
            var vHiddenBefore = VerticalOffset > 0.5;
            var vHiddenAfter = VerticalOffset < ScrollableHeight - 0.5;
            if (ActualHeight <= EdgeFadeWidth * 2 || (!vHiddenBefore && !vHiddenAfter))
            {
                ApplyOpacityMask(null);
                return;
            }
            ApplyOpacityMask(GetFadeMask(vertical: true, vHiddenBefore, vHiddenAfter, ActualHeight));
            return;
        }

        var hiddenBefore = HorizontalOffset > 0.5;
        var hiddenAfter = HorizontalOffset < ScrollableWidth - 0.5;
        if (ActualWidth <= EdgeFadeWidth * 2 || (!hiddenBefore && !hiddenAfter))
        {
            ApplyOpacityMask(null);
            return;
        }

        ApplyOpacityMask(GetFadeMask(vertical: false, hiddenBefore, hiddenAfter, ActualWidth));
    }

    private LinearGradientBrush GetFadeMask(bool vertical, bool hiddenBefore, bool hiddenAfter, double extent)
    {
        var key = (vertical ? 4 : 0) | (hiddenBefore ? 2 : 0) | (hiddenAfter ? 1 : 0);
        var cached = _cachedFadeMasks[key];
        if (cached is not null && Math.Abs(_cachedFadeExtents[key] - extent) < 0.5) return cached;

        var fade = EdgeFadeWidth / extent;
        var mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = vertical ? new Point(0, 1) : new Point(1, 0)
        };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(hiddenBefore ? (byte)0 : (byte)255, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, fade));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - fade));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(hiddenAfter ? (byte)0 : (byte)255, 0, 0, 0), 1));
        mask.Freeze();
        _cachedFadeMasks[key] = mask;
        _cachedFadeExtents[key] = extent;
        return mask;
    }

    private static void LayoutChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not DockScrollViewer viewer) return;
        var inset = viewer.ContentInset;
        var headroom = viewer.HoverHeadroom;
        // Expand the clipping viewport away from the screen edge so magnified icons pop out of the dock cleanly
        // on Bottom, Left, and Right edges while keeping the shelf content at its original position.
        switch (viewer.DockEdge)
        {
            case OpenTaskbar.Models.DockEdge.Left:
                // Side docks do not use horizontal ContentInset; keep left and right padding symmetric.
                viewer.SetCurrentValue(MarginProperty, new Thickness(0, 0, -headroom, 0));
                viewer.SetCurrentValue(PaddingProperty, new Thickness(0, 0, headroom, 0));
                break;
            case OpenTaskbar.Models.DockEdge.Right:
                viewer.SetCurrentValue(MarginProperty, new Thickness(-headroom, 0, 0, 0));
                viewer.SetCurrentValue(PaddingProperty, new Thickness(headroom, 0, 0, 0));
                break;
            default:
                viewer.SetCurrentValue(MarginProperty, new Thickness(inset.Left, inset.Top - headroom, inset.Right, inset.Bottom));
                viewer.SetCurrentValue(PaddingProperty, new Thickness(0, headroom, 0, 0));
                break;
        }
        viewer.UpdateIndicatorLayout();
    }
}
