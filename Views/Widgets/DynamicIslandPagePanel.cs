using System.Windows;
using System.Windows.Controls;

namespace OpenTaskbar.Views.Widgets;

public sealed class DynamicIslandPagePanel : Panel
{
    public static readonly DependencyProperty HorizontalOffsetProperty = DependencyProperty.Register(
        nameof(HorizontalOffset), typeof(double), typeof(DynamicIslandPagePanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double HorizontalOffset
    {
        get => (double)GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pageWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : 620;
        var pageHeight = double.IsFinite(availableSize.Height) ? availableSize.Height : 50;
        var childSize = new Size(Math.Max(0, pageWidth), Math.Max(0, pageHeight));
        foreach (UIElement child in Children)
        {
            child.Measure(childSize);
        }

        return new Size(pageWidth, pageHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var pageWidth = finalSize.Width;
        var childSize = new Size(Math.Max(0, pageWidth), Math.Max(0, finalSize.Height));
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(childSize);
            var desired = child.DesiredSize;
            var width = Math.Min(childSize.Width, desired.Width);
            var height = Math.Min(childSize.Height, desired.Height);
            var left = index * pageWidth + (pageWidth - width) / 2 + HorizontalOffset;
            var top = Math.Max(0, (finalSize.Height - height) / 2);
            child.Arrange(new Rect(left, top, width, height));
        }

        return finalSize;
    }
}
