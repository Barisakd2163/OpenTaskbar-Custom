using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenTaskbar;
using OpenTaskbar.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Services;
using OpenTaskbar.Views;
using OpenTaskbar.Widgets;
using System.Windows.Threading;

internal static class DockLayoutChecks
{
    public static int Run(Application app)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var minimum = (double)app.FindResource("DockMinimumShelfHeight");
        var headroom = (double)app.FindResource("DockHeadroom");
        foreach (var dpi in new uint[] { 96, 120, 144, 192 })
        foreach (var nativeHeight in new[] { 0d, 48d, 100d })
        {
            var metrics = DockLayoutMetrics.Calculate(minimum, nativeHeight, headroom, dpi);
            var nativePixels = (int)Math.Ceiling(nativeHeight * dpi / 96d);
            Check(metrics.ShelfPixels * 96d / dpi >= Math.Max(minimum, nativeHeight),
                "The reserved shelf must fit the dock and native taskbar at every supported scale.");
            Check(metrics.WindowPixels - metrics.ShelfPixels == metrics.HeadroomPixels,
                "Hover headroom must enlarge the window without enlarging the desktop reservation.");
            Check(nativePixels + metrics.AdditionalReservation(nativePixels, false) == metrics.ShelfPixels,
                "Existing native space plus the extra AppBar must reserve exactly one shelf.");
            Check(metrics.AdditionalReservation(0, false) == metrics.ShelfPixels,
                "When Explorer reserves no strip, OpenTaskbar must reserve the complete shelf.");
            Check(metrics.AdditionalReservation(metrics.ShelfPixels + 20, false) == 0,
                "A larger existing reservation must not receive another shelf.");
            Check(metrics.AdditionalReservation(nativePixels, true) == 0,
                "Auto-hide must release the additional desktop reservation.");
        }

        foreach (var width in new[] { 140d, 280d, 500d })
        {
            var iconSize = (double)app.FindResource("DockIconSize");
            var cellSize = (double)app.FindResource("DockCellSize");
            var icon = new Border
            {
                Width = iconSize, Height = iconSize, Background = Brushes.Cyan,
                RenderTransformOrigin = new Point(0.5, 0.95),
                RenderTransform = new TransformGroup
                {
                    Children = { new ScaleTransform(cellSize / iconSize, cellSize / iconSize), new TranslateTransform(0, -12) }
                }
            };
            var cell = new Grid { Width = cellSize, Height = cellSize };
            cell.Children.Add(icon);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Width = 800, Height = cellSize, Background = Brushes.Magenta };
            content.Children.Add(cell);
            var viewer = new DockScrollViewer
            {
                Style = (Style)app.FindResource("DockScrollViewerStyle"),
                HoverHeadroom = headroom, ContentInset = (Thickness)app.FindResource("DockScrollContentInset"), Content = content
            };
            var shelf = new Border
            {
                Padding = (Thickness)app.FindResource("DockPadding"), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 6), Child = viewer
            };
            var root = new Grid { Width = width, Height = minimum + headroom };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headroom) });
            root.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(shelf, 1);
            root.Children.Add(shelf);

            void Layout()
            {
                root.Measure(new Size(width, minimum + headroom));
                root.Arrange(new Rect(0, 0, width, minimum + headroom));
                root.UpdateLayout();
            }
            Color PixelAt(Point point, double scale)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale),
                    (int)Math.Ceiling(root.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(point.X * scale), (int)(point.Y * scale), 1, 1), pixel, 4, 0);
                return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            }

            Layout();
            var bounds = icon.TransformToAncestor(root).TransformBounds(new Rect(0, 0, iconSize, iconSize));
            var sample = new Point(bounds.X + bounds.Width / 2, Math.Ceiling(bounds.Top) + 2);
            Check(sample.Y >= 0 && sample.Y < headroom,
                "The fixture must exercise magnification and bounce above the visible shelf.");
            foreach (var scale in new[] { 1d, 2d })
                Check(PixelAt(sample, scale) == Colors.Cyan,
                    "The actual dock scroll template must paint the top of a magnified, bouncing icon.");
            var right = viewer.TranslatePoint(new Point(viewer.ActualWidth + 2, viewer.ActualHeight - 12), root);
            Check(PixelAt(right, 1) != Colors.Magenta, "Horizontal overflow must remain clipped beside the resize gutter.");

            viewer.HoverHeadroom = 0;
            Layout();
            var clippedBounds = icon.TransformToAncestor(root).TransformBounds(new Rect(0, 0, iconSize, iconSize));
            var clippedSample = new Point(clippedBounds.X + clippedBounds.Width / 2, Math.Ceiling(clippedBounds.Top) + 2);
            Check(PixelAt(clippedSample, 1) != Colors.Cyan,
                "The regression fixture must reproduce the old clipping when internal headroom is removed.");
            viewer.HoverHeadroom = headroom;
            Layout();
            viewer.ScrollToHorizontalOffset(80);
            Layout();
            Check(viewer.HorizontalOffset == 80 && viewer.VerticalOffset == 0,
                "Hover headroom must preserve native horizontal scrolling without vertical scrolling.");
        }
        var cellWidth = (double)app.FindResource("DockCellSize");
        var iconWidth = (double)app.FindResource("DockIconSize");
        var padding = (Thickness)app.FindResource("DockPadding");
        var stroke = (Thickness)app.FindResource("ControlStroke");
        var inset = (Thickness)app.FindResource("DockScrollContentInset");
        var chrome = padding.Left + padding.Right + stroke.Left + stroke.Right + inset.Left + inset.Right;
        var oneCellWidth = cellWidth + chrome;
        foreach (var width in new[] { oneCellWidth, cellWidth * 2 + chrome, 600d, 1000d })
        {
            var icons = Enumerable.Range(0, 10).Select(index => new Border
            {
                Width = iconWidth, Height = iconWidth, Background = Brushes.Cyan
            }).ToArray();
            var buttons = icons.Select((icon, index) => new Button
            {
                Style = (Style)app.FindResource("DockButton"), Content = icon,
                DataContext = new DockApp($"Test app {index}", $"test-{index}.exe")
            }).ToArray();
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var button in buttons) content.Children.Add(button);
            var viewer = new DockScrollViewer
            {
                Style = (Style)app.FindResource("DockScrollViewerStyle"), HoverHeadroom = headroom,
                ContentInset = inset, Content = content
            };
            var frame = new Border { Padding = padding, BorderThickness = stroke, Background = Brushes.Black, Child = viewer };
            var left = new Thumb { Style = (Style)app.FindResource("DockResizeThumb"), HorizontalAlignment = HorizontalAlignment.Left };
            var right = new Thumb { Style = (Style)app.FindResource("DockResizeThumb"), HorizontalAlignment = HorizontalAlignment.Right };
            var surface = new Grid { Width = width, Height = minimum };
            surface.Children.Add(frame);
            surface.Children.Add(left);
            surface.Children.Add(right);
            void Layout()
            {
                surface.Measure(new Size(width, minimum));
                surface.Arrange(new Rect(0, 0, width, minimum));
                surface.UpdateLayout();
            }
            bool IsWithin(DependencyObject? hit, DependencyObject target)
            {
                while (hit is not null)
                {
                    if (ReferenceEquals(hit, target)) return true;
                    hit = VisualTreeHelper.GetParent(hit);
                }
                return false;
            }
            Layout();
            Check(buttons.All(button => button.ActualWidth == cellWidth && button.ActualHeight == cellWidth)
                && icons.All(icon => icon.ActualWidth == iconWidth && icon.ActualHeight == iconWidth),
                "Narrowing the dock must preserve each application cell and icon size.");
            Check(viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden
                && viewer.Template.FindName("PART_HorizontalScrollBar", viewer) is null,
                "Overflow must remain scrollable without a visible scrollbar.");
            Check(IsWithin(VisualTreeHelper.HitTest(surface, new Point(0.5, minimum / 2))?.VisualHit, left)
                && IsWithin(VisualTreeHelper.HitTest(surface, new Point(width - 0.5, minimum / 2))?.VisualHit, right),
                "Resize hit targets must cover the actual outside borders rather than the content padding.");
            left.ApplyTemplate();
            var grip = left.Template.FindName("GripIndicator", left) as FrameworkElement;
            Check(grip is not null, "The resize thumb must provide a visual grip indicator.");
            if (width == oneCellWidth)
                Check(viewer.ViewportWidth == cellWidth, "The minimum dock width must expose exactly one complete application cell.");
            var widget = new ContentControl { Content = new OpenTaskbar.Models.DockWidget("Clock", "Clock") { Value = "14:32" } };
            var previousExtent = viewer.ExtentWidth;
            content.Children.Add(widget);
            Layout();
            Check(viewer.ExtentWidth > previousExtent
                && buttons.All(button => button.ActualWidth == cellWidth)
                && icons.All(icon => icon.ActualWidth == iconWidth),
                "Adding widgets must extend scrollable content without resizing application icons.");
            if (viewer.ScrollableWidth > 0)
            {
                Check(viewer.ScrollByWheel(-Mouse.MouseWheelDeltaForOneLine, ModifierKeys.Shift), "Shift+wheel must be handled for overflowing content.");
                Layout();
                Check(viewer.HorizontalOffset == cellWidth, "One wheel notch must move one application cell.");
                Check(viewer.ScrollByWheel(-Mouse.MouseWheelDeltaForOneLine / 4, ModifierKeys.Control), "Ctrl+wheel must support high-resolution deltas.");
                Layout();
                Check(viewer.HorizontalOffset == Math.Min(cellWidth * 1.25, viewer.ScrollableWidth), "High-resolution wheel deltas must preserve fractional cell movement.");
                Check(!viewer.ScrollByWheel(-120, ModifierKeys.None) && !viewer.ScrollByWheel(0, ModifierKeys.Shift),
                    "Plain or zero wheel input must not trigger modifier scrolling.");
                viewer.ScrollByWheel(int.MinValue, ModifierKeys.Control | ModifierKeys.Shift);
                Layout();
                Check(viewer.HorizontalOffset == viewer.ScrollableWidth && viewer.VerticalOffset == 0,
                    "Forward scrolling must stop at the content end without vertical movement.");
                viewer.ScrollByWheel(int.MaxValue, ModifierKeys.Shift);
                Layout();
                Check(viewer.HorizontalOffset == 0, "Backward scrolling must stop at the content start.");
            }
            else Check(!viewer.ScrollByWheel(-120, ModifierKeys.Control), "A dock that already fits must not consume modifier wheel scrolling.");
        }

        var testWindow = new Window { ShowActivated = false, ShowInTaskbar = false, Width = 10, Height = 10, WindowStyle = WindowStyle.None };
        var helper = new System.Windows.Interop.WindowInteropHelper(testWindow);
        helper.EnsureHandle();
        Check(MainWindow.ExcludeFromTaskSwitcher(helper.Handle), "Task switcher exclusion must successfully apply to windows.");
        var exStyle = MainWindow.GetWindowLongPtr(helper.Handle, -20);
        Check((exStyle & 0x00000080) != 0, "Task switcher exclusion must enforce WS_EX_TOOLWINDOW.");
        Check((exStyle & 0x00040000) == 0, "Task switcher exclusion must clear WS_EX_APPWINDOW.");
        testWindow.Close();

        var targetSize = new Size(48, 48);
        var popupSize = new Size(120, 26);
        DockFlyout.Edge = DockEdge.Bottom;
        var bottomPlacements = DockMetrics.PlaceDockTooltip(popupSize, targetSize, new Point(0, 0));
        Check(bottomPlacements.Length == 2, "Bottom dock tooltip must provide primary and fallback placements.");
        Check(bottomPlacements[0].Point.X == (targetSize.Width - popupSize.Width) / 2, "Bottom dock tooltip primary placement must be horizontally centered.");
        Check(bottomPlacements[0].Point.Y == -popupSize.Height - 8, "Bottom dock tooltip primary placement must sit 8 DIPs above the button.");
        Check(bottomPlacements[1].Point.X == (targetSize.Width - popupSize.Width) / 2, "Bottom dock tooltip fallback placement must be horizontally centered.");
        Check(bottomPlacements[1].Point.Y == targetSize.Height + 8, "Bottom dock tooltip fallback placement must sit 8 DIPs below the button.");

        DockFlyout.Edge = DockEdge.Left;
        var leftPlacements = DockMetrics.PlaceDockTooltip(popupSize, targetSize, new Point(0, 0));
        Check(leftPlacements[0].Point.X == targetSize.Width + 8, "Left edge primary placement must be to the right of the dock.");
        Check(leftPlacements[0].Point.Y == (targetSize.Height - popupSize.Height) / 2, "Left edge primary placement must be vertically centered.");

        DockFlyout.Edge = DockEdge.Right;
        var rightPlacements = DockMetrics.PlaceDockTooltip(popupSize, targetSize, new Point(0, 0));
        Check(rightPlacements[0].Point.X == -popupSize.Width - 8, "Right edge primary placement must be to the left of the dock.");
        Check(rightPlacements[0].Point.Y == (targetSize.Height - popupSize.Height) / 2, "Right edge primary placement must be vertically centered.");
        DockFlyout.Edge = DockEdge.Bottom;

        foreach (var size in new[] { DockSize.Compact, DockSize.Medium, DockSize.Large })
        foreach (var scale in new[] { 0.75, 1.0, 1.25, 1.5 })
        {
            var profile = DockMetrics.For(size, scale);
            Check(Math.Round(DockMetrics.WidgetDesignHeight * profile.WidgetScale) + 2 <= profile.Cell,
                $"Widget strip scale for {size}@{scale} must fit inside the dock cell height.");
            app.Resources["DockCellSize"] = profile.Cell;
            app.Resources["DockCellHeight"] = profile.Cell;
            app.Resources["DockIconSize"] = profile.Icon;
            var button = new Button { Style = (Style)app.FindResource("DockButton"), DataContext = new DockApp("App", "app.exe") { WindowCount = 1, IsForeground = true } };
            button.Measure(new Size(profile.Cell, profile.Cell));
            button.Arrange(new Rect(0, 0, profile.Cell, profile.Cell));
            button.UpdateLayout();
            Check(button.ActualWidth == profile.Cell && button.ActualHeight == profile.Cell,
                $"DockButton must dynamically resize to {profile.Cell} DIP for {size}@{scale}.");
        }
        app.Resources["DockCellSize"] = DockMetrics.CellSize;
        app.Resources["DockCellHeight"] = DockMetrics.CellSize;
        app.Resources["DockIconSize"] = DockMetrics.IconSize;

        // Verify that narrowing the dock with both applications and WidgetIsland inside DockScrollViewer keeps the first application visible.
        {
            var firstButton = new Button
            {
                Style = (Style)app.FindResource("DockButton"),
                Content = new Border { Width = iconWidth, Height = iconWidth, Background = Brushes.Cyan },
                DataContext = new DockApp("First App", "first.exe")
            };
            var secondButton = new Button
            {
                Style = (Style)app.FindResource("DockButton"),
                Content = new Border { Width = iconWidth, Height = iconWidth, Background = Brushes.Cyan },
                DataContext = new DockApp("Second App", "second.exe")
            };
            var dockContents = new StackPanel { Orientation = Orientation.Horizontal };
            dockContents.Children.Add(firstButton);
            dockContents.Children.Add(secondButton);
            var widgetIsland = new Border { Width = 600, Height = 38, Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Orange };
            var layoutRoot = new Grid();
            layoutRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layoutRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(dockContents, 0);
            Grid.SetColumn(widgetIsland, 1);
            layoutRoot.Children.Add(dockContents);
            layoutRoot.Children.Add(widgetIsland);
            var viewer = new DockScrollViewer
            {
                Style = (Style)app.FindResource("DockScrollViewerStyle"),
                HoverHeadroom = headroom,
                ContentInset = inset,
                Content = layoutRoot
            };
            var frame = new Border { Padding = padding, BorderThickness = stroke, Child = viewer };
            var surface = new Grid { Width = oneCellWidth, Height = minimum };
            surface.Children.Add(frame);
            surface.Measure(new Size(oneCellWidth, minimum));
            surface.Arrange(new Rect(0, 0, oneCellWidth, minimum));
            surface.UpdateLayout();
            var firstBounds = firstButton.TransformToAncestor(surface).TransformBounds(new Rect(0, 0, firstButton.ActualWidth, firstButton.ActualHeight));
            Check(firstBounds.Left >= 0 && firstBounds.Right <= oneCellWidth + 0.5 && firstButton.ActualWidth == cellWidth,
                "Narrowing the dock to one cell must keep the first application visible instead of hiding it behind widgets.");
            Check(viewer.ScrollableWidth > 500, "Overflowing widgets must remain reachable by scrolling to the right.");
        }

        // Verify dynamic widget label localization
        var widgetService = new DockWidgetService(TimeProvider.System);
        var testWidgets = widgetService.CreateWidgets(DockWidgetService.SupportedIds.ToArray());
        UiText.Current.SetLanguage(AppLanguage.Turkish);
        DockWidgetService.Refresh(testWidgets, AppLanguage.Turkish);
        var clockTr = testWidgets.First(w => w.Id == DockWidgetService.ClockId);
        var stopwatchTr = testWidgets.First(w => w.Id == DockWidgetService.StopwatchId);
        Check(clockTr.Label == "Tarih ve saat", "Clock widget label must localize to Turkish.");
        Check(stopwatchTr.Label == "Kronometre", "Stopwatch widget label must localize to Turkish.");

        UiText.Current.SetLanguage(AppLanguage.English);
        DockWidgetService.Refresh(testWidgets, AppLanguage.English);
        Check(clockTr.Label == "Date and time", "Clock widget label must localize to English.");
        Check(stopwatchTr.Label == "Stopwatch", "Stopwatch widget label must localize to English.");

        // Verify TaskbarReplacement error keys localization
        Check(UiText.Get("ErrorTaskbarHide") == "Cannot hide the Windows taskbar.", "Taskbar hide error must localize to English.");
        Check(UiText.Get("ErrorTaskbarRestoreFailed").Contains("Cannot restore the Windows taskbar"), "Taskbar restore error must localize to English.");
        UiText.Current.SetLanguage(AppLanguage.Turkish);
        Check(UiText.Get("ErrorTaskbarHide") == "Windows görev çubuğu gizlenemedi.", "Taskbar hide error must localize to Turkish.");
        Check(UiText.Get("ErrorTaskbarRestoreFailed").Contains("Windows görev çubuğu geri yüklenemedi"), "Taskbar restore error must localize to Turkish.");

        // Verify optical glass materials and bevel resources
        var darkTheme = new ResourceDictionary { Source = new Uri("/OpenTaskbar;component/Design/Palettes/Dark.xaml", UriKind.Relative) };
        var lightTheme = new ResourceDictionary { Source = new Uri("/OpenTaskbar;component/Design/Palettes/Light.xaml", UriKind.Relative) };
        Check(darkTheme["DockBorderMaterial"] is LinearGradientBrush { GradientStops.Count: >= 4 }, "Dark theme must define multi-stop Fresnel rim light gradient.");
        Check(lightTheme["DockBorderMaterial"] is LinearGradientBrush { GradientStops.Count: >= 4 }, "Light theme must define multi-stop Fresnel rim light gradient.");
        Check(darkTheme["DockInnerBevelMaterial"] is LinearGradientBrush, "Dark theme must define optical inner bevel refraction line.");
        Check(lightTheme["DockInnerBevelMaterial"] is LinearGradientBrush, "Light theme must define optical inner bevel refraction line.");
        Check(darkTheme["DockHighlight"] is LinearGradientBrush, "Dark theme must define surface specular highlight.");
        Check(lightTheme["DockHighlight"] is LinearGradientBrush, "Light theme must define surface specular highlight.");
        Check(app.FindResource("StartDockButton") is Style, "StartDockButton tactile tile style must be defined.");

        // Verify WidgetCoordinator ordering, reordering and validation
        Check(WidgetCoordinator.DefaultWidgetOrder.Length == 9, "WidgetCoordinator must have 9 default widgets.");
        Check(WidgetCoordinator.GetLabelKey("Clock") == "WidgetClock", "Clock widget label key must match.");
        Check(WidgetCoordinator.GetLabelKey("Media") == "WidgetMedia", "Media widget label key must match.");

        UiText.Current.SetLanguage(AppLanguage.Turkish);
        Check(UiText.Get("WidgetOrderHeading") == "Widget Sıralaması", "WidgetOrderHeading must localize to Turkish.");
        Check(UiText.Get("MoveUp") == "Yukarı taşı", "MoveUp must localize to Turkish.");
        UiText.Current.SetLanguage(AppLanguage.English);
        Check(UiText.Get("WidgetOrderHeading") == "Widget Order", "WidgetOrderHeading must localize to English.");
        Check(UiText.Get("MoveUp") == "Move up", "MoveUp must localize to English.");

        var testStrip = new StackPanel();
        using var coordinator = new WidgetCoordinator(testStrip, Dispatcher.CurrentDispatcher, _ => { });
        Check(coordinator.CurrentOrder.SequenceEqual(WidgetCoordinator.DefaultWidgetOrder), "Initial coordinator order must match defaults.");

        coordinator.ApplyOrder(["Clock", "Focus", "Media"]);
        Check(coordinator.CurrentOrder[0] == "Clock" && coordinator.CurrentOrder[1] == "Focus" && coordinator.CurrentOrder[2] == "Media",
            "ApplyOrder must reorder widgets per specified order.");
        Check(testStrip.Children.Count == 9, "Strip children count must remain 9 after reordering.");

        IReadOnlyList<string>? capturedOrder = null;
        coordinator.OrderChanged = order => capturedOrder = order;
        coordinator.ReorderWidgets(0, 1);
        Check(coordinator.CurrentOrder[0] == "Focus" && coordinator.CurrentOrder[1] == "Clock",
            "ReorderWidgets must swap widget indices.");
        Check(capturedOrder != null && capturedOrder[0] == "Focus" && capturedOrder[1] == "Clock",
            "OrderChanged callback must trigger with updated order.");

        // Verify DockSettingsStore validation for WidgetOrder
        var validSettings = new DockSettings(WidgetOrder: ["Clock", "Weather", "Media"]);
        Check(DockSettingsStore.Validate(validSettings), "Valid WidgetOrder must pass settings validation.");
        var duplicateSettings = new DockSettings(WidgetOrder: ["Clock", "Clock"]);
        Check(!DockSettingsStore.Validate(duplicateSettings), "Duplicate WidgetOrder must fail settings validation.");
        var whitespaceSettings = new DockSettings(WidgetOrder: ["Clock", "   "]);
        Check(!DockSettingsStore.Validate(whitespaceSettings), "Whitespace WidgetOrder must fail settings validation.");

        // Verify unified ClockWidget day, date, time and weather capsule
        var clockWidget = new ClockWidget();
        clockWidget.ApplySettings(new DockSettings(Language: AppLanguage.English, ShowWeather: true));
        clockWidget.Activate();
        clockWidget.Tick(new DateTime(2026, 11, 16, 20, 16, 0));
        var dayText = clockWidget.FindName("DayOfWeekText") as TextBlock;
        var dateText = clockWidget.FindName("DateText") as TextBlock;
        var timeText = clockWidget.FindName("TimeText") as TextBlock;
        Check(dayText?.Text == "MONDAY", "ClockWidget must display uppercase day of week.");
        Check(dateText?.Text == "Nov 16", "ClockWidget must display date format.");
        Check(timeText?.Text.Contains("20") == true || timeText?.Text.Contains("8") == true, "ClockWidget must display time.");

        // Verify DockStack icon property and notifications
        var tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenTaskbarStackTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempFolder);
        try
        {
            var stack = new DockStack(tempFolder, Dispatcher.CurrentDispatcher);
            var iconNotified = false;
            stack.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DockStack.Icon)) iconNotified = true; };
            stack.Icon = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            Check(iconNotified, "DockStack.Icon property change must raise PropertyChanged event.");
            Check(stack.Icon != null, "DockStack.Icon must hold assigned BitmapSource.");
            stack.Dispose();
        }
        finally
        {
            System.IO.Directory.Delete(tempFolder, recursive: true);
        }

        // Verify DockWidget RenderTransform initialization for drag-and-drop animation
        var dummyWidget = new ClockWidget();
        Check(dummyWidget.RenderTransform is TransformGroup tg && tg.Children.Count >= 2 && tg.Children[0] is ScaleTransform && tg.Children[1] is TranslateTransform,
            "DockWidget must initialize TransformGroup with ScaleTransform and TranslateTransform for fluid animations.");
        Check(dummyWidget.RenderTransformOrigin == new Point(0.5, 0.5),
            "DockWidget RenderTransformOrigin must be centered for balanced scaling.");
        dummyWidget.Dispose();

        // Verify WidgetCoordinator.HandlesWidget helper
        Check(WidgetCoordinator.HandlesWidget("Clock"), "HandlesWidget must return true for default widget Clock.");
        Check(WidgetCoordinator.HandlesWidget("Media"), "HandlesWidget must return true for default widget Media.");
        Check(!WidgetCoordinator.HandlesWidget("NonExistentWidget"), "HandlesWidget must return false for unknown widget.");

        // Verify Fullscreen video / window bounds detection (TaskbarReplacementSession.IsFullScreenBounds)
        const long wsCaption = 0x00C00000;
        var normalMon = new TaskbarReplacementSession.Rect(0, 0, 1920, 1080);
        var normalWork = new TaskbarReplacementSession.Rect(0, 0, 1920, 1032);
        var normalMaximizedWin = new TaskbarReplacementSession.Rect(-8, -8, 1928, 1040);
        Check(!TaskbarReplacementSession.IsFullScreenBounds(normalMaximizedWin, wsCaption, isZoomed: true, normalMon, normalWork),
            "Normal maximized window conforming to work area must not be detected as full screen.");

        var fullScreenVideoWin = new TaskbarReplacementSession.Rect(0, 0, 1920, 1080);
        Check(TaskbarReplacementSession.IsFullScreenBounds(fullScreenVideoWin, 0, isZoomed: false, normalMon, normalWork),
            "Fullscreen video covering monitor must be detected as full screen.");

        Check(TaskbarReplacementSession.IsFullScreenBounds(fullScreenVideoWin, wsCaption, isZoomed: true, normalMon, normalWork),
            "Fullscreen video spanning across reserved shelf must be detected as full screen even if browser retained caption style.");

        var highDpiMon = new TaskbarReplacementSession.Rect(0, 0, 2560, 1440);
        var highDpiWork = new TaskbarReplacementSession.Rect(0, 0, 2560, 1380);
        var highDpiVideoWin = new TaskbarReplacementSession.Rect(1, 0, 2559, 1440);
        Check(TaskbarReplacementSession.IsFullScreenBounds(highDpiVideoWin, 0, isZoomed: false, highDpiMon, highDpiWork),
            "High-DPI fullscreen video with subpixel rounding within 2px tolerance must be detected as full screen.");

        var autoHideMon = new TaskbarReplacementSession.Rect(0, 0, 1920, 1080);
        var autoHideWork = new TaskbarReplacementSession.Rect(0, 0, 1920, 1080);
        var autoHideMaximizedWin = new TaskbarReplacementSession.Rect(-8, -8, 1928, 1088);
        Check(!TaskbarReplacementSession.IsFullScreenBounds(autoHideMaximizedWin, wsCaption, isZoomed: true, autoHideMon, autoHideWork),
            "Captioned maximized window in auto-hide mode must not be detected as full screen.");

        var autoHideVideoWin = new TaskbarReplacementSession.Rect(0, 0, 1920, 1080);
        Check(TaskbarReplacementSession.IsFullScreenBounds(autoHideVideoWin, 0, isZoomed: false, autoHideMon, autoHideWork),
            "Fullscreen video in auto-hide mode must be detected as full screen.");

        return checks;
    }
}
