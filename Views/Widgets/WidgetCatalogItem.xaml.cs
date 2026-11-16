using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Models;

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
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DockWidgetOption { IsInstalled: false }) RequestInstallationChange();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DockWidgetOption { IsInstalled: true }) RequestInstallationChange();
    }

    private void RequestInstallationChange() => RaiseEvent(new RoutedEventArgs(InstallationRequestedEvent, this));
}
