using System.Windows;
using System.Windows.Controls;

namespace OpenTaskbar.Views.Widgets;

public partial class WidgetCatalogItem : UserControl
{
    public static readonly RoutedEvent InstallationRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(InstallationRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(WidgetCatalogItem));

    public WidgetCatalogItem() => InitializeComponent();
    public event RoutedEventHandler InstallationRequested
    {
        add => AddHandler(InstallationRequestedEvent, value);
        remove => RemoveHandler(InstallationRequestedEvent, value);
    }
    private void Installation_Click(object sender, RoutedEventArgs e) =>
        RaiseEvent(new RoutedEventArgs(InstallationRequestedEvent, this));
}
