using System;
using System.Windows;
using System.Windows.Controls;

namespace OpenTaskbar.Views;

/// <summary>
/// A responsive multi-column grid panel that adapts the number of columns to the available width
/// and stretches each item to fill its column evenly.
/// </summary>
public class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(
            nameof(MinItemWidth),
            typeof(double),
            typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(230.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var count = InternalChildren.Count;
        if (count == 0) return new Size(0, 0);

        var availableWidth = constraint.Width;
        if (double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            availableWidth = !double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
        }
        if (double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            availableWidth = 700;
        }

        var minWidth = MinItemWidth > 0 ? MinItemWidth : 230.0;
        var columns = Math.Max(1, (int)Math.Floor(availableWidth / minWidth));
        var itemWidth = availableWidth / columns;
        var rows = (count + columns - 1) / columns;
        var rowHeights = new double[rows];

        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            var row = i / columns;
            if (child.DesiredSize.Height > rowHeights[row])
            {
                rowHeights[row] = child.DesiredSize.Height;
            }
        }

        var totalHeight = 0.0;
        for (var r = 0; r < rows; r++)
        {
            totalHeight += rowHeights[r];
        }

        return new Size(availableWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var count = InternalChildren.Count;
        if (count == 0) return arrangeSize;

        var availableWidth = arrangeSize.Width;
        var minWidth = MinItemWidth > 0 ? MinItemWidth : 230.0;
        var columns = Math.Max(1, (int)Math.Floor(availableWidth / minWidth));
        var itemWidth = availableWidth / columns;
        var rows = (count + columns - 1) / columns;
        var rowHeights = new double[rows];

        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            var row = i / columns;
            if (child.DesiredSize.Height > rowHeights[row])
            {
                rowHeights[row] = child.DesiredSize.Height;
            }
        }

        var y = 0.0;
        for (var r = 0; r < rows; r++)
        {
            var rowHeight = rowHeights[r];
            for (var c = 0; c < columns; c++)
            {
                var index = r * columns + c;
                if (index >= count) break;
                var child = InternalChildren[index];
                child.Arrange(new Rect(c * itemWidth, y, itemWidth, rowHeight));
            }
            y += rowHeight;
        }

        return new Size(availableWidth, y);
    }
}
