using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

internal static class DesktopAutoHideChecks
{
    public static int Run(Application application)
    {
        application.Dispatcher.VerifyAccess();
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        // These are eligible applications' visible DWM frames, not GetWindowRect resize borders.
        // A null frame models a still-tracked window that is minimized, cloaked, or no longer valid.
        var frames = new Dictionary<nint, (int Left, int Top, int Right, int Bottom)?>();
        using var primary = new DockFixture((0, 0, 1920, 1080), frames);
        using var secondary = new DockFixture((-1920, 0, 0, 1080), frames);
        void RefreshBoth()
        {
            primary.Controller.InvalidateWindowOverlap();
            secondary.Controller.InvalidateWindowOverlap();
        }
        void ExpireBoth()
        {
            primary.ExpireHideDelay();
            secondary.ExpireHideDelay();
        }

        primary.Controller.SetMode(AutoHideMode.Desktop);
        secondary.Controller.SetMode(AutoHideMode.Desktop);
        ExpireBoth();
        Check(primary.IsRevealed && secondary.IsRevealed,
            "Desktop mode must reveal both empty monitors with no foreground window.");

        frames[1] = (100, 100, 500, 400);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsHidden && secondary.IsRevealed,
            "A small background application far above the dock must hide only its own monitor's dock.");

        // The hidden dock's revealed guard is stopped. Invalidation alone, without any foreground
        // callback or dispatcher tick, must recover when the final application is minimized/closed.
        frames[1] = null;
        primary.Controller.InvalidateWindowOverlap();
        Check(primary.IsRevealed && secondary.IsRevealed,
            "Minimizing the last application must immediately reveal a hidden dock with zero foreground.");
        frames[1] = (100, 100, 500, 400);
        primary.Controller.InvalidateWindowOverlap();
        primary.ExpireHideDelay();
        Check(primary.IsHidden, "Restoring a tracked application must restore Desktop hiding.");
        frames.Remove(1);
        primary.Controller.InvalidateWindowOverlap();
        Check(primary.IsRevealed,
            "Closing the last application must reveal the hidden dock without a foreground event or guard tick.");

        frames[1] = (-900, 100, -500, 400);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsRevealed && secondary.IsHidden,
            "An application on the secondary monitor must not hide the primary monitor's dock.");
        frames[1] = (100, 100, 500, 400);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsHidden && secondary.IsRevealed,
            "Moving an application between monitors must reveal the old dock and hide the new dock.");

        frames[1] = (-100, 100, 100, 400);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsHidden && secondary.IsHidden,
            "A genuinely spanning visible application frame must hide both monitors' docks.");
        frames[1] = (-1920, 0, 0, 1080);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsRevealed && secondary.IsHidden,
            "A maximized secondary DWM frame ending at the shared edge must not bleed onto the primary monitor.");
        frames[1] = (0, 0, 1920, 1080);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsHidden && secondary.IsRevealed,
            "A maximized primary DWM frame must not hide the adjacent monitor's dock.");
        frames[1] = (-1, 100, 100, 400);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsHidden && secondary.IsHidden,
            "Even a one-pixel genuine visible-frame intersection must count on the adjacent monitor.");
        frames[1] = (0, 100, 0, 400);
        RefreshBoth();
        ExpireBoth();
        Check(primary.IsRevealed && secondary.IsRevealed,
            "An empty visible frame must not hide either monitor.");

        frames[1] = (100, 100, 500, 400);
        RefreshBoth();
        ExpireBoth();
        primary.Controller.OnMouseEnterEdge();
        Check(primary.IsRevealed, "The screen edge must reveal a Desktop-hidden dock at zero translation.");
        primary.ExpireHideDelay();
        Check(primary.IsRevealed, "A Desktop dock must stay revealed while the screen edge is held.");
        primary.Controller.OnMouseLeaveEdge();
        primary.ExpireHideDelay();
        Check(primary.IsHidden, "Leaving the edge must resume Desktop hiding while an application is visible.");

        primary.Cursor = (800, 1040);
        primary.Controller.OnMouseEnterDock();
        primary.ExpireHideDelay();
        Check(primary.IsRevealed, "Dock hover must preserve access to apps and widgets in Desktop mode.");
        primary.Cursor = (100, 500);
        primary.Controller.OnMouseLeaveDock();
        primary.ExpireHideDelay();
        Check(primary.IsHidden, "Leaving the dock must resume Desktop hiding.");
        primary.InteractionLocked = true;
        primary.Controller.EvaluateState();
        primary.ExpireHideDelay();
        Check(primary.IsRevealed, "An active widget or interaction lock must reveal and hold a Desktop dock.");
        primary.InteractionLocked = false;
        primary.Controller.EvaluateState();
        primary.ExpireHideDelay();
        Check(primary.IsHidden, "Releasing the interaction lock must restore Desktop hiding.");
        primary.ShellMenuOpen = true;
        primary.Controller.EvaluateState();
        primary.ExpireHideDelay();
        Check(primary.IsRevealed, "An open Start menu must hold the Desktop dock revealed.");
        primary.ShellMenuOpen = false;
        primary.Controller.EvaluateState();
        primary.ExpireHideDelay();

        primary.Controller.SetMode(AutoHideMode.Overlay);
        primary.ExpireHideDelay();
        Check(primary.IsRevealed, "Switching Desktop to Overlay must reveal the dock and cancel pending hiding.");
        primary.Controller.SetMode(AutoHideMode.Always);
        primary.ExpireHideDelay();
        frames.Clear();
        primary.Controller.InvalidateWindowOverlap();
        primary.ExpireHideDelay();
        Check(primary.IsHidden, "Always mode must continue hiding even when no applications remain.");
        primary.Controller.SetMode(AutoHideMode.Desktop);
        Check(primary.IsRevealed, "Switching Always to Desktop on an empty monitor must reveal immediately.");
        frames[1] = (100, 100, 500, 400);
        primary.Controller.InvalidateWindowOverlap();
        primary.ExpireHideDelay();
        primary.Controller.SetMode(AutoHideMode.Disabled);
        primary.ExpireHideDelay();
        Check(primary.IsRevealed, "Disabling Desktop auto-hide must reset the visible dock translation.");
        primary.Controller.SetMode(AutoHideMode.Desktop);
        primary.ExpireHideDelay();
        Check(primary.IsHidden, "Re-enabling Desktop must rescan the current applications.");

        // Isolate Smart's foreground from the user's desktop. A shell foreground without a previous
        // application and an empty background list cannot inherit Desktop's monitor-presence result.
        primary.Controller.IsShellMenuWindow = _ => true;
        primary.Controller.LastApplicationWindowProvider = () => 0;
        primary.Controller.BackgroundWindowsProvider = () => [];
        primary.Controller.SetMode(AutoHideMode.Smart);
        primary.ExpireHideDelay();
        Check(primary.IsRevealed, "Switching Desktop to Smart must not retain Desktop-only presence as overlap.");
        primary.Controller.SetMode(AutoHideMode.Desktop);
        primary.Controller.BackgroundWindowsProvider = () => frames.Keys;
        primary.Controller.InvalidateWindowOverlap();
        primary.ExpireHideDelay();
        Check(primary.IsHidden, "Switching Smart back to Desktop must restore monitor-wide application detection.");

        frames.Clear();
        primary.Foreground = 99; // A desktop/shell foreground has no eligible application frame.
        primary.Controller.InvalidateWindowOverlap();
        Check(primary.IsRevealed, "A shell or desktop foreground must not keep the final application result latched.");
        return checks;
    }

    private sealed class DockFixture : IDisposable
    {
        private static readonly MethodInfo HideTick = typeof(DockAutoHideController).GetMethod(
            "HideTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Auto-hide delay callback was not found.");
        private readonly Window _host = new();
        private readonly TranslateTransform _translation = new();

        public DockAutoHideController Controller { get; }
        public bool InteractionLocked { get; set; }
        public bool ShellMenuOpen { get; set; }
        public nint Foreground { get; set; }
        public (int X, int Y) Cursor { get; set; } = (100, 500);
        public bool IsRevealed => !Controller.IsHidden && _translation.X == 0 && _translation.Y == 0;
        public bool IsHidden => Controller.IsHidden && _translation.X == 0
            && _translation.Y >= DockMetrics.HideMinimumOffset;

        public DockFixture(
            (int Left, int Top, int Right, int Bottom) monitor,
            Dictionary<nint, (int Left, int Top, int Right, int Bottom)?> frames)
        {
            var surface = new Border { Width = 720, Height = 60, RenderTransform = _translation };
            surface.Measure(new Size(720, 60));
            surface.Arrange(new Rect(0, 0, 720, 60));
            // Never create an HWND, show a window, register an AppBar, or use the user's monitor/cursor state.
            Controller = new DockAutoHideController(_host, 0, _translation, surface, () => InteractionLocked)
            {
                AnimationsEnabled = false,
                DockBoundsProvider = () => (monitor.Left + 600, monitor.Bottom - 60, monitor.Left + 1320, monitor.Bottom),
                MonitorBoundsProvider = () => monitor,
                VisibleApplicationBoundsProvider = hwnd => frames.TryGetValue(hwnd, out var frame) ? frame : null,
                ForegroundProvider = () => Foreground,
                BackgroundWindowsProvider = () => frames.Keys,
                CursorPositionProvider = () => Cursor,
                IsShellMenuOpen = () => ShellMenuOpen
            };
        }

        // Advance only the controller's existing delay callback, not wall-clock time or a real desktop.
        public void ExpireHideDelay() => HideTick.Invoke(Controller, [null, EventArgs.Empty]);

        public void Dispose()
        {
            Controller.Dispose();
            _host.Close();
        }
    }
}
