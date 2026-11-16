using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Models;

namespace OpenTaskbar.Views.Widgets;

public partial class TimedWidgetPanel : UserControl
{
    public TimedWidgetPanel() => InitializeComponent();

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ITimedDockWidget widget) widget.Toggle();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ITimedDockWidget widget) widget.Reset();
    }
}
