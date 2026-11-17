using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>
/// Opens Windows' own flyouts and settings pages, which stand in for the hidden taskbar's tray area.
/// </summary>
public static class ShellShortcuts
{
    public const ushort VkLeftWindows = 0x5B;
    public const ushort VkControl = 0x11;
    public const ushort VkLeft = 0x25;
    public const ushort VkRight = 0x27;
    public const ushort VkTab = 0x09;
    public const ushort VkD = 0x44;
    private const ushort VkA = 0x41;
    private const ushort VkN = 0x4E;
    private const ushort VkP = 0x50;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtended = 0x0001;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int ErrorCancelled = 1223;

    private static bool IsExtended(ushort key) => key is VkLeft or VkRight or VkLeftWindows;

    /// <summary>Win+A: Quick Settings (Windows 11) or Action Center (Windows 10), anchored above the clicked button when given.</summary>
    public static string? OpenQuickSettings(FrameworkElement? anchor = null)
    {
        var error = SendWindowsShortcut(VkA);
        if (error is null && anchor is not null)
        {
            _ = RepositionControlCenterAsync(anchor);
        }
        return error;
    }

    /// <summary>Win+N: notification center and calendar, anchored above the clicked button when given.</summary>
    public static string? OpenNotificationCenter(FrameworkElement? anchor = null)
    {
        var error = SendWindowsShortcut(VkN);
        if (error is null && anchor is not null)
        {
            _ = RepositionNotificationCenterAsync(anchor);
        }
        return error;
    }

    /// <summary>Win+P: display projection flyout, anchored above the clicked button when given.</summary>
    public static string? OpenProjection(FrameworkElement? anchor = null)
    {
        var error = SendWindowsShortcut(VkP);
        if (error is null && anchor is not null)
        {
            _ = RepositionControlCenterAsync(anchor);
        }
        return error;
    }

    private static async Task RepositionControlCenterAsync(FrameworkElement anchor)
    {
        if (PresentationSource.FromVisual(anchor) is not HwndSource source || source.Handle == 0) return;
        Point topLeft;
        Point bottomRight;
        try
        {
            topLeft = anchor.PointToScreen(new Point(0, 0));
            bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var monitor = MonitorFromWindow(source.Handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo)) return;
        var edge = DockFlyout.Edge;
        var movedOnce = false;

        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(25);
            var hwnd = FindWindow("ControlCenterWindow", null);
            if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect)) continue;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 80 || height < 80) continue;

            var (targetX, targetY) = CalculateTargetPosition(topLeft, bottomRight, width, height, monitorInfo, edge);
            SetWindowPos(hwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            if (movedOnce) break;
            movedOnce = true;
            // Re-apply once more after ShellHost finishes its entrance animation.
            await Task.Delay(75);
        }
    }

    private static async Task RepositionNotificationCenterAsync(FrameworkElement anchor)
    {
        AppLog.Info("RepositionNotificationCenterAsync: invoked.");
        HwndSource? source = PresentationSource.FromVisual(anchor) as HwndSource;
        if (source is null || source.Handle == 0)
        {
            if (Application.Current?.MainWindow is Window main && PresentationSource.FromVisual(main) is HwndSource mainSource)
            {
                source = mainSource;
            }
        }
        if (source is null || source.Handle == 0)
        {
            AppLog.Warning("RepositionNotificationCenterAsync: no HwndSource available.");
            return;
        }

        Point topLeft;
        Point bottomRight;
        try
        {
            topLeft = anchor.PointToScreen(new Point(0, 0));
            bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warning($"RepositionNotificationCenterAsync PointToScreen failed: {ex.Message}");
            return;
        }

        var monitor = MonitorFromWindow(source.Handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            AppLog.Warning("RepositionNotificationCenterAsync: GetMonitorInfo failed.");
            return;
        }

        var edge = DockFlyout.Edge;
        var movedOnce = false;

        for (var attempt = 0; attempt < 35; attempt++)
        {
            await Task.Delay(25);
            var hwnd = FindNotificationCenterHandle();
            if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect)) continue;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 80 || height < 80) continue;

            if (!movedOnce)
            {
                AppLog.Info($"RepositionNotificationCenterAsync: found hwnd 0x{hwnd:X} at ({rect.Left}, {rect.Top}, {width}x{height}) on attempt {attempt}");
                _ = Task.Run(() => EnsureCalendarCollapsed(hwnd));
                movedOnce = true;
            }

            var (targetX, targetY) = CalculateTargetPosition(topLeft, bottomRight, width, height, monitorInfo, edge);
            var setRes = SetWindowPos(hwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            AppLog.Info($"RepositionNotificationCenterAsync: SetWindowPos to ({targetX}, {targetY}) returned {setRes}");

            // Hold position across the entrance animation window
            for (var frame = 0; frame < 5; frame++)
            {
                await Task.Delay(40);
                SetWindowPos(hwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            }
            break;
        }

        if (!movedOnce)
        {
            AppLog.Warning("RepositionNotificationCenterAsync: notification window not found within timeout.");
        }
    }

    private static (int TargetX, int TargetY) CalculateTargetPosition(
        Point topLeft, Point bottomRight, int width, int height, MonitorInfo monitorInfo, DockEdge edge)
    {
        var centerX = (int)Math.Round((topLeft.X + bottomRight.X) / 2);
        var centerY = (int)Math.Round((topLeft.Y + bottomRight.Y) / 2);
        switch (edge)
        {
            case DockEdge.Left:
                return (
                    (int)Math.Round(bottomRight.X) + 12,
                    Math.Clamp(centerY - height / 2, monitorInfo.Work.Top + 8, monitorInfo.Work.Bottom - height - 8)
                );
            case DockEdge.Right:
                return (
                    (int)Math.Round(topLeft.X) - width - 12,
                    Math.Clamp(centerY - height / 2, monitorInfo.Work.Top + 8, monitorInfo.Work.Bottom - height - 8)
                );
            default:
                return (
                    Math.Clamp(centerX - width / 2, monitorInfo.Work.Left + 8, monitorInfo.Work.Right - width - 8),
                    Math.Max(monitorInfo.Work.Top + 8, (int)Math.Round(topLeft.Y) - height - 12)
                );
        }
    }

    private static nint FindNotificationCenterHandle()
    {
        // 1. Direct class & title lookups
        foreach (var cls in new[] { "NotificationCenterWindow", "Shell_ActionCenter", "Windows.UI.Core.CoreWindow" })
        {
            foreach (var title in new[] { "Notification Center", "Bildirim Merkezi", "Action center", null })
            {
                if (cls == "Windows.UI.Core.CoreWindow" && title is null) continue;
                var h = FindWindow(cls, title);
                if (h != 0 && IsWindowVisible(h))
                {
                    AppLog.Info($"FindNotificationCenterHandle found via FindWindow({cls}, '{title}') -> 0x{h:X}");
                    return h;
                }
            }
        }

        // 2. FindWindowEx traversal across desktop children (bypasses EnumWindows desktop band filtering)
        nint current = 0;
        while ((current = FindWindowEx(0, current, "Windows.UI.Core.CoreWindow", null)) != 0)
        {
            if (IsWindowVisible(current))
            {
                GetWindowRect(current, out var r);
                var w = r.Right - r.Left;
                var hgt = r.Bottom - r.Top;
                if (w >= 180 && hgt >= 180)
                {
                    AppLog.Info($"FindNotificationCenterHandle matched CoreWindow via FindWindowEx 0x{current:X} ({w}x{hgt})");
                    return current;
                }
            }
        }

        // 3. Fallback traversal: any top-level window from ShellExperienceHost or ShellHost
        current = 0;
        while ((current = FindWindowEx(0, current, null, null)) != 0)
        {
            if (!IsWindowVisible(current)) continue;
            GetWindowThreadProcessId(current, out var pid);
            string procName = string.Empty;
            try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

            if (procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || procName.Equals("ShellHost", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder(64);
                GetClassName(current, sb, 64);
                var className = sb.ToString();
                if (className != "ControlCenterWindow")
                {
                    GetWindowRect(current, out var r);
                    var w = r.Right - r.Left;
                    var hgt = r.Bottom - r.Top;
                    if (w >= 180 && hgt >= 180)
                    {
                        AppLog.Info($"FindNotificationCenterHandle matched {procName} ({className}) via FindWindowEx 0x{current:X} ({w}x{hgt})");
                        return current;
                    }
                }
            }
        }

        return 0;
    }

    private static void EnsureCalendarCollapsed(nint hwnd)
    {
        try
        {
            AppLog.Info($"EnsureCalendarCollapsed: starting for hwnd 0x{hwnd:X}");
            var element = AutomationElement.FromHandle(hwnd);
            if (element is null)
            {
                AppLog.Warning("EnsureCalendarCollapsed: AutomationElement is null");
                return;
            }

            for (var retry = 0; retry < 6; retry++)
            {
                var collapsibles = element.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true));
                AppLog.Info($"EnsureCalendarCollapsed: attempt {retry}, found {collapsibles.Count} expand/collapse elements");

                foreach (AutomationElement col in collapsibles)
                {
                    if (col.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pat) && pat is ExpandCollapsePattern ecp)
                    {
                        if (ecp.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                        {
                            ecp.Collapse();
                            AppLog.Info($"EnsureCalendarCollapsed: collapsed element {col.Current.AutomationId}/{col.Current.Name} via ExpandCollapsePattern");
                            return;
                        }
                    }
                }

                var buttons = element.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement btn in buttons)
                {
                    var autoId = btn.Current.AutomationId;
                    var name = btn.Current.Name;

                    // If the toggle already says "Expand", the calendar is already collapsed; keep it collapsed.
                    if (name?.Contains("Genişlet", StringComparison.OrdinalIgnoreCase) == true
                        || name?.Contains("Expand", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        AppLog.Info($"EnsureCalendarCollapsed: calendar is already collapsed ({autoId}/{name})");
                        return;
                    }

                    if (autoId is "CollapseButton" or "ExpandCollapseButton" or "ChevronButton" or "Chevron"
                        || name?.Contains("Collapse", StringComparison.OrdinalIgnoreCase) == true
                        || name?.Contains("Daralt", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out var inv) && inv is InvokePattern ip)
                        {
                            ip.Invoke();
                            AppLog.Info($"EnsureCalendarCollapsed: invoked {autoId}/{name}");
                            return;
                        }
                        if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tog) && tog is TogglePattern tp && tp.Current.ToggleState == ToggleState.On)
                        {
                            tp.Toggle();
                            AppLog.Info($"EnsureCalendarCollapsed: toggled {autoId}/{name}");
                            return;
                        }
                    }
                }

                Thread.Sleep(80);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"EnsureCalendarCollapsed exception: {ex.Message}");
        }
    }

    public static string? OpenUri(string uri) => Start(uri, "ErrorSettingsPageOpen");

    /// <summary>
    /// Task Manager by its full path, so a program named taskmgr elsewhere on the search path is never started.
    /// The Shell handles its elevation request.
    /// </summary>
    public static string? OpenTaskManager() => Start(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe"), "ErrorTaskManagerOpen");

    private static string? Start(string target, string failureKey)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user declined the elevation prompt; that is a choice, not a failure.
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Warning($"Cannot open {target}: {ex.Message}");
            return failureKey;
        }
    }

    private static string? SendWindowsShortcut(ushort key) => SendChord(VkLeftWindows, key);

    /// <summary>Presses the keys in order and releases them in reverse, like a user typing the chord.</summary>
    public static string? SendChord(params ushort[] keys)
    {
        var inputs = keys.Select(key => KeyInput(key, IsExtended(key) ? KeyEventExtended : 0))
            .Concat(keys.Reverse().Select(key => KeyInput(key, KeyEventKeyUp | (IsExtended(key) ? KeyEventExtended : 0))))
            .ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return null;
        AppLog.Warning($"SendInput sent {sent}/{inputs.Length} events: {Marshal.GetLastPInvokeError()}");
        return "ErrorShortcutSend";
    }

    private static Input KeyInput(ushort key, uint flags) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Keyboard;
        // Pads the union to the size of MOUSEINPUT, the largest member.
        private readonly long _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "InternalGetWindowText", CharSet = CharSet.Unicode)]
    private static extern int InternalGetWindowText(nint hWnd, [Out] StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
