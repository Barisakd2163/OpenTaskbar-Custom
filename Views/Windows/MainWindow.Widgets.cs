using System.Windows;
using OpenTaskbar.Widgets;

namespace OpenTaskbar;

/// <summary>Widget area sizing: widgets that exceed their share of the dock move into an overflow flyout.</summary>
public partial class MainWindow
{
    private const double WidgetOverflowButtonWidth = 30;

    private readonly DockFlyout _overflowFlyout = new();

    private void InitializeWidgetOverflow()
    {
        _widgetCoordinator.AvailabilityChanged += WidgetCoordinator_AvailabilityChanged;
        // A widget inside the overflow flyout opens its own flyout from the overflow button, because its own anchor
        // lives in a popup that closes when another flyout opens.
        DockFlyout.AnchorResolver = anchor => _widgetCoordinator.OverflowPanel.IsAncestorOf(anchor) ? WidgetOverflowButton : anchor;
    }

    private void DisposeWidgetOverflow()
    {
        _widgetCoordinator.AvailabilityChanged -= WidgetCoordinator_AvailabilityChanged;
        DockFlyout.AnchorResolver = null;
        _overflowFlyout.Close();
    }

    private void WidgetCoordinator_AvailabilityChanged()
    {
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void WidgetOverflowButton_Click(object sender, RoutedEventArgs e) =>
        _overflowFlyout.Toggle(WidgetOverflowButton, () => _widgetCoordinator.OverflowPanel, _preferences.Current.AnimationsEnabled);

    /// <summary>Places widgets within their share of the dock and returns whether the widget area shows anything.</summary>
    private bool FitWidgetArea(double availableContentWidth)
    {
        var budget = Math.Max(WidgetOverflowButtonWidth, availableContentWidth * WidgetOverflowPlanner.BudgetRatio);
        if (_widgetCoordinator.FitOverflow(budget, WidgetOverflowButtonWidth, Profile.WidgetScale)) _overflowFlyout.Close();
        WidgetOverflowButton.Visibility = _widgetCoordinator.HasOverflow ? Visibility.Visible : Visibility.Collapsed;
        var hasWidgets = _widgetCoordinator.HasOverflow
            || WidgetStrip.Children.OfType<UIElement>().Any(child => child.Visibility == Visibility.Visible);
        WidgetIsland.Visibility = hasWidgets ? Visibility.Visible : Visibility.Collapsed;
        return hasWidgets;
    }
}
