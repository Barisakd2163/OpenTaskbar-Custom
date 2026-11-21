using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public partial class MainWindow
{
    private readonly record struct MagnificationTarget(Button Button, FrameworkElement Icon, double RestCenter, double BaseSize);

    private static readonly Point IconCenterOrigin = new(0.5, 0.5);
    private static readonly Point IconBottomOrigin = new(0.5, 1.0);

    private readonly HashSet<FrameworkElement> _currentMagnifiedBuffer = [];
    private readonly List<MagnificationTarget> _magnificationTargets = [];
    private bool _magnificationTargetsDirty = true;
    private double _availableExpansion;
    private double _effectiveMaxScale = DockMetrics.HoverScale;
    // Frozen for the duration of one hover session so a mid-hover budget recapture cannot make the same hover
    // bloom small first and oversized later (or push icons past the dock edge).
    private double _sessionMaxScale = DockMetrics.HoverScale;
    private readonly Dictionary<FrameworkElement, double> _magnificationScaleTargets = [];
    private readonly Dictionary<FrameworkElement, bool> _hoverIconStates = [];
    private bool _magnificationActive;
    private readonly HashSet<FrameworkElement> _resetIconBuffer = [];
    private double _lastMagnificationPointerAxis;
    private bool _hasMagnificationPointer;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, BitmapSource> _restingIcons = new();

    // Cache the transform pair per icon so the per-frame render loop never runs a LINQ
    // OfType/FirstOrDefault walk or allocates a TransformGroup after the first frame.
    // Static so the (frozen-transform recovery) contract stays callable without a MainWindow instance.
    private sealed class IconTransforms(ScaleTransform scale, TranslateTransform translate)
    {
        public ScaleTransform Scale { get; } = scale;
        public TranslateTransform Translate { get; } = translate;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, IconTransforms> s_iconTransforms = new();

    internal void InvalidateMagnificationTargets()
    {
        // A hover in progress uses render transforms only, so rest geometry stays valid; avoid a
        // TranslatePoint/FontVisualTree walk while the pointer is moving over the dock.
        if (_magnificationActive) return;
        _magnificationTargetsDirty = true;
    }

    private void RebuildMagnificationTargets()
    {
        // While a hover is active the button geometry never changes (only render transforms do), so
        // cached rest centers remain exact. At rest we re-measure for resizes, scrolling and edge switches.
        var atRest = !_magnificationActive;
        Dictionary<Button, MagnificationTarget>? previousRest = null;
        if (!atRest)
        {
            previousRest = new Dictionary<Button, MagnificationTarget>(_magnificationTargets.Count);
            foreach (var target in _magnificationTargets) previousRest[target.Button] = target;
        }

        _magnificationTargets.Clear();
        if (DockHost is null || !IsLoaded) return;

        var fallbackCell = Profile.Cell;
        foreach (var button in GetAllDockButtons())
        {
            if (button.Visibility != Visibility.Visible) continue;
            if (button.ActualWidth <= 0 || button.ActualHeight <= 0) continue;
            if (!button.IsDescendantOf(DockHost)) continue;
            if (GetButtonIcon(button) is not { } icon) continue;
            // Native shell buttons (Start, Search, Task View, tray, Show Desktop, Recycle Bin) are Windows'
            // own surfaces. Magnifying them makes the dock fight the native flyouts they open, so keep the
            // fisheye for application icons and stacks only.
            if (IsNativeShellButton(button)) continue;

            if (previousRest is not null && previousRest.TryGetValue(button, out var known) && ReferenceEquals(known.Icon, icon))
            {
                _magnificationTargets.Add(known);
                continue;
            }

            var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), DockHost);
            var restCenter = IsVertical ? center.Y : center.X;
            var baseSize = IsVertical ? button.ActualHeight : button.ActualWidth;
            if (!double.IsFinite(baseSize) || baseSize <= 0) baseSize = fallbackCell;
            _magnificationTargets.Add(new MagnificationTarget(button, icon, restCenter, baseSize));
        }

        // Never sample the expansion budget from geometry that magnification itself has grown or an in-flight
        // width animation is still moving; that feedback is what made the same hover bloom to different sizes.
        if (atRest)
        {
            CaptureMagnificationExpansionBudget();
            if (_hasMagnificationPointer) UpdateMagnificationTargetsForPointer(_lastMagnificationPointerAxis);
        }
        _magnificationTargetsDirty = false;
    }

    /// <summary>
    /// Rebuilds the spring goals from the last known pointer position. Called on mouse move and whenever the
    /// cap/radius changes, so a stationary pointer still gets the peak scale that is currently affordable.
    /// </summary>
    private void UpdateMagnificationTargetsForPointer(double pointerAxis)
    {
        if (_magnificationTargets.Count == 0) return;
        var fraction = DockFisheyeLayout.ComputeExpansionFraction(
            Profile.Cell, DockMetrics.HoverScale, _availableExpansion);
        var radius = DockFisheyeLayout.InfluenceRadiusForExpansion(Profile.Cell, fraction);

        _currentMagnifiedBuffer.Clear();
        _magnificationScaleTargets.Clear();
        foreach (var target in _magnificationTargets)
        {
            var scale = DockFisheyeLayout.ComputeTargetScale(pointerAxis - target.RestCenter, radius, _sessionMaxScale);
            _magnificationScaleTargets[target.Icon] = scale;
            if (scale > HoverIconThreshold) _currentMagnifiedBuffer.Add(target.Icon);
        }

        foreach (var icon in _magnifiedIcons)
        {
            if (!_magnificationScaleTargets.ContainsKey(icon)) _magnificationScaleTargets[icon] = 1.0;
        }
        _magnifiedIcons.Clear();
        foreach (var icon in _currentMagnifiedBuffer) _magnifiedIcons.Add(icon);
    }

    /// <summary>Live peak magnification scale coupled to available slot expansion room.</summary>
    internal double CurrentMagnificationMaxScale => _effectiveMaxScale;

    private void UpdateEffectiveMaxScale()
    {
        var profile = Profile;
        var appIconSize = TryFindResource("DockApplicationIconSize") as double? ?? profile.Icon;
        var visualIconSize = Math.Max(profile.Icon, appIconSize);
        _effectiveMaxScale = DockFisheyeLayout.ComputeEffectiveMaxScale(
            profile.Cell, visualIconSize, DockMetrics.HoverScale, _availableExpansion);
    }

    private void CaptureMagnificationExpansionBudget()
    {
        // Dock geometry never changes during magnification any more (render transforms only),
        // so the resting budget is always safe to sample.
        var margin = DockSurface.Margin;
        if (IsVertical)
        {
            var availableHeight = _dockArea.Height > 0 ? _dockArea.Height : ActualHeight;
            var maximumSurfaceHeight = Math.Max(1.0, availableHeight - margin.Top - margin.Bottom);
            _availableExpansion = DockScrollViewer.ScrollableHeight <= 0.5 && DockSurface.ActualHeight > 0
                ? Math.Max(0.0, maximumSurfaceHeight - DockSurface.ActualHeight)
                : 0.0;
            UpdateEffectiveMaxScale();
            return;
        }

        var availableWidth = _dockArea.Width > 0 ? _dockArea.Width : ActualWidth;
        var maximumSurfaceWidth = Math.Max(1.0, availableWidth - margin.Left - margin.Right);
        var restWidth = !double.IsNaN(DockSurface.Width) && DockSurface.Width > 0
            ? DockSurface.Width : DockSurface.ActualWidth;
        if (!IsFullWidth && _dockWidthOverride is null && DockScrollViewer.ScrollableWidth <= 0.5
            && restWidth > 0 && _dockArea.Width > 0)
        {
            _availableExpansion = Math.Max(0.0, maximumSurfaceWidth - restWidth);
        }
        else if (DockScrollViewer.ScrollableWidth <= 0.5 && double.IsFinite(DockScrollViewer.MaxWidth)
            && DockContents.ActualWidth > 0)
        {
            _availableExpansion = Math.Max(0.0, DockScrollViewer.MaxWidth - DockContents.ActualWidth);
        }
        else
        {
            _availableExpansion = 0.0;
        }

        UpdateEffectiveMaxScale();
    }

    internal static (ScaleTransform Scale, TranslateTransform Translate) GetIconTransforms(FrameworkElement icon)
    {
        if (s_iconTransforms.TryGetValue(icon, out var cached)
            && !cached.Scale.IsFrozen && !cached.Scale.IsSealed
            && !cached.Translate.IsFrozen && !cached.Translate.IsSealed)
        {
            return (cached.Scale, cached.Translate);
        }

        ScaleTransform newScale;
        TranslateTransform newTranslate;
        if (icon.RenderTransform is TransformGroup { IsFrozen: false, IsSealed: false } group
            && group.Children.Count == 2
            && group.Children[0] is ScaleTransform existingScale
            && group.Children[1] is TranslateTransform existingTranslate
            && !existingScale.IsFrozen && !existingScale.IsSealed
            && !existingTranslate.IsFrozen && !existingTranslate.IsSealed)
        {
            newScale = existingScale;
            newTranslate = existingTranslate;
        }
        else
        {
            newScale = new ScaleTransform();
            newTranslate = new TranslateTransform();
            var newGroup = new TransformGroup();
            newGroup.Children.Add(newScale);
            newGroup.Children.Add(newTranslate);
            icon.RenderTransform = newGroup;
            icon.RenderTransformOrigin = IconCenterOrigin;
        }

        s_iconTransforms.AddOrUpdate(icon, new IconTransforms(newScale, newTranslate));
        return (newScale, newTranslate);
    }

    private static FrameworkElement? GetButtonIcon(Button button) =>
        button.Content as FrameworkElement ?? (FrameworkElement?)FindVisualChild<Image>(button) ?? FindVisualChild<System.Windows.Shapes.Path>(button);

    /// <summary>
    /// Buttons that launch Windows-owned UI. Uses both direct references (fallback dock order) and the free-form
    /// order tokens, so a shell button keeps behaving the same after drag-and-drop reordering.
    /// </summary>
    private bool IsNativeShellButton(Button button)
    {
        if (ReferenceEquals(button, StartButton) || ReferenceEquals(button, SearchButton)
            || ReferenceEquals(button, TaskViewButton) || ReferenceEquals(button, TrayButton)
            || ReferenceEquals(button, ShowDesktopButton) || ReferenceEquals(button, RecycleBinButton))
        {
            return true;
        }

        foreach (var (token, element) in _dockItemElements)
        {
            if (!ShellDockTokens.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;
            if (ReferenceEquals(element, button)
                || ReferenceEquals(FindVisualChild<Button>(element), button))
            {
                return true;
            }
        }
        return false;
    }

    private IEnumerable<Button> GetAllDockButtons()
    {
        if (_dockItemElements.Count > 0)
        {
            foreach (var (token, element) in _dockItemElements)
            {
                if (token.StartsWith("widget:", StringComparison.OrdinalIgnoreCase)) continue;
                if (element is Button btn)
                {
                    yield return btn;
                }
                else if (FindVisualChild<Button>(element) is { } button)
                {
                    yield return button;
                }
            }
            yield break;
        }

        yield return StartButton;

        // Profiles filter the pinned view, so look containers up by item rather than by index.
        foreach (var app in _pinnedApps)
        {
            if (PinnedApps.ItemContainerGenerator.ContainerFromItem(app) is DependencyObject container
                && FindVisualChild<Button>(container) is { } button)
            {
                yield return button;
            }
        }

        foreach (var app in _runningApps)
        {
            if (RunningApps.ItemContainerGenerator.ContainerFromItem(app) is DependencyObject container
                && FindVisualChild<Button>(container) is { } button)
            {
                yield return button;
            }
        }

        foreach (var button in GetExtraDockButtons()) yield return button;
    }

    private bool IsPointerOverNonMagnifiedZone(DependencyObject? source)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, DockSurface))
        {
            if (ReferenceEquals(current, WidgetIsland)
                || ReferenceEquals(current, LeftResizeThumb)
                || ReferenceEquals(current, RightResizeThumb)
                || ReferenceEquals(current, ScrollPrevOverlay)
                || ReferenceEquals(current, ScrollNextOverlay))
            {
                return true;
            }
            // Hovering a native shell button must not magnify its neighbours either.
            if (current is Button button && IsNativeShellButton(button)) return true;

            if (current is FrameworkElement fe)
            {
                foreach (var (token, element) in _dockItemElements)
                {
                    if (token.StartsWith("widget:", StringComparison.OrdinalIgnoreCase) && ReferenceEquals(element, fe))
                    {
                        return true;
                    }
                }
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void AnimateActivationFeedback(Button button)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var icon = GetButtonIcon(button);
        if (icon is null) return;
        var (_, translate) = GetIconTransforms(icon);

        var bounce = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(DockMetrics.LaunchBounceMs),
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(-DockMetrics.LaunchBounceHeight * BounceDirection, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
                    new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)),
                    new CubicEase { EasingMode = EasingMode.EaseIn }),
                new EasingDoubleKeyFrame(-5 * BounceDirection, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)),
                    new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DockMetrics.LaunchBounceMs)),
                    new CubicEase { EasingMode = EasingMode.EaseIn })
            }
        };
        translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, bounce);
    }

    private long _lastFisheyeMoveTicks;

    private void DockSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_preferences.Current.MagnificationEnabled || !_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
            return;

        // Pointer devices fire 80–125 Hz. Each handled move rebuilds spring targets and starts ~60 WPF
        // animations; at full rate that is thousands of animation clocks per second. 30 Hz is visually
        // indistinguishable (the animations themselves run at display rate on the composition thread)
        // and cuts the UI-thread work by ~3x.
        var ticks = Environment.TickCount64;
        if (ticks - _lastFisheyeMoveTicks < 33) return;
        _lastFisheyeMoveTicks = ticks;

        if (LeftResizeThumb.IsDragging || RightResizeThumb.IsDragging || DockScrollViewer.IsPanning)
            return;

        if (LeftResizeThumb.IsMouseOver || RightResizeThumb.IsMouseOver
            || ScrollPrevOverlay.IsMouseOver || ScrollNextOverlay.IsMouseOver
            || IsPointerOverNonMagnifiedZone(e.OriginalSource as DependencyObject))
        {
            ResetMagnification();
            return;
        }

        if (_magnificationTargetsDirty)
        {
            RebuildMagnificationTargets();
        }

        if (_magnificationTargets.Count == 0) return;

        var mousePos = e.GetPosition(DockHost);
        var mouseAxis = IsVertical ? mousePos.Y : mousePos.X;
        if (!_hasMagnificationPointer) _sessionMaxScale = _effectiveMaxScale;
        _hasMagnificationPointer = true;
        _lastMagnificationPointerAxis = mouseAxis;
        UpdateMagnificationTargetsForPointer(mouseAxis);

        ApplyFisheye();
    }

    // Smooth deceleration approximates the old spring without running a per-frame physics loop on the UI thread.
    // WPF interpolates these animations on the composition thread, so no layout/render invalidation happens here.
    private static readonly QuadraticEase FisheyeEase = new() { EasingMode = EasingMode.EaseOut };
    private static readonly TimeSpan FisheyeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FisheyeReturnDuration = TimeSpan.FromMilliseconds(260);

    /// <summary>
    /// Drives the entire fisheye through WPF animations. Called only from MouseMove (and from reset paths),
    /// so the UI thread does zero work per rendered frame: no spring integration, no Button.Width writes,
    /// no Measure/Arrange, no ZIndex churn, and no CompositionTarget.Rendering subscription.
    /// Slot breathing is expressed as a cumulative Translate offset instead of resizing buttons, which keeps
    /// hitboxes and indicator dots stable while neighbours visually slide apart.
    /// </summary>
    private void ApplyFisheye()
    {
        if (_magnificationTargetsDirty) RebuildMagnificationTargets();
        if (_magnificationTargets.Count == 0) return;

        _magnificationActive = true;
        var vertical = IsVertical;
        var liftSign = vertical ? (Edge == DockEdge.Left ? 1.0 : -1.0) : -1.0;
        var scaleOrigin = vertical ? IconCenterOrigin : IconBottomOrigin;
        var maxScale = _sessionMaxScale;
        var profile = Profile;
        var availableHeadroom = DockMetrics.Headroom + profile.VerticalPadding;

        var count = _magnificationTargets.Count;
        var scales = count <= 64 ? stackalloc double[count] : new double[count];
        var baseSizes = count <= 64 ? stackalloc double[count] : new double[count];
        var extras = count <= 64 ? stackalloc double[count] : new double[count];

        for (var i = 0; i < count; i++)
        {
            var target = _magnificationTargets[i];
            var desired = _magnificationScaleTargets.GetValueOrDefault(target.Icon, 1.0);
            var (scale, translate) = GetIconTransforms(target.Icon);
            if (target.Icon.RenderTransformOrigin != scaleOrigin) target.Icon.RenderTransformOrigin = scaleOrigin;

            AnimateDouble(scale, ScaleTransform.ScaleXProperty, desired, FisheyeDuration);
            AnimateDouble(scale, ScaleTransform.ScaleYProperty, desired, FisheyeDuration);

            // Swap bitmaps at the threshold with hysteresis (same as before), based on the target scale.
            var wasHover = _hoverIconStates.TryGetValue(target.Icon, out var state) && state;
            var wantHover = wasHover ? desired > 1.06 : desired > HoverIconThreshold;
            if (wantHover != wasHover)
            {
                _hoverIconStates[target.Icon] = wantHover;
                UseHoverIcon(target.Icon, wantHover);
            }

            scales[i] = desired;
            baseSizes[i] = target.BaseSize;
        }

        // Distribute slot breathing under the same expansion budget, then convert each extra into a
        // cumulative offset so icons slide apart symmetrically without changing any layout property.
        DockFisheyeLayout.ComputeSlotExtras(scales, baseSizes, _availableExpansion, extras);
        var cumulative = 0.0;
        for (var i = 0; i < count; i++)
        {
            var target = _magnificationTargets[i];
            var (_, translate) = GetIconTransforms(target.Icon);
            var lift = DockFisheyeLayout.ComputeSafeLift(scales[i], maxScale, profile.Icon, availableHeadroom);
            var offset = (cumulative + extras[i] * 0.5) + lift;
            cumulative += extras[i];

            var axis = vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
            var sign = vertical ? liftSign : -1.0;
            AnimateDouble(translate, axis, offset * sign, FisheyeDuration);
            var otherAxis = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
            AnimateDouble(translate, otherAxis, 0.0, FisheyeDuration);
        }
    }

    private static void AnimateDouble(Animatable target, System.Windows.DependencyProperty property, double value, TimeSpan duration)
    {
        if (Math.Abs((double)target.GetValue(property) - value) < 0.001) return;
        var animation = new DoubleAnimation(value, duration) { EasingFunction = FisheyeEase };
        target.BeginAnimation(property, animation);
    }

    /// <summary>No-op now: magnification never changes layout geometry, so nothing to restore.</summary>
    internal void ClearMagnificationLayoutOverrides() { }

    private void DockSurface_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
    }

    private void DockSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        NativeMemoryMethods.CancelIdleTrim();
        _autoHideController?.OnMouseEnterDock();
    }

    private void DockSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hasMagnificationPointer = false;
        ResetMagnification();
        _autoHideController?.OnMouseLeaveDock();
        NativeMemoryMethods.ScheduleIdleTrim(TimeSpan.FromSeconds(2));
    }

    private void DockEdgeTrigger_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseEnterEdge();
    private void DockEdgeTrigger_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseLeaveEdge();

    private void ResetMagnification(bool immediate = false)
    {
        _hasMagnificationPointer = false;
        _magnificationActive = false;
        _resetIconBuffer.Clear();
        foreach (var icon in _magnificationScaleTargets.Keys) _resetIconBuffer.Add(icon);
        foreach (var icon in _magnifiedIcons) _resetIconBuffer.Add(icon);
        foreach (var target in _magnificationTargets) _resetIconBuffer.Add(target.Icon);

        foreach (var icon in _resetIconBuffer)
        {
            var (scale, translate) = GetIconTransforms(icon);
            if (immediate)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.X = 0;
                translate.Y = 0;
            }
            else
            {
                AnimateDouble(scale, ScaleTransform.ScaleXProperty, 1.0, FisheyeReturnDuration);
                AnimateDouble(scale, ScaleTransform.ScaleYProperty, 1.0, FisheyeReturnDuration);
                AnimateDouble(translate, TranslateTransform.XProperty, 0.0, FisheyeReturnDuration);
                AnimateDouble(translate, TranslateTransform.YProperty, 0.0, FisheyeReturnDuration);
            }
            UseHoverIcon(icon, false);
        }

        _magnificationScaleTargets.Clear();
        _magnifiedIcons.Clear();
        _hoverIconStates.Clear();
        if (immediate) _magnificationTargetsDirty = true;
    }

    /// <summary>Scale above which an icon shows its larger variant instead of the 1:1 resting bitmap.</summary>
    private const double HoverIconThreshold = 1.08;

    /// <summary>
    /// Swaps a dock icon between its resting bitmap (drawn 1:1, sharp at scale 1) and a larger native variant
    /// (scaled down while magnified, so it stays sharp instead of blurring). The binding to the resting icon is kept:
    /// the variant is set with SetCurrentValue and the binding is re-read to restore.
    /// </summary>
    private void UseHoverIcon(FrameworkElement icon, bool magnified)
    {
        if ((icon as Image ?? FindVisualChild<Image>(icon)) is not { } image) return;
        if (magnified)
        {
            if (image.Source is not BitmapSource current
                || ApplicationIconProvider.GetHoverVariant(current) is not { } hover) return;
            _restingIcons.AddOrUpdate(image, current);
            image.SetCurrentValue(Image.SourceProperty, hover);
        }
        else if (_restingIcons.TryGetValue(image, out var resting))
        {
            _restingIcons.Remove(image);
            if (BindingOperations.GetBindingExpression(image, Image.SourceProperty) is { } binding) binding.UpdateTarget();
            else image.SetCurrentValue(Image.SourceProperty, resting);
        }
    }
}
