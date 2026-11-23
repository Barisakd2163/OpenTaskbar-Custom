using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenTaskbar.Views;

/// <summary>
/// Sidebar navigation layout for Settings: pins primary tabs at the top and secondary
/// application tabs (Preferences, About) at the bottom, separated by a hairline divider.
/// </summary>
public sealed class SidebarNavPanel : Panel
{
    public static readonly DependencyProperty BottomCountProperty =
        DependencyProperty.Register(nameof(BottomCount), typeof(int), typeof(SidebarNavPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public int BottomCount
    {
        get => (int)GetValue(BottomCountProperty);
        set => SetValue(BottomCountProperty, value);
    }

    private const double ItemGap = 3;
    private const double SectionGap = 12;
    private double _dividerY = double.NaN;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 185 : availableSize.Width;
        var totalHeight = 0.0;
        var count = InternalChildren.Count;
        var split = Math.Max(0, count - Math.Max(0, BottomCount));
        var topVisible = 0;
        var bottomVisible = 0;

        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(width, double.PositiveInfinity));
            totalHeight += child.DesiredSize.Height;
            if (i < split)
            {
                if (topVisible++ > 0) totalHeight += ItemGap;
            }
            else
            {
                if (bottomVisible++ > 0) totalHeight += ItemGap;
            }
        }

        if (topVisible > 0 && bottomVisible > 0) totalHeight += SectionGap * 2 + 1;

        var desiredHeight = double.IsInfinity(availableSize.Height)
            ? totalHeight
            : Math.Max(totalHeight, availableSize.Height);
        return new Size(width, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        var split = Math.Max(0, count - Math.Max(0, BottomCount));
        var width = finalSize.Width;
        var y = 0.0;
        var hasTopVisible = false;

        for (var i = 0; i < split; i++)
        {
            var child = InternalChildren[i];
            if (child.Visibility == Visibility.Collapsed) continue;
            hasTopVisible = true;
            var h = child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, width, h));
            y += h + ItemGap;
        }

        var bottomHeight = 0.0;
        var bottomVisible = 0;
        for (var i = split; i < count; i++)
        {
            var child = InternalChildren[i];
            if (child.Visibility == Visibility.Collapsed) continue;
            bottomHeight += child.DesiredSize.Height;
            if (bottomVisible++ > 0) bottomHeight += ItemGap;
        }

        if (hasTopVisible && bottomVisible > 0)
        {
            var bottomStart = Math.Max(y + SectionGap * 2, finalSize.Height - bottomHeight);
            _dividerY = bottomStart - SectionGap;
            var by = bottomStart;
            for (var i = split; i < count; i++)
            {
                var child = InternalChildren[i];
                if (child.Visibility == Visibility.Collapsed) continue;
                var h = child.DesiredSize.Height;
                child.Arrange(new Rect(0, by, width, h));
                by += h + ItemGap;
            }
        }
        else
        {
            _dividerY = double.NaN;
            for (var i = split; i < count; i++)
            {
                var child = InternalChildren[i];
                if (child.Visibility == Visibility.Collapsed) continue;
                var h = child.DesiredSize.Height;
                child.Arrange(new Rect(0, y, width, h));
                y += h + ItemGap;
            }
        }

        InvalidateVisual();
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (double.IsNaN(_dividerY) || ActualWidth <= 0) return;
        if (TryFindResource("StrokeSubtle") is not Brush stroke) return;
        var pen = new Pen(stroke, 1);
        var y = Math.Floor(_dividerY) + 0.5;
        dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
    }
}
