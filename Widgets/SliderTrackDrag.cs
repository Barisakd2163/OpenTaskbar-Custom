using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Lets a Slider with IsMoveToPointEnabled="True" be dragged continuously from any point on its
/// track: the initial press jumps the value to the cursor, layout updates the thumb to that point,
/// and the same mouse press is forwarded to the thumb to begin dragging immediately.
/// </summary>
public static class SliderTrackDrag
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SliderTrackDrag),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly MouseButtonEventHandler PreviewDownHandler = OnSliderPreviewMouseLeftButtonDown;

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, PreviewDownHandler);
        if (e.NewValue is true)
        {
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, PreviewDownHandler, handledEventsToo: true);
        }
    }

    private static void OnSliderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsMoveToPointEnabled) return;
        if (IsInsideThumb(e.OriginalSource as DependencyObject)) return;

        if (slider.Template?.FindName("PART_Track", slider) is not Track { Thumb: { } thumb }) return;

        // IsMoveToPointEnabled updates slider.Value in the Slider's own PreviewMouseLeftButtonDown
        // before our handledEventsToo handler runs. Flush layout so the thumb sits at the new value
        // before handing it the press, otherwise the thumb drags from its old position.
        slider.UpdateLayout();

        var thumbPress = new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb
        };
        thumb.RaiseEvent(thumbPress);
    }

    private static bool IsInsideThumb(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Thumb) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
