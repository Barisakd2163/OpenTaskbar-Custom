using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Views;

// P3: simulates the ScrollChanged -> magnification reset -> deferred resize -> Reset feedback chain
// purely inside DockScrollViewer. No MainWindow instance, no Explorer and no real taskbar state is used.
internal static class DockGestureChecks
{
    internal static int RunUi()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }

        var viewer = new DockScrollViewer
        {
            Width = 240,
            Height = 60,
            // WPF's default is Disabled; the dock style uses Hidden so the strip can scroll without a bar.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Width = 1600, Height = 40 }
        };
        // A template-less ScrollViewer publishes its offset during a layout pass, so force one after
        // every write to keep the assertions deterministic (stands in for the next dock render frame).
        void Layout()
        {
            viewer.Measure(new Size(240, 60));
            viewer.Arrange(new Rect(0, 0, 240, 60));
            viewer.UpdateLayout();
        }

        Layout();
        Check(viewer.ScrollableWidth > 100,
            "The gesture test strip must actually overflow so offsets can be observed.");

        var offset = Math.Min(160, viewer.ScrollableWidth);
        viewer.ScrollToHorizontalOffset(offset);
        Layout();
        Check(Math.Abs(viewer.HorizontalOffset - offset) < 0.5
            && Math.Abs(viewer.DesiredHorizontalOffset - offset) < 0.5,
            "A scroll must update both the actual and desired offsets owned by the viewer.");
        Check(!viewer.IsGestureActive && viewer.ContentOverflows,
            "A settled viewer must start outside a gesture with overflow assumed until measured.");

        // ScrollChanged -> ResetMagnification -> deferred resize -> Reset. Each layout phase reports
        // "no overflow", but the open gesture scope must keep the strip exactly where the user left it.
        viewer.BeginGesture();
        Check(viewer.IsGestureActive, "BeginGesture must open the reset-suppressing gesture scope.");
        viewer.SetContentOverflow(false);
        Layout();
        Check(!viewer.ContentOverflows && Math.Abs(viewer.HorizontalOffset - offset) < 0.5,
            "A transient no-overflow signal must be recorded without moving the strip during a gesture.");
        viewer.SuppressScrollReset = true;
        viewer.ResetHorizontalScroll();
        Layout();
        Check(Math.Abs(viewer.HorizontalOffset - offset) < 0.5,
            "ResetHorizontalScroll must be blocked while an external scope suppresses resets.");
        viewer.SuppressScrollReset = false;
        Check(Math.Abs(viewer.HorizontalOffset - offset) < 0.5
            && Math.Abs(viewer.DesiredHorizontalOffset - offset) < 0.5,
            "The desired offset must survive the whole simulated resize chain unchanged.");
        viewer.EndGesture();
        Check(!viewer.IsGestureActive, "EndGesture must close the gesture scope.");

        // Once gesture and suppression are gone, a genuine no-overflow signal may reset to the start.
        viewer.SetContentOverflow(false);
        Layout();
        Check(viewer.HorizontalOffset == 0 && viewer.DesiredHorizontalOffset == 0,
            "A no-overflow signal outside any gesture must return the strip to the start.");

        // Explicit resets use the same guard but do not change the overflow signal.
        viewer.SetContentOverflow(true);
        viewer.ScrollToHorizontalOffset(offset);
        Layout();
        viewer.SuppressScrollReset = true;
        viewer.ResetHorizontalScroll();
        Layout();
        Check(Math.Abs(viewer.HorizontalOffset - offset) < 0.5 && viewer.ContentOverflows,
            "A suppressed explicit reset must preserve both the offset and the overflow signal.");
        viewer.SuppressScrollReset = false;
        viewer.ResetHorizontalScroll();
        Layout();
        Check(viewer.HorizontalOffset == 0 && viewer.DesiredHorizontalOffset == 0 && !viewer.ContentOverflows,
            "Releasing the suppression must let a later explicit reset complete.");

        return checks;
    }
}
