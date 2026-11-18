using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
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
    private const ushort VkS = 0x53;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtended = 0x0001;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int ErrorCancelled = 1223;
    private static CancellationTokenSource? _repositionCts;
    private static CancellationTokenSource? _searchRepositionCts;
    public static Func<bool>? IsMediaWidgetActive { get; set; }

    private static bool IsExtended(ushort key) => key is VkLeft or VkRight or VkLeftWindows;

    /// <summary>Win+A: Quick Settings (Windows 11) or Action Center (Windows 10), anchored above the clicked button when given.</summary>
    public static string? OpenQuickSettings(FrameworkElement? anchor = null)
    {
        var wasVisible = IsControlCenterVisible();
        var error = SendWindowsShortcut(VkA);
        if (error is null && anchor is not null && !wasVisible)
        {
            ScheduleControlCenterReposition(anchor);
        }
        return error;
    }

    /// <summary>Opens Windows' native available Wi-Fi networks flyout without throwing the user into full Settings.</summary>
    public static string? OpenAvailableNetworks(FrameworkElement? anchor = null)
    {
        var wasVisible = IsControlCenterVisible();
        var error = Start("ms-availablenetworks:", "ErrorSettingsPageOpen");
        if (error is null && anchor is not null && !wasVisible)
        {
            ScheduleControlCenterReposition(anchor);
        }
        return error;
    }

    /// <summary>Win+N: notification center and calendar, opened natively via Windows shortcut.</summary>
    public static string? OpenNotificationCenter(FrameworkElement? anchor = null)
    {
        AppLog.Info("OpenNotificationCenter: invoked.");
        EnsureShellExperienceHost();
        var error = SendWindowsShortcut(VkN);
        if (error is not null) AppLog.Warning($"OpenNotificationCenter failed: {error}");
        return error;
    }

    private static void EnsureShellExperienceHost()
    {
        try
        {
            if (Process.GetProcessesByName("ShellExperienceHost").Length == 0)
            {
                AppLog.Info("EnsureShellExperienceHost: launching ShellExperienceHost package...");
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy!App") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"EnsureShellExperienceHost exception: {ex.Message}");
        }
    }

    /// <summary>Win+P: display projection flyout, anchored above the clicked button when given.</summary>
    public static string? OpenProjection(FrameworkElement? anchor = null)
    {
        var wasVisible = IsControlCenterVisible();
        var error = SendWindowsShortcut(VkP);
        if (error is null && anchor is not null && !wasVisible)
        {
            ScheduleControlCenterReposition(anchor);
        }
        return error;
    }

    /// <summary>Win+S: Windows Search flyout, opened natively via Windows shortcut and anchored above the clicked button when given.</summary>
    public static string? OpenWindowsSearch(FrameworkElement? anchor = null)
    {
        AppLog.Info("OpenWindowsSearch: invoked.");
        var wasVisible = IsSearchVisible();
        var error = SendWindowsShortcut(VkS);
        if (error is null && anchor is not null && !wasVisible)
        {
            ScheduleSearchReposition(anchor);
        }
        if (error is not null) AppLog.Warning($"OpenWindowsSearch failed: {error}");
        return error;
    }

    private static bool IsSearchVisible() => FindSearchWindows().Count > 0;

    private static List<nint> FindSearchWindows()
    {
        var list = new List<nint>();
        try
        {
            var pids = Process.GetProcessesByName("SearchHost")
                .Concat(Process.GetProcessesByName("SearchApp"))
                .Select(p => (uint)p.Id)
                .ToHashSet();

            if (pids.Count > 0)
            {
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pids.Contains(pid))
                    {
                        if (GetWindowRect(hwnd, out var rect))
                        {
                            var w = rect.Right - rect.Left;
                            var h = rect.Bottom - rect.Top;
                            if (w > 40 && h > 20)
                            {
                                list.Add(hwnd);
                            }
                        }
                    }
                    return true;
                }, 0);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"FindSearchWindows exception: {ex.Message}");
        }

        if (list.Count == 0)
        {
            foreach (var title in new[] { "Search", "Arama", "Ara" })
            {
                var h = FindWindow("Windows.UI.Core.CoreWindow", title);
                if (h != 0 && IsWindowVisible(h))
                {
                    list.Add(h);
                    break;
                }
            }
        }

        return list;
    }

    private static void ScheduleSearchReposition(FrameworkElement anchor)
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchRepositionCts, next);
        previous?.Cancel();
        previous?.Dispose();
        _ = RepositionSearchAsync(anchor, next.Token);
    }

    private static async Task RepositionSearchAsync(FrameworkElement anchor, CancellationToken cancellation)
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

        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(25, cancellation);
                if (cancellation.IsCancellationRequested) return;

                var searchWindows = FindSearchWindows();
                if (searchWindows.Count == 0) continue;

                var mainHwnd = searchWindows.OrderByDescending(h =>
                {
                    if (GetWindowRect(h, out var r)) return (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    return 0L;
                }).First();

                if (!GetWindowRect(mainHwnd, out var rect)) continue;
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width < 100 || height < 100) continue;

                var centerX = (int)Math.Round((topLeft.X + bottomRight.X) / 2);
                var centerY = (int)Math.Round((topLeft.Y + bottomRight.Y) / 2);
                int targetX;
                int targetY;
                var minY = monitorInfo.Work.Top + 8;
                var maxY = Math.Max(minY, monitorInfo.Work.Bottom - height - 8);

                switch (edge)
                {
                    case DockEdge.Left:
                        targetX = (int)Math.Round(bottomRight.X) + 12;
                        targetY = Math.Clamp(centerY - height / 2, minY, maxY);
                        break;
                    case DockEdge.Right:
                        targetX = (int)Math.Round(topLeft.X) - width - 12;
                        targetY = Math.Clamp(centerY - height / 2, minY, maxY);
                        break;
                    default:
                        var minX = monitorInfo.Work.Left + 8;
                        var maxX = Math.Max(minX, monitorInfo.Work.Right - width - 8);
                        targetX = Math.Clamp(centerX - width / 2, minX, maxX);
                        targetY = Math.Clamp((int)Math.Round(topLeft.Y) - height - 12, minY, maxY);
                        break;
                }

                if (searchWindows.Count > 1)
                {
                    var sorted = searchWindows.OrderByDescending(h =>
                    {
                        if (GetWindowRect(h, out var r)) return r.Bottom - r.Top;
                        return 0;
                    }).ToList();

                    var flyout = sorted[0];
                    var inputBar = sorted[1];

                    if (GetWindowRect(inputBar, out var inputRect) && GetWindowRect(flyout, out var flyoutRect))
                    {
                        var inputW = inputRect.Right - inputRect.Left;
                        var inputH = inputRect.Bottom - inputRect.Top;
                        var flyoutW = flyoutRect.Right - flyoutRect.Left;
                        var flyoutH = flyoutRect.Bottom - flyoutRect.Top;

                        var inputTargetX = Math.Clamp(centerX - inputW / 2, monitorInfo.Work.Left + 8, monitorInfo.Work.Right - inputW - 8);
                        var flyoutTargetX = Math.Clamp(centerX - flyoutW / 2, monitorInfo.Work.Left + 8, monitorInfo.Work.Right - flyoutW - 8);

                        var inputTargetY = Math.Clamp((int)Math.Round(topLeft.Y) - inputH - 10, minY, maxY);
                        var flyoutTargetY = Math.Clamp(inputTargetY - flyoutH - 4, minY, maxY);

                        SetWindowPos(flyout, 0, flyoutTargetX, flyoutTargetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                        SetWindowPos(inputBar, 0, inputTargetX, inputTargetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                    }
                }
                else
                {
                    SetWindowPos(mainHwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                }

                // Settle across extra frames to smoothly complete shell entrance transitions
                await Task.Delay(40, cancellation);
                SetWindowPos(mainHwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                await Task.Delay(50, cancellation);
                SetWindowPos(mainHwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsControlCenterVisible()
    {
        var hwnd = FindWindow("ControlCenterWindow", null);
        return hwnd != 0 && IsWindowVisible(hwnd);
    }

    private static void ScheduleControlCenterReposition(FrameworkElement anchor)
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _repositionCts, next);
        previous?.Cancel();
        previous?.Dispose();
        _ = RepositionControlCenterAsync(anchor, next.Token);
    }

    private static async Task RepositionControlCenterAsync(FrameworkElement anchor, CancellationToken cancellation)
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

        try
        {
            for (var attempt = 0; attempt < 24; attempt++)
            {
                await Task.Delay(25, cancellation);
                if (cancellation.IsCancellationRequested) return;
                var hwnd = FindWindow("ControlCenterWindow", null);
                if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect)) continue;
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width < 80 || height < 80) continue;

                var centerX = (int)Math.Round((topLeft.X + bottomRight.X) / 2);
                var centerY = (int)Math.Round((topLeft.Y + bottomRight.Y) / 2);
                int targetX;
                int targetY;
                var minY = monitorInfo.Work.Top + 8;
                var maxY = Math.Max(minY, monitorInfo.Work.Bottom - height - 8);
                switch (edge)
                {
                    case DockEdge.Left:
                        targetX = (int)Math.Round(bottomRight.X) + 12;
                        targetY = Math.Clamp(centerY - height / 2, minY, maxY);
                        break;
                    case DockEdge.Right:
                        targetX = (int)Math.Round(topLeft.X) - width - 12;
                        targetY = Math.Clamp(centerY - height / 2, minY, maxY);
                        break;
                    default:
                        var minX = monitorInfo.Work.Left + 8;
                        var maxX = Math.Max(minX, monitorInfo.Work.Right - width - 8);
                        targetX = Math.Clamp(centerX - width / 2, minX, maxX);
                        targetY = Math.Clamp((int)Math.Round(topLeft.Y) - height - 12, minY, maxY);
                        break;
                }

                SetWindowPos(hwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                if (movedOnce) break;
                movedOnce = true;
                // Re-apply once more after ShellHost finishes its entrance animation.
                await Task.Delay(75, cancellation);
            }
        }
        catch (OperationCanceledException)
        {
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
            return UiText.Get(failureKey);
        }
    }

    private static string? SendWindowsShortcut(ushort key) => SendChord(VkLeftWindows, key);

    /// <summary>Presses the keys in order and releases them in reverse, like a user typing the chord.</summary>
    public static string? SendChord(params ushort[] keys)
    {
        if (keys.Length == 0) return null;
        var inputs = new Input[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            inputs[i] = KeyInput(key, IsExtended(key) ? KeyEventExtended : 0);
        }

        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[keys.Length - 1 - i];
            inputs[keys.Length + i] = KeyInput(key, KeyEventKeyUp | (IsExtended(key) ? KeyEventExtended : 0));
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return null;
        AppLog.Warning($"SendInput sent {sent}/{inputs.Length} events: {Marshal.GetLastPInvokeError()}");
        return UiText.Get("ErrorShortcutSend");
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

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
