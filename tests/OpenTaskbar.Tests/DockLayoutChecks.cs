using System.Windows;
using System.Windows.Controls;
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
            var iconSize = (double)app.FindResource("DockMaximumIconSize");
            var cellSize = (double)app.FindResource("DockMaximumCellSize");
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
        return checks;
    }
}
