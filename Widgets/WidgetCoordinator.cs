using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Owns the dock widgets: builds them into the strip (eagerly or lazily on first enable), applies order and
/// visibility from settings, supports drag-and-drop reordering, and drives a shared 1 s tick.
/// </summary>
public sealed class WidgetCoordinator : IDisposable
{
    public sealed record WidgetItem(string Id, string LabelKey, DockWidget Widget, Func<DockSettings, bool> IsEnabled);

    private sealed class WidgetSlot(
        string id,
        string labelKey,
        Func<DockWidget> factory,
        Func<DockSettings, bool> isEnabled)
    {
        public string Id { get; } = id;
        public string LabelKey { get; } = labelKey;
        public Func<DockWidget> Factory { get; } = factory;
        public Func<DockSettings, bool> IsEnabled { get; } = isEnabled;
        public DockWidget? Widget { get; set; }
    }

    public static readonly string[] DefaultWidgetOrder =
    [
        "Media",
        "Desktops",
        "SystemStats",
        "Focus",
        "Weather",
        "Audio",
        "QuickSettings",
        "Tray",
        "Clock"
    ];

    public static string? GetLabelKey(string id) => id switch
    {
        "Media" => "WidgetMedia",
        "Desktops" => "WidgetDesktops",
        "SystemStats" => "WidgetSystemStats",
        "Focus" => "WidgetFocus",
        "Weather" => "WidgetWeather",
        "Audio" => "WidgetAudio",
        "QuickSettings" => "WidgetQuickSettings",
        "Tray" => "WidgetTray",
        "Clock" => "WidgetClock",
        _ => null
    };

    public static bool HandlesWidget(string id) =>
        DefaultWidgetOrder.Contains(id, StringComparer.OrdinalIgnoreCase);

    private sealed record WidgetDragPayload(string WidgetId);

    private readonly Panel _strip;
    private readonly Action<string> _reportError;
    private readonly bool _lazy;
    private readonly List<WidgetSlot> _slots;
    private readonly DispatcherTimer _timer;
    private readonly WidgetHoverCoordinator _hoverCoordinator;
    private readonly Dictionary<DockWidget, double> _displacedWidgets = [];
    private Point _dragStartPoint;
    private DockWidget? _draggedWidget;
    private DockWidget? _activeDraggedWidget;
    private bool _animationsEnabled = true;
    private bool _anyActive;
    private bool _suspended;
    private bool _disposed;

    public Action<IReadOnlyList<string>>? OrderChanged { get; set; }
    public event Action<string>? CloseApplicationRequested;
    public event Action<string>? TerminateApplicationRequested;
    public event Action<bool>? HoverActiveChanged
    {
        add => _hoverCoordinator.HoverActiveChanged += value;
        remove => _hoverCoordinator.HoverActiveChanged -= value;
    }
    public IReadOnlyList<string> CurrentOrder => _slots.Select(s => s.Id).ToArray();
    public bool IsHoverActive => _hoverCoordinator.IsHoverActive;

    private static bool IsCatalogEnabled(DockSettings settings, string id) =>
        settings.Widgets?.Contains(id, StringComparer.OrdinalIgnoreCase) == true;

    public WidgetCoordinator(
        Panel strip,
        Dispatcher dispatcher,
        Action<string> reportError,
        bool lazy = false,
        Func<DockWidget, bool>? canExpandInline = null,
        Action? onHoverLayoutChanged = null)
    {
        _strip = strip;
        _reportError = reportError;
        _lazy = lazy;
        _hoverCoordinator = new WidgetHoverCoordinator(
            dispatcher,
            canExpandInline ?? (_ => false),
            _ => onHoverLayoutChanged?.Invoke());
        _slots =
        [
            new("Media", "WidgetMedia", static () => new MediaWidget(), static s => IsCatalogEnabled(s, "Media")),
            new("Desktops", "WidgetDesktops", static () => new DesktopsWidget(), static s => IsCatalogEnabled(s, "Desktops")),
            new("SystemStats", "WidgetSystemStats", static () => new SystemStatsWidget(), static s => IsCatalogEnabled(s, "SystemStats")),
            new("Focus", "WidgetFocus", static () => new FocusWidget(), static s => IsCatalogEnabled(s, "Focus")),
            new("Weather", "WidgetWeather", static () => new WeatherWidget(), static s => IsCatalogEnabled(s, "Weather")),
            new("Audio", "WidgetAudio", static () => new AudioWidget(), static s => IsCatalogEnabled(s, "Audio")),
            new("QuickSettings", "WidgetQuickSettings", static () => new QuickSettingsWidget(), static s => IsCatalogEnabled(s, "QuickSettings")),
            new("Tray", "WidgetTray", CreateTrayWidget, static s => IsCatalogEnabled(s, "Tray")),
            new("Clock", "WidgetClock", static () => new ClockWidget(), static s => IsCatalogEnabled(s, "Clock") || IsCatalogEnabled(s, "Date"))
        ];

        if (!_lazy)
        {
            foreach (var slot in _slots)
            {
                var widget = CreateWidget(slot);
                slot.Widget = widget;
                _strip.Children.Add(widget);
                _hoverCoordinator.Attach(widget);
            }
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
    }

    private DockWidget CreateTrayWidget()
    {
        var tray = new TrayWidget();
        tray.CloseApplicationRequested += OnTrayCloseApplicationRequested;
        tray.TerminateApplicationRequested += OnTrayTerminateApplicationRequested;
        return tray;
    }

    private void OnTrayCloseApplicationRequested(string path) => CloseApplicationRequested?.Invoke(path);
    private void OnTrayTerminateApplicationRequested(string path) => TerminateApplicationRequested?.Invoke(path);

    private DockWidget CreateWidget(WidgetSlot slot)
    {
        var widget = slot.Factory();
        widget.ReportError = _reportError;
        widget.AllowDrop = true;
        widget.PreviewMouseLeftButtonDown += Widget_PreviewMouseLeftButtonDown;
        widget.PreviewMouseLeftButtonUp += Widget_PreviewMouseLeftButtonUp;
        widget.PreviewMouseMove += Widget_PreviewMouseMove;
        widget.DragOver += Widget_DragOver;
        widget.DragLeave += Widget_DragLeave;
        widget.Drop += Widget_Drop;
        _hoverCoordinator.Attach(widget);
        return widget;
    }

    private void DestroyWidget(WidgetSlot slot)
    {
        var widget = slot.Widget;
        if (widget is null) return;
        slot.Widget = null;
        _hoverCoordinator.Detach(widget);

        if (_draggedWidget == widget) _draggedWidget = null;
        if (_activeDraggedWidget == widget) _activeDraggedWidget = null;
        _displacedWidgets.Remove(widget);

        if (widget is TrayWidget tray)
        {
            tray.CloseApplicationRequested -= OnTrayCloseApplicationRequested;
            tray.TerminateApplicationRequested -= OnTrayTerminateApplicationRequested;
        }

        widget.PreviewMouseLeftButtonDown -= Widget_PreviewMouseLeftButtonDown;
        widget.PreviewMouseLeftButtonUp -= Widget_PreviewMouseLeftButtonUp;
        widget.PreviewMouseMove -= Widget_PreviewMouseMove;
        widget.DragOver -= Widget_DragOver;
        widget.DragLeave -= Widget_DragLeave;
        widget.Drop -= Widget_Drop;
        widget.ReportError = null;
        _strip.Children.Remove(widget);
        widget.Dispose();
    }

    public void Apply(DockSettings settings)
    {
        if (_disposed) return;

        _animationsEnabled = settings.AnimationsEnabled;
        if (settings.WidgetOrder is { Count: > 0 } order)
        {
            SortSlots(order);
        }

        var anyActive = false;
        var stripChanged = false;
        foreach (var slot in _slots)
        {
            if (slot.IsEnabled(settings))
            {
                if (slot.Widget is null)
                {
                    slot.Widget = CreateWidget(slot);
                    stripChanged = true;
                }

                slot.Widget.ApplySettings(settings);
                slot.Widget.Activate();
                anyActive = true;
            }
            else if (slot.Widget is not null)
            {
                if (_lazy)
                {
                    DestroyWidget(slot);
                    stripChanged = true;
                }
                else
                {
                    slot.Widget.ApplySettings(settings);
                    slot.Widget.Deactivate();
                }
            }
        }

        if (stripChanged || !StripMatchesSlotOrder())
        {
            SyncStripChildren();
        }

        _anyActive = anyActive;
        UpdateTimerState(tickNow: _anyActive && !_suspended);
    }

    public void SetSuspended(bool suspended)
    {
        if (_disposed || _suspended == suspended) return;
        _suspended = suspended;
        UpdateTimerState(tickNow: _anyActive && !_suspended);
    }

    private void UpdateTimerState(bool tickNow)
    {
        if (_anyActive && !_suspended)
        {
            if (tickNow) Timer_Tick(null, EventArgs.Empty);
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void ApplyOrder(IReadOnlyList<string> order)
    {
        SortSlots(order);
        if (!StripMatchesSlotOrder())
        {
            SyncStripChildren();
        }
    }

    private void SortSlots(IReadOnlyList<string> order)
    {
        var orderMap = new Dictionary<string, int>(order.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            orderMap[order[i]] = i;
        }

        _slots.Sort((a, b) =>
        {
            var indexA = orderMap.TryGetValue(a.Id, out var ia) ? ia : int.MaxValue;
            var indexB = orderMap.TryGetValue(b.Id, out var ib) ? ib : int.MaxValue;
            return indexA.CompareTo(indexB);
        });
    }

    private bool StripMatchesSlotOrder()
    {
        var expectedIndex = 0;
        var children = _strip.Children;
        foreach (var slot in _slots)
        {
            if (slot.Widget is null) continue;
            if (expectedIndex >= children.Count || !ReferenceEquals(children[expectedIndex], slot.Widget))
            {
                return false;
            }
            expectedIndex++;
        }

        return expectedIndex == children.Count;
    }

    private void SyncStripChildren()
    {
        _strip.Children.Clear();
        foreach (var slot in _slots)
        {
            if (slot.Widget is not null)
            {
                _strip.Children.Add(slot.Widget);
            }
        }
    }

    public void ReorderWidgets(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _slots.Count || newIndex < 0 || newIndex >= _slots.Count || oldIndex == newIndex)
        {
            return;
        }

        var slot = _slots[oldIndex];
        _slots.RemoveAt(oldIndex);
        _slots.Insert(newIndex, slot);

        if (!StripMatchesSlotOrder())
        {
            SyncStripChildren();
        }

        var newOrder = _slots.Select(w => w.Id).ToArray();
        OrderChanged?.Invoke(newOrder);
    }

    internal static (ScaleTransform Scale, TranslateTransform Translate) GetWidgetTransforms(DockWidget widget)
    {
        if (widget.RenderTransform is TransformGroup { IsFrozen: false, IsSealed: false } group)
        {
            var s = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            var t = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (s is { IsFrozen: false, IsSealed: false } && t is { IsFrozen: false, IsSealed: false })
            {
                return (s, t);
            }
        }

        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        widget.RenderTransform = new TransformGroup { Children = { scale, translate } };
        widget.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, translate);
    }

    private void AnimateWidgetDrag(DockWidget widget, string widgetId)
    {
        widget.Opacity = 0.55;
        _activeDraggedWidget = widget;
        var animate = _animationsEnabled && SystemParameters.ClientAreaAnimation;
        var (scale, translate) = GetWidgetTransforms(widget);
        var animDuration = TimeSpan.FromMilliseconds(130);
        var easeOut = animate ? new CubicEase { EasingMode = EasingMode.EaseOut } : null;

        if (animate)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, animDuration) { EasingFunction = easeOut });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, animDuration) { EasingFunction = easeOut });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, animDuration) { EasingFunction = easeOut });
        }

        try
        {
            DragDrop.DoDragDrop(widget, new WidgetDragPayload(widgetId), DragDropEffects.Move);
        }
        finally
        {
            _activeDraggedWidget = null;
            widget.Opacity = 1.0;
            ResetWidgetDisplacements();
            if (animate)
            {
                var resetScale = new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut };
                resetScale.Completed += (_, _) =>
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                };
                var resetY = new DoubleAnimation(0, animDuration) { EasingFunction = easeOut };
                resetY.Completed += (_, _) => translate.BeginAnimation(TranslateTransform.YProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, resetScale);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, resetScale);
                translate.BeginAnimation(TranslateTransform.YProperty, resetY);
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
                translate.Y = 0;
            }
        }
    }

    private void UpdateWidgetDisplacement(DockWidget targetWidget, DragEventArgs e)
    {
        if (ReferenceEquals(targetWidget, _activeDraggedWidget)) return;
        var pos = e.GetPosition(targetWidget);
        var shift = pos.X > targetWidget.ActualWidth / 2 ? -10.0 : 10.0;
        if (_displacedWidgets.TryGetValue(targetWidget, out var currentShift) && Math.Abs(currentShift - shift) < 0.01)
        {
            return;
        }

        _displacedWidgets[targetWidget] = shift;
        var (_, translate) = GetWidgetTransforms(targetWidget);
        if (_animationsEnabled && SystemParameters.ClientAreaAnimation)
        {
            var anim = new DoubleAnimation(shift, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            translate.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = shift;
        }
    }

    private void ResetWidgetDisplacement(DockWidget widget)
    {
        if (!_displacedWidgets.Remove(widget)) return;
        var (_, translate) = GetWidgetTransforms(widget);
        if (_animationsEnabled && SystemParameters.ClientAreaAnimation)
        {
            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (_, _) =>
            {
                if (!_displacedWidgets.ContainsKey(widget))
                    translate.BeginAnimation(TranslateTransform.XProperty, null);
            };
            translate.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = 0;
        }
    }

    private void ResetWidgetDisplacements()
    {
        foreach (var widget in _displacedWidgets.Keys)
        {
            var (_, translate) = GetWidgetTransforms(widget);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = 0;
        }
        _displacedWidgets.Clear();
    }

    private void AnimateWidgetDropSettle(DockWidget widget)
    {
        if (!_animationsEnabled || !SystemParameters.ClientAreaAnimation) return;
        var (scale, _) = GetWidgetTransforms(widget);
        var settleScale = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(200),
            KeyFrames =
            {
                new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(0.98, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new CubicEase { EasingMode = EasingMode.EaseOut })
            }
        };
        settleScale.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, settleScale);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleScale);
    }

    private void Widget_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && IsInteractiveControl(dep))
        {
            _draggedWidget = null;
            return;
        }

        if (sender is DockWidget widget)
        {
            _dragStartPoint = e.GetPosition(_strip);
            _draggedWidget = widget;
        }
    }

    internal static bool IsInteractiveControl(DependencyObject dep)
    {
        var current = dep;
        while (current is not null)
        {
            if (current is Slider or Thumb or TextBoxBase)
            {
                return true;
            }
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            else if (current is FrameworkContentElement fce)
            {
                current = fce.Parent;
            }
            else
            {
                break;
            }
        }
        return false;
    }

    private void Widget_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedWidget = null;
    }

    private void Widget_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedWidget is null || sender is not DockWidget)
        {
            return;
        }

        var currentPoint = e.GetPosition(_strip);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var targetWidget = _draggedWidget;
            _draggedWidget = null;
            var widgetId = _slots.FirstOrDefault(w => ReferenceEquals(w.Widget, targetWidget))?.Id;
            if (widgetId is null) return;

            if (Mouse.Captured is UIElement captured)
            {
                captured.ReleaseMouseCapture();
            }
            e.Handled = true;
            AnimateWidgetDrag(targetWidget, widgetId);
        }
    }

    private void Widget_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(WidgetDragPayload)) && sender is DockWidget targetWidget)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            UpdateWidgetDisplacement(targetWidget, e);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void Widget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is DockWidget widget)
        {
            ResetWidgetDisplacement(widget);
        }
    }

    private void Widget_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(WidgetDragPayload)) is WidgetDragPayload payload
            && sender is DockWidget targetWidget)
        {
            var oldIndex = _slots.FindIndex(w => string.Equals(w.Id, payload.WidgetId, StringComparison.OrdinalIgnoreCase));
            var newIndex = _slots.FindIndex(w => ReferenceEquals(w.Widget, targetWidget));

            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                ResetWidgetDisplacements();
                ReorderWidgets(oldIndex, newIndex);
                AnimateWidgetDropSettle(targetWidget);
            }
        }
        ResetWidgetDisplacements();
        e.Handled = true;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        foreach (var slot in _slots) slot.Widget?.Tick(now);
    }

    /// <summary>
    /// Computes which Auto-mode widgets should run in compact capsule state so the widget island fits alongside apps
    /// without clipping off-screen, using a hysteresis margin to avoid compact/full oscillation.
    /// </summary>
    public bool UpdateAdaptiveDensity(
        double maxTotalWidth,
        double appsContentWidth,
        DockSettings settings,
        double hysteresisMargin = 32d,
        double widgetScale = 1.0)
    {
        if (_disposed || maxTotalWidth <= 0) return false;

        var scale = double.IsFinite(widgetScale) && widgetScale > 0 ? widgetScale : 1.0;
        var changed = false;
        var autoCandidates = new List<DockWidget>();
        double fixedWidgetsWidth = 0;

        foreach (var slot in _slots)
        {
            if (slot.Widget is not { Visibility: Visibility.Visible } widget) continue;
            var mode = settings.WidgetDensityOverrides is not null
                && settings.WidgetDensityOverrides.TryGetValue(slot.Id, out var overrideMode)
                    ? overrideMode
                    : settings.GlobalWidgetDensity;

            if (!widget.SupportsCompactMode || mode == WidgetDensityMode.Full)
            {
                if (widget.IsCompact)
                {
                    widget.SetCompactState(false);
                    changed = true;
                }
                fixedWidgetsWidth += (widget.IsHoverExpanded ? widget.FullWidthEstimate : CurrentWidth(widget)) * scale;
            }
            else if (mode == WidgetDensityMode.Compact)
            {
                if (!widget.IsCompact)
                {
                    widget.SetCompactState(true);
                    changed = true;
                }
                fixedWidgetsWidth += (widget.IsHoverExpanded ? widget.FullWidthEstimate : widget.CompactWidthEstimate) * scale;
            }
            else
            {
                autoCandidates.Add(widget);
            }
        }

        if (autoCandidates.Count == 0) return changed;

        // Start from each Auto widget's resting state, then shrink high-priority savings first or un-shrink with hysteresis.
        var compactMap = new Dictionary<DockWidget, bool>(autoCandidates.Count);
        double autoTotal = 0;
        foreach (var w in autoCandidates)
        {
            compactMap[w] = w.IsCompact;
            autoTotal += (w.IsCompact ? w.CompactWidthEstimate : w.FullWidthEstimate) * scale;
        }

        var budgetForWidgets = maxTotalWidth - appsContentWidth;
        if (fixedWidgetsWidth + autoTotal > budgetForWidgets)
        {
            foreach (var w in autoCandidates.OrderBy(w => w.ShrinkPriority))
            {
                if (fixedWidgetsWidth + autoTotal <= budgetForWidgets) break;
                if (compactMap[w]) continue;
                compactMap[w] = true;
                autoTotal -= Math.Max(0, w.FullWidthEstimate - w.CompactWidthEstimate) * scale;
            }
        }
        else
        {
            // Expand back in reverse shrink order only if extra hysteresis margin is genuinely free.
            foreach (var w in autoCandidates.OrderByDescending(w => w.ShrinkPriority))
            {
                if (!compactMap[w]) continue;
                var delta = Math.Max(0, w.FullWidthEstimate - w.CompactWidthEstimate) * scale;
                if (fixedWidgetsWidth + autoTotal + delta + hysteresisMargin <= budgetForWidgets)
                {
                    compactMap[w] = false;
                    autoTotal += delta;
                }
            }
        }

        foreach (var (w, shouldCompact) in compactMap)
        {
            if (w.IsCompact != shouldCompact)
            {
                w.SetCompactState(shouldCompact);
                changed = true;
            }
        }

        return changed;

        static double CurrentWidth(DockWidget w) => w.IsCompact ? w.CompactWidthEstimate : w.FullWidthEstimate;
    }

    public bool TryCompactNextAutoWidget(DockSettings settings)
    {
        if (_disposed) return false;
        foreach (var slot in _slots
            .Where(s => s.Widget is { Visibility: Visibility.Visible, SupportsCompactMode: true, IsCompact: false })
            .OrderBy(s => s.Widget!.ShrinkPriority))
        {
            var mode = settings.WidgetDensityOverrides is not null
                && settings.WidgetDensityOverrides.TryGetValue(slot.Id, out var overrideMode)
                    ? overrideMode
                    : settings.GlobalWidgetDensity;
            if (mode != WidgetDensityMode.Auto) continue;
            slot.Widget!.SetCompactState(true);
            return true;
        }
        return false;
    }

    public void CollapseHover() => _hoverCoordinator.CollapseActive();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hoverCoordinator.Dispose();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        foreach (var slot in _slots)
        {
            DestroyWidget(slot);
        }
    }
}
