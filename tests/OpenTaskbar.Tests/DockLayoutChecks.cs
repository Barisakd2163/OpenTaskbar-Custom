using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenTaskbar.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Services;
using OpenTaskbar.Views;

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
            if (width == oneCellWidth)
                Check(viewer.ViewportWidth == cellWidth, "The minimum dock width must expose exactly one complete application cell.");
            var widget = new ContentControl { Content = new DockWidget("Clock", "Clock") { Value = "14:32" } };
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
        return checks;
    }
}
