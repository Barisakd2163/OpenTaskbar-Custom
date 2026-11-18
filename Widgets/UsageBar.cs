using System.Windows;
using System.Windows.Media;

namespace OpenTaskbar.Widgets;

/// <summary>
/// A two-tone horizontal bar: the used share on the left and the remaining share on the right.
/// </summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceValue));

    public static readonly DependencyProperty UsedBrushProperty = DependencyProperty.Register(
        nameof(UsedBrush), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RemainingBrushProperty = DependencyProperty.Register(
        nameof(RemainingBrush), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Used share from 0 to 1; non-finite values draw as empty.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush UsedBrush { get => (Brush)GetValue(UsedBrushProperty); set => SetValue(UsedBrushProperty, value); }
    public Brush RemainingBrush { get => (Brush)GetValue(RemainingBrushProperty); set => SetValue(RemainingBrushProperty, value); }

    private static object CoerceValue(DependencyObject d, object value) =>
        value is double v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.0;

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var radius = ActualHeight / 2;
        context.DrawRoundedRectangle(RemainingBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
        // A used share below the bar height would render as a dot wider than its value, so it stays visible but minimal.
        var used = Value <= 0 ? 0 : Math.Max(ActualHeight, ActualWidth * Value);
        if (used > 0)
            context.DrawRoundedRectangle(UsedBrush, null, new Rect(0, 0, Math.Min(used, ActualWidth), ActualHeight), radius, radius);
    }
}