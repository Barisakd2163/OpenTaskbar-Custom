using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Widgets;

internal static class WidgetViewChecks
{
    public static int Run()
    {
        Exception? failure = null;
        var checks = 0;
        var thread = new Thread(() =>
        {
            try { checks = RunOnDispatcher(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return checks;
    }

    private static int RunOnDispatcher()
    {
        var checks = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            foreach (var resource in new[] { "Theme", "ContextMenus", "WidgetControls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/OpenTaskbar;component/Design/{resource}.xaml", UriKind.Relative)
                });
            ThemeManager.Apply(AppTheme.Dark);
            UiText.Current.SetLanguage(AppLanguage.Turkish);

            void Check(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                checks++;
            }

            var clock = new TestClock();
            var timer = new CountdownDockWidget(clock);
            var timerHost = new ContentControl { Content = timer };
            Layout(timerHost);
            var timerTile = RequiredChild<TimedWidgetTile>(timerHost);
            var action = RequiredName<Button>(timerTile, "QuickActionButton");
            var details = RequiredName<Button>(timerTile, "DetailsButton");
            var detailRequests = 0;
            timerHost.AddHandler(TimedWidgetTile.DetailsRequestedEvent,
                new RoutedEventHandler((_, _) => detailRequests++));
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(detailRequests == 1 && !timer.IsRunning, "An unconfigured tile must open details instead of starting.");
            Check(AutomationProperties.GetName(action) == UiText.Get("WidgetSetDuration"), "The unconfigured action must have an accessible configure label.");
            Check(RequiredName<TextBlock>(timerTile, "ValueText").Text == UiText.Get("WidgetSetDuration"), "A blank duration must have an explicit visible prompt.");

            timer.MinutesText = "1";
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(timerHost);
            Check(timer.IsRunning && detailRequests == 1, "A configured quick action must start without opening details.");
            Check(AutomationProperties.GetName(action) == UiText.Get("TimedWidgetPause"), "The running action must have an accessible pause label.");
            clock.Advance(TimeSpan.FromSeconds(15));
            timer.Refresh();
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(timer.State == CountdownState.Paused && timer.Value == "00:00:45", "The tile must pause the same countdown model.");
            details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(detailRequests == 2, "The value area must open details independently of the quick action.");
            Check(Descendants<TextBox>(timerTile).Count() == 0, "The dock tile must contain no duration form.");
            Check(Descendants<Button>(timerTile).Count() == 2, "The dock tile must offer one detail target and one quick action.");
            Check(action.ActualWidth >= 36 && action.ActualHeight >= 36, "The quick action must keep its pointer target.");
            Check(timerHost.DesiredSize.Width < 200 && timerHost.DesiredSize.Height < 60, "The compact timer must fit within the dock shelf.");

            var panel = new TimedWidgetPanel { DataContext = timer };
            var frame = new WidgetPanelFrame { Heading = UiText.Get("WidgetCountdown"), Body = panel };
            Layout(frame);
            var duration = Descendants<TextBox>(panel).Single();
            Check(duration.IsReadOnly, "Paused duration must remain locked until reset.");
            var panelButtons = Descendants<Button>(panel).ToArray();
            panelButtons.Single(button => Equals(button.Content, UiText.Get("TimedWidgetReset")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(frame);
            Check(timer.State == CountdownState.Ready && !duration.IsReadOnly, "Panel reset must unlock the configured duration.");
            duration.Text = "0";
            duration.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Layout(frame);
            Check(timer.HasValidationError && !RequiredName<Button>(panel, "PrimaryActionButton").IsEnabled,
                "Invalid panel input must disable Start through the bound model.");
            Check(Descendants<TextBlock>(panel).Any(text => text.Text == timer.StatusText), "The validation error must be visible in the panel.");
            duration.Text = "1";
            duration.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            RequiredName<Button>(panel, "PrimaryActionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(frame);
            Check(timer.IsRunning && RequiredName<TextBlock>(timerTile, "ValueText").Text == "00:01:00",
                "Panel actions and tile display must share one timer state.");

            var stopwatch = new StopwatchDockWidget(clock);
            stopwatch.Refresh();
            var stopwatchHost = new ContentControl { Content = stopwatch };
            Layout(stopwatchHost);
            var stopwatchTile = RequiredChild<TimedWidgetTile>(stopwatchHost);
            RequiredName<Button>(stopwatchTile, "QuickActionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            clock.Advance(TimeSpan.FromHours(26));
            stopwatch.Refresh();
            timer.Refresh();
            Layout(stopwatchHost);
            Layout(timerHost);
            Check(stopwatch.Value == "26:00:00" && timer.State == CountdownState.Finished, "Both compact views must retain independent timer behavior.");
            Check(timerTile.Title == UiText.Get("CountdownFinished"), "The completed tile must display a non-color completion label.");

            UiText.Current.SetLanguage(AppLanguage.English);
            Layout(timerHost);
            Layout(stopwatchHost);
            Check(stopwatchTile.Title == "Stopwatch" && timerTile.Title == "Time is up", "Tile titles must update when language changes.");
            Check(AutomationProperties.GetName(RequiredName<Button>(stopwatchTile, "QuickActionButton")) == "Pause",
                "Quick action names must update when language changes.");
            UiText.Current.SetLanguage(AppLanguage.Turkish);

            var closeRequests = 0;
            frame.CloseRequested += (_, _) => closeRequests++;
            Descendants<Button>(frame).Single(button => AutomationProperties.GetName(button) == UiText.Get("Close"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(closeRequests == 1, "The shared panel frame must expose its close request.");
            var stopwatchPanel = new TimedWidgetPanel { DataContext = stopwatch };
            Layout(stopwatchPanel);
            Check(!Descendants<TextBox>(stopwatchPanel).Any(), "The stopwatch panel must not instantiate a countdown form.");
            frame.MaxWidth = 220;
            Layout(frame);
            Check(frame.ActualWidth <= 220 && Descendants<Button>(panel).All(button =>
            {
                var left = button.TranslatePoint(new Point(), frame).X;
                return button.ActualWidth > 0 && left >= 0 && left + button.ActualWidth <= frame.ActualWidth;
            }),
                "A narrow panel must retain reachable action controls.");
            frame.MaxWidth = double.PositiveInfinity;

            timer.Reset();
            timer.Toggle();
            clock.Advance(TimeSpan.FromSeconds(15));
            timer.Refresh();
            stopwatch.Reset();
            stopwatch.Toggle();
            clock.Advance(TimeSpan.FromSeconds(24));
            stopwatch.Refresh();
            timer.Refresh();
            var directory = Path.Combine(Path.GetTempPath(), "OpenTaskbarWidgetComponents");
            Directory.CreateDirectory(directory);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                var strip = new StackPanel { Orientation = Orientation.Horizontal };
                strip.Children.Add(new ContentControl { Content = stopwatch });
                strip.Children.Add(new ContentControl { Content = timer });
                var preview = new StackPanel { Margin = new Thickness(16) };
                preview.Children.Add(strip);
                preview.Children.Add(new WidgetPanelFrame
                {
                    Heading = UiText.Get("WidgetCountdown"), Body = new TimedWidgetPanel { DataContext = timer },
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0)
                });
                var surface = new Border { Background = (Brush)app.Resources["SettingsBackground"], Child = preview };
                Layout(surface);
                Render(surface, Path.Combine(directory, $"timed-widgets-{theme.ToString().ToLowerInvariant()}.png"));
            }
            return checks;
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(), element.DesiredSize));
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static T RequiredName<T>(FrameworkElement owner, string name) where T : FrameworkElement =>
        owner.FindName(name) is T element ? element : throw new InvalidOperationException($"Missing control {name}.");
    private static T RequiredChild<T>(DependencyObject owner) where T : DependencyObject =>
        Descendants<T>(owner).FirstOrDefault() ?? throw new InvalidOperationException($"Missing {typeof(T).Name}.");
    private static IEnumerable<T> Descendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
