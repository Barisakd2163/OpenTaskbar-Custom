using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenTaskbar.Services;

namespace OpenTaskbar.Views;

/// <summary>
/// Attached behavior for <c>WidgetCardBorder</c> cards so every Border-rooted widget gets a per-instance
/// themed hover and pressed background (solid or gradient) that tracks live theme and WidgetOpacity changes
/// without double-stacking or leaking static event subscriptions.
/// </summary>
public static class WidgetCardFeedback
{
    private static readonly Duration PressInDuration = new(TimeSpan.FromMilliseconds(80));
    private static readonly Duration PressOutDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration HoverOutDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IEasingFunction Ease = CreateEase();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(WidgetCardFeedback),
        new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty PropagatesCardPressProperty = DependencyProperty.RegisterAttached(
        "PropagatesCardPress",
        typeof(bool),
        typeof(WidgetCardFeedback),
        new PropertyMetadata(false));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(BorderState),
        typeof(WidgetCardFeedback),
        new PropertyMetadata(null));

    private sealed class BorderState
    {
        public bool IsPressed;
        public bool IsThemeSubscribed;
        public EventHandler? ThemeChangedHandler;
    }

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    public static bool GetPropagatesCardPress(DependencyObject obj) => (bool)obj.GetValue(PropagatesCardPressProperty);
    public static void SetPropagatesCardPress(DependencyObject obj, bool value) => obj.SetValue(PropagatesCardPressProperty, value);

    internal static bool IsCardPressed(Border border) =>
        border.GetValue(StateProperty) is BorderState { IsPressed: true };

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;

        if ((bool)e.NewValue)
            Attach(border);
        else
            Detach(border);
    }

    private static void Attach(Border border)
    {
        if (border.GetValue(StateProperty) is BorderState) return;

        var state = new BorderState();
        state.ThemeChangedHandler = (_, _) => OnThemeChanged(border, state);
        border.SetValue(StateProperty, state);

        border.Loaded += Border_Loaded;
        border.Unloaded += Border_Unloaded;
        border.IsEnabledChanged += Border_IsEnabledChanged;
        border.PreviewMouseLeftButtonDown += Border_PreviewMouseLeftButtonDown;
        border.PreviewMouseLeftButtonUp += Border_PreviewMouseLeftButtonUp;
        border.MouseLeave += Border_MouseLeave;
        border.LostMouseCapture += Border_LostMouseCapture;

        SyncBackground(border, state, animate: false);
        if (border.IsLoaded) SubscribeTheme(state);
    }

    private static void Detach(Border border)
    {
        if (border.GetValue(StateProperty) is not BorderState state) return;

        UnsubscribeTheme(state);
        border.Loaded -= Border_Loaded;
        border.Unloaded -= Border_Unloaded;
        border.IsEnabledChanged -= Border_IsEnabledChanged;
        border.PreviewMouseLeftButtonDown -= Border_PreviewMouseLeftButtonDown;
        border.PreviewMouseLeftButtonUp -= Border_PreviewMouseLeftButtonUp;
        border.MouseLeave -= Border_MouseLeave;
        border.LostMouseCapture -= Border_LostMouseCapture;
        state.IsPressed = false;
        border.ClearValue(StateProperty);
    }

    private static void SubscribeTheme(BorderState state)
    {
        if (state.IsThemeSubscribed || state.ThemeChangedHandler is null) return;
        ThemeManager.ThemeChanged += state.ThemeChangedHandler;
        state.IsThemeSubscribed = true;
    }

    private static void UnsubscribeTheme(BorderState state)
    {
        if (!state.IsThemeSubscribed || state.ThemeChangedHandler is null) return;
        ThemeManager.ThemeChanged -= state.ThemeChangedHandler;
        state.IsThemeSubscribed = false;
    }

    private static void Border_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border || border.GetValue(StateProperty) is not BorderState state) return;
        SubscribeTheme(state);
        SyncBackground(border, state, animate: false);
    }

    private static void Border_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border || border.GetValue(StateProperty) is not BorderState state) return;
        UnsubscribeTheme(state);
        state.IsPressed = false;
    }

    private static void Border_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border border || border.GetValue(StateProperty) is not BorderState state) return;
        if (e.NewValue is false && state.IsPressed)
            ReleasePress(border, state, keepHover: false);
    }

    private static void Border_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.GetValue(StateProperty) is not BorderState state) return;
        if (!border.IsEnabled || IsNestedInteractiveControl(e.OriginalSource as DependencyObject, border)) return;

        state.IsPressed = true;
        ApplyPressedVisual(border);
    }

    private static void Border_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.GetValue(StateProperty) is not BorderState state) return;
        if (!state.IsPressed) return;

        ReleasePress(border, state, keepHover: border.IsMouseOver);
    }

    private static void Border_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || border.GetValue(StateProperty) is not BorderState state) return;
        if (!state.IsPressed) return;

        ReleasePress(border, state, keepHover: false);
    }

    private static void Border_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || border.GetValue(StateProperty) is not BorderState state) return;
        if (!state.IsPressed || border.IsMouseOver) return;

        ReleasePress(border, state, keepHover: false);
    }

    private static void OnThemeChanged(Border border, BorderState state)
    {
        if (!border.Dispatcher.CheckAccess())
        {
            _ = border.Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(border.GetValue(StateProperty), state))
                    SyncBackground(border, state, animate: false);
            });
            return;
        }

        SyncBackground(border, state, animate: false);
    }

    private static void ApplyPressedVisual(Border border)
    {
        var pressedSource = ResolveMaterial(border, "WidgetPressedMaterial", "WidgetCardPressed");
        if (border.Background is SolidColorBrush currentSolid && !currentSolid.IsFrozen
            && pressedSource is SolidColorBrush pressedSolid)
        {
            currentSolid.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(pressedSolid.Color, PressInDuration) { EasingFunction = Ease });
            if (currentSolid.Opacity < 0.99)
            {
                currentSolid.BeginAnimation(Brush.OpacityProperty,
                    new DoubleAnimation(1.0, PressInDuration) { EasingFunction = Ease });
            }
            return;
        }

        border.Background = CloneUnfrozen(pressedSource, opacity: 1.0);
    }

    private static void ReleasePress(Border border, BorderState state, bool keepHover)
    {
        state.IsPressed = false;
        var hoverSource = ResolveMaterial(border, "WidgetHoverMaterial", "WidgetCardHover");

        if (border.Background is SolidColorBrush currentSolid && !currentSolid.IsFrozen
            && hoverSource is SolidColorBrush hoverSolid)
        {
            currentSolid.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(hoverSolid.Color, PressOutDuration) { EasingFunction = Ease });
            if (!keepHover)
            {
                currentSolid.BeginAnimation(Brush.OpacityProperty,
                    new DoubleAnimation(0.0, HoverOutDuration) { EasingFunction = Ease });
            }
            return;
        }

        var hoverClone = CloneUnfrozen(hoverSource, opacity: keepHover ? 1.0 : border.Background?.Opacity ?? 0.0);
        border.Background = hoverClone;
        if (!keepHover && hoverClone.Opacity > 0.0)
        {
            hoverClone.BeginAnimation(Brush.OpacityProperty,
                new DoubleAnimation(0.0, HoverOutDuration) { EasingFunction = Ease });
        }
    }

    private static void SyncBackground(Border border, BorderState state, bool animate)
    {
        var targetSource = state.IsPressed
            ? ResolveMaterial(border, "WidgetPressedMaterial", "WidgetCardPressed")
            : ResolveMaterial(border, "WidgetHoverMaterial", "WidgetCardHover");

        if (border.Background is SolidColorBrush currentSolid && !currentSolid.IsFrozen
            && targetSource is SolidColorBrush targetSolid)
        {
            if (animate)
            {
                currentSolid.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(targetSolid.Color, PressOutDuration) { EasingFunction = Ease });
            }
            else
            {
                currentSolid.BeginAnimation(SolidColorBrush.ColorProperty, null);
                currentSolid.Color = targetSolid.Color;
            }
            return;
        }

        var targetOpacity = state.IsPressed || border.IsMouseOver ? 1.0 : 0.0;
        border.Background = CloneUnfrozen(targetSource, targetOpacity);
    }

    private static Brush ResolveMaterial(Border border, string primaryKey, string fallbackKey) =>
        border.TryFindResource(primaryKey) as Brush
        ?? border.TryFindResource(fallbackKey) as Brush
        ?? Brushes.Transparent;

    private static Brush CloneUnfrozen(Brush source, double opacity)
    {
        var clone = source.CloneCurrentValue();
        clone.Opacity = opacity;
        return clone;
    }

    private static bool IsNestedInteractiveControl(DependencyObject? source, Border card)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, card))
        {
            if (current is ButtonBase or Slider or Thumb or TextBoxBase or ComboBox)
            {
                if (!GetPropagatesCardPress(current))
                    return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
