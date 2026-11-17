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

    /// <summary>Optional predicate indicating whether the dock's Media widget is active.</summary>
    public static Func<bool>? IsMediaWidgetActive { get; set; }

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

        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(25);
            var hwnd = FindWindow("ControlCenterWindow", null);
            if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect)) continue;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 80 || height < 80) continue;

            bool hideMedia = IsMediaWidgetActive?.Invoke() == true;
            int topOffset = 0;
            int visibleHeight = height;

            if (hideMedia)
            {
                try
                {
                    var element = AutomationElement.FromHandle(hwnd);
                    if (element is not null)
                    {
                        AutomationElement? mediaElem = element.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "MediaTransportControls"));
                        mediaElem ??= element.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ClassNameProperty, "ControlCenter.MediaTransportControls"));
                        if (mediaElem is null)
                        {
                            var all = element.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                            foreach (AutomationElement el in all)
                            {
                                var id = el.Current.AutomationId;
                                var cls = el.Current.ClassName;
                                if (id.Contains("MediaTransportControls", StringComparison.OrdinalIgnoreCase)
                                    || cls.Contains("MediaTransportControls", StringComparison.OrdinalIgnoreCase)
                                    || id.Contains("MediaControls", StringComparison.OrdinalIgnoreCase))
                                {
                                    mediaElem = el;
                                    break;
                                }
                            }
                        }

                        if (mediaElem is not null)
                        {
                            var mediaRect = mediaElem.Current.BoundingRectangle;
                            if (!double.IsInfinity(mediaRect.Height) && mediaRect.Height > 20)
                            {
                                bool isAtTop = (mediaRect.Top - rect.Top) < (rect.Bottom - mediaRect.Bottom);
                                if (isAtTop)
                                {
                                    topOffset = (int)Math.Clamp(Math.Round(mediaRect.Bottom - rect.Top), 0, height - 120);
                                    visibleHeight = height - topOffset;
                                    AppLog.Info($"RepositionControlCenterAsync: hiding media at top (height={mediaRect.Height}, topOffset={topOffset}, visibleHeight={visibleHeight})");
                                }
                                else
                                {
                                    visibleHeight = (int)Math.Clamp(Math.Round(mediaRect.Top - rect.Top), 120, height);
                                    AppLog.Info($"RepositionControlCenterAsync: hiding media at bottom (height={mediaRect.Height}, visibleHeight={visibleHeight})");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"RepositionControlCenterAsync media detection failed: {ex.Message}");
                }
            }

            var (targetX, targetY) = CalculateTargetPosition(topLeft, bottomRight, width, visibleHeight, monitorInfo, edge);
            int winY = targetY - topOffset;

            for (var frame = 0; frame < 3; frame++)
            {
                SetWindowPos(hwnd, 0, targetX, winY, width, height, SwpNoZOrder | SwpNoActivate);
                if (topOffset > 0 || visibleHeight < height)
                {
                    var rgn = topOffset > 0
                        ? CreateRoundRectRgn(0, topOffset, width, height, 16, 16)
                        : CreateRoundRectRgn(0, 0, width, visibleHeight, 16, 16);
                    SetWindowRgn(hwnd, rgn, true);
                }
                else
                {
                    SetWindowRgn(hwnd, 0, true);
                }
                await Task.Delay(50);
            }
            return;
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

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
