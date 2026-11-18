using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Widgets;

internal static class NetSpeedChecks
{
    public static int Run(Application app)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        CheckSpeedFormatting(Check);
        CheckCompactFormatting(Check);
        CheckBytesFormatting(Check);
        CheckWidgetLifecycleAndCalculations(Check);
        CheckHoverFlyoutAndInteractions(Check);

        return checks;
    }

    private static void CheckSpeedFormatting(Action<bool, string> check)
    {
        check(NetSpeedWidget.FormatSpeed(0) == "0 B/s", "0 bytes/s must format to '0 B/s'.");
        check(NetSpeedWidget.FormatSpeed(500) == "500 B/s", "500 bytes/s must format to '500 B/s'.");
        check(NetSpeedWidget.FormatSpeed(1024 * 45.4) == "45.4 KB/s", "45.4 KB/s must format with one decimal.");
        check(NetSpeedWidget.FormatSpeed(1024 * 450) == "450 KB/s", "450 KB/s must format without decimals.");
        check(NetSpeedWidget.FormatSpeed(1024 * 1024 * 12.8) == "12.8 MB/s", "12.8 MB/s must format with one decimal.");
        check(NetSpeedWidget.FormatSpeed(1024 * 1024 * 250) == "250 MB/s", "250 MB/s must format without decimals.");
        check(NetSpeedWidget.FormatSpeed(1024L * 1024 * 1024 * 2.5) == "2.50 GB/s", "2.5 GB/s must format with two decimals.");
    }

    private static void CheckCompactFormatting(Action<bool, string> check)
    {
        check(NetSpeedWidget.FormatCompactSpeed(0) == "0", "0 bytes compact must format to '0'.");
        check(NetSpeedWidget.FormatCompactSpeed(1024 * 350) == "350K", "350 KB compact must format to '350K'.");
        check(NetSpeedWidget.FormatCompactSpeed(1024 * 1024 * 4.5) == "4.5M", "4.5 MB compact must format to '4.5M'.");
        check(NetSpeedWidget.FormatCompactSpeed(1024 * 1024 * 35) == "35M", "35 MB compact must format to '35M'.");
        check(NetSpeedWidget.FormatCompactSpeed(1024L * 1024 * 1024 * 1.8) == "1.8G", "1.8 GB compact must format to '1.8G'.");
    }

    private static void CheckBytesFormatting(Action<bool, string> check)
    {
        check(NetSpeedWidget.FormatBytes(0) == "0 B", "0 bytes must format to '0 B'.");
        check(NetSpeedWidget.FormatBytes(500) == "500 B", "500 bytes must format to '500 B'.");
        check(NetSpeedWidget.FormatBytes(1024 * 50) == "50.0 KB", "50 KB must format to '50.0 KB'.");
        check(NetSpeedWidget.FormatBytes(1024 * 1024 * 150) == "150.0 MB", "150 MB must format to '150.0 MB'.");
        check(NetSpeedWidget.FormatBytes(1024L * 1024 * 1024 * 3) == "3.00 GB", "3 GB must format to '3.00 GB'.");
    }

    private static void CheckWidgetLifecycleAndCalculations(Action<bool, string> check)
    {
        var widget = new NetSpeedWidget();
        check(widget.SupportsCompactMode, "NetSpeedWidget must support compact mode.");
        check(widget.CompactWidthEstimate == 54, "Compact width estimate must be 54 DIP.");
        check(widget.FullWidthEstimate == 84, "Full width estimate must be 84 DIP.");

        long fakeReceived = 100_000_000;
        long fakeSent = 20_000_000;
        widget.NetworkReader = () => (fakeReceived, fakeSent, "TestAdapter");

        widget.Activate();
        check(widget.Visibility == Visibility.Visible, "Widget must become visible on activation.");

        // First tick sets baseline (speed = 0)
        widget.Tick(DateTime.Now);

        // Advance bytes by 10 MB received and 2 MB sent
        fakeReceived += 10 * 1024 * 1024;
        fakeSent += 2 * 1024 * 1024;
        widget.Tick(DateTime.Now.AddSeconds(1));

        check(widget.Content is Button, "Widget content must be a WidgetCardButton.");

        // Test compact mode
        widget.SetCompactState(true);
        check(widget.IsCompact, "Widget must reflect compact state.");

        widget.SetCompactState(false);
        check(!widget.IsCompact, "Widget must revert from compact state.");

        widget.Deactivate();
        check(widget.Visibility == Visibility.Collapsed, "Widget must collapse on deactivation.");
    }

    private static void CheckHoverFlyoutAndInteractions(Action<bool, string> check)
    {
        var widget = new NetSpeedWidget();
        var clicked = false;
        widget.OpenSettingsAction = () => clicked = true;

        var flyout = widget.BuildHoverFlyoutContent();
        check(flyout is StackPanel, "BuildHoverFlyoutContent must return a non-null StackPanel.");

        var panel = (StackPanel)flyout!;
        check(panel.Children.Count >= 2, "Flyout must include at least heading and metrics.");

        if (widget.Content is Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(clicked, "Clicking the card button must invoke the settings action.");
        }
    }
}
